using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

public sealed class ArrayImageSource : IImageSource
{
    private readonly Rgb24[] _pixels;

    public ArrayImageSource(int width, int height, Rgb24[] pixels)
    {
        if (pixels.Length != width * height)
        {
            throw new ArgumentException($"Expected {width * height} pixels, got {pixels.Length}.", nameof(pixels));
        }

        Width = width;
        Height = height;
        _pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<Rgb24> GetScanline(int y) => _pixels.AsSpan(y * Width, Width);

    /// <summary>Deep-copies <paramref name="source"/>'s pixels into a fresh, independent instance --
    /// callers holding a decoder's <c>LineDecoded</c>-supplied <c>DecodedImageUpdate.Image</c> must use
    /// this, never keep that reference directly, since that source is a LIVE ALIAS of the decoder's own
    /// mutable buffer (see <c>ISstvDecoder.LineDecoded</c>'s own doc comment). Same technique
    /// <c>ReceivedImageBuffer</c>/<c>ReceiveHistoryRecorder</c> already use via their own private
    /// copies of this exact loop.</summary>
    public static ArrayImageSource CopyFrom(IImageSource source)
    {
        var pixels = new Rgb24[source.Width * source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            source.GetScanline(y).CopyTo(pixels.AsSpan(y * source.Width, source.Width));
        }

        return new ArrayImageSource(source.Width, source.Height, pixels);
    }
}
