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

/// <summary>TX editor gap-items plan, line element (2026-09-01) -- shared ink-inflation math both
/// `TemplateStore` (Application layer, its own thumbnail-render reconstruction path,
/// `ToTemplateElementAsync`) and the TX editor VM (UI layer, `BuildTemplateElement`) need to compute
/// IDENTICALLY. Declared here rather than duplicated as two independently-maintained formulas -- the
/// exact drift class this project has been bitten by before (`TransmitImageLimits`'s own doc comment
/// above states the same reasoning; see also `PersistedBoxElement`'s own gradient-thumbnail-drop
/// precedent, where a SECOND reconstruction site silently missed a field the first one had).</summary>
public static class TemplateLineGeometry
{
    /// <summary>The line's endpoint bounding box, INFLATED by half the stroke's own ink extent per
    /// axis -- what keeps <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s shared degenerate-bbox
    /// skip rule (<see cref="TemplateElement"/>'s own doc comment: zero/negative Width/Height is
    /// skipped, not rendered) from dropping an axis-aligned line with a real, positive thickness.
    /// <paramref name="thickness"/> is HEIGHT-relative (same convention as
    /// <see cref="TemplateBoxElement.BorderThickness"/>), so the X-axis inflation converts through
    /// <paramref name="imageWidthPx"/>/<paramref name="imageHeightPx"/>'s own ratio -- a bare
    /// <c>thickness/2</c> applied to BOTH axes would over-inflate horizontally on any non-square
    /// target (line-element plan-review round 2/3 finding). A zero/negative
    /// <paramref name="imageWidthPx"/> OR <paramref name="imageHeightPx"/> falls back to the
    /// un-converted (height-relative) half-thickness for the X axis too, rather than computing a
    /// zero/negative conversion factor -- an unreachable input in practice (every real caller has
    /// positive target dimensions), kept only so this stays a total function. A negative
    /// <paramref name="thickness"/> clamps to 0 (code-review finding: matches
    /// <see cref="TransmitImagePreparer"/>'s own <c>MathF.Max(0f, ...)</c> clamp on the SAME field --
    /// the two must agree, or a negative thickness would inflate Bounds as if ink existed while the
    /// pipeline itself draws nothing).</summary>
    public static NormalizedRect ComputeInflatedBounds(double x1, double y1, double x2, double y2, double thickness, double imageWidthPx, double imageHeightPx)
    {
        var halfThicknessHeightRelative = Math.Max(0, thickness) / 2;
        var halfThicknessWidthRelative = imageWidthPx > 0 && imageHeightPx > 0
            ? halfThicknessHeightRelative * (imageHeightPx / imageWidthPx)
            : halfThicknessHeightRelative;
        var minX = Math.Min(x1, x2) - halfThicknessWidthRelative;
        var maxX = Math.Max(x1, x2) + halfThicknessWidthRelative;
        var minY = Math.Min(y1, y2) - halfThicknessHeightRelative;
        var maxY = Math.Max(y1, y2) + halfThicknessHeightRelative;
        return new NormalizedRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Element rotation (2026-09-20) -- rotates a line's endpoints around their own midpoint
    /// by <paramref name="clockwiseDegrees"/>, clockwise-positive (same convention as
    /// <see cref="TemplateTextElement.RotationDegrees"/>). Unlike Box/Image (which rotate at RENDER
    /// time in real destination-pixel space, see <see cref="TemplateImageElement.RotationDegrees"/>),
    /// a line has no separate "content" from its own endpoints, so this is applied ONCE, directly to
    /// X1/Y1/X2/Y2, at command time -- no persisted angle exists on <see cref="TemplateLineElement"/>
    /// or <c>LineElementViewModel</c> (that type's own doc comment explains why: a persisted angle
    /// would have no well-defined zero-reference once a user manually drags an endpoint).
    /// <para>MUST rotate in real PIXEL space, not the normalized space <paramref name="x1"/>..
    /// <paramref name="y2"/> arrive in -- X is width-relative and Y is height-relative (same
    /// convention as <see cref="ComputeInflatedBounds"/>'s own thickness conversion above), so
    /// rotating the normalized delta directly changes the line's real rendered length and angle on
    /// any non-square target (a naive rotation of a 0.6-wide horizontal line on a 320x256 target
    /// comes out 20% short). Convert to pixels using the CALLER's own <paramref name="imageWidthPx"/>/
    /// <paramref name="imageHeightPx"/> (canvas/working-copy space at command time -- see
    /// <c>TxImageEditorPaneViewModel</c>'s own rotate-command implementation for the caller-side
    /// coordinate-space note), rotate there, then convert back.</para>
    /// <para>Code-review finding (2026-09-20): this real-pixel-space conversion uses the CANVAS's
    /// own working-copy aspect, not the final transmitted-image aspect Box/Image rotate against at
    /// render time. The two coincide exactly whenever <c>PreserveAspect</c> is on (the default,
    /// letterboxed case -- verified by direct algebra through <c>ProjectRectToCropRelative</c>/
    /// <c>TryGetCropContentMetrics</c>) but can diverge when <c>PreserveAspect</c> is off AND the
    /// crop is not the full frame, in which case a "90°" line rotation reads as slightly off-90° in
    /// the transmitted output even though it matches the canvas preview exactly -- a stated,
    /// accepted tradeoff for that combination, not a bug.</para></summary>
    public static (double X1, double Y1, double X2, double Y2) RotateEndpoints(
        double x1, double y1, double x2, double y2, double clockwiseDegrees, double imageWidthPx, double imageHeightPx)
    {
        if (imageWidthPx <= 0 || imageHeightPx <= 0)
        {
            return (x1, y1, x2, y2);
        }

        var midX = (x1 + x2) / 2;
        var midY = (y1 + y2) / 2;
        var theta = clockwiseDegrees * Math.PI / 180;
        var cos = Math.Cos(theta);
        var sin = Math.Sin(theta);

        (double X, double Y) RotatePoint(double x, double y)
        {
            var dxPx = (x - midX) * imageWidthPx;
            var dyPx = (y - midY) * imageHeightPx;
            var dxPxRotated = (dxPx * cos) - (dyPx * sin);
            var dyPxRotated = (dxPx * sin) + (dyPx * cos);
            return (midX + (dxPxRotated / imageWidthPx), midY + (dyPxRotated / imageHeightPx));
        }

        var (rx1, ry1) = RotatePoint(x1, y1);
        var (rx2, ry2) = RotatePoint(x2, y2);
        return (rx1, ry1, rx2, ry2);
    }
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
/// Regular-only behavior, unchanged. The two BUNDLED families always have a real Bold/Italic/
/// BoldItalic variant registered (see <c>TransmitImagePreparer</c>'s own constructor for exactly
/// which weight/style files are loaded per family), so a Bold/Italic combination never falls back
/// for them. <paramref name="Family"/> can also name an OS-installed SYSTEM font (added later,
/// discovered via <c>FontCollectionExtensions.AddSystemFonts</c>) -- an arbitrary system font may
/// have no real face for the requested style, which silently falls back to Regular rather than
/// throwing (<c>TransmitImagePreparer.ResolveAvailableStyle</c>).</summary>
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
/// degenerate one-color gradient.
/// <para>Code-review finding (2026-09-01, box gradient fill): custom <see cref="Equals(TextGradient?)"/>/
/// <see cref="GetHashCode"/> are REQUIRED, not cosmetic -- a record's auto-generated equality compares
/// an <see cref="IReadOnlyList{T}"/>-typed property via <c>EqualityComparer&lt;T&gt;.Default</c>, which
/// for an interface type falls back to REFERENCE equality (the interface itself declares no
/// <c>Equals</c> override). <c>BuildTemplateElement</c>'s own gradient composition mints a fresh
/// <see cref="Stops"/> array on every call (a C# collection expression, never the same instance
/// twice), so without this override, two structurally-identical <see cref="TextGradient"/>s built a
/// moment apart from the SAME element never compare equal -- <c>Flatten</c>'s own stale-generation
/// check (<c>!BuildTemplateElement(element).Equals(request.Element)</c>) always sees them as
/// "changed," discarding every flatten on a gradient text or box element as stale, unconditionally.</para></summary>
public sealed record TextGradient(TextGradientKind Kind, IReadOnlyList<GradientColorStop> Stops)
{
    public bool Equals(TextGradient? other) => other is not null && Kind == other.Kind && Stops.SequenceEqual(other.Stops);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        foreach (var stop in Stops)
        {
            hash.Add(stop);
        }

        return hash.ToHashCode();
    }
}

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
/// byte-for-byte port of its GDI-specific color-interpolation/shadow-mode-coupling.</para>
/// <para>TX editor gap-items plan, item 4b (picture fill): <paramref name="BitmapFill"/> is an
/// already-resolved <see cref="IImageSource"/>, same "resolved before hitting the pipeline"
/// convention as <see cref="TemplateImageElement.Source"/> -- never re-loaded/re-decoded here.
/// STABLE-INSTANCE INVARIANT: every caller composing this record must pass the SAME cached
/// <see cref="IImageSource"/> instance across a template's own edit session (never a freshly
/// re-loaded copy per frame) -- this record's auto-generated equality falls back to REFERENCE
/// equality on this interface-typed member (the exact class of bug <see cref="TextGradient"/>'s own
/// doc comment documents for its <c>Stops</c> list), so a fresh instance every call would make
/// Flatten's own stale-result guard never match a picture-filled text element, discarding every
/// flatten of one as stale. PRECEDENCE INVARIANT: when both <see cref="BitmapFill"/> and
/// <see cref="Gradient"/> are non-null (representable via a hand-edited/shared template file, since
/// nothing here enforces mutual exclusivity at the data level), <see cref="BitmapFill"/> wins -- this
/// rule is stated ONCE here and must be enforced identically at every site that COMPOSES this
/// record (the live pipeline, the persisted-template thumbnail-reconstruction path, and the editor's
/// own canvas preview), not just wherever a caller happens to check first.</para>
/// <para>User-requested (2026-09-15): <paramref name="GrowToFillEnabled"/> opts a single element into
/// searching ABOVE <see cref="FontSpec.Size"/>, not just below it, when its box has room -- see
/// <c>OverlayElementViewModel.GrowToFillEnabled</c>'s own doc comment for the legacy precedent
/// (<c>CDrawText::Move</c> scales font size proportionally with box-drag in both directions) and why
/// this stays opt-in rather than becoming the new default search behavior.</para></summary>
public sealed record TemplateTextElement(
    NormalizedRect Bounds, int Z, string Content, FontSpec Font, Rgb24 Color,
    Rgb24? StrokeColor = null, double StrokeThickness = 0,
    Rgb24? ShadowColor = null, double ShadowOffsetX = 0, double ShadowOffsetY = 0,
    double RotationDegrees = 0, TextGradient? Gradient = null,
    Rgb24? StackColor = null, double StackStepX = 0, double StackStepY = 0,
    IImageSource? BitmapFill = null, bool GrowToFillEnabled = false)
    : TemplateElement(Bounds, Z);

/// <summary><paramref name="Source"/> is an already-resolved <see cref="IImageSource"/>, not a
/// deferred binding token — last-received-image/RX-history/file/clipboard resolution happens at
/// the VM/Application layer, before a <see cref="TemplateDocument"/> is built, so
/// <see cref="ITransmitImagePreparer.ApplyTemplate"/> stays synchronous and I/O-free (matches
/// <c>TxImageEditorPaneViewModel.RecomputePreview</c>'s existing synchronous-per-frame
/// model).
/// <para>Element rotation (2026-09-20): <paramref name="RotationDegrees"/> is in-plane (2D)
/// rotation, clockwise-positive, same convention as <see cref="TemplateTextElement.RotationDegrees"/>.
/// PRECEDENCE INVARIANT, same null-means-none/one-stated-rule discipline as
/// <see cref="TemplateTextElement.BitmapFill"/> vs <see cref="TemplateTextElement.Gradient"/>: when
/// both <paramref name="Perspective"/> and a non-zero <paramref name="RotationDegrees"/> are set on
/// the same element (only reachable via a hand-edited/older template file — the live editor never
/// lets you set both), <paramref name="Perspective"/> wins. Enforced identically at every
/// composing/rendering site (the live pipeline, <c>TemplateStore</c>'s thumbnail-reconstruction
/// path, and the editor's own canvas preview), not just wherever a caller happens to check
/// first.</para></summary>
public sealed record TemplateImageElement(NormalizedRect Bounds, int Z, IImageSource Source, ImageFitMode Fit, PerspectiveCorners? Perspective = null, double RotationDegrees = 0)
    : TemplateElement(Bounds, Z);

/// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- 8 FLAT doubles,
/// not an array/list/tuple collection -- a record struct's auto-generated equality already compares
/// every field (including a nested value type) by value, but a COLLECTION-typed member falls back to
/// REFERENCE equality, the exact <see cref="TextGradient"/>-<c>Stops</c>/<see cref="TemplateTextElement"/>-<c>BitmapFill</c>
/// trap this session has already hit twice: <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s
/// caller-side Flatten stale-result guard compares two composed <see cref="TemplateElement"/>
/// instances with <c>Equals</c>, and a reference-equality member there would make every flatten of a
/// warped element discarded as stale, unconditionally. Corner winding is TopLeft/TopRight/
/// BottomRight/BottomLeft (0/1/2/3), same as <see cref="TemplateLineElement"/>'s own flat-scalar
/// X1/Y1/X2/Y2 precedent, generalized from 2 points to 4. Positions are in the SAME normalized space
/// as <see cref="TemplateElement.Bounds"/> (destination-image-space at render time; the editor VM's
/// own corners are canvas-normalized full-working-copy space, PROJECTED into this space via
/// <c>TxImageEditorPaneViewModel.ProjectRectToCropRelative</c>, one call per corner, the same
/// technique <c>BuildTemplateLineElement</c> already uses for its own 2 endpoints -- see that
/// method's own doc comment).</summary>
public readonly record struct PerspectiveCorners(
    double Corner0X, double Corner0Y, double Corner1X, double Corner1Y,
    double Corner2X, double Corner2Y, double Corner3X, double Corner3Y)
{
    /// <summary>Bounding box of the 4 corners, in whatever space they're already in -- the ONE
    /// shared helper every caller (the live pipeline, on PROJECTED corners; <c>TemplateStore</c>'s
    /// own thumbnail-reconstruction path, on RAW persisted corners, no projection -- that path has
    /// no crop-rect concept at all) must use, so they can never independently compute a slightly
    /// different bbox and drift apart -- the exact same "one shared function" discipline
    /// <see cref="TemplateLineElement"/>'s own doc comment already mandates for its own
    /// <c>TemplateLineGeometry.ComputeInflatedBounds</c>.</summary>
    public NormalizedRect ToBoundingBox()
    {
        var minX = Math.Min(Math.Min(Corner0X, Corner1X), Math.Min(Corner2X, Corner3X));
        var maxX = Math.Max(Math.Max(Corner0X, Corner1X), Math.Max(Corner2X, Corner3X));
        var minY = Math.Min(Math.Min(Corner0Y, Corner1Y), Math.Min(Corner2Y, Corner3Y));
        var maxY = Math.Max(Math.Max(Corner0Y, Corner1Y), Math.Max(Corner2Y, Corner3Y));
        return new NormalizedRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- moved here
    /// from <c>TransmitImagePreparer</c> (was <c>private static</c> in <c>ScanlineStudio.Core.Imaging</c>,
    /// which <c>ScanlineStudio.UI</c> cannot reference -- <c>UiLayeringArchitectureTests</c> bans it)
    /// so the TX editor's own real-time corner-drag clamp can call the EXACT SAME check the render
    /// path uses, not a second, independently-drifting copy -- the ONE-shared-helper discipline
    /// <see cref="ToBoundingBox"/> itself already established. Logic unchanged from the original.
    /// <para>The normalized cross product at each of the 4 vertices (a dimensionless sin-of-interior-
    /// angle, scale-invariant BY CONSTRUCTION -- a raw, un-normalized cross product scales as edge-
    /// length-squared, so a small legitimately-square element and a large near-degenerate quad can't
    /// be compared against the same fixed threshold correctly), plus a separate ABSOLUTE edge-length
    /// floor (rejects near-coincident vertices specifically, which would otherwise make the
    /// normalized ratio an unstable 0/0 -- NOT scale-invariant, callers in a different coordinate
    /// scale than pixels should convert first). All 4 must share the same sign AND clear the angular
    /// epsilon for the quad to be accepted.</para></summary>
    public bool IsConvexAndWellFormed()
    {
        Span<(double X, double Y)> pts = [(Corner0X, Corner0Y), (Corner1X, Corner1Y), (Corner2X, Corner2Y), (Corner3X, Corner3Y)];
        const double minEdgeLength = 1e-3;
        const double minAngleSin = 1e-3;
        double? sign = null;
        for (var i = 0; i < 4; i++)
        {
            var prev = pts[(i + 3) % 4];
            var curr = pts[i];
            var next = pts[(i + 1) % 4];
            var inX = curr.X - prev.X;
            var inY = curr.Y - prev.Y;
            var outX = next.X - curr.X;
            var outY = next.Y - curr.Y;
            var inLen = Math.Sqrt((inX * inX) + (inY * inY));
            var outLen = Math.Sqrt((outX * outX) + (outY * outY));
            if (inLen < minEdgeLength || outLen < minEdgeLength)
            {
                return false;
            }

            var cross = (inX * outY) - (inY * outX);
            var normalized = cross / (inLen * outLen);
            if (Math.Abs(normalized) < minAngleSin)
            {
                return false;
            }

            var thisSign = Math.Sign(normalized);
            if (sign is null)
            {
                sign = thisSign;
            }
            else if (sign != thisSign)
            {
                return false;
            }
        }

        return true;
    }
}

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
/// <summary><paramref name="Gradient"/> (TX editor gap-items plan, box gradient fill, 2026-09-01)
/// -- reuses <see cref="TextGradient"/>/<see cref="TextGradientKind"/> verbatim rather than a
/// separate box-specific gradient type: the shape (Kind + 2-stop-or-empty color list) is already
/// generic, and <see cref="ITransmitImagePreparer"/>'s own <c>BuildGradientBrush</c> implementation
/// already takes wherever-it's-drawn bounds + a fallback color, with no text-specific assumption --
/// confirmed by reading it before reusing it, not assumed from the type name alone. Null means a
/// plain solid <see cref="FillColor"/> fill (today's existing behavior, unchanged) -- same
/// null-means-none convention <see cref="TemplateTextElement.Gradient"/> already established.
/// <para>Element rotation (2026-09-20): <paramref name="RotationDegrees"/> is in-plane (2D)
/// rotation, clockwise-positive, same convention as <see cref="TemplateTextElement.RotationDegrees"/>.
/// Same Perspective-wins precedence invariant as <see cref="TemplateImageElement.RotationDegrees"/>'s
/// own doc comment states -- see it for the full rule, stated once, not duplicated here.</para></summary>
public sealed record TemplateBoxElement(
    NormalizedRect Bounds, int Z, Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity = 1.0,
    double CornerRadius = 0, TextGradient? Gradient = null, PerspectiveCorners? Perspective = null,
    // Legacy `.mtm` import -- plan-review finding: legacy's plain CM_BOX draws an outline with NO
    // fill (GetStockObject(NULL_BRUSH)), which this model could not express at all before this field
    // existed. Trailing/defaulted true so every existing caller (nobody sets this) keeps today's
    // "always opaque fill" behavior byte-for-byte unchanged -- chosen over a nullable FillColor
    // (plan-review: cheaper, touches fewer call sites, no discriminator/fallback-color ambiguity for
    // a gradient-enabled box with no fill).
    bool FillEnabled = true, double RotationDegrees = 0)
    : TemplateElement(Bounds, Z);

/// <summary>TX editor gap-items plan, line element (2026-09-01) -- a 4th element kind, a single
/// straight stroke between two points. <paramref name="X1"/>/<paramref name="Y1"/>/
/// <paramref name="X2"/>/<paramref name="Y2"/> are the endpoints in the SAME normalized space as
/// <see cref="TemplateElement.Bounds"/> (X/X-extent width-relative, Y/Y-extent height-relative,
/// per <c>NormalizedRect</c>'s own convention) -- deliberately NOT re-derived from
/// <paramref name="Bounds"/> at render time, since a perfectly horizontal or vertical line's own
/// bounding box collapses one axis to zero, which the endpoints alone don't. <paramref name="Bounds"/>
/// itself must therefore be the endpoint bounding box INFLATED by half the stroke's own ink extent --
/// every caller that constructs one of these MUST compute it via the shared
/// <see cref="TemplateLineGeometry.ComputeInflatedBounds"/>, not its own re-derivation --
/// <c>TemplateStore.ToTemplateElementAsync</c> is one real caller, and the TX editor VM's own
/// template-building path is another, both required to agree. This is
/// what keeps <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s shared degenerate-bbox skip rule
/// (see <see cref="TemplateElement"/>'s own doc comment: zero/negative <c>Width</c>/<c>Height</c>
/// is skipped, not rendered) from silently dropping an axis-aligned line with a real, positive
/// thickness. <paramref name="Thickness"/> is relative to the target image's HEIGHT, same
/// convention as <see cref="TemplateBoxElement.BorderThickness"/>. No fill, no gradient, no corner
/// radius -- a line has nothing to fill.</summary>
public sealed record TemplateLineElement(
    NormalizedRect Bounds, int Z, double X1, double Y1, double X2, double Y2,
    Rgb24 StrokeColor, double Thickness, double Opacity = 1.0)
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
    /// "pass 0 when inactive" default.</para>
    /// <para>User-requested (2026-09-15): <paramref name="growToFill"/> mirrors
    /// <see cref="TemplateTextElement.GrowToFillEnabled"/> -- see that field's own doc comment.
    /// Defaults <see langword="false"/> so every existing call site keeps today's shrink-only
    /// behavior unchanged.</para></summary>
    double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx, double strokeThicknessRelative = 0,
        double shadowOffsetXRelative = 0, double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0, bool growToFill = false);

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

