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
        // SyncFreq's `d -= 128` (sstv.cpp:2347) is a small fixed calibration nudge applied to every
        // reading before it's used, ported as-is (see AfcTracker's constructor comment) rather than
        // dropped as presumed-insignificant -- so even a perfectly on-frequency reading locks a
        // small nonzero correction, not exactly 0: 128 in legacy's x16384/BWH scale is
        // 128*400/16384 = 3.125Hz here. correction = syncTarget - (input - calibrationOffset) =
        // (syncTarget - input) + calibrationOffset = 0 + 3.125.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        var correction = FeedConstantReading(tracker, 1200.0, durationMs: 50);

        Assert.Equal(3.125, correction, tolerance: 0.01);
    }

    [Fact]
    public void AfcTracker_OffsetSyncFrequency_LocksOntoTheCorrection()
    {
        // If the sync tone actually measures at 1210Hz instead of the expected 1200Hz, the
        // correction that AfcTracker.ProcessSample returns (and that AnalogFmSstvDecoder adds to
        // every subsequent demodulated sample) is (syncTarget - measured) + calibrationOffset =
        // (1200 - 1210) + 3.125 = -6.875, per CSSTVDEM::SyncFreq's
        // `m_AFCDiff = m_AFC_SyncVal - m_AFCLock` (sstv.cpp:2360) plus the same calibration nudge as
        // the exact-frequency case above.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1200, bandLowHz: 1000, bandHighHz: 1325, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 400);

        var correction = FeedConstantReading(tracker, 1210.0, durationMs: 50);

        Assert.Equal(-6.875, correction, tolerance: 0.01);
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
        // (1900-1905)+1.0 = -4.0Hz.
        var tracker = new AfcTracker(SampleRate, syncTargetHz: 1900, bandLowHz: 1800, bandHighHz: 1950, afcBeginMs: 1.5, afcWidthMs: 3.0, bandwidthHalfHz: 128);

        var correction = FeedConstantReading(tracker, 1905.0, durationMs: 50);

        Assert.Equal(-4.0, correction, tolerance: 0.01);
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
    public void AnalogFmSstvDecoder_AfcEnabledTrue_AvtStillExcludedRegardlessOfTheToggle()
    {
        // AVT's own exclusion (real legacy behavior) must survive this new toggle unchanged --
        // afcEnabled: true must NOT force a tracker onto AVT.
        var decoder = new AnalogFmSstvDecoder(SampleRate, afcEnabled: true);

        decoder.InitializeAfcForTests(SstvModeRegistry.Avt);

        Assert.False(decoder.HasAfcTrackerForTests);
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
