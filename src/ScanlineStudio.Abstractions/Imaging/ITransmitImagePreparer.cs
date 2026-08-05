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

    /// <summary>Must run AFTER <see cref="Resize"/>, not before — overlay text is rasterized at
    /// the FINAL mode dimensions, so a non-aspect-preserving "stretch" resize never
    /// smears/distorts already-drawn glyphs. Crop -&gt; Resize -&gt; ApplyOverlay is the only
    /// correct order.</summary>
    IImageSource ApplyOverlay(IImageSource source, ImageOverlay overlay);
}
