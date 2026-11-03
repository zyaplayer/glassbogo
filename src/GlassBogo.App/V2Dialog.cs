namespace GlassBogo.App;

// Ordinary modal window with consistent exits and a footer that stays visible
// when the working area is small. In-flight commits finish before dismissal.
internal class V2Dialog : LiquidWindow
{
    internal readonly ContentControl Main = new();
    internal readonly WrapPanel Actions = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
    internal readonly Button Cancel;
    private readonly Button close;
    internal bool Busy { get; private set; }
    internal V2Dialog(Window owner, V2Context ctx, string title, double width, double height) : base(ctx.Graphics)
    {
        Title = title; Owner = owner; Width = width; Height = height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new DockPanel { Margin = new(0, 0, 0, 10) };
        close = V2Ui.Button("×", Close); close.MinWidth = 32; close.Padding = new(6); close.ToolTip = "关闭";
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭对话框");
        DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        var label = V2Ui.Label(title, 19); label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextWrapping = TextWrapping.NoWrap; heading.Children.Add(label);
        root.Children.Add(heading); Grid.SetRow(Main, 1); root.Children.Add(Main);
        Cancel = V2Ui.Button("取消", Close); Cancel.IsCancel = true; Actions.Children.Add(Cancel);
        Grid.SetRow(Actions, 2); root.Children.Add(Actions); Body.Content = root;
        Closing += (_, e) => { if (Busy && !ctx.Closing) e.Cancel = true; };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (!Busy) Close(); } };
        Loaded += (_, _) =>
        {
            var work = Display.PrimaryScreen.WorkingArea;
            if (!Native.GetWindowRect(Handle, out var bounds)) return;
            var x = Math.Clamp(bounds.Left, work.Left + 8, Math.Max(work.Left + 8, work.Right - (bounds.Right - bounds.Left) - 8));
            var y = Math.Clamp(bounds.Top, work.Top + 8, Math.Max(work.Top + 8, work.Bottom - (bounds.Bottom - bounds.Top) - 8));
            Native.SetWindowPos(Handle, 0, x, y, 0, 0, 0x15); // keep size/Z order/focus
        };
    }
    internal void SetBusy(bool value)
    {
        Busy = value; close.IsEnabled = Cancel.IsEnabled = Main.IsEnabled = !value;
    }
    internal void Complete() { SetBusy(false); if (IsVisible) DialogResult = true; }
}
