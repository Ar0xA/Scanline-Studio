using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="IImageFileLoader"/> — see spec/07-image-pipeline.md.
///
/// <b>Never cast/blit between pixel types</b>: <see cref="ScanlineStudio.Abstractions.Imaging.Rgb24"/> and
/// <see cref="SixLabors.ImageSharp.PixelFormats.Rgb24"/> are two unrelated 3-byte-RGB struct types —
/// both happening to be laid out identically today is not a contract either one promises to keep, so
/// every pixel is converted with an explicit field-by-field copy, never <c>MemoryMarshal.Cast</c> or
/// similar reinterpretation.</summary>
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
        return CopyToImageSource(image);
    }

    public async Task<IImageSource> LoadOriginalAsync(string path, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(path, ct).ConfigureAwait(false);
        image.Mutate(x => x.AutoOrient());
        return CopyToImageSource(image);
    }

    internal static ArrayImageSource CopyToImageSource(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image)
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
}
