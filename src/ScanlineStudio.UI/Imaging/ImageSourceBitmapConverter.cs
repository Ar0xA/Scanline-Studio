using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Imaging;

/// <summary><see cref="IImageSource"/> -&gt; Avalonia <see cref="WriteableBitmap"/> conversion --
/// lives in `ScanlineStudio.UI` deliberately, not `ScanlineStudio.Core.Imaging` (which may not hold an Avalonia type at
/// all, spec/01-architecture.md's layering rule). Shared by <c>RxImagePaneViewModel</c> (RX preview)
/// and <c>TxControlsPaneViewModel</c> (TX image preview) rather than duplicated in both.</summary>
internal static class ImageSourceBitmapConverter
{
    public static WriteableBitmap ToBitmap(IImageSource source)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(source.Width, source.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);

        using var frameBuffer = bitmap.Lock();
        var rowBuffer = new byte[source.Width * 4];
        for (var y = 0; y < source.Height; y++)
        {
            var row = source.GetScanline(y);
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = row[x];
                var offset = x * 4;
                rowBuffer[offset + 0] = pixel.B;
                rowBuffer[offset + 1] = pixel.G;
                rowBuffer[offset + 2] = pixel.R;
                rowBuffer[offset + 3] = 255;
            }

            Marshal.Copy(rowBuffer, 0, frameBuffer.Address + (y * frameBuffer.RowBytes), rowBuffer.Length);
        }

        return bitmap;
    }
}
