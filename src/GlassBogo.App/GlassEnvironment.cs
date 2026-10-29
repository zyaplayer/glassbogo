using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace GlassBogo.App;

internal static class GlassEnvironment
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte Ac, Battery, Percent, Saver; public uint Lifetime, FullLifetime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus status);
    internal static bool Transparency { get; private set; }
    internal static bool Animate { get; private set; }
    internal static bool HighContrast { get; private set; }
    private static (bool Contrast, bool Transparency, bool Animate)? qaPolicy;
    internal static IDisposable QaOverride(bool contrast, bool transparency, bool animate)
    {
        DesktopVisibilityTest.RequireIsolatedDesktop();
        var previous = qaPolicy;
        qaPolicy = (contrast, transparency && !contrast, animate && transparency && !contrast);
        return new PolicyLease(() => qaPolicy = previous);
    }
    private sealed class PolicyLease(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
    internal static void Refresh()
    {
        if (qaPolicy is { } policy)
        {
            HighContrast = policy.Contrast;
            Transparency = policy.Transparency;
            Animate = policy.Animate;
            return;
        }
        HighContrast = SystemParameters.HighContrast;
        var enabled = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            enabled = (int)(key?.GetValue("EnableTransparency") ?? 1) != 0;
        }
        catch (System.Security.SecurityException) { enabled = false; }
        var saving = GetSystemPowerStatus(out var power) && power.Saver == 1;
        Transparency = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && enabled &&
            !HighContrast && !SystemParameters.IsRemoteSession && !saving;
        Animate = Transparency && SystemParameters.ClientAreaAnimation;
    }
}
