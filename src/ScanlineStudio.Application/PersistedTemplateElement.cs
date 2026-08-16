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

public sealed record PersistedTextElement(
    double X, double Y, double Width, double Height, int Z, bool Locked,
    string Text, double FontSizeRelative, Rgb24 Color,
    string FontFamily, Rgb24? StrokeColor, double StrokeThickness)
    : PersistedTemplateElement(X, Y, Width, Height, Z, Locked);

public sealed record PersistedBoxElement(
    double X, double Y, double Width, double Height, int Z, bool Locked,
    Rgb24 FillColor, Rgb24? BorderColor, double BorderThickness, double Opacity)
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
