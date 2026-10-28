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
    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromInvariantString(value)!;
    private static Brush Gradient(string top, string bottom) => new LinearGradientBrush((Color)ColorConverter.ConvertFromString(top), (Color)ColorConverter.ConvertFromString(bottom), 90);
    internal static bool Dark
    {
        get; private set;
    }
    internal static void Apply(string theme)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        Dark = theme == "dark" || theme == "system" && (int)(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        var r = Application.Current.Resources;
        r["Ink"] = new SolidColorBrush(Dark ? Color.FromRgb(235, 240, 250) : Color.FromRgb(28, 40, 58));
        r["Muted"] = new SolidColorBrush(Dark ? Color.FromRgb(164, 181, 205) : Color.FromRgb(85, 104, 125));
        r["Card"] = Dark ? Gradient("#243F5974", "#1425364C") : Gradient("#90FFFFFF", "#30FFFFFF");
        r["Surface"] = Dark ? Gradient("#B923364D", "#AC101E31") : Gradient("#88FFFFFF", "#50E5EEFA");
        r["ScrollThumb"] = Brush(Dark ? "#608BA3BF" : "#506580A0");
        r["Line"] = Dark ? Gradient("#65C7DCFF", "#184B6380") : Gradient("#F2FFFFFF", "#63FFFFFF");
        r["Inset"] = Brush(Dark ? "#36101C2D" : "#40FFFFFF");
        r["MenuSurface"] = Brush(Dark ? "#FF1F3046" : "#FFF4F8FE");
        r["Selected"] = Dark ? Gradient("#684B88C8", "#32366594") : Gradient("#C9E0F0FF", "#80C6E0FB");
        r["AccentLine"] = Brush(Dark ? "#909ECFFB" : "#85A4C8ED");
        r["Accent"] = new SolidColorBrush(Dark ? Color.FromRgb(134, 190, 245) : Color.FromRgb(41, 102, 174));
        r["Hover"] = Dark ? Gradient("#456991BA", "#125C83B0") : Gradient("#95FFFFFF", "#30D3E7FF");
        if (SystemParameters.HighContrast)
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
    private static void Animate(System.Windows.Media.Animation.Animatable target, DependencyProperty property, double value, double milliseconds = 180)
    {
        var from = (double)target.GetValue(property);
        target.SetValue(property, value);
        var versions = animationVersions.GetOrCreateValue(target);
        var version = versions.GetValueOrDefault(property) + 1;
        versions[property] = version;
        if (!SystemParameters.ClientAreaAnimation) { target.BeginAnimation(property, null); return; }
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
        button.MouseEnter += (_, _) => Move(-2, 1.015);
        button.MouseLeave += (_, _) => Move(0, 1);
        button.PreviewMouseLeftButtonDown += (_, _) => Move(0, .965);
        button.PreviewMouseLeftButtonUp += (_, _) => Move(button.IsMouseOver ? -2 : 0, button.IsMouseOver ? 1.015 : 1);
        button.Unloaded += (_, _) => { shift.BeginAnimation(TranslateTransform.YProperty, null); scale.BeginAnimation(ScaleTransform.ScaleXProperty, null); scale.BeginAnimation(ScaleTransform.ScaleYProperty, null); shift.Y = 0; scale.ScaleX = scale.ScaleY = 1; };
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
    internal static void Reveal(FrameworkElement element)
    {
        if (!SystemParameters.ClientAreaAnimation)
            return;
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        var fade = new DoubleAnimation(.35, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) => { if (ReferenceEquals(element.RenderTransform, transform)) element.BeginAnimation(UIElement.OpacityProperty, null); };
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.Y = 10;
        Animate(transform, TranslateTransform.YProperty, 0, 240);
    }
}
internal class GlassWindow : Window
{
    private readonly Border frame;
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
        frame.SetResourceReference(Border.BorderBrushProperty, "Line");
        frame.SetResourceReference(Border.BackgroundProperty, "Surface");
        Content = frame;
        SourceInitialized += (_, _) => UpdateBackdrop();
    }
    internal void UpdateBackdrop()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0)
            return;
        var dark = Design.Dark ? 1 : 0;
        Native.DwmSetWindowAttribute(handle, 20, ref dark, 4);
        var corner = 2;
        Native.DwmSetWindowAttribute(handle, 33, ref corner, 4);
        var noNativeBorder = unchecked((int)0xFFFFFFFE);
        Native.DwmSetWindowAttribute(handle, 34, ref noNativeBorder, 4);
        var backdrop = SystemParameters.HighContrast ? 1 : 3;
        var result = Native.DwmSetWindowAttribute(handle, 38, ref backdrop, 4);
        if (result == 0 && !SystemParameters.HighContrast)
        {
            var margins = new Margins { Left = -1 };
            DwmExtendFrameIntoClientArea(handle, ref margins);
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
        }
        else
            frame.Background = SystemParameters.HighContrast ? SystemColors.WindowBrush : new SolidColorBrush(Design.Dark ? Color.FromRgb(24, 32, 44) : Color.FromRgb(239, 245, 250));
    }
    internal void SetBody(UIElement content) => frame.Child = content;
    internal void SetInsets(double padding) => frame.Padding = new(padding);
    internal void RevealBody() { if (frame.Child is FrameworkElement body) Design.Reveal(body); }
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
        frame.Child = layout;
    }
}
