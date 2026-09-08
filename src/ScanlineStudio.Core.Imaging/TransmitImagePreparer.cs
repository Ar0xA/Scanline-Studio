using System.Collections.Concurrent;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
// NOT "using SixLabors.ImageSharp.Drawing" -- SixLabors.ImageSharp.Drawing.Path collides with
// System.IO.Path (used throughout this file, e.g. the constructor's Path.Combine). Reference
// SixLabors.ImageSharp.Drawing.RectangularPolygon fully-qualified at each use site instead.
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="ITransmitImagePreparer"/> — see spec/07-image-pipeline.md's
/// "TX image editor" section. Mirrors <see cref="ImageFileLoader"/>'s/<see cref="StockImageLibrary"/>'s
/// explicit-field-copy pixel pattern (never <c>MemoryMarshal.Cast</c> between the two unrelated
/// <c>Rgb24</c> types).</summary>
public sealed class TransmitImagePreparer : ITransmitImagePreparer
{
    private readonly FontCollection _fontCollection;
    private readonly FontFamily _defaultFontFamily;

    /// <summary>Phase 4 (spec/15-template-designer.md, "small bundled set that renders identically
    /// everywhere") -- first entry is the default/fallback family, matching
    /// <see cref="ResolveFontFamily"/>'s own fallback. Barlow is vendored a SECOND time into this
    /// pipeline's own <c>assets/fonts/</c> tree (alongside its existing UI-chrome copy under
    /// <c>src/ScanlineStudio.UI/Assets/Fonts/</c>) -- this project has zero Avalonia reference and
    /// loads fonts by filesystem path, so the UI's own <c>avares://</c>-embedded copy isn't
    /// reachable here (Phase 4 plan-review correction; see LICENSES.md's Barlow entry for the
    /// dual-vendoring rationale, same precedent DejaVu Sans Mono already established).</summary>
    public IReadOnlyList<string> AvailableFontFamilies { get; }

    public TransmitImagePreparer(string? fontFilePath = null)
    {
        var path = fontFilePath ?? Path.Combine(AppContext.BaseDirectory, "assets", "fonts", "DejaVuSansMono.ttf");
        var fontDirectory = Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        _fontCollection = new FontCollection();
        _defaultFontFamily = _fontCollection.Add(path, System.Globalization.CultureInfo.InvariantCulture);
        // Bold/Italic (auditor usability review follow-up, 2026-08-18): SixLabors.Fonts groups
        // multiple Add() calls under the SAME family name into one FontFamily with several style
        // variants -- FontFamily.CreateFont(size, FontStyle) then picks whichever of these matches.
        // Without registering these, CreateFont(size, FontStyle.Bold) throws (verified via reflection
        // + a real render before committing to this design, not assumed) since only Regular was ever
        // loaded. Same license/sourcing precedent as the existing DejaVuSansMono.ttf entry
        // (LICENSES.md) -- copied from the SAME already-vetted `fonts-dejavu-mono` Debian package.
        _fontCollection.Add(Path.Combine(fontDirectory, "DejaVuSansMono-Bold.ttf"), System.Globalization.CultureInfo.InvariantCulture);
        _fontCollection.Add(Path.Combine(fontDirectory, "DejaVuSansMono-Oblique.ttf"), System.Globalization.CultureInfo.InvariantCulture);
        _fontCollection.Add(Path.Combine(fontDirectory, "DejaVuSansMono-BoldOblique.ttf"), System.Globalization.CultureInfo.InvariantCulture);

        var barlowPath = Path.Combine(fontDirectory, "Barlow", "Barlow-Regular.ttf");
        var barlowFamily = _fontCollection.Add(barlowPath, System.Globalization.CultureInfo.InvariantCulture);
        _fontCollection.Add(Path.Combine(fontDirectory, "Barlow", "Barlow-Bold.ttf"), System.Globalization.CultureInfo.InvariantCulture);
        _fontCollection.Add(Path.Combine(fontDirectory, "Barlow", "Barlow-Italic.ttf"), System.Globalization.CultureInfo.InvariantCulture);
        _fontCollection.Add(Path.Combine(fontDirectory, "Barlow", "Barlow-BoldItalic.ttf"), System.Globalization.CultureInfo.InvariantCulture);

        AvailableFontFamilies = [_defaultFontFamily.Name, barlowFamily.Name];
    }

    public IImageSource Crop(IImageSource source, NormalizedRect region)
    {
        using var image = ToImageSharp(source);
        CropInto(image, source.Width, source.Height, region);
        return FromImageSharp(image);
    }

    // T1-14: extracted so ComposePreview's own fused pipeline shares this EXACT logic rather than a
    // second, independently-maintained copy of it -- the two can't silently drift apart, since
    // there's only one implementation to drift. sourceWidth/sourceHeight are passed explicitly
    // (not read from `image`) for clarity at the call site, not because it's load-bearing today --
    // `image.Width`/`.Height` already equal them at every current call site (Mutate hasn't run yet).
    private static void CropInto(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, int sourceWidth, int sourceHeight, NormalizedRect region)
    {
        // TX workflow modernization plan, Phase 7: rounding arithmetic moved to CropGeometry.Measure
        // so the flatten command's write-back geometry shares this EXACT logic -- see that type's
        // own doc comment.
        var (x, y, width, height) = CropGeometry.Measure(sourceWidth, sourceHeight, region);
        image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
    }

