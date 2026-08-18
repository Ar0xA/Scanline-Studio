# Image Pipeline

## Related

[[06-sstv-dsp]] (source/sink of raw image data) · consumed by → [[09-ui]] · replaces `RxView.cpp`, `PicRect.cpp`, `PicRectDlg.cpp`, `PicFilte.cpp`, `PicSel.cpp`, `ZoomView.cpp`, `PrevView.cpp`, `StockVew.cpp`/`TxStock*.jpg`, `HistView.cpp`/`History.bin`

## Purpose

Everything image-related that sits between the DSP core and the screen/disk: receiving images progressively as they decode, preparing images for transmission (crop/resize/filter/overlay), a picture library for TX stock images, and a receive history.

## Core abstractions

```csharp
namespace ScanlineStudio.Abstractions.Imaging;

public interface IImageSource
{
    int Width { get; }
    int Height { get; }
    ReadOnlySpan<Rgb24> GetScanline(int y);
}

public interface IReceivedImageBuffer
{
    // Updated incrementally as ISstvDecoder.LineDecoded fires; UI binds to this for live partial-image rendering.
    IImageSource Current { get; }
    event Action? Updated;
    Task SaveAsync(string path, CancellationToken ct = default);
}

public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

// Anchor is the CENTER of the text (matches drag-to-position UX: the user grabs the visual
// center, not a corner). FontSizeRelative is relative to the image's HEIGHT (stable reference
// regardless of aspect/stretch, unlike width which varies more under a non-aspect-preserving
// resize). Color is Rgb24 -- already the pixel type this whole namespace uses -- not a string,
// which would add an unspecified parse format and a new runtime failure mode for no reason.
public sealed record ImageOverlayElement(string Text, double X, double Y, double FontSizeRelative, Rgb24 Color);

public sealed record ImageOverlay(IReadOnlyList<ImageOverlayElement> Elements);

public interface ITransmitImagePreparer
{
    IImageSource Crop(IImageSource source, NormalizedRect region);

    // Always returns EXACTLY width x height -- ISstvEncoder.EncodeAsync throws ArgumentException
    // on any dimension mismatch (IImageFileLoader.cs's own doc comment), so this method can never
    // return something close-but-not-exact. preserveAspect: true letterboxes with solid black
    // (matches this app's own raw-instrumentation aesthetic direction, not a "smart" fill);
    // preserveAspect: false stretches to fill exactly, matching the user's own explicit ask for
    // "stretch" as a distinct option from aspect-preserving resize.
    IImageSource Resize(IImageSource source, int width, int height, bool preserveAspect);

    // Must run AFTER Resize, not before -- overlay text is rasterized at the FINAL mode
    // dimensions, so a non-aspect-preserving "stretch" resize never smears/distorts already-drawn
    // glyphs. (Crop -> Resize -> ApplyOverlay is the only correct order; stated explicitly here
    // because it's load-bearing, not just the order the interface happens to list them in.)
    IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay);
}
```

`ApplyFilter`/`IImageFilter` (brightness/contrast/sharpen-style filters, e.g. a future "high contrast" preset) are deliberately NOT part of this interface yet -- the user explicitly deferred filters to a later pass ("that's for later"), and shipping an interface method with zero implementations/consumers is exactly the kind of speculative surface this project avoids elsewhere. Filters are already framed as a plugin extension point ([[11-plugin-system]]) in the TX flow section below, so the eventual re-add is more likely a new `IImageFilter` abstraction entirely, not a change to `ITransmitImagePreparer` itself.

**Font source for `ApplyOverlay`**: `SixLabors.Fonts.SystemFonts` enumeration can legitimately be empty on a minimal Linux install (no system fonts registered) -- a real cross-platform trap for a feature that burns text into pixel data, not just displays it via the OS's own font stack the way UI chrome text does. Resolved by bundling a specific open-license font file (loaded into a `SixLabors.Fonts.FontCollection` explicitly at startup, never relying on system enumeration) -- needs its own [LICENSES.md](../LICENSES.md) entry once a specific font is picked, per CLAUDE.md's license-audit rule; not picked yet, tracked as a prerequisite for implementing `ApplyOverlay`, not a blocker for the rest of this design.

