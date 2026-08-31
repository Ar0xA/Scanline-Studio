namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Crop/resize/overlay pipeline for TX image prep — see spec/07-image-pipeline.md's "TX
/// image editor" section. Declared here (not alongside its `ScanlineStudio.Core.Imaging`
/// implementation) so `ScanlineStudio.UI` can depend on the interface without pulling in a
/// concrete `SixLabors.ImageSharp`/`SixLabors.Fonts` reference, same reasoning as
/// <see cref="IImageFileLoader"/>/<see cref="IStockImageLibrary"/>.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

/// <summary>TX workflow modernization plan, Phase 7 -- shared numeric limits that both
/// `TransmitImagePreparer` (Core.Imaging) and the TX editor VM (ScanlineStudio.UI, which cannot
/// reference Core.Imaging directly, only this Abstractions project) need to agree on. Declared here
/// rather than duplicated as two independently-maintained literals -- exactly the drift class this
/// project has been bitten by before (a formula/constant re-derived at a second call site silently
/// disagreeing with the first).</summary>
public static class TransmitImageLimits
{
    /// <summary>Flat, destination-independent ceiling on an image element's own resize target
    /// (mirrors `TransmitImagePreparer`'s own internal resize-cache sizing logic). The flatten
    /// command's bake-scale computation must stay inside the same clamped/unclamped regime the live
    /// render already uses, or a baked element could render at a different effective resolution than
    /// what the operator saw in preview.</summary>
    public const double MaxElementResizeDimensionPx = 4096;
}

/// <summary>Anchor is the CENTER of the text (matches drag-to-position UX: the user grabs the
/// visual center, not a corner). <see cref="FontSizeRelative"/> is relative to the image's
/// HEIGHT (stable reference regardless of aspect/stretch, unlike width which varies more under a
/// non-aspect-preserving resize). <see cref="Color"/> is <see cref="Rgb24"/> — already the pixel
/// type this whole namespace uses — not a string, which would add an unspecified parse format and
/// a new runtime failure mode for no reason.</summary>
public sealed record ImageOverlayElement(string Text, double X, double Y, double FontSizeRelative, Rgb24 Color);

public sealed record ImageOverlay(IReadOnlyList<ImageOverlayElement> Elements);

/// <summary>All fields are the TX image editor slider's own raw value, not pre-normalized —
/// <see cref="ITransmitImagePreparer.ApplyAdjustments"/> maps each to its underlying operation's
/// own expected range. Brightness/Contrast/Saturation: -50..50, 0 = no change. Gamma: -100..100,
/// 0 = no change (positive brightens, matching Brightness/Contrast's own sign convention — NOT
/// the raw `out = in^gamma` convention, where a gamma exponent above 1 darkens). Sharpen/Denoise:
/// 0..100, 0 = no change — a true, exact no-op (not "applied at zero strength": the underlying
/// Gaussian operations are not defined at sigma=0, see the implementation's own doc comment).</summary>
public sealed record ImageAdjustments(
    double Brightness = 0, double Contrast = 0, double Saturation = 0, double Gamma = 0,
    double Sharpen = 0, double Denoise = 0)
{
    /// <summary>True when every field is its own no-op default — lets
    /// <see cref="ITransmitImagePreparer.ApplyAdjustments"/> skip a real image round-trip entirely
    /// on the common case (sliders untouched), which matters because
    /// <c>TxImageEditorPaneViewModel.RecomputePreview</c> calls it on every interactive frame.</summary>
    public bool IsIdentity => Brightness == 0 && Contrast == 0 && Saturation == 0
        && Gamma == 0 && Sharpen == 0 && Denoise == 0;
}

