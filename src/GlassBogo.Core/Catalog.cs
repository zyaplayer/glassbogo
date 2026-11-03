namespace GlassBogo.Core;

public sealed class Catalog(Database db) : IEntryCatalog
{
    public static string Identity(Entry e) => $"{e.Kind}\0{Normalize(e.Target)}\0{e.Arguments}\0{Normalize(e.WorkingDirectory)}";
    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToUpperInvariant();
    public IReadOnlyList<Entry> PlaceRange(IEnumerable<Entry> candidates, string? category = null, bool? pinned = null)
    {
        var values = candidates.ToArray(); var result = new List<Entry>();
        db.Update(s =>
        {
            if (category is { Length: > 0 } && !s.Categories.Any(c => c.Id == category)) throw new InvalidOperationException("分类不存在。");
            foreach (var candidate in values)
            {
                var key = Identity(candidate); var at = s.Entries.FindIndex(e => Identity(e) == key);
                var entry = at >= 0 ? s.Entries[at] : candidate with { Id = Guid.NewGuid().ToString("N"), CategoryId = "", CategoryOrder = NextOrder(s, ""), Pinned = false, PinOrder = 0 };
                if (category is not null && entry.CategoryId != category) entry = entry with { CategoryId = category, CategoryOrder = NextOrder(s, category) };
                if (pinned.HasValue && entry.Pinned != pinned.Value) entry = entry with { Pinned = pinned.Value, PinOrder = pinned.Value ? s.Entries.Where(e => e.Pinned).Select(e => e.PinOrder).DefaultIfEmpty(-1).Max() + 1 : 0 };
                if (at >= 0) s.Entries[at] = entry; else s.Entries.Add(entry);
                result.Add(entry);
            }
        });
        return result.DistinctBy(e => e.Id).ToArray();
    }
    public Entry Add(Entry entry)
    {
        var existing = db.Current.Entries.FirstOrDefault(e => string.Equals(Identity(e), Identity(entry), StringComparison.Ordinal));
        if (existing is not null)
            return existing;
        Entry added = entry;
        db.Update(s => { added = entry with { CategoryOrder = NextOrder(s, entry.CategoryId) }; s.Entries.Add(added); });
        return added;
    }
    public (int Added, int Existing) AddRange(IEnumerable<Entry> entries)
    {
        int added = 0, existing = 0;
        db.Update(s => { var keys = s.Entries.Select(Identity).ToHashSet(StringComparer.Ordinal); foreach (var e in entries) { if (keys.Add(Identity(e))) { s.Entries.Add(e with { CategoryOrder = NextOrder(s, e.CategoryId) }); added++; } else existing++; } });
        return (added, existing);
    }
    public void Move(IEnumerable<string> ids, string categoryId)
    {
        var selected = ids.ToHashSet();
        db.Update(s =>
        {
            if (categoryId.Length > 0 && !s.Categories.Any(c => c.Id == categoryId))
                throw new InvalidOperationException("分类不存在。");
            var order = NextOrder(s, categoryId);
            var moving = s.Entries.Where(e => selected.Contains(e.Id) && e.CategoryId != categoryId).OrderBy(e => e.CategoryOrder).Select(e => e with { CategoryId = categoryId, CategoryOrder = order++ }).ToArray();
            var movingIds = moving.Select(e => e.Id).ToHashSet();
            s.Entries.RemoveAll(e => movingIds.Contains(e.Id));
            s.Entries.AddRange(moving);
        });
    }
    public void Reorder(string id, string before)
    {
        if (id == before)
            return;
        db.Update(s => { var entry = s.Entries.First(e => e.Id == id); var anchor = s.Entries.First(e => e.Id == before); s.Entries.Remove(entry); s.Entries.Insert(s.Entries.IndexOf(anchor), entry with { CategoryId = anchor.CategoryId }); NormalizeCategoryOrder(s, anchor.CategoryId); });
    }
    public void Remove(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet();
        db.Update(s => s.Entries.RemoveAll(e => selected.Contains(e.Id)));
    }
    public IEnumerable<Entry> Search(string query) => db.Current.Entries.Where(e => e.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || e.Target.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    public IEnumerable<Entry> Pinned() => db.Current.Entries.Where(e => e.Pinned).OrderBy(e => e.PinOrder).ThenBy(e => e.Name, StringComparer.CurrentCulture);
    public IEnumerable<Entry> InCategory(string id) => db.Current.Entries.Where(e => e.CategoryId == id).OrderBy(e => e.CategoryOrder);
    public void Rename(string id, string name)
    {
        name = name.Trim();
        if (name.Length is 0 or > 200) throw new InvalidOperationException("显示名称需为 1–200 字。");
        db.Update(s => { var at = s.Entries.FindIndex(e => e.Id == id); if (at < 0) throw new InvalidOperationException("入口已被移除。"); s.Entries[at] = s.Entries[at] with { Name = name }; });
    }
    public void Relocate(string id, Entry resolved)
    {
        db.Update(s =>
        {
            var at = s.Entries.FindIndex(e => e.Id == id);
            if (at < 0) throw new InvalidOperationException("入口已被移除。");
            s.Entries[at] = s.Entries[at] with { Target = resolved.Target, Kind = resolved.Kind, Arguments = resolved.Arguments, WorkingDirectory = resolved.WorkingDirectory, IconSource = resolved.IconSource };
        });
    }
    public void StepPinned(string id, int direction) => db.Update(s =>
    {
        var list = s.Entries.Where(e => e.Pinned).OrderBy(e => e.PinOrder).ThenBy(e => e.Name, StringComparer.CurrentCulture).ToList();
        Swap(list, list.FindIndex(e => e.Id == id), direction);
        for (var i = 0; i < list.Count; i++) { var at = s.Entries.FindIndex(e => e.Id == list[i].Id); s.Entries[at] = s.Entries[at] with { PinOrder = i }; }
    });
    public void StepEntry(string id, int direction) => db.Update(s =>
    {
        var entry = s.Entries.First(e => e.Id == id);
        var list = s.Entries.Where(e => e.CategoryId == entry.CategoryId).OrderBy(e => e.CategoryOrder).ToList();
        Swap(list, list.FindIndex(e => e.Id == id), direction);
        for (var i = 0; i < list.Count; i++) { var at = s.Entries.FindIndex(e => e.Id == list[i].Id); s.Entries[at] = s.Entries[at] with { CategoryOrder = i }; }
    });
    public void StepCategory(string id, int direction) => db.Update(s => Swap(s.Categories, s.Categories.FindIndex(c => c.Id == id), direction));
    private static void Swap<T>(List<T> list, int at, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (at < 0) throw new InvalidOperationException("项目已被移除。");
        var target = at + direction;
        if (target < 0 || target >= list.Count) return;
        (list[at], list[target]) = (list[target], list[at]);
    }
    private static double NextOrder(State state, string category) => state.Entries.Where(e => e.CategoryId == category).Select(e => e.CategoryOrder).DefaultIfEmpty(-1).Max() + 1;
    private static void NormalizeCategoryOrder(State state, string category)
    {
        var order = 0;
        for (var i = 0; i < state.Entries.Count; i++) if (state.Entries[i].CategoryId == category) state.Entries[i] = state.Entries[i] with { CategoryOrder = order++ };
    }
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
                if (selected.Contains(s.Entries[i].Id) && s.Entries[i].Pinned != pinned)
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
                if (i < 0) throw new InvalidOperationException("分类已被删除，请重新选择。");
                s.Categories[i] = s.Categories[i] with
                {
                    Name = name
                };
            }
        });
    }
    public void RemoveCategory(string id)
    {
        if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("不能删除未分类。");
        db.Update(s =>
        {
            var next = NextOrder(s, "");
            var moving = s.Entries.Where(e => e.CategoryId == id).OrderBy(e => e.CategoryOrder).ToArray();
            s.Categories.RemoveAll(c => c.Id == id);
            foreach (var entry in moving)
            {
                var at = s.Entries.FindIndex(e => e.Id == entry.Id);
                s.Entries[at] = entry with { CategoryId = "", CategoryOrder = next++ };
            }
        });
    }
    public void ReorderCategory(string id, string before)
    {
        if (id == before)
            return;
        db.Update(s => { var item = s.Categories.First(c => c.Id == id); s.Categories.Remove(item); s.Categories.Insert(s.Categories.FindIndex(c => c.Id == before), item); });
    }
}
