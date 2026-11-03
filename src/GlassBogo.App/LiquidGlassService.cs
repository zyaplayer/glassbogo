using Microsoft.Win32;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace GlassBogo.App;

// One serialized GPU queue and one monitor duplication shared by every v2 surface.
// UI snapshots are immutable. No DependencyObject is read by the GPU worker.
internal sealed class LiquidGlassService : IAsyncDisposable
{
    private readonly Dispatcher dispatcher;
    private readonly Func<Preferences> preferences;
    private readonly bool qa;
    private readonly List<LiquidWindow> clients = [];
    private readonly DispatcherTimer timer;
    private readonly SemaphoreSlim gpuGate = new(1);
    private readonly Dictionary<LiquidWindow, State> states = [];
    private DesktopCapture? capture;
    private ID3D11Texture2D? synthetic;
    private ID3D11Texture2D? desktopCache;
    private LiquidGlassGpu? resources;
    private readonly Queue<double> renderTimes = new();
    private readonly Native.WinEventProc foregroundCallback;
    private nint hook;
    private Task pending = Task.CompletedTask;
    private Task managementChange = Task.CompletedTask;
    private bool managementOpen;
    private int managementVersion;
    internal bool ScreenshotReady { get; private set; }
    private bool closing, reset, releasing;
    private bool sessionActive = true;
    private DateTime nextRetry;
    private int failures;
    internal string Status { get; private set; } = "等待窗口";
    internal string? LastFailure { get; private set; }
    internal long RenderedFrames { get; private set; }
    internal long TickCount { get; private set; }
    internal long WakeCount { get; private set; }
    internal int LiveSurfaces => states.Count;
    internal int ClientCount => clients.Count;
    private sealed class State : IDisposable
    {
        internal required LiquidGlassGpu Gpu;
        internal ID3D11Texture2D? Source;
        internal required int Width, Height;
        internal int SourceWidth, SourceHeight;
        internal Native.Rect Bounds;
        public void Dispose() { Source?.Dispose(); Gpu.Dispose(); }
    }
    private sealed record Frame(LiquidWindow Client, nint Hwnd, int X, int Y, int Width, int Height, float Dpi, Vector2 Pointer, bool Dirty);
    private sealed record Presented(Frame Frame, nint Surface, bool Drew);
    private sealed record Frozen(LiquidWindow Client, BitmapSource Bitmap, Native.Rect Bounds);
    internal sealed record Metrics(string Adapter, int Surfaces, double? LocalMemoryMiB, double? NonLocalMemoryMiB, double? RenderFenceP95Milliseconds, string? QueryError);
    internal async Task<Metrics> GetMetrics()
    {
        Metrics result = new("", 0, null, null, null, "No active graphics device");
        await OnGpu(() =>
        {
            if (capture is null) return;
            double? local = null, shared = null; string? error = null;
            try
            {
                using var dxgiDevice = capture.Device.QueryInterface<IDXGIDevice>();
                dxgiDevice.GetAdapter(out var adapter).CheckError();
                using (adapter)
                using (var info = adapter.QueryInterface<IDXGIAdapter3>())
                { local = info.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage / 1048576d; shared = info.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage / 1048576d; }
            }
            catch (Exception ex) { error = ex.Message; }
            var times = renderTimes.Order().ToArray();
            result = new(capture.AdapterName, states.Count, local, shared, times.Length == 0 ? null : times[(int)Math.Floor((times.Length - 1) * .95)], error);
        });
        return result;
    }

