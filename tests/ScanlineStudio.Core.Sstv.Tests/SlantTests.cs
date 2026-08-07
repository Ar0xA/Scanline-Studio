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
            lastResult = tracker.ProcessLine(0.0) ?? lastResult;
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
            tracker.ProcessLineHistoryOnly(line + 1.0);
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
        Assert.Equal(Enumerable.Range(1, 10).Select(v => (double)v), history[^10..]);
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

            var result = tracker.ProcessLine(trueCumulative - assumedCumulative);
            if (result is not null)
            {
                lastResult = result;
                assumedSamplesPerLine = nominalSamplesPerLine / SampleRate * result.Value;
            }
        }

        Assert.NotNull(lastResult);
        Assert.Equal(expectedCorrectedRate, lastResult!.Value, tolerance: expectedCorrectedRate * 0.002);
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

        const double spuriousPosition = 1000.0; // >> 160
        for (var line = 0; line < 4; line++)
        {
            tracker.ProcessLine(spuriousPosition);
        }

        tracker.ProcessLine(spuriousPosition); // 5th line: near-zero delta from the 4th, but a huge one from history[10]'s zero default

        Assert.False(tracker.HasBaselineForTests, "Baseline was set on line 5 -- the jitter gate only checked 4 deltas instead of legacy's 5, missing the spurious jump into history[10]'s zero default.");
    }

    [Fact]
    public void SlantTracker_JitterGate_AcceptsOnceTheSpuriousReadingAgesOutOfTheFiveDeltaWindow()
    {
        const double nominalSamplesPerLine = SampleRate * 0.15;
        var tracker = new SlantTracker(SampleRate, nominalSamplesPerLine, thresholdLinePositions: [64, 128, 160, 220]);

        const double spuriousPosition = 1000.0;
        tracker.ProcessLine(spuriousPosition); // line 1 -- this is the one reading that must age out

        // The 5-delta window (indices 15..10) reaches history[10] via its last delta -- a value fed
        // at line 1 (starting at index 15) shifts one index left per subsequent line, so it only
        // clears index 10 (moves to index 9) once 6 more lines have been fed (lines 2-7, 7 total).
        for (var line = 0; line < 6; line++)
        {
            tracker.ProcessLine(0.0); // lines 2-7, all consistent with each other
        }

        Assert.True(tracker.HasBaselineForTests, "Baseline still not set by line 7 -- the spurious line-1 reading should have aged out of the 5-delta window by now.");
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
            result = tracker.ProcessLine(trueCumulative - assumedCumulative);
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