    public IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect)
    {
        using var image = ToImageSharp(source);
        ResizeInto(image, width, height, preserveAspect);
        return FromImageSharp(image);
    }

    // T1-14: extracted, same reasoning as CropInto above.
    private static void ResizeInto(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, int width, int height, bool preserveAspect)
    {
        if (preserveAspect)
        {
            // Letterbox with solid black -- matches this app's raw-instrumentation aesthetic
            // direction, not a "smart" fill. ResizeMode.Pad does exactly this in one call: resize
            // to the largest size that fits within width x height preserving aspect, then pad the
            // remainder onto a width x height canvas.
            image.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Pad,
                PadColor = Color.Black,
            }));
        }
        else
        {
            // Stretch to fill exactly -- the user's own explicit ask for "stretch" as a distinct
            // option from aspect-preserving resize. Same resampler ImageFileLoader/StockImageLibrary
            // already use (ImageSharp's implicit default, Bicubic) -- named here so it's a stated
            // choice, not a repeated silent default.
            image.Mutate(ctx => ctx.Resize(width, height));
        }
    }

    /// <summary>Brightness/Contrast/Saturation map the slider's -50..50 range to ImageSharp's own
    /// 1.0-centered multiplier convention (0..2, 1.0 = identity). Gamma is hand-rolled via
    /// <c>ProcessPixelRowsAsVector4</c> -- ImageSharp 3.1.12/4.0.0 have no <c>GammaCorrection</c>
    /// operation at all (verified directly against the package's own API surface, not assumed) --
    /// applying <c>MathF.Pow</c> per RGB channel directly against the already sRGB-encoded pixel
    /// values (no linearization step: this is the GIMP/Photoshop midtone-gamma convention, not a
    /// physically-correct display-gamma one). Exponent is negated relative to the raw
    /// <c>out = in^gamma</c> formula (where an exponent above 1 DARKENS) so that positive slider
    /// values brighten, consistent with Brightness/Contrast's own sign convention. Gamma and
    /// Sharpen/Denoise are all conditionally SKIPPED at 0 rather than always invoked -- Gamma's
    /// hand-rolled per-pixel loop is real synchronous work (code-review finding: skipping it
    /// matters concretely since <c>RecomputePreview</c> calls this on every interactive
    /// pointer-move frame, and the common case once a user touches ANY slider is that most of the
    /// other five stay at 0); Sharpen/Denoise are skipped for a correctness reason instead --
    /// ImageSharp's Gaussian operations are not documented to throw at <c>sigma=0</c>, but a zero
    /// sigma collapses to a degenerate kernel (silent NaN/corrupted output, worse than a throw).
    /// Denoise runs BEFORE Sharpen (not declaration order) so sharpening doesn't amplify noise
    /// denoise was meant to remove. All non-skipped operations run in one <c>Mutate</c> chain (one
    /// pipeline pass, not N). **Precondition, found while testing**: ImageSharp's Gaussian
    /// convolution throws <see cref="ArgumentOutOfRangeException"/> when the kernel radius derived
    /// from sigma (up to ~9px at Sharpen/Denoise=100's sigma=3.0) exceeds the SOURCE image's own
    /// width/height -- not reachable via this app's real call site (only ever invoked on an
    /// already-<see cref="Resize"/>d image at a target mode's exact dimensions; the smallest real
    /// mode is 320x120), but a genuine constraint for any other caller passing a smaller
    /// image.</summary>
    public IImageSource ApplyAdjustments(IImageSource source, ImageAdjustments adjustments)
    {
        if (adjustments.IsIdentity)
        {
            return source;
        }

        using var image = ToImageSharp(source);
        ApplyAdjustmentsInto(image, adjustments);
        return FromImageSharp(image);
    }

    // T1-14: extracted, same reasoning as CropInto above. Callers (ComposePreview included) are
    // responsible for their own IsIdentity check -- this method always applies the full chain.
    private static void ApplyAdjustmentsInto(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, ImageAdjustments adjustments)
    {
        image.Mutate(ctx =>
        {
            ctx.Brightness(1.0f + (float)(adjustments.Brightness / 50.0));
            ctx.Contrast(1.0f + (float)(adjustments.Contrast / 50.0));
            ctx.Saturate(1.0f + (float)(adjustments.Saturation / 50.0));

            // Code-review finding: unlike Brightness/Contrast/Saturate (genuinely cheap ImageSharp
            // built-ins), this hand-rolled per-pixel loop is real synchronous work -- skipping it
            // at Gamma=0 (same shape as the Sharpen/Denoise guards below, not just relying on
            // MathF.Pow(x, 1f) being a mathematical identity) avoids ~950k MathF.Pow calls on every
            // interactive preview frame (RecomputePreview fires on every pointer-move) whenever
            // ANY other slider is non-zero, which is the common case once a user touches a slider
            // at all.
            if (adjustments.Gamma != 0)
            {
                var gammaExponent = MathF.Pow(10f, -(float)(adjustments.Gamma / 100.0));
                ctx.ProcessPixelRowsAsVector4(row =>
                {
                    for (var i = 0; i < row.Length; i++)
                    {
                        var pixel = row[i];
                        row[i] = new System.Numerics.Vector4(
                            MathF.Pow(pixel.X, gammaExponent),
                            MathF.Pow(pixel.Y, gammaExponent),
                            MathF.Pow(pixel.Z, gammaExponent),
                            pixel.W);
                    }
                });
            }

            // Denoise BEFORE Sharpen (code-review finding) -- conventional order, so a user
            // adjusting both doesn't have Sharpen amplify noise that Denoise was meant to remove.
            if (adjustments.Denoise > 0)
            {
                var denoiseSigma = (float)(adjustments.Denoise / 100.0) * 3.0f;
                ctx.GaussianBlur(denoiseSigma);
            }

            if (adjustments.Sharpen > 0)
            {
                var sharpenSigma = 0.3f + ((float)(adjustments.Sharpen / 100.0) * 2.7f);
                ctx.GaussianSharpen(sharpenSigma);
            }
        });
    }

    public IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay)
    {
        using var image = ToImageSharp(source);

        foreach (var element in overlay.Elements)
        {
            var fontSize = (float)(element.FontSizeRelative * source.Height);
            var origin = new PointF((float)(element.X * source.Width), (float)(element.Y * source.Height));

            // WrappingLength = source.Width (not -1/no-wrap) -- v1's own clip-at-image-width
            // overflow rule, preserved byte-for-byte through the ApplyTemplate refactor (Phase 0
            // plan-review round-2 finding: WrappingLength also drives HorizontalAlignment.Center's
            // own alignment box, so this is a real behavioral parameter, not just a wrap toggle --
            // ApplyOverlay's existing tests are the acceptance criterion that this still matches).
            // hintingMode: null -- leaves TextOptions.HintingMode at its own pre-refactor default,
            // never touched by this call site (code-review round-1 finding: this must stay
            // completely unchanged from before the refactor, unlike DrawTemplateText's call below).
            DrawGlyphs(image, element.Text, _defaultFontFamily, fontSize, origin, element.Color, wrappingLength: source.Width, clip: null, hintingMode: null);
        }

        return FromImageSharp(image);
    }

    /// <summary>See <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s own doc comment for the
    /// full contract. <c>OrderBy</c> is a documented STABLE sort (preserves original relative
    /// order among equal keys), which is what makes "ascending (Z, list index)" correct with just
    /// <c>OrderBy(e =&gt; e.Z)</c> alone -- no separate index tiebreaker needed.</summary>
    public IImageSource ApplyTemplate(IImageSource existingBase, TemplateDocument document)
    {
        if (document.IsEmpty)
        {
            return existingBase;
        }

        using var image = ToImageSharp(existingBase);
        ApplyTemplateInto(image, document);
        return FromImageSharp(image);
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- real
    /// implementation, overriding the interface's own graceful default (see that default's doc
    /// comment for the full contract: <paramref name="element"/>'s corners are in the SAME space as
    /// its own <c>Bounds</c>, need only be mutually consistent, not full-canvas-normalized). Falls
    /// back to the SAME plain (unwarped)/solid-fill behavior the interface default uses whenever
    /// <c>Perspective</c> is null or the quad turns out degenerate -- this class can't call the
    /// default implementation from inside its own override (C# has no way to reach a DIM once a
    /// class provides its own), so that small amount of fallback logic is intentionally duplicated,
    /// not shared.</summary>
    public BgraPixelBuffer RenderWarpedElementPreview(TemplateElement element, int targetWidthPx, int targetHeightPx, double? styleImageHeightPx = null)
    {
        targetWidthPx = Math.Max(1, targetWidthPx);
        targetHeightPx = Math.Max(1, targetHeightPx);

        switch (element)
        {
            case TemplateImageElement { Perspective: { } corners } image:
                return RenderWarpedImagePreview(image, corners, targetWidthPx, targetHeightPx);
            case TemplateImageElement image:
                return BgraPixelBuffer.FromOpaqueSource(Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false));
            case TemplateBoxElement { Perspective: { } corners } box:
                return RenderWarpedBoxPreview(box, corners, targetWidthPx, targetHeightPx,
                    styleImageHeightPx is > 0 && double.IsFinite(styleImageHeightPx.Value) ? styleImageHeightPx.Value : targetHeightPx);
            case TemplateBoxElement box:
                return BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx);
            default:
                return BgraPixelBuffer.FromSolidColor(new Abstractions.Imaging.Rgb24(0, 0, 0), targetWidthPx, targetHeightPx);
        }
    }

    private BgraPixelBuffer RenderWarpedImagePreview(TemplateImageElement image, PerspectiveCorners corners, int targetWidthPx, int targetHeightPx)
    {
        if (!TryComputeLocalWarpGeometry(corners, targetWidthPx, targetHeightPx, out var localCorners))
        {
            return BgraPixelBuffer.FromOpaqueSource(Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false));
        }

        using var sourceImage = ToImageSharpRgba32(image.Source);
        var matrix = SolveHomography(localCorners, sourceImage.Width, sourceImage.Height);
        if (!IsWellConditioned(matrix, sourceImage.Width, sourceImage.Height))
        {
            return BgraPixelBuffer.FromOpaqueSource(Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false));
        }

        using var warped = sourceImage.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, sourceImage.Width, sourceImage.Height), matrix, new Size(targetWidthPx, targetHeightPx), KnownResamplers.Bicubic));
        return ToPremultipliedBgra(warped);
    }

    private static BgraPixelBuffer RenderWarpedBoxPreview(TemplateBoxElement box, PerspectiveCorners corners, int targetWidthPx, int targetHeightPx, double styleImageHeightPx)
    {
        if (!TryComputeLocalWarpGeometry(corners, targetWidthPx, targetHeightPx, out var localCorners))
        {
            return BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx);
        }

        using var contentBitmap = new Image<Rgba32>(targetWidthPx, targetHeightPx);
        // Styles are relative to the full output image, converted by the caller to this local
        // bitmap's resolution; the element bounding-box height is not the style reference.
        contentBitmap.Mutate(ctx => DrawBoxContent(ctx, box, new PixelBounds(0, 0, targetWidthPx, targetHeightPx), styleImageHeightPx));

        var matrix = SolveHomography(localCorners, targetWidthPx, targetHeightPx);
        if (!IsWellConditioned(matrix, targetWidthPx, targetHeightPx))
        {
            return BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx);
        }

        using var warped = contentBitmap.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, targetWidthPx, targetHeightPx), matrix, new Size(targetWidthPx, targetHeightPx), KnownResamplers.Bicubic));
        return ToPremultipliedBgra(warped);
    }

    /// <summary>Unlike <see cref="TryComputeWarpGeometry"/> (which maps corners into a LARGER
    /// destination image's own pixel space), this element-preview render's target canvas IS exactly
    /// the corners' own bounding box -- so the bbox maps 1:1 onto <c>(0,0)-(targetWidthPx,targetHeightPx)</c>,
    /// no separate destination-image scale conversion needed. Corners can be in ANY consistent unit
    /// (normalized, crop-relative, raw pixels) since only their RELATIVE positions matter here.</summary>
    private static bool TryComputeLocalWarpGeometry(PerspectiveCorners corners, int targetWidthPx, int targetHeightPx, out PerspectiveCorners localCorners)
    {
        var bbox = corners.ToBoundingBox();
        if (!double.IsFinite(bbox.X) || !double.IsFinite(bbox.Y)
            || !double.IsFinite(bbox.Width) || !double.IsFinite(bbox.Height)
            || bbox.Width <= 0 || bbox.Height <= 0)
        {
            localCorners = default;
            return false;
        }

        var scaleX = targetWidthPx / bbox.Width;
        var scaleY = targetHeightPx / bbox.Height;
        localCorners = new PerspectiveCorners(
            (corners.Corner0X - bbox.X) * scaleX, (corners.Corner0Y - bbox.Y) * scaleY,
            (corners.Corner1X - bbox.X) * scaleX, (corners.Corner1Y - bbox.Y) * scaleY,
            (corners.Corner2X - bbox.X) * scaleX, (corners.Corner2Y - bbox.Y) * scaleY,
            (corners.Corner3X - bbox.X) * scaleX, (corners.Corner3Y - bbox.Y) * scaleY);
        // The shared edge-length floor is in pixels, so apply it after mapping out of the
        // caller's arbitrary (often normalized) coordinate space.
        return localCorners.IsConvexAndWellFormed();
    }

    /// <summary>ImageSharp's own <see cref="Rgba32"/> is STRAIGHT (unpremultiplied) alpha;
    /// <see cref="BgraPixelBuffer"/>'s own contract requires PREMULTIPLIED -- verified empirically
    /// (not assumed) that a freshly-<c>Clone()</c>d destination canvas starts zero-initialized
    /// (0,0,0,0), so pixels the warp never touches (outside the quad, inside the target rectangle)
    /// correctly come out fully transparent with no extra fill step needed.</summary>
    private static BgraPixelBuffer ToPremultipliedBgra(Image<Rgba32> image)
    {
        var pixels = new byte[image.Width * image.Height * 4];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var rowOffset = y * image.Width * 4;
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = row[x];
                    var offset = rowOffset + (x * 4);
                    var alpha = pixel.A / 255f;
                    pixels[offset] = (byte)MathF.Round(pixel.B * alpha);
                    pixels[offset + 1] = (byte)MathF.Round(pixel.G * alpha);
                    pixels[offset + 2] = (byte)MathF.Round(pixel.R * alpha);
                    pixels[offset + 3] = pixel.A;
                }
            }
        });

        return new BgraPixelBuffer { Pixels = pixels, Width = image.Width, Height = image.Height };
    }

    // T1-14: extracted, same reasoning as CropInto above. Reads image.Width/image.Height (not a
    // separately-passed existingBase.Width/.Height) -- always equivalent since Resize's own
    // contract guarantees `image` is always EXACTLY the target mode's dimensions by the time
    // ComposePreview reaches this stage (ITransmitImagePreparer.Resize's own doc comment: "Always
    // returns EXACTLY width x height").
    private void ApplyTemplateInto(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateDocument document)
    {
        foreach (var element in document.Elements.OrderBy(e => e.Z))
        {
            if (element.Bounds.Width <= 0 || element.Bounds.Height <= 0)
            {
                continue;
            }

            var bounds = ToPixelBounds(element.Bounds, image.Width, image.Height);

            switch (element)
            {
                case TemplateTextElement text:
                    DrawTemplateText(image, text, bounds, image.Height);
                    break;
                case TemplateImageElement img:
                    DrawTemplateImage(image, img, bounds);
                    break;
                case TemplateBoxElement box:
                    DrawTemplateBox(image, box, bounds, image.Height);
                    break;
                case TemplateLineElement line:
                    DrawTemplateLine(image, line, image.Height);
                    break;
                default:
                    // TemplateElement is a public abstract record -- an unrecognized subtype means
                    // this switch fell out of sync with the real hierarchy. Throw, don't silently
                    // skip (code-review round-1 finding): a silently-dropped element is a much
                    // harder bug to notice than a build/test failure right here.
                    throw new NotSupportedException($"Unrecognized {nameof(TemplateElement)} subtype: {element.GetType()}.");
            }
        }
    }

    /// <summary>T1-14 (production_audit.md): the real, fused override of
    /// <see cref="ITransmitImagePreparer.ComposePreview"/>'s own default 4-call-chain implementation
    /// -- converts to <c>Image&lt;Rgb24&gt;</c> exactly ONCE, runs every stage's own Mutate/draw logic
    /// (the SAME <c>*Into</c> helpers <see cref="Crop"/>/<see cref="Resize"/>/
    /// <see cref="ApplyAdjustments"/>/<see cref="ApplyTemplate"/> themselves call, so there is only
    /// ONE copy of each stage's own logic, not a second implementation that could drift from it) on
    /// that single instance, then converts back exactly once -- instead of once per stage. Must stay
    /// pixel-identical to the interface's own default implementation (see that method's own doc
    /// comment) and to <c>TxImageEditorPaneViewModel.BuildFinalOutput</c>'s own separate, un-fused
    /// chain -- pinned by this project's own pixel-exact equivalence tests, not just asserted
    /// here.</summary>
    public IImageSource ComposePreview(
        IImageSource source, NormalizedRect cropRegion, int targetWidth, int targetHeight, bool preserveAspect,
        ImageAdjustments adjustments, TemplateDocument templateDocument)
    {
        using var image = ToImageSharp(source);

        CropInto(image, source.Width, source.Height, cropRegion);
        ResizeInto(image, targetWidth, targetHeight, preserveAspect);

        if (!adjustments.IsIdentity)
        {
            ApplyAdjustmentsInto(image, adjustments);
        }

        if (!templateDocument.IsEmpty)
        {
            ApplyTemplateInto(image, templateDocument);
        }

        return FromImageSharp(image);
    }

    public double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0)
    {
        var fontFamily = ResolveFontFamily(font.Family);
        var startingSizePx = MathF.Max((float)(font.Size * imageHeightPx), MinFontSizePx);
        var strokeThicknessPx = (float)(strokeThicknessRelative * imageHeightPx);
        var shadowOffsetXPx = (float)(shadowOffsetXRelative * imageHeightPx);
        var shadowOffsetYPx = (float)(shadowOffsetYRelative * imageHeightPx);
        var stackStepXPx = (float)(stackStepXRelative * imageHeightPx);
        var stackStepYPx = (float)(stackStepYRelative * imageHeightPx);
        var (boundedWidth, boundedHeight) = ShrinkFitBoxForEffects(
            boundsWidthPx, boundsHeightPx, strokeThicknessPx, shadowOffsetXPx, shadowOffsetYPx, rotationDegrees, stackStepXPx, stackStepYPx);
        return ComputeFittedFontSizePx(text, fontFamily, startingSizePx, MinFontSizePx, boundedWidth, boundedHeight, ToFontStyle(font));
    }

    /// <summary>Auditor usability review follow-up (2026-08-18): <see cref="FontSpec.Bold"/>/
    /// <see cref="FontSpec.Italic"/> -&gt; <see cref="SixLabors.Fonts.FontStyle"/>, shared by every
    /// call site that needs to pick a font VARIANT (not just size) -- one conversion, not several
    /// independently-maintained copies of the same 4-way mapping.</summary>
    private static SixLabors.Fonts.FontStyle ToFontStyle(FontSpec font) => (font.Bold, font.Italic) switch
    {
        (true, true) => SixLabors.Fonts.FontStyle.BoldItalic,
        (true, false) => SixLabors.Fonts.FontStyle.Bold,
        (false, true) => SixLabors.Fonts.FontStyle.Italic,
        (false, false) => SixLabors.Fonts.FontStyle.Regular,
    };

    /// <summary>[Code-review nit, fixed here] This doc comment used to sit (misfiled) directly above
    /// <see cref="ShrinkFitBoxForEffects"/> instead of here, on the actual method it describes --
    /// moved down when Phase 8 renamed/extended that method and gave it its own, separate doc
    /// comment. Shared by <see cref="MeasureFittedFontSize"/> (canvas-side) and
    /// <see cref="DrawTemplateText"/> (real pipeline) -- Phase 4 plan-review blocker: these two
    /// MUST apply the identical stroke-allowance correction or the canvas silently desyncs from
    /// the transmitted image (exactly the failure mode <see cref="MeasureFittedFontSize"/> exists
    /// to prevent in general). Extracted into one method, not two independently-maintained copies
    /// of the same arithmetic, specifically so they cannot drift apart.
    /// <para>Why the shrink is needed: a <see cref="SixLabors.ImageSharp.Drawing.Processing.Pen"/>-
    /// stroked glyph grows ink by <paramref name="strokeThicknessPx"/>/2 per side past whatever
    /// <c>TextMeasurer.MeasureSize</c> reports for the fill alone -- without this correction the
    /// fit search can pick a font size whose stroke ink extends past <c>Bounds</c>. In practice
    /// that ink gets silently CLIPPED at the render-time clip rect (confirmed empirically: a
    /// direct pixel dump showed zero stroke pixels ever land outside <c>Bounds</c> with or without
    /// this correction, since <see cref="DrawTemplateText"/>'s clip is unconditional) rather than
    /// visibly bleeding into the surrounding image -- so the observable defect is an
    /// asymmetrically-clipped/incomplete-looking outline, not out-of-bounds pixels. This method's
    /// own effect is verified directly (the returned FITTED SIZE shrinks with a stroke present),
    /// not via a pixel-bleed assertion that the clip makes structurally unable to fail.</para>
    /// <para>[Code-review correction, applies to the sibling <see cref="ShrinkFitBoxForShadow"/> too]
    /// This symmetric-centered-Pen reasoning is SPECIFIC to stroke -- it does NOT generalize to the
    /// shadow case, where the copy is TRANSLATED rather than symmetrically grown, and needs DOUBLE
    /// the offset shrunk on each axis instead (see that method's own doc comment for the real,
    /// code-review-caught bug this distinction fixed).</para></summary>
    private static (int Width, int Height) ShrinkFitBoxForStroke(int boundsWidthPx, int boundsHeightPx, float strokeThicknessPx)
    {
        // Code-review finding: a negative strokeThicknessPx (nothing upstream validates the
        // TextBox-bound StrokeThickness VM property) would GROW the fit box instead of shrinking
        // it -- ComputeFittedFontSizePx then picks a size larger than Bounds, DrawGlyphs's own
        // `strokeThicknessPx > 0` gate means no outline gets drawn to fill that extra allowance
        // either, and the result is silently hard-clipped, oversized transmitted text with no
        // outline. Clamped here (the one shared call path both sides use) rather than at each of
        // the VM/AXAML boundary, so this can't regress if a future caller reaches this method a
        // different way.
        var strokeThicknessPxRounded = Math.Max(0, (int)MathF.Round(strokeThicknessPx));
        return (Math.Max(1, boundsWidthPx - strokeThicknessPxRounded), Math.Max(1, boundsHeightPx - strokeThicknessPxRounded));
    }

    /// <summary>Phase 8: renamed from <c>ShrinkFitBoxForStroke</c> and extended -- shadow and
    /// rotation are the SECOND and THIRD occurrences of the identical "an effect pushes ink past
    /// what a plain-text fit search measures" problem stroke already solved (see this method's own
    /// callers' doc comments), so all three shrinks are applied here, in sequence, rather than as
    /// three independently-maintained copies of the same shape of correction.
    ///
    /// <b>Rotation MUST run first, on the raw (unshrunk) bounds</b> (Tier B functional-audit
    /// finding, correcting an earlier version of this method that ran rotation LAST, after
    /// stroke/shadow/stack): rotation's own <c>k</c> factor (<see cref="ShrinkFitBoxForRotation"/>)
    /// gives the largest SAME-ASPECT-RATIO box whose rotated AABB fits the original bounds -- but
    /// the actual pre-rotation ink is the fit box PLUS the effect allowances drawn around it
    /// (stroke/shadow/stack are translated/thickened copies of the fit-sized glyph run, not
    /// additional shrink of it). Computing <c>k</c> from an ALREADY-effect-shrunk box and then
    /// adding the effects back on top can make the real pre-rotation ink bigger than <c>k</c> was
    /// derived for, under-shrinking the final fit box relative to what rotation actually needs --
    /// reachable and real: a wide, short box with a large shadow offset and a 90° rotation could
    /// hard-clip roughly half the glyph run under the old order. Running rotation first makes this
    /// exact rather than approximate: with <c>(rotW, rotH) = k*(boundsWidth, boundsHeight)</c>
    /// computed from the RAW bounds, then stroke/shadow/stack subtracted from THAT, the resulting
    /// fit box plus every effect allowance sums back to exactly <c>k*(boundsWidth, boundsHeight)</c>
    /// -- precisely the size <c>k</c> was derived to keep inside the original bounds when rotated.
    /// Each shrink still only ever makes the box smaller, so the result stays safe (no bleed past
    /// the clip) regardless of order -- this reordering is about TIGHTNESS (not hard-clipping valid
    /// content), not safety. Stroke-then-shadow-then-stack (post-rotation) mirrors the order effects
    /// are actually drawn in (see <see cref="DrawGlyphs"/>'s own doc comment).</summary>
    private static (int Width, int Height) ShrinkFitBoxForEffects(
        int boundsWidthPx, int boundsHeightPx, float strokeThicknessPx, float shadowOffsetXPx, float shadowOffsetYPx, double rotationDegrees,
        float stackStepXPx = 0, float stackStepYPx = 0)
    {
        var (rotatedW, rotatedH) = ShrinkFitBoxForRotation(boundsWidthPx, boundsHeightPx, rotationDegrees);
        var (strokeW, strokeH) = ShrinkFitBoxForStroke(rotatedW, rotatedH, strokeThicknessPx);
        var (shadowW, shadowH) = ShrinkFitBoxForShadow(strokeW, strokeH, shadowOffsetXPx, shadowOffsetYPx);
        // Stack's farthest copy sits at the full (stackStepXPx, stackStepYPx) offset from center --
        // same translated-copy shape of growth as the shadow offset above, so it reuses the identical
        // "double the offset" shrink math (ShrinkFitBoxForShadow's own doc comment).
        return ShrinkFitBoxForShadow(shadowW, shadowH, stackStepXPx, stackStepYPx);
    }

    /// <summary>[Code-review fix] A shadow offset by (dx,dy) pushes the SHADOW copy's own ink that
    /// far past the FILL glyph's own footprint -- but the glyph run is drawn CENTER-anchored (origin
    /// = <c>Bounds</c>'s own center), so the fill already occupies the full available half-width on
    /// EACH side of center. The shadow copy is the SAME size, just translated by <c>dx</c>: it spans
    /// <c>[cx - a/2 + dx, cx + a/2 + dx]</c> against a fill spanning <c>[cx - a/2, cx + a/2]</c>. For
    /// the shadow's own far edge to stay within <c>Bounds</c> (half-width <c>W/2</c> from center),
    /// the fit box must satisfy <c>a/2 + dx &lt;= W/2</c>, i.e. <c>a &lt;= W - 2*dx</c> -- DOUBLE the
    /// offset, not the offset itself. An earlier version of this method shrank by just <c>dx</c>/
    /// <c>dy</c> (correct for the stroke case above, where a <see cref="Pen"/>'s SYMMETRIC centered
    /// stroke grows ink by the same amount on every side regardless of position -- the shadow's
    /// asymmetric TRANSLATION is a fundamentally different shape of growth, which the original
    /// comment here missed) -- a real code-review-caught bug: the shadow's far edge silently bled
    /// past <c>Bounds</c> by up to <c>dx/2</c>/<c>dy/2</c>, invisible in the shipped regression test
    /// because the unconditional clip in <see cref="DrawTemplateText"/> eats it (same "clip makes
    /// bleed structurally undetectable by a pixel-outside-Bounds assertion alone" caveat already
    /// documented for the stroke case). Negative offsets still clamp via <c>Abs</c> first (an offset
    /// pushes OUTWARD on one side; the sign only picks which side, magnitude is what matters for the
    /// shrink), same defensive-clamp shape as the stroke correction above.</summary>
    private static (int Width, int Height) ShrinkFitBoxForShadow(int boundsWidthPx, int boundsHeightPx, float shadowOffsetXPx, float shadowOffsetYPx)
    {
        var dx = Math.Max(0, (int)MathF.Round(MathF.Abs(shadowOffsetXPx)));
        var dy = Math.Max(0, (int)MathF.Round(MathF.Abs(shadowOffsetYPx)));
        return (Math.Max(1, boundsWidthPx - (2 * dx)), Math.Max(1, boundsHeightPx - (2 * dy)));
    }

    /// <summary>Rotation-vs-clip policy (Phase 8 plan-review blocker): shrink the fit box, don't
    /// widen the clip -- <see cref="DrawTemplateText"/>'s clip stays exactly <c>Bounds</c> for every
    /// effect, no per-effect exceptions. A box of size (w,h) rotated by <paramref name="rotationDegrees"/>
    /// has an axis-aligned bounding box of <c>(w*c + h*s, w*s + h*c)</c> where <c>c</c>/<c>s</c> are
    /// <c>|cos|</c>/<c>|sin|</c> of the angle. Solving for the largest (w,h) -- constrained to the
    /// SAME aspect ratio as the original <paramref name="boundsWidthPx"/>/<paramref name="boundsHeightPx"/>,
    /// i.e. <c>(w,h) = k*(boundsWidthPx, boundsHeightPx)</c> for some scalar <c>k</c> -- whose rotated
    /// bbox fits within the ORIGINAL bounds on both axes simultaneously gives
    /// <c>k = min(boundsWidthPx / (boundsWidthPx*c + boundsHeightPx*s), boundsHeightPx / (boundsWidthPx*s + boundsHeightPx*c))</c>.
    /// This is a genuinely conservative (not tight) bound for a non-square box at large angles --
    /// verified correct by direct substitution, not just derived on paper, and pinned by
    /// <c>ApplyTemplate_RotatedText_NoInkOutsideBounds</c> (written first, per the plan's own
    /// discipline for this exact class of ImageSharp/geometry surprise). At 0° (<c>c=1,s=0</c>),
    /// <c>k=1</c> exactly -- no shrink, so this is a total no-op for every element that doesn't use
    /// rotation, including every existing test.</summary>
    private static (int Width, int Height) ShrinkFitBoxForRotation(int boundsWidthPx, int boundsHeightPx, double rotationDegrees)
    {
        // [Code-review nit, fixed here] Nothing upstream validates the AXAML TextBox-bound
        // RotationDegrees property (same gap StrokeThickness's own defensive clamp above already
        // exists to close) -- a NaN/Infinity value would otherwise propagate through Cos/Sin/Min into
        // an undefined (int)Math.Floor(NaN) cast on every preview frame. Treat as "no rotation" (the
        // same safe default 0 already produces via k=1 below), not a thrown exception -- this is
        // canvas-preview/pipeline code running on every frame, not a validation boundary.
        if (double.IsNaN(rotationDegrees) || double.IsInfinity(rotationDegrees))
        {
            return (boundsWidthPx, boundsHeightPx);
        }

        var radians = rotationDegrees * Math.PI / 180.0;
        var c = Math.Abs(Math.Cos(radians));
        var s = Math.Abs(Math.Sin(radians));
        var w = (double)boundsWidthPx;
        var h = (double)boundsHeightPx;

        var widthDenominator = (w * c) + (h * s);
        var heightDenominator = (w * s) + (h * c);
        var k1 = widthDenominator > 0 ? w / widthDenominator : 1.0;
        var k2 = heightDenominator > 0 ? h / heightDenominator : 1.0;
        var k = Math.Min(1.0, Math.Min(k1, k2));

        return (Math.Max(1, (int)Math.Floor(w * k)), Math.Max(1, (int)Math.Floor(h * k)));
    }

    /// <summary>Phase 8: extended for shadow/rotation/gradient. Rotation forks into a genuinely
    /// separate rendering path (an offscreen sub-bitmap render→rotate→composite, since there is no
    /// GDI-style native rotated-glyph-run primitive here) -- everything else (shadow, stroke,
    /// gradient) is handled uniformly by <see cref="DrawGlyphs{TPixel}"/> regardless of which
    /// surface it's drawing onto.</summary>
    private void DrawTemplateText(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateTextElement element, PixelBounds bounds, int imageHeightPx)
    {
        var fontFamily = ResolveFontFamily(element.Font.Family);
        var startingSizePx = MathF.Max((float)(element.Font.Size * imageHeightPx), MinFontSizePx);
        var boundsWidthPx = Math.Max(1, (int)MathF.Round(bounds.Width));
        var boundsHeightPx = Math.Max(1, (int)MathF.Round(bounds.Height));

        var strokeThicknessPx = element.StrokeColor is { } ? (float)(element.StrokeThickness * imageHeightPx) : 0f;
        var shadowOffsetXPx = element.ShadowColor is { } ? (float)(element.ShadowOffsetX * imageHeightPx) : 0f;
        var shadowOffsetYPx = element.ShadowColor is { } ? (float)(element.ShadowOffsetY * imageHeightPx) : 0f;
        var stackStepXPx = element.StackColor is { } ? (float)(element.StackStepX * imageHeightPx) : 0f;
        var stackStepYPx = element.StackColor is { } ? (float)(element.StackStepY * imageHeightPx) : 0f;
        var (fitWidthPx, fitHeightPx) = ShrinkFitBoxForEffects(
            boundsWidthPx, boundsHeightPx, strokeThicknessPx, shadowOffsetXPx, shadowOffsetYPx, element.RotationDegrees, stackStepXPx, stackStepYPx);
        var fontStyle = ToFontStyle(element.Font);
        var fittedSizePx = ComputeFittedFontSizePx(element.Content, fontFamily, startingSizePx, MinFontSizePx, fitWidthPx, fitHeightPx, fontStyle);

        // [Code-review nits, fixed here] `% 360 == 0` rather than `== 0` -- 360/720/etc. are visually
        // identical to no rotation but previously took the resampling sub-bitmap path anyway (a real,
        // if minor, quality/perf gap: an extra resample blurs the text slightly for no visual gain).
        // C#'s `%` on a negative operand can return a negative zero (e.g. -360 % 360 == -0.0), which
        // compares equal to 0 under IEEE 754, so this also correctly treats negative full-turn values
        // as unrotated. NaN/Infinity (nothing upstream validates the AXAML TextBox-bound property)
        // also route through the unrotated path here -- ctx.Rotate(float.NaN) has no defined,
        // safe-to-assume behavior, unlike ShrinkFitBoxForRotation's own NaN guard, which only had to
        // pick a safe FIT SIZE, not actually perform a transform.
        var isUnrotated = element.RotationDegrees % 360 == 0 || double.IsNaN(element.RotationDegrees) || double.IsInfinity(element.RotationDegrees);

        // Gradient coordinates are derived from wherever the glyph run is ACTUALLY drawn -- the main
        // image's own Bounds when unrotated, or the sub-bitmap's own LOCAL (0,0,boundsWidthPx,
        // boundsHeightPx) space when rotated, so the gradient rotates WITH the text (Phase 8 plan's
        // own stated decision, not left implicit).
        var gradientBounds = isUnrotated ? bounds : new PixelBounds(0, 0, boundsWidthPx, boundsHeightPx);

        // Picture fill (TX editor gap-items plan, item 4b) -- takes priority over Gradient when both
        // are somehow set (deserialized hand-edited template, see TemplateTextElement.BitmapFill's
        // own doc comment for why this precedence is stated once and enforced at every composition
        // site, not left to whichever field a caller happens to check first). ImageBrush is a
        // TEXTURE brush (tiles from an offset), not a stretch-to-region brush -- resizing the source
        // to EXACTLY boundsWidthPx/boundsHeightPx first (same GetOrCreateResizedImage cache
        // DrawTemplateImage already uses, so this doesn't re-resize every frame) means exactly one
        // tile covers the whole gradientBounds when offset to its own origin, achieving the intended
        // stretch-to-fill without needing a different brush type.
        using var bitmapFillImage = element.BitmapFill is { } bitmapSource
            ? ToImageSharp(GetOrCreateResizedImage(bitmapSource, boundsWidthPx, boundsHeightPx, ImageFitMode.Stretch))
            : null;
        Brush? fillBrush = bitmapFillImage is not null
            ? new ImageBrush(bitmapFillImage, new Point((int)MathF.Round(gradientBounds.X), (int)MathF.Round(gradientBounds.Y)))
            : element.Gradient is { } gradient
                ? BuildGradientBrush(gradient, gradientBounds, element.Color)
                : element.StrokeColor is { } ? Brushes.Solid(ToRgba32(element.Color)) : null;

        if (isUnrotated)
        {
            var origin = new PointF(bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));

            // Render-time overflow policy (Phase 0 plan-review blocker 4): the fit search can bottom
            // out at MinFontSizePx and the text STILL not fit Bounds -- clip to Bounds rather than
            // overflow or ellipsize. Clipping unconditionally (not just in the overflow case) is both
            // simpler and correct: when text already fits, nothing is outside the clip region, so it's
            // a no-op. Deliberately still exactly Bounds (not widened for stroke/shadow) -- the fit-box
            // shrink above is what keeps their ink inside this same clip rect.
            var clip = new SixLabors.ImageSharp.Drawing.RectangularPolygon(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            // hintingMode: HintingMode.None -- code-review round-1 finding: must match
            // ComputeFittedFontSizePx's own measurement HintingMode exactly, or the fitted size this
            // method just computed can render slightly larger/smaller than what was actually measured
            // (hinting's grid-fitting quantization), silently reintroducing the overflow this whole
            // mechanism exists to prevent.
            DrawGlyphs(
                image, element.Content, fontFamily, fittedSizePx, origin, element.Color, wrappingLength: -1f, clip, hintingMode: HintingMode.None,
                element.StrokeColor, strokeThicknessPx, element.ShadowColor, shadowOffsetXPx, shadowOffsetYPx, fillBrush, fontStyle,
                element.StackColor, stackStepXPx, stackStepYPx);
            return;
        }

        // Rotation path (Phase 8): render onto an offscreen, transparent Rgba32 sub-bitmap sized to
        // the ORIGINAL (unshrunk) bounds -- the fit search above already guarantees the fitted glyph
        // run's own ink, once rotated, stays within Bounds (see ShrinkFitBoxForRotation's own doc
        // comment); the sub-bitmap canvas itself just needs to be big enough to hold that ink before
        // rotation, with room to spare -- any transparent margin around it is a no-op once composited
        // back (alpha=0 blends to nothing). MUST be Rgba32, not Rgb24 -- an opaque sub-bitmap would
        // composite a visible black box around the rotated text instead of a transparent one.
        using var subBitmap = new Image<Rgba32>(boundsWidthPx, boundsHeightPx);
        var subOrigin = new PointF(boundsWidthPx / 2f, boundsHeightPx / 2f);
        DrawGlyphs(
            subBitmap, element.Content, fontFamily, fittedSizePx, subOrigin, element.Color, wrappingLength: -1f, clip: null, hintingMode: HintingMode.None,
            element.StrokeColor, strokeThicknessPx, element.ShadowColor, shadowOffsetXPx, shadowOffsetYPx, fillBrush, fontStyle,
            element.StackColor, stackStepXPx, stackStepYPx);

        subBitmap.Mutate(ctx => ctx.Rotate((float)element.RotationDegrees));

        // Composite point recomputed from the ROTATED (post-transform) size, not the original --
        // ImageSharp's Rotate auto-expands the canvas to the rotated content's own bounding box, so
        // re-centering on the NEW size is what keeps the visual center anchored at Bounds' own
        // center. This is also what keeps a later Tier 3 swap from Rotate to the full
        // ProjectiveTransformBuilder.Transform a one-line change instead of a rework (Phase 8 plan's
        // own note). The outer Clip is a belt-and-suspenders safety net, not load-bearing given the
        // fit-box-shrink math above -- but it's what keeps "Bounds is the one, single clip rect
        // everywhere in this pipeline" literally true with no per-effect exception, cheap when
        // already correct, and a real backstop if the math has an edge case this session didn't find.
        var centerX = bounds.X + (bounds.Width / 2f);
        var centerY = bounds.Y + (bounds.Height / 2f);
        var compositeLocation = new Point(
            (int)MathF.Round(centerX - (subBitmap.Width / 2f)),
            (int)MathF.Round(centerY - (subBitmap.Height / 2f)));
        var boundsClip = new SixLabors.ImageSharp.Drawing.RectangularPolygon(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        image.Mutate(ctx => ctx.Clip(boundsClip, innerCtx => innerCtx.DrawImage(subBitmap, compositeLocation, 1f)));
    }

    /// <summary>Phase 8: <paramref name="bounds"/> is destination-image-space when drawing directly
    /// onto the main image, or sub-bitmap-LOCAL space when called from the rotation path -- either
    /// way it's "wherever the glyph run is actually being drawn," which is exactly what an ImageSharp
    /// gradient brush's own absolute control points need (confirmed: neither <see cref="LinearGradientBrush"/>
    /// nor <see cref="RadialGradientBrush"/> has a glyph/bounds-relative mode). Empty
    /// <see cref="TextGradient.Stops"/> falls back to a single stop at <paramref name="fallbackColor"/>
    /// (the element's own plain <see cref="TemplateTextElement.Color"/>) -- a gradient with nothing
    /// configured yet is a degenerate one-color gradient, not an error.</summary>
    private static Brush BuildGradientBrush(TextGradient gradient, PixelBounds bounds, Abstractions.Imaging.Rgb24 fallbackColor)
    {
        var stops = gradient.Stops.Count > 0
            ? gradient.Stops.Select(s => new ColorStop(s.Offset, ToRgba32(s.Color))).ToArray()
            : [new ColorStop(0f, ToRgba32(fallbackColor))];

        var centerX = bounds.X + (bounds.Width / 2f);
        var centerY = bounds.Y + (bounds.Height / 2f);

        return gradient.Kind switch
        {
            TextGradientKind.Horizontal => new LinearGradientBrush(
                new PointF(bounds.X, centerY), new PointF(bounds.X + bounds.Width, centerY), GradientRepetitionMode.None, stops),
            TextGradientKind.Vertical => new LinearGradientBrush(
                new PointF(centerX, bounds.Y), new PointF(centerX, bounds.Y + bounds.Height), GradientRepetitionMode.None, stops),
            TextGradientKind.Radial => new RadialGradientBrush(
                new PointF(centerX, centerY), MathF.Max(bounds.Width, bounds.Height) / 2f, GradientRepetitionMode.None, stops),
            // Auditor usability review follow-up (2026-08-18): reuses the SAME 2 stops
            // Horizontal/Vertical/Radial already resolved above as the pattern's fore/back colors --
            // no separate color pair, see TextGradientKind.BitmapPattern's own doc comment for why.
            // A single-stop (degenerate/fallback) gradient makes stops[0] and stops[^1] the SAME
            // element, which correctly reads as a flat single-color "pattern," not a crash.
            TextGradientKind.BitmapPattern => Brushes.Percent20(stops[0].Color, stops[^1].Color),
            _ => throw new NotSupportedException($"Unrecognized {nameof(TextGradientKind)}: {gradient.Kind}."),
        };
    }

    private static Rgba32 ToRgba32(Abstractions.Imaging.Rgb24 color) => new(color.R, color.G, color.B, 255);

    // Safety ceiling for DrawTemplateImage's element resize -- see that method's own comment.
    // Destination-RELATIVE (round-2 audit finding correcting an earlier flat-4096px version of
    // this comment's own claim of "only pathological Bounds ever hits this"): a flat constant ties
    // worst-case memory to a fixed pixel count regardless of how small the actual destination is,
    // so a routine ~5-8x oversized element on a typical SSTV-mode-sized canvas (320-640px) was
    // already reaching a meaningful fraction of a flat 4096px ceiling -- tying the ceiling to the
    // destination's own size instead means "how oversized is genuinely reasonable" scales with what
    // this specific canvas actually needs, while MaxElementResizeDimensionPxCeiling still bounds
    // the absolute worst case (a tiny destination with an astronomically oversized element).
    private const float MaxElementResizeDestinationMultiplier = 8f;

    // TX workflow modernization plan, Phase 7: moved to Abstractions.Imaging.TransmitImageLimits so
    // the flatten command's bake-scale computation (ScanlineStudio.UI, which cannot reference this
    // project) reads the SAME ceiling rather than a second, driftable copy.
    private const float MaxElementResizeDimensionPxCeiling = (float)TransmitImageLimits.MaxElementResizeDimensionPx;

    private void DrawTemplateImage(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateImageElement element, PixelBounds bounds)
    {
        // Defensive floor at 1px -- element.Bounds.Width/Height > 0 is already guaranteed by the
        // caller's skip check, but rounding a very thin bounds rect to pixels can still floor to 0.
        //
        // Tier B functional-audit blocker, fixed here: this used to clamp targetWidth/targetHeight
        // INDEPENDENTLY to the destination image's own Width/Height. DrawImage below has no
        // scale-to-rect overload -- it composites at native size -- so clamping the resize TARGET
        // size silently clamped the element's RENDERED SCALE too, and clamping each axis separately
        // also silently distorted its ASPECT RATIO whenever only one axis overflowed. A full-frame
        // element ("set as background") combined with a smaller active crop rect
        // (TxImageEditorPaneViewModel.ProjectRectToCropRelative) routinely projects Bounds several
        // times larger than the destination -- a completely normal, reachable case, not a
        // pathological one -- and the old clamp squashed it down to 1x scale (or, with independent
        // per-axis clamping, a squashed/stretched wrong aspect), sometimes moving it fully off-canvas
        // (nothing rendered, no diagnostic) when Bounds.X/Y also went negative.
        //
        // DrawImage naturally clips to the destination canvas (standard image-compositing behavior,
        // same as every other element type in this file relies on for a partially-off-canvas Bounds)
        // -- so resizing to the FULL correct, unclamped size and letting DrawImage clip produces
        // exactly correct scale and aspect for any oversized element, with no manual crop/intersect
        // math needed. Only a SINGLE, UNIFORM (same factor on both axes, so aspect is always
        // preserved even here) safety ceiling remains, to bound worst-case allocation for a truly
        // oversized Bounds value -- see GetOrCreateResizedImage's own pixel-budget cache-skip for
        // the other half of this: even a resize under this ceiling must not permanently occupy a
        // slot in the 64-entry cache if it's large enough to matter for total retained memory.
        var rawWidthPx = MathF.Max(1f, MathF.Round(bounds.Width));
        var rawHeightPx = MathF.Max(1f, MathF.Round(bounds.Height));
        var ceiling = MathF.Min(MaxElementResizeDimensionPxCeiling, MaxElementResizeDestinationMultiplier * MathF.Max(image.Width, image.Height));
        var safetyScale = MathF.Min(1f, ceiling / MathF.Max(rawWidthPx, rawHeightPx));
        var targetWidth = Math.Max(1, (int)MathF.Round(rawWidthPx * safetyScale));
        var targetHeight = Math.Max(1, (int)MathF.Round(rawHeightPx * safetyScale));

        // TX editor gap-items plan, item 3 (perspective transform) -- takes priority over the plain
        // axis-aligned render below when set. A warp always stretches the FULL source to exactly
        // fill the quad, ignoring element.Fit (v1 scope decision: "warp to fill" has no obvious
        // Contain/Cover equivalent for a non-axis-aligned quad -- matches how corner-drag/free-
        // transform tools in other image editors already behave).
        if (element.Perspective is { } corners && TryWarpElementContent(image, element.Source, corners, ceiling))
        {
            return;
        }

        var resized = GetOrCreateResizedImage(element.Source, targetWidth, targetHeight, element.Fit);

        using var resizedImage = ToImageSharp(resized);
        var location = new Point((int)MathF.Round(bounds.X), (int)MathF.Round(bounds.Y));
        image.Mutate(ctx => ctx.DrawImage(resizedImage, location, 1f));
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- warps
    /// <paramref name="source"/> to fill <paramref name="corners"/> (destination-image-NORMALIZED
    /// space, same convention as <see cref="TemplateElement.Bounds"/>) and composites it onto
    /// <paramref name="image"/>. Returns false (does nothing, caller falls back to the plain
    /// axis-aligned render) when the quad is degenerate -- a hand-edited/imported template can carry
    /// self-intersecting or near-collinear corners the UI's own real-time drag clamp never validated;
    /// this must degrade gracefully, never throw or propagate NaN/Inf pixels, same convention as
    /// every other decorative/edge-case render path this session's own features already established
    /// (picture-fill's missing-asset handling, etc.).
    /// <para>Offscreen sub-bitmap is <see cref="Rgba32"/> (not <see cref="Rgb24"/>), matching
    /// <c>DrawTemplateText</c>'s own rotation-path precedent exactly -- transparent outside the
    /// mapped quad, so the composite below only paints inside it, same "render effects to an
    /// offscreen transparent sub-bitmap, then composite" shape.</para></summary>
    private static bool TryWarpElementContent(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, IImageSource source, PerspectiveCorners corners, float destinationSizeCeiling)
    {
        if (!TryComputeWarpGeometry(corners, image.Width, image.Height, destinationSizeCeiling, out var bboxPx, out var subBitmapWidth, out var subBitmapHeight, out var localCorners))
        {
            return false;
        }

        using var sourceImage = ToImageSharpRgba32(source);
        var matrix = SolveHomography(localCorners, sourceImage.Width, sourceImage.Height);
        if (!IsWellConditioned(matrix, sourceImage.Width, sourceImage.Height))
        {
            return false;
        }

        using var subBitmap = sourceImage.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, sourceImage.Width, sourceImage.Height), matrix, new Size(subBitmapWidth, subBitmapHeight), KnownResamplers.Bicubic));

        var location = new Point((int)MathF.Round(bboxPx.X), (int)MathF.Round(bboxPx.Y));
        image.Mutate(ctx => ctx.DrawImage(subBitmap, location, 1f));
        return true;
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- box elements have no
    /// natural source image to warp, so this draws the SAME fill/border/corner-radius/gradient
    /// content <see cref="DrawBoxContent"/> already renders directly, into an offscreen sub-bitmap
    /// sized to the corners' own (safety-clamped) bounding box, then warps THAT via the shared
    /// homography path -- "what the box would look like unwarped, at its own bbox size" is the
    /// natural pre-warp content, consistent with how turning <c>PerspectiveEnabled</c> ON seeds the 4
    /// corners from the box's current axis-aligned bbox in the first place.</summary>
    private static bool TryWarpBoxContent(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateBoxElement element, PerspectiveCorners corners, int imageHeightPx)
    {
        var ceiling = MathF.Min(MaxElementResizeDimensionPxCeiling, MaxElementResizeDestinationMultiplier * MathF.Max(image.Width, image.Height));
        if (!TryComputeWarpGeometry(corners, image.Width, image.Height, ceiling, out var bboxPx, out var subBitmapWidth, out var subBitmapHeight, out var localCorners))
        {
            return false;
        }

        using var contentBitmap = new Image<Rgba32>(subBitmapWidth, subBitmapHeight);
        contentBitmap.Mutate(ctx => DrawBoxContent(ctx, element, new PixelBounds(0, 0, subBitmapWidth, subBitmapHeight), imageHeightPx));

        var matrix = SolveHomography(localCorners, subBitmapWidth, subBitmapHeight);
        if (!IsWellConditioned(matrix, subBitmapWidth, subBitmapHeight))
        {
            return false;
        }

        using var subBitmap = contentBitmap.Clone(ctx => ctx.Transform(
            new Rectangle(0, 0, subBitmapWidth, subBitmapHeight), matrix, new Size(subBitmapWidth, subBitmapHeight), KnownResamplers.Bicubic));

        var location = new Point((int)MathF.Round(bboxPx.X), (int)MathF.Round(bboxPx.Y));
        image.Mutate(ctx => ctx.DrawImage(subBitmap, location, 1f));
        return true;
    }

    /// <summary>Shared by <see cref="TryWarpElementContent"/> (image) and <see cref="TryWarpBoxContent"/>
    /// (box) -- computes the destination-pixel-space corners, their bounding box, and the
    /// safety-clamped sub-bitmap size + LOCAL (bbox-relative) corners every warp render needs, so the
    /// two content-acquisition paths (load a source image vs. draw box fill/border fresh) can't
    /// independently compute a slightly different bbox and drift apart. Returns false (all out params
    /// default) when the destination quad itself is degenerate -- caller falls back to the plain
    /// axis-aligned render, same convention as every other decorative/edge-case path in this file.</summary>
    private static bool TryComputeWarpGeometry(
        PerspectiveCorners corners, int imageWidth, int imageHeight, float destinationSizeCeiling,
        out PixelBounds bboxPx, out int subBitmapWidth, out int subBitmapHeight, out PerspectiveCorners localCorners)
    {
        var destCorners = new PerspectiveCorners(
            corners.Corner0X * imageWidth, corners.Corner0Y * imageHeight,
            corners.Corner1X * imageWidth, corners.Corner1Y * imageHeight,
            corners.Corner2X * imageWidth, corners.Corner2Y * imageHeight,
            corners.Corner3X * imageWidth, corners.Corner3Y * imageHeight);

        if (!destCorners.IsConvexAndWellFormed())
        {
            bboxPx = default;
            subBitmapWidth = 0;
            subBitmapHeight = 0;
            localCorners = default;
            return false;
        }

        var bboxNormalized = corners.ToBoundingBox();
        bboxPx = ToPixelBounds(bboxNormalized, imageWidth, imageHeight);
        var rawWidthPx = MathF.Max(1f, MathF.Round(bboxPx.Width));
        var rawHeightPx = MathF.Max(1f, MathF.Round(bboxPx.Height));
        var safetyScale = MathF.Min(1f, destinationSizeCeiling / MathF.Max(rawWidthPx, rawHeightPx));
        subBitmapWidth = Math.Max(1, (int)MathF.Round(rawWidthPx * safetyScale));
        subBitmapHeight = Math.Max(1, (int)MathF.Round(rawHeightPx * safetyScale));
        // Corners relative to the sub-bitmap's own origin, at the sub-bitmap's own (possibly
        // safety-scaled-down) resolution -- NOT the full destination-image resolution.
        var scaleX = subBitmapWidth / MathF.Max(1f, rawWidthPx);
        var scaleY = subBitmapHeight / MathF.Max(1f, rawHeightPx);
        localCorners = new PerspectiveCorners(
            (destCorners.Corner0X - bboxPx.X) * scaleX, (destCorners.Corner0Y - bboxPx.Y) * scaleY,
            (destCorners.Corner1X - bboxPx.X) * scaleX, (destCorners.Corner1Y - bboxPx.Y) * scaleY,
            (destCorners.Corner2X - bboxPx.X) * scaleX, (destCorners.Corner2Y - bboxPx.Y) * scaleY,
            (destCorners.Corner3X - bboxPx.X) * scaleX, (destCorners.Corner3Y - bboxPx.Y) * scaleY);
        return true;
    }

    /// <summary>Render-layer degeneracy guard (defense-in-depth for a hand-edited/imported template
    /// that never went through the UI's own real-time convexity clamp -- <see cref="PerspectiveCorners.IsConvexAndWellFormed"/>
    /// already rejects most bad quads before a matrix is even solved, but a near-degenerate-not-quite-
    /// degenerate quad can still solve to an ill-conditioned matrix). Checks the projective
    /// denominator <c>w = g*x + h*y + 1</c> ONLY at the source rectangle's 4 corners -- <c>w</c> is
    /// AFFINE (linear) over a rectangle, so its extrema provably occur at the corners, no need to
    /// sample the whole extent. Requires BOTH: all 4 corner <c>w</c> values share the exact same sign
    /// (catches a horizon-crossing quad even when the ratio check below would pass -- e.g.
    /// w={+1,+1,-1,-1} has ratio 1 but genuinely crosses zero), AND a RELATIVE (not absolute -- an
    /// absolute threshold repeats the exact non-scale-invariant mistake <see cref="PerspectiveCorners.IsConvexAndWellFormed"/>
    /// already avoids) <c>min(|w|) &gt; k*max(|w|)</c> for a small constant k.</summary>
    private static bool IsWellConditioned(Matrix4x4 m, int sourceWidthPx, int sourceHeightPx)
    {
        const float minRelativeW = 0.05f;
        Span<float> ws =
        [
            (0 * m.M14) + (0 * m.M24) + m.M44,
            (sourceWidthPx * m.M14) + (0 * m.M24) + m.M44,
            (sourceWidthPx * m.M14) + (sourceHeightPx * m.M24) + m.M44,
            (0 * m.M14) + (sourceHeightPx * m.M24) + m.M44,
        ];
        var minAbs = float.MaxValue;
        var maxAbs = 0f;
        var sign = 0;
        foreach (var w in ws)
        {
            if (!float.IsFinite(w))
            {
                return false;
            }

            var thisSign = MathF.Sign(w);
            if (sign == 0)
            {
                sign = thisSign;
            }
            else if (sign != thisSign)
            {
                return false;
            }

            minAbs = MathF.Min(minAbs, MathF.Abs(w));
            maxAbs = MathF.Max(maxAbs, MathF.Abs(w));
        }

        return minAbs > minRelativeW * maxAbs;
    }

    /// <summary>Opacity goes through <see cref="GraphicsOptions.BlendPercentage"/>, NOT the fill
    /// color's own alpha channel -- verified empirically (this project's own "no assumptions"
    /// discipline caught it, not a documentation read): on an <see cref="Image{TPixel}"/> of
    /// <see cref="SixLabors.ImageSharp.PixelFormats.Rgb24"/> (no alpha channel), <c>Fill</c>/
    /// <c>Draw</c> silently ignore a semi-transparent <c>Rgba32</c>/<c>Color</c> argument's own
    /// alpha and paint fully opaque regardless -- confirmed by a standalone repro before writing
    /// this, not assumed from how it works on an <c>Rgba32</c>-format destination (where color
    /// alpha DOES blend correctly). <c>BlendPercentage</c> works on either destination format, so
    /// it's the one real opacity control here.</summary>
    private static void DrawTemplateBox(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateBoxElement element, PixelBounds bounds, int imageHeightPx)
    {
        // TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- same priority
        // order as DrawTemplateImage's own Perspective check: takes over from the plain axis-aligned
        // fill/border render below when set.
        if (element.Perspective is { } corners && TryWarpBoxContent(image, element, corners, imageHeightPx))
        {
            return;
        }

        image.Mutate(ctx => DrawBoxContent(ctx, element, bounds, imageHeightPx));
    }

    /// <summary>Extracted so <see cref="TryWarpBoxContent"/> can draw the SAME fill/border/corner-
    /// radius/gradient content into an offscreen local-coordinate sub-bitmap before warping it, not a
    /// second, driftable copy of this logic. <paramref name="bounds"/> is destination-image space
    /// for the plain (unwarped) path, or sub-bitmap-LOCAL space (always starting at (0,0)) for the
    /// warp path -- same "wherever it's actually being drawn" convention <see cref="BuildGradientBrush"/>
    /// already documents for text's own rotation path.</summary>
    private static void DrawBoxContent(IImageProcessingContext ctx, TemplateBoxElement element, PixelBounds bounds, double imageHeightPx)
    {
        var cornerRadiusPx = MathF.Max(0f, (float)(element.CornerRadius * imageHeightPx));
        var rect = BuildBoxPath(bounds.X, bounds.Y, bounds.Width, bounds.Height, cornerRadiusPx);
        var opacity = Math.Clamp((float)element.Opacity, 0f, 1f);
        var options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { BlendPercentage = opacity } };
        var fillColor = new Rgba32(element.FillColor.R, element.FillColor.G, element.FillColor.B, 255);
        // Box gradient fill (TX editor gap-items plan, 2026-09-01) -- SAME BuildGradientBrush call
        // DrawTemplateText already uses; that method's own bounds/fallback-color params carry no
        // text-specific assumption, confirmed before reuse.
        Brush fillBrush = element.Gradient is { } gradient
            ? BuildGradientBrush(gradient, bounds, element.FillColor)
            : Brushes.Solid(fillColor);

        ctx.Fill(options, fillBrush, rect);

        if (element.BorderColor is { } borderColor && element.BorderThickness > 0)
        {
            var requestedThicknessPx = (float)(element.BorderThickness * imageHeightPx);
            // ImageSharp's Draw(pen, path) CENTERS the stroke on the path -- confirmed via the
            // same empirical check as the opacity fix above, not assumed -- so drawing directly
            // on `rect` would bleed BorderThickness/2 outside Bounds on every side (code-review
            // round-1 finding). Inset the stroked rect by half the (clamped) thickness so the
            // stroke's OUTER edge lands exactly at Bounds, keeping the whole border inside it --
            // matches the same "clip to Bounds" philosophy DrawTemplateText already uses.
            var maxThicknessPx = MathF.Max(0f, MathF.Min(bounds.Width, bounds.Height) - 0.5f);
            var borderThicknessPx = MathF.Min(requestedThicknessPx, maxThicknessPx);
            if (borderThicknessPx > 0)
            {
                var half = borderThicknessPx / 2f;
                // Corner radius insets by the same half-thickness the straight-edge case already
                // uses, floored at 0 -- a thick border on a small radius would otherwise go
                // negative, which BuildBoxPath itself also clamps (MathF.Min against half the
                // shorter side), but doing it here too keeps this call site's own intent explicit.
                var borderRadiusPx = MathF.Max(0f, cornerRadiusPx - half);
                var borderRect = BuildBoxPath(
                    bounds.X + half, bounds.Y + half, bounds.Width - borderThicknessPx, bounds.Height - borderThicknessPx, borderRadiusPx);
                var borderColorRgba = new Rgba32(borderColor.R, borderColor.G, borderColor.B, 255);
                ctx.Draw(options, borderColorRgba, borderThicknessPx, borderRect);
            }
        }
    }

    /// <summary>Round 1/3 plan-review finding (line element design): the shared degenerate-bbox skip
    /// in <c>ApplyTemplateInto</c> only catches an AXIS-ALIGNED zero-thickness line -- its own
    /// un-inflated bbox collapses one axis to zero area, which the skip rule already drops. A
    /// DIAGONAL line's bbox stays non-degenerate even at zero thickness (both axes have real
    /// extent), so this method needs its own guard, mirroring <see cref="DrawTemplateBox"/>'s own
    /// <c>element.BorderThickness &gt; 0</c> gate. Ignores <c>bounds</c> entirely for geometry --
    /// unlike every other element kind, a line's actual shape comes from its own endpoints, not its
    /// (already ink-inflated, see <see cref="TemplateLineElement"/>'s own doc comment) bounding
    /// box.</summary>
    private static void DrawTemplateLine(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateLineElement element, int imageHeightPx)
    {
        var thicknessPx = MathF.Max(0f, (float)(element.Thickness * imageHeightPx));
        if (thicknessPx <= 0)
        {
            return;
        }

        var pointA = new PointF((float)(element.X1 * image.Width), (float)(element.Y1 * imageHeightPx));
        var pointB = new PointF((float)(element.X2 * image.Width), (float)(element.Y2 * imageHeightPx));
        // Exact PointF equality alone only catches a TRUE zero-length line -- a future endpoint-drag
        // gesture will routinely produce a sub-pixel non-equal segment mid-drag. Squared-length
        // epsilon check catches both cases the same way -- "genuinely invisible" reasoning as the
        // thickness guard above.
        var dx = pointB.X - pointA.X;
        var dy = pointB.Y - pointA.Y;
        if ((dx * dx) + (dy * dy) < 1e-6f)
        {
            return;
        }

        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.AddLine(pointA, pointB);
        var path = pb.Build();

        var opacity = Math.Clamp((float)element.Opacity, 0f, 1f);
        var options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { BlendPercentage = opacity } };
        var strokeColor = new Rgba32(element.StrokeColor.R, element.StrokeColor.G, element.StrokeColor.B, 255);
        // ctx.Draw(pen, path) CENTERS the stroke on the path (see DrawTemplateBox's own doc comment
        // -- verified empirically, not assumed from the API shape) -- exactly the expected behavior
        // for a line's own centerline, no inset needed the way the box border case required.
        image.Mutate(ctx => ctx.Draw(options, strokeColor, thicknessPx, path));
    }

    /// <summary>Plain axis-aligned rect when <paramref name="cornerRadiusPx"/> is 0 (the byte-for-byte
    /// unchanged fast path every existing template hits) or a hand-built rounded-rectangle path
    /// otherwise -- 4 <c>PathBuilder.AddArc</c> quarter-arcs (each 90°, centered on the corresponding
    /// corner, radius clamped to half the shorter side so a large radius on a small/thin box degrades
    /// to a stadium/pill shape rather than a self-intersecting path) connected by 4 straight edges.
    /// Verified against a real rendered PNG before use (not assumed from the API shape alone) -- see
    /// this method's own call sites' doc comments for why: ImageSharp.Drawing has no built-in
    /// rounded-rectangle primitive at the version this project is pinned to.</summary>
    private static SixLabors.ImageSharp.Drawing.IPath BuildBoxPath(float x, float y, float width, float height, float cornerRadiusPx)
    {
        if (cornerRadiusPx <= 0)
        {
            return new SixLabors.ImageSharp.Drawing.RectangularPolygon(x, y, width, height);
        }

        var r = MathF.Min(cornerRadiusPx, MathF.Min(width, height) / 2f);
        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.StartFigure();
        pb.AddArc(new PointF(x + r, y + r), r, r, 0f, 180f, 90f);
        pb.AddLine(new PointF(x + r, y), new PointF(x + width - r, y));
        pb.AddArc(new PointF(x + width - r, y + r), r, r, 0f, 270f, 90f);
        pb.AddLine(new PointF(x + width, y + r), new PointF(x + width, y + height - r));
        pb.AddArc(new PointF(x + width - r, y + height - r), r, r, 0f, 0f, 90f);
        pb.AddLine(new PointF(x + width - r, y + height), new PointF(x + r, y + height));
        pb.AddArc(new PointF(x + r, y + height - r), r, r, 0f, 90f, 90f);
        pb.AddLine(new PointF(x, y + height - r), new PointF(x, y + r));
        pb.CloseFigure();
        return pb.Build();
    }

    /// <summary>Shared by <see cref="ApplyOverlay"/> and <see cref="ApplyTemplate"/> (Phase 0
    /// plan-review finding: one drawing path, not two parallel implementations).
    /// <paramref name="clip"/> null means "no clipping" (ApplyOverlay's own historical behavior --
    /// its <paramref name="wrappingLength"/> already handles overflow). <paramref name="hintingMode"/>
    /// null leaves <see cref="TextOptions.HintingMode"/> at its own default, UNTOUCHED -- required
    /// for <see cref="ApplyOverlay"/>'s call site so this method's rendered OUTPUT stays
    /// byte-for-byte identical to before this refactor. <see cref="DrawTemplateText"/>'s call site
    /// passes <see cref="HintingMode.None"/> explicitly instead (code-review round-1 finding: it
    /// MUST match <see cref="ComputeFittedFontSizePx"/>'s own measurement <see cref="HintingMode"/>
    /// exactly, or a fitted size can render larger than what was actually measured).</summary>
    /// <summary>Phase 4: <paramref name="strokeColor"/>/<paramref name="strokeThicknessPx"/>
    /// (both optional, default none) add an outline. Deliberately NOT taken when no stroke is
    /// requested: the no-stroke path keeps calling the plain <c>DrawText(options, text, Rgba32)</c>
    /// Color overload completely unchanged, so <see cref="ApplyOverlay"/>'s own byte-for-byte
    /// behavior (its only caller that never passes a stroke) stays untouched by this addition.
    /// <para><b>Two SEPARATE draw calls, stroke then fill -- NOT the combined
    /// <c>DrawText(options, text, Brush, Pen)</c> overload.</b> That combined overload was the
    /// first thing tried and looked correct on paper (it's a real, documented API,
    /// ImageSharp.Drawing 2.1.7), but real-window verification (a Phase 5 ready-rack thumbnail
    /// rendering as solid black with zero non-background pixels) traced back to it: at this
    /// engine's usual proportions -- <see cref="TemplateTextElement.StrokeThickness"/> normalized to
    /// image height the same as <see cref="FontSpec.Size"/>, e.g. the shipped default 0.02 stroke
    /// vs. 0.1 font size -- the stroke pixel width ends up comparable to or larger than a normal-
    /// weight glyph's own ink width. A <see cref="Pen"/> centers its stroke ON the glyph outline
    /// (grows ink by half the pen width on EACH side, i.e. inward as well as outward), and in one
    /// combined draw call that inward growth from a center-tracing stroke pass entirely overwrites
    /// the interior fill for thin strokes -- confirmed empirically (isolated ImageSharp-only repro
    /// against the exact installed package version): font=12px/stroke=2.4px produced ZERO non-
    /// background pixels; the identical geometry via two passes recovered legible fill. Drawing the
    /// STROKE first (Pen only, so its full centered width lands, inward portion included) and then
    /// the FILL on top (Brush only, opaque, painted last) guarantees the fill always fully covers
    /// its own glyph interior regardless of stroke width -- only the stroke's OUTWARD-extending
    /// portion (the part not already covered by the fill pass) remains visible, which is what a
    /// legible text outline is actually supposed to look like. <see cref="DrawTemplateText"/>'s own
    /// caller is responsible for shrinking the FIT BOX by the stroke width before calling this (a
    /// separate correction, still needed either way -- see that method's own doc comment).</para>
    /// <para>Phase 8: generic over <typeparamref name="TPixel"/> (was fixed to
    /// <see cref="SixLabors.ImageSharp.PixelFormats.Rgb24"/>) so the SAME method draws both onto the
    /// main <c>Rgb24</c> image (unrotated text) and an offscreen <see cref="Rgba32"/> sub-bitmap (the
    /// rotation path) -- one drawing path, not two near-duplicates. <paramref name="shadowColor"/>
    /// null means no shadow (matches <paramref name="strokeColor"/>'s own null-means-none
    /// convention); when present, drawn FIRST (a plain filled copy, offset by
    /// (<paramref name="shadowOffsetXPx"/>,<paramref name="shadowOffsetYPx"/>), no stroke of its
    /// own) so stroke and fill both paint on top of it, matching the plan's own fixed draw order
    /// (shadow pre-pass → stroke → fill last). <paramref name="fillBrush"/> non-null overrides the
    /// plain solid-<paramref name="color"/> fill with a <see cref="Brush"/> (a gradient, or the
    /// pre-Phase-8 <c>Brushes.Solid(color)</c> stroke-requires-a-brush case) -- null keeps the
    /// ORIGINAL plain-<c>Rgba32</c>-overload fill call byte-for-byte unchanged, which is what keeps
    /// <see cref="ApplyOverlay"/>'s own output identical to before any of Phase 4/8's additions.</para></summary>
    /// <summary>Auditor usability review follow-up (2026-08-18): copy count is clamped to
    /// <see cref="MaxStackCopies"/> -- nothing upstream validates the AXAML-bound StackStepX/Y
    /// properties, and an unclamped huge step would draw thousands of copies per frame (a real perf
    /// cliff on every preview repaint, not just the final render). 128 mirrors legacy's own
    /// int8-packed step range (-128..127), a natural, already-battle-tested ceiling for this same
    /// effect, not an arbitrarily chosen number.</summary>
    private const int MaxStackCopies = 128;

    private static void DrawGlyphs<TPixel>(
        Image<TPixel> image, string text, FontFamily fontFamily, float fontSizePx, PointF origin,
        Abstractions.Imaging.Rgb24 color,
        float wrappingLength, SixLabors.ImageSharp.Drawing.RectangularPolygon? clip, HintingMode? hintingMode,
        Abstractions.Imaging.Rgb24? strokeColor = null, float strokeThicknessPx = 0,
        Abstractions.Imaging.Rgb24? shadowColor = null, float shadowOffsetXPx = 0, float shadowOffsetYPx = 0,
        Brush? fillBrush = null, SixLabors.Fonts.FontStyle fontStyle = SixLabors.Fonts.FontStyle.Regular,
        Abstractions.Imaging.Rgb24? stackColor = null, float stackStepXPx = 0, float stackStepYPx = 0)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var font = fontFamily.CreateFont(fontSizePx, fontStyle);
        var rgba = ToRgba32(color);
        var options = new RichTextOptions(font)
        {
            Origin = origin,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            WrappingLength = wrappingLength,
        };

        if (hintingMode is { } mode)
        {
            options.HintingMode = mode;
        }

        void Render(IImageProcessingContext ctx)
        {
            // Stack draws FIRST (furthest back), behind the shadow/stroke/fill passes below -- an
            // extruded "3D" look, one solid-color copy per pixel of the dominant step axis, stepping
            // from the farthest copy in toward the origin (legacy YONIQ's own real mechanism, see
            // TemplateTextElement.StackColor's own doc comment).
            if (stackColor is { } stack)
            {
                var copies = Math.Min(MaxStackCopies, (int)MathF.Round(MathF.Max(MathF.Abs(stackStepXPx), MathF.Abs(stackStepYPx))));
                var stackBrush = Brushes.Solid(ToRgba32(stack));
                for (var f = copies; f >= 1; f--)
                {
                    var stackOptions = new RichTextOptions(font)
                    {
                        Origin = origin + new PointF(stackStepXPx * f / copies, stackStepYPx * f / copies),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        WrappingLength = wrappingLength,
                    };
                    if (hintingMode is { } stackHinting)
                    {
                        stackOptions.HintingMode = stackHinting;
                    }

                    ctx.DrawText(stackOptions, text, stackBrush);
                }
            }

            if (shadowColor is { } shadow)
            {
                var shadowOptions = new RichTextOptions(font)
                {
                    Origin = origin + new PointF(shadowOffsetXPx, shadowOffsetYPx),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    WrappingLength = wrappingLength,
                };
                if (hintingMode is { } shadowHinting)
                {
                    shadowOptions.HintingMode = shadowHinting;
                }

                ctx.DrawText(shadowOptions, text, Brushes.Solid(ToRgba32(shadow)));
            }

            if (strokeColor is { } stroke && strokeThicknessPx > 0)
            {
                var strokeRgba = ToRgba32(stroke);
                ctx.DrawText(options, text, Pens.Solid(strokeRgba, strokeThicknessPx));
            }

            if (fillBrush is { } brush)
            {
                ctx.DrawText(options, text, brush);
            }
            else
            {
                ctx.DrawText(options, text, rgba);
            }
        }

        if (clip is { } clipPath)
        {
            image.Mutate(ctx => ctx.Clip(clipPath, Render));
        }
        else
        {
            image.Mutate(Render);
        }
    }

    /// <summary>Floor for <see cref="ComputeFittedFontSizePx"/>'s shrink-to-fit search, in pixels
    /// -- deliberately a placeholder value (Phase 0 plan's own open question 6: "pick a concrete
    /// number," not yet a real UX-tuned decision). Below this, <see cref="DrawTemplateText"/>'s
    /// clip-to-<see cref="TemplateElement.Bounds"/> is what actually enforces the render-time
    /// overflow policy, not a smaller font.</summary>
    private const float MinFontSizePx = 6f;

    /// <summary>Binary-searches downward from <paramref name="startingSizePx"/> for the largest
    /// font size at or above <paramref name="minSizePx"/> that measures within
    /// <paramref name="boundsWidthPx"/> x <paramref name="boundsHeightPx"/>, single-line
    /// (spec/15-template-designer.md decision 2: SSTV text is always short, so shrink-to-fit alone
    /// is sufficient -- no wrap mode). <see cref="SixLabors.Fonts"/> 2.1.3 has no fit-to-box helper
    /// (verified directly against its real API surface during Phase 0 plan-review, not assumed) --
    /// this manual search is the only path. <see cref="HintingMode.None"/> is pinned for the
    /// measurement so a naive bisect can't land on a size that measures as fitting only because of
    /// hinting's grid-fitting quantization. The search is robust to non-monotonic measurement BY
    /// CONSTRUCTION, not by a defensive decrement pass afterward (code-review round-1 finding: an
    /// earlier draft had one, but it was provably dead code -- <c>low</c> is only ever assigned
    /// from a candidate that <c>FitsAt</c> independently verified true at that exact size, so the
    /// returned value is always either a verified-fitting size or the untested floor
    /// <paramref name="minSizePx"/> itself; there is no reachable state in between where a
    /// verify-and-decrement pass would ever find something to correct). Returns
    /// <paramref name="minSizePx"/> if even the floor doesn't fit -- <see cref="DrawTemplateText"/>'s
    /// own clip-to-bounds is what actually enforces the render-time overflow policy in that
    /// case, not this method.</summary>
    private static float ComputeFittedFontSizePx(
        string text, FontFamily fontFamily, float startingSizePx, float minSizePx, int boundsWidthPx, int boundsHeightPx,
        SixLabors.Fonts.FontStyle fontStyle = SixLabors.Fonts.FontStyle.Regular)
    {
        if (string.IsNullOrEmpty(text))
        {
            return MathF.Max(startingSizePx, minSizePx);
        }

        bool FitsAt(float candidateSizePx)
        {
            // Bold glyphs measure WIDER than Regular at the same point size -- fontStyle MUST match
            // DrawGlyphs' own CreateFont call exactly (same "measurement and draw use the identical
            // font instance shape" discipline this method's own doc comment already establishes for
            // HintingMode), or the shrink-to-fit search picks a size that overflows once actually
            // drawn bold.
            var font = fontFamily.CreateFont(candidateSizePx, fontStyle);
            var options = new TextOptions(font) { HintingMode = HintingMode.None };
            var measured = TextMeasurer.MeasureSize(text, options);
            return measured.Width <= boundsWidthPx && measured.Height <= boundsHeightPx;
        }

        var upper = MathF.Max(startingSizePx, minSizePx);
        if (FitsAt(upper))
        {
            return upper;
        }

        var low = minSizePx;
        var high = upper;
        for (var i = 0; i < 12 && high - low > 0.5f; i++)
        {
            var mid = (low + high) / 2f;
            if (FitsAt(mid))
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    /// <summary>Phase 4: a real per-family lookup against <see cref="_fontCollection"/> (both
    /// bundled families, see <see cref="AvailableFontFamilies"/>), falling back to
    /// <see cref="_defaultFontFamily"/> on a miss (unknown/unavailable family, or an empty string
    /// from a pre-Phase-4 call site that never set one). <c>CultureInfo.InvariantCulture</c>
    /// explicitly, matching the constructor's own <c>Add</c> calls -- <see cref="FontCollection"/>
    /// indexes by culture, so a mismatched culture argument would silently miss even a family that
    /// really is loaded.</summary>
    private FontFamily ResolveFontFamily(string family) =>
        !string.IsNullOrEmpty(family) && _fontCollection.TryGet(family, System.Globalization.CultureInfo.InvariantCulture, out var found)
            ? found
            : _defaultFontFamily;

    private static PixelBounds ToPixelBounds(NormalizedRect bounds, int imageWidth, int imageHeight) => new(
        (float)(bounds.X * imageWidth), (float)(bounds.Y * imageHeight),
        (float)(bounds.Width * imageWidth), (float)(bounds.Height * imageHeight));

    private readonly record struct PixelBounds(float X, float Y, float Width, float Height);

    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- solves the
    /// standard "map a source rectangle to an arbitrary destination quadrilateral" projective
    /// (homography) matrix (Heckbert's classic closed-form derivation, not novel research -- see
    /// e.g. Paul Heckbert's "Fundamentals of Texture Mapping and Image Warping," 1989). Maps
    /// <c>(0,0)-(sourceWidthPx,0)-(sourceWidthPx,sourceHeightPx)-(0,sourceHeightPx)</c> (the source
    /// rectangle's own 4 corners, in that TopLeft/TopRight/BottomRight/BottomLeft winding order) onto
    /// <paramref name="corners"/>'s own 4 points, in the SAME winding order.
    /// <para><see cref="System.Numerics.Matrix4x4"/>/<see cref="Vector4.Transform(Vector4,Matrix4x4)"/>'s
    /// own field layout is a stable, documented .NET BCL convention (row-vector: <c>result = v * M</c>,
    /// i.e. <c>result.X = v.X*M11 + v.Y*M21 + v.Z*M31 + v.W*M41</c>, and so on for Y/Z/W) -- NOT an
    /// ImageSharp-specific ambiguity. Feeding a 2D point as <c>(x, y, 0, 1)</c>, the classical 3x3
    /// projective matrix <c>[[a,b,c],[d,e,f],[g,h,1]]</c> (destX = (a*x+b*y+c)/w, destY =
    /// (d*x+e*y+f)/w, w = g*x+h*y+1) maps onto <see cref="System.Numerics.Matrix4x4"/> fields
    /// M11=a/M21=b/M41=c (the X output row), M12=d/M22=e/M42=f (the Y output row), M14=g/M24=h/M44=1
    /// (the W/perspective row -- the FOURTH COLUMN in row-vector form, not the third row, since W is
    /// computed from M14/M24/M34/M44). The Z row/column (M13/M23/M33/M43, M31/M32/M34) is left as an
    /// unused identity placeholder (M33=1, everything else 0) -- this is a pure 2D transform, Z is
    /// never read. VERIFIED against real rendered pixels through the actual
    /// <c>ctx.Transform(Rectangle, Matrix4x4, Size, IResampler)</c> call this method feeds, not
    /// trusted from documentation alone -- see the dedicated pixel-level tests for this method and
    /// for the render call site that consumes it.</para></summary>
    private static Matrix4x4 SolveHomography(PerspectiveCorners corners, float sourceWidthPx, float sourceHeightPx)
    {
        // Heckbert's own derivation is for the UNIT square (0,0)-(1,0)-(1,1)-(0,1) -- generalized
        // here to an arbitrary source rectangle by substituting sourceWidthPx/sourceHeightPx for the
        // unit square's own "1" at each of the 4 corner positions (TopRight/BottomRight's own X is
        // sourceWidthPx, not 1; BottomRight/BottomLeft's own Y is sourceHeightPx, not 1), rather than
        // solving for the unit square and composing a separate scale matrix afterward -- one fewer
        // matrix multiplication, same result.
        double x0 = corners.Corner0X, y0 = corners.Corner0Y;
        double x1 = corners.Corner1X, y1 = corners.Corner1Y;
        double x2 = corners.Corner2X, y2 = corners.Corner2Y;
        double x3 = corners.Corner3X, y3 = corners.Corner3Y;
        double sw = sourceWidthPx, sh = sourceHeightPx;

        var dx1 = x1 - x2;
        var dx2 = x3 - x2;
        var dx3 = x0 - x1 + x2 - x3;
        var dy1 = y1 - y2;
        var dy2 = y3 - y2;
        var dy3 = y0 - y1 + y2 - y3;

        double a, b, c, d, e, f, g, h;
        if (Math.Abs(dx3) < 1e-9 && Math.Abs(dy3) < 1e-9)
        {
            // Affine case (a parallelogram -- source rect maps to a quad with no true perspective
            // skew): g=h=0, matching Heckbert's own documented degenerate case exactly.
            g = 0;
            h = 0;
            a = (x1 - x0) / sw;
            b = (x3 - x0) / sh;
            c = x0;
            d = (y1 - y0) / sw;
            e = (y3 - y0) / sh;
            f = y0;
        }
        else
        {
            var denom = (dx1 * dy2) - (dx2 * dy1);
            g = ((dx3 * dy2) - (dx2 * dy3)) / denom;
            h = ((dx1 * dy3) - (dx3 * dy1)) / denom;
            a = (x1 - x0 + (g * x1)) / sw;
            b = (x3 - x0 + (h * x3)) / sh;
            c = x0;
            d = (y1 - y0 + (g * y1)) / sw;
            e = (y3 - y0 + (h * y3)) / sh;
            f = y0;
            g /= sw;
            h /= sh;
        }

        return new Matrix4x4(
            (float)a, (float)d, 0, (float)g,
            (float)b, (float)e, 0, (float)h,
            0, 0, 1, 0,
            (float)c, (float)f, 0, 1);
    }

    /// <summary>Approximately-bounded, thread-safe-in-the-weak-sense (this preparer is a DI
    /// singleton, <c>Program.cs</c>) cache for <see cref="TemplateImageElement"/>'s per-frame
    /// resize -- ImageSharp's own <c>DrawImage</c> has no scale-to-destination-rect overload, so
    /// every image element needs a real <see cref="Resize"/>/crop-to-fill pass on every
    /// <c>RecomputePreview</c> frame without this. Key equality is <see cref="IImageSource"/>
    /// REFERENCE identity, enforced EXPLICITLY by <see cref="ImageResizeCacheKeyComparer"/> (not
    /// left to the default record-struct equality, which would silently start comparing by VALUE
    /// the day some future <c>IImageSource</c> implementation happens to be a <c>record</c> --
    /// code-review round-1 finding: <see cref="ArrayImageSource"/> not overriding <c>Equals</c>
    /// today made the old implicit approach correct only by, not because of, that fact).
    /// "Approximately" bounded: the check-clear-add sequence below is not atomic, so N concurrent
    /// callers can transiently push the count to <see cref="MaxCachedResizedImages"/> + N, and a
    /// racing <see cref="ConcurrentDictionary{TKey,TValue}.Clear"/> can drop an entry another
    /// thread just inserted (harmless -- a future lookup just misses and recomputes). No corruption
    /// either way; real LRU is Phase 0 over-scope, revisit only if profiling ever shows this
    /// matters in practice.</summary>
    private readonly ConcurrentDictionary<ImageResizeCacheKey, IImageSource> _imageResizeCache = new(new ImageResizeCacheKeyComparer());

    private const int MaxCachedResizedImages = 64;

    // Round-2 Tier B audit finding: entry-COUNT alone doesn't bound entry SIZE. Before
    // DrawTemplateImage's own destination-clamp was removed (a real scale/aspect bug, see that
    // method's own doc comment), every cached entry was accidentally capped at the destination
    // canvas's own resolution -- removing that clamp meant a single oversized element (a routine
    // ~5x crop-zoom on an ordinary SSTV-mode canvas, not a pathological one) could fill all 64 slots
    // with tens-of-megabytes-each images, retaining up to ~gigabytes. Skipping the cache write
    // (still returning the computed image -- correctness is unaffected, only reuse-across-frames
    // is) above this pixel budget keeps typical/small elements cheaply cached while a genuinely
    // large one is recomputed each frame instead of permanently occupying a slot.
    private const long MaxCachedResizedImagePixels = 4_000_000; // ~12MB per Abstractions.Imaging.Rgb24 entry

    private IImageSource GetOrCreateResizedImage(IImageSource source, int width, int height, ImageFitMode fit)
    {
        var key = new ImageResizeCacheKey(source, width, height, fit);
        if (_imageResizeCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resized = fit switch
        {
            ImageFitMode.Stretch => Resize(source, width, height, preserveAspect: false),
            ImageFitMode.Contain => Resize(source, width, height, preserveAspect: true),
            ImageFitMode.Cover => ResizeCover(source, width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(fit), fit, message: null),
        };

        if ((long)width * height > MaxCachedResizedImagePixels)
        {
            return resized;
        }

        if (_imageResizeCache.Count >= MaxCachedResizedImages)
        {
            _imageResizeCache.Clear();
        }

        _imageResizeCache[key] = resized;
        return resized;
    }

    private static ArrayImageSource ResizeCover(IImageSource source, int width, int height)
    {
        using var image = ToImageSharp(source);
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(width, height),
            Mode = ResizeMode.Crop,
        }));
        return FromImageSharp(image);
    }

    private readonly record struct ImageResizeCacheKey(IImageSource Source, int Width, int Height, ImageFitMode Fit);

    /// <summary>Explicit reference-identity equality for <see cref="ImageResizeCacheKey.Source"/> --
    /// see the cache field's own doc comment for why this must not be left to default record-struct
    /// equality.</summary>
    private sealed class ImageResizeCacheKeyComparer : IEqualityComparer<ImageResizeCacheKey>
    {
        public bool Equals(ImageResizeCacheKey x, ImageResizeCacheKey y) =>
            ReferenceEquals(x.Source, y.Source) && x.Width == y.Width && x.Height == y.Height && x.Fit == y.Fit;

        public int GetHashCode(ImageResizeCacheKey obj) =>
            HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Source), obj.Width, obj.Height, obj.Fit);
    }

    public IImageSource Rotate(IImageSource source)
    {
        using var image = ToImageSharp(source);
        image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90));
        return FromImageSharp(image);
    }

    /// <summary>TX workflow modernization plan, Phase 7 -- direct scanline copy, deliberately NOT
    /// routed through <see cref="ToImageSharp"/>/<see cref="FromImageSharp"/> like every resampling
    /// operation in this file: there is no resampler choice to make for an exact pixel-rect
    /// extraction, and a full two-way ImageSharp conversion of a multi-megapixel source for what is
    /// a memcpy is real, avoidable per-call cost on the flatten path (which already does one genuine
    /// full-frame render via <see cref="ComposePreview"/>).</summary>
    public IImageSource CropPixels(IImageSource source, int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"Crop size must be positive, got {width}x{height}.");
        }

        if (x < 0 || y < 0 || x + width > source.Width || y + height > source.Height)
        {
            throw new ArgumentOutOfRangeException(
                nameof(x), $"Crop rect ({x},{y},{width},{height}) is outside the {source.Width}x{source.Height} source.");
        }

        var pixels = new Abstractions.Imaging.Rgb24[width * height];
        for (var row = 0; row < height; row++)
        {
            source.GetScanline(y + row).Slice(x, width).CopyTo(pixels.AsSpan(row * width, width));
        }

        return new ArrayImageSource(width, height, pixels);
    }

    public IImageSource Composite(IImageSource background, IImageSource overlay, int x, int y)
    {
        var pixels = new Abstractions.Imaging.Rgb24[background.Width * background.Height];
        for (var row = 0; row < background.Height; row++)
        {
            background.GetScanline(row).CopyTo(pixels.AsSpan(row * background.Width, background.Width));
        }

        var startX = Math.Max(0, x);
        var startY = Math.Max(0, y);
        var endX = Math.Min(background.Width, x + overlay.Width);
        var endY = Math.Min(background.Height, y + overlay.Height);
        var count = endX - startX;
        if (count > 0)
        {
            for (var destinationY = startY; destinationY < endY; destinationY++)
            {
                overlay.GetScanline(destinationY - y)
                    .Slice(startX - x, count)
                    .CopyTo(pixels.AsSpan((destinationY * background.Width) + startX, count));
            }
        }

        return new ArrayImageSource(background.Width, background.Height, pixels);
    }

    private static Image<SixLabors.ImageSharp.PixelFormats.Rgb24> ToImageSharp(IImageSource source)
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
                    var pixel = sourceRow[x];
                    destinationRow[x] = new SixLabors.ImageSharp.PixelFormats.Rgb24(pixel.R, pixel.G, pixel.B);
                }
            }
        });
        return image;
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- Rgba32 variant of
    /// <see cref="ToImageSharp"/>, opaque (alpha=255 -- <see cref="IImageSource"/> itself carries no
    /// alpha), needed because ImageSharp's own <c>Transform</c> operates within a single image's own
    /// pixel format and the WARP's own offscreen destination must be Rgba32 (transparent outside the
    /// mapped quad).</summary>
    private static Image<Rgba32> ToImageSharpRgba32(IImageSource source)
    {
        var image = new Image<Rgba32>(source.Width, source.Height);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < source.Height; y++)
            {
                var sourceRow = source.GetScanline(y);
                var destinationRow = accessor.GetRowSpan(y);
                for (var x = 0; x < source.Width; x++)
                {
                    var pixel = sourceRow[x];
                    destinationRow[x] = new Rgba32(pixel.R, pixel.G, pixel.B, byte.MaxValue);
                }
            }
        });
        return image;
    }

    private static ArrayImageSource FromImageSharp(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image)
    {
        var pixels = new Abstractions.Imaging.Rgb24[image.Width * image.Height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < image.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < image.Width; x++)
                {
                    var pixel = row[x];
                    pixels[(y * image.Width) + x] = new Abstractions.Imaging.Rgb24(pixel.R, pixel.G, pixel.B);
                }
            }
        });

        return new ArrayImageSource(image.Width, image.Height, pixels);
    }
}
