using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ImageFileLoaderTests
{
    [Fact]
    public async Task LoadAsync_RoundTripsPixelsAtTheOriginalSize()
    {
        // Channel-asymmetric color deliberately (not e.g. gray) -- an R/B swap bug between
        // ScanlineStudio.Abstractions.Imaging.Rgb24 and SixLabors.ImageSharp.PixelFormats.Rgb24 would fail
        // this loudly instead of silently passing.
        var path = await WriteFixturePngAsync(2, 2, new Rgb24(255, 128, 0));
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadAsync(path, targetWidth: 2, targetHeight: 2);

            Assert.Equal(2, image.Width);
            Assert.Equal(2, image.Height);
            var pixel = image.GetScanline(0)[0];
            Assert.Equal(255, pixel.R);
            Assert.Equal(128, pixel.G);
            Assert.Equal(0, pixel.B);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LoadAsync_ResizesToTheRequestedTargetDimensions()
    {
        var path = await WriteFixturePngAsync(4, 4, new Rgb24(10, 20, 30));
        try
        {
            var loader = new ImageFileLoader();

            var image = await loader.LoadAsync(path, targetWidth: 2, targetHeight: 3);

            Assert.Equal(2, image.Width);
            Assert.Equal(3, image.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static async Task<string> WriteFixturePngAsync(int width, int height, Rgb24 fillColor)
    {
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                accessor.GetRowSpan(y).Fill(fillColor);
            }
        });
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-loader-test-{Guid.NewGuid()}.png");
        await image.SaveAsPngAsync(path);
        return path;
    }
}
