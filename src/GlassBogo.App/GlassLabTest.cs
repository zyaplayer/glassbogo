using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace GlassBogo.App;

internal static class GlassLabTest
{
    [DllImport("user32.dll")] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint kind);

    internal static async Task Run(string output, Application app)
    {
        DesktopVisibilityTest.RequireIsolatedDesktop();
        var checks = new List<string>();
        var matrix = new List<object>();
        Window? owner = null;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            owner = new Window { Title = "Glass Lab isolated owner", Width = 160, Height = 90, ShowActivated = false };
            owner.Show();
            var realSystem = GlassSystemState.Read();
            var baseline = new GlassHostOptions();
            Check(realSystem.Desktop.StartsWith("GlassBogo-QA-", StringComparison.Ordinal), "Isolated desktop guard");
            Check((realSystem with { HighContrast = true }).PolicyReason(GlassBackend.DesktopAcrylic) == "SystemHighContrast", "High contrast policy");
            Check((realSystem with { HighContrast = false, TransparencySetting = false }).PolicyReason(GlassBackend.ExperimentalAccent) == "SystemTransparencyOff", "Transparency off also disables Accent");
            Check((realSystem with { HighContrast = false, TransparencySetting = null }).PolicyReason(GlassBackend.DesktopAcrylic) == "TransparencySettingUnknown", "Unknown transparency is not success");
            var allowed = realSystem with { HighContrast = false, TransparencySetting = true, RemoteSession = false, BatterySaver = false, DwmComposition = true, ClientAreaAnimation = true };
            Check((allowed with { RemoteSession = true }).PolicyReason(GlassBackend.DesktopAcrylic) == "RemoteSession", "Remote policy");
            Check((allowed with { BatterySaver = true }).PolicyReason(GlassBackend.ExperimentalAccent) == "BatterySaver", "Power policy");
            Check((allowed with { DwmComposition = false }).PolicyReason(GlassBackend.DesktopAcrylic) == "DwmCompositionUnavailable", "DWM unavailable policy");
            Check(allowed.PolicyReason(GlassBackend.Opaque) == "ExplicitOpaqueControl", "Opaque is explicit fallback");

            // Cross product of the important host composition variables; no pixel-success assertion.
            foreach (var mode in new[] { RenderMode.SoftwareOnly, RenderMode.Default })
            foreach (var backend in new[] { GlassBackend.DesktopAcrylic, GlassBackend.ExperimentalAccent })
            foreach (var layered in new[] { false, true })
            foreach (var chrome in layered ? new[] { false } : new[] { false, true })
            foreach (var extend in new[] { false, true })
            {
                RenderOptions.ProcessRenderMode = mode;
                var options = baseline with { Backend = backend, Layered = layered, Chrome = chrome, ExtendFrame = extend };
                var window = new GlassLabWindow("QA material host", options, true) { Owner = owner };
                window.Show();
                await Task.Delay(35);
                var hwnd = window.Handle;
                Check(((GlassNative.GetWindowLongPtr(hwnd, -20).ToInt64() & 0x80000) != 0) == layered, "Actual WS_EX_LAYERED matches requested host");
                Check(GlassNative.GetWindow(hwnd, 4) == new WindowInteropHelper(owner).Handle, "Owner HWND retained");
                Check(window.Opacity == 1, "Whole HWND opacity is never animated");
                Check(window.Result.VisualStatus == "UnverifiedRequiresHumanDesktopObservation", "API result is not visual approval");
                var material = window.Result;
                window.SetTint(.10, .20);
                Check(window.Handle == hwnd && ReferenceEquals(material, window.Result), "Tint adjustment reuses HWND and native request");
                matrix.Add(window.Describe());
                window.Close();
                Check(!IsWindow(hwnd), "Disposed host HWND is destroyed");
            }
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var sample = new GlassLabWindow("QA prototype", baseline, true) { Owner = owner };
            sample.Show();
            await Task.Delay(100);
            var highlight = (GlassHighlightLayer)sample.Surface.Children[1];
            highlight.Configure(true, true, .18);
            highlight.Track(new(380, 210), new(432, 320));
            highlight.ReturnToRest();
            Check(highlight.IsAnimating, "Pointer exit starts finite return animation");
            await Task.Delay(240);
            Check(!highlight.IsAnimating, "Pointer exit releases animation clocks");
            highlight.Track(new(380, 210), new(432, 320));
            highlight.ReturnToRest();
            highlight.Track(new(40, 20), new(432, 320));
            Check(!highlight.IsAnimating, "New pointer position interrupts previous return immediately");
            highlight.ReturnToRest(); sample.Hide();
            Check(!highlight.IsAnimating, "Hidden surface stops clocks");
            sample.Show();
            highlight.Configure(true, false, .18);
            highlight.Track(new(100, 50), new(432, 320)); highlight.ReturnToRest();
            Check(!highlight.IsAnimating, "Reduced motion performs no animation");
            foreach (var policy in new[] { allowed with { TransparencySetting = false }, allowed with { HighContrast = true } })
            {
                sample.RefreshMaterial(policy);
                Check(sample.Result.Applied == GlassBackend.Opaque && !sample.Result.ApiAccepted, "Accessibility policy creates opaque fallback");
                Check(HwndSource.FromHwnd(sample.Handle).CompositionTarget.BackgroundColor.A == 255, "Fallback composition target is opaque");
                Check(highlight.Visibility == Visibility.Collapsed, "Fallback hides specular layer");
            }
            sample.RefreshMaterial();
            await Task.Delay(120);
            SaveLayout(sample, Path.Combine(output, "wpf-layout-only-not-dwm.png"));
            matrix.Add(sample.Describe()); sample.Close();

            foreach (var active in new[] { false, true })
            foreach (var owned in new[] { false, true })
            foreach (var topmost in new[] { false, true })
            {
                var options = baseline with { ActivateOnShow = active, Owned = owned, Topmost = topmost, TransparentTarget = false };
                var window = new GlassLabWindow("QA activation/ownership", options, false);
                if (owned) window.Owner = owner;
                window.Show();
                await Task.Delay(30);
                Check(GlassNative.GetWindow(window.Handle, 4) == (owned ? new WindowInteropHelper(owner).Handle : 0), "Owner/no owner matrix");
                Check(((GlassNative.GetWindowLongPtr(window.Handle, -20).ToInt64() & 8) != 0) == topmost, "Actual topmost style");
                Check(HwndSource.FromHwnd(window.Handle).CompositionTarget.BackgroundColor.A == 255, "Opaque composition-target control");
                matrix.Add(window.Describe()); window.Close();
            }
            nint nativeHandle;
            using (var native = new GlassLabNativeWindow(baseline, new WindowInteropHelper(owner).Handle, 20, 20))
            {
                nativeHandle = native.Handle;
                Check(IsWindow(nativeHandle), "Independent Win32 baseline exists");
                matrix.Add(native.Describe());
                native.Refresh(); Check(native.Handle == nativeHandle, "Native refresh reuses HWND");
            }
            Check(!IsWindow(nativeHandle), "Native baseline disposes HWND");
            Check(Application.Current.Windows.Count == 1, "No sample windows leaked after matrix");
            owner.Close(); owner = null;
            var controller = new GlassLabController(Path.Combine(output, "controller"), ownsApplicationLifetime: false);
            Directory.CreateDirectory(Path.Combine(output, "controller"));
            controller.Show();
            await Task.Delay(300);
            Check(Application.Current.Windows.OfType<GlassLabWindow>().Count() == 3, "Controller opens three independent WPF samples");
            Check(Application.Current.Windows.OfType<GlassLabWindow>().Single(w => w.Options.Backend == GlassBackend.Opaque).Result.FallbackReason == "ExplicitOpaqueControl", "C is an explicit opaque control");
            SaveLayout(controller, Path.Combine(output, "controller-layout-only-not-dwm.png"));
            var layerSwitch = Walk(controller).OfType<CheckBox>().Single(c => c.Content as string == "分层窗口 AllowsTransparency（对照用）");
            layerSwitch.IsChecked = true;
            var chromeSwitch = Walk(controller).OfType<CheckBox>().Single(c => c.Content as string == "WindowChrome");
            Check(chromeSwitch.IsChecked == false && !chromeSwitch.IsEnabled, "Controller excludes WindowChrome on layered hosts");
            Walk(controller).OfType<Button>().Single(b => b.Content as string == "应用配置 / 重建对照窗口").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(120);
            Check(Application.Current.Windows.OfType<GlassLabWindow>().All(w => w.AllowsTransparency), "Controller applies layered comparison only on explicit selection");
            Walk(controller).OfType<Button>().Single(b => b.Content as string == "兼容性并排对照").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(120);
            var compared = Application.Current.Windows.OfType<GlassLabWindow>().OrderBy(w => w.Title).ToArray();
            Check(compared.Length == 4, "Compatibility recipes are displayed simultaneously");
            Check(compared[0].Options.Backend == GlassBackend.DesktopAcrylic && !compared[0].AllowsTransparency, "Official recipe stays non-layered");
            Check(compared[1].Options.Backend == GlassBackend.ExperimentalAccent && !compared[1].AllowsTransparency, "Accent non-layered recipe");
            Check(compared[2].Options.Backend == GlassBackend.ExperimentalAccent && compared[2].AllowsTransparency && !compared[2].Options.Chrome && !compared[2].Options.ExtendFrame, "Accent layered recipe excludes WindowChrome and frame extension");
            Check(compared[3].Result.Applied == GlassBackend.Opaque, "Opaque control remains independent");
            Check(compared.Select(w => w.Options.TintAlpha).Distinct().Count() == 1, "All compatibility recipes share WPF tint strength");
            var cHandle = compared[2].Handle;
            var blurToggle = Walk(compared[2]).OfType<Button>().Single(b => b.Content as string == "仅透明 / 恢复模糊");
            blurToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(compared[2].Options.Backend == GlassBackend.TransparentControl && compared[2].Handle == cHandle, "Direct transparency control uses the identical layered HWND");
            Check(compared[2].Result.Calls.Any(c => c.Name == "ExperimentalAccentDisable"), "Direct transparency explicitly disables Accent");
            Check(compared[2].Result.Applied != GlassBackend.ExperimentalAccent, "Direct transparency is never labeled Acrylic");
            compared[2].RefreshMaterial(allowed with { TransparencySetting = false });
            Check(compared[2].Result.Applied == GlassBackend.Opaque, "Direct transparency also respects system transparency off");
            blurToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(compared[2].Options.Backend == GlassBackend.ExperimentalAccent && compared[2].Handle == cHandle, "Restore Accent without recreating the sample window");
            Walk(controller).OfType<Button>().Single(b => b.Content as string == "颜色 / 条纹参照板").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Application.Current.Windows.Cast<Window>().Count(w => w.Content is GlassLabReferencePattern) == 1, "Diagnostic pattern is a separate application window");
            Walk(controller).OfType<Button>().Single(b => b.Content as string == "颜色 / 条纹参照板").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Application.Current.Windows.Cast<Window>().Count(w => w.Content is GlassLabReferencePattern) == 1, "Diagnostic pattern window is reused");
            controller.Close();
            await controller.FlushDiagnostics();
            Check(Application.Current.Windows.Count == 0, "Controller closes all owned and independent samples");
            var process = Process.GetCurrentProcess();
            var cpu = process.TotalProcessorTime;
            var wait = Stopwatch.StartNew();
            await Task.Delay(2000);
            process.Refresh();
            var report = new
            {
                status = "PASS", stage = "P0MaterialPrototype", assemblySha256 = GlassLab.AssemblyHash,
                system = realSystem, checks, matrix,
                visualGate = "NOT_PASSED_NO_REAL_DESKTOP_EVIDENCE", pixelClaims = false,
                privateMiB = process.PrivateMemorySize64 / 1048576d,
                idleObservationSeconds = wait.Elapsed.TotalSeconds,
                idleCpuPercentOfMachine = (process.TotalProcessorTime - cpu).TotalMilliseconds / wait.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100,
                userObjects = GetGuiResources(process.Handle, 1), gdiObjects = GetGuiResources(process.Handle, 0),
                performanceNote = "Prototype smoke measurements only. Not the 500-entry / 10000-task / 30-minute product performance gate."
            };
            await File.WriteAllTextAsync(Path.Combine(output, "glass-lab-report.json"), JsonSerializer.Serialize(report, GlassLab.Json));
            app.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "glass-lab-failure.txt"), ex.ToString());
            owner?.Close(); app.Shutdown(1);
        }
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        yield return parent;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static void SaveLayout(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
}
