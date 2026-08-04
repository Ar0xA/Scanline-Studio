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

    // SHOULD item 12 (spec/14-roadmap.md): legacy's tone-selector decode branch is NOT gated by the
    // segment's own full nominal span -- round-1-review correction, an earlier version of this fix
    // wrongly assumed no narrower legacy window existed and applied an invented margin instead.
    // Confirmed directly against source: `sstv.cpp:664-665` (SetSampFreq, case smR36) sets
    // `m_SG = (88.0+1.25)*SampFreq/1000` and `m_CG = (88.0+3.5)*SampFreq/1000` -- both measured from
    // the START OF THE Y SCAN (ps=88.0ms is exactly where the tone-selector tone begins, matching
    // this port's own segment boundary). `Main.cpp:4286-4297`'s RX switch enters the TCS branch for
    // `ps < m_CG`, but its own body only executes (`ps -= m_SG; if (ps >= 0)`) once ps has reached
    // m_SG -- so legacy's real per-sample m_DSEL re-decision only ever happens for
    // ps in [m_SG, m_CG) = tone-relative [1.25ms, 3.5ms), and FREEZES at whatever it was on the last
    // sample before ps reaches m_CG once the next branch (`ps < m_CB`, chroma) takes over. m_CG sits
    // 1.0ms before the tone segment's own real end (4.5ms - 3.5ms) -- this port's real fidelity gap
    // was reading a full 1.0ms (~11 samples at 11025Hz) later than legacy's own last real decision,
    // squarely inside the following 1900Hz porch's own settling/contamination zone (1900Hz being
    // exactly the ambiguity midpoint) -- not a generic "boundary safety margin," a genuine missing
    // legacy constant.
    //
    // Round-2-review note: `sstv.cpp:665` computes m_CG from the bare (nominal/ini) `SampFreq`, not
    // the member `m_SampFreq` every neighboring line in that same switch uses (the one slant/AFC
    // rewrite) -- looks like a legacy typo, not reproduced here deliberately (this port always uses
    // its own already-slant-corrected `sampleRate` parameter). Flagged so a future reader doesn't
    // "fix" this port to match the typo; no measurable behavioral impact either way (the window is
    // 2.25ms wide, slant correction is well under 1%).
    private const double DecisiveWindowTailMarginMs = 1.0;

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
                    // SHOULD item 11 (spec/14-roadmap.md): luma also gets legacy's own Limit256 clamp
                    // here (Main.cpp:4282, `d = Limit256(d)` right after the same `d += 128`-equivalent
                    // domain this port's own formula already produces) -- chroma does NOT (see the
                    // chroma call below's own comment).
                    DecodePixels(scan, mode, sampleRate, lineStartSample, reader.ReadPeakPicked, ref idealSamplesSoFar, y, clamp: true);
                    scanSegmentsSeen++;
                    break;

                case ToneSelectorSegment selector:
                {
                    idealSamplesSoFar += selector.DurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);
                    // Legacy re-decides m_DSEL on every single sample of this segment with no
                    // "first wins" gate (Main.cpp:4286-4297, unlike the per-pixel scans' m_AX-gated
                    // "first sample" behavior) -- so the real effective reading is whatever the
                    // segment's LAST sample decided, not an average and not the first sample.
                    // Tone-selector always reads bare -- matches legacy's own GetPixelLevel here
                    // (Main.cpp:4289), never GetPictureLevel. Unaffected by piece 10.
                    //
                    // SHOULD item 12 (spec/14-roadmap.md, milestone audit): read near legacy's own real
                    // last-decided sample (m_CG's boundary), not this segment's own full nominal end --
                    // see DecisiveWindowTailMarginMs's own doc comment for the full derivation. The
                    // extra `-1` (beyond DecisiveWindowTailMarginMs's own rounding) is deliberate, not
                    // an off-by-one: round-2-review confirmed this reads ~0.1 sample earlier than
                    // legacy's exact last decisive sample (still strictly inside legacy's own
                    // [1.25ms, 3.5ms) decisive window either way), trading a hair of precision for
                    // robustness against `Math.Round`'s own worst-case rounding landing past m_CG.
                    // No lower clamp against the segment's own start: m_CG sits at tone-relative 3.5ms,
                    // comfortably inside the segment's own [0, 4.5ms) span at every sample rate this
                    // port supports (reaching the segment start would need DecisiveWindowTailMarginMs
                    // within ~1 sample of the full segment duration, which it structurally never is).
                    var decisiveWindowEndSample = endSample - 1 - (int)Math.Round(DecisiveWindowTailMarginMs / 1000.0 * sampleRate);
                    var freq = reader.ReadBare(decisiveWindowEndSample, decisiveWindowEndSample + 1);
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
                    // SHOULD item 11 (spec/14-roadmap.md): confirmed directly against source -- legacy
                    // stores this raw (`m_D36[m_DSEL][x] = short(d);`, Main.cpp:4304) with NO Limit256
                    // call, unlike luma above. Not a port gap to fix, a real legacy asymmetry to
                    // preserve: clamp: false.
                    var target = isEvenLine ? _rMinusY : _bMinusY;
                    DecodePixels(chromaScan, mode, sampleRate, lineStartSample, reader.ReadBare, ref idealSamplesSoFar, target!, clamp: false);
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
        double[] destination,
        bool clamp)
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
            var value = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);
            destination[x] = clamp ? Math.Clamp(value, 0, 255) : value;
        }

        idealSamplesSoFar = segmentStartSample + scan.DurationMs / 1000.0 * sampleRate;
    }
}
