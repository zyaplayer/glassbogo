namespace GlassBogo.Core;

// Pure conversion, shared by the explicit v2 startup and backup import. Never
// touches targets or drops deferred modules. Persistence/backup belong to Database.
public static class StateUpgrade
{
    public static State ToV2(State original)
    {
        DataJson.Validate(original);
        var next = DataJson.Clone(original);
        if (next.Version == 2) return next;
        next.Categories.RemoveAll(c => c.Id == "inbox");
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < next.Entries.Count; i++)
        {
            var entry = next.Entries[i];
            var category = entry.CategoryId == "inbox" ? "" : entry.CategoryId;
            var at = order.GetValueOrDefault(category);
            next.Entries[i] = entry with { CategoryId = category, CategoryOrder = at };
            order[category] = at + 1;
        }
        next.Preferences = next.Preferences with { SearchHotkey = next.Preferences.SearchHotkey ?? new SearchShortcut() };
        next.Version = 2;
        DataJson.Validate(next);
        return next;
    }
}
