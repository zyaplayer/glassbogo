using Microsoft.Win32;
using System.Windows.Threading;

namespace GlassBogo.App;

internal sealed class V2Context : IAsyncDisposable
{
    internal Database Db { get; }
    internal Catalog Catalog { get; }
    internal CalendarService Calendar { get; }
    internal readonly IconCache Icons = new();
    internal readonly AppDiscovery Discovery;
    internal readonly AppDiscovery DesktopDiscovery;
    internal readonly DesktopRefresh Refresher;
    internal readonly DesktopIconController DesktopIcons;
    internal readonly LiquidGlassService Graphics;
    internal readonly bool Qa;
    private readonly ILaunchService launcher;
    private readonly SemaphoreSlim writes = new(1);
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly GlobalSearchHotkey hotkey;
    private Task changingHotkey = Task.CompletedTask;
    private Preferences applied;
    internal bool Closing { get; private set; }
    internal int Menus, Modals;
    internal Dock2Window? Dock;
    private Manager2Window? manager;
    private Search2Window? search;
    private DesktopCalendarWindow? calendar;
    internal DesktopCalendarWindow? CalendarWindow => calendar;
    internal int IconRevision { get; private set; }
    internal event EventHandler? Changed;
    internal string HotkeyStatus { get; private set; } = "尚未注册";
    internal Preferences Preferences => Db.Current.Preferences;
    internal double IconSize => Preferences.IconSize switch { "small" => 32, "large" => 48, _ => 40 };
    internal V2Context(Database database, bool qa = false, ILaunchService? launcher = null, AppDiscovery? discovery = null, Func<Entry, Task<TargetState>>? targetProbe = null, AppDiscovery? desktopDiscovery = null)
    {
        Db = database; Qa = qa; this.launcher = launcher ?? new Launcher(); Catalog = new(Db); Calendar = new(Db);
        Discovery = discovery ?? new(qa ? () => Task.FromResult(new List<Entry>()) : null);
        DesktopDiscovery = desktopDiscovery ?? new(qa ? () => Task.FromResult(new List<Entry>()) : Platform.DesktopEntries);
        Refresher = new(DesktopDiscovery, targetProbe ?? (qa ? _ => Task.FromResult(TargetState.Unknown) : null));
        DesktopIcons = new(Db.DirectoryPath, qa ? new MemoryDesktopIconBackend() : null, listen: !qa);
        applied = Preferences;
        Design.Apply(Preferences.Theme);
        Graphics = new(() => Preferences, qa);
        hotkey = new(ToggleSearch);
        if (!qa)
        {
            var shortcut = Preferences.SearchHotkey ?? new SearchShortcut();
            HotkeyStatus = hotkey.TryChange(shortcut, _ => { }, out var error) ? "已启用" : error;
        }
        Db.Changed += DataChanged;
        SystemEvents.UserPreferenceChanged += ThemeChanged;
    }
    private void DataChanged(object? sender, EventArgs e) => dispatcher.BeginInvoke(() =>
    {
        if (Closing) return;
        var next = Preferences;
        if (applied.Theme != next.Theme) Design.Apply(next.Theme);
        if (applied.Theme != next.Theme || applied.VisualQuality != next.VisualQuality) Graphics.Refresh();
        applied = next; SyncCalendar(); Changed?.Invoke(this, EventArgs.Empty);
    });
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => dispatcher.BeginInvoke(() => { if (!Closing) { Design.Apply(Preferences.Theme); Graphics.Refresh(); Changed?.Invoke(this, EventArgs.Empty); } });
    internal async Task Write(Action action)
    {
        if (Closing) throw new InvalidOperationException("程序正在退出。");
        await writes.WaitAsync();
        try { if (Closing) throw new InvalidOperationException("程序正在退出。"); await Task.Run(action); }
        finally { writes.Release(); }
    }
    internal async Task<DesktopRefreshResult> RefreshDesktop(CancellationToken token)
    {
        var result = await Refresher.Run(Db, Write, token);
        if (!Closing)
        {
            Icons.Clear();
            IconRevision++;
            await dispatcher.InvokeAsync(() => { if (!Closing) Changed?.Invoke(this, EventArgs.Empty); });
        }
        return result;
    }
    internal Task InitializeDesktopIcons() => Write(() => DesktopIcons.Initialize(Preferences.HideDesktopIcons));
    internal void SyncCalendar()
    {
        if (Closing || Dock is not { IsLoaded: true }) return;
        if (calendar is null && Preferences.DesktopCalendarVisible) calendar = new(this);
        calendar?.SyncVisibility();
    }
    internal Task SetCalendarVisible(bool visible) => Write(() => Db.Update(s => s.Preferences = s.Preferences with { DesktopCalendarVisible = visible }));
    internal Task SetCalendarView(string view) => Write(() => Db.Update(s => s.Preferences = s.Preferences with { CalendarView = view }));
    internal Task SaveCalendarPosition(double x, double y) => Write(() => Db.Update(s => s.Preferences = s.Preferences with { CalendarX = x, CalendarY = y }));
    internal Task SetDesktopIconsHidden(bool value) => Write(() =>
    {
        var previous = Preferences.HideDesktopIcons;
        try
        {
            DesktopIcons.SetHidden(value);
            Db.Update(s => s.Preferences = s.Preferences with { HideDesktopIcons = value });
        }
        catch
        {
            DesktopIcons.SetHidden(previous);
            throw;
        }
    });
    internal async Task<LaunchResult> Launch(Entry entry)
    {
        var current = Db.Current.Entries.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
        var result = await launcher.LaunchAsync(current);
        if (result.Success && !Closing && Db.Current.Entries.Any(e => e.Id == current.Id))
        {
            try { await Write(() => Catalog.Touch(current.Id, DateTime.Now)); }
            catch { /* A launched target must not be reported as failed because recents could not be saved. */ }
        }
        return result;
    }
    internal void OpenManager()
    {
        if (Closing) return;
        if (manager is null) { manager = new(this); manager.Closed += (_, _) => manager = null; }
        if (manager.WindowState == WindowState.Minimized) manager.WindowState = WindowState.Normal;
        manager.Show(); manager.Activate();
    }
    internal Manager2Window? Manager => manager;
    internal Search2Window Search => search ??= new(this);
    internal void ToggleSearch()
    {
        if (Closing) return;
        if (Search.IsVisible) Search.Dismiss(true); else { Dock?.DismissPanel(); Search.Open(); }
    }
    internal async Task<(bool Success, string Error)> ChangeHotkey(SearchShortcut shortcut)
    {
        if (Closing) return (false, "程序正在退出。");
        if (!changingHotkey.IsCompleted) return (false, "正在保存快捷键。");
        (bool Success, string Error) result = default;
        changingHotkey = Change(); await changingHotkey; return result;
        async Task Change()
        {
            result = await hotkey.TryChangeAsync(shortcut, value => Write(() => Db.Update(s => s.Preferences = s.Preferences with { SearchHotkey = value })));
            HotkeyStatus = result.Success ? "已启用" : result.Error;
        }
    }
    public async ValueTask DisposeAsync()
    {
        Closing = true; Db.Changed -= DataChanged; SystemEvents.UserPreferenceChanged -= ThemeChanged;
        await changingHotkey;
        hotkey.Dispose(); Discovery.Dispose(); DesktopDiscovery.Dispose();
        manager?.Close(); search?.Close(); calendar?.Close(); Dock?.Close();
        await Graphics.DisposeAsync();
        await writes.WaitAsync();
        try
        {
            try { await DesktopIcons.DisposeAsync(); }
            finally { await Task.Run(Db.Dispose); }
        }
        finally { writes.Release(); }
    }
}
