using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Imaging;

/// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) --
/// <see cref="BgraPixelBuffer"/> -&gt; Avalonia <see cref="WriteableBitmap"/> conversion, sibling to
/// <see cref="ImageSourceBitmapConverter"/>. <see cref="AlphaFormat.Premul"/> (not
/// <see cref="AlphaFormat.Opaque"/> like that sibling) -- <see cref="BgraPixelBuffer"/>'s own contract
/// is PREMULTIPLIED alpha, already in the exact BGRA byte order this converter targets, so this is a
/// row-by-row COPY, not a per-pixel channel reorder. Row-by-row honoring <c>RowBytes</c> (not a flat
/// single <c>Marshal.Copy</c> across the whole buffer) mirrors
/// <see cref="ImageSourceBitmapConverter.BlitInto"/>'s own already-established, empirically-required
/// pattern -- a locked <see cref="WriteableBitmap"/>'s stride is not contractually <c>width * 4</c>, a
/// flat copy risks heap corruption/skew on any backend with row padding.</summary>
internal static class BgraPixelBufferConverter
{
    public static WriteableBitmap ToBitmap(BgraPixelBuffer buffer)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(buffer.Width, buffer.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using var frameBuffer = bitmap.Lock();
        var rowBytes = buffer.Width * 4;
        for (var y = 0; y < buffer.Height; y++)
        {
            Marshal.Copy(buffer.Pixels, y * rowBytes, frameBuffer.Address + (y * frameBuffer.RowBytes), rowBytes);
        }

        return bitmap;
    }
}
