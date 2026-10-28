using System.ComponentModel;
using System.Windows.Data;
using Microsoft.Win32;
using GlassBogo.PluginSdk;

namespace GlassBogo.App;

internal sealed class EntryRow : INotifyPropertyChanged
{
    internal Entry Entry
    {
        get;
    }
    public string Name => Entry.Name;
    public string Hint
    {
        get; private set;
    }
    public ImageSource? Icon
    {
        get; private set;
    }
    private bool loaded;
    internal EntryRow(Entry entry, string category)
    {
        Entry = entry;
        Hint = category + "  ·  " + entry.Target;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    internal async Task Load(AppContext ctx)
    {
        if (loaded)
            return;
        loaded = true;
        try
        {
            Icon = await ctx.Icons.Get(Entry);
            if (Entry.Kind == "path" && !await Task.Run(() => File.Exists(Entry.Target) || Directory.Exists(Entry.Target)))
                Hint = "⚠ 目标失效 · " + Hint;
            PropertyChanged?.Invoke(this, new(null));
        }
        catch { Hint = "⚠ 暂无法读取 · " + Hint; PropertyChanged?.Invoke(this, new(null)); }
    }
}
internal sealed class WorkspaceWindow : GlassWindow
{
    private readonly AppContext ctx;
    private StackPanel? homeTasks;
    private DockPanel? entryPage; private readonly ContentControl content = new(); private string page = "home";
    private readonly TextBox search = new() { ToolTip = "搜索已收录的入口" }; private readonly ListBox entries = new() { SelectionMode = SelectionMode.Extended };
    private readonly ComboBox filter = new() { MinWidth = 130, Margin = new(5), DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    private readonly TextBlock status = Design.Text("", 12, "Muted");
    private readonly Dictionary<string, Button> navigation = new();
    internal WorkspaceWindow(AppContext context)
    {
        ctx = context;
        Title = "GlassBogo · 工作台";
        Width = 1000;
        Height = 700;
        MinWidth = 760;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        var nav = new StackPanel();
        foreach (var (key, label, iconName) in new[] { ("home", "工作台", "home"), ("entries", "全部入口", "folder"), ("calendar", "日历", "calendar"), ("plugins", "插件", "plugin"), ("settings", "设置", "settings") })
        {
            var button = Design.Button("", () => { if (key == "calendar") ctx.OpenCalendar(); else ShowPage(key); });
            var glyph = Design.Icon(iconName, 18);
            glyph.Margin = new(0, 0, 12, 0);
            button.Content = Design.Row(glyph, Design.Text(label, 14));
            button.SetResourceReference(FrameworkElement.StyleProperty, "NavButton");
            nav.Children.Add(button);
            navigation[key] = button;
        }
        var rail = new Border { Child = nav, Width = 162, CornerRadius = new(18), Padding = new(8), Margin = new(0, 0, 24, 0) };
        rail.SetResourceReference(Border.BackgroundProperty, "Inset");
        DockPanel.SetDock(rail, Dock.Left);
        root.Children.Add(rail);
        root.Children.Add(content);
        SetPage("GlassBogo", root);
        ctx.Db.Changed += OnData;
        Closed += (_, _) => ctx.Db.Changed -= OnData;
        search.TextChanged += (_, _) => RefreshEntries();
        filter.SelectionChanged += (_, _) => RefreshEntries();
        var template = new DataTemplate(typeof(EntryRow));
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(Image.WidthProperty, 28d);
        icon.SetValue(Image.HeightProperty, 28d);
        icon.SetValue(Image.MarginProperty, new Thickness(0, 0, 12, 0));
        icon.SetBinding(Image.SourceProperty, new Binding("Icon"));
        row.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        name.SetValue(TextBlock.FontSizeProperty, 14d);
        text.AppendChild(name);
        var hint = new FrameworkElementFactory(typeof(TextBlock));
        hint.SetBinding(TextBlock.TextProperty, new Binding("Hint"));
        hint.SetValue(TextBlock.FontSizeProperty, 11d);
        hint.SetValue(TextBlock.OpacityProperty, .65);
        hint.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        text.AppendChild(hint);
        row.AppendChild(text);
        template.VisualTree = row;
        entries.ItemTemplate = template;
        var style = new Style(typeof(ListBoxItem), (Style?)TryFindResource(typeof(ListBoxItem)));
        style.Setters.Add(new EventSetter(LoadedEvent, new RoutedEventHandler(async (s, _) =>
        {
            if (s is not ListBoxItem container)
                return;
            if (container.Tag is not true)
            {
                container.Tag = true;
                container.DataContextChanged += async (_, _) => { if (container.DataContext is EntryRow recycled) await recycled.Load(ctx); };
            }
            if (container.DataContext is EntryRow item)
                await item.Load(ctx);
        })));
        entries.ItemContainerStyle = style;
        entries.MouseDoubleClick += async (_, _) => { if (entries.SelectedItem is EntryRow e) await ctx.Open(e.Entry); };
        entries.KeyDown += async (_, e) => { if (e.Key == Key.Enter && entries.SelectedItem is EntryRow item) { await ctx.Open(item.Entry); e.Handled = true; } };
        entries.AllowDrop = true;
        entries.Drop += async (_, e) => { e.Handled = true; try { if (ctx.Dock is not null) await ctx.Dock.ImportDrop(e.Data, string.IsNullOrEmpty((string?)filter.SelectedValue) ? "inbox" : (string)filter.SelectedValue); } catch (Exception ex) { Design.Error(ex); } };
        Point start = default;
        entries.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(entries);
        entries.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(entries) - start).Length > 8 && entries.SelectedItem is EntryRow item) DragDrop.DoDragDrop(entries, new DataObject("GlassBogo.Entry", item.Entry.Id), DragDropEffects.Move); };
        entries.ContextMenu = new ContextMenu();
        entries.PreviewMouseRightButtonDown += (_, _) => EntryMenu();
        ShowPage("home");
    }
    private void OnData(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => { if (page == "entries") { RefreshCategoryFilter(); RefreshEntries(); } else if (page == "home") RefreshToday(); });
    private void ShowPage(string target)
    {
        page = target;
        foreach (var (key, button) in navigation) button.Tag = key == target ? "active" : null;
        switch (target)
        {
            case "entries":
                EntryPage();
                break;
            case "plugins":
                PluginPage();
                break;
            case "settings":
                SettingsPage();
                break;
            default:
                Home();
                break;
        }
        Design.Reveal(content);
    }
    private void Home()
    {
        var panel = new StackPanel();
        var eyebrow = Design.Text("你的桌面，一切就绪", 12, "Muted");
        eyebrow.Margin = new(0, 8, 0, 12);
        panel.Children.Add(eyebrow);
        var heading = Design.Text("留一点空间，\n给正在做的事。", 32);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Margin = new(0, 0, 0, 12);
        panel.Children.Add(heading);
        var description = Design.Text("把常用入口归在一起，随手展开，即刻开始。", 14, "Muted");
        description.Margin = new(0, 0, 0, 20);
        panel.Children.Add(description);
        var shortcuts = new WrapPanel { Margin = new(-4, 0, 0, 14) };
        shortcuts.Children.Add(Design.Button("整理桌面入口", DiscoverDesktop));
        shortcuts.Children.Add(Design.AsyncButton("添加应用", () => PickApps()));
        shortcuts.Children.Add(Design.Button("快速记一件事", () => CalendarWindow.EditTask(ctx, this, null, true)));
        panel.Children.Add(shortcuts);
        homeTasks = new StackPanel();
        panel.Children.Add(Design.Card(homeTasks));
        RefreshToday();
        foreach (var plugin in ctx.Plugins.List().Where(p => p.Manifest is not null && ctx.Db.Current.Plugins.GetValueOrDefault(p.Manifest.Id)))
        {
            var m = plugin.Manifest!;
            if (m.Capabilities.Contains("card"))
                panel.Children.Add(new PluginCard(ctx, m, plugin.Folder));
            if (m.Capabilities.Contains("command"))
                panel.Children.Add(Design.Button(m.Name + " · 执行命令", () => OpenPlugin(m, plugin.Folder, "command")));
        }
        panel.Children.Add(Design.Text("原文件始终留在原处。移除入口不会删除文件。", 12, "Muted"));
        content.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void RefreshToday()
    {
        var today = ctx.Calendar.Query(DateTime.Today.ToString("yyyy-MM-dd")).ToArray();
        if (homeTasks is null)
            return;
        var daily = homeTasks;
        daily.Children.Clear();
        daily.Children.Add(Design.Text($"今天 · {DateTime.Today:MM月dd日}  /  {today.Count(t => !t.Completed)} 项待办", 18));
        foreach (var task in today.Take(8))
        {
            var check = new CheckBox { Content = $"{task.Time}  {task.Title}", IsChecked = task.Completed };
            check.Click += (_, _) => Design.Guard(() => ctx.Calendar.Save(task with { Completed = check.IsChecked == true }));
            daily.Children.Add(check);
        }
        if (today.Length == 0)
            daily.Children.Add(Design.Text("今天还没有安排。", 14, "Muted"));
        daily.Children.Add(Design.Button("查看日历", ctx.OpenCalendar));

    }
    private void EntryPage()
    {
        if (entryPage is not null)
        {
            content.Content = entryPage;
            RefreshCategoryFilter();
            RefreshEntries();
            return;
        }
        var panel = new DockPanel();
        entryPage = panel;
        var top = new StackPanel();
        top.Children.Add(Design.Text("全部入口", 24));
        top.Children.Add(Design.Text("搜索入口名称或目标", 12, "Muted"));
        top.Children.Add(search);
        filter.ItemsSource = new[] { new Category("", "全部分类") }.Concat(ctx.Db.Current.Categories).ToArray();
        filter.SelectedIndex = 0;
        top.Children.Add(Design.Row(filter, Design.Button("桌面导入", DiscoverDesktop), Design.AsyncButton("选择应用", () => PickApps()), Design.AsyncButton("选择文件", PickFiles), Design.Button("选择文件夹", PickFolder)));
        top.Children.Add(Design.Row(Design.Button("全选", entries.SelectAll), Design.Button("调整分类", MoveSelected), Design.Button("移除入口", () => { var ids = SelectedIds(); if (ids.Length > 0) ctx.Catalog.Remove(ids); }), Design.AsyncButton("打开", async () => { if (entries.SelectedItem is EntryRow e) await ctx.Open(e.Entry); }), Design.Button("重新定位", Relocate)));
        top.Children.Add(status);
        DockPanel.SetDock(top, Dock.Top);
        panel.Children.Add(top);
        panel.Children.Add(entries);
        content.Content = panel;
        RefreshEntries();
    }
    private void RefreshCategoryFilter()
    {
        var selected = filter.SelectedValue as string;
        filter.ItemsSource = new[] { new Category("", "全部分类") }.Concat(ctx.Db.Current.Categories).ToArray();
        filter.SelectedValue = ctx.Db.Current.Categories.Any(c => c.Id == selected) ? selected : "";
    }
    private string[] SelectedIds() => entries.SelectedItems.Cast<EntryRow>().Select(e => e.Entry.Id).ToArray();
    internal string[] QaSelectedIds() => SelectedIds();
    internal void LocateEntries(string[] ids)
    {
        ShowPage("entries");
        search.Clear();
        filter.SelectedValue = "";
        Dispatcher.BeginInvoke(() =>
        {
            RefreshEntries();
            foreach (var row in entries.Items.Cast<EntryRow>().Where(r => ids.Contains(r.Entry.Id)))
                entries.SelectedItems.Add(row);
            if (entries.SelectedItem is not null) entries.ScrollIntoView(entries.SelectedItem);
            entries.Focus();
            status.Text = $"已定位 {entries.SelectedItems.Count} 个入口；重复入口保留原有分类。";
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void RefreshEntries()
    {
        if (page != "entries")
            return;
        var id = (string?)filter.SelectedValue;
        var rows = ctx.Catalog.Search(search.Text.Trim()).Where(e => string.IsNullOrEmpty(id) || e.CategoryId == id).Select(e => new EntryRow(e, ctx.Db.Current.Categories.First(c => c.Id == e.CategoryId).Name)).ToArray();
        entries.ItemsSource = rows;
        status.Text = $"{rows.Length} 个入口 · Ctrl / Shift 可多选；拖到下方分类调整归属。";
    }
    private void MoveSelected()
    {
        var ids = SelectedIds();
        if (ids.Length == 0)
            return;
        var dialog = new GlassWindow { Owner = this, Width = 380, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var combo = new ComboBox { ItemsSource = ctx.Db.Current.Categories, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedIndex = 0, Margin = new(5), Height = 34 };
        var body = new StackPanel();
        body.Children.Add(combo);
        body.Children.Add(Design.Button("移动入口到此分类", () => { ctx.Catalog.Move(ids, (string)combo.SelectedValue); dialog.Close(); }));
        dialog.SetPage("调整分类", body);
        dialog.ShowDialog();
    }
    private async Task PickFiles()
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "选择要收录的入口" };
        if (picker.ShowDialog(this) != true)
            return;
        var selected = await Platform.Sta(() => picker.FileNames.Select(Platform.Resolve).ToArray());
        var result = ctx.Catalog.AddRange(selected);
        if (result.Existing > 0) ctx.LocateEntries(selected);
        else MessageBox.Show($"添加 {result.Added} 个入口。", "GlassBogo");
    }
    private void PickFolder()
    {
        var picker = new OpenFolderDialog { Title = "添加文件夹入口" };
        if (picker.ShowDialog(this) == true)
        {
            var item = ctx.Catalog.Add(new(Guid.NewGuid().ToString("N"), Path.GetFileName(picker.FolderName), picker.FolderName));
            ctx.LocateEntries([item]);
        }
    }
    private void Relocate()
    {
        if (entries.SelectedItem is not EntryRow selected)
            return;
        var chooseFolder = MessageBox.Show("目标是文件夹吗？\n选择“否”可重新选择文件或程序。", "重新定位", MessageBoxButton.YesNoCancel);
        if (chooseFolder == MessageBoxResult.Cancel)
            return;
        string? path = null;
        if (chooseFolder == MessageBoxResult.Yes)
        {
            var p = new OpenFolderDialog();
            if (p.ShowDialog(this) == true)
                path = p.FolderName;
        }
        else
        {
            var p = new OpenFileDialog();
            if (p.ShowDialog(this) == true)
                path = p.FileName;
        }
        if (path is null)
            return;
        var target = path;
        ctx.Db.Update(s => { var i = s.Entries.FindIndex(e => e.Id == selected.Entry.Id); s.Entries[i] = s.Entries[i] with { Target = target, Kind = "path" }; });
    }
    private void EntryMenu()
    {
        var menu = new ContextMenu();
        foreach (var plugin in ctx.Plugins.List().Where(p => p.Manifest is not null && ctx.Db.Current.Plugins.GetValueOrDefault(p.Manifest.Id) && p.Manifest.Capabilities.Contains("entry-menu")))
        {
            var item = new MenuItem { Header = plugin.Manifest!.Name };
            item.Click += (_, _) => { if (entries.SelectedItem is EntryRow e) OpenPlugin(plugin.Manifest!, plugin.Folder, "entry-menu", e.Entry.Name); };
            menu.Items.Add(item);
        }
        entries.ContextMenu = menu;
    }
    internal async void DiscoverDesktop()
    {
        try
        {
            await PickCandidates("桌面入口 · 勾选要收录的内容", await ctx.Desktop.DiscoverAsync(CancellationToken.None), true);
        }
        catch (Exception ex) { Design.Error(ex); }
    }
    private async Task PickApps()
    {
        status.Text = "正在读取应用列表…";
        await PickCandidates("选择已安装应用", await Platform.InstalledApps(), false);
    }
    private Task PickCandidates(string title, IReadOnlyList<Entry> candidates, bool desktop)
    {
        var existing = ctx.Db.Current.Entries.Select(Catalog.Identity).ToHashSet(StringComparer.Ordinal);
        var rows = candidates.Where(e => !existing.Contains(Catalog.Identity(e))).DistinctBy(Catalog.Identity, StringComparer.Ordinal).Select(e => new Candidate(e)).ToArray();
        var dialog = new GlassWindow { Owner = this, Width = 650, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new DockPanel();
        var header = new StackPanel();
        header.Children.Add(Design.Text("只添加快捷入口，不改变原文件。", 14, "Muted"));
        var query = new TextBox();
        header.Children.Add(query);
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);
        var list = new ListBox { ItemsSource = rows };
        var template = new DataTemplate(typeof(Candidate));
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.ContentProperty, new Binding("Name"));
        check.SetBinding(CheckBox.ToolTipProperty, new Binding("Target"));
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Selected") { Mode = BindingMode.TwoWay });
        template.VisualTree = check;
        list.ItemTemplate = template;
        query.TextChanged += (_, _) => list.ItemsSource = rows.Where(r => r.Name.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var bottom = Design.Row(Design.Button("全选当前结果", () => { foreach (var r in list.Items.Cast<Candidate>()) r.Selected = true; list.Items.Refresh(); }), Design.Button("添加选中", () => { var result = ctx.Catalog.AddRange(rows.Where(r => r.Selected).Select(r => r.Entry)); ctx.Db.Update(s => s.Preferences = s.Preferences with { Onboarded = true }); dialog.Close(); MessageBox.Show($"添加 {result.Added} 个入口；已有 {result.Existing} 个。", "GlassBogo"); }), Design.Button("稍后整理", () => { ctx.Db.Update(s => s.Preferences = s.Preferences with { Onboarded = true }); dialog.Close(); }));
        if (desktop)
            bottom.Children.Add(Design.Button("忽略选中提示", () => { ctx.Db.Update(s => { foreach (var r in rows.Where(r => r.Selected)) if (!s.DismissedDiscoveries.Contains(Catalog.Identity(r.Entry))) s.DismissedDiscoveries.Add(Catalog.Identity(r.Entry)); s.Preferences = s.Preferences with { Onboarded = true }; }); dialog.Close(); }));
        DockPanel.SetDock(bottom, Dock.Bottom);
        body.Children.Add(bottom);
        body.Children.Add(list);
        dialog.SetPage(title, body);
        dialog.ShowDialog();
        return Task.CompletedTask;
    }
    private sealed class Candidate(Entry entry)
    {
        public Entry Entry => entry; public string Name => entry.Name; public string Target => entry.Target; public bool Selected
        {
            get; set;
        }
    }
    private void SettingsPage()
    {
        var p = new StackPanel();
        p.Children.Add(Design.Text("设置", 24));
        var theme = new ComboBox { ItemsSource = new[] { new Choice("system", "跟随系统"), new Choice("light", "浅色"), new Choice("dark", "深色") }, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = ctx.Db.Current.Preferences.Theme, Width = 170, Margin = new(5) };
        theme.SelectionChanged += (_, _) => Design.Guard(() => { var value = (string)theme.SelectedValue; ctx.Db.Update(s => s.Preferences = s.Preferences with { Theme = value }); Design.Apply(value); });
        p.Children.Add(Design.Row(Design.Text("外观主题"), theme));
        var hidden = new CheckBox { Content = "隐藏 Windows 桌面图标（退出时恢复）", IsChecked = ctx.Desktop.HiddenByUs };
        hidden.Click += (_, _) => { try { if (hidden.IsChecked == true) ctx.Desktop.HideIcons(); else ctx.Desktop.RestoreIcons(); } catch (Exception ex) { hidden.IsChecked = ctx.Desktop.HiddenByUs; Design.Error(ex); } };
        p.Children.Add(hidden);
        var auto = new CheckBox { Content = "鼠标离开后收起底部分类栏", IsChecked = ctx.Db.Current.Preferences.AutoHide };
        auto.Click += (_, _) => Design.Guard(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { AutoHide = auto.IsChecked == true }));
        p.Children.Add(auto);
        var hardware = new CheckBox { Content = "硬件加速控件绘制（关闭时更省内存，重启生效）", IsChecked = ctx.Db.Current.Preferences.HardwareAcceleration };
        hardware.Click += (_, _) => Design.Guard(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { HardwareAcceleration = hardware.IsChecked == true }));
        p.Children.Add(hardware);
        var startup = new CheckBox { Content = "登录 Windows 时启动", IsChecked = ctx.Db.Current.Preferences.AutoStart };
        startup.Click += (_, _) => Design.Guard(() => { Platform.AutoStart(startup.IsChecked == true); ctx.Db.Update(s => s.Preferences = s.Preferences with { AutoStart = startup.IsChecked == true }); });
        p.Children.Add(startup);
        var monitor = new ComboBox { ItemsSource = Display.AllScreens.Select(s => s.DeviceName).ToArray(), SelectedItem = ctx.Db.Current.Preferences.Monitor ?? Display.PrimaryScreen!.DeviceName, Width = 240, Margin = new(5) };
        monitor.SelectionChanged += (_, _) => Design.Guard(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { Monitor = (string)monitor.SelectedItem }));
        p.Children.Add(Design.Row(Design.Text("底部栏显示器"), monitor));
        var shortcut = new ComboBox { ItemsSource = new[] { "N", "Q", "J", "" }, SelectedItem = ctx.Db.Current.Preferences.QuickShortcut, Width = 100, Margin = new(5) };
        shortcut.SelectionChanged += (_, _) => Design.Guard(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { QuickShortcut = (string)shortcut.SelectedItem }));
        p.Children.Add(Design.Row(Design.Text("速记快捷键 Ctrl+Alt+（空白表示关闭）"), shortcut));
        p.Children.Add(Design.Text(ctx.Dock?.ShortcutStatus ?? "", 12, "Muted"));
        p.Children.Add(Design.Row(Design.Button("导出全部数据", () => { var d = new SaveFileDialog { Filter = "GlassBogo 备份|*.json", FileName = "GlassBogo-backup.json" }; if (d.ShowDialog(this) == true) ctx.Db.Export(d.FileName); }), Design.Button("从备份恢复", () => { var d = new OpenFileDialog { Filter = "GlassBogo 备份|*.json", InitialDirectory = Path.Combine(ctx.Db.DirectoryPath, "backups") }; if (d.ShowDialog(this) == true && MessageBox.Show("用所选备份替换入口和日历？当前数据会先保存恢复副本。", "恢复数据", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { ctx.Db.Import(d.FileName); Design.Apply(ctx.Db.Current.Preferences.Theme); SettingsPage(); } })));
        p.Children.Add(Design.Row(Design.AsyncButton("导入旧 GlassDesk 入口", async () => { var d = new OpenFolderDialog { Title = "选择含 apps.json 的旧 Data 文件夹" }; if (d.ShowDialog(this) == true) { var result = await Platform.Sta(() => Migration.GlassDesk(ctx.Db, d.FolderName, e => Path.GetExtension(e.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? Platform.Resolve(e.Target) with { Id = e.Id, Name = e.Name } : e)); MessageBox.Show(result, "迁移结果"); } }), Design.Button("导入旧日历", () => { var d = new OpenFileDialog { Filter = "日历数据|*.json", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopCalendar", "data") }; if (d.ShowDialog(this) == true) MessageBox.Show(ctx.Calendar.Import(d.FileName), "迁移结果"); })));
        p.Children.Add(Design.Button("打开数据目录", () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ctx.Db.DirectoryPath) { UseShellExecute = true })));
        p.Children.Add(Design.Card(Design.Text("GlassBogo 0.1.1\n为轻盈的桌面与专注的工作而设计。\n独立实现；原文件始终保留原位。\n第三方：.NET / WPF（MIT），Microsoft.Data.Sqlite（MIT），SQLitePCLRaw（Apache-2.0），SQLite（Public Domain）。\n完整声明见程序目录 THIRD-PARTY-NOTICES.txt。", 13, "Muted")));
        content.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void PluginPage()
    {
        var p = new StackPanel();
        p.Children.Add(Design.Text("本地插件", 24));
        p.Children.Add(Design.Text("插件是独立本地程序。仅安装可信来源；启用前会展示声明的能力。", 13, "Muted"));
        p.Children.Add(Design.Button("从文件夹安装", () => { var d = new OpenFolderDialog { Title = "选择含 plugin.json 的插件目录" }; if (d.ShowDialog(this) != true) return; var m = ctx.Plugins.Inspect(d.FolderName); if (MessageBox.Show($"安装 {m.Name} {m.Version}？\n来源：{d.FolderName}\n能力：{string.Join("、", m.Capabilities)}\n安装后默认停用。", "安装插件", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { ctx.Plugins.Install(d.FolderName); PluginPage(); } }));
        foreach (var item in ctx.Plugins.List())
        {
            if (item.Manifest is not { } m)
            {
                p.Children.Add(Design.Card(Design.Text("插件无效：" + item.Error)));
                continue;
            }
            var enabled = ctx.Db.Current.Plugins.GetValueOrDefault(m.Id);
            var row = new StackPanel();
            row.Children.Add(Design.Text(m.Name + " · " + m.Version, 18));
            row.Children.Add(Design.Text(string.Join(" / ", m.Capabilities), 12, "Muted"));
            row.Children.Add(Design.Row(Design.Button(enabled ? "停用" : "启用", () => { if (!enabled && MessageBox.Show($"启用 {m.Name}？\n来源：本地插件目录\n{item.Folder}\n能力：{string.Join("、", m.Capabilities)}\n插件作为当前用户的独立程序运行。", "启用插件", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return; ctx.Db.Update(s => s.Plugins[m.Id] = !enabled); PluginPage(); }), Design.Button("卸载", () => { if (MessageBox.Show("卸载此插件？", "GlassBogo", MessageBoxButton.YesNo) == MessageBoxResult.Yes) { ctx.Plugins.Uninstall(m); PluginPage(); } })));
            var extensions = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var capability in m.Capabilities.Where(c => c != "entry-menu"))
            {
                var label = capability switch { "card" => "打开卡片", "page" => "打开页面", "command" => "执行命令", _ => capability };
                extensions.Children.Add(Design.Button(label, () => { if (!ctx.Db.Current.Plugins.GetValueOrDefault(m.Id)) throw new InvalidOperationException("请先启用插件。"); OpenPlugin(m, item.Folder, capability); }));
            }
            row.Children.Add(extensions);
            p.Children.Add(Design.Card(row));
        }
        content.Content = new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private void OpenPlugin(PluginManifest manifest, string folder, string method, string? entryName = null)
    {
        if (!manifest.Capabilities.Contains(method))
            return;
        var w = new GlassWindow { Title = manifest.Name, Owner = this, Width = 500, Height = 340, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel();
        panel.Children.Add(Design.Text("正在启动…"));
        w.SetPage(manifest.Name, panel);
        PluginSession? session = null;
        var cancel = new CancellationTokenSource();
        async Task Load(string? action = null)
        {
            try
            {
                session ??= new(folder, manifest);
                var view = await session.Request(method, action, entryName, cancel.Token);
                if (cancel.IsCancellationRequested)
                    return;
                panel.Children.Clear();
                panel.Children.Add(Design.Text(view.Title, 22));
                panel.Children.Add(Design.Text(view.Text));
                foreach (var a in view.Actions.Take(20))
                    panel.Children.Add(Design.AsyncButton(a.Label, () => Load(a.Id)));
            }
            catch (Exception ex) { if (!cancel.IsCancellationRequested) { panel.Children.Clear(); panel.Children.Add(Design.Text("插件不可用：" + ex.Message)); } }
        }
        w.Loaded += async (_, _) => await Load();
        w.Closed += async (_, _) => { cancel.Cancel(); if (session is not null) await session.DisposeAsync(); cancel.Dispose(); };
        w.ShowDialog();
    }
}