Image decode/encode to/from standard file formats (PNG/JPEG/BMP) uses `SixLabors.ImageSharp` (cross-platform, no GDI+/System.Drawing dependency, which is Windows-only and increasingly discouraged even there) rather than porting the legacy `Draw.cpp` GDI wrapper.

## RX flow

`IReceivedImageBuffer` is populated by an application-layer adapter subscribing to `ISstvDecoder.LineDecoded` ([[06-sstv-dsp]]), so the UI's live "image filling in top-to-bottom as it decodes" behavior (legacy `RxView`) falls out of the same incremental event stream the decoder already produces — no separate polling or redraw timer needed. On decode completion, the image is auto-saved to the configured RX history folder (see [[08-logging]] for how RX images link to logged QSOs) using a filename scheme preserving legacy conventions (timestamp + mode + optional callsign) for continuity with users' existing archives.

## TX flow

1. User selects a source image (file, stock picker, or clipboard paste — `TxControlsPaneViewModel`'s existing flow, plus a clipboard image source added in a later pass, see below) — webcam/screen-capture frame capture, OS drag-drop as an image source, and legacy `PerSpect.cpp` perspective correction, stay deferred (see below).
2. The picked image loads at its ORIGINAL/native resolution (a real behavior change from Phase 4's first slice, which auto-resized straight to the mode's exact dimensions with no edit step at all) and opens the TX image editor (see below) for crop/resize/stretch/overlay.
3. `ITransmitImagePreparer`'s `Crop` → `Resize` → `ApplyOverlay` pipeline (that exact order — see the interface's own doc comment for why) produces the final mode-exact image.
4. Prepared image is handed to `ISstvEncoder.EncodeAsync` ([[06-sstv-dsp]]).

## TX image editor

Combines crop/resize/stretch and text overlay in one editor with a realtime preview — user's own
framing: "modern mini image editor functionality... more 2026 instead of 2020," not legacy's
"type numbers into a form, click apply, hope it looks right" flow. Filters
(brightness/contrast/sharpen/preset "modes" like a future "high contrast" filter), full
macro-key placeholder auto-substitution, and the full QSL/vector template designer are all
explicitly deferred past this pass — user's own words, "that's for later."

**Placement**: an in-window view inside the existing `RxToolDock` region (tab-grouped alongside
RX Image/RX History, or a full takeover of that region while editing — implementation detail,
not a separate OS-level `Window`) — direct user decision, keeping RX/TX/history/editing reachable
from one window rather than a second window to manage separately, honoring the "unified working
area" principle ([[feedback_ui_effort_allocation]] memory). `TxToolDock`'s own 30%-width region
is too narrow for freeform-cropping a multi-megapixel source photo; a real editing surface needs
the same real estate the RX/History tab group already has.

**Realtime preview — what "TX-accurate" actually means this pass**: the preview panel renders the
*real* `ITransmitImagePreparer` output (`ImageSourceBitmapConverter.ToBitmap(Crop→Resize→ApplyOverlay(...))`),
not a separately-drawn Avalonia approximation — the interactive canvas (crop handles, draggable
overlay text) is chrome drawn over the source image, but the preview panel is always the pipeline's
actual pixels, recomputed on every interactive change. This is **geometry/framing-accurate, not a
full encode→decode simulation** — it shows exactly what region/scale/overlay placement will be
sent, but does not model per-mode chroma-family loss (e.g. `YCbCrRobot`'s line-to-line chroma
alternation, `YCbCrLinePaired`'s shared chroma pair — `SstvModeDefinition`'s `ColorEncoding`
values). Labelled as such in the UI (a small caption, not a silent gap) rather than implying full
post-decode fidelity the preview doesn't actually provide.

