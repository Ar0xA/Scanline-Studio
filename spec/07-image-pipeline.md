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

    // [0.0, 1.0] fraction of decoded rows, null when idle. Reaches exactly 1.0 on the completing
    // scanline group, not an asymptotic approximation.
    double? Progress { get; }

    // Monotonic counter bumped only when Current's IDENTITY changes (a fresh image starting, or
    // the buffer blanking on restart) -- not on every pixel update to the SAME image. Lets a
    // Saved subscriber detect a newer image superseding the one a completed save actually wrote.
    int Generation { get; }

    event Action? Updated;

    // Fires once a write to disk finishes, carrying the destination path and the Generation
    // captured when that write was invoked (not when it completes). Raised on whichever thread
    // the save's background work completes on -- the subscriber marshals to the UI thread itself.
    event Action<string, int>? Saved;

    Task SaveAsync(string path, CancellationToken ct = default);

    // Raises Saved directly, for a caller (ReceiveHistoryRecorder) that already wrote its own
    // pixel snapshot without going through SaveAsync, to avoid racing a DecodeRestarted that can
    // blank Current before an async SaveAsync read would happen.
    void NotifySaved(string path, int generation);
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

    // Brightness/Contrast/Saturation/Gamma/Sharpen/Denoise sliders, mapped from the TX image
    // editor's raw values -- see ImageAdjustments (Abstractions.Imaging). Must run AFTER Resize
    // (judged against the final framed/sized output) and BEFORE ApplyOverlay/ApplyTemplate (must
    // never touch already-burned-in overlay pixels). A no-op ImageAdjustments returns the SAME
    // source instance, not a copy, so the interactive preview skips a real round-trip when the
    // sliders sit untouched.
    IImageSource ApplyAdjustments(IImageSource source, ImageAdjustments adjustments);

    // Legacy path, superseded by ApplyTemplate for the 1.1 template editor but not yet removed
    // (the editor VM still migrates onto TemplateDocument incrementally). Must run AFTER Resize
    // and ApplyAdjustments, not before -- overlay text is rasterized at the FINAL mode
    // dimensions, so a non-aspect-preserving "stretch" resize never smears/distorts already-drawn
    // glyphs. Crop -> Resize -> ApplyAdjustments -> ApplyOverlay is the only correct order.
    IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay);

    // The template-designer compositor (see [[15-template-designer]]) -- draws every element of a
    // TemplateDocument (text/image/box elements, gradients, shadows, stack/3D effects, rotation)
    // over existingBase in ascending (Z, list index) order. Same pipeline position as
    // ApplyOverlay: after Resize/ApplyAdjustments, never before. A no-op (empty) document returns
    // the SAME existingBase instance, same no-op convention as ApplyAdjustments.
    IImageSource ApplyTemplate(IImageSource existingBase, TemplateDocument document);

    // Lets the UI mirror ApplyTemplate's own shrink-to-fit text measurement for WYSIWYG canvas
    // rendering, without ScanlineStudio.UI needing a SixLabors.Fonts reference.
    double MeasureFittedFontSize(
        string text, FontSpec font, int imageHeightPx, int boundsWidthPx, int boundsHeightPx,
        double strokeThicknessRelative = 0, double shadowOffsetXRelative = 0,
        double shadowOffsetYRelative = 0, double rotationDegrees = 0,
        double stackStepXRelative = 0, double stackStepYRelative = 0);

    // Bundled font-family names for the template editor's font picker; first entry is the
    // fallback family ApplyTemplate uses when a requested family isn't found.
    IReadOnlyList<string> AvailableFontFamilies { get; }

    // Rotates 90 degrees clockwise, always -- no direction parameter (4 clicks returns to the
    // original orientation); width/height swap in the result.
    IImageSource Rotate(IImageSource source);
}
```

`ImageAdjustments`, `TemplateDocument`, and the full `TemplateElement` hierarchy (`TemplateTextElement`/
`TemplateImageElement`/`TemplateBoxElement`, gradients, shadow/stack effects, rotation) are defined in
full in [[15-template-designer]] — not repeated here. `ApplyFilter`/`IImageFilter` as a separate
plugin-extension-point abstraction never shipped; the brightness/contrast/sharpen-style adjustments the
user originally deferred ("that's for later") shipped instead as `ApplyAdjustments` above, a fixed set
of sliders on `ITransmitImagePreparer` itself rather than a plugin surface — see [[11-plugin-system]]
for what plugin extension points remain actually open.

**Font source for `ApplyOverlay`/`ApplyTemplate`**: `SixLabors.Fonts.SystemFonts` enumeration can legitimately be empty on a minimal Linux install (no system fonts registered) -- a real cross-platform trap for a feature that burns text into pixel data, not just displays it via the OS's own font stack the way UI chrome text does. Resolved by bundling open-license font files (loaded into a `SixLabors.Fonts.FontCollection` explicitly at startup, never relying on system enumeration) -- **done**: `TransmitImagePreparer`'s constructor registers DejaVu Sans Mono and the Barlow family (Regular/Bold/Italic/BoldItalic variants), surfaced via `ITransmitImagePreparer.AvailableFontFamilies`; both have their own [LICENSES.md](../LICENSES.md) entries per CLAUDE.md's license-audit rule.

Image decode/encode to/from standard file formats (PNG/JPEG/BMP) uses `SixLabors.ImageSharp` (cross-platform, no GDI+/System.Drawing dependency, which is Windows-only and increasingly discouraged even there) rather than porting the legacy `Draw.cpp` GDI wrapper.

## RX flow

`IReceivedImageBuffer` is populated by an application-layer adapter subscribing to `ISstvDecoder.LineDecoded` ([[06-sstv-dsp]]), so the UI's live "image filling in top-to-bottom as it decodes" behavior (legacy `RxView`) falls out of the same incremental event stream the decoder already produces — no separate polling or redraw timer needed. On decode completion, the image is auto-saved to the configured RX history folder (see [[08-logging]] for how RX images link to logged QSOs) using a filename scheme preserving legacy conventions (timestamp + mode + optional callsign) for continuity with users' existing archives.

## TX flow

1. User selects a source image (file, stock picker, or clipboard paste — `TxControlsPaneViewModel`'s existing flow, plus a clipboard image source added in a later pass, see below) — webcam/screen-capture frame capture, OS drag-drop as an image source, and legacy `PerSpect.cpp` perspective correction, stay deferred (see below).
2. The picked image loads at its ORIGINAL/native resolution (a real behavior change from Phase 4's first slice, which auto-resized straight to the mode's exact dimensions with no edit step at all) and opens the TX image editor (see below) for crop/resize/stretch/overlay.
3. `ITransmitImagePreparer`'s `Crop` → `Resize` → `ApplyAdjustments` → `ApplyTemplate` pipeline (that exact order — see the interface's own doc comment for why) produces the final mode-exact image.
4. Prepared image is handed to `ISstvEncoder.EncodeAsync` ([[06-sstv-dsp]]).

## TX image editor

Combines crop/resize/stretch, brightness/contrast/saturation/gamma/sharpen/denoise adjustments, and
a template-element compositor (text/image/box, see [[15-template-designer]]) in one editor with a
realtime preview — user's own framing: "modern mini image editor functionality... more 2026 instead
of 2020," not legacy's "type numbers into a form, click apply, hope it looks right" flow. Full
macro-key placeholder auto-substitution remains explicitly deferred — user's own words, "that's for
later"; filters and the QSL/template designer, both once deferred by that same line, have since
shipped (see below).

**Placement — updated for the shipped fixed-tab layout** ([[09-ui]]): the editor is the CENTER
column of the Transmit tab's own 3-column grid (`MainWindow.axaml`), swapped in via
`MainViewModel.ActiveEditor` (a nullable `ContentControl.Content` binding — null renders nothing,
no separate visibility toggle needed) between the left TX-controls column and the right
queue/log-cards column — not a separate OS-level `Window`, and not sharing space with RX History
(RX History now lives in its own Gallery tab, see "RX history" below). This keeps
RX/TX/history/editing reachable from one window rather than a second window to manage separately,
honoring the "unified working area" principle ([[feedback_ui_effort_allocation]] memory) — the
same goal the original dockable-pane design pursued, achieved differently once the dockable-pane
architecture itself was replaced by the fixed-tab shell.

**Realtime preview — what "TX-accurate" actually means this pass**: the preview panel renders the
*real* `ITransmitImagePreparer` output (`ImageSourceBitmapConverter.ToBitmap(Crop→Resize→ApplyAdjustments→ApplyTemplate(...))`),
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
- **Stale claim removed**: this section used to say free-typed text was deliberately **not** wired
  to an auto-substituting macro-key system, for lack of a data source. A macro system has since
  shipped (`IMacroTextResolver`/`MacroTextResolver`, `ScanlineStudio.Application`) — a scoped-down
  C# port of legacy `MacroText` (`Main.cpp:10679-10833`) covering `%m` (operator callsign)/`%D`/`%T`
  plus new `{name}`/`{grid}`/`{freq}`/`{mode}`/`{dist}`/`{bearing}` tokens and generic `{word}`
  template variables (see [[15-template-designer]]'s fill-bar mechanism). An element's `Content`
  text is free-typed as before, but is resolved through this macro system before
  `ApplyTemplate`/`ApplyOverlay` ever sees it — an unrecognized `{word}` token is left verbatim
  rather than silently vanishing (a deliberate plan-review decision, not an oversight).
- Overflow (text wider than the image at narrow SSTV modes): clipped at the image bounds for v1 —
  simplest safe default; shrink-to-fit is a possible future refinement, not needed to ship this.

**Mode-change interaction**: because crop region and overlay positions are stored in `NormalizedRect`/
relative (0..1) coordinates rather than absolute pixels, `TxControlsPaneViewModel`'s already-shipped
mode-change retention (Phase 4's TX stock picker work) composes with this directly — a mode change
re-runs the same `Crop→Resize→ApplyAdjustments→ApplyTemplate` pipeline against the *new* mode's `(ImageWidth,
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
setting. The full-resolution `Crop→Resize→ApplyAdjustments→ApplyTemplate` pipeline runs once, against the real
original, on "Apply"/"Done."

**Layering test gap this feature would otherwise walk through — closed**: `UiLayeringArchitectureTests`'s
`PackageReference` check (added in Phase 4's first slice) originally matched on exact package names
(`Microsoft.Data.Sqlite`, `SixLabors.ImageSharp`) — `SixLabors.ImageSharp.Drawing`/`SixLabors.Fonts`
(needed for `ApplyOverlay`/`ApplyTemplate`'s text rendering) are different package names and would
have slipped through silently. Fixed by switching the check to a `bannedPackagePrefixes` prefix
match (`"Microsoft.Data.Sqlite"`, `"SixLabors."`) — closes the exact bug class this project had
already caught twice before.

**Explicitly deferred past this pass** (unchanged/reconfirmed): legacy `PerSpect.cpp` perspective
correction. Everything else once listed here — filters, the macro-key auto-substitution system, and
the QSL/template designer — has since shipped; see the "Overlay text specifics" bullets above and
the `ITransmitImagePreparer` interface above for what actually landed. **Stale as of 2026-08-16,
updated 2026-08-18**: the QSL/template designer ([[15-template-designer]]) is no longer
deferred — it was the active 1.1 target, fully redesigned (a modern templating layer, not a legacy
`.mtm` port), and is now **implemented**. **Updated again, 2026-08-18 (later same day)**: clipboard
paste as an image-element source has since shipped (the "+ IMAGE" flyout's 4th source, alongside
File/Last-RX/RX-History, plus a Ctrl+V shortcut — `ImageSourceKind.Clipboard` /
`IFilePickerService.PickClipboardImageAsync`); OS drag-drop as an image source was NOT part of that
work and remains not built.

## Navigation: fixed Receive/Transmit/Gallery/Logbook tabs, not legacy's paged main window, not a dockable-pane shell either

Legacy's `Main.h` declares a real `TPageControl *Page` with `TabSync`/`TabRX`/`TabHist`/`TabTX`/`TabTemp`
tab sheets (`ComLib.h`'s `enum { pgSync, pgRX, pgHist, pgTX, pgTemp }`) — the RX/Hist/TX/Temp/Sync
*views* are paged, one visible at a time, switched either by the tab strip itself (`Main.cpp`'s
`PageChange`) or programmatically via `Mmsstv->AdjustPage(...)` (which only ever targets
pgRX/pgHist/pgTX/pgTemp, never pgSync). `HistView.cpp`'s `THistViewDlg` is a *separate* floating
thumbnail-browser dialog that merely drives that main-window tab (`PBClick` sets `UDHist->Position`,
double-click calls `AdjustPage(pgHist)`) — so legacy actually has two History UIs layered on each other,
not one. (Whether the waterfall/FFT panels themselves sit inside or outside the paged region isn't
verifiable from `Main.h`'s flat component list alone — not claimed either way here.)

**Superseded — this section originally described a dockable-`Tool`-pane shell (`Dock.Avalonia`,
`AppDockFactory`, an `RxToolDock`/`WaterfallToolDock` split) that never shipped in that form and has
since been fully replaced.** The shipped design ([[09-ui]]) is a fixed shell: a menu/header-card
row plus a `TabControl` with 4 source-ordered tabs — Receive (0), Transmit (1), Gallery (2), Logbook
(3) — bound two-way to `MainViewModel.SelectedTabIndex`. RX History (still named `RxHistoryPane`/
`RxHistoryPaneViewModel` in code) lives inside the **Gallery** tab, not tab-grouped alongside a live
RX Image pane; the Stock/template picker lives inline in the Transmit tab's TX Controls column (see
"Stock image library" below), not a separate dockable pane either. Legacy's paged main window is
still not replicated — RX/TX/Gallery/Logbook are each a full tab, not nested behind a sub-page a
user must dig into — but "dockable, simultaneously visible, tear-out panes" was a design that was
tried and abandoned, not the shipped shape. `Dock.Avalonia` is not a package reference of
`ScanlineStudio.UI.csproj` today.

**Dropped, not carried forward** (CLAUDE.md's removal rule — logged in
[docs/removed-features.md](../docs/removed-features.md)): legacy's `UDHist` step prev/next,
`SBLatest` jump-to-latest, `HistStat` status label, and the specific *drag-in-from-thumbnail*/
`SBCopy`/`SBPaste` mechanisms (`HistView.cpp`'s `BeginDrag` + `Main.cpp`'s `pHistView->IsPBox(...)`
drag-accept handlers, and clipboard-copy/paste of a history image). **Superseded, not actually a
gap**: this section used to say there was no history→TX/template compositing path at all — that's
since shipped a different way. `TxImageEditorPaneViewModel.ImageSourceKind` includes `RxHistory` as
a real image-element source alongside `File`/`LastRx`/`Clipboard` — an operator picks a history
entry from the "+ IMAGE" flyout to composite it into the TX template, a picker-based path rather
than legacy's drag-in gesture. Direct step-through navigation (`UDHist`) and the drag/clipboard
mechanisms above remain dropped.

## Stock image library

Legacy `StockVew.cpp` managed a user-configurable `StockDir` folder of local "TX stock" images (`TxStock1.jpg`/`.bmp`... up to `STOCKMAX` 300, paginated `STOCKPAGE` 4-at-a-time, `Main.h`) for quick reuse. Rewritten as `IStockImageLibrary` — a user-managed folder of images with thumbnails, no fixed page-size UI constraint. **Corrected claim**: this used to say the library is "indexed the same way the RX history is" — false, and the two are not analogous: `StockImageLibrary` (`ScanlineStudio.Core.Imaging`) is a live `Directory.EnumerateFiles` scan of the configured folder with no persisted index at all, unlike RX history's real SQLite-backed index (see "RX history" below). Surfaced inline in the TX Controls pane as a thumbnail strip (recently-used + library), with the existing file-browse flow kept alongside it as the direct/expert path — picking a thumbnail is a shortcut, not a replacement.

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

    // Mirrors IImageFileLoader.LoadOriginalAsync -- loads at the source file's own native
    // resolution, no resize at all, for the TX image editor's entry point.
    Task<IImageSource> LoadOriginalAsync(StockImageEntry entry, CancellationToken ct = default);
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

Legacy `HistView.cpp` + `History.bin` kept a browsable thumbnail history of received images in a proprietary binary format. Rewritten as `IReceiveHistoryStore`, backed by a lightweight embedded index (SQLite via `Microsoft.Data.Sqlite`, one row per received image) rather than a bespoke binary format — this also gives the logbook ([[08-logging]]) a natural foreign key to link a logged QSO to the image received during it. **Surfaced in the Gallery tab** ([[09-ui]]'s fixed Receive/Transmit/Gallery/Logbook `TabControl`, not a dockable pane — see "Navigation" above), still backed by `RxHistoryPane`/`RxHistoryPaneViewModel` in code — selecting a history entry loads it into a read-only preview and never touches the live `IReceivedImageBuffer` binding `RxImagePaneViewModel` owns, so browsing history cannot appear to interrupt or corrupt an in-progress live decode.

```csharp
namespace ScanlineStudio.Abstractions.Imaging;

public enum ReceiveDecodeState { Completed, Abandoned }

public sealed record ReceiveHistoryEntry(
    string Id, DateTimeOffset ReceivedAt, string ModeId, string FilePath, string? LinkedQsoId,
    ReceiveDecodeState DecodeState, string? Note = null, bool IsFlagged = false);

public sealed record ReceiveHistoryFilter(string? ModeId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public interface IReceiveHistoryStore
{
    // Fires once RecordAsync's write completes -- the Gallery list and the Receive tab's
    // "Previous frames" strip share one RxHistoryPaneViewModel singleton and both stay live off
    // this event rather than only refreshing at construction/manual-refresh/filter-change. Raised
    // on whatever thread the underlying write completes on; a subscriber marshals to the UI
    // thread itself.
    event Action<ReceiveHistoryEntry>? Recorded;

    Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default);

    // Same IImageSource-only contract as IStockImageLibrary above — no SQLite/ImageSharp type ever
    // crosses into ScanlineStudio.UI. The SQLite-backed implementation lives in a Core.* project;
    // ScanlineStudio.UI only ever sees this interface via DI.
    Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default);

    // Called by the same application-layer adapter that populates IReceivedImageBuffer, on decode completion.
    Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default);

    // Resolved saved-image folder, for the Gallery tab's Storage card.
    Task<string> GetImagesDirectoryAsync(CancellationToken ct = default);

    // Sets/clears the Gallery frame metadata card's user-entered note. False (not an exception) if
    // entryId no longer exists -- defensive; no automatic deletion path exists in production
    // today (docs/removed-features.md's "RX history retention limit" entry, 2026-08-26).
    Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default);

    // Sets the Gallery "Flagged" filter/toggle. Same missing-entryId contract as SetNoteAsync.
    Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default);

    // Sets LinkedQsoId -- the primitive behind the Gallery's "Log entry"/"Open in log" actions,
    // clicked AFTER an image is already saved. Same missing-entryId contract as SetNoteAsync.
    Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default);
}
```

**Corrected**: this section's `ReceiveHistoryEntry`/`IReceiveHistoryStore` snippet, and its
"Callsign is deliberately not a filter field... no QSO-linking UI exists yet" comment, are both
stale — QSO-linking has since shipped (`SetLinkedQsoIdAsync` above, plus the Gallery's "Log entry"/
"Open in log" actions), and the entry/interface both gained several fields not shown before
(`DecodeState`, `Note`, `IsFlagged`, the `Recorded` event, `GetImagesDirectoryAsync`,
`SetNoteAsync`, `SetFlaggedAsync`). `ReceiveDecodeState` also formalizes the `_partial_`
filename-suffix convention `ReceiveHistoryRecorder.RecordAbandonedImageAsync` already writes for a
mid-reception abandoned image (legacy has no equivalent classification) into a real, queryable
field.

**Layering test coverage — closed**: `UiLayeringArchitectureTests`'s `bannedPackagePrefixes` check
(`"Microsoft.Data.Sqlite"`, `"SixLabors."`, see "TX image editor" above) now catches a direct
`PackageReference` to either from `ScanlineStudio.UI.csproj` — the gap this section used to flag as
open is resolved.

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

- [x] `IImageSource`/`ITransmitImagePreparer` implemented on ImageSharp, unit-tested — `IImageSource` (`ArrayImageSource`) and `ITransmitImagePreparer` (`TransmitImagePreparer`: Crop/Resize/ApplyAdjustments/ApplyOverlay/ApplyTemplate/Rotate) all done, backing the TX image editor (`TxImageEditorPaneViewModel`/`TxImageEditorPaneView`). Adjustment sliders and the template compositor shipped on `ITransmitImagePreparer` itself, not as a separate `ApplyFilter`/`IImageFilter` plugin surface — see [[11-plugin-system]] for what plugin extension points remain actually open.
- [x] RX live-fill behavior verified end-to-end against a real [[06-sstv-dsp]] decode — not just a fixture waveform: Phase 3's demo used real `MiniAudioEngine` capture over a real virtual audio cable, real `AnalogFmSstvDecoder`, feeding `ReceivedImageBuffer` live.
- [x] RX history backed by a SQLite index (`SqliteReceiveHistoryStore`, `ScanlineStudio.Core.Logbook`) — done.
- [ ] Stock library was deliberately NOT given an index — `StockImageLibrary` is a live `Directory.EnumerateFiles` scan of the configured folder, not a SQLite-backed index (see "Stock image library" above); no migrated-from-legacy `History.bin` import path was ever built, and [[12-settings]] documents no such migration — a clean start with the old folder left untouched is the de facto behavior today, not a documented decision.
