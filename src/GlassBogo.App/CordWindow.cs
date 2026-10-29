using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBogo.App;

/// <summary>
/// 顶部拉绳：平时只是屏幕正上方的一条细线，鼠标碰到 120 ms 后拉出日历面板，
/// 离开拉绳和面板 400 ms 后收回；前台是全屏窗口时自动隐藏。
/// </summary>
internal sealed class CordWindow : GlassWindow
{
    private const double HotWidth = 220;
    private const double HotHeight = 18;

    private readonly AppContext ctx;
    private readonly DispatcherTimer openTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer environmentTimer = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly Border line = new() { Width = 40, Height = 6, CornerRadius = new(3), Opacity = .38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    private readonly Native.WinEventProc foregroundCallback;
    private nint hook;
    private CalendarPanelWindow? panel;

    internal CordWindow(AppContext context)
    {
        ctx = context;
        Title = "GlassBogo 拉绳";
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        MinHeight = 0;
        SetInsets(0);
        line.SetResourceReference(Border.BackgroundProperty, "Ink");
        SetBody(line);
        SetSurfaceHidden(true);
        MouseEnter += (_, _) => { closeTimer.Stop(); openTimer.Stop(); openTimer.Start(); };
        MouseLeave += (_, _) => ScheduleClose();
        openTimer.Tick += (_, _) => { openTimer.Stop(); if (IsMouseOver) Pull(); };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer.Stop();
            if (IsMouseOver || panel is { IsMouseOver: true } || panel is { IsKeyboardFocusWithin: true })
                return;
            Push();
        };
        foregroundCallback = (_, ev, _, _, _, _, _) =>
        {
            if (ev != 3)
                return;
            Dispatcher.BeginInvoke(() => { environmentTimer.Stop(); environmentTimer.Start(); });
        };
        environmentTimer.Tick += (_, _) => { environmentTimer.Stop(); AssessForeground(); };
        Loaded += (_, _) =>
        {
            Place();
            hook = Native.SetWinEventHook(3, 3, 0, foregroundCallback, 0, 0, 2);
        };
        SystemEvents.DisplaySettingsChanged += DisplaySettings;
        Closed += (_, _) =>
        {
            openTimer.Stop();
            closeTimer.Stop();
            environmentTimer.Stop();
            Native.UnhookWinEvent(hook);
            SystemEvents.DisplaySettingsChanged -= DisplaySettings;
            panel?.Close();
        };
    }

    private void DisplaySettings(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Place);

    private void Place()
    {
        var screen = Display.PrimaryScreen!;
        var scale = screen.Scale;
        var work = screen.WorkingArea;
        var width = (int)Math.Round(HotWidth * scale);
        var height = (int)Math.Round(HotHeight * scale);
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (int)Math.Round(6 * scale);
        Width = width / scale;
        Height = height / scale;
        Left = left / scale;
        Top = top / scale;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            Native.SetWindowPos(handle, 0, left, top, width, height, 0x14);
    }

