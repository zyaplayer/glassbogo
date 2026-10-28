using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media.Animation;

namespace GlassBogo.App;

internal record Choice(string Value, string Label);
internal static class Design
{
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
        r["Card"] = new SolidColorBrush(Dark ? Color.FromArgb(170, 29, 41, 58) : Color.FromArgb(175, 247, 251, 255));
        r["Surface"] = new SolidColorBrush(Dark ? Color.FromArgb(175, 22, 30, 43) : Color.FromArgb(165, 236, 243, 250));
        r["Line"] = new SolidColorBrush(Dark ? Color.FromArgb(65, 190, 215, 245) : Color.FromArgb(200, 255, 255, 255));
        r["Accent"] = new SolidColorBrush(Dark ? Color.FromRgb(134, 190, 245) : Color.FromRgb(41, 102, 174));
        r["Hover"] = new SolidColorBrush(Dark ? Color.FromArgb(100, 82, 114, 155) : Color.FromArgb(220, 215, 232, 248));
        if (SystemParameters.HighContrast)
        {
            r["Ink"] = SystemColors.WindowTextBrush;
            r["Muted"] = SystemColors.WindowTextBrush;
            r["Card"] = SystemColors.WindowBrush;
            r["Surface"] = SystemColors.WindowBrush;
            r["Line"] = SystemColors.WindowTextBrush;
            r["Accent"] = SystemColors.HighlightBrush;
            r["Hover"] = SystemColors.ControlBrush;
        }
        if (!r.Contains(typeof(Button)))
        {
            var styles = (ResourceDictionary)XamlReader.Parse("""
            <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
            <Style TargetType="Button"><Setter Property="Foreground" Value="{DynamicResource Ink}"/><Setter Property="Background" Value="{DynamicResource Card}"/><Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="BorderThickness" Value="1"/><Setter Property="Padding" Value="12,8"/><Setter Property="Margin" Value="3"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="12" Padding="{TemplateBinding Padding}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Box" Property="Background" Value="{DynamicResource Hover}"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Box" Property="Opacity" Value="0.72"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Box" Property="Opacity" Value="0.45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
            <Style TargetType="TextBox"><Setter Property="Foreground" Value="{DynamicResource Ink}"/><Setter Property="Background" Value="{DynamicResource Card}"/><Setter Property="BorderBrush" Value="{DynamicResource Line}"/><Setter Property="Padding" Value="9"/><Setter Property="Margin" Value="3,5"/><Setter Property="MinHeight" Value="34"/></Style>
            <Style TargetType="CheckBox"><Setter Property="Foreground" Value="{DynamicResource Ink}"/><Setter Property="Margin" Value="5"/><Setter Property="VerticalContentAlignment" Value="Center"/></Style>
            <Style TargetType="ListBox"><Setter Property="Foreground" Value="{DynamicResource Ink}"/><Setter Property="Background" Value="Transparent"/><Setter Property="BorderThickness" Value="0"/><Setter Property="ScrollViewer.CanContentScroll" Value="True"/><Setter Property="VirtualizingPanel.IsVirtualizing" Value="True"/><Setter Property="VirtualizingPanel.VirtualizationMode" Value="Recycling"/></Style>
            <Style TargetType="ListBoxItem"><Setter Property="Padding" Value="8"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/></Style>
            </ResourceDictionary>
            """);
            r.MergedDictionaries.Add(styles);
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
        var b = new Border { Child = content, CornerRadius = new(16), Padding = new(12), Margin = new(4), BorderThickness = new(1) };
        b.SetResourceReference(Border.BackgroundProperty, "Card");
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }
    internal static StackPanel Row(params UIElement[] items)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
            s.Children.Add(i);
        return s;
    }
    internal static string? Prompt(Window owner, string title, string value = "", Action<string>? save = null)
    {
        var window = new GlassWindow { Title = title, Width = 400, Height = 210, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner };
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
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(160)));
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
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brushes.Transparent;
        MinWidth = 220;
        MinHeight = 70;
        frame = new Border { CornerRadius = new(18), BorderThickness = new(1), Padding = new(14) };
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
    internal void SetPage(string title, UIElement content, bool close = true)
    {
        var layout = new DockPanel();
        var header = new DockPanel { Margin = new(4, 0, 4, 12) };
        if (close)
        {
            var x = Design.Button("×", Close);
            x.FontSize = 20;
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
