using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// RX buffer subsystem Phase 6a -- isolated tests for <see cref="ReplayOriginCalculator"/>, before any
/// decoder/replay-engine wiring exists (Phase 6c/6d). No decode internals involved.
/// </summary>
public class ReplayOriginCalculatorTests
{
    private const double SampleRate = 11025.0;

    // ---- AdjustPosition: per-mode-family table ----

    [Fact]
    public void AdjustPosition_ScottieFamily_WrapsWhenResultIsNegative()
    {
        // base = -(argmax-ofp) = -(100-5) = -95 -- negative, must wrap by adding the corrected line
        // width (Main.cpp:5436): -95 + 1653 = 1558.
        const double correctedTw = 1653.75;
        var wrapped = ReplayOriginCalculator.AdjustPosition(histogramArgmaxBin: 100, ofpSamples: 5.0, correctedLineWidthSamples: correctedTw, mode: SstvModeRegistry.ScottieS2, wStgLineCount: 50, sampleRate: SampleRate);

        Assert.Equal(-95 + (int)correctedTw, wrapped);
        Assert.True(wrapped >= 0);
    }

    [Fact]
    public void AdjustPosition_ScottieFamily_NoWrapWhenNonNegative()
    {
        var n = ReplayOriginCalculator.AdjustPosition(histogramArgmaxBin: 5, ofpSamples: 100.0, correctedLineWidthSamples: 1653.75, mode: SstvModeRegistry.ScottieDx, wStgLineCount: 50, sampleRate: SampleRate);

        Assert.Equal(95, n); // -(5-100) = 95, already non-negative, wrap is a no-op
    }

