using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace GlassBogo.App;

// WPF content stays above a D3DImage material. The shared graphics service owns
// capture and GPU resources; this window never starts a private duplication.
internal class LiquidWindow : Window
{
    internal readonly D3DImage Material = new();
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
    private Native.Rect retainedBounds;
    private int transition;
    internal bool HasRetainedFrame => RetainedImage.Source is not null && Native.GetWindowRect(Handle, out var rect) && rect.Left == retainedBounds.Left && rect.Top == retainedBounds.Top && rect.Right == retainedBounds.Right && rect.Bottom == retainedBounds.Bottom;
    internal System.Numerics.Vector2 Pointer = new(-1000, -1000);
    private readonly LiquidGlassService graphics;
    internal bool UsesGlass { get; }
    internal string EffectiveVisualMode { get; private set; } = "普通界面";
    internal nint Handle => new WindowInteropHelper(this).Handle;
    internal LiquidWindow(LiquidGlassService graphics, bool glass = false)
    {
        this.graphics = graphics; UsesGlass = glass;
        if (glass) EffectiveVisualMode = "兼容模式";
        WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        FontFamily = new("Segoe UI Variable, Microsoft YaHei UI"); FontSize = 14;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Fallback = new Border { CornerRadius = new(24), BorderThickness = new(1) };
        MaterialImage = new Image { Source = Material, Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        RetainedImage = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        var content = new Border { CornerRadius = new(24), Padding = new(16), Child = Body };
        var root = new Grid(); root.Children.Add(Fallback); root.Children.Add(RetainedImage); root.Children.Add(MaterialImage); root.Children.Add(content);
        Content = root;
        ThemeChanged();
        Loaded += (_, _) => { if (UsesGlass) graphics.Add(this); else Design.Reveal(Body); };
        SourceInitialized += (_, _) =>
        {
            var screen = Display.PrimaryScreen;
            MaxWidth = Math.Max(80, screen.WorkingArea.Width / screen.Scale - 16);
            MaxHeight = Math.Max(80, screen.WorkingArea.Height / screen.Scale - 16);
        };
        IsVisibleChanged += (_, _) => { if (UsesGlass) { MaterialDirty = true; graphics.Wake(); } };
        SizeChanged += (_, _) => GeometryChanged();
        LocationChanged += (_, _) => GeometryChanged();
        MouseMove += (_, e) => { if (UsesGlass) { var p = e.GetPosition(this); Pointer = new((float)p.X, (float)p.Y); MaterialDirty = true; graphics.Wake(); } };
        MouseLeave += (_, _) => { if (UsesGlass) { Pointer = new(-1000, -1000); MaterialDirty = true; graphics.Wake(); } };
        Material.IsFrontBufferAvailableChanged += (_, _) => { if (UsesGlass && !Removed) graphics.FrontBufferChanged(this); };
        Closed += (_, _) => { Removed = true; if (UsesGlass) graphics.Remove(this); };
    }
    private void GeometryChanged()
    {
        if (!UsesGlass) return;
        MaterialDirty = true;
        if (Attached || FrozenMaterial) { if (!ShowRetained("等待底栏更新")) SetMaterialVisible(false, "等待底栏更新"); }
        graphics.Wake();
    }
    internal void ThemeChanged()
    {
        Fallback.SetResourceReference(Border.BackgroundProperty, UsesGlass ? "DockFallback" : "SurfaceFallback");
        Fallback.SetResourceReference(Border.BorderBrushProperty, "Line");
        SetResourceReference(ForegroundProperty, "Ink"); MaterialDirty = true;
    }
    internal void SetMaterialVisible(bool visible, string mode)
    {
        EffectiveVisualMode = mode;
        var entering = visible && MaterialImage.Visibility != Visibility.Visible;
        MaterialImage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Fallback.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        if (!visible) { transition++; RetainedImage.Visibility = Visibility.Collapsed; MaterialImage.BeginAnimation(OpacityProperty, null); MaterialImage.Opacity = 1; }
        else if (entering && HasRetainedFrame && GlassEnvironment.Animate)
        {
            var version = ++transition;
            RetainedImage.Visibility = Visibility.Visible; MaterialImage.Opacity = 1;
            var animation = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }, FillBehavior = System.Windows.Media.Animation.FillBehavior.Stop };
            animation.Completed += (_, _) => { if (transition == version) { MaterialImage.BeginAnimation(OpacityProperty, null); RetainedImage.Visibility = Visibility.Collapsed; } };
            MaterialImage.BeginAnimation(OpacityProperty, animation);
        }
        else if (entering) { transition++; MaterialImage.BeginAnimation(OpacityProperty, null); MaterialImage.Opacity = 1; RetainedImage.Visibility = Visibility.Collapsed; }
    }
    internal void RetainFrame(BitmapSource bitmap, Native.Rect bounds)
    {
        retainedBounds = bounds; RetainedImage.Source = bitmap;
        FrozenMaterial = true; DetachMaterial();
        if (!ShowRetained(EffectiveVisualMode)) SetMaterialVisible(false, "等待底栏更新");
    }
    internal bool ShowRetained(string mode)
    {
        if (!HasRetainedFrame) return false;
        transition++; EffectiveVisualMode = mode; MaterialImage.BeginAnimation(OpacityProperty, null); MaterialImage.Opacity = 1;
        RetainedImage.Visibility = Visibility.Visible; MaterialImage.Visibility = Visibility.Collapsed; Fallback.Visibility = Visibility.Collapsed;
        return true;
    }
    internal void ClearRetained()
    {
        RetainedImage.Source = null; RetainedImage.Visibility = Visibility.Collapsed; FrozenMaterial = false;
    }
    internal void DetachMaterial()
    {
        if (!Attached) return;
        Material.Lock();
        try { Material.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0); Attached = false; }
        finally { Material.Unlock(); }
    }
}
