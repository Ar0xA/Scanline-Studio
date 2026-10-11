using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Application;
using static ScanlineStudio.UI.ViewModels.TxImageEditorPaneViewModel;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Maps the TX editor's <see cref="RawElementSnapshot"/>s to and from the saved
/// <see cref="PersistedTemplateElement"/> format, writing and reading each element's image assets
/// under the template's own <c>assets/</c> folder. Logs through the pane's own logger and
/// <c>Log</c> class, so the emitted events keep the pane's category, event ids and templates.</summary>
internal sealed class TemplateElementPersistenceMapper
{
    private readonly ITemplateStore _templateStore;
    private readonly IImageSourceWriter _imageSourceWriter;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly ILogger _logger;

    public TemplateElementPersistenceMapper(
        ITemplateStore templateStore,
        IImageSourceWriter imageSourceWriter,
        IImageFileLoader imageFileLoader,
        ILogger logger)
    {
        _templateStore = templateStore;
        _imageSourceWriter = imageSourceWriter;
        _imageFileLoader = imageFileLoader;
        _logger = logger;
    }

    /// <summary>Builds the saved form of one element. <paramref name="computeFittedFontSizeRelative"/>
    /// is the pane's live fitted-font-size computation, called once per text element after its
    /// picture-fill asset write, so it reads the crop as of that moment.</summary>
    public async Task<PersistedTemplateElement> BuildPersistedElementAsync(
        string templateId, RawElementSnapshot raw, Func<RawTextElementSnapshot, double> computeFittedFontSizeRelative)
    {
        switch (raw)
        {
            case RawTextElementSnapshot text:
                // TX editor gap-items plan, item 4b -- same GUID-based, never-index-derived asset
                // naming as the image case below, into the SAME shared assets/ folder (no new
                // per-kind subfolder). Degrades BitmapFillEnabled to false (rather than persisting a
                // dangling Enabled=true with no asset) when Enabled is set but Source is somehow null
                // -- shouldn't be reachable from the UI (picking a picture always sets both together),
                // defensive only.
                string? bitmapFillAssetFileName = null;
                if (text.BitmapFillEnabled && text.BitmapFillSource is { } bitmapFillSource)
                {
                    bitmapFillAssetFileName = $"{Guid.NewGuid():N}.png";
                    var bitmapFillAssetPath = _templateStore.GetAssetPath(templateId, bitmapFillAssetFileName);
                    await _imageSourceWriter.WritePngAsync(bitmapFillSource, bitmapFillAssetPath);
                }

                return new PersistedTextElement(
                    text.X, text.Y, text.Width, text.Height, text.Z, text.Locked,
                    // User-reported gap (2026-09-15): FontSizeRelative here used to be the pre-grow
                    // "set" size verbatim -- see ComputeFittedFontSizeRelative's own doc comment for
                    // why GrowToFillEnabled's actual visual RESULT needs to be baked in here instead,
                    // since the flag itself is deliberately not persisted below.
                    text.Text, computeFittedFontSizeRelative(text), text.Color, text.FontFamily, text.StrokeColor, text.StrokeThickness,
                    text.ShadowColor, text.ShadowOffsetX, text.ShadowOffsetY, text.RotationDegrees,
                    text.GradientEnabled, text.GradientKind, text.GradientStartColor, text.GradientEndColor,
                    text.Bold, text.Italic,
                    text.StackColor, text.StackStepX, text.StackStepY,
                    bitmapFillAssetFileName is not null, bitmapFillAssetFileName);
            case RawBoxElementSnapshot box:
                return new PersistedBoxElement(
                    box.X, box.Y, box.Width, box.Height, box.Z, box.Locked,
                    box.FillColor, box.BorderColor, box.BorderThickness, box.Opacity, box.CornerRadius,
                    box.GradientEnabled, box.GradientKind, box.GradientStartColor, box.GradientEndColor,
                    box.PerspectiveEnabled,
                    box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y,
                    box.FillEnabled, box.RotationDegrees);
            case RawImageElementSnapshot image:
                // GUID-based, never index-derived (plan-review finding -- see PersistedImageElement's
                // own doc comment): safe against any reordering/filtering between here and the manifest
                // write, and against a partial/failed save leaving a stale name behind.
                var assetFileName = $"{Guid.NewGuid():N}.png";
                var assetPath = _templateStore.GetAssetPath(templateId, assetFileName);
                // GetAssetPath is documented pure/no-I/O (its own doc comment: "safe to call before
                // the template's directory exists") -- SaveAsync's own Directory.CreateDirectory only
                // creates the template's ROOT folder, and only runs AFTER this write (code-review
                // finding: assets are written before SaveAsync is ever called), so the "assets/"
                // subdirectory itself is never created by anything else in time. Fixed inside the
                // REAL ImageSourceWriter itself (Core.Imaging), not here -- this VM works against the
                // ITemplateStore/IImageSourceWriter ABSTRACTIONS, which make no promise their own
                // paths are real, creatable filesystem locations (FakeTemplateStore's own
                // GetAssetPath deliberately returns a non-real "/fake/templates/..." path for exactly
                // this reason); a blind Directory.CreateDirectory call here against ANY path either
                // implementation hands back doesn't belong at this layer.
                await _imageSourceWriter.WritePngAsync(image.Source, assetPath);
                var (originKind, originPayload) = image.Origin.Kind switch
                {
                    ImageSourceKind.File => (PersistedImageSourceKind.File, image.Origin.Payload),
                    ImageSourceKind.RxHistory => (PersistedImageSourceKind.RxHistory, image.Origin.Payload),
                    ImageSourceKind.LastRx => (PersistedImageSourceKind.LastRx, image.Origin.Payload),
                    ImageSourceKind.Clipboard => (PersistedImageSourceKind.Clipboard, image.Origin.Payload),
                    ImageSourceKind.Promoted => (PersistedImageSourceKind.Promoted, image.Origin.Payload),
                    _ => throw new NotSupportedException($"Unrecognized {nameof(ImageSourceKind)}: {image.Origin.Kind}."),
                };
                return new PersistedImageElement(
                    image.X, image.Y, image.Width, image.Height, image.Z, image.Locked,
                    assetFileName, image.Fit, originKind, originPayload, image.IsBackground,
                    image.NaturalPixelWidth, image.NaturalPixelHeight,
                    image.PerspectiveEnabled,
                    image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y,
                    image.RotationDegrees);
            case RawLineElementSnapshot line:
                return new PersistedLineElement(
                    line.X, line.Y, line.Width, line.Height, line.Z, line.Locked,
                    line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity);
            default:
                throw new NotSupportedException($"Unrecognized {nameof(RawElementSnapshot)}: {raw.GetType()}.");
        }
    }

