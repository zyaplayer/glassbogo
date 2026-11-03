namespace GlassBogo.App;

// A complete presentation contract; matching size alone cannot validate a frame
// from an earlier theme, output, DPI, capture lifetime or window revision.
internal readonly record struct GlassFrameStamp(long Generation, long Revision, string Display, PixelRect Bounds, double Scale, bool Dark, string Quality);
