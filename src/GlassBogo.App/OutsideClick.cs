using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace GlassBogo.App;

// Exists only while a category panel is open. Never swallows input or records it;
// foreground activation alone is not a click (launching must preserve pinned panels).
internal sealed class OutsideClick : IDisposable
{
    private delegate nint MouseProc(int code, nint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public int X, Y; public uint Mouse, Flags, Time; public nuint Extra; }
    private readonly MouseProc callback;
    private nint hook;
    private static int active;
    internal static int ActiveCount => Volatile.Read(ref active);
    internal OutsideClick(Func<bool> held, Func<nint[]> windows, Action outside)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        callback = (code, message, data) =>
        {
            try
            {
                if (code >= 0 && hook != 0 && (message == 0x201 || message == 0x204 || message == 0x207) && !held())
                {
                    var mouse = Marshal.PtrToStructure<MouseData>(data);
                    var inside = windows().Any(hwnd => Native.IsWindowVisible(hwnd) && Native.GetWindowRect(hwnd, out var r) && mouse.X >= r.Left && mouse.X < r.Right && mouse.Y >= r.Top && mouse.Y < r.Bottom);
                    if (!inside) dispatcher.BeginInvoke(() => { if (hook != 0) outside(); });
                }
            }
            catch (InvalidOperationException) { }
            return CallNextHookEx(0, code, message, data);
        };
        hook = SetWindowsHookEx(14, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法订阅面板外部点击。");
        Interlocked.Increment(ref active);
    }
    public void Dispose() { if (hook != 0) { UnhookWindowsHookEx(hook); hook = 0; Interlocked.Decrement(ref active); } }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, MouseProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
