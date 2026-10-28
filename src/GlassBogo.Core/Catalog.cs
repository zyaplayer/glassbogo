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
                if (id == "inbox")
                    throw new InvalidOperationException("待整理分类不可重命名。");
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
        if (id == "inbox")
            throw new InvalidOperationException("待整理分类不可移除。");
        db.Update(s => { s.Categories.RemoveAll(c => c.Id == id); for (int i = 0; i < s.Entries.Count; i++) if (s.Entries[i].CategoryId == id) s.Entries[i] = s.Entries[i] with { CategoryId = "inbox" }; });
    }
    public void ReorderCategory(string id, string before)
    {
        if (id == before || id == "inbox" || before == "inbox")
            return;
        db.Update(s => { var item = s.Categories.First(c => c.Id == id); s.Categories.Remove(item); s.Categories.Insert(s.Categories.FindIndex(c => c.Id == before), item); });
    }
}
