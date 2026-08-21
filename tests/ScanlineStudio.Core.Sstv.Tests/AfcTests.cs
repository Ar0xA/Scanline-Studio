using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Targeted tests for the AFC (automatic frequency control) port
/// (<see cref="ZeroCrossingFrequencyCounter"/>/<see cref="AfcTracker"/>, porting legacy's
/// <c>CFQC</c>/<c>CSSTVDEM::SyncFreq</c>). AFC exists to correct a *real* carrier frequency offset
/// between transmitter and receiver — a same-process, no-channel synthetic round-trip has no such
/// offset to correct, so <see cref="SstvRoundTripTests"/> passing/failing can't tell us whether this
/// actually works. These tests instead feed synthetic tones directly, with and without a deliberate
/// offset, and check the math against what legacy's own formulas predict.
/// </summary>
public class AfcTests
{
    private const int SampleRate = 44100;

    [Fact]
    public void ZeroCrossingFrequencyCounter_PureTone_ConvergesToActualFrequency()
    {
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        var freq = SampleSineWaveFrequency(counter, targetHz: 1200, durationMs: 100);

        Assert.Equal(1200.0, freq, tolerance: 5.0);
    }

    [Fact]
    public void ZeroCrossingFrequencyCounter_OffsetTone_MeasuresTheOffset()
    {
        // Simulates a receiver whose nominal 1200Hz sync tone actually arrives at 1210Hz -- e.g. a
        // 10Hz carrier/clock offset between transmitter and receiver, exactly what AFC exists to
        // detect and correct for.
        var counter = new ZeroCrossingFrequencyCounter(SampleRate);
        var freq = SampleSineWaveFrequency(counter, targetHz: 1210, durationMs: 100);

        Assert.Equal(1210.0, freq, tolerance: 5.0);
    }

    [Fact]
    public void AfcTracker_ExactSyncFrequency_LocksWithCalibrationOffsetOnly()
    {
        // SyncFreq's `d -= 128` (sstv.cpp:2347), translated out of legacy's inverted/scaled x16384/BWH
        // domain, corresponds to ADDING 128*BWH/16384 real Hz to the measured reading before it's used
        // (ultracode audit finding #2 -- an earlier version of this had the sign backwards, which this
        // test's own then-expected value of +3.125 baked in as a regression-locking bug). 128 in that
        // scale is 128*400/16384 = 3.125Hz here. correction = syncTarget - (measured + calibrationOffset)
        // = (syncTarget - measured) - calibrationOffset = 0 - 3.125.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        var correction = FeedConstantReading(tracker, 1200.0, durationMs: 50);

        Assert.Equal(-3.125, correction, tolerance: 0.01);
    }

    [Fact]
    public void AfcTracker_OffsetSyncFrequency_LocksOntoTheCorrection()
    {
        // If the sync tone actually measures at 1210Hz instead of the expected 1200Hz, the
        // correction that AfcTracker.ProcessSample returns (and that AnalogFmSstvDecoder adds to
        // every subsequent demodulated sample) is (syncTarget - measured) - calibrationOffset =
        // (1200 - 1210) - 3.125 = -13.125, per CSSTVDEM::SyncFreq's
        // `m_AFCDiff = m_AFC_SyncVal - m_AFCLock` (sstv.cpp:2360) plus the same calibration nudge as
        // the exact-frequency case above (ultracode audit finding #2 -- corrected sign).
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        var correction = FeedConstantReading(tracker, 1210.0, durationMs: 50);

        Assert.Equal(-13.125, correction, tolerance: 0.01);
    }

    [Fact]
    public void AfcTracker_ReadingOutsideAcceptanceBand_NeverLocks()
    {
        // A reading nowhere near the sync tone (e.g. mid-image luminance data) must never trigger a
        // lock -- otherwise AFC would corrupt the demodulated stream based on picture content.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        var correction = FeedConstantReading(tracker, 1900.0, durationMs: 50);

        Assert.Equal(0.0, correction);
    }

    [Fact]
    public void AfcTracker_NarrowFamilyThresholds_LockOntoNarrowSyncOffset()
    {
        // MN/MC ("narrow") family: NARROW_SYNC=1900, NARROW_AFCLOW=1800, NARROW_AFCHIGH=1950,
        // NARROW_BWH=128 (sstv.h) -- a much smaller calibration offset here (128*128/16384 = 1.0Hz)
        // since it scales with BWH. A tone measured 5Hz high (1905 instead of 1900) locks
        // (1900-1905)-1.0 = -6.0Hz (ultracode audit finding #2 -- corrected sign; the pre-fix value
        // was -4.0).
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1900, bandLowHz: 1800, bandHighHz: 1950, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 128);

