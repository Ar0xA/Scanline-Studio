using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Options stub backlog item 2 (docs/plans/options-stub-item2-zerocrossing-tuning-plan.md) -- the
/// <see cref="AnalogFmSstvDecoder.RequestZeroCrossingTuning"/> WIRING (deferred-request drain,
/// decoder-level current-tuning tracking), same shape as <see cref="RequestPllTuningTests"/>. The
/// zero-crossing demodulator's own DSP math is <see cref="ZeroCrossingFrequencyCounterTests"/>'s job.
/// </summary>
public class RequestZeroCrossingTuningTests
{
    [Fact]
    public void RequestZeroCrossingTuning_BeforeAnyLockEverHappens_UpdatesTrackedTuning_DoesNotThrow()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestZeroCrossingTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 7, outputCutoffHz: 800, smoothingFrequencyHz: 2500);
        decoder.PushSamples(new float[64]); // never matches any header -- stays idle, drain still runs

        var tuning = decoder.ZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Fir, tuning.SmoothingMode);
        Assert.Equal(7, tuning.OutputOrder);
        Assert.Equal(800, tuning.OutputCutoffHz);
        Assert.Equal(2500, tuning.SmoothingFrequencyHz);
    }

    [Fact]
    public void RequestZeroCrossingTuning_ReachesBothLiveCounterInstances_NotJustTheTrackingFields()
    {
        // Code-review round 1 finding: the decoder-level tracking fields above are non-load-bearing
        // (test-visibility only, per their own field doc comment) -- deleting BOTH
        // ApplyPendingZeroCrossingTuningRequest's SetTuning calls would still pass every other test in
        // this file. Read the two LIVE ZeroCrossingFrequencyCounter instances' own applied mode
        // instead, mirroring RestartableSstvDecoderTests' own "assert the live inner, not just the
        // wrapper" discipline.
        var decoder = new AnalogFmSstvDecoder(11025);
        Assert.Equal(ZeroCrossingSmoothingMode.Iir, decoder.DemodulatorSmoothingModeForTests);
        Assert.Equal(ZeroCrossingSmoothingMode.Iir, decoder.AfcCounterSmoothingModeForTests);

        decoder.RequestZeroCrossingTuning(ZeroCrossingSmoothingMode.Fir, outputOrder: 7, outputCutoffHz: 800, smoothingFrequencyHz: 2500);
        decoder.PushSamples(new float[64]);

        Assert.Equal(ZeroCrossingSmoothingMode.Fir, decoder.DemodulatorSmoothingModeForTests);
        Assert.Equal(ZeroCrossingSmoothingMode.Fir, decoder.AfcCounterSmoothingModeForTests);
    }

    [Fact]
    public void RequestZeroCrossingTuning_NoRequestMade_KeepsLegacyDefaults()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        decoder.PushSamples(new float[64]);

        var tuning = decoder.ZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Iir, tuning.SmoothingMode);
        Assert.Equal(3, tuning.OutputOrder);
        Assert.Equal(900, tuning.OutputCutoffHz);
        Assert.Equal(2200, tuning.SmoothingFrequencyHz);
    }

    [Fact]
    public void RequestZeroCrossingTuning_ConstructedWithNonDefaultTuning_StartsTracked()
    {
        // Proves the constructor-supplied values (the persisted-settings seed path, matching
        // RestartableSstvDecoder's own CreateInner re-seed) flow into the decoder-level tracking
        // fields, not just into _zeroCrossingDemodulator/_afcZeroCrossingCounter's own constructors.
        var decoder = new AnalogFmSstvDecoder(11025, zeroCrossingSmoothingMode: ZeroCrossingSmoothingMode.Off, zeroCrossingOutputOrder: 9, zeroCrossingOutputCutoffHz: 700, zeroCrossingSmoothingFrequencyHz: 3000);

        var tuning = decoder.ZeroCrossingTuningForTests;
        Assert.Equal(ZeroCrossingSmoothingMode.Off, tuning.SmoothingMode);
        Assert.Equal(9, tuning.OutputOrder);
        Assert.Equal(700, tuning.OutputCutoffHz);
        Assert.Equal(3000, tuning.SmoothingFrequencyHz);
    }
}
