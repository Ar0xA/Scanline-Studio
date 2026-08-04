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
                        // SHOULD item 4 (spec/14-roadmap.md): legacy's real averaging (TMmsstv::LineRM,
                        // Main.cpp:6796-6799: `YY = (YY + Y[x]) / 2;`) is INTEGER division, a THIRD
                        // truncation specific to this family (beyond FromRgb's and ColorToFreq's own
                        // two) -- found by reading LineRM directly, not assumed from the other
                        // families' shape. y1/y2 are already truncated+clamped by FromRgb (always
                        // non-negative), so Math.Floor reproduces C++'s truncate-toward-zero exactly
                        // here too.
                        var averagedY = Math.Floor((y1 + y2) / 2.0);
                        yield return (YCbCr.ColorToFreq(averagedY, mode.LuminanceMinHz, mode.LuminanceMaxHz), perPixelDurationMs);
                    }

                    break;
            }
        }
    }
}
