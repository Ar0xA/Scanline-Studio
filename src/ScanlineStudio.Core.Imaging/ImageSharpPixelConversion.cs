using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>T1-15 (production_audit.md): the shared field-by-field pixel-copy loop between
/// <see cref="ScanlineStudio.Abstractions.Imaging.Rgb24"/> and
/// <see cref="SixLabors.ImageSharp.PixelFormats.Rgb24"/> — see <see cref="ImageFileLoader"/>'s own
/// doc comment for why this is never a <c>MemoryMarshal.Cast</c>/reinterpretation, even though the
/// two 3-byte-RGB struct types happen to be laid out identically today. Extracted from 4
/// previously hand-duplicated copies (<see cref="ImageFileLoader"/>, <see cref="StockImageLibrary"/>,
/// <see cref="ReceivedImageBuffer"/>, <see cref="ImageSourceWriter"/>) — this duplication already
/// caused a real bug once (<see cref="StockImageLibrary"/>'s own missing-<c>AutoOrient</c> gap,
/// see that class's own doc comment). Callers remain responsible for calling <c>AutoOrient()</c>
/// and any resize/mutate themselves, BEFORE <see cref="ToImageSource"/> — this is purely the pixel
/// copy, since exactly where <c>AutoOrient</c> must run relative to other mutations (e.g. a fit
/// computation that needs post-orient dimensions) varies per caller.
/// <para><see cref="Core.Logbook"/>'s own two near-identical sites
/// (<c>SqliteReceiveHistoryStore.LoadThumbnailAsync</c>, <c>ReceiveHistoryRecorder.SaveSnapshotAsync</c>)
/// deliberately do NOT share this class — <c>Core.Logbook</c> has no project reference to
/// <c>Core.Imaging</c> (see the architecture diagram in spec/01-architecture.md: they're peer
/// modules, not layered), and adding one purely to dedupe a ~15-line loop would be a bigger,
/// separate architectural decision. Those two sites keep their own inline loops.</para></summary>
internal static class ImageSharpPixelConversion
{
    // Round-1 Tier B finding (StockImageLibrary): reads the image's own post-mutation Width/Height
    // rather than trusting a caller-passed target size -- ImageSharp's Resize treats a 0 dimension
    // as "auto-compute from aspect," so a caller-supplied size can silently diverge from the
    // image's real post-resize dimensions.
    public static ArrayImageSource ToImageSource(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = new Rgb24[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var source = row[x];
                    pixels[(y * width) + x] = new Rgb24(source.R, source.G, source.B);
                }
            }
        });

        return new ArrayImageSource(width, height, pixels);
    }

    public static Image<SixLabors.ImageSharp.PixelFormats.Rgb24> FromImageSource(IImageSource source)
    {
        var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(source.Width, source.Height);
        try
        {
            image.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < source.Height; y++)
                {
                    var sourceRow = source.GetScanline(y);
                    var destinationRow = accessor.GetRowSpan(y);
                    for (var x = 0; x < source.Width; x++)
                    {
                        var pixel = sourceRow[x];
                        destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(pixel.R, pixel.G, pixel.B);
                    }
                }
            });
        }
        catch
        {
            image.Dispose();
            throw;
        }

        return image;
    }
}
