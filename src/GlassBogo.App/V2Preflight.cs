using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GlassBogo.App;

internal static class V2Preflight
{
    internal static void Run(string[] args)
    {
        var qa = args[0] == "--v2-preflight-qa";
        if (qa) DesktopVisibilityTest.RequireIsolatedDesktop();
        var output = Path.GetFullPath(args.Length > 1 ? args[1] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GlassBogo-Lab", "v2-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        var assemblyHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(V2Preflight).Assembly.Location)));
        RenderOptions.ProcessRenderMode = RenderMode.Default;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var checks = new List<object>();
        var journal = new PreflightReportWriter(Path.Combine(output, "v2-preflight.json"));
        void Record(string name, string status, object? detail = null)
        {
            if (checks.Count >= 512) checks.RemoveAt(0);
            checks.Add(new { name, status, detail });
            journal.Save(ReportJson(checks, "CLOSED", qa ? "isolated" : "manual", assemblyHash));
        }
        app.DispatcherUnhandledException += async (_, e) => { e.Handled = true; Record("UnhandledException", "FAIL", e.Exception.ToString()); try { await journal.FlushAsync(); } finally { app.Shutdown(1); } };
        if (qa)
        {
            app.Startup += async (_, _) =>
            {
                try { await Automated(Record); Record("AutomatedChecks", "PASS"); await journal.FlushAsync(); app.Shutdown(0); }
                catch (Exception ex) { Record("AutomatedChecks", "FAIL", ex.ToString()); try { await journal.FlushAsync(); } finally { app.Shutdown(2); } }
            };
            app.Run();
        }
        else
        {
            var controller = new Window { Title = "GlassBogo 2.0 · 前置验证控制面板", Width = 680, Height = 590, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            var panel = new StackPanel { Margin = new(24) };
            var text = new TextBlock { Text = "独立实验，不读取或修改应用数据库。\n先检查采样排除，再测试 Win+D 与实际玻璃。", TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new(0, 0, 0, 16) };
            panel.Children.Add(text);
            var pattern = new Window { Title = "GlassBogo 2.0 · 参照背景", Width = 720, Height = 450, Background = new SolidColorBrush(Color.FromRgb(20, 200, 80)), WindowStartupLocation = WindowStartupLocation.CenterScreen };
            pattern.Content = new GlassLabReferencePattern();
            // Keep a known solid patch exactly behind the magenta marker.
            var patternGrid = new Grid();
            patternGrid.Children.Add(new GlassLabReferencePattern());
            patternGrid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(20, 200, 80)), Width = 160, Height = 160,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new(100, 60, 0, 0) });
            pattern.Content = patternGrid;
            var glass = new V2ProbeWindow(s => text.Text = s, () => { controller.Show(); controller.Activate(); });
            var running = false; var exiting = false;
            async Task Open()
            {
                if (running) return;
                running = true;
                try
                {
                    pattern.Show(); pattern.UpdateLayout();
                    glass.Left = pattern.Left + 80; glass.Top = pattern.Top + 40;
                    glass.Show(); glass.UpdateLayout();
                    await glass.Initialize();
                    Native.SetWindowPos(glass.Handle, 0, 0, 0, 0, 0, 0x13);
                    Record("LiveRenderer", "PASS", "Initialized; visual quality is not yet approved");
                    text.Text = "样本已打开。先运行采样排除检查，再按 Win+D 检查玻璃窗口是否仍可见。\n强视觉需对照不同壁纸和动态应用背景观察。";
                }
                catch (Exception ex) { Record("LiveRenderer", "FAIL", ex.ToString()); text.Text = "前置验证失败，禁止接入产品：\n" + ex.Message; }
            }
            void Button(string label, Func<Task> action)
            {
                var b = new Button { Content = label, Margin = new(0, 5, 0, 5), Padding = new(12, 8, 12, 8) };
                b.Click += async (_, _) => { b.IsEnabled = false; try { await action(); } catch (Exception ex) { Record(label, "FAIL", ex.ToString()); text.Text = ex.Message; } finally { b.IsEnabled = true; } };
                panel.Children.Add(b);
            }
            Button("1. 打开真实玻璃样本", Open);
            Button("2. 检查自身采样排除（实际像素）", async () =>
            {
                await Open();
                if (!glass.Ready) throw new InvalidOperationException("实时渲染尚未初始化。");
                glass.SuspendLayer(true);
                pattern.Show(); pattern.Activate();
                Native.SetWindowPos(glass.Handle, 0, 0, 0, 0, 0, 0x13);
                try
                {
                    if (!glass.SetExclusion(false)) throw new InvalidOperationException("无法设置采样对照状态。");
                    await Task.Delay(250);
                    var visible = await glass.SampleMarker();
                    if (!glass.SetExclusion(true)) throw new InvalidOperationException("无法排除实验窗口。");
                    await Task.Delay(250);
                    var excluded = await glass.SampleMarker();
                    var markerOk = Math.Abs(visible.R - 240) < 20 && visible.G < 20 && Math.Abs(visible.B - 200) < 20;
                    var backgroundOk = Math.Abs(excluded.R - 20) < 20 && Math.Abs(excluded.G - 200) < 20 && Math.Abs(excluded.B - 80) < 20;
                    Record("CaptureExclusionPixels", markerOk && backgroundOk ? "PASS" : "FAIL", new { visible = new { visible.R, visible.G, visible.B }, excluded = new { excluded.R, excluded.G, excluded.B } });
                    text.Text = markerOk && backgroundOk ? "采样排除像素通过：标记消失，背后参照色正确。\n还需人工检查 Win+D、普通窗口覆盖和强视觉。" : "采样排除像素未通过。禁止产品接入；结果已保存。";
                }
                finally { glass.SetExclusion(true); glass.SuspendLayer(false); }
            });
            Button("切换强视觉 / 降低效果", () => { glass.Reduced = !glass.Reduced; glass.InvalidateGlass(); text.Text = glass.Reduced ? "当前：降低效果（不计为强视觉通过）" : "当前：强视觉实验"; return Task.CompletedTask; });
            Button("切换浅色 / 深色材质", () => { glass.Dark = !glass.Dark; glass.InvalidateGlass(); return Task.CompletedTask; });
            Button("切换桌面层 / 临时浮层样本", () =>
            {
                var floating = !glass.Topmost;
                glass.SuspendLayer(floating);
                glass.Topmost = floating;
                text.Text = floating ? "当前为临时浮层实验，可在其他应用上方检查实时背景。测试 Win+D 前请切回桌面层。" : "当前为非置顶桌面层样本。";
                return Task.CompletedTask;
            });
            Button("记录人工观察结果", () =>
            {
                Record("ManualObservation", "PENDING", new { desktopRestores = glass.DesktopRestores, renderedFrames = glass.RenderedFrames, failure = glass.Failure });
                text.Text = "诊断已保存：\n" + output + "\n\n请分别反馈 Win+D 后是否可见、普通窗口能否覆盖，以及背景参与/折射/可读性。此按钮不自动宣布验收通过。";
                return Task.CompletedTask;
            });
            controller.Content = panel;
            controller.Closing += async (_, e) =>
            {
                if (exiting) return;
                e.Cancel = true; exiting = true;
                glass.Close(); pattern.Close(); await glass.FullyClosed;
                Record("Closed", "PASS", new { glass.RenderedFrames, glass.DesktopRestores });
                await journal.FlushAsync();
                app.Shutdown();
            };
            Record("ManualValidation", "PENDING");
            app.Run(controller);
        }
    }

    private static async Task Automated(Action<string, string, object?> record)
    {
        var foreground = Native.GetForegroundWindow();
        var host = new Window { Title = "V2 isolated desktop-layer test", Width = 320, Height = 200,
            ShowActivated = false, ShowInTaskbar = false, Topmost = false, WindowStyle = WindowStyle.None, Background = Brushes.Green };
        host.Show(); host.UpdateLayout();
        var hwnd = new WindowInteropHelper(host).Handle;
        using var layer = new DesktopLayer(host);
        try
        {
            if ((GlassNative.GetWindowLongPtr(hwnd, -20).ToInt64() & 8) != 0) throw new InvalidOperationException("实验窗口不应置顶。");
            if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("显示实验窗口抢占了前台焦点。");
            record("NonTopmostNoActivate", "PASS", null);
            Native.ShowWindow(hwnd, 6);
            layer.RestoreOnDesktop(hwnd);
            await Task.Delay(80);
            if (DesktopLayer.IsIconic(hwnd) || !Native.IsWindowVisible(hwnd)) throw new InvalidOperationException("不激活恢复窗口失败。");
            if (Native.GetForegroundWindow() != foreground) throw new InvalidOperationException("恢复窗口抢占了前台焦点。");
            record("SimulatedDesktopRestore", "PASS", "Direct restore exercise; real Explorer Win+D still requires manual validation");
            var excluded = SetWindowDisplayAffinity(hwnd, 0x11);
            record("CaptureExclusionApi", excluded ? "PASS" : "FAIL", new { win32Error = excluded ? 0 : Marshal.GetLastWin32Error(), pixelsVerified = false });
            if (!excluded) throw new InvalidOperationException("采样排除接口失败。");
            VerifyHotkeyRollback();
            record("HotkeyConflictAndPersistenceRollback", "PASS", null);
            using var device = await Task.Run(() => new DesktopCapture(Display.PrimaryScreen.DeviceName, duplicate: false));
            using var gpu = await Task.Run(() => new LiquidGlassGpu(device.Device, device.Context, device.AdapterLuid, hwnd, 320, 200));
            var image = new D3DImage();
            try
            {
                image.Lock();
                try { image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, gpu.Surface); }
                finally { image.Unlock(); }
                image.Lock();
                try
                {
                    var difference = await Task.Run(() =>
                    {
                        using var texture = device.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 320, 200, 1, 1, BindFlags.ShaderResource));
                        var pixels = new byte[320 * 200 * 4];
                        for (var y = 0; y < 200; y++) for (var x = 0; x < 320; x++)
                        {
                            var at = (y * 320 + x) * 4;
                            pixels[at] = (byte)((x / 24 % 2) * 200 + 30); pixels[at + 1] = (byte)((y / 24 % 2) * 150 + 40); pixels[at + 2] = 120; pixels[at + 3] = 255;
                        }
                        device.Context.UpdateSubresource<byte>(pixels, texture, 0, 320 * 4);
                        gpu.Render(device.Device, texture, new(0, 0, 1, 1), new(-1000, -1000), false, true);
                        var reduced = device.ReadPixels(gpu.Texture);
                        gpu.Render(device.Device, texture, new(0, 0, 1, 1), new(-1000, -1000), false, false);
                        var strong = device.ReadPixels(gpu.Texture);
                        if (strong[3] != 0 || strong[(100 * 320 + 160) * 4 + 3] < 250) throw new InvalidOperationException("圆角透明或中心 alpha 不正确。");
                        var delta = strong.Zip(reduced, (a, b) => Math.Abs(a - b)).Sum();
                        if (delta < 1000) throw new InvalidOperationException("强视觉着色器未产生可测量的空间变化。");
                        gpu.Render(device.Device, texture, new(0, 0, 1, 1), new(20, 20), false, false);
                        var highlighted = device.ReadPixels(gpu.Texture);
                        var highlightDelta = highlighted.Zip(strong, (a, b) => Math.Abs(a - b)).Sum();
                        if (highlightDelta < 1000) throw new InvalidOperationException("指针局部高光没有产生空间变化。");
                        return new { refractionDelta = delta, highlightDelta };
                    });
                    image.AddDirtyRect(new Int32Rect(0, 0, 320, 200));
                    record("GpuShaderAndSharedSurface", "PASS", new { adapter = device.AdapterName, difference, frontBufferAvailable = image.IsFrontBufferAvailable });
                }
                finally { image.Unlock(); }
            }
            finally { image.Lock(); try { image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, 0); } finally { image.Unlock(); } }
            record("RealDesktopAndVisualGate", "PENDING", "Inactive QA desktop does not establish real Win+D, capture exclusion pixels, or visual approval. Use explicit --v2-preflight on the active desktop.");
        }
        finally { host.Close(); }
    }

    private static void VerifyHotkeyRollback()
    {
        using var first = new GlobalSearchHotkey(() => { });
        SearchShortcut? shortcut = null;
        for (uint key = 0x87; key >= 0x7C; key--)
        {
            var candidate = new SearchShortcut(7, key); // Ctrl+Alt+Shift+F13..24, temporary and isolated
            if (first.TryChange(candidate, _ => { }, out _)) { shortcut = candidate; break; }
        }
        if (shortcut is null) throw new InvalidOperationException("隔离桌面没有可用的临时快捷键。");
        using var second = new GlobalSearchHotkey(() => { });
        var saved = false;
        if (second.TryChange(shortcut, _ => saved = true, out _) || saved)
            throw new InvalidOperationException("快捷键冲突时不应保存新配置。");
        SearchShortcut? alternative = null;
        using (var reserve = new GlobalSearchHotkey(() => { }))
        {
            for (uint key = 0x87; key >= 0x7C; key--)
            {
                if (key == shortcut.VirtualKey) continue;
                var candidate = new SearchShortcut(7, key);
                if (reserve.TryChange(candidate, _ => { }, out _)) { alternative = candidate; break; }
            }
        }
        if (alternative is null) throw new InvalidOperationException("没有可用的第二个临时快捷键。");
        if (first.TryChange(alternative, _ => throw new IOException("Injected persistence failure"), out _) || first.Current != shortcut)
            throw new InvalidOperationException("保存失败后必须保留原有效快捷键。");
        if (second.TryChange(shortcut, _ => { }, out _)) throw new InvalidOperationException("原快捷键注册被意外释放。");
        if (!second.TryChange(alternative, _ => { }, out _)) throw new InvalidOperationException("保存失败的候选快捷键未释放。");
    }

    private static string ReportJson(List<object> checks, string gate, string mode, string assemblyHash) => JsonSerializer.Serialize(new
        {
            schemaVersion = 1, stage = "GlassBogo 2.0 preflight", productionGate = gate, mode,
            assemblySha256 = assemblyHash,
            checks, limits = "No production database, desktop icon mutation or target launch. No captured pixels persisted. Production integration stays closed pending real desktop evidence."
        }, new JsonSerializerOptions { WriteIndented = true });

    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    // No Application, windows, database, mutex, tray or desktop mutation in this mode.
    internal static void CaptureCheck(string output)
    {
        Directory.CreateDirectory(output);
        var watch = Stopwatch.StartNew();
        var checks = new List<object>();
        checks.Add(new { name = "DesktopSession", status = "INFO", threadDesktop = GlassNative.DesktopName(), inputDesktop = InputDesktopName(), sessionId = Process.GetCurrentProcess().SessionId });
        var passed = false;
        try
        {
            using var capture = new DesktopCapture(Display.PrimaryScreen.DeviceName);
            checks.Add(new { name = "PrimaryOutputDuplication", status = "PASS", adapter = capture.AdapterName, luid = capture.AdapterLuid });
            for (var attempt = 0; attempt < 20; attempt++)
            {
                using var frame = capture.Acquire();
                if (frame is null) continue;
                // Prove that a real GPU texture is readable, without storing pixels or pixel hashes.
                var pixels = capture.ReadPixels(frame);
                checks.Add(new { name = "DesktopFrameReadback", status = "PASS", width = frame.Description.Width, height = frame.Description.Height, bytes = pixels.Length });
                Array.Clear(pixels);
                passed = true;
                break;
            }
            if (!passed) checks.Add(new { name = "DesktopFrameReadback", status = "FAIL", reason = "No frame within 20 bounded acquisition attempts" });
        }
        catch (Exception ex)
        {
            checks.Add(new { name = "Capture", status = "FAIL", exception = ex.GetType().FullName, stage = (ex as DesktopCaptureException)?.Stage, adapter = (ex as DesktopCaptureException)?.Adapter, hresult = $"0x{ex.HResult:X8}", reason = ex.Message });
        }
        File.WriteAllText(Path.Combine(output, "capture-check.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, stage = "GlassBogo 2.0 preflight", status = passed ? "PASS" : "FAIL",
            assemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(V2Preflight).Assembly.Location))),
            elapsedSeconds = watch.Elapsed.TotalSeconds, checks,
            limits = "No window created. Capture exclusion, Win+D and visual approval are not tested. No captured pixels saved."
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = passed ? 0 : 2;
    }

    private static string InputDesktopName()
    {
        var desktop = OpenInputDesktop(0, false, 1); // DESKTOP_READOBJECTS
        if (desktop == 0) return "Unavailable; Win32=" + Marshal.GetLastWin32Error();
        try
        {
            var name = new System.Text.StringBuilder(256);
            return GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _) ? name.ToString() : "Unknown; Win32=" + Marshal.GetLastWin32Error();
        }
        finally { CloseDesktop(desktop); }
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, System.Text.StringBuilder name, int bytes, out int needed);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
}
