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
    Task SaveAsync(string path, CancellationToken ct);
}

public interface ITransmitImagePreparer
{
    IImageSource Crop(IImageSource source, Rectangle region);
    IImageSource Resize(IImageSource source, int width, int height, ResizeQuality quality);
    IImageSource ApplyFilter(IImageSource source, IImageFilter filter);
    IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay);
}
```

Image decode/encode to/from standard file formats (PNG/JPEG/BMP) uses `SixLabors.ImageSharp` (cross-platform, no GDI+/System.Drawing dependency, which is Windows-only and increasingly discouraged even there) rather than porting the legacy `Draw.cpp` GDI wrapper.

## RX flow

`IReceivedImageBuffer` is populated by an application-layer adapter subscribing to `ISstvDecoder.LineDecoded` ([[06-sstv-dsp]]), so the UI's live "image filling in top-to-bottom as it decodes" behavior (legacy `RxView`) falls out of the same incremental event stream the decoder already produces — no separate polling or redraw timer needed. On decode completion, the image is auto-saved to the configured RX history folder (see [[08-logging]] for how RX images link to logged QSOs) using a filename scheme preserving legacy conventions (timestamp + mode + optional callsign) for continuity with users' existing archives.

## TX flow

1. User selects a source image (file, clipboard paste, or webcam/screen-capture frame — legacy `PerSpect.cpp`/perspective-correction tooling is deferred, see below).
2. `ITransmitImagePreparer` crop/resize/filter operations mirror legacy `PicRect`/`PicFilte` (brightness/contrast/sharpen presets), implemented as composable `IImageFilter` instances rather than the legacy fixed dialog-bound filter list, so new filters can be added without new dialog code (ties into [[11-plugin-system]] — filters are a plugin extension point).
3. `ImageOverlay` covers legacy macro-key text/callsign overlay burned into the TX image (`TextIn.cpp`/`TextEdit.cpp` equivalent) — positioned text pulling live values (own callsign, contact's callsign, free text) from the same template/placeholder mechanism used by macro keys in [[09-ui]]. This is a deliberately minimal v1 stand-in for the *much* larger legacy QSL/template designer (`Draw.cpp`, `.mtm` files — vector shapes, boxes, embedded pictures, OLE objects, not just text) — see [[15-template-designer]] for that subsystem's full scope, which is specified but deferred past v1.
4. Prepared image is handed to `ISstvEncoder.EncodeAsync` ([[06-sstv-dsp]]).

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
    Task<IReadOnlyList<StockImageEntry>> ListAsync(CancellationToken ct);

    // Thumbnails and full loads both return IImageSource — the same abstraction IImageFileLoader
    // already returns — never a UI-toolkit bitmap type or an ImageSharp type; ScanlineStudio.UI owns
    // converting IImageSource -> Avalonia Bitmap via the existing ImageSourceBitmapConverter, exactly
    // like every other pane today.
    Task<IImageSource> LoadThumbnailAsync(StockImageEntry entry, int maxDimension, CancellationToken ct);

    // Mirrors IImageFileLoader.LoadAsync's fit-to-mode contract, so TxControlsPaneViewModel can treat
    // a stock pick and a browsed file identically once loaded.
    Task<IImageSource> LoadFullAsync(StockImageEntry entry, int targetWidth, int targetHeight, CancellationToken ct);
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
    Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct);

    // Same IImageSource-only contract as IStockImageLibrary above — no SQLite/ImageSharp type ever
    // crosses into ScanlineStudio.UI. The SQLite-backed implementation lives in a Core.* project;
    // ScanlineStudio.UI only ever sees this interface via DI.
    Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct);

    // Called by the same application-layer adapter that populates IReceivedImageBuffer, on decode completion.
    Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct);
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
- The full QSL/template designer and `.mtm` format — see [[15-template-designer]] — is specified separately and deferred past v1; `ImageOverlay` above covers only the text-overlay subset needed for basic macro-key TX prep.

## Testing

- `ITransmitImagePreparer` operations are pure functions over `IImageSource` — unit-tested with small synthetic bitmaps and pixel-exact assertions.
- `IReceivedImageBuffer` incremental-update behavior is tested by feeding synthetic `LineDecoded` events and asserting the buffer's pixel state after each.
- `IReceiveHistoryStore` is tested against a temp SQLite file, verifying round-trip of inserted records.

## Definition of done

- [ ] `IImageSource`/`ITransmitImagePreparer` implemented on ImageSharp, unit-tested. `IImageSource` yes (`ArrayImageSource`); `ITransmitImagePreparer` (crop/resize/filter/overlay) is still Phase 4 — Phase 3 only built the minimal `IImageFileLoader` (load + fit-to-mode resize, no user-facing crop/resize tooling).
- [x] RX live-fill behavior verified end-to-end against a real [[06-sstv-dsp]] decode — not just a fixture waveform: Phase 3's demo used real `MiniAudioEngine` capture over a real virtual audio cable, real `AnalogFmSstvDecoder`, feeding `ReceivedImageBuffer` live.
- [ ] RX history and stock library backed by SQLite index, migrated-from-legacy path documented in [[12-settings]] (best-effort import of existing `History.bin` if feasible, otherwise a clean start with the old folder left untouched).