/// <summary>Which fixed-size box <see cref="TemplateImageElement"/> resizes into, when its
/// <see cref="TemplateImageElement.Bounds"/> pixel size doesn't already match the source image's
/// own aspect ratio. Mirrors <see cref="ITransmitImagePreparer.Resize"/>'s own two modes
/// (Stretch/Contain) plus a third, Cover, that mode doesn't offer. <c>Stretch</c>: fills
/// <see cref="TemplateImageElement.Bounds"/> exactly, distorting aspect if needed (same as
/// <see cref="ITransmitImagePreparer.Resize"/> with <c>preserveAspect: false</c>). <c>Contain</c>:
/// fits entirely within <see cref="TemplateImageElement.Bounds"/> preserving aspect, letterboxed
/// with black on the remainder (same as <see cref="ITransmitImagePreparer.Resize"/> with
/// <c>preserveAspect: true</c>). <c>Cover</c>: fills <see cref="TemplateImageElement.Bounds"/>
/// completely preserving aspect, cropping whatever overflows (the "background-as-element" case —
/// spec/15-template-designer.md — typically wants this, not letterboxing, for a full-frame
/// image).</summary>
public enum ImageFitMode { Stretch, Contain, Cover }

/// <summary>Plain, SixLabors-free font descriptor — same "declared here so
/// <c>ScanlineStudio.UI</c> never needs a concrete <c>SixLabors.Fonts</c> reference" reasoning as
/// the rest of this file (<c>ScanlineStudio.Abstractions</c> has zero <c>PackageReference</c>s;
/// keep it that way). <paramref name="Size"/> is the MAXIMUM/starting size for
/// <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s shrink-to-fit search, not a fixed rendered
/// size — relative to the target image's HEIGHT, same convention as
/// <see cref="ImageOverlayElement.FontSizeRelative"/>, for the same reason (stable regardless of
/// aspect/stretch). <paramref name="Family"/> selects among <c>TransmitImagePreparer</c>'s own
/// bundled font set (<see cref="ITransmitImagePreparer.AvailableFontFamilies"/>) -- this doc comment
/// used to say it wasn't meaningfully consumed; that was stale even before today (Phase 4 already
/// added real family selection), corrected here while adding <paramref name="Bold"/>/<paramref
/// name="Italic"/> (auditor usability review follow-up, 2026-08-18) -- both false is the pre-existing
/// Regular-only behavior, unchanged. A requested Bold/Italic combination the selected family has no
/// matching bundled variant for throws at draw time (verified against the actually-bundled variant
/// set, not silently substituted) -- see <c>TransmitImagePreparer</c>'s own constructor for exactly
/// which weight/style files are registered per family.</summary>
public sealed record FontSpec(string Family, double Size, bool Bold = false, bool Italic = false);

/// <summary>Base for every element a <see cref="TemplateDocument"/> can composite —
/// spec/15-template-designer.md. <paramref name="Bounds"/> is normalized against the FULL target
/// image (same space as <see cref="ITransmitImagePreparer.Crop"/>'s own region), deliberately NOT
/// clamped into <c>[0,1]</c> the way <see cref="ITransmitImagePreparer.Crop"/> clamps its own
/// region — an element may legitimately sit partially or fully outside the frame (matches the
/// existing far-out-of-bounds <see cref="ImageOverlayElement"/> precedent,
/// <c>TransmitImagePreparerTests.cs</c>'s own <c>X = -50</c> case). An element with zero or
/// negative <see cref="NormalizedRect.Width"/>/<see cref="NormalizedRect.Height"/> is skipped
/// entirely by <see cref="ITransmitImagePreparer.ApplyTemplate"/>, not rendered.
/// <paramref name="Z"/> is layer order — <see cref="ITransmitImagePreparer.ApplyTemplate"/> draws
/// ALL elements in ascending <c>(Z, list index)</c> order via a stable sort; there is no special
/// case for any particular <paramref name="Z"/> value (an image element happening to sit at the
/// lowest Z and cover the full frame becomes "the background" as a structural consequence of
/// ordinary z-order, not a flag).</summary>
public abstract record TemplateElement(NormalizedRect Bounds, int Z);

