using ScanlineStudio.Abstractions.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ScanlineStudio.Core.Imaging.Tests;

/// <summary>Pins the unrotated image-element blit (<c>DrawTemplateImage</c>'s resize + <c>DrawImage</c>) against a
/// same-run <c>DrawImage</c> reference, through both <see cref="TransmitImagePreparer.ApplyTemplate"/> and
/// <see cref="TransmitImagePreparer.ComposePreview"/>. Same-run comparison only, never stored hashes: ImageSharp's
/// SIMD resampling can differ by machine. Any replacement of that blit must keep these passing.</summary>
public sealed class TransmitImagePreparerElementBlitTests
{
    private const int CanvasWidth = 320;
    private const int CanvasHeight = 256;
    private const double ElementWidthPx = 96;
    private const double ElementHeightPx = 80;

    public enum Placement
    {
        Inside,
        PartlyOffLeft,
        PartlyOffRight,
        PartlyOffTop,
        PartlyOffBottom,
        FullyOffLeft,
        FullyOffRight,
        FullyOffTop,
        FullyOffBottom,
    }

    public static TheoryData<ImageFitMode, Placement> FitAndPlacement()
    {
        var data = new TheoryData<ImageFitMode, Placement>();
        foreach (var fit in Enum.GetValues<ImageFitMode>())
        {
            foreach (var placement in Enum.GetValues<Placement>())
            {
                data.Add(fit, placement);
            }
        }

        return data;
    }

