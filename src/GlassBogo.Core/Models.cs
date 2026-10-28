using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassBogo.Core;

public record Category(string Id, string Name);
public record Entry(string Id, string Name, string Target, string Kind = "path", string Arguments = "", string WorkingDirectory = "", string CategoryId = "inbox");
public record TaskCategory(string Id, string Name, string Color);
public record CalendarTask(string Id, string Title, string Date, string Time = "", string Notes = "", string Priority = "normal", bool Completed = false, double Order = 0, string? CategoryId = null);
public record Preferences(string Theme = "system", string? Monitor = null, bool AutoHide = false, bool Onboarded = false, string QuickShortcut = "N", bool AutoStart = false, bool HardwareAcceleration = false);
public record State
{
    public int Version { get; init; } = 1;
    public List<Category> Categories { get; init; } = [new("inbox", "待整理")];
    public List<Entry> Entries { get; init; } = [];
    public List<CalendarTask> Tasks { get; init; } = [];
    public List<TaskCategory> TaskCategories { get; init; } = [new("work", "工作", "#6B9FE8"), new("life", "生活", "#72B5A0")];
    public Preferences Preferences { get; set; } = new();
    public List<string> DismissedDiscoveries { get; init; } = [];
    public List<string> Migrations { get; init; } = [];
    public Dictionary<string, bool> Plugins { get; init; } = [];
}
public static class DataJson
{
    public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    // The contained records are immutable. Copy collections, not every task string,
    // to preserve transactional rollback without serializing a 10k-task calendar twice.
    public static State Clone(State value) => new()
    {
        Version = value.Version,
        Categories = [.. value.Categories],
        Entries = [.. value.Entries],
        Tasks = [.. value.Tasks],
        TaskCategories = [.. value.TaskCategories],
        Preferences = value.Preferences,
        DismissedDiscoveries = [.. value.DismissedDiscoveries],
        Migrations = [.. value.Migrations],
        Plugins = new(value.Plugins)
    };
    public static void Validate(State s)
    {
        if (s.Version != 1)
            throw new InvalidDataException("不支持的数据版本。");
        if (s.Categories.Count(c => c.Id == "inbox") != 1 || s.Categories.Select(c => c.Id).Distinct().Count() != s.Categories.Count)
            throw new InvalidDataException("分类标识重复或缺少待整理分类。");
        if (s.Categories.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 40))
            throw new InvalidDataException("分类名称需为 1–40 字。");
        if (s.Entries.Select(e => e.Id).Distinct().Count() != s.Entries.Count || s.Entries.Select(Catalog.Identity).Distinct(StringComparer.Ordinal).Count() != s.Entries.Count)
            throw new InvalidDataException("存在重复入口。");
        if (s.Entries.Any(e => string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Target) || !s.Categories.Any(c => c.Id == e.CategoryId) || e.Kind is not ("path" or "app" or "url")))
            throw new InvalidDataException("入口数据无效。");
        if (s.Tasks.Select(t => t.Id).Distinct().Count() != s.Tasks.Count)
            throw new InvalidDataException("任务标识重复。");
        foreach (var t in s.Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Title) || t.Title.Length > 200 || !DateOnly.TryParseExact(t.Date, "yyyy-MM-dd", out _) || (t.Time != "" && !TimeOnly.TryParseExact(t.Time, "HH:mm", out _)) || t.Priority is not ("normal" or "high" or "low") || !double.IsFinite(t.Order))
                throw new InvalidDataException("任务日期、时间或内容无效。");
        }
        if (s.TaskCategories.Select(c => c.Id).Distinct().Count() != s.TaskCategories.Count || s.TaskCategories.Any(c => string.IsNullOrWhiteSpace(c.Name) || !System.Text.RegularExpressions.Regex.IsMatch(c.Color, "^#[0-9A-Fa-f]{6}$")))
            throw new InvalidDataException("任务分类无效。");
    }
}
public interface IEntryCatalog
{
    Entry Add(Entry entry);
    (int Added, int Existing) AddRange(IEnumerable<Entry> entries);
    void Move(IEnumerable<string> ids, string categoryId);
    void Reorder(string id, string before);
    void SaveCategory(string? id, string name);
    void RemoveCategory(string id);
    void ReorderCategory(string id, string before);
    void Remove(IEnumerable<string> ids);
    IEnumerable<Entry> Search(string query);
}
public record LaunchResult(bool Success, string? Error = null);
public interface ILaunchService
{
    Task<LaunchResult> LaunchAsync(Entry entry);
}
public interface IDesktopIntegration : IDisposable
{
    event EventHandler? Changed;
    Task<IReadOnlyList<Entry>> DiscoverAsync(CancellationToken cancellationToken);
    bool HiddenByUs
    {
        get;
    }
    void HideIcons();
    void RestoreIcons();
}
public interface ICalendarService
{
    event EventHandler? Changed;
    void Save(CalendarTask task);
    void Remove(string id);
    void Reorder(string id, string before);
    void SaveCategory(TaskCategory category);
    void RemoveCategory(string id);
    string Import(string path);
    void Export(string path);
    IEnumerable<CalendarTask> Query(string? date = null, string query = "", string? categoryId = null, string status = "all");
}