/// <summary>Which axis (or radial center) a <see cref="TextGradient"/> fills across a text
/// element's own <see cref="TemplateElement.Bounds"/>. <see cref="BitmapPattern"/> (auditor
/// usability review follow-up, 2026-08-18) is legacy YONIQ's real "bitmap mask" text-fill option --
/// confirmed via <c>TextIn.cpp</c>'s <c>RGGrade</c> radio group and <c>BitMask.cpp</c>'s
/// <c>MakeBitmapPtn</c>: despite the name, it is NOT a glyph-shaped stencil mask, it's a tiled
/// 2-color pattern/texture BRUSH used as the text fill, the exact same conceptual slot as
/// <see cref="Horizontal"/>/<see cref="Vertical"/> in the SAME legacy radio group (NO/Horizontal/
/// Vertical/Bitmap) -- which is why it's added HERE, as a 4th <see cref="TextGradient"/> kind, rather
/// than a separate feature with its own enable-flag/color-pair. Legacy procedurally generates its own
/// 8-style dither/checkerboard bitmap by hand (`MakeBitmapPtn`'s <c>sw</c> parameter); this reuses
/// ImageSharp.Drawing's own already-available <c>Brushes.Percent20</c> tiled 2-color pattern instead
/// of hand-rolling an equivalent generator -- a real, "improved on, not replicated" primitive swap
/// (CLAUDE.md §2), not a port, since this is UI/editing functionality, not DSP/codec math.</summary>
public enum TextGradientKind { Horizontal, Vertical, Radial, BitmapPattern }

/// <summary><paramref name="Offset"/> is 0..1 along the gradient's own axis (matches ImageSharp's
/// own <c>ColorStop</c> convention, which this maps directly onto at render time).</summary>
public readonly record struct GradientColorStop(float Offset, Rgb24 Color);

/// <summary>Phase 8 (spec/15-template-designer.md, YONIQ-style text-effects follow-up). Coordinates
/// are derived from the element's own pixel <see cref="TemplateElement.Bounds"/> at render time (an
/// ImageSharp gradient brush's own control points are in absolute destination-image space, not
/// glyph/bounds-relative) — when the SAME element also has <see cref="TemplateTextElement.RotationDegrees"/>
/// set, those points are computed against the ROTATION SUB-BITMAP's own local space instead, so the
/// gradient rotates WITH the text (a deliberate choice, stated here rather than left implicit).
/// <paramref name="Stops"/> empty falls back to the element's own solid <see cref="TemplateTextElement.Color"/>
/// as a single stop — a gradient with no stops configured isn't a distinct error case, just a
/// degenerate one-color gradient.</summary>
public sealed record TextGradient(TextGradientKind Kind, IReadOnlyList<GradientColorStop> Stops);

