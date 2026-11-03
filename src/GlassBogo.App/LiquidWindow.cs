using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace GlassBogo.App;

// WPF only receives completed immutable material bitmaps. No compositor owns a
// GPU resource that the capture worker can overwrite, resize or dispose.
internal class LiquidWindow : Window
{
    internal BitmapSource? Material => MaterialImage.Source as BitmapSource;
    internal readonly Image MaterialImage;
    internal readonly Image RetainedImage;
    internal readonly Border Fallback;
    internal readonly ContentControl Body = new();
    internal bool MaterialDirty = true, Removed;
    internal bool Attached;
    internal bool FrozenMaterial;
    internal bool ExcludedFromCapture;
    internal DateTime LastMaterialFrame;
    internal bool LastFrameDark;
    internal string? LastFrameDisplay;
    internal long MaterialRevision
    {
        get; private set;
    }
    internal GlassFrameStamp? CommittedStamp
    {
        get; private set;
    }
    private Native.Rect retainedBounds;
    private bool retainedDark;
    private int transition;
    private (Display Screen, double Left, double Top, double Width, double Height)? placement;
    private HwndSource? placementSource;
    internal bool HasRetainedFrame => RetainedImage.Source is not null && retainedDark == Design.Dark && Native.GetWindowRect(Handle, out var rect) && rect.Left == retainedBounds.Left && rect.Top == retainedBounds.Top && rect.Right == retainedBounds.Right && rect.Bottom == retainedBounds.Bottom;
    internal System.Numerics.Vector2 Pointer = new(-1000, -1000);
    private readonly LiquidGlassService graphics;
    internal bool UsesGlass
    {
        get;
    }
    internal string EffectiveVisualMode { get; private set; } = "普通界面";
    internal nint Handle => new WindowInteropHelper(this).Handle;
    internal LiquidWindow(LiquidGlassService graphics, bool glass = false)
    {
        this.graphics = graphics;
        UsesGlass = glass;
        if (glass)
            EffectiveVisualMode = "兼容模式";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        FontFamily = new("Segoe UI Variable, Microsoft YaHei UI");
        FontSize = 14;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Fallback = new Border { CornerRadius = new(24), BorderThickness = new(1) };
        MaterialImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        RetainedImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var content = new Border { CornerRadius = new(24), Padding = new(16), Child = Body };
        var root = new Grid();
        root.Children.Add(Fallback);
        root.Children.Add(RetainedImage);
        root.Children.Add(MaterialImage);
        root.Children.Add(content);
        Content = root;
        ThemeChanged();
        Loaded += (_, _) => { ApplyPlacement(); if (UsesGlass) graphics.Add(this); else Design.Reveal(Body); };
        SourceInitialized += (_, _) =>
        {
            ApplyDisplayLimits(placement?.Screen ?? (Owner is LiquidWindow owner ? Display.FromHandle(owner.Handle) : Display.FromHandle(Handle)));
            placementSource = HwndSource.FromHwnd(Handle);
            placementSource?.AddHook(DisplayMessage);
        };
        IsVisibleChanged += (_, _) => { if (UsesGlass) { MaterialRevision++; MaterialDirty = true; graphics.Wake(); } };
        SizeChanged += (_, _) => GeometryChanged();
        LocationChanged += (_, _) => GeometryChanged();
        MouseMove += (_, e) => { if (UsesGlass) { var p = e.GetPosition(this); Pointer = new((float)p.X, (float)p.Y); MaterialDirty = true; graphics.Wake(); } };
        MouseLeave += (_, _) => { if (UsesGlass) { Pointer = new(-1000, -1000); MaterialDirty = true; graphics.Wake(); } };
        Closed += (_, _) => { placementSource?.RemoveHook(DisplayMessage); Removed = true; if (UsesGlass) graphics.Remove(this); };
    }
    internal void ApplyDisplayLimits(Display screen)
    {
        MaxWidth = Math.Max(80, screen.WorkingArea.Width / screen.Scale - 16);
        MaxHeight = Math.Max(80, screen.WorkingArea.Height / screen.Scale - 16);
    }
    internal void PlaceOn(Display screen, double left, double top)
    {
        ApplyDisplayLimits(screen);
        placement = (screen, left, top, Width, Height);
        if (IsLoaded)
            ApplyPlacement();
        else
        {
            Left = left;
            Top = top;
        }
    }
    internal void ReleasePlacement() => placement = null;
    private void ApplyPlacement()
    {
        if (placement is not { } target || Handle == 0)
            return;
        var scale = target.Screen.Scale;
        var x = (int)Math.Round(target.Left * scale);
        var y = (int)Math.Round(target.Top * scale);
        var width = (int)Math.Round(target.Width * scale);
        var height = (int)Math.Round(target.Height * scale);
        if (Native.GetWindowRect(Handle, out var r) && r.Left == x && r.Top == y && r.Right - r.Left == width && r.Bottom - r.Top == height)
            return;
        Native.SetWindowPos(Handle, 0, x, y, width, height, 0x14);
    }
    private nint DisplayMessage(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message == 0x2E0)
            _ = Dispatcher.BeginInvoke(() =>
        {
            if (Removed)
                return;
            if (placement is null)
                ApplyDisplayLimits(Display.FromHandle(Handle));
            else
                ApplyPlacement();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        return 0;
    }
    private void GeometryChanged()
    {
        if (!UsesGlass)
            return;
        MaterialRevision++;
        MaterialDirty = true;
        // Keep the complete last material during a short move. The worker's
        // revision check prevents an old result from becoming the new commit.
        // Geometry/DPI changes are serviced immediately by the coalesced queue.
        graphics.Wake();
    }
    internal void ThemeChanged()
    {
        Fallback.SetResourceReference(Border.BackgroundProperty, UsesGlass ? "DockFallback" : "SurfaceFallback");
        Fallback.SetResourceReference(Border.BorderBrushProperty, "Line");
        SetResourceReference(ForegroundProperty, "Ink");
        MaterialRevision++;
        MaterialDirty = true;
    }
    internal void SetMaterialVisible(bool visible, string mode)
    {
        EffectiveVisualMode = mode;
        var entering = visible && MaterialImage.Visibility != Visibility.Visible;
        MaterialImage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Fallback.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        if (!visible)
        {
            transition++;
            RetainedImage.Visibility = Visibility.Collapsed;
            MaterialImage.BeginAnimation(OpacityProperty, null);
            MaterialImage.Opacity = 1;
        }
        else if (entering && HasRetainedFrame && GlassEnvironment.Animate)
        {
            var version = ++transition;
            RetainedImage.Visibility = Visibility.Visible;
            MaterialImage.Opacity = 1;
            var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            animation.Completed += (_, _) => { if (transition == version) { MaterialImage.BeginAnimation(OpacityProperty, null); RetainedImage.Visibility = Visibility.Collapsed; } };
            MaterialImage.BeginAnimation(OpacityProperty, animation);
        }
        else if (entering)
        {
            transition++;
            MaterialImage.BeginAnimation(OpacityProperty, null);
            MaterialImage.Opacity = 1;
            RetainedImage.Visibility = Visibility.Collapsed;
        }
    }
    internal void RetainFrame(BitmapSource bitmap, Native.Rect bounds)
    {
        retainedBounds = bounds;
        retainedDark = LastFrameDark;
        RetainedImage.Source = bitmap;
        FrozenMaterial = true;
        DetachMaterial();
        if (!ShowRetained(EffectiveVisualMode))
            SetMaterialVisible(false, "等待底栏更新");
    }
    internal bool ShowRetained(string mode)
    {
        if (!HasRetainedFrame)
            return false;
        transition++;
        EffectiveVisualMode = mode;
        MaterialImage.BeginAnimation(OpacityProperty, null);
        MaterialImage.Opacity = 1;
        RetainedImage.Visibility = Visibility.Visible;
        MaterialImage.Visibility = Visibility.Collapsed;
        Fallback.Visibility = Visibility.Collapsed;
        return true;
    }
    internal void ClearRetained()
    {
        RetainedImage.Source = null;
        RetainedImage.Visibility = Visibility.Collapsed;
        FrozenMaterial = false;
    }
    internal void CommitFrame(BitmapSource bitmap, GlassFrameStamp stamp, string mode)
    {
        if (!bitmap.IsFrozen || bitmap.PixelWidth != stamp.Bounds.Width || bitmap.PixelHeight != stamp.Bounds.Height)
            throw new InvalidOperationException("玻璃画面尚未完整就绪。");
        MaterialImage.Source = bitmap;
        Attached = true;
        FrozenMaterial = false;
        CommittedStamp = stamp;
        LastFrameDark = stamp.Dark;
        LastFrameDisplay = stamp.Display;
        LastMaterialFrame = DateTime.UtcNow;
        SetMaterialVisible(true, mode);
    }
    internal void DetachMaterial()
    {
        Attached = false;
    }
}
