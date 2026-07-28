using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>Robot 72-style: Y, R-Y, B-Y each scanned every line, no alternation. Channel identity
/// comes from <see cref="ScanSegment.ChannelName"/> ("Y"/"RY"/"BY"), timing/order is pure data —
/// see <c>TMmsstv::LineR72</c> (`Main.cpp`).</summary>
internal sealed class YCbCrSequentialScanlineEncoder : IScanlineEncoder
{
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
                        var pixel = image.GetScanline(lineIndex)[x];
                        var (y, rMinusY, bMinusY) = YCbCr.FromRgb(pixel.R, pixel.G, pixel.B);
                        var value = scan.ChannelName switch
                        {
                            "Y" => y,
                            "RY" => rMinusY,
                            "BY" => bMinusY,
                            _ => throw new NotSupportedException($"Unknown channel '{scan.ChannelName}'."),
                        };
                        yield return (mode.LuminanceMinHz + value * (mode.LuminanceMaxHz - mode.LuminanceMinHz) / 256.0, perPixelDurationMs);
                    }

                    break;
            }
        }
    }
}
