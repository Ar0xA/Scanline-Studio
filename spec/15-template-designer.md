# Template / QSL Designer

## Related

[[07-image-pipeline]] (this subsystem produces `ImageOverlay`-like content, at much greater scope) · [[11-plugin-system]] (CItems is this subsystem's plugin surface, not a general-purpose one) · replaces `Draw.cpp`/`Draw.h`, the `.mtm` file format, `PARALIST.BIN`, `CItems/`

## Status: specified, deferred past v1

This document exists so the legacy template designer is an **explicit, scoped deferral** rather than a silent gap — an earlier draft of the spec set folded this entirely into [[07-image-pipeline]]'s minimal `ImageOverlay` type, which covers positioned text only and materially understates what the legacy feature does. See [[14-roadmap]] for sequencing and [docs/removed-features.md](../docs/removed-features.md) for user-facing impact until this ships.

## What it actually is

`Draw.cpp` (134KB — plausibly the second-largest single subsystem in the legacy codebase after the SSTV DSP core) implements a small vector-graphics/object-composition engine used to design QSL-card-style overlays and full custom TX templates, independent of the simpler macro-key text overlay covered in [[07-image-pipeline]]. Class hierarchy (`Draw.h`):

```
CDraw                       // base drawable object
├─ CDrawLine
├─ CDrawBox
│  ├─ CDrawBoxS
│  │  └─ CDrawTitle
│  └─ CDrawText             // via CDrawBoxS
│  ├─ CDrawPic               // embedded raster image
│  ├─ CDrawOle               // embedded OLE object — see open question below
│  ├─ CDrawLib               // reference to a library/resource item
│  └─ CDrawGroup             // composite of other CDraw objects
CPolygon                    // standalone, not CDraw-derived
CGrid                       // snap/alignment grid, not a drawable
CLIBL                       // library list / item registry
```

i.e. a real (if small) scene graph: lines, boxes, styled text, embedded pictures, embedded OLE objects, grouping, and a library-item reference mechanism, composited onto a canvas and rendered into the TX/RX image pipeline.

## Persisted format (`.mtm`)

Legacy templates persist as `.mtm` files (`def1.mtm`–`def5.mtm`, `t1.mtm`–`t5.mtm`, `Stock/List.mtm`, `Current.mtm`) — a custom binary format, not currently fully reverse-engineered as of this document. What's confirmed by direct inspection (`od`/`xxd` against `def1.mtm`): a fixed-layout binary prologue (32 bytes observed identically across sampled files) followed by a sequence of records; embedded ASCII strings including `"CQ SSTV"` and `"Comic Sans MS"` (a font name, consistent with `CDrawText` styling) appear later in the file. `PARALIST.BIN` shares an identical prologue layout with `def1.mtm`, suggesting it's either a `.mtm`-format file under a different name or a closely related sibling format — not yet confirmed which. `Draw.cpp:510`'s `LoadString(TStream*, AnsiString&)` reads locale-encoded strings directly out of these binaries, meaning **any text harvested from `.mtm` files is subject to the same encoding rule as everything else** (CP932 for Japanese-origin content — see CLAUDE.md) — do not assume UTF-8 when parsing template strings.

**This format is not fully specified here.** Before implementation starts on this subsystem, the prerequisite work is a proper reverse-engineering pass (ideally cross-checked against `Draw.cpp`'s own `Load`/`Save` methods for each `CDraw` subtype, which is the authoritative source for field layout, rather than inferring purely from hex dumps).

## CItems as this subsystem's plugin surface

[[11-plugin-system]] originally and incorrectly claimed no legacy plugin precedent existed; `CItems/` (documented in `CItems/ECUSTOM.TXT`) is in fact a DLL-based extension mechanism, but it's scoped to *this* subsystem specifically — a CItems DLL is a loadable custom drawable that participates in the template designer's scene graph, not a general application plugin. The rewrite's successor extension point (a future `ITemplateItem` interface, following the same `IYoniqPlugin` pattern as every other extension point in [[11-plugin-system]]) belongs here, not in the general plugin document. Native CItems DLLs (Win32, C++Builder ABI) are not binary-portable to a cross-platform managed host regardless — see [docs/removed-features.md](../docs/removed-features.md).

## Proposed shape (once undeferred)

Mirrors the legacy scene graph fairly directly, since there's no strong reason to redesign a working object model — the value here is in the persisted-format and rendering port, not a redesign:

```csharp
namespace Yoniq.Abstractions.Templates;

public interface ITemplateElement { Rectangle Bounds { get; } void Render(IDrawingContext ctx); }

public sealed record TemplateLine(...) : ITemplateElement;
public sealed record TemplateBox(...) : ITemplateElement;
public sealed record TemplateText(string Text, FontSpec Font, ...) : ITemplateElement;
public sealed record TemplatePicture(IImageSource Image, ...) : ITemplateElement;
public sealed record TemplateGroup(IReadOnlyList<ITemplateElement> Children) : ITemplateElement;

public interface ITemplateDocument
{
    IReadOnlyList<ITemplateElement> Elements { get; }
    IImageSource Render(int width, int height);
}

public interface ITemplateStore
{
    Task<ITemplateDocument> LoadAsync(string path, CancellationToken ct);   // new format, see below
    Task SaveAsync(ITemplateDocument document, string path, CancellationToken ct);
}
```

`CDrawOle` (embedded OLE objects) is the one element type without an obvious cross-platform equivalent — OLE embedding is a Windows COM mechanism. Recommended default: drop OLE-object support and treat any `.mtm` file using it as partially importable (other elements convert, the OLE element is reported as unsupported) rather than blocking the whole import path on it; revisit only if real-world templates are found to depend on it heavily.

## Persisted format going forward

New templates are saved in a new, documented format (JSON manifest + referenced image assets, consistent with [[12-settings]]'s JSON-first approach), not `.mtm` — `.mtm` is a read-only import target (`ITemplateStore.LoadAsync` supporting legacy `.mtm` files, once the format is fully reverse-engineered per above), not something new templates are written back into.

## Definition of done

- [ ] `.mtm` format reverse-engineered and documented (cross-checked against `Draw.cpp`'s `Load`/`Save` methods), including confirming or refuting the `PARALIST.BIN` relationship noted above.
- [ ] `ITemplateDocument`/`ITemplateElement` model implemented for at least Line/Box/Text/Picture/Group; `CDrawOle` explicitly scoped in-or-out based on real-world template survey.
- [ ] Legacy `.mtm` import path implemented and tested against the sample templates already in the legacy tree (`def1–5.mtm`, `t1–5.mtm`).
- [ ] `ITemplateItem` plugin extension point implemented, following [[11-plugin-system]]'s conventions, as the documented successor to CItems.
- [ ] This subsystem's own porting-gotchas note added once the reverse-engineering pass surfaces further encoding/format traps, mirroring how [[03-cat-layer]] and [[10-localization]] were corrected after review.
