using GlassBogo.App;
using GlassBogo.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class V2CoreTests
{
    internal static void Run(Action<string, Action> test, string root)
    {
        static void Check(bool value) { if (!value) throw new InvalidOperationException("V2 assertion failed"); }
        static void Reject(Action action) { try { action(); } catch { return; } throw new InvalidOperationException("Expected rejection"); }
        string Folder() => Path.Combine(root, "v2-" + Guid.NewGuid().ToString("N"));
        static Entry E(string id, string category = "") => new(id, id, @"C:\Generated\" + id + ".exe", CategoryId: category);
        static string ReadDocument(string directory)
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(directory, "glassbogo.db"));
            db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT json FROM document WHERE id=1";
            return (string)cmd.ExecuteScalar()!;
        }
        static void SeedV1(string path)
        {
            using var db = new Database(path);
            db.Update(s =>
            {
                s.Categories.Add(new("inbox", "待整理")); s.Categories.Add(new("work", "工作"));
                s.Entries.Add(E("a", "inbox") with { Pinned = true, PinOrder = 7, Arguments = "--Profile A", WorkingDirectory = @"C:\Generated" });
                s.Entries.Add(E("b", "work")); s.Entries.Add(E("c", "work"));
                s.Tasks.Add(new("task", "旧任务", "2026-11-01", Notes: "保留"));
                s.Plugins["old.plugin"] = true; s.Migrations.Add("legacy-import"); s.DismissedDiscoveries.Add("old-dismissed");
                s.Preferences = s.Preferences with { AutoStart = true, QuickShortcut = "N" };
            });
        }

        test("V2 迁移独立备份保留引用及延后模块", () =>
        {
            var path = Folder(); SeedV1(path); var original = ReadDocument(path);
            using var upgraded = new Database(path, upgradeToV2: true);
            Check(upgraded.Current.Version == 2 && upgraded.Current.Categories.All(c => c.Id != "inbox"));
            Check(upgraded.Current.Entries.Single(e => e.Id == "a") is { CategoryId: "", Pinned: true, PinOrder: 7, Arguments: "--Profile A" });
            Check(upgraded.Current.Entries.Where(e => e.CategoryId == "work").Select(e => e.CategoryOrder).SequenceEqual([0d, 1d]));
            Check(upgraded.Current.Tasks.Single().Notes == "保留" && upgraded.Current.Plugins["old.plugin"]);
            Check(upgraded.Current.Migrations.Contains("legacy-import") && upgraded.Current.DismissedDiscoveries.Contains("old-dismissed"));
            Check(upgraded.Current.Preferences.AutoStart && upgraded.Current.Preferences.SearchHotkey == new SearchShortcut());
            Check(File.ReadAllText(Directory.GetFiles(Path.Combine(path, "recovery"), "v1-before-v2-*.json").Single()) == original);
        });
        test("V2 重复启动不重复迁移或备份", () =>
        {
            var path = Folder(); SeedV1(path);
            using (var first = new Database(path, true)) Check(first.Current.Version == 2);
            var before = ReadDocument(path);
            using (var second = new Database(path, true)) Check(second.Current.Version == 2);
            Check(ReadDocument(path) == before && Directory.GetFiles(Path.Combine(path, "recovery")).Length == 1);
        });
        test("V2 SQL 迁移失败保留原库和原版本", () =>
        {
            var path = Folder(); SeedV1(path); var original = ReadDocument(path);
            using (var db = new SqliteConnection("Data Source=" + Path.Combine(path, "glassbogo.db")))
            {
                db.Open(); using var cmd = db.CreateCommand(); cmd.CommandText = "CREATE TRIGGER reject_v2 BEFORE UPDATE ON document BEGIN SELECT RAISE(ABORT,'migration rejected'); END"; cmd.ExecuteNonQuery();
            }
            Reject(() => { using var failed = new Database(path, true); });
            Check(ReadDocument(path) == original);
            using var reopened = new Database(path); Check(reopened.Current.Version == 1 && reopened.Current.Categories.Any(c => c.Id == "inbox"));
            Check(Directory.GetFiles(Path.Combine(path, "recovery")).Length == 1);
        });
        test("V2 迁移备份失败不得写入新状态", () =>
        {
            var path = Folder(); SeedV1(path); var original = ReadDocument(path);
            File.WriteAllText(Path.Combine(path, "recovery"), "block directory creation");
            Reject(() => { using var failed = new Database(path, true); });
            Check(ReadDocument(path) == original);
        });
        test("V2 导入旧备份升级并恢复插件配置", () =>
        {
            var old = Folder(); SeedV1(old); var backup = Path.Combine(root, Guid.NewGuid() + ".json");
            using (var original = new Database(old)) original.Export(backup);
            using var current = new Database(Folder(), true); current.Update(s => s.Plugins["new.plugin"] = true);
            current.Import(backup);
            Check(current.Current.Version == 2 && current.Current.Plugins.Count == 1 && current.Current.Plugins["old.plugin"]);
            Check(current.Current.Entries.Single(e => e.Id == "a").CategoryId == "");
        });
        test("V2 恢复新备份保留格式与全部状态", () =>
        {
            var path = Folder(); var backup = Path.Combine(root, Guid.NewGuid() + ".json");
            using (var db = new Database(path, true)) { new Catalog(db).Add(E("a")); db.Update(s => s.Plugins["old"] = false); db.Export(backup); }
            var target = Folder(); DatabaseRecovery.Restore(target, backup);
            using var reopened = new Database(target, true); Check(reopened.Current.Version == 2 && reopened.Current.Entries.Single().Id == "a" && reopened.Current.Plugins.ContainsKey("old"));
        });
        test("V2 统一入口改名及移出分类保持固定关系", () =>
        {
            using var db = new Database(Folder(), true); var c = new Catalog(db);
            c.SaveCategory(null, "工作"); var category = db.Current.Categories.Single().Id;
            c.Add(E("a", category)); c.SetPinned(["a"], true); c.Rename("a", "  改名  ");
            Check(c.Pinned().Single().Name == "改名" && c.InCategory(category).Single().Name == "改名");
            c.Move(["a"], ""); Check(c.Pinned().Single().CategoryId == "" && !c.InCategory(category).Any());
            c.SetPinned(["a"], false); Check(db.Current.Entries.Count == 1 && !db.Current.Entries[0].Pinned);
        });
        test("V2 三种按钮排序彼此独立并持久化", () =>
        {
            var path = Folder();
            using (var db = new Database(path, true))
            {
                var c = new Catalog(db); c.SaveCategory(null, "一"); c.SaveCategory(null, "二"); var cats = db.Current.Categories.ToArray();
                c.Add(E("a", cats[0].Id)); c.Add(E("b", cats[0].Id)); c.SetPinned(["a", "b"], true);
                c.StepEntry("b", -1); Check(c.InCategory(cats[0].Id).First().Id == "b" && c.Pinned().First().Id == "a");
                c.StepPinned("b", -1); c.StepCategory(cats[1].Id, -1);
                Check(c.Pinned().First().Id == "b" && db.Current.Categories.First().Name == "二");
                var pinOrder = c.Pinned().Select(e => e.Id).ToArray(); c.SetPinned(["b"], true); Check(c.Pinned().Select(e => e.Id).SequenceEqual(pinOrder));
            }
            using var reopened = new Database(path, true); Check(new Catalog(reopened).Pinned().First().Id == "b" && reopened.Current.Categories.First().Name == "二");
        });
        test("V2 重新定位重复目标回滚且原文件不变", () =>
        {
            var original = Path.Combine(root, Guid.NewGuid() + ".txt"); File.WriteAllText(original, "untouched");
            using var db = new Database(Folder(), true); var c = new Catalog(db);
            c.Add(E("a") with { Target = original }); c.Add(E("b"));
            Reject(() => c.Relocate("a", E("b"))); Check(db.Current.Entries.Single(e => e.Id == "a").Target == original);
            c.Relocate("a", E("c")); c.Remove(["a"]); Check(File.ReadAllText(original) == "untouched");
        });
        test("V2 搜索合并优先托管记录不改变数据库", () =>
        {
            var managed = E("a") with { Name = "自定义编辑器", Arguments = "--Profile A" };
            var alias = managed with { Id = "discovered", Name = "自动发现名称" };
            var otherArgs = alias with { Id = "other", Arguments = "--Profile B", Name = "另一个编辑器" };
            var found = Finder.Search([managed], [alias, otherArgs], "编辑器");
            Check(found.Count == 2 && found.Any(e => e.Id == "a") && found.All(e => e.Id != "discovered"));
            Check(Finder.Search([managed], [alias], "").Count == 0 && Finder.Search([managed], [alias], "missing").Count == 0);
            Check(Finder.Search([managed], [alias], "GENERATED", 1).Count == 1);
        });
        test("V2 无效设置或顺序值不能持久化", () =>
        {
            using var db = new Database(Folder(), true);
            Reject(() => db.Update(s => s.Preferences = s.Preferences with { SearchHotkey = new(0, 65) }));
            Reject(() => db.Update(s => s.Preferences = s.Preferences with { VisualQuality = "invalid" }));
            Reject(() => db.Update(s => s.Entries.Add(E("a") with { CategoryOrder = double.NaN })));
            Reject(() => db.Update(s => s.Version = 1));
            Check(db.Current.Version == 2 && db.Current.Entries.Count == 0);
        });
        test("V2 固定面板只点击切换启动保留", () =>
        {
            var model = new CategoryInteraction(); model.Click("a"); Check(model.Hover("b") is null);
            model.Launched(); Check(model.Active == "a" && model.Pinned && model.Leave() is null);
            model.Click("b"); Check(model.Active == "b" && model.Pinned); model.Click("b"); Check(model.Active is null);
        });
        test("V2 最新悬停和关闭令牌隔离过期回调", () =>
        {
            var model = new CategoryInteraction(); var a = model.Hover("a")!.Value; var b = model.Hover("b")!.Value;
            Check(!model.CommitHover("a", a) && model.CommitHover("b", b));
            var close = model.Leave()!.Value; model.Stay(); Check(!model.CommitClose(close, false, false));
            close = model.Leave()!.Value; Check(!model.CommitClose(close, false, true));
            Check(model.CommitClose(close, false, false) && model.Active is null);
        });
        test("V2 临时面板启动关闭固定模式 Esc 可关闭", () =>
        {
            var model = new CategoryInteraction(); var token = model.Hover("a")!.Value; model.CommitHover("a", token); model.Launched(); Check(model.Active is null);
            model.Click("b"); model.Close(); Check(model.Active is null && !model.Pinned);
        });
        test("V2 一次放置批量去重并保留已有显示名", () =>
        {
            using var db = new Database(Folder(), true); var catalog = new Catalog(db);
            catalog.SaveCategory(null, "分类"); var category = db.Current.Categories.Single().Id;
            var result = catalog.PlaceRange([E("one"), E("one") with { Name = "Alias" }, E("two")], category, true);
            Check(result.Count == 2 && db.Current.Entries.Count == 2 && catalog.Pinned().Select(e => e.PinOrder).Distinct().Count() == 2);
            Check(db.Current.Entries.All(e => e.CategoryId == category) && db.Current.Entries[0].Name == "one");
            var id = result[0].Id; catalog.PlaceRange([E("one") with { Name = "Discovered alias" }], pinned: true);
            Check(db.Current.Entries[0].Id == id && db.Current.Entries[0].Name == "one" && db.Current.Entries[0].CategoryId == category);
        });
        test("V2 批量放置无效项或数据库失败全部回滚", () =>
        {
            using var db = new Database(Folder(), true); var catalog = new Catalog(db);
            Reject(() => catalog.PlaceRange([E("good"), E("bad") with { Target = "" }], pinned: true));
            Check(db.Current.Entries.Count == 0);
            using var sql = new SqliteConnection("Data Source=" + Path.Combine(db.DirectoryPath, "glassbogo.db")); sql.Open();
            using var cmd = sql.CreateCommand(); cmd.CommandText = "CREATE TRIGGER reject_place BEFORE UPDATE ON document BEGIN SELECT RAISE(ABORT,'reject'); END"; cmd.ExecuteNonQuery();
            Reject(() => catalog.PlaceRange([E("a"), E("b")], pinned: true)); Check(db.Current.Entries.Count == 0);
        });
        test("V2 同分类移动不改序且删除分类按序追加未分类", () =>
        {
            using var db = new Database(Folder(), true); var catalog = new Catalog(db);
            db.Update(s =>
            {
                s.Categories.Add(new("cat", "分类"));
                s.Entries.AddRange([E("tail", "cat") with { CategoryOrder = 8, Pinned = true, PinOrder = 9 }, E("existing") with { CategoryOrder = 3 }, E("first", "cat") with { CategoryOrder = 1 }]);
            });
            var before = db.Current.Entries.ToArray(); catalog.Move(["tail", "first"], "cat");
            Check(db.Current.Entries.SequenceEqual(before));
            catalog.RemoveCategory("cat");
            Check(catalog.InCategory("").Select(e => e.Id).SequenceEqual(new[] { "existing", "first", "tail" }));
            Check(db.Current.Entries.Single(e => e.Id == "tail").Pinned && db.Current.Entries.Single(e => e.Id == "tail").PinOrder == 9);
            Reject(() => catalog.RemoveCategory("")); Reject(() => catalog.SaveCategory("cat", "已不存在"));
        });
        test("课表七天与四个时段沿用原项目边界且每周独立", () =>
        {
            Check(Timetable.Slots.Select(s => s.Start).SequenceEqual(new[] { "08:00", "09:50", "13:45", "15:35" }));
            Check(Timetable.Slots.Select(s => s.End).SequenceEqual(new[] { "09:35", "11:25", "15:20", "17:10" }));
            Check(Timetable.PeriodForTime("08:00") == 0 && Timetable.PeriodForTime("09:34") == 0 && Timetable.PeriodForTime("09:35") is null && Timetable.PeriodForTime("09:49") is null);
            Check(Timetable.PeriodForTime("09:50") == 1 && Timetable.PeriodForTime("13:45") == 2 && Timetable.PeriodForTime("15:35") == 3 && Timetable.PeriodForTime("17:10") is null && Timetable.PeriodForTime("") is null);
            var selected = new DateOnly(2026, 11, 4); var week = Timetable.WeekDays(selected);
            Check(week.Count == 7 && week[0].DayOfWeek == DayOfWeek.Monday && week[6].DayOfWeek == DayOfWeek.Sunday && Timetable.ShiftWeek(selected, 1) == selected.AddDays(7));
            Check(Timetable.WeekDays(DateOnly.MaxValue).Last() == DateOnly.MaxValue && Timetable.ShiftWeek(DateOnly.MaxValue, 1) == DateOnly.MaxValue);
        });
        test("日历位置与课表视图持久化并拒绝无效坐标", () =>
        {
            var folder = Folder(); using var db = new Database(folder, true);
            db.Update(s => s.Preferences = s.Preferences with { CalendarX = 400, CalendarY = 36, CalendarView = "week" });
            using var reopened = new Database(folder, true); Check(reopened.Current.Preferences.CalendarX == 400 && reopened.Current.Preferences.CalendarY == 36 && reopened.Current.Preferences.CalendarView == "week");
            Reject(() => db.Update(s => s.Preferences = s.Preferences with { CalendarX = double.NaN }));
            Reject(() => db.Update(s => s.Preferences = s.Preferences with { CalendarY = null }));
        });
        test("V2 两区溢出始终有界并释放空区宽度", () =>
        {
            foreach (var width in new[] { 160d, 320, 800, 1920 }) foreach (var icon in new[] { 32d, 40, 48 })
            {
                var layout = DockSections.Measure(width, icon, 500, 60);
                Check(layout.Width <= width && layout.PinnedWidth > 0 && layout.CategoryWidth > 0);
                Check(layout.PinnedVisible < 500 && layout.CategoriesVisible < 60);
            }
            var onlyPinned = DockSections.Measure(800, 40, 50, 0);
            var both = DockSections.Measure(800, 40, 50, 50);
            Check(onlyPinned.PinnedVisible > both.PinnedVisible && onlyPinned.CategoryWidth == 0);
        });
    }
}
