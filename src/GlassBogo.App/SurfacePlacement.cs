using System;

namespace GlassBogo.App;

internal record PixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

// Native work areas and the results are physical pixels. Convert DIP exactly once.
internal static class SurfacePlacement
{
    private static int Pixels(double dip, double scale) => Math.Max(1, (int)Math.Round(dip * scale));

    internal static PixelRect Dock(PixelRect work, double scale, int categories, bool collapsed)
    {
        var width = Math.Min((int)(work.Width * .7), Pixels(Math.Max(440, categories * 88 + 280), scale));
        var height = Math.Min(work.Height, Pixels(collapsed ? 20 : 90, scale));
        var left = work.Left + (work.Width - width) / 2;
        var top = Math.Max(work.Top, work.Bottom - height - Pixels(12, scale));
        return new(left, top, left + width, top + height);
    }

    internal static PixelRect Popup(PixelRect work, double scale, PixelRect dock)
    {
        var width = Math.Min(work.Width, Pixels(472, scale));
        var height = Math.Min(work.Height, Pixels(396, scale));
        var left = Math.Clamp(dock.Left + (dock.Width - width) / 2, work.Left, work.Right - width);
        var top = Math.Clamp(dock.Top - height - Pixels(8, scale), work.Top, work.Bottom - height);
        return new(left, top, left + width, top + height);
    }
}
