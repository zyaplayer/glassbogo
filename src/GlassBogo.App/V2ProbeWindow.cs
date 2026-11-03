using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Vortice.Direct3D11;

namespace GlassBogo.App;

// This window exists only in the explicitly launched preflight. No production data.
internal sealed class V2ProbeWindow : Window
{
    internal readonly D3DImage Backdrop = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Action<string> status;
    private DesktopCapture? capture;
    private LiquidGlassGpu? renderer;
    private ID3D11Texture2D? lastFrame;
    private Task pending = Task.CompletedTask;
    private Task initialization = Task.CompletedTask;
    private bool closing, attached, dirty = true;
    private Vector2 pointer = new(-1000, -1000);
    private DesktopLayer? layer;
    internal bool Reduced { get; set; }
    internal bool Dark { get; set; }
    internal int RenderedFrames { get; private set; }
    internal int DesktopRestores => layer?.DesktopRestores ?? 0;
    internal nint Handle => new WindowInteropHelper(this).Handle;
    internal bool ExclusionApplied { get; private set; }
    internal string? Failure { get; private set; }
    internal bool Ready => renderer is not null && capture is not null;
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task FullyClosed => closed.Task;

    internal V2ProbeWindow(Action<string> status, Action returnToControls)
    {
        this.status = status;
        Title = "GlassBogo 2.0 · 独立玻璃验证";
        Width = 472; Height = 320;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = false;
        var grid = new Grid();
        grid.Children.Add(new Image { Source = Backdrop, Stretch = Stretch.Fill, IsHitTestVisible = false });
        var content = new StackPanel { Margin = new(24) };
        content.Children.Add(new TextBlock { Text = "Liquid Glass · 前置验证", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        content.Children.Add(new TextBlock { Text = "真实背景采样 / 高光 / 边缘折射\n本窗口不是已交付的 Dock。", FontSize = 13, Foreground = Brushes.White, Margin = new(0, 8, 0, 0) });
        var returnButton = new Button { Content = "返回验证控制面板", Margin = new(0, 140, 0, 0), Padding = new(10, 6, 10, 6) };
        returnButton.Click += (_, _) => returnToControls();
        content.Children.Add(returnButton);
        grid.Children.Add(content);
        // Deliberately distinct marker. It must disappear from the captured frame,
        // revealing the reference color beneath it, not turn into a black square.
        grid.Children.Add(new Border { Width = 40, Height = 40, Background = new SolidColorBrush(Color.FromRgb(240, 0, 200)),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(32, 96, 0, 0) });
        Content = grid;
        MouseMove += (_, e) => { var p = e.GetPosition(this); pointer = new((float)p.X, (float)p.Y); dirty = true; };
        MouseLeave += (_, _) => { pointer = new(-1000, -1000); dirty = true; };
        LocationChanged += (_, _) => dirty = true;
        timer.Tick += (_, _) => { if (pending.IsCompleted) pending = Frame(); };
        IsVisibleChanged += (_, _) => { if (!closing && IsVisible && Ready) { dirty = true; timer.Start(); } else timer.Stop(); };
        Closed += async (_, _) =>
        {
            closing = true; timer.Stop(); layer?.Dispose();
            try
            {
                try { await initialization; } catch { /* Initialization failure is recorded by the controller. */ }
                await pending;
                if (attached) { Backdrop.Lock(); try { Backdrop.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0); } finally { Backdrop.Unlock(); } }
                // UI backbuffer detached before native resources are released.
                await Task.Run(() => { lastFrame?.Dispose(); renderer?.Dispose(); capture?.Dispose(); });
            }
            finally { closed.TrySetResult(); }
        };
    }

    internal Task Initialize() => initialization = InitializeCore();

    private async Task InitializeCore()
    {
        ExclusionApplied = SetExclusion(true);
        if (!ExclusionApplied) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "采样排除设置失败。");
        layer = new DesktopLayer(this);
        var screen = Display.PrimaryScreen;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Round(ActualWidth * dpi);
        var height = (int)Math.Round(ActualHeight * dpi);
        var hwnd = Handle;
        try
        {
            await Task.Run(() =>
            {
                capture = new DesktopCapture(screen.DeviceName);
                renderer = new LiquidGlassGpu(capture.Device, capture.Context, capture.AdapterLuid, hwnd, width, height);
            });
            if (closing) throw new OperationCanceledException("实验窗口已关闭。");
            Backdrop.Lock();
            try { Backdrop.SetBackBuffer(D3DResourceType.IDirect3DSurface9, renderer!.Surface); attached = true; }
            finally { Backdrop.Unlock(); }
            timer.Start();
        }
        catch (Exception ex) { Failure = ex.Message; status("实时玻璃初始化失败：" + ex.Message); throw; }
    }

