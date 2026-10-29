using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Shell;

namespace GlassBogo.App;

internal enum GlassBackend { DesktopAcrylic, ExperimentalAccent, Opaque, TransparentControl }

internal sealed record GlassHostOptions(
    GlassBackend Backend = GlassBackend.DesktopAcrylic,
    bool Chrome = true, bool ExtendFrame = true, bool Layered = false,
    bool TransparentTarget = true, bool Topmost = true, bool ActivateOnShow = false,
    bool Owned = true, bool Dark = false, double TintAlpha = .08, double HighlightAlpha = .18);

internal sealed record GlassSystemState(
    string Version, string? DisplayVersion, int? Revision, bool? TransparencySetting,
    bool HighContrast, bool ClientAreaAnimation, bool RemoteSession, bool? BatterySaver,
    bool DwmComposition, string DwmCompositionResult, string Desktop, int WpfRenderingTier,
    string ProcessRenderMode)
{
    internal static GlassSystemState Read()
    {
        string? release = null; int? revision = null; bool? transparency = null;
        try
        {
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            release = version?.GetValue("DisplayVersion") as string;
            revision = version?.GetValue("UBR") as int?;
            using var personal = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (personal?.GetValue("EnableTransparency") is int enabled) transparency = enabled != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { }
        var powerKnown = GlassNative.GetSystemPowerStatus(out var power);
        var hr = GlassNative.DwmIsCompositionEnabled(out var composition);
        return new(Environment.OSVersion.Version.ToString(), release, revision, transparency,
            SystemParameters.HighContrast, SystemParameters.ClientAreaAnimation, SystemParameters.IsRemoteSession,
            powerKnown ? power.Saver == 1 : null, hr >= 0 && composition, GlassNative.Hr(hr),
            GlassNative.DesktopName(), RenderCapability.Tier >> 16, RenderOptions.ProcessRenderMode.ToString());
    }

    internal string? PolicyReason(GlassBackend backend)
    {
        if (backend == GlassBackend.Opaque) return "ExplicitOpaqueControl";
        if (HighContrast) return "SystemHighContrast";
        if (TransparencySetting != true) return TransparencySetting == false ? "SystemTransparencyOff" : "TransparencySettingUnknown";
        if (RemoteSession) return "RemoteSession";
        if (BatterySaver != false) return BatterySaver == true ? "BatterySaver" : "PowerPolicyUnknown";
        if (!DwmComposition) return "DwmCompositionUnavailable";
        if (backend == GlassBackend.DesktopAcrylic && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)) return "DesktopAcrylicRequires22621";
        return null;
    }
}

internal sealed record GlassApiCall(string Name, string Result, bool Accepted);
internal sealed record GlassBackdropResult(GlassBackend Requested, GlassBackend Applied,
    bool ApiAccepted, string? FallbackReason, GlassApiCall[] Calls)
{
    // There is deliberately no "glass visible" field derived from API return values.
    internal string VisualStatus => "UnverifiedRequiresHumanDesktopObservation";
}

