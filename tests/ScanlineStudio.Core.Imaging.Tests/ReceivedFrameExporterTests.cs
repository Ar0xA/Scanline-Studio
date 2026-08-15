using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class ReceivedFrameExporterTests
{
    [Fact]
    public async Task ExportAsync_PngToPng_PreservesDimensionsAndPixels()
    {
        var sourcePath = await WriteFixturePngAsync(3, 2, new Rgb24(255, 128, 0));
        var destinationPath = TempPath(".png");
        try
        {
            var exporter = new ReceivedFrameExporter();

            await exporter.ExportAsync(sourcePath, destinationPath, jpegQuality: 85);

            using var exported = await Image.LoadAsync<Rgb24>(destinationPath);
            Assert.Equal(3, exported.Width);
            Assert.Equal(2, exported.Height);
            var pixel = exported[0, 0];
            Assert.Equal(255, pixel.R);
            Assert.Equal(128, pixel.G);
            Assert.Equal(0, pixel.B);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task ExportAsync_PngToJpeg_ProducesAValidJpegAtTheRequestedDimensions()
    {
        var sourcePath = await WriteFixturePngAsync(4, 4, new Rgb24(10, 20, 30));
        var destinationPath = TempPath(".jpg");
        try
        {
            var exporter = new ReceivedFrameExporter();

            await exporter.ExportAsync(sourcePath, destinationPath, jpegQuality: 85);

            using var exported = await Image.LoadAsync<Rgb24>(destinationPath);
            Assert.Equal(4, exported.Width);
            Assert.Equal(4, exported.Height);
            Assert.Equal("JPEG", (await Image.IdentifyAsync(destinationPath)).Metadata.DecodedImageFormat?.Name);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Theory]
    [InlineData(".jpeg")]
    [InlineData(".JPG")]
    public async Task ExportAsync_RecognizesJpegExtensionCaseInsensitivelyAndAlternateSpelling(string extension)
    {
        var sourcePath = await WriteFixturePngAsync(2, 2, new Rgb24(1, 2, 3));
        var destinationPath = TempPath(extension);
        try
        {
            var exporter = new ReceivedFrameExporter();

            await exporter.ExportAsync(sourcePath, destinationPath, jpegQuality: 85);

            Assert.Equal("JPEG", (await Image.IdentifyAsync(destinationPath)).Metadata.DecodedImageFormat?.Name);
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(destinationPath);
        }
    }

    [Fact]
    public async Task ExportAsync_HigherJpegQuality_NeverProducesASmallerFileThanLowerQuality_ForTheSameSource()
    {
        // Weak but real: a higher quality setting should never compress WORSE than a lower one for
        // identical source data -- confirms the Quality parameter actually reaches the encoder,
        // without needing to hand-derive an exact expected byte count.
        var sourcePath = await WriteFixtureNoisyPngAsync(64, 64);
        var lowQualityPath = TempPath(".jpg");
        var highQualityPath = TempPath(".jpg");
        try
        {
            var exporter = new ReceivedFrameExporter();

            await exporter.ExportAsync(sourcePath, lowQualityPath, jpegQuality: 10);
            await exporter.ExportAsync(sourcePath, highQualityPath, jpegQuality: 95);

            var lowQualitySize = new FileInfo(lowQualityPath).Length;
            var highQualitySize = new FileInfo(highQualityPath).Length;
            Assert.True(highQualitySize > lowQualitySize, $"Expected quality=95 ({highQualitySize} bytes) to produce a larger file than quality=10 ({lowQualitySize} bytes).");
        }
        finally
        {
            File.Delete(sourcePath);
            File.Delete(lowQualityPath);
            File.Delete(highQualityPath);
        }
    }

    private static string TempPath(string extension) => Path.Combine(Path.GetTempPath(), $"yoniq-frame-exporter-test-{Guid.NewGuid()}{extension}");

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
        var path = TempPath(".png");
        await image.SaveAsPngAsync(path);
        return path;
    }

    private static async Task<string> WriteFixtureNoisyPngAsync(int width, int height)
    {
        // JPEG quality differences are only visible on data with enough high-frequency detail --
        // a flat-color fixture compresses to (near-)identical tiny sizes at every quality level.
        var random = new Random(12345);
        using var image = new Image<Rgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgb24((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
                }
            }
        });
        var path = TempPath(".png");
        await image.SaveAsPngAsync(path);
        return path;
    }
}
