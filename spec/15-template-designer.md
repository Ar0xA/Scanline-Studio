# 15 — TX Template Editor (formerly "Template / QSL Designer")

## Related

[[07-image-pipeline]] (this subsystem supersedes `ImageOverlay`'s minimal text-overlay-only subset)
· [[09-ui]] · [[18-path-to-1.0]] (previous milestone, complete) · [[11-plugin-system]] and
[docs/removed-features.md](../docs/removed-features.md) (their CItems-successor `ITemplateItem`
cross-references are now stale against this redesign — see "Non-goals" below) · replaces
`Draw.cpp`/`Draw.h`, the `.mtm` file format, `PARALIST.BIN`, `CItems/` **as a capability reference
only**, not an implementation to port — see the redesign note immediately below.

## Status: scoped for 1.1, not yet planned or implemented

## Redesign note (2026-08-16) — supersedes this document's own original scope

This document originally scoped a legacy-faithful port of `Draw.cpp`'s scene-graph engine (a real
vector-graphics/object-composition model: line/box/text/picture/OLE/group elements, a custom binary
`.mtm` file format, a `CItems` DLL plugin surface) and was explicitly marked "specified, deferred
past v1."

**User decision: 1.1 does not build that.** Produced from a design conversation plus two research
passes: a 6-branch ADHD divergent-ideation pass (5 cognitive frames + an independent Opus branch, 36
raw ideas, scored/clustered/3 deepened) on *how* to build this, and a separate open-ended Opus pass
(grounded in actual source reads — `MacroTextResolver.cs`, `RadioSessionService.cs`,
`TxImageEditorPaneViewModel.cs`, legacy `Draw.h`) on *what functionality* it needs to deliver.

The end-result *capability* should match what legacy could produce (positioned text/images/shapes
composited onto the TX frame), but the implementation is a fresh, modern design — legacy is a
capability checklist, never an implementation to replicate. This matches this project's own standing
rule that UI/image-editing work should be improved on, not replicated (port-first fidelity is scoped
to DSP/codec math only, CLAUDE.md §2).

Concretely: the scene-graph object model, `.mtm` reverse-engineering, `CDrawOle`/OLE embedding, and
the `CItems`-successor plugin surface are **dropped as 1.1 goals entirely**, not just deferred
further — see Non-goals. `.mtm` *import* specifically is **not dropped**, only sequenced after 1.1's
modern core ships — see Deferred, below, which preserves this document's original legacy research
for when that's picked up. **Next step is plan-review** (UI/UX design decisions get an auditor
plan-review pass before implementation, same as any other UI/UX design work) — not yet done.

## Goal

A single, always-interactive TX-image-editor surface (extending the existing editor — crop/rotate/
adjustments/undo-redo/basic overlay-text, none of which is being redesigned, see [[07-image-pipeline]])
that lets an operator build flexible, modern templates (drag/drop, color, font, resizable elements)
*and* respond fast during a live contact, without a separate "design mode" vs. "fill-in mode."

## Confirmed requirements (decided in the design conversation, not open)

1. Resizing a text element's box auto-fits the font size to the new box (Figma/Canva "text in a
   shape" behavior) — see Friction risks below for what this needs to actually work correctly.
2. Element types: text, image, box/shape. Elements have z-order/layer priority. Content boxes have
   an independently-settable background/fill color.
3. ONE unified always-interactive editor — no separate design/fill-in mode. Save-as-template /
   load-a-template are optional actions inside the same live surface, not a mode switch.
4. Must preserve a specific legacy capability the user explicitly liked: "text-only" templates with
   a blank/transparent background, meant to overlay on top of whatever base image is currently
   loaded — as opposed to templates that carry their own background image.
5. "RX image insert" (pulling the currently-received image into the TX editor) is wanted; Opus's
   pass generalizes this — see Functional scope below.
6. "Rapid response" (swap content in an existing template, send, in seconds mid-QSO) matters as
   much as authoring flexibility.

## Functional scope (Opus research pass, cross-checked against real source + legacy)

