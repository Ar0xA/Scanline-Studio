using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>RM8/RM12 decode counterpart to <see cref="MonoAveragedPairedScanlineEncoder"/>. Legacy's
/// real RX for this family (<c>Main.cpp:4431-4453</c>'s <c>smRM8</c>/<c>smRM12</c> decode branch)
/// writes the calibrated pixel level directly into R/G/B with no <c>YCtoRGB</c> matrix involved at
/// all (there's no chroma to combine it with) -- this decoder now ports that directly (gray computed
/// once, written straight into R=G=B), not routed through <see cref="YCbCr.ToRgb"/> the way every
/// other Y-bearing family here is. An earlier version of this port DID route RM8/RM12 through
/// <see cref="YCbCr.ToRgb"/> with neutral (128,128) chroma, reasoned as "the same path every other
/// Y-bearing family already uses, rather than inventing a third, RM-specific reconstruction
/// convention" -- that reasoning was wrong: legacy already HAS a second, genuinely different
/// reconstruction convention here (direct gray write, no matrix at all), so porting it isn't
/// inventing a third one, it's porting the second one that already exists. Confirmed by 2 rounds of
/// auditor plan review (see PROJECT_BRIEF.md's "Piece 12" entry) before this rewrite.</summary>
internal sealed class MonoAveragedPairedScanlineDecoder : IScanlineDecoder
{
    // RM8/RM12-specific gain (Main.cpp:4438: `d *= (256.0/(256.0-32.0))`, applied to the zero-centered
    // picture level BEFORE the +128 re-bias -- no other mode/family has this). Legacy's picture-level
    // domain (GetPixelLevel(freq)+128, derived and independently re-verified twice via 2 different
    // demodulator paths during this piece's plan review) is algebraically identical to this port's own
    // `(freq-LuminanceMinHz)*256/(LuminanceMaxHz-LuminanceMinHz)` for any mode on the standard
    // 1500-2300Hz band (which RM8/RM12 both use, unmodified defaults) -- so this multiplies the
    // zero-centered form of that SAME value, not a separately-derived one.
    // internal, not private: S21 (spec/14-roadmap.md) references this same constant directly from
    // MonoAveragedPairedScanlineDecoderTests' int-truncation divergence test, so the two-truncation
    // legacy-chain replication there stays pinned to the SAME gain value this class actually uses
    // rather than a separately-drifting literal copy.
    internal const double RmGainFactor = 256.0 / (256.0 - 32.0);

    public int RowsPerTransmissionLine => 2;

    public void DecodeLine(
        SstvModeDefinition mode,
        int sampleRate,
        int lineStartSample,
        int lineIndex,
        PixelSampleReader reader,
        Rgb24[] pixels)
    {
        var y = new byte[mode.ImageWidth];
        var idealSamplesSoFar = 0.0;

        foreach (var segment in mode.LineSegments)
        {
            if (segment is ScanSegment scan)
            {
                // Trimmed to m_KSS, not the raw scan duration -- legacy's real x-mapping is
                // `x = ps * Width / m_KSS`, never `x = ps * Width / m_KS`. RM8/RM12 has no chroma
                // segment, so this is always the luma (non-chroma) branch of the trim factor.
                //
                // Milestone-audit MUST fix 3 note (spec/14-roadmap.md, "Milestone audit, Phase 1+2"):
                // this decoder was DELIBERATELY left with the old single-accumulator shape (unlike
                // RgbSequentialScanlineDecoder.cs's own copy of that fix) -- RM8/RM12 has exactly one
                // scan segment per line, so there is no SUBSEQUENT segment for a trimmed-accumulation
                // error to shift early (confirmed by measurement, not just this argument: rm8's own
                // golden-vector delta was numerically unchanged, 13.76 before and after). If a
                // trailing segment is ever added to this family, this loop would need the same
                // segmentStartSample-based restructuring the other 4 decoders got, or the bug returns.
                var perPixelDurationMs = scan.DurationMs / mode.ImageWidth
                    * SstvModeRegistry.GetPixelPitchTrimFactor(mode, scan.ChannelName);
                for (var x = 0; x < mode.ImageWidth; x++)
                {
                    // ultracode audit finding #29: ceiling, not round-to-nearest.
                    var startSample = lineStartSample + (int)Math.Ceiling(idealSamplesSoFar);
                    idealSamplesSoFar += perPixelDurationMs / 1000.0 * sampleRate;
                    var endSample = lineStartSample + (int)Math.Round(idealSamplesSoFar);

                    // RM8/RM12's single channel always peak-picks in legacy (Main.cpp:4437,
                    // GetPictureLevel) -- no chroma exception to worry about here, unlike the
                    // YCbCr-paired families.
                    var freq = reader.ReadPeakPicked(startSample, endSample);
                    var rawValue = (freq - mode.LuminanceMinHz) * 256.0 / (mode.LuminanceMaxHz - mode.LuminanceMinHz);

                    // Legacy truncates to int twice here (once inside GetPixelLevel's own `d *=
                    // sys.m_DemWhite/m_DemBlack`, confirmed both default to 128.0/16384.0 --
                    // ComLib.h:242-243, Main.cpp:876-877 -- once more at this branch's own
                    // `d *= gain`, Main.cpp:4438) -- not replicated, matching every other decoder in
                    // this codebase (none reproduce legacy's int-truncation semantics either).
                    // S21 (spec/14-roadmap.md): exact divergence bound MEASURED, not estimated --
                    // MonoAveragedPairedScanlineDecoderTests.IntTruncationDivergence_MatchesLegacysExactTwoTruncationChain_WithinMeasuredBound
                    // exhaustively replicates legacy's real double-truncation chain (over every
                    // achievable int16 input, +-16384, matching this port's own AGC'd-sample domain)
                    // against this exact formula and finds a max divergence of EXACTLY 2 levels
                    // (never more), asymmetric (legacy-minus-port ranges -1..+2, not +-2) -- legacy
                    // truncates-toward-zero on the still-negative pre-bias value below mid-gray (i.e.
                    // rounds UP there), this port's clamp-then-cast rounds DOWN on the already-positive
                    // post-bias value. Well inside the existing 10.0 round-trip / 15.0-25.0
                    // golden-vector tolerances.
                    var corrected = (rawValue - 128.0) * RmGainFactor + 128.0;
                    y[x] = (byte)Math.Clamp(corrected, 0, 255);
                }
            }
            else
            {
                idealSamplesSoFar += segment.DurationMs / 1000.0 * sampleRate;
            }
        }

        for (var x = 0; x < mode.ImageWidth; x++)
        {
            var gray = new Rgb24(y[x], y[x], y[x]);
            pixels[lineIndex * mode.ImageWidth + x] = gray;
            pixels[(lineIndex + 1) * mode.ImageWidth + x] = gray;
        }
    }
}
