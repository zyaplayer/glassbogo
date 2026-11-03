using System.IO;
using GlassBogo.App;
using GlassBogo.Core;
using GlassBogo.PluginSdk;
using System.Text.Json;
using System.Diagnostics;

if (args.Length is 2 or 3 && args[0] == "--calendar-check")
{
    using var source = JsonDocument.Parse(File.ReadAllText(args[1])); var sourceRoot = source.RootElement;
    if (sourceRoot.TryGetProperty("data", out var wrapped)) sourceRoot = wrapped;
    var tasks = sourceRoot.GetProperty("tasks").Deserialize<List<CalendarTask>>(DataJson.Options)!;
    var categories = sourceRoot.TryGetProperty("categories", out var categoryValue) ? categoryValue.Deserialize<List<TaskCategory>>(DataJson.Options)! : [];
    var path = Path.Combine(Environment.GetEnvironmentVariable("GLASSBOGO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo"), "glassbogo.db");
    using var sql = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); sql.Open();
    using var command = sql.CreateCommand(); command.CommandText = "SELECT json FROM document WHERE id=1";
    var currentJson = (string)command.ExecuteScalar()!;
    var state = JsonSerializer.Deserialize<State>(currentJson, DataJson.Options)!;
    string? targetBackup = null, sourceBackup = null;
    if (args.Length == 3)
    {
        if (args[2] != "--backup" || tasks.Any(t => !state.Tasks.Contains(t)) || categories.Any(c => !state.TaskCategories.Contains(c))) throw new InvalidOperationException("源数据未完全匹配，不能标记迁移完成。");
        var recovery = Path.Combine(Path.GetDirectoryName(path)!, "recovery"); var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff");
        targetBackup = Path.Combine(recovery, "before-timetable-" + stamp + ".json"); sourceBackup = Path.Combine(recovery, "desktopcalendar-source-" + stamp + ".json");
        Database.AtomicWrite(targetBackup, currentJson); File.Copy(Path.GetFullPath(args[1]), sourceBackup, false);
    }
    Console.WriteLine(JsonSerializer.Serialize(new { sourceTasks = tasks.Count, sourceCategories = categories.Count, targetTasks = state.Tasks.Count, exactTasks = tasks.Count(t => state.Tasks.Contains(t)), missingIds = tasks.Count(t => !state.Tasks.Any(x => x.Id == t.Id)), exactCategories = categories.Count(c => state.TaskCategories.Contains(c)), targetBackup, sourceBackup })); return;
}
if (args.Length == 2 && args[0] == "--icon-check")
{
    var path = Path.Combine(Environment.GetEnvironmentVariable("GLASSBOGO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo"), "glassbogo.db");
    using var sql = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString()); sql.Open();
    using var command = sql.CreateCommand(); command.CommandText = "SELECT json FROM document WHERE id=1";
    var state = JsonSerializer.Deserialize<State>((string)command.ExecuteScalar()!, DataJson.Options)!;
    var matches = state.Entries.Where(e => e.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase) || e.Target.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
    var reports = new List<object>();
    foreach (var entry in matches)
    {
        var source = entry.IconSource;
        var sourceRead = !string.IsNullOrWhiteSpace(source) && (File.Exists(source) || Directory.Exists(source)) && await Platform.Sta(() => Native.GetIcon(source)) is not null;
        var targetRead = (File.Exists(entry.Target) || Directory.Exists(entry.Target)) && await Platform.Sta(() => Native.GetIcon(entry.Target)) is not null;
        reports.Add(new { entry.Kind, targetFile = Path.GetFileName(entry.Target), hasIconSource = source is not null, sourceExists = File.Exists(source) || Directory.Exists(source), targetExists = File.Exists(entry.Target) || Directory.Exists(entry.Target), sourceRead, targetRead });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { matched = reports.Count, entries = reports })); return;
}
if (args.Length == 2 && args[0] == "--discover-check")
{
    using var discovery = new AppDiscovery();
    var apps = await discovery.GetAsync(CancellationToken.None, refresh: true);
    var matches = apps.Where(e => e.Name.Contains(args[1], StringComparison.CurrentCultureIgnoreCase)).ToArray();
    var managedCount = -1; var managedMatches = -1; var combinedMatches = -1; var renamedMatches = -1;
    var workingDirectoryAliases = -1; var shortcutAliases = -1; var classifiedNameShadows = -1;
    var path = Path.Combine(Environment.GetEnvironmentVariable("GLASSBOGO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo"), "glassbogo.db");
    if (File.Exists(path))
    {
        using var sql = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        sql.Open(); using var command = sql.CreateCommand(); command.CommandText = "SELECT json FROM document WHERE id=1";
        var state = JsonSerializer.Deserialize<State>((string)command.ExecuteScalar()!, DataJson.Options)!;
        managedCount = state.Entries.Count;
        bool Match(Entry e) => e.Name.Contains(args[1], StringComparison.CurrentCultureIgnoreCase) || e.Target.Contains(args[1], StringComparison.CurrentCultureIgnoreCase);
        managedMatches = state.Entries.Count(Match);
        combinedMatches = EntryPickerWindow.Combined(state.Entries, apps).Count(Match);
        renamedMatches = matches.Count(e => state.Entries.Any(m => Catalog.Identity(m) == Catalog.Identity(e) && !Match(m)));
        var extra = apps.Where(e => !state.Entries.Any(m => Catalog.Identity(m) == Catalog.Identity(e))).ToArray();
        workingDirectoryAliases = extra.Count(e => state.Entries.Any(m => Catalog.Identity(m with { WorkingDirectory = "" }) == Catalog.Identity(e with { WorkingDirectory = "" })));
        shortcutAliases = extra.Count(e => e.IconSource is not null && state.Entries.Any(m => string.Equals(m.Target, e.IconSource, StringComparison.OrdinalIgnoreCase)));
        classifiedNameShadows = EntryPickerWindow.Combined(state.Entries, apps).Count(e => e.CategoryId.Length == 0 && state.Entries.Any(m => m.CategoryId.Length > 0 && string.Equals(m.Name, e.Name, StringComparison.CurrentCultureIgnoreCase)));
    }
    Console.WriteLine(JsonSerializer.Serialize(new { found = matches.Length > 0, matchCount = matches.Length, matchedKinds = matches.Select(e => e.Kind).Distinct().ToArray(), managedCount, managedMatches, combinedMatches, renamedMatches, workingDirectoryAliases, shortcutAliases, classifiedNameShadows }));
    Environment.ExitCode = matches.Length > 0 ? 0 : 1;
    return;
}

