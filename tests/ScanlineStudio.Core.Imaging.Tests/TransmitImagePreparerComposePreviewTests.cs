using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

/// <summary>
/// T1-14 (production_audit.md)'s required gate: <see cref="TransmitImagePreparer.ComposePreview"/>
/// (fused, one ImageSharp round-trip) must be pixel-EXACT against manually chaining
/// Crop -&gt; Resize -&gt; ApplyAdjustments -&gt; ApplyTemplate (the old, per-stage-round-trip shape,
/// still used unchanged by <c>TxImageEditorPaneViewModel.BuildFinalOutput</c> for the actual
/// transmitted image). This is a legitimate exact-equality gate, not a tolerance-bounded one --
/// <c>ToImageSharp</c>/<c>FromImageSharp</c> are lossless 8-bit R/G/B field copies in both directions,
/// so removing intermediate round-trips cannot change a single pixel if the fused implementation is
/// correct. Every case here uses a non-uniform (position-dependent) source pattern specifically so a
/// coordinate-space bug (e.g. accidentally reading the post-crop image's own width/height where the
/// pre-crop source's width/height was required) would show up as a real pixel mismatch, not be masked
/// by every pixel already having the same value.
///
/// Code-review finding: this file is the ONLY guard on the real, fused pipeline. The UI project's
/// own <c>TxImageEditorPaneViewModelTests.cs</c> assertions against its fake preparer's per-stage
/// call counts (e.g. <c>ResizeCallCount</c>) exercise <see cref="ITransmitImagePreparer.ComposePreview"/>'s
/// own DEFAULT interface implementation (the un-fused 4-call chain every OTHER implementer inherits),
/// not <see cref="TransmitImagePreparer"/>'s real override -- they say nothing about the production
/// pipeline's own behavior.
/// </summary>
public sealed class TransmitImagePreparerComposePreviewTests
{
    private static readonly string FontPath = Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    // Deliberately non-square and non-power-of-two, so a width/height mixup or an off-by-one in the
    // crop/resize math doesn't coincidentally still line up.
    private const int SourceWidth = 37;
    private const int SourceHeight = 23;

    private static IImageSource BuildPatternedSource()
    {
        var pixels = new Rgb24[SourceWidth * SourceHeight];
        for (var y = 0; y < SourceHeight; y++)
        {
            for (var x = 0; x < SourceWidth; x++)
            {
                pixels[(y * SourceWidth) + x] = new Rgb24((byte)(x * 6 % 256), (byte)(y * 10 % 256), (byte)((x + y) * 3 % 256));
            }
        }

        return new ArrayImageSource(SourceWidth, SourceHeight, pixels);
    }

    private static TemplateDocument EmptyTemplate() => new(Name: null, Elements: []);

    private static TemplateDocument TextTemplate() => new(Name: null, Elements:
    [
        new TemplateTextElement(
            new NormalizedRect(0.1, 0.1, 0.5, 0.3), Z: 0, Content: "Hi", new FontSpec("DejaVu Sans Mono", 0.2),
            new Rgb24(255, 255, 255)),
    ]);

    private static TemplateDocument ImageElementTemplate(IImageSource elementSource) => new(Name: null, Elements:
    [
        new TemplateImageElement(new NormalizedRect(0.2, 0.2, 0.4, 0.4), Z: 0, elementSource, ImageFitMode.Cover),
    ]);

    private static TemplateDocument BoxTemplate() => new(Name: null, Elements:
    [
        new TemplateBoxElement(
            new NormalizedRect(0.1, 0.1, 0.3, 0.3), Z: 0, FillColor: new Rgb24(20, 40, 60),
            BorderColor: new Rgb24(200, 200, 200), BorderThickness: 0.05, CornerRadius: 0.02),
    ]);

