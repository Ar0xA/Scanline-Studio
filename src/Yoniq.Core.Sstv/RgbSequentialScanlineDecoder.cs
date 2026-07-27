using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

internal sealed class RgbSequentialScanlineDecoder : IScanlineDecoder
{
    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        Func<int, int, double> averageFrequencyInWindow,
        Rgb24[] pixels)
    {
        // Mirrors the encoder's running-accumulator approach (see AnalogFmSstvEncoder) so pixel
        // window boundaries line up with where the encoder actually placed them, rather than each
        // side independently rounding ms->samples and drifting apart over 320 pixels x 3 channels.
        var idealSamplesSoFar = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is ScanSegment scan)
            {
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth;
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    idealSamplesSoFar += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);

                    var avgFreq = averageFrequencyInWindow(startSample, endSample);
                    var value = (byte)Math.Clamp(
                        (avgFreq - mode.LuminanceMinHz) / (mode.LuminanceMaxHz - mode.LuminanceMinHz) * 255.0,
                        0,
                        255);

                    var index = lineIndex * mode.ImageWidth + x;
                    pixels[index] = SetChannel(pixels[index], scan.ChannelName, value);
                }
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
            }
        }
    }

    private static Rgb24 SetChannel(Rgb24 pixel, string channelName, byte value) => channelName switch
    {
        "R" => pixel with { R = value },
        "G" => pixel with { G = value },
        "B" => pixel with { B = value },
        _ => throw new NotSupportedException($"Unknown channel '{channelName}'."),
    };
}