    private void AssessForeground()
    {
        if (ctx.Closing)
            return;
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == new WindowInteropHelper(this).Handle)
            return;
        if (panel is not null && hwnd == new WindowInteropHelper(panel).Handle)
            return;
        var cls = new System.Text.StringBuilder(128);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        var desktop = cls.ToString() is "Progman" or "WorkerW";
        var bounds = Display.PrimaryScreen!.Bounds;
        var full = !desktop && Native.GetWindowRect(hwnd, out var rect)
            && rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
        if (full)
        {
            panel?.Hide();
            if (IsVisible)
                Hide();
        }
        else if (!IsVisible)
        {
            Show();
            Place();
        }
    }

    private void Pull()
    {
        panel ??= new CalendarPanelWindow(ctx, ScheduleClose);
        panel.PlaceUnder();
        if (!panel.IsVisible)
            panel.Show();
        panel.Refresh();
    }

    private void Push()
    {
        if (panel is null || !panel.IsVisible)
            return;
        if (panel.IsKeyboardFocusWithin || panel.HasModalInput)
            return;
        panel.Hide();
    }

    private void ScheduleClose()
    {
        closeTimer.Stop();
        closeTimer.Start();
    }

    internal void Toggle()
    {
        if (panel is { IsVisible: true })
            Push();
        else
            Pull();
    }

    internal CalendarPanelWindow? Panel => panel;

    internal void QaVerify()
    {
        if (ActualWidth > 320 || ActualHeight > 40)
            throw new InvalidOperationException("拉绳热区过大。");
        Pull();
        if (panel is not { IsVisible: true })
            throw new InvalidOperationException("拉绳没有拉出日历面板。");
        if (panel.Top > PanelTopLimit())
            throw new InvalidOperationException("日历面板没有贴在屏幕顶部。");
        var texts = Walk(panel).OfType<TextBlock>().Select(t => t.Text).ToArray();
        if (!texts.Any(t => t.StartsWith("今天", StringComparison.Ordinal) || t.StartsWith("10月", StringComparison.Ordinal) || t.Contains('月')))
            throw new InvalidOperationException("日历面板没有显示日期标题。");
        var dayCells = Walk(panel).OfType<UniformGrid>().Where(g => g.Columns == 7).SelectMany(g => g.Children.OfType<Button>()).ToArray();
        if (dayCells.Length != 42)
            throw new InvalidOperationException("迷你月历没有 42 个日期格。");
        var todayKey = DateTime.Today.ToString("yyyy-MM-dd");
        var other = dayCells.Select(b => (button: b, date: b.ToolTip as string)).FirstOrDefault(x => x.date is not null && x.date != todayKey);
        if (other.button is null)
            throw new InvalidOperationException("迷你月历没有可点击的其他日期。");
        other.button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var expected = DateOnly.Parse(other.date!).ToString("MM月dd日");
        if (!Walk(panel).OfType<TextBlock>().Any(t => t.Text.StartsWith(expected, StringComparison.Ordinal)))
            throw new InvalidOperationException("选择日期后标题没有更新。");
        Push();
        if (panel is { IsVisible: true })
            throw new InvalidOperationException("日历面板没有收回。");
    }

    private double PanelTopLimit()
    {
        var screen = Display.PrimaryScreen!;
        return screen.WorkingArea.Top / screen.Scale + 80;
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
}

/// <summary>拉绳拉出的紧凑日历：迷你月历 + 选中日期的任务，可勾选、可速记，也能跳完整日历。</summary>
internal sealed class CalendarPanelWindow : GlassWindow
{
    private readonly AppContext ctx;
    private readonly Action onLeave;
    private readonly TextBlock heading = Design.Text("", 17);
    private readonly TextBlock summary = Design.Text("", 12, "Muted");
    private readonly TextBlock monthLabel = Design.Text("", 13, "Muted");
    private readonly UniformGrid days = new() { Columns = 7, Rows = 6 };
    private readonly StackPanel tasks = new();
    private DateOnly month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateOnly selected = DateOnly.FromDateTime(DateTime.Today);
    internal bool HasModalInput { get; private set; }

