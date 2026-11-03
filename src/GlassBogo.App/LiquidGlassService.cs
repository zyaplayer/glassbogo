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

// One serialized GPU queue; surfaces on each display share that output's capture.
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
    private readonly Dictionary<string, OutputGpu> outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime At, int Count)> retries = new(StringComparer.OrdinalIgnoreCase);
    private sealed class OutputGpu(DesktopCapture capture) : IDisposable
    {
        internal readonly DesktopCapture Capture = capture;
        internal ID3D11Texture2D? Synthetic, DesktopCache;
        internal LiquidGlassGpu? Resources;
        internal bool Changed;
        internal int SceneVersion = -1;
        public void Dispose()
        {
            Resources?.Dispose();
            Synthetic?.Dispose();
            DesktopCache?.Dispose();
            Capture.Dispose();
        }
    }
    private readonly Queue<double> renderTimes = new();
    private readonly Native.WinEventProc foregroundCallback;
    private nint hook;
    private Task pending = Task.CompletedTask;
    private Task managementChange = Task.CompletedTask;
    private bool managementOpen;
    private bool capturingManagement;
    private bool managementBackgroundReady;
    private DateTime managementRetryAt;
    private long generation;
    private bool wakeQueued;
    private int managementVersion;
    internal bool ScreenshotReady
    {
        get; private set;
    }
    private bool closing, reset, releasing;
    private bool sessionActive = true;
    internal string Status { get; private set; } = "等待窗口";
    internal string? LastFailure
    {
        get; private set;
    }
    internal long RenderedFrames
    {
        get; private set;
    }
    internal long TickCount
    {
        get; private set;
    }
    internal long WakeCount
    {
        get; private set;
    }
    internal long RejectedFrames
    {
        get; private set;
    }
    internal Func<Task>? BeforePresentForQa;
    internal int FailCapturesForQa;
    private int qaSceneVersion;
    private uint? qaBackground;
    private readonly Dictionary<string, uint> qaOutputColors = new(StringComparer.OrdinalIgnoreCase);
    internal void SetQaBackground(uint? rgb)
    {
        if (!qa)
            throw new InvalidOperationException("仅生成数据验证可替换背景。");
        qaOutputColors.Clear();
        qaBackground = rgb;
        qaSceneVersion++;
        generation++;
        foreach (var client in clients)
            client.MaterialDirty = true;
        Wake();
    }
    internal void SetQaOutputColor(string display, uint rgb)
    {
        if (!qa)
            throw new InvalidOperationException("仅生成数据验证可替换背景。");
        qaOutputColors[display] = rgb;
        qaSceneVersion++;
        generation++;
        foreach (var client in clients)
            client.MaterialDirty = true;
        Wake();
    }
    internal string Presentation => "GPU material / immutable BGRA bitmap";
    internal int LiveSurfaces => states.Count;
    internal int ClientCount => clients.Count;
    internal string DebugState => $"management={managementOpen},releasing={releasing},generation={generation},outputs={outputs.Count},rejected={RejectedFrames}; " + string.Join("; ", clients.Select(c => $"{c.Title}:visible={c.IsVisible},excluded={c.ExcludedFromCapture},covered={Covered(c)},dirty={c.MaterialDirty},bounds={c.ActualWidth}x{c.ActualHeight}"));
    private sealed class State : IDisposable
    {
        internal required LiquidGlassGpu Gpu;
        internal ID3D11Texture2D? Source;
        internal required int Width, Height;
        internal required string DisplayName;
        internal int SourceWidth, SourceHeight;
        internal Native.Rect Bounds;
        public void Dispose()
        {
            Source?.Dispose();
            Gpu.Dispose();
        }
    }
    private sealed record Frame(LiquidWindow Client, nint Hwnd, int X, int Y, int Width, int Height, float Dpi, Vector2 Pointer, bool Dirty, string DisplayName, GlassFrameStamp Stamp);
    private sealed record Presented(Frame Frame, BitmapSource Bitmap);
    internal sealed record Metrics(string Adapter, int Surfaces, double? LocalMemoryMiB, double? NonLocalMemoryMiB, double? MaterialProcessingP95Milliseconds, string? QueryError);
    internal async Task<Metrics> GetMetrics()
    {
        Metrics result = new("", 0, null, null, null, "No active graphics device");
        await OnGpu(() =>
        {
            if (outputs.Count == 0)
                return;
            double? local = null, shared = null;
            string? error = null;
            try
            {
                local = shared = 0;
                foreach (var capture in outputs.Values.Select(o => o.Capture).DistinctBy(c => c.AdapterLuid))
                {
                    using var dxgiDevice = capture.Device.QueryInterface<IDXGIDevice>();
                    dxgiDevice.GetAdapter(out var adapter).CheckError();
                    using (adapter)
                    using (var info = adapter.QueryInterface<IDXGIAdapter3>())
                    {
                        local += info.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local).CurrentUsage / 1048576d;
                        shared += info.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal).CurrentUsage / 1048576d;
                    }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            var times = renderTimes.Order().ToArray();
            result = new(string.Join("; ", outputs.Values.Select(o => o.Capture.AdapterName).Distinct()), states.Count, local, shared, times.Length == 0 ? null : times[(int)Math.Floor((times.Length - 1) * .95)], error);
        });
        return result;
    }

    internal LiquidGlassService(Func<Preferences> preferences, bool qa = false)
    {
        this.preferences = preferences;
        this.qa = qa;
        dispatcher = Dispatcher.CurrentDispatcher;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        timer.Tick += (_, _) => StartTick();
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
        if (closing || !client.UsesGlass || clients.Contains(client))
            return;
        if (clients.Count >= 8)
        {
            client.SetMaterialVisible(false, "兼容模式");
            return;
        }
        clients.Add(client);
        client.ExcludedFromCapture = (!managementOpen || capturingManagement) && (qa || SetWindowDisplayAffinity(client.Handle, 0x11));
        if (managementOpen && !capturingManagement)
            SetWindowDisplayAffinity(client.Handle, 0);
        if (!client.ExcludedFromCapture)
            client.SetMaterialVisible(false, "兼容模式");
        Wake();
    }
    internal void Remove(LiquidWindow client)
    {
        // Wait for any queued material work before removing its renderer.
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
        if (closing || releasing || clients.All(c => !c.IsVisible || c.Removed))
            return;
        timer.Start();
        if (wakeQueued)
            return;
        wakeQueued = true;
        dispatcher.BeginInvoke(() => { wakeQueued = false; StartTick(); }, DispatcherPriority.Background);
    }
    private void StartTick()
    {
        if (managementOpen && managementRetryAt != default)
        {
            if (!closing && !releasing && managementChange.IsCompleted && DateTime.UtcNow >= managementRetryAt)
                Refresh();
            return;
        }
        if (pending.IsCompleted && !closing && !releasing)
            pending = Tick();
    }
    internal void SetManagementOpen(bool open)
    {
        if (closing || managementOpen == open)
            return;
        managementOpen = open;
        managementBackgroundReady = false;
        managementRetryAt = default;
        generation++;
        ScreenshotReady = false;
        timer.Stop();
        var version = ++managementVersion;
        var previous = managementChange;
        managementChange = ChangeManagement(previous, version);
    }
    private async Task ChangeManagement(Task previous, int version)
    {
        try
        {
            await previous;
            await pending;
            if (closing || version != managementVersion)
                return;
            if (managementOpen)
            {
                await FreezeIdle(clients.Where(c => c.IsVisible && !c.Removed).ToArray(), force: true);
                await OnGpu(ReleaseSurfaces);
                if (closing || version != managementVersion)
                    return;
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
                    if (!client.ExcludedFromCapture)
                    {
                        client.ClearRetained();
                        client.SetMaterialVisible(false, "兼容模式");
                    }
                }
                reset = true;
                Wake();
            }
        }
        catch (Exception ex)
        {
            LastFailure = ex.ToString();
            foreach (var client in clients)
                client.SetMaterialVisible(false, "兼容模式");
            Status = "兼容模式";
        }
    }
    internal void Refresh()
    {
        if (closing)
            return;
        generation++;
        foreach (var client in clients.Where(c => !c.Removed))
        {
            client.ThemeChanged();
            if (managementOpen)
            {
                if (preferences().VisualQuality == "off" || !GlassEnvironment.Transparency || client.LastFrameDark != Design.Dark)
                {
                    client.ClearRetained();
                    client.SetMaterialVisible(false, preferences().VisualQuality == "off" ? "已关闭" : "兼容模式");
                }
                continue;
            }
            client.ExcludedFromCapture = qa || SetWindowDisplayAffinity(client.Handle, 0x11);
            if (!client.ExcludedFromCapture)
                client.SetMaterialVisible(false, "兼容模式");
        }
        if (managementOpen && sessionActive && preferences().VisualQuality != "off" && GlassEnvironment.Transparency)
        {
            ScreenshotReady = false;
            Status = "正在更新切屏背景";
            managementBackgroundReady = false;
            managementRetryAt = default;
            timer.Stop();
            var version = ++managementVersion;
            managementChange = RefreshManagementBackground(managementChange, version);
            return;
        }
        reset = !managementOpen;
        Wake();
    }
    private async Task RefreshManagementBackground(Task previous, int version)
    {
        var restore = new List<(Window Window, nint Handle, uint Affinity)>();
        var errors = new List<string>();
        var restored = true;
        try
        {
            await previous;
            await pending;
            if (closing || !managementOpen || version != managementVersion)
                return;
            releasing = capturingManagement = true;
            foreach (Window window in Application.Current.Windows)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (!window.IsVisible || handle == 0)
                    continue;
                var queried = GetWindowDisplayAffinity(handle, out var affinity);
                if (!queried)
                    throw new InvalidOperationException("无法保存窗口采样状态。");
                restore.Add((window, handle, affinity));
                if (!SetWindowDisplayAffinity(handle, 0x11) || !GetWindowDisplayAffinity(handle, out var excluded) || excluded != 0x11)
                    throw new InvalidOperationException("无法安全排除窗口，保留原材质并重试。");
            }
            var displays = Display.AllScreens.Select(d => d.DeviceName).ToArray();
            var palette = new Dictionary<string, uint>(qaOutputColors, StringComparer.OrdinalIgnoreCase);
            var color = qaBackground;
            var sceneVersion = qaSceneVersion;
            await OnGpu(() =>
            {
                if (!qa)
                    Marshal.ThrowExceptionForHR(DwmFlush());
                foreach (var stale in outputs.Keys.Where(d => !displays.Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray())
                    DropOutput(stale);
                foreach (var display in displays)
                {
                    try
                    {
                        DropOutput(display);
                        if (qa && FailCapturesForQa > 0)
                        {
                            FailCapturesForQa--;
                            throw new IOException("QA management background capture interruption");
                        }
                        var output = new OutputGpu(new DesktopCapture(display, duplicate: !qa));
                        outputs[display] = output;
                        var capture = output.Capture;
                        if (qa)
                        {
                            var r = capture.Output.DesktopCoordinates;
                            var w = r.Right - r.Left;
                            var h = r.Bottom - r.Top;
                            output.Synthetic = capture.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                            var rgb = palette.TryGetValue(display, out var perDisplay) ? perDisplay : color;
                            var pixels = new byte[checked(w * h * 4)];
                            for (var y = 0; y < h; y++)
                            for (var x = 0; x < w; x++)
                            {
                                var at = (y * w + x) * 4;
                                pixels[at] = rgb is { } b ? (byte)b : (byte)(40 + x / 32 % 2 * 100);
                                pixels[at + 1] = rgb is { } g ? (byte)(g >> 8) : (byte)(80 + y / 32 % 2 * 80);
                                pixels[at + 2] = rgb is { } red ? (byte)(red >> 16) : (byte)80;
                                pixels[at + 3] = 255;
                            }
                            capture.Context.UpdateSubresource<byte>(pixels, output.Synthetic, 0, (uint)w * 4);
                            Array.Clear(pixels);
                            output.SceneVersion = sceneVersion;
                        }
                        else
                        {
                            for (var attempt = 0; attempt < 8 && output.DesktopCache is null; attempt++)
                            {
                                try
                                {
                                    using var frame = capture.Acquire(25);
                                    if (frame is null)
                                        continue;
                                    output.DesktopCache = capture.Device.CreateTexture2D(frame.Description with
                                    {
                                        BindFlags = BindFlags.ShaderResource,
                                        MiscFlags = ResourceOptionFlags.None
                                    });
                                    capture.Context.CopyResource(output.DesktopCache, frame);
                                    // Retain the clean capture lease until the copy is
                                    // complete, before restoring screenshot eligibility.
                                    using var copied = capture.Device.CreateQuery(new QueryDescription(Vortice.Direct3D11.QueryType.Event));
                                    capture.Context.End(copied); capture.Context.Flush();
                                    var wait = System.Diagnostics.Stopwatch.StartNew();
                                    while (!capture.Context.GetData(copied, out int complete) || complete == 0)
                                    {
                                        if (wait.ElapsedMilliseconds > 1000) throw new TimeoutException("切屏背景复制未完成，保留原材质并重试。");
                                        Thread.Sleep(1);
                                    }
                                }
                                finally { capture.Release(); }
                            }
                            if (output.DesktopCache is null)
                                throw new IOException("新显示器背景尚未就绪。");
                        }
                        output.Changed = true;
                        retries.Remove(display);
                    }
                    catch (Exception ex) { errors.Add(display + ": " + ex.Message); DropOutput(display); }
                }
            });
        }
        catch (Exception ex) { errors.Add(ex.Message); }
        finally
        {
            foreach (var item in restore)
                if (IsWindow(item.Handle) && GetWindowThreadProcessId(item.Handle, out var owner) != 0 && owner == Environment.ProcessId && item.Window is not LiquidWindow { UsesGlass: true })
                    restored &= SetWindowDisplayAffinity(item.Handle, item.Affinity);
            foreach (var client in clients.Where(c => !c.Removed && IsWindow(c.Handle)))
            {
                var affinity = managementOpen ? 0u : 0x11u;
                var allowed = SetWindowDisplayAffinity(client.Handle, affinity);
                restored &= allowed || qa;
                client.ExcludedFromCapture = !managementOpen && (allowed || qa);
                client.MaterialDirty = true;
            }
            capturingManagement = releasing = false;
            if (!closing && managementOpen && version == managementVersion)
            {
                managementBackgroundReady = restored && errors.Count == 0;
                ScreenshotReady = false;
                if (!managementBackgroundReady)
                {
                    LastFailure = string.Join("; ", errors);
                    Status = "正在重试切屏背景";
                    managementRetryAt = DateTime.UtcNow.AddMilliseconds(500);
                }
                else
                {
                    LastFailure = null;
                    Status = "正在呈现切屏玻璃";
                }
            }
            if (!closing)
                Wake();
        }
    }
    private void Post(Action action)
    {
        if (closing || dispatcher.HasShutdownStarted)
            return;
        try
        {
            dispatcher.BeginInvoke(() => { if (!closing) action(); });
        }
        catch (InvalidOperationException) { }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Post(Refresh);
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Post(Refresh);
    private void SessionChanged(object sender, SessionSwitchEventArgs e) => Post(() =>
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            sessionActive = false;
            Wake();
        }
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            sessionActive = true;
            Refresh();
        }
    });
    private async Task OnGpu(Action action)
    {
        await gpuGate.WaitAsync();
        try
        {
            await Task.Run(action);
        }
        finally { gpuGate.Release(); }
    }
    private static bool Covered(LiquidWindow client)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == 0 || fg == client.Handle || DesktopLayer.IsDesktop(fg))
            return false;
        return Native.GetWindowRect(client.Handle, out var own) && Native.GetWindowRect(fg, out var other)
            && other.Left <= own.Left && other.Top <= own.Top && other.Right >= own.Right && other.Bottom >= own.Bottom;
    }
    private GlassFrameStamp Stamp(LiquidWindow client, Preferences settings)
    {
        if (!Native.GetWindowRect(client.Handle, out var r))
            throw new InvalidOperationException("无法读取玻璃表面位置。");
        return new(generation, client.MaterialRevision, Display.FromHandle(client.Handle).DeviceName, new(r.Left, r.Top, r.Right, r.Bottom), VisualTreeHelper.GetDpi(client).DpiScaleX, Design.Dark, settings.VisualQuality);
    }
    private bool TryStamp(LiquidWindow client, Preferences settings, out GlassFrameStamp stamp)
    {
        try
        {
            stamp = Stamp(client, settings);
            return stamp.Bounds.Width > 0 && stamp.Bounds.Height > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { stamp = default; return false; }
    }
    private void ReleaseSurfaces()
    {
        foreach (var state in states.Values)
            state.Dispose();
        states.Clear();
    }
    private void DropOutput(string display)
    {
        foreach (var pair in states.Where(p => p.Value.DisplayName == display).ToArray())
        {
            pair.Value.Dispose();
            states.Remove(pair.Key);
        }
        if (outputs.Remove(display, out var output))
            output.Dispose();
    }
    private async Task Tick()
    {
        TickCount++;
        if (releasing || closing)
            return;
        var wakeAtStart = WakeCount;
        var settings = preferences();
        var enabled = sessionActive && settings.VisualQuality != "off" && GlassEnvironment.Transparency;
        timer.Interval = TimeSpan.FromMilliseconds(settings.VisualQuality == "reduced" ? 100 : 67);
        var visible = clients.Where(c => (managementOpen || c.ExcludedFromCapture) && c.IsVisible && !c.Removed && c.WindowState != WindowState.Minimized && !Covered(c)).ToArray();
        if (!enabled || visible.Length == 0)
        {
            timer.Stop();
            if (enabled && managementOpen && managementBackgroundReady) { ScreenshotReady = true; managementBackgroundReady = false; }
            if (enabled)
                await PreserveVisibleFrames();
            else
                foreach (var client in clients)
                {
                    client.ClearRetained();
                    client.MaterialImage.Source = null;
                    client.SetMaterialVisible(false, settings.VisualQuality == "off" ? "已关闭" : "兼容模式");
                }
            if (managementOpen && enabled)
                await OnGpu(ReleaseSurfaces);
            else
                await ReleaseGraphics(preserveFrozen: enabled);
            Status = !enabled ? settings.VisualQuality == "off" ? "已关闭" : "兼容模式" : "待机";
            return;
        }
        if (reset && !managementOpen)
        {
            reset = false;
            await PreserveVisibleFrames();
            await ReleaseGraphics(preserveFrozen: true);
        }
        var frames = new List<Frame>();
        foreach (var client in visible)
        {
            if (!TryStamp(client, settings, out var stamp))
            {
                client.MaterialDirty = true;
                continue;
            }
            var r = stamp.Bounds;
            var dpi = (float)stamp.Scale;
            frames.Add(new(client, client.Handle, r.Left, r.Top, r.Width, r.Height, dpi, client.Pointer * dpi, client.MaterialDirty, stamp.Display, stamp));
        }
        var presented = new List<Presented>();
        var errors = new List<string>();
        var backgroundChanged = false;
        var frozenBackground = managementOpen;
        var captureGeneration = generation;
        var palette = new Dictionary<string, uint>(qaOutputColors, StringComparer.OrdinalIgnoreCase);
        try
        {
            await OnGpu(() =>
            {
                foreach (var display in frames.Select(f => f.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (frozenBackground)
                    {
                        if (outputs.TryGetValue(display, out var cached))
                            cached.Changed = false;
                        continue;
                    }
                    if (retries.TryGetValue(display, out var retry) && DateTime.UtcNow < retry.At)
                        continue;
                    try
                    {
                        if (qa && FailCapturesForQa > 0)
                        {
                            FailCapturesForQa--;
                            throw new IOException("QA transient capture interruption");
                        }
                        if (!outputs.TryGetValue(display, out var output))
                            outputs[display] = output = new(new DesktopCapture(display, duplicate: !qa));
                        var capture = output.Capture;
                        output.Changed = false;
                        if (qa && (output.Synthetic is null || output.SceneVersion != qaSceneVersion))
                        {
                            var r = capture.Output.DesktopCoordinates;
                            var w = r.Right - r.Left;
                            var h = r.Bottom - r.Top;
                            output.Synthetic ??= capture.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                            var pixels = new byte[checked(w * h * 4)];
                            var rgb = palette.TryGetValue(display, out var outputColor) ? outputColor : qaBackground;
                            for (var y = 0; y < h; y++)
                                for (var x = 0; x < w; x++)
                                {
                                    var at = (y * w + x) * 4;
                                    pixels[at] = rgb is { } color ? (byte)color : (byte)(40 + x / 32 % 2 * 100);
                                    pixels[at + 1] = rgb is { } green ? (byte)(green >> 8) : (byte)(80 + y / 32 % 2 * 80);
                                    pixels[at + 2] = rgb is { } red ? (byte)(red >> 16) : (byte)80;
                                    pixels[at + 3] = 255;
                                }
                            capture.Context.UpdateSubresource<byte>(pixels, output.Synthetic, 0, (uint)w * 4);
                            Array.Clear(pixels);
                            output.SceneVersion = qaSceneVersion;
                            output.Changed = true;
                        }
                        if (!qa)
                        {
                            try
                            {
                                using var acquired = capture.Acquire(1);
                                if (acquired is not null && captureGeneration == Volatile.Read(ref generation) && !Volatile.Read(ref managementOpen) && (capture.FrameInfo.LastPresentTime != 0 || output.DesktopCache is null))
                                {
                                    var d = acquired.Description;
                                    if (output.DesktopCache is not null && (output.DesktopCache.Description.Width != d.Width || output.DesktopCache.Description.Height != d.Height || output.DesktopCache.Description.Format != d.Format))
                                    {
                                        foreach (var pair in states.Where(p => p.Value.DisplayName == display).ToArray())
                                        {
                                            pair.Value.Dispose();
                                            states.Remove(pair.Key);
                                        }
                                        output.DesktopCache.Dispose();
                                        output.DesktopCache = null;
                                    }
                                    output.DesktopCache ??= capture.Device.CreateTexture2D(d with
                                    {
                                        BindFlags = BindFlags.ShaderResource,
                                        MiscFlags = ResourceOptionFlags.None
                                    });
                                    capture.Context.CopyResource(output.DesktopCache, acquired);
                                    output.Changed = true;
                                }
                            }
                            finally { capture.Release(); }
                        }
                        retries.Remove(display);
                        backgroundChanged |= output.Changed;
                    }
                    catch (Exception ex)
                    {
                        var count = retries.TryGetValue(display, out var old) ? old.Count + 1 : 1;
                        retries[display] = (DateTime.UtcNow.AddMilliseconds(Math.Min(2000, 250 * count)), count);
                        errors.Add(display + ": " + ex.Message);
                        DropOutput(display);
                    }
                }
                foreach (var item in frames)
                {
                    if (!outputs.TryGetValue(item.DisplayName, out var output))
                        continue;
                    var capture = output.Capture;
                    var source = qa ? output.Synthetic : output.DesktopCache;
                    if (source is null)
                        continue;
                    if (!item.Dirty && !output.Changed && (item.Client.Attached || item.Client.FrozenMaterial))
                        continue;
                    try
                    {
                        output.Resources ??= new(capture.Device, capture.Context, capture.AdapterLuid, item.Hwnd, 1, 1, sharedPresentation: false);
                        if (!states.TryGetValue(item.Client, out var state) || state.Width != item.Width || state.Height != item.Height || state.DisplayName != item.DisplayName)
                        {
                            state?.Dispose();
                            state = new State { Gpu = new(capture.Device, capture.Context, capture.AdapterLuid, item.Hwnd, item.Width, item.Height, output.Resources), Width = item.Width, Height = item.Height, DisplayName = item.DisplayName };
                            states[item.Client] = state;
                        }
                        var area = capture.Output.DesktopCoordinates;
                        var sw = area.Right - area.Left;
                        var sh = area.Bottom - area.Top;
                        var rotation = capture.Output.Rotation switch
                        {
                            ModeRotation.Rotate90 => 90,
                            ModeRotation.Rotate180 => 180,
                            ModeRotation.Rotate270 => 270,
                            _ => 0
                        };
                        var margin = (int)Math.Ceiling(32 * item.Dpi);
                        var left = Math.Clamp(item.X - area.Left - margin, 0, sw - 1);
                        var top = Math.Clamp(item.Y - area.Top - margin, 0, sh - 1);
                        var right = Math.Clamp(item.X - area.Left + item.Width + margin, left + 1, sw);
                        var bottom = Math.Clamp(item.Y - area.Top + item.Height + margin, top + 1, sh);
                        var w = rotation == 0 ? right - left : (int)source.Description.Width;
                        var h = rotation == 0 ? bottom - top : (int)source.Description.Height;
                        if (state.Source is null || state.SourceWidth != w || state.SourceHeight != h)
                        {
                            state.Source?.Dispose();
                            state.Source = capture.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.ShaderResource));
                            state.SourceWidth = w;
                            state.SourceHeight = h;
                        }
                        if (rotation == 0)
                            capture.Context.CopySubresourceRegion(state.Source, 0, 0, 0, 0, source, 0, new Box(left, top, 0, right, bottom, 1));
                        else
                            capture.Context.CopyResource(state.Source, source);
                        var uv = rotation == 0 ? new Vector4((float)(item.X - area.Left - left) / w, (float)(item.Y - area.Top - top) / h, (float)item.Width / w, (float)item.Height / h) : new Vector4((float)(item.X - area.Left) / sw, (float)(item.Y - area.Top) / sh, (float)item.Width / sw, (float)item.Height / sh);
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        state.Gpu.Render(capture.Device, state.Source, uv, item.Pointer, item.Stamp.Dark, settings.VisualQuality == "reduced", 24 * item.Dpi, rotation);
                        var pixels = state.Gpu.ReadFrame(capture.Device);
                        var bitmap = BitmapSource.Create(item.Width, item.Height, 96 * item.Dpi, 96 * item.Dpi, PixelFormats.Pbgra32, null, pixels, item.Width * 4);
                        bitmap.Freeze();
                        state.Bounds = new Native.Rect { Left = item.X, Top = item.Y, Right = item.X + item.Width, Bottom = item.Y + item.Height };
                        if (renderTimes.Count >= 1024)
                            renderTimes.Dequeue();
                        renderTimes.Enqueue(watch.Elapsed.TotalMilliseconds);
                        presented.Add(new(item, bitmap));
                    }
                    catch (Exception ex)
                    {
                        errors.Add(item.DisplayName + ": " + ex.Message);
                        DropOutput(item.DisplayName);
                        retries[item.DisplayName] = (DateTime.UtcNow.AddMilliseconds(500), 1);
                    }
                }
            });
            if (qa && presented.Count > 0 && BeforePresentForQa is { } pause)
            {
                BeforePresentForQa = null;
                await pause();
            }
            var committedCount = 0;
            foreach (var result in presented)
            {
                var client = result.Frame.Client;
                if (closing || client.Removed || !client.IsVisible || frozenBackground != managementOpen || !TryStamp(client, preferences(), out var currentStamp) || result.Frame.Stamp != currentStamp)
                {
                    RejectedFrames++;
                    if (!client.Removed)
                        client.MaterialDirty = true;
                    continue;
                }
                if (!managementOpen && !client.ExcludedFromCapture)
                {
                    client.MaterialDirty = true;
                    continue;
                }
                client.CommitFrame(result.Bitmap, result.Frame.Stamp, settings.VisualQuality == "reduced" ? "降低效果" : "强视觉");
                client.MaterialDirty = false;
                RenderedFrames++;
                committedCount++;
            }
            if (errors.Count > 0)
            {
                LastFailure = string.Join("; ", errors);
                Status = "保留完整画面，正在重试采样";
            }
            else if (committedCount > 0 && retries.Count == 0)
            {
                LastFailure = null;
                Status = managementOpen ? "背景已冻结，材质可随窗口更新" : settings.VisualQuality == "reduced" ? "降低效果" : "强视觉";
            }
            if (managementOpen && managementBackgroundReady && visible.All(c => !c.MaterialDirty && c.CommittedStamp is { } stamp && TryStamp(c, preferences(), out var current) && stamp == current))
            {
                ScreenshotReady = true; managementBackgroundReady = false; Status = "切屏玻璃已就绪，可截图";
            }
            if (!backgroundChanged && visible.All(c => !c.MaterialDirty && (c.Attached || c.FrozenMaterial)))
            {
                await FreezeIdle(visible);
                timer.Interval = TimeSpan.FromMilliseconds(150);
                if ((qa || managementOpen) && retries.Count == 0 && wakeAtStart == WakeCount && visible.All(c => c.FrozenMaterial && !c.MaterialDirty))
                    timer.Stop();
            }
            if (managementOpen && presented.Count == 0 && frames.All(f => !outputs.ContainsKey(f.DisplayName)))
                timer.Stop();
        }
        catch (Exception ex)
        {
            LastFailure = ex.ToString();
            Status = "保留完整画面，正在重试采样";
            await PreserveVisibleFrames();
            await ReleaseGraphics(preserveFrozen: true);
        }
    }
    private async Task FreezeIdle(LiquidWindow[] visible, bool force = false)
    {
        foreach (var client in visible)
        {
            if (!client.Attached || client.FrozenMaterial || client.Material is not { } bitmap || client.CommittedStamp is not { } stamp || !force && (client.MaterialDirty || DateTime.UtcNow - client.LastMaterialFrame < TimeSpan.FromMilliseconds(500)))
                continue;
            if (!Native.GetWindowRect(client.Handle, out var r) || stamp.Bounds != new PixelRect(r.Left, r.Top, r.Right, r.Bottom) || stamp.Dark != Design.Dark)
                continue;
            client.RetainFrame(bitmap, r);
        }
        if (visible.All(c => c.FrozenMaterial || c.Removed))
            await OnGpu(ReleaseSurfaces);
    }
    private async Task PreserveVisibleFrames()
    {
        try
        {
            await FreezeIdle(clients.Where(c => c.IsVisible && !c.Removed).ToArray(), force: true);
        }
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
                if (preserveFrozen && client.ShowRetained(client.EffectiveVisualMode))
                    client.FrozenMaterial = true;
                else if (preserveFrozen && client.Material is not null && client.LastFrameDark == Design.Dark)
                {
                    // A complete CPU-owned frame can outlive the graphics device.
                    // Hold it during geometry recovery instead of exposing an empty image.
                    client.ClearRetained();
                    client.MaterialDirty = true;
                    client.SetMaterialVisible(true, client.EffectiveVisualMode);
                }
                else
                {
                    client.ClearRetained();
                    client.MaterialImage.Source = null;
                    client.SetMaterialVisible(false, preferences().VisualQuality == "off" ? "已关闭" : "兼容模式");
                }
            }
            await OnGpu(() => { ReleaseSurfaces(); foreach (var output in outputs.Values) output.Dispose(); outputs.Clear(); });
        }
        finally
        {
            releasing = false;
            if (!closing && !managementOpen && sessionActive && preferences().VisualQuality != "off" && GlassEnvironment.Transparency && clients.Any(c => c.ExcludedFromCapture && c.IsVisible && !c.Removed && !Covered(c)))
                Wake();
        }
    }
    public async ValueTask DisposeAsync()
    {
        closing = true;
        timer.Stop();
        Native.UnhookWinEvent(hook);
        hook = 0;
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.SessionSwitch -= SessionChanged;
        await managementChange;
        await pending;
        await ReleaseGraphics();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
