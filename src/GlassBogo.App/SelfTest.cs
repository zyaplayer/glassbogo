using System.Diagnostics;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace GlassBogo.App;

internal static class SelfTest
{
    internal static void Run(string output, int stressSeconds)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = Environment.GetEnvironmentVariable("GLASSBOGO_HARDWARE_RENDERING") == "1" ? System.Windows.Interop.RenderMode.Default : System.Windows.Interop.RenderMode.SoftwareOnly;
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var log = Path.Combine(output, "events.log");
        void Log(string s) => File.AppendAllText(log, DateTime.UtcNow.ToString("O") + " " + s + Environment.NewLine);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Log(e.Exception.ToString()); e.Handled = true; app.Shutdown(1); };
        try
        {
            DesktopVisibilityTest.RequireIsolatedDesktop();
            using var ctx = new AppContext(Path.Combine(output, "data"));
            ctx.Db.Update(s => { s.Preferences = s.Preferences with { Onboarded = true }; if (s.Entries.Count > 0) return; for (int c = 0; c < 8; c++) s.Categories.Add(new("cat" + c, new[] { "办公", "开发", "创作", "工具", "学习", "生活", "文档", "常用" }[c])); for (int i = 0; i < 500; i++) s.Entries.Add(new(i.ToString(), "示例应用 " + i, @"C:\Windows\notepad.exe", Arguments: i.ToString(), CategoryId: "cat" + (i % 8))); for (int i = 0; i < 10000; i++) s.Tasks.Add(new(i.ToString(), "测试任务 " + i, DateTime.Today.AddDays(i % 365).ToString("yyyy-MM-dd"), CategoryId: "work")); });
            Design.Apply("light");
            var dock = new DockWindow(ctx);
            ctx.Dock = dock;
            dock.Loaded += async (_, _) =>
            {
                try
                {
                    await Task.Delay(1500);
                    dock.QaVerifyIcons();
                    Log("dock loaded; preview icons verified");
                    var motionButton = Walk(dock).OfType<Button>().First();
                    motionButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseEnterEvent });
                    var motion = (TransformGroup)motionButton.RenderTransform;
                    if (SystemParameters.ClientAreaAnimation && !motion.Children.Any(t => t.HasAnimatedProperties))
                        throw new InvalidOperationException("悬浮动画未启动。");
                    await Task.Delay(300);
                    if (motion.Children.Any(t => t.HasAnimatedProperties))
                        throw new InvalidOperationException("悬浮动画结束后时钟未释放。");
                    motionButton.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
                    await Task.Delay(300);
                    if (motion.Children.OfType<TranslateTransform>().Single().Y != 0)
                        throw new InvalidOperationException("鼠标离开后位移未归零。");
                    Log("hover motion starts/stops and returns to rest; system animations=" + SystemParameters.ClientAreaAnimation);
                    dock.QaVerifyNavigation();
                    ctx.LocateEntries([ctx.Db.Current.Entries[0]]);
                    await Task.Delay(300);
                    var located = Application.Current.Windows.OfType<WorkspaceWindow>().Single();
                    if (!located.QaSelectedIds().SequenceEqual(new[] { ctx.Db.Current.Entries[0].Id }))
                        throw new Exception("重复入口定位未选中现有入口。");
                    located.Close();
                    located = null;
                    Log("category paging/keyboard/cache and duplicate-entry location verified");
                    _ = dock.Dispatcher.BeginInvoke(() =>
                    {
                        var prompt = Application.Current.Windows.Cast<Window>().Single(w => w.Title == "QA 保存失败");
                        var input = Walk(prompt).OfType<TextBox>().Single();
                        input.Text = "需要保留的分类名称";
                        Walk(prompt).OfType<Button>().Single(b => b.Content as string == "保存").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (!prompt.IsVisible || input.Text != "需要保留的分类名称" || !Walk(prompt).OfType<TextBlock>().Any(t => t.Text.Contains("模拟写入失败")))
                            throw new InvalidOperationException("保存失败后输入内容未保留。");
                        prompt.Close();
                    });
                    Design.Prompt(dock, "QA 保存失败", save: _ => throw new IOException("模拟写入失败"));
                    Log("category save failure preserves editor and input");
                    DesktopVisibilityTest.Verify(ctx.Desktop, ctx.Db.DirectoryPath);
                    Log("desktop hide/restore/original-hidden/crash-recovery passed");
                    Snapshot(dock, Path.Combine(output, "dock.png"));
                    dock.QaCycle(2);
                    await Task.Delay(500);
                    var popup = Application.Current.Windows.Cast<Window>().First(w => w.Title == "GlassBogo 分类");
                    Snapshot(popup, Path.Combine(output, "category.png"));
                    dock.QaHide();
                    var workspace = new WorkspaceWindow(ctx);
                    var pluginPath = Environment.GetEnvironmentVariable("GLASSBOGO_QA_PLUGIN");
                    if (!string.IsNullOrEmpty(pluginPath))
                    {
                        var m = ctx.Plugins.Inspect(pluginPath);
                        if (!ctx.Plugins.List().Any())
                            ctx.Plugins.Install(pluginPath);
                        ctx.Db.Update(s => s.Plugins[m.Id] = true);
                        workspace.Close();
                        workspace = new WorkspaceWindow(ctx);
                    }
                    workspace.Show();
                    await Task.Delay(700);
                    if (!string.IsNullOrEmpty(pluginPath))
                    {
                        if (PluginSession.ActiveCount != 1)
                            throw new Exception("插件卡片未建立会话。");
                        var action = Walk(workspace).OfType<Button>().First(b => b.Content as string == "完成一次专注");
                        action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        await Task.Delay(300);
                        ctx.Db.Update(s => s.Preferences = s.Preferences with { Theme = "light" });
                        await Task.Delay(500);
                        if (!Walk(workspace).OfType<TextBlock>().Any(t => t.Text.Contains("已完成 1 次")))
                            throw new Exception("插件卡片在数据刷新后丢失会话状态。");
                    }
                    Snapshot(workspace, Path.Combine(output, "workspace.png"));
                    Design.Apply("dark");
                    await Task.Delay(200);
                    Snapshot(workspace, Path.Combine(output, "workspace-dark.png"));
                    Design.Apply("light");
                    var navigate = typeof(WorkspaceWindow).GetMethod("ShowPage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                    foreach (var page in new[] { "entries", "settings", "plugins", "home", "entries" })
                    {
                        navigate.Invoke(workspace, [page]);
                        await Task.Delay(500);
                        Snapshot(workspace, Path.Combine(output, page + ".png"));
                        Log(page + " rendered");
                    }
                    workspace.Close();
                    workspace = null;
                    await Task.Delay(300);
                    if (PluginSession.ActiveCount != 0)
                        throw new Exception("关闭工作台后插件进程未释放。");
                    var calendar = new CalendarWindow(ctx);
                    calendar.Show();
                    await Task.Delay(500);
                    Snapshot(calendar, Path.Combine(output, "calendar.png"));
                    calendar.QaToggleView();
                    await Task.Delay(200);
                    Snapshot(calendar, Path.Combine(output, "timetable.png"));
                    var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                    watchdog.Tick += (_, _) => { watchdog.Stop(); Application.Current.Windows.Cast<Window>().FirstOrDefault(w => w.Title == "编辑任务")?.Close(); };
                    watchdog.Start();
                    _ = calendar.Dispatcher.BeginInvoke(() => { var editor = Application.Current.Windows.Cast<Window>().First(w => w.Title == "编辑任务"); editor.UpdateLayout(); var title = Walk(editor).OfType<TextBox>().First(t => t.Name == "TaskTitleInput"); title.Text = "QA 新任务"; Snapshot(editor, Path.Combine(output, "task-editor.png")); Walk(editor).OfType<Button>().First(b => b.Content as string == "保存任务").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); });
                    CalendarWindow.EditTask(ctx, calendar, null);
                    watchdog.Stop();
                    var added = ctx.Db.Current.Tasks.Single(t => t.Title == "QA 新任务");
                    ctx.Calendar.Remove(added.Id);
                    Log("calendar month/timetable/editor save verified");
                    var activeWorkspace = new WorkspaceWindow(ctx);
                    activeWorkspace.Show();
                    navigate.Invoke(activeWorkspace, ["entries"]);
                    dock.QaCycle(1);
                    await Task.Delay(300);
                    using (var measure = Process.GetCurrentProcess())
                    {
                        measure.Refresh();
                        Log($"active calendar+entries privateMB={measure.PrivateMemorySize64 / 1048576d:F1}");
                    }
                    activeWorkspace.Close();
                    activeWorkspace = null;
                    dock.QaHide();
                    calendar.Close();
                    calendar = null;
                    var samples = new List<double>();
                    for (int i = 0; i < 30; i++)
                    {
                        dock.QaCycle(i);
                        await Task.Delay(200);
                    }
                    dock.QaHide();
                    ctx.ScheduleMemoryRelease();
                    Log("smoke completed; entering idle measurement");
                    var process = Process.GetCurrentProcess();
                    process.Refresh();
                    var cpu = process.TotalProcessorTime;
                    var idleWatch = Stopwatch.StartNew();
                    await Task.Delay(stressSeconds >= 1800 ? 120000 : 18000);
                    process.Refresh();
                    var idleCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / idleWatch.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                    var idleBytes = process.PrivateMemorySize64;
                    var idleWorking = process.WorkingSet64;
                    var baselineHandles = process.HandleCount;
                    var baselineWindows = Application.Current.Windows.Count;
                    Log($"managedCommittedMB={GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d:F1} liveMB={GC.GetTotalMemory(false) / 1048576d:F1}");
                    Log($"idle privateMB={idleBytes / 1048576d:F1} workingMB={idleWorking / 1048576d:F1} cpuPercent={idleCpu:F3} handles={baselineHandles}");
                    var stress = Stopwatch.StartNew();
                    int count = 0;
                    int calendarCycles = 0;
                    long peak = process.PrivateMemorySize64;
                    var checkpoints = new List<object>();
                    while (stress.Elapsed.TotalSeconds < stressSeconds)
                    {
                        samples.Add(dock.QaCycle(count++));
                        await Task.Delay(200);
                        if (count % 100 == 0)
                        {
                            var cycleCalendar = new CalendarWindow(ctx);
                            cycleCalendar.Show();
                            await Task.Delay(150);
                            cycleCalendar.QaToggleView();
                            await Task.Delay(150);
                            cycleCalendar.Close();
                            cycleCalendar = null;
                            calendarCycles++;
                            if (count % 500 == 0)
                            {
                                var cycleWorkspace = new WorkspaceWindow(ctx);
                                cycleWorkspace.Show();
                                await Task.Delay(350);
                                navigate.Invoke(cycleWorkspace, ["entries"]);
                                await Task.Delay(150);
                                cycleWorkspace.Close();
                                cycleWorkspace = null;
                                await Task.Delay(150);
                                if (PluginSession.ActiveCount != 0)
                                    throw new Exception("长测中关闭页面后插件会话未释放。");
                            }
                            process.Refresh();
                            peak = Math.Max(peak, process.PrivateMemorySize64);
                            checkpoints.Add(new
                            {
                                seconds = stress.Elapsed.TotalSeconds,
                                bytes = process.PrivateMemorySize64,
                                handles = process.HandleCount,
                                windows = Application.Current.Windows.Count,
                                calendarCycles
                            });
                            Log($"stress {stress.Elapsed.TotalSeconds:F0}s memoryMB={process.PrivateMemorySize64 / 1048576d:F1} handles={process.HandleCount} windows={Application.Current.Windows.Count} calendarCycles={calendarCycles}");
                        }
                    }
                    dock.QaHide();
                    var measuredStressSeconds = stress.Elapsed.TotalSeconds;
                    if (Application.Current.Windows.Count != baselineWindows)
                        throw new Exception("连续切换后窗口数量发生增长。");
                    ctx.ScheduleMemoryRelease();
                    await Task.Delay(stressSeconds >= 1800 ? 24000 : 1000);
                    process.Refresh();
                    samples.Sort();
                    var report = new
                    {
                        entries = 500,
                        tasks = 10000,
                        stressSeconds = measuredStressSeconds,
                        iterations = count,
                        calendarCycles,
                        idlePrivateMB = idleBytes / 1048576d,
                        idleWorkingMB = idleWorking / 1048576d,
                        idleCpuPercent = idleCpu,
                        p95Milliseconds = samples[(int)(samples.Count * .95)],
                        peakPrivateMB = peak / 1048576d,
                        finalPrivateMB = process.PrivateMemorySize64 / 1048576d,
                        baselineHandles,
                        finalHandles = process.HandleCount,
                        baselineWindows,
                        finalWindows = Application.Current.Windows.Count,
                        checkpoints
                    };
                    File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    Log("PASS completed");
                    app.Shutdown();
                }
                catch (Exception ex) { Log(ex.ToString()); app.Shutdown(1); }
            };
            dock.Show();
            app.Run();
        }
        catch (Exception ex) { Log(ex.ToString()); Environment.ExitCode = 1; }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(parent, i)))
                yield return child;
    }
    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        if (window.WindowState == WindowState.Normal && (Math.Abs(window.ActualWidth - window.Width) > 1 || Math.Abs(window.ActualHeight - window.Height) > 1))
            throw new InvalidOperationException("窗口客户区被原生非客户边框挤压。");
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
