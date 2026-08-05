using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class StockImageLibraryTests
{
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
