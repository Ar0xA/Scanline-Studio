using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>Martin/Scottie-family: N sequential same-shaped channel scans per line, timing and
/// channel order are pure <see cref="SstvModeDefinition.LineSegments"/> data.</summary>
internal sealed class RgbSequentialScanlineEncoder : IScanlineEncoder
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
                        // Re-fetched per pixel (not hoisted) because ReadOnlySpan<T> can't be
                        // stored across a yield-return boundary in an iterator state machine.
                        var value = GetChannelValue(image.GetScanline(lineIndex)[x], scan.ChannelName);
                        // SHOULD item 4 (spec/14-roadmap.md): value is already an integer RGB byte
                        // (no GetRY step for this family, matching legacy's own ColorToFreq(cp->b.r)
                        // etc. call sites, Main.cpp:6610-6629), so only YCbCr.ColorToFreq's own
                        // internal integer-division truncation applies here, not FromRgb's.
                        yield return (YCbCr.ColorToFreq(value, mode.LuminanceMinHz, mode.LuminanceMaxHz), perPixelDurationMs);
                    }

                    break;
            }
        }
    }

    internal static byte GetChannelValue(Rgb24 pixel, string channelName) => channelName switch
    {
        "R" => pixel.R,
        "G" => pixel.G,
        "B" => pixel.B,
        _ => throw new NotSupportedException($"Unknown channel '{channelName}'."),
    };
}
