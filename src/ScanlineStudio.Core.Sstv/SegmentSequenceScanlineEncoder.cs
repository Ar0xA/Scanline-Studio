using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Channel identity a <see cref="ScanSegment.ChannelName"/> resolves to, once per scan segment.</summary>
internal enum ScanChannel
{
    R,
    G,
    B,
    Y,
    RY,
    BY,
    Y1,
    Y2,
}

/// <summary>
/// Shared Sync/Hold/Scan walk over <see cref="SstvModeDefinition.LineSegments"/> for the families whose
/// line shape is pure segment data (RGB-sequential, YCbCr-sequential, line-paired, mono-averaged).
/// Each family supplies only which channel names it accepts and how one pixel's colour value is read.
/// Segment kinds without a case here (e.g. <see cref="ToneSelectorSegment"/>) emit nothing.
/// </summary>
internal abstract class SegmentSequenceScanlineEncoder : IScanlineEncoder
{
    /// <inheritdoc/>
    public virtual int RowsPerTransmissionLine => 1;

    public IEnumerable<(double FrequencyHz, double DurationMs)> GenerateLine(SstvModeDefinition mode, IImageSource image, int lineIndex)
    {
        // A hold repeats the last transmitted tone, as legacy's MR/ML gaps reuse the hoisted `short d;` (TMmsstv::LineMR, Main.cpp:6766-6782).
        // Not a fixed 1900 Hz hold: 1900 is in-band mid-grey and would pull RX pixel integration toward grey.
        // 1900.0 is only a fallback for a hold before any tone, which no registered mode has.
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
                    var channel = ResolveChannel(scan.ChannelName);
                    var perPixelDurationMs = scan.DurationMs / mode.ImageWidth;
                    for (var x = 0; x < mode.ImageWidth; x++)
                    {
                        // PixelValue re-fetches the scanline per pixel: a ReadOnlySpan<T> cannot be held across a yield return.
                        lastFrequencyHz = YCbCr.ColorToFreq(PixelValue(mode, image, lineIndex, channel, x), mode.LuminanceMinHz, mode.LuminanceMaxHz);
                        yield return (lastFrequencyHz, perPixelDurationMs);
                    }

                    break;
            }
        }
    }

    /// <summary>Maps a segment's channel name to this family's channel, throwing
    /// <see cref="NotSupportedException"/> for a name the family does not accept.</summary>
    protected abstract ScanChannel ResolveChannel(string channelName);

    /// <summary>The already-integer colour value (0-255) of pixel <paramref name="x"/> for
    /// <paramref name="channel"/>, in legacy's integer domain for <see cref="YCbCr.ColorToFreq"/>.</summary>
    protected abstract double PixelValue(SstvModeDefinition mode, IImageSource image, int lineIndex, ScanChannel channel, int x);

    private protected static NotSupportedException UnknownChannel(string channelName) =>
        new($"Unknown channel '{channelName}'.");
}