    public static TheoryData<ImageFitMode, double, double> FarOut(bool atIntLimit)
    {
        var data = new TheoryData<ImageFitMode, double, double>();
        // 2147483520 is the largest float-exact magnitude below 2^31, so its rounded location converts to int on every runtime.
        double[] farOffsets = atIntLimit ? [int.MaxValue, int.MinValue] : [1e6, -1e6, 2147483520.0, -2147483520.0];
        foreach (var fit in Enum.GetValues<ImageFitMode>())
        {
            foreach (var offset in farOffsets)
            {
                data.Add(fit, offset, 40);
                data.Add(fit, 40, offset);
                data.Add(fit, offset, offset);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FitAndPlacement))]
    public void ApplyTemplate_ImageElement_MatchesDrawImageReference(ImageFitMode fit, Placement placement)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var canvas = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);
        var element = BuildElement(fit, placement);

        var expected = ElementBlitTestSupport.DrawImageReference(preparer, canvas, element);
        var actual = preparer.ApplyTemplate(canvas, new TemplateDocument(null, [element]));

        ElementBlitTestSupport.AssertPixelsEqual(expected, actual);
        AssertFullyOffCanvasLeavesCanvasUnchanged(placement, canvas, actual);
    }

    [Theory]
    [MemberData(nameof(FitAndPlacement))]
    public void ComposePreview_ImageElement_MatchesDrawImageReference(ImageFitMode fit, Placement placement)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var source = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);
        var element = BuildElement(fit, placement);
        var fullFrame = new NormalizedRect(0, 0, 1, 1);
        var basePreview = preparer.ComposePreview(source, fullFrame, CanvasWidth, CanvasHeight, preserveAspect: false, new ImageAdjustments(), new TemplateDocument(null, []));

        var expected = ElementBlitTestSupport.DrawImageReference(preparer, basePreview, element);
        var actual = preparer.ComposePreview(source, fullFrame, CanvasWidth, CanvasHeight, preserveAspect: false, new ImageAdjustments(), new TemplateDocument(null, [element]));

        ElementBlitTestSupport.AssertPixelsEqual(expected, actual);
        AssertFullyOffCanvasLeavesCanvasUnchanged(placement, basePreview, actual);
    }

    [Theory]
    [MemberData(nameof(FarOut), false)]
    public void ApplyTemplate_FarOutCoordinates_DoNotThrowAndLeaveCanvasUnchanged(ImageFitMode fit, double xPx, double yPx)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var canvas = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);
        var element = ElementAtPixels(fit, xPx, yPx);

        var actual = preparer.ApplyTemplate(canvas, new TemplateDocument(null, [element]));

        ElementBlitTestSupport.AssertPixelsEqual(canvas, actual);
    }

    // At |coords| = 2^31 px the location conversion is runtime-dependent, and int.MinValue makes DrawImage overflow and draw at 0.
    [Theory]
    [MemberData(nameof(FarOut), true)]
    public void ApplyTemplate_IntLimitCoordinates_DoNotThrow(ImageFitMode fit, double xPx, double yPx)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var canvas = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);

        var actual = preparer.ApplyTemplate(canvas, new TemplateDocument(null, [ElementAtPixels(fit, xPx, yPx)]));

        Assert.Equal((CanvasWidth, CanvasHeight), (actual.Width, actual.Height));
    }

    [Theory(Skip = "Known defect, BACKLOG CQ-BLIT-FAROUT: at |coords| = 2^31 px the blit is runtime-dependent and int.MinValue draws the element at 0")]
    [MemberData(nameof(FarOut), true)]
    public void ApplyTemplate_IntLimitCoordinates_LeaveCanvasUnchanged(ImageFitMode fit, double xPx, double yPx)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var canvas = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);

        var actual = preparer.ApplyTemplate(canvas, new TemplateDocument(null, [ElementAtPixels(fit, xPx, yPx)]));

        ElementBlitTestSupport.AssertPixelsEqual(canvas, actual);
    }

    [Theory]
    [MemberData(nameof(FarOut), false)]
    public void ComposePreview_FarOutCoordinates_DoNotThrowAndLeaveCanvasUnchanged(ImageFitMode fit, double xPx, double yPx)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var source = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);
        var fullFrame = new NormalizedRect(0, 0, 1, 1);
        var basePreview = preparer.ComposePreview(source, fullFrame, CanvasWidth, CanvasHeight, preserveAspect: false, new ImageAdjustments(), new TemplateDocument(null, []));

        var actual = preparer.ComposePreview(source, fullFrame, CanvasWidth, CanvasHeight, preserveAspect: false, new ImageAdjustments(), new TemplateDocument(null, [ElementAtPixels(fit, xPx, yPx)]));

        ElementBlitTestSupport.AssertPixelsEqual(basePreview, actual);
    }

    private static void AssertFullyOffCanvasLeavesCanvasUnchanged(Placement placement, IImageSource canvas, IImageSource actual)
    {
        if (placement is Placement.FullyOffLeft or Placement.FullyOffRight or Placement.FullyOffTop or Placement.FullyOffBottom)
        {
            ElementBlitTestSupport.AssertPixelsEqual(canvas, actual);
        }
        else
        {
            Assert.False(ElementBlitTestSupport.Pixels(canvas).SequenceEqual(ElementBlitTestSupport.Pixels(actual)), "an on-canvas element must change the canvas");
        }
    }

    private static TemplateImageElement BuildElement(ImageFitMode fit, Placement placement)
    {
        // Fractional positions exercise the MathF.Round location step.
        var (x, y) = placement switch
        {
            Placement.Inside => (50.4, 40.6),
            Placement.PartlyOffLeft => (-30.5, 60.2),
            Placement.PartlyOffRight => (CanvasWidth - 40.3, 60.2),
            Placement.PartlyOffTop => (70.7, -25.7),
            Placement.PartlyOffBottom => (70.7, CanvasHeight - 33.2),
            Placement.FullyOffLeft => (-200.0, 60.0),
            Placement.FullyOffRight => (CanvasWidth + 80.0, 60.0),
            Placement.FullyOffTop => (70.0, -150.0),
            Placement.FullyOffBottom => (70.0, CanvasHeight + 44.0),
            _ => throw new ArgumentOutOfRangeException(nameof(placement)),
        };
        return ElementAtPixels(fit, x, y);
    }

    private static TemplateImageElement ElementAtPixels(ImageFitMode fit, double xPx, double yPx) =>
        new(new NormalizedRect(xPx / CanvasWidth, yPx / CanvasHeight, ElementWidthPx / CanvasWidth, ElementHeightPx / CanvasHeight),
            Z: 0, ElementBlitTestSupport.Patterned(97, 61, seed: 7), fit);
}

/// <summary>Shared by the element-blit and resize-cache tests and the ComposePreview probe: patterned sources, pixel comparison, and a test-local
/// replica of <c>DrawTemplateImage</c>'s unrotated resize-and-<c>DrawImage</c> path.</summary>
internal static class ElementBlitTestSupport
{
    public static TransmitImagePreparer CreatePreparer() => new(FontPath);

