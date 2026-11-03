namespace GlassBogo.App;

internal sealed class ScheduleEditorWindow : V2Dialog
{
    internal readonly TextBox TitleInput;
    internal readonly DatePicker DateInput;
    internal readonly TextBox TimeInput, NotesInput;
    internal readonly CheckBox Completed;
    private readonly ComboBox priority, category;
    private readonly TextBlock error = V2Ui.Label("", 12, "Warning");
    internal ScheduleEditorWindow(Window owner, V2Context ctx, CalendarTask? existing, DateOnly date, Action<DateOnly> saved, string initialTime = "") : base(owner, ctx, existing is null ? "新增日程" : "编辑日程", 470, 550)
    {
        var task = existing ?? new CalendarTask(Guid.NewGuid().ToString("N"), "", date.ToString("yyyy-MM-dd"), initialTime, Order: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var body = new StackPanel();
        TitleInput = new() { Text = task.Title, MaxLength = 200, Margin = new(0, 4, 0, 10) }; body.Children.Add(V2Ui.Label("日程标题")); body.Children.Add(TitleInput);
        DateInput = new() { SelectedDate = DateTime.Parse(task.Date), Margin = new(0, 4, 0, 10) }; body.Children.Add(V2Ui.Label("日期")); body.Children.Add(DateInput);
        TimeInput = new() { Text = task.Time, ToolTip = "HH:mm，留空表示全天", Margin = new(0, 4, 0, 10) }; body.Children.Add(V2Ui.Label("时间（HH:mm，留空表示全天）", 12)); body.Children.Add(TimeInput);
        priority = new() { ItemsSource = new[] { new Choice("normal", "普通"), new Choice("high", "重要"), new Choice("low", "较低") }, DisplayMemberPath = "Label", SelectedValuePath = "Value", SelectedValue = task.Priority, Margin = new(0, 4, 0, 8) }; body.Children.Add(V2Ui.Label("优先级", 12)); body.Children.Add(priority);
        category = new() { ItemsSource = new[] { new TaskCategory("", "未分类", "#808080") }.Concat(ctx.Db.Current.TaskCategories).ToArray(), DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = task.CategoryId ?? "", Margin = new(0, 4, 0, 8) }; body.Children.Add(V2Ui.Label("日程分类", 12)); body.Children.Add(category);
        NotesInput = new() { Text = task.Notes, AcceptsReturn = true, Height = 64, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; body.Children.Add(V2Ui.Label("备注", 12)); body.Children.Add(NotesInput);
        Completed = new() { Content = "已完成", IsChecked = task.Completed, Margin = new(0, 8, 0, 6) }; body.Children.Add(Completed);
        var content = new DockPanel(); DockPanel.SetDock(error, Dock.Bottom); content.Children.Add(error); content.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }); Main.Content = content;
        var save = V2Ui.Async("保存日程", async () =>
        {
            var title = TitleInput.Text.Trim(); var time = TimeInput.Text.Trim();
            if (title.Length == 0) { error.Text = "请输入日程标题。"; return; }
            if (DateInput.SelectedDate is not { } picked) { error.Text = "请选择有效日期。"; return; }
            if (time.Length > 0 && !TimeOnly.TryParseExact(time, "HH:mm", out _)) { error.Text = "时间请填写 HH:mm，例如 09:30；全天可留空。"; return; }
            var chosenDate = DateOnly.FromDateTime(picked); var notes = NotesInput.Text; var done = Completed.IsChecked == true; var level = priority.SelectedValue as string ?? "normal"; var group = category.SelectedValue as string;
            SetBusy(true);
            try
            {
                await ctx.Write(() =>
                {
                    var current = existing is null ? task : ctx.Db.Current.Tasks.FirstOrDefault(t => t.Id == task.Id) ?? throw new InvalidOperationException("日程已被删除，请重新添加。");
                    ctx.Calendar.Save(current with { Title = title, Date = chosenDate.ToString("yyyy-MM-dd"), Time = time, Notes = notes, Completed = done, Priority = level, CategoryId = string.IsNullOrEmpty(group) ? null : group });
                });
                if (!ctx.Closing) saved(chosenDate); Complete();
            }
            catch (Exception ex) { error.Text = "未保存：" + ex.Message; }
            finally { SetBusy(false); }
        });
        save.IsDefault = true; Actions.Children.Add(save); Loaded += (_, _) => { TitleInput.Focus(); TitleInput.SelectAll(); };
    }
}
