using SixLabors.ImageSharp;
using ImageSharpRgb24 = SixLabors.ImageSharp.PixelFormats.Rgb24;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

public sealed class TransmitImagePreparerTests
{
    [Fact]
    public async Task Crop_ExtractsTheRequestedNormalizedRegion()
    {
        // 4x4 image, top-left 2x2 quadrant is red, everything else blue.
        var path = await WriteFixturePngAsync(4, 4, (x, y) => x < 2 && y < 2
            ? new ImageSharpRgb24(255, 0, 0)
            : new ImageSharpRgb24(0, 0, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 4);
            var preparer = new TransmitImagePreparer(FontPath);

            var cropped = preparer.Crop(source, new NormalizedRect(0, 0, 0.5, 0.5));

            Assert.Equal(2, cropped.Width);
            Assert.Equal(2, cropped.Height);
            AssertPixel(cropped, 0, 0, r: 255, g: 0, b: 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resize_Stretch_ReturnsExactTargetDimensions()
    {
        var path = await WriteFixturePngAsync(4, 8, (_, _) => new ImageSharpRgb24(10, 20, 30));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 8);
            var preparer = new TransmitImagePreparer(FontPath);

            var resized = preparer.Resize(source, 6, 3, preserveAspect: false);

            Assert.Equal(6, resized.Width);
            Assert.Equal(3, resized.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Resize_PreserveAspect_ReturnsExactTargetDimensionsWithBlackLetterboxing()
    {
        // A wide 8x2 source into a square 4x4 target must letterbox top/bottom with black.
        var path = await WriteFixturePngAsync(8, 2, (_, _) => new ImageSharpRgb24(200, 200, 200));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 8, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var resized = preparer.Resize(source, 4, 4, preserveAspect: true);

            Assert.Equal(4, resized.Width);
            Assert.Equal(4, resized.Height);
            // Top row should be letterboxed black.
            AssertPixel(resized, 0, 0, r: 0, g: 0, b: 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyOverlay_DrawsTextNearTheSpecifiedPosition_LeavingFarPixelsUnchanged()
    {
        var path = await WriteFixturePngAsync(64, 64, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 64, 64);
            var preparer = new TransmitImagePreparer(FontPath);
            var overlay = new ImageOverlay([
                new ImageOverlayElement("W", 0.5, 0.5, FontSizeRelative: 0.5, new Rgb24(255, 0, 0)),
            ]);

            var result = preparer.ApplyOverlay(source, overlay);

            Assert.Equal(64, result.Width);
            Assert.Equal(64, result.Height);
            AssertOverlayWasDrawnNearCenterButNotInTheCorner(result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rotate_SwapsDimensions_AndMovesTheTopLeftPixelToTheTopRight()
    {
        // spec/18-path-to-1.0.md High item 3. A 4x2 source, top-left pixel red, everything else
        // blue. This is the ONLY test that pins RotateMode.Rotate90 as actually clockwise (not
        // counter-clockwise) -- TxImageEditorPaneViewModel's own crop-rect/overlay coordinate
        // transforms are derived assuming clockwise, and would be wrong-by-mirror otherwise.
        var path = await WriteFixturePngAsync(4, 2, (x, y) => x == 0 && y == 0
            ? new ImageSharpRgb24(255, 0, 0)
            : new ImageSharpRgb24(0, 0, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var rotated = preparer.Rotate(source);

            Assert.Equal(2, rotated.Width);
            Assert.Equal(4, rotated.Height);
            // Old top-left (0,0) -> new top-right (W-1, 0) under a clockwise rotation.
            AssertPixel(rotated, 1, 0, r: 255, g: 0, b: 0);
            AssertPixel(rotated, 0, 0, r: 0, g: 0, b: 255);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Span-typed locals (ReadOnlySpan<Rgb24> from IImageSource.GetScanline) cannot be declared
    // inside an async method body at all -- a C# language restriction, not an ambiguity issue --
    // so every scanline-touching assertion is a plain synchronous helper called after all
    // awaiting is already done.
    private static void AssertPixel(IImageSource image, int x, int y, byte r, byte g, byte b)
    {
        var pixel = image.GetScanline(y)[x];
        Assert.Equal(r, pixel.R);
        Assert.Equal(g, pixel.G);
        Assert.Equal(b, pixel.B);
    }

    private static void AssertOverlayWasDrawnNearCenterButNotInTheCorner(IImageSource image)
    {
        var foundNonWhite = false;
        for (var y = 24; y < 40 && !foundNonWhite; y++)
        {
            var row = image.GetScanline(y);
            for (var x = 24; x < 40; x++)
            {
                if (row[x].R != 255 || row[x].G != 255 || row[x].B != 255)
                {
                    foundNonWhite = true;
                    break;
                }
            }
        }

        Assert.True(foundNonWhite, "Expected some non-white pixel near the overlay's requested position.");

        var cornerPixel = image.GetScanline(0)[0];
        Assert.Equal(255, cornerPixel.R);
        Assert.Equal(255, cornerPixel.G);
        Assert.Equal(255, cornerPixel.B);
    }

    private static string FontPath { get; } = Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return dir.FullName;
    }

    private static async Task<string> WriteFixturePngAsync(int width, int height, Func<int, int, ImageSharpRgb24> pixelAt)
    {
        using var image = new Image<ImageSharpRgb24>(width, height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    row[x] = pixelAt(x, y);
                }
            }
        });
        var path = Path.Combine(Path.GetTempPath(), $"scanline-studio-preparer-test-{Guid.NewGuid()}.png");
        await image.SaveAsPngAsync(path);
        return path;
    }
}
