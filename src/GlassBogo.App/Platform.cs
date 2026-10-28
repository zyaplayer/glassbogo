using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace GlassBogo.App;

internal static class Native
{
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern nint FindWindowEx(nint parent, nint after, string? cls, string? title);
    internal delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(nint hwnd, System.Text.StringBuilder name, int max);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(nint hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint hwnd, int id);
    internal delegate void WinEventProc(nint hook, uint ev, nint hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint min, uint max, nint module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] internal static extern bool UnhookWinEvent(nint hook);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint SHGetFileInfo(string path, uint attr, ref ShellInfo info, uint size, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellInfo
    {
        public nint Icon; public int Index; public uint Attributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    internal static ImageSource? GetIcon(string target)
    {
        var info = new ShellInfo();
        SHGetFileInfo(target, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100 | 0x1);
        if (info.Icon == 0)
            return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            image.Freeze();
            return image;
        }
        finally { DestroyIcon(info.Icon); }
    }
    internal static nint DesktopList()
    {
        nint list = 0;
        EnumWindows((window, _) => { var view = FindWindowEx(window, 0, "SHELLDLL_DefView", null); if (view != 0) list = FindWindowEx(view, 0, "SysListView32", null); return list == 0; }, 0);
        return list;
    }
}
internal sealed class WindowsDesktop : IDesktopIntegration
{
    private readonly string recovery;
    private readonly List<FileSystemWatcher> watchers = [];
    public event EventHandler? Changed;
    public bool HiddenByUs => File.Exists(recovery);
    internal static string[] Folders => new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    public WindowsDesktop(string data)
    {
        recovery = Path.Combine(data, "desktop-visibility.json");
        if (File.Exists(recovery))
            RestoreIcons();
        foreach (var folder in Folders)
        {
            var w = new FileSystemWatcher(folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName, EnableRaisingEvents = true };
            w.Created += OnChange;
            w.Deleted += OnChange;
            w.Renamed += OnChange;
            w.Error += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
            watchers.Add(w);
        }
    }
    private void OnChange(object sender, FileSystemEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
    public async Task<IReadOnlyList<Entry>> DiscoverAsync(CancellationToken token)
    {
        return await Platform.Sta(() =>
        {
            var entries = new List<Entry>();
            foreach (var folder in Folders)
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                {
                    token.ThrowIfCancellationRequested();
                    if (Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        continue;
                    try
                    {
                        entries.Add(Platform.Resolve(path));
                    }
                    catch (Exception ex) when (ex is IOException or COMException or UnauthorizedAccessException) { }
                }
            return (IReadOnlyList<Entry>)entries;
        });
    }
    public void HideIcons()
    {
        var hwnd = Native.DesktopList();
        if (hwnd == 0)
            throw new InvalidOperationException("找不到 Windows 桌面，请稍后重试。");
        if (!File.Exists(recovery))
            Database.AtomicWrite(recovery, Native.IsWindowVisible(hwnd) ? "true" : "false");
        Native.ShowWindow(hwnd, 0);
    }
    public void RestoreIcons()
    {
        if (!File.Exists(recovery))
            return;
        var hwnd = Native.DesktopList();
        if (hwnd == 0)
            throw new InvalidOperationException("桌面尚未就绪。恢复记录已保留，可稍后重试。");
        var visible = bool.Parse(File.ReadAllText(recovery));
        Native.ShowWindow(hwnd, visible ? 5 : 0);
        File.Delete(recovery);
    }
    public void Dispose()
    {
        foreach (var w in watchers)
            w.Dispose();
        RestoreIcons();
    }
}
internal sealed class Launcher : ILaunchService
{
    public Task<LaunchResult> LaunchAsync(Entry entry) => Task.Run(() =>
    {
        try
        {
            if (entry.Kind == "app")
                Process.Start(new ProcessStartInfo("explorer.exe", entry.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? entry.Target : "shell:AppsFolder\\" + entry.Target) { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(entry.Target) { UseShellExecute = true, Arguments = entry.Arguments, WorkingDirectory = entry.WorkingDirectory });
            return new LaunchResult(true);
        }
        catch (Exception ex) { return new LaunchResult(false, ex.Message); }
    });
}
internal static class Platform
{
    internal static Task<T> Sta<T>(Func<T> action)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { source.SetResult(action()); } catch (Exception ex) { source.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return source.Task;
    }
    internal static Entry Resolve(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        string target = Path.GetFullPath(path), args = "", working = "";
        if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            object? shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                link = ((dynamic)shell!).CreateShortcut(path);
                var resolved = (string)((dynamic)link).TargetPath;
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    target = resolved;
                    args = (string)((dynamic)link).Arguments;
                    working = (string)((dynamic)link).WorkingDirectory;
                }
            }
            finally { if (link is not null) Marshal.FinalReleaseComObject(link); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        }
        return new(Guid.NewGuid().ToString("N"), name, target, "path", args, working);
    }
    internal static Task<List<Entry>> InstalledApps() => Sta(() =>
    {
        var entries = new List<Entry>();
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) }.Where(Directory.Exists))
        {
            foreach (var p in Directory.EnumerateFiles(folder, "*.lnk", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                try
                {
                    entries.Add(Resolve(p));
                }
                catch (COMException) { }
        }
        object? shell = null, folderObject = null, items = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!);
            folderObject = ((dynamic)shell!).NameSpace("shell:AppsFolder");
            if (folderObject is not null)
            {
                items = ((dynamic)folderObject).Items();
                foreach (var item in (dynamic)items)
                {
                    try
                    {
                        string path = item.Path, name = item.Name;
                        string appId = Convert.ToString(item.ExtendedProperty("System.AppUserModel.ID")) ?? "";
                        if (!string.IsNullOrWhiteSpace(appId))
                            entries.Add(new(Guid.NewGuid().ToString("N"), name, appId, "app"));
                        else if (File.Exists(path))
                            entries.Add(Resolve(path) with
                            {
                                Name = name
                            });
                    }
                    finally { Marshal.FinalReleaseComObject(item); }
                }
            }
        }
        finally { if (items is not null) Marshal.FinalReleaseComObject(items); if (folderObject is not null) Marshal.FinalReleaseComObject(folderObject); if (shell is not null) Marshal.FinalReleaseComObject(shell); }
        return entries.DistinctBy(Catalog.Identity, StringComparer.Ordinal).OrderBy(e => e.Name).ToList();
    });
    internal static void AutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue("GlassBogo", $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue("GlassBogo", false);
    }
}
internal sealed class IconCache
{
    private static ImageSource Fallback { get; } = CreateFallback();
    private static ImageSource CreateFallback()
    {
        var group = new DrawingGroup();
        var brush = new SolidColorBrush(Color.FromRgb(100, 155, 210));
        group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(2, 2, 28, 28), 7, 7)));
        for (int i = 0; i < 4; i++)
            group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(8 + i % 2 * 10, 8 + i / 2 * 10, 6, 6), 2, 2)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim workers = new(2);
    internal async Task<ImageSource?> Get(Entry entry)
    {
        if (entry.Kind != "path")
            return Fallback;
        lock (cache)
            if (cache.TryGetValue(entry.Target, out var hit))
                return hit;
        await workers.WaitAsync();
        try
        {
            lock (cache)
                if (cache.TryGetValue(entry.Target, out var hit))
                    return hit;
            var icon = await Task.Run(() => Native.GetIcon(entry.Target));
            lock (cache)
            {
                if (cache.Count >= 512)
                    cache.Remove(cache.Keys.First());
                cache[entry.Target] = icon ?? Fallback;
            }
            return icon ?? Fallback;
        }
        finally { workers.Release(); }
    }
}
