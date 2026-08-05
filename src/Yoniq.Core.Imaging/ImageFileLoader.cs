using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using Yoniq.Abstractions.Imaging;

namespace Yoniq.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="IImageFileLoader"/> — see spec/07-image-pipeline.md.
///
/// <b>Never cast/blit between pixel types</b>: <see cref="Yoniq.Abstractions.Imaging.Rgb24"/> and
/// <see cref="SixLabors.ImageSharp.PixelFormats.Rgb24"/> are two unrelated 3-byte-RGB struct types —
/// both happening to be laid out identically today is not a contract either one promises to keep, so
/// every pixel is converted with an explicit field-by-field copy, never <c>MemoryMarshal.Cast</c> or
/// similar reinterpretation.</summary>
public sealed class ImageFileLoader : IImageFileLoader
{
    public async Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(path, ct).ConfigureAwait(false);
        image.Mutate(x => x.Resize(targetWidth, targetHeight));

        var pixels = new Rgb24[targetWidth * targetHeight];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < targetHeight; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < targetWidth; x++)
                {
                    var source = row[x];
                    pixels[(y * targetWidth) + x] = new Rgb24(source.R, source.G, source.B);
                }
            }
        });

        return new ArrayImageSource(targetWidth, targetHeight, pixels);
    }
}
