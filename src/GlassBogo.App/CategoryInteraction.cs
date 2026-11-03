namespace GlassBogo.App;

// Timer-free v2 interaction model. Tokens make delayed hover/close callbacks inert
// after a newer click, hover or teardown. The legacy model remains untouched.
internal sealed class CategoryInteraction
{
    internal const int HoverDelay = 150, CloseDelay = 300;
    internal string? Active { get; private set; }
    internal string? Pending { get; private set; }
    internal bool Pinned { get; private set; }
    internal int Generation { get; private set; }
    internal int? Hover(string id)
    {
        if (Pinned) return null;
        Pending = id;
        return ++Generation;
    }
    internal bool CommitHover(string id, int token)
    {
        if (Pinned || token != Generation || Pending != id) return false;
        Active = id; Pending = null;
        return true;
    }
    internal void Click(string id)
    {
        if (Active == id && Pinned) { Close(); return; }
        Generation++; Pending = null; Active = id; Pinned = true;
    }
    internal void Stay() { Generation++; Pending = null; }
    internal int? Leave() => Pinned ? null : ++Generation;
    internal bool CommitClose(int token, bool held, bool inCorridor)
    {
        if (token != Generation || Pinned || held || inCorridor) return false;
        Close(); return true;
    }
    internal void Launched() { if (!Pinned) Close(); }
    internal void Close() { Generation++; Active = Pending = null; Pinned = false; }
}
