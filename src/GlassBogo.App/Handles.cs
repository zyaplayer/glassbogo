using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace GlassBogo.App;

/// <summary>
/// 收起态手柄：一条带渐变的胶囊 + 三个小点做抓握暗示。悬停时立即放大提亮，
/// 让用户先看到"它活了"，再触发 120 ms 后的展开。
/// </summary>
internal static class HandleVisual
{
    internal static Grid Create(double width, double height, out ScaleTransform scale, out Border pill)
    {
        var host = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            IsHitTestVisible = true,
        };
        scale = new ScaleTransform(1, 1);
        pill = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(height / 2),
            Opacity = .55,
            RenderTransform = scale,
            RenderTransformOrigin = new Point(.5, .5),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 1, Direction = 270, Opacity = .5, Color = Colors.Black },
        };
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x7C, 0x8B, 0xE8), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x65, 0x76, 0xC5), .55));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x8E, 0x7C, 0xE0), 1));
        pill.Background = gradient;
        host.Children.Add(pill);
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Ellipse { Width = 2.5, Height = 2.5, Margin = new Thickness(1.5, 0, 1.5, 0), Fill = Brushes.White, Opacity = .85 });
        host.Children.Add(dots);
        return host;
    }

    internal static LinearGradientBrush Gradient()
    {
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x7C, 0x8B, 0xE8), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x65, 0x76, 0xC5), .55));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(255, 0x8E, 0x7C, 0xE0), 1));
        return gradient;
    }

    /// <summary>
    /// 收起态直接让整个窗口就是这颗胶囊：不隐藏材质、不用"透明大窗口里画一条线"，
    /// 这样不会出现 DWM 背板关闭后客户区变黑的方块。
    /// </summary>
    internal static UIElement Fill(out ScaleTransform scale, out Border pill)
    {
        scale = new ScaleTransform(1, 1);
        pill = new Border
        {
            CornerRadius = new CornerRadius(11),
            Opacity = .92,
            Background = Gradient(),
            RenderTransform = scale,
            RenderTransformOrigin = new Point(.5, .5),
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Direction = 270, Opacity = .35, Color = Colors.Black },
        };
        var host = new Grid { Background = Brushes.Transparent };
        host.Children.Add(pill);
        var dots = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < 3; i++)
            dots.Children.Add(new Ellipse { Width = 3, Height = 3, Margin = new Thickness(2, 0, 2, 0), Fill = Brushes.White, Opacity = .9 });
        host.Children.Add(dots);
        return host;
    }

    internal static void Hover(ScaleTransform scale, Border pill, bool inside)
    {
        Design.Animate(scale, ScaleTransform.ScaleXProperty, inside ? 1.28 : 1, 90);
        Design.Animate(scale, ScaleTransform.ScaleYProperty, inside ? 1.28 : 1, 90);
        // Border 不是 Animatable，用 BeginAnimation 直接做不透明度过渡。
        pill.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(inside ? 1 : .55, TimeSpan.FromMilliseconds(90))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }
}