    /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- renders ONE
    /// element's own content (an image's <c>Source</c>, or a box's fill+border+corner-radius+gradient
    /// rasterized flat first) warped into a <paramref name="targetWidthPx"/> x
    /// <paramref name="targetHeightPx"/> bitmap, for the TX editor's own live canvas preview during a
    /// corner drag -- a genuinely different concern from <see cref="ApplyTemplate"/> (which composes
    /// EVERY element onto the full, already-cropped destination canvas): this renders exactly one
    /// element's own content, at whatever pixel size the CANVAS (not the final transmitted image)
    /// currently displays it at, with no crop-projection or Z-ordering involved at all -- the caller
    /// builds a throwaway <paramref name="element"/> whose own <c>Bounds</c>/<c>Perspective</c> corners
    /// need only be MUTUALLY CONSISTENT with each other (this method normalizes the corners relative
    /// to their own bounding box internally, as its first step, so the caller never needs to
    /// pre-normalize or know this method's own internal coordinate convention) -- NOT crop-projected,
    /// NOT full-canvas-normalized, just "this element's own corners, in the same space as its own
    /// Bounds." <paramref name="element"/>.<c>Perspective</c> must be non-null (an unwarped element has
    /// no reason to call this at all -- callers only reach for this while a corner-drag is actually
    /// live).
    /// <para>Returns <see cref="BgraPixelBuffer"/> (real, PREMULTIPLIED alpha), not
    /// <see cref="IImageSource"/> (this namespace's Rgb24, alpha-less, wrong for this call: a warped
    /// rounded-corner box's own true silhouette is warped ARCS, not a plain quadrilateral -- an
    /// Avalonia-side polygon clip cannot represent that, confirmed empirically before this shape was
    /// settled on; real alpha is what makes the silhouette correct uniformly for both a plain warped
    /// image and a warped rounded box, with zero clip-geometry code on the UI side at all).</para>
    /// <para><b>Default implementation</b> degrades gracefully, never throws -- exactly what a fake/mock
    /// implementation of this interface (this project's test doubles) gets for free without its own
    /// override, matching <see cref="ComposePreview"/>'s own established precedent for this exact
    /// problem. For a <see cref="TemplateImageElement"/>: a plain (unwarped) <see cref="Resize"/> to
    /// the target size, opaque-alpha-expanded into a <see cref="BgraPixelBuffer"/> -- fit-mode-
    /// APPROXIMATE, not a byte-for-byte match of the real warp (a fake has no reason to implement real
    /// perspective math). For a <see cref="TemplateBoxElement"/> (no source image to resize): a flat
    /// buffer solid-filled with the element's own <c>FillColor</c> (border/corner-radius/gradient
    /// ignored in this fallback specifically), or fully TRANSPARENT when
    /// <see cref="TemplateBoxElement.FillEnabled"/> is <see langword="false"/> -- the real, production
    /// <c>TransmitImagePreparer</c> always overrides this with the true warp; this default only matters
    /// to a test double that never exercises perspective rendering directly.</para></summary>
    /// <remarks><paramref name="styleImageHeightPx"/> is the full output image height mapped into
    /// the local preview bitmap's pixel scale, including crop/zoom/resolution limits. It is the
    /// reference length for height-relative border and radius styles, not the element height.
    /// Omission retains the local-target-height convention for callers without output context.</remarks>
    BgraPixelBuffer RenderWarpedElementPreview(TemplateElement element, int targetWidthPx, int targetHeightPx, double? styleImageHeightPx = null)
    {
        targetWidthPx = Math.Max(1, targetWidthPx);
        targetHeightPx = Math.Max(1, targetHeightPx);
        if (element is TemplateImageElement image)
        {
            var resized = Resize(image.Source, targetWidthPx, targetHeightPx, preserveAspect: false);
            return BgraPixelBuffer.FromOpaqueSource(resized);
        }

        if (element is TemplateBoxElement box)
        {
            return box.FillEnabled
                ? BgraPixelBuffer.FromSolidColor(box.FillColor, targetWidthPx, targetHeightPx)
                : BgraPixelBuffer.FromTransparent(targetWidthPx, targetHeightPx);
        }

        return BgraPixelBuffer.FromSolidColor(new Rgb24(0, 0, 0), targetWidthPx, targetHeightPx);
    }
}

