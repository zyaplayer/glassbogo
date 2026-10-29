using System.Runtime.InteropServices;

namespace GlassBogo.App;

// A real Win32 HWND with no WPF visual tree. Its black client pixels carry zero alpha
// in the DWM extended frame. A standard caption also makes this baseline easy to move manually.
internal sealed class GlassLabNativeWindow : IDisposable
{
    private delegate nint WndProc(nint hwnd, uint message, nint wparam, nint lparam);
    private static readonly WndProc windowProc = HandleMessage;
    private static readonly Dictionary<nint, GlassLabNativeWindow> windows = new();
    private const string ClassName = "GlassBogoV2NativeMaterialBaseline";
    private static bool registered;
    internal nint Handle { get; private set; }
    internal GlassBackdropResult Result { get; private set; } = null!;
    internal GlassHostOptions Options { get; }
    private Color fill;
    private bool opaque;

    internal GlassLabNativeWindow(GlassHostOptions options, nint owner, int x, int y)
    {
        Options = options with { Backend = GlassBackend.DesktopAcrylic, Layered = false, Chrome = false };
        if (!registered)
        {
            var cls = new WindowClass { Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = windowProc,
                Instance = GetModuleHandle(null), ClassName = ClassName, Cursor = LoadCursor(0, 32512) };
            if (RegisterClassEx(ref cls) == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            registered = true;
        }
        Handle = CreateWindowEx(options.Topmost ? 8u : 0u, ClassName, "Win32 基准 · 官方 Desktop Acrylic · 未经视觉验证",
            0x00CF0000, x, y, 432, 270, options.Owned ? owner : 0, 0, GetModuleHandle(null), 0);
        if (Handle == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        windows.Add(Handle, this);
        Refresh();
        ShowWindow(Handle, options.ActivateOnShow ? 5 : 4);
    }
    internal void Refresh()
    {
        if (Handle == 0) return;
        var system = GlassSystemState.Read();
        Result = GlassBackdrop.Apply(Handle, Options, system);
        opaque = Result.Applied == GlassBackend.Opaque;
        fill = GlassBackdrop.Fallback(Options.Dark);
        InvalidateRect(Handle, 0, false);
    }
    internal object Describe() => GlassBackdrop.DescribeWindow(Handle, null, Options, Result, GlassSystemState.Read());
    private static nint HandleMessage(nint hwnd, uint message, nint wparam, nint lparam)
    {
        if (message == 0x14) return 1; // WM_ERASEBKGND: paint is performed once below.
        if (message == 0x0F && windows.TryGetValue(hwnd, out var window))
        {
            var hdc = BeginPaint(hwnd, out var paint);
            try
            {
                GetClientRect(hwnd, out var rect);
                var color = window.opaque ? (uint)(window.fill.R | window.fill.G << 8 | window.fill.B << 16) : 0;
                var brush = CreateSolidBrush(color);
                try { FillRect(hdc, ref rect, brush); }
                finally { DeleteObject(brush); }
            }
            finally { EndPaint(hwnd, ref paint); }
            return 0;
        }
        if (message == 0x02 && windows.Remove(hwnd, out var closed)) closed.Handle = 0;
        return DefWindowProc(hwnd, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (Handle != 0) DestroyWindow(Handle);
        Handle = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    {
        public uint Size, Style; public WndProc Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? Menu, ClassName; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Paint
    {
        public nint Hdc; public int Erase; public NativeRect Rect; public int Restore, IncUpdate;
        // PAINTSTRUCT.rgbReserved[32]; avoid unsafe or an allocation per WM_PAINT.
        public long Reserved0, Reserved1, Reserved2, Reserved3;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint ex, string cls, string title, uint style, int x, int y, int width, int height, nint owner, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint DefWindowProc(nint hwnd, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int show);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, int cursor);
    [DllImport("user32.dll")] private static extern nint BeginPaint(nint hwnd, out Paint paint);
    [DllImport("user32.dll")] private static extern bool EndPaint(nint hwnd, ref Paint paint);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern int FillRect(nint hdc, ref NativeRect rect, nint brush);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("user32.dll")] private static extern bool InvalidateRect(nint hwnd, nint rect, bool erase);
}
