using Yoniq.Abstractions.Imaging;

namespace Yoniq.Core.Imaging;

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
}
