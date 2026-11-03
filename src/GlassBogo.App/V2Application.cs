using System.Windows.Interop;

namespace GlassBogo.App;

internal static class V2Application
{
    internal static void Run()
    {
        var data = Environment.GetEnvironmentVariable("GLASSBOGO_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo");
        using var mutex = new Mutex(true, @"Local\GlassBogo-" + Environment.UserName, out var created);
        var activationName = @"Local\GlassBogo-Open-" + Environment.UserName;
        if (!created)
        {
            try { using var existing = EventWaitHandle.OpenExisting(activationName); existing.Set(); }
            catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("GlassBogo 正在启动，请稍后从托盘打开。", "GlassBogo"); }
            return;
        }
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, activationName);
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        V2Context? context = null; Tray? tray = null; RegisteredWaitHandle? activateWait = null; var stopping = false;
        async Task Stop()
        {
            if (stopping) return; stopping = true;
            activateWait?.Unregister(null);
            try { if (context is not null) await context.DisposeAsync(); }
            catch (Exception ex) { MessageBox.Show("退出时未能恢复桌面图标，恢复记录已保留。可重新启动后关闭隐藏开关，或使用 --restore-desktop 恢复。\n\n" + ex.Message, "GlassBogo"); }
            finally { tray?.Dispose(); app.Shutdown(); }
        }
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show(e.Exception.Message, "GlassBogo", MessageBoxButton.OK, MessageBoxImage.Warning); e.Handled = true; };
        app.Startup += async (_, _) =>
        {
            try
            {
                var db = await Task.Run(() => new Database(data, upgradeToV2: true));
                context = new(db); var dock = new Dock2Window(context); app.MainWindow = dock; dock.Show();
                try { await context.InitializeDesktopIcons(); }
                catch (Exception ex) { MessageBox.Show("桌面显示状态暂未恢复，原恢复记录已保留。可在设置中重试。\n\n" + ex.Message, "GlassBogo"); }
                tray = new Tray(dock, [("管理入口和设置", context.OpenManager), ("搜索", context.ToggleSearch), ("恢复桌面图标", async () => { try { await context.SetDesktopIconsHidden(false); } catch (Exception ex) { Design.Error(ex); } }), ("退出", async () => await Stop())]);
                activateWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => app.Dispatcher.BeginInvoke(() => { if (!stopping) context.OpenManager(); }), null, Timeout.Infinite, false);
                if (!db.Current.Preferences.Onboarded)
                {
                    context.OpenManager();
                    await context.Write(() => db.Update(s => s.Preferences = s.Preferences with { Onboarded = true }));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动 GlassBogo，原数据已保留。\n" + ex.Message + "\n\n如需恢复备份，可使用 --recover-data 命令。", "GlassBogo", MessageBoxButton.OK, MessageBoxImage.Error);
                await Stop();
            }
        };
        app.Run();
    }
}