    private static void AssertPixelExact(IImageSource expected, IImageSource actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var y = 0; y < expected.Height; y++)
        {
            var expectedRow = expected.GetScanline(y);
            var actualRow = actual.GetScanline(y);
            for (var x = 0; x < expected.Width; x++)
            {
                Assert.True(
                    expectedRow[x].R == actualRow[x].R && expectedRow[x].G == actualRow[x].G && expectedRow[x].B == actualRow[x].B,
                    $"pixel ({x},{y}): expected {expectedRow[x]}, got {actualRow[x]}");
            }
        }
    }

    private static IImageSource ComputeViaManualChain(
        TransmitImagePreparer preparer, IImageSource source, NormalizedRect cropRegion, int targetWidth, int targetHeight,
        bool preserveAspect, ImageAdjustments adjustments, TemplateDocument template)
    {
        var cropped = preparer.Crop(source, cropRegion);
        var resized = preparer.Resize(cropped, targetWidth, targetHeight, preserveAspect);
        var adjusted = preparer.ApplyAdjustments(resized, adjustments);
        return preparer.ApplyTemplate(adjusted, template);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposePreview_IdentityAdjustments_EmptyTemplate_MatchesManualChain(bool preserveAspect)
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var source = BuildPatternedSource();
        var cropRegion = new NormalizedRect(0, 0, 1, 1);
        var adjustments = new ImageAdjustments();
        var template = EmptyTemplate();

        var expected = ComputeViaManualChain(preparer, source, cropRegion, 20, 15, preserveAspect, adjustments, template);
        var actual = preparer.ComposePreview(source, cropRegion, 20, 15, preserveAspect, adjustments, template);

        AssertPixelExact(expected, actual);
    }

    [Fact]
    public void ComposePreview_OffsetNonFullFrameCrop_WithNonEmptyTemplate_MatchesManualChain()
    {
        // Plan-review finding: a full-frame crop can't distinguish a source.Width-vs-image.Width bug
        // (they're equal there) -- this crop is genuinely offset and smaller than the source.
        var preparer = new TransmitImagePreparer(FontPath);
        var source = BuildPatternedSource();
        var cropRegion = new NormalizedRect(0.25, 0.1, 0.5, 0.6);
        var adjustments = new ImageAdjustments(Brightness: 15, Contrast: -10);
        var template = TextTemplate();

        var expected = ComputeViaManualChain(preparer, source, cropRegion, 24, 18, preserveAspect: false, adjustments, template);
        var actual = preparer.ComposePreview(source, cropRegion, 24, 18, preserveAspect: false, adjustments, template);

        AssertPixelExact(expected, actual);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComposePreview_NonIdentityAdjustments_NonEmptyTemplate_MatchesManualChain(bool preserveAspect)
    {
        // preserveAspect: true exercises the letterbox bars (Resize's own Pad mode) combined with a
        // template element potentially overlapping them.
        var preparer = new TransmitImagePreparer(FontPath);
        var source = BuildPatternedSource();
        var cropRegion = new NormalizedRect(0.05, 0.05, 0.9, 0.9);
        var adjustments = new ImageAdjustments(Brightness: -8, Contrast: 12, Saturation: 5);
        var template = TextTemplate();

        var expected = ComputeViaManualChain(preparer, source, cropRegion, 30, 20, preserveAspect, adjustments, template);
        var actual = preparer.ComposePreview(source, cropRegion, 30, 20, preserveAspect, adjustments, template);

        AssertPixelExact(expected, actual);
    }

    [Fact]
    public void ComposePreview_AllThreePerPixelAdjustmentsNonZero_MatchesManualChain()
    {
        // Gamma/Denoise/Sharpen are the hand-rolled/Gaussian per-pixel-cost operations -- the ones
        // most likely to expose a subtle Mutate-chain-ordering slip if the extraction got it wrong.
        var preparer = new TransmitImagePreparer(FontPath);
        var source = BuildPatternedSource();
        var cropRegion = new NormalizedRect(0, 0, 1, 1);
        var adjustments = new ImageAdjustments(Gamma: 20, Denoise: 40, Sharpen: 60);
        var template = EmptyTemplate();

        var expected = ComputeViaManualChain(preparer, source, cropRegion, 25, 20, preserveAspect: false, adjustments, template);
        var actual = preparer.ComposePreview(source, cropRegion, 25, 20, preserveAspect: false, adjustments, template);

        AssertPixelExact(expected, actual);
    }

    [Fact]
    public void ComposePreview_TemplateImageElement_MatchesManualChain()
    {
        // Exercises DrawTemplateImage's own GetOrCreateResizedImage/_imageResizeCache path and
        // DrawImage clipping -- untouched by this extraction, but only reachable via ApplyTemplate's
        // own element-drawing loop, which ComposePreview must invoke identically.
        var preparer = new TransmitImagePreparer(FontPath);
        var source = BuildPatternedSource();
        var elementSource = new ArrayImageSource(6, 4, Enumerable.Range(0, 24)
            .Select(i => new Rgb24((byte)(i * 10 % 256), (byte)(i * 5 % 256), (byte)(i * 3 % 256))).ToArray());
        var cropRegion = new NormalizedRect(0, 0, 1, 1);
        var adjustments = new ImageAdjustments();
        var template = ImageElementTemplate(elementSource);

        var expected = ComputeViaManualChain(preparer, source, cropRegion, 22, 16, preserveAspect: false, adjustments, template);
        var actual = preparer.ComposePreview(source, cropRegion, 22, 16, preserveAspect: false, adjustments, template);

        AssertPixelExact(expected, actual);
    }

    [Fact]
    public void ComposePreview_TemplateBoxElement_MatchesManualChain()
    {
        var preparer = new TransmitImagePreparer(FontPath);
        var source = BuildPatternedSource();
        var cropRegion = new NormalizedRect(0, 0, 1, 1);
        var adjustments = new ImageAdjustments();
        var template = BoxTemplate();

        var expected = ComputeViaManualChain(preparer, source, cropRegion, 22, 16, preserveAspect: false, adjustments, template);
        var actual = preparer.ComposePreview(source, cropRegion, 22, 16, preserveAspect: false, adjustments, template);

        AssertPixelExact(expected, actual);
    }

    [Fact]
    public void ComposePreview_IsImplementedByTheConcreteClass_NotFallingThroughToTheInterfaceDefault()
    {
        // Code-review finding: a default interface member is silent on signature mismatch -- no
        // compile error. If TransmitImagePreparer.ComposePreview's own signature ever drifted from
        // ITransmitImagePreparer.ComposePreview's, the class would stop implicitly implementing it,
        // and every call through an ITransmitImagePreparer-typed reference (the VM's own _preparer
        // field IS interface-typed) would silently fall back to the slow, un-fused default chain.
        // Output would stay pixel-identical either way (both are correct), so none of this file's
        // other tests could ever catch that regression -- only checking the actual method binding can.
        var method = typeof(TransmitImagePreparer).GetMethod(nameof(ITransmitImagePreparer.ComposePreview));

        Assert.NotNull(method);
        Assert.Equal(typeof(TransmitImagePreparer), method!.DeclaringType);
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