/// <summary>TX editor gap-items plan, item 3 (perspective transform) -- a small, real-alpha pixel
/// buffer, deliberately NOT growing <see cref="IImageSource"/> itself (every other call site in this
/// codebase already assumes that type is alpha-less Rgb24; adding alpha to it would be a much larger,
/// unrelated change). BGRA byte order matches Avalonia's own <c>PixelFormat.Bgra8888</c> (the UI-side
/// blit target), so the UI layer's own conversion is a straight byte copy, not a channel-reorder.
/// <b>Alpha is PREMULTIPLIED</b> -- Avalonia's <c>WriteableBitmap</c> with
/// <c>AlphaFormat.Premul</c> requires it; ImageSharp's own <c>Rgba32</c> is STRAIGHT (unpremultiplied)
/// alpha, so every real producer of this type must explicitly premultiply at the boundary (verified
/// empirically before this shape was settled on, not assumed from either library's own
/// documentation).</summary>
public sealed class BgraPixelBuffer
{
    public required byte[] Pixels { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public static BgraPixelBuffer FromOpaqueSource(IImageSource source)
    {
        var pixels = new byte[source.Width * source.Height * 4];
        for (var y = 0; y < source.Height; y++)
        {
            var row = source.GetScanline(y);
            var rowOffset = y * source.Width * 4;
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = row[x];
                var offset = rowOffset + (x * 4);
                pixels[offset] = pixel.B;
                pixels[offset + 1] = pixel.G;
                pixels[offset + 2] = pixel.R;
                pixels[offset + 3] = 255;
            }
        }

        return new BgraPixelBuffer { Pixels = pixels, Width = source.Width, Height = source.Height };
    }

    public static BgraPixelBuffer FromSolidColor(Rgb24 color, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        return new BgraPixelBuffer { Pixels = pixels, Width = width, Height = height };
    }

    /// <summary>All-zero premultiplied BGRA -- fully transparent, not merely alpha-0-with-arbitrary-
    /// color (irrelevant here since alpha 0 makes color unobservable either way, but zero-init is
    /// simplest and matches every other "nothing to show" buffer in this class). Legacy `.mtm` import
    /// -- a <see cref="TemplateBoxElement"/> with <see cref="TemplateBoxElement.FillEnabled"/> false
    /// (an outline-only legacy box) has nothing to paint here; the real border still draws separately.</summary>
    public static BgraPixelBuffer FromTransparent(int width, int height) =>
        new() { Pixels = new byte[width * height * 4], Width = width, Height = height };
}
