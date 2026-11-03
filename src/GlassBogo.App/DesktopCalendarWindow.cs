using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace GlassBogo.App;

internal sealed class DesktopCalendarWindow : LiquidWindow
{
    private readonly V2Context ctx;
    private readonly UniformGrid days = new() { Columns = 7, Rows = 7 };
    private readonly ContentControl calendarHost = new();
    private readonly Grid layout = new();
    private string view;
    private int? selectedPeriod;
    private bool dragging, savingPosition;
    private Point dragStart;
    private double dragLeft, dragTop;
    internal sealed record LessonCell(DateOnly Date, int Period);
    private readonly TextBlock month = V2Ui.Label("", 18), heading = V2Ui.Label("", 14), summary = V2Ui.Label("", 12, "Muted");
    internal readonly ListBox Agenda = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateOnly today = DateOnly.FromDateTime(DateTime.Today);
    internal DateOnly Selected { get; private set; } = DateOnly.FromDateTime(DateTime.Today);
    private CalendarTask[] snapshot = [];
    private TaskCategory[] categories = [];
    private DesktopLayer? layer;
    internal DesktopCalendarWindow(V2Context ctx) : base(ctx.Graphics, glass: true)
    {
        this.ctx = ctx; view = ctx.Preferences.CalendarView; Title = "GlassBogo · 桌面日历与课表"; ShowInTaskbar = false; ShowActivated = false; Topmost = false;
        Owner = ctx.Dock; Body.Margin = new(-2);
        var root = layout; root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(150) }); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var top = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(Compact(V2Ui.Button("‹", () => Navigate(-1))));
        controls.Children.Add(Compact(V2Ui.Button("今天", () => SelectDate(DateOnly.FromDateTime(DateTime.Today)))));
        controls.Children.Add(Compact(V2Ui.Button("›", () => Navigate(1))));
        controls.Children.Add(Compact(V2Ui.Async("收起", () => ctx.SetCalendarVisible(false))));
        DockPanel.SetDock(controls, Dock.Right); top.Children.Add(controls); top.Children.Add(month); root.Children.Add(top);
        month.Cursor = Cursors.SizeAll; month.ToolTip = "拖动日历；松手保存位置";
        month.MouseLeftButtonDown += (_, e) => { if (savingPosition || e.ClickCount != 1) return; BeginDrag(month.PointToScreen(e.GetPosition(month))); month.CaptureMouse(); e.Handled = true; };
        month.MouseMove += (_, e) => { if (dragging && e.LeftButton == MouseButtonState.Pressed) DragTo(month.PointToScreen(e.GetPosition(month))); };
        month.MouseLeftButtonUp += async (_, e) => { if (!dragging) return; month.ReleaseMouseCapture(); await EndDrag(); e.Handled = true; };
        month.LostMouseCapture += async (_, _) => { if (dragging) await EndDrag(); };
        var modes = new DockPanel { Margin = new(0, 5, 0, 7) };
        var reset = Compact(V2Ui.Async("右上角", ResetPosition)); DockPanel.SetDock(reset, Dock.Right); modes.Children.Add(reset);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal }; tabs.Children.Add(Compact(V2Ui.Async("月历", () => ctx.SetCalendarView("month")))); tabs.Children.Add(Compact(V2Ui.Async("课表", () => ctx.SetCalendarView("week")))); modes.Children.Add(tabs);
        Grid.SetRow(modes, 1); root.Children.Add(modes);
        Grid.SetRow(calendarHost, 2); root.Children.Add(calendarHost);
        var day = new DockPanel { Margin = new(0, 8, 0, 6) };
        var add = Compact(V2Ui.Button("＋ 日程", () => Edit(initialTime: selectedPeriod is { } index ? Timetable.Slots[index].Start : ""))); DockPanel.SetDock(add, Dock.Right); day.Children.Add(add);
        var labels = new StackPanel(); labels.Children.Add(heading); labels.Children.Add(summary); day.Children.Add(labels); Grid.SetRow(day, 3); root.Children.Add(day);
        var template = new DataTemplate(typeof(CalendarTask)); var row = new FrameworkElementFactory(typeof(V2RowActions)); row.SetValue(V2RowActions.BuilderProperty, (Func<object, UIElement>)(value => TaskRow((CalendarTask)value))); template.VisualTree = row; Agenda.ItemTemplate = template;
        VirtualizingPanel.SetIsVirtualizing(Agenda, true); VirtualizingPanel.SetVirtualizationMode(Agenda, VirtualizationMode.Recycling); ScrollViewer.SetHorizontalScrollBarVisibility(Agenda, ScrollBarVisibility.Disabled);
        Grid.SetRow(Agenda, 4); root.Children.Add(Agenda); Body.Content = root;
        Loaded += (_, _) => { layer = new(this); clock.Start(); };
        Closing += async (_, e) => { if (!ctx.Closing) { e.Cancel = true; try { await ctx.SetCalendarVisible(false); } catch (Exception ex) { Design.Error(ex); } } };
        ctx.Changed += Changed;
        Closed += (_, _) => { ctx.Changed -= Changed; clock.Stop(); layer?.Dispose(); };
        clock.Tick += (_, _) =>
        {
            var now = DateOnly.FromDateTime(DateTime.Today); if (today == now) return;
            if (Selected == today) Selected = now; today = now; Refresh();
        };
        Refresh();
    }
    private static Button Compact(Button button) { button.MinWidth = 28; button.MinHeight = 26; button.Padding = new(6, 3, 6, 3); button.Margin = new(2, 0, 0, 0); return button; }
    private void Changed(object? sender, EventArgs e)
    {
        if (ctx.Closing) return;
        if (view != ctx.Preferences.CalendarView || !snapshot.SequenceEqual(ctx.Db.Current.Tasks) || !categories.SequenceEqual(ctx.Db.Current.TaskCategories)) Refresh();
        SyncVisibility();
    }
    internal void SyncVisibility()
    {
        if (ctx.Closing) return;
        if (view != ctx.Preferences.CalendarView) { view = ctx.Preferences.CalendarView; Refresh(); }
        if (!ctx.Preferences.DesktopCalendarVisible) { Hide(); clock.Stop(); return; }
        var now = DateOnly.FromDateTime(DateTime.Today); if (today != now) { if (Selected == today) Selected = now; today = now; Refresh(); }
        Position(); if (!IsVisible) { Show(); clock.Start(); } layer?.Assess();
    }
    internal void Position()
    {
        if (dragging || savingPosition || ctx.Dock is not { IsLoaded: true }) return;
        var screen = Display.PrimaryScreen; var work = screen.WorkingArea; var scale = screen.Scale;
        Width = Math.Min(view == "week" ? 900 : 420, work.Width / scale - 24); Height = Math.Min(view == "week" ? 620 : 450, work.Height / scale - 24);
        MoveTo(ctx.Preferences.CalendarX is { } x ? work.Left / scale + x : work.Right / scale - Width - 16, ctx.Preferences.CalendarY is { } y ? work.Top / scale + y : work.Top / scale + 16);
    }
    internal void MoveTo(double left, double top)
    {
        var screen = Display.PrimaryScreen; var work = screen.WorkingArea; var scale = screen.Scale;
        Left = Math.Clamp(left, work.Left / scale + 8, Math.Max(work.Left / scale + 8, work.Right / scale - Width - 8));
        Top = Math.Clamp(top, work.Top / scale + 8, Math.Max(work.Top / scale + 8, work.Bottom / scale - Height - 8));
    }
    internal void BeginDrag(Point screenPoint) { if (savingPosition) return; dragging = true; dragStart = screenPoint; dragLeft = Left; dragTop = Top; }
    internal void DragTo(Point screenPoint) { if (!dragging) return; var scale = Display.PrimaryScreen.Scale; MoveTo(dragLeft + (screenPoint.X - dragStart.X) / scale, dragTop + (screenPoint.Y - dragStart.Y) / scale); }
    internal async Task EndDrag()
    {
        if (!dragging) return; dragging = false;
        if (ctx.Closing) return;
        if (Math.Abs(Left - dragLeft) < 1 && Math.Abs(Top - dragTop) < 1) return;
        var screen = Display.PrimaryScreen; var x = Left - screen.WorkingArea.Left / screen.Scale; var y = Top - screen.WorkingArea.Top / screen.Scale;
        savingPosition = true;
        try { await ctx.SaveCalendarPosition(x, y); }
        catch (Exception ex) { Design.Error(ex); }
        finally { savingPosition = false; if (!ctx.Closing) Position(); }
    }
    private Task ResetPosition() => ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { CalendarX = null, CalendarY = null }));
    internal void SelectDate(DateOnly date) { Selected = date; selectedPeriod = null; Refresh(); }
    private void Navigate(int offset) { if (view == "week") { SelectDate(Timetable.ShiftWeek(Selected, offset)); return; } var month = (Selected.Year - 1) * 12 + Selected.Month - 1 + offset; if (month >= 0 && month < 9999 * 12) SelectDate(Selected.AddMonths(offset)); }
    private void Refresh()
    {
        snapshot = ctx.Db.Current.Tasks.ToArray(); categories = ctx.Db.Current.TaskCategories.ToArray();
        view = ctx.Preferences.CalendarView;
        month.Text = view == "week" ? $"{Timetable.WeekStart(Selected):M/d} – {Timetable.WeekDays(Selected).Last():M/d} · 课表" : Selected.ToString("yyyy年 M月"); heading.Text = Selected.ToString("M月d日 dddd");
        var date = Selected.ToString("yyyy-MM-dd"); var rows = ctx.Calendar.Query(date).ToArray();
        summary.Text = rows.Length == 0 ? "当天没有日程，点击“＋ 日程”添加" : $"{rows.Count(t => !t.Completed)} 项待办 · {rows.Count(t => t.Completed)} 项已完成";
        var selectedId = (Agenda.SelectedItem as CalendarTask)?.Id; Agenda.ItemsSource = rows; Agenda.SelectedItem = rows.FirstOrDefault(t => t.Id == selectedId);
        var counts = snapshot.Where(t => !t.Completed).GroupBy(t => t.Date).ToDictionary(g => g.Key, g => g.Count());
        days.Children.Clear(); foreach (var text in new[] { "一", "二", "三", "四", "五", "六", "日" }) { var label = V2Ui.Label(text, 11, "Muted"); label.TextAlignment = TextAlignment.Center; days.Children.Add(label); }
        var first = new DateOnly(Selected.Year, Selected.Month, 1); first = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        for (var i = 0; i < 42; i++)
        {
            if (first.DayNumber + i > DateOnly.MaxValue.DayNumber) { days.Children.Add(new Border()); continue; }
            var value = first.AddDays(i); var count = counts.GetValueOrDefault(value.ToString("yyyy-MM-dd"));
            var label = V2Ui.Label(value.Day + (count > 0 ? " •" : ""), 12, value == Selected || value == today ? "Accent" : "Ink");
            var button = V2Ui.Button("", () => SelectDate(value)); button.Content = label; button.Tag = value; button.MinWidth = 0; button.MinHeight = 0; button.Padding = new(1); button.Margin = new(1); button.Opacity = value.Month == Selected.Month ? 1 : .42;
            if (value == Selected) button.SetResourceReference(Control.BackgroundProperty, "Selected");
            button.ToolTip = value.ToString("yyyy-MM-dd") + (count > 0 ? $" · {count} 项待办" : ""); days.Children.Add(button);
        }
        layout.RowDefinitions[2].Height = view == "week" ? new(1, GridUnitType.Star) : new(150);
        layout.RowDefinitions[4].Height = view == "week" ? new(120) : new(1, GridUnitType.Star);
        calendarHost.Content = view == "week" ? BuildTimetable() : days;
        Position();
    }
    private UIElement BuildTimetable()
    {
        const string format = "GlassBogo.Timetable.Task";
        var grid = new Grid { MinWidth = 750, MinHeight = 304 };
        grid.ColumnDefinitions.Add(new() { Width = new(76) }); for (var i = 0; i < 7; i++) grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = new(38) }); for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var week = Timetable.WeekDays(Selected);
        void Add(UIElement element, int row, int column) { Grid.SetRow(element, row); Grid.SetColumn(element, column); grid.Children.Add(element); }
        Add(V2Ui.Label("时间段", 12, "Muted"), 0, 0);
        for (var i = 0; i < week.Count; i++)
        {
            var date = week[i]; var label = new StackPanel(); label.Children.Add(V2Ui.Label("周" + "一二三四五六日"[i], 12, date == today ? "Accent" : "Ink")); label.Children.Add(V2Ui.Label(date.ToString("M/d"), 11, "Muted"));
            var button = Compact(V2Ui.Button("", () => SelectDate(date))); button.Content = label; if (date == Selected) button.SetResourceReference(Control.BackgroundProperty, "Selected"); Add(button, 0, i + 1);
        }
        var groups = snapshot.Select(t => (Task: t, Period: Timetable.PeriodForTime(t.Time))).Where(x => x.Period.HasValue).GroupBy(x => (x.Task.Date, x.Period!.Value)).ToDictionary(g => g.Key, g => g.Select(x => x.Task).OrderBy(t => t.Completed).ThenBy(t => t.Time).ThenBy(t => t.Order).ToArray());
        foreach (var slot in Timetable.Slots)
        {
            var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; label.Children.Add(V2Ui.Label(slot.Label, 12)); label.Children.Add(V2Ui.Label(slot.Start, 11, "Muted")); label.Children.Add(V2Ui.Label("— " + slot.End, 11, "Muted")); Add(label, slot.Index + 1, 0);
            for (var i = 0; i < week.Count; i++)
            {
                var date = week[i]; var contents = new StackPanel();
                var cell = new Border { Margin = new(1), Padding = new(3), CornerRadius = new(8), BorderThickness = new(1), AllowDrop = true, Tag = new LessonCell(date, slot.Index) };
                cell.SetResourceReference(Border.BorderBrushProperty, "Line"); cell.SetResourceReference(Border.BackgroundProperty, date == Selected && slot.Index == selectedPeriod ? "Selected" : "Inset");
                foreach (var task in groups.GetValueOrDefault((date.ToString("yyyy-MM-dd"), slot.Index), []))
                {
                    var course = Compact(V2Ui.Button("", () => Edit(task))); course.Tag = task; course.ToolTip = task.Title + "\n" + task.Time + (task.Notes.Length > 0 ? "\n" + task.Notes : "");
                    var text = new StackPanel(); var title = V2Ui.Label(task.Title, 12); title.TextWrapping = TextWrapping.Wrap; title.TextTrimming = TextTrimming.CharacterEllipsis; if (task.Completed) title.TextDecorations = TextDecorations.Strikethrough; text.Children.Add(title);
                    var category = categories.FirstOrDefault(c => c.Id == task.CategoryId);
                    text.Children.Add(V2Ui.Label((category?.Name ?? "未分类") + (task.Priority == "high" ? " · 重要" : "") + (task.Completed ? " · 已完成" : ""), 10, "Muted")); course.Content = text; course.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                    if (category is not null) { var color = (Color)ColorConverter.ConvertFromString(category.Color); course.Background = new SolidColorBrush(color) { Opacity = .18 }; course.BorderBrush = new SolidColorBrush(color) { Opacity = .65 }; }
                    var menu = V2Ui.Menu(ctx); V2Ui.Command(menu, task.Completed ? "标记未完成" : "标记完成", () => ctx.Write(() => { var current = ctx.Db.Current.Tasks.FirstOrDefault(t => t.Id == task.Id) ?? throw new InvalidOperationException("课程已被删除。"); ctx.Calendar.Save(current with { Completed = !current.Completed }); })); V2Ui.Command(menu, "删除课程…", () => { Remove(task); return Task.CompletedTask; }); course.ContextMenu = menu;
                    Point start = default; course.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(course);
                    course.PreviewMouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && (e.GetPosition(course) - start).Length > 8) { e.Handled = true; V2Ui.Modal(ctx, () => DragDrop.DoDragDrop(course, new DataObject(format, task.Id), DragDropEffects.Move)); } };
                    contents.Children.Add(course);
                }
                var add = Compact(V2Ui.Button("＋", () => Edit(date: date, initialTime: slot.Start))); add.ToolTip = $"添加 {date:yyyy-MM-dd} {slot.Label} {slot.Start}"; contents.Children.Add(add);
                cell.Child = new ScrollViewer { Content = contents, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                cell.MouseLeftButtonDown += (_, e) => { if (FromControl(e.OriginalSource as DependencyObject)) return; Selected = date; selectedPeriod = slot.Index; if (e.ClickCount == 2) Edit(date: date, initialTime: slot.Start); else Refresh(); };
                cell.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(format) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
                cell.Drop += async (_, e) => { if (e.Data.GetData(format) is not string id) return; e.Handled = true; try { await MoveCourse(id, date, slot.Index); } catch (Exception ex) { summary.Text = "未移动：" + ex.Message; } };
                Add(cell, slot.Index + 1, i + 1);
            }
        }
        var panel = new DockPanel(); var hint = V2Ui.Label("每周独立 · 拖动课程改日期和课时 · 其他时间安排见下方当天列表", 11, "Muted"); DockPanel.SetDock(hint, Dock.Bottom); panel.Children.Add(hint);
        panel.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); return panel;
    }
    private static bool FromControl(DependencyObject? element)
    {
        for (var current = element; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current)) if (current is ButtonBase or Thumb or ScrollBar) return true;
        return false;
    }
    internal Task MoveCourse(string id, DateOnly date, int period)
    {
        if (period < 0 || period >= Timetable.Slots.Count) throw new ArgumentOutOfRangeException(nameof(period));
        return ctx.Write(() => { var task = ctx.Db.Current.Tasks.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("课程已被删除。"); ctx.Calendar.Save(task with { Date = date.ToString("yyyy-MM-dd"), Time = Timetable.Slots[period].Start }); });
    }
    private UIElement TaskRow(CalendarTask task)
    {
        var row = new DockPanel { Margin = new(0, 2, 0, 2) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(Compact(V2Ui.Button("编辑", () => Edit(task)))); actions.Children.Add(Compact(V2Ui.Button("删除", () => Remove(task))));
        DockPanel.SetDock(actions, Dock.Right); row.Children.Add(actions);
        var complete = new CheckBox { IsChecked = task.Completed, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0), ToolTip = "标记完成／未完成" };
        complete.Click += async (_, _) =>
        {
            var done = complete.IsChecked == true;
            complete.IsEnabled = false;
            try { await ctx.Write(() => { var current = ctx.Db.Current.Tasks.FirstOrDefault(t => t.Id == task.Id) ?? throw new InvalidOperationException("日程已被删除。"); ctx.Calendar.Save(current with { Completed = done }); }); }
            catch (Exception ex) { complete.IsChecked = task.Completed; Design.Error(ex); }
            finally { complete.IsEnabled = true; }
        };
        row.Children.Add(complete);
        var content = new StackPanel(); var title = V2Ui.Label(task.Title, 13); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; if (task.Completed) title.TextDecorations = TextDecorations.Strikethrough; content.Children.Add(title);
        content.Children.Add(V2Ui.Label((task.Time.Length == 0 ? "全天" : task.Time) + (task.Priority == "high" ? " · 重要" : "") + (categories.FirstOrDefault(c => c.Id == task.CategoryId) is { } category ? " · " + category.Name : ""), 11, "Muted"));
        row.Children.Add(content); return row;
    }
    internal void Edit(CalendarTask? task = null, DateOnly? date = null, string initialTime = "") => V2Ui.Modal(ctx, () => new ScheduleEditorWindow(this, ctx, task, date ?? Selected, SelectDate, initialTime).ShowDialog());
    internal void Remove(CalendarTask task)
    {
        var dialog = new V2Dialog(this, ctx, "删除日程", 380, 210); dialog.Main.Content = V2Ui.Label("删除“" + task.Title + "”？", 14);
        dialog.Actions.Children.Add(V2Ui.Async("删除", async () =>
        {
            dialog.SetBusy(true); try { await ctx.Write(() => ctx.Calendar.Remove(task.Id)); dialog.Complete(); }
            finally { dialog.SetBusy(false); }
        }));
        V2Ui.Modal(ctx, () => dialog.ShowDialog());
    }
}
