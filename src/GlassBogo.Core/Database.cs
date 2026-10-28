using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GlassBogo.Core;

public sealed class Database : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate = new();
    public string DirectoryPath
    {
        get;
    }
    public State Current
    {
        get; private set;
    }
    public event EventHandler? Changed;
    public Database(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "glassbogo.db"), Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check";
            if (!string.Equals(check.ExecuteScalar()?.ToString(), "ok", StringComparison.Ordinal))
                throw new InvalidDataException("数据库校验失败，请从备份恢复。");
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TABLE IF NOT EXISTS document (id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); PRAGMA user_version;";
            var version = Convert.ToInt32(cmd.ExecuteScalar());
            if (version > 1)
                throw new InvalidDataException("数据库由更新版本创建，请升级 GlassBogo。");
            cmd.CommandText = "SELECT json FROM document WHERE id=1";
            var json = cmd.ExecuteScalar() as string;
            Current = json is null ? new State() : JsonSerializer.Deserialize<State>(json, DataJson.Options) ?? throw new InvalidDataException("数据为空。");
            DataJson.Validate(Current);
            if (version == 0)
            {
                cmd.CommandText = "PRAGMA user_version=1";
                cmd.ExecuteNonQuery();
            }
        }
        catch { connection.Dispose(); throw; }
    }
    public void Update(Action<State> edit)
    {
        lock (gate)
        {
            var next = DataJson.Clone(Current);
            edit(next);
            DataJson.Validate(next);
            var backupDir = Path.Combine(DirectoryPath, "backups");
            Directory.CreateDirectory(backupDir);
            var backup = Path.Combine(backupDir, DateTime.Today.ToString("yyyy-MM-dd") + ".json");
            if (!File.Exists(backup))
                AtomicWrite(backup, JsonSerializer.Serialize(Current, DataJson.Options));
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO document(id,json) VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=$json";
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(next, DataJson.Options));
            cmd.ExecuteNonQuery();
            tx.Commit();
            Current = next;
            // Retention is best-effort; a committed edit must not be reported as failed.
            try
            {
                foreach (var f in System.IO.Directory.GetFiles(backupDir, "????-??-??.json").OrderDescending().Skip(7))
                    File.Delete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
    public string RecoveryPoint()
    {
        var path = Path.Combine(DirectoryPath, "recovery", DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + ".json");
        AtomicWrite(path, JsonSerializer.Serialize(Current, DataJson.Options));
        return path;
    }
    public void Export(string path) => AtomicWrite(path, JsonSerializer.Serialize(Current, DataJson.Options));
    public void Import(string path)
    {
        var next = JsonSerializer.Deserialize<State>(File.ReadAllText(path), DataJson.Options) ?? throw new InvalidDataException("备份为空。");
        DataJson.Validate(next);
        RecoveryPoint();
        Update(s => { s.Categories.Clear(); s.Categories.AddRange(next.Categories); s.Entries.Clear(); s.Entries.AddRange(next.Entries); s.Tasks.Clear(); s.Tasks.AddRange(next.Tasks); s.TaskCategories.Clear(); s.TaskCategories.AddRange(next.TaskCategories); s.Preferences = next.Preferences; s.DismissedDiscoveries.Clear(); s.DismissedDiscoveries.AddRange(next.DismissedDiscoveries); s.Migrations.Clear(); s.Migrations.AddRange(next.Migrations); });
    }
    public static void AtomicWrite(string path, string content)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, full, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Dispose() => connection.Dispose();
}
