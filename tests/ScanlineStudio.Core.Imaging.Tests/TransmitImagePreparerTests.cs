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
    public async Task ApplyOverlay_FarOutOfBoundsPosition_DoesNotThrowAndLeavesImageUnchanged()
    {
        // TxImageEditorPaneViewModel's crop-relative re-projection (spec/18-path-to-1.0.md Medium
        // item: overlay-text WYSIWYG) can legitimately produce X/Y far outside [0,1] for an element
        // positioned outside a tight crop -- a 2%-wide crop can re-project to relX magnitudes around
        // 50, i.e. an Origin thousands of pixels off-canvas. Code-review finding: this was asserted
        // at the view-model level (against a fake preparer) but never against the REAL ImageSharp
        // DrawText call this actually reaches -- confirm directly that a far-out-of-bounds Origin is
        // a silent no-op, not a throw.
        var path = await WriteFixturePngAsync(16, 16, (_, _) => new ImageSharpRgb24(255, 255, 255));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 16, 16);
            var preparer = new TransmitImagePreparer(FontPath);
            var overlay = new ImageOverlay([
                new ImageOverlayElement("W", -50, -50, FontSizeRelative: 0.5, new Rgb24(255, 0, 0)),
            ]);

            var result = preparer.ApplyOverlay(source, overlay);

            Assert.Equal(16, result.Width);
            Assert.Equal(16, result.Height);
            AssertPixel(result, 0, 0, r: 255, g: 255, b: 255);
            AssertPixel(result, 8, 8, r: 255, g: 255, b: 255);
            AssertPixel(result, 15, 15, r: 255, g: 255, b: 255);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_AllZero_ReturnsTheSameInstanceUnchanged()
    {
        // ImageAdjustments.IsIdentity's whole point (spec/18-path-to-1.0.md Medium item: TX image
        // editor adjustment sliders): TxImageEditorPaneViewModel.RecomputePreview calls this on
        // every interactive frame, so the untouched-sliders case must skip a real ImageSharp
        // round-trip entirely, not just produce a visually-identical copy.
        var path = await WriteFixturePngAsync(4, 4, (_, _) => new ImageSharpRgb24(128, 128, 128));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 4);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments());

            Assert.Same(source, result);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_SharpenAndDenoiseAtZero_LeaveTheImageExactlyUnchanged()
    {
        // Code-review finding (round-1 plan-review): GaussianSharpen/GaussianBlur are not
        // documented to throw at sigma=0, but a zero sigma collapses to a degenerate kernel that
        // can silently produce NaN/corrupted output -- worse than a throw, since a naive "doesn't
        // throw" assertion wouldn't catch it. The real contract is that Sharpen=0/Denoise=0 must be
        // SKIPPED entirely (never call GaussianSharpen/GaussianBlur with sigma=0), which this test
        // pins via exact pixel equality, not just absence of an exception. Uses a non-uniform
        // source (checkerboard) so a real blur/sharpen pass -- if wrongly NOT skipped -- would
        // visibly smear it, unlike a flat-color source that a botched blur might leave looking
        // unchanged by coincidence.
        var path = await WriteFixturePngAsync(4, 4, (x, y) => (x + y) % 2 == 0
            ? new ImageSharpRgb24(255, 255, 255)
            : new ImageSharpRgb24(0, 0, 0));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 4, 4);
            var preparer = new TransmitImagePreparer(FontPath);
            // Brightness non-zero so IsIdentity is false and the Mutate chain actually runs --
            // Sharpen/Denoise still must be individually skipped within it.
            var adjustments = new ImageAdjustments(Brightness: 0, Contrast: 0, Saturation: 0, Gamma: 0, Sharpen: 0, Denoise: 0) with { Brightness = 0.0001 };

            var result = preparer.ApplyAdjustments(source, adjustments);

            for (var y = 0; y < 4; y++)
            {
                for (var x = 0; x < 4; x++)
                {
                    var expected = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                    AssertPixel(result, x, y, expected, expected, expected);
                }
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_PositiveBrightness_BrightensMidGray()
    {
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(128, 128, 128));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Brightness: 50));

            var pixel = result.GetScanline(0)[0];
            Assert.True(pixel.R > 128, $"Expected brightened pixel > 128, got {pixel.R}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_NegativeBrightness_DarkensMidGray()
    {
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(128, 128, 128));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Brightness: -50));

            var pixel = result.GetScanline(0)[0];
            Assert.True(pixel.R < 128, $"Expected darkened pixel < 128, got {pixel.R}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_PositiveContrast_PushesMidGrayLighterHalfTowardWhite()
    {
        // Code-review finding: Contrast/Saturation had no Core-layer pixel test at all -- a field
        // swap at the Brightness/Contrast/Saturate call sites would have passed every existing
        // test. Contrast pivots around mid-gray (128), pushing values above it further from 128 --
        // 200 (already lighter than the 128 pivot) must end up even lighter.
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(200, 200, 200));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Contrast: 50));

            var pixel = result.GetScanline(0)[0];
            Assert.True(pixel.R > 200, $"Expected higher contrast to push 200 further from the mid-gray pivot, got {pixel.R}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_NegativeSaturation_DesaturatesAChromaticColorTowardGray()
    {
        // Mid-gray is a fixed point of the saturation matrix (R=G=B already), so a chromatic
        // fixture is required to actually exercise Saturate -- picked directly per this sub-piece's
        // own code-review finding.
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(200, 100, 50));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Saturation: -50));

            var pixel = result.GetScanline(0)[0];
            var spreadBefore = 200 - 50;
            var spreadAfter = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B)) - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
            Assert.True(spreadAfter < spreadBefore, $"Expected desaturation to shrink the R/G/B spread below {spreadBefore}, got {spreadAfter}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_PositiveGamma_BrightensMidGray()
    {
        // Round-1 plan-review blocker: the raw `out = in^gamma` formula DARKENS as the exponent
        // rises above 1 -- this pins that the slider's own sign convention was corrected (positive
        // Gamma must brighten, matching Brightness/Contrast's own sign, not the raw formula's).
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(128, 128, 128));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Gamma: 100));

            var pixel = result.GetScanline(0)[0];
            Assert.True(pixel.R > 128, $"Expected gamma-brightened pixel > 128, got {pixel.R}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_NegativeGamma_DarkensMidGray()
    {
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(128, 128, 128));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Gamma: -100));

            var pixel = result.GetScanline(0)[0];
            Assert.True(pixel.R < 128, $"Expected gamma-darkened pixel < 128, got {pixel.R}.");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_ZeroGamma_IsAnExactIdentity()
    {
        // Gamma=0 is skipped entirely now (code-review finding: the hand-rolled per-pixel loop is
        // real synchronous work, not free like Brightness/Contrast/Saturate's ImageSharp built-ins,
        // so it's skipped the same way Sharpen/Denoise are) -- confirmed here with exact pixel
        // equality, same shape as the Sharpen/Denoise skip-at-zero test above.
        var path = await WriteFixturePngAsync(2, 2, (_, _) => new ImageSharpRgb24(37, 128, 211));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 2, 2);
            var preparer = new TransmitImagePreparer(FontPath);
            var adjustments = new ImageAdjustments(Gamma: 0) with { Brightness = 0.0001 };

            var result = preparer.ApplyAdjustments(source, adjustments);

            AssertPixel(result, 0, 0, 37, 128, 211);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_NonZeroSharpen_ChangesACurvedGradient()
    {
        // 32x32, not a tiny fixture -- caught a real finding while writing this test: ImageSharp's
        // Gaussian convolution throws ArgumentOutOfRangeException when the kernel radius (derived
        // from sigma, up to ~9px at Sharpen=100's sigma=3.0) exceeds a too-small image's own
        // dimensions. Not reachable in the real pipeline -- ApplyAdjustments only ever runs on an
        // already-Resize()'d image at the target mode's exact dimensions, and the smallest real
        // mode is 320x120 (verified against every SstvModeDefinition in the codebase) -- but real
        // enough to document on ApplyAdjustments' own doc comment as a precondition, and to avoid
        // silently masking with an unrealistically tiny test fixture here.
        //
        // A strict checkerboard (the ApplyAdjustments_NonZeroDenoise test's own fixture) turned
        // out to be the WRONG fixture for sharpen specifically -- caught by this test actually
        // failing first: every pixel is already at a clamped local extreme (0 or 255), so a
        // sharpen kernel (which pushes AWAY from the local neighborhood average) has nowhere
        // further to push -- the checkerboard is already "maximally sharp." A single-pixel mid-gray
        // transition column also turned out wrong -- that ONE column sits almost exactly at its own
        // local blurred average by construction, so the unsharp-mask "original - blurred" delta is
        // itself near zero there. A LINEAR ramp turned out wrong too (code-review finding): a
        // symmetric normalized unsharp kernel reproduces a linear function exactly in its interior
        // (kernel weights sum to 1, and the weighted sum of offsets around each tap sums to 0), so
        // every interior pixel of a straight ramp is mathematically unchanged by construction --
        // that test was only exercising border-replication effects at the image edges, not real
        // sharpening. Use a sinusoidal curve instead (real, non-zero curvature at essentially every
        // point) so sharpening has real interior work to do, and assert broadly (ANY pixel changed)
        // rather than betting on one specific coordinate.
        var path = await WriteFixturePngAsync(32, 32, (x, _) =>
        {
            var v = (byte)(128 + (100 * Math.Sin(2 * Math.PI * x / 32.0)));
            return new ImageSharpRgb24(v, v, v);
        });
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 32, 32);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Sharpen: 100));

            Assert.Equal(32, result.Width);
            Assert.Equal(32, result.Height);
            AssertAnyPixelDiffersFrom(result, (x, _) => (byte)(128 + (100 * Math.Sin(2 * Math.PI * x / 32.0))));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyAdjustments_NonZeroDenoise_ChangesAHighContrastImage()
    {
        var path = await WriteFixturePngAsync(32, 32, (x, y) => (x + y) % 2 == 0
            ? new ImageSharpRgb24(255, 255, 255)
            : new ImageSharpRgb24(0, 0, 0));
        try
        {
            var source = await new ImageFileLoader().LoadAsync(path, 32, 32);
            var preparer = new TransmitImagePreparer(FontPath);

            var result = preparer.ApplyAdjustments(source, new ImageAdjustments(Denoise: 100));

            Assert.Equal(32, result.Width);
            Assert.Equal(32, result.Height);
            AssertImageDiffersFromCheckerboard(result);
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

    private static void AssertAnyPixelDiffersFrom(IImageSource image, Func<int, int, byte> expectedGrayAt)
    {
        var foundDifference = false;
        for (var y = 0; y < image.Height && !foundDifference; y++)
        {
            var row = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                var expected = expectedGrayAt(x, y);
                if (row[x].R != expected)
                {
                    foundDifference = true;
                    break;
                }
            }
        }

        Assert.True(foundDifference, "Expected the filter to visibly change at least one pixel of the gradient.");
    }

    private static void AssertImageDiffersFromCheckerboard(IImageSource image)
    {
        var foundDifference = false;
        for (var y = 0; y < image.Height && !foundDifference; y++)
        {
            var row = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                var expected = (x + y) % 2 == 0 ? (byte)255 : (byte)0;
                if (row[x].R != expected || row[x].G != expected || row[x].B != expected)
                {
                    foundDifference = true;
                    break;
                }
            }
        }

        Assert.True(foundDifference, "Expected the filter to visibly change at least one pixel from the original checkerboard.");
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
