namespace GlassBogo.Core;

/// <summary>
/// Pure matching rules for the dock search: entries by name or target, archived
/// files by file name. Kept free of IO so the ranking rules stay testable.
/// </summary>
public static class Finder
{
    public static IReadOnlyList<Entry> Search(IEnumerable<Entry> managed, IEnumerable<Entry> discovered, string query, int limit = 200)
    {
        query = query.Trim();
        if (query.Length == 0 || limit <= 0) return [];
        // Managed record wins before matching, so a custom name/arguments are never
        // replaced by a newly discovered alias for the same logical target.
        return Entries(managed.Concat(discovered).DistinctBy(Catalog.Identity, StringComparer.Ordinal), query, limit);
    }
    public static IReadOnlyList<Entry> Entries(IEnumerable<Entry> entries, string query, int limit = 8)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];
        return [.. entries
            .Where(e => e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || e.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(e => e.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenByDescending(e => e.LastUsed, StringComparer.Ordinal)
            .ThenBy(e => e.Name, StringComparer.CurrentCulture)
            .Take(limit)];
    }

    public static IReadOnlyList<string> Files(IEnumerable<string> paths, string query, int limit = 8)
    {
        query = query.Trim();
        if (query.Length == 0 || limit <= 0)
            return [];
        return [.. paths
            .Where(p => Path.GetFileName(p).Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(p => Path.GetFileName(p).StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 : 1)
            .ThenBy(Path.GetFileName, StringComparer.CurrentCulture)
            .Take(limit)];
    }
}