    [Fact]
    public void AdjustPosition_MartinFamily_Adds045Ms()
    {
        // base = -(10-20) = 10; +0.45ms*11025/1000 = 4.96125 -> truncates to 4 -> 14.
        var expected = 10 + (int)(0.45 / 1000.0 * SampleRate);

        // Two different members of the same legacy case-label group -- catches a mis-grouping, not
        // just a formula bug in one mode.
        var martinM1 = ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.MartinM1, 50, SampleRate);
        var mc110 = ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Mc110, 50, SampleRate);

        Assert.Equal(expected, martinM1);
        Assert.Equal(expected, mc110);
    }

    [Fact]
    public void AdjustPosition_MartinM2Family_UsesShorterOffset_WhenFewerThan20LinesStaged()
    {
        var expected030 = 10 + (int)(0.30 / 1000.0 * SampleRate);

        var martinM2 = ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.MartinM2, wStgLineCount: 19, sampleRate: SampleRate);
        var sc260 = ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Sc260, wStgLineCount: 0, sampleRate: SampleRate);

        Assert.Equal(expected030, martinM2);
        Assert.Equal(expected030, sc260);
    }

    [Fact]
    public void AdjustPosition_MartinM2Family_UsesLongerOffset_WhenAtLeast20LinesStaged()
    {
        var expected040 = 10 + (int)(0.40 / 1000.0 * SampleRate);

        var atExactly20 = ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.MartinM2, wStgLineCount: 20, sampleRate: SampleRate);
        var wellAbove20 = ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Sc260, wStgLineCount: 200, sampleRate: SampleRate);

        Assert.Equal(expected040, atExactly20);
        Assert.Equal(expected040, wellAbove20);
    }

    [Fact]
    public void AdjustPosition_RobotFamily_Adds016Ms()
    {
        var expected = 10 + (int)(0.16 / 1000.0 * SampleRate);

        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Robot36, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Robot72, 50, SampleRate));
    }

    [Fact]
    public void AdjustPosition_MpMnMrMlFamily_Adds02Ms()
    {
        var expected = 10 + (int)(0.2 / 1000.0 * SampleRate);

        // One representative from each legacy case-label group within this family.
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Mp140, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Mn110, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Mr115, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Ml240, 50, SampleRate));
    }

    [Fact]
    public void AdjustPosition_R24AndRmFamily_Adds05Ms()
    {
        var expected = 10 + (int)(0.5 / 1000.0 * SampleRate);

        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.R24, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Rm8, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Rm12, 50, SampleRate));
    }

    [Fact]
    public void AdjustPosition_PdFamilyAndAvt_NoAdjustmentAtAll()
    {
        // Main.cpp:5485-5486's `default: break` -- exhaustively confirmed against the mode enum during
        // this phase's own plan-review to be ONLY AVT and the seven PD modes.
        const int expected = 10; // base = -(10-20) = 10, unchanged

        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Pd50, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Pd240, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Pd290, 50, SampleRate));
        Assert.Equal(expected, ReplayOriginCalculator.AdjustPosition(10, 20.0, 1653.75, SstvModeRegistry.Avt, 50, SampleRate));
    }

    [Fact]
    public void AdjustPosition_TruncationOrder_MatchesLegacysTwoSeparateIntConversions()
    {
        // Round-1 code-review correction: an earlier version of this test used Pd50 (a NO-OFFSET mode),
        // which cannot actually distinguish "combine-then-truncate-once" from "truncate-OFP-first" --
        // both produce the same final int for a mode with no further arithmetic after the base
        // computation. A mode WITH a real per-mode addition is required to expose the hazard: MartinM1
        // (+0.45ms), argmax=31, ofp=20.02, fs=11025.
        //   Correct (this implementation): base = (int)(31 - 20.02) = (int)10.98 = 10, negated = -10;
        //     then (int)(-10 + 0.45/1000*11025) = (int)(-10 + 4.96125) = (int)(-5.03875) = -5.
        //   Wrong (single final truncation of the whole combined expression): (int)(-(31-20.02) +
        //     4.96125) = (int)(-10.98 + 4.96125) = (int)(-6.01875) = -6.
        // These disagree by exactly 1, proving this test can actually distinguish the two.
        var correct = ReplayOriginCalculator.AdjustPosition(histogramArgmaxBin: 31, ofpSamples: 20.02, correctedLineWidthSamples: 1653.75, mode: SstvModeRegistry.MartinM1, wStgLineCount: 50, sampleRate: SampleRate);

        Assert.Equal(-5, correct);
    }

    // ---- ComputeOrigin: histogram fold + argmax + AdjustPosition + Hilbert correction ----

    [Fact]
    public void ComputeOrigin_FindsArgmaxBin_AndAppliesAdjustPosition()
    {
        const double correctedTw = 100.0;
        // Single spike at absolute sample 30, well within the folded extent -- argmax should land at
        // bin 30. 2 staged lines of 100 samples each, folded sample count 200 (both under the 32-line
        // cap, so folded == staged here).
        double Envelope(int n) => n == 30 ? 1000.0 : 0.0;

        var origin = ReplayOriginCalculator.ComputeOrigin(
            Envelope, foldedSampleCount: 200, correctedTw, stagedLineCount: 2, ofpSamples: 20.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.ZeroCrossing, hilbertGroupDelayCorrection: 999, sampleRate: SampleRate);

        // PD50 has no adjustment (default case), no Hilbert correction (not Hilbert demod type):
        // origin = -(argmax - ofp) = -(30-20) = -10.
        Assert.Equal(-10, origin);
    }

    [Fact]
    public void ComputeOrigin_HistogramFold_CappedAtFirst32StagedLines()
    {
        // A stronger spike beyond the 32-line fold cap must NOT win the argmax -- proves the caller-
        // supplied foldedSampleCount (not stagedLineCount) is what actually bounds the fold. 40 lines
        // of 100 samples staged (4000 total), but only the first 32 lines (3200 samples) are folded.
        const double correctedTw = 100.0;
        double Envelope(int n)
        {
            if (n == 30) return 500.0; // within the folded 3200-sample extent
            if (n == 3250) return 5000.0; // past the 3200-sample fold bound (line 33 of 40 staged)
            return 0.0;
        }

        var origin = ReplayOriginCalculator.ComputeOrigin(
            Envelope, foldedSampleCount: 3200, correctedTw, stagedLineCount: 40, ofpSamples: 0.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.ZeroCrossing, hilbertGroupDelayCorrection: 0, sampleRate: SampleRate);

        // If the stronger spike at sample 3250 (past the fold bound) won, its bin would be
        // 3250 % 100 = 50, giving origin = -(50-0) = -50. The capped fold must instead find the
        // weaker in-range spike at bin 30, giving origin = -(30-0) = -30.
        Assert.Equal(-30, origin);
    }

    [Fact]
    public void ComputeOrigin_HistogramBinBounds_LastBinIsNeverSearched()
    {
        // Main.cpp:5519-5526's argmax search is `i < wd`, not `i < wd+2` -- a spike landing exactly at
        // bin `wd` (reachable via fmod when the corrected line width has a fractional part) must be
        // accumulated into but never win the argmax search, reproducing legacy's exact (odd) bound.
        const double correctedTw = 99.5; // wd = (int)99.5 = 99 -- bin "wd" (99) is reachable via fmod
        // n=99: fmod(99, 99.5) = 99.0 -> bin 99 (== wd, must be accumulated but never searched). Give
        // it a huge value and confirm it does NOT win over a much smaller in-bounds spike (bin 10).
        double EnvelopeAtBinWd(int n) => n == 99 ? 99999.0 : (n == 10 ? 1.0 : 0.0);

        var origin = ReplayOriginCalculator.ComputeOrigin(
            EnvelopeAtBinWd, foldedSampleCount: 400, correctedTw, stagedLineCount: 2, ofpSamples: 0.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.ZeroCrossing, hilbertGroupDelayCorrection: 0, sampleRate: SampleRate);

        // If bin `wd` (99) were searched, the huge spike there would win: origin = -(99-0) = -99.
        // Since it must NOT be searched, the tiny in-bounds spike at bin 10 wins instead: origin = -10.
        Assert.Equal(-10, origin);
    }

    [Fact]
    public void ComputeOrigin_HilbertDemodType_AppliesGroupDelayCorrection()
    {
        const double correctedTw = 100.0;
        double Envelope(int n) => n == 30 ? 1000.0 : 0.0;

        var withHilbert = ReplayOriginCalculator.ComputeOrigin(
            Envelope, foldedSampleCount: 200, correctedTw, stagedLineCount: 2, ofpSamples: 20.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.Hilbert, hilbertGroupDelayCorrection: 7, sampleRate: SampleRate);
        var withoutHilbert = ReplayOriginCalculator.ComputeOrigin(
            Envelope, foldedSampleCount: 200, correctedTw, stagedLineCount: 2, ofpSamples: 20.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.ZeroCrossing, hilbertGroupDelayCorrection: 7, sampleRate: SampleRate);

        Assert.Equal(-10, withoutHilbert); // base origin, unaffected by hilbertGroupDelayCorrection
        Assert.Equal(-10 - 7, withHilbert); // direct subtraction, not a sign-flipped addition
    }

    [Fact]
    public void ComputeOrigin_PllOrZeroCrossingDemodType_NeverAppliesHilbertCorrection()
    {
        const double correctedTw = 100.0;
        double Envelope(int n) => n == 30 ? 1000.0 : 0.0;

        var pll = ReplayOriginCalculator.ComputeOrigin(
            Envelope, foldedSampleCount: 200, correctedTw, stagedLineCount: 2, ofpSamples: 20.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.Pll, hilbertGroupDelayCorrection: 500, sampleRate: SampleRate);

        Assert.Equal(-10, pll); // large hilbertGroupDelayCorrection value must be entirely ignored
    }

    [Fact]
    public void ComputeOrigin_CanReturnNegativeOrigin_ForNonScottieModes()
    {
        // Origin-shear resolution: for the "default: no adjustment" family, a negative base result
        // stays negative (no wrap -- that's Scottie-only), matching legacy's own real behavior. Needs
        // argmax > ofp for origin = -(argmax-ofp) to come out negative.
        const double correctedTw = 100.0;
        double Envelope(int n) => n == 50 ? 1000.0 : 0.0;

        var origin = ReplayOriginCalculator.ComputeOrigin(
            Envelope, foldedSampleCount: 200, correctedTw, stagedLineCount: 2, ofpSamples: 5.0,
            mode: SstvModeRegistry.Pd50, demodType: DemodType.ZeroCrossing, hilbertGroupDelayCorrection: 0, sampleRate: SampleRate);

        Assert.Equal(-45, origin); // -(argmax - ofp) = -(50-5) = -45
        Assert.True(origin < 0);
    }

}