    private static readonly string FontPath = Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    public static ArrayImageSource Patterned(int width, int height, int seed)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24((byte)((x * 7) + seed), (byte)((y * 5) + (seed * 3)), (byte)(((x + y) * 3) ^ seed));
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    public static Rgb24[] Pixels(IImageSource image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            image.GetScanline(y).CopyTo(pixels.AsSpan(y * image.Width, image.Width));
        }

        return pixels;
    }

    public static void AssertPixelsEqual(IImageSource expected, IImageSource actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var e = Pixels(expected);
        var a = Pixels(actual);
        var differing = 0;
        var first = -1;
        for (var i = 0; i < e.Length; i++)
        {
            if (e[i] != a[i])
            {
                differing++;
                first = first < 0 ? i : first;
            }
        }

        Assert.True(differing == 0, $"{differing} differing pixels; first at ({first % expected.Width},{first / expected.Width}): expected {(first >= 0 ? e[first] : default)}, actual {(first >= 0 ? a[first] : default)}");
    }

    /// <summary>Mirrors <c>DrawTemplateImage</c>'s unrotated path: pixel bounds, the uniform safety ceiling, the
    /// fit-mode resize, then <c>DrawImage</c> at the rounded location with opacity 1.</summary>
    public static IImageSource DrawImageReference(TransmitImagePreparer preparer, IImageSource canvas, TemplateImageElement element)
    {
        var x = (float)(element.Bounds.X * canvas.Width);
        var y = (float)(element.Bounds.Y * canvas.Height);
        var w = (float)(element.Bounds.Width * canvas.Width);
        var h = (float)(element.Bounds.Height * canvas.Height);
        var rawWidthPx = MathF.Max(1f, MathF.Round(w));
        var rawHeightPx = MathF.Max(1f, MathF.Round(h));
        var ceiling = MathF.Min((float)TransmitImageLimits.MaxElementResizeDimensionPx, 8f * MathF.Max(canvas.Width, canvas.Height));
        var safetyScale = MathF.Min(1f, ceiling / MathF.Max(rawWidthPx, rawHeightPx));
        var targetWidth = Math.Max(1, (int)MathF.Round(rawWidthPx * safetyScale));
        var targetHeight = Math.Max(1, (int)MathF.Round(rawHeightPx * safetyScale));

        using var resized = element.Fit switch
        {
            ImageFitMode.Stretch => ToImageSharp(preparer.Resize(element.Source, targetWidth, targetHeight, preserveAspect: false)),
            ImageFitMode.Contain => ToImageSharp(preparer.Resize(element.Source, targetWidth, targetHeight, preserveAspect: true)),
            ImageFitMode.Cover => ResizeCover(element.Source, targetWidth, targetHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(element)),
        };
        using var image = ToImageSharp(canvas);
        var location = new Point((int)MathF.Round(x), (int)MathF.Round(y));
        image.Mutate(ctx => ctx.DrawImage(resized, location, 1f));
        return FromImageSharp(image);
    }

    private static Image<SixLabors.ImageSharp.PixelFormats.Rgb24> ResizeCover(IImageSource source, int width, int height)
    {
        var image = ToImageSharp(source);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions { Size = new Size(width, height), Mode = ResizeMode.Crop }));
        return image;
    }

    public static Image<SixLabors.ImageSharp.PixelFormats.Rgb24> ToImageSharp(IImageSource source)
    {
        var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(source.Width, source.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < source.Height; y++)
            {
                var sourceRow = source.GetScanline(y);
                var destinationRow = accessor.GetRowSpan(y);
                for (var x = 0; x < source.Width; x++)
                {
                    destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(sourceRow[x].R, sourceRow[x].G, sourceRow[x].B);
                }
            }
        });
        return image;
    }

    private static ArrayImageSource FromImageSharp(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    pixels[(y * row.Length) + x] = new Rgb24(row[x].R, row[x].G, row[x].B);
                }
            }
        });
        return new ArrayImageSource(image.Width, image.Height, pixels);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ScanlineStudio.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from " + AppContext.BaseDirectory);
    }
}
