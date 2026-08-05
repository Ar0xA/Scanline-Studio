namespace ScanlineStudio.Abstractions.Imaging;

public interface IImageSource
{
    int Width { get; }

    int Height { get; }

    ReadOnlySpan<Rgb24> GetScanline(int y);
}
