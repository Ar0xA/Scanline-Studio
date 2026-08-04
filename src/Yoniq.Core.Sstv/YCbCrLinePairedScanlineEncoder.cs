using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>MP/PD-family: Y(odd line), R-Y, B-Y, Y(even line) — one chroma pair shared between two
/// luma lines. Read from <c>TMmsstv::LineMP</c>/<c>LinePD</c> (`Main.cpp`), which are structurally
/// identical (only sync/porch/scan durations differ).</summary>
internal sealed class YCbCrLinePairedScanlineEncoder : IScanlineEncoder
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
                    var sourceRow = scan.ChannelName == "Y2" ? lineIndex + 1 : lineIndex; // Y1/RY/BY read the odd (first) row
                    for (var x = 0; x < mode.ImageWidth; x++)
                    {
                        var pixel = image.GetScanline(sourceRow)[x];
                        var (y, rMinusY, bMinusY) = YCbCr.FromRgb(pixel.R, pixel.G, pixel.B);
                        var value = scan.ChannelName switch
                        {
                            "Y1" or "Y2" => y,
                            "RY" => rMinusY,
                            "BY" => bMinusY,
                            _ => throw new NotSupportedException($"Unknown channel '{scan.ChannelName}'."),
                        };
                        yield return (YCbCr.ColorToFreq(value, mode.LuminanceMinHz, mode.LuminanceMaxHz), perPixelDurationMs);
                    }

                    break;
            }
        }
    }
}
