namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Options stub backlog item 1 (docs/plans/options-stub-item1-pll-tuning-plan.md) -- the
/// <see cref="AnalogFmSstvDecoder.RequestPllTuning"/> WIRING (deferred-request drain, decoder-level
/// current-tuning tracking), not the PLL demodulator's own DSP math (that's
/// <see cref="PllFmDemodulatorTests"/>'s job).
/// </summary>
public class RequestPllTuningTests
{
    [Fact]
    public void RequestPllTuning_BeforeAnyLockEverHappens_UpdatesTrackedTuning_DoesNotThrow()
    {
        var decoder = new AnalogFmSstvDecoder(11025);

        decoder.RequestPllTuning(vcoGain: 2.0, loopOrder: 5, loopCutoffHz: 1200, outputOrder: 7, outputCutoffHz: 800);
        decoder.PushSamples(new float[64]); // never matches any header -- stays idle, drain still runs

        var tuning = decoder.PllTuningForTests;
        Assert.Equal(2.0, tuning.VcoGain);
        Assert.Equal(5, tuning.LoopOrder);
        Assert.Equal(1200, tuning.LoopCutoffHz);
        Assert.Equal(7, tuning.OutputOrder);
        Assert.Equal(800, tuning.OutputCutoffHz);
    }

    [Fact]
    public void RequestPllTuning_NoRequestMade_KeepsLegacyDefaults()
    {
        var decoder = new AnalogFmSstvDecoder(11025);
        decoder.PushSamples(new float[64]);

        var tuning = decoder.PllTuningForTests;
        Assert.Equal(1.0, tuning.VcoGain);
        Assert.Equal(1, tuning.LoopOrder);
        Assert.Equal(1500, tuning.LoopCutoffHz);
        Assert.Equal(3, tuning.OutputOrder);
        Assert.Equal(900, tuning.OutputCutoffHz);
    }

    [Fact]
    public void RequestPllTuning_ConstructedWithNonDefaultTuning_StartsTracked()
    {
        // Proves the constructor-supplied values (the persisted-settings seed path, matching
        // RestartableSstvDecoder's own CreateInner re-seed) flow into the decoder-level tracking
        // fields, not just into _pllDemodulator's own constructor.
        var decoder = new AnalogFmSstvDecoder(11025, pllVcoGain: 3.5, pllLoopOrder: 9, pllLoopCutoffHz: 1100, pllOutputOrder: 11, pllOutputCutoffHz: 700);

        var tuning = decoder.PllTuningForTests;
        Assert.Equal(3.5, tuning.VcoGain);
        Assert.Equal(9, tuning.LoopOrder);
        Assert.Equal(1100, tuning.LoopCutoffHz);
        Assert.Equal(11, tuning.OutputOrder);
        Assert.Equal(700, tuning.OutputCutoffHz);
    }
}