Display-only upscaling of the small mode-resolution result to a comfortable on-screen preview size
uses nearest-neighbor interpolation (Avalonia's `RenderOptions.BitmapInterpolationMode` on the
preview `Image` control) — hard pixel edges preserved, not smoothed, so the user sees the *real*
pixelation/fidelity of low-resolution SSTV modes rather than a misleadingly soft preview. This is
purely a display concern; it never touches the actual `IImageSource` data used for transmission.
The `Resize` step that actually produces the mode-exact pixels (downscaling a multi-megapixel
source down to e.g. 320x256) uses the same resampler `ImageFileLoader`/`StockImageLibrary` already
use (ImageSharp's implicit default, Bicubic) — named explicitly here so it's a stated choice, not
a repeated silent default, and so the editor's commit path and the existing load paths stay
visually consistent with each other.

**Precision — legacy's actual mechanism, verified directly (`yoniq-old/YONIQ-main/PicRect.cpp:925-1001`),
not assumed**: legacy's crop dialog (`PicRect.h`) has no numeric X/Y/width/height fields at all —
only scrollbars and speed buttons. Precision came from **keyboard nudge**: arrow keys move the
crop rect by 1px (16px with Ctrl held), Shift+arrow resizes a corner by 1px and auto-engages
stretch mode (`SBStrach`). This port's editor adopts the same nudge mechanism for the crop
rect — the cheap, high-payoff way to give technical users pixel-precise control without a
numeric-form regression from drag-only interaction (the "approachable without dumbing down"
bar — [[project_target_audience_ham_radio]] memory). Legacy's overlay text dialog (`TextIn.h`),
by contrast, genuinely did have numeric `TEdit` X/Y fields (`SEX`/`SEY`) — this port's overlay
elements get an equivalent: draggable on the canvas, with numeric X/Y entry fields alongside
for exact placement, matching legacy's own real precision affordance for text specifically.

**Overlay text specifics**:
- Plain free-typed text per element (the user types their own callsign, contact's callsign, etc.
  directly into a text field on the element) — **not** an auto-substituting macro-key placeholder
  system. No data source exists yet to auto-fill from: `spec/12-settings.md` has no "operator's own
  callsign" setting, and the logbook's per-QSO `Callsign` field ([[08-logging]]) isn't wired to any
  UI yet. A token/placeholder syntax with no substituter would be exactly the kind of unused stub
  this design correctly avoids for `ApplyFilter` — not built until Settings/Logbook actually exist
  to substitute from.
- Overflow (text wider than the image at narrow SSTV modes): clipped at the image bounds for v1 —
  simplest safe default; shrink-to-fit is a possible future refinement, not needed to ship this.

**Mode-change interaction**: because crop region and overlay positions are stored in `NormalizedRect`/
relative (0..1) coordinates rather than absolute pixels, `TxControlsPaneViewModel`'s already-shipped
mode-change retention (Phase 4's TX stock picker work) composes with this directly — a mode change
re-runs the same `Crop→Resize→ApplyOverlay` pipeline against the *new* mode's `(ImageWidth,
ImageHeight)` instead of a flat `LoadAsync` call. **Revised during implementation**: the original plan
here was to reuse Phase 4's cancel-and-replace `CancellationTokenSource` machinery for this reflow, but
that machinery existed specifically to guard an async I/O reload race (a second mode change completing
before the first's file/stock load finished). Once the editor caches the native-resolution original in
memory (`TxControlsPaneViewModel`'s `EditState`), the reflow is synchronous CPU work against an
already-loaded image — there is no I/O race left to guard against, so the `CancellationTokenSource` was
removed rather than kept "just in case" (this project's own working-methodology line: if a fix's
complexity doesn't earn its keep, revert to the simpler alternative).

**Interactive-preview performance**: a pre-downsampled working copy of the source (scaled once,
at editor-open time, to roughly 2x the target mode's dimensions) backs the interactive crop/overlay
canvas, so every drag-frame recompute works against a small image, not a re-crop of a multi-megapixel
original on every mouse-move. This is a data-structure decision made now, not a tuning knob to
retrofit later — changing it after the fact means rewriting the preview loop, not adjusting a
setting. The full-resolution `Crop→Resize→ApplyOverlay` pipeline runs once, against the real
original, on "Apply"/"Done."

**Layering test gap this feature would otherwise walk through**: `UiLayeringArchitectureTests`'s
`PackageReference` check (added in Phase 4's first slice) matches on exact package names
(`Microsoft.Data.Sqlite`, `SixLabors.ImageSharp`) — `SixLabors.ImageSharp.Drawing`/`SixLabors.Fonts`
(needed for `ApplyOverlay`'s text rendering) are different package names and would slip through
silently. Switch that check to a prefix match (`StartsWith("SixLabors.", Ordinal)`) as part of this
work, not a follow-up — otherwise the exact bug class this project has already caught twice ships a
third time, just not caught by the test meant to catch it.

**Explicitly deferred past this pass** (unchanged/reconfirmed): `ApplyFilter`/`IImageFilter`, the
macro-key auto-substitution system, legacy `PerSpect.cpp` perspective correction. **Stale as of
2026-08-16, updated 2026-08-18**: the QSL/template designer ([[15-template-designer]]) is no longer
deferred — it was the active 1.1 target, fully redesigned (a modern templating layer, not a legacy
`.mtm` port), and is now **implemented**. **Updated again, 2026-08-18 (later same day)**: clipboard
paste as an image-element source has since shipped (the "+ IMAGE" flyout's 4th source, alongside
File/Last-RX/RX-History, plus a Ctrl+V shortcut — `ImageSourceKind.Clipboard` /
`IFilePickerService.PickClipboardImageAsync`); OS drag-drop as an image source was NOT part of that
work and remains not built.

## Navigation: dockable panes, not legacy's paged main window

Legacy's `Main.h` declares a real `TPageControl *Page` with `TabSync`/`TabRX`/`TabHist`/`TabTX`/`TabTemp`
tab sheets (`ComLib.h`'s `enum { pgSync, pgRX, pgHist, pgTX, pgTemp }`) — the RX/Hist/TX/Temp/Sync
*views* are paged, one visible at a time, switched either by the tab strip itself (`Main.cpp`'s
`PageChange`) or programmatically via `Mmsstv->AdjustPage(...)` (which only ever targets
pgRX/pgHist/pgTX/pgTemp, never pgSync). `HistView.cpp`'s `THistViewDlg` is a *separate* floating
thumbnail-browser dialog that merely drives that main-window tab (`PBClick` sets `UDHist->Position`,
double-click calls `AdjustPage(pgHist)`) — so legacy actually has two History UIs layered on each other,
not one. (Whether the waterfall/FFT panels themselves sit inside or outside the paged region isn't
verifiable from `Main.h`'s flat component list alone — not claimed either way here.)

This port deliberately does not replicate the paged main window — [[09-ui]]'s "Main window layout"
section's 3-region waterfall/RX/TX arrangement (already built in Phase 3) is kept, but paging
History/Template behind a tab a user must switch away from RX/TX to see is not. RX
History and the Stock/template picker below are **dockable `Tool` panes** ([[09-ui]]'s existing
`Dock.Avalonia` mechanism, already used for RX Image/RX History today), not a `HistoryView`/`StockImageView`
modal dialog and not a legacy-style page — letting an operator see RX/History/TX simultaneously, tear any
pane out to view side-by-side, without losing the tab-grouped-by-default compactness this port already
established for RX Image/RX History (the waterfall is deliberately **not** part of that tab group — a
fixed strip above it instead, see [[09-ui]]'s "Main window layout" section for why).

**Dropped, not carried forward this pass** (CLAUDE.md's removal rule — logged in
[docs/removed-features.md](../docs/removed-features.md)): legacy's `UDHist` step prev/next,
`SBLatest` jump-to-latest, `HistStat` status label, drag-*in* from a history thumbnail into the TX
template composition (`HistView.cpp`'s `BeginDrag` + `Main.cpp`'s `pHistView->IsPBox(...)` drag-accept
handlers — dragging a history image directly into `TabTemp`/`TabTX`, not dragging out to another
app), `SBCopy` (history image → clipboard), and `SBPaste` (clipboard → TX slot). The real gap: there
is no history→TX/template compositing path at all in this design — a selected `RxHistoryPane` entry
is a read-only preview only. Click-to-view thumbnails plus the stock/browse picker cover the core
"pick a TX source image" workflow; direct step-through navigation, drag-in compositing, and clipboard
transfer are not in this pass.

## Stock image library

Legacy `StockVew.cpp` managed a user-configurable `StockDir` folder of local "TX stock" images (`TxStock1.jpg`/`.bmp`... up to `STOCKMAX` 300, paginated `STOCKPAGE` 4-at-a-time, `Main.h`) for quick reuse. Rewritten as `IStockImageLibrary` — a user-managed folder of images with thumbnails, no fixed page-size UI constraint, indexed the same way the RX history is (see below) for UI consistency. Surfaced inline in the TX Controls pane as a thumbnail strip (recently-used + library), with the existing file-browse flow kept alongside it as the direct/expert path — picking a thumbnail is a shortcut, not a replacement.

```csharp
namespace ScanlineStudio.Abstractions.Imaging;

public sealed record StockImageEntry(string Id, string FileName, string FilePath);

public interface IStockImageLibrary
{
    Task<IReadOnlyList<StockImageEntry>> ListAsync(CancellationToken ct = default);

    // Thumbnails and full loads both return IImageSource — the same abstraction IImageFileLoader
    // already returns — never a UI-toolkit bitmap type or an ImageSharp type; ScanlineStudio.UI owns
    // converting IImageSource -> Avalonia Bitmap via the existing ImageSourceBitmapConverter, exactly
    // like every other pane today.
    Task<IImageSource> LoadThumbnailAsync(StockImageEntry entry, int maxDimension, CancellationToken ct = default);

    // Mirrors IImageFileLoader.LoadAsync's fit-to-mode contract, so TxControlsPaneViewModel can treat
    // a stock pick and a browsed file identically once loaded.
    Task<IImageSource> LoadFullAsync(StockImageEntry entry, int targetWidth, int targetHeight, CancellationToken ct = default);
}
```

**Mode-change interaction** (`TxControlsPaneViewModel`): today, changing the selected SSTV mode nulls
the loaded image entirely (`OnSelectedModeChanged`), which reads as broken once a stock strip exists —
clicking a thumbnail then changing mode would silently deselect it with no visible cause. Instead, the
view model retains the selected source (a `StockImageEntry` or a browsed file path) across a mode
change and re-runs `LoadFullAsync`/`IImageFileLoader.LoadAsync` at the new mode's dimensions; only a
failed reload (source no longer available) falls back to clearing, using the existing error-message path.
`OnSelectedModeChanged` is currently synchronous, so the reload is necessarily fire-and-forget from
it — during that in-flight window, `_loadedImage` must not be left pointing at the *old* mode's
already-loaded image (it would pass `CanTransmit()` and `ISstvEncoder.EncodeAsync` would throw on the
dimension mismatch): `TransmitCommand` stays disabled until the reload completes, and a second mode
change before the first reload finishes cancels/supersedes it (last-write-wins), never applying a
stale-mode result.

## RX history

Legacy `HistView.cpp` + `History.bin` kept a browsable thumbnail history of received images in a proprietary binary format. Rewritten as `IReceiveHistoryStore`, backed by a lightweight embedded index (SQLite via `Microsoft.Data.Sqlite`, one row per received image: timestamp, mode, file path, linked QSO id if any) rather than a bespoke binary format — this also gives the logbook ([[08-logging]]) a natural foreign key to link a logged QSO to the image received during it. Surfaced as a dockable `RxHistoryPane`, tab-grouped with RX Image by default in their own `ToolDock` (`AppDockFactory`'s `RxToolDock` — separate from the waterfall's own fixed-strip `WaterfallToolDock`, see [[09-ui]]'s "Main window layout" section) — selecting a history entry loads it into a read-only preview and never touches the live `IReceivedImageBuffer` binding `RxImagePaneViewModel` owns, so browsing history cannot appear to interrupt or corrupt an in-progress live decode.

```csharp
namespace ScanlineStudio.Abstractions.Imaging;

public sealed record ReceiveHistoryEntry(string Id, DateTimeOffset ReceivedAt, string ModeId, string FilePath, string? LinkedQsoId);

public sealed record ReceiveHistoryFilter(string? ModeId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

// Callsign is deliberately not a filter field: ReceiveHistoryEntry has nothing to filter against
// today (no QSO-linking UI exists yet to populate LinkedQsoId, and callsign isn't stored directly
// on the entry) -- a real inconsistency both audit rounds missed, caught during implementation and
// fixed by removing the field rather than adding unused/dead-code filtering.

public interface IReceiveHistoryStore
{
    Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default);

    // Same IImageSource-only contract as IStockImageLibrary above — no SQLite/ImageSharp type ever
    // crosses into ScanlineStudio.UI. The SQLite-backed implementation lives in a Core.* project;
    // ScanlineStudio.UI only ever sees this interface via DI.
    Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default);

    // Called by the same application-layer adapter that populates IReceivedImageBuffer, on decode completion.
    Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default);
}
```

**Layering test coverage**: `UiLayeringArchitectureTests` today only checks referenced assembly names
and `<ProjectReference>` items against `ScanlineStudio.Core.*` — it would not catch a direct `PackageReference` to
`Microsoft.Data.Sqlite` or `SixLabors.ImageSharp` from `ScanlineStudio.UI.csproj` even though that
would defeat the point of the `IImageSource`-only contract above. Add an explicit assertion (or a
`.csproj`-level check) that `ScanlineStudio.UI` carries neither package reference, alongside the
existing namespace check.

## Explicitly deferred (not v1)

- Perspective correction (`PerSpect.cpp`) and webcam capture (`PicSel.cpp` camera path) are useful but not core to preserving SSTV send/receive behavior; tracked as a post-v1 item in [[14-roadmap]] rather than blocking the core rewrite.
- **Stale as of 2026-08-16, updated 2026-08-18**: this line used to defer the full QSL/template
  designer past v1 — 1.0 is done and [[15-template-designer]] is now **implemented** (was the active
  1.1 target, redesigned, not a legacy `.mtm` port). `ImageOverlay` above remains the basic
  text-overlay subset that shipped in 1.0; spec/15's richer element model supersedes it, now live.

## Testing

- `ITransmitImagePreparer` operations are pure functions over `IImageSource` — unit-tested with small synthetic bitmaps and pixel-exact assertions.
- `IReceivedImageBuffer` incremental-update behavior is tested by feeding synthetic `LineDecoded` events and asserting the buffer's pixel state after each.
- `IReceiveHistoryStore` is tested against a temp SQLite file, verifying round-trip of inserted records.

## Definition of done

- [x] `IImageSource`/`ITransmitImagePreparer` implemented on ImageSharp, unit-tested — `IImageSource` (`ArrayImageSource`) and `ITransmitImagePreparer` (`TransmitImagePreparer`: Crop/Resize/ApplyOverlay) both done, backing the TX image editor (`TxImageEditorPaneViewModel`/`TxImageEditorPaneView`). Filter/preset support (`ApplyFilter`/`IImageFilter`) is not part of this interface — deferred to the plugin system, [[11-plugin-system]].
- [x] RX live-fill behavior verified end-to-end against a real [[06-sstv-dsp]] decode — not just a fixture waveform: Phase 3's demo used real `MiniAudioEngine` capture over a real virtual audio cable, real `AnalogFmSstvDecoder`, feeding `ReceivedImageBuffer` live.
- [ ] RX history and stock library backed by SQLite index, migrated-from-legacy path documented in [[12-settings]] (best-effort import of existing `History.bin` if feasible, otherwise a clean start with the old folder left untouched).