internal static class GlassBackdrop
{
    internal static GlassBackdropResult Apply(nint hwnd, GlassHostOptions options, GlassSystemState system)
    {
        var calls = new List<GlassApiCall>();
        bool Dwm(string label, int attribute, int value)
        {
            var hr = GlassNative.DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int));
            calls.Add(new(label, GlassNative.Hr(hr), hr >= 0)); return hr >= 0;
        }
        bool Extend(bool enabled)
        {
            var margins = new GlassNative.Margins { Left = enabled ? -1 : 0 };
            var hr = GlassNative.DwmExtendFrameIntoClientArea(hwnd, ref margins);
            calls.Add(new("DwmExtendFrameIntoClientArea", GlassNative.Hr(hr), hr >= 0)); return hr >= 0;
        }
        Dwm("ImmersiveDarkMode", 20, options.Dark ? 1 : 0);
        Dwm("CornerPreference", 33, 2);
        Dwm("BorderColorNone", 34, unchecked((int)0xFFFFFFFE));
        var reason = system.PolicyReason(options.Backend);
        var accepted = false;
        if (reason is null)
        {
            if (options.Backend == GlassBackend.DesktopAcrylic)
            {
                // Don't call undocumented APIs on the official path.
                accepted = Dwm("SystemBackdropDesktopAcrylic", 38, 3);
                accepted = Extend(options.ExtendFrame) && accepted;
            }
            else if (options.Backend == GlassBackend.TransparentControl)
            {
                // Only the explicit layered-Accent comparison exposes this control.
                // Keep WPF alpha intact while removing all native blur on the same HWND.
                var noBackdrop = Dwm("TransparentControlSystemBackdropNone", 38, 1);
                var noExtension = Extend(false);
                var disabled = GlassNative.TryAccent(hwnd, false, options.Dark);
                calls.Add(disabled);
                accepted = noBackdrop && noExtension && disabled.Accepted;
            }
            else
            {
                Dwm("SystemBackdropNoneForAccent", 38, 1);
                var extended = Extend(options.ExtendFrame);
                var accent = GlassNative.TryAccent(hwnd, true, options.Dark);
                calls.Add(accent);
                accepted = accent.Accepted && extended;
            }
            if (!accepted) reason = "NativeRequestRejected";
        }
        if (reason is not null)
        {
            Dwm("FallbackSystemBackdropNone", 38, 1);
            if (options.Backend is GlassBackend.ExperimentalAccent or GlassBackend.TransparentControl) calls.Add(GlassNative.TryAccent(hwnd, false, options.Dark));
            Extend(false);
        }
        return new(options.Backend, accepted ? options.Backend : GlassBackend.Opaque, accepted, reason, calls.ToArray());
    }

    internal static Color Fallback(bool dark) => SystemParameters.HighContrast ? SystemColors.WindowColor
        : dark ? Color.FromRgb(29, 35, 44) : Color.FromRgb(244, 247, 251);

    internal static object DescribeWindow(nint hwnd, Window? window, GlassHostOptions options,
        GlassBackdropResult result, GlassSystemState system)
    {
        var style = GlassNative.GetWindowLongPtr(hwnd, -16).ToInt64();
        var extended = GlassNative.GetWindowLongPtr(hwnd, -20).ToInt64();
        var hr = GlassNative.DwmGetWindowAttribute(hwnd, 38, out var backdrop, sizeof(int));
        var chrome = window is null ? null : WindowChrome.GetWindowChrome(window);
        var source = window is null ? null : HwndSource.FromHwnd(hwnd);
        return new
        {
            utc = DateTime.UtcNow, hwnd = $"0x{hwnd:X}", system, options, result,
            style = $"0x{style:X8}", extendedStyle = $"0x{extended:X8}",
            layered = (extended & 0x80000) != 0, noRedirectionBitmap = (extended & 0x200000) != 0,
            owner = $"0x{GlassNative.GetWindow(hwnd, 4):X}",
            foreground = GlassNative.GetForegroundWindow() == hwnd, visible = GlassNative.IsWindowVisible(hwnd),
            dpi = GlassNative.GetDpiForWindow(hwnd),
            backdropReadback = new { result = GlassNative.Hr(hr), value = hr >= 0 ? backdrop : (int?)null, provesPixels = false },
            wpf = window is null ? null : new
            {
                window.AllowsTransparency, window.Opacity, background = window.Background?.ToString(),
                compositionTargetBackground = source?.CompositionTarget?.BackgroundColor.ToString(),
                usesPerPixelOpacity = source?.UsesPerPixelOpacity,
                window.Topmost, window.ShowActivated, window.IsActive,
                windowStyle = window.WindowStyle.ToString(), resizeMode = window.ResizeMode.ToString(),
                chrome = chrome is null ? null : new
                {
                    chrome.CaptionHeight, frame = chrome.GlassFrameThickness.ToString(),
                    resizeBorder = chrome.ResizeBorderThickness.ToString(), corners = chrome.CornerRadius.ToString()
                }
            }
        };
    }
}

// Independently authored interop; the experimental API is deliberately isolated here.
internal static class GlassNative
{
    [StructLayout(LayoutKind.Sequential)] internal struct Margins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct PowerStatus { public byte Ac, Battery, Percent, Saver; public uint Lifetime, FullLifetime; }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public int State, Flags; public uint Color; public int Animation; }
    [StructLayout(LayoutKind.Sequential)] private struct CompositionData { public int Attribute; public nint Data; public nuint Size; }
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] internal static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
    [DllImport("dwmapi.dll")] internal static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint obj, int index, System.Text.StringBuilder text, int length, out int needed);
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(nint hwnd, ref CompositionData data);

    internal static string Hr(int hr) => $"0x{hr:X8}";
    internal static string DesktopName()
    {
        var name = new System.Text.StringBuilder(256);
        return GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, name, name.Capacity * 2, out _) ? name.ToString() : "Unknown";
    }
    internal static GlassApiCall TryAccent(nint hwnd, bool enable, bool dark)
    {
        // ABGR, very low but nonzero native tint. Distinct from the optional WPF tint.
        // This is a compatibility experiment, not a documented Desktop Acrylic contract.
        var policy = new AccentPolicy { State = enable ? 4 : 0, Flags = 2,
            Color = dark ? 0x18231A10u : 0x10FFFFFFu };
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>());
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new CompositionData { Attribute = 19, Data = buffer, Size = (nuint)Marshal.SizeOf<AccentPolicy>() };
            var value = SetWindowCompositionAttribute(hwnd, ref data);
            return new(enable ? "ExperimentalAccentEnable" : "ExperimentalAccentDisable", $"BOOL={value}; not HRESULT", value != 0);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        { return new("ExperimentalAccentUnavailable", ex.GetType().Name, false); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
