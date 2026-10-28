using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace GlassBogo.App;

internal static class DesktopVisibilityTest
{
    private delegate nint WindowProc(nint hwnd, uint message, nint w, nint l);
    private static readonly WindowProc proc = DefWindowProc;
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style; public WindowProc Proc; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? Menu, Class;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClass(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nint w, nint l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint obj, int index, StringBuilder name, int length, out int needed);
    internal static void RequireIsolatedDesktop()
    {
        var name = new StringBuilder(256);
        GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _);
        if (!name.ToString().StartsWith("GlassBogo-QA-", StringComparison.Ordinal))
            throw new InvalidOperationException("UI 验收只允许通过 DesktopHarness 在隔离桌面运行。");
    }
    internal static void Verify(WindowsDesktop desktop, string data)
    {
        RequireIsolatedDesktop();
        var shellClass = new WindowClass { Proc = proc, Class = "SHELLDLL_DefView" };
        RegisterClass(ref shellClass);
        var listClass = new WindowClass { Proc = proc, Class = "SysListView32" };
        RegisterClass(ref listClass);
        var parent = new GlassWindow { Width = 250, Height = 100, Title = "QA desktop simulation" };
        parent.Show();
        var root = new WindowInteropHelper(parent).Handle;
        var view = CreateWindowEx(0, "SHELLDLL_DefView", "", 0x50000000, 0, 0, 200, 80, root, 0, 0, 0);
        var list = CreateWindowEx(0, "SysListView32", "", 0x50000000, 0, 0, 200, 80, view, 0, 0, 0);
        try
        {
            if (view == 0 || list == 0 || Native.DesktopList() != list)
                throw new InvalidOperationException("无法建立隔离桌面模拟。");
            desktop.HideIcons();
            if (Native.IsWindowVisible(list) || !desktop.HiddenByUs)
                throw new Exception("隐藏未生效。");
            desktop.RestoreIcons();
            if (!Native.IsWindowVisible(list) || desktop.HiddenByUs)
                throw new Exception("恢复未生效。");
            Native.ShowWindow(list, 0);
            desktop.HideIcons();
            desktop.RestoreIcons();
            if (Native.IsWindowVisible(list))
                throw new Exception("原本隐藏状态未保留。");
            Native.ShowWindow(list, 5);
            desktop.HideIcons();
            using (var recovery = new WindowsDesktop(data))
            {
            }
            if (!Native.IsWindowVisible(list))
                throw new Exception("重启恢复未生效。");
        }
        finally { DestroyWindow(view); parent.Close(); }
    }
}
