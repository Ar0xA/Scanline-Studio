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
        PixelSampleReader reader,
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
                // Piece 10: peak-vs-bare folded into the SAME exhaustive channel switch that already
                // picks the destination array, not a second parallel switch that could drift out of
                // sync with this one. "Y" peak-picks (Main.cpp:4330, GetPictureLevel); "RY"/"BY" stay
                // bare (Main.cpp:4338/4347, GetPixelLevel) -- legacy never peak-picks chroma here.
                (double[] destination, Func<int, int, double> read) = scan.ChannelName switch
                {
                    "Y" => (y, (Func<int, int, double>)reader.ReadPeakPicked),
                    "RY" => (rMinusY, reader.ReadBare),
                    "BY" => (bMinusY, reader.ReadBare),
                    _ => throw new NotSupportedException($"Unknown channel '{scan.ChannelName}'."),
                };

                // Trimmed to m_KSS/m_KS2S, not the raw scan duration -- legacy's real x-mapping is
                // `x = ps * Width / m_KSS` (`m_KS2S` for RY/BY), never `x = ps * Width / m_KS`.
                //
                // Milestone-audit MUST fix (spec/14-roadmap.md, "Milestone audit, Phase 1+2", finding
                // 1; see RgbSequentialScanlineDecoder.cs's own copy of this fix for the full
                // derivation): the segment's own START position must be tracked separately from the
                // trimmed intra-segment pixel walk, and idealSamplesSoFar advanced by the segment's
                // FULL untrimmed duration afterward -- not accumulated from the trimmed per-pixel
                // steps, which used to start every segment after the first early.
                var segmentStartSample = idealSamplesSoFar;
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth
                    * SstvModeRegistry.GetPixelPitchTrimFactor(mode, scan.ChannelName);
                var pixelWalk = 0.0;
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    var startSample = lineStartSample + (int)Math.Round(segmentStartSample + pixelWalk);
                    pixelWalk += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(segmentStartSample + pixelWalk);

                    var freq = read(startSample, endSample);
                    destination[x] = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);
                }

                idealSamplesSoFar = segmentStartSample + scan.DurationMs / 1000.0 * sampleRate;
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
