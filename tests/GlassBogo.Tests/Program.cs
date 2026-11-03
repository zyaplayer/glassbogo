using GlassBogo.Core;
using GlassBogo.PluginSdk;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using GlassBogo.App;

var root = Path.Combine(Path.GetTempPath(), "GlassBogo-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0, failed = 0;
void Check(bool success, string message = "断言失败") { if (!success) throw new Exception(message); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("预期失败却成功了"); }
void Test(string name, Action action) { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
Database New() => new(Path.Combine(root, Guid.NewGuid().ToString("N")));
Entry Item(string name = "Demo", string target = @"C:\Apps\demo.exe") => new(Guid.NewGuid().ToString("N"), name, target);
Test("入口去重及参数区别", () => { using var db = New(); var c = new Catalog(db); var item = c.Add(Item()); Check(c.Add(Item()).Id == item.Id); c.Add(Item() with { Arguments = "--new" }); Check(db.Current.Entries.Count == 2); });
Test("单归属与分类移除", () => { using var db = New(); var c = new Catalog(db); c.SaveCategory(null, "工作"); var id = db.Current.Categories.Last().Id; var e = c.Add(Item()); c.Move([e.Id], id); Check(db.Current.Entries.Single().CategoryId == id); c.RemoveCategory(id); Check(db.Current.Entries.Single().CategoryId == ""); });
Test("分类名称与重复校验", () => { using var db = New(); var c = new Catalog(db); Throws(() => c.SaveCategory(null, " ")); c.SaveCategory(null, "工作"); Throws(() => c.SaveCategory(null, "工作")); Check(db.Current.Categories.Count == 1); });
Test("调整顺序及同项拖放", () => { using var db = New(); var c = new Catalog(db); var a = c.Add(Item("A", "a")); var b = c.Add(Item("B", "b")); c.Reorder(b.Id, a.Id); Check(db.Current.Entries[0].Id == b.Id); c.Reorder(a.Id, a.Id); Check(db.Current.Entries.Count == 2); });
Test("数据验证失败回滚", () => { using var db = New(); var c = new Catalog(db); var item = c.Add(Item()); Throws(() => c.Move([item.Id], "absent")); Check(db.Current.Entries.Single().CategoryId == ""); });
Test("SQL 写入失败保留内存", () => { using var db = New(); new Catalog(db).Add(Item()); using var connection = new SqliteConnection("Data Source=" + Path.Combine(db.DirectoryPath, "glassbogo.db")); connection.Open(); using var cmd = connection.CreateCommand(); cmd.CommandText = "CREATE TRIGGER reject_update BEFORE UPDATE ON document BEGIN SELECT RAISE(ABORT,'test'); END"; cmd.ExecuteNonQuery(); Throws(() => db.Update(s => s.Entries.Clear())); Check(db.Current.Entries.Count == 1); });
Test("重启持久化", () => { string path; using (var db = New()) { new Catalog(db).Add(Item()); path = db.DirectoryPath; } using var reopened = new Database(path); Check(reopened.Current.Entries.Count == 1); });
Test("原文件完整性", () => { var directory = Path.Combine(root, "files"); Directory.CreateDirectory(directory); var file = Path.Combine(directory, "sample.txt"); File.WriteAllText(file, "keep this unchanged"); var before = File.ReadAllBytes(file); using var db = New(); var c = new Catalog(db); var e = c.Add(Item("sample", file)); c.SaveCategory(null, "文档"); var cat = db.Current.Categories.Last().Id; c.Move([e.Id], cat); c.RemoveCategory(cat); c.Remove([e.Id]); Check(File.ReadAllBytes(file).SequenceEqual(before)); Check(Directory.GetFiles(directory).Length == 1); });
Test("导出导入及恢复点", () => { using var db = New(); var c = new Catalog(db); c.Add(Item()); var file = Path.Combine(root, "backup.json"); db.Export(file); db.Update(s => s.Entries.Clear()); db.Import(file); Check(db.Current.Entries.Count == 1); Check(Directory.GetFiles(Path.Combine(db.DirectoryPath, "recovery")).Length == 1); });
Test("损坏备份不覆盖", () => { using var db = New(); new Catalog(db).Add(Item()); var file = Path.Combine(root, "bad.json"); File.WriteAllText(file, "{broken"); Throws(() => db.Import(file)); Check(db.Current.Entries.Count == 1); });
Test("损坏数据库不重建", () => { var folder = Path.Combine(root, "bad-db"); Directory.CreateDirectory(folder); var file = Path.Combine(folder, "glassbogo.db"); File.WriteAllText(file, "broken"); Throws(() => new Database(folder)); Check(File.ReadAllText(file) == "broken"); });
Test("未知数据库版本拒绝", () => { string path; using (var db = New()) path = db.DirectoryPath; using (var conn = new SqliteConnection("Data Source=" + Path.Combine(path, "glassbogo.db"))) { conn.Open(); using var cmd = conn.CreateCommand(); cmd.CommandText = "PRAGMA user_version=99"; cmd.ExecuteNonQuery(); } Throws(() => new Database(path)); });
Test("日备份只生成一次", () => { using var db = New(); for (int i = 0; i < 3; i++) new Catalog(db).Add(Item(i.ToString(), i.ToString())); Check(Directory.GetFiles(Path.Combine(db.DirectoryPath, "backups")).Length == 1); });
Test("日历闰年与无效日期", () => { using var db = New(); var cal = new CalendarService(db); cal.Save(new("1", "有效", "2028-02-29")); Throws(() => cal.Save(new("2", "无效", "2027-02-29"))); Throws(() => cal.Save(new("3", "时间", "2028-02-29", "25:00"))); Check(db.Current.Tasks.Count == 1); });
Test("日历完成筛选与搜索", () => { using var db = New(); var cal = new CalendarService(db); cal.Save(new("1", "交报告", "2026-10-28", Notes: "季度会议")); cal.Save(new("2", "完成", "2026-10-28", Completed: true)); Check(cal.Query(status: "open").Count() == 1); Check(cal.Query(query: "季度").Single().Id == "1"); });
Test("任务分类删除保留任务", () => { using var db = New(); var cal = new CalendarService(db); cal.Save(new("1", "任务", "2026-10-28", CategoryId: "work")); cal.RemoveCategory("work"); Check(db.Current.Tasks.Single().CategoryId is null); });
Test("任务拖动排序", () => { using var db = New(); var c = new CalendarService(db); c.Save(new("a", "A", "2026-10-28", Order: 0)); c.Save(new("b", "B", "2026-10-28", Order: 1)); c.Reorder("b", "a"); Check(c.Query().First().Id == "b"); });
Test("中文速记明天", () => { var t = QuickAdd.Parse("明天18点交报告", new(2026, 10, 28)); Check(t.Date == "2026-10-29" && t.Time == "18:00" && t.Title == "交报告"); });
Test("中文速记下午三点半", () => { var t = QuickAdd.Parse("周五下午三点半交报告", new(2026, 10, 28)); Check(t.Date == "2026-10-30" && t.Time == "15:30"); });
Test("中文速记跨月课表", () => { var t = QuickAdd.Parse("下周一第二节高数", new(2026, 10, 28)); Check(t.Date == "2026-11-02" && t.Time == "09:50" && t.Title == "高数"); });
Test("中文速记非法时间与空标题", () => { Throws(() => QuickAdd.Parse("明天25点报告", new(2026, 10, 28))); Throws(() => QuickAdd.Parse("明天18点", new(2026, 10, 28))); Throws(() => QuickAdd.Parse("2026-02-30 报告", new(2026, 10, 28))); });
Test("旧日历重复导入不增加任务", () => { using var db = New(); var file = Path.Combine(root, "old-calendar.json"); File.WriteAllText(file, JsonSerializer.Serialize(new { version = 1, tasks = new[] { new CalendarTask("old", "旧任务", "2026-10-28", Notes: "原备注", CategoryId: "study") }, categories = new[] { new TaskCategory("study", "学习", "#123456") } }, DataJson.Options)); var before = File.ReadAllText(file); Migration.Calendar(db, file); Migration.Calendar(db, file); Check(db.Current.Tasks.Count == 1 && db.Current.Tasks[0].Notes == "原备注"); Check(File.ReadAllText(file) == before); });
Test("旧分类首个归属与重复入口", () => { using var db = New(); var dir = Path.Combine(root, "old-desk"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "apps.json"), """{"schemaVersion":1,"items":[{"id":"a","displayName":"A","target":"C:\\a.exe"},{"id":"b","displayName":"A2","target":"C:\\a.exe"}]}"""); File.WriteAllText(Path.Combine(dir, "dock-layout.json"), """{"categories":[{"id":"x","name":"X","itemIds":["a"]},{"id":"y","name":"Y","itemIds":["a"]}]}"""); Migration.GlassDesk(db, dir); Check(db.Current.Entries.Count == 1 && db.Current.Entries[0].CategoryId == "import-x"); Migration.GlassDesk(db, dir); Check(db.Current.Entries.Count == 1); });
Test("插件兼容性与入口边界", () => { Protocol.Validate(new("test.plugin", "test", "1", 1, "Plugin.exe", ["card"])); Throws(() => Protocol.Validate(new("../escape", "x", "1", 1, "Plugin.exe", []))); Throws(() => Protocol.Validate(new("test.plugin", "x", "1", 9, "Plugin.exe", []))); Throws(() => Protocol.Validate(new("test.plugin", "x", "1", 1, "../Plugin.exe", []))); Throws(() => Protocol.Validate(new("test.plugin", "x", "1", 1, "Plugin.exe", ["admin"]))); });
Test("参数大小写保留不同入口", () => { using var db = New(); var c = new Catalog(db); c.Add(Item() with { Arguments = "--profile A" }); c.Add(Item() with { Arguments = "--profile a" }); Check(db.Current.Entries.Count == 2); });
Test("损坏数据库从备份恢复", () => { var dir = Path.Combine(root, "recover-db"); Directory.CreateDirectory(dir); var path = Path.Combine(dir, "glassbogo.db"); File.WriteAllText(path, "broken-original"); var backup = Path.Combine(root, "restore-source.json"); using (var db = New()) { new Catalog(db).Add(Item()); db.Export(backup); } var archive = DatabaseRecovery.Restore(dir, backup); Check(File.ReadAllText(Path.Combine(archive, "glassbogo.db")) == "broken-original"); using var recovered = new Database(dir); Check(recovered.Current.Entries.Count == 1); });
Test("首次日历导入保留原分类颜色", () => { using var db = New(); var file = Path.Combine(root, "colors.json"); File.WriteAllText(file, JsonSerializer.Serialize(new { version = 1, tasks = new[] { new CalendarTask("1", "旧任务", "2026-10-28", CategoryId: "work") }, categories = new[] { new TaskCategory("work", "工作", "#123456") } }, DataJson.Options)); Migration.Calendar(db, file); Check(db.Current.TaskCategories.Single(c => c.Id == "work").Color == "#123456"); new CalendarService(db).SaveCategory(new("work", "工作", "#654321")); Migration.Calendar(db, file); Check(db.Current.TaskCategories.Single(c => c.Id == "work").Color == "#654321"); });
Test("500入口及10000任务规模", () => { using var db = New(); db.Update(s => { for (int i = 0; i < 500; i++) s.Entries.Add(Item("App " + i, "C:\\apps\\" + i + ".exe")); for (int i = 0; i < 10000; i++) s.Tasks.Add(new(i.ToString(), "Task " + i, new DateOnly(2026, 1, 1).AddDays(i % 365).ToString("yyyy-MM-dd"))); }); Check(new Catalog(db).Search("App").Count() == 500); Check(new CalendarService(db).Query(query: "Task 9999").Count() == 1); var watch = System.Diagnostics.Stopwatch.StartNew(); db.Update(s => s.Preferences = s.Preferences with { Theme = "dark" }); Console.WriteLine("  large-state edit ms=" + watch.ElapsedMilliseconds); });
Test("日历服务契约导入导出及同项拖放", () => { using var source = New(); ICalendarService calendar = new CalendarService(source); calendar.SaveCategory(new("course", "课程", "#123456")); calendar.Save(new("roundtrip", "复习", "2026-10-28", CategoryId: "course", Notes: "保留备注", Order: 4)); calendar.Reorder("roundtrip", "roundtrip"); var path = Path.Combine(root, "calendar-contract.json"); calendar.Export(path); using var destination = New(); ICalendarService imported = new CalendarService(destination); imported.Import(path); Check(imported.Query().Single() == calendar.Query().Single()); Check(destination.Current.TaskCategories.Single(c => c.Id == "course").Color == "#123456"); });
Test("悬浮离开取消待开启", () => { var h = new HoverCoordinator(); Check(h.Enter("a") == 100); h.Leave("a"); Check(h.Pending is null && h.Phase == HoverPhase.Closed); });
Test("快速经过五类只保留最新请求", () => { var h = new HoverCoordinator(); h.Open("a"); foreach (var id in new[] { "b", "c", "d", "e", "f" }) Check(h.Enter(id) == 35); h.Leave("b"); Check(h.Pending == "f" && h.Selected == "a"); Check(h.Open("f")); Check(!h.Open("f")); });
Test("窄通道保护且不覆盖桌面", () => { Check(HoverCoordinator.InCorridor(140, 204, 100, 180, 208, 0, 472, 200, 12)); Check(!HoverCoordinator.InCorridor(800, 204, 100, 180, 208, 0, 472, 200, 12)); Check(!HoverCoordinator.InCorridor(140, 100, 100, 180, 208, 0, 472, 200, 12)); var h = new HoverCoordinator(); h.Open("a"); Check(h.BeginClose(true) is null); Check(h.Selected == "a"); });
Test("菜单嵌套保持和释放", () => { var h = new HoverCoordinator(); h.Open("a"); using (h.Hold(HoldReason.ContextMenu)) { using (h.Hold(HoldReason.ContextMenu)) Check(h.BeginClose(false) is null); Check(h.IsHeld); } Check(!h.IsHeld && h.BeginClose(false) is not null); });
Test("拖拽失败也释放保持锁", () => { var h = new HoverCoordinator(); h.Open("a"); try { using var hold = h.Hold(HoldReason.Dragging); Check(h.Enter("b", true) == 350); throw new IOException("cancel"); } catch (IOException) { } Check(!h.IsHeld); Check(h.BeginClose(false) is not null); });
Test("关闭中重开使旧回调失效", () => { var h = new HoverCoordinator(); h.Open("a"); var generation = h.BeginClose(false)!.Value; h.CancelClose(); h.Open("a"); Check(!h.CompleteClose(generation)); Check(h.Selected == "a"); var second = h.BeginClose(false)!.Value; Check(h.CompleteClose(second) && h.Selected is null); });
Test("键盘焦点和 Esc 清理延迟请求", () => { var h = new HoverCoordinator(); h.Open("a"); h.SetHold(HoldReason.KeyboardFocus, true); Check(h.BeginClose(false) is null); h.Enter("b"); var old = h.Generation; h.Reset(); h.Opened(old); Check(h.Phase == HoverPhase.Closed && h.Pending is null); h.SetHold(HoldReason.KeyboardFocus, false); Check(!h.IsHeld); });
Test("旧动画结束不覆盖新分类状态", () => { var h = new HoverCoordinator(); h.Open("a"); var old = h.Generation; h.Open("b"); h.Opened(old); Check(h.Phase == HoverPhase.Opening); h.Opened(h.Generation); Check(h.Phase == HoverPhase.Open && h.Selected == "b"); });
Test("100/125/150/200% 与负坐标副屏定位", () =>
{
    foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
    foreach (var work in new[] { new PixelRect(0, 0, 1920, 1040), new PixelRect(-1920, -300, 0, 740), new PixelRect(80, 0, 1920, 1080), new PixelRect(0, 80, 1920, 1080), new PixelRect(0, 0, 1840, 1080) })
    {
        var dock = SurfacePlacement.Dock(work, scale, 24, false);
        Check(dock.Width <= work.Width * .7 && dock.Left >= work.Left && dock.Right <= work.Right);
        Check(work.Bottom - dock.Bottom == Math.Round(12 * scale));
        var popup = SurfacePlacement.Popup(work, scale, dock);
        Check(popup.Left >= work.Left && popup.Right <= work.Right && popup.Top >= work.Top && popup.Bottom <= work.Bottom);
        if (dock.Top - popup.Height - Math.Round(8 * scale) >= work.Top) Check(dock.Top - popup.Bottom == Math.Round(8 * scale));
    }
});
Test("高缩放小工作区浮层不越界", () =>
{
    var work = new PixelRect(-800, -100, 0, 580);
    var dock = SurfacePlacement.Dock(work, 2, 10, false);
    var popup = SurfacePlacement.Popup(work, 2, dock);
    Check(popup == work);
    Check(SurfacePlacement.Dock(work, 2, 10, true).Height == 40);
});
// ---- 桌面收纳：唯一会移动用户文件的路径 ----
string HarvestDesk(string tag) { var dir = Path.Combine(root, "desk-" + tag); Directory.CreateDirectory(dir); return dir; }
Test("钉住顺序与取消钉住", () =>
{
    using var db = New();
    var c = new Catalog(db);
    var a = c.Add(Item("A", "a"));
    var b = c.Add(Item("B", "b"));
    c.SetPinned([b.Id], true);
    c.SetPinned([a.Id], true);
    Check(c.Pinned().Select(e => e.Id).SequenceEqual([b.Id, a.Id]));
    c.SetPinned([b.Id], false);
    Check(c.Pinned().Single().Id == a.Id && db.Current.Entries.Single(e => e.Id == b.Id).PinOrder == 0);
});
Test("最近使用排序与上限", () =>
{
    using var db = New();
    var c = new Catalog(db);
    var a = c.Add(Item("A", "a"));
    var b = c.Add(Item("B", "b"));
    c.Touch(a.Id, new DateTime(2026, 10, 29, 10, 0, 0));
    c.Touch(b.Id, new DateTime(2026, 10, 29, 11, 0, 0));
    Check(c.Recents(1).Single().Id == b.Id && c.Recents(5).Count() == 2);
    Check(db.Current.Entries.Single(e => e.Id == b.Id).LastUsed == "2026-10-29T11:00:00");
});
Test("搜索按名称目标命中并限流", () =>
{
    using var db = New();
    var c = new Catalog(db);
    c.Add(Item("Visual Studio", @"C:\Apps\devenv.exe"));
    c.Add(Item("记事本", @"C:\Windows\notepad.exe"));
    Check(Finder.Entries(db.Current.Entries, "stu").Single().Name == "Visual Studio");
    Check(Finder.Entries(db.Current.Entries, "notepad").Single().Name == "记事本");
    Check(Finder.Entries(db.Current.Entries, " ").Count == 0);
    Check(Finder.Entries(db.Current.Entries, "e", 1).Count == 1);
    Check(Finder.Files([@"C:\a\季度报告.docx", @"C:\a\会议记录.txt"], "报告").Single().EndsWith("季度报告.docx"));
    Check(Finder.Files([@"C:\a\x.txt"], "").Count == 0);
});
Test("最近使用时间非法被拒绝", () =>
{
    using var db = New();
    Throws(() => db.Update(s => s.Entries.Add(Item("X", "x") with { LastUsed = "昨天" })));
});
Test("钉住状态持久化且拒绝非法顺序", () =>
{
    string path;
    using (var db = New()) { var c = new Catalog(db); c.SetPinned([c.Add(Item("A", "a")).Id], true); path = db.DirectoryPath; }
    using (var reopened = new Database(path)) Check(reopened.Current.Entries.Single().Pinned);
    using var bad = New();
    Throws(() => bad.Update(s => s.Entries.Add(Item("X", "x") with { Pinned = true, PinOrder = double.NaN })));
});
Test("收纳规划跳过系统文件与不存在项", () =>
{
    var desktop = HarvestDesk("plan");
    File.WriteAllText(Path.Combine(desktop, "desktop.ini"), "x");
    File.WriteAllText(Path.Combine(desktop, "报告.docx"), "doc");
    var archive = Path.Combine(root, "archive-plan");
    var plan = new DesktopHarvest(Path.Combine(root, "data-plan")).Plan([Path.Combine(desktop, "desktop.ini"), Path.Combine(desktop, "报告.docx"), Path.Combine(desktop, "ghost.txt")], archive, new DateTime(2026, 10, 29, 16, 0, 0));
    Check(plan.Items.Count == 1 && plan.Items[0].Kind == HarvestKind.Document && plan.Items[0].Name == "报告.docx");
    Check(plan.Items[0].Destination.StartsWith(Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase));
});
Test("收纳规划同名加序号不覆盖", () =>
{
    var a = HarvestDesk("dup-a");
    var b = HarvestDesk("dup-b");
    File.WriteAllText(Path.Combine(a, "同名.txt"), "one");
    File.WriteAllText(Path.Combine(b, "同名.txt"), "two");
    var archive = Path.Combine(root, "archive-dup");
    Directory.CreateDirectory(archive);
    File.WriteAllText(Path.Combine(archive, "同名.txt"), "existing");
    var plan = new DesktopHarvest(Path.Combine(root, "data-dup")).Plan([Path.Combine(a, "同名.txt"), Path.Combine(b, "同名.txt")], archive, DateTime.Now);
    Check(plan.Items.Count == 2);
    Check(plan.Items[0].Destination.EndsWith("同名 (2).txt", StringComparison.Ordinal) && plan.Items[1].Destination.EndsWith("同名 (3).txt", StringComparison.Ordinal));
    Check(File.ReadAllText(Path.Combine(archive, "同名.txt")) == "existing");
});
Test("收纳执行移动文件与文件夹并写清单", () =>
{
    var desktop = HarvestDesk("exec");
    var file = Path.Combine(desktop, "笔记.txt");
    File.WriteAllText(file, "keep");
    var folder = Path.Combine(desktop, "项目");
    Directory.CreateDirectory(folder);
    File.WriteAllText(Path.Combine(folder, "a.cs"), "code");
    var link = Path.Combine(desktop, "VSCode.lnk");
    File.WriteAllText(link, "lnk");
    var harvest = new DesktopHarvest(Path.Combine(root, "data-exec"));
    var plan = harvest.Plan([file, folder, link], Path.Combine(root, "archive-exec"), DateTime.Now);
    Check(plan.Items.Count == 3);
    var batch = harvest.Execute(plan);
    Check(batch.MovedCount == 3 && batch.RestingCount == 0);
    Check(!File.Exists(file) && !Directory.Exists(folder) && !File.Exists(link));
    Check(File.ReadAllText(batch.Items.Single(i => i.Kind == HarvestKind.Document).Destination) == "keep");
    Check(File.ReadAllText(Path.Combine(batch.Items.Single(i => i.Kind == HarvestKind.Folder).Destination, "a.cs")) == "code");
    Check(File.Exists(harvest.ManifestPath(batch.Id)));
    Check(harvest.Latest()?.Id == batch.Id && harvest.History().Count == 1);
});
Test("收纳中途失败自动回滚", () =>
{
    var desktop = HarvestDesk("rollback");
    var first = Path.Combine(desktop, "a.txt");
    File.WriteAllText(first, "a");
    var locked = Path.Combine(desktop, "b.txt");
    File.WriteAllText(locked, "b");
    var archive = Path.Combine(root, "archive-rollback");
    var harvest = new DesktopHarvest(Path.Combine(root, "data-rollback"));
    var plan = harvest.Plan([first, locked], archive, DateTime.Now);
    using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        Throws(() => harvest.Execute(plan));
    Check(File.ReadAllText(first) == "a" && File.ReadAllText(locked) == "b");
    Check(!File.Exists(Path.Combine(archive, "a.txt")));
    Check(harvest.Latest() is null);
});
Test("撤销收纳全部回原位", () =>
{
    var desktop = HarvestDesk("undo");
    var file = Path.Combine(desktop, "报告.pdf");
    File.WriteAllText(file, "pdf");
    var harvest = new DesktopHarvest(Path.Combine(root, "data-undo"));
    var batch = harvest.Execute(harvest.Plan([file], Path.Combine(root, "archive-undo"), DateTime.Now));
    var result = harvest.Undo(batch);
    Check(result.Restored == 1 && result.Occupied == 0 && result.Missing == 0);
    Check(File.ReadAllText(file) == "pdf");
    Check(harvest.Latest() is null);
});
Test("撤销时原位被占保留归档", () =>
{
    var desktop = HarvestDesk("occupied");
    var file = Path.Combine(desktop, "数据.xlsx");
    File.WriteAllText(file, "original");
    var harvest = new DesktopHarvest(Path.Combine(root, "data-occupied"));
    var batch = harvest.Execute(harvest.Plan([file], Path.Combine(root, "archive-occupied"), DateTime.Now));
    File.WriteAllText(file, "new");
    var result = harvest.Undo(batch);
    Check(result.Occupied == 1 && result.Restored == 0);
    Check(File.ReadAllText(batch.Items[0].Destination) == "original");
    Check(File.ReadAllText(file) == "new");
});
Test("归档夹自身不被收纳且空计划拒绝", () =>
{
    var archive = Path.Combine(root, "archive-self");
    Directory.CreateDirectory(Path.Combine(archive, "旧"));
    var harvest = new DesktopHarvest(Path.Combine(root, "data-self"));
    var plan = harvest.Plan([archive], archive, DateTime.Now);
    Check(plan.Items.Count == 0);
    Throws(() => harvest.Execute(plan));
});
V2CoreTests.Run(Test, root);
Console.WriteLine($"RESULT passed={passed} failed={failed}; isolated data={root}");
Environment.ExitCode = failed == 0 ? 0 : 1;
