using System.Windows.Media.Animation;

namespace GlassBogo.App;

// V2 material prototype. Production windows are not opted in before desktop approval.
internal sealed class GlassSurface : Grid
{
    private readonly Border tint = new() { CornerRadius = new(22), IsHitTestVisible = false };
    private readonly GlassHighlightLayer highlight = new();
    private readonly Border foreground = new() { Padding = new(20) };
    internal GlassSurface(UIElement content)
    {
        Background = Brushes.Transparent;
        Children.Add(tint);
        Children.Add(highlight);
        foreground.Child = content;
        Children.Add(foreground);
        MouseMove += (_, e) => highlight.Track(e.GetPosition(this), RenderSize);
        MouseLeave += (_, _) => highlight.ReturnToRest();
        IsVisibleChanged += (_, _) => { if (!IsVisible) highlight.Stop(); };
        Unloaded += (_, _) => highlight.Stop();
    }
    internal bool HighlightAnimating => highlight.IsAnimating;
    internal void Configure(bool opaque, bool decorated, GlassHostOptions options, GlassSystemState system)
    {
        tint.Background = new SolidColorBrush(opaque ? GlassBackdrop.Fallback(options.Dark)
            : decorated ? Color.FromArgb((byte)Math.Round(Math.Clamp(options.TintAlpha, 0, 1) * 255),
                options.Dark ? (byte)12 : (byte)255, options.Dark ? (byte)22 : (byte)255, options.Dark ? (byte)36 : (byte)255)
            : Colors.Transparent);
        highlight.Configure(decorated && !opaque && !system.HighContrast, system.ClientAreaAnimation, options.HighlightAlpha);
        TextElementForeground(system.HighContrast ? SystemColors.WindowTextBrush
            : new SolidColorBrush(options.Dark ? Color.FromRgb(245, 248, 255) : Color.FromRgb(22, 32, 46)));
    }
    private void TextElementForeground(Brush brush) => System.Windows.Documents.TextElement.SetForeground(foreground, brush);
}

// Draws only a short top rim and a localized glow. No full outline, shader, timer or render subscription.
internal sealed class GlassHighlightLayer : FrameworkElement
{
    private readonly TranslateTransform glintShift = new();
    private bool enabled, animate;
    private double strength;
    private int generation;
    internal bool IsAnimating => glintShift.HasAnimatedProperties;

    internal GlassHighlightLayer() { IsHitTestVisible = false; }

    internal void Configure(bool visible, bool motion, double alpha)
    {
        enabled = visible; animate = motion; strength = Math.Clamp(alpha, 0, 1);
        Stop(); Visibility = visible ? Visibility.Visible : Visibility.Collapsed; InvalidateVisual();
    }
    internal void Track(Point pointer, Size size)
    {
        if (!enabled || !animate || size.Width <= 0 || size.Height <= 0) return;
        var x = Math.Clamp((pointer.X / size.Width - .5) * 16, -8, 8);
        var y = Math.Clamp((pointer.Y / size.Height - .5) * 6, -3, 3);
        if (!IsAnimating && Math.Abs(glintShift.X - x) < .05 && Math.Abs(glintShift.Y - y) < .05) return;
        generation++;
        glintShift.BeginAnimation(TranslateTransform.XProperty, null);
        glintShift.BeginAnimation(TranslateTransform.YProperty, null);
        glintShift.X = x; glintShift.Y = y;
    }
    internal void ReturnToRest()
    {
        if (!enabled || !animate || !IsVisible) { Stop(); return; }
        var x = glintShift.X; var y = glintShift.Y;
        Stop();
        if (Math.Abs(x) < .01 && Math.Abs(y) < .01) return;
        var token = generation;
        var ax = new DoubleAnimation(x, 0, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        var ay = new DoubleAnimation(y, 0, ax.Duration) { EasingFunction = ax.EasingFunction, FillBehavior = FillBehavior.Stop };
        ax.Completed += (_, _) => { if (generation == token) Stop(); };
        glintShift.BeginAnimation(TranslateTransform.XProperty, ax);
        glintShift.BeginAnimation(TranslateTransform.YProperty, ay);
    }
    internal void Stop()
    {
        generation++;
        glintShift.BeginAnimation(TranslateTransform.XProperty, null);
        glintShift.BeginAnimation(TranslateTransform.YProperty, null);
        glintShift.X = 0; glintShift.Y = 0;
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!enabled || ActualWidth < 44 || ActualHeight < 44) return;
        var white = Color.FromArgb((byte)Math.Round(strength * 255), 255, 255, 255);
        var rim = new LinearGradientBrush
        {
            StartPoint = new(0, 0), EndPoint = new(1, 0),
            GradientStops = new() { new(Colors.Transparent, 0), new(white, .18), new(white, .45), new(Colors.Transparent, .9) }
        };
        rim.Freeze();
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new(1, 25), false, false);
            g.BezierTo(new(1, 8), new(8, 1), new(23, 1), true, false);
            g.LineTo(new(ActualWidth - 23, 1), true, false);
            g.BezierTo(new(ActualWidth - 8, 1), new(ActualWidth - 1, 8), new(ActualWidth - 1, 22), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(rim, .8), geometry);
        dc.PushClip(new RectangleGeometry(new Rect(RenderSize), 22, 22));
        dc.PushTransform(glintShift);
        var glow = new RadialGradientBrush(Color.FromArgb((byte)(white.A / 2), 255, 255, 255), Colors.Transparent);
        dc.DrawEllipse(glow, null, new(ActualWidth * .28, 4), ActualWidth * .30, 42);
        dc.Pop(); dc.Pop();
    }
}
