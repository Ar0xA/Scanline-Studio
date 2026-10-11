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

public sealed partial class TransmitImagePreparer
{
    public double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0, bool growToFill = false)
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
        return ComputeFittedFontSizePx(text, fontFamily, startingSizePx, MinFontSizePx, boundedWidth, boundedHeight, ResolveAvailableStyle(fontFamily, ToFontStyle(font)), growToFill);
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
    /// widen the clip -- <see cref="DrawTemplateText"/>'s clip stays <c>Bounds</c> (snapped outward to
    /// whole pixels, see <see cref="BuildPixelSnappedClip"/> -- never smaller than <c>Bounds</c>) for
    /// every effect, no per-effect exceptions. A box of size (w,h) rotated by <paramref name="rotationDegrees"/>
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
        // User-reported gap (2026-09-15): Floor, not Round -- this integer box feeds the font-FIT
        // SEARCH below (ShrinkFitBoxForEffects/ComputeFittedFontSizePx) and a few other downstream
        // uses (gradientBounds' rotated-path fallback, the bitmap-fill resize target, the rotation
        // sub-bitmap size), while the actual UNROTATED draw clip a few lines down (`clip`) uses the
        // RAW, unrounded bounds.Width/Height. Rounding UP here let the fit search assume up to ~0.5px
        // more room than that real clip has -- normally invisible, but GrowToFillEnabled's search
        // deliberately minimizes headroom (stops once the gap is <= 0.5px, see
        // ComputeFittedFontSizePx's own loop), turning that slop into a real, visible sliver of ink
        // sitting right at (and clipped hard against) the box's bottom-right edge -- only ever
        // bottom/right, never top/left, since X/Y themselves aren't rounded here, only the far edge
        // (X+Width/Y+Height) is sensitive to Width/Height's rounding direction. Floor guarantees this
        // box is never larger than the true (float) clip, so the fit search can only undershoot,
        // never overshoot, the real drawable area; the other downstream uses stay safe under Floor
        // too (yoniq-auditor-verified) -- worst case is a rotation/bitmap-fill texture staying
        // ≤1px smaller than before, never larger.
        var boundsWidthPx = Math.Max(1, (int)MathF.Floor(bounds.Width));
        var boundsHeightPx = Math.Max(1, (int)MathF.Floor(bounds.Height));

        var strokeThicknessPx = element.StrokeColor is { } ? (float)(element.StrokeThickness * imageHeightPx) : 0f;
        var shadowOffsetXPx = element.ShadowColor is { } ? (float)(element.ShadowOffsetX * imageHeightPx) : 0f;
        var shadowOffsetYPx = element.ShadowColor is { } ? (float)(element.ShadowOffsetY * imageHeightPx) : 0f;
        var stackStepXPx = element.StackColor is { } ? (float)(element.StackStepX * imageHeightPx) : 0f;
        var stackStepYPx = element.StackColor is { } ? (float)(element.StackStepY * imageHeightPx) : 0f;
        var (fitWidthPx, fitHeightPx) = ShrinkFitBoxForEffects(
            boundsWidthPx, boundsHeightPx, strokeThicknessPx, shadowOffsetXPx, shadowOffsetYPx, element.RotationDegrees, stackStepXPx, stackStepYPx);
        var fontStyle = ResolveAvailableStyle(fontFamily, ToFontStyle(element.Font));
        var fittedSizePx = ComputeFittedFontSizePx(element.Content, fontFamily, startingSizePx, MinFontSizePx, fitWidthPx, fitHeightPx, fontStyle, element.GrowToFillEnabled);

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
            ? ImageSharpPixelConversion.FromImageSource(GetOrCreateResizedImage(bitmapSource, boundsWidthPx, boundsHeightPx, ImageFitMode.Stretch))
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
            // a no-op. Bounds snapped outward to whole pixels (BuildPixelSnappedClip, 2026-09-18 fix
            // for a fractional-edge seam -- see that method's own doc comment), never widened beyond
            // that for stroke/shadow -- the fit-box shrink above is what keeps their ink inside this
            // same clip rect.
            var clip = BuildPixelSnappedClip(bounds);
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
        // fit-box-shrink math above -- but it's what keeps "Bounds (snapped outward to whole pixels,
        // BuildPixelSnappedClip) is the one, single clip rect everywhere in this pipeline" true with
        // no per-effect exception, cheap when already correct, and a real backstop if the math has an
        // edge case this session didn't find.
        var centerX = bounds.X + (bounds.Width / 2f);
        var centerY = bounds.Y + (bounds.Height / 2f);
        var compositeLocation = ComputeRecenteredCompositeLocation(centerX, centerY, subBitmap.Width, subBitmap.Height);
        var boundsClip = BuildPixelSnappedClip(bounds);
        image.Mutate(ctx => ctx.Clip(boundsClip, innerCtx => innerCtx.DrawImage(subBitmap, compositeLocation, 1f)));
    }

    /// <summary>Element rotation (2026-09-20) -- the one genuinely shared piece across every
    /// offscreen-rotate-composite path in this file (text's own rotation branch above, plus the new
    /// Box/Image ones below): given the ORIGINAL content's center and a rotated bitmap's own
    /// POST-rotate <c>Width</c>/<c>Height</c> (ImageSharp's <c>ctx.Rotate</c> auto-expands the canvas
    /// to the rotated content's own bounding box), returns the composite location that keeps the
    /// visual center anchored at the original center. Extracted here (round-1 plan-review finding:
    /// the original plan assumed this was already a larger, reusable helper -- it wasn't, this ~4-line
    /// recenter is the only part that actually generalizes) rather than duplicated three times.</summary>
    private static Point ComputeRecenteredCompositeLocation(float centerX, float centerY, int rotatedWidth, int rotatedHeight) =>
        new Point((int)MathF.Round(centerX - (rotatedWidth / 2f)), (int)MathF.Round(centerY - (rotatedHeight / 2f)));

    // User-reported 2026-09-18: a faint hairline seam along the BOTTOM/RIGHT edge of a text
    // element's box, visible in the mode-exact PREVIEW panel against certain backgrounds --
    // distinct from (and not fixed by) the GrowToFillEnabled ink-overflow fix ABOVE
    // (boundsWidthPx/boundsHeightPx's own Floor). Root cause: DrawTemplateText's two RectangularPolygon
    // clips were built straight from `bounds`' raw float X/Y/Width/Height, so a clip edge routinely
    // landed on a fractional pixel coordinate -- ImageSharp's Clip(path, action) anti-aliases a
    // fractional-coordinate polygon edge, blending the freshly-composited region into the untouched
    // original at partial coverage right at that boundary, which shows as a seam whenever the two
    // differ even slightly. Flooring the near edge and ceiling the far edge lands every boundary on a
    // whole pixel (0% or 100% coverage, no partial blend) without ever shrinking the clip below the
    // true `bounds` -- so no real ink already proven to stay inside `bounds` can be cut.
    private static SixLabors.ImageSharp.Drawing.RectangularPolygon BuildPixelSnappedClip(PixelBounds bounds)
    {
        var left = MathF.Floor(bounds.X);
        var top = MathF.Floor(bounds.Y);
        var right = MathF.Ceiling(bounds.X + bounds.Width);
        var bottom = MathF.Ceiling(bounds.Y + bounds.Height);
        return new SixLabors.ImageSharp.Drawing.RectangularPolygon(left, top, right - left, bottom - top);
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
    /// case, not this method.
    /// <para>User-requested (2026-09-15): when <paramref name="growToFill"/> is set and
    /// <paramref name="startingSizePx"/> already fits, this ALSO searches upward -- an exponential
    /// probe finds a size that no longer fits (or a safety ceiling, 8x the box's larger dimension,
    /// which no real glyph run reaches first, since SixLabors glyph metrics are monotonic in point
    /// size for every font this app resolves), then a binary search mirrors the shrink search above
    /// exactly, just probing the opposite direction. When <paramref name="growToFill"/> is
    /// <see langword="false"/> (every existing call site), behavior is completely unchanged: return
    /// <paramref name="startingSizePx"/> the moment it fits, same as before this parameter
    /// existed.</para></summary>
    private static float ComputeFittedFontSizePx(
        string text, FontFamily fontFamily, float startingSizePx, float minSizePx, int boundsWidthPx, int boundsHeightPx,
        SixLabors.Fonts.FontStyle fontStyle = SixLabors.Fonts.FontStyle.Regular, bool growToFill = false)
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
            if (!growToFill)
            {
                return upper;
            }

            var growCeiling = MathF.Max(boundsWidthPx, boundsHeightPx) * 8f;
            var fitsLow = upper;
            var doesNotFitHigh = upper * 2f;
            while (doesNotFitHigh < growCeiling && FitsAt(doesNotFitHigh))
            {
                fitsLow = doesNotFitHigh;
                doesNotFitHigh *= 2f;
            }

            if (doesNotFitHigh >= growCeiling)
            {
                doesNotFitHigh = growCeiling;
                if (FitsAt(doesNotFitHigh))
                {
                    return doesNotFitHigh;
                }
            }

            for (var i = 0; i < 12 && doesNotFitHigh - fitsLow > 0.5f; i++)
            {
                var mid = (fitsLow + doesNotFitHigh) / 2f;
                if (FitsAt(mid))
                {
                    fitsLow = mid;
                }
                else
                {
                    doesNotFitHigh = mid;
                }
            }

            return fitsLow;
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
    /// bundled families, see <see cref="AvailableFontFamilies"/>), then against
    /// <see cref="_systemFontCollection"/> (OS-installed fonts), falling back to
    /// <see cref="_defaultFontFamily"/> on a miss in both (unknown/unavailable family, an empty
    /// string from a pre-Phase-4 call site that never set one, or a system font that was available
    /// when a template was saved but isn't on this machine). <c>CultureInfo.InvariantCulture</c>
    /// explicitly, matching the constructor's own <c>Add</c> calls -- <see cref="FontCollection"/>
    /// indexes by culture, so a mismatched culture argument would silently miss even a family that
    /// really is loaded.</summary>
    private FontFamily ResolveFontFamily(string family)
    {
        if (string.IsNullOrEmpty(family))
        {
            return _defaultFontFamily;
        }

        if (_fontCollection.TryGet(family, System.Globalization.CultureInfo.InvariantCulture, out var bundled))
        {
            return bundled;
        }

        return _systemFontCollection.TryGet(family, System.Globalization.CultureInfo.InvariantCulture, out var system)
            ? system
            : _defaultFontFamily;
    }

    /// <summary>Bundled families always register Bold/Italic/BoldItalic variants (constructor
    /// comment above), so <c>FontFamily.CreateFont(size, style)</c> never throws for them. An
    /// arbitrary OS-installed family may lack a real face for the requested style -- SixLabors.Fonts
    /// throws in that case rather than synthesizing one -- so any style not in
    /// <see cref="FontFamily.GetAvailableStyles"/> falls back to Regular. Called identically by both
    /// the fit-measurement path (<see cref="MeasureFittedFontSize"/>) and the draw path
    /// (<see cref="DrawTemplateText"/>) so they can never disagree about which style actually
    /// renders.</summary>
    private static SixLabors.Fonts.FontStyle ResolveAvailableStyle(FontFamily family, SixLabors.Fonts.FontStyle requested) =>
        requested == SixLabors.Fonts.FontStyle.Regular || family.GetAvailableStyles().Contains(requested)
            ? requested
            : SixLabors.Fonts.FontStyle.Regular;
}
