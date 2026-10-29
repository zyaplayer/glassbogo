namespace GlassBogo.App;

internal sealed record GlassLabPreset(string Name, GlassHostOptions Options, bool Decorated);

internal static class GlassLabPresets
{
    internal static GlassLabPreset[] Compatibility(GlassHostOptions common)
    {
        // Match tint/theme/topmost/owner/activation. Only the named host recipe changes.
        // These are experimental recipes, not automatically selected production backends.
        var clear = common with { TransparentTarget = true };
        return
        [
            new("A · 官方 DWM / 非分层", clear with { Backend = GlassBackend.DesktopAcrylic, Layered = false, Chrome = true, ExtendFrame = true }, true),
            new("B · Accent / 非分层", clear with { Backend = GlassBackend.ExperimentalAccent, Layered = false, Chrome = false, ExtendFrame = true }, true),
            new("C · Accent / 分层", clear with { Backend = GlassBackend.ExperimentalAccent, Layered = true, Chrome = false, ExtendFrame = false }, true),
            new("D · 不透明回退", clear with { Backend = GlassBackend.Opaque, Layered = false, Chrome = true, ExtendFrame = false }, false)
        ];
    }
}

// A normal application window, painted once/on resize. No desktop sampling, timers,
// wallpaper changes or simulated refraction. Used to make blur versus opacity observable.
internal sealed class GlassLabReferencePattern : FrameworkElement
{
    internal GlassLabReferencePattern() => SizeChanged += (_, _) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        var palette = new[] { Color.FromRgb(230, 80, 100), Color.FromRgb(40, 160, 220), Color.FromRgb(40, 190, 125), Color.FromRgb(240, 185, 45) };
        var width = ActualWidth; var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        for (var i = 0; i < palette.Length; i++)
            dc.DrawRectangle(new SolidColorBrush(palette[i]), null, new Rect(width * i / palette.Length, 0, width / palette.Length + 1, height));
        var white = new SolidColorBrush(Color.FromArgb(235, 255, 255, 255));
        var dark = new SolidColorBrush(Color.FromArgb(210, 20, 30, 40));
        for (var x = 0d; x < width; x += 12)
            dc.DrawRectangle(white, null, new Rect(x, height * .22, 3, height * .28));
        for (var x = 0d; x < width; x += 64)
            dc.DrawRectangle(dark, null, new Rect(x, height * .65, 20, height * .20));
    }
}
