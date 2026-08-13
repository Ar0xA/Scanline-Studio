using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// RX buffer subsystem Phase 6a -- isolated, decoder-independent port of legacy's `AdjustSyncPos`
/// (`Main.cpp:5428-5489`) and `ReSyncSSTV` (`Main.cpp:5491-5537`): given the staged sync-envelope
/// stream, computes the signed REPLAY ORIGIN (legacy's `SSTVSET.m_IOFS`/`m_OFS`/`dp->m_rBase`) that
/// Phase 6c's replay engine anchors its destination-coordinate mapping to. No decoder wiring here --
/// takes plain values/delegates, returns a plain <see cref="int"/>.
///
/// <b>The origin CAN be negative</b> (see the RX buffer plan's own "origin shear" resolution) -- this
/// is legacy-correct, not a bug: `DrawSSTVNormal`'s own destination cursor (`Main.cpp:4146`,
/// `if(n&lt;0) continue`) silently skips any sample that maps to a negative destination, so a negative
/// origin here is a legitimate signal that some of this reception's earliest staged samples logically
/// precede the image's own row 0.
/// </summary>
internal static class ReplayOriginCalculator
{
    /// <summary>Port of `AdjustSyncPos` (`Main.cpp:5428-5489`), all cases, exhaustively re-derived
    /// against the mode enum during this phase's own plan-review (round 1 caught a wrong "Martin =
    /// no adjustment" claim in an earlier draft -- Martin 1/2 both have real adjustments; only AVT and
    /// the seven PD modes hit "no adjustment").
    ///
    /// <b>Truncation order matches legacy's own two SEPARATE int-conversion points exactly</b> (round-3
    /// plan-review finding) -- legacy's `n` stays declared `int` throughout, so `n -= SSTVSET.m_OFP`
    /// truncates ONCE (the double difference `histogramArgmaxBin - ofpSamples`, truncated toward zero
    /// exactly like a C++ double-to-int conversion, which C#'s own `(int)` cast on a `double` also
    /// does), and each subsequent `n += X_ms/1000.0 * SampFreq` mode-specific addition (and the
    /// Scottie-only wrap, `n += int(SSTVSET.m_TW)`) triggers its own SEPARATE, independent truncation
    /// of `n`'s own (already-int) value plus the double offset -- NOT one single truncation of the
    /// whole combined expression. Do not "simplify" this to computing everything in `double` and
    /// truncating once at the end; that produces a different (up to 1 sample off) result whenever
    /// `ofpSamples` has a nonzero fractional part.</summary>
    /// <param name="histogramArgmaxBin">The sync-envelope histogram's own argmax bin index (an
    /// already-integer quantity, matching legacy's own `int n` fold-search result).</param>
    /// <param name="ofpSamples">The UNTRUNCATED, full-double-precision sync-peak-offset-in-samples
    /// value (legacy's `SSTVSET.m_OFP`) -- deliberately NOT this port's existing
    /// `AnalogFmSstvDecoder.ComputeSyncPeakOffsetSamples` (which truncates to `int` before returning,
    /// too early for this method's own truncation-order requirement above). Also deliberately NOT
    /// `_syncSegmentOffsetSamples`/`SstvModeRegistry.GetSyncPeakOffsetMs` used for anything other than
    /// its own literal formula -- <c>GetSyncPeakOffsetMs</c>'s own doc comment explicitly warns its
    /// per-mode table is a DIFFERENT mechanism's values, not this one's (`SstvModeRegistry.cs:948-955`);
    /// this method's own mode-specific offsets below are the real, separate `AdjustSyncPos` table, not
    /// substitutes for or reuses of that one.</param>
    /// <param name="correctedLineWidthSamples">The CURRENT, just-corrected line width (legacy's
    /// `SSTVSET.m_TW`) -- used ONLY by the Scottie-family wrap below, matching `Main.cpp:5436`'s own
    /// `int(SSTVSET.m_TW)`.</param>
    /// <param name="wStgLineCount">Legacy's own `dp->m_wStgLine` -- the TOTAL count of staged lines
    /// (not capped at the histogram fold's own 32-line limit) -- only consumed by the Martin
    /// 2/SC2-60 branch below.</param>
    /// <param name="sampleRate">Legacy's `SSTVSET.m_SampFreq` -- the CORRECTED rate `ReSyncSSTV`'s own
    /// `SetSampFreq()` call just recomputed, matching every per-mode `X_ms/1000.0 * SampFreq` term
    /// below literally (`Main.cpp:5438` etc.). NOT the nominal/declared device rate -- a real,
    /// deliberate distinction from `AnalogFmSstvDecoder.ComputeSyncPeakOffsetSamples`'s own OFP
    /// computation, which uses the nominal `_sampleRate` for a DIFFERENT purpose (the VIS-lock anchor,
    /// computed before any slant correction exists to apply) -- 6b/6c's own caller must pass the
    /// CORRECTED rate here, not reuse whatever nominal rate feeds that other computation.</param>
    public static int AdjustPosition(int histogramArgmaxBin, double ofpSamples, double correctedLineWidthSamples, SstvModeDefinition mode, int wStgLineCount, double sampleRate)
    {
        var n = (int)(histogramArgmaxBin - ofpSamples); // Main.cpp:5430, single truncation point
        n = -n; // Main.cpp:5431, exact negation, no new truncation

        if (mode == SstvModeRegistry.ScottieS1 || mode == SstvModeRegistry.ScottieS2 || mode == SstvModeRegistry.ScottieDx)
        {
            if (n < 0)
            {
                n += (int)correctedLineWidthSamples; // Main.cpp:5436
            }

            return n;
        }

        if (mode == SstvModeRegistry.MartinM1 || mode == SstvModeRegistry.Sc2180 || mode == SstvModeRegistry.Sc2120
            || mode == SstvModeRegistry.P3 || mode == SstvModeRegistry.P5 || mode == SstvModeRegistry.P7
            || mode == SstvModeRegistry.Mc110 || mode == SstvModeRegistry.Mc140 || mode == SstvModeRegistry.Mc180)
        {
            return (int)(n + 0.45 / 1000.0 * sampleRate); // Main.cpp:5438-5448
        }

        if (mode == SstvModeRegistry.MartinM2 || mode == SstvModeRegistry.Sc260)
        {
            var offsetMs = wStgLineCount < 20 ? 0.30 : 0.40; // Main.cpp:5451-5456
            return (int)(n + offsetMs / 1000.0 * sampleRate);
        }

        if (mode == SstvModeRegistry.Robot36 || mode == SstvModeRegistry.Robot72)
        {
            return (int)(n + 0.16 / 1000.0 * sampleRate); // Main.cpp:5458-5461
        }

        if (mode == SstvModeRegistry.Mp73 || mode == SstvModeRegistry.Mp115 || mode == SstvModeRegistry.Mp140 || mode == SstvModeRegistry.Mp175
            || mode == SstvModeRegistry.Mn73 || mode == SstvModeRegistry.Mn110 || mode == SstvModeRegistry.Mn140
            || mode == SstvModeRegistry.Mr73 || mode == SstvModeRegistry.Mr90 || mode == SstvModeRegistry.Mr115 || mode == SstvModeRegistry.Mr140 || mode == SstvModeRegistry.Mr175
            || mode == SstvModeRegistry.Ml180 || mode == SstvModeRegistry.Ml240 || mode == SstvModeRegistry.Ml280 || mode == SstvModeRegistry.Ml320)
        {
            return (int)(n + 0.2 / 1000.0 * sampleRate); // Main.cpp:5462-5479
        }

        if (mode == SstvModeRegistry.R24 || mode == SstvModeRegistry.Rm8 || mode == SstvModeRegistry.Rm12)
        {
            return (int)(n + 0.5 / 1000.0 * sampleRate); // Main.cpp:5480-5484
        }

        // Main.cpp:5485-5486's `default: break` -- AVT and every PD mode (PD50-PD290). AVT itself is
        // never actually reachable here (ApplySlantTracking/replay both exclude it before this could be
        // called), listed only for completeness against legacy's own exhaustive switch.
        return n;
    }