int passed = 0, failed = 0;
async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
void Check(bool ok) { if (!ok) throw new Exception("assertion failed"); }
var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var sample = Path.Combine(root, "artifacts", "sample-plugin");
var fault = Path.Combine(root, "artifacts", "fault-plugin");
var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(sample, "plugin.json")), Protocol.Json)!;
await Test("真实管道请求与命令", async () => { await using var session = new PluginSession(sample, manifest); var first = await session.Request("card"); Check(first.Text.Contains("0 次")); var next = await session.Request("card", "finish"); Check(next.Text.Contains("1 次")); });
await Test("插件声明事件通知", async () => { await using var session = new PluginSession(sample, manifest); var view = await session.Request("event", "workspace.changed"); Check(view.Title == "专注小记"); });
await Test("插件异常返回", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); try { await session.Request("card"); throw new Exception("did not fail"); } catch (InvalidOperationException e) { Check(e.Message.Contains("测试错误")); } });
await Test("插件崩溃不影响宿主", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); try { await session.Request("card", "crash"); throw new Exception("did not fail"); } catch (IOException) { } });
await Test("插件超时结束进程", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); var timer = Stopwatch.StartNew(); try { await session.Request("card", "timeout"); throw new Exception("did not fail"); } catch (OperationCanceledException) { Check(timer.Elapsed.TotalSeconds < 7); } });
await Test("调用取消", async () => { await using var session = new PluginSession(fault, new("test.fault", "test", "1", 1, "FaultPlugin.exe", ["card"])); using var cancellation = new CancellationTokenSource(100); try { await session.Request("card", "timeout", cancellation: cancellation.Token); throw new Exception("did not cancel"); } catch (OperationCanceledException) { } });
await Test("插件安装停用卸载", () => { using var db = new Database(Path.Combine(Path.GetTempPath(), "GlassBogo-plugin-tests-" + Guid.NewGuid().ToString("N"))); var plugins = new Plugins(db); plugins.Install(sample); Check(plugins.List().Single().Manifest!.Id == manifest.Id); Check(!db.Current.Plugins[manifest.Id]); plugins.Uninstall(manifest); Check(!plugins.List().Any()); return Task.CompletedTask; });
await Test("快捷方式解析不改写文件", async () => { var dir = Path.Combine(Path.GetTempPath(), "GlassBogo-link-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "sample.lnk"); await Platform.Sta(() => { dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!; dynamic link = shell.CreateShortcut(path); try { link.TargetPath = Environment.ProcessPath; link.Arguments = "--profile A"; link.WorkingDirectory = dir; link.Save(); } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); } return true; }); var before = File.ReadAllBytes(path); var entry = await Platform.Sta(() => Platform.Resolve(path)); Check(entry.Arguments == "--profile A" && entry.WorkingDirectory == dir && File.ReadAllBytes(path).SequenceEqual(before)); });
await Test("错误目标启动反馈", async () => { var result = await new Launcher().LaunchAsync(new("x", "missing", @"C:\does-not-exist-glassbogo\app.exe")); Check(!result.Success && !string.IsNullOrWhiteSpace(result.Error)); });
await Test("插件全部声明能力分别调用", async () => { await using var session = new PluginSession(sample, manifest); foreach (var capability in manifest.Capabilities) { var view = await session.Request(capability, entryName: capability == "entry-menu" ? "测试入口" : null); Check(!string.IsNullOrWhiteSpace(view.Title)); } });
await Test("应用发现共享扫描、消费者取消与过期目标过滤", async () =>
{
    var source = new TaskCompletionSource<List<Entry>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var scans = 0;
    using var discovery = new AppDiscovery(() => { Interlocked.Increment(ref scans); return source.Task; });
    using var cancel = new CancellationTokenSource();
    var first = discovery.GetAsync(cancel.Token); var second = discovery.GetAsync(CancellationToken.None);
    cancel.Cancel();
    try { await first; throw new Exception("did not cancel"); } catch (OperationCanceledException) { }
    source.SetResult([new("one", "Synthetic app", "QA!app", "app"), new("two", "Duplicate app", "QA!app", "app"), new("missing", "Missing", @"C:\does-not-exist-glassbogo\missing.exe")]);
    Check((await second).Count == 1 && (await discovery.GetAsync(CancellationToken.None)).Count == 1 && scans == 1);
    discovery.Dispose();
    try { await discovery.GetAsync(CancellationToken.None); throw new Exception("accepted disposed consumer"); } catch (ObjectDisposedException) { }
});
await Test("强制应用刷新绕过两分钟缓存", async () =>
{
    var scans = 0;
    using var discovery = new AppDiscovery(() => Task.FromResult(new List<Entry> { new("app", "Scan " + Interlocked.Increment(ref scans), "QA!refresh", "app") }));
    var first = await discovery.GetAsync(CancellationToken.None);
    Check((await discovery.GetAsync(CancellationToken.None)).Single().Name == first.Single().Name && scans == 1);
    Check((await discovery.GetAsync(CancellationToken.None, refresh: true)).Single().Name == "Scan 2" && scans == 2);
    Check((await discovery.GetAsync(CancellationToken.None)).Single().Name == "Scan 2" && scans == 2);
});
await Test("扫描期间刷新排队重扫且合并并发刷新请求", async () =>
{
    var old = new TaskCompletionSource<List<Entry>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var fresh = new TaskCompletionSource<List<Entry>>(TaskCreationOptions.RunContinuationsAsynchronously);
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var scans = 0;
    using var discovery = new AppDiscovery(() => { if (Interlocked.Increment(ref scans) == 1) return old.Task; entered.TrySetResult(); return fresh.Task; });
    var initial = discovery.GetAsync(CancellationToken.None);
    var refresh1 = discovery.GetAsync(CancellationToken.None, refresh: true); var refresh2 = discovery.GetAsync(CancellationToken.None, refresh: true);
    Check(scans == 1);
    old.SetResult([new("old", "Old snapshot", "QA!old", "app")]);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Check((await initial).Single().Name == "Old snapshot" && scans == 2);
    fresh.SetResult([new("fresh", "New snapshot", "QA!new", "app")]);
    Check((await refresh1).Single().Name == "New snapshot" && (await refresh2).Single().Name == "New snapshot" && scans == 2);
});
await Test("选择列表合并快捷方式来源、保留分类与不同启动配置", () =>
{
    var shortcut = new Entry("saved-link", "已分类链接", @"C:\QA\Desktop\editor.lnk", CategoryId: "cat", Pinned: true, PinOrder: 4);
    var resolved = new Entry("resolved", "已分类链接", @"C:\QA\editor.exe", WorkingDirectory: @"C:\QA", IconSource: shortcut.Target);
    var savedResolved = resolved with { Id = "saved-resolved", Name = "自定义名称", WorkingDirectory = "", CategoryId = "cat" };
    var otherProfile = resolved with { Id = "other-profile", Arguments = "--profile B" };
    var sameNameOtherFile = resolved with { Id = "unrelated", Target = @"C:\QA\other.exe", IconSource = @"C:\QA\other.lnk" };
    var rows = EntryPickerWindow.Combined([shortcut], [resolved, resolved with { Id = "duplicate" }, sameNameOtherFile]).ToArray();
    Check(rows.Length == 2 && rows[0] == shortcut && rows[1] == sameNameOtherFile);
    rows = EntryPickerWindow.Combined([savedResolved], [resolved, otherProfile, sameNameOtherFile]).ToArray();
    Check(rows.Length == 3 && rows[0] == savedResolved && rows.Contains(otherProfile) && rows.Contains(sameNameOtherFile));
    var savedProfile = savedResolved with { Id = "saved-profile", WorkingDirectory = @"C:\QA\profile", CategoryId = "second" };
    rows = EntryPickerWindow.Combined([savedResolved, savedProfile], [resolved]).ToArray();
    Check(rows.SequenceEqual(new[] { savedResolved, savedProfile }));
    var alternateLink = resolved with { IconSource = @"C:\QA\PublicDesktop\editor.lnk", WorkingDirectory = @"C:\QA\another-default" };
    rows = EntryPickerWindow.Combined([savedResolved, savedProfile], [alternateLink, otherProfile]).ToArray();
    Check(rows.SequenceEqual(new[] { savedResolved, savedProfile, otherProfile }));
    return Task.CompletedTask;
});
await Test("完整刷新清理失效引用、恢复点及真实目标保护", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "GlassBogo-refresh-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    var target = Path.Combine(dir, "live.txt"); await File.WriteAllTextAsync(target, "unchanged");
    using var db = new Database(Path.Combine(dir, "data"), true);
    var live = new Entry("live", "自定义名称", target, CategoryId: "cat", Pinned: true, PinOrder: 7, CategoryOrder: 3);
    var missing = new Entry("missing", "过期文件", Path.Combine(dir, "gone.txt"), Pinned: true);
    var unknown = new Entry("offline", "离线目标", @"\\unavailable-glassbogo\share\file.txt");
    db.Update(s => { s.Categories.Add(new("cat", "分类")); s.Entries.AddRange([live, missing, unknown]); s.Plugins["legacy"] = true; });
    using var discovery = new AppDiscovery(() => Task.FromResult(new List<Entry> { live with { Id = "candidate", Name = "系统名称" }, new("new", "新应用", "QA!fresh", "app") }));
    var refresh = new DesktopRefresh(discovery);
    var result = await refresh.Run(db, action => Task.Run(action), CancellationToken.None);
    Check(result.Removed == 1 && result.Unverified == 1 && db.Current.Entries.Count == 2 && db.Current.Entries.Single(e => e.Id == live.Id) == live);
    Check(db.Current.Plugins["legacy"] && File.ReadAllText(target) == "unchanged" && !File.Exists(missing.Target));
    var recovery = Directory.GetFiles(Path.Combine(db.DirectoryPath, "recovery"), "*.json").Single();
    Check(JsonSerializer.Deserialize<State>(File.ReadAllText(recovery), DataJson.Options)!.Entries.Contains(missing));
    var candidates = EntryPickerWindow.Combined(db.Current.Entries, result.Candidates).ToArray();
    Check(candidates.Single(e => Catalog.Identity(e) == Catalog.Identity(live)) == live && candidates.Any(e => e.Name == "新应用") && db.Current.Entries.All(e => e.Id != "new"));
    var again = await refresh.Run(db, action => Task.Run(action), CancellationToken.None);
    Check(again.Removed == 0 && Directory.GetFiles(Path.Combine(db.DirectoryPath, "recovery"), "*.json").Length == 1);
    using var reopened = new Database(db.DirectoryPath, true); Check(reopened.Current.Entries.SequenceEqual(db.Current.Entries));
});
await Test("刷新尊重并发重新定位、取消和写入失败", async () =>
{
    using var db = new Database(Path.Combine(Path.GetTempPath(), "GlassBogo-refresh-race-" + Guid.NewGuid().ToString("N")), true);
    var entry = new Entry("stale", "待核实", @"C:\Generated-GlassBogo-QA\stale.txt"); db.Update(s => s.Entries.Add(entry));
    using var discovery = new AppDiscovery(() => Task.FromResult(new List<Entry>()));
    var catalog = new Catalog(db);
    var refresh = new DesktopRefresh(discovery, e => { catalog.Relocate(e.Id, e with { Target = Environment.ProcessPath! }); return Task.FromResult(TargetState.Missing); });
    Check((await refresh.Run(db, a => Task.Run(a), CancellationToken.None)).Removed == 0 && db.Current.Entries.Count == 1);
    using var cancel = new CancellationTokenSource();
    refresh = new DesktopRefresh(discovery, _ => { cancel.Cancel(); return Task.FromResult(TargetState.Missing); });
    try { await refresh.Run(db, a => Task.Run(a), cancel.Token); throw new Exception("cancel ignored"); } catch (OperationCanceledException) { }
    Check(db.Current.Entries.Count == 1);
    refresh = new DesktopRefresh(discovery, _ => Task.FromResult(TargetState.Missing));
    try { await refresh.Run(db, _ => Task.FromException(new IOException("write unavailable")), CancellationToken.None); throw new Exception("write accepted"); } catch (IOException) { }
    Check(db.Current.Entries.Count == 1);
});
await Test("刷新提交前重新出现的目标不会被清理", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "GlassBogo-reappear-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    var path = Path.Combine(dir, "restored.txt"); using var db = new Database(Path.Combine(dir, "data"), true);
    db.Update(s => s.Entries.Add(new("restored", "恢复中的文件", path)));
    using var discovery = new AppDiscovery(() => Task.FromResult(new List<Entry>()));
    var result = await new DesktopRefresh(discovery).Run(db, async action => { await File.WriteAllTextAsync(path, "keep"); await Task.Run(action); }, CancellationToken.None);
    Check(result.Removed == 0 && db.Current.Entries.Count == 1 && File.ReadAllText(path) == "keep" && !Directory.Exists(Path.Combine(db.DirectoryPath, "recovery")));
});
await Test("根目录和点号文件夹显示名称有效", async () =>
{
    var drive = Path.GetPathRoot(Environment.SystemDirectory)!;
    Check(Platform.TargetName(drive, true) == drive && Platform.TargetName(@"C:\QA\folder.with.dots", true) == "folder.with.dots");
    Check(Platform.TargetName(@"C:\QA\.config", true) == ".config" && Platform.TargetName(@"C:\QA\.env") == ".env");
    var resolved = await Platform.Sta(() => Platform.Resolve(drive)); Check(resolved.Name == drive);
});
await Test("图标刷新丢弃旧异步结果避免缓存回填", async () =>
{
    var old = new System.Windows.Media.DrawingImage(); old.Freeze(); var fresh = new System.Windows.Media.DrawingImage(); fresh.Freeze();
    var pending = new TaskCompletionSource<System.Windows.Media.ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
    var cache = new IconCache(_ => ++calls == 1 ? pending.Task : Task.FromResult<System.Windows.Media.ImageSource?>(fresh));
    var entry = new Entry("icon", "图标", "QA!icon", "app"); var first = cache.Get(entry); cache.Clear(); pending.SetResult(old); await first;
    Check(ReferenceEquals(await cache.Get(entry), fresh) && ReferenceEquals(await cache.Get(entry), fresh) && calls == 2);
});
await Test("失效图标来源继续解析目标并在短暂失败后重试", async () =>
{
    var image = new System.Windows.Media.DrawingImage(); image.Freeze(); var seen = new List<string>();
    var cache = new IconCache(source => { seen.Add(source); return Task.FromResult<System.Windows.Media.ImageSource?>(source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image : null); });
    var entry = new Entry("icon-fallback", "图标", @"C:\QA\app.exe", IconSource: @"C:\QA\gone.lnk");
    Check(ReferenceEquals(await cache.Get(entry), image) && seen.SequenceEqual(new[] { entry.IconSource!, entry.Target }));
    var recovered = false; var calls = 0;
    cache = new IconCache(_ => { calls++; return Task.FromResult<System.Windows.Media.ImageSource?>(recovered ? image : null); });
    var placeholder = await cache.Get(entry with { IconSource = "" }); Check(!ReferenceEquals(placeholder, image));
    await cache.Get(entry with { IconSource = "" }); Check(calls == 1);
    recovered = true; await Task.Delay(3100); Check(ReferenceEquals(await cache.Get(entry with { IconSource = "" }), image) && calls == 2);
    recovered = false; cache.Clear(); Check(ReferenceEquals(await cache.Get(entry with { IconSource = "" }), image));
});
await Test("隐藏桌面可恢复、保留原状态且不碰目标文件", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "GlassBogo-icons-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
    var file = Path.Combine(dir, "target.txt"); await File.WriteAllTextAsync(file, "untouched");
    var backend = new TestDesktopIconBackend();
    await using (var controller = new DesktopIconController(dir, backend, listen: false))
    {
        controller.Initialize(false); controller.SetHidden(true); controller.SetHidden(true);
        Check(!backend.Visible && File.ReadAllText(Path.Combine(dir, "desktop-visibility.json")) == "true");
        backend.Show(true); controller.Reconcile(); Check(!backend.Visible);
        controller.SetHidden(false); Check(backend.Visible && !File.Exists(Path.Combine(dir, "desktop-visibility.json")));
        backend.Show(false); controller.SetHidden(true); controller.SetHidden(false); Check(!backend.Visible);
        backend.Show(true); controller.SetHidden(true);
    }
    Check(backend.Visible && File.ReadAllText(file) == "untouched" && !File.Exists(Path.Combine(dir, "desktop-visibility.json")));
});
await Test("桌面隐藏异常恢复与恢复失败保留记录", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "GlassBogo-icon-recovery-" + Guid.NewGuid().ToString("N")); var backend = new TestDesktopIconBackend();
    var old = new DesktopIconController(dir, backend, listen: false); old.SetHidden(true);
    await using (var restarted = new DesktopIconController(dir, backend, listen: false)) { restarted.Initialize(false); Check(backend.Visible); }
    await old.DisposeAsync();
    await using var controller = new DesktopIconController(dir, backend, listen: false); controller.SetHidden(true); backend.Available = false;
    try { controller.SetHidden(false); throw new Exception("restore failure ignored"); } catch (IOException) { }
    Check(File.Exists(Path.Combine(dir, "desktop-visibility.json")));
    backend.Available = true; controller.SetHidden(false); Check(backend.Visible);
});
Console.WriteLine($"RESULT passed={passed} failed={failed}");
Environment.ExitCode = failed == 0 ? 0 : 1;

internal sealed class TestDesktopIconBackend : IDesktopIconBackend
{
    internal bool Available = true;
    private bool visible = true;
    public bool Visible => Available ? visible : throw new IOException("QA desktop unavailable");
    public void Show(bool value) { if (!Available) throw new IOException("QA desktop unavailable"); visible = value; }
}