    internal CalendarPanelWindow(AppContext context, Action onLeave)
    {
        ctx = context;
        this.onLeave = onLeave;
        Title = "GlassBogo · 今日";
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 460;
        Height = 560;
        SetRadius(22);
        SetInsets(18);
        var root = new DockPanel();
        var header = new StackPanel { Margin = new(0, 0, 0, 8) };
        header.Children.Add(heading);
        header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var actions = new WrapPanel { Margin = new(0, 10, 0, 0) };
        actions.Children.Add(Design.Button("快速记一件事", () => WithModal(() => CalendarWindow.EditTask(ctx, this, null, true))));
        actions.Children.Add(Design.Button("完整日历", () => { Hide(); ctx.OpenCalendar(); }));
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
        var monthBar = Design.Row(
            Design.Button("‹", () => { month = month.AddMonths(-1); Refresh(); }),
            monthLabel,
            Design.Button("›", () => { month = month.AddMonths(1); Refresh(); }),
            Design.Button("今天", () => { selected = DateOnly.FromDateTime(DateTime.Today); month = new(selected.Year, selected.Month, 1); Refresh(); }));
        DockPanel.SetDock(monthBar, Dock.Top);
        root.Children.Add(monthBar);
        var weekday = new UniformGrid { Columns = 7, Margin = new(0, 2, 0, 2) };
        foreach (var label in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            var text = Design.Text(label, 10, "Muted");
            text.HorizontalAlignment = HorizontalAlignment.Center;
            weekday.Children.Add(text);
        }
        DockPanel.SetDock(weekday, Dock.Top);
        root.Children.Add(weekday);
        var dayHost = new Border { Padding = new(2), Margin = new(0, 0, 0, 8) };
        dayHost.SetResourceReference(Border.BackgroundProperty, "Inset");
        dayHost.CornerRadius = new(12);
        dayHost.Child = days;
        DockPanel.SetDock(dayHost, Dock.Top);
        root.Children.Add(dayHost);
        root.Children.Add(new ScrollViewer { Content = tasks, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        SetBody(root);
        MouseLeave += (_, _) => onLeave();
        LostKeyboardFocus += (_, _) => Dispatcher.BeginInvoke(onLeave);
        ctx.Db.Changed += OnData;
        Closed += (_, _) => ctx.Db.Changed -= OnData;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        Refresh();
    }

    private void OnData(object? sender, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void WithModal(Action action)
    {
        HasModalInput = true;
        try { action(); }
        finally { HasModalInput = false; }
    }

    internal void Refresh()
    {
        if (!IsVisible && IsLoaded)
            return;
        monthLabel.Text = $"{month.Year} 年 {month.Month} 月";
        BuildDays();
        var key = selected.ToString("yyyy-MM-dd");
        var items = ctx.Calendar.Query(key).ToArray();
        var isToday = selected == DateOnly.FromDateTime(DateTime.Today);
        heading.Text = $"{selected:MM月dd日} · {WeekdayOf(selected)}" + (isToday ? " · 今天" : "");
        summary.Text = items.Length == 0 ? "这一天还没有安排。" : $"{items.Count(t => !t.Completed)} 项待办 · {items.Count(t => t.Completed)} 项已完成";
        tasks.Children.Clear();
        foreach (var task in items.Take(10))
        {
            var check = new CheckBox { Content = $"{task.Time}  {task.Title}".Trim(), IsChecked = task.Completed, Margin = new(2, 4, 0, 4) };
            check.Click += (_, _) => Design.Guard(() => ctx.Calendar.Save(task with { Completed = check.IsChecked == true }));
            tasks.Children.Add(check);
        }
        if (items.Length == 0)
            tasks.Children.Add(Design.Text("碰一下拉绳就能看今天；按「快速记一件事」用中文速记添加。", 13, "Muted"));
        Design.Reveal(tasks);
    }

    private void BuildDays()
    {
        days.Children.Clear();
        var offset = ((int)month.DayOfWeek + 6) % 7;
        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var index = 0; index < 42; index++)
        {
            var date = month.AddDays(index - offset);
            var label = Design.Text(date.Day.ToString(), 12);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            if (date.Month != month.Month)
                label.Opacity = .35;
            if (date == today)
            {
                label.FontWeight = FontWeights.Bold;
                label.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            }
            var button = new Button { Content = label, Padding = new(2), Margin = new(1), MinWidth = 0, ToolTip = date.ToString("yyyy-MM-dd") };
            button.SetResourceReference(FrameworkElement.StyleProperty, "QuietButton");
            if (date == selected)
                button.SetResourceReference(Control.BackgroundProperty, "Selected");
            var target = date;
            button.Click += (_, _) => { selected = target; Refresh(); };
            days.Children.Add(button);
        }
    }

    private static string WeekdayOf(DateOnly date) => "星期" + "一二三四五六日"[(int)date.DayOfWeek is 0 ? 6 : (int)date.DayOfWeek - 1];

    internal void PlaceUnder()
    {
        var screen = Display.PrimaryScreen!;
        var scale = screen.Scale;
        var work = screen.WorkingArea;
        var width = (int)Math.Round(460 * scale);
        var height = Math.Min(work.Height, (int)Math.Round(560 * scale));
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (int)Math.Round(28 * scale);
        Width = width / scale;
        Height = height / scale;
        Left = left / scale;
        Top = top / scale;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != 0)
            Native.SetWindowPos(handle, 0, left, top, width, height, 0x14);
    }
}
