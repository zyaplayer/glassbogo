using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBogo.App;

internal sealed class DockWindow : GlassWindow
{
    private readonly AppContext ctx;
    private State renderedState;
    private readonly StackPanel strip = new() { Orientation = Orientation.Horizontal };
    private readonly GlassWindow popup;
    private readonly DispatcherTimer openTimer = new();
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(HoverCoordinator.CloseDelay) };
    private readonly DispatcherTimer environmentTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer discoveryTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private sealed record PopupPage(UIElement Content, Button[] Entries);
    private readonly Dictionary<string, PopupPage> popupPages = new();
    private Button[] visibleEntries = [];
    private readonly HoverCoordinator hover = new();
    private readonly GlassMotion popupMotion;
    private readonly Border selection = new() { Width = 80, Height = 72, CornerRadius = new(16), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(4, 0, 0, 0), IsHitTestVisible = false, Visibility = Visibility.Hidden };
    private readonly TranslateTransform selectionShift = new();
    private Category? pending, active; private Button? anchor; private int page;
    private bool gridMode; private Button? gridAnchor; private Button[] gridButtons = [];
    private string[] archiveFiles = []; private DateTime archiveIndexAt;
    private nint hook; private nint locationHook; private readonly Native.WinEventProc foregroundCallback;
    private HwndSource? source; private bool collapsed; private readonly Button discoveries;
    private bool disposed;
    private readonly Grid line;
    private readonly ScaleTransform handleScale;
    private readonly Border handlePill;
    private FrameworkElement? bodyPanel;
    internal string SummonStatus { get; private set; } = "";
    internal string ShortcutStatus { get; private set; } = "";
    internal override void UpdateBackdrop()
    {
        base.UpdateBackdrop();
        if (!GlassEnvironment.Animate)
        {
            if (popupMotion.IsClosing) HidePopup(); else popupMotion.Stop();
            selectionShift.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
    internal DockWindow(AppContext context)
    {
        ctx = context;
        renderedState = context.Db.Current;
        Title = "GlassBogo";
        Height = 90;
        SetInsets(8);
        TransientMaterial = true;
        MinHeight = 12;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        popup = new GlassWindow { Title = "GlassBogo 分类", Width = 472, Height = 396, MinWidth = 0, MinHeight = 0, Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize };
        popup.TransientMaterial = true;
        popup.SetRadius(22);
        popup.SetInsets(16);
        popupMotion = new GlassMotion(popup.SurfaceRoot);
        popup.MouseEnter += (_, _) => { hover.SetHold(HoldReason.PointerInPopup, true); ResumePopup(); };
        popup.MouseLeave += (_, _) => { hover.SetHold(HoldReason.PointerInPopup, false); ScheduleClose(); };
        popup.PreviewKeyDown += PopupKeyDown;
        popup.PreviewMouseWheel += (_, e) => { if (e.Delta != 0 && popup.Width >= 472 && popup.Height >= 396) { ChangePage(e.Delta < 0 ? 1 : -1); e.Handled = true; } };
        popup.GotKeyboardFocus += (_, _) => { hover.SetHold(HoldReason.KeyboardFocus, true); ResumePopup(); };
        popup.LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(() => { hover.SetHold(HoldReason.KeyboardFocus, popup.IsKeyboardFocusWithin); ScheduleClose(); });
        TrackExternalDrag(popup);
        discoveries = Design.IconButton("plus", "添加入口 / 新发现", () => ctx.OpenWorkspace(true));
        discoveries.ToolTip = "添加入口 / 新发现";
        var body = new DockPanel();
        var actions = Design.Row(Design.IconButton("search", "搜索与工作台", () => ctx.OpenWorkspace()), Design.IconButton("calendar", "日历", () => ctx.OpenCalendar()), discoveries);
        actions.VerticalAlignment = VerticalAlignment.Center;
        actions.Margin = new(12, 0, 0, 0);
        DockPanel.SetDock(actions, Dock.Right);
        body.Children.Add(actions);
        var categoryLayer = new Grid();
        selection.SetResourceReference(Border.BackgroundProperty, "Selected");
        selection.RenderTransform = selectionShift;
        categoryLayer.Children.Add(selection);
        categoryLayer.Children.Add(strip);
        var scroller = new ScrollViewer { Content = categoryLayer, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroller.PreviewMouseWheel += (_, e) => { scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset - e.Delta); e.Handled = true; };
        body.Children.Add(scroller);
        line = HandleVisual.Create(104, 9, out handleScale, out handlePill);
        line.Visibility = Visibility.Collapsed;
        var shell = new Grid();
        shell.Children.Add(body);
        shell.Children.Add(line);
        bodyPanel = body;
        SetBody(shell);
        openTimer.Tick += (_, _) => { openTimer.Stop(); if (pending is not null && anchor is not null && hover.Pending == pending.Id) ShowCategory(pending, anchor); };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (hover.IsHeld || popup.IsMouseOver || IsMouseOver || popup.IsKeyboardFocusWithin) return;
            if (PointerInCorridor()) { closeTimer.Start(); return; }
            HidePopup(true);
            SetCollapsed(true);
        };
        MouseEnter += (_, _) => { hover.SetHold(HoldReason.PointerInDock, true); ResumePopup(); HandleVisual.Hover(handleScale, handlePill, true); };
        MouseLeave += (_, _) => { hover.SetHold(HoldReason.PointerInDock, false); HandleVisual.Hover(handleScale, handlePill, false); ScheduleClose(); };
        // 收起态改成点击展开：悬停只提亮，不再自动弹开，避免窗口缩放补偿导致的闪开闪收。
        MouseLeftButtonUp += (_, _) => { if (collapsed) SetCollapsed(false); };
        environmentTimer.Tick += (_, _) => { environmentTimer.Stop(); AssessForeground(); };
        foregroundCallback = (_, ev, hwnd, obj, _, _, _) => { if (ev == 3 || hwnd == Native.GetForegroundWindow() && obj == 0) Dispatcher.BeginInvoke(() => { environmentTimer.Stop(); environmentTimer.Start(); }); };
        Loaded += (_, _) => { popup.Owner = this; PositionDock(); hook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 2); locationHook = Native.SetWinEventHook(0x800B, 0x800B, 0, foregroundCallback, 0, 0, 2); source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle); source?.AddHook(WindowMessage); RegisterShortcut(); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        ctx.Db.Changed += DataChanged;
        ctx.Desktop.Changed += DesktopChanged;
        discoveryTimer.Tick += async (_, _) => { discoveryTimer.Stop(); await RefreshDiscoveries(); };
        Closing += (_, _) => { if (!ctx.Closing) { ctx.Closing = true; Dispatcher.BeginInvoke(() => Application.Current.Shutdown()); } };
        Closed += (_, _) => { disposed = true; openTimer.Stop(); closeTimer.Stop(); environmentTimer.Stop(); discoveryTimer.Stop(); popupMotion.Stop(); selectionShift.BeginAnimation(TranslateTransform.XProperty, null); Native.UnhookWinEvent(hook); Native.UnhookWinEvent(locationHook); if (source is not null) { Native.UnregisterHotKey(source.Handle, 1); Native.UnregisterHotKey(source.Handle, 2); source.RemoveHook(WindowMessage); } SystemEvents.DisplaySettingsChanged -= DisplayChanged; SystemEvents.PowerModeChanged -= PowerChanged; ctx.Db.Changed -= DataChanged; ctx.Desktop.Changed -= DesktopChanged; popup.Close(); };
        Render();
        SetCollapsed(true);
        discoveryTimer.Start();
    }
    private void DataChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        var next = ctx.Db.Current;
        var before = renderedState;
        renderedState = next;
        if (!before.Categories.SequenceEqual(next.Categories) || !before.Entries.SequenceEqual(next.Entries))
        {
            var selectedId = active?.Id;
            var wasVisible = popup.IsVisible;
            var affected = before.Categories.Concat(next.Categories).Select(c => c.Id).Distinct()
                .Where(id => before.Categories.FirstOrDefault(c => c.Id == id) != next.Categories.FirstOrDefault(c => c.Id == id) ||
                    !before.Entries.Where(e => e.CategoryId == id).SequenceEqual(next.Entries.Where(e => e.CategoryId == id))).ToHashSet();
            foreach (var key in popupPages.Keys.Where(key => affected.Any(id => key.StartsWith(id + ":", StringComparison.Ordinal))).ToArray()) popupPages.Remove(key);
            Render();
            var category = next.Categories.FirstOrDefault(c => c.Id == selectedId);
            if (wasVisible && gridMode)
                ShowGrid();
            else if (wasVisible && category is not null)
            {
                active = category;
                DrawPopup();
            }
            else HidePopup();
        }
        if (before.Preferences.Monitor != next.Preferences.Monitor)
        { HidePopup(); PositionDock(); }
        if (before.Preferences.AutoHide != next.Preferences.AutoHide && !next.Preferences.AutoHide)
            SetCollapsed(false);
        if (before.Preferences.QuickShortcut != next.Preferences.QuickShortcut)
            RegisterShortcut();
        if (!before.Entries.SequenceEqual(next.Entries) || !before.DismissedDiscoveries.SequenceEqual(next.DismissedDiscoveries))
        {
            discoveryTimer.Stop();
            discoveryTimer.Start();
        }
    });
    private void DesktopChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { discoveryTimer.Stop(); discoveryTimer.Start(); });
    private async Task RefreshDiscoveries()
    {
        try
        {
            var found = await ctx.Desktop.DiscoverAsync(CancellationToken.None);
            var keys = ctx.Db.Current.Entries.Select(Catalog.Identity).ToHashSet(StringComparer.Ordinal);
            var count = found.Count(e => !keys.Contains(Catalog.Identity(e)) && !ctx.Db.Current.DismissedDiscoveries.Contains(Catalog.Identity(e), StringComparer.OrdinalIgnoreCase));
            discoveries.ToolTip = count > 0 ? $"发现 {count} 个新入口，点击整理" : "添加入口";
            discoveries.Tag = count > 0 ? "active" : null;
        }
        catch { discoveries.ToolTip = "桌面暂不可读，点击可重新发现"; }
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { HidePopup(); PositionDock(); });
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) => Dispatcher.BeginInvoke(() => { HidePopup(); Design.Apply(ctx.Db.Current.Preferences.Theme); PositionDock(); });
    private nint WindowMessage(nint h, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x2E0 || msg == 0x1A)
            Dispatcher.BeginInvoke(() => { HidePopup(); PositionDock(); });
        if (msg == 0x312)
        {
            if (w == 2) ToggleSummon(); else ctx.QuickTask();
            handled = true;
        }
        return 0;
    }
    private void RegisterShortcut()
    {
        if (source is null)
            return;
        Native.UnregisterHotKey(source.Handle, 1);
        var key = ctx.Db.Current.Preferences.QuickShortcut;
        ShortcutStatus = key.Length == 0 ? "已关闭" : Native.RegisterHotKey(source.Handle, 1, 0x4000 | 0x1 | 0x2, (uint)key[0]) ? $"Ctrl+Alt+{key} 已启用" : "快捷键被占用，可使用日历中的速记按钮。";
        Native.UnregisterHotKey(source.Handle, 2);
        SummonStatus = Native.RegisterHotKey(source.Handle, 2, 0x1, 0xC0) ? "Alt+` 唤起 Dock" : "Alt+` 被其他程序占用";
    }
    private void Render()
    {
        openTimer.Stop();
        pending = null;
        strip.Children.Clear();
        selection.Visibility = Visibility.Hidden;
        var categories = new Button
        {
            Content = Tile(Design.Icon("folder", 20), "分类", ctx.Db.Current.Categories.Count + " 类"),
            Width = 76,
            Height = 72,
            Padding = new(4),
            Margin = new(4, 0, 4, 0),
            ToolTip = "全部分类"
        };
        categories.SetResourceReference(StyleProperty, "CategoryButton");
        System.Windows.Automation.AutomationProperties.SetName(categories, "全部分类");
        categories.MouseEnter += (_, _) => ResumePopup();
        categories.MouseLeave += (_, _) => ScheduleClose();
        categories.Click += (_, _) => { ShowGrid(true); popup.Activate(); popup.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); };
        gridAnchor = categories;
        strip.Children.Add(categories);
        foreach (var entry in ctx.Catalog.Pinned())
        {
            var image = new Image { Width = 40, Height = 40, Margin = new(0, 0, 0, 6) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _ = SetIcon(image, entry);
            var button = new Button { Content = Tile(image, entry.Name, null), DataContext = entry, Width = 76, Height = 72, Padding = new(4), Margin = new(4, 0, 4, 0), ToolTip = entry.Target };
            button.SetResourceReference(StyleProperty, "CategoryButton");
            System.Windows.Automation.AutomationProperties.SetName(button, entry.Name);
            button.MouseEnter += (_, _) => ResumePopup();
            button.Click += async (_, _) => { HidePopup(); SetCollapsed(true); await ctx.Open(entry); };
            var menu = new ContextMenu();
            AddMenu(menu, "取消钉住", () => ctx.Catalog.SetPinned([entry.Id], false));
            AddMenu(menu, "归类到…", () => { HidePopup(); ctx.OpenWorkspace(); });
            AddMenu(menu, "移除入口（保留原文件）", () => ctx.Catalog.Remove([entry.Id]));
            TrackMenu(menu);
            button.ContextMenu = menu;
            _ = MarkUnavailable(button, entry);
            strip.Children.Add(button);
        }
        var add = Design.Button("＋ 钉住", () => { HidePopup(); ctx.OpenWorkspace(); });
        add.Width = 76;
        add.Height = 72;
        add.Padding = new(8);
        add.Margin = new(4, 0, 4, 0);
        add.ToolTip = "在工作台里挑选要钉住到这里的入口";
        add.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        add.VerticalAlignment = VerticalAlignment.Center;
        strip.Children.Add(add);
        PositionDock();
        if (popup.IsVisible)
        {
            if (gridMode)
                ShowGrid();
            else if (active is not null)
                DrawPopup();
        }
    }
    private static StackPanel Tile(UIElement icon, string name, string? hint)
    {
        var panel = new StackPanel();
        panel.Children.Add(icon);
        var label = Design.Text(name, 12);
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.TextWrapping = TextWrapping.NoWrap;
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.FontWeight = FontWeights.Medium;
        label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        panel.Children.Add(label);
        if (hint is not null)
        {
            var muted = Design.Text(hint, 10, "Muted");
            muted.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(muted);
        }
        return panel;
    }
    private async Task SetIcon(Image image, Entry entry)
    {
        try
        {
            var icon = await ctx.Icons.Get(entry);
            if (!image.Dispatcher.HasShutdownStarted)
                await image.Dispatcher.InvokeAsync(() => image.Source = icon);
        }
        catch { }
    }
    private void QueueCategory(Category category, Button button, bool dragging = false)
    {
        ResumePopup();
        pending = category;
        anchor = button;
        openTimer.Stop();
        openTimer.Interval = TimeSpan.FromMilliseconds(hover.Enter(category.Id, dragging));
        openTimer.Start();
    }
    private void UpdateSelection(Button button)
    {
        foreach (var tile in strip.Children.OfType<Button>()) tile.Tag = ReferenceEquals(tile, button) ? "active" : null;
        var x = Math.Max(0, button.TranslatePoint(new Point(), strip).X - 4);
        if (selection.Visibility != Visibility.Visible) selectionShift.X = x;
        selection.Visibility = Visibility.Visible;
        selection.Width = Math.Max(48, button.ActualWidth > 0 ? button.ActualWidth : button.Width);
        Design.Animate(selectionShift, TranslateTransform.XProperty, x, 90);
    }
    private void ShowCategory(Category category, Button button, bool clicked = false)
    {
        openTimer.Stop();
        closeTimer.Stop();
        var wasClosing = hover.Phase == HoverPhase.Closing;
        var fresh = !popup.IsVisible;
        var changed = hover.Open(category.Id);
        var generation = hover.Generation;
        if (!changed && !fresh && !gridMode)
        {
            if (wasClosing) popupMotion.Open(false, 140, () => hover.Opened(generation));
            else hover.Opened(generation);
            return;
        }
        gridMode = false;
        active = category;
        anchor = strip.Children.Contains(button) ? button : gridAnchor;
        if (strip.Children.Contains(button))
            UpdateSelection(button);
        page = 0;
        DrawPopup(!fresh && !wasClosing);
        Present(fresh, wasClosing, clicked, generation);
    }
    private void ShowGrid(bool clicked = false)
    {
        openTimer.Stop();
        closeTimer.Stop();
        var wasClosing = hover.Phase == HoverPhase.Closing;
        var fresh = !popup.IsVisible;
        var changed = hover.Open("__grid");
        var generation = hover.Generation;
        if (!changed && !fresh && gridMode)
        {
            if (wasClosing) popupMotion.Open(false, 140, () => hover.Opened(generation));
            return;
        }
        gridMode = true;
        active = null;
        if (gridAnchor is not null)
        {
            anchor = gridAnchor;
            UpdateSelection(gridAnchor);
        }
        popup.SetBody(BuildGrid(), !fresh);
        Present(fresh, wasClosing, clicked, generation, 640, 420);
    }
    private UIElement BuildGrid()
    {
        var layout = new DockPanel { MinWidth = 400, MinHeight = 362 };
        var header = new DockPanel { Height = 48, LastChildFill = true };
        var manage = Design.IconButton("settings", "管理入口", () => { HidePopup(); ctx.OpenWorkspace(); });
        manage.Width = manage.Height = 36;
        DockPanel.SetDock(manage, Dock.Right);
        header.Children.Add(manage);
        var searchBox = new TextBox { Width = 200, Margin = new(6, 8, 6, 8), ToolTip = "搜索入口和归档文件，Enter 打开第一个" };
        DockPanel.SetDock(searchBox, Dock.Right);
        header.Children.Add(searchBox);
        var title = Design.Text($"全部分类  ·  {ctx.Db.Current.Categories.Count}", 18);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new(6, 0, 4, 0);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var footer = Design.Row(
            Design.Text("点分类看入口；输入即搜入口和归档文件。", 12, "Muted"),
            Design.Button("新建分类", () => { HidePopup(); ctx.OpenWorkspace(); }));
        footer.Height = 44;
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);

        var host = new Grid();
        var results = new StackPanel();
        var resultsScroll = new ScrollViewer { Content = results, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
        host.Children.Add(resultsScroll);

        var browse = new StackPanel();
        var recents = ctx.Catalog.Recents(8).ToArray();
        if (recents.Length > 0)
        {
            browse.Children.Add(SectionLabel("最近用过"));
            var row = new WrapPanel();
            foreach (var entry in recents)
            {
                var image = new Image { Width = 24, Height = 24, Margin = new(0, 0, 6, 0) };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                _ = SetIcon(image, entry);
                var button = new Button { Content = Design.Row(image, Design.Text(entry.Name, 12)), Padding = new(10, 5, 10, 5), Margin = new(0, 0, 6, 6), ToolTip = entry.Target };
                button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
                button.Click += async (_, _) => { HidePopup(); SetCollapsed(true); await ctx.Open(entry); };
                row.Children.Add(button);
            }
            browse.Children.Add(row);
        }
        browse.Children.Add(SectionLabel("全部分类"));
        var grid = new UniformGrid { Columns = 4 };
        var buttons = new List<Button>();
        foreach (var cat in ctx.Db.Current.Categories)
        {
            var entries = ctx.Db.Current.Entries.Where(e => e.CategoryId == cat.Id).ToArray();
            var panel = new StackPanel();
            var mini = new UniformGrid { Columns = 2, Rows = 2, Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 4) };
            if (entries.Length == 0)
                mini.Children.Add(Design.Icon("folder", 20));
            else
                foreach (var entry in entries.Take(4))
                {
                    var icon = new Image { Width = 18, Height = 18, Margin = new(1) };
                    RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                    mini.Children.Add(icon);
                    _ = SetIcon(icon, entry);
                }
            panel.Children.Add(mini);
            var label = Design.Text(cat.Name, 12);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.TextWrapping = TextWrapping.NoWrap;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.FontWeight = FontWeights.Medium;
            panel.Children.Add(label);
            var count = Design.Text($"{entries.Length} 项", 10, "Muted");
            count.HorizontalAlignment = HorizontalAlignment.Center;
            panel.Children.Add(count);
            var button = new Button { Content = panel, DataContext = cat, Width = 92, Height = 80, Padding = new(4), Margin = new(4), ToolTip = cat.Name, AllowDrop = true };
            button.SetResourceReference(StyleProperty, "CategoryButton");
            System.Windows.Automation.AutomationProperties.SetName(button, cat.Name + " " + entries.Length);
            button.Click += (_, _) => { ShowCategory(cat, button, true); popup.Activate(); popup.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); };
            var menu = new ContextMenu();
            if (cat.Id != "inbox")
            {
                AddMenu(menu, "重命名", () => { var hold = hover.Hold(HoldReason.Editing); try { Design.Prompt(this, "分类名称", cat.Name, name => ctx.Catalog.SaveCategory(cat.Id, name)); } finally { hold.Dispose(); ScheduleClose(); } });
                AddMenu(menu, "移除分类（入口回到待整理）", () => ctx.Catalog.RemoveCategory(cat.Id));
            }
            AddMenu(menu, "添加入口", () => ctx.OpenWorkspace(true));
            TrackMenu(menu);
            button.ContextMenu = menu;
            TrackExternalDrag(button);
            button.Drop += async (_, e) => { e.Handled = true; var hold = hover.Hold(HoldReason.OLEDrag); try { await ImportDrop(e.Data, cat.Id); } catch (Exception ex) { Design.Error(ex); } finally { hold.Dispose(); ScheduleClose(); } };
            grid.Children.Add(button);
            buttons.Add(button);
        }
        var gridScroll = new ScrollViewer { Content = browse, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        browse.Children.Add(grid);
        host.Children.Add(gridScroll);
        layout.Children.Add(host);
        Array.ForEach(grid.Children.OfType<Button>().ToArray(), b => b.Tag = null);
        gridButtons = buttons.ToArray();
        visibleEntries = gridButtons;

        void RefreshResults()
        {
            var query = searchBox.Text.Trim();
            results.Children.Clear();
            if (query.Length == 0)
            {
                resultsScroll.Visibility = Visibility.Collapsed;
                gridScroll.Visibility = Visibility.Visible;
                visibleEntries = gridButtons;
                return;
            }
            var hits = new List<Button>();
            foreach (var entry in Finder.Entries(ctx.Db.Current.Entries, query, 6))
            {
                var target = entry;
                var button = ResultButton(target.Name, target.Target, () => { HidePopup(); SetCollapsed(true); _ = ctx.Open(target); });
                results.Children.Add(button);
                hits.Add(button);
            }
            foreach (var file in Finder.Files(ArchiveFiles(), query, 6))
            {
                var path = file;
                var button = ResultButton(Path.GetFileName(path), Path.GetDirectoryName(path) ?? "", () => { HidePopup(); SetCollapsed(true); OpenPath(path); });
                results.Children.Add(button);
                hits.Add(button);
            }
            if (hits.Count == 0)
                results.Children.Add(Design.Text("没有匹配的入口或归档文件。", 13, "Muted"));
            visibleEntries = hits.ToArray();
            gridScroll.Visibility = Visibility.Collapsed;
            resultsScroll.Visibility = Visibility.Visible;
        }
        searchBox.TextChanged += (_, _) => RefreshResults();
        searchBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || visibleEntries.Length == 0 || searchBox.Text.Trim().Length == 0)
                return;
            visibleEntries[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
        return layout;
    }
    private static TextBlock SectionLabel(string text)
    {
        var label = Design.Text(text, 12, "Muted");
        label.Margin = new(6, 8, 0, 6);
        return label;
    }
    private static Button ResultButton(string name, string hint, Action open)
    {
        var panel = new DockPanel { LastChildFill = true };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Design.Text(name, 14));
        var muted = Design.Text(hint, 11, "Muted");
        muted.TextTrimming = TextTrimming.CharacterEllipsis;
        muted.TextWrapping = TextWrapping.NoWrap;
        text.Children.Add(muted);
        panel.Children.Add(text);
        var button = new Button { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 4), Padding = new(10, 6, 10, 6), ToolTip = hint };
        button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
        button.Click += (_, _) => open();
        System.Windows.Automation.AutomationProperties.SetName(button, name);
        return button;
    }
    private string[] ArchiveFiles()
    {
        if (archiveFiles.Length > 0 && DateTime.UtcNow - archiveIndexAt < TimeSpan.FromSeconds(20))
            return archiveFiles;
        var list = new List<string>();
        foreach (var batch in ctx.Harvest.History().Take(12))
        {
            if (!Directory.Exists(batch.ArchiveRoot))
                continue;
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(batch.ArchiveRoot, "*", SearchOption.AllDirectories).Take(2000))
                    list.Add(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        archiveFiles = [.. list];
        archiveIndexAt = DateTime.UtcNow;
        return archiveFiles;
    }
    private void OpenPath(string path)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Design.Error(ex); }
    }
    private void Present(bool fresh, bool wasClosing, bool clicked, int generation, double widthDip = 472, double heightDip = 396)
    {
        var screen = Display.FromHandle(new WindowInteropHelper(this).Handle);
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // A stable panel position avoids chasing a moving window while switching groups.
        var pos = PointToScreen(new Point());
        var placement = SurfacePlacement.Popup(screen.WorkingArea, scale,
            new((int)pos.X, (int)pos.Y, (int)(pos.X + ActualWidth * scale), (int)(pos.Y + ActualHeight * scale)), widthDip, heightDip);
        popup.Width = placement.Width / scale;
        popup.Height = placement.Height / scale;
        popup.Left = placement.Left / scale;
        popup.Top = placement.Top / scale;
        if (!popup.IsVisible)
            popup.Show();
        if (fresh || wasClosing) popupMotion.Open(fresh, clicked ? 140 : 150, () => hover.Opened(generation));
        else hover.Opened(generation);
        Native.SetWindowPos(new WindowInteropHelper(popup).Handle, 0, placement.Left, placement.Top, placement.Width, placement.Height, 0x14);
    }
    private void DrawPopup(bool crossFade = false)
    {
        if (active is null)
            return;
        var entries = ctx.Db.Current.Entries.Where(e => e.CategoryId == active.Id).ToArray();
        var pages = Math.Max(1, (int)Math.Ceiling(entries.Length / 12d));
        page = Math.Clamp(page, 0, pages - 1);
        var cacheKey = active.Id + ":" + page;
        if (popupPages.TryGetValue(cacheKey, out var cached))
        {
            popup.SetBody(cached.Content, crossFade);
            visibleEntries = cached.Entries;
            return;
        }
        var layout = new DockPanel { MinWidth = 400, MinHeight = 362 };
        var header = new DockPanel { Height = 48, LastChildFill = true };
        var manage = Design.IconButton("search", "搜索与管理入口", () => { HidePopup(); ctx.OpenWorkspace(); });
        manage.Width = manage.Height = 36;
        DockPanel.SetDock(manage, Dock.Right);
        header.Children.Add(manage);
        var title = Design.Text(active.Name + $"  ·  {entries.Length}", 18);
        title.TextWrapping = TextWrapping.NoWrap;
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new(6, 0, 4, 0);
        header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        var footer = Design.Row(Design.Button("‹ 分类", () => ShowGrid()), Design.Button("‹", () => { page = Math.Max(0, page - 1); DrawPopup(); }), Design.Text($"{page + 1} / {pages} · {entries.Length} 个入口", 12, "Muted"), Design.Button("›", () => { page = Math.Min(pages - 1, page + 1); DrawPopup(); }), Design.Button("管理", () => { HidePopup(); ctx.OpenWorkspace(); }));
        footer.Height = 44;
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        var grid = new UniformGrid { Columns = 4, Rows = 3 };
        var buttons = new List<Button>();
        foreach (var entry in entries.Skip(page * 12).Take(12))
        {
            var content = new StackPanel();
            var image = new Image { Width = 44, Height = 44, Margin = new(0, 0, 0, 8) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            _ = SetIcon(image, entry);
            content.Children.Add(image);
            var label = Design.Text(entry.Name, 12);
            label.TextWrapping = TextWrapping.NoWrap;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            content.Children.Add(label);
            var b = new Button { Content = content, ToolTip = entry.Target, Padding = new(4), AllowDrop = true };
            b.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            System.Windows.Automation.AutomationProperties.SetName(b, entry.Name);
            var dragged = false;
            Point start = default;
            b.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(b); dragged = false; };
            b.PreviewMouseMove += (_, e) => { if (e.LeftButton != MouseButtonState.Pressed || dragged || (e.GetPosition(b) - start).Length < 8) return; dragged = true; var hold = hover.Hold(HoldReason.Dragging); try { DragDrop.DoDragDrop(b, new DataObject("GlassBogo.Entry", entry.Id), DragDropEffects.Move); } finally { hold.Dispose(); ScheduleClose(); } };
            b.Click += async (_, _) => { if (dragged) return; HidePopup(); SetCollapsed(true); await ctx.Open(entry); };
            b.Drop += (_, e) => { if (e.Data.GetData("GlassBogo.Entry") is string id) { e.Handled = true; Design.Guard(() => ctx.Catalog.Reorder(id, entry.Id)); } };
            var menu = new ContextMenu();
            AddMenu(menu, "重新定位目标…", () => { HidePopup(); ctx.LocateEntries([entry]); });
            AddMenu(menu, "移除入口（保留原文件）", () => ctx.Catalog.Remove([entry.Id]));
            TrackMenu(menu);
            b.ContextMenu = menu;
            _ = MarkUnavailable(b, entry);
            grid.Children.Add(b);
            buttons.Add(b);
        }
        if (entries.Length == 0)
        {
            var empty = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            empty.Children.Add(Design.Icon("folder", 36));
            var hint = Design.Text("拖入应用或文件以添加入口", 14, "Muted");
            hint.Margin = new(0, 16, 0, 12);
            empty.Children.Add(hint);
            empty.Children.Add(Design.Button("添加入口", () => { HidePopup(); ctx.OpenWorkspace(true); }));
            layout.Children.Add(empty);
        }
        else
            layout.Children.Add(grid);
        if (popupPages.Count >= 24)
            popupPages.Remove(popupPages.Keys.First());
        visibleEntries = buttons.ToArray();
        var viewport = new ScrollViewer { Content = layout, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        popupPages[cacheKey] = new(viewport, visibleEntries);
        popup.SetBody(viewport, crossFade);
        popup.AllowDrop = true;
        popup.Drop -= PopupDrop;
        popup.Drop += PopupDrop;
    }
    private async void PopupDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || active is null)
            return;
        e.Handled = true;
        var hold = hover.Hold(HoldReason.OLEDrag);
        try
        {
            await ImportDrop(e.Data, active.Id);
        }
        catch (Exception ex) { Design.Error(ex); }
        finally { hold.Dispose(); ScheduleClose(); }
    }
    internal async Task ImportDrop(IDataObject data, string category)
    {
        if (data.GetData("GlassBogo.Entry") is string id)
        {
            ctx.Catalog.Move([id], category);
            return;
        }
        if (data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var entries = await Platform.Sta(() => paths.Select(p => Platform.Resolve(p) with { CategoryId = category }).ToArray());
            var result = ctx.Catalog.AddRange(entries);
            if (result.Existing > 0)
                ctx.LocateEntries(entries);
        }
    }
    private static void AddMenu(ContextMenu menu, string text, Action action)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => Design.Guard(action);
        menu.Items.Add(item);
    }
    private void ScheduleClose()
    {
        if (disposed) return;
        closeTimer.Stop();
        hover.ScheduleClose();
        closeTimer.Start();
    }
    private async Task MarkUnavailable(Button button, Entry entry)
    {
        if (entry.Kind != "path") return;
        var missing = await Task.Run(() => !File.Exists(entry.Target) && !Directory.Exists(entry.Target));
        if (!missing || disposed) return;
        // This continuation owns only its cached tile, never the current category or a shared icon.
        button.ToolTip = "⚠ 目标失效 · 右键选择“重新定位目标”\n" + entry.Target;
        System.Windows.Automation.AutomationProperties.SetHelpText(button, "目标失效，可重新定位目标");
        if (button.Content is StackPanel panel && panel.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
        {
            label.Text = "⚠ " + entry.Name;
        }
    }
    private void ResumePopup()
    {
        closeTimer.Stop();
        hover.CancelClose();
        if (popup.IsVisible && popupMotion.IsClosing)
        {
            var generation = hover.Generation;
            popupMotion.Open(false, 140, () => hover.Opened(generation));
        }
    }
    private void TrackMenu(ContextMenu menu)
    {
        IDisposable? hold = null;
        menu.Opened += (_, _) => { ResumePopup(); hold ??= hover.Hold(HoldReason.ContextMenu); };
        menu.Closed += (_, _) => { hold?.Dispose(); hold = null; ScheduleClose(); };
    }
    private void TrackExternalDrag(UIElement element)
    {
        IDisposable? hold = null;
        void Release() { hold?.Dispose(); hold = null; ScheduleClose(); }
        element.PreviewDragEnter += (_, _) => { ResumePopup(); hold ??= hover.Hold(HoldReason.OLEDrag); };
        element.PreviewDragLeave += (_, _) => Release();
        element.PreviewDrop += (_, _) => { openTimer.Stop(); Release(); };
        if (element is FrameworkElement framework) framework.Unloaded += (_, _) => Release();
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);
    private bool PointerInCorridor()
    {
        if (!popup.IsVisible || !GetCursorPos(out var cursor) ||
            !Native.GetWindowRect(new WindowInteropHelper(this).Handle, out var dockRect) ||
            !Native.GetWindowRect(new WindowInteropHelper(popup).Handle, out var popupRect)) return false;
        // Only the current category's path to the panel is protected, not the whole desktop.
        var left = dockRect.Left;
        var right = dockRect.Right;
        if (anchor is { IsLoaded: true })
        {
            var start = anchor.PointToScreen(new Point());
            left = (int)start.X;
            right = (int)(start.X + anchor.ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX);
        }
        return HoverCoordinator.InCorridor(cursor.X, cursor.Y, left, right, dockRect.Top,
            popupRect.Left, popupRect.Right, popupRect.Bottom, 12 * VisualTreeHelper.GetDpi(this).DpiScaleX);
    }
    private void ChangePage(int direction)
    {
        if (active is null) return;
        var pages = Math.Max(1, (int)Math.Ceiling(ctx.Db.Current.Entries.Count(e => e.CategoryId == active.Id) / 12d));
        var next = Math.Clamp(page + direction, 0, pages - 1);
        if (next == page) return;
        page = next;
        DrawPopup(true);
    }
    private void PopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HidePopup(); e.Handled = true; return; }
        if (e.Key == Key.PageDown || e.Key == Key.PageUp)
        {
            ChangePage(e.Key == Key.PageDown ? 1 : -1);
            visibleEntries.FirstOrDefault()?.Focus();
            e.Handled = true;
            return;
        }
        var at = Array.FindIndex(visibleEntries, b => b.IsKeyboardFocusWithin);
        if (e.Key == Key.Enter && at >= 0)
        {
            visibleEntries[at].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
            return;
        }
        var offset = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -4, Key.Down => 4, _ => 0 };
        if (offset == 0 || visibleEntries.Length == 0) return;
        var next = at < 0 ? 0 : at + offset;
        if (next >= visibleEntries.Length) { ChangePage(1); next = 0; }
        else if (next < 0) { ChangePage(-1); next = visibleEntries.Length - 1; }
        visibleEntries[Math.Clamp(next, 0, visibleEntries.Length - 1)].Focus();
        e.Handled = true;
    }
    private void HidePopup(bool animate = false)
    {
        openTimer.Stop();
        closeTimer.Stop();
        pending = null;
        void Finish()
        {
            popup.Hide();
            popup.StopBodyTransition();
            popupMotion.Stop();
            active = null;
            selection.Visibility = Visibility.Hidden;
            foreach (var tile in strip.Children.OfType<Button>()) tile.Tag = null;
            hover.SetHold(HoldReason.PointerInPopup, false);
            hover.SetHold(HoldReason.KeyboardFocus, false);
        }
        if (animate && popup.IsVisible)
        {
            if (hover.BeginClose(false) is not { } generation) return;
            popupMotion.Close(() => { if (hover.CompleteClose(generation)) Finish(); });
        }
        else { hover.Reset(); Finish(); }
    }
    private void SetCollapsed(bool value)
    {
        collapsed = value;
        if (bodyPanel is not null)
            bodyPanel.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
        line.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        SetInsets(value ? 0 : 8);
        SetSurfaceHidden(value);
        if (value)
            HidePopup();
        PositionDock();
    }
    private void ToggleSummon()
    {
        if (collapsed)
        {
            SetCollapsed(false);
            Activate();
        }
        else
        {
            HidePopup();
            SetCollapsed(true);
        }
    }
    private Display Screen() => Display.AllScreens.FirstOrDefault(s => s.DeviceName == ctx.Db.Current.Preferences.Monitor) ?? Display.PrimaryScreen!;
    private void PositionDock()
    {
        var screen = Screen();
        var scale = screen.Scale;
        var work = screen.WorkingArea;
        PixelRect placement;
        if (collapsed)
        {
            // 收起态只是一条带抓握点的细胶囊：窗口保留热区，材质隐藏。
            var width = (int)Math.Round(280 * scale);
            var height = (int)Math.Round(26 * scale);
            var left = work.Left + (work.Width - width) / 2;
            var top = Math.Max(work.Top, work.Bottom - height - (int)Math.Round(8 * scale));
            placement = new(left, top, left + width, top + height);
        }
        else
            placement = SurfacePlacement.Dock(work, scale, ctx.Db.Current.Categories.Count, false);
        Width = placement.Width / scale;
        Height = placement.Height / scale;
        Left = placement.Left / scale;
        Top = placement.Top / scale;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            Native.SetWindowPos(handle, 0, placement.Left, placement.Top, placement.Width, placement.Height, 0x14);
    }
    private void AssessForeground()
    {
        if (ctx.Closing)
            return;
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == new WindowInteropHelper(this).Handle || hwnd == new WindowInteropHelper(popup).Handle)
            return;
        var cls = new System.Text.StringBuilder(128);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        var desktop = cls.ToString() is "Progman" or "WorkerW";
        var bounds = Screen().Bounds;
        var full = !desktop && Native.GetWindowRect(hwnd, out var r) && r.Left <= bounds.Left && r.Top <= bounds.Top && r.Right >= bounds.Right && r.Bottom >= bounds.Bottom;
        if (full)
        {
            HidePopup();
            Hide();
        }
        else if (!IsVisible)
        {
            Show();
            PositionDock();
        }
    }
    internal async Task QaVerifyIcons()
    {
        if (!strip.Children.OfType<Button>().Any(b => (b.ToolTip as string) == "全部分类"))
            throw new InvalidOperationException("Dock 缺少分类按钮。");
        var stamp = DateTime.Now;
        ctx.Catalog.Touch(ctx.Db.Current.Entries[0].Id, stamp);
        ctx.Catalog.Touch(ctx.Db.Current.Entries[1].Id, stamp.AddMinutes(1));
        ShowGrid();
        if (!popup.IsVisible || gridButtons.Length != ctx.Db.Current.Categories.Count)
            throw new InvalidOperationException("分类网格未生成。");
        // 网格图标是异步加载的，等它们落到 Image.Source 再断言。
        await Task.Delay(800);
        var previews = gridButtons.SelectMany(b => (b.Content as Panel)?.Children.OfType<UniformGrid>() ?? [])
            .SelectMany(g => g.Children.OfType<Image>()).ToArray();
        if (previews.Length == 0 || previews.Any(i => i.Source is null))
            throw new InvalidOperationException("分类预览图标未加载。");
        if (!Walk(popup).OfType<TextBlock>().Any(t => t.Text == "最近用过"))
            throw new InvalidOperationException("最近用过区域未出现。");
        var searchBox = Walk(popup).OfType<TextBox>().FirstOrDefault()
            ?? throw new InvalidOperationException("分类网格缺少搜索框。");
        searchBox.Text = "示例应用 1";
        if (visibleEntries.Length == 0)
            throw new InvalidOperationException("搜索没有返回结果。");
        searchBox.Text = "";
        HidePopup();
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Walk(child))
                yield return nested;
        }
    }
    internal void QaVerifyNavigation()
    {
        var category = ctx.Db.Current.Categories.First(c => ctx.Db.Current.Entries.Count(e => e.CategoryId == c.Id) > 12);
        ShowGrid();
        var tile = gridButtons.FirstOrDefault(b => b.DataContext is Category c && c.Id == category.Id)
            ?? throw new InvalidOperationException("分类网格缺少该分类。");
        ShowCategory(category, tile, true);
        var first = visibleEntries[0];
        ChangePage(1);
        if (page != 1 || ReferenceEquals(first, visibleEntries[0]))
            throw new InvalidOperationException("分类滚动未切换页面。");
        ChangePage(-1);
        if (page != 0 || !ReferenceEquals(first, visibleEntries[0]))
            throw new InvalidOperationException("分类缓存页面未复用。");
        popup.Activate();
        first.Focus();
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(popup), 0, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        PopupKeyDown(popup, key);
        if (!key.Handled || !visibleEntries[1].IsKeyboardFocusWithin)
            throw new InvalidOperationException("分类方向键未移动焦点。");
        HidePopup();
    }
    internal async Task QaVerifyTransitions()
    {
        ShowGrid();
        var cat = ctx.Db.Current.Categories[1];
        var button = gridButtons.First(b => b.DataContext is Category c && c.Id == cat.Id);
        ShowCategory(cat, button);
        await Task.Delay(180);
        var handle = new WindowInteropHelper(popup).Handle;
        var first = visibleEntries[0];
        hover.SetHold(HoldReason.PointerInDock, false);
        hover.SetHold(HoldReason.PointerInPopup, false);
        hover.SetHold(HoldReason.KeyboardFocus, false);
        HidePopup(true);
        await Task.Delay(40);
        ShowCategory(cat, button);
        await Task.Delay(180);
        if (!popup.IsVisible || popup.SurfaceRoot.Opacity != 1 || !ReferenceEquals(first, visibleEntries[0]))
            throw new InvalidOperationException("中断关闭后没有恢复相同页面。");
        for (var i = 2; i < 6; i++) QaCycle(i);
        var outgoing = visibleEntries[0];
        QaCycle(6);
        popup.Activate();
        if (GlassEnvironment.Animate && outgoing.Focus())
            throw new InvalidOperationException("交叉淡化中的旧页面仍能获得键盘焦点。");
        var focused = visibleEntries[0];
        if (!focused.Focus()) throw new InvalidOperationException("新页面无法获得焦点。");
        await Task.Delay(200);
        if (!focused.IsKeyboardFocusWithin)
            throw new InvalidOperationException("交叉淡化结束后当前页面丢失焦点。");
        if (handle != new WindowInteropHelper(popup).Handle || popup.SurfaceRoot.HasAnimatedProperties ||
            ((TranslateTransform)popup.SurfaceRoot.RenderTransform).HasAnimatedProperties)
            throw new InvalidOperationException("分类切换没有复用窗口或动画时钟未释放。");
        if (Width > Screen().WorkingArea.Width / Screen().Scale * .7 + 1 || button.Width != 92 || button.Height != 80)
            throw new InvalidOperationException("Dock 尺寸不符合规范。");
        HidePopup();
    }
    internal void QaCompactPopup()
    {
        QaCycle(2);
        popup.StopBodyTransition();
        popup.Width = 360;
        popup.Height = 280;
        popup.UpdateLayout();
        var viewport = (ScrollViewer)popupPages[active!.Id + ":" + page].Content;
        if (viewport.ScrollableHeight <= 0 || viewport.ScrollableWidth <= 0)
            throw new InvalidOperationException("小工作区没有提供可访问的滚动内容。");
        visibleEntries.Last().BringIntoView();
        popup.UpdateLayout();
        if (viewport.VerticalOffset <= 0)
            throw new InvalidOperationException("小工作区的最后一行入口不可访问。");
    }
    internal double QaCycle(int index)
    {
        var cats = ctx.Db.Current.Categories;
        var at = index % cats.Count;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (gridButtons.Length == 0)
            ShowGrid();
        var tile = gridButtons.FirstOrDefault(b => b.DataContext is Category c && c.Id == cats[at].Id) ?? gridAnchor;
        ShowCategory(cats[at], tile!);
        popup.UpdateLayout();
        return watch.Elapsed.TotalMilliseconds;
    }
    internal void QaHide() => HidePopup();
}
