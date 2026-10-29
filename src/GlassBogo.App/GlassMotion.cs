using System.Windows.Media.Animation;

namespace GlassBogo.App;

// One interruptible timeline per surface; never animate Window.Opacity (a layered HWND).
internal sealed class GlassMotion
{
    private readonly FrameworkElement surface;
    private readonly TranslateTransform shift = new();
    private int version;
    internal bool IsClosing { get; private set; }
    internal GlassMotion(FrameworkElement surface)
    {
        this.surface = surface;
        surface.RenderTransform = shift;
    }
    internal void Open(bool fresh, int milliseconds = 150, Action? completed = null)
    {
        if (fresh) { Stop(); surface.Opacity = 0; shift.Y = 8; }
        Play(1, 0, milliseconds, EasingMode.EaseOut, completed);
    }
    internal void Close(Action completed) => Play(0, 4, 110, EasingMode.EaseIn, completed);
    private void Play(double opacity, double y, int milliseconds, EasingMode mode, Action? completed)
    {
        IsClosing = opacity == 0;
        var token = ++version;
        var fromOpacity = surface.Opacity;
        var fromY = shift.Y;
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        surface.Opacity = opacity;
        shift.Y = y;
        if (!GlassEnvironment.Animate) { completed?.Invoke(); return; }
        var fade = new DoubleAnimation(fromOpacity, opacity, TimeSpan.FromMilliseconds(milliseconds))
            { EasingFunction = new CubicEase { EasingMode = mode }, FillBehavior = FillBehavior.Stop };
        var move = new DoubleAnimation(fromY, y, fade.Duration)
            { EasingFunction = new CubicEase { EasingMode = mode }, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (token != version) return;
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            completed?.Invoke();
        };
        shift.BeginAnimation(TranslateTransform.YProperty, move);
        surface.BeginAnimation(UIElement.OpacityProperty, fade);
    }
    internal void Stop()
    {
        IsClosing = false;
        version++;
        surface.BeginAnimation(UIElement.OpacityProperty, null);
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        surface.Opacity = 1;
        shift.Y = 0;
    }
}
