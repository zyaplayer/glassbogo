using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace GlassBogo.App;

internal record Choice(string Value, string Label);
internal static class Design
{
    private static bool stylesLoaded;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Button, object> motionButtons = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Media.Animation.Animatable, Dictionary<DependencyProperty, int>> animationVersions = new();
    private static Brush Brush(string value) { var brush = (Brush)new BrushConverter().ConvertFromInvariantString(value)!; brush.Freeze(); return brush; }
    internal static bool Dark
    {
        get; private set;
    }
    internal static void Apply(string theme)
    {
        GlassEnvironment.Refresh();
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        Dark = theme == "dark" || theme == "system" && (int)(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        var r = Application.Current.Resources;
        r["Ink"] = r["InkPrimary"] = Brush(Dark ? "#FFF3F7FE" : "#FF172333");
        r["Muted"] = r["InkSecondary"] = Brush(Dark ? "#FFAFBED1" : "#FF566477");
        r["Card"] = Brush(Dark ? "#183F5974" : "#38FFFFFF");
        r["SurfaceTint"] = Brush(Dark ? "#300B1628" : "#18FFFFFF");
        r["SurfaceFallback"] = Brush(Dark ? "#FF19283C" : "#FFF0F6FD");
        r["Surface"] = r[GlassEnvironment.Transparency ? "SurfaceTint" : "SurfaceFallback"];
        r["ScrollThumb"] = Brush(Dark ? "#608BA3BF" : "#506580A0");
        r["Line"] = r["SurfaceLine"] = Brush(Dark ? "#30EAF5FF" : "#65FFFFFF");
        r["SpecularTop"] = Brush(Dark ? "#50FFFFFF" : "#A8FFFFFF");
        r["FocusRing"] = Brush(Dark ? "#FF9DCBFF" : "#FF2779E6");
        r["Warning"] = Brush(Dark ? "#FFF2B960" : "#FFD68C25");
        r["Inset"] = Brush(Dark ? "#36101C2D" : "#40FFFFFF");
        r["MenuSurface"] = Brush(Dark ? "#FF1F3046" : "#FFF4F8FE");
        r["Selected"] = r["SelectedFill"] = Brush(Dark ? "#5070AEFF" : "#70CEE6FF");
        r["AccentLine"] = Brush(Dark ? "#909ECFFB" : "#85A4C8ED");
        r["Accent"] = Brush(Dark ? "#FF79B5FF" : "#FF468FF2");
        r["Hover"] = r["HoverFill"] = Brush(Dark ? "#2679B5FF" : "#30E2F0FF");
        r["DisabledInk"] = Brush(Dark ? "#FFAFBED1" : "#FF566477");
        r["SelectedInk"] = r["Ink"];
        r["ControlRadius"] = new CornerRadius(12);
        r["CategoryRadius"] = new CornerRadius(16);
        r["GlowVisibility"] = GlassEnvironment.Transparency ? Visibility.Visible : Visibility.Collapsed;
        if (GlassEnvironment.HighContrast)
        {
            r["Ink"] = SystemColors.WindowTextBrush;
            r["Muted"] = SystemColors.WindowTextBrush;
            r["Card"] = SystemColors.WindowBrush;
            r["Surface"] = SystemColors.WindowBrush;
            r["Line"] = SystemColors.WindowTextBrush;
            r["Accent"] = SystemColors.HighlightBrush;
            r["Hover"] = SystemColors.ControlBrush;
            r["Inset"] = r["MenuSurface"] = SystemColors.WindowBrush;
            r["Selected"] = SystemColors.HighlightBrush;
            r["AccentLine"] = SystemColors.WindowTextBrush;
            r["ScrollThumb"] = SystemColors.WindowTextBrush;
            r["SurfaceFallback"] = r["SurfaceTint"] = SystemColors.WindowBrush;
            r["FocusRing"] = r["Warning"] = SystemColors.WindowTextBrush;
            r["SelectedInk"] = SystemColors.HighlightTextBrush;
            r["DisabledInk"] = SystemColors.GrayTextBrush;
            r["SpecularTop"] = Brushes.Transparent;
            r["InkPrimary"] = r["Ink"];
            r["InkSecondary"] = r["Muted"];
            r["SurfaceLine"] = r["Line"];
            r["SelectedFill"] = r["Selected"];
            r["HoverFill"] = r["Hover"];
        }
        if (!stylesLoaded)
        {
            var styles = (ResourceDictionary)Application.LoadComponent(new Uri("/GlassBogo;component/GlassTheme.xaml", UriKind.Relative));
            ((Style)styles[typeof(Button)]).Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => AttachMotion((Button)sender))));
            r.MergedDictionaries.Add(styles);
            stylesLoaded = true;
        }
        foreach (Window w in Application.Current.Windows)
            if (w is GlassWindow glass)
                glass.UpdateBackdrop();
    }
    internal static TextBlock Text(string text, double size = 14, string resource = "Ink")
    {
        var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        t.SetResourceReference(TextBlock.ForegroundProperty, resource);
        return t;
    }
    internal static FrameworkElement Icon(string name, double size = 20)
    {
        var geometry = name switch
        {
            "search" => "M15,15 L21,21 M17,10 A7,7 0 1 1 3,10 A7,7 0 1 1 17,10",
            "calendar" => "M4,5 L20,5 20,21 4,21 Z M4,10 L20,10 M8,2 L8,7 M16,2 L16,7 M8,14 L10,14 M14,14 L16,14 M8,18 L10,18",
            "home" => "M3,11 L12,3 21,11 M6,10 L6,21 10,21 10,15 14,15 14,21 18,21 18,10",
            "folder" => "M3,7 L3,4 10,4 13,7 21,7 21,20 3,20 Z M3,10 L21,10",
            "settings" => "M4,5 L20,5 M4,12 L20,12 M4,19 L20,19 M8,2 L8,8 M16,9 L16,15 M10,16 L10,22",
            "plugin" => "M4,4 L10,4 10,10 4,10 Z M14,4 L20,4 20,10 14,10 Z M4,14 L10,14 10,20 4,20 Z M14,14 L20,14 20,20 14,20 Z",
            _ => "M12,4 L12,20 M4,12 L20,12"
        };
        var path = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = 1.65, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        path.SetResourceReference(System.Windows.Shapes.Path.StrokeProperty, "Ink");
        return path;
    }
    internal static Button IconButton(string icon, string tooltip, Action action)
    {
        var button = Button("", action);
        button.Content = Icon(icon);
        button.ToolTip = tooltip;
        button.Width = 42;
        button.Height = 42;
        button.Padding = new(10);
        button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }
    internal static void Animate(System.Windows.Media.Animation.Animatable target, DependencyProperty property, double value, double milliseconds = 180)
    {
        var from = (double)target.GetValue(property);
        target.SetValue(property, value);
        var versions = animationVersions.GetOrCreateValue(target);
        var version = versions.GetValueOrDefault(property) + 1;
        versions[property] = version;
        if (!GlassEnvironment.Animate) { target.BeginAnimation(property, null); return; }
        var animation = new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(milliseconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        animation.Completed += (_, _) => { if (versions[property] == version) target.BeginAnimation(property, null); };
        target.BeginAnimation(property, animation);
    }
    private static void AttachMotion(Button button)
    {
        if (motionButtons.TryGetValue(button, out _)) return;
        motionButtons.Add(button, new object());
        var scale = new ScaleTransform(1, 1);
        var shift = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(shift);
        button.RenderTransformOrigin = new(.5, .5);
        button.RenderTransform = transforms;
        void Move(double amount, double size)
        {
            Animate(shift, TranslateTransform.YProperty, amount);
            Animate(scale, ScaleTransform.ScaleXProperty, size);
            Animate(scale, ScaleTransform.ScaleYProperty, size);
        }
        button.MouseEnter += (_, _) => Move(0, 1);
        button.MouseLeave += (_, _) => Move(0, 1);
        button.PreviewMouseLeftButtonDown += (_, _) => { Animate(scale, ScaleTransform.ScaleXProperty, .97, 70); Animate(scale, ScaleTransform.ScaleYProperty, .97, 70); };
        button.PreviewMouseLeftButtonUp += (_, _) => Move(0, 1);
        button.LostMouseCapture += (_, _) => Move(0, 1);
        // The glow moves at most 10 DIP, only on actual pointer events; no rendering loop.
        long lastGlow = 0;
        button.MouseMove += (_, e) =>
        {
            if (!GlassEnvironment.Animate || Environment.TickCount64 - lastGlow < 16) return;
            lastGlow = Environment.TickCount64;
            if (button.Template.FindName("Glow", button) is Border { Background: RadialGradientBrush brush } glow)
            {
                if (brush.IsFrozen) { brush = brush.Clone(); glow.Background = brush; }
                var p = e.GetPosition(button);
                var center = new Point(.5 + Math.Clamp(p.X - button.ActualWidth / 2, -10, 10) / Math.Max(1, button.ActualWidth),
                    .5 + Math.Clamp(p.Y - button.ActualHeight / 2, -10, 10) / Math.Max(1, button.ActualHeight));
                brush.Center = brush.GradientOrigin = center;
            }
        };
        button.Unloaded += (_, _) => { shift.BeginAnimation(TranslateTransform.XProperty, null); shift.BeginAnimation(TranslateTransform.YProperty, null); scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); shift.X = shift.Y = 0; scale.ScaleX = scale.ScaleY = 1; };
    }
    internal static Button Button(string text, Action action)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => Guard(action);
        return b;
    }
    internal static Button AsyncButton(string text, Func<Task> action)
    {
        var b = new Button { Content = text };
        b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception ex) { Error(ex); } finally { b.IsEnabled = true; } };
        return b;
    }
    internal static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) { Error(ex); }
    }
    internal static void Error(Exception ex) => MessageBox.Show(ex.Message, "GlassBogo", MessageBoxButton.OK, MessageBoxImage.Warning);
    internal static Border Card(UIElement content)
    {
        var b = new Border { Child = content, CornerRadius = new(20), Padding = new(20), Margin = new(0, 8, 0, 8), BorderThickness = new(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Card");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }
    internal static WrapPanel Row(params UIElement[] items)
    {
        var s = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
            s.Children.Add(i);
        return s;
    }
    internal static string? Prompt(Window owner, string title, string value = "", Action<string>? save = null)
    {
        var window = new GlassWindow { Title = title, Width = 420, Height = 260, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var input = new TextBox { Text = value, MaxLength = 200 };
        var error = Text("", 12, "Accent");
        var panel = new StackPanel();
        panel.Children.Add(input);
        var saveButton = Button("保存", () =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { error.Text = "请输入名称。"; return; }
            try { save?.Invoke(input.Text.Trim()); window.DialogResult = true; }
            catch (Exception ex) { error.Text = "未保存：" + ex.Message; }
        });
        saveButton.IsDefault = true;
        panel.Children.Add(Row(Button("取消", () => window.DialogResult = false), saveButton));
        panel.Children.Add(error);
        window.SetPage(title, panel);
        window.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        return window.ShowDialog() == true ? input.Text.Trim() : null;
    }
    internal static void Reveal(FrameworkElement element, double milliseconds = 200, double offset = 10)
    {
        if (!GlassEnvironment.Animate)
            return;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        var fade = new DoubleAnimation(.65, 1, TimeSpan.FromMilliseconds(milliseconds)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (ReferenceEquals(element.RenderTransform, transform)) element.BeginAnimation(UIElement.OpacityProperty, null); };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.Y = offset;
        Animate(transform, TranslateTransform.YProperty, 0, milliseconds);
    }
}
internal class GlassWindow : Window
{
    private readonly Border frame;
    private readonly Grid bodyHost = new();
    private UIElement? currentBody;
    private int bodyGeneration;
    internal FrameworkElement SurfaceRoot { get; }
    internal bool TransientMaterial { get; set; }
    internal string BackdropStatus { get; private set; } = "not initialized";
    internal Brush MaterialBrush => frame.Background;
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left, Right, Top, Bottom;
    }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    internal GlassWindow()
    {
        Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/icon.ico"));
        FontFamily = new("Segoe UI Variable, Microsoft YaHei UI");
        FontSize = 14;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome { CaptionHeight = 0, GlassFrameThickness = new(-1), ResizeBorderThickness = new(5), UseAeroCaptionButtons = false, CornerRadius = new(24) });
        MinWidth = 220;
        MinHeight = 70;
        frame = new Border { CornerRadius = new(24), BorderThickness = new(1), Padding = new(22) };
        frame.Child = bodyHost;
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        var layers = new Grid();
        layers.Children.Add(frame);
        var highlight = new Border { Height = 1, Margin = new(28, 1, 28, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        highlight.SetResourceReference(Border.BackgroundProperty, "SpecularTop");
        layers.Children.Add(highlight);
        Content = SurfaceRoot = layers;
        SourceInitialized += (_, _) => UpdateBackdrop();
        Closed += (_, _) => StopBodyTransition();
    }
    internal virtual void UpdateBackdrop()
    {
        if (!GlassEnvironment.Animate) StopBodyTransition();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
            return;
        var dark = Design.Dark ? 1 : 0;
        var themeResult = Native.DwmSetWindowAttribute(handle, 20, ref dark, 4);
        var corner = 2;
        var cornerResult = Native.DwmSetWindowAttribute(handle, 33, ref corner, 4);
        var noNativeBorder = unchecked((int)0xFFFFFFFE);
        var borderResult = Native.DwmSetWindowAttribute(handle, 34, ref noNativeBorder, 4);
        var backdrop = GlassEnvironment.Transparency ? (TransientMaterial ? 3 : 2) : 1;
        var result = Native.DwmSetWindowAttribute(handle, 38, ref backdrop, 4);
        var margins = GlassEnvironment.Transparency && result == 0 ? new Margins { Left = -1 } : new Margins();
        var extendResult = DwmExtendFrameIntoClientArea(handle, ref margins);
        var composed = result == 0 && extendResult == 0 && GlassEnvironment.Transparency;
        if (composed)
        {
            frame.SetResourceReference(Border.BackgroundProperty, "SurfaceTint");
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
        }
        else
        {
            var none = 1;
            Native.DwmSetWindowAttribute(handle, 38, ref none, 4);
            var resetMargins = new Margins();
            DwmExtendFrameIntoClientArea(handle, ref resetMargins);
            frame.SetResourceReference(Border.BackgroundProperty, "SurfaceFallback");
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = ((SolidColorBrush)Application.Current.Resources["SurfaceFallback"]).Color;
        }
        BackdropStatus = $"requested={backdrop}; DWM=0x{result:X8}; extend=0x{extendResult:X8}; theme=0x{themeResult:X8}; corner=0x{cornerResult:X8}; border=0x{borderResult:X8}; composed-request={composed}; build={Environment.OSVersion.Version}";
    }
    internal void StopBodyTransition()
    {
        bodyGeneration++;
        foreach (UIElement child in bodyHost.Children)
        {
            child.BeginAnimation(UIElement.OpacityProperty, null);
            child.Opacity = 1;
            child.IsHitTestVisible = true;
            child.IsEnabled = true;
        }
        for (var i = bodyHost.Children.Count - 1; i >= 0; i--)
            if (!ReferenceEquals(bodyHost.Children[i], currentBody)) bodyHost.Children.RemoveAt(i);
    }
    internal void SetBody(UIElement content, bool crossFade = false)
    {
        if (ReferenceEquals(content, currentBody)) return;
        var old = currentBody;
        StopBodyTransition();
        currentBody = content;
        if (!crossFade || !GlassEnvironment.Animate || old is null)
        {
            bodyHost.Children.Clear();
            bodyHost.Children.Add(content);
            return;
        }
        var generation = bodyGeneration;
        old.IsHitTestVisible = false;
        old.IsEnabled = false;
        bodyHost.Children.Add(content);
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90)) { FillBehavior = FillBehavior.Stop };
        var fadeIn = new DoubleAnimation(0, 1, fadeOut.Duration) { FillBehavior = FillBehavior.Stop };
        fadeIn.Completed += (_, _) => { if (generation == bodyGeneration) StopBodyTransition(); };
        old.BeginAnimation(UIElement.OpacityProperty, fadeOut);
        content.BeginAnimation(UIElement.OpacityProperty, fadeIn);
    }
    internal void SetInsets(double padding) => frame.Padding = new(padding);
    internal void SetRadius(double radius) => frame.CornerRadius = new(radius);
    internal void SetPage(string title, UIElement content, bool close = true)
    {
        var layout = new DockPanel();
        var header = new DockPanel { Margin = new(2, 0, 2, 20) };
        if (close)
        {
            var x = Design.Button("×", Close);
            x.FontSize = 20;
            x.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            x.Padding = new(10, 2, 10, 2);
            DockPanel.SetDock(x, Dock.Right);
            header.Children.Add(x);
        }
        var caption = Design.Text(title, 19);
        caption.FontWeight = FontWeights.SemiBold;
        caption.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2 && ResizeMode != ResizeMode.NoResize) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; else if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        header.Children.Add(caption);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(content);
        SetBody(layout);
    }
}
