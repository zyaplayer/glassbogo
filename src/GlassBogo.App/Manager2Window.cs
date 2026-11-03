using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace GlassBogo.App;

internal sealed class Manager2Window : LiquidWindow
{
    private readonly V2Context ctx;
    private readonly ContentControl pageHost = new();
    private readonly TextBox query = new();
    internal readonly ListBox Entries;
    private readonly ListBox categories = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private string categoryId = "$all";
    private readonly TextBlock entryHeading = V2Ui.Label("全部入口", 22);
    private readonly TextBlock status = V2Ui.Label("", 12, "Muted");
    private readonly WrapPanel bulkActions = new() { Visibility = Visibility.Collapsed, Margin = new(0, 8, 0, 4) };
    private readonly TextBlock selectionLabel = V2Ui.Label("", 12, "Muted");
    private DockPanel? entryPage;
    private Grid? managementGrid;
    private string page = "entries";
    private bool filtering, refreshingEntries;
    private readonly CancellationTokenSource refreshCancellation = new();
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private TextBlock? visualStatus;
    private CheckBox? desktopToggle;
    internal Manager2Window(V2Context ctx) : base(ctx.Graphics)
    {
        this.ctx = ctx; Title = "GlassBogo · 管理";
        var work = Display.PrimaryScreen;
        Width = Math.Min(1000, work.WorkingArea.Width / work.Scale - 24);
        // Leave the bottom dock visible for management and screenshot review.
        var dockTop = ctx.Dock is { IsLoaded: true } dock && double.IsFinite(dock.Top) ? dock.Top : work.WorkingArea.Bottom / work.Scale - ctx.IconSize - 74;
        var top = work.WorkingArea.Top / work.Scale + 12;
        var available = Math.Max(80, dockTop - 12 - top);
        Height = Math.Min(700, available);
        Left = work.WorkingArea.Left / work.Scale + (work.WorkingArea.Width / work.Scale - Width) / 2;
        Top = top + Math.Max(0, (available - Height) / 2); WindowStartupLocation = WindowStartupLocation.Manual;
        Entries = V2Ui.EntryList(ctx, item => item is V2EntryItem row ? EntryActions(row.Entry) : new Border());
        System.Windows.Automation.AutomationProperties.SetName(Entries, "入口列表");
        System.Windows.Automation.AutomationProperties.SetName(categories, "分类列表");
        System.Windows.Automation.AutomationProperties.SetName(bulkActions, "批量入口操作");
        selectionLabel.Margin = new(0, 0, 12, 0);
        var root = new DockPanel(); var header = new DockPanel { Margin = new(0, 0, 0, 12) };
        var close = V2Ui.Button("关闭", Close); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var title = V2Ui.Label("GlassBogo", 23); title.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }; header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 0, 0, 10) };
        foreach (var (key, label) in new[] { ("entries", "入口与分类"), ("settings", "设置") }) navigation.Children.Add(V2Ui.Button(label, () => ShowPage(key)));
        DockPanel.SetDock(navigation, Dock.Top); root.Children.Add(navigation); root.Children.Add(pageHost); Body.Content = root;
        query.TextChanged += (_, _) => RefreshEntries();
        categories.SelectionChanged += (_, _) => { if (!filtering && categories.SelectedItem is Category category) { categoryId = category.Id; entryHeading.Text = category.Name; RefreshEntries(); } };
        Entries.MouseDoubleClick += async (_, e) => { if (!FromAction(e.OriginalSource as DependencyObject) && Entries.SelectedItem is V2EntryItem row) { var result = await ctx.Launch(row.Entry); status.Text = result.Success ? "已打开" : "无法打开：" + result.Error; } };
        Entries.KeyDown += async (_, e) => { if (e.Key == Key.Enter && !FromAction(e.OriginalSource as DependencyObject) && Entries.SelectedItem is V2EntryItem row) { e.Handled = true; var result = await ctx.Launch(row.Entry); status.Text = result.Success ? "已打开" : "无法打开：" + result.Error; } };
        Entries.SelectionChanged += (_, _) => { if (!refreshingEntries) UpdateSelection(); };
        Entries.PreviewMouseRightButtonDown += (_, e) =>
        {
            Entries.ContextMenu = null;
            if (ItemsControl.ContainerFromElement(Entries, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: V2EntryItem row } item)
            { item.IsSelected = true; Entries.ContextMenu = V2Ui.EntryMenu(this, ctx, row.Entry, categoryId == "$pinned"); }
        };
        categories.PreviewMouseRightButtonDown += (_, e) =>
        {
            categories.ContextMenu = null;
            if (ItemsControl.ContainerFromElement(categories, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: Category category } item && Managed(category))
            { item.IsSelected = true; categories.ContextMenu = CategoryMenu(category); }
        };
        ctx.Changed += Changed;
        statusTimer.Tick += (_, _) => { if (visualStatus is not null) visualStatus.Text = "当前效果：" + ctx.Graphics.Status; };
        Loaded += (_, _) => { if (IsVisible && !ctx.Closing) ctx.Graphics.SetManagementOpen(true); };
        Closed += (_, _) => { refreshCancellation.Cancel(); refreshCancellation.Dispose(); ctx.Changed -= Changed; statusTimer.Stop(); ctx.Graphics.SetManagementOpen(false); };
        SizeChanged += (_, _) => { if (managementGrid is not null) managementGrid.ColumnDefinitions[0].Width = new(Math.Clamp(ActualWidth * .24, 160, 240)); };
        ShowPage("entries");
    }
    internal void ShowPage(string next)
    {
        var target = next == "settings" ? "settings" : "entries";
        if (pageHost.Content is not null && page == target) return;
        page = target; statusTimer.Stop();
        if (page == "entries") { BuildEntries(); RefreshCategories(); RefreshEntries(); }
        else { BuildSettings(); statusTimer.Start(); }
        Design.Reveal(pageHost);
    }
    private void Changed(object? sender, EventArgs e)
    {
        if (page == "entries") { RefreshCategories(); RefreshEntries(); }
        else if (desktopToggle is { IsEnabled: true }) desktopToggle.IsChecked = ctx.Preferences.HideDesktopIcons;
    }
    private string[] SelectedIds() => Entries.SelectedItems.Cast<V2EntryItem>().Select(r => r.Entry.Id).ToArray();
    private static bool FromAction(DependencyObject? source)
    {
        for (var current = source; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase) return true;
        return false;
    }
    private static Button Compact(Button button, string tooltip, bool symbol = false)
    {
        button.MinWidth = symbol ? 32 : 76; button.MinHeight = 30; button.Padding = new(8, 4, 8, 4); button.Margin = new(3, 0, 0, 0);
        button.ToolTip = tooltip; System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }
    private UIElement EntryActions(Entry entry)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Compact(V2Ui.Async(entry.Pinned ? "取消固定" : "固定", () => ctx.Write(() => ctx.Catalog.SetPinned([entry.Id], !entry.Pinned))), (entry.Pinned ? "取消固定“" : "固定“") + entry.Name + "”"));
        var more = Compact(V2Ui.Button("•••", () => { }), "管理“" + entry.Name + "”", true);
        more.Click += (_, _) => { more.ContextMenu = V2Ui.EntryMenu(this, ctx, entry, categoryId == "$pinned"); V2Ui.OpenMenu(more, more.ContextMenu); };
        actions.Children.Add(more); return actions;
    }
    private void UpdateSelection()
    {
        var count = Entries.SelectedItems.Count;
        bulkActions.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        selectionLabel.Text = $"已选 {count} 项";
        status.Text = $"{Entries.Items.Count} 个入口 · 行右侧管理单项 · Ctrl / Shift 多选";
    }
    private void BuildEntries()
    {
        if (entryPage is not null) { pageHost.Content = managementGrid; return; }
        entryPage = new DockPanel();
        var header = new StackPanel();
        var add = V2Ui.Button("添加入口", () => { });
        add.Click += (_, _) =>
        {
            var category = categoryId;
            if (category is not "$all") V2Ui.ChooseEntries(this, ctx, category == "$pinned" ? null : category, category == "$pinned" ? true : null);
            else V2Ui.OpenMenu(add, V2Ui.AddMenu(this, ctx));
        };
        var heading = new DockPanel(); DockPanel.SetDock(add, Dock.Right); heading.Children.Add(add); heading.Children.Add(entryHeading); header.Children.Add(heading);
        var refresh = V2Ui.Async("刷新", async () =>
        {
            status.Text = "正在刷新桌面第一层内容，核对已有入口…";
            try { var result = await ctx.RefreshDesktop(refreshCancellation.Token); if (IsVisible) status.Text = result.Summary + "；新增内容可在“添加入口”中选择"; }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsVisible) status.Text = "刷新失败：" + ex.Message; }
        });
        refresh.ToolTip = "只重扫桌面第一层内容并清理失效引用；不扫描全盘，不删除实际文件";
        DockPanel.SetDock(refresh, Dock.Right); heading.Children.Insert(1, refresh);
        query.Margin = new(0, 8, 0, 12); query.ToolTip = "按名称或路径筛选"; header.Children.Add(query);
        DockPanel.SetDock(header, Dock.Top); entryPage.Children.Add(header);
        var actions = new StackPanel { Margin = new(0, 10, 0, 0) };
        bulkActions.Children.Add(selectionLabel);
        bulkActions.Children.Add(V2Ui.Async("固定", async () => { var ids = SelectedIds(); if (ids.Length > 0) await ctx.Write(() => ctx.Catalog.SetPinned(ids, true)); }));
        bulkActions.Children.Add(V2Ui.Async("取消固定", async () => { var ids = SelectedIds(); if (ids.Length > 0) await ctx.Write(() => ctx.Catalog.SetPinned(ids, false)); }));
        bulkActions.Children.Add(V2Ui.Button("移动到分类", () => V2Ui.ChooseCategory(this, ctx, SelectedIds())));
        var more = V2Ui.Button("更多", () => { });
        more.Click += (_, _) =>
        {
            var ids = SelectedIds(); var menu = V2Ui.Menu(ctx);
            V2Ui.Command(menu, "移出分类", () => ctx.Write(() => ctx.Catalog.Move(ids, "")));
            menu.Items.Add(new Separator());
            V2Ui.Command(menu, "移除选中入口…", () => V2Ui.Remove(this, ctx, ids)); more.ContextMenu = menu; V2Ui.OpenMenu(more, menu);
        };
        bulkActions.Children.Add(more); actions.Children.Add(bulkActions);
        actions.Children.Add(status); DockPanel.SetDock(actions, Dock.Bottom); entryPage.Children.Add(actions); entryPage.Children.Add(Entries);
        var sidebar = new DockPanel(); var categoryHeader = new DockPanel { Margin = new(0, 0, 0, 10) };
        var newCategory = Compact(V2Ui.Button("＋", () => V2Ui.NewCategory(this, ctx)), "新建分类", true); DockPanel.SetDock(newCategory, Dock.Right); categoryHeader.Children.Add(newCategory); categoryHeader.Children.Add(V2Ui.Label("分类", 18));
        DockPanel.SetDock(categoryHeader, Dock.Top); sidebar.Children.Add(categoryHeader); sidebar.Children.Add(categories); ConfigureCategories();
        managementGrid = new Grid(); managementGrid.ColumnDefinitions.Add(new() { Width = new(Math.Clamp(Width * .24, 160, 240)) }); managementGrid.ColumnDefinitions.Add(new() { Width = new(16) }); managementGrid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        managementGrid.Children.Add(sidebar); Grid.SetColumn(entryPage, 2); managementGrid.Children.Add(entryPage); pageHost.Content = managementGrid;
    }
    private void RefreshCategories()
    {
        filtering = true;
        try
        {
            var rows = new[] { new Category("$all", "全部入口"), new Category("$pinned", "固定入口"), new Category("", "未分类") }.Concat(ctx.Db.Current.Categories).ToArray();
            if (!rows.Any(c => c.Id == categoryId)) categoryId = "$all";
            categories.ItemsSource = rows; categories.SelectedItem = rows.Single(c => c.Id == categoryId); entryHeading.Text = ((Category)categories.SelectedItem).Name;
        }
        finally { filtering = false; }
    }
    internal void RefreshEntries()
    {
        if (page != "entries") return;
        var selected = SelectedIds().ToHashSet(); var value = categoryId;
        var state = ctx.Db.Current;
        IEnumerable<Entry> found = value == "$pinned" ? ctx.Catalog.Pinned() : value == "$all" ? state.Entries.OrderBy(e => state.Categories.FindIndex(c => c.Id == e.CategoryId)).ThenBy(e => e.CategoryOrder) : ctx.Catalog.InCategory(value);
        var text = query.Text.Trim();
        var rows = found.Where(e => e.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || e.Target.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Select(e => new V2EntryItem(e, (state.Categories.FirstOrDefault(c => c.Id == e.CategoryId)?.Name ?? "未分类") + " · " + e.Target)).ToArray();
        refreshingEntries = true;
        try { Entries.ItemsSource = rows; foreach (var item in rows.Where(r => selected.Contains(r.Entry.Id))) Entries.SelectedItems.Add(item); }
        finally { refreshingEntries = false; }
        UpdateSelection();
    }
    private void ConfigureCategories()
    {
        var template = new DataTemplate(typeof(Category)); var row = new FrameworkElementFactory(typeof(DockPanel));
        var commands = new FrameworkElementFactory(typeof(V2RowActions)); commands.SetValue(DockPanel.DockProperty, Dock.Right); commands.SetValue(V2RowActions.BuilderProperty, (Func<object, UIElement>)(item => item is Category category ? CategoryActions(category) : new Border())); row.AppendChild(commands);
        var name = new FrameworkElementFactory(typeof(TextBlock)); name.SetBinding(TextBlock.TextProperty, new Binding(nameof(Category.Name))); name.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(Category.Name))); name.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center); name.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); row.AppendChild(name); template.VisualTree = row; categories.ItemTemplate = template;
        VirtualizingPanel.SetIsVirtualizing(categories, true); VirtualizingPanel.SetVirtualizationMode(categories, VirtualizationMode.Recycling);
    }
    private bool Managed(Category category) => ctx.Db.Current.Categories.Any(c => c.Id == category.Id);
    private UIElement CategoryActions(Category category)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        if (!Managed(category)) return actions;
        var more = Compact(V2Ui.Button("•••", () => { }), "管理分类“" + category.Name + "”", true); more.Click += (_, _) => { more.ContextMenu = CategoryMenu(category); V2Ui.OpenMenu(more, more.ContextMenu); };
        actions.Children.Add(more); return actions;
    }
    private ContextMenu CategoryMenu(Category category)
    {
        var menu = V2Ui.Menu(ctx);
        var at = ctx.Db.Current.Categories.FindIndex(c => c.Id == category.Id);
        if (at < 0) return menu;
        V2Ui.Command(menu, "改名", () => { V2Ui.Name(this, ctx, "分类名称", category.Name, name => ctx.Write(() => ctx.Catalog.SaveCategory(category.Id, name)), 40); return Task.CompletedTask; });
        V2Ui.Command(menu, "上移", () => ctx.Write(() => ctx.Catalog.StepCategory(category.Id, -1))); ((MenuItem)menu.Items[^1]).IsEnabled = at > 0;
        V2Ui.Command(menu, "下移", () => ctx.Write(() => ctx.Catalog.StepCategory(category.Id, 1))); ((MenuItem)menu.Items[^1]).IsEnabled = at < ctx.Db.Current.Categories.Count - 1;
        menu.Items.Add(new Separator());
        V2Ui.Command(menu, "删除分类…", () => V2Ui.Modal(ctx, () => MessageBox.Show(this, $"删除分类“{category.Name}”？\n入口仍会保留，固定状态不变。", "删除分类", MessageBoxButton.YesNo)) == MessageBoxResult.Yes ? ctx.Write(() => ctx.Catalog.RemoveCategory(category.Id)) : Task.CompletedTask);
        return menu;
    }
    private void BuildSettings()
    {
        var panel = new StackPanel(); panel.Children.Add(V2Ui.Label("基础设置", 22));
        void Choice(string title, Choice[] values, string selected, Func<string, Task> save)
        {
            var savedValue = selected;
            var combo = new ComboBox { ItemsSource = values, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = selected, Width = 190, Margin = new(12, 6, 0, 6) };
            combo.SelectionChanged += async (_, _) => { if (!combo.IsEnabled) return; combo.IsEnabled = false; try { var value = (string)combo.SelectedValue; await save(value); savedValue = value; } catch (Exception ex) { combo.SelectedValue = savedValue; Design.Error(ex); } finally { combo.IsEnabled = true; } };
            panel.Children.Add(Design.Row(V2Ui.Label(title), combo));
        }
        Choice("外观", [new("system", "跟随系统"), new("light", "浅色"), new("dark", "深色")], ctx.Preferences.Theme, value => ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { Theme = value })));
        Choice("图标大小", [new("small", "小 · 32"), new("medium", "中 · 40"), new("large", "大 · 48")], ctx.Preferences.IconSize, value => ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { IconSize = value })));
        Choice("视觉效果", [new("strong", "强视觉"), new("reduced", "降低效果"), new("off", "关闭效果")], ctx.Preferences.VisualQuality, value => ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { VisualQuality = value })));
        visualStatus = V2Ui.Label("当前效果：" + ctx.Graphics.Status, 12, "Muted"); panel.Children.Add(visualStatus);
        panel.Children.Add(V2Ui.Label("管理窗口打开期间，底栏暂停实时更新以便截图；关闭管理后恢复。", 12, "Muted"));
        panel.Children.Add(V2Ui.Button("重试视觉效果", ctx.Graphics.Refresh));
        var desktopIcons = new CheckBox { Content = "隐藏全部桌面图标", IsChecked = ctx.Preferences.HideDesktopIcons, Margin = new(0, 12, 0, 4) };
        desktopToggle = desktopIcons;
        desktopIcons.Click += async (_, _) =>
        {
            var previous = ctx.Preferences.HideDesktopIcons; desktopIcons.IsEnabled = false;
            try { await ctx.SetDesktopIconsHidden(desktopIcons.IsChecked == true); }
            catch (Exception ex) { desktopIcons.IsChecked = previous; Design.Error(ex); }
            finally { desktopIcons.IsEnabled = true; }
        };
        panel.Children.Add(desktopIcons);
        panel.Children.Add(V2Ui.Label("隐藏应用、文件和文件夹图标；关闭开关或退出程序时恢复原显示状态，文件保留原位。", 12, "Muted"));
        var startup = new CheckBox { Content = "登录 Windows 时启动", IsChecked = ctx.Preferences.AutoStart, Margin = new(0, 15, 0, 15) };
        startup.Click += async (_, _) =>
        {
            var previous = ctx.Preferences.AutoStart; var enabled = startup.IsChecked == true; startup.IsEnabled = false;
            try
            {
                await ctx.Write(() =>
                {
                    if (!ctx.Qa) Platform.AutoStart(enabled);
                    try { ctx.Db.Update(s => s.Preferences = s.Preferences with { AutoStart = enabled }); }
                    catch { if (!ctx.Qa) Platform.AutoStart(previous); throw; }
                });
            }
            catch (Exception ex) { startup.IsChecked = previous; Design.Error(ex); }
            finally { startup.IsEnabled = true; }
        }; panel.Children.Add(startup);
        panel.Children.Add(V2Ui.Label("全局搜索快捷键", 16));
        var draft = ctx.Preferences.SearchHotkey ?? new SearchShortcut();
        var input = new TextBox { IsReadOnly = true, Text = FormatHotkey(draft), ToolTip = "点击后按下新的组合键", Margin = new(0, 8, 0, 8) };
        var feedback = V2Ui.Label(ctx.HotkeyStatus, 12, "Muted");
        input.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true; var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftAlt or Key.RightAlt or Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var m = Keyboard.Modifiers; uint mods = ((m & ModifierKeys.Alt) != 0 ? 1u : 0) | ((m & ModifierKeys.Control) != 0 ? 2u : 0) | ((m & ModifierKeys.Shift) != 0 ? 4u : 0) | ((m & ModifierKeys.Windows) != 0 ? 8u : 0);
            if (mods == 0) { feedback.Text = "请使用包含 Alt、Ctrl、Shift 或 Win 的组合键。"; return; }
            draft = new(mods, (uint)KeyInterop.VirtualKeyFromKey(key)); input.Text = FormatHotkey(draft); feedback.Text = "点击保存后生效";
        };
        panel.Children.Add(input); panel.Children.Add(V2Ui.Async("保存快捷键", async () => { var result = await ctx.ChangeHotkey(draft); feedback.Text = result.Success ? "快捷键已保存" : result.Error; })); panel.Children.Add(feedback);
        pageHost.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static string FormatHotkey(SearchShortcut shortcut) => string.Join("+", new[] { (shortcut.Modifiers & 2) != 0 ? "Ctrl" : null, (shortcut.Modifiers & 1) != 0 ? "Alt" : null, (shortcut.Modifiers & 4) != 0 ? "Shift" : null, (shortcut.Modifiers & 8) != 0 ? "Win" : null,
        shortcut.VirtualKey == 0xC0 ? "`" : KeyInterop.KeyFromVirtualKey((int)shortcut.VirtualKey).ToString() }.Where(v => v is not null));
}
