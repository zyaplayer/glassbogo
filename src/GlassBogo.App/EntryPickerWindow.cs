using Microsoft.Win32;

namespace GlassBogo.App;

internal sealed class EntryPickerWindow : V2Dialog
{
    private const int AllSource = 0, UnclassifiedSource = 1, DiscoveredSource = 2, StagedSource = 3;
    private readonly V2Context ctx;
    private readonly string? category;
    private readonly bool? pinned;
    internal readonly TextBox Query = new() { Margin = new(0, 8, 0, 8), ToolTip = "按名称或路径查找入口" };
    internal readonly ComboBox Source = new() { ItemsSource = new[] { "全部入口", "未分类", "发现已安装应用", "新选择的文件／文件夹" }, SelectedIndex = AllSource, MinWidth = 175 };
    internal readonly ListBox Entries;
    private readonly Button add;
    private readonly Button refresh;
    private readonly TextBlock sourceHint = V2Ui.Label("", 12, "Muted");
    private readonly TextBlock status = V2Ui.Label("", 12, "Muted");
    private readonly TextBlock selection = V2Ui.Label("", 12, "Muted");
    private readonly CancellationTokenSource cancellation = new();
    private readonly CancellationToken token;
    private Entry[] desktop = [], discoveredApps = [], staged = [];
    private int generation;
    private bool filtering, loading, changingSource;
    internal EntryPickerWindow(Window owner, V2Context ctx, string? category = null, bool? pinned = null, bool discover = false)
        : base(owner, ctx, "添加入口", 660, 560)
    {
        this.ctx = ctx; this.category = category; this.pinned = pinned; token = cancellation.Token;
        var root = new DockPanel(); var heading = new StackPanel();
        var name = ctx.Db.Current.Categories.FirstOrDefault(c => c.Id == category)?.Name;
        heading.Children.Add(V2Ui.Label(name is not null ? "添加到“" + name + "”" : pinned == true ? "固定到 Dock" : "选择要添加的入口", 16));
        heading.Children.Add(V2Ui.Label(category is not null ? "优先从已有入口选择；属于其他分类的入口将移到这里，固定状态保持。" : "已有入口会复用原记录；新增内容在点击“添加选中”后保存。", 12, "Muted"));
        var sources = new DockPanel { Margin = new(0, 8, 0, 0) };
        var create = V2Ui.Button("新增入口…", () => { }); DockPanel.SetDock(create, Dock.Right); sources.Children.Add(create); sources.Children.Add(Source); heading.Children.Add(sources); heading.Children.Add(Query);
        refresh = V2Ui.Button("刷新", () => { });
        DockPanel.SetDock(refresh, Dock.Right); sources.Children.Insert(1, refresh);
        heading.Children.Insert(heading.Children.Count - 1, sourceHint);
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var footer = new StackPanel(); footer.Children.Add(status); footer.Children.Add(selection); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        Entries = V2Ui.EntryList(ctx); root.Children.Add(Entries); Main.Content = root;
        add = V2Ui.Async("添加选中", Save); add.IsEnabled = false; add.IsDefault = true; Actions.Children.Add(add);
        create.Click += (_, _) =>
        {
            var menu = V2Ui.Menu(ctx);
            V2Ui.Command(menu, "发现已安装应用", () => { Source.SelectedIndex = DiscoveredSource; return Task.CompletedTask; });
            V2Ui.Command(menu, "选择应用或文件…", () => Stage(false));
            V2Ui.Command(menu, "选择文件夹…", () => Stage(true)); V2Ui.OpenMenu(create, menu);
        };
        refresh.Click += async (_, _) =>
        {
            if (loading || Busy) return;
            changingSource = true;
            try { if (Source.SelectedIndex == StagedSource) Source.SelectedIndex = AllSource; } finally { changingSource = false; }
            await RefreshSource(force: true);
        };
        Query.TextChanged += (_, _) => Filter();
        Query.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up && Entries.Items.Count > 0)
            {
                Entries.SelectedIndex = Entries.SelectedIndex < 0 ? e.Key == Key.Down ? 0 : Entries.Items.Count - 1 : Math.Clamp(Entries.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Entries.Items.Count - 1);
                Entries.ScrollIntoView(Entries.SelectedItem); e.Handled = true;
            }
        };
        Entries.SelectionChanged += (_, _) => { if (!filtering) SelectionChanged(); };
        Source.SelectionChanged += async (_, _) => { if (IsLoaded && !changingSource) await RefreshSource(); };
        Loaded += async (_, _) => { if (discover) Source.SelectedIndex = DiscoveredSource; else await RefreshSource(); Query.Focus(); };
        ctx.Changed += Changed;
        Closed += (_, _) => { generation++; cancellation.Cancel(); ctx.Changed -= Changed; cancellation.Dispose(); };
    }
    private void Changed(object? sender, EventArgs e) { if (IsVisible && !Busy) Filter(); }
    private async Task RefreshSource(bool force = false)
    {
        var request = ++generation;
        var source = Source.SelectedIndex;
        loading = force || source != StagedSource; Filter();
        if (!loading) return;
        try
        {
            DesktopRefreshResult? result = null;
            var values = source == DiscoveredSource ? await ctx.Discovery.GetAsync(token, refresh: force)
                : force ? (result = await ctx.RefreshDesktop(token)).Candidates : await ctx.DesktopDiscovery.GetAsync(token);
            if (token.IsCancellationRequested || request != generation || !IsVisible) return;
            if (source == DiscoveredSource) discoveredApps = values.ToArray(); else desktop = values.ToArray();
            loading = false; Filter();
            if (result is not null) status.Text = result.Summary + (Entries.Items.Count == 0 && Query.Text.Length > 0 ? "；当前搜索没有匹配项，可清空搜索查看全部" : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (request == generation && IsVisible) { loading = false; Filter(); status.Text = "刷新失败：" + ex.Message + "，可重试或切回全部入口。"; } }
    }
    private void Filter()
    {
        if (Busy) return;
        var selected = Entries.SelectedItems.Cast<V2EntryItem>().Select(r => Catalog.Identity(r.Entry)).ToHashSet();
        IEnumerable<Entry> candidates = Source.SelectedIndex switch
        {
            UnclassifiedSource => Combined(ctx.Db.Current.Entries, desktop).Where(e => e.CategoryId.Length == 0),
            DiscoveredSource => Combined(ctx.Db.Current.Entries, discoveredApps).Where(e => V2Ui.TypeHint(e) == "应用"),
            StagedSource => staged,
            _ => Combined(ctx.Db.Current.Entries, desktop)
        };
        var text = Query.Text.Trim();
        var managed = ctx.Db.Current.Entries.ToDictionary(Catalog.Identity);
        var groups = ctx.Db.Current.Categories.ToDictionary(c => c.Id, c => c.Name);
        var rows = candidates.Where(e => e.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || e.Target.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .Select(e => new V2EntryItem(e, Hint(e, managed, groups))).ToArray();
        filtering = true;
        try
        {
            Entries.ItemsSource = rows;
            foreach (var row in rows.Where(r => selected.Contains(Catalog.Identity(r.Entry)))) Entries.SelectedItems.Add(row);
        }
        finally { filtering = false; }
        status.Text = loading ? "正在发现应用，仍可取消或切回全部入口…" : rows.Length > 0 ? $"{rows.Length} 个入口 · Ctrl / Shift 可多选" : text.Length > 0 ? "没有匹配的入口" : Source.SelectedIndex == AllSource ? "全部入口还是空的，可从右上角新增入口。" : Source.SelectedIndex == UnclassifiedSource ? "没有未分类入口" : "没有可选项，可切换来源或新增入口。";
        sourceHint.Text = Source.SelectedIndex == UnclassifiedSource ? "未分类的已有入口与新桌面内容；标记“尚未添加”的项将在确认后保存。" : Source.SelectedIndex == AllSource ? "已有入口与桌面第一层内容；刷新仅重扫桌面，不扫描文件夹内部或全盘。" : Source.SelectedIndex == DiscoveredSource ? "单独发现开始菜单与系统中的应用，不混入默认桌面列表。" : "新选择内容尚未保存，点击“添加选中”后加入。";
        SelectionChanged();
    }
    internal static IEnumerable<Entry> Combined(IEnumerable<Entry> managed, IEnumerable<Entry> discovered)
    {
        var existing = managed.ToArray();
        var identities = existing.Select(Catalog.Identity).ToHashSet(StringComparer.Ordinal);
        // Discovery offers a target already managed with the user's chosen working
        // directory. Keep that saved configuration, including every saved profile.
        var managedTargets = existing.Select(e => Catalog.Identity(e with { WorkingDirectory = "" })).ToHashSet(StringComparer.Ordinal);
        var links = existing.Where(e => e.Kind == "path" && e.Arguments.Length == 0 && Path.GetExtension(e.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            .Select(e => Normalize(e.Target)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origins = existing.Where(e => e.IconSource is not null && e.Kind == "path")
            .Select(e => (Origin: Normalize(e.IconSource!), Target: Normalize(e.Target), e.Arguments)).ToHashSet();
        foreach (var entry in existing) yield return entry;
        foreach (var entry in discovered)
        {
            if (identities.Contains(Catalog.Identity(entry)) || managedTargets.Contains(Catalog.Identity(entry with { WorkingDirectory = "" }))) continue;
            // A saved .lnk and its resolved target are the same picker choice.
            // Match the actual source, never a display name shared by unrelated files.
            if (entry.Kind == "path" && entry.IconSource is { } source &&
                (links.Contains(Normalize(source)) || origins.Contains((Normalize(source), Normalize(entry.Target), entry.Arguments)))) continue;
            if (identities.Add(Catalog.Identity(entry))) yield return entry;
        }
        static string Normalize(string value) => value.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    }
    private string Hint(Entry entry, IReadOnlyDictionary<string, Entry> entries, IReadOnlyDictionary<string, string> groups)
    {
        if (!entries.TryGetValue(Catalog.Identity(entry), out var managed)) return "未分类 · 尚未添加 · " + V2Ui.TypeHint(entry);
        var group = groups.GetValueOrDefault(managed.CategoryId) ?? "未分类";
        return "已有入口 · " + group + (category is not null && managed.CategoryId == category ? " · 已在此分类" : "") + (managed.Pinned ? " · 已固定" : "") + " · " + V2Ui.TypeHint(managed);
    }
    private void SelectionChanged()
    {
        selection.Text = Entries.SelectedItems.Count == 0 ? "请选择要添加的入口" : $"已选 {Entries.SelectedItems.Count} 项";
        add.IsEnabled = !Busy && Entries.SelectedItems.Count > 0;
        refresh.IsEnabled = !Busy && !loading; refresh.Content = loading ? "刷新中…" : "刷新";
        refresh.ToolTip = Source.SelectedIndex == DiscoveredSource ? "重新发现已安装应用；仅更新这个来源" : "重新读取桌面第一层内容并清理失效引用；不会删除实际文件";
    }
    private async Task Save()
    {
        var chosen = Entries.SelectedItems.Cast<V2EntryItem>().Select(r => r.Entry).ToArray();
        if (chosen.Length == 0 || Busy) return;
        var existing = ctx.Db.Current.Entries.Select(e => e.Id).ToHashSet();
        SetBusy(true);
        try
        {
            await ctx.Write(() =>
            {
                var current = chosen;
                current = chosen.Select(e => existing.Contains(e.Id) ? ctx.Db.Current.Entries.FirstOrDefault(now => now.Id == e.Id) ?? throw new InvalidOperationException("部分入口已被移除，请重新选择。") : e).ToArray();
                ctx.Catalog.PlaceRange(current, category, pinned);
            });
            Complete();
        }
        catch (Exception ex) { status.Text = "未添加：" + ex.Message; }
        finally { SetBusy(false); SelectionChanged(); }
    }
    private async Task Stage(bool folder)
    {
        Entry[] values;
        if (folder)
        {
            var picker = new OpenFolderDialog { Title = "选择要新增的文件夹" };
            if (V2Ui.Modal(ctx, () => picker.ShowDialog(this)) != true) return;
            var path = picker.FolderName;
            values = [new(Guid.NewGuid().ToString("N"), Platform.TargetName(path, folder: true), path)];
        }
        else
        {
            var picker = new OpenFileDialog { Multiselect = true, Title = "选择要新增的应用或文件" };
            if (V2Ui.Modal(ctx, () => picker.ShowDialog(this)) != true) return;
            var paths = picker.FileNames; values = await Platform.Sta(() => paths.Select(Platform.Resolve).ToArray());
        }
        if (!IsVisible || token.IsCancellationRequested) return;
        SetStaged(values);
    }
    internal void SetStaged(IEnumerable<Entry> values)
    {
        staged = values.DistinctBy(Catalog.Identity).ToArray(); Query.Clear(); Source.SelectedIndex = StagedSource; loading = false; generation++; Filter();
        foreach (var row in Entries.Items.Cast<V2EntryItem>().Where(row => !Entries.SelectedItems.Contains(row))) Entries.SelectedItems.Add(row);
    }
}
