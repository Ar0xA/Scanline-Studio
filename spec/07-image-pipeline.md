# Image Pipeline

## Related

[[06-sstv-dsp]] (source/sink of raw image data) · consumed by → [[09-ui]] · replaces `RxView.cpp`, `PicRect.cpp`, `PicRectDlg.cpp`, `PicFilte.cpp`, `PicSel.cpp`, `ZoomView.cpp`, `PrevView.cpp`, `StockVew.cpp`/`TxStock*.jpg`, `HistView.cpp`/`History.bin`

## Purpose

Everything image-related that sits between the DSP core and the screen/disk: receiving images progressively as they decode, preparing images for transmission (crop/resize/filter/overlay), a picture library for TX stock images, and a receive history.

## Core abstractions

```csharp
namespace Yoniq.Abstractions.Imaging;

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

## Stock image library

Legacy `StockVew.cpp` managed a small fixed set of local "TX stock" images (`TxStock1.jpg`...`TxStock10.jpg`) for quick reuse. Rewritten as `IStockImageLibrary` — a user-managed folder of images with thumbnails, no longer hardcoded to a fixed count, indexed the same way the RX history is (see below) for UI consistency.

## RX history

Legacy `HistView.cpp` + `History.bin` kept a browsable thumbnail history of received images in a proprietary binary format. Rewritten as `IReceiveHistoryStore`, backed by a lightweight embedded index (SQLite via `Microsoft.Data.Sqlite`, one row per received image: timestamp, mode, file path, linked QSO id if any) rather than a bespoke binary format — this also gives the logbook ([[08-logging]]) a natural foreign key to link a logged QSO to the image received during it.

## Explicitly deferred (not v1)

- Perspective correction (`PerSpect.cpp`) and webcam capture (`PicSel.cpp` camera path) are useful but not core to preserving SSTV send/receive behavior; tracked as a post-v1 item in [[14-roadmap]] rather than blocking the core rewrite.
- The full QSL/template designer and `.mtm` format — see [[15-template-designer]] — is specified separately and deferred past v1; `ImageOverlay` above covers only the text-overlay subset needed for basic macro-key TX prep.

## Testing

- `ITransmitImagePreparer` operations are pure functions over `IImageSource` — unit-tested with small synthetic bitmaps and pixel-exact assertions.
- `IReceivedImageBuffer` incremental-update behavior is tested by feeding synthetic `LineDecoded` events and asserting the buffer's pixel state after each.
- `IReceiveHistoryStore` is tested against a temp SQLite file, verifying round-trip of inserted records.

## Definition of done

- [ ] `IImageSource`/`ITransmitImagePreparer` implemented on ImageSharp, unit-tested.
- [ ] RX live-fill behavior verified end-to-end against a real [[06-sstv-dsp]] decode of a fixture waveform.
- [ ] RX history and stock library backed by SQLite index, migrated-from-legacy path documented in [[12-settings]] (best-effort import of existing `History.bin` if feasible, otherwise a clean start with the old folder left untouched).
