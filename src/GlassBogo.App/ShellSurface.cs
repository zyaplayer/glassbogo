using System.Runtime.InteropServices;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace GlassBogo.App;

internal record PixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left; internal int Height => Bottom - Top;
}
internal record Display(string DeviceName, PixelRect Bounds, PixelRect WorkingArea, bool Primary, double Scale)
{
    private delegate bool MonitorProc(nint monitor, nint dc, nint rect, nint data);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size; public Native.Rect Bounds, Work; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProc callback, nint data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
    private static Display Read(nint handle)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(handle, ref info))
            throw new InvalidOperationException("无法读取显示器。");
        GetDpiForMonitor(handle, 0, out var dpi, out _);
        return new(info.Device, new(info.Bounds.Left, info.Bounds.Top, info.Bounds.Right, info.Bounds.Bottom), new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), (info.Flags & 1) != 0, dpi == 0 ? 1 : dpi / 96d);
    }
    internal static Display[] AllScreens
    {
        get
        {
            var list = new List<Display>();
            EnumDisplayMonitors(0, 0, (m, _, _, _) => { list.Add(Read(m)); return true; }, 0);
            return list.ToArray();
        }
    }
    internal static Display PrimaryScreen => AllScreens.First(s => s.Primary);
    internal static Display FromHandle(nint hwnd) => Read(MonitorFromWindow(hwnd, 2));
}
internal sealed class Tray : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public int Size; public nint Window; public uint Id, Flags, Callback; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip; public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info; public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle; public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint action, ref NotifyData data);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint module, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    private nint customIcon;
    private NotifyData data; private readonly HwndSource source; private readonly Action activate; private readonly ContextMenu menu = new(); private readonly uint taskbarCreated;
    internal Tray(Window window, Action open, Action calendar, Action restore, Action exit)
    {
        activate = open;
        var handle = new WindowInteropHelper(window).Handle;
        source = HwndSource.FromHwnd(handle)!;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        foreach (var item in new[] { ("工作台", open), ("日历", calendar), ("恢复桌面图标", restore), ("退出", exit) })
        {
            var entry = new MenuItem { Header = item.Item1 };
            entry.Click += (_, _) => Design.Guard(item.Item2);
            menu.Items.Add(entry);
        }
        data = new()
        {
            Size = Marshal.SizeOf<NotifyData>(),
            Window = handle,
            Id = 1,
            Flags = 1 | 2 | 4,
            Callback = 0x8001,
            Icon = LoadIcon(0, (nint)32512),
            Tip = "GlassBogo"
        };
        ExtractIconEx(Environment.ProcessPath!, 0, out var large, out var small, 1);
        if (large != 0)
            DestroyIcon(large);
        if (small != 0)
        {
            customIcon = small;
            data.Icon = small;
        }
        source.AddHook(Message);
        Shell_NotifyIcon(0, ref data);
    }
    private nint Message(nint hwnd, int msg, nint w, nint l, ref bool handled)
    {
        if ((uint)msg == taskbarCreated)
            Shell_NotifyIcon(0, ref data);
        if (msg == 0x8001)
        {
            if ((int)l == 0x203)
                activate();
            if ((int)l == 0x205)
            {
                SetForegroundWindow(hwnd);
                menu.Placement = PlacementMode.MousePoint;
                menu.IsOpen = true;
            }
            handled = true;
        }
        return 0;
    }
    public void Dispose()
    {
        menu.IsOpen = false;
        Shell_NotifyIcon(2, ref data);
        source.RemoveHook(Message);
        if (customIcon != 0)
        {
            DestroyIcon(customIcon);
            customIcon = 0;
        }
    }
}
