using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;

namespace GlassBogo.App;

internal sealed class Dock2Window : LiquidWindow
{
    private readonly V2Context ctx;
    private readonly Grid strip = new();
    internal readonly LiquidWindow Panel;
    internal readonly CategoryInteraction Interaction = new();
    private readonly DispatcherTimer openTimer = new(), closeTimer = new() { Interval = TimeSpan.FromMilliseconds(CategoryInteraction.CloseDelay) };
    private readonly Dictionary<string, Button> anchors = [];
    private DesktopLayer? layer, panelLayer;
    private HwndSource? source;
    private OutsideClick? outside;
    private int openToken, closeToken;
    private string? pendingCategory, overflow;
    private Button? anchor;
    private ListBox? gridList;
    private Entry[] panelEntries = [];
    private int focusIndex;
    private string? displayedCategory;
    private sealed record Page(UIElement Body, ListBox Grid, Entry[] Entries, TextBlock Status);
    private readonly Dictionary<string, Page> pages = [];
    private Entry[] renderedEntries = [];
    private Category[] renderedCategories = [];
    private double renderedIconSize;
    private int renderedIconRevision = -1;
    private TextBlock panelStatus = V2Ui.Label("", 12, "Warning");
    internal DockSections? Layout { get; private set; }
    internal Dock2Window(V2Context ctx) : base(ctx.Graphics, glass: true)
    {
        this.ctx = ctx; ctx.Dock = this;
        Title = "GlassBogo Dock"; ShowInTaskbar = false; ShowActivated = false; Topmost = false;
        Body.Margin = new(-4); Body.Content = strip;
        Panel = new(ctx.Graphics) { Title = "GlassBogo 分类", ShowInTaskbar = false, ShowActivated = false, Topmost = false };
        Panel.MouseEnter += (_, _) => { Interaction.Stay(); closeTimer.Stop(); };
        Panel.MouseLeave += (_, _) => ScheduleClose();
        MouseEnter += (_, _) => closeTimer.Stop(); MouseLeave += (_, _) => { openTimer.Stop(); ScheduleClose(); };
        Panel.PreviewKeyDown += PanelKey;
        Closing += (_, e) => { if (!ctx.Closing) e.Cancel = true; };
        Panel.Closing += (_, e) => { if (!ctx.Closing) { e.Cancel = true; DismissPanel(); } };
        openTimer.Tick += (_, _) =>
        {
            openTimer.Stop();
            if (pendingCategory is { } id && Interaction.CommitHover(id, openToken)) ShowCategory(id, false);
        };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (ctx.Menus > 0 || ctx.Modals > 0 || IsMouseOver || Panel.IsMouseOver) return;
            if (Corridor()) { closeTimer.Start(); return; }
            if (Interaction.CommitClose(closeToken, false, false)) HidePanel();
            else if (overflow is not null) DismissPanel();
        };
        var menu = V2Ui.Menu(ctx);
        V2Ui.Command(menu, "添加固定入口…", () => { V2Ui.ChooseEntries(this, ctx, pin: true); return Task.CompletedTask; });
        V2Ui.Command(menu, "新建分类…", () => { V2Ui.NewCategory(this, ctx); return Task.CompletedTask; });
        V2Ui.Command(menu, "管理入口、分类和设置", () => { ctx.OpenManager(); return Task.CompletedTask; }); ContextMenu = menu;
        V2Ui.Command(menu, "隐藏全部桌面图标", () => ctx.SetDesktopIconsHidden(!ctx.Preferences.HideDesktopIcons));
        var desktopIcons = (MenuItem)menu.Items[menu.Items.Count - 1];
        menu.Opened += (_, _) => desktopIcons.Header = ctx.Preferences.HideDesktopIcons ? "恢复桌面图标" : "隐藏全部桌面图标";
        ctx.Changed += Changed; SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SourceInitialized += (_, _) => { source = HwndSource.FromHwnd(Handle); source?.AddHook(WindowMessage); };
        Loaded += (_, _) => { Panel.Owner = this; Render(); layer = new(this); ctx.SyncCalendar(); };
        Closed += (_, _) => { openTimer.Stop(); closeTimer.Stop(); source?.RemoveHook(WindowMessage); outside?.Dispose(); layer?.Dispose(); panelLayer?.Dispose(); Panel.Close(); ctx.Changed -= Changed; SystemEvents.DisplaySettingsChanged -= DisplayChanged; };
    }
    private nint WindowMessage(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message is 0x2E0 or 0x1A) _ = Dispatcher.BeginInvoke(() => { Render(); ctx.Graphics.Refresh(); });
        return 0;
    }
    private void Changed(object? sender, EventArgs e)
    {
        if (renderedIconSize == ctx.IconSize && renderedIconRevision == ctx.IconRevision && renderedCategories.SequenceEqual(ctx.Db.Current.Categories) && renderedEntries.SequenceEqual(ctx.Db.Current.Entries.Select(entry => entry with { LastUsed = null }))) return;
        pages.Clear(); Render();
    }
    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Render);
    internal void Render()
    {
        if (ctx.Closing) return;
        renderedEntries = ctx.Db.Current.Entries.Select(entry => entry with { LastUsed = null }).ToArray(); renderedCategories = ctx.Db.Current.Categories.ToArray(); renderedIconSize = ctx.IconSize; renderedIconRevision = ctx.IconRevision;
        var screen = Display.PrimaryScreen; var work = screen.WorkingArea; var scale = screen.Scale; var size = ctx.IconSize;
        var pinned = ctx.Catalog.Pinned().ToArray(); var categories = ctx.Db.Current.Categories.ToArray();
        Layout = DockSections.Measure(work.Width / scale, size, pinned.Length, categories.Length);
        Width = Layout.Width; Height = Math.Min(size + 50, work.Height / scale - 12);
        Left = (work.Left + (work.Width - Width * scale) / 2) / scale; Top = Math.Max(work.Top / scale, work.Bottom / scale - Height - 12);
        strip.Children.Clear(); strip.ColumnDefinitions.Clear(); anchors.Clear();
        strip.ColumnDefinitions.Add(new() { Width = new(Layout.PinnedWidth) }); strip.ColumnDefinitions.Add(new() { Width = new(Layout.CategoryWidth) }); strip.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        var pins = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true };
        foreach (var entry in pinned.Take(Layout.PinnedVisible)) pins.Children.Add(EntryTile(entry, true, size + 20));
        if (Layout.PinnedVisible < pinned.Length) pins.Children.Add(More("固定入口", Math.Min(size + 20, Layout.PinnedWidth), button => ShowOverflow("pinned", button)));
        strip.Children.Add(pins);
        var cats = new StackPanel { Orientation = Orientation.Horizontal, ClipToBounds = true };
        foreach (var category in categories.Take(Layout.CategoriesVisible)) cats.Children.Add(CategoryTile(category, Math.Max(64, size + 32)));
        if (Layout.CategoriesVisible < categories.Length) cats.Children.Add(More("分类", Math.Min(Math.Max(64, size + 32), Layout.CategoryWidth), button => ShowOverflow("categories", button)));
        Grid.SetColumn(cats, 1); strip.Children.Add(cats);
        var actions = new UniformGrid { Columns = 3 };
        var search = Design.IconButton("search", "搜索", () => ctx.ToggleSearch()); search.Width = double.NaN; search.MinWidth = 0; search.Margin = new(0); search.Padding = new(4);
        var settings = Design.IconButton("settings", "管理入口、分类与设置", ctx.OpenManager); settings.Width = double.NaN; settings.MinWidth = 0; settings.Margin = new(0); settings.Padding = new(4);
        var calendar = Design.IconButton("calendar", "桌面日历与日程", async () => { try { await ctx.SetCalendarVisible(!ctx.Preferences.DesktopCalendarVisible); } catch (Exception ex) { Design.Error(ex); } }); calendar.Width = double.NaN; calendar.MinWidth = 0; calendar.Margin = new(0); calendar.Padding = new(4);
        actions.Children.Add(search); actions.Children.Add(calendar); actions.Children.Add(settings); Grid.SetColumn(actions, 2); strip.Children.Add(actions);
        ctx.CalendarWindow?.Position();
        if (Panel.IsVisible)
        {
            if (Interaction.Active is { } active && ctx.Db.Current.Categories.Any(c => c.Id == active)) ShowCategory(active, false);
            else if (overflow is { } type && anchor is not null) ShowOverflow(type, anchor);
            else DismissPanel();
        }
    }
    private Button Tile(UIElement content, double width)
    {
        var tile = new Button { Content = content, Width = Math.Max(1, width), Height = ctx.IconSize + 24, Margin = new(0), Padding = new(4), MinWidth = 0, MinHeight = 0 };
        tile.SetResourceReference(StyleProperty, "CategoryButton"); return tile;
    }
    private Button More(string label, double width, Action<Button> show)
    {
        var tile = Tile(V2Ui.Label("•••", 18), width); tile.ToolTip = "更多" + label; tile.Click += (_, _) => show(tile); return tile;
    }
    internal Button EntryTile(Entry entry, bool fixedArea, double width, bool? pinnedContext = null)
    {
        var image = new Image { Width = ctx.IconSize, Height = ctx.IconSize }; RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        UIElement content = image;
        if (!fixedArea)
        {
            var stack = new StackPanel(); stack.Children.Add(image);
            var label = V2Ui.Label(entry.Name, 12); label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextAlignment = TextAlignment.Center;
            stack.Children.Add(label); content = stack;
        }
        var tile = Tile(content, width); tile.Height = ctx.IconSize + (fixedArea ? 24 : 42); tile.Tag = entry; tile.ToolTip = entry.Name;
        tile.Click += async (_, _) => await Launch(entry);
        tile.PreviewMouseRightButtonDown += (_, _) => tile.ContextMenu = V2Ui.EntryMenu(Panel.IsVisible ? Panel : this, ctx, entry, pinnedContext ?? fixedArea);
        tile.Loaded += async (_, _) => { try { image.Source = await ctx.Icons.Get(entry); } catch { } };
        return tile;
    }
    private Button CategoryTile(Category category, double width)
    {
        var stack = new StackPanel(); var preview = new UniformGrid { Columns = 2, Rows = 2, Width = ctx.IconSize, Height = ctx.IconSize };
        var entries = ctx.Catalog.InCategory(category.Id).Take(4).ToArray();
        for (var i = 0; i < 4; i++)
        {
            var image = new Image { Margin = new(1) }; preview.Children.Add(image);
            if (i < entries.Length) { var entry = entries[i]; image.Loaded += async (_, _) => { try { image.Source = await ctx.Icons.Get(entry); } catch { } }; }
        }
        if (entries.Length == 0) { preview.Children.Clear(); preview.Columns = 1; preview.Rows = 1; preview.Children.Add(Design.Icon("folder", ctx.IconSize * .7)); }
        stack.Children.Add(preview); var label = V2Ui.Label(category.Name, 12); label.TextTrimming = TextTrimming.CharacterEllipsis; label.TextAlignment = TextAlignment.Center; stack.Children.Add(label);
        var tile = Tile(stack, width); tile.ToolTip = category.Name; anchors[category.Id] = tile;
        tile.MouseEnter += (_, _) =>
        {
            if (Interaction.Hover(category.Id) is not { } token) return;
            pendingCategory = category.Id; openToken = token; openTimer.Stop(); openTimer.Interval = TimeSpan.FromMilliseconds(CategoryInteraction.HoverDelay); openTimer.Start();
        };
        tile.MouseLeave += (_, _) => { openTimer.Stop(); ScheduleClose(); };
        tile.Click += (_, _) => { openTimer.Stop(); Interaction.Click(category.Id); if (Interaction.Active is null) HidePanel(); else { ShowCategory(category.Id, true); } };
        var menu = V2Ui.Menu(ctx);
        V2Ui.Command(menu, "添加入口…", () => { V2Ui.ChooseEntries(this, ctx, category.Id); return Task.CompletedTask; });
        V2Ui.Command(menu, "重命名", () => { V2Ui.Name(this, ctx, "分类名称", category.Name, name => ctx.Write(() => ctx.Catalog.SaveCategory(category.Id, name)), 40); return Task.CompletedTask; });
        V2Ui.Command(menu, "上移", () => ctx.Write(() => ctx.Catalog.StepCategory(category.Id, -1))); V2Ui.Command(menu, "下移", () => ctx.Write(() => ctx.Catalog.StepCategory(category.Id, 1)));
        V2Ui.Command(menu, "删除分类", async () => { if (V2Ui.Modal(ctx, () => MessageBox.Show(this, $"删除“{category.Name}”？入口和固定状态会保留。", "删除分类", MessageBoxButton.YesNo)) == MessageBoxResult.Yes) await ctx.Write(() => ctx.Catalog.RemoveCategory(category.Id)); });
        tile.ContextMenu = menu; return tile;
    }
    internal void ShowCategory(string id, bool activate)
    {
        var category = ctx.Db.Current.Categories.FirstOrDefault(c => c.Id == id); if (category is null) return;
        var reveal = !Panel.IsVisible || displayedCategory != id; displayedCategory = id;
        panelStatus = V2Ui.Label("", 12, "Warning");
        closeTimer.Stop(); overflow = null; if (anchors.TryGetValue(id, out var button)) anchor = button;
        var cacheKey = id + ":" + ctx.IconSize;
        if (pages.TryGetValue(cacheKey, out var cached))
        {
            panelEntries = cached.Entries; gridList = cached.Grid; panelStatus = cached.Status; Panel.Body.Content = cached.Body; PlacePanel();
            if (reveal) Design.Reveal(Panel.Body);
            if (activate) { Panel.Activate(); gridList.Focus(); }
            return;
        }
        panelEntries = ctx.Catalog.InCategory(id).ToArray();
        var body = new DockPanel(); var header = new DockPanel();
        var close = V2Ui.Button("×", DismissPanel); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(V2Ui.Label(category.Name, 20));
        DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        var footer = new StackPanel(); var add = V2Ui.Button("添加入口", () => V2Ui.ChooseEntries(Panel, ctx, id)); footer.Children.Add(add); footer.Children.Add(panelStatus);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        gridList = BuildGrid(panelEntries); body.Children.Add(panelEntries.Length == 0 ? V2Ui.Label("分类还是空的，点击下方添加入口。", 14, "Muted") : gridList);
        Panel.Body.Content = body; panelStatus.Text = ""; PlacePanel();
        if (reveal) Design.Reveal(Panel.Body);
        if (pages.Count >= 24) pages.Remove(pages.Keys.First()); pages[cacheKey] = new(body, gridList, panelEntries, panelStatus);
        if (activate) { Panel.Activate(); gridList.Focus(); }
    }
    private ListBox BuildGrid(Entry[] entries, bool pinned = false) => BuildTileGrid(entries.Select(entry => (Func<UIElement>)(() => EntryTile(entry, false, double.NaN, pinned))).ToArray());
    private ListBox BuildTileGrid(Func<UIElement>[] factories)
    {
        var list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch, Background = Brushes.Transparent, BorderThickness = new(0), Padding = new(0) };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var rows = factories.Chunk(4).Select(part => new TileQuad(part)).ToArray(); list.ItemsSource = rows;
        var template = new DataTemplate(typeof(TileQuad)); template.VisualTree = new FrameworkElementFactory(typeof(TileQuadView)); list.ItemTemplate = template;
        var style = new Style(typeof(ListBoxItem)); style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch)); list.ItemContainerStyle = style;
        return list;
    }
    private void ShowOverflow(string type, Button button)
    {
        openTimer.Stop(); Interaction.Close(); overflow = type; anchor = button;
        var body = new DockPanel(); var header = new DockPanel();
        var close = V2Ui.Button("×", DismissPanel); close.ToolTip = "关闭面板"; DockPanel.SetDock(close, Dock.Right); header.Children.Add(close); header.Children.Add(V2Ui.Label(type == "pinned" ? "更多固定入口" : "更多分类", 20)); DockPanel.SetDock(header, Dock.Top); body.Children.Add(header);
        if (type == "pinned") { panelEntries = ctx.Catalog.Pinned().ToArray(); gridList = BuildGrid(panelEntries, true); body.Children.Add(gridList); }
        else
        {
            panelEntries = []; gridList = BuildTileGrid(ctx.Db.Current.Categories.Select(category => (Func<UIElement>)(() => CategoryTile(category, 88))).ToArray()); body.Children.Add(gridList);
        }
        Panel.Body.Content = body; PlacePanel();
    }
    private void PlacePanel()
    {
        var screen = Display.PrimaryScreen; var work = screen.WorkingArea; var scale = screen.Scale;
        Panel.Width = Math.Min(472, work.Width / scale - 16); Panel.Height = Math.Min(396, work.Height / scale * .7);
        var pos = anchor is { IsLoaded: true } ? anchor.PointToScreen(new Point()) : PointToScreen(new Point());
        var center = pos.X + (anchor?.ActualWidth ?? ActualWidth) * scale / 2;
        Panel.Left = Math.Clamp(center - Panel.Width * scale / 2, work.Left + 4, Math.Max(work.Left + 4, work.Right - Panel.Width * scale - 4)) / scale;
        Panel.Top = Math.Max(work.Top / scale, Top - Panel.Height - 8);
        if (!Panel.IsVisible) Panel.Show();
        panelLayer ??= new(Panel);
        outside ??= new(() => ctx.Menus > 0 || ctx.Modals > 0, () => [Handle, Panel.Handle], DismissPanel);
        panelLayer.Assess();
    }
    internal async Task Launch(Entry entry)
    {
        var generation = Interaction.Generation;
        var status = panelStatus;
        var result = await ctx.Launch(entry);
        if (ctx.Closing || generation != Interaction.Generation) return;
        if (!result.Success) { status.Text = "无法打开：" + result.Error; return; }
        Interaction.Launched(); if (!Interaction.Pinned) HidePanel();
    }
    internal void ScheduleClose()
    {
        if (!Panel.IsVisible) return;
        if (Interaction.Leave() is not { } token && overflow is null) return;
        closeToken = Interaction.Generation; closeTimer.Stop(); closeTimer.Start();
    }
    internal void DismissPanel() { Interaction.Close(); overflow = null; HidePanel(); }
    private void HidePanel() { displayedCategory = null; openTimer.Stop(); closeTimer.Stop(); outside?.Dispose(); outside = null; panelLayer?.Dispose(); panelLayer = null; Panel.Hide(); }
    private bool Corridor()
    {
        if (!Panel.IsVisible || !GetCursorPos(out var p) || !Native.GetWindowRect(Handle, out var d) || !Native.GetWindowRect(Panel.Handle, out var panel)) return false;
        var a = anchor is { IsLoaded: true } ? anchor.PointToScreen(new Point()) : PointToScreen(new Point());
        return HoverCoordinator.InCorridor(p.X, p.Y, a.X, a.X + (anchor?.ActualWidth ?? ActualWidth) * Display.PrimaryScreen.Scale, d.Top, panel.Left, panel.Right, panel.Bottom, 12 * Display.PrimaryScreen.Scale);
    }
    private async void PanelKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; DismissPanel(); return; }
        if (e.OriginalSource is Button { Tag: not Entry }) return;
        if (panelEntries.Length == 0) return;
        if (Keyboard.FocusedElement is Button { Tag: Entry entry }) focusIndex = Math.Max(0, Array.FindIndex(panelEntries, x => x.Id == entry.Id));
        if (e.Key == Key.Enter) { e.Handled = true; await Launch(panelEntries[Math.Clamp(focusIndex, 0, panelEntries.Length - 1)]); return; }
        var step = e.Key switch { Key.Left => -1, Key.Right => 1, Key.Up => -4, Key.Down => 4, _ => 0 }; if (step == 0) return;
        e.Handled = true; focusIndex = Math.Clamp(focusIndex + step, 0, panelEntries.Length - 1);
        if (gridList is null) return;
        gridList.ScrollIntoView(gridList.Items[focusIndex / 4]); gridList.UpdateLayout();
        var target = panelEntries[focusIndex].Id;
        _ = Dispatcher.BeginInvoke(() => Descendants(Panel).OfType<Button>().FirstOrDefault(b => b.Tag is Entry v && v.Id == target)?.Focus(), DispatcherPriority.ContextIdle);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var next in Descendants(child)) yield return next; } }
    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint point);
}

internal sealed record TileQuad(Func<UIElement>[] Tiles);
internal sealed class TileQuadView : UniformGrid
{
    public TileQuadView()
    {
        Columns = 4;
        DataContextChanged += (_, _) =>
        {
            Children.Clear(); if (DataContext is not TileQuad row) return;
            foreach (var factory in row.Tiles) { var tile = factory(); if (tile is FrameworkElement element) { element.Width = double.NaN; element.HorizontalAlignment = HorizontalAlignment.Stretch; } Children.Add(tile); }
            for (var i = row.Tiles.Length; i < 4; i++) Children.Add(new Border());
        };
    }
}
