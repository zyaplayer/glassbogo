using System.Runtime.InteropServices;

namespace GlassBogo.App;

internal sealed class Search2Window : LiquidWindow
{
    private readonly V2Context ctx;
    internal readonly TextBox Query = new() { FontSize = 20, Margin = new(0, 0, 0, 12) };
    internal readonly ListBox Results;
    private readonly TextBlock status = V2Ui.Label("输入名称搜索应用或已管理入口", 12, "Muted");
    private CancellationTokenSource? cancellation;
    private int generation;
    private int session;
    private bool launching;
    private nint previous;
    internal Search2Window(V2Context ctx) : base(ctx.Graphics)
    {
        this.ctx = ctx; Title = "GlassBogo 搜索"; Width = 640; Height = 420;
        ShowInTaskbar = false; Topmost = true;
        Results = V2Ui.EntryList(ctx); Results.SelectionMode = SelectionMode.Single;
        var body = new DockPanel(); DockPanel.SetDock(Query, Dock.Top); body.Children.Add(Query);
        DockPanel.SetDock(status, Dock.Bottom); body.Children.Add(status); body.Children.Add(Results); Body.Content = body;
        Query.TextChanged += async (_, _) => await RefreshResults();
        Query.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up)
            {
                e.Handled = true; if (Results.Items.Count == 0) return;
                var step = e.Key == Key.Down ? 1 : -1;
                Results.SelectedIndex = Results.SelectedIndex < 0 ? step > 0 ? 0 : Results.Items.Count - 1 : Math.Clamp(Results.SelectedIndex + step, 0, Results.Items.Count - 1);
                if (Results.SelectedItem is not null) Results.ScrollIntoView(Results.SelectedItem);
            }
            else if (e.Key == Key.Enter) { e.Handled = true; await LaunchSelected(); }
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(true); } };
        Results.PreviewMouseLeftButtonUp += async (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: V2EntryItem row }) return;
            Results.SelectedItem = row; e.Handled = true; await LaunchSelected();
        };
        Results.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await LaunchSelected(); } };
        Results.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: V2EntryItem item } container)
            { container.IsSelected = true; Results.ContextMenu = V2Ui.EntryMenu(this, ctx, item.Entry); }
        };
        Deactivated += (_, _) => { if (IsVisible && !launching && ctx.Menus == 0 && ctx.Modals == 0) Dismiss(false); };
        Closing += (_, e) => { if (!ctx.Closing) { e.Cancel = true; Dismiss(true); } };
        ctx.Changed += Changed;
        Closed += (_, _) => { cancellation?.Cancel(); cancellation?.Dispose(); ctx.Changed -= Changed; };
    }
    private async void Changed(object? sender, EventArgs e) { if (IsVisible) await RefreshResults(); }
    internal void Open()
    {
        session++;
        previous = Native.GetForegroundWindow();
        var screen = Display.PrimaryScreen; var work = screen.WorkingArea; var scale = screen.Scale;
        Width = Math.Min(640, work.Width / scale - 24); Height = Math.Min(420, work.Height / scale * .7);
        Left = (work.Left + (work.Width - Width * scale) / 2) / scale; Top = (work.Top + work.Height * .18) / scale;
        Query.Clear(); Results.ItemsSource = null; status.Text = "输入名称搜索应用或已管理入口"; Show(); Activate(); Query.Focus();
    }
    internal void Dismiss(bool restore)
    {
        session++; generation++; cancellation?.Cancel(); Hide();
        if (restore && previous != 0 && IsWindow(previous)) SetForegroundWindow(previous);
    }
    internal async Task RefreshResults()
    {
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = new(); var token = cancellation.Token; var request = ++generation;
        var text = Query.Text.Trim();
        if (text.Length == 0) { Results.ItemsSource = null; status.Text = "输入名称搜索应用或已管理入口"; return; }
        // Managed matches are immediate; discovered applications arrive asynchronously.
        Apply([]);
        try { var discovered = await ctx.Discovery.GetAsync(token); token.ThrowIfCancellationRequested(); Apply(discovered); }
        catch (OperationCanceledException) { }
        catch { if (request == generation) status.Text = "应用发现暂不可用，仍可搜索已管理入口。"; }
        void Apply(IReadOnlyList<Entry> discovered)
        {
            if (request != generation || token.IsCancellationRequested) return;
            var chosen = (Results.SelectedItem as V2EntryItem)?.Entry;
            var found = Finder.Search(ctx.Db.Current.Entries, discovered.Where(e => V2Ui.TypeHint(e) == "应用"), text);
            Results.ItemsSource = found.Select(e => new V2EntryItem(e, V2Ui.TypeHint(e))).ToArray();
            Results.SelectedIndex = found.Count == 0 ? -1 : chosen is null ? 0 : Math.Max(0, found.ToList().FindIndex(e => Catalog.Identity(e) == Catalog.Identity(chosen)));
            status.Text = found.Count == 0 ? "没有匹配结果" : $"{found.Count} 个结果 · ↑↓ 选择 · Enter 打开 · Esc 返回";
        }
    }
    internal async Task LaunchSelected()
    {
        if (launching || Results.SelectedItem is not V2EntryItem row) return;
        launching = true;
        var started = session;
        try
        {
            var result = await ctx.Launch(row.Entry);
            if (started != session || ctx.Closing || !IsVisible) return;
            if (result.Success) Dismiss(false); else status.Text = "无法打开：" + result.Error;
        }
        catch (Exception ex) { if (started == session && IsVisible && !ctx.Closing) status.Text = "无法打开：" + ex.Message; }
        finally { launching = false; }
    }
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
}
