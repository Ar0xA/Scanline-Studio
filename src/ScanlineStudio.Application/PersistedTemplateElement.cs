using System.Text.Json.Serialization;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application;

/// <summary>Persistence-layer element family for the TX template editor's saved templates
/// (spec/15-template-designer.md, Phase 5) — a DELIBERATELY separate hierarchy from both the
/// pipeline's own <see cref="TemplateElement"/> (whose image variant holds a live, non-serializable
/// <see cref="IImageSource"/>) and the UI layer's <c>RawElementSnapshot</c> family (a
/// <c>ScanlineStudio.UI</c>-only type <c>ScanlineStudio.Application</c> cannot reference — the
/// mapping between the two lives on <c>TxImageEditorPaneViewModel</c> itself, the one place both
/// shapes are reachable). X/Y/Width/Height/Z/Locked mirror <c>RawElementSnapshot</c>'s own
/// center-anchored, un-macro-resolved shape exactly.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(PersistedTextElement), "text")]
[JsonDerivedType(typeof(PersistedBoxElement), "box")]
[JsonDerivedType(typeof(PersistedImageElement), "image")]
public abstract record PersistedTemplateElement(double X, double Y, double Width, double Height, int Z, bool Locked);

/// <summary>Phase 8 (YONIQ-style text-effects follow-up) additions — <paramref name="ShadowColor"/>/
/// <paramref name="ShadowOffsetX"/>/<paramref name="ShadowOffsetY"/>/<paramref name="RotationDegrees"/>
/// are flat scalars (mechanical, additive default fields, matching <see cref="PersistedImageElement.IsBackground"/>'s
/// own already-shipped precedent — missing JSON properties on an older saved template deserialize to
/// these defaults, no migration needed). <paramref name="GradientEnabled"/>/<paramref name="GradientKind"/>/
/// <paramref name="GradientStartColor"/>/<paramref name="GradientEndColor"/> persist
/// <c>OverlayElementViewModel</c>'s own simplified 2-stop-gradient shape directly as plain scalars —
/// deliberately NOT a separate <c>PersistedGradientColorStop</c> record + a new
/// <see cref="PersistedTemplateJsonContext"/> registration (what the Phase 8 plan's own "gradient is
/// medium cost" note anticipated for an N-stop shape): since the VM itself only ever supports exactly
/// 2 stops, there is no variable-length list to serialize, and <see cref="Rgb24"/> already round-trips
/// correctly (proven by <paramref name="StrokeColor"/>/<paramref name="Color"/> above) — so this
/// sidesteps the "silent missing-registration" failure mode entirely rather than needing a dedicated
/// discriminator test for it. No mask-related fields — bitmap-mask fill's persistence side was
/// explicitly re-costed as a real outlier during Phase 8 plan-review and deferred out of this
/// pass.
/// <para><paramref name="StackColor"/>/<paramref name="StackStepX"/>/<paramref name="StackStepY"/>
/// (auditor usability review follow-up, 2026-08-18) are the same kind of trailing, defaulted,
/// missing-property-deserializes-to-default addition as everything else in this record.</para></summary>
public sealed record PersistedTextElement(
    double X, double Y, double Width, double Height, int Z, bool Locked,
    string Text, double FontSizeRelative, Rgb24 Color,
    string FontFamily, Rgb24? StrokeColor, double StrokeThickness,
    Rgb24? ShadowColor = null, double ShadowOffsetX = 0.02, double ShadowOffsetY = 0.02, double RotationDegrees = 0,
    bool GradientEnabled = false, TextGradientKind GradientKind = TextGradientKind.Horizontal,
    Rgb24? GradientStartColor = null, Rgb24? GradientEndColor = null,
    bool Bold = false, bool Italic = false,
    Rgb24? StackColor = null, double StackStepX = 0.02, double StackStepY = 0.02)
    : PersistedTemplateElement(X, Y, Width, Height, Z, Locked);

