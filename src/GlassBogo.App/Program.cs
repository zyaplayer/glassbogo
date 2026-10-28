using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GlassBogo.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--qa")
        {
            SelfTest.Run(args[1], args.Length > 2 ? int.Parse(args[2]) : 10);
            return;
        }
        var data = Environment.GetEnvironmentVariable("GLASSBOGO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo");
        if (args.Contains("--restore-desktop"))
        {
            try
            {
                using var desktop = new WindowsDesktop(data);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "GlassBogo 恢复桌面"); }
            return;
        }
        using var mutex = new Mutex(true, @"Local\GlassBogo-" + Environment.UserName, out var created);
        var activationName = @"Local\GlassBogo-Open-" + Environment.UserName;
        if (!created)
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(activationName);
                existing.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("GlassBogo 正在启动，请稍后从托盘打开。", "GlassBogo"); }
            return;
        }
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
        if (args.Length == 2 && args[0] == "--recover-data")
        {
            try
            {
                DatabaseRecovery.Restore(data, args[1]);
                MessageBox.Show("数据已恢复，原数据库已归档。请重新启动 GlassBogo。", "GlassBogo");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "恢复失败"); }
            return;
        }
        if (args.Length == 3 && args[0] == "--migrate")
        {
            try
            {
                using var db = new Database(data);
                var report = args[1] == "glassdesk" ? Platform.Sta(() => Migration.GlassDesk(db, args[2], e => Path.GetExtension(e.Target).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? Platform.Resolve(e.Target) with { Id = e.Id, Name = e.Name } : e)).GetAwaiter().GetResult() : args[1] == "calendar" ? Migration.Calendar(db, args[2]) : throw new ArgumentException("迁移类型无效。");
                db.Update(s => s.Preferences = s.Preferences with { Onboarded = true });
                File.AppendAllText(Path.Combine(data, "migration-report.txt"), report + Environment.NewLine);
            }
            catch (Exception ex) { Directory.CreateDirectory(data); File.AppendAllText(Path.Combine(data, "migration-report.txt"), "失败：" + ex.Message + Environment.NewLine); Environment.ExitCode = 1; }
            return;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Design.Error(e.Exception); e.Handled = true; };
        try
        {
            using var context = new AppContext(data);
            System.Windows.Media.RenderOptions.ProcessRenderMode = context.Db.Current.Preferences.HardwareAcceleration ? System.Windows.Interop.RenderMode.Default : System.Windows.Interop.RenderMode.SoftwareOnly;
            Design.Apply(context.Db.Current.Preferences.Theme);
            var dock = new DockWindow(context);
            context.Dock = dock;
            dock.Show();
            using var tray = new Tray(dock, () => context.OpenWorkspace(), context.OpenCalendar, context.Desktop.RestoreIcons, () => app.Shutdown());
            var activateWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => app.Dispatcher.BeginInvoke(() => context.OpenWorkspace()), null, Timeout.Infinite, false);
            app.Exit += (_, _) => { activateWait.Unregister(null); context.Closing = true; };
            if (!context.Db.Current.Preferences.Onboarded)
                dock.Dispatcher.BeginInvoke(() => context.OpenWorkspace(true), DispatcherPriority.ApplicationIdle);
            app.Run();
        }
        catch (Exception ex)
        {
            var answer = MessageBox.Show("无法启动 GlassBogo。现有数据已保留。\n" + ex.Message + "\n\n是否选择 JSON 备份恢复数据？原数据库会先归档。", "GlassBogo", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (answer == MessageBoxResult.Yes)
            {
                var picker = new Microsoft.Win32.OpenFileDialog { Filter = "GlassBogo 备份|*.json", InitialDirectory = Path.Combine(data, "backups") };
                if (picker.ShowDialog() == true)
                    try
                    {
                        DatabaseRecovery.Restore(data, picker.FileName);
                        MessageBox.Show("数据已恢复，请重新打开 GlassBogo。", "GlassBogo");
                    }
                    catch (Exception recovery) { MessageBox.Show(recovery.Message, "恢复失败"); }
            }
        }
    }
}
internal sealed class AppContext : IDisposable
{
    internal Database Db
    {
        get;
    }
    internal Catalog Catalog
    {
        get;
    }
    internal CalendarService Calendar
    {
        get;
    }
    internal WindowsDesktop Desktop
    {
        get;
    }
    internal Launcher Launch { get; } = new();
    internal IconCache Icons { get; } = new();
    internal Plugins Plugins
    {
        get;
    }
    internal DockWindow? Dock;
    internal bool Closing;
    private WorkspaceWindow? workspace; private CalendarWindow? calendar;
    private DispatcherTimer? memoryTimer;
    internal void ScheduleMemoryRelease()
    {
        memoryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        memoryTimer.Stop();
        memoryTimer.Tick -= ReleaseMemory;
        memoryTimer.Tick += ReleaseMemory;
        memoryTimer.Start();
    }
    private void ReleaseMemory(object? sender, EventArgs e)
    {
        if (Application.Current.Windows.Cast<Window>().Any(w => w.IsVisible && (w is not DockWindow || w.IsMouseOver)))
            return;
        memoryTimer?.Stop();
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, true, true);
        GC.WaitForPendingFinalizers();
    }
    internal AppContext(string path)
    {
        Db = new(path);
        Catalog = new(Db);
        Calendar = new(Db);
        try
        {
            Desktop = new(path);
        }
        catch { Db.Dispose(); throw; }
        Plugins = new(Db);
        SystemEvents.UserPreferenceChanged += ThemeChanged;
    }
    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e) => Application.Current.Dispatcher.BeginInvoke(() => Design.Apply(Db.Current.Preferences.Theme));
    internal void OpenWorkspace(bool discover = false)
    {
        if (workspace is null)
        {
            workspace = new(this);
            workspace.Closed += (_, _) => { workspace = null; ScheduleMemoryRelease(); };
        }
        workspace.Show();
        workspace.Activate();
        if (discover)
            workspace.DiscoverDesktop();
    }
    internal void OpenCalendar()
    {
        if (calendar is null)
        {
            calendar = new(this);
            calendar.Closed += (_, _) => { calendar = null; ScheduleMemoryRelease(); };
        }
        calendar.Show();
        calendar.Activate();
    }
    internal void LocateEntries(IEnumerable<Entry> candidates)
    {
        var identities = candidates.Select(Catalog.Identity).ToHashSet(StringComparer.Ordinal);
        var ids = Db.Current.Entries.Where(e => identities.Contains(Catalog.Identity(e))).Select(e => e.Id).ToArray();
        OpenWorkspace();
        workspace!.LocateEntries(ids);
    }
    internal void QuickTask()
    {
        var owner = (Window?)calendar ?? workspace ?? (Window?)Dock;
        if (owner is not null)
            CalendarWindow.EditTask(this, owner, null, true);
    }
    internal async Task Open(Entry entry)
    {
        var result = await Launch.LaunchAsync(entry);
        if (!result.Success)
            MessageBox.Show(result.Error, "无法打开入口", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
    public void Dispose()
    {
        Closing = true;
        memoryTimer?.Stop();
        SystemEvents.UserPreferenceChanged -= ThemeChanged;
        try
        {
            Desktop.Dispose();
        }
        catch (Exception ex) { MessageBox.Show("桌面恢复记录已保留。请运行 GlassBogo.exe --restore-desktop。\n" + ex.Message, "GlassBogo"); }
        Db.Dispose();
    }
}