**The frame that should drive scope decisions**: a real SSTV QSO gives roughly a 90-second window
(while the other station's image paints in) to build a reply. Everything here is either prepared
calmly in advance and recalled instantly, or touched in that window — almost nothing is done at
leisure. Three usage moments, each needing different things: **CQ/beacon image** (set once, reused
for weeks, nothing per-QSO) · **reply image** (template + this QSO's variable text + often a new
photo, assembled fast — this is what 1.1 is mainly buying) · **one-off** (already works today via
the existing editor, no template involved).

**The biggest gap, reframed**: the "7 of 12 macro tokens blocked on no current-QSO-context" framing
from the 1.0 audit is not quite right.
- **FREQ and MODE are not actually blocked** — `RadioSessionService` already exposes live rig
  frequency, and TX mode is already selected in the editor. These are settings-tier macros, same
  effort as the 5 already-working tokens (MY CALL/MY GRID/DATE/UTC/MY NAME).
- The genuinely QSO-specific tokens (HIS CALL, HIS GRID, HIS RSV, etc.) don't need a "current QSO"
  model at all. The actual fix: **named template variables + a fill bar** — a text element can
  contain `{his_call}`/`{rsv}`/etc.; the editor shows a compact input row listing only the fields
  the *loaded template* actually uses; typing updates every reference live; one "clear fields"
  action after the QSO. No QSO record, no logbook coupling, no schema. (If the logbook is later
  wired to *prefill* those same fields, that's additive, not a dependency.)

**Macro/field resolution timing**: legacy modeled this explicitly (`CDrawText::IsTimeMacro`/
`UpdateTimeText`, `yoniq-old/YONIQ-main/Draw.h:255+`) — a template built at 14:02 and sent at 14:07
must show 14:07. Dynamic macros resolve at render/TX time by default, with an explicit per-element
"freeze this value" for the cases where a specific timestamp is wanted.

**Fast template recall**: legacy shipped `def1–5.mtm`/`t1–5.mtm` — five-plus-five instant slots.
Without an equivalent (a template gallery with thumbnails, favorites/slots, one-click apply,
last-used auto-restored on launch), "save/load are just actions in the surface" degrades into a
file dialog mid-QSO — exactly the friction this feature exists to remove.

**Photo/template independence as first-class actions**: "apply this template to a new photo" and
"swap the photo, keep the overlay" are the two most-repeated operations in the reply-image moment.
They need defined one-click semantics against the existing crop/rotate state (the photo-anchored vs.
crop-anchored coordinate distinction already built for the overlay-text system needs to hold for
template elements too), not manual reconstruction each time.

**Generalize "RX image insert"**: a picture element's source should be selectable as last-received /
pick-from-RX-history / file / clipboard-paste / OS drag-drop. Legacy had this hook (`CDrawPic::
UpdateHistPic`). Clipboard paste and drag-drop are cheap and will be missed immediately if absent.

**Text-legibility toolkit, prioritized over fancy fill**: text sits over arbitrary photos and then
through a noisy analog channel. What matters: outline/stroke, drop shadow, a background plate with
padding/opacity, font family/size/weight/alignment. If only one styling feature ships, make it
outline/shadow. (Gradients, texture brushes, perspective — see Non-goals.)

**Layout aids**: snap to grid/edges, alignment guides, align/distribute, arrow-key nudge, numeric
position+size fields, duplicate, multi-select move, and **element lock** (matters more than it
sounds — in an always-interactive no-mode-switch editor, accidental drags under time pressure are a
real risk; lock is the answer, not a mode split).

**A compact element list**: z-order is confirmed but unusable past ~4 elements without something to
reorder in. One list doubling as select/rename/lock/reorder covers z-order + lock + "where did that
invisible element go" in one control — less fussy than a full layers panel.

**Font portability**: templates will be shared cross-platform; legacy templates literally embed
`"Comic Sans MS"` (see the Deferred section below). Store family + fallback, warn visibly when a
template's font is unavailable, standardize on a small bundled set that renders identically
everywhere.

**Template sharing**: a single-file export/import bundle (manifest + embedded assets) — ham SSTV
culture trades templates. Distinct from, and much cheaper than, legacy `.mtm` import.

**Safe-area / mode-aware canvas**: canvas should be the target mode's exact geometry with guides
marking a margin inside the edges (edge columns are where slant/sync artifacts land).

## Decisions from this session (resolving the 3 open questions Opus flagged)

1. **Legacy `.mtm` import**: real goal, confirmed by the user — **deferred to after 1.1's modern
   core ships**, not part of the initial build. The Deferred section below remains the reference for
   this when it's picked up.
2. **Multi-line/paragraph text frequency**: SSTV text is always short. Shrink-to-fit is the primary
   and sufficient auto-fit behavior; a separate wrap-mode is lower priority, can be deferred or
   dropped.
3. **"Preview as received"** (round-trip the composed image through the real encoder/decoder for the
   target mode before sending): explicitly not wanted — dropped from scope.

## Candidate architecture (ADHD pass — directional, not locked; needs plan-review)

Three ideas scored highest and were deepened; they compose into one coherent direction rather than
competing:

1. **Background-as-element**: the base/background image is not a special template property — it's
   an ordinary image element pinned at z=0 with a "fit to frame" constraint. A "text-only template"
   is then just a document whose element list contains no z=0 image element, so it composites
   transparently over whatever's already loaded. Resolves confirmed requirement 4 structurally, not
   via a special-case flag. Load-bearing risk: compositing must explicitly start from the existing
   canvas content, not a cleared buffer (`RenderTemplate(existingBase, elements)` needs to be an
   explicit contract, not an emergent side effect).
2. **Live-bound elements, base+diff-overlay templates**: text elements store literal characters
   mixed with unresolved macro-tokens (resolved at render/send time, never baked in at type-time);
   image elements store a *source binding* (last-RX/live-RX/file/clipboard) rather than raw pixels,
   re-resolving each render. Templates persist as an immutable base document + a mutable per-session
   diff overlay, so loading never destroys in-progress work and "reset this element" is one delete.
   Real open risk: `LastRxImage` is a moving target — binding-vs-snapshot semantics if a new frame
   lands mid-edit needs an explicit decision, not an assumption.
3. **Slot-fill bar + ready rack**: elements optionally carry a named "slot" key; a second, always-
   visible panel lists one row per slot, tabbable, editable without touching the canvas — same
   document, second view, not a mode switch (satisfies requirement 3 by construction). Complemented
   by a persistent strip of pinned, live-rendered template thumbnails with number-key accelerators
   for swapping the whole document. This is the direct mechanism for "fast template recall" and the
   fill-bar functional requirement above — they're the same thing looked at from two angles.

New templates persist in a new, documented format (JSON manifest + referenced image assets,
consistent with [[12-settings]]'s JSON-first approach) — carried forward from this document's
original proposal, still correct under the redesign.

## Friction risks in what's already confirmed (need resolving before/during plan-review)

- **Auto-fit font is under-specified**: needs a per-element fit mode (shrink-to-fit as the default
  and, given decision 2 above, probably sufficient on its own — wrap-mode is low-priority). More
  important: a **render-time overflow policy** — a box sized at design time for "W1AW" will clip a
  macro-expanded "VK9/PA0XYZ/P" unless overflow is handled at *resolve* time, not just design time.
  Needs min/max font clamps too.
- **Text-only template + no base image loaded**: currently an undefined case. Transparent has to
  resolve to *something* when transmitted (black is the conventional answer) — needs to be a
  deliberate, visibly-indicated choice, not an accident.
- **Box elements** need border, corner-radius, and opacity to actually work as text plates/frames,
  beyond the already-confirmed fill color.

## Non-goals for 1.1

Perspective transform, vertical/stacked text, multi-color gradients, texture-brush fills, **OLE
embedding, and a CItems-equivalent `ITemplateItem` plugin surface** (this reverses this document's
own earlier proposal; **user decision 2026-08-16: move it fully out of scope**, not just deferred —
no CItems-successor extension point is planned at all), and "preview as received" (decision 3 above).
Legacy `.mtm` *import* is a real future goal, not a non-goal — see Decisions above.

**Cross-references fixed 2026-08-16**: [[11-plugin-system]] and
[docs/removed-features.md](../docs/removed-features.md) no longer claim a future `ITemplateItem`/
CItems-successor extension point tied to this document — both now state it as a permanent,
acknowledged gap. [[07-image-pipeline]] and [[14-roadmap]] no longer carry this document's old
"specified but deferred past v1" framing.

## Deferred, not dropped: legacy `.mtm` import

This section preserves this document's original research so it's not lost — do this work only after
1.1's modern core (above) ships, per the Decisions section.

### What legacy's template designer actually is

`Draw.cpp` (134KB — plausibly the second-largest single subsystem in the legacy codebase after the
SSTV DSP core) implements a small vector-graphics/object-composition engine used to design
QSL-card-style overlays and full custom TX templates. Class hierarchy (`Draw.h`):

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

i.e. a real (if small) scene graph: lines, boxes, styled text, embedded pictures, embedded OLE
objects, grouping, and a library-item reference mechanism, composited onto a canvas and rendered
into the TX/RX image pipeline. `CDrawText::IsTimeMacro`/`UpdateTimeText` (`Draw.h:255+`) is the
legacy precedent for the render-time macro resolution requirement captured above.

### Persisted format (`.mtm`)

Legacy templates persist as `.mtm` files (`def1.mtm`–`def5.mtm`, `t1.mtm`–`t5.mtm`, `Stock/List.mtm`,
`Current.mtm`) — a custom binary format, not currently fully reverse-engineered as of this document.
What's confirmed by direct inspection (`od`/`xxd` against `def1.mtm`): a fixed-layout binary
prologue (32 bytes observed identically across sampled files) followed by a sequence of records;
embedded ASCII strings including `"CQ SSTV"` and `"Comic Sans MS"` (a font name, consistent with
`CDrawText` styling) appear later in the file. `PARALIST.BIN` shares an identical prologue layout
with `def1.mtm`, suggesting it's either a `.mtm`-format file under a different name or a closely
related sibling format — not yet confirmed which. `Draw.cpp:510`'s `LoadString(TStream*,
AnsiString&)` reads locale-encoded strings directly out of these binaries, meaning **any text
harvested from `.mtm` files is subject to the same encoding rule as everything else** (CP932 for
Japanese-origin content — see CLAUDE.md) — do not assume UTF-8 when parsing template strings.

**This format is not fully specified here.** Before implementation starts on this sub-effort, the
prerequisite work is a proper reverse-engineering pass (ideally cross-checked against `Draw.cpp`'s
own `Load`/`Save` methods for each `CDraw` subtype, which is the authoritative source for field
layout, rather than inferring purely from hex dumps).

### `CDrawOle`/CItems — dropped, not part of the import path either

`CDrawOle` (embedded OLE objects) has no cross-platform equivalent — OLE embedding is a Windows COM
mechanism — and is a non-goal per above, including for `.mtm` import: any imported `.mtm` file using
it should report that one element as unsupported rather than blocking the whole import. `CItems`
(`CItems/ECUSTOM.TXT`) is a DLL-based extension mechanism scoped to the legacy template designer
specifically (PERIMG, QSLBox, TextArt, TEXTBOX are its four reference implementations, each a
separate native Win32 DLL) — not binary-portable to a cross-platform managed host regardless, and
with no successor planned under this redesign (see Non-goals above).

### Proposed shape for the import path (once undeferred)

```csharp
namespace ScanlineStudio.Abstractions.Templates;

public interface ITemplateElement { Rectangle Bounds { get; } void Render(IDrawingContext ctx); }

public sealed record TemplateLine(...) : ITemplateElement;
public sealed record TemplateBox(...) : ITemplateElement;
public sealed record TemplateText(string Text, FontSpec Font, ...) : ITemplateElement;
public sealed record TemplatePicture(IImageSource Image, ...) : ITemplateElement;
public sealed record TemplateGroup(IReadOnlyList<ITemplateElement> Children) : ITemplateElement;

public interface ITemplateStore
{
    Task<ITemplateDocument> ImportLegacyAsync(string mtmPath, CancellationToken ct);
}
```

`ITemplateElement` here should converge with whatever element model the modern editor (above) ships
with, not duplicate it — this is an import adapter onto that model, not a parallel one.

### Definition of done (for this sub-effort specifically, once picked up)

- [ ] `.mtm` format reverse-engineered and documented (cross-checked against `Draw.cpp`'s `Load`/
      `Save` methods), including confirming or refuting the `PARALIST.BIN` relationship noted above.
- [ ] Legacy `.mtm` import path implemented and tested against the sample templates already in the
      legacy tree (`def1–5.mtm`, `t1–5.mtm`), importing onto the modern element model above.
- [ ] `CDrawOle` elements explicitly reported as unsupported-on-import, not silently dropped or
      blocking the rest of the import.
- [ ] This sub-effort's own porting-gotchas note added once the reverse-engineering pass surfaces
      further encoding/format traps, mirroring how [[03-cat-layer]] and [[10-localization]] were
      corrected after review.

## Open questions / next steps

- Plan-review pass (auditor, UI/UX design decisions per this project's established process) before
  any implementation starts — the architecture above is directional, not final.
- Concrete UI design for: the element list (select/rename/lock/reorder), the slot-fill bar, the
  ready-rack thumbnail strip, and the fill-field bar for template variables.
- Font portability: decide the actual bundled fallback font set.
- Whether box border/corner-radius/opacity ship in the same pass as the base box element or as a
  quick follow-up.
- Fix the stale [[11-plugin-system]]/[docs/removed-features.md](../docs/removed-features.md)/
  [[07-image-pipeline]]/[[14-roadmap]] cross-references flagged under Non-goals above.
