using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ImageSharpRgb24 = SixLabors.ImageSharp.PixelFormats.Rgb24;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ImageSourceWriterTests
{
    [Fact]
    public async Task WritePngAsync_RoundTripsPixelsAndDimensions()
    {
        // Round-1 Tier B finding: this is the third independent hand-written Rgb24-to-Rgb24 pixel
        // copy loop in this project (ImageFileLoader/StockImageLibrary have the other two), and had
        // zero direct test coverage of channel order -- an R/B swap here would have passed the
        // entire green suite. Channel-asymmetric color deliberately (not e.g. gray), same reasoning
        // as ImageFileLoaderTests' own equivalent test.
        var pixels = new Rgb24[]
        {
            new(255, 128, 0), new(1, 2, 3),
            new(4, 5, 6), new(7, 8, 9),
        };
        var source = new ArrayImageSource(2, 2, pixels);
        var path = Path.Combine(Path.GetTempPath(), $"yoniq-image-source-writer-test-{Guid.NewGuid()}.png");
        try
        {
            var writer = new ImageSourceWriter();

            await writer.WritePngAsync(source, path);

            using var written = await Image.LoadAsync<ImageSharpRgb24>(path);
            Assert.Equal(2, written.Width);
            Assert.Equal(2, written.Height);
            AssertPixel(written[0, 0], 255, 128, 0);
            AssertPixel(written[1, 0], 1, 2, 3);
            AssertPixel(written[0, 1], 4, 5, 6);
            AssertPixel(written[1, 1], 7, 8, 9);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WritePngAsync_CreatesTheDestinationDirectoryWhenItDoesNotExist()
    {
        var root = Path.Combine(Path.GetTempPath(), $"yoniq-image-source-writer-test-{Guid.NewGuid()}");
        var path = Path.Combine(root, "assets", "image.png");
        try
        {
            var writer = new ImageSourceWriter();
            var source = new ArrayImageSource(1, 1, [new Rgb24(9, 9, 9)]);

            await writer.WritePngAsync(source, path);

            Assert.True(File.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertPixel(ImageSharpRgb24 pixel, byte r, byte g, byte b)
    {
        Assert.Equal(r, pixel.R);
        Assert.Equal(g, pixel.G);
        Assert.Equal(b, pixel.B);
    }
}
