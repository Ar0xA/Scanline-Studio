using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Imaging;

/// <summary>Folder-scan-based <see cref="IStockImageLibrary"/> — see spec/07-image-pipeline.md's
/// "Stock image library" section. Mirrors <see cref="ImageFileLoader"/>'s explicit-field-copy pixel
/// pattern (never <c>MemoryMarshal.Cast</c> between the two unrelated <c>Rgb24</c> types, see that
/// class's own doc comment).</summary>
public sealed class StockImageLibrary : IStockImageLibrary
{
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private readonly ISettingsStore _settingsStore;

    public StockImageLibrary(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore;
    }

    public async Task<IReadOnlyList<StockImageEntry>> ListAsync(CancellationToken ct = default)
    {
        var directory = await ResolveDirectoryAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Select(path => new StockImageEntry(path, Path.GetFileName(path), path))
            .ToList();
    }

    public async Task<IImageSource> LoadThumbnailAsync(StockImageEntry entry, int maxDimension, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(entry.FilePath, ct).ConfigureAwait(false);
        // Round-1 Tier B finding: this class used to skip AutoOrient entirely while its sibling
        // ImageFileLoader always applied it -- the two are interchangeable branches of one caller
        // switch (TxControlsPaneViewModel's stock-vs-file-picker source selection), so a phone
        // photo from the stock folder transmitted sideways while the identical file loaded via
        // Browse did not. Must run BEFORE both the fit computation and the resize, same ordering
        // and rationale as ImageFileLoader's own doc comment -- the fit must be measured from the
        // POST-orient dimensions, not the raw on-disk ones.
        image.Mutate(x => x.AutoOrient());
        var (width, height) = FitWithinLongestSide(image.Width, image.Height, maxDimension);
        image.Mutate(x => x.Resize(width, height));
        return CopyToImageSource(image);
    }

    public async Task<IImageSource> LoadFullAsync(StockImageEntry entry, int targetWidth, int targetHeight, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(entry.FilePath, ct).ConfigureAwait(false);
        image.Mutate(x => x.AutoOrient().Resize(targetWidth, targetHeight));
        return CopyToImageSource(image);
    }

    public async Task<IImageSource> LoadOriginalAsync(StockImageEntry entry, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(entry.FilePath, ct).ConfigureAwait(false);
        image.Mutate(x => x.AutoOrient());
        return CopyToImageSource(image);
    }

    private async Task<string> ResolveDirectoryAsync(CancellationToken ct)
    {
        var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = settings.GetSection(ImageLibrarySettings.SectionKey, ImageLibrarySettingsJsonContext.Default.ImageLibrarySettings);
        if (!string.IsNullOrWhiteSpace(section?.StockDirectory))
        {
            return section.StockDirectory;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "Stock");
    }

    private static (int Width, int Height) FitWithinLongestSide(int width, int height, int maxDimension)
    {
        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height);
        }

        var scale = width >= height ? (double)maxDimension / width : (double)maxDimension / height;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    // Reads the image's own post-mutation Width/Height rather than trusting a caller-passed target
    // size (round-1 Tier B finding, matches ImageFileLoader.CopyToImageSource's own shape) --
    // ImageSharp's Resize treats a 0 dimension as "auto-compute from aspect," so a caller-supplied
    // size can silently diverge from the image's real post-resize dimensions.
    private static ArrayImageSource CopyToImageSource(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image)
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
