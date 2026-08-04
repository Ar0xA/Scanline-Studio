using System.Buffers.Binary;
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

        // Round-1-review nitpick fix: explicit little-endian reads throughout (BMP fields are
        // always little-endian on disk), matching MmvFile's own explicit-endianness convention
        // rather than relying on BitConverter's host-endianness default.
        var pixelDataOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14));
        if (headerSize != 40)
        {
            throw new NotSupportedException(
                $"'{path}' uses a {headerSize}-byte DIB header; only the 40-byte BITMAPINFOHEADER variant is supported.");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        var heightRaw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        var bitCount = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(28));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(30));

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

        // Round-2-review nitpick fix: a corrupt/malicious width or pixelDataOffset (e.g. negative)
        // used to reach the pixel loop below and fail with a bare IndexOutOfRangeException instead
        // of one of this reader's own clear messages -- same class of fix as the truncated-length
        // check a few lines down.
        if (width <= 0 || pixelDataOffset < 54)
        {
            throw new InvalidDataException(
                $"'{path}': implausible width={width} or pixelDataOffset={pixelDataOffset}.");
        }

        var height = heightRaw;
        var pixels = new Rgb24[width * height];

        // Each row is padded to a 4-byte boundary; all four fixtures happen to need no padding
        // (320*3=960, already a multiple of 4), but this is computed generally rather than assumed.
        var stride = ((width * 3) + 3) / 4 * 4;

        // Round-1-review nitpick fix: a truncated file used to throw a bare IndexOutOfRangeException
        // from deep inside the pixel loop below instead of one of this reader's own clear messages.
        var requiredLength = pixelDataOffset + ((long)height * stride);
        if (requiredLength > data.Length)
        {
            throw new InvalidDataException(
                $"'{path}' is truncated: pixel data would need {requiredLength} bytes, file has {data.Length}.");
        }

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

    /// <summary>Inverse of <see cref="Read"/> -- writes the same uncompressed 24bpp, no-color-table,
    /// 40-byte-DIB-header (BITMAPINFOHEADER) variant, bottom-up row order, BGR pixel order. Added for
    /// TX-side golden-vector fixture prep (spec/14-roadmap.md, "Milestone audit, Phase 1+2" ->
    /// "Explicit prerequisite before Phase 3") -- generates new source .bmp images for modes that
    /// don't have an existing RX-direction fixture yet, using the same gradient convention
    /// (CreateGradientTestImage's own R/G/B formula, replicated in the generator, not here) so the
    /// whole fixture set stays visually/formulaically consistent.</summary>
    public static void Write(string path, IImageSource image)
    {
        var width = image.Width;
        var height = image.Height;
        var stride = ((width * 3) + 3) / 4 * 4;
        var pixelDataOffset = 54;
        var pixelDataSize = stride * height;
        var fileSize = pixelDataOffset + pixelDataSize;

        var data = new byte[fileSize];
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), fileSize);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), pixelDataOffset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40); // DIB header size
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), height); // positive -> bottom-up
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 1); // planes
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), 24); // bitCount
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(30), 0); // compression (BI_RGB)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(34), pixelDataSize);

        for (var fileRow = 0; fileRow < height; fileRow++)
        {
            // Mirrors Read's own flip: pixels[] is addressed top-down (GetScanline(0) = visual top),
            // BMP storage is bottom-up.
            var imageRow = height - 1 - fileRow;
            var scanline = image.GetScanline(imageRow);
            var rowOffset = pixelDataOffset + (fileRow * stride);

            for (var x = 0; x < width; x++)
            {
                var pixelOffset = rowOffset + (x * 3);
                data[pixelOffset] = scanline[x].B;
                data[pixelOffset + 1] = scanline[x].G;
                data[pixelOffset + 2] = scanline[x].R;
            }
        }

        File.WriteAllBytes(path, data);
    }
}
