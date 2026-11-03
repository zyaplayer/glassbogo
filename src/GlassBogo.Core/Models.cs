using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlassBogo.Core;

public record Category(string Id, string Name);
public record Entry(string Id, string Name, string Target, string Kind = "path", string Arguments = "", string WorkingDirectory = "", string CategoryId = "", bool Pinned = false, double PinOrder = 0, string? LastUsed = null, double CategoryOrder = 0, string? IconSource = null);
public record TaskCategory(string Id, string Name, string Color);
public record CalendarTask(string Id, string Title, string Date, string Time = "", string Notes = "", string Priority = "normal", bool Completed = false, double Order = 0, string? CategoryId = null);
public record SearchShortcut(uint Modifiers = 1, uint VirtualKey = 0xC0);
public record Preferences(string Theme = "system", string? Monitor = null, bool AutoHide = false, bool Onboarded = false, string QuickShortcut = "N", bool AutoStart = false, bool HardwareAcceleration = false, string IconSize = "medium", string VisualQuality = "strong", SearchShortcut? SearchHotkey = null, bool HideDesktopIcons = false, bool DesktopCalendarVisible = true, double? CalendarX = null, double? CalendarY = null, string CalendarView = "month", string? CalendarMonitor = null);
public record State
{
    public int Version { get; set; } = 1;
    public List<Category> Categories { get; init; } = [];
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
        if (s.Version is not (1 or 2))
            throw new InvalidDataException("不支持的数据版本。");
        if (s.Categories.Select(c => c.Id).Distinct().Count() != s.Categories.Count || s.Categories.Any(c => string.IsNullOrWhiteSpace(c.Name)))
            throw new InvalidDataException("收纳夹标识重复或名称为空。");
        if (s.Categories.Any(c => string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 40))
            throw new InvalidDataException("分类名称需为 1–40 字。");
        if (s.Entries.Select(e => e.Id).Distinct().Count() != s.Entries.Count || s.Entries.Select(Catalog.Identity).Distinct(StringComparer.Ordinal).Count() != s.Entries.Count)
            throw new InvalidDataException("存在重复入口。");
        if (s.Entries.Any(e => string.IsNullOrWhiteSpace(e.Name) || string.IsNullOrWhiteSpace(e.Target) || (e.CategoryId.Length > 0 && !s.Categories.Any(c => c.Id == e.CategoryId)) || e.Kind is not ("path" or "app" or "url")))
            throw new InvalidDataException("入口数据无效。");
        if (s.Entries.Any(e => !double.IsFinite(e.PinOrder) || !double.IsFinite(e.CategoryOrder)))
            throw new InvalidDataException("钉住排序值无效。");
        if (s.Preferences.IconSize is not ("small" or "medium" or "large") || s.Preferences.VisualQuality is not ("strong" or "reduced" or "off"))
            throw new InvalidDataException("图标大小或视觉效果设置无效。");
        if (s.Preferences.CalendarView is not ("month" or "week" or "agenda") || s.Preferences.CalendarX.HasValue != s.Preferences.CalendarY.HasValue || s.Preferences.CalendarX is { } x && !double.IsFinite(x) || s.Preferences.CalendarY is { } y && !double.IsFinite(y))
            throw new InvalidDataException("日历位置或视图无效。");
        if (s.Preferences.SearchHotkey is { } shortcut && (shortcut.Modifiers == 0 || (shortcut.Modifiers & ~15u) != 0 || shortcut.VirtualKey is 0 or > 254))
            throw new InvalidDataException("搜索快捷键无效。");
        if (s.Entries.Any(e => e.LastUsed is { } used && !DateTime.TryParse(used, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _)))
            throw new InvalidDataException("最近使用时间无效。");
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
    IEnumerable<Entry> InCategory(string id);
    void Rename(string id, string name);
    void Relocate(string id, Entry resolved);
    void StepPinned(string id, int direction);
    void StepEntry(string id, int direction);
    void StepCategory(string id, int direction);
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
