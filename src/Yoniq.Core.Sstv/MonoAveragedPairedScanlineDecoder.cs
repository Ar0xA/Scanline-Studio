using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

/// <summary>RM8/RM12 decode counterpart to <see cref="MonoAveragedPairedScanlineEncoder"/>. Note:
/// legacy's real RX for this family (<c>Main.cpp</c>'s <c>smRM8</c>/<c>smRM12</c> decode branch)
/// writes the calibrated pixel level directly into R/G/B with no <c>YCtoRGB</c> matrix involved at
/// all (there's no chroma to combine it with) — this port instead reconstructs gray via
/// <see cref="YCbCr.ToRgb"/> with neutral chroma, the same path every other Y-bearing family here
/// already uses, rather than inventing a third, RM-specific reconstruction convention. Neutral
/// chroma is <c>128</c>, not <c>0</c> — see <see cref="YCbCr"/>'s doc comment: R-Y/B-Y are centered
/// at 128 (mirroring legacy's <c>GetRY</c>), so 128 is "no color difference," not 0.</summary>
internal sealed class MonoAveragedPairedScanlineDecoder : IScanlineDecoder
{
    public int RowsPerTransmissionLine => 2;

    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        PixelSampleReader reader,
        Rgb24[] pixels)
    {
        var y = new double[mode.ImageWidth];
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

                    // RM8/RM12's single channel always peak-picks in legacy (Main.cpp:4437,
                    // GetPictureLevel) -- no chroma exception to worry about here, unlike the
                    // YCbCr-paired families.
                    var freq = reader.ReadPeakPicked(startSample, endSample);
                    y[x] = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);
                }
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
            }
        }

        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var (r, g, b) = YCbCr.ToRgb(y[x], 128, 128);
            var gray = new Rgb24(r, g, b);
            pixels[lineIndex * mode.ImageWidth + x] = gray;
            pixels[(lineIndex + 1) * mode.ImageWidth + x] = gray;
        }
    }
}