/// <summary><paramref name="Content"/> may contain macro/variable tokens (spec/15's fill-bar
/// mechanism, Phase 3) — resolution happens above this layer, same as
/// <see cref="ImageOverlayElement.Text"/> today; by the time a <see cref="TemplateDocument"/>
/// reaches <see cref="ITransmitImagePreparer.ApplyTemplate"/>, <paramref name="Content"/> is
/// already fully resolved. Text is always drawn single-line, shrunk to fit
/// <see cref="TemplateElement.Bounds"/> (down to an implementation-defined minimum size, below
/// which it's clipped to <see cref="TemplateElement.Bounds"/> rather than overflowing), centered
/// within it — matches <see cref="ImageOverlayElement"/>'s own center-anchor precedent.
/// <paramref name="StrokeColor"/> null means no outline (matches <see cref="TemplateBoxElement.BorderColor"/>'s
/// own null-means-none convention) — Phase 4 (spec/15-template-designer.md, user-requested
/// legibility mechanism for text against varying backgrounds). <paramref name="StrokeThickness"/>
/// is relative to the target image's HEIGHT, same convention as <see cref="FontSpec.Size"/>/
/// <see cref="TemplateBoxElement.BorderThickness"/>.
/// <para>Phase 8 additions (YONIQ-style text-effects follow-up — this record's own doc comment used
/// to say drop-shadow fields were "deliberately NOT staged for a later phase"; that decision is
/// reversed here, not silently). <paramref name="ShadowColor"/> null means no shadow (same
/// null-means-none convention as <paramref name="StrokeColor"/>) — a plain offset-duplicate-glyph
/// draw (legacy YONIQ's own real mechanism, confirmed via <c>Draw.cpp</c>: hard-edged, no blur), NOT
/// the soft <c>GaussianBlur</c> shadow Phase 4 originally sketched — a stated tradeoff (cheaper,
/// visibly different look), not a silent substitution. <paramref name="ShadowOffsetX"/>/
/// <paramref name="ShadowOffsetY"/> are relative to the target image's HEIGHT, same convention as
/// <see cref="FontSpec.Size"/>/<paramref name="StrokeThickness"/> (both axes, so a template saved at
/// one SSTV mode renders correctly at another). <paramref name="RotationDegrees"/> is in-plane
/// (2D) rotation only, clockwise-positive — true 3D/perspective is an explicit, separate future
/// phase (Tier 3, not this one); rendered via an offscreen sub-bitmap render→rotate→composite path,
/// not a GDI-style native rotated draw (no equivalent primitive here). <paramref name="Gradient"/>
/// null means a plain solid <paramref name="Color"/> fill (today's existing behavior, unchanged).</para>
/// <para>Auditor usability review follow-up (2026-08-18): <paramref name="StackColor"/> null means no
/// stack effect (same null-means-none convention as the other optional effects above) -- legacy
/// YONIQ's "3D" text option (confirmed via <c>TextIn.cpp</c>'s <c>CBStack</c>/<c>Draw.cpp</c>'s
/// <c>m_Stack</c>/<c>m_StackPara</c>: NOT a 3D transform, a stepped stack of solid-color offset
/// copies drawn behind the main glyphs, creating an extruded look). <paramref name="StackStepX"/>/
/// <paramref name="StackStepY"/> are height-relative like <paramref name="ShadowOffsetX"/>/Y; the
/// copy COUNT is derived at render time from the larger of the two resolved pixel steps (one copy
/// per pixel of the dominant axis), not persisted separately -- a simplified, "improved on, not
/// replicated" re-derivation (CLAUDE.md §2) of legacy's own signed-byte-packed step count, not a
/// byte-for-byte port of its GDI-specific color-interpolation/shadow-mode-coupling.</para></summary>
public sealed record TemplateTextElement(
    NormalizedRect Bounds, int Z, string Content, FontSpec Font, Rgb24 Color,
    Rgb24? StrokeColor = null, double StrokeThickness = 0,
    Rgb24? ShadowColor = null, double ShadowOffsetX = 0, double ShadowOffsetY = 0,
    double RotationDegrees = 0, TextGradient? Gradient = null,
    Rgb24? StackColor = null, double StackStepX = 0, double StackStepY = 0)
    : TemplateElement(Bounds, Z);

/// <summary><paramref name="Source"/> is an already-resolved <see cref="IImageSource"/>, not a
/// deferred binding token — last-received-image/RX-history/file/clipboard resolution happens at
/// the VM/Application layer, before a <see cref="TemplateDocument"/> is built, so
/// <see cref="ITransmitImagePreparer.ApplyTemplate"/> stays synchronous and I/O-free (matches
/// <c>TxImageEditorPaneViewModel.RecomputePreview</c>'s existing synchronous-per-frame
/// model).</summary>
public sealed record TemplateImageElement(NormalizedRect Bounds, int Z, IImageSource Source, ImageFitMode Fit)
    : TemplateElement(Bounds, Z);

