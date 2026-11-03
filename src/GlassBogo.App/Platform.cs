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
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(IconSize size, int flags, out nint bitmap);
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IconSize
    {
        public int Width, Height;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, nint bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint handle);
    // 256px 覆盖 40 DIP 图标在 300% 缩放下的显示；SHGetFileInfo 回退只有 16/32px，放大必然模糊。
    private const int IconPixels = 256;
    internal static ImageSource? GetIcon(string target) => TryShellImage(target) ?? TryShellIcon(target);
    private static ImageSource? TryShellImage(string target)
    {
        IShellItemImageFactory? factory = null;
        nint bitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(target, 0, ref iid, out factory);
            factory.GetImage(new IconSize { Width = IconPixels, Height = IconPixels }, 0x4, out bitmap);
            if (bitmap == 0)
                return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is COMException or FileNotFoundException or ArgumentException or NotSupportedException) { return null; }
        finally
        {
            if (bitmap != 0) DeleteObject(bitmap);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
        }
    }
    private static ImageSource? TryShellIcon(string target)
    {
        var info = new ShellInfo();
        if (SHGetFileInfo(target, 0, ref info, (uint)Marshal.SizeOf<ShellInfo>(), 0x100) == 0 || info.Icon == 0)
            return null;
        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
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
    public IReadOnlyList<string> ItemPaths()
    {
        var items = new List<string>();
        foreach (var folder in Folders)
        {
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(folder))
                    if (!Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                        items.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return items;
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
        var name = TargetName(path, Directory.Exists(path));
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
        return new(Guid.NewGuid().ToString("N"), name, target, "path", args, working, IconSource: path);
    }
    internal static string TargetName(string path, bool folder = false)
    {
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        var name = folder ? leaf : Path.GetFileNameWithoutExtension(leaf);
        return string.IsNullOrEmpty(name) ? string.IsNullOrEmpty(leaf) ? Path.GetPathRoot(Path.GetFullPath(path))! : leaf : name;
    }
    internal static Task<List<Entry>> DesktopEntries() => Sta(() =>
    {
        var entries = new List<Entry>();
        // Desktop refresh is deliberately shallow and never includes the Start menu.
        foreach (var desktop in WindowsDesktop.Folders)
            foreach (var path in Directory.EnumerateFileSystemEntries(desktop, "*", new EnumerationOptions { IgnoreInaccessible = true }))
                if (!Path.GetFileName(path).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    try { entries.Add(Resolve(path)); }
                    catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
        return entries.DistinctBy(Catalog.Identity, StringComparer.Ordinal).OrderBy(e => e.Name).ToList();
    });
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
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
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
                        if (File.Exists(path))
                            entries.Add(Resolve(path) with
                            {
                                Name = name
                            });
                        else if (!string.IsNullOrWhiteSpace(appId))
                            entries.Add(new(Guid.NewGuid().ToString("N"), name, appId, "app"));
                    }
                    catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException) { }
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
    private int epoch;
    private readonly Func<string, Task<ImageSource?>> load;
    internal IconCache(Func<string, Task<ImageSource?>>? load = null) => this.load = load ?? (target => Platform.Sta(() => target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(target) || Directory.Exists(target) ? Native.GetIcon(target) : null));
    internal void Clear() { lock (cache) epoch++; }
    private static ImageSource Fallback { get; } = CreateFallback();
    private static ImageSource CreateFallback()
    {
        var group = new DrawingGroup();
        var brush = new SolidColorBrush(Color.FromRgb(113, 128, 150));
        group.Children.Add(new GeometryDrawing(brush, null, new RectangleGeometry(new Rect(2, 4, 28, 24), 4, 4)));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new RectangleGeometry(new Rect(5, 11, 22, 14), 1, 1)));
        group.Children.Add(new GeometryDrawing(Brushes.White, null, new EllipseGeometry(new Point(7, 8), 1, 1)));
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private sealed record Cached(ImageSource? Icon, DateTime RetryAt, int Epoch);
    private readonly Dictionary<string, Cached> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim workers = new(2);
    internal async Task<ImageSource?> Get(Entry entry)
    {
        if (entry.Kind == "url")
            return Fallback;
        var target = entry.Kind == "app" && !entry.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? "shell:AppsFolder\\" + entry.Target : entry.Target;
        var targets = new[] { entry.IconSource, target }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var iconTarget = string.Join('\0', targets);
        lock (cache)
            if (cache.TryGetValue(iconTarget, out var hit) && hit.Epoch == epoch && DateTime.UtcNow < hit.RetryAt)
                return hit.Icon ?? Fallback;
        await workers.WaitAsync();
        try
        {
            int requestEpoch;
            ImageSource? previous;
            lock (cache)
            {
                if (cache.TryGetValue(iconTarget, out var hit) && hit.Epoch == epoch && DateTime.UtcNow < hit.RetryAt)
                    return hit.Icon ?? Fallback;
                previous = hit?.Icon;
                requestEpoch = epoch;
            }
            ImageSource? icon = null;
            foreach (var source in targets)
            {
                try { icon = await load(source); }
                catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
                if (icon is not null) break;
            }
            lock (cache)
            {
                if (requestEpoch == epoch)
                {
                    if (cache.Count >= 512 && !cache.ContainsKey(iconTarget)) cache.Remove(cache.Keys.First());
                    cache[iconTarget] = new(icon ?? previous, icon is null ? DateTime.UtcNow.AddSeconds(3) : DateTime.MaxValue, epoch);
                }
            }
            return icon ?? previous ?? Fallback;
        }
        finally { workers.Release(); }
    }
}
