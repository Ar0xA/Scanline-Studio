using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="IImageFileLoader"/> — see spec/07-image-pipeline.md. Pixel
/// conversion (see <see cref="ImageSharpPixelConversion"/> for why it's never a
/// <c>MemoryMarshal.Cast</c> between the two unrelated <c>Rgb24</c> types) is shared with
/// <see cref="StockImageLibrary"/>/<see cref="ReceivedImageBuffer"/>/<see cref="ImageSourceWriter"/>
/// (T1-15, production_audit.md) — this class stays responsible only for <c>AutoOrient</c>/resize
/// ordering, which varies per caller.</summary>
public sealed class ImageFileLoader : IImageFileLoader
{
    public async Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(path, ct).ConfigureAwait(false);
        // spec/18-path-to-1.0.md High item 3: a phone photo with EXIF orientation used to load
        // sideways with no in-app remedy. AutoOrient reads the EXIF Orientation tag, physically
        // rotates/flips the pixel data to match, and resets the tag to Normal -- must run BEFORE
        // Resize, or the target width/height below gets applied to the still-sideways raw pixel
        // dimensions.
        image.Mutate(x => x.AutoOrient().Resize(targetWidth, targetHeight));
        return ImageSharpPixelConversion.ToImageSource(image);
    }

    public async Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(path, ct).ConfigureAwait(false);
        image.Mutate(x => x.AutoOrient());
        return ImageSharpPixelConversion.ToImageSource(image);
    }
}
