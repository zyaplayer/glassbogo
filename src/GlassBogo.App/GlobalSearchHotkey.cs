using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace GlassBogo.App;

// Dedicated invisible message-only HWND: registration does not depend on the dock
// being visible, foreground, topmost or recreated after a display change.
internal sealed class GlobalSearchHotkey : IDisposable
{
    private readonly HwndSource source;
    private readonly Action invoked;
    private readonly HashSet<int> registrations = [];
    private int nextId = 100, currentId;
    private bool changing;
    internal SearchShortcut? Current { get; private set; }
    internal GlobalSearchHotkey(Action invoked)
    {
        this.invoked = invoked;
        source = new HwndSource(new HwndSourceParameters("GlassBogo 2.0 Search Hotkey") { ParentWindow = new nint(-3), WindowStyle = 0 });
        source.AddHook(Message);
    }
    internal bool TryChange(SearchShortcut desired, Action<SearchShortcut> persist, out string error)
    {
        source.Dispatcher.VerifyAccess();
        if (desired.Modifiers == 0 || (desired.Modifiers & ~15u) != 0 || desired.VirtualKey is 0 or > 254)
        { error = "请选择有效的组合快捷键。"; return false; }
        if (desired == Current)
        {
            try { persist(desired); error = ""; return true; }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        var candidate = ++nextId;
        if (candidate > 0xBFFF) { error = "本次会话的快捷键变更次数过多，请重启。"; return false; }
        if (!RegisterHotKey(source.Handle, candidate, desired.Modifiers | 0x4000u, desired.VirtualKey))
        { error = "快捷键已被占用或系统拒绝注册：" + new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
        registrations.Add(candidate);
        try { persist(desired); }
        catch (Exception ex) { UnregisterHotKey(source.Handle, candidate); registrations.Remove(candidate); error = ex.Message; return false; }
        if (currentId != 0 && UnregisterHotKey(source.Handle, currentId)) registrations.Remove(currentId);
        Current = desired; currentId = candidate; error = "";
        return true;
    }
    internal async Task<(bool Success, string Error)> TryChangeAsync(SearchShortcut desired, Func<SearchShortcut, Task> persist)
    {
        source.Dispatcher.VerifyAccess();
        if (changing) return (false, "正在保存快捷键。");
        if (desired.Modifiers == 0 || (desired.Modifiers & ~15u) != 0 || desired.VirtualKey is 0 or > 254) return (false, "请选择有效的组合快捷键。");
        changing = true;
        var candidate = 0;
        try
        {
            if (desired != Current)
            {
                candidate = ++nextId;
                if (candidate > 0xBFFF) return (false, "快捷键变更次数过多，请重启。");
                if (!RegisterHotKey(source.Handle, candidate, desired.Modifiers | 0x4000u, desired.VirtualKey)) return (false, "快捷键已被其他程序占用，请重新选择。");
                registrations.Add(candidate);
            }
            await persist(desired);
            if (candidate != 0)
            {
                if (currentId != 0 && UnregisterHotKey(source.Handle, currentId)) registrations.Remove(currentId);
                Current = desired; currentId = candidate; candidate = 0;
            }
            return (true, "");
        }
        catch (Exception ex) { return (false, "未保存：" + ex.Message); }
        finally
        {
            if (candidate != 0 && registrations.Contains(candidate)) { UnregisterHotKey(source.Handle, candidate); registrations.Remove(candidate); }
            changing = false;
        }
    }
    private nint Message(nint hwnd, int message, nint w, nint l, ref bool handled)
    {
        if (message == 0x312 && w == currentId) { handled = true; invoked(); }
        return 0;
    }
    public void Dispose()
    {
        source.Dispatcher.VerifyAccess();
        foreach (var id in registrations) UnregisterHotKey(source.Handle, id);
        registrations.Clear(); source.RemoveHook(Message); source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