        var correction = FeedConstantReading(tracker, 1905.0, durationMs: 50);

        Assert.Equal(-6.0, correction, tolerance: 0.01);
    }

    [Fact]
    public void AfcTracker_GuardTimeoutBeforeFirstLock_LockAverageSeededWithSyncTarget_NotZero()
    {
        // ultracode audit finding #3: legacy's InitAFC pre-seeds m_AFCLock/m_AFCData to the nominal
        // sync-tone-equivalent value (sstv.cpp:1662/1665) before the 15-tap lock average has ever
        // filled. 10 near-miss cycles (each a stable in-band run that never reaches _afcEndSamples --
        // expired early by toggling out of band right after arming) exhaust _gardRemaining (starts
        // at 10, sstv.cpp:1669) and trigger `_lockAverage.Reset(_lockedFrequencyHz)`. With this fix,
        // _lockedFrequencyHz is still its seeded syncTargetHz (1200) at that point (none of the
        // near-miss cycles ever locked), so the 15-tap average is now full of 1200s. A subsequent
        // genuine dead-on-target lock takes the OTHER branch (_lockAverage.Add, since gard is now 0),
        // blending in just ONE new ~1203.125 sample against 14 seeded 1200s:
        // (14*1200 + 1203.125)/15 = 1200.2083, correction = 1200 - 1200.2083 = -0.2083 -- small, as
        // AFC correcting an on-frequency signal should be. Contrast the pre-fix bug: seeding with 0
        // instead would give (14*0 + 1203.125)/15 = 80.21, correction = 1200 - 80.21 = +1119.79 -- a
        // grossly wrong ~1120Hz correction, matching the audit's measured figure.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        for (var i = 0; i < 10; i++)
        {
            FeedNearMissCycle(tracker);
        }

        var correction = FeedConstantReading(tracker, 1200.0, durationMs: 50);

        Assert.Equal(-0.2083, correction, tolerance: 0.01);
        Assert.True(Math.Abs(correction) < 50.0, $"Correction {correction} is nowhere near the seeded-with-syncTarget expectation -- looks like the pre-fix zero-seeded bug.");
    }

    [Fact]
    public void AfcTracker_CooldownActive_SuppressesRelockUntilExpired()
    {
        // Closes a coverage gap flagged by Tier A Batch 7 chunk 7e (docs/functional-audit-playbook.md):
        // no existing test ever drove _disabledSamplesRemaining (the 100ms post-lock cooldown,
        // sstv.cpp:2270's SyncFreq's m_AFCDis countdown) down to a genuine re-lock attempt. Legacy's
        // own quirk -- m_AFCDis decrements ONLY in the out-of-band branch (sstv.cpp:2374), never while
        // holding in-band -- is exercised here directly, not assumed.
        //
        // Sample-exact bookkeeping at SampleRate=44100: _afcBeginSamples=66, _afcEndSamples=198,
        // _cooldownSamples=4410 (100ms). First lock arms the cooldown at exactly 4410. Feeding 2000
        // out-of-band samples leaves 2410 remaining -- still active. A NEW in-band reading held for
        // 300 samples (comfortably past the 199-sample arm-to-lock window) must NOT relock while the
        // gate is active. Feeding the remaining 2410 out-of-band samples exhausts the cooldown
        // exactly; a third in-band hold then DOES relock.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        var firstCorrection = FeedConstantReading(tracker, 1200.0, durationMs: 50);
        Assert.Equal(-3.125, firstCorrection, tolerance: 0.01);

        FeedOutOfBandSamples(tracker, 2000);

        var duringCooldown = FeedConstantReadingForSamples(tracker, 1210.0, 300);
        Assert.Equal(-3.125, duringCooldown, tolerance: 0.0001);

        FeedOutOfBandSamples(tracker, 2410);
        var afterCooldown = FeedConstantReadingForSamples(tracker, 1210.0, 300);

        Assert.NotEqual(-3.125, afterCooldown);
        Assert.True(afterCooldown < -3.125, $"expected a larger-magnitude negative correction toward the new 1210Hz reading once the cooldown expired, got {afterCooldown}");
    }

    private static double FeedConstantReadingForSamples(AfcTracker tracker, double readingHz, int sampleCount)
    {
        var correction = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            correction = tracker.ProcessSample(readingHz);
        }

        return correction;
    }

    private static void FeedOutOfBandSamples(AfcTracker tracker, int sampleCount)
    {
        for (var i = 0; i < sampleCount; i++)
        {
            tracker.ProcessSample(1900.0); // outside every band this file's trackers construct with
        }
    }

    /// <summary>Feeds an in-band run just long enough to pass <c>_afcBeginSamples</c> (arming the
    /// guard-timeout countdown) but drops out of band before <c>_afcEndSamples</c> (never actually
    /// locking) -- one iteration of the "stable but never quite locks" pattern that exhausts
    /// <c>_gardRemaining</c>.</summary>
    private static void FeedNearMissCycle(AfcTracker tracker)
    {
        var beginSamples = (int)(1.5 / 1000.0 * SampleRate);
        for (var i = 0; i < beginSamples + 1; i++)
        {
            tracker.ProcessSample(1200.0);
        }

        tracker.ProcessSample(1900.0); // drop out of band -- never reaches _afcEndSamples
    }

    [Fact]
    public void AnalogFmSstvDecoder_DefaultConstructor_AfcTrackerIsActiveForANonAvtMode()
    {
        // Regression guard for the new afcEnabled toggle's default: unchanged from before this
        // feature existed (matches legacy's own always-on behavior).
        var decoder = new AnalogFmSstvDecoder(SampleRate);

        decoder.InitializeAfcForTests(SstvModeRegistry.Robot36);

        Assert.True(decoder.HasAfcTrackerForTests);
    }

    [Fact]
    public void AnalogFmSstvDecoder_AfcEnabledFalse_AfcTrackerStaysNullForANonAvtMode()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate, afcEnabled: false);

        decoder.InitializeAfcForTests(SstvModeRegistry.Robot36);

        Assert.False(decoder.HasAfcTrackerForTests);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_RealMistunedDecode_RetunesSyncEnvelopeDetector_ButNotVisTimeDetector()
    {
        // ultracode audit finding #1: legacy's InitTone retunes the Auto-Slant sync-envelope
        // resonator on every AFC lock update, but must NOT retune the separate VIS-time tone
        // detectors legacy never retunes this way. Uses the same real encode-at-a-mistuned-rate,
        // decode-at-the-declared-rate pattern as NarrowModeAfcTests (a genuine carrier offset a
        // same-process synthetic round-trip otherwise has none of) so this exercises the real
        // production call path (ApplyAfcCorrections), not a hand-rolled test-only substitute for it.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new ScanlineStudio.Abstractions.Imaging.Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new ScanlineStudio.Abstractions.Imaging.Rgb24(200, 120, 60));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005); // same 500ppm mismatch SlantTests/NarrowModeAfcTests use

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);

        // Snapshot on every LineDecoded, not after PushSamples returns: EndOfImage nulls
        // _syncEnvelopeDetector once the whole (single-frame) image finishes decoding within this
        // one PushSamples call, so a post-call read would always see null.
        double? lastSyncEnvelopeFrequency = null;
        double? lastBypassFrequency = null;
        decoder.LineDecoded += _ =>
        {
            lastSyncEnvelopeFrequency = decoder.SyncEnvelopeDetectorForTests?.AppliedCenterFrequencyHzForTests;
            lastBypassFrequency = decoder.SyncBypass1200DetectorForTests.AppliedCenterFrequencyHzForTests;
        };

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(lastSyncEnvelopeFrequency);
        // Milestone-audit fix: the ORIGINAL assertion (NotEqual(1200.0, ...)) passed for either sign
        // of the retune direction, so it never actually caught a real sign-inversion bug the
        // milestone audit found. True sample rate is HIGHER than declared, so the decoder's own
        // sample clock measures the sync tone as slightly BELOW 1200Hz (~1199.4Hz -- decoder thinks
        // each sample spans more time than it really does). Legacy's InitTone retunes the resonator
        // to track the MEASURED tone plus the +3.125Hz calibration nudge (finding #2) -- landing
        // slightly ABOVE 1200Hz (~1202.5Hz) here, not below it. A sign-inverted implementation would
        // retune to ~1197.5Hz instead (verified: this is exactly what the pre-fix code computed).
        Assert.True(lastSyncEnvelopeFrequency!.Value > 1200.0, $"Expected the resonator to retune ABOVE 1200Hz (tracking the measured tone + calibration nudge), got {lastSyncEnvelopeFrequency.Value} -- looks like the retune direction is sign-inverted.");
        // Batch 6 chunk 6a correction: legacy's single real m_iir12 IS retuned during a lock (same
        // InitTone call the assertion above confirms) -- it does not stay pinned at nominal. Legacy
        // resets it back to nominal via Stop()'s own InitTone(0) between images (sstv.cpp:1769-1780),
        // so a post-single-image observation like this one correctly sees nominal either way. This
        // port models that with two separate SyncEnvelopeDetector instances (one retuned for AFC/
        // Slant, one that stays nominal forever for the sync-bypass mode-detection path) rather than
        // legacy's one instance retuned-then-reset -- behaviorally equivalent for this observation,
        // architecturally different; not "legacy never retunes this detector."
        Assert.Equal(1200.0, lastBypassFrequency!.Value, tolerance: 0.01);
    }

    [Fact]
    public async Task AnalogFmSstvDecoder_RealMistunedDecodeWithAutoSlant_AfcCorrectionKeepsPaceThroughTheLastLine()
    {
        // Functional-audit fix (D7, round 1): _afcProcessedUpTo/_afcBoundSample cap AFC correction
        // at this image's own NOMINAL (pre-slant-correction) extent -- a bound legacy has no
        // equivalent of (legacy's own AFC correction is unconditional for every sample while
        // m_Sync, sstv.cpp:2270). Auto Slant lengthening the actual per-line stride under a real
        // clock mismatch can make the image's TRUE elapsed sample count exceed that nominal bound
        // before the last line finishes -- and this exact field already shipped one silent
        // total-AFC-disable regression once (see _afcBoundSample's own call-site comment: Commit's
        // body was inlined instead of calling it, which meant this field was never set at all for a
        // while). Zero test coverage existed for whether AFC correction actually reaches the LAST
        // line of a real image, not just the first few -- this proves it does, through a real
        // encode-at-a-mistuned-rate/decode-at-the-declared-rate round trip with Auto Slant active
        // (the default), same pattern as the sibling test above.
        var mode = SstvModeRegistry.Robot36; // many transmission lines, real multi-second duration
        var pixels = new ScanlineStudio.Abstractions.Imaging.Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new ScanlineStudio.Abstractions.Imaging.Rgb24(200, 120, 60));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005); // same 500ppm mismatch AfcTests/SlantTests already use -- triggers a real Auto-Slant correction

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);

        int? afcProcessedUpToAtLastLine = null;
        int? consumedSamplesAtLastLine = null;
        int? nextLineAtLastLine = null;
        var hadAfcTrackerAtLastLine = false;
        decoder.LineDecoded += _ =>
        {
            // Captured HERE, not after PushSamples returns: EndOfImage nulls _afcTracker once the
            // whole (single-frame) image finishes decoding within this one PushSamples call, so a
            // post-call read would always see it already gone.
            afcProcessedUpToAtLastLine = decoder.AfcProcessedUpToForTests;
            consumedSamplesAtLastLine = decoder.ConsumedSamplesForTests;
            nextLineAtLastLine = decoder.NextLineForTests;
            hadAfcTrackerAtLastLine = decoder.HasAfcTrackerForTests;
        };

        decoder.PushSamples(samples.ToArray());

        // Functional-audit fix (D7, round 2 -- corrected twice: Auto Slant's own PerformReplay
        // redraws earlier rows, inflating the raw LineDecoded event COUNT well past ImageHeight, so
        // counting events isn't a reliable "did decode reach the end" signal here; and
        // NextLineForTests is snapshotted at the LAST LineDecoded event, which fires BEFORE that
        // row's own `_nextLine += RowsPerTransmissionLine` runs -- confirmed empirically, not
        // assumed: this test's own real run measured 239, one less than ImageHeight's 240, for
        // Robot36's RowsPerTransmissionLine of 1). Without this, a future change that truncated
        // decode early (e.g. at line 3) would still pass -- the assertions below only ever check the
        // LAST observed snapshot, whatever line that happened to be, not that decode actually
        // reached the real end of the image.
        Assert.True(
            nextLineAtLastLine >= mode.ImageHeight - 1,
            $"Expected decode to have reached (within one row's pre-increment lag of) the real end of the image, but the last LineDecoded snapshot had NextLineForTests={nextLineAtLastLine} against ImageHeight={mode.ImageHeight}.");

        Assert.NotNull(afcProcessedUpToAtLastLine);
        Assert.NotNull(consumedSamplesAtLastLine);
        Assert.True(hadAfcTrackerAtLastLine, "Test setup problem -- AFC tracker was never active for this non-AVT mode.");

        // AFC correction must have kept pace through to (near) the live cursor by the last decoded
        // line -- if _afcBoundSample cut it off early, afcProcessedUpToAtLastLine would freeze well
        // below consumedSamplesAtLastLine instead of tracking within about one line's width of it.
        var lagSamples = consumedSamplesAtLastLine.Value - afcProcessedUpToAtLastLine.Value;
        var oneLineWidthSamples = mode.LineDurationMs / 1000.0 * declaredSampleRate;
        Assert.True(
            lagSamples < oneLineWidthSamples,
            $"Expected AFC correction to have caught up to within one line of the live cursor by the last decoded line, but it lagged by {lagSamples} samples (afcProcessedUpTo={afcProcessedUpToAtLastLine.Value}, consumedSamples={consumedSamplesAtLastLine.Value}, oneLineWidthSamples={oneLineWidthSamples:F0}) -- _afcBoundSample may be cutting off correction before the image actually ends.");
    }

    [Fact]
    public void AnalogFmSstvDecoder_AfcEnabledTrue_AvtStillExcludedRegardlessOfTheToggle()
    {
        // AVT's own exclusion (real legacy behavior) must survive this new toggle unchanged --
        // afcEnabled: true must NOT force a tracker onto AVT.
        var decoder = new AnalogFmSstvDecoder(SampleRate, afcEnabled: true);

        decoder.InitializeAfcForTests(SstvModeRegistry.Avt);

        Assert.False(decoder.HasAfcTrackerForTests);
    }

    // Closes a coverage gap flagged by Tier A Batch 5 chunk 5b (docs/functional-audit-playbook.md):
    // IsFastAfcGroup's 8-mode fast group (sstv.cpp:1162-1177) had no direct test -- every other AFC
    // test always passes explicit 1.5/3.0 literals, so this table was unpinned.
    public static IEnumerable<object[]> AllModesWithExpectedFastAfcGroup() =>
        SstvModeRegistry.All.Select(m => new object[]
        {
            m,
            m == SstvModeRegistry.MartinM1 || m == SstvModeRegistry.MartinM2
                || m == SstvModeRegistry.Sc2180 || m == SstvModeRegistry.Sc2120 || m == SstvModeRegistry.Sc260
                || m == SstvModeRegistry.Mc110 || m == SstvModeRegistry.Mc140 || m == SstvModeRegistry.Mc180,
        });

    [Theory]
    [MemberData(nameof(AllModesWithExpectedFastAfcGroup))]
    public void IsFastAfcGroup_MatchesLegacySetSampFreqGrouping(SstvModeDefinition mode, bool expectedFast)
    {
        Assert.Equal(expectedFast, SstvModeRegistry.IsFastAfcGroup(mode));
    }

    [Theory]
    [InlineData("robot-36", 1200.0)]
    [InlineData("scottie-s1", 1200.0)]
    [InlineData("mn73", 1900.0)]
    [InlineData("mc180", 1900.0)]
    public void InitializeSlant_PicksTheLegacySyncBufferTone(string modeId, double expectedHz)
    {
        // Closes a coverage gap flagged by Tier A Batch 6 chunk 6a (docs/functional-audit-playbook.md):
        // sstv.cpp:2291-2302 -- NARROW_SYNC is really 1900 (sstv.h:440), so the compiled #else branch
        // is the live one: m_fNarrow -> d19 (1900), else non-AVT -> d12 (1200). m_fNarrow ==
        // IsNarrowMode (sstv.cpp:550), i.e. exactly the MN/MC family (NarrowModeCode is not null).
        // No prior test pinned which modes get 1200Hz vs 1900Hz here.
        var decoder = new AnalogFmSstvDecoder(SampleRate);
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        decoder.InitializeSlantForTests(mode);

        Assert.Equal(expectedHz, decoder.SyncEnvelopeDetectorForTests!.AppliedCenterFrequencyHzForTests, tolerance: 1e-9);
    }

    [Fact]
    public void InitializeSlant_Avt_LeavesNoSyncEnvelopeDetector()
    {
        var decoder = new AnalogFmSstvDecoder(SampleRate);

        decoder.InitializeSlantForTests(SstvModeRegistry.Avt);

        Assert.Null(decoder.SyncEnvelopeDetectorForTests);
    }

    private static double SampleSineWaveFrequency(ZeroCrossingFrequencyCounter counter, double targetHz, double durationMs)
    {
        var sampleCount = (int)(durationMs / 1000.0 * SampleRate);
        var phaseIncrement = 2 * Math.PI * targetHz / SampleRate;
        var phase = 0.0;
        var freq = 0.0;

        for (var i = 0; i < sampleCount; i++)
        {
            phase += phaseIncrement;
            freq = counter.ProcessSample(Math.Sin(phase));
        }

        return freq;
    }

    private static double FeedConstantReading(AfcTracker tracker, double readingHz, double durationMs)
    {
        var sampleCount = (int)(durationMs / 1000.0 * SampleRate);
        var correction = 0.0;

        for (var i = 0; i < sampleCount; i++)
        {
            correction = tracker.ProcessSample(readingHz);
        }

        return correction;
    }
}
