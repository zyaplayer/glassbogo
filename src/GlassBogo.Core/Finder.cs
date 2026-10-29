namespace GlassBogo.Core;

/// <summary>
/// Pure matching rules for the dock search: entries by name or target, archived
/// files by file name. Kept free of IO so the ranking rules stay testable.
/// </summary>
public static class Finder
{
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
