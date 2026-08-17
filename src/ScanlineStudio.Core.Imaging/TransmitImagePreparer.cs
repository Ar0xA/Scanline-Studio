using System.Collections.Concurrent;
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

        var barlowPath = Path.Combine(fontDirectory, "Barlow", "Barlow-Regular.ttf");
        var barlowFamily = _fontCollection.Add(barlowPath, System.Globalization.CultureInfo.InvariantCulture);

        AvailableFontFamilies = [_defaultFontFamily.Name, barlowFamily.Name];
    }

    public IImageSource Crop(IImageSource source, NormalizedRect region)
    {
        using var image = ToImageSharp(source);

        var x = Math.Clamp((int)Math.Round(region.X * source.Width), 0, source.Width - 1);
        var y = Math.Clamp((int)Math.Round(region.Y * source.Height), 0, source.Height - 1);
        var width = Math.Clamp((int)Math.Round(region.Width * source.Width), 1, source.Width - x);
        var height = Math.Clamp((int)Math.Round(region.Height * source.Height), 1, source.Height - y);

        image.Mutate(ctx => ctx.Crop(new Rectangle(x, y, width, height)));
        return FromImageSharp(image);
    }

    public IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect)
    {
        using var image = ToImageSharp(source);

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

        return FromImageSharp(image);
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

        return FromImageSharp(image);
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

        foreach (var element in document.Elements.OrderBy(e => e.Z))
        {
            if (element.Bounds.Width <= 0 || element.Bounds.Height <= 0)
            {
                continue;
            }

            var bounds = ToPixelBounds(element.Bounds, existingBase.Width, existingBase.Height);

            switch (element)
            {
                case TemplateTextElement text:
                    DrawTemplateText(image, text, bounds, existingBase.Height);
                    break;
                case TemplateImageElement img:
                    DrawTemplateImage(image, img, bounds);
                    break;
                case TemplateBoxElement box:
                    DrawTemplateBox(image, box, bounds, existingBase.Height);
                    break;
                default:
                    // TemplateElement is a public abstract record -- an unrecognized subtype means
                    // this switch fell out of sync with the real hierarchy. Throw, don't silently
                    // skip (code-review round-1 finding): a silently-dropped element is a much
                    // harder bug to notice than a build/test failure right here.
                    throw new NotSupportedException($"Unrecognized {nameof(TemplateElement)} subtype: {element.GetType()}.");
            }
        }

        return FromImageSharp(image);
    }

    public double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0)
    {
        var fontFamily = ResolveFontFamily(font.Family);
        var startingSizePx = MathF.Max((float)(font.Size * imageHeightPx), MinFontSizePx);
        var strokeThicknessPx = (float)(strokeThicknessRelative * imageHeightPx);
        var shadowOffsetXPx = (float)(shadowOffsetXRelative * imageHeightPx);
        var shadowOffsetYPx = (float)(shadowOffsetYRelative * imageHeightPx);
        var (boundedWidth, boundedHeight) = ShrinkFitBoxForEffects(
            boundsWidthPx, boundsHeightPx, strokeThicknessPx, shadowOffsetXPx, shadowOffsetYPx, rotationDegrees);
        return ComputeFittedFontSizePx(text, fontFamily, startingSizePx, MinFontSizePx, boundedWidth, boundedHeight);
    }

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
    /// three independently-maintained copies of the same shape of correction. Each shrink only ever
    /// makes the box smaller, so the RESULT is always a valid (safe, no-bleed-past-clip) bound
    /// regardless of order. [Code-review correction] An earlier version of this comment claimed order
    /// "doesn't matter mathematically" — that's true for SAFETY but not for TIGHTNESS: rotation's own
    /// <c>k</c> factor (<see cref="ShrinkFitBoxForRotation"/>) is computed from whatever box stroke/
    /// shadow already shrank, so a DIFFERENT order would generally produce a DIFFERENT (still safe,
    /// but not identically-sized) final box — the three shrinks don't commute in the "produces the
    /// same answer" sense, only in the "still correct" sense. In the specific combination of rotation
    /// PLUS an asymmetric (non-both-axes-equal) shadow offset, this can leave a few pixels of
    /// theoretical over-shrink slack on one axis relative to the other — cosmetic only (the
    /// unconditional <c>Bounds</c> clip in <see cref="DrawTemplateText"/> means any residual
    /// imprecision here shows as slightly-smaller-than-necessary text, never as bleed), not tracked as
    /// a bug to fix, just documented accurately rather than claimed away. Stroke-then-shadow-then-
    /// rotation is the order effects are actually drawn in (see <see cref="DrawGlyphs"/>'s own doc
    /// comment), kept parallel for readability.</summary>
    private static (int Width, int Height) ShrinkFitBoxForEffects(
        int boundsWidthPx, int boundsHeightPx, float strokeThicknessPx, float shadowOffsetXPx, float shadowOffsetYPx, double rotationDegrees)
    {
        var (strokeW, strokeH) = ShrinkFitBoxForStroke(boundsWidthPx, boundsHeightPx, strokeThicknessPx);
        var (shadowW, shadowH) = ShrinkFitBoxForShadow(strokeW, strokeH, shadowOffsetXPx, shadowOffsetYPx);
        return ShrinkFitBoxForRotation(shadowW, shadowH, rotationDegrees);
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
        var (fitWidthPx, fitHeightPx) = ShrinkFitBoxForEffects(
            boundsWidthPx, boundsHeightPx, strokeThicknessPx, shadowOffsetXPx, shadowOffsetYPx, element.RotationDegrees);
        var fittedSizePx = ComputeFittedFontSizePx(element.Content, fontFamily, startingSizePx, MinFontSizePx, fitWidthPx, fitHeightPx);

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
        Brush? fillBrush = element.Gradient is { } gradient
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
                element.StrokeColor, strokeThicknessPx, element.ShadowColor, shadowOffsetXPx, shadowOffsetYPx, fillBrush);
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
            element.StrokeColor, strokeThicknessPx, element.ShadowColor, shadowOffsetXPx, shadowOffsetYPx, fillBrush);

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
            _ => throw new NotSupportedException($"Unrecognized {nameof(TextGradientKind)}: {gradient.Kind}."),
        };
    }

    private static Rgba32 ToRgba32(Abstractions.Imaging.Rgb24 color) => new(color.R, color.G, color.B, 255);

    private void DrawTemplateImage(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateImageElement element, PixelBounds bounds)
    {
        // Defensive floor at 1px -- element.Bounds.Width/Height > 0 is already guaranteed by the
        // caller's skip check, but rounding a very thin bounds rect to pixels can still floor to 0.
        // Also capped at the DESTINATION image's own dimensions (code-review finding, Scanline
        // Studio TX template designer Phase 2): an image element's Bounds is normalized against the
        // CROP rect (TxImageEditorPaneViewModel.ProjectRectToCropRelative), so a full-frame element
        // ("set as background") combined with a small crop rect -- or simply a manually resized
        // element bigger than the working copy -- can project to a Bounds many times larger than
        // 0..1. Resizing the source to that raw pixel size would allocate gigabytes for content
        // that's almost entirely off-canvas; nothing needs to be resolved above the destination's
        // own resolution, since none of it is visible beyond that. A no-op in the normal case (a
        // legitimately-sized element's Bounds is already <= the destination).
        var targetWidth = Math.Clamp((int)MathF.Round(bounds.Width), 1, image.Width);
        var targetHeight = Math.Clamp((int)MathF.Round(bounds.Height), 1, image.Height);
        var resized = GetOrCreateResizedImage(element.Source, targetWidth, targetHeight, element.Fit);

        using var resizedImage = ToImageSharp(resized);
        var location = new Point((int)MathF.Round(bounds.X), (int)MathF.Round(bounds.Y));
        image.Mutate(ctx => ctx.DrawImage(resizedImage, location, 1f));
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
        var rect = new SixLabors.ImageSharp.Drawing.RectangularPolygon(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var opacity = Math.Clamp((float)element.Opacity, 0f, 1f);
        var options = new DrawingOptions { GraphicsOptions = new GraphicsOptions { BlendPercentage = opacity } };
        var fillColor = new Rgba32(element.FillColor.R, element.FillColor.G, element.FillColor.B, 255);

        image.Mutate(ctx =>
        {
            ctx.Fill(options, fillColor, rect);

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
                    var borderRect = new SixLabors.ImageSharp.Drawing.RectangularPolygon(
                        bounds.X + half, bounds.Y + half, bounds.Width - borderThicknessPx, bounds.Height - borderThicknessPx);
                    var borderColorRgba = new Rgba32(borderColor.R, borderColor.G, borderColor.B, 255);
                    ctx.Draw(options, borderColorRgba, borderThicknessPx, borderRect);
                }
            }
        });
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
    private static void DrawGlyphs<TPixel>(
        Image<TPixel> image, string text, FontFamily fontFamily, float fontSizePx, PointF origin,
        Abstractions.Imaging.Rgb24 color,
        float wrappingLength, SixLabors.ImageSharp.Drawing.RectangularPolygon? clip, HintingMode? hintingMode,
        Abstractions.Imaging.Rgb24? strokeColor = null, float strokeThicknessPx = 0,
        Abstractions.Imaging.Rgb24? shadowColor = null, float shadowOffsetXPx = 0, float shadowOffsetYPx = 0,
        Brush? fillBrush = null)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var font = fontFamily.CreateFont(fontSizePx);
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
        string text, FontFamily fontFamily, float startingSizePx, float minSizePx, int boundsWidthPx, int boundsHeightPx)
    {
        if (string.IsNullOrEmpty(text))
        {
            return MathF.Max(startingSizePx, minSizePx);
        }

        bool FitsAt(float candidateSizePx)
        {
            var font = fontFamily.CreateFont(candidateSizePx);
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
