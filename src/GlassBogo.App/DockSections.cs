namespace GlassBogo.App;

internal sealed record DockSections(double Width, double PinnedWidth, double CategoryWidth, int PinnedVisible, int CategoriesVisible)
{
    // Dimensions are DIPs; physical screen conversion remains in SurfacePlacement.
    internal static DockSections Measure(double workWidth, double iconSize, int pinned, int categories)
    {
        if (!double.IsFinite(workWidth) || workWidth <= 0 || !double.IsFinite(iconSize) || iconSize <= 0 || pinned < 0 || categories < 0)
            throw new System.ArgumentOutOfRangeException(nameof(workWidth));
        var pinTile = iconSize + 20;
        var categoryTile = System.Math.Max(64, iconSize + 32);
        var functional = System.Math.Min(144, workWidth * .45);
        var max = System.Math.Max(1, workWidth - System.Math.Min(24, workWidth * .05));
        var width = System.Math.Min(max, functional + pinned * pinTile + categories * categoryTile);
        var room = System.Math.Max(0, width - functional);
        var p = System.Math.Min(pinned * pinTile, room * .5);
        var c = System.Math.Min(categories * categoryTile, room - p);
        p = System.Math.Min(pinned * pinTile, room - c);
        c = System.Math.Min(categories * categoryTile, room - p);
        static int Visible(double budget, double tile, int count)
        {
            var slots = System.Math.Max(0, (int)System.Math.Floor((budget + .001) / tile));
            return slots >= count ? count : System.Math.Max(0, slots - 1);
        }
        return new(width, p, c, Visible(p, pinTile, pinned), Visible(c, categoryTile, categories));
    }
}