    internal bool SetExclusion(bool exclude) => SetWindowDisplayAffinity(Handle, exclude ? 0x11u : 0u);
    internal void InvalidateGlass() => dirty = true;
    internal void SuspendLayer(bool suspend)
    {
        if (suspend) { layer?.Dispose(); layer = null; }
        else if (!closing) layer ??= new DesktopLayer(this);
    }

    private async Task Frame()
    {
        if (closing || !Ready || !IsVisible || !Backdrop.IsFrontBufferAvailable) return;
        if (!Backdrop.TryLock(new Duration(TimeSpan.Zero))) return;
        var point = PointToScreen(new Point());
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var cursor = pointer * (float)dpi;
        var needsDraw = dirty; dirty = false;
        var dark = Dark; var reduced = Reduced;
        try
        {
            var drew = await Task.Run(() =>
            {
                using var frame = capture!.Acquire(1);
                if (frame is not null)
                {
                    if (lastFrame is null) lastFrame = capture.Device.CreateTexture2D(frame.Description with { BindFlags = BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.None });
                    capture.Context.CopyResource(lastFrame, frame);
                    capture.Release();
                    needsDraw = true;
                }
                if (!needsDraw || lastFrame is null) return false;
                var rect = capture.Output.DesktopCoordinates;
                var desc = lastFrame.Description;
                var sourceRect = new Vector4((float)(point.X - rect.Left) / desc.Width, (float)(point.Y - rect.Top) / desc.Height,
                    (float)(ActualWidthForWorker * dpi) / desc.Width, (float)(ActualHeightForWorker * dpi) / desc.Height);
                renderer!.Render(capture.Device, lastFrame, sourceRect, cursor, dark, reduced);
                return true;
            });
            if (drew && !closing)
            {
                Backdrop.AddDirtyRect(new Int32Rect(0, 0, Backdrop.PixelWidth, Backdrop.PixelHeight));
                RenderedFrames++;
            }
        }
        catch (Exception ex) { Failure = ex.Message; timer.Stop(); status("已停止实验渲染：" + ex.Message); }
        finally { Backdrop.Unlock(); }
    }

    // Fixed probe dimensions; no WPF DependencyObject is read on the worker thread.
    private const double ActualWidthForWorker = 472, ActualHeightForWorker = 320;

    internal async Task<(byte B, byte G, byte R)> SampleMarker()
    {
        timer.Stop(); await pending;
        var marker = PointToScreen(new Point(52, 116));
        try
        {
            return await Task.Run(() =>
            {
                for (var i = 0; i < 20; i++)
                {
                    using var frame = capture!.Acquire(100);
                    if (frame is null) continue;
                    var rect = capture.Output.DesktopCoordinates;
                    var x = checked((int)marker.X - rect.Left); var y = checked((int)marker.Y - rect.Top);
                    var pixels = capture.ReadPixels(frame);
                    try
                    {
                        if (x < 0 || y < 0 || x >= frame.Description.Width || y >= frame.Description.Height)
                            throw new InvalidOperationException("验证标记不在主屏范围内。");
                        var at = checked((y * (int)frame.Description.Width + x) * 4);
                        return (pixels[at], pixels[at + 1], pixels[at + 2]);
                    }
                    finally { Array.Clear(pixels); capture.Release(); }
                }
                throw new TimeoutException("未取得标记所在的桌面帧。");
            });
        }
        finally { if (!closing && Failure is null) timer.Start(); }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
}
