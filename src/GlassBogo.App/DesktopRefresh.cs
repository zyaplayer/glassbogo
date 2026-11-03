using System.Runtime.InteropServices;

namespace GlassBogo.App;

internal enum TargetState { Present, Missing, Unknown }
internal sealed record DesktopRefreshResult(IReadOnlyList<Entry> Candidates, int Removed, int Unverified)
{
    internal string Summary => $"刷新完成：{Candidates.Count} 个可选项，清理 {Removed} 个失效入口" + (Unverified > 0 ? $"，{Unverified} 个暂无法核实，已保留" : "");
}

// Only an explicit refresh reconciles the user's library. Discovery by itself is read-only.
internal sealed class DesktopRefresh(AppDiscovery discovery, Func<Entry, Task<TargetState>>? probe = null)
{
    private readonly Func<Entry, Task<TargetState>> inspect = probe ?? Probe;
    internal async Task<DesktopRefreshResult> Run(Database db, Func<Action, Task> write, CancellationToken token)
    {
        var candidates = await discovery.GetAsync(token, refresh: true);
        var snapshot = db.Current.Entries.ToArray();
        var missing = new List<Entry>(); var unknown = 0;
        foreach (var entry in snapshot)
        {
            token.ThrowIfCancellationRequested();
            var state = await inspect(entry).ConfigureAwait(false);
            if (state == TargetState.Missing) missing.Add(entry);
            else if (state == TargetState.Unknown) unknown++;
        }
        token.ThrowIfCancellationRequested();
        var removed = 0;
        if (missing.Count > 0) await write(() =>
        {
            token.ThrowIfCancellationRequested();
            // Concurrent rename/relocation/pinning wins over this older snapshot.
            var stale = missing.Where(e => db.Current.Entries.Contains(e) && (e.Kind != "path" || ProbePath(e) == TargetState.Missing)).ToHashSet();
            if (stale.Count == 0) return;
            db.RecoveryPoint();
            db.Update(s => removed = s.Entries.RemoveAll(stale.Contains));
        }).ConfigureAwait(false);
        return new(candidates, removed, unknown);
    }

    internal static Task<TargetState> Probe(Entry entry) => entry.Kind == "app" ? Platform.Sta(() =>
    {
        object? shell = null, folder = null, item = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folder = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folder is null) return TargetState.Unknown;
            var target = entry.Target.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) ? entry.Target[17..] : entry.Target;
            item = ((dynamic)folder).ParseName(target);
            return item is null ? TargetState.Missing : TargetState.Present;
        }
        catch (COMException) { return TargetState.Unknown; }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    }) : Task.Run(() => ProbePath(entry));

    private static TargetState ProbePath(Entry entry)
    {
        if (entry.Kind != "path" || !Path.IsPathFullyQualified(entry.Target) || entry.Target.StartsWith(@"\\", StringComparison.Ordinal)) return TargetState.Unknown;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(entry.Target)!);
            if (!drive.IsReady || drive.DriveType is not DriveType.Fixed) return TargetState.Unknown;
            var attributes = File.GetAttributes(entry.Target);
            return (attributes & FileAttributes.Offline) != 0 ? TargetState.Unknown : TargetState.Present;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return TargetState.Missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return TargetState.Unknown; }
    }
}