    internal LiquidGlassService(Func<Preferences> preferences, bool qa = false)
    {
        this.preferences = preferences; this.qa = qa; dispatcher = Dispatcher.CurrentDispatcher;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => { if (pending.IsCompleted && !closing) pending = Tick(); };
        foregroundCallback = (_, _, _, _, _, _, _) =>
        {
            Post(Wake);
        };
        hook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 2);
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.SessionSwitch += SessionChanged;
    }
    internal void Add(LiquidWindow client)
    {
        if (closing || !client.UsesGlass || clients.Contains(client)) return;
        if (clients.Count >= 8) { client.SetMaterialVisible(false, "兼容模式"); return; }
        clients.Add(client);
        client.ExcludedFromCapture = qa || SetWindowDisplayAffinity(client.Handle, 0x11);
        if (!client.ExcludedFromCapture) client.SetMaterialVisible(false, "兼容模式");
        Wake();
    }
    internal void Remove(LiquidWindow client)
    {
        // Tick owns any D3DImage lock. Detach only after that tick releases it.
        _ = RemoveAsync(client);
    }
    private async Task RemoveAsync(LiquidWindow client)
    {
        await pending;
        client.DetachMaterial();
        clients.Remove(client);
        await OnGpu(() => { if (states.Remove(client, out var state)) state.Dispose(); });
        Wake();
    }
    internal void Wake()
    {
        WakeCount++;
        if (closing || releasing || managementOpen || clients.All(c => !c.IsVisible || c.Removed)) return;
        timer.Start();
    }
    internal void SetManagementOpen(bool open)
    {
        if (closing || managementOpen == open) return;
        managementOpen = open; ScreenshotReady = false; timer.Stop();
        var version = ++managementVersion; var previous = managementChange;
        managementChange = ChangeManagement(previous, version);
    }
    private async Task ChangeManagement(Task previous, int version)
    {
        try
        {
            await previous; await pending;
            if (closing || version != managementVersion) return;
            if (managementOpen)
            {
                await FreezeIdle(clients.Where(c => c.IsVisible && !c.Removed).ToArray(), force: true);
                await ReleaseGraphics(preserveFrozen: true);
                if (closing || version != managementVersion) return;
                var allowed = true;
                foreach (var client in clients.Where(c => !c.Removed))
                {
                    allowed &= SetWindowDisplayAffinity(client.Handle, 0);
                    client.ExcludedFromCapture = false;
                }
                ScreenshotReady = allowed;
                Status = allowed ? "底栏画面已冻结，可截图" : "截图排除未能解除";
            }
            else
            {
                // Re-enable exclusion before creating a fresh duplication, so
                // a screenshot frame containing the dock can never feed back.
                foreach (var client in clients.Where(c => !c.Removed))
                {
                    client.ExcludedFromCapture = qa || SetWindowDisplayAffinity(client.Handle, 0x11);
                    if (!client.ExcludedFromCapture) { client.ClearRetained(); client.SetMaterialVisible(false, "兼容模式"); }
                }
                reset = true; failures = 0; nextRetry = default; Wake();
            }
        }
        catch (Exception ex)
        {
            LastFailure = ex.ToString();
            foreach (var client in clients) client.SetMaterialVisible(false, "兼容模式");
            Status = "兼容模式";
        }
    }
    internal void Refresh()
    {
        if (closing) return;
        foreach (var client in clients.Where(c => !c.Removed))
        {
            client.ThemeChanged();
            if (managementOpen)
            {
                if (preferences().VisualQuality == "off" || !GlassEnvironment.Transparency || client.LastFrameDark != Design.Dark) { client.ClearRetained(); client.SetMaterialVisible(false, preferences().VisualQuality == "off" ? "已关闭" : "兼容模式"); }
                else if (!client.ShowRetained("底栏画面已冻结，可截图")) client.SetMaterialVisible(false, "等待底栏更新");
                continue;
            }
            client.ExcludedFromCapture = qa || SetWindowDisplayAffinity(client.Handle, 0x11);
            if (!client.ExcludedFromCapture) client.SetMaterialVisible(false, "兼容模式");
        }
        failures = 0; nextRetry = default; reset = true; Wake();
    }
    internal void FrontBufferChanged(LiquidWindow client)
    {
        client.MaterialDirty = true;
        if (managementOpen) return;
        if (!client.Material.IsFrontBufferAvailable)
        {
            if (!client.ShowRetained("等待底栏更新")) client.SetMaterialVisible(false, "等待底栏更新");
            reset = true;
        }
        Wake();
    }
    private void Post(Action action)
    {
        if (closing || dispatcher.HasShutdownStarted) return;
        try { dispatcher.BeginInvoke(() => { if (!closing) action(); }); } catch (InvalidOperationException) { }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Post(Refresh);
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Post(Refresh);
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Post(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { sessionActive = false; Wake(); }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect) { sessionActive = true; Refresh(); }
    });
    private async Task OnGpu(Action action)
    {
        await gpuGate.WaitAsync();
        try { await Task.Run(action); }
        finally { gpuGate.Release(); }
    }
    private static bool Covered(LiquidWindow client)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == 0 || fg == client.Handle || DesktopLayer.IsDesktop(fg)) return false;
        return Native.GetWindowRect(client.Handle, out var own) && Native.GetWindowRect(fg, out var other)
            && other.Left <= own.Left && other.Top <= own.Top && other.Right >= own.Right && other.Bottom >= own.Bottom;
    }
    private async Task Tick()
    {
        TickCount++;
        var wakeAtStart = WakeCount;
        if (releasing || closing || managementOpen) return;
        var settings = preferences();
        var enabled = sessionActive && settings.VisualQuality != "off" && GlassEnvironment.Transparency;
        timer.Interval = TimeSpan.FromMilliseconds(settings.VisualQuality == "reduced" ? 67 : 33);
        var visible = clients.Where(c => c.ExcludedFromCapture && c.IsVisible && !c.Removed && c.WindowState != WindowState.Minimized && !Covered(c)).ToArray();
        if (!enabled || visible.Length == 0)
        {
            timer.Stop();
            Status = settings.VisualQuality == "off" ? "已关闭" : !enabled || clients.Any(c => c.IsVisible && !c.ExcludedFromCapture) ? "兼容模式" : "待机";
            if (enabled) await PreserveVisibleFrames();
            else foreach (var client in clients) { client.ClearRetained(); client.SetMaterialVisible(false, settings.VisualQuality == "off" ? "已关闭" : "兼容模式"); }
            await ReleaseGraphics(preserveFrozen: enabled);
            return;
        }
        if (DateTime.UtcNow < nextRetry) return;
        if (reset) { reset = false; await PreserveVisibleFrames(); await ReleaseGraphics(preserveFrozen: true); }
        var frames = new List<Frame>();
        var dark = Design.Dark; var displayName = Display.PrimaryScreen.DeviceName;
        var presented = new List<Presented>();
        Exception? error = null;
        try
        {
            var backgroundChanged = false;
            // Acquire before locking WPF surfaces. An unchanged desktop must not
            // cause D3DImage locks, compositor work or material redraws.
            await OnGpu(() =>
            {
                capture ??= new DesktopCapture(displayName, duplicate: !qa);
                if (qa && synthetic is null)
                {
                    var r = capture.Output.DesktopCoordinates; var w = r.Right - r.Left; var h = r.Bottom - r.Top;
                    synthetic = capture.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                    var pixels = new byte[checked(w * h * 4)];
                    for (var y = 0; y < h; y++) for (var x = 0; x < w; x++) { var at = (y * w + x) * 4; pixels[at] = (byte)(40 + x / 32 % 2 * 100); pixels[at + 1] = (byte)(80 + y / 32 % 2 * 80); pixels[at + 2] = 80; pixels[at + 3] = 255; }
                    capture.Context.UpdateSubresource<byte>(pixels, synthetic, 0, (uint)w * 4); backgroundChanged = true;
                }
                if (!qa)
                {
                    try
                    {
                        using var frame = capture.Acquire(1);
                        if (frame is not null && (capture.FrameInfo.LastPresentTime != 0 || desktopCache is null))
                        {
                            if (desktopCache is null) desktopCache = capture.Device.CreateTexture2D(frame.Description with { BindFlags = BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.None });
                            capture.Context.CopyResource(desktopCache, frame); backgroundChanged = true;
                        }
                    }
                    finally { capture.Release(); }
                }
            });
            if (!backgroundChanged && visible.All(c => (c.Attached || c.FrozenMaterial) && !c.MaterialDirty))
            {
                await FreezeIdle(visible);
                // GPU disposal yields to the UI. Do not discard a newer wake
                // or pointer update received while that idle work was pending.
                if (qa && wakeAtStart == WakeCount && visible.All(c => c.FrozenMaterial && !c.MaterialDirty)) timer.Stop();
                return;
            }
            foreach (var client in visible)
            {
                if (client.Removed || !client.IsVisible || !client.Material.IsFrontBufferAvailable || !client.Material.TryLock(new Duration(TimeSpan.Zero))) continue;
                var dpi = (float)VisualTreeHelper.GetDpi(client).DpiScaleX;
                if (!Native.GetWindowRect(client.Handle, out var bounds)) { client.Material.Unlock(); continue; }
                frames.Add(new(client, client.Handle, bounds.Left, bounds.Top,
                    Math.Max(1, bounds.Right - bounds.Left), Math.Max(1, bounds.Bottom - bounds.Top), dpi, client.Pointer * dpi, client.MaterialDirty));
                client.MaterialDirty = false;
            }
            if (frames.Count == 0) return;
            await OnGpu(() =>
            {
                var frame = qa ? synthetic : desktopCache; if (capture is null || frame is null) return;
                var live = frames.FirstOrDefault(f => !f.Client.Removed); if (live is null) return;
                resources ??= new(capture.Device, capture.Context, capture.AdapterLuid, live.Hwnd, 1, 1);
                var hasFrame = backgroundChanged;
                foreach (var item in frames)
                {
                    if (item.Client.Removed) continue;
                    if (!states.TryGetValue(item.Client, out var state) || state.Width != item.Width || state.Height != item.Height)
                    {
                        // The old pointer was detached below before disposing its owner.
                        if (state is not null) { continue; }
                        state = new State { Gpu = new(capture.Device, capture.Context, capture.AdapterLuid, item.Hwnd, item.Width, item.Height, resources), Width = item.Width, Height = item.Height };
                        states[item.Client] = state;
                    }
                    var output = capture.Output.DesktopCoordinates;
                    var sw = output.Right - output.Left; var sh = output.Bottom - output.Top;
                    var rotation = capture.Output.Rotation switch { ModeRotation.Rotate90 => 90, ModeRotation.Rotate180 => 180, ModeRotation.Rotate270 => 270, _ => 0 };
                    var left = Math.Clamp(item.X - output.Left - 24, 0, sw - 1); var top = Math.Clamp(item.Y - output.Top - 24, 0, sh - 1);
                    var right = Math.Clamp(item.X - output.Left + item.Width + 24, left + 1, sw); var bottom = Math.Clamp(item.Y - output.Top + item.Height + 24, top + 1, sh);
                    var w = rotation == 0 ? right - left : frame is not null ? (int)frame.Description.Width : state.SourceWidth;
                    var h = rotation == 0 ? bottom - top : frame is not null ? (int)frame.Description.Height : state.SourceHeight;
                    if (w <= 0 || h <= 0) continue;
                    var sourceChanged = state.Source is null || state.SourceWidth != w || state.SourceHeight != h;
                    if (sourceChanged)
                    {
                        state.Source?.Dispose(); state.Source = null;
                        if (frame is null) continue;
                        state.Source = capture.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                        state.SourceWidth = w; state.SourceHeight = h;
                    }
                    if (!hasFrame && !item.Dirty && item.Client.Attached && !sourceChanged) continue;
                    if (frame is not null)
                    {
                        if (rotation == 0) capture.Context.CopySubresourceRegion(state.Source, 0, 0, 0, 0, frame, 0, new Box(left, top, 0, right, bottom, 1));
                        else capture.Context.CopyResource(state.Source, frame);
                    }
                    var uv = rotation == 0 ? new Vector4((float)(item.X - output.Left - left) / w, (float)(item.Y - output.Top - top) / h, (float)item.Width / w, (float)item.Height / h)
                        : new Vector4((float)(item.X - output.Left) / sw, (float)(item.Y - output.Top) / sh, (float)item.Width / sw, (float)item.Height / sh);
                    if (state.Source is null) continue;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    state.Gpu.Render(capture.Device, state.Source, uv, item.Pointer, dark, settings.VisualQuality == "reduced", 24 * item.Dpi, rotation);
                    state.Bounds = new Native.Rect { Left = item.X, Top = item.Y, Right = item.X + item.Width, Bottom = item.Y + item.Height };
                    if (renderTimes.Count >= 1024) renderTimes.Dequeue(); renderTimes.Enqueue(watch.Elapsed.TotalMilliseconds);
                    presented.Add(new(item, state.Gpu.Surface, true));
                }
            });
            foreach (var result in presented)
            {
                var client = result.Frame.Client;
                if (client.Removed || managementOpen || !client.IsVisible || !client.ExcludedFromCapture) continue;
                if (!Native.GetWindowRect(client.Handle, out var bounds) || bounds.Left != result.Frame.X || bounds.Top != result.Frame.Y || bounds.Right - bounds.Left != result.Frame.Width || bounds.Bottom - bounds.Top != result.Frame.Height)
                { client.MaterialDirty = true; continue; }
                if (!client.Attached) { client.Material.SetBackBuffer(D3DResourceType.IDirect3DSurface9, result.Surface); client.Attached = true; }
                client.MaterialImage.Source = client.Material; client.FrozenMaterial = false; client.LastMaterialFrame = DateTime.UtcNow; client.LastFrameDark = dark;
                client.Material.AddDirtyRect(new Int32Rect(0, 0, result.Frame.Width, result.Frame.Height));
                client.SetMaterialVisible(true, settings.VisualQuality == "reduced" ? "降低效果" : "强视觉"); RenderedFrames++;
            }
            if (presented.Count > 0) { Status = settings.VisualQuality == "reduced" ? "降低效果" : "强视觉"; failures = 0; }
        }
        catch (Exception ex)
        {
            error = ex; LastFailure = ex.ToString(); Status = "当前设备使用兼容模式"; failures++;
            nextRetry = DateTime.UtcNow.AddSeconds(Math.Min(30, failures * 5));
            foreach (var client in clients) if (!client.ShowRetained("兼容模式")) client.SetMaterialVisible(false, "兼容模式");
            if (failures >= 3) timer.Stop();
        }
        finally { foreach (var item in frames) item.Client.Material.Unlock(); }
        if (error is not null) await ReleaseGraphics(preserveFrozen: true);
        else
        {
            var resized = frames.Where(f => states.TryGetValue(f.Client, out var state) && (state.Width != f.Width || state.Height != f.Height)).Select(f => f.Client).ToArray();
            foreach (var client in resized) { if (!client.ShowRetained("等待底栏更新")) client.SetMaterialVisible(false, "等待底栏更新"); client.DetachMaterial(); client.MaterialDirty = true; }
            if (resized.Length > 0) await OnGpu(() => { foreach (var client in resized) if (states.Remove(client, out var state)) state.Dispose(); });
        }
    }
    private async Task FreezeIdle(LiquidWindow[] visible, bool force = false)
    {
        var wanted = visible.Where(c => c.Attached && !c.FrozenMaterial && (force || !c.MaterialDirty && DateTime.UtcNow - c.LastMaterialFrame > TimeSpan.FromMilliseconds(500))).ToArray();
        if (wanted.Length == 0) return;
        var snapshots = new List<Frozen>();
        await OnGpu(() =>
        {
            if (capture is null) return;
            foreach (var client in wanted)
            {
                if (!states.TryGetValue(client, out var state)) continue;
                var pixels = capture.ReadPixels(state.Gpu.Texture);
                try
                {
                    var bitmap = BitmapSource.Create(state.Width, state.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, state.Width * 4);
                    bitmap.Freeze(); snapshots.Add(new(client, bitmap, state.Bounds));
                }
                finally { Array.Clear(pixels); }
            }
        });
        foreach (var snapshot in snapshots)
        {
            var client = snapshot.Client;
            if (client.Removed || !force && client.MaterialDirty || !client.IsVisible) continue;
            client.RetainFrame(snapshot.Bitmap, snapshot.Bounds);
        }
        // Release presentation devices after all currently visible materials have
        // a static image. Keep only the capture device/desktop texture for changes.
        if (visible.All(c => c.FrozenMaterial || c.Removed))
        {
            foreach (var client in clients.Where(c => c.Attached)) { client.DetachMaterial(); if (!client.FrozenMaterial) client.SetMaterialVisible(false, "兼容模式"); }
            await OnGpu(() => { foreach (var state in states.Values) state.Dispose(); states.Clear(); resources?.Dispose(); resources = null; });
        }
    }
    private async Task PreserveVisibleFrames()
    {
        try { await FreezeIdle(clients.Where(c => c.IsVisible && !c.Removed).ToArray(), force: true); }
        catch (Exception ex) { LastFailure = ex.ToString(); }
    }
    private async Task ReleaseGraphics(bool preserveFrozen = false)
    {
        releasing = true;
        try
        {
            foreach (var client in clients)
            {
                client.DetachMaterial();
                if (preserveFrozen && client.ShowRetained(client.EffectiveVisualMode)) client.FrozenMaterial = true;
                else { client.ClearRetained(); client.MaterialImage.Source = client.Material; }
            }
            await OnGpu(() => { foreach (var state in states.Values) state.Dispose(); states.Clear(); resources?.Dispose(); resources = null; synthetic?.Dispose(); synthetic = null; desktopCache?.Dispose(); desktopCache = null; capture?.Dispose(); capture = null; });
        }
        finally
        {
            releasing = false;
            if (!closing && !managementOpen && sessionActive && failures < 3 && preferences().VisualQuality != "off" && GlassEnvironment.Transparency && clients.Any(c => c.ExcludedFromCapture && c.IsVisible && !c.Removed && !Covered(c))) Wake();
        }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true; timer.Stop(); Native.UnhookWinEvent(hook); hook = 0;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        await managementChange; await pending; await ReleaseGraphics();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
