namespace GlassBogo.Core;

public sealed class Catalog(Database db) : IEntryCatalog
{
    public static string Identity(Entry e) => $"{e.Kind}\0{Normalize(e.Target)}\0{e.Arguments}\0{Normalize(e.WorkingDirectory)}";
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    public Entry Add(Entry entry)
    {
        var existing = db.Current.Entries.FirstOrDefault(e => string.Equals(Identity(e), Identity(entry), StringComparison.Ordinal));
        if (existing is not null)
            return existing;
        db.Update(s => s.Entries.Add(entry));
        return entry;
    }
    public (int Added, int Existing) AddRange(IEnumerable<Entry> entries)
    {
        int added = 0, existing = 0;
        db.Update(s => { var keys = s.Entries.Select(Identity).ToHashSet(StringComparer.Ordinal); foreach (var e in entries) { if (keys.Add(Identity(e))) { s.Entries.Add(e); added++; } else existing++; } });
        return (added, existing);
    }
    public void Move(IEnumerable<string> ids, string categoryId)
    {
        var selected = ids.ToHashSet();
        db.Update(s =>
        {
            if (!s.Categories.Any(c => c.Id == categoryId))
                throw new InvalidOperationException("分类不存在。");
            var moving = s.Entries.Where(e => selected.Contains(e.Id)).Select(e => e with { CategoryId = categoryId }).ToArray();
            s.Entries.RemoveAll(e => selected.Contains(e.Id));
            s.Entries.AddRange(moving);
        });
    }
    public void Reorder(string id, string before)
    {
        if (id == before)
            return;
        db.Update(s => { var entry = s.Entries.First(e => e.Id == id); var anchor = s.Entries.First(e => e.Id == before); s.Entries.Remove(entry); s.Entries.Insert(s.Entries.IndexOf(anchor), entry with { CategoryId = anchor.CategoryId }); });
    }
    public void Remove(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet();
        db.Update(s => s.Entries.RemoveAll(e => selected.Contains(e.Id)));
    }
    public IEnumerable<Entry> Search(string query) => db.Current.Entries.Where(e => e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || e.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    public IEnumerable<Entry> Pinned() => db.Current.Entries.Where(e => e.Pinned).OrderBy(e => e.PinOrder).ThenBy(e => e.Name, StringComparer.CurrentCulture);
    public void Touch(string id, DateTime now)
    {
        var stamp = now.ToString("s");
        db.Update(s => { var at = s.Entries.FindIndex(e => e.Id == id); if (at >= 0) s.Entries[at] = s.Entries[at] with { LastUsed = stamp }; });
    }
    public IEnumerable<Entry> Recents(int count = 8) => db.Current.Entries.Where(e => e.LastUsed is not null).OrderByDescending(e => e.LastUsed, StringComparer.Ordinal).Take(count);
    public void SetPinned(IEnumerable<string> ids, bool pinned)
    {
        var selected = ids.ToHashSet();
        db.Update(s =>
        {
            var next = s.Entries.Where(e => e.Pinned).Select(e => e.PinOrder).DefaultIfEmpty(0).Max() + 1;
            for (var i = 0; i < s.Entries.Count; i++)
                if (selected.Contains(s.Entries[i].Id))
                    s.Entries[i] = s.Entries[i] with { Pinned = pinned, PinOrder = pinned ? next++ : 0 };
        });
    }
    public void SaveCategory(string? id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 40)
            throw new InvalidOperationException("分类名称需为 1–40 字。");
        db.Update(s =>
        {
            if (s.Categories.Any(c => c.Id != id && c.Name.Equals(name, StringComparison.CurrentCultureIgnoreCase)))
                throw new InvalidOperationException("分类名称已存在。");
            if (id is null)
                s.Categories.Add(new(Guid.NewGuid().ToString("N"), name));
            else
            {
                var i = s.Categories.FindIndex(c => c.Id == id);
                s.Categories[i] = s.Categories[i] with
                {
                    Name = name
                };
            }
        });
    }
    public void RemoveCategory(string id)
    {
        db.Update(s => { s.Categories.RemoveAll(c => c.Id == id); for (int i = 0; i < s.Entries.Count; i++) if (s.Entries[i].CategoryId == id) s.Entries[i] = s.Entries[i] with { CategoryId = "" }; });
    }
    public void ReorderCategory(string id, string before)
    {
        if (id == before)
            return;
        db.Update(s => { var item = s.Categories.First(c => c.Id == id); s.Categories.Remove(item); s.Categories.Insert(s.Categories.FindIndex(c => c.Id == before), item); });
    }
}
