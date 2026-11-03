using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace GlassBogo.App;

// Event-driven, process-owned top-level window. Never reparents into Explorer or
// changes another process's window. Kept independent of the legacy dock until QA.
internal sealed class DesktopLayer : IDisposable
{
    private readonly Window window;
    private readonly nint hwnd;
    private readonly HwndSource source;
    private readonly Native.WinEventProc callback;
    private nint foregroundHook, minimizeHook;
    private volatile bool disposed;
    private int queued;
    internal int DesktopRestores { get; private set; }

    internal DesktopLayer(Window window)
    {
        this.window = window;
        hwnd = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(hwnd)!;
        window.Topmost = false;
        window.ShowActivated = false;
        source.AddHook(Message);
        callback = (_, ev, target, _, _, _, _) =>
        {
            if (ev != 3 && target != hwnd) return;
            if (disposed || window.Dispatcher.HasShutdownStarted || Interlocked.Exchange(ref queued, 1) != 0) return;
            try { window.Dispatcher.BeginInvoke(() => { Interlocked.Exchange(ref queued, 0); if (!disposed) Assess(); }); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref queued, 0); }
        };
        foregroundHook = Native.SetWinEventHook(3, 3, 0, callback, 0, 0, 2);
        minimizeHook = Native.SetWinEventHook(0x16, 0x17, 0, callback, 0, 0, 2);
        if (foregroundHook == 0 || minimizeHook == 0)
        {
            Dispose();
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "桌面层事件订阅失败。");
        }
        Assess();
    }

    private nint Message(nint handle, int msg, nint w, nint l, ref bool handled)
    {
        if (msg == 0x21) { handled = true; return 3; } // MA_NOACTIVATE
        return 0;
    }

    internal void Assess()
    {
        var foreground = Native.GetForegroundWindow();
        if (IsDesktop(foreground)) RestoreOnDesktop(foreground);
        else if (foreground != 0 && foreground != hwnd && !IsIconic(foreground))
            PositionOnDesktop(foreground, show: false);
    }

    internal void PlaceBelow(nint target, uint flags = 0x13)
    {
        // Inserting into a topmost group's Z order can promote this window.
        // Keep the dock explicitly in the non-topmost group instead.
        if (target != 0 && (GlassNative.GetWindowLongPtr(target, -20).ToInt64() & 8) != 0) target = new(-2);
        Native.SetWindowPos(hwnd, target, 0, 0, 0, 0, flags);
    }

    internal void RestoreOnDesktop(nint desktop)
        => PositionOnDesktop(desktop, show: true);

    private void PositionOnDesktop(nint desktop, bool show)
    {
        if (disposed) return;
        // Insert immediately above the desktop root, not at HWND_BOTTOM (which
        // can put the dock behind an opaque WorkerW wallpaper window).
        var list = Native.DesktopList();
        if (list == 0 && !show) { PlaceBelow(desktop); return; }
        var root = list != 0 ? GetAncestor(list, 2) : desktop;
        var above = GetWindow(root, 3); // GW_HWNDPREV
        if (above == hwnd) above = GetWindow(hwnd, 3);
        if (show) Native.ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        PlaceBelow(above, show ? 0x53u : 0x13u);
        if (show) DesktopRestores++;
    }

    internal static bool IsDesktop(nint handle)
    {
        if (handle == 0) return false;
        var name = new StringBuilder(128);
        Native.GetClassName(handle, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
        if (minimizeHook != 0) Native.UnhookWinEvent(minimizeHook);
        source.RemoveHook(Message);
    }

    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern bool IsIconic(nint hwnd);
}
