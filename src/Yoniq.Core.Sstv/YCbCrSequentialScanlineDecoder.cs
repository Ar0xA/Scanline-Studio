using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

internal sealed class YCbCrSequentialScanlineDecoder : IScanlineDecoder
{
    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        Func<int, int, double> averageFrequencyInWindow,
        Rgb24[] pixels)
    {
        var y = new double[mode.ImageWidth];
        var rMinusY = new double[mode.ImageWidth];
        var bMinusY = new double[mode.ImageWidth];
        var idealSamplesSoFar = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is ScanSegment scan)
            {
                var destination = scan.ChannelName switch
                {
                    "Y" => y,
                    "RY" => rMinusY,
                    "BY" => bMinusY,
                    _ => throw new NotSupportedException($"Unknown channel '{scan.ChannelName}'."),
                };

                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth;
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    idealSamplesSoFar += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);

                    var avgFreq = averageFrequencyInWindow(startSample, endSample);
                    destination[x] = (avgFreq - 1500) * 256.0 / (2300 - 1500);
                }
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
            }
        }

        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var (r, g, b) = YCbCr.ToRgb(y[x], rMinusY[x], bMinusY[x]);
            pixels[lineIndex * mode.ImageWidth + x] = new Rgb24(r, g, b);
        }
    }
}
