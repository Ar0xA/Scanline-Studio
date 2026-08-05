using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

/// <summary>Mimics `AnalogFmSstvDecoder`'s private `MutableImageSource`: a live view over a pixel
/// array the "decoder" (the test) keeps mutating after handing out a <see cref="DecodedImageUpdate"/>
/// — lets tests prove <see cref="ReceivedImageBuffer"/> actually snapshots rather than aliasing.</summary>
internal sealed class MutableTestImageSource(int width, int height, Rgb24[] pixels) : IImageSource
{
    public int Width { get; } = width;

    public int Height { get; } = height;

    public ReadOnlySpan<Rgb24> GetScanline(int y) => pixels.AsSpan(y * Width, Width);
}
