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
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0)
    {
        var fontFamily = ResolveFontFamily(font.Family);
        var startingSizePx = MathF.Max((float)(font.Size * imageHeightPx), MinFontSizePx);
        var strokeThicknessPx = (float)(strokeThicknessRelative * imageHeightPx);
        var (boundedWidth, boundedHeight) = ShrinkFitBoxForStroke(boundsWidthPx, boundsHeightPx, strokeThicknessPx);
        return ComputeFittedFontSizePx(text, fontFamily, startingSizePx, MinFontSizePx, boundedWidth, boundedHeight);
    }

    /// <summary>Shared by <see cref="MeasureFittedFontSize"/> (canvas-side) and
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
    /// not via a pixel-bleed assertion that the clip makes structurally unable to fail.</para></summary>
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

    private void DrawTemplateText(Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, TemplateTextElement element, PixelBounds bounds, int imageHeightPx)
    {
        var fontFamily = ResolveFontFamily(element.Font.Family);
        var startingSizePx = MathF.Max((float)(element.Font.Size * imageHeightPx), MinFontSizePx);
        var boundsWidthPx = Math.Max(1, (int)MathF.Round(bounds.Width));
        var boundsHeightPx = Math.Max(1, (int)MathF.Round(bounds.Height));

        var strokeThicknessPx = element.StrokeColor is { } ? (float)(element.StrokeThickness * imageHeightPx) : 0f;
        var (fitWidthPx, fitHeightPx) = ShrinkFitBoxForStroke(boundsWidthPx, boundsHeightPx, strokeThicknessPx);
        var fittedSizePx = ComputeFittedFontSizePx(element.Content, fontFamily, startingSizePx, MinFontSizePx, fitWidthPx, fitHeightPx);

        var origin = new PointF(bounds.X + (bounds.Width / 2f), bounds.Y + (bounds.Height / 2f));

        // Render-time overflow policy (Phase 0 plan-review blocker 4): the fit search can bottom
        // out at MinFontSizePx and the text STILL not fit Bounds -- clip to Bounds rather than
        // overflow or ellipsize. Clipping unconditionally (not just in the overflow case) is both
        // simpler and correct: when text already fits, nothing is outside the clip region, so it's
        // a no-op. Deliberately still exactly Bounds (not widened for the stroke) -- the fit-box
        // shrink above is what keeps stroke ink inside this same clip rect.
        var clip = new SixLabors.ImageSharp.Drawing.RectangularPolygon(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        // hintingMode: HintingMode.None -- code-review round-1 finding: must match
        // ComputeFittedFontSizePx's own measurement HintingMode exactly, or the fitted size this
        // method just computed can render slightly larger/smaller than what was actually measured
        // (hinting's grid-fitting quantization), silently reintroducing the overflow this whole
        // mechanism exists to prevent.
        DrawGlyphs(
            image, element.Content, fontFamily, fittedSizePx, origin, element.Color, wrappingLength: -1f, clip, hintingMode: HintingMode.None,
            element.StrokeColor, strokeThicknessPx);
    }

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
    /// separate correction, still needed either way -- see that method's own doc comment).</para></summary>
    private static void DrawGlyphs(
        Image<SixLabors.ImageSharp.PixelFormats.Rgb24> image, string text, FontFamily fontFamily, float fontSizePx, PointF origin,
        Abstractions.Imaging.Rgb24 color,
        float wrappingLength, SixLabors.ImageSharp.Drawing.RectangularPolygon? clip, HintingMode? hintingMode,
        Abstractions.Imaging.Rgb24? strokeColor = null, float strokeThicknessPx = 0)
    {
        var font = fontFamily.CreateFont(fontSizePx);
        var rgba = new Rgba32(color.R, color.G, color.B, 255);
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
            if (strokeColor is { } stroke && strokeThicknessPx > 0)
            {
                var strokeRgba = new Rgba32(stroke.R, stroke.G, stroke.B, 255);
                ctx.DrawText(options, text, Pens.Solid(strokeRgba, strokeThicknessPx));
                ctx.DrawText(options, text, Brushes.Solid(rgba));
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
