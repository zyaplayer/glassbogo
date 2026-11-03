using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace GlassBogo.App;

internal static class V2UiTest
{
    private sealed class FakeLauncher : ILaunchService
    {
        internal bool Fail;
        internal string? Last;
        internal TaskCompletionSource<LaunchResult>? Pending;
        public Task<LaunchResult> LaunchAsync(Entry entry) { Last = entry.Id; if (Pending is { } pending) { Pending = null; return pending.Task; } return Task.FromResult(new LaunchResult(!Fail, Fail ? "生成的失效入口" : null)); }
    }
    private sealed class FakeDiscovery
    {
        internal const string SystemOnlyName = "QA 系统独有应用";
        internal bool NewInstalled, FailNext;
        internal int Scans;
        internal TaskCompletionSource<List<Entry>>? Pending;
        internal const string NewName = "QA 新安装应用";
        internal Task<List<Entry>> Scan()
        {
            Interlocked.Increment(ref Scans);
            if (Pending is { } pending) { Pending = null; return pending.Task; }
            if (FailNext) { FailNext = false; return Task.FromException<List<Entry>>(new IOException("QA refresh failure")); }
            return Task.FromResult(NewInstalled ? new List<Entry> { new("qa-new-discovered", NewName, "QA!newly-installed", "app") } : []);
        }
    }
    internal static void Run(string output, int seconds)
    {
        DesktopVisibilityTest.RequireIsolatedDesktop();
        Directory.CreateDirectory(output);
        var events = new StringBuilder(); var logWriter = new PreflightReportWriter(Path.Combine(output, "events.log"));
        void Log(string text) { events.AppendLine(DateTime.UtcNow.ToString("O") + " " + text); logWriter.Save(events.ToString()); }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds + 300) };
        watchdog.Tick += (_, _) => { Log("FAIL watchdog"); app.Shutdown(2); };
        app.DispatcherUnhandledException += (_, e) => { Log("FAIL " + e.Exception); e.Handled = true; app.Shutdown(2); };
        app.Startup += async (_, _) =>
        {
            watchdog.Start(); V2Context? ctx = null;
            var assertions = 0;
            void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); assertions++; Log("PASS " + name); }
            try
            {
                using var policy = GlassEnvironment.QaOverride(false, true, true);
                await DesktopVisibilityTest.VerifyController(Path.Combine(output, "visibility-native"));
                Check(true, "desktop icon controller hides and restores native views, including a recreated Explorer view, only on isolated desktop");
                var db = await Task.Run(() =>
                {
                    var db = new Database(Path.Combine(output, "data"), true);
                    db.Update(s =>
                    {
                        s.Preferences = s.Preferences with { Onboarded = true };
                        for (var c = 0; c < 40; c++) s.Categories.Add(new("qa-cat-" + c, "测试分类 " + c));
                        for (var i = 0; i < 500; i++) s.Entries.Add(new("qa-entry-" + i, $"QA Entry {i:D4}", @"C:\Generated-GlassBogo-QA\" + i + ".exe", CategoryId: "qa-cat-" + (i % 40), Pinned: i < 80, PinOrder: i, CategoryOrder: i));
                        for (var i = 0; i < 10000; i++) s.Tasks.Add(new("qa-task-" + i, "Deferred task " + i, DateTime.Today.ToString("yyyy-MM-dd")));
                        s.Plugins["deferred.plugin"] = true;
                    }); return db;
                });
                var launch = new FakeLauncher(); var discoveredApps = new FakeDiscovery(); ctx = new(db, qa: true, launcher: launch, discovery: new AppDiscovery(() => Task.FromResult(new List<Entry> { new("qa-system-only", FakeDiscovery.SystemOnlyName, "QA!system-only", "app") })), targetProbe: e => Task.FromResult(e.Id == "qa-refresh-missing" ? TargetState.Missing : TargetState.Unknown), desktopDiscovery: new AppDiscovery(discoveredApps.Scan));
                var dock = new Dock2Window(ctx); dock.Show(); dock.UpdateLayout();
                await Until(() => ctx.Graphics.RenderedFrames > 0);
                Check(!dock.Topmost && !dock.ShowActivated, "T01/T02 non-topmost dock, no activation");
                var topmost = new Window { Title = "QA topmost ordering target", Width = 80, Height = 80, Topmost = true, ShowActivated = false, ShowInTaskbar = false };
                topmost.Show();
                using (var ordering = new DesktopLayer(dock))
                {
                    ordering.PlaceBelow(new WindowInteropHelper(topmost).Handle);
                    Check((GlassNative.GetWindowLongPtr(dock.Handle, -20).ToInt64() & 8) == 0, "T02 native dock style remains non-topmost below a topmost application");
                }
                topmost.Close();
                Check(dock.Layout is { PinnedVisible: < 80, CategoriesVisible: < 40 }, "independent overflow in both sections");
                Check(dock.ActualWidth * Display.PrimaryScreen.Scale <= Display.PrimaryScreen.WorkingArea.Width, "dock inside work area");
                Check(ctx.Graphics.LiveSurfaces > 0 && dock.EffectiveVisualMode == "强视觉", "shared GPU material initialized with synthetic source");
                await VerifyCalendar(ctx, output, Check);
                Check(ctx.Graphics.ClientCount == 2 && dock.Panel.UsesGlass, "dock and calendar share glass rendering; reusable category panel also opts into glass");
                await Snapshot(dock, output, "dock");

                dock.Interaction.Click("qa-cat-0"); dock.ShowCategory("qa-cat-0", true); await Task.Delay(180); dock.Panel.UpdateLayout();
                var panelHandle = new WindowInteropHelper(dock.Panel).Handle;
                Check(dock.Panel.IsVisible && !dock.Panel.Topmost, "T04 category visible without topmost");
                var headerClose = Walk(dock.Panel).OfType<Button>().Single(b => b.Content as string == "×");
                var closeKey = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dock.Panel)!, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                headerClose.RaiseEvent(closeKey);
                Check(!closeKey.Handled, "category panel does not hijack Enter on its close/add buttons");
                Check(dock.Interaction.Hover("qa-cat-1") is null, "pinned panel ignores other-category hover");
                await dock.Launch(db.Current.Entries.First());
                Check(dock.Panel.IsVisible && dock.Interaction.Pinned && launch.Last == "qa-entry-0", "T03 pinned panel survives target launch");
                dock.Interaction.Click("qa-cat-1"); dock.ShowCategory("qa-cat-1", true);
                Check(new WindowInteropHelper(dock.Panel).Handle == panelHandle, "T14 same panel HWND reused");
                await Snapshot(dock.Panel, output, "category");
                await Until(() => dock.Panel.EffectiveVisualMode == "强视觉" && (dock.Panel.Attached || dock.Panel.HasRetainedFrame));
                Check(ctx.Graphics.ClientCount == 3 && dock.Panel.UsesGlass, "category panel has a live shared glass surface instead of an opaque fallback");
                dock.DismissPanel(); Check(!dock.Panel.IsVisible, "pinned panel explicit dismissal");
                Check(OutsideClick.ActiveCount == 0, "outside-click subscription removed with panel");
                dock.Interaction.Click("qa-cat-0"); dock.ShowCategory("qa-cat-0", false); dock.Panel.Close();
                dock.Interaction.Click("qa-cat-0"); dock.ShowCategory("qa-cat-0", false);
                Check(dock.Panel.IsVisible && new WindowInteropHelper(dock.Panel).Handle == panelHandle, "category native close dismisses reusable panel without destroying it");
                dock.DismissPanel();
                Walk(dock).OfType<Button>().Single(b => b.ToolTip as string == "更多分类").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                dock.Panel.UpdateLayout(); await Task.Delay(60);
                var overflowClose = Walk(dock.Panel).OfType<Button>().Single(b => b.Content as string == "×"); overflowClose.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!dock.Panel.IsVisible && OutsideClick.ActiveCount == 0, "overflow panel has a working explicit close button");

                await VerifyDisplays(ctx, Check);
                await VerifyEditor(dock, ctx, Check);

                ctx.OpenManager(); Manager2Window? manager = ctx.Manager!; manager.UpdateLayout();
                await Until(() => ctx.Graphics.ScreenshotReady);
                Check(!manager.UsesGlass && Affinity(manager) == 0 && Affinity(dock) == 0, "management and paused dock are not excluded from screenshots");
                Check(manager.Top + manager.Height <= dock.Top - 8, "management initially leaves the bottom dock visible");
                var pausedFrames = ctx.Graphics.RenderedFrames; await Task.Delay(120);
                Check(ctx.Graphics.RenderedFrames == pausedFrames && ctx.Graphics.LiveSurfaces == 0, "opening management freezes dock sampling and releases GPU surfaces");
                manager.WindowState = WindowState.Minimized; ctx.OpenManager();
                Check(manager.WindowState == WindowState.Normal, "management can be restored after show-desktop/minimization");
                Check(!Walk(manager).OfType<Button>().Any(b => b.Content is string text && text is "日历" or "插件" or "收纳桌面"), "deferred modules absent from management navigation");
                await VerifyManagerRows(manager, ctx, launch, output, Check);
                await VerifyEntryPicker(manager, ctx, output, discoveredApps, Check);
                await Snapshot(manager, output, "entries");
                manager.ShowPage("categories"); await Snapshot(manager, output, "categories");
                manager.ShowPage("settings"); await Snapshot(manager, output, "settings");
                await VerifyDesktopToggle(manager, ctx, Check);
                manager.Close(); manager = null;
                try { await Until(() => ctx.Graphics.RenderedFrames > pausedFrames && dock.ExcludedFromCapture); }
                catch (Exception ex) { throw new InvalidOperationException(ctx.Graphics.DebugState, ex); }
                Check(ctx.Graphics.ClientCount == 3 && !ctx.Graphics.ScreenshotReady, "closing management restores dock, category and calendar capture");
                await VerifyDockStability(dock, ctx, Check);
                await VerifyMaterialContinuity(dock, ctx, Check);

                var beforeCount = db.Current.Entries.Count;
                await ctx.Write(() => ctx.Catalog.PlaceRange([db.Current.Entries[90] with { Name = "Discovered alias" }], pinned: true));
                Check(db.Current.Entries.Count == beforeCount && db.Current.Entries[90].Pinned && db.Current.Entries[90].Name != "Discovered alias", "T07 atomic pin reuses managed identity");
                var categoryBefore = db.Current.Entries[90].CategoryId;
                await ctx.Write(() => ctx.Catalog.SetPinned(["qa-entry-90"], false));
                Check(db.Current.Entries[90].CategoryId == categoryBefore, "T08 unpin retains membership");

                ctx.Search.Open(); ctx.Search.Query.Text = "QA Entry 0042"; await ctx.Search.RefreshResults();
                Check(ctx.Search.Results.Items.Count == 1 && ctx.Search.Topmost, "T09/T10 independent query and temporary topmost");
                Check(!ctx.Search.UsesGlass && Affinity(ctx.Search) == 0 && ctx.Graphics.ClientCount == 3, "search stays ordinary while dock, category and calendar use glass");
                launch.Fail = true; await ctx.Search.LaunchSelected(); Check(ctx.Search.IsVisible, "launch failure keeps search usable");
                launch.Fail = false; await ctx.Search.LaunchSelected(); Check(!ctx.Search.IsVisible && launch.Last == "qa-entry-42", "T10 successful launch dismisses search");
                ctx.Search.Open(); ctx.Search.Dismiss(true); Check(!ctx.Search.IsVisible, "T11 cancel dismisses temporary search");
                ctx.Search.Open(); var searchHandle = ctx.Search.Handle; ctx.Search.Close(); ctx.Search.Open();
                Check(ctx.Search.IsVisible && ctx.Search.Handle == searchHandle, "search native close preserves reusable window"); ctx.Search.Dismiss(true);
                await VerifyAsyncLaunches(dock, ctx, launch, Check);

                await ctx.Write(() => ctx.Catalog.Remove(["qa-entry-499"]));
                Check(!db.Current.Entries.Any(e => e.Id == "qa-entry-499"), "T12 missing-target reference removed without launching target");
                Check(db.Current.Tasks.Count == 10000 && db.Current.Plugins["deferred.plugin"], "deferred data preserved");

                using (var key = new GlobalSearchHotkey(() => { }))
                using (var conflict = new GlobalSearchHotkey(() => { }))
                {
                    var original = new SearchShortcut(3, 0x85); var replacement = new SearchShortcut(3, 0x86);
                    Check((await key.TryChangeAsync(original, _ => Task.CompletedTask)).Success, "independent search hotkey registered");
                    var rejected = await key.TryChangeAsync(replacement, _ => Task.FromException(new IOException("QA persistence failure")));
                    Check(!rejected.Success && key.Current == original && (await conflict.TryChangeAsync(replacement, _ => Task.CompletedTask)).Success, "async hotkey save rollback retains old key and releases candidate");
                    Check(!(await key.TryChangeAsync(replacement, _ => Task.CompletedTask)).Success && key.Current == original, "hotkey conflict retains effective registration");
                }

                await ctx.Write(() => db.Update(s => s.Preferences = s.Preferences with { VisualQuality = "reduced" }));
                await Until(() => dock.EffectiveVisualMode == "降低效果");
                Check(dock.IsVisible, "T16 reduced material preserves dock controls");

                foreach (var highContrast in new[] { false, true })
                {
                    using (GlassEnvironment.QaOverride(highContrast, false, false))
                    {
                        Design.Apply(ctx.Preferences.Theme); ctx.Graphics.Refresh();
                        await Until(() => dock.EffectiveVisualMode == "兼容模式" && ctx.Graphics.LiveSurfaces == 0);
                        Check(dock.IsVisible && dock.Body.IsEnabled, highContrast ? "high contrast fallback retains controls" : "system transparency off releases graphics");
                    }
                    Design.Apply(ctx.Preferences.Theme); ctx.Graphics.Refresh();
                    await Until(() => dock.EffectiveVisualMode == "降低效果");
                }

                await ctx.Write(() => db.Update(s => s.Preferences = s.Preferences with { VisualQuality = "off" }));
                await Until(() => ctx.Graphics.LiveSurfaces == 0 && dock.EffectiveVisualMode == "已关闭");
                Check(dock.EffectiveVisualMode == "已关闭", "T16 effects off releases graphics and preserves dock");
                await ctx.Write(() => db.Update(s => s.Preferences = s.Preferences with { VisualQuality = "strong", IconSize = "large" }));
                await Until(() => dock.EffectiveVisualMode == "强视觉" && ctx.Graphics.LiveSurfaces > 0);
                Check(dock.Height == 98, "large icons reflow bounded dock");
                await ctx.Write(() => db.Update(s => s.Preferences = s.Preferences with { IconSize = "medium" }));
                await Task.Delay(300);

                await Until(() => dock.FrozenMaterial && ctx.Graphics.LiveSurfaces == 0);
                var resumedAt = ctx.Graphics.RenderedFrames;
                dock.MaterialDirty = true; ctx.Graphics.Wake();
                await Until(() => ctx.Graphics.RenderedFrames > resumedAt);
                Check(dock.EffectiveVisualMode == "强视觉", "static material hibernates and resumes on interaction");

                var process = Process.GetCurrentProcess();
                GC.Collect(); GC.WaitForPendingFinalizers();
                var idleSeconds = seconds >= 1800 ? 120 : 18;
                Log("idle measurement started");
                var idleFramesBefore = ctx.Graphics.RenderedFrames; var idleTicksBefore = ctx.Graphics.TickCount; var idleWakesBefore = ctx.Graphics.WakeCount;
                process.Refresh(); var cpu = process.TotalProcessorTime; var idle = Stopwatch.StartNew();
                await Task.Delay(TimeSpan.FromSeconds(idleSeconds));
                process.Refresh(); var idleCpu = (process.TotalProcessorTime - cpu).TotalMilliseconds / idle.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                var idlePrivate = process.PrivateMemorySize64 / 1048576d;
                var idleFrames = ctx.Graphics.RenderedFrames - idleFramesBefore; var idleTicks = ctx.Graphics.TickCount - idleTicksBefore; var idleWakes = ctx.Graphics.WakeCount - idleWakesBefore;
                using var gpuUsage = await Task.Run(() => new GpuUsage());
                await Task.Delay(1000);
                var idleGpu = await Task.Run(gpuUsage.Sample); var idleGraphics = await ctx.Graphics.GetMetrics();
                var baselineWindows = Application.Current.Windows.Count;
                var baselineHandles = process.HandleCount;
                process.Refresh(); var stressCpuStart = process.TotalProcessorTime;
                var stress = Stopwatch.StartNew(); var times = new List<double>(); var iterations = 0;
                var checkpoints = new List<object>(); var nextCheckpoint = 30;
                while (stress.Elapsed.TotalSeconds < seconds)
                {
                    var id = "qa-cat-" + (iterations % 20 == 0 ? iterations / 20 % 40 : iterations % 8); var watch = Stopwatch.StartNew();
                    dock.Interaction.Click(id); dock.ShowCategory(id, false); dock.Panel.UpdateLayout();
                    times.Add(watch.Elapsed.TotalMilliseconds); iterations++;
                    await Task.Delay(50);
                    if (stress.Elapsed.TotalSeconds >= nextCheckpoint)
                    {
                        var gpu = await Task.Run(gpuUsage.Sample); var graphics = await ctx.Graphics.GetMetrics();
                        process.Refresh(); checkpoints.Add(new { seconds = stress.Elapsed.TotalSeconds, privateMiB = process.PrivateMemorySize64 / 1048576d, handles = process.HandleCount, windows = Application.Current.Windows.Count, graphics, gpuBusiestEnginePercent = gpu.Percent, gpuCounterError = gpu.Error });
                        Log($"stress {stress.Elapsed.TotalSeconds:F0}s iterations={iterations} privateMiB={process.PrivateMemorySize64 / 1048576d:F1}"); nextCheckpoint += 30;
                    }
                }
                process.Refresh(); var stressCpu = (process.TotalProcessorTime - stressCpuStart).TotalMilliseconds / stress.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
                dock.DismissPanel(); await Task.Delay(500); process.Refresh();
                Check(Application.Current.Windows.Count == baselineWindows, "no WPF window growth after switching");
                times.Sort();
                var finalGpu = await Task.Run(gpuUsage.Sample); var finalGraphics = await ctx.Graphics.GetMetrics();
                var finalWindowCount = Application.Current.Windows.Count; var finalHandles = process.HandleCount; var finalPrivate = process.PrivateMemorySize64 / 1048576d; var renderedFrames = ctx.Graphics.RenderedFrames;
                var expectedPinned = ctx.Catalog.Pinned().Select(e => e.Id).ToArray(); var expectedCategories = db.Current.Categories.Select(c => c.Name).ToArray();
                var dataPath = db.DirectoryPath;
                await ctx.DisposeAsync(); ctx = null;
                await VerifyShutdown(output, Check);
                Check(Application.Current.Windows.Count == 0 && OutsideClick.ActiveCount == 0, "all UI windows and pointer subscriptions released");
                var restored = await Task.Run(() =>
                {
                    using var reopened = new Database(dataPath, true);
                    return reopened.Current.Version == 2 && new Catalog(reopened).Pinned().Select(e => e.Id).SequenceEqual(expectedPinned) && reopened.Current.Categories.Select(c => c.Name).SequenceEqual(expectedCategories)
                        && reopened.Current.Preferences.IconSize == "medium" && reopened.Current.Tasks.Count == 10000 && reopened.Current.Plugins["deferred.plugin"];
                });
                Check(restored, "T13 restart restores order, settings, membership and deferred data");
                await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, stage = "GlassBogo 2.0 isolated UI", assertions, entries = 500, tasks = 10000,
                    assemblySha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(V2UiTest).Assembly.Location))),
                    source = "generated synthetic GPU texture, no live desktop capture", wpfRenderTier = RenderCapability.Tier >> 16, logicalProcessors = Environment.ProcessorCount, hotCategories = 8, totalCategories = 40, idleSeconds, idleCpuPercentOfMachine = idleCpu, idlePrivateMiB = idlePrivate, idleFrames, idleTicks, idleWakes,
                    stressSeconds = stress.Elapsed.TotalSeconds, stressCpuPercentOfMachine = stressCpu, iterations, cachedLayoutP95Milliseconds = times[(int)Math.Floor((times.Count - 1) * .95)],
                    baselineWindows, finalWindows = finalWindowCount, baselineHandles, finalHandles, cleanupWindows = Application.Current.Windows.Count,
                    idleGpuBusiestEnginePercent = idleGpu.Percent, idleGpuCounterError = idleGpu.Error, idleGraphics,
                    finalGpuBusiestEnginePercent = finalGpu.Percent, finalGpuCounterError = finalGpu.Error, finalGraphics,
                    finalPrivateMiB = finalPrivate, renderedFrames, checkpoints,
                    limits = "No target process launched, no real user data. Snapshots use opaque material for WPF layout QA. Real Win+D, real capture performance and new product visual approval remain manual checks."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Log("PASS completed"); await logWriter.FlushAsync(); watchdog.Stop(); app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Log("FAIL " + ex);
                if (ctx?.Graphics.LastFailure is { } detail) Log("GPU " + detail);
                if (ctx?.Dock is { } failedDock) Log($"GPU state={ctx.Graphics.Status} frames={ctx.Graphics.RenderedFrames} ticks={ctx.Graphics.TickCount} wakes={ctx.Graphics.WakeCount} clients={ctx.Graphics.ClientCount} surfaces={ctx.Graphics.LiveSurfaces} frozen={failedDock.FrozenMaterial} dirty={failedDock.MaterialDirty} front={failedDock.Material.IsFrontBufferAvailable} attached={failedDock.Attached} mode={failedDock.EffectiveVisualMode}");
                if (ctx is not null) await ctx.DisposeAsync();
                await logWriter.FlushAsync(); watchdog.Stop(); app.Shutdown(2);
            }
        };
        app.Run();
    }
    private static async Task VerifyEditor(Window owner, V2Context ctx, Action<bool, string> check)
    {
        var preserved = false; Exception? failure = null;
        var automation = owner.Dispatcher.InvokeAsync(async () =>
        {
            LiquidWindow? dialog = null;
            try
            {
                await Task.Delay(120);
                dialog = Application.Current.Windows.OfType<LiquidWindow>().Single(w => w.Title == "QA category editor");
                var input = Walk(dialog).OfType<TextBox>().Single(); input.Text = "QA preserved input";
                await SetTrigger(true);
                var save = Walk(dialog).OfType<Button>().Single(b => b.Content as string == "保存");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Walk(dialog).OfType<TextBlock>().Any(t => t.Text.StartsWith("未保存：", StringComparison.Ordinal)));
                preserved = dialog.IsVisible && input.Text == "QA preserved input";
                await SetTrigger(false);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception ex) { failure = ex; dialog?.Close(); }
        });
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        watchdog.Tick += (_, _) => { watchdog.Stop(); foreach (var window in Application.Current.Windows.Cast<Window>().Where(w => w.Title == "QA category editor").ToArray()) window.Close(); };
        watchdog.Start();
        V2Ui.Name(owner, ctx, "QA category editor", "", value => ctx.Write(() => ctx.Catalog.SaveCategory(null, value)), 40);
        watchdog.Stop(); await automation.Task.Unwrap();
        if (failure is not null) throw failure;
        check(preserved && ctx.Db.Current.Categories.Any(c => c.Name == "QA preserved input"), "T06 save failure preserves editor/input, retry commits category");
        async Task SetTrigger(bool reject)
        {
            await Task.Run(() =>
            {
                using var sql = new SqliteConnection("Data Source=" + Path.Combine(ctx.Db.DirectoryPath, "glassbogo.db") + ";Pooling=False"); sql.Open(); using var cmd = sql.CreateCommand();
                cmd.CommandText = reject ? "CREATE TRIGGER reject_v2_editor BEFORE UPDATE ON document BEGIN SELECT RAISE(ABORT,'QA save failure'); END" : "DROP TRIGGER IF EXISTS reject_v2_editor";
                cmd.ExecuteNonQuery();
            });
        }
    }
    private static async Task VerifyManagerRows(Manager2Window manager, V2Context ctx, FakeLauncher launch, string output, Action<bool, string> check)
    {
        await Task.Delay(150); manager.UpdateLayout();
        var bulk = Walk(manager).OfType<FrameworkElement>().Single(e => System.Windows.Automation.AutomationProperties.GetName(e) == "批量入口操作");
        V2EntryItem Row(string id) => manager.Entries.Items.Cast<V2EntryItem>().Single(r => r.Entry.Id == id);
        Button Action(string name) => Walk(manager).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == name);
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Entry Entry(string id) => ctx.Db.Current.Entries.Single(e => e.Id == id);
        async Task Reveal(string id) { manager.Entries.ScrollIntoView(Row(id)); manager.Entries.UpdateLayout(); await Task.Delay(80); }

        check(bulk.Visibility == Visibility.Collapsed, "manager has no default bottom action toolbar");
        manager.Entries.SelectedItem = Row("qa-entry-0");
        check(bulk.Visibility == Visibility.Collapsed, "single selection keeps batch toolbar hidden");
        await Reveal("qa-entry-4");
        Click(Action("取消固定“QA Entry 0004”"));
        await Until(() => !Entry("qa-entry-4").Pinned);
        await Task.Delay(120);
        check(Entry("qa-entry-0").Pinned, "row pin action targets its own item rather than another selected row");

        await Reveal("qa-entry-4"); var more = Action("管理“QA Entry 0004”"); Click(more);
        var menu = more.ContextMenu!;
        check(new[] { "重命名显示名称", "重新定位", "移除入口", "上移", "下移" }.All(label => menu.Items.OfType<MenuItem>().Any(item => item.Header as string == label)), "entry management remains available on the corresponding row");
        menu.IsOpen = false;
        var launchedBefore = launch.Last;
        more.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
        check(launch.Last == launchedBefore, "double clicking a row action does not launch the selected entry");

        manager.Entries.SelectedItems.Add(Row("qa-entry-1"));
        check(bulk.Visibility == Visibility.Visible, "multiple selection reveals the batch toolbar");
        Click(Walk(bulk).OfType<Button>().Single(b => b.Content as string == "取消固定"));
        await Until(() => !Entry("qa-entry-0").Pinned && !Entry("qa-entry-1").Pinned);
        await Task.Delay(120);
        check(manager.Entries.SelectedItems.Count == 2, "batch edit preserves the selected set after refresh");
        await Snapshot(manager, output, "entries-batch");
        manager.Entries.SelectedItems.Clear();
        check(bulk.Visibility == Visibility.Collapsed, "clearing selection removes the batch toolbar");

        manager.Entries.SelectedItem = Row("qa-entry-0"); await Reveal("qa-entry-450");
        Click(Action("固定“QA Entry 0450”"));
        await Until(() => Entry("qa-entry-450").Pinned);
        check(!Entry("qa-entry-0").Pinned, "virtualized recycled row action follows its current entry");
        manager.Entries.SelectedItems.Clear(); await Reveal("qa-entry-0");
        var width = manager.Width; manager.Width = Math.Min(640, width);
        await Snapshot(manager, output, "entries-narrow");
        var listRight = manager.Entries.TranslatePoint(new Point(manager.Entries.ActualWidth, 0), manager).X;
        check(Walk(manager.Entries).OfType<V2RowActions>().Where(v => v.IsVisible).All(v => v.TranslatePoint(new Point(v.ActualWidth, 0), manager).X <= listRight + 1), "row actions remain inside the narrow list viewport");
        manager.Width = width;

        manager.ShowPage("categories"); manager.UpdateLayout(); await Task.Delay(100);
        var categories = Walk(manager).OfType<ListBox>().Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "分类列表");
        categories.SelectedItem = ctx.Db.Current.Categories.Single(c => c.Id == "qa-cat-1");
        check(manager.Entries.Items.Cast<V2EntryItem>().All(row => row.Entry.CategoryId == "qa-cat-1"), "merged category sidebar filters the entry list on the same page");
        check(!Walk(manager).OfType<Button>().Any(b => b.Content as string is "入口" or "分类"), "management has no separate entry and category navigation tabs");
        var reorder = Action("管理分类“测试分类 0”"); Click(reorder);
        var reorderMenu = reorder.ContextMenu!;
        reorderMenu.Items.OfType<MenuItem>().Single(item => item.Header as string == "下移").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); reorderMenu.IsOpen = false;
        await Until(() => ctx.Db.Current.Categories[1].Id == "qa-cat-0"); await Task.Delay(120);
        check((categories.SelectedItem as Category)?.Id == "qa-cat-1", "category row reorder is independent of selected category");
        var categoryMore = Action("管理分类“测试分类 0”"); Click(categoryMore);
        check(categoryMore.ContextMenu!.Items.OfType<MenuItem>().Any(item => item.Header as string == "删除分类…"), "category removal belongs to its row menu");
        categoryMore.ContextMenu.IsOpen = false;
        categories.SelectedItem = categories.Items.Cast<Category>().Single(c => c.Id == "$all");
        manager.ShowPage("entries"); await Reveal("qa-entry-0");
    }
    private static async Task VerifyEntryPicker(Window owner, V2Context ctx, string output, FakeDiscovery discovery, Action<bool, string> check)
    {
        var initial = ctx.Db.Current.Entries.Single(e => e.Id == "qa-entry-40");
        var count = ctx.Db.Current.Entries.Count;
        async Task Dialog(Action open, Func<V2Dialog, Task> inspect, Window? dialogOwner = null)
        {
            var expectedOwner = dialogOwner ?? owner;
            Exception? failure = null;
            var automation = owner.Dispatcher.InvokeAsync(async () =>
            {
                V2Dialog? dialog = null;
                try
                {
                    await Task.Delay(100);
                    dialog = Application.Current.Windows.OfType<V2Dialog>().Single(w => w.IsVisible && w.Owner == expectedOwner);
                    await inspect(dialog);
                }
                catch (Exception ex) { failure = ex; if (dialog is not null) { dialog.SetBusy(false); dialog.Close(); } }
            });
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); foreach (var dialog in Application.Current.Windows.OfType<V2Dialog>().Where(w => w.Owner == expectedOwner).ToArray()) { dialog.SetBusy(false); dialog.Close(); } };
            watchdog.Start(); try { open(); await automation.Task.Unwrap(); } finally { watchdog.Stop(); }
            if (failure is not null) throw failure;
            if (Application.Current.Windows.OfType<V2Dialog>().Any(w => w.IsVisible && w.Owner == expectedOwner)) throw new InvalidOperationException("Dialog was not dismissed");
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Escape(V2Dialog dialog) => dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        void Open() => V2Ui.ChooseEntries(owner, ctx, "qa-cat-2");
        await Dialog(() => V2Ui.ChooseEntries(ctx.Dock!, ctx, "qa-cat-2"), dialog =>
        {
            var work = Display.PrimaryScreen.WorkingArea;
            check(Native.GetWindowRect(dialog.Handle, out var bounds) && bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom, "picker opened from the bottom dock stays fully inside the working area");
            dialog.Close(); return Task.CompletedTask;
        }, ctx.Dock);
        for (var exit = 0; exit < 3; exit++)
        {
            await Dialog(Open, async dialog =>
            {
                var picker = (EntryPickerWindow)dialog;
                check(picker.Source.SelectedIndex == 0 && picker.Entries.Items.Count == count, "add picker defaults to all managed entries before discovery");
                if (exit == 0)
                {
                    picker.Entries.SelectedIndex = -1;
                    picker.Query.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(picker)!, Environment.TickCount, Key.Up) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                    check(picker.Entries.SelectedIndex == count - 1, "picker Up from no selection starts at the last result");
                    picker.Entries.SelectedIndex = -1;
                }
                if (exit == 0) await Snapshot(picker, output, "entry-picker");
                if (exit == 0) Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "×"));
                else if (exit == 1) Click(picker.Cancel);
                else Escape(picker);
            });
        }
        check(ctx.Db.Current.Entries.Count == count && ctx.Db.Current.Entries.Single(e => e.Id == initial.Id) == initial && ctx.Modals == 0, "close button, cancel and Esc leave library/category/pins untouched and release modal hold");

        await Dialog(Open, dialog =>
        {
            var picker = (EntryPickerWindow)dialog;
            picker.SetStaged([new("qa-unsaved-file", "未保存文件", @"C:\Generated-GlassBogo-QA\unsaved.txt")]);
            check(picker.Source.SelectedItem as string == "新选择的文件／文件夹" && picker.Entries.SelectedItems.Count == 1 && ctx.Db.Current.Entries.Count == count, "new file selection is staged without creating a library entry");
            Escape(picker); return Task.CompletedTask;
        });
        check(ctx.Db.Current.Entries.Count == count, "cancelling staged files does not persist them");

        await Dialog(Open, async dialog =>
        {
            var picker = (EntryPickerWindow)dialog;
            var add = Walk(picker).OfType<Button>().Single(b => b.Content as string == "添加选中");
            picker.Query.Text = "NoMatch-Generated-QA";
            check(picker.Entries.Items.Count == 0 && !add.IsEnabled, "empty picker results disable confirmation");
            picker.Query.Text = initial.Name; picker.Entries.SelectedItem = picker.Entries.Items.Cast<V2EntryItem>().Single(r => r.Entry.Id == initial.Id);
            await RejectWrite(true); Click(add);
            await Until(() => !picker.Busy && Walk(picker).OfType<TextBlock>().Any(t => t.Text.StartsWith("未添加：", StringComparison.Ordinal)));
            check(picker.IsVisible && picker.Query.Text == initial.Name && picker.Entries.SelectedItems.Count == 1, "failed add preserves the picker query and selection for retry");
            await RejectWrite(false); Click(add);
        });
        var moved = ctx.Db.Current.Entries.Single(e => e.Id == initial.Id);
        check(ctx.Db.Current.Entries.Count == count && moved.CategoryId == "qa-cat-2" && moved.Name == initial.Name && moved.Pinned == initial.Pinned && moved.PinOrder == initial.PinOrder, "choosing an existing entry moves the unified reference without duplicating or changing pin order/name");
        await Dialog(Open, dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Query.Text = initial.Name;
            var item = picker.Entries.Items.Cast<V2EntryItem>().Single(r => r.Entry.Id == initial.Id);
            check(item.Hint.Contains("已在此分类", StringComparison.Ordinal), "already-classified entries are clearly marked");
            picker.Entries.SelectedItem = item; Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "添加选中")); return Task.CompletedTask;
        });
        check(ctx.Db.Current.Entries.Single(e => e.Id == initial.Id).CategoryOrder == moved.CategoryOrder, "readding an entry in the same category does not reorder it");
        await Dialog(() => V2Ui.ChooseCategory(owner, ctx, [initial.Id]), dialog => { check(dialog.Cancel.IsVisible, "category chooser provides an explicit cancel button"); Escape(dialog); return Task.CompletedTask; });
        await Dialog(() => V2Ui.Name(owner, ctx, "QA cancel editor", initial.Name, value => ctx.Write(() => ctx.Catalog.Rename(initial.Id, value))), dialog => { Walk(dialog).OfType<TextBox>().Single().Text = "不应保存"; Escape(dialog); return Task.CompletedTask; });
        check(ctx.Db.Current.Entries.Single(e => e.Id == initial.Id).Name == initial.Name && ctx.Modals == 0, "Esc cancels rename/category dialogs without writes or lingering modal state");

        await Dialog(Open, dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Source.SelectedItem = "未分类";
            check(picker.Entries.Items.Count == 0 && !Walk(picker).OfType<Button>().Single(b => b.Content as string == "添加选中").IsEnabled && Walk(picker).OfType<TextBlock>().Any(t => t.Text == "没有未分类入口"), "unclassified picker source has an explicit empty state and disabled confirmation");
            picker.Close(); return Task.CompletedTask;
        });
        var unclassified = ctx.Db.Current.Entries.Single(e => e.Id == "qa-entry-41");
        await ctx.Write(() => ctx.Catalog.PlaceRange(ctx.Db.Current.Entries.Where(e => e.Id is "qa-entry-41" or "qa-entry-42").ToArray(), category: ""));
        await Dialog(Open, async dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Source.SelectedItem = "未分类";
            check(picker.Entries.Items.Count == 2 && picker.Entries.Items.Cast<V2EntryItem>().All(row => row.Entry.CategoryId.Length == 0), "unclassified source includes only uncategorized managed entries, including pinned ones");
            await Snapshot(picker, output, "entry-picker-unclassified");
            picker.Query.Text = unclassified.Name;
            check(picker.Entries.Items.Count == 1, "unclassified source supports query filtering");
            picker.Entries.SelectedItem = picker.Entries.Items[0]; Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "添加选中"));
        });
        var classified = ctx.Db.Current.Entries.Single(e => e.Id == unclassified.Id);
        check(ctx.Db.Current.Entries.Count == count && classified.CategoryId == "qa-cat-2" && classified.Pinned == unclassified.Pinned && classified.PinOrder == unclassified.PinOrder, "adding from unclassified reuses its record and preserves pin state/order");

        await Dialog(Open, async dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Query.Text = FakeDiscovery.SystemOnlyName;
            check(picker.Entries.Items.Count == 0, "default all-entry source excludes applications found only in the system catalog");
            picker.Source.SelectedItem = "发现已安装应用";
            await Until(() => picker.Entries.Items.Count == 1);
            var scansBefore = discovery.Scans;
            Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新"));
            await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
            check(picker.Source.SelectedItem as string == "发现已安装应用" && discovery.Scans == scansBefore, "application refresh stays in its own source without scanning the desktop");
            picker.Source.SelectedIndex = 0;
            await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
            check(picker.Entries.Items.Count == 0, "switching back to desktop entries never leaks the system discovery cache");
            picker.Close();
        });
        await ctx.DesktopDiscovery.GetAsync(CancellationToken.None);
        var scans = discovery.Scans; discovery.NewInstalled = true;
        await ctx.Write(() => ctx.Catalog.Add(new("qa-refresh-missing", "QA 已删除文件", @"C:\Generated-GlassBogo-QA\deleted.txt", CategoryId: "qa-cat-2", Pinned: true)));
        await Dialog(Open, async dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Query.Text = FakeDiscovery.NewName;
            Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新"));
            await Until(() => picker.Entries.Items.Cast<V2EntryItem>().Any(row => row.Entry.Name == FakeDiscovery.NewName));
            check(picker.Source.SelectedItem as string == "全部入口" && discovery.Scans > scans, "refresh shows fresh discovery directly in all entries");
            check(picker.Query.Text == FakeDiscovery.NewName && ctx.Db.Current.Entries.Count == count, "refresh preserves the query and does not import discovered applications");
            check(!ctx.Db.Current.Entries.Any(e => e.Id == "qa-refresh-missing") && Directory.GetFiles(Path.Combine(ctx.Db.DirectoryPath, "recovery"), "*.json").Any(), "explicit refresh removes stale references and creates a recovery point");
            picker.Source.SelectedItem = "未分类";
            check(picker.Entries.Items.Count == 1 && picker.Entries.Items.Cast<V2EntryItem>().Single().Hint.Contains("未分类 · 尚未添加", StringComparison.Ordinal) && ctx.Db.Current.Entries.Single(e => e.Id == initial.Id) == moved, "new desktop entries carry the unclassified label and appear in that source without changing managed categories");
            picker.Query.Clear();
            check(picker.Entries.Items.Cast<V2EntryItem>().All(row => row.Entry.CategoryId.Length == 0 && !ctx.Db.Current.Entries.Any(e => Catalog.Identity(e) == Catalog.Identity(row.Entry) && e.CategoryId.Length > 0)), "unclassified rows exclude every classified managed identity");
            Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新"));
            await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
            check(picker.Source.SelectedItem as string == "未分类" && picker.Entries.Items.Cast<V2EntryItem>().All(row => row.Entry.CategoryId.Length == 0) && picker.Entries.Items.Cast<V2EntryItem>().Any(row => row.Entry.Name == FakeDiscovery.NewName), "refresh preserves unclassified scope and includes new desktop entries");
            picker.Source.SelectedIndex = 0;
            picker.Query.Text = FakeDiscovery.NewName;
            await Snapshot(picker, output, "entry-picker-refresh");
            discovery.FailNext = true;
            Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新"));
            await Until(() => Walk(picker).OfType<TextBlock>().Any(t => t.Text.StartsWith("刷新失败：", StringComparison.Ordinal)));
            check(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新").IsEnabled && picker.Cancel.IsEnabled, "failed refresh keeps retry and cancel available");
            var beforeRetry = discovery.Scans;
            Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新"));
            await Until(() => discovery.Scans > beforeRetry && Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled) && !Walk(picker).OfType<TextBlock>().Any(t => t.Text.StartsWith("刷新失败：", StringComparison.Ordinal)));
            check(picker.Entries.Items.Cast<V2EntryItem>().Any(row => row.Entry.Name == FakeDiscovery.NewName), "refresh can recover from a failed scan");
            picker.Close();
        });
        var pendingRefresh = new TaskCompletionSource<List<Entry>>(TaskCreationOptions.RunContinuationsAsynchronously);
        discovery.Pending = pendingRefresh;
        await Dialog(Open, dialog =>
        {
            var picker = (EntryPickerWindow)dialog;
            Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新"));
            check(picker.Cancel.IsEnabled && Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新中…" && !b.IsEnabled), "refresh in progress leaves cancel available and prevents duplicate clicks");
            picker.Close(); return Task.CompletedTask;
        });
        pendingRefresh.SetResult([new("qa-refresh-after-close", FakeDiscovery.NewName, "QA!refresh-after-close", "app")]);
        await ctx.DesktopDiscovery.GetAsync(CancellationToken.None); await Task.Delay(60);
        check(ctx.Modals == 0 && ctx.Db.Current.Entries.Count == count && !Application.Current.Windows.OfType<EntryPickerWindow>().Any(), "closing during refresh ignores late UI results and does not import applications");
        await Dialog(Open, async dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Source.SelectedItem = "未分类"; picker.Query.Text = FakeDiscovery.NewName;
            await Until(() => picker.Entries.Items.Count == 1);
            var row = picker.Entries.Items.Cast<V2EntryItem>().Single();
            check(row.Hint.Contains("未分类 · 尚未添加", StringComparison.Ordinal), "fresh unclassified picker loads the desktop snapshot and labels unsaved entries");
            picker.Entries.SelectedItem = row; Click(Walk(picker).OfType<Button>().Single(b => b.Content as string == "添加选中"));
        });
        var added = ctx.Db.Current.Entries.Single(e => e.Name == FakeDiscovery.NewName);
        check(ctx.Db.Current.Entries.Count == count + 1 && added.CategoryId == "qa-cat-2", "adding a new desktop entry from unclassified saves its chosen category exactly once");
        await Dialog(Open, async dialog =>
        {
            var picker = (EntryPickerWindow)dialog; picker.Source.SelectedItem = "未分类"; picker.Query.Text = FakeDiscovery.NewName;
            await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
            check(picker.Entries.Items.Count == 0, "classified desktop entry disappears from unclassified without leaving a discovered copy");
            picker.Source.SelectedIndex = 0;
            await Until(() => picker.Entries.Items.Count == 1);
            check(picker.Entries.Items.Cast<V2EntryItem>().Single().Hint.Contains("测试分类 2", StringComparison.Ordinal), "all entries shows the saved category instead of the pending unclassified label");
            picker.Close();
        });
        await ctx.Write(() => ctx.Catalog.Remove([added.Id]));

        async Task RejectWrite(bool reject)
        {
            await Task.Run(() =>
            {
                using var sql = new SqliteConnection("Data Source=" + Path.Combine(ctx.Db.DirectoryPath, "glassbogo.db") + ";Pooling=False"); sql.Open(); using var cmd = sql.CreateCommand();
                cmd.CommandText = reject ? "CREATE TRIGGER reject_v2_picker BEFORE UPDATE ON document BEGIN SELECT RAISE(ABORT,'QA picker write failure'); END" : "DROP TRIGGER IF EXISTS reject_v2_picker"; cmd.ExecuteNonQuery();
            });
        }
    }

    // Read-only end-to-end check against this PC's discovery sources. Real targets
    // stay in memory; the diagnostic only records counts, never names or paths.
    internal static void RunDiscoveryCheck(string output, string query)
    {
        DesktopVisibilityTest.RequireIsolatedDesktop();
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            V2Context? ctx = null;
            var data = Path.Combine(Path.GetTempPath(), "GlassBogo-discovery-ui-" + Guid.NewGuid().ToString("N"));
            try
            {
                var db = await Task.Run(() => new Database(data, true));
                ctx = new(db, qa: true, discovery: new AppDiscovery(), desktopDiscovery: new AppDiscovery(Platform.DesktopEntries));
                var owner = new Window { Width = 400, Height = 200 }; owner.Show();
                var picker = new EntryPickerWindow(owner, ctx); picker.Show(); picker.Query.Text = query;
                await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
                var initial = picker.Entries.Items.Count;
                Walk(picker).OfType<Button>().Single(b => b.Content as string == "刷新").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
                var all = picker.Entries.Items.Count;
                picker.Source.SelectedItem = "未分类";
                await Until(() => Walk(picker).OfType<Button>().Any(b => b.Content as string == "刷新" && b.IsEnabled));
                var unclassified = picker.Entries.Items.Count;
                var labeled = picker.Entries.Items.Cast<V2EntryItem>().All(row => row.Hint.Contains("未分类 · 尚未添加", StringComparison.Ordinal));
                var passed = initial > 0 && all > 0 && unclassified > 0 && labeled && db.Current.Entries.Count == 0;
                File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed, initialMatches = initial, refreshedMatches = all, unclassifiedMatches = unclassified, unclassifiedLabeled = labeled, managedEntries = db.Current.Entries.Count, assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(V2UiTest).Assembly.Location))) }));
                picker.Close(); owner.Close(); await ctx.DisposeAsync(); ctx = null;
                app.Shutdown(passed ? 0 : 2);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), ex.GetType().Name);
                if (ctx is not null) await ctx.DisposeAsync(); app.Shutdown(2);
            }
        };
        app.Run();
    }
    private static async Task VerifyCalendar(V2Context ctx, string output, Action<bool, string> check)
    {
        var calendar = ctx.CalendarWindow!; var count = ctx.Db.Current.Tasks.Count; var selected = DateOnly.FromDateTime(DateTime.Today).AddDays(7); calendar.SelectDate(selected);
        check(calendar.IsVisible && !calendar.Topmost && !calendar.ShowActivated && calendar.Owner == ctx.Dock && calendar.UsesGlass, "desktop calendar is non-topmost, owned by dock, and uses the shared glass pipeline");
        var screen = Display.PrimaryScreen; var work = screen.WorkingArea; var scale = screen.Scale;
        check(Math.Abs(calendar.Left + calendar.Width - (work.Right / scale - 16)) < 2 && Math.Abs(calendar.Top - (work.Top / scale + 16)) < 2, "new calendar starts in the primary work area's upper-right corner");
        calendar.BeginDrag(new(100, 100)); calendar.DragTo(new(100 - 120 * scale, 100 + 80 * scale)); await calendar.EndDrag();
        var draggedLeft = calendar.Left; var draggedTop = calendar.Top;
        await ctx.SetCalendarVisible(false); await Until(() => !calendar.IsVisible); await ctx.SetCalendarVisible(true); await Until(() => calendar.IsVisible);
        check(Math.Abs(calendar.Left - draggedLeft) < 1 && Math.Abs(calendar.Top - draggedTop) < 1 && ctx.Preferences.CalendarX.HasValue, "dragged position saves and survives hide, restore and data refresh");
        calendar.BeginDrag(new(0, 0)); calendar.DragTo(new(-100000, -100000)); await calendar.EndDrag();
        check(calendar.Left >= work.Left / scale && calendar.Top >= work.Top / scale, "dragging clamps the calendar to the primary working area");
        await ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { CalendarX = null, CalendarY = null }));
        var dockButton = Walk(ctx.Dock!).OfType<Button>().Single(b => b.ToolTip as string == "搜索");
        async Task Dialog(Action open, Func<V2Dialog, Task> inspect)
        {
            Exception? error = null;
            var automation = calendar.Dispatcher.InvokeAsync(async () =>
            {
                V2Dialog? dialog = null;
                try { await Task.Delay(90); dialog = Application.Current.Windows.OfType<V2Dialog>().Single(d => d.IsVisible && d.Owner == calendar); await inspect(dialog); }
                catch (Exception ex) { error = ex; if (dialog is not null) { dialog.SetBusy(false); dialog.Close(); } }
            });
            var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            watchdog.Tick += (_, _) => { watchdog.Stop(); foreach (var dialog in Application.Current.Windows.OfType<V2Dialog>().Where(d => d.Owner == calendar).ToArray()) { dialog.SetBusy(false); dialog.Close(); } };
            watchdog.Start(); try { open(); await automation.Task.Unwrap(); } finally { watchdog.Stop(); }
            if (error is not null) throw error;
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dialog(() => calendar.Edit(), async dialog => { var editor = (ScheduleEditorWindow)dialog; check(DateOnly.FromDateTime(editor.DateInput.SelectedDate!.Value) == selected, "new schedule defaults to the calendar's selected date"); await Snapshot(editor, output, "schedule-editor"); dialog.Close(); });
        check(ctx.Db.Current.Tasks.Count == count, "canceling schedule creation does not write data");
        await Dialog(() => calendar.Edit(), async dialog =>
        {
            var editor = (ScheduleEditorWindow)dialog; editor.TitleInput.Text = "QA 桌面日程"; editor.TimeInput.Text = "25:70";
            Click(Walk(editor).OfType<Button>().Single(b => b.Content as string == "保存日程"));
            await Until(() => !editor.Busy && Walk(editor).OfType<TextBlock>().Any(t => t.Text.StartsWith("时间请填写", StringComparison.Ordinal)));
            check(ctx.Db.Current.Tasks.Count == count && editor.TitleInput.Text == "QA 桌面日程", "invalid schedule time keeps the editor open and preserves inputs");
            editor.TimeInput.Text = "09:30"; editor.NotesInput.Text = "QA notes";
            Walk(editor).OfType<ComboBox>().Single(c => c.ItemsSource is Choice[]).SelectedValue = "high";
            Click(Walk(editor).OfType<Button>().Single(b => b.Content as string == "保存日程"));
        });
        var task = ctx.Db.Current.Tasks.Single(t => t.Title == "QA 桌面日程");
        await Until(() => calendar.Agenda.Items.Cast<CalendarTask>().Any(t => t.Id == task.Id));
        check(task.Date == selected.ToString("yyyy-MM-dd") && task.Time == "09:30" && task.Priority == "high" && task.Notes == "QA notes", "schedule saves title, time, notes and priority to the existing calendar data");
        check(ReferenceEquals(dockButton, Walk(ctx.Dock!).OfType<Button>().Single(b => b.ToolTip as string == "搜索")), "schedule writes do not rebuild dock buttons or reload their icons");

        await ctx.SetCalendarView("agenda"); await Until(() => calendar.Agenda.Items.Count == count + 1);
        check(calendar.Agenda.Items.Cast<CalendarTask>().Any(t => t.Id == task.Id) && Walk(calendar).OfType<Button>().Any(b => b.Content as string == $"全部日程 {count + 1}"), "all schedules shows saved tasks across dates with a visible total");
        calendar.Agenda.ScrollIntoView(task); await Snapshot(calendar, output, "all-schedules");
        await calendar.LocateSchedule(task); await Until(() => ctx.Preferences.CalendarView == "month" && calendar.Agenda.Items.Count == 1);
        check(calendar.Selected == selected, "locate schedule opens the original saved date without importing or duplicating tasks");
        await ctx.SetCalendarView("week"); await Until(() => Walk(calendar).OfType<Border>().Count(b => b.Tag is DesktopCalendarWindow.LessonCell) == 28);
        check(calendar.Width > 700 && Walk(calendar).OfType<Button>().Any(b => b.Tag is CalendarTask t && t.Id == task.Id), "weekly timetable shows seven days and four periods, including the saved course");
        await Dialog(() => calendar.Edit(date: selected, initialTime: Timetable.Slots[2].Start), dialog => { check(((ScheduleEditorWindow)dialog).TimeInput.Text == "13:45", "course creation pre-fills the selected lesson start time"); dialog.Close(); return Task.CompletedTask; });
        var courseDate = selected.AddDays(1); await calendar.MoveCourse(task.Id, courseDate, 3); calendar.SelectDate(courseDate);
        task = ctx.Db.Current.Tasks.Single(t => t.Id == task.Id);
        check(task.Date == courseDate.ToString("yyyy-MM-dd") && task.Time == "15:35" && task.Notes == "QA notes" && task.Priority == "high", "course move changes date and slot while preserving all other task data");
        var taskCount = ctx.Db.Current.Tasks.Count; var next = Walk(calendar).OfType<Button>().Single(b => b.Content as string == "›"); Click(next);
        check(calendar.Selected == courseDate.AddDays(7) && ctx.Db.Current.Tasks.Count == taskCount && !Walk(calendar).OfType<Button>().Any(b => b.Tag is CalendarTask t && t.Id == task.Id), "switching weeks never repeats or duplicates courses");
        Click(Walk(calendar).OfType<Button>().Single(b => b.Content as string == "‹"));
        await ctx.Write(() => ctx.Calendar.Save(new("qa-outside-period", "QA 课外安排", courseDate.ToString("yyyy-MM-dd"), "20:30")));
        await Until(() => calendar.Agenda.Items.Cast<CalendarTask>().Any(t => t.Id == "qa-outside-period"));
        check(!Walk(calendar).OfType<Button>().Any(b => b.Tag is CalendarTask t && t.Id == "qa-outside-period"), "out-of-period tasks stay accessible in the daily agenda without entering a lesson cell");
        await Snapshot(calendar, output, "desktop-timetable"); await ctx.Write(() => ctx.Calendar.Remove("qa-outside-period"));
        await ctx.SetCalendarView("month"); await Until(() => Math.Abs(calendar.Width - 420) < 1);
        await Snapshot(calendar, output, "desktop-calendar");
        await Dialog(() => calendar.Edit(task), dialog => { var editor = (ScheduleEditorWindow)dialog; editor.TitleInput.Text = "QA 桌面日程已编辑"; editor.TimeInput.Text = "10:45"; Click(Walk(editor).OfType<Button>().Single(b => b.Content as string == "保存日程")); return Task.CompletedTask; });
        await Until(() => calendar.Agenda.Items.Cast<CalendarTask>().Any(t => t.Id == task.Id && t.Time == "10:45"));
        calendar.Agenda.UpdateLayout(); var completion = Walk(calendar.Agenda).OfType<CheckBox>().Single(); completion.IsChecked = true; completion.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
        await Until(() => ctx.Db.Current.Tasks.Single(t => t.Id == task.Id).Completed);
        check(ctx.Db.Current.Tasks.Count == count + 1 && ctx.Db.Current.Tasks.Single(t => t.Id == task.Id).Order == task.Order, "editing and completing a schedule keeps its identity and order");
        await Dialog(() => calendar.Remove(task), dialog => { Click(Walk(dialog).OfType<Button>().Single(b => b.Content as string == "删除")); return Task.CompletedTask; });
        await Until(() => ctx.Db.Current.Tasks.Count == count && calendar.Agenda.Items.Count == 0);
        check(ctx.Modals == 0, "confirmed schedule removal updates the agenda and releases modal state");
        var handle = calendar.Handle; await ctx.SetCalendarVisible(false); await Until(() => !calendar.IsVisible);
        await ctx.SetCalendarVisible(true); await Until(() => calendar.IsVisible);
        check(calendar.Handle == handle && !calendar.Topmost, "calendar can collapse and restore alongside the dock without a new window");
        calendar.SelectDate(DateOnly.MaxValue); calendar.SelectDate(DateOnly.MinValue); calendar.SelectDate(selected);
        check(true, "calendar handles boundary years without drawing dates outside the valid range");
    }
    private static async Task VerifyDisplays(V2Context ctx, Action<bool, string> check)
    {
        var screens = Display.AllScreens; var dock = ctx.Dock!; var calendar = ctx.CalendarWindow!;
        var saved = ctx.Preferences;
        static bool Inside(LiquidWindow window, Display display) => Native.GetWindowRect(window.Handle, out var r) && r.Left >= display.WorkingArea.Left && r.Top >= display.WorkingArea.Top && r.Right <= display.WorkingArea.Right && r.Bottom <= display.WorkingArea.Bottom;
        try
        {
            foreach (var screen in screens)
            {
                var other = screens.FirstOrDefault(s => s.DeviceName != screen.DeviceName) ?? screen;
                await ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { Monitor = screen.DeviceName, CalendarMonitor = other.DeviceName, CalendarX = null, CalendarY = null }));
                await Until(() => Inside(dock, screen) && Inside(calendar, other));
                dock.ShowCategory("qa-cat-0", true);
                try { await Until(() => Inside(dock.Panel, screen) && dock.LastFrameDisplay == screen.DeviceName && dock.Panel.LastFrameDisplay == screen.DeviceName && calendar.LastFrameDisplay == other.DeviceName); }
                catch (Exception ex)
                {
                    Native.GetWindowRect(dock.Panel.Handle, out var p); Native.GetWindowRect(dock.Handle, out var d);
                    throw new InvalidOperationException($"Display route {screen.DeviceName}/{other.DeviceName}: panel={p.Left},{p.Top},{p.Right},{p.Bottom}, dock={d.Left},{d.Top},{d.Right},{d.Bottom}; work={screen.WorkingArea}; frames={dock.LastFrameDisplay}/{dock.Panel.LastFrameDisplay}/{calendar.LastFrameDisplay}; failure={ctx.Graphics.LastFailure}", ex);
                }
                check(dock.EffectiveVisualMode == "强视觉" && dock.Panel.EffectiveVisualMode == "强视觉" && calendar.EffectiveVisualMode == "强视觉", $"display {screen.DeviceName}: dock/category use their own output; calendar independently uses {other.DeviceName} ({screen.Scale:P0}/{other.Scale:P0} DPI)");
                dock.DismissPanel();
            }
            await ctx.Write(() => ctx.Db.Update(s => s.Preferences = s.Preferences with { Monitor = "QA-disconnected-display", CalendarMonitor = "QA-disconnected-calendar", CalendarX = -100000, CalendarY = 100000 }));
            await Until(() => Inside(dock, Display.PrimaryScreen) && Inside(calendar, Display.PrimaryScreen));
            check(ctx.Preferences.Monitor == "QA-disconnected-display", "missing display falls back inside primary work area without discarding saved monitor choice");
            var movable = new LiquidWindow(ctx.Graphics) { Width = 400, Height = 200, ShowInTaskbar = false, ShowActivated = false };
            try
            {
                var primary = Display.PrimaryScreen; movable.PlaceOn(primary, primary.WorkingArea.Left / primary.Scale + 20, primary.WorkingArea.Top / primary.Scale + 20); movable.Show(); movable.ReleasePlacement();
                var destination = screens.FirstOrDefault(s => !s.Primary) ?? primary;
                Native.SetWindowPos(movable.Handle, 0, destination.WorkingArea.Left + 30, destination.WorkingArea.Top + 30, (int)(400 * destination.Scale), (int)(200 * destination.Scale), 0x14);
                await Task.Delay(180);
                check(Inside(movable, destination), "manual window movement across mixed DPI does not snap back to its initial monitor");
            }
            finally { movable.Close(); }
        }
        finally
        {
            dock.DismissPanel();
            await ctx.Write(() => ctx.Db.Update(s => s.Preferences = saved));
            await Until(() => Inside(dock, ctx.DockScreen) && Inside(calendar, ctx.CalendarScreen));
        }
    }
    private static async Task VerifyMaterialContinuity(Dock2Window dock, V2Context ctx, Action<bool, string> check)
    {
        await Until(() => dock.HasRetainedFrame);
        ctx.OpenManager(); var manager = ctx.Manager!; await Until(() => ctx.Graphics.ScreenshotReady);
        for (var i = 0; i < 6; i++)
        {
            manager.ShowPage(i % 2 == 0 ? "settings" : "entries"); await Task.Delay(35);
            check(dock.Fallback.Visibility == Visibility.Collapsed && dock.RetainedImage.Visibility == Visibility.Visible, "management page switching retains the last glass frame without exposing the fallback");
        }
        var fallbackFrames = 0; EventHandler rendering = (_, _) => { if (dock.IsVisible && dock.Fallback.Visibility == Visibility.Visible) fallbackFrames++; };
        CompositionTarget.Rendering += rendering;
        try
        {
            var before = ctx.Graphics.RenderedFrames; manager.Close(); await Until(() => ctx.Graphics.RenderedFrames > before && dock.ExcludedFromCapture);
            before = ctx.Graphics.RenderedFrames; ctx.Graphics.Refresh(); await Until(() => ctx.Graphics.RenderedFrames > before); await Task.Delay(180);
        }
        finally { CompositionTarget.Rendering -= rendering; }
        check(fallbackFrames == 0 && dock.MaterialImage.Opacity == 1, "management close and graphics reset crossfade into fresh frames without fallback flashes");
    }
    private static async Task VerifyDesktopToggle(Manager2Window manager, V2Context ctx, Action<bool, string> check)
    {
        var toggle = Walk(manager).OfType<CheckBox>().Single(c => c.Content as string == "隐藏全部桌面图标");
        toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => toggle.IsEnabled && ctx.Preferences.HideDesktopIcons);
        check(ctx.DesktopIcons.Hidden && File.Exists(Path.Combine(ctx.Db.DirectoryPath, "desktop-visibility.json")), "desktop hide setting persists and creates its recovery record without touching live desktop");
        await ctx.SetDesktopIconsHidden(false); await Task.Delay(60);
        check(!ctx.DesktopIcons.Hidden && toggle.IsChecked == false && !File.Exists(Path.Combine(ctx.Db.DirectoryPath, "desktop-visibility.json")), "restoring from another entry point synchronizes settings and clears the recovery record");
        await Task.Run(() =>
        {
            using var sql = new SqliteConnection("Data Source=" + Path.Combine(ctx.Db.DirectoryPath, "glassbogo.db") + ";Pooling=False"); sql.Open(); using var cmd = sql.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER reject_icons BEFORE UPDATE ON document BEGIN SELECT RAISE(ABORT,'QA icons persistence failure'); END"; cmd.ExecuteNonQuery();
        });
        try
        {
            try { await ctx.SetDesktopIconsHidden(true); throw new InvalidOperationException("Rejected icon write was accepted"); } catch (SqliteException) { }
            check(!ctx.DesktopIcons.Hidden && !ctx.Preferences.HideDesktopIcons && !File.Exists(Path.Combine(ctx.Db.DirectoryPath, "desktop-visibility.json")), "failed desktop setting save rolls visibility back together with preferences");
        }
        finally
        {
            await Task.Run(() => { using var sql = new SqliteConnection("Data Source=" + Path.Combine(ctx.Db.DirectoryPath, "glassbogo.db") + ";Pooling=False"); sql.Open(); using var cmd = sql.CreateCommand(); cmd.CommandText = "DROP TRIGGER reject_icons"; cmd.ExecuteNonQuery(); });
        }
    }
    private static async Task VerifyAsyncLaunches(Dock2Window dock, V2Context ctx, FakeLauncher launcher, Action<bool, string> check)
    {
        var search = ctx.Search;
        search.Open(); search.Query.Text = "QA Entry 00"; await search.RefreshResults(); search.Results.UpdateLayout();
        var target = (V2EntryItem)search.Results.Items[1];
        var container = (ListBoxItem)search.Results.ItemContainerGenerator.ContainerFromIndex(1); search.Results.SelectedIndex = 0;
        search.Results.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent, Source = container });
        await Until(() => !search.IsVisible);
        check(launcher.Last == target.Entry.Id, "search mouse launch uses the clicked row rather than a stale selected row");
        search.Open(); search.Query.Text = "QA Entry 0042"; await search.RefreshResults();
        var pending = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously); launcher.Pending = pending;
        var launch = search.LaunchSelected(); search.Dismiss(false); search.Open(); search.Query.Text = "QA Entry 0043"; await search.RefreshResults();
        pending.SetResult(new(true)); await launch;
        check(search.IsVisible && search.Query.Text == "QA Entry 0043", "late search launch completion never dismisses a newly opened search session");
        search.Query.Text = "QA-no-such-result"; await search.RefreshResults();
        check(search.Results.Items.Count == 0 && search.Results.SelectedIndex == -1, "empty search results never retain an invalid selection");
        search.Query.Clear(); search.Dismiss(false); search.Open();
        check(Walk(search).OfType<TextBlock>().Any(t => t.Text == "输入名称搜索应用或已管理入口"), "reopening empty search restores its prompt"); search.Dismiss(false);
        dock.Interaction.Click("qa-cat-0"); dock.ShowCategory("qa-cat-0", false);
        pending = new(TaskCreationOptions.RunContinuationsAsynchronously); launcher.Pending = pending;
        launch = dock.Launch(ctx.Db.Current.Entries.Single(e => e.Id == "qa-entry-0"));
        dock.DismissPanel(); var token = dock.Interaction.Hover("qa-cat-1")!.Value; dock.Interaction.CommitHover("qa-cat-1", token); dock.ShowCategory("qa-cat-1", false);
        pending.SetResult(new(true)); await launch;
        check(dock.Panel.IsVisible && dock.Interaction.Active == "qa-cat-1", "late dock launch completion never closes a different category panel"); dock.DismissPanel();
    }
    private static async Task VerifyShutdown(string output, Action<bool, string> check)
    {
        var db = await Task.Run(() => new Database(Path.Combine(output, "shutdown-data"), true));
        var ctx = new V2Context(db, qa: true); ctx.OpenManager();
        var dialog = new V2Dialog(ctx.Manager!, ctx, "QA busy shutdown", 300, 180); dialog.Show(); dialog.SetBusy(true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = ctx.Write(() => { entered.SetResult(); release.Task.GetAwaiter().GetResult(); }); await entered.Task;
        var wrote = false; var queued = ctx.Write(() => wrote = true);
        var closing = ctx.DisposeAsync().AsTask(); release.SetResult(); await first;
        try { await queued; } catch (InvalidOperationException) { }
        await closing;
        check(!wrote && !dialog.IsVisible, "shutdown closes busy owned dialogs and rejects writes queued before disposal");
    }
    private static async Task VerifyDockStability(Dock2Window dock, V2Context ctx, Action<bool, string> check)
    {
        for (var i = 0; i < 4; i++)
        {
            var before = ctx.Graphics.RenderedFrames;
            dock.Left += 4; dock.Width -= 12; dock.MaterialDirty = true; ctx.Graphics.Wake();
            await Task.Delay(5);
            dock.Left -= 3; dock.Width += 8; dock.MaterialDirty = true; ctx.Graphics.Wake();
            await Until(() => ctx.Graphics.RenderedFrames > before && Native.GetWindowRect(dock.Handle, out var bounds)
                && dock.Material.PixelWidth == bounds.Right - bounds.Left && dock.Material.PixelHeight == bounds.Bottom - bounds.Top);
        }
        check(true, "rapid dock move/resize keeps the material aligned with the physical HWND bounds");
        dock.Render(); await Task.Delay(100);
        for (var i = 0; i < 4; i++)
        {
            var before = ctx.Graphics.RenderedFrames;
            ctx.OpenManager();
            if (i % 2 == 0) await Until(() => ctx.Graphics.ScreenshotReady);
            ctx.Manager!.Close();
            await Until(() => ctx.Graphics.RenderedFrames > before && dock.ExcludedFromCapture && !ctx.Graphics.ScreenshotReady);
        }
        check(ctx.Graphics.ClientCount == 3 && ctx.Graphics.LastFailure is null, "repeated management close restores glass windows without stale screenshot state");
    }
    private static uint Affinity(Window window)
    {
        if (!GetWindowDisplayAffinity(new WindowInteropHelper(window).Handle, out var affinity)) throw new InvalidOperationException("Cannot inspect screenshot affinity");
        return affinity;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("UI/GPU state did not settle"); await Task.Delay(50); }
    }
    private static async Task Snapshot(LiquidWindow window, string output, string name)
    {
        await Task.Delay(120);
        var material = window.MaterialImage.Visibility; var fallback = window.Fallback.Visibility; var retained = window.RetainedImage.Visibility;
        window.Fallback.SetResourceReference(Border.BackgroundProperty, "SurfaceFallback");
        window.MaterialImage.Visibility = Visibility.Collapsed; window.RetainedImage.Visibility = Visibility.Collapsed; window.Fallback.Visibility = Visibility.Visible; window.UpdateLayout();
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)), Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window); bitmap.Freeze(); window.MaterialImage.Visibility = material; window.RetainedImage.Visibility = retained; window.Fallback.Visibility = fallback;
        window.Fallback.SetResourceReference(Border.BackgroundProperty, window.UsesGlass ? "DockFallback" : "SurfaceFallback");
        await Task.Run(() => { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file); });
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root; for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
