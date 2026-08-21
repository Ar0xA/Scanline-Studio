using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>MP/PD/MN-family: Y(odd line), R-Y, B-Y, Y(even line) — one chroma pair shared between two
/// luma lines. Read from <c>TMmsstv::LineMP</c>/<c>LinePD</c>/<c>LineMN</c> (`Main.cpp:6733`/`:6686`/
/// `:6803`) — all three structurally identical; MP/PD differ only in sync/porch/scan durations, MN
/// additionally uses the narrow band (carried here by <c>mode.LuminanceMinHz</c>/<c>MaxHz</c>, not a
/// separate code path).</summary>
internal sealed class YCbCrLinePairedScanlineEncoder : IScanlineEncoder
{
    public int RowsPerTransmissionLine => 2;

    public IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex)
    {
        // Defensive-only, see RgbSequentialScanlineEncoder's own copy of this comment (ultracode
        // audit finding #24) -- no mode using THIS encoder has a HoldPreviousFrequencySegment today.
        var lastFrequencyHz = 1900.0;

        foreach (var lineSegment in mode.LineSegments)
        {
            switch (lineSegment)
            {
                case SyncSegment sync:
                    lastFrequencyHz = sync.FrequencyHz;
                    yield return (sync.FrequencyHz, sync.DurationMs);
                    break;

                case HoldPreviousFrequencySegment hold:
                    yield return (lastFrequencyHz, hold.DurationMs);
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
                        lastFrequencyHz = YCbCr.ColorToFreq(value, mode.LuminanceMinHz, mode.LuminanceMaxHz);
                        yield return (lastFrequencyHz, perPixelDurationMs);
                    }

                    break;
            }
        }
    }
}