    /// <summary>Port of `ReSyncSSTV` (`Main.cpp:5491-5537`): folds the staged sync-envelope stream
    /// (capped at the first 32 staged lines, `Main.cpp:5504`, load-bearing for Phase 8's own later
    /// reuse against a much larger buffer even though unreachable at this phase's own early trigger)
    /// into a histogram over one CORRECTED line width, argmaxes it, applies
    /// <see cref="AdjustPosition"/>, and applies the Hilbert-only group-delay correction
    /// (`Main.cpp:5528`, `if(dp->m_Type==2) n -= m_hill.m_htap/4` -- deliberately NOT folded into
    /// <see cref="AdjustPosition"/> itself, since `AdjustSyncPos`'s OTHER call site,
    /// `RedrawAdjustSync`, `Main.cpp:5705`, does not apply this correction and Phase 6 deliberately
    /// does not port that second call site -- see the RX buffer plan's own "explicitly OUT OF SCOPE"
    /// note for why).</summary>
    /// <param name="stagedSyncEnvelope">Reads exactly <paramref name="foldedSampleCount"/> samples
    /// starting at index 0 -- the caller's own <see cref="RxLineStagingBuffer.SyncEnvelopeAt"/> is the
    /// real production source.</param>
    /// <param name="foldedSampleCount">The EXACT sample count spanned by the first
    /// <c>min(stagedLineCount, 32)</c> staged lines (`Main.cpp:5504`'s own `i &lt; 32` cap) -- the
    /// caller's own <see cref="RxLineStagingBuffer.SampleCountThroughLine"/> is the real production
    /// source. Round-1 code-review correction: deliberately NOT derived here from a per-line stride
    /// (e.g. `min(stagedLineCount,32) * someLineWidth`) -- unlike legacy's own `m_WD` (fixed for a
    /// whole reception), this port's own captured per-line sample count VARIES (fractional-carry
    /// rounding, Auto-Slant commits mid-reception), so only the caller, which has each line's own real
    /// boundary, can supply this correctly.</param>
    /// <param name="correctedLineWidthSamples">The CURRENT, just-corrected line width (legacy's
    /// `SSTVSET.m_TW` immediately after `SetSampFreq()`) -- this IS the histogram's own modulus, and
    /// the uniform stride Phase 6c's replay engine applies to every replayed row.</param>
    /// <param name="stagedLineCount">Legacy's own `dp->m_wStgLine` exactly -- the TOTAL count of staged
    /// lines (not capped at the histogram fold's own 32-line limit) -- the caller's own
    /// <see cref="RxLineStagingBuffer.LineCount"/> is the real production source. Only consumed by
    /// <see cref="AdjustPosition"/>'s Martin-2/SC2-60 branch; the histogram fold itself uses
    /// <paramref name="foldedSampleCount"/> directly, not this value.</param>
    /// <param name="hilbertGroupDelayCorrection">Legacy's `m_hill.m_htap/4` -- ALREADY divided by 4 by
    /// the caller (this method applies it as-is, via a direct, unflipped `origin -= ...`, matching
    /// `ReSyncSSTV`'s own literal `n -= m_hill.m_htap/4` exactly, since `origin` plays the same
    /// direct-assignment role as legacy's own `n` here -- NOT the same role as
    /// `SyncAnchorCorrector.ComputeAnchorCorrection`'s own `delta`, which is an ADDITIVE correction to
    /// a pre-existing sample cursor and therefore uses the opposite sign by that call site's own
    /// established derivation; do not copy that sign flip here, it doesn't apply to this quantity).
    /// Pass `_demodulator.HalfTap / 4` from a `HilbertFmDemodulator` instance -- confirmed
    /// `HilbertFmDemodulator.HalfTap` (`internal int HalfTap => _htap;`) is a direct alias for legacy's
    /// own `m_htap`, matching the existing `SyncAnchorCorrector` call site's own established
    /// `_demodulator.HalfTap / 4` usage for this exact legacy quantity. Only applied when
    /// <paramref name="demodType"/> is <see cref="DemodType.Hilbert"/>.</param>
    public static int ComputeOrigin(
        Func<int, double> stagedSyncEnvelope,
        int foldedSampleCount,
        double correctedLineWidthSamples,
        int stagedLineCount,
        double ofpSamples,
        SstvModeDefinition mode,
        DemodType demodType,
        int hilbertGroupDelayCorrection,
        double sampleRate)
    {
        var wd = (int)correctedLineWidthSamples; // Main.cpp:5499's `int wd = int(SSTVSET.m_TW)`
        var bins = new double[wd + 2]; // Main.cpp:5500's `new int[wd+2]` -- see the bin-bounds note below
        for (var n = 0; n < foldedSampleCount; n++)
        {
            var x = (int)(n % correctedLineWidthSamples); // Main.cpp:5514's `fmod(n, SSTVSET.m_TW)`
            bins[x] += stagedSyncEnvelope(n);
        }

        // Main.cpp:5519-5526: argmax search is bounded `i < wd`, NOT `i < wd+2` -- bin index `wd`
        // itself (reachable by the fold above, since `fmod` can return exactly `wd` when `m_TW` has a
        // fractional part) is accumulated into but deliberately never searched. Reproduce this exact
        // bound, not a "corrected" wd+1 or wd+2 search.
        var argmaxBin = 0;
        var max = 0.0;
        for (var i = 0; i < wd; i++)
        {
            if (max < bins[i])
            {
                max = bins[i];
                argmaxBin = i;
            }
        }

        var origin = AdjustPosition(argmaxBin, ofpSamples, correctedLineWidthSamples, mode, stagedLineCount, sampleRate);

        if (demodType == DemodType.Hilbert)
        {
            origin -= hilbertGroupDelayCorrection; // Main.cpp:5528, ReSyncSSTV's own caller-side term, not AdjustSyncPos's
        }

        return origin;
    }
}
