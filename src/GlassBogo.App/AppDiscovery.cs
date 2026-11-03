namespace GlassBogo.App;

// Shared discovery task with cancellation per consumer. Closing a search never
// publishes stale results or starts another concurrent COM scan.
internal sealed class AppDiscovery : IDisposable
{
    private readonly Func<Task<List<Entry>>> discover;
    internal AppDiscovery(Func<Task<List<Entry>>>? discover = null) => this.discover = discover ?? Platform.InstalledApps;
    private readonly object gate = new();
    private Task<IReadOnlyList<Entry>>? scan;
    private Task<IReadOnlyList<Entry>>? forced;
    private DateTime scannedAt;
    private bool disposed;
    internal Task<IReadOnlyList<Entry>> GetAsync(CancellationToken cancellationToken, bool refresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<IReadOnlyList<Entry>> task;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (refresh)
            {
                if (forced is null || forced.IsCompleted)
                    scan = forced = ScanAfter(scan is { IsCompleted: false } ? scan : null);
            }
            else if (scan is null || scan.IsCompleted && (scan.IsFaulted || scan.IsCanceled || DateTime.UtcNow - scannedAt > TimeSpan.FromMinutes(2)))
                scan = Scan();
            task = scan!;
        }
        return task.WaitAsync(cancellationToken);
    }
    private async Task<IReadOnlyList<Entry>> ScanAfter(Task<IReadOnlyList<Entry>>? previous)
    {
        // Refresh requested during a scan gets one fresh scan after it, rather
        // than returning the old in-flight snapshot or running COM scans in parallel.
        if (previous is not null) { try { await previous.ConfigureAwait(false); } catch { } }
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        return await Scan().ConfigureAwait(false);
    }
    private async Task<IReadOnlyList<Entry>> Scan()
    {
        var candidates = await discover().ConfigureAwait(false);
        var filtered = await Task.Run(() => (IReadOnlyList<Entry>)candidates
            .Where(e => e.Kind == "app" || File.Exists(e.Target) || Directory.Exists(e.Target))
            .DistinctBy(Catalog.Identity, StringComparer.Ordinal).ToArray()).ConfigureAwait(false);
        lock (gate) { if (!disposed) scannedAt = DateTime.UtcNow; }
        return filtered;
    }
    public void Dispose() { lock (gate) { disposed = true; scan = forced = null; } }
}
