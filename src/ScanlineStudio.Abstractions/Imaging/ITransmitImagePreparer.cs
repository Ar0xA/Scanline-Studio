namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Crop/resize/overlay pipeline for TX image prep — see spec/07-image-pipeline.md's "TX
/// image editor" section. Declared here (not alongside its `ScanlineStudio.Core.Imaging`
/// implementation) so `ScanlineStudio.UI` can depend on the interface without pulling in a
/// concrete `SixLabors.ImageSharp`/`SixLabors.Fonts` reference, same reasoning as
/// <see cref="IImageFileLoader"/>/<see cref="IStockImageLibrary"/>.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height);

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

    /// <summary>Rotates 90° clockwise, always -- no direction parameter. Matches the TX image
    /// editor's single-button UX (4 clicks returns to the original orientation); width/height are
    /// swapped in the result.</summary>
    IImageSource Rotate(IImageSource source);
}
