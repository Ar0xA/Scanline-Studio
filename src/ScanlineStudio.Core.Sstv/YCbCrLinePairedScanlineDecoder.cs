using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

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
                //
                // SHOULD item 11 (spec/14-roadmap.md): clamp does NOT follow the same "both luma
                // segments alike" pattern peak-picking does -- confirmed directly against source, a
                // real, easy-to-miss legacy asymmetry (not a port gap to close uniformly). Y1 gets
                // Limit256 (Main.cpp:4387, `d = Limit256(d)` right after Y1's own read); Y2 does NOT
                // (Main.cpp:4420-4422: `d = GetPictureLevel(ip); d += 128;` feeds straight into
                // YCtoRGB with no Limit256 call at all in between) -- genuinely no clamp on Y2 in
                // legacy, not merely an omission this port should "correct." RY/BY also unclamped
                // (Main.cpp:4396-4397/4405-4406, same as YCbCrSequentialScanlineDecoder's own chroma).
                (double[] destination, Func<int, int, double> read, bool clamp) = scan.ChannelName switch
                {
                    "Y1" => (y1, (Func<int, int, double>)reader.ReadPeakPicked, true),
                    "Y2" => (y2, reader.ReadPeakPicked, false),
                    "RY" => (rMinusY, reader.ReadBare, false),
                    "BY" => (bMinusY, reader.ReadBare, false),
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
                // steps, which used to start every segment after the first early. PD90's own Y2
                // (fourth segment) measured up to ~4px of drift from this before the fix -- the worst
                // case among the 4 affected decoders, since it compounds across the most segments.
                var segmentStartSample = idealSamplesSoFar;
                // ultracode audit finding #32: legacy's PD/MP/MN chroma segments (R-Y/B-Y) use `m_KSS`
                // -- the LUMA trim factor -- not `m_KS2S` (Main.cpp:4393/4402), unlike
                // YCbCrSequentialScanlineDecoder's chroma (which genuinely does use m_KS2S/Ks2sTrimFactor
                // for its own MR/ML family). GetPixelPitchTrimFactor(mode, scan.ChannelName) would
                // route this decoder's chroma through Ks2sTrimFactor instead -- wrong family's rule.
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth
                    * SstvModeRegistry.GetPeakPickParameters(mode).KssTrimFactor;
                var pixelWalk = 0.0;
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    // ultracode audit finding #29: ceiling, not round-to-nearest.
                    var startSample = lineStartSample + (int)Math.Ceiling(segmentStartSample + pixelWalk);
                    pixelWalk += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(segmentStartSample + pixelWalk);

                    var freq = read(startSample, endSample);
                    var value = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);

                    // ultracode audit finding #28: truncate in the RAW zero-centered domain, before
                    // the +128 bias -- see RobotScanlineDecoder.cs's DecodePixels for the full rationale.
                    value = Math.Truncate(value - 128.0) + 128.0;
                    destination[x] = clamp ? Math.Clamp(value, 0, 255) : value;
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
            var (r1, g1, b1) = YCbCr.ToRgb(y1[x], rMinusY[x], bMinusY[x]);
            pixels[lineIndex * mode.ImageWidth + x] = new Rgb24(r1, g1, b1);

            var (r2, g2, b2) = YCbCr.ToRgb(y2[x], rMinusY[x], bMinusY[x]);
            pixels[(lineIndex + 1) * mode.ImageWidth + x] = new Rgb24(r2, g2, b2);
        }
    }
}
