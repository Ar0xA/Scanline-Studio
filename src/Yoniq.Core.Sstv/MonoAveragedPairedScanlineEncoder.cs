using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>RM8/RM12: read from <c>TMmsstv::LineRM</c> (`Main.cpp`) — no chroma at all. Reads two
/// consecutive source rows, averages each pixel's luminance (<c>GetRY</c>'s Y term, via
/// <see cref="YCbCr.FromRgb"/>), and scans only that single averaged value.</summary>
internal sealed class MonoAveragedPairedScanlineEncoder : IScanlineEncoder
{
    public int RowsPerTransmissionLine => 2;

    public IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex)
    {
        foreach (var lineSegment in mode.LineSegments)
        {
            switch (lineSegment)
            {
                case SyncSegment sync:
                    yield return (sync.FrequencyHz, sync.DurationMs);
                    break;

                case ScanSegment scan:
                    var perPixelDurationMs = scan.DurationMs / mode.ImageWidth;
                    for (var x = 0; x < mode.ImageWidth; x++)
                    {
                        var pixel1 = image.GetScanline(lineIndex)[x];
                        var pixel2 = image.GetScanline(lineIndex + 1)[x];
                        var (y1, _, _) = YCbCr.FromRgb(pixel1.R, pixel1.G, pixel1.B);
                        var (y2, _, _) = YCbCr.FromRgb(pixel2.R, pixel2.G, pixel2.B);
                        var averagedY = (y1 + y2) / 2.0;
                        // Divisor is 256, not 255 -- matches YCbCrSequentialScanlineEncoder and
                        // legacy's own ColorToFreq(Y): Y from GetRY/YCbCr.FromRgb already carries
                        // its own +16 headroom offset and is treated as a raw byte-scale value,
                        // not renormalized against an actual 0-255 range.
                        var frequencyHz = mode.LuminanceMinHz
                            + averagedY * (mode.LuminanceMaxHz - mode.LuminanceMinHz) / 256.0;
                        yield return (frequencyHz, perPixelDurationMs);
                    }

                    break;
            }
        }
    }
}
