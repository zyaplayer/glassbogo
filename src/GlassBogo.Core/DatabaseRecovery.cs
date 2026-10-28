using System.Text.Json;

namespace GlassBogo.Core;

public static class DatabaseRecovery
{
    // The caller must own the single-instance lock. Build and validate the replacement
    // before archiving the damaged file; never silently initialize over user data.
    public static string Restore(string directory, string backup)
    {
        var snapshot = JsonSerializer.Deserialize<State>(File.ReadAllText(backup), DataJson.Options) ?? throw new InvalidDataException("备份为空。");
        DataJson.Validate(snapshot);
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".restore-" + Guid.NewGuid().ToString("N"));
        using (var test = new Database(staging))
            test.Import(backup);
        var archive = Path.Combine(root, "recovery-db", DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff"));
        Directory.CreateDirectory(archive);
        var moved = new List<(string Source, string Backup)>();
        try
        {
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                var path = Path.Combine(root, "glassbogo.db" + suffix);
                if (!File.Exists(path))
                    continue;
                var saved = Path.Combine(archive, Path.GetFileName(path));
                File.Move(path, saved);
                moved.Add((path, saved));
            }
            File.Move(Path.Combine(staging, "glassbogo.db"), Path.Combine(root, "glassbogo.db"));
        }
        catch { foreach (var item in moved.AsEnumerable().Reverse()) if (!File.Exists(item.Source)) File.Move(item.Backup, item.Source); throw; }
        Directory.Delete(staging, true);
        return archive;
    }
}
