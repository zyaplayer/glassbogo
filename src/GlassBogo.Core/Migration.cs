using System.Text.Json;

namespace GlassBogo.Core;

public static class Migration
{
    public static string Calendar(Database db, string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.TryGetProperty("data", out var data))
            root = data;
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 1)
            throw new InvalidDataException("不支持的旧日历版本。");
        var tasks = root.GetProperty("tasks").Deserialize<List<CalendarTask>>(DataJson.Options) ?? [];
        var categories = root.TryGetProperty("categories", out var c) ? c.Deserialize<List<TaskCategory>>(DataJson.Options) ?? [] : [];
        var candidate = new State { Tasks = tasks, TaskCategories = categories };
        DataJson.Validate(candidate);
        db.RecoveryPoint();
        int added = 0;
        db.Update(s =>
        {
            var firstImport = !s.Migrations.Contains("calendar-v1");
            foreach (var category in categories)
            {
                var at = s.TaskCategories.FindIndex(x => x.Id == category.Id);
                if (at < 0)
                    s.TaskCategories.Add(category);
                else if (firstImport)
                    s.TaskCategories[at] = category;
            }
            if (firstImport)
                s.Migrations.Add("calendar-v1");
            foreach (var task in tasks)
                if (!s.Tasks.Any(t => t.Id == task.Id))
                {
                    s.Tasks.Add(task);
                    added++;
                }
        });
        return $"导入 {added} 个任务，跳过 {tasks.Count - added} 个已有任务。旧日历文件未修改。";
    }
    public static string GlassDesk(Database db, string folder, Func<Entry, Entry>? resolve = null)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "apps.json")));
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("items");
        var entries = new List<Entry>();
        foreach (var item in items.EnumerateArray())
        {
            string Get(string key, string fallback = "") => item.TryGetProperty(key, out var v) ? v.ToString() : fallback;
            var target = Get("target");
            if (target.Length == 0)
                continue;
            var kind = target.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "url" : target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ? "app" : "path";
            var entry = new Entry(Get("id", Guid.NewGuid().ToString("N")), Get("displayName", Path.GetFileName(target)), target, kind, Get("arguments"), Get("workingDirectory"));
            entries.Add(resolve?.Invoke(entry) ?? entry);
        }
        var categories = new List<Category>();
        var membership = new Dictionary<string, string>();
        int repeated = 0;
        var layout = Path.Combine(folder, "dock-layout.json");
        if (File.Exists(layout))
        {
            using var ld = JsonDocument.Parse(File.ReadAllText(layout));
            if (ld.RootElement.TryGetProperty("categories", out var cs))
                foreach (var cat in cs.EnumerateArray())
                {
                    var id = "import-" + cat.GetProperty("id").GetString();
                    categories.Add(new(id, cat.GetProperty("name").GetString() ?? "导入分类"));
                    foreach (var entry in cat.GetProperty("itemIds").EnumerateArray())
                        if (!membership.TryAdd(entry.GetString()!, id))
                            repeated++;
                }
        }
        db.RecoveryPoint();
        int added = 0;
        db.Update(s =>
        {
            foreach (var c in categories)
                if (!s.Categories.Any(x => x.Id == c.Id))
                    s.Categories.Add(c);
            var keys = s.Entries.Select(Catalog.Identity).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in entries)
                if (keys.Add(Catalog.Identity(entry)))
                {
                    s.Entries.Add(entry with
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        CategoryId = membership.GetValueOrDefault(entry.Id, "inbox")
                    });
                    added++;
                }
        });
        return $"导入 {added} 个入口；跳过 {entries.Count - added} 个重复入口；{repeated} 个重复分类归属保留首个。原文件未修改。";
    }
}
