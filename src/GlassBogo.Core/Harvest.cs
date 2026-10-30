using System.Text.Json;

namespace GlassBogo.Core;

public static class HarvestKind
{
    public const string Shortcut = "shortcut";
    public const string Folder = "folder";
    public const string Document = "document";
    public static string Of(string path) =>
        Directory.Exists(path) ? Folder
        : Path.GetExtension(path).ToLowerInvariant() is ".lnk" or ".url" ? Shortcut
        : Document;
}

public sealed record HarvestItem(string Name, string Source, string Destination, string Kind);

public sealed record HarvestPlan(string ArchiveRoot, string Id, string CreatedAt, List<HarvestItem> Items);

public sealed class HarvestEntry(string name, string source, string destination, string kind)
{
    public string Name { get; } = name;
    public string Source { get; } = source;
    public string Destination { get; } = destination;
    public string Kind { get; } = kind;
    public bool Moved { get; set; }
}

public sealed record HarvestBatch(string Id, string CreatedAt, string ArchiveRoot, List<HarvestEntry> Items)
{
    public int MovedCount => Items.Count(i => i.Moved);
    public int RestingCount => Items.Count(i => !i.Moved);
}

public sealed record HarvestUndoResult(int Restored, int Occupied, int Missing);

public sealed class HarvestException(string message) : InvalidOperationException(message);

/// <summary>
/// Moves desktop items into a dated archive folder. This is the only path in GlassBogo
/// that touches user files: it never deletes, never overwrites, writes the manifest
/// before the first move and restores moved items when a later move fails.
/// </summary>
public sealed class DesktopHarvest
{
    private static readonly JsonSerializerOptions Json = new(DataJson.Options) { WriteIndented = true };
    private readonly string directory;
    public DesktopHarvest(string dataDirectory) => directory = Path.Combine(Path.GetFullPath(dataDirectory), "harvest");

    public static string NewArchiveRoot(DateTime now) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "桌面归档",
        now.ToString("yyyy-MM-dd-HHmm"));

    public HarvestPlan Plan(IEnumerable<string> sources, string archiveRoot, DateTime now)
    {
        var root = Path.GetFullPath(archiveRoot);
        var items = new List<HarvestItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!File.Exists(source) && !Directory.Exists(source))
                continue;
            var name = Path.GetFileName(source);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            // Office 临时锁文件（~$xxx.docx）正在被占用；归档/备份夹本身也不该再被收进去。
            if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("GlassBogo-归档-", StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsInside(source, root))
                continue;
            items.Add(new HarvestItem(name, source, Unique(root, name, used), HarvestKind.Of(source)));
        }
        return new HarvestPlan(root, NewId(now), now.ToString("yyyy-MM-dd HH:mm:ss"), items);
    }

    public HarvestBatch Execute(HarvestPlan plan)
    {
        if (plan.Items.Count == 0)
            throw new HarvestException("没有可收纳的项目。");
        Directory.CreateDirectory(plan.ArchiveRoot);
        var batch = new HarvestBatch(plan.Id, plan.CreatedAt, plan.ArchiveRoot,
            [.. plan.Items.Select(i => new HarvestEntry(i.Name, i.Source, i.Destination, i.Kind))]);
        // Manifest lands on disk before the first move so a crash leaves a recoverable record.
        Save(batch);
        var moved = new List<HarvestEntry>();
        try
        {
            foreach (var entry in batch.Items)
            {
                if (!File.Exists(entry.Source) && !Directory.Exists(entry.Source))
                    continue;
                if (File.Exists(entry.Destination) || Directory.Exists(entry.Destination))
                    throw new HarvestException($"目标已存在，未覆盖：{entry.Destination}");
                if (Directory.Exists(entry.Source))
                    Directory.Move(entry.Source, entry.Destination);
                else
                    File.Move(entry.Source, entry.Destination);
                entry.Moved = true;
                moved.Add(entry);
                Save(batch);
            }
        }
        catch (Exception error)
        {
            var failures = 0;
            foreach (var entry in Enumerable.Reverse(moved))
            {
                try
                {
                    if (Directory.Exists(entry.Destination))
                        Directory.Move(entry.Destination, entry.Source);
                    else if (File.Exists(entry.Destination))
                        File.Move(entry.Destination, entry.Source);
                    entry.Moved = false;
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { failures++; }
            }
            Save(batch);
            throw new HarvestException(failures == 0
                ? "收纳失败，已恢复原状：" + error.Message
                : $"收纳失败，{failures} 项未能自动恢复，清单保留在 {ManifestPath(batch.Id)}。");
        }
        return batch;
    }

    public HarvestUndoResult Undo(HarvestBatch batch)
    {
        int restored = 0, occupied = 0, missing = 0;
        foreach (var entry in batch.Items.Where(i => i.Moved).Reverse().ToList())
        {
            if (!File.Exists(entry.Destination) && !Directory.Exists(entry.Destination))
            {
                entry.Moved = false;
                missing++;
                continue;
            }
            if (File.Exists(entry.Source) || Directory.Exists(entry.Source))
            {
                occupied++;
                continue;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Source)!);
                if (Directory.Exists(entry.Destination))
                    Directory.Move(entry.Destination, entry.Source);
                else
                    File.Move(entry.Destination, entry.Source);
                entry.Moved = false;
                restored++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { occupied++; }
        }
        Save(batch);
        return new HarvestUndoResult(restored, occupied, missing);
    }

    public IReadOnlyList<HarvestBatch> History()
    {
        if (!Directory.Exists(directory))
            return [];
        var batches = new List<HarvestBatch>();
        foreach (var file in Directory.GetFiles(directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<HarvestBatch>(File.ReadAllText(file), Json) is { } batch)
                    batches.Add(batch);
            }
            catch (Exception error) when (error is IOException or JsonException) { }
        }
        return [.. batches.OrderByDescending(b => b.Id, StringComparer.Ordinal)];
    }

    public HarvestBatch? Latest() => History().FirstOrDefault(b => b.MovedCount > 0);

    public string ManifestPath(string id) => Path.Combine(directory, id + ".json");

    private void Save(HarvestBatch batch) => Database.AtomicWrite(ManifestPath(batch.Id), JsonSerializer.Serialize(batch, Json));

    private static string Unique(string root, string name, HashSet<string> used)
    {
        var candidate = Path.Combine(root, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 2; used.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate); index++)
            candidate = Path.Combine(root, $"{stem} ({index}){extension}");
        used.Add(candidate);
        return candidate;
    }

    private static bool IsInside(string path, string root) =>
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.Equals(root, StringComparison.OrdinalIgnoreCase);

    private static string NewId(DateTime now) => now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N")[..6];
}
