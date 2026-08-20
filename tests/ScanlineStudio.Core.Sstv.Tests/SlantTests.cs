using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted, staged tests for the Auto Slant port (<see cref="TankFilter"/>,
/// <see cref="SyncEnvelopeDetector"/>, <see cref="SlantTracker"/>, porting legacy's <c>CIIRTANK</c>,
/// the <c>d12</c>/<c>d19</c> envelope detector, and <c>AutoStopJob</c>'s slant-specific branch
/// respectively). Each class is checked independently before any of them are wired into
/// <see cref="AnalogFmSstvDecoder"/>, so a mistake in one layer doesn't hide inside a full-pipeline
/// pass/fail the way it would in only an end-to-end test.
/// </summary>
public class SlantTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void TankFilter_ResonatesMuchMoreAtTunedFrequencyThanFarAway()
    {
        var onFreq = SteadyStateAmplitude(1200, 1200);
        var offFreq = SteadyStateAmplitude(1200, 1900);

        // A resonant bandpass at 100Hz bandwidth should pass a tone 700Hz away with only a small
        // fraction of the gain it gives a tone dead-center.
        Assert.True(onFreq > offFreq * 5, $"on-freq={onFreq}, off-freq={offFreq}");
    }

    [Fact]
    public void SyncEnvelopeDetector_TunedTone_ProducesStrongerEnvelopeThanOffTuneTone()
    {
        var onFreq = SteadyStateEnvelope(1200, 1200);
        var offFreq = SteadyStateEnvelope(1200, 1900);

        Assert.True(onFreq > offFreq * 3, $"on-freq={onFreq}, off-freq={offFreq}");
    }

    [Fact]
    public void SlantTracker_NoDrift_NeverReportsACorrection()
    {
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        double? lastResult = null;
        for (var line = 0; line < 200; line++)
        {
            lastResult = tracker.ProcessLine(0, hasStagingBuffer: true) ?? lastResult;
        }

        Assert.Null(lastResult);
    }

    [Fact]
    public void SlantTracker_ProcessLineHistoryOnly_RecordsHistoryButNeverEstablishesABaseline()
    {
        // Manual ReSync's port of legacy's m_AutoSyncCount gate (Main.cpp:3968): AutoStopJob's
        // unconditional history/counter bookkeeping keeps running while it's set, but the whole
        // fit/baseline/correction branch never executes -- confirmed here directly against
        // SlantTracker, independent of the decoder, so this pins the class's own real behavior rather
        // than inferring it from a decoder-level symptom.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        // Distinctive, increasing, non-zero values -- a plain all-default double[] would trivially
        // satisfy an "equals 0.0" assertion without recording anything, so this specifically checks
        // that these EXACT fed values (not just "something non-default") landed in history in order.
        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLineHistoryOnly(line + 1);
        }

        // (a) Baseline capture is part of the gated fit/correction branch -- must never fire, even
        // though 10 lines is comfortably past FitPoints (5), where a normal ProcessLine call would
        // have established one.
        Assert.False(tracker.HasBaselineForTests);

        // (b) But it's not a no-op either -- history really was recorded, not inferred from (a) (which
        // would stay false either way and so can't distinguish "recorded but disabled" from "nothing
        // happened at all").
        Assert.Equal(10, tracker.TotalLinesObservedForTests);
        var history = tracker.HistoryForTests;
        Assert.Equal(Enumerable.Range(1, 10), history[^10..]);
    }

    [Fact]
    public void SlantTracker_ConsistentDrift_LocksACorrectionCloseToTheTrueRate()
    {
        // Simulate a receiver whose true samples-per-line is 1% higher than assumed. This is a
        // CLOSED-LOOP simulation, not a fixed linear drift sequence: each line, the position fed to
        // ProcessLine is re-derived from (true cumulative samples so far) minus (this test's own
        // running total using whatever samples-per-line the tracker's *last* correction implied) --
        // mirroring exactly what AnalogFmSstvDecoder.ApplySlantTracking does with its mutable
        // _effectiveSamplesPerLine. A first version of this test fed a fixed per-line increment
        // instead, decoupled from the tracker's own corrections -- that was a fair model of the
        // pre-fix implementation (which never updated its internal nominal value either) but stopped
        // matching reality once SlantTracker was fixed to keep its internal rate/nominal pair in
        // sync the way legacy's SetSampFreq() does (see SlantTracker's own doc comment on that bug).
        //
        // SlantTracker's output is a corrected *sample rate* (matching legacy's SSTVSET.m_SampFreq),
        // not a corrected samples-per-line value (Main.cpp:3994-3997).
        const double nominalSamplesPerLine = SampleRate * 0.15; // 6615 samples/line at 44100Hz
        const double trueSamplesPerLine = nominalSamplesPerLine * 1.01;
        var expectedCorrectedRate = SampleRate * (trueSamplesPerLine / nominalSamplesPerLine);

        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        double? lastResult = null;
        var trueCumulative = 0.0;
        var assumedCumulative = 0.0;
        var assumedSamplesPerLine = nominalSamplesPerLine;
        for (var line = 0; line < 300; line++)
        {
            trueCumulative += trueSamplesPerLine;
            assumedCumulative += assumedSamplesPerLine;

            var result = tracker.ProcessLine((int)(trueCumulative - assumedCumulative), hasStagingBuffer: true);
            if (result is not null)
            {
                lastResult = result;
                assumedSamplesPerLine = nominalSamplesPerLine / SampleRate * result.Value;
            }
        }

        Assert.NotNull(lastResult);
        Assert.Equal(expectedCorrectedRate, lastResult!.Value, tolerance: expectedCorrectedRate * 0.002);
    }

    [Fact]
    public void SlantTracker_AdoptCorrectedRate_FirstNaturalCorrectionUsesTheAdoptedBaseline()
    {
        // RX buffer subsystem Phase 8c -- unit-level replacement for a real-audio integration test an
        // earlier version of this batch tried and auditor code-review (round 2) found was actually
        // vacuous. A FIRST version of this replacement itself turned out to be vacuous too, caught by
        // hand-verification (stubbing AdoptCorrectedRate to a complete no-op and confirming the test
        // still passed) before it was ever sent for review: it reused the sibling test's own
        // CLOSED-LOOP technique (feeding the tracker's own reported output back into the next line's
        // assumed rate), which self-corrects toward the true external steady-state drift REGARDLESS of
        // the tracker's own internal starting state -- exactly like a PI controller's integrator: a
        // wrong initial value changes the transient path, never the point it converges to. A
        // closed-loop simulation therefore cannot discriminate ANYTHING AdoptCorrectedRate does or
        // doesn't update, no matter how many corrections it runs through.
        //
        // This version is deliberately OPEN-LOOP: a fixed per-line drift, never adjusted by the
        // tracker's own output, so the bias from whatever AdoptCorrectedRate did or didn't set has no
        // feedback path to erase itself. It checks the FIRST natural correction directly against a
        // value hand-derived from TryComputeCorrection's own formula (SlantTracker.cs), independently
        // computing the adopted nominal-samples-per-line from first principles rather than reading it
        // back from the tracker (a bug in AdoptCorrectedRate's own _nominalSamplesPerLine write would
        // make this expected value diverge from what a broken implementation actually returns). Also
        // hand-verified the other direction: reverting AdoptCorrectedRate's own two field writes (one
        // at a time) both made THIS test fail.
        const double nominalSamplesPerLine = SampleRate * 0.15; // 6615 samples/line at 44100Hz
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        const double adoptedRate = SampleRate * 1.02; // a rate the tracker's own convergence never proposed
        tracker.AdoptCorrectedRate(adoptedRate, hasStagingBuffer: true);

        var adoptedNominalSamplesPerLine = nominalSamplesPerLine / SampleRate * adoptedRate;

        // A perfectly linear open-loop drift: GetSqerrPos's least-squares fit reproduces a perfectly
        // linear sequence exactly, so the fitted position at any point is just perLineDrift*lineNumber
        // -- no fit-noise to account for. 20 samples/line stays comfortably under the jitter gate's
        // own limit (8*mult=160 at this nominal width) while still clearing the correction ladder's
        // lowest threshold (_limitsHz[0]=25*rate/11025=100Hz at 44100Hz) on the very first opportunity.
        const double perLineDrift = 20.0;
        double? firstResult = null;
        for (var line = 1; line <= 8 && firstResult is null; line++)
        {
            firstResult = tracker.ProcessLine((int)(perLineDrift * line), hasStagingBuffer: true);
        }

        Assert.NotNull(firstResult);

        // Hand-traced against ProcessLineCore/TryComputeCorrection's own logic: baseline captures at
        // _totalLinesObserved==5 (fittedPosition = perLineDrift*5, the most-recent value in a perfect
        // linear fit); _linesSinceBaseline first reaches its own >=3 gate on the very next fit after
        // that, at _totalLinesObserved==8 (perLineDrift*8), giving linesSinceBaseline==3 at the moment
        // of use.
        const double baselineLine = 5;
        const double correctionLine = 8;
        const int linesSinceBaseline = 3;
        var baselinePosition = perLineDrift * baselineLine;
        var fittedPosition = perLineDrift * correctionLine;
        var expectedD = (baselinePosition - fittedPosition) * adoptedRate / adoptedNominalSamplesPerLine / linesSinceBaseline;
        var expectedRawCandidate = adoptedRate - expectedD; // MovingAverage.Add's first call (freshly Reset()) returns its own single input unchanged, no averaging dilution
        var expectedCorrectedRate = Math.Floor(expectedRawCandidate * 50.0 + 0.5) / 50.0; // NormalSampleRate(_, 50) -- SlantTracker's own private helper, not clamped (well under the 1100/1060 ceiling)

        Assert.Equal(expectedCorrectedRate, firstResult!.Value, tolerance: 0.001);
    }

    [Theory]
    [InlineData("robot-36", 64, 128, 160, 240 - 36)] // default group, Robot36.ImageHeight=240
    [InlineData("mn73", 48, 64, 72, 110)] // override group A
    [InlineData("pd160", 48, 80, 126, 160)]
    [InlineData("pd290", 64, 128, 160, 240)]
    [InlineData("p3", 64, 200, 360, 448)]
    [InlineData("p5", 64, 200, 300, 380)]
    [InlineData("p7", 64, 128, 220, 280)]
    // ultracode audit finding #8: PD120/180/240 are YCbCrLinePaired (2 image rows per transmission
    // line), so the correct 4th threshold uses legacy's real transmitted line count `m_L` = 248 for
    // all three (Main.cpp:792/812/822), i.e. 248-36=212 -- NOT the doubled ImageHeight (496-36=460,
    // permanently unreachable for a mode that only ever transmits 248 lines).
    [InlineData("pd120", 64, 128, 160, 212)]
    [InlineData("pd180", 64, 128, 160, 212)]
    [InlineData("pd240", 64, 128, 160, 212)]
    public void GetAutoSlantThresholdPositions_MatchesLegacyPerModeGrouping(string modeId, int p0, int p1, int p2, int p3)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        Assert.Equal(new[] { p0, p1, p2, p3 }, SstvModeRegistry.GetAutoSlantThresholdPositions(mode));
    }

    [Fact]
    public void SlantTracker_JitterGate_RejectsOnASpuriousReadingFiveLinesBack_NotJustFourLinesBack()
    {
        // ultracode audit finding #7: legacy's jitter gate checks 5 consecutive deltas (indices
        // 15..10), not 4 (15..11). Feed 4 lines with a large spurious position (well above the
        // mult*8 jitter threshold), then a 5th line whose delta from the 4th is small -- the BUGGY
        // (4-delta) gate never looks back far enough to see the big jump into that spurious run, so
        // it would incorrectly accept and set a baseline at line 5; the FIXED (5-delta) gate does see
        // it (as |history[11]-history[10]|, history[10] still its zero-initialized default) and
        // correctly rejects, matching legacy's real requirement of one more line of confirmation.
        const double nominalSamplesPerLine = SampleRate * 0.15; // mult = (int)(6615/320) = 20, threshold = 8*20 = 160
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        const int spuriousPosition = 1000; // >> 160
        for (var line = 0; line < 4; line++)
        {
            tracker.ProcessLine(spuriousPosition, hasStagingBuffer: true);
        }

        tracker.ProcessLine(spuriousPosition, hasStagingBuffer: true); // 5th line: near-zero delta from the 4th, but a huge one from history[10]'s zero default

        Assert.False(tracker.HasBaselineForTests, "Baseline was set on line 5 -- the jitter gate only checked 4 deltas instead of legacy's 5, missing the spurious jump into history[10]'s zero default.");
    }

    [Fact]
    public void SlantTracker_JitterGate_AcceptsOnceTheSpuriousReadingAgesOutOfTheFiveDeltaWindow()
    {
        const double nominalSamplesPerLine = SampleRate * 0.15;
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        const int spuriousPosition = 1000;
        tracker.ProcessLine(spuriousPosition, hasStagingBuffer: true); // line 1 -- this is the one reading that must age out

        // The 5-delta window (indices 15..10) reaches history[10] via its last delta -- a value fed
        // at line 1 (starting at index 15) shifts one index left per subsequent line, so it only
        // clears index 10 (moves to index 9) once 6 more lines have been fed (lines 2-7, 7 total).
        for (var line = 0; line < 6; line++)
        {
            tracker.ProcessLine(0, hasStagingBuffer: true); // lines 2-7, all consistent with each other
        }

        Assert.True(tracker.HasBaselineForTests, "Baseline still not set by line 7 -- the spurious line-1 reading should have aged out of the 5-delta window by now.");
    }

    [Fact]
    public void SlantTracker_ProcessLineSuppressed_NeverCommitsARateChange_ButBaselineAndBitmaskSurvive()
    {
        // RX buffer subsystem Phase 6b: legacy's own suppressed-replay re-feed (Main.cpp:3989-4017,
        // m_ASDis=1) runs the fit/baseline/average/bitmask logic but never writes SSTVSET.m_SampFreq.
        // Open-loop (never fed back), unlike SlantTracker_ConsistentDrift's closed-loop technique --
        // ProcessLineSuppressed's return value is discarded by design, so there is no corrected rate to
        // feed back even if this test wanted to. Since nothing ever commits, the per-line divergence
        // (trueSamplesPerLine - nominalSamplesPerLine, a small CONSTANT here) stays well under the
        // jitter gate the whole run, so a baseline reliably establishes on schedule (line 5) and is
        // reachable for the correction ladder from line 8 onward.
        const double nominalSamplesPerLine = SampleRate * 0.15;
        const double trueSamplesPerLine = nominalSamplesPerLine * 1.01;
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        var trueCumulative = 0.0;
        var assumedCumulative = 0.0;
        for (var line = 0; line < 300; line++)
        {
            trueCumulative += trueSamplesPerLine;
            assumedCumulative += nominalSamplesPerLine;
            tracker.ProcessLineSuppressed((int)(trueCumulative - assumedCumulative));
        }

        Assert.Equal(300, tracker.TotalLinesObservedForTests);
        Assert.True(tracker.HasBaselineForTests, "Baseline was never established -- ProcessLineSuppressed should still run the fit/baseline logic, just not the final commit.");
        Assert.Equal(0.0, tracker.DriftPpm); // the one thing that must NEVER move: no commit ever writes _currentSampleRate.
        // Round-1 code-review addition: the confidence-tier latch bits (Main.cpp:4006-4010) are the
        // single most legacy-specific behavior `!m_ASDis` withholds nothing from -- they must still
        // latch during a suppressed pass, even though the final rate write never happens. 300 lines of
        // sustained 1% drift (~441Hz-equivalent d, comfortably past every one of the seven
        // _limitsHz thresholds, the tightest of which is ~0.3Hz) and comfortably past every line-count
        // threshold in the ladder (the largest, _thresholdLinePositions[3]=220, is well under 300)
        // means at least one bit must have latched by the end -- proving the bitmask block genuinely
        // ran, not just the average/fit steps above it.
        Assert.NotEqual(0, tracker.BitMaskForTests);
    }

    [Fact]
    public void SlantTracker_ProcessLineSuppressed_UnlikeProcessLineHistoryOnly_StillEstablishesABaseline()
    {
        // Direct differentiator from the sibling ProcessLineHistoryOnly test above (which asserts
        // HasBaselineForTests stays FALSE for that method) -- proves the two suppression shapes are
        // genuinely different code paths, not accidentally the same one under a new name. Uses a
        // perfectly stable (zero-drift) position sequence specifically because
        // SlantTracker_NoDrift_NeverReportsACorrection already established that ProcessLine itself
        // never COMMITS on this exact input -- so any baseline observed here is attributable only to
        // ProcessLineSuppressed's own fit/baseline step, not a side effect of a would-be commit this
        // input can't produce anyway.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLineSuppressed(0);
        }

        Assert.True(tracker.HasBaselineForTests);
    }

    [Fact]
    public void SlantTracker_ResetBaseline_ClearsStateTheSameWayAPostCommitResetDoes()
    {
        // RX buffer subsystem Phase 6b: ResetBaseline is a thin public wrapper around the exact same
        // private Reset() TryComputeCorrection already calls after every real commit -- this pins that
        // it's reachable and has the documented effect, not that it's a NEW reset shape.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLine(0, hasStagingBuffer: true);
        }

        Assert.True(tracker.HasBaselineForTests, "Test setup problem: baseline never established before ResetBaseline was even called.");
        Assert.Equal(10, tracker.TotalLinesObservedForTests);

        tracker.ResetBaseline();

        Assert.False(tracker.HasBaselineForTests);
        Assert.Equal(0, tracker.TotalLinesObservedForTests);
        Assert.All(tracker.HistoryForTests, v => Assert.Equal(0, v));
    }

    [Fact]
    public void SlantTracker_GetSqerrPos_TruncatesTowardZero_NotFloorAndNotRounded()
    {
        // Main.h:1342 declares `int __fastcall GetSqerrPos(int n)`; Main.cpp:3878-3880 computes
        // `double l0` and does `return l0;` -- a C truncation TOWARD ZERO. For a negative fit that
        // is materially different from floor (which .NET's Math.Floor / a naive (int)Math.Floor
        // port would give) and from Math.Round's banker's rounding.
        //
        // The 5 fed values below are chosen so the exact least-squares value at i=0 is -1.4:
        //   history (oldest..newest) = -10, -8, -6, -4, -1, so l_i = h[15-i] gives
        //   l0=-1, l1=-4, l2=-6, l3=-8, l4=-10; L=-29, T=10, TT=30, TL=-80;
        //   l0 = (L*TT - T*TL)/(5*TT - T*T) = (-870 + 800)/50 = -1.4
        // Truncation toward zero -> -1. Floor -> -2. Math.Round(-1.4) -> -1 (indistinguishable
        // here), so the discriminating pair this test pins is trunc-vs-floor; the banker's-rounding
        // half is pinned by the sibling test below at a .5 boundary.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        foreach (var v in new[] { -10, -8, -6, -4, -1 })
        {
            tracker.ProcessLine(v, hasStagingBuffer: true); // all deltas (max 10) are well under this tracker's jitter gate (160)
        }

        Assert.True(tracker.HasBaselineForTests, "Test setup problem: the jitter gate rejected this sequence, so no baseline was captured.");
        Assert.Equal(-1, tracker.BaselinePositionForTests);
    }

    [Fact]
    public void SlantTracker_GetSqerrPos_DoesNotBankersRoundAHalfBoundary()
    {
        // l0 = (L*30 - 10*TL)/50 with fed values 0, 1, 2, 3, 6:
        //   l0=6, l1=3, l2=2, l3=1, l4=0; L=12, TL = 0*6+1*3+2*2+3*1+4*0 = 10
        //   l0 = (12*30 - 10*10)/50 = (360-100)/50 = 5.2  -> trunc 5
        // and with 0, 1, 2, 3, 7: L=13, TL=10 -> (390-100)/50 = 5.8 -> trunc 5, NOT 6.
        // Math.Round would give 6 for the second case; legacy gives 5.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        foreach (var v in new[] { 0, 1, 2, 3, 7 })
        {
            tracker.ProcessLine(v, hasStagingBuffer: true);
        }

        Assert.True(tracker.HasBaselineForTests, "Test setup problem: the jitter gate rejected this sequence, so no baseline was captured.");
        Assert.Equal(5, tracker.BaselinePositionForTests);
    }

    [Fact]
    public void SlantTracker_RestoreRateWithoutReset_PreservesBaselineAndHistory_UnlikeAdoptCorrectedRate()
    {
        // Legacy's manual Correct Slant REVERT arm (Main.cpp:5420-5423) is a bare
        // `SSTVSET.m_SampFreq = StartSamp; SSTVSET.SetSampFreq();` -- it never calls InitAutoStop
        // (Main.cpp:3801-3810), which is reachable only through the SUCCESS arm's
        // RedrawSampFreq -> UpdateSampFreq chain (Main.cpp:5418 -> 5585 -> 5600). So Auto Slant's
        // baseline, 16-entry history, moving average and bitmask all survive a reverted manual
        // correction in legacy, and tracking continues uninterrupted rather than restarting from
        // zero (5 lines to re-establish a baseline, +3 more before a correction is even eligible).
        //
        // A constant non-zero position (7) is used deliberately: with an all-zero history a
        // preserved history and a Reset()-cleared one are indistinguishable, so the assertion below
        // would pass vacuously. 7 also never commits (fittedPosition == baselinePosition -> d == 0),
        // so nothing but the method under test can touch the state between setup and assertion.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLine(7, hasStagingBuffer: true);
        }

        Assert.True(tracker.HasBaselineForTests, "Test setup problem: baseline never established before the revert.");
        var historyBefore = tracker.HistoryForTests;
        var baselineBefore = tracker.BaselinePositionForTests;

        tracker.RestoreRateWithoutReset(SampleRate * 1.01); // forward manual commit
        tracker.RestoreRateWithoutReset(SampleRate);        // the revert: back to the pre-manual rate

        Assert.True(tracker.HasBaselineForTests, "A reverted manual correction wiped the Auto Slant baseline -- legacy's revert arm (Main.cpp:5420-5423) never calls InitAutoStop.");
        Assert.Equal(10, tracker.TotalLinesObservedForTests);
        Assert.Equal(baselineBefore, tracker.BaselinePositionForTests);
        Assert.Equal(historyBefore, tracker.HistoryForTests);
        Assert.Equal(0.0, tracker.DriftPpm); // rate pair really did go back
    }

    [Fact]
    public void SlantTracker_AdoptCorrectedRate_StillResets_TheAutomaticCommitPathIsUnchanged()
    {
        // Negative control for the sibling test above: the AUTOMATIC path keeps legacy's
        // InitAutoStop-after-commit behaviour (ultracode audit finding #9), so the two methods must
        // remain observably different, not accidentally the same one under two names.
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);

        for (var line = 0; line < 10; line++)
        {
            tracker.ProcessLine(7, hasStagingBuffer: true);
        }

        Assert.True(tracker.HasBaselineForTests, "Test setup problem: baseline never established.");

        tracker.AdoptCorrectedRate(SampleRate * 1.01, hasStagingBuffer: true);

        Assert.False(tracker.HasBaselineForTests);
        Assert.Equal(0, tracker.TotalLinesObservedForTests);
        Assert.All(tracker.HistoryForTests, v => Assert.Equal(0, v));
    }

    [Fact]
    public void SlantTracker_AutomaticCommit_ResetsBaselineOnlyWhenAStagingBufferExists()
    {
        // Batch 2 chunk 2b round-2 fix. Legacy reaches InitAutoStop (Main.cpp:3801-3810 -- the
        // baseline/history/average/bitmask wipe) after an AUTOMATIC commit only through
        // UpdateSampFreq's staging-buffer guard: `if( (dp->m_StgBuf != NULL) || WaveStg.IsOpen() )`
        // (Main.cpp:5597) wraps the InitAutoStop call at :5600, while the commit's own rate write
        // (Main.cpp:4015-4016) and UpdateSampFreq's bare SetSampFreq() (:5596) both sit OUTSIDE it.
        // So under RxBufferMode.Off (legacy sys.m_UseRxBuff == 0 -- reachable there too: Main.cpp:11903
        // only DISABLES the Auto Slant menu item, it never clears KRSA->Checked, and :3968 reads only
        // Checked) legacy keeps m_ASBgnPos/m_AutoStopAPos/m_AutoStopACnt across every automatic commit.
        //
        // Both arms are driven by the IDENTICAL OPEN-LOOP sequence, so the flag is the only variable.
        // Open-loop for the same reason as
        // SlantTracker_AdoptCorrectedRate_FirstNaturalCorrectionUsesTheAdoptedBaseline: a closed loop
        // self-corrects toward the true external drift regardless of internal state and would erase
        // exactly the difference under test.
        const double nominalSamplesPerLine = SampleRate * 0.15; // 6615 samples/line at 44100Hz
        const double perLineDrift = 20.0; // under the jitter gate (8*mult == 160), over _limitsHz[0] (100Hz)

        var withoutBuffer = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);
        var withBuffer = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        double? withoutBufferRate = null;
        double? withBufferRate = null;
        for (var line = 1; line <= 8; line++)
        {
            var position = (int)(perLineDrift * line);
            withoutBufferRate = withoutBuffer.ProcessLine(position, hasStagingBuffer: false) ?? withoutBufferRate;
            withBufferRate = withBuffer.ProcessLine(position, hasStagingBuffer: true) ?? withBufferRate;
        }

        // Positive control: a commit really fired on BOTH arms (otherwise every assertion below is
        // vacuous), and the RATE half of the commit is NOT gated by the flag -- Main.cpp:4015-4016 is
        // unconditional, only InitAutoStop is guarded.
        Assert.NotNull(withoutBufferRate);
        Assert.Equal(withBufferRate, withoutBufferRate);
        Assert.Equal(withBuffer.DriftPpm, withoutBuffer.DriftPpm, tolerance: 1e-9);
        Assert.True(withoutBuffer.DriftPpm > 0.0, "Test setup problem: no correction was adopted, so neither arm discriminates anything.");

        // RxBufferMode.Off: everything InitAutoStop would have wiped survives the commit.
        Assert.True(withoutBuffer.HasBaselineForTests);
        Assert.Equal(100, withoutBuffer.BaselinePositionForTests); // GetSqerrPos at the line-5 capture: perLineDrift*5
        Assert.Equal(8, withoutBuffer.TotalLinesObservedForTests);
        Assert.Equal(160, withoutBuffer.HistoryForTests[^1]); // perLineDrift*8, the line the commit fired on

        // RxBufferMode.On/Extended: unchanged from before this fix -- InitAutoStop still runs.
        Assert.False(withBuffer.HasBaselineForTests);
        Assert.Equal(int.MaxValue, withBuffer.BaselinePositionForTests);
        Assert.Equal(0, withBuffer.TotalLinesObservedForTests);
        Assert.All(withBuffer.HistoryForTests, v => Assert.Equal(0, v));
    }

    [Fact]
    public void SlantTracker_Mult_IsReDerivedFromTheCorrectedWidth_OnlyWhenAStagingBufferExists()
    {
        // Batch 2 chunk 2b round-3 fix. Legacy's m_Mult (Main.cpp:3860) is recomputed from the
        // CURRENT line width every time InitAutoStop runs -- which, like the rest of InitAutoStop's
        // wipe, only happens after an automatic commit when a staging buffer exists
        // (Main.cpp:5597's guard around :5600). An earlier version of this port froze _mult at its
        // construction-time value forever, silently disagreeing with legacy (and with this port's
        // OWN re-derived Auto Sync copy of the same legacy variable) by up to 8 samples on the
        // jitter gate after any commit that crossed a 320-sample line-width boundary.
        //
        // A large-enough per-line drift (150 samples/line, still under the initial gate 8*mult=160)
        // over 8 lines produces one commit whose corrected width crosses the 6720-sample boundary
        // (6615 nominal -> mult 20; the correction pushes the width up past 6720 -> mult 21) --
        // verified by asserting against the tracker's own current nominal width, not a hand-derived
        // constant, so this doesn't depend on reproducing NormalSampFreq's exact quantization by hand.
        const double nominalSamplesPerLine = SampleRate * 0.15; // 6615 samples/line at 44100Hz, mult 20
        const double perLineDrift = 150.0; // under 8*20=160; large enough to cross a 320-sample boundary

        var withoutBuffer = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);
        var withBuffer = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        const int constructionMult = 20; // Math.Max(1, (int)(6615/320.0))
        Assert.Equal(constructionMult, withoutBuffer.MultForTests);
        Assert.Equal(constructionMult, withBuffer.MultForTests);

        double? withoutBufferRate = null;
        double? withBufferRate = null;
        for (var line = 1; line <= 8; line++)
        {
            var position = (int)(perLineDrift * line);
            withoutBufferRate = withoutBuffer.ProcessLine(position, hasStagingBuffer: false) ?? withoutBufferRate;
            withBufferRate = withBuffer.ProcessLine(position, hasStagingBuffer: true) ?? withBufferRate;
        }

        // Positive control: a commit really fired on both arms, and it moved the corrected width
        // enough to actually cross a mult boundary -- otherwise every assertion below is vacuous.
        Assert.NotNull(withoutBufferRate);
        Assert.Equal(withBufferRate, withoutBufferRate); // the rate write itself is never gated
        Assert.NotEqual(nominalSamplesPerLine, withBuffer.NominalSamplesPerLineForTests);

        // RxBufferMode.Off: legacy never re-derives m_Mult here (InitAutoStop never runs) -- frozen
        // at its construction value, even though the corrected width moved.
        Assert.Equal(constructionMult, withoutBuffer.MultForTests);

        // RxBufferMode.On/Extended: legacy DOES re-derive m_Mult here -- must equal a fresh
        // computation from the tracker's own current (corrected) nominal width, and that must
        // actually differ from the frozen construction-time value (proving the recompute really
        // crossed a boundary, not just ran and landed on the same number by coincidence).
        var expectedMult = Math.Max(1, (int)(withBuffer.NominalSamplesPerLineForTests / 320.0));
        Assert.Equal(expectedMult, withBuffer.MultForTests);
        Assert.NotEqual(constructionMult, withBuffer.MultForTests);
    }

    [Fact]
    public void GetSyncSegmentOffsetMs_Robot36_SyncIsAtLineStart()
    {
        Assert.Equal(0.0, SstvModeRegistry.GetSyncSegmentOffsetMs(SstvModeRegistry.Robot36));
    }

    [Fact]
    public void GetSyncSegmentOffsetMs_ScottieS1_SyncIsMidLineAfterGAndB()
    {
        // LineSCT: separator-G-separator-B-SYNC-separator-R. Sync starts after 1.5(sep)+138.24(G)+1.5(sep)+138.24(B).
        var mode = SstvModeRegistry.ScottieS1;
        Assert.Equal(1.5 + 138.24 + 1.5 + 138.24, SstvModeRegistry.GetSyncSegmentOffsetMs(mode), tolerance: 0.001);
    }

    [Fact]
    public void GetSyncSegmentOffsetMs_Mn73_NarrowSyncIsAtLineStart()
    {
        Assert.Equal(0.0, SstvModeRegistry.GetSyncSegmentOffsetMs(SstvModeRegistry.Mn73));
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_RealisticClockMismatch_DecodesWithinTolerance()
    {
        // Simulate a real clock mismatch: encode as if the true sample rate is 0.05% (500ppm) higher
        // than what the decoder is told -- already a generous/pessimistic real crystal-oscillator
        // tolerance (typical sound card clocks are within tens of ppm) -- exactly what Auto Slant
        // exists to detect and correct for (unlike AFC, which corrects a *frequency* offset, this is
        // a *timing/rate* offset -- see SlantTracker's doc comment). Without slant correction, every
        // line's pixel windows would progressively misalign against the encoder's, growing worse
        // line by line.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detectedMode);
        Assert.NotNull(decodedImage);

        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);
        Assert.True(averageDelta <= 10.0, $"Average per-channel delta {averageDelta:F2} exceeded tolerance 10 -- slant correction should have kept this within normal round-trip tolerance despite the 500ppm rate mismatch.");
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_SevereClockMismatch_ConvergesButDoesNotFullyCorrect()
    {
        // A 1% mismatch is a large, atypical clock error for real hardware (500-2000x a normal
        // crystal's tolerance) -- included not as a realistic scenario but to document a real,
        // verified characteristic of legacy's own algorithm: AutoStopJob's m_ASBitMask permanently
        // disables each of its 5 confidence tiers once used (Main.cpp:4006-4010), so for a single
        // long transmission it converges quickly early on and then locks -- it does NOT keep
        // re-adjusting for the rest of the image, even if a full correction hasn't been reached.
        // For a small (realistic) mismatch that's irrelevant (it converges before the residual
        // matters); for a mismatch this large over a 240-line image, the residual compounds to a
        // visible decode error by the end -- correctly reproducing legacy's own limitation, not a
        // bug in the port. This test only checks that correction is clearly happening (average delta
        // far below what zero correction would produce), not that it's perfect.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        IImageSource? decodedImage = null;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(decodedImage);
        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);

        // Uncorrected, a 1% mismatch over Robot36's 240 lines accumulates to roughly a full line's
        // width of misalignment by the end (see SlantTracker's doc comment's residual-drift math) --
        // an uncorrected decode of this scenario averages well over 100. Convergence should get this
        // well under half that, even though legacy's own design means it won't reach the normal <=10
        // round-trip tolerance for a mismatch this severe.
        Assert.True(averageDelta < 60.0, $"Average per-channel delta {averageDelta:F2} -- expected meaningfully better than an uncorrected decode (~100+) even though full correction isn't expected for a mismatch this severe.");
    }

    [Fact]
    public void SlantTracker_DriftPpm_ZeroBeforeAnyCorrectionCommits()
    {
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine: SampleRate * 0.15, thresholdLinePositions: [64, 128, 160, 220]);
        Assert.Equal(0.0, tracker.DriftPpm);
    }

    [Fact]
    public void SlantTracker_DriftPpm_MatchesLegacyDrawSlantInfoFormulaAfterACorrectionCommits()
    {
        // Main.cpp:5537: (SSTVSET.m_SampFreq - sys.m_SampFreq) * 1e6 / sys.m_SampFreq -- re-derived
        // here independently from the committed corrected rate, not by reading DriftPpm's own
        // implementation, so this actually discriminates a wrong formula.
        const double nominalSamplesPerLine = SampleRate * 0.15;
        const double trueSamplesPerLine = nominalSamplesPerLine * 1.01;
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        double? lastResult = null;
        var trueCumulative = 0.0;
        var assumedCumulative = 0.0;
        var assumedSamplesPerLine = nominalSamplesPerLine;
        for (var line = 0; line < 300 && lastResult is null; line++)
        {
            trueCumulative += trueSamplesPerLine;
            assumedCumulative += assumedSamplesPerLine;
            lastResult = tracker.ProcessLine((int)(trueCumulative - assumedCumulative), hasStagingBuffer: true);
        }

        Assert.NotNull(lastResult);
        var expectedPpm = (lastResult!.Value - SampleRate) * 1_000_000.0 / SampleRate;
        Assert.Equal(expectedPpm, tracker.DriftPpm, tolerance: 0.001);
    }

    [Fact]
    public void AnalogFmSstvDecoder_BeforeAnyLock_SlantPpmAndSyncOffsetSamplesAreNull()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        Assert.Null(decoder.SlantPpm);
        Assert.Null(decoder.SyncOffsetSamples);
    }

    [Fact]
    public void AnalogFmSstvDecoder_AvtMode_SlantPpmAndSyncOffsetSamplesStayNull()
    {
        // AVT has no Auto Slant tracking -- InitializeSlant nulls _slantTracker for it (see
        // AnalogFmSstvDecoder's own class doc comment and SlantTracker's "Deliberately NOT ported"
        // note). ForceMode(Avt) locks and announces immediately -- the simplest way to reach a
        // locked-but-AVT state without a full encode/decode round trip (same pattern
        // ForceModeTests.cs uses).
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        decoder.ForceMode(SstvModeRegistry.Avt);
        decoder.PushSamples(new float[64]);

        Assert.Equal(SstvModeRegistry.Avt.Id, decoder.ModeForTests?.Id);
        Assert.Null(decoder.SlantPpm);
        Assert.Null(decoder.SyncOffsetSamples);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_AfterARealDecode_SyncOffsetSamplesMatchesManualComputationFromExposedTestFields()
    {
        // Sampled from inside LineDecoded, not after PushSamples returns -- a bulk push that
        // completes the whole image also runs EndOfImage() before PushSamples returns, which nulls
        // _mode/_slantTracker/_lastLineSyncPeakPosition (see EndOfImage's own doc comment) -- the
        // property must read back null at that point (already covered by the pre-lock/AVT tests
        // above), so this test needs to observe mid-decode state instead.
        var mode = SstvModeRegistry.Robot36;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(SampleRate);
        int? actual = null;
        int? expected = null;
        var observations = 0;
        decoder.LineDecoded += _ =>
        {
            if (decoder.LastLineSyncPeakPositionForTests is not { } syncPeakPosition)
            {
                return;
            }

            observations++;
            var ofp = decoder.SyncPeakOffsetSamplesForTests!.Value;
            var raw = (int)syncPeakPosition - ofp;
            var half = (int)(decoder.EffectiveSamplesPerLineForTests / 2.0);
            expected = raw > half ? raw - (int)decoder.EffectiveSamplesPerLineForTests : raw;
            actual = decoder.SyncOffsetSamples;
        };

        decoder.PushSamples(samples.ToArray());

        Assert.True(observations > 0, "LineDecoded never fired with a non-null LastLineSyncPeakPositionForTests -- test setup didn't exercise the code path under test.");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_AfterASlantCorrectionCommits_SlantPpmMatchesTheUnderlyingTrackersOwnValue()
    {
        // Reuses AnalogFmSstvDecoder_RealisticClockMismatch's own 500ppm scenario (SlantTests.cs
        // above) -- realistic enough that Auto Slant actually commits a correction during the
        // decode, so this exercises the non-zero path, not just the zero-before-any-commit default.
        // Sampled from inside LineDecoded -- see the sibling SyncOffsetSamples test's own doc comment
        // on why reading back after PushSamples returns would only ever observe the post-EndOfImage
        // null state.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        double? lastNonZeroTrackerPpm = null;
        double? lastNonZeroPropertyPpm = null;
        decoder.LineDecoded += _ =>
        {
            var trackerPpm = decoder.SlantTrackerForTests?.DriftPpm;
            if (trackerPpm is not (null or 0.0))
            {
                lastNonZeroTrackerPpm = trackerPpm;
                lastNonZeroPropertyPpm = decoder.SlantPpm;
            }
        };

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(lastNonZeroTrackerPpm); // sanity: a correction actually committed during the decode
        Assert.Equal(lastNonZeroTrackerPpm, lastNonZeroPropertyPpm);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_AutoSlantDisabled_SlantPpmNeverMovesOffZero_ButBookkeepingStillAdvances()
    {
        // Regression test for the Auto-correct on/off toggle (auditor plan-review, batch 6): reuses
        // AnalogFmSstvDecoder_AfterASlantCorrectionCommits_SlantPpmMatchesTheUnderlyingTrackersOwnValue's
        // own 500ppm scenario above, which reliably commits a correction with the toggle on -- with
        // autoSlantEnabled: false, no commit must ever happen.
        //
        // NOT "SlantPpm stays null" -- an earlier version of this test (and this port's own doc
        // comments) wrongly assumed that, caught by this test actually failing once written:
        // SlantTracker.DriftPpm is `(_currentSampleRate - _sampleRate) * 1e6 / _sampleRate`, and
        // _currentSampleRate is initialized to _sampleRate in the constructor (SlantTracker.cs:80) --
        // so DriftPpm reads exactly 0.0 (a real, non-null double) from the moment InitializeSlant
        // constructs the tracker, regardless of this flag's value, since construction is deliberately
        // NOT gated (see this file's own ApplySlantTracking gate comment). The correct invariant is
        // "SlantPpm never moves AWAY from 0.0" -- proving no commit ever mutates _currentSampleRate --
        // not "SlantPpm is null."
        //
        // Asserting that alone is still insufficient (auditor finding): staying at 0.0 is also
        // satisfied by two WRONG implementations -- skipping ApplySlantTracking's slant call entirely,
        // or calling ProcessLine(...)-and-discarding the result before it would have committed
        // (explicitly documented as wrong in SlantTracker.cs's own ProcessLineHistoryOnly doc comment,
        // since ProcessLine itself mutates the baseline/_correctionAverage/_bitMask/history state as a
        // side effect regardless of what the caller does with its return value, even on lines where it
        // returns null). So this also asserts the tracker's own bookkeeping-only hook:
        // TotalLinesObservedForTests keeps advancing (proving ProcessLineHistoryOnly specifically ran,
        // not a full skip), while HasBaselineForTests stays false (proving ProcessLine was never
        // called).
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        // Captured DURING LineDecoded, not re-read after PushSamples returns -- EndOfImage() (which
        // runs before PushSamples returns for a single bulk push covering a whole image) nulls both
        // _mode and _slantTracker, so decoder.SlantPpm/SlantTrackerForTests would read null/null
        // post-completion regardless of this test's own outcome, same reasoning as the sibling
        // AfterASlantCorrectionCommits test above never asserting decoder.SlantPpm after the push.
        var decoder = new AnalogFmSstvDecoder(declaredSampleRate, autoSlantEnabled: false);
        var observedNonZeroSlantPpm = false;
        var maxTotalLinesObserved = 0;
        var everHadBaseline = false;
        decoder.LineDecoded += _ =>
        {
            if (decoder.SlantPpm is not (null or 0.0))
            {
                observedNonZeroSlantPpm = true;
            }

            var tracker = decoder.SlantTrackerForTests;
            if (tracker is not null)
            {
                maxTotalLinesObserved = Math.Max(maxTotalLinesObserved, tracker.TotalLinesObservedForTests);
                everHadBaseline |= tracker.HasBaselineForTests;
            }
        };

        decoder.PushSamples(samples.ToArray());

        Assert.False(observedNonZeroSlantPpm);
        Assert.True(maxTotalLinesObserved > 0); // bookkeeping DID run
        Assert.False(everHadBaseline); // but no commit ever happened
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_DuringAPendingAvtTrainingWindow_SlantPpmIsNullDespiteTheAbandonedTrackerStillBeingAlive()
    {
        // Auditor finding (real bug, fixed): AbandonInProgressImage (the S7 mid-reception AVT
        // hand-off, AvtNoiseTolerantDetectionTests.cs's own
        // MidReception_RealAvtTransmissionAfterAnotherMode_ChunkedPush_StaysBounded exercises the same
        // scenario) nulls _mode but deliberately leaves _slantTracker alive while a new AVT training
        // lock resolves -- a real window, chunked-push-only, up to ~7.1s (that sibling test's own doc
        // comment). A single bulk push (tried first here) doesn't reach this window at all: with the
        // whole AVT header already buffered, TryResolveAvtTraining resolves same-call and
        // InitializeSlant(Avt) (which nulls _slantTracker itself, independently of this fix) runs
        // before DecodeRestarted ever fires -- confirmed by direct inspection, not assumed. Only a
        // chunked push that stops mid-training actually leaves _mode null while the OLD tracker is
        // still alive. A first version of SlantPpm was plain `_slantTracker?.DriftPpm`, which would
        // have kept reporting the abandoned image's stale drift for that whole pending window instead
        // of null -- this test reproduces the real window and pins the fixed (mode-checked) behavior.
        // The precondition assertion below proves the exploit window actually occurred during this
        // run, not just in theory -- without it, this test would pass vacuously if the window was
        // never hit.
        var firstMode = SstvModeRegistry.MartinM1;
        var firstImage = CreateGradientTestImage(firstMode.ImageWidth, firstMode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(11025);
        var firstSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(firstMode, firstImage))
        {
            firstSamples.Add(sample);
        }

        var truncatedFirstSamples = firstSamples.Take(firstSamples.Count * 3 / 10).ToArray();

        var avtMode = SstvModeRegistry.Avt;
        var avtPixels = new Rgb24[avtMode.ImageWidth * avtMode.ImageHeight];
        Array.Fill(avtPixels, new Rgb24(10, 20, 30));
        var avtImage = new ArrayImageSource(avtMode.ImageWidth, avtMode.ImageHeight, avtPixels);
        var avtSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(avtMode, avtImage))
        {
            avtSamples.Add(sample);
        }

        var combined = truncatedFirstSamples.Concat(avtSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        var sawPendingWindow = false;

        const int chunkSize = 500;
        for (var offset = 0; offset < combined.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, combined.Length - offset);
            decoder.PushSamples(combined.AsMemory(offset, length));

            if (decoder.ModeForTests is null && decoder.SlantTrackerForTests is not null)
            {
                sawPendingWindow = true;
                Assert.Null(decoder.SlantPpm);
            }
        }

        Assert.True(sawPendingWindow, "Never observed _mode == null with a still-alive SlantTrackerForTests -- test setup no longer exercises the pending-AVT-training window this test targets.");
    }

    [Fact]
    public void SlantTracker_AfterACommit_BaselineResetsInsteadOfStayingStale()
    {
        // ultracode audit finding #9: legacy's UpdateSampFreq calls InitAutoStop immediately after
        // every commit (Main.cpp:5600->3801-3810), fully reinitializing baseline/history/bitmask --
        // the fixed baseline reset must be visible on the very same call that returns a correction,
        // not lag a call behind.
        const double nominalSamplesPerLine = SampleRate * 0.15;
        const double trueSamplesPerLine = nominalSamplesPerLine * 1.01; // large enough to commit quickly
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        double? result = null;
        var trueCumulative = 0.0;
        var assumedCumulative = 0.0;
        var assumedSamplesPerLine = nominalSamplesPerLine;
        for (var line = 0; line < 300 && result is null; line++)
        {
            trueCumulative += trueSamplesPerLine;
            assumedCumulative += assumedSamplesPerLine;
            result = tracker.ProcessLine((int)(trueCumulative - assumedCumulative), hasStagingBuffer: true);
        }

        Assert.NotNull(result); // sanity: a correction actually happened within 300 lines
        Assert.False(tracker.HasBaselineForTests, "Baseline was still set immediately after a commit -- Reset() should have cleared it (a fresh baseline is only re-established on the NEXT eligible line, matching legacy's InitAutoStop).");
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_SevereClockMismatch_SlantAccumulatorNeverExceedsOneLine_AcrossACommit()
    {
        // ultracode audit finding #10: on the line a correction commits, the within-line accumulator
        // must carry against the OLD samples-per-line, not whatever the correction just changed it
        // to -- otherwise a rate-change line produces a one-time jump of |old-new| samples instead of
        // staying in [0, effectiveSamplesPerLine). Uses the same severe (1%) mismatch as
        // AnalogFmSstvDecoder_SevereClockMismatch_ConvergesButDoesNotFullyCorrect specifically because
        // its own doc comment confirms this converges quickly (i.e. commits at least once early),
        // making a reintroduced jump easy to catch via this bound.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(230, 230, 230));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.01);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        var violated = false;
        decoder.LineDecoded += _ =>
        {
            // Milestone-audit note: a milestone-audit suggestion to tighten this bound to strictly
            // [0,1) was tried and reverted -- that invariant only holds immediately AFTER a
            // slant-tracker line boundary commits, but this handler samples state at LineDecoded
            // time (driven by PIXEL-decode progress, a different cursor than the slant tracker's own
            // raw-sample cursor), so the carry can legitimately be anywhere in [0, effectiveSamplesPerLine)
            // at the moment this fires, not just [0,1). Confirmed by testing: tightening this to
            // >= 1.0 made a previously-passing (and still-correct) run fail. [0, effectiveSamplesPerLine)
            // is the right bound for THIS sampling point; it still catches this test's own
            // rate-increasing regression case (carry goes negative), which is what it was written for.
            var carry = decoder.SlantIdealSamplesSoFarInLineForTests;
            var bound = decoder.EffectiveSamplesPerLineForTests;
            if (carry < 0.0 || carry >= bound)
            {
                violated = true;
            }
        };

        decoder.PushSamples(samples.ToArray());

        Assert.False(violated, "Slant accumulator left its valid [0, effectiveSamplesPerLine) range on some line -- likely a reintroduced boundary-jump bug on a rate-change commit.");
    }

    private static double ComputeAveragePerChannelDelta(IImageSource expected, IImageSource actual)
    {
        double totalDelta = 0;
        var sampleCount = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);
            for (var x = 0; x < expected.Width; x++)
            {
                totalDelta += Math.Abs(expectedLine[x].R - actualLine[x].R);
                totalDelta += Math.Abs(expectedLine[x].G - actualLine[x].G);
                totalDelta += Math.Abs(expectedLine[x].B - actualLine[x].B);
                sampleCount += 3;
            }
        }

        return totalDelta / sampleCount;
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    private static double SteadyStateAmplitude(double resonantFrequencyHz, double inputFrequencyHz)
    {
        var filter = new TankFilter();
        filter.SetFreq(resonantFrequencyHz, SampleRate, 100.0);
        return DriveAndMeasurePeak(filter.Process, inputFrequencyHz);
    }

    private static double SteadyStateEnvelope(double resonantFrequencyHz, double inputFrequencyHz)
    {
        var detector = new SyncEnvelopeDetector(SampleRate, resonantFrequencyHz);
        return DriveAndMeasurePeak(detector.ProcessSample, inputFrequencyHz);
    }

    private static double DriveAndMeasurePeak(Func<double, double> process, double inputFrequencyHz)
    {
        var phaseIncrement = 2 * Math.PI * inputFrequencyHz / SampleRate;
        var phase = 0.0;
        var peak = 0.0;

        // Run long enough for the resonator/lowpass to reach steady state, then measure peak over
        // one more full cycle at the input frequency.
        var settleSamples = (int)(0.5 * SampleRate);
        var measureSamples = (int)(SampleRate / inputFrequencyHz) + 1;

        for (var i = 0; i < settleSamples; i++)
        {
            phase += phaseIncrement;
            process(Math.Sin(phase));
        }

        for (var i = 0; i < measureSamples; i++)
        {
            phase += phaseIncrement;
            var output = process(Math.Sin(phase));
            peak = Math.Max(peak, Math.Abs(output));
        }

        return peak;
    }
}