/// <summary><paramref name="Opacity"/> (0..1) applies to fill and border alike — text/image
/// elements don't have their own opacity yet (not a confirmed 1.1 requirement; add if actually
/// wanted). <paramref name="BorderThickness"/> is relative to the target image's HEIGHT, same
/// convention as <see cref="FontSpec.Size"/>, so borders scale consistently across different
/// target-mode render sizes rather than looking wrong at a fixed pixel width on a small mode.
/// <paramref name="CornerRadius"/> (added later, same height-relative convention) -- the original
/// "no corner-radius, ImageSharp.Drawing 2.1.7 has no rounded-rectangle primitive" blocker was real,
/// but a major ImageSharp.Drawing upgrade (3.x, which HAS one) needs ImageSharp core 4.x too, a much
/// bigger change than this one field justifies. Built instead via a hand-built rounded-rectangle
/// <c>IPath</c> (4 <c>PathBuilder.AddArc</c> quarter-arcs + 4 straight edges, verified against a real
/// rendered PNG before use, same as every other unfamiliar-API check this project does) -- no new
/// package dependency, works against the ALREADY-installed 2.1.7. 0 (the default) renders identically
/// to the old plain-<see cref="SixLabors.ImageSharp.Drawing.RectangularPolygon"/> path, byte-for-byte
/// unchanged for every existing template.</summary>
public sealed record TemplateBoxElement(
    NormalizedRect Bounds, int Z, Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity = 1.0,
    double CornerRadius = 0)
    : TemplateElement(Bounds, Z);

/// <summary><paramref name="Elements"/> in any order — <see cref="ITransmitImagePreparer.ApplyTemplate"/>
/// sorts by <c>(Z, list index)</c> itself. <see cref="IsEmpty"/> drives the same same-instance
/// no-op convention <see cref="ImageAdjustments.IsIdentity"/> already established for
/// <see cref="ITransmitImagePreparer.ApplyAdjustments"/>.</summary>
public sealed record TemplateDocument(string? Name, IReadOnlyList<TemplateElement> Elements)
{
    public bool IsEmpty => Elements.Count == 0;
}

public interface ITransmitImagePreparer
{
    IImageSource Crop(IImageSource source, NormalizedRect region);

