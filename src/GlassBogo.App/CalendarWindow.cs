using System.Windows.Controls.Primitives;
using System.Windows.Data;
using Microsoft.Win32;

namespace GlassBogo.App;

internal sealed class CalendarWindow : GlassWindow
{
    private Dictionary<string, CalendarTask[]> dayLookup = [];
    private readonly AppContext ctx; private DateOnly selected = DateOnly.FromDateTime(DateTime.Today); private bool timetable;
    private readonly TextBlock heading = Design.Text("", 22); private readonly ContentControl calendar = new(); private readonly ListBox tasks = new();
    private readonly TextBox search = new() { ToolTip = "搜索标题、备注和分类（跨日期）" };
    private readonly ComboBox status = new() { ItemsSource = new[] { "全部", "未完成", "已完成", "已逾期", "重要" }, SelectedIndex = 0, Width = 105, Margin = new(4) };
    private readonly ComboBox category = new() { Width = 120, Margin = new(4), DisplayMemberPath = "Name", SelectedValuePath = "Id" };
    internal CalendarWindow(AppContext context)
    {
        ctx = context;
        Title = "GlassBogo · 日历";
        Width = 900;
        Height = 760;
        MinWidth = 700;
        MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel();
        var top = new StackPanel();
        top.Children.Add(Design.Row(Design.Button("‹", () => Navigate(-1)), heading, Design.Button("›", () => Navigate(1)), Design.Button("今天", () => { selected = DateOnly.FromDateTime(DateTime.Today); Refresh(); }), Design.Button("月历 / 课表", () => { timetable = !timetable; Refresh(); }), Design.Button("固定 / 取消置顶", () => Topmost = !Topmost)));
        top.Children.Add(Design.Row(Design.Button("添加任务", () => EditTask(ctx, this, new(Guid.NewGuid().ToString("N"), "", selected.ToString("yyyy-MM-dd")))), Design.Button("中文速记", () => EditTask(ctx, this, null, true)), Design.Button("任务分类", ManageCategories), Design.Button("导出日历", Export)));
        top.Children.Add(Design.Text("搜索标题、备注或分类（跨日期）", 12, "Muted"));
        top.Children.Add(search);
        top.Children.Add(Design.Row(status, category));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        var body = new Grid();
        body.RowDefinitions.Add(new()
        {
            Height = new GridLength(1.1, GridUnitType.Star)
        });
        body.RowDefinitions.Add(new()
        {
            Height = new GridLength(1, GridUnitType.Star)
        });
        body.Children.Add(calendar);
        var taskPanel = new DockPanel();
        Grid.SetRow(taskPanel, 1);
        var actions = Design.Row(Design.Button("编辑选中", () => { if (tasks.SelectedItem is CalendarTask t) EditTask(ctx, this, t); }), Design.Button("删除选中", () => { if (tasks.SelectedItem is CalendarTask t && MessageBox.Show("删除此任务？", "GlassBogo", MessageBoxButton.YesNo) == MessageBoxResult.Yes) ctx.Calendar.Remove(t.Id); }));
        DockPanel.SetDock(actions, Dock.Bottom);
        taskPanel.Children.Add(actions);
        taskPanel.Children.Add(tasks);
        body.Children.Add(taskPanel);
        root.Children.Add(body);
        SetPage("日历与今日安排", root);
        var template = new DataTemplate(typeof(CalendarTask));
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var stripe = new FrameworkElementFactory(typeof(Border));
        stripe.SetValue(Border.WidthProperty, 3d);
        stripe.SetValue(Border.HeightProperty, 24d);
        stripe.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        stripe.SetBinding(Border.BackgroundProperty, new Binding("CategoryId") { Converter = new TaskColorConverter(ctx) });
        row.AppendChild(stripe);
        var check = new FrameworkElementFactory(typeof(CheckBox));
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding("Completed") { Mode = BindingMode.OneWay });
        check.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((s, _) => { if (s is CheckBox { DataContext: CalendarTask t } c) Design.Guard(() => ctx.Calendar.Save(t with { Completed = c.IsChecked == true })); }));
        row.AppendChild(check);
        var date = new FrameworkElementFactory(typeof(TextBlock));
        date.SetBinding(TextBlock.TextProperty, new Binding("Date"));
        date.SetValue(TextBlock.MarginProperty, new Thickness(4));
        row.AppendChild(date);
        var time = new FrameworkElementFactory(typeof(TextBlock));
        time.SetBinding(TextBlock.TextProperty, new Binding("Time"));
        time.SetValue(TextBlock.MarginProperty, new Thickness(4));
        row.AppendChild(time);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new Binding("Title"));
        title.SetValue(TextBlock.MarginProperty, new Thickness(5));
        row.AppendChild(title);
        template.VisualTree = row;
        tasks.ItemTemplate = template;
        tasks.MouseDoubleClick += (_, _) => { if (tasks.SelectedItem is CalendarTask t) EditTask(ctx, this, t); };
        Point start = default;
        tasks.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(tasks);
        tasks.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(tasks) - start).Length > 8 && tasks.SelectedItem is CalendarTask t) DragDrop.DoDragDrop(tasks, new DataObject("GlassBogo.Task", t.Id), DragDropEffects.Move); };
        tasks.AllowDrop = true;
        tasks.Drop += (_, e) => { if (e.Data.GetData("GlassBogo.Task") is not string id) return; var hit = ItemsControl.ContainerFromElement(tasks, e.OriginalSource as DependencyObject) as ListBoxItem; if (hit?.DataContext is CalendarTask before) Design.Guard(() => ctx.Calendar.Reorder(id, before.Id)); };
        search.TextChanged += (_, _) => RefreshTasks();
        status.SelectionChanged += (_, _) => { DrawCalendar(); RefreshTasks(); };
        category.SelectionChanged += (_, _) => { DrawCalendar(); RefreshTasks(); };
        ctx.Db.Changed += OnData;
        Closed += (_, _) => ctx.Db.Changed -= OnData;
        Refresh();
    }
    private void OnData(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);
    private void Navigate(int direction)
    {
        selected = timetable ? selected.AddDays(direction * 7) : selected.AddMonths(direction);
        Refresh();
    }
    private void Refresh()
    {
        var old = category.SelectedValue as string;
        category.ItemsSource = new[] { new TaskCategory("", "全部分类", "#808080") }.Concat(ctx.Db.Current.TaskCategories).ToArray();
        category.SelectedValue = old ?? "";
        DrawCalendar();
        RefreshTasks();
    }
    private IEnumerable<CalendarTask> Filter(string? date) => ctx.Calendar.Query(date, search.Text.Trim(), string.IsNullOrEmpty(category.SelectedValue as string) ? null : (string)category.SelectedValue, new[] { "all", "open", "done", "overdue", "high" }[Math.Max(0, status.SelectedIndex)]);
    private void RefreshTasks() => tasks.ItemsSource = Filter(search.Text.Trim().Length > 0 ? null : selected.ToString("yyyy-MM-dd")).ToArray();
    private void DrawCalendar()
    {
        dayLookup = Filter(null).GroupBy(t => t.Date).ToDictionary(g => g.Key, g => g.ToArray());
        heading.Text = timetable ? $"{selected:yyyy年MM月dd日} · 本周" : $"{selected:yyyy年MM月}";
        var grid = new UniformGrid { Columns = 7, Rows = timetable ? 5 : 7 };
        var monday = selected.AddDays(-(((int)selected.DayOfWeek + 6) % 7));
        for (int d = 0; d < 7; d++)
            grid.Children.Add(Design.Text(timetable ? $"周{"一二三四五六日"[d]} {monday.AddDays(d):MM/dd}" : $"周{"一二三四五六日"[d]}", 12, "Muted"));
        if (timetable)
        {
            for (int period = 0; period < 4; period++)
                for (int day = 0; day < 7; day++)
                    grid.Children.Add(DayCell(monday.AddDays(day), CalendarService.Periods[period], period));
        }
        else
        {
            var first = new DateOnly(selected.Year, selected.Month, 1);
            first = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
            for (int day = 0; day < 42; day++)
                grid.Children.Add(DayCell(first.AddDays(day), null, 0));
        }
        calendar.Content = grid;
    }
    private Button DayCell(DateOnly date, string? time, int period)
    {
        var key = date.ToString("yyyy-MM-dd");
        var dayTasks = dayLookup.GetValueOrDefault(key, []).Where(t => time is null || Period(t.Time) == period).ToArray();
        var panel = new StackPanel();
        panel.Children.Add(Design.Text(time is null ? date.Day.ToString() : $"第{period + 1}节 · {time}", 12, date == selected ? "Accent" : "Ink"));
        if (dayTasks.Length > 0)
        {
            if (time is null)
                panel.Orientation = Orientation.Horizontal;
            var summary = Design.Text(time is null ? $" · {dayTasks.Length}" : dayTasks[0].Title, 11, "Accent");
            summary.TextWrapping = TextWrapping.NoWrap;
            summary.TextTrimming = TextTrimming.CharacterEllipsis;
            panel.Children.Add(summary);
        }
        var b = new Button { Content = panel, Padding = new(3), Margin = new(2), AllowDrop = true, Tag = date, Opacity = !timetable && date.Month != selected.Month ? .5 : 1 };
        b.Click += (_, _) => { selected = date; RefreshTasks(); if (calendar.Content is UniformGrid parent) foreach (var cell in parent.Children.OfType<Button>()) cell.SetResourceReference(Button.BorderBrushProperty, cell.Tag is DateOnly d && d == selected ? "Accent" : "Line"); };
        b.MouseDoubleClick += (_, e) => { e.Handled = true; EditTask(ctx, this, new(Guid.NewGuid().ToString("N"), "", key, time ?? "")); };
        b.Drop += (_, e) => { if (e.Data.GetData("GlassBogo.Task") is string id) { e.Handled = true; Design.Guard(() => { var task = ctx.Db.Current.Tasks.First(t => t.Id == id); ctx.Calendar.Save(task with { Date = key, Time = time ?? task.Time }); }); } };
        return b;
    }
    private static int Period(string time)
    {
        if (!TimeOnly.TryParseExact(time, "HH:mm", out var t))
            return -1;
        var ends = new[] { "09:35", "11:25", "15:20", "17:10" };
        for (int i = 0; i < 4; i++)
            if (t >= TimeOnly.Parse(CalendarService.Periods[i]) && t <= TimeOnly.Parse(ends[i]))
                return i;
        return -1;
    }
    internal static void EditTask(AppContext ctx, Window owner, CalendarTask? existing, bool quick = false)
    {
        var open = Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "编辑任务");
        if (open is not null)
        {
            open.Activate();
            return;
        }
        var task = existing ?? new CalendarTask(Guid.NewGuid().ToString("N"), "", DateTime.Today.ToString("yyyy-MM-dd"), Order: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var w = new GlassWindow { Title = "编辑任务", Owner = owner, Width = 540, Height = 650, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel();
        var title = new TextBox { Name = "TaskTitleInput", Text = task.Title, MaxLength = 200 };
        var date = new DatePicker { SelectedDate = DateTime.Parse(task.Date), Margin = new(4) };
        var time = new TextBox { Text = task.Time, Width = 100, ToolTip = "HH:mm；留空表示无指定时间" };
        var notes = new TextBox { Text = task.Notes, AcceptsReturn = true, Height = 110, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var priority = new ComboBox { ItemsSource = new[] { new Choice("normal", "普通"), new Choice("high", "重要"), new Choice("low", "较低") }, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = task.Priority, Margin = new(5), Height = 32 };
        var cats = new ComboBox { ItemsSource = new[] { new TaskCategory("", "未分类", "#808080") }.Concat(ctx.Db.Current.TaskCategories).ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = task.CategoryId ?? "", Margin = new(5), Height = 32 };
        var complete = new CheckBox { Content = "已完成", IsChecked = task.Completed };
        var error = Design.Text("", 12, "Accent");
        Button? saveButton = null, parseButton = null;
        if (quick)
        {
            var input = new TextBox { ToolTip = "例如：明天18点交报告、下周一第二节高数" };
            body.Children.Add(Design.Text("中文速记 · 识别后可修改", 16));
            body.Children.Add(input);
            parseButton = Design.Button("识别日期与时间", () => { try { var parsed = QuickAdd.Parse(input.Text, DateOnly.FromDateTime(DateTime.Today)); title.Text = parsed.Title; date.SelectedDate = DateTime.Parse(parsed.Date); time.Text = parsed.Time; error.Text = "识别完成，请核对后保存。"; if (saveButton is not null) saveButton.IsDefault = true; if (parseButton is not null) parseButton.IsDefault = false; } catch (Exception ex) { error.Text = ex.Message; } });
            parseButton.IsDefault = true;
            body.Children.Add(parseButton);
            w.Loaded += (_, _) => input.Focus();
        }
        body.Children.Add(Design.Text("任务内容"));
        body.Children.Add(title);
        body.Children.Add(Design.Text("日期与时间（HH:mm，时间可留空）", 12, "Muted"));
        body.Children.Add(Design.Row(date, time));
        body.Children.Add(Design.Text("分类"));
        body.Children.Add(cats);
        body.Children.Add(Design.Text("优先级"));
        body.Children.Add(priority);
        body.Children.Add(Design.Text("备注"));
        body.Children.Add(notes);
        body.Children.Add(complete);
        body.Children.Add(error);
        saveButton = Design.AsyncButton("保存任务", async () => { try { if (date.SelectedDate is null) throw new InvalidOperationException("请选择日期。"); var next = task with { Title = title.Text.Trim(), Date = date.SelectedDate.Value.ToString("yyyy-MM-dd"), Time = time.Text.Trim(), Notes = notes.Text, Priority = (string)priority.SelectedValue, CategoryId = string.IsNullOrEmpty((string?)cats.SelectedValue) ? null : (string)cats.SelectedValue, Completed = complete.IsChecked == true }; await Task.Run(() => ctx.Calendar.Save(next)); w.Close(); } catch (Exception ex) { error.Text = "未保存：" + ex.Message; } });
        saveButton.IsDefault = !quick;
        body.Children.Add(saveButton);
        if (!quick)
            w.Loaded += (_, _) => title.Focus();
        w.SetPage(existing is null || existing.Title == "" ? "添加任务" : "编辑任务", new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) w.Close(); };
        w.ShowDialog();
    }
    internal void QaToggleView()
    {
        timetable = !timetable;
        Refresh();
    }
    private void ManageCategories()
    {
        var w = new GlassWindow { Owner = this, Width = 470, Height = 420, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var body = new StackPanel();
        var list = new ListBox { ItemsSource = ctx.Db.Current.TaskCategories, DisplayMemberPath = "Name", Height = 145 };
        var name = new TextBox { MaxLength = 24 };
        var palette = new List<Choice> { new("#6B9FE8", "蓝色"), new("#72B5A0", "绿色"), new("#E8A26B", "橙色"), new("#B69AE8", "紫色"), new("#E8899C", "粉色") };
        var colors = new ComboBox { ItemsSource = palette, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedIndex = 0, Margin = new(5) };
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is TaskCategory c) { name.Text = c.Name; if (!palette.Any(p => p.Value == c.Color)) { palette.Add(new(c.Color, "原配色")); colors.Items.Refresh(); } colors.SelectedValue = c.Color; } };
        body.Children.Add(list);
        body.Children.Add(name);
        body.Children.Add(colors);
        body.Children.Add(Design.Row(Design.Button("保存 / 新增", () => { var old = list.SelectedItem as TaskCategory; ctx.Calendar.SaveCategory(new(old?.Id ?? Guid.NewGuid().ToString("N"), name.Text.Trim(), (string?)colors.SelectedValue ?? "#6B9FE8")); list.ItemsSource = ctx.Db.Current.TaskCategories; name.Text = ""; }), Design.Button("新建", () => { list.SelectedItem = null; name.Text = ""; }), Design.Button("删除分类", () => { if (list.SelectedItem is TaskCategory c) { ctx.Calendar.RemoveCategory(c.Id); list.ItemsSource = ctx.Db.Current.TaskCategories; } })));
        w.SetPage("任务分类与颜色", body);
        w.ShowDialog();
    }
    private void Export()
    {
        var d = new SaveFileDialog { FileName = "GlassBogo-calendar.json", Filter = "日历数据|*.json" };
        if (d.ShowDialog(this) == true)
            ctx.Calendar.Export(d.FileName);
    }
}