    public async Task<RawElementSnapshot> ToRawElementSnapshotAsync(string templateId, PersistedTemplateElement element)
    {
        switch (element)
        {
            case PersistedTextElement text:
                // TX editor gap-items plan, item 4b -- same degrade-to-null-and-log, never-throw
                // shape as TemplateStore.LoadTextBitmapFillAsync's own thumbnail-path counterpart
                // (see that method's own doc comment for why): a missing/corrupt picture-fill asset
                // must not abort the WHOLE template load over one decorative field.
                IImageSource? bitmapFillSource = null;
                if (text.BitmapFillEnabled && !string.IsNullOrEmpty(text.BitmapFillAssetFileName))
                {
                    try
                    {
                        var bitmapFillAssetPath = _templateStore.GetAssetPath(templateId, text.BitmapFillAssetFileName);
                        bitmapFillSource = await _imageFileLoader.LoadOriginalAsync(bitmapFillAssetPath);
                    }
                    // Round-1 code-review finding: must not swallow OperationCanceledException --
                    // matches TemplateStore.LoadTextBitmapFillAsync's own identical fix.
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log.BitmapFillAssetLoadFailed(_logger, templateId, text.BitmapFillAssetFileName, ex);
                    }
                }

                return new RawTextElementSnapshot(
                    X: text.X, Y: text.Y, Width: text.Width, Height: text.Height, Z: text.Z, Locked: text.Locked,
                    Text: text.Text, FontSizeRelative: text.FontSizeRelative, Color: text.Color,
                    FontFamily: text.FontFamily, StrokeColor: text.StrokeColor, StrokeThickness: text.StrokeThickness,
                    ShadowColor: text.ShadowColor, ShadowOffsetX: text.ShadowOffsetX, ShadowOffsetY: text.ShadowOffsetY,
                    RotationDegrees: text.RotationDegrees,
                    GradientEnabled: text.GradientEnabled, GradientKind: text.GradientKind,
                    GradientStartColor: text.GradientStartColor, GradientEndColor: text.GradientEndColor,
                    Bold: text.Bold, Italic: text.Italic,
                    StackColor: text.StackColor, StackStepX: text.StackStepX, StackStepY: text.StackStepY,
                    BitmapFillEnabled: bitmapFillSource is not null, BitmapFillSource: bitmapFillSource,
                    // Grow-to-fill is not persisted; a loaded text element starts with it off.
                    GrowToFillEnabled: false);
            case PersistedBoxElement box:
                return new RawBoxElementSnapshot(
                    X: box.X, Y: box.Y, Width: box.Width, Height: box.Height, Z: box.Z, Locked: box.Locked,
                    FillColor: box.FillColor, BorderColor: box.BorderColor, BorderThickness: box.BorderThickness,
                    Opacity: box.Opacity, CornerRadius: box.CornerRadius,
                    GradientEnabled: box.GradientEnabled, GradientKind: box.GradientKind,
                    GradientStartColor: box.GradientStartColor, GradientEndColor: box.GradientEndColor,
                    PerspectiveEnabled: box.PerspectiveEnabled,
                    Corner0X: box.Corner0X, Corner0Y: box.Corner0Y, Corner1X: box.Corner1X, Corner1Y: box.Corner1Y,
                    Corner2X: box.Corner2X, Corner2Y: box.Corner2Y, Corner3X: box.Corner3X, Corner3Y: box.Corner3Y,
                    FillEnabled: box.FillEnabled, RotationDegrees: box.RotationDegrees);
            case PersistedImageElement image:
                var assetPath = _templateStore.GetAssetPath(templateId, image.AssetFileName);
                var source = await _imageFileLoader.LoadOriginalAsync(assetPath);
                // Plan-review decision: a LOADED image element's Origin always points at its own
                // copied asset file, never whatever Kind/Payload it originally had when first saved
                // (a File/RxHistory/LastRx/Clipboard origin recorded on the persisted DTO is
                // informational only -- see PersistedImageElement's own doc comment for why none of
                // the four are safe to re-resolve from later).
                var origin = new ImageSourceOrigin(ImageSourceKind.File, assetPath);
                return new RawImageElementSnapshot(
                    X: image.X, Y: image.Y, Width: image.Width, Height: image.Height, Z: image.Z, Locked: image.Locked,
                    Source: source, Fit: image.Fit, Origin: origin, IsBackground: image.IsBackground,
                    // Pre-Phase-7 templates carry no natural size. Falling back to the ASSET's own
                    // dimensions is the honest value (the asset IS the pixels this element has) and
                    // needs no migration -- same additive convention IsBackground's own doc comment
                    // describes.
                    NaturalPixelWidth: image.NaturalPixelWidth > 0 ? image.NaturalPixelWidth : source.Width,
                    NaturalPixelHeight: image.NaturalPixelHeight > 0 ? image.NaturalPixelHeight : source.Height,
                    PerspectiveEnabled: image.PerspectiveEnabled,
                    Corner0X: image.Corner0X, Corner0Y: image.Corner0Y, Corner1X: image.Corner1X, Corner1Y: image.Corner1Y,
                    Corner2X: image.Corner2X, Corner2Y: image.Corner2Y, Corner3X: image.Corner3X, Corner3Y: image.Corner3Y,
                    RotationDegrees: image.RotationDegrees);
            case PersistedLineElement line:
                return new RawLineElementSnapshot(
                    line.X, line.Y, line.Width, line.Height, line.Z, line.Locked,
                    line.X1, line.Y1, line.X2, line.Y2, line.StrokeColor, line.StrokeThickness, line.Opacity);
            default:
                throw new NotSupportedException($"Unrecognized {nameof(PersistedTemplateElement)}: {element.GetType()}.");
        }
    }
}
