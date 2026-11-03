using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Controls.Primitives;

namespace GlassBogo.App;

internal sealed class V2EntryItem(Entry entry, string hint) : INotifyPropertyChanged
{
    internal Entry Entry { get; } = entry;
    public string Name => Entry.Name;
    public string Hint { get; private set; } = hint;
    public ImageSource? Icon { get; private set; }
    private bool loading;
    public event PropertyChangedEventHandler? PropertyChanged;
    internal async Task Load(V2Context ctx)
    {
        if (loading) return; loading = true;
        try
        {
            Icon = await ctx.Icons.Get(Entry);
            if (Entry.Kind == "path")
            {
                var type = await Task.Run(() => Directory.Exists(Entry.Target) ? "文件夹" : File.Exists(Entry.Target) ? "文件" : "目标失效");
                Hint = type == "目标失效" ? "目标失效 · " + Hint : Hint.Replace("文件 / 文件夹", type, StringComparison.Ordinal);
            }
            PropertyChanged?.Invoke(this, new(null));
        }
        catch { Hint = "暂无法读取 · " + Hint; PropertyChanged?.Invoke(this, new(null)); }
    }
}

internal static class V2Ui
{
    internal static T Modal<T>(V2Context ctx, Func<T> show)
    {
        ctx.Modals++;
        try { return show(); }
        finally { ctx.Modals--; ctx.Dock?.ScheduleClose(); }
    }
    internal static TextBlock Label(string text, double size = 14, string color = "Ink") => Design.Text(text, size, color);
    internal static Button Button(string label, Action action) => Design.Button(label, action);
    internal static Button Async(string label, Func<Task> action) => Design.AsyncButton(label, action);
    internal static ContextMenu Menu(V2Context ctx)
    {
        var menu = new ContextMenu { Tag = ctx }; var held = false;
        menu.Opened += (_, _) => { if (!held) { held = true; ctx.Menus++; } };
        menu.Closed += (_, _) => { if (held) { held = false; ctx.Menus--; ctx.Dock?.ScheduleClose(); } };
        return menu;
    }
    internal static void Command(ContextMenu menu, string label, Func<Task> action)
    {
        var ctx = (V2Context)menu.Tag;
        var item = new MenuItem { Header = label };
        item.Click += async (_, _) => { try { await action(); } catch (Exception ex) { Modal(ctx, () => { Design.Error(ex); return true; }); } };
        menu.Items.Add(item);
    }
    internal static void Name(Window owner, V2Context ctx, string title, string initial, Func<string, Task> save, int max = 200)
    {
        var dialog = new V2Dialog(owner, ctx, title, 420, 245);
        var body = new StackPanel();
        var input = new TextBox { Text = initial, MaxLength = max, Margin = new(0, 14, 0, 8) }; body.Children.Add(input);
        var error = Label("", 12, "Warning");
        var done = Async("保存", async () =>
        {
            if (string.IsNullOrWhiteSpace(input.Text)) { error.Text = "请输入名称。"; return; }
            var name = input.Text.Trim(); dialog.SetBusy(true);
            try { await save(name); dialog.Complete(); }
            catch (Exception ex) { error.Text = "未保存：" + ex.Message; }
            finally { dialog.SetBusy(false); }
        });
        done.IsDefault = true;
        dialog.Actions.Children.Add(done); body.Children.Add(error);
        dialog.Main.Content = body; dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };
        ctx.Modals++; try { dialog.ShowDialog(); } finally { ctx.Modals--; ctx.Dock?.ScheduleClose(); }
    }
    internal static void NewCategory(Window owner, V2Context ctx) => Name(owner, ctx, "新建分类", "", name => ctx.Write(() => ctx.Catalog.SaveCategory(null, name)), 40);
    internal static async Task Pin(V2Context ctx, Entry entry)
    {
        await ctx.Write(() => ctx.Catalog.PlaceRange([entry], pinned: true));
    }
    internal static ContextMenu EntryMenu(Window owner, V2Context ctx, Entry entry, bool fixedArea = false)
    {
        var menu = Menu(ctx);
        Command(menu, "打开", async () => { var result = await ctx.Launch(entry); if (!result.Success) Modal(ctx, () => MessageBox.Show(owner, result.Error, "无法打开")); });
        var existing = ctx.Db.Current.Entries.FirstOrDefault(e => Catalog.Identity(e) == Catalog.Identity(entry));
        Command(menu, existing?.Pinned == true ? "取消固定" : "固定到 Dock", () => existing?.Pinned == true ? ctx.Write(() => ctx.Catalog.SetPinned([existing.Id], false)) : Pin(ctx, entry));
        if (existing is not null)
        {
            Command(menu, "重命名显示名称", () => { Name(owner, ctx, "显示名称", existing.Name, name => ctx.Write(() => ctx.Catalog.Rename(existing.Id, name))); return Task.CompletedTask; });
            Command(menu, "上移", () => ctx.Write(() => { if (fixedArea) ctx.Catalog.StepPinned(existing.Id, -1); else ctx.Catalog.StepEntry(existing.Id, -1); }));
            Command(menu, "下移", () => ctx.Write(() => { if (fixedArea) ctx.Catalog.StepPinned(existing.Id, 1); else ctx.Catalog.StepEntry(existing.Id, 1); }));
            if (existing.CategoryId.Length > 0) Command(menu, "移出分类", () => ctx.Write(() => ctx.Catalog.Move([existing.Id], "")));
            Command(menu, "移除入口", () => Remove(owner, ctx, [existing.Id]));
            Command(menu, "重新定位", () => Relocate(owner, ctx, existing));
        }
        Command(menu, "移动到分类…", () => { ChooseCategory(owner, ctx, existing is null ? [] : [existing.Id], existing is null ? entry : null); return Task.CompletedTask; });
        Command(menu, "打开所在位置", () => Location(entry));
        return menu;
    }
    internal static async Task Remove(Window owner, V2Context ctx, string[] ids)
    {
        if (ids.Length == 0) return;
        if (Modal(ctx, () => MessageBox.Show(owner, $"移除 {ids.Length} 个入口？\n分类和固定区的引用会一并移除，真实文件保留。", "移除入口", MessageBoxButton.YesNo, MessageBoxImage.Question)) == MessageBoxResult.Yes)
            await ctx.Write(() => ctx.Catalog.Remove(ids));
    }
    internal static void ChooseCategory(Window owner, V2Context ctx, string[] ids, Entry? candidate = null)
    {
        if (ids.Length == 0 && candidate is null) return;
        var dialog = new V2Dialog(owner, ctx, "选择分类", 420, 255);
        var body = new StackPanel(); body.Children.Add(Label("移动到分类", 16));
        var combo = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", Margin = new(0, 12, 0, 10) };
        void Refresh() { var id = combo.SelectedValue as string; combo.ItemsSource = new[] { new Category("", "不属于分类") }.Concat(ctx.Db.Current.Categories).ToArray(); combo.SelectedValue = id is not null && ctx.Db.Current.Categories.Any(c => c.Id == id) ? id : ""; }
        Refresh(); body.Children.Add(combo);
        var error = Label("", 12, "Warning");
        body.Children.Add(Button("新建分类", () => { NewCategory(dialog, ctx); Refresh(); }));
        var done = Async("确定", async () =>
        {
            var category = combo.SelectedValue as string ?? ""; dialog.SetBusy(true);
            try
            {
                await ctx.Write(() => { if (candidate is null) ctx.Catalog.Move(ids, category); else ctx.Catalog.PlaceRange([candidate], category); });
                dialog.Complete();
            }
            catch (Exception ex) { error.Text = "未保存：" + ex.Message; }
            finally { dialog.SetBusy(false); }
        }); done.IsDefault = true; dialog.Actions.Children.Add(done); body.Children.Add(error); dialog.Main.Content = body;
        ctx.Modals++; try { dialog.ShowDialog(); } finally { ctx.Modals--; ctx.Dock?.ScheduleClose(); }
    }
    internal static async Task PickFiles(Window owner, V2Context ctx, string? category = null, bool? pin = null)
    {
        var picker = new OpenFileDialog { Multiselect = true, Title = "添加应用、快捷方式或文件" };
        if (Modal(ctx, () => picker.ShowDialog(owner)) != true) return;
        var names = picker.FileNames;
        var entries = await Platform.Sta(() => names.Select(Platform.Resolve).ToArray());
        await ctx.Write(() => ctx.Catalog.PlaceRange(entries, category, pin));
    }
    internal static async Task PickFolder(Window owner, V2Context ctx, string? category = null, bool? pin = null)
    {
        var picker = new OpenFolderDialog { Title = "添加文件夹" };
        if (Modal(ctx, () => picker.ShowDialog(owner)) != true) return;
        var path = picker.FolderName;
        await ctx.Write(() => ctx.Catalog.PlaceRange([new Entry(Guid.NewGuid().ToString("N"), Platform.TargetName(path, folder: true), path)], category, pin));
    }
    internal static async Task Relocate(Window owner, V2Context ctx, Entry entry)
    {
        var answer = Modal(ctx, () => MessageBox.Show(owner, "新目标是文件夹吗？\n选择“否”可选择应用或文件。", "重新定位", MessageBoxButton.YesNoCancel));
        if (answer == MessageBoxResult.Cancel) return;
        string path;
        if (answer == MessageBoxResult.Yes) { var p = new OpenFolderDialog(); if (Modal(ctx, () => p.ShowDialog(owner)) != true) return; path = p.FolderName; }
        else { var p = new OpenFileDialog(); if (Modal(ctx, () => p.ShowDialog(owner)) != true) return; path = p.FileName; }
        var resolved = await Platform.Sta(() => Platform.Resolve(path));
        await ctx.Write(() => ctx.Catalog.Relocate(entry.Id, resolved));
    }
    internal static Task Location(Entry entry) => Task.Run(() =>
    {
        if (entry.Kind == "app") { Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder") { UseShellExecute = true }); return; }
        var target = entry.Target;
        if (!File.Exists(target) && !Directory.Exists(target)) throw new FileNotFoundException("目标不存在，请重新定位入口。", target);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(target); Process.Start(start);
    });
    internal static ListBox EntryList(V2Context ctx, Func<object, UIElement>? rowActions = null)
    {
        var list = new ListBox { SelectionMode = SelectionMode.Extended, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        ScrollViewer.SetCanContentScroll(list, true); ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        var template = new DataTemplate(typeof(V2EntryItem));
        var row = new FrameworkElementFactory(typeof(DockPanel));
        if (rowActions is not null)
        {
            var actions = new FrameworkElementFactory(typeof(V2RowActions));
            actions.SetValue(DockPanel.DockProperty, Dock.Right);
            actions.SetValue(V2RowActions.BuilderProperty, rowActions);
            actions.SetValue(FrameworkElement.MarginProperty, new Thickness(12, 0, 0, 0));
            row.AppendChild(actions);
        }
        var icon = new FrameworkElementFactory(typeof(Image)); icon.SetValue(FrameworkElement.WidthProperty, 32d); icon.SetValue(FrameworkElement.HeightProperty, 32d); icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 12, 0)); icon.SetBinding(Image.SourceProperty, new Binding("Icon")); row.AppendChild(icon);
        var text = new FrameworkElementFactory(typeof(StackPanel));
        foreach (var (field, size) in new[] { ("Name", 14d), ("Hint", 11d) })
        {
            var block = new FrameworkElementFactory(typeof(TextBlock)); block.SetBinding(TextBlock.TextProperty, new Binding(field)); block.SetValue(TextBlock.FontSizeProperty, size); block.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); text.AppendChild(block);
        }
        row.AppendChild(text); template.VisualTree = row; list.ItemTemplate = template;
        var style = new Style(typeof(ListBoxItem), (Style?)Application.Current.TryFindResource(typeof(ListBoxItem)));
        style.Setters.Add(new EventSetter(FrameworkElement.LoadedEvent, new RoutedEventHandler(async (sender, _) =>
        {
            var item = (ListBoxItem)sender;
            if (item.Tag is not true) { item.Tag = true; item.DataContextChanged += async (_, _) => { if (item.DataContext is V2EntryItem vm) await vm.Load(ctx); }; }
            if (item.DataContext is V2EntryItem vm) await vm.Load(ctx);
        }))); list.ItemContainerStyle = style;
        return list;
    }
    internal static string TypeHint(Entry e) => e.Kind == "app" ? "应用" : Path.GetExtension(e.Target).Equals(".exe", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(e.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "应用" : "文件 / 文件夹";
    internal static void ChooseEntries(Window owner, V2Context ctx, string? category = null, bool? pin = null, bool discover = false)
    {
        var dialog = new EntryPickerWindow(owner, ctx, category, pin, discover);
        Modal(ctx, () => dialog.ShowDialog());
    }
    internal static ContextMenu AddMenu(Window owner, V2Context ctx, string? category = null, bool? pin = null)
    {
        var menu = Menu(ctx);
        Command(menu, "从全部入口选择…", () => { ChooseEntries(owner, ctx, category, pin); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        Command(menu, "添加新应用…", () => { ChooseEntries(owner, ctx, category, pin, discover: true); return Task.CompletedTask; });
        Command(menu, "选择应用或文件…", () => PickFiles(owner, ctx, category, pin));
        Command(menu, "选择文件夹…", () => PickFolder(owner, ctx, category, pin));
        return menu;
    }
    internal static void OpenMenu(Button anchor, ContextMenu menu) { menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true; }
}

// Rebuild the small action area for the current row when a virtualized item is
// recycled. Commands always capture this row's item, not the list's selection.
internal sealed class V2RowActions : ContentControl
{
    public static readonly DependencyProperty BuilderProperty = DependencyProperty.Register(nameof(Builder), typeof(Func<object, UIElement>), typeof(V2RowActions), new PropertyMetadata(null, (d, _) => ((V2RowActions)d).Refresh()));
    public Func<object, UIElement>? Builder { get => (Func<object, UIElement>?)GetValue(BuilderProperty); set => SetValue(BuilderProperty, value); }
    public V2RowActions() { VerticalContentAlignment = VerticalAlignment.Center; DataContextChanged += (_, _) => Refresh(); }
    private void Refresh() => Content = DataContext is { } item ? Builder?.Invoke(item) : null;
}
