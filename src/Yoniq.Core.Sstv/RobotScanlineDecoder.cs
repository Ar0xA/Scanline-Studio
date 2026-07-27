using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>
/// Robot-family decoder counterpart to <see cref="RobotScanlineEncoder"/>. Decodes Y at full
/// resolution every line; the color-select tone tells it whether this line's chroma scan is R-Y or
/// B-Y, and the *other* channel's most recently decoded values are carried over from a previous
/// line (persisted instance state) — mirrors legacy's <c>m_D36[2][320]</c> arrays in
/// <c>Main.cpp</c>'s RX decode switch. A new decoder strategy instance is created per session (see
/// <see cref="IScanlineDecoder"/>'s doc comment), so this cross-line state never leaks between
/// unrelated decodes.
/// </summary>
internal sealed class RobotScanlineDecoder : IScanlineDecoder
{
    private double[]? _rMinusY;
    private double[]? _bMinusY;

    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        Func<int, int, double> averageFrequencyInWindow,
        Rgb24[] pixels)
    {
        _rMinusY ??= new double[mode.ImageWidth];
        _bMinusY ??= new double[mode.ImageWidth];

        var idealSamplesSoFar = 0.0;
        var y = new double[mode.ImageWidth];
        var isEvenLine = true;
        var scanSegmentsSeen = 0;

        foreach (var segment in mode.LineSegments)
        {
            switch (segment)
            {
                case ScanSegment scan when scanSegmentsSeen == 0:
                    DecodePixels(scan, mode.ImageWidth, sampleRate, lineStartSample, averageFrequencyInWindow, ref idealSamplesSoFar, y);
                    scanSegmentsSeen++;
                    break;

                case ToneSelectorSegment selector:
                {
                    var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    idealSamplesSoFar += selector.DurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    var avgFreq = averageFrequencyInWindow(startSample, endSample);
                    var midpoint = (selector.LowFrequencyHz + selector.HighFrequencyHz) / 2;
                    isEvenLine = avgFreq < midpoint; // closer to LowFrequencyHz (R-Y) => even line
                    break;
                }

                case ScanSegment chromaScan:
                {
                    var target = isEvenLine ? _rMinusY : _bMinusY;
                    DecodePixels(chromaScan, mode.ImageWidth, sampleRate, lineStartSample, averageFrequencyInWindow, ref idealSamplesSoFar, target!);
                    scanSegmentsSeen++;
                    break;
                }

                default:
                    idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
                    break;
            }
        }

        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var (r, g, b) = YCbCr.ToRgb(y[x], _rMinusY![x], _bMinusY![x]);
            pixels[lineIndex * mode.ImageWidth + x] = new Rgb24(r, g, b);
        }
    }

    private static void DecodePixels(
        ScanSegment scan,
        int width,
        int sampleRate,
        int lineStartSample,
        Func<int, int, double> averageFrequencyInWindow,
        ref double idealSamplesSoFar,
        double[] destination)
    {
        var perPixelDurationMs = scan.DurationMs / width;
        for (var x = 0; x < width; x++)
        {
            var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
            idealSamplesSoFar += perPixelDurationMs / 1000.0 * sampleRate;
            var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);

            var avgFreq = averageFrequencyInWindow(startSample, endSample);
            destination[x] = (avgFreq - 1500) * 256.0 / (2300 - 1500); // inverse of ColorToFreq, not +1500
        }
    }
}
