namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>The exact normalized-region-to-source-pixel-rect rounding <see cref="ITransmitImagePreparer.Crop"/>
/// applies, lifted out of its implementation (TX workflow modernization plan, Phase 7) so a caller
/// that needs to know WHERE in the source a crop landed (the TX editor's flatten command, which
/// must composite baked pixels back at that exact offset) shares ONE implementation with the
/// cropper itself instead of a second, driftable copy of the same rounding. A 1px disagreement
/// here is a 1px drift of every flattened element's position.</summary>
public static class CropGeometry
{
    /// <summary>Byte-for-byte the arithmetic <c>TransmitImagePreparer.CropInto</c> uses --
    /// including the deliberate asymmetry that X/Y clamp against <c>size - 1</c> while Width/Height
    /// clamp against the remaining extent with a floor of 1. Not "cleaned up" while moving: the
    /// whole point is that this stays identical to what the real crop does.</summary>
    public static (int X, int Y, int Width, int Height) Measure(int sourceWidth, int sourceHeight, NormalizedRect region)
    {
        var x = Math.Clamp((int)Math.Round(region.X * sourceWidth), 0, sourceWidth - 1);
        var y = Math.Clamp((int)Math.Round(region.Y * sourceHeight), 0, sourceHeight - 1);
        var width = Math.Clamp((int)Math.Round(region.Width * sourceWidth), 1, sourceWidth - x);
        var height = Math.Clamp((int)Math.Round(region.Height * sourceHeight), 1, sourceHeight - y);
        return (x, y, width, height);
    }
}
