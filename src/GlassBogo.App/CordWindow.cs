using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBogo.App;

/// <summary>
/// 顶部拉绳：平时只是屏幕正上方的一条细线，鼠标碰到 120 ms 后拉出日历面板，
/// 离开拉绳和面板 400 ms 后收回。面板复用 CalendarService 的同一份 SQLite 数据。
/// </summary>
internal sealed class CordWindow : GlassWindow
{
    private const double LineWidth = 40;
    private const double LineHeight = 6;
    private const double HotWidth = 220;
    private const double HotHeight = 18;

    private readonly AppContext ctx;
    private readonly DispatcherTimer openTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer closeTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly Border line = new() { Width = LineWidth, Height = LineHeight, CornerRadius = new(3), Opacity = .38, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
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
        Loaded += (_, _) => Place();
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(Place);
        Closed += (_, _) => { openTimer.Stop(); closeTimer.Stop(); panel?.Close(); };
    }

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
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != 0)
            Native.SetWindowPos(handle, 0, left, top, width, height, 0x14);
    }

    private void Pull()
    {
        panel ??= new CalendarPanelWindow(ctx, () => ScheduleClose());
        panel.PlaceUnder(this);
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
        if (Walk(panel).OfType<TextBlock>().All(t => !t.Text.StartsWith("今天", StringComparison.Ordinal)))
            throw new InvalidOperationException("日历面板没有显示今天的标题。");
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

/// <summary>拉绳拉出的紧凑日历面板：今日任务可勾选、可速记，也能跳完整日历。</summary>
internal sealed class CalendarPanelWindow : GlassWindow
{
    private readonly AppContext ctx;
    private readonly Action onLeave;
    private readonly TextBlock heading = Design.Text("", 18);
    private readonly TextBlock summary = Design.Text("", 12, "Muted");
    private readonly StackPanel tasks = new();
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
        Height = 420;
        SetRadius(22);
        SetInsets(18);
        var root = new DockPanel();
        var header = new StackPanel { Margin = new(0, 0, 0, 10) };
        header.Children.Add(heading);
        header.Children.Add(summary);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var actions = new WrapPanel { Margin = new(0, 10, 0, 0) };
        actions.Children.Add(Design.Button("快速记一件事", () => WithModal(() => CalendarWindow.EditTask(ctx, this, null, true))));
        actions.Children.Add(Design.Button("添加今日任务", () => WithModal(() => CalendarWindow.EditTask(ctx, this, new(Guid.NewGuid().ToString("N"), "", DateTime.Today.ToString("yyyy-MM-dd"))))));
        actions.Children.Add(Design.Button("完整日历", () => { Hide(); ctx.OpenCalendar(); }));
        DockPanel.SetDock(actions, Dock.Bottom);
        root.Children.Add(actions);
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
        var today = ctx.Calendar.Query(DateTime.Today.ToString("yyyy-MM-dd")).ToArray();
        heading.Text = $"今天 · {DateTime.Today:MM月dd日}";
        summary.Text = today.Length == 0 ? "今天还没有安排。" : $"{today.Count(t => !t.Completed)} 项待办 · {today.Count(t => t.Completed)} 项已完成";
        tasks.Children.Clear();
        foreach (var task in today.Take(10))
        {
            var check = new CheckBox { Content = $"{task.Time}  {task.Title}".Trim(), IsChecked = task.Completed, Margin = new(2, 4, 0, 4), IsThreeState = false };
            check.Click += (_, _) => Design.Guard(() => ctx.Calendar.Save(task with { Completed = check.IsChecked == true }));
            tasks.Children.Add(check);
        }
        if (today.Length == 0)
            tasks.Children.Add(Design.Text("碰一下拉绳就能看今天；按「快速记一件事」用中文速记添加。", 13, "Muted"));
        Design.Reveal(tasks);
    }

    internal void PlaceUnder(Window cord)
    {
        var screen = Display.PrimaryScreen!;
        var scale = screen.Scale;
        var work = screen.WorkingArea;
        var width = (int)Math.Round(460 * scale);
        var height = Math.Min(work.Height, (int)Math.Round(420 * scale));
        var left = work.Left + (work.Width - width) / 2;
        var top = work.Top + (int)Math.Round(28 * scale);
        Width = width / scale;
        Height = height / scale;
        Left = left / scale;
        Top = top / scale;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (handle != 0)
            Native.SetWindowPos(handle, 0, left, top, width, height, 0x14);
    }
}
