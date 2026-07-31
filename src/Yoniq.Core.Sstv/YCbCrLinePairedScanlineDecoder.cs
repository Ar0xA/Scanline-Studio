using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.Core.Sstv;

internal sealed class YCbCrLinePairedScanlineDecoder : IScanlineDecoder
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
        var y1 = new double[mode.ImageWidth];
        var y2 = new double[mode.ImageWidth];
        var rMinusY = new double[mode.ImageWidth];
        var bMinusY = new double[mode.ImageWidth];
        var idealSamplesSoFar = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is ScanSegment scan)
            {
                // Piece 10: peak-vs-bare folded into the same exhaustive channel switch that already
                // picks the destination array. "Y1"/"Y2" BOTH peak-pick in legacy (Main.cpp:4385/4420,
                // GetPictureLevel) -- PD/MP/MN's two luma segments are not a "first only" case; "RY"/
                // "BY" stay bare (Main.cpp:4393/4402, GetPixelLevel).
                (double[] destination, Func<int, int, double> read) = scan.ChannelName switch
                {
                    "Y1" => (y1, (Func<int, int, double>)reader.ReadPeakPicked),
                    "Y2" => (y2, reader.ReadPeakPicked),
                    "RY" => (rMinusY, reader.ReadBare),
                    "BY" => (bMinusY, reader.ReadBare),
                    _ => throw new NotSupportedException($"Unknown channel '{scan.ChannelName}'."),
                };

                // Trimmed to m_KSS/m_KS2S, not the raw scan duration -- legacy's real x-mapping is
                // `x = ps * Width / m_KSS` (`m_KS2S` for RY/BY), never `x = ps * Width / m_KS`.
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth
                    * SstvModeRegistry.GetPixelPitchTrimFactor(mode, scan.ChannelName);
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    idealSamplesSoFar += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);

                    var freq = read(startSample, endSample);
                    destination[x] = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);
                }
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
            }
        }

        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var (r1, g1, b1) = YCbCr.ToRgb(y1[x], rMinusY[x], bMinusY[x]);
            pixels[lineIndex * mode.ImageWidth + x] = new Rgb24(r1, g1, b1);

            var (r2, g2, b2) = YCbCr.ToRgb(y2[x], rMinusY[x], bMinusY[x]);
            pixels[(lineIndex + 1) * mode.ImageWidth + x] = new Rgb24(r2, g2, b2);
        }
    }
}