    /// <summary>Always returns EXACTLY <paramref name="width"/> x <paramref name="height"/> —
    /// <c>ISstvEncoder.EncodeAsync</c> throws on any dimension mismatch
    /// (<see cref="IImageFileLoader"/>'s own doc comment), so this method can never return
    /// something close-but-not-exact. <paramref name="preserveAspect"/>: true letterboxes with
    /// solid black (matches this app's own raw-instrumentation aesthetic direction, not a "smart"
    /// fill); false stretches to fill exactly.</summary>
    IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect);

    /// <summary>Must run AFTER <see cref="Resize"/> and BEFORE <see cref="ApplyOverlay"/> —
    /// judged against the FINAL framed/sized output (downsampling itself changes perceived
    /// contrast/sharpness, so adjusting a larger pre-resize source would fight the resize's own
    /// effect), and must never touch already-burned-in overlay text pixels. When
    /// <paramref name="adjustments"/>.<see cref="ImageAdjustments.IsIdentity"/>, returns
    /// <paramref name="source"/> unchanged (the same instance, not a copy) rather than performing a
    /// real ImageSharp round-trip for a no-op.</summary>
    IImageSource ApplyAdjustments(IImageSource source, ImageAdjustments adjustments);

    /// <summary>Must run AFTER <see cref="Resize"/>, not before — overlay text is rasterized at
    /// the FINAL mode dimensions, so a non-aspect-preserving "stretch" resize never
    /// smears/distorts already-drawn glyphs. Crop -&gt; Resize -&gt; ApplyAdjustments -&gt;
    /// ApplyOverlay is the only correct order.</summary>
    IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay);

    /// <summary>The successor to <see cref="ApplyOverlay"/> for the 1.1 template editor
    /// (spec/15-template-designer.md) — additive for now, <see cref="ApplyOverlay"/>/
    /// <see cref="ImageOverlay"/>/<see cref="ImageOverlayElement"/> are not yet removed (tracked
    /// for deletion once the editor VM migrates onto <see cref="TemplateDocument"/>, Phase 1).
    /// Must run in the same pipeline position <see cref="ApplyOverlay"/> did — after
    /// <see cref="Resize"/>, never before. Compositing starts from <paramref name="existingBase"/>;
    /// every element in <paramref name="document"/> is drawn on top of it in ascending
    /// <c>(Z, list index)</c> order (see <see cref="TemplateElement"/>'s own doc comment for why
    /// there's no Z=0-is-the-background special case). Adjustments
    /// (<see cref="ApplyAdjustments"/>) are NOT reapplied to any element here, including an image
    /// element that ends up covering the whole frame — adjustment sliders stay scoped to the photo
    /// being edited, applied earlier in the pipeline, by design. When
    /// <paramref name="document"/>.<see cref="TemplateDocument.IsEmpty"/>, returns
    /// <paramref name="existingBase"/> unchanged (the SAME instance, matching
    /// <see cref="ApplyAdjustments"/>'s established no-op convention) — a document whose elements
    /// are all individually skipped (zero/negative size) is a distinct case and may return a
    /// copy.</summary>
    IImageSource ApplyTemplate(IImageSource existingBase, TemplateDocument document);

    /// <summary>Measures the font size <see cref="ApplyTemplate"/>'s shrink-to-fit text rendering
    /// would actually use for <paramref name="text"/> at <paramref name="font"/>.Size inside a
    /// <paramref name="boundsWidthPx"/> x <paramref name="boundsHeightPx"/> box of a
    /// <paramref name="imageHeightPx"/>-tall target image (needed separately from the bounds
    /// dimensions since <see cref="FontSpec.Size"/> is relative to the image's height, not the
    /// box's) — lets the UI mirror the pipeline's own fit computation for WYSIWYG canvas rendering
    /// without depending on <c>SixLabors.Fonts</c> itself (same "declared in
    /// <c>ScanlineStudio.Abstractions</c> so the UI project never references the concrete package"
    /// reasoning as this whole file). Plain pixel dimensions, not a UI-framework size type — this
    /// project deliberately never references Avalonia types from
    /// <c>ScanlineStudio.Abstractions</c> either. <paramref name="strokeThicknessRelative"/>
    /// (Phase 4, default 0) mirrors <see cref="TemplateTextElement.StrokeThickness"/>'s own
    /// image-height-relative convention — a stroked glyph's ink grows past what
    /// <c>TextMeasurer.MeasureSize</c> alone reports, so callers that render a stroke MUST pass
    /// this or the canvas-side fitted size will disagree with what <see cref="ApplyTemplate"/>
    /// actually renders (Phase 4 plan-review blocker: this is exactly the kind of silent
    /// canvas/pipeline desync this method exists to prevent in the first place). Pass 0 (the
    /// default) when the element has no stroke.
    /// <para>Phase 8: <paramref name="shadowOffsetXRelative"/>/<paramref name="shadowOffsetYRelative"/>
    /// and <paramref name="rotationDegrees"/> are the THIRD occurrence of this same fit-box-shrink
    /// requirement (stroke was the first, Phase 4) — both effects push ink past what a plain-text fit
    /// search measures (shadow offsets the whole glyph; rotation's own bounding box grows for any
    /// non-zero angle), so both must be passed here whenever the element has them, for the same
    /// canvas/pipeline-desync reason as <paramref name="strokeThicknessRelative"/>. All three
    /// defaults (0) mean "this effect is not active."</para>
    /// <para>Auditor usability review follow-up (2026-08-18): <paramref name="stackStepXRelative"/>/
    /// <paramref name="stackStepYRelative"/> are the fourth occurrence, for
    /// <see cref="TemplateTextElement.StackColor"/>'s stepped-copy effect -- same reasoning, same
    /// "pass 0 when inactive" default.</para></summary>
    double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0);

    /// <summary>Font family names available for <see cref="FontSpec.Family"/>/
    /// <see cref="TemplateTextElement.Font"/> — the TX template editor's font-family picker's
    /// ItemsSource, so the UI layer doesn't need to hardcode what's actually bundled (Phase 4).
    /// First entry is the default/fallback family <see cref="ApplyTemplate"/> uses when a
    /// requested family isn't found — mirrors the existing <c>AvailableModes</c>
    /// (SSTV mode picker) pattern.</summary>
    IReadOnlyList<string> AvailableFontFamilies { get; }

    /// <summary>Rotates 90° clockwise, always -- no direction parameter. Matches the TX image
    /// editor's single-button UX (4 clicks returns to the original orientation); width/height are
    /// swapped in the result.</summary>
    IImageSource Rotate(IImageSource source);

    /// <summary>TX workflow modernization plan, Phase 7 (flatten command) -- exact, resampler-free
    /// pixel-rect extraction, unlike <see cref="Crop"/>, which takes a NORMALIZED region and rounds
    /// it. The flatten command has already computed an integer rect (matching the pipeline's own
    /// crop rounding exactly, via the caller's own shared arithmetic) and must not have it
    /// re-derived through a normalize/round round-trip that can slip a pixel. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a non-positive size or a rect that isn't fully
    /// inside <paramref name="source"/> -- deliberately NOT silently clamped like <see cref="Crop"/>,
    /// since every caller of this overload is doing precise geometry and a silent clamp would hide
    /// exactly the arithmetic bug it would otherwise mask.</summary>
    IImageSource CropPixels(IImageSource source, int x, int y, int width, int height);

    /// <summary>Opaque copy of <paramref name="overlay"/> onto a copy of <paramref name="background"/>
    /// at (<paramref name="x"/>, <paramref name="y"/>), clipped to the background's own bounds --
    /// no blending, no alpha (this namespace's <see cref="Rgb24"/> has none), no resampling. Returns
    /// a NEW instance at <paramref name="background"/>'s dimensions; neither input is mutated
    /// (<see cref="IImageSource"/> is read-only by contract). Off-canvas/negative offsets are legal
    /// and produce a partial paste, matching how <see cref="ApplyTemplate"/> already treats a
    /// partially out-of-frame element.</summary>
    IImageSource Composite(IImageSource background, IImageSource overlay, int x, int y);

    /// <summary>T1-14 (production_audit.md): the TX image editor's live preview pipeline
    /// (<c>TxImageEditorPaneViewModel.RecomputePreviewPipeline</c>, fired on every coalesced
    /// pointer-move frame) as ONE call instead of 4 -- <see cref="Crop"/> -&gt; <see cref="Resize"/>
    /// -&gt; <see cref="ApplyAdjustments"/> -&gt; <see cref="ApplyTemplate"/>, that exact order (same
    /// as <see cref="ApplyTemplate"/>'s own doc comment requires). Exists purely to let
    /// <c>TransmitImagePreparer</c> do the whole chain against ONE underlying image representation
    /// instead of converting in and out of it once per stage -- a real per-frame cost this signature
    /// itself doesn't change or need to know about (the 4 individual methods still exist, unchanged,
    /// for every other caller).
    ///
    /// <b>Default implementation</b> is the literal 4-call chain, byte-for-byte what a caller could
    /// already do by hand -- this is what a mock/fake implementation of this interface gets for free
    /// without needing its own override, and it is also the CONTRACT every real override (this
    /// project has exactly one, <c>TransmitImagePreparer</c>) must stay pixel-identical to. This
    /// method must ALSO stay pixel-identical to <c>TxImageEditorPaneViewModel.BuildFinalOutput</c>'s
    /// own separate, un-fused Crop-&gt;Resize-&gt;ApplyAdjustments-&gt;ApplyTemplate chain (the one
    /// that actually produces the transmitted image) -- the preview and the real output must never
    /// silently diverge. Pixel-exact equivalence tests are the drift guard for both of these, not
    /// just a nice-to-have regression check.</summary>
    IImageSource ComposePreview(
        IImageSource source, NormalizedRect cropRegion, int targetWidth, int targetHeight, bool preserveAspect,
        ImageAdjustments adjustments, TemplateDocument templateDocument)
    {
        var cropped = Crop(source, cropRegion);
        var resized = Resize(cropped, targetWidth, targetHeight, preserveAspect);
        var adjusted = ApplyAdjustments(resized, adjustments);
        return ApplyTemplate(adjusted, templateDocument);
    }
}
