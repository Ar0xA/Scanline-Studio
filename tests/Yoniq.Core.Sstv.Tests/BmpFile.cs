using Yoniq.Abstractions.Imaging;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Test-only, minimal 24bpp BMP reader for golden-vector fixtures captured from the real legacy
/// binary (see Fixtures/GoldenVectors/README.md). Deliberately hand-rolled rather than
/// System.Drawing: CLAUDE.md bans System.Drawing/Win32 interop from <c>Yoniq.Core.*</c>/<c>Yoniq.UI</c>,
/// and there is no existing BMP reader anywhere in this repo to reuse (grep-confirmed) -- this stays
/// in the test project rather than being promoted into a shared library, since nothing outside the
/// golden-vector fixtures needs BMP support (see <see cref="MmvFile"/>'s doc comment for the same
/// reasoning about the `.mmv` reader).
///
/// Only supports what the fixtures actually are: uncompressed (BI_RGB) 24-bit-per-pixel, no color
/// table, `BITMAPINFOHEADER` (40-byte) variant -- confirmed by reading all four fixture files'
/// headers directly (`bfType="BM"`, `biCompression=0`, `biBitCount=24`, `biSize=40`, `biClrUsed=0`).
/// Throws rather than guessing for anything else, since a silent wrong-format read would corrupt a
/// pixel-level comparison without any visible symptom.
/// </summary>
internal static class BmpFile
{
    public static ArrayImageSource Read(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 54 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            throw new InvalidDataException($"'{path}' is not a BMP file (missing 'BM' magic).");
        }

        var pixelDataOffset = BitConverter.ToInt32(data, 10);
        var headerSize = BitConverter.ToInt32(data, 14);
        if (headerSize != 40)
        {
            throw new NotSupportedException(
                $"'{path}' uses a {headerSize}-byte DIB header; only the 40-byte BITMAPINFOHEADER variant is supported.");
        }

        var width = BitConverter.ToInt32(data, 18);
        var heightRaw = BitConverter.ToInt32(data, 22);
        var bitCount = BitConverter.ToInt16(data, 28);
        var compression = BitConverter.ToInt32(data, 30);

        if (bitCount != 24 || compression != 0)
        {
            throw new NotSupportedException(
                $"'{path}': only uncompressed 24bpp BMPs are supported (found bitCount={bitCount}, compression={compression}).");
        }

        // Negative height means top-down storage; every fixture here is the standard positive
        // (bottom-up) DIB layout, confirmed by direct header inspection -- throw rather than
        // silently mis-orienting a comparison if that ever changes.
        if (heightRaw <= 0)
        {
            throw new NotSupportedException($"'{path}': top-down (negative-height) BMPs are not supported.");
        }

        var height = heightRaw;
        var pixels = new Rgb24[width * height];

        // Each row is padded to a 4-byte boundary; all four fixtures happen to need no padding
        // (320*3=960, already a multiple of 4), but this is computed generally rather than assumed.
        var stride = ((width * 3) + 3) / 4 * 4;

        for (var fileRow = 0; fileRow < height; fileRow++)
        {
            // BMP rows are stored bottom-up: the first row in the file is the visual bottom of the
            // image. Flip here so pixels[] is addressed top-down, matching every other IImageSource
            // in this codebase (GetScanline(0) is the visual top row).
            var imageRow = height - 1 - fileRow;
            var rowOffset = pixelDataOffset + (fileRow * stride);

            for (var x = 0; x < width; x++)
            {
                var pixelOffset = rowOffset + (x * 3);
                // BMP stores BGR, not RGB.
                var b = data[pixelOffset];
                var g = data[pixelOffset + 1];
                var r = data[pixelOffset + 2];
                pixels[(imageRow * width) + x] = new Rgb24(r, g, b);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
