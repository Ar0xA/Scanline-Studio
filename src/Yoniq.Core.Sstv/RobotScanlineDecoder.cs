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
    // Legacy's ambiguity fallback for the tone-selector reading, Main.cpp:4289-4296:
    //   d = GetPixelLevel(ip);
    //   if( (d >= 64) || (d < -64) )  m_DSEL = (d >= 0) ? 1 : 0;   // decisive
    //   else                          m_DSEL = m_DSEL ? 0 : 1;     // ambiguous -> toggle
    // GetPixelLevel's raw output is zero-centered on the 1900Hz free-running frequency, scaled by
    // m_DemWhite=m_DemBlack=128/16384 (Main.cpp:876-877) -- i.e. GetPixelLevel(freq) =
    // (freq-1900)*128/400 (400 = half the tone-selector's 1500-2300Hz span, matching the +/-128
    // domain GetRY/ColorToFreq use elsewhere -- see YCbCr's doc comment). Solving d=64 for freq
    // gives the 200Hz half-width below: this exact number isn't a literal Hz constant anywhere in
    // legacy source, it's derived from the 128/16384 scale factor that is, so it's flagged as
    // derived rather than presented as a directly-read value.
    private const double AmbiguityHalfWidthHz = 200.0;

    private double[]? _rMinusY;
    private double[]? _bMinusY;

    // Legacy m_DSEL defaults to 0 (Main.cpp:712), i.e. R-Y selected -- matches this port's existing
    // "even line -> R-Y" convention (isEvenLine: true).
    private bool _lastSelectionIsEvenLine = true;

    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        PixelSampleReader reader,
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
                    // Luma peak-picks in legacy (Main.cpp:4280, GetPictureLevel) -- unlike the
                    // tone-selector and chroma sites below, which stay bare (GetPixelLevel).
                    DecodePixels(scan, mode, sampleRate, lineStartSample, reader.ReadPeakPicked, ref idealSamplesSoFar, y);
                    scanSegmentsSeen++;
                    break;

                case ToneSelectorSegment selector:
                {
                    var startSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    idealSamplesSoFar += selector.DurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    // Legacy re-decides m_DSEL on every single sample of this segment with no
                    // "first wins" gate (Main.cpp:4286-4297, unlike the per-pixel scans' m_AX-gated
                    // "first sample" behavior) -- so the real effective reading is whatever the
                    // segment's LAST sample decided, not an average and not the first sample.
                    // Tone-selector always reads bare -- matches legacy's own GetPixelLevel here
                    // (Main.cpp:4289), never GetPictureLevel. Unaffected by piece 10.
                    var freq = reader.ReadBare(endSample - 1, endSample);
                    var midpoint = (selector.LowFrequencyHz + selector.HighFrequencyHz) / 2;
                    var deviation = freq - midpoint;
                    isEvenLine = deviation >= AmbiguityHalfWidthHz || deviation < -AmbiguityHalfWidthHz
                        ? deviation < 0 // decisive: closer to LowFrequencyHz (R-Y) => even line
                        : !_lastSelectionIsEvenLine; // ambiguous: toggle, Main.cpp:4294
                    _lastSelectionIsEvenLine = isEvenLine;
                    break;
                }

                case ScanSegment chromaScan:
                {
                    // Chroma always reads bare -- matches legacy's own GetPixelLevel here
                    // (Main.cpp:4303), never GetPictureLevel. Unaffected by piece 10.
                    var target = isEvenLine ? _rMinusY : _bMinusY;
                    DecodePixels(chromaScan, mode, sampleRate, lineStartSample, reader.ReadBare, ref idealSamplesSoFar, target!);
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

    // Piece 10: takes a bound method GROUP (reader.ReadBare or reader.ReadPeakPicked), decided once
    // by the caller for this whole scan segment -- not a PixelSampleReader plus a `bool usePeak`
    // flag, which would reintroduce the exact same-typed-parameter ambiguity ReadBare/ReadPeakPicked
    // were split out to eliminate (round-2 plan review finding).
    private static void DecodePixels(
        ScanSegment scan,
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        Func<int, int, double> read,
        ref double idealSamplesSoFar,
        double[] destination)
    {
        // Trimmed to m_KSS/m_KS2S, not the raw scan duration -- legacy's real x-mapping is
        // `x = ps * Width / m_KSS` for luma, `x = ps * Width / m_KS2S` for the tone-selected chroma
        // channel ("C" -- Main.cpp:4300, inside smR36's chroma branch), never `x = ps * Width / m_KS`.
        //
        // Milestone-audit MUST fix (spec/14-roadmap.md, "Milestone audit, Phase 1+2", finding 1; see
        // RgbSequentialScanlineDecoder.cs's own copy of this fix for the full derivation): the
        // segment's own START position must be tracked separately from the trimmed intra-segment
        // pixel walk, and idealSamplesSoFar advanced by the segment's FULL untrimmed duration
        // afterward (via the ref parameter) -- not accumulated from the trimmed per-pixel steps,
        // which used to start Robot 36's chroma segment early relative to its own real, untrimmed
        // boundary.
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
            // Inverse of ColorToFreq, not "+1500" -- uses the mode's own LuminanceMinHz/MaxHz.
            destination[x] = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);
        }

        idealSamplesSoFar = segmentStartSample + scan.DurationMs / 1000.0 * sampleRate;
    }
}
