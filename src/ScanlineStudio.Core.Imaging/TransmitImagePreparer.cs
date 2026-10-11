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
public sealed partial class TransmitImagePreparer : ITransmitImagePreparer
{
    private readonly FontCollection _fontCollection;
    private readonly FontCollection _systemFontCollection;
    private readonly FontFamily _defaultFontFamily;

    /// <summary>Phase 4 (spec/15-template-designer.md, "small bundled set that renders identically
    /// everywhere") -- first two entries are the guaranteed bundled families (index 0 is also the
    /// default/fallback family, matching <see cref="ResolveFontFamily"/>'s own fallback); any
    /// further entries are OS-installed fonts discovered via <see cref="_systemFontCollection"/>,
    /// offered as additional optional choices, never as a replacement for the guaranteed two --
    /// system-font enumeration can legitimately be empty (e.g. a minimal Linux install with no
    /// fonts registered), so a feature that burns text into transmitted pixel data still needs a
    /// guaranteed-present font (LICENSES.md's DejaVu Sans Mono entry). Barlow is vendored a SECOND
    /// time into this pipeline's own <c>assets/fonts/</c> tree (alongside its existing UI-chrome
    /// copy under <c>src/ScanlineStudio.UI/Assets/Fonts/</c>) -- this project has zero Avalonia
    /// reference and loads fonts by filesystem path, so the UI's own <c>avares://</c>-embedded copy
    /// isn't reachable here (Phase 4 plan-review correction; see LICENSES.md's Barlow entry for the
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

        var bundledNames = new[] { _defaultFontFamily.Name, barlowFamily.Name };
        _systemFontCollection = new FontCollection();
        try
        {
            _systemFontCollection.AddSystemFonts();
        }
        catch (Exception)
        {
            // Best-effort only -- enumeration failure (unreadable font directory, corrupt font
            // file, sandboxed environment) must never take down the two guaranteed bundled
            // families above; the picker just offers no extra choices in that case.
        }

        var systemNames = _systemFontCollection.Families
            .Select(family => family.Name)
            .Where(name => !bundledNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);

        AvailableFontFamilies = [.. bundledNames, .. systemNames];
    }

    public IImageSource Crop(IImageSource source, NormalizedRect region)
    {
        using var image = ImageSharpPixelConversion.FromImageSource(source);
        CropInto(image, source.Width, source.Height, region);
        return ImageSharpPixelConversion.ToImageSource(image);
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
        using var image = ImageSharpPixelConversion.FromImageSource(source);
        ResizeInto(image, width, height, preserveAspect);
        return ImageSharpPixelConversion.ToImageSource(image);
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

        using var image = ImageSharpPixelConversion.FromImageSource(source);
        ApplyAdjustmentsInto(image, adjustments);
        return ImageSharpPixelConversion.ToImageSource(image);
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
        using var image = ImageSharpPixelConversion.FromImageSource(source);

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

        return ImageSharpPixelConversion.ToImageSource(image);
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

        using var image = ImageSharpPixelConversion.FromImageSource(existingBase);
        ApplyTemplateInto(image, document);
        return ImageSharpPixelConversion.ToImageSource(image);
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
        using var image = ImageSharpPixelConversion.FromImageSource(source);

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

        return ImageSharpPixelConversion.ToImageSource(image);
    }

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

    /// <summary>Floor for <see cref="ComputeFittedFontSizePx"/>'s shrink-to-fit search, in pixels
    /// -- deliberately a placeholder value (Phase 0 plan's own open question 6: "pick a concrete
    /// number," not yet a real UX-tuned decision). Below this, <see cref="DrawTemplateText"/>'s
    /// clip-to-<see cref="TemplateElement.Bounds"/> is what actually enforces the render-time
    /// overflow policy, not a smaller font.</summary>
    private const float MinFontSizePx = 6f;

    /// <summary>Per-source cache for <see cref="TemplateImageElement"/>'s per-frame resize. ImageSharp's
    /// <c>DrawImage</c> has no scale-to-destination-rect overload, so without it every image element
    /// needs a full <see cref="Resize"/>/crop-to-fill pass on every <c>RecomputePreview</c> frame.
    /// <para>Keyed weakly on the <see cref="IImageSource"/> by REFERENCE identity
    /// (<see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/> never calls
    /// <c>Equals</c>), so a source the editor has discarded is not kept alive by this singleton, and
    /// its cached resizes are released with it.</para>
    /// <para>Threading: this preparer is a DI singleton (<c>Program.cs</c>), called from the UI thread
    /// (preview recompute, template apply) and from thread-pool workers (the <c>Task.Run</c> element bake).
    /// The outer table and the inner <see cref="ConcurrentDictionary{TKey,TValue}"/> are both
    /// thread-safe. The per-source check-clear-add below is not atomic, so racing callers can
    /// transiently exceed <see cref="MaxCachedResizesPerSource"/> or drop an entry another thread just
    /// inserted; that only costs a later recompute, never a wrong pixel.</para></summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IImageSource, PerSourceResizeCache> _imageResizeCache = new();

    // A source is normally drawn at one or two sizes; overflow means a resize drag, whose stale sizes are worthless.
    private const int MaxCachedResizesPerSource = 8;

    // Entry count alone does not bound entry size: a routine ~5x crop-zoom on an ordinary SSTV canvas
    // would cache tens of megabytes per entry. Above this budget the resize is recomputed each frame
    // instead (correctness is unaffected, only reuse across frames).
    private const long MaxCachedResizedImagePixels = 4_000_000; // ~12MB per Abstractions.Imaging.Rgb24 entry

    public IImageSource Rotate(IImageSource source)
    {
        using var image = ImageSharpPixelConversion.FromImageSource(source);
        image.Mutate(ctx => ctx.Rotate(RotateMode.Rotate90));
        return ImageSharpPixelConversion.ToImageSource(image);
    }

    /// <summary>TX workflow modernization plan, Phase 7 -- direct scanline copy, deliberately NOT
    /// routed through <see cref="ImageSharpPixelConversion.FromImageSource"/>/<see cref="ImageSharpPixelConversion.ToImageSource"/> like every resampling
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
}
