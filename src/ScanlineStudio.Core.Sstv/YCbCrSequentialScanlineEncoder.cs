using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Robot 72-style: Y, R-Y, B-Y each scanned every line, no alternation. Channel identity
/// comes from <see cref="ScanSegment.ChannelName"/> ("Y"/"RY"/"BY"), timing/order is pure data —
/// see <c>TMmsstv::LineR72</c> (`Main.cpp`).</summary>
internal sealed class YCbCrSequentialScanlineEncoder : IScanlineEncoder
{
    public IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex)
    {
        // ultracode audit finding #24: MR/ML's three 0.1ms inter-channel gaps hold the LAST
        // TRANSMITTED PIXEL'S FREQUENCY (Main.cpp:6766-6782's `short d;` hoisted out of all 3 scan
        // loops specifically to reuse it), not a fixed tone -- a fixed 1900Hz literal (this class's
        // pre-fix behavior) is worse than "non-information-bearing": 1900Hz is itself an in-band
        // mid-gray luma value, so it can pull an RX integration window straddling the gap toward
        // gray, whereas legacy's hold is non-disturbing by construction. 1900.0 is only a fallback
        // for the structurally-unreachable case of a hold segment appearing before any real tone.
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
                    for (var x = 0; x < mode.ImageWidth; x++)
                    {
                        var pixel = image.GetScanline(lineIndex)[x];
                        var (y, rMinusY, bMinusY) = YCbCr.FromRgb(pixel.R, pixel.G, pixel.B);
                        var value = scan.ChannelName switch
                        {
                            "Y" => y,
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
