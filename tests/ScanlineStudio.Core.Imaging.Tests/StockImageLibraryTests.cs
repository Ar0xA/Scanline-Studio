using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class StockImageLibraryTests
{
    // Round-1 Tier B finding: this class used to skip AutoOrient entirely, unlike its sibling
    // ImageFileLoader (see ImageFileLoaderTests' own comment on the original spec/18-path-to-1.0.md
    // bug this pattern guards against) -- the two are interchangeable branches of one caller switch
    // (TxControlsPaneViewModel), so a phone photo from the stock folder transmitted sideways while
    // the identical file loaded via Browse did not. Orientation=6 on a 4-wide x 2-tall stored
    // source should come out 2-wide x 4-tall once AutoOrient runs.

    [Fact]
    public async Task LoadOriginalAsync_ExifOrientation_AutoOrientsSwappingWidthAndHeight()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "pic.jpg");
            await WriteFixtureJpegWithOrientationAsync(path, width: 4, height: 2, orientation: 6);
            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));
            var entry = (await library.ListAsync()).Single();

            var image = await library.LoadOriginalAsync(entry);

            Assert.Equal(2, image.Width);
            Assert.Equal(4, image.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadThumbnailAsync_ExifOrientation_FitsUsingPostOrientDimensions()
    {
        // The fit must be computed from the POST-orient dimensions, not the raw on-disk ones -- a
        // 4x2 source rotated to 2x4 is now TALL, so fitting within maxDimension=4 should produce a
        // 2-wide x 4-tall thumbnail, not the pre-orient 4-wide x 2-tall shape.
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "pic.jpg");
            await WriteFixtureJpegWithOrientationAsync(path, width: 4, height: 2, orientation: 6);
            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));
            var entry = (await library.ListAsync()).Single();

            var thumbnail = await library.LoadThumbnailAsync(entry, maxDimension: 4);

            Assert.Equal(2, thumbnail.Width);
            Assert.Equal(4, thumbnail.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFullAsync_ExifOrientation_StillResizesToTheRequestedTargetDimensions()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "pic.jpg");
            await WriteFixtureJpegWithOrientationAsync(path, width: 4, height: 2, orientation: 6);
            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));
            var entry = (await library.ListAsync()).Single();

            var image = await library.LoadFullAsync(entry, targetWidth: 5, targetHeight: 7);

            Assert.Equal(5, image.Width);
            Assert.Equal(7, image.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WriteFixtureJpegWithOrientationAsync(string path, int width, int height, ushort orientation)
    {
        using var image = new Image<Rgb24>(width, height);
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, orientation);
        await image.SaveAsJpegAsync(path);
    }

    [Fact]
    public async Task ListAsync_ReturnsOnlySupportedImageFilesInTheConfiguredDirectory()
    {
        var directory = CreateTempDirectory();
        try
        {
            await WriteFixturePngAsync(Path.Combine(directory, "a.png"), 2, 2, new Rgb24(1, 2, 3));
            await WriteFixturePngAsync(Path.Combine(directory, "b.jpg"), 2, 2, new Rgb24(4, 5, 6));
            File.WriteAllText(Path.Combine(directory, "notes.txt"), "not an image");

            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));

            var entries = await library.ListAsync();

            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.FileName == "a.png");
            Assert.Contains(entries, e => e.FileName == "b.jpg");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyWhenTheConfiguredDirectoryDoesNotExist()
    {
        var library = new StockImageLibrary(SettingsStoreWithDirectory(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid()}")));

        var entries = await library.ListAsync();

        Assert.Empty(entries);
    }

    [Fact]
    public async Task LoadThumbnailAsync_FitsWithinMaxDimensionPreservingAspectRatio()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "wide.png");
            await WriteFixturePngAsync(path, width: 8, height: 4, new Rgb24(255, 0, 128));
            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));
            var entry = (await library.ListAsync()).Single();

            var thumbnail = await library.LoadThumbnailAsync(entry, maxDimension: 4);

            Assert.Equal(4, thumbnail.Width);
            Assert.Equal(2, thumbnail.Height);
            var pixel = thumbnail.GetScanline(0)[0];
            Assert.Equal(255, pixel.R);
            Assert.Equal(0, pixel.G);
            Assert.Equal(128, pixel.B);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFullAsync_ResizesToExactTargetDimensions()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "pic.png");
            await WriteFixturePngAsync(path, width: 4, height: 4, new Rgb24(10, 20, 30));
            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));
            var entry = (await library.ListAsync()).Single();

            var image = await library.LoadFullAsync(entry, targetWidth: 6, targetHeight: 3);

            Assert.Equal(6, image.Width);
            Assert.Equal(3, image.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LoadOriginalAsync_PreservesTheFilesNativeResolution_NoResize()
    {
        var directory = CreateTempDirectory();
        try
        {
            var path = Path.Combine(directory, "pic.png");
            await WriteFixturePngAsync(path, width: 9, height: 4, new Rgb24(1, 2, 3));
            var library = new StockImageLibrary(SettingsStoreWithDirectory(directory));
            var entry = (await library.ListAsync()).Single();

            var image = await library.LoadOriginalAsync(entry);

            Assert.Equal(9, image.Width);
            Assert.Equal(4, image.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FakeSettingsStore SettingsStoreWithDirectory(string directory)
    {
        var settings = new AppSettings().WithSection(
            ImageLibrarySettings.SectionKey,
            new ImageLibrarySettings { StockDirectory = directory },
            ImageLibrarySettingsJsonContext.Default.ImageLibrarySettings);
        return new FakeSettingsStore { Settings = settings };
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanline-studio-stock-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WriteFixturePngAsync(string path, int width, int height, Rgb24 fillColor)
    {
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                accessor.GetRowSpan(y).Fill(fillColor);
            }
        });
        await image.SaveAsPngAsync(path);
    }
}