/// <summary><paramref name="CornerRadius"/> (auditor usability review follow-up, 2026-08-18) is a
/// trailing, defaulted scalar -- same "missing JSON property on an older saved template
/// deserializes to the default, no migration needed" convention as <see cref="PersistedTextElement"/>'s
/// own Phase 8 additions above.</summary>
public sealed record PersistedBoxElement(
    double X, double Y, double Width, double Height, int Z, bool Locked,
    Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity, double CornerRadius = 0)
    : PersistedTemplateElement(X, Y, Width, Height, Z, Locked);

/// <summary>Persistence-layer counterpart to <c>TxImageEditorPaneViewModel.ImageSourceKind</c> (a
/// UI-layer nested type this project cannot reference from here) — informational only, recording
/// WHERE an image element's pixels originally came from. Never used to re-resolve pixels on load
/// (see <see cref="PersistedImageElement.AssetFileName"/>'s own doc comment for why); kept only so a
/// possible future "re-resolve from RX history" feature has something to read.</summary>
public enum PersistedImageSourceKind { File, RxHistory, LastRx }

/// <summary><paramref name="AssetFileName"/> is a generated GUID-based name (e.g.
/// <c>"3f9c2b1a....png"</c>), relative to the template's own <c>assets/</c> folder — deliberately
/// NOT derived from the element's list index (plan-review finding: index-derived names are fragile
/// against any future reordering/filtering between writing the manifest and writing assets, or a
/// partial/failed save). <paramref name="OriginKind"/>/<paramref name="OriginPayload"/> record the
/// ORIGINAL source (informational only — see <see cref="PersistedImageSourceKind"/>'s own doc
/// comment); the actual pixels backing this element are always the real, self-contained file copy at
/// <paramref name="AssetFileName"/>, embedded at save time regardless of origin (a <c>File</c> path
/// can move, an RX-history entry can be pruned, <c>LastRx</c> is inherently ephemeral — none of the
/// three origins are safe to re-resolve from days/weeks later).</summary>
public sealed record PersistedImageElement(
    double X, double Y, double Width, double Height, int Z, bool Locked,
    string AssetFileName, ImageFitMode Fit, PersistedImageSourceKind OriginKind, string? OriginPayload,
    bool IsBackground = false)
    : PersistedTemplateElement(X, Y, Width, Height, Z, Locked);

/// <summary>What <see cref="ITemplateStore.SaveAsync"/> accepts and <see cref="ITemplateStore.LoadAsync"/>
/// returns — just the element list. Name/id/timestamp live on <see cref="TemplateMetadata"/> instead
/// (Save takes name as its own parameter; Load's caller already knows the id it asked for).</summary>
public sealed record PersistedTemplateDocument(IReadOnlyList<PersistedTemplateElement> Elements);

/// <summary><paramref name="ThumbnailPath"/> is an absolute path to a pre-rendered
/// <c>thumbnail.png</c> (rendered once at save time, see <see cref="ITemplateStore.SaveAsync"/>'s own
/// doc comment — never recomputed on every <see cref="ITemplateStore.ListAsync"/> call).</summary>
public sealed record TemplateMetadata(string Id, string Name, DateTimeOffset SavedAt, string ThumbnailPath);

[JsonSerializable(typeof(PersistedTemplateElement))]
[JsonSerializable(typeof(PersistedTextElement))]
[JsonSerializable(typeof(PersistedBoxElement))]
[JsonSerializable(typeof(PersistedImageElement))]
[JsonSerializable(typeof(TemplateManifest))]
public sealed partial class PersistedTemplateJsonContext : JsonSerializerContext;

/// <summary>The actual on-disk shape of <c>template.json</c> — <see cref="TemplateStore"/>'s own
/// implementation detail (id/name/timestamp + elements folded into one file); public only because
/// the source-generated <see cref="PersistedTemplateJsonContext"/> requires it (a <c>JsonTypeInfo&lt;T&gt;</c>
/// property can't be less accessible than its own type) — callers of <see cref="ITemplateStore"/>
/// only ever see <see cref="PersistedTemplateDocument"/>/<see cref="TemplateMetadata"/> separately,
/// per that interface's own decided public shape.</summary>
public sealed record TemplateManifest(string Id, string Name, DateTimeOffset SavedAt, IReadOnlyList<PersistedTemplateElement> Elements);
