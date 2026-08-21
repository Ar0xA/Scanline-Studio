namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="PllFmDemodulator"/>, including its new (Phase 1 of the
/// demod-type runtime-dispatch plan) <see cref="PllFmDemodulator.SetWidth"/> narrow-mode retune
/// method -- each tested independently before any wiring into <see cref="AnalogFmSstvDecoder"/>, per
/// this project's chop-into-pieces methodology. Existing coverage
/// (<see cref="PllScaleBridgeTests"/>, <see cref="AvtTrainingLockStateMachineTests"/>) exercises this
/// class only through a full encode/decode round trip or AVT's own state machine -- neither is a
/// substitute for isolated per-class coverage of <c>SetWidth</c> itself, which no test touched
/// before this file.
/// </summary>
public class PllFmDemodulatorTests
{
    private const int SampleRate = 11025;

    private static double[] GenerateTone(double frequencyHz, double sampleRate, int count)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            // Scale bridge -- see PllFmDemodulator's own doc comment: callers must scale by 32768.0
            // before calling ProcessSample, same convention AvtTrainingLockStateMachineTests already
            // uses for this exact class.
            samples[i] = Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate) * 32768.0;
        }

        return samples;
    }

    /// <summary>Runs every sample through <paramref name="demod"/> and returns the MEAN of the last
    /// <paramref name="tailCount"/> outputs, not just the single final sample. Round-1 code-review
    /// finding: a locked type-1 PLL has zero steady-state FREQUENCY error -- a single-sample readout
    /// can land anywhere within the loop's residual 2f ripple, which is a measurement artifact of
    /// which instant you happen to sample, not a real per-frequency bias. Averaging separates the
    /// two, letting every frequency (including the former "edge droop" case) use one tight
    /// tolerance instead of a loosened, unexplained one for edge frequencies only.</summary>
    private static double SettleAndReadMean(PllFmDemodulator demod, double[] samples, int tailCount)
    {
        double sum = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var output = demod.ProcessSample(samples[i]);
            if (i >= samples.Length - tailCount)
            {
                sum += output;
            }
        }

        return sum / tailCount;
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(1900.0)]
    [InlineData(2300.0)]
    public void ProcessSample_WideBandSteadyTone_SettlesToCorrectFrequency(double freqHz)
    {
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300);
        var samples = GenerateTone(freqHz, SampleRate, count: SampleRate); // 1 full second, generous settle
        var mean = SettleAndReadMean(demod, samples, tailCount: 200);

        Assert.True(Math.Abs(mean - freqHz) < 5.0, $"freq={freqHz}: settled mean {mean}");
    }

    [Theory]
    [InlineData(2044.0)]
    [InlineData(2172.0)]
    [InlineData(2300.0)]
    public void ProcessSample_NarrowBandSteadyTone_AfterSetWidth_SettlesToCorrectFrequency(double freqHz)
    {
        // Constructed with the same wide (1500,2300) ctor params the main picture path and AVT both
        // use -- SetWidth(true) is the only thing that should make narrow-band tones read back
        // correctly, not the constructor.
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300);
        demod.SetWidth(isNarrow: true);

        var samples = GenerateTone(freqHz, SampleRate, count: SampleRate);
        var mean = SettleAndReadMean(demod, samples, tailCount: 200);

        Assert.True(Math.Abs(mean - freqHz) < 5.0, $"narrow freq={freqHz}: settled mean {mean}");
    }

    [Fact]
    public void SetWidth_MidStream_CausesBoundedTransient_ThenResettlesToNarrowBandValue()
    {
        // Proves SetWidth retunes in place rather than requiring a fresh instance: a real transient
        // is expected right at the flip (center/bandwidth genuinely changed), but it must be bounded
        // -- not an open-ended re-acquisition from cold, which is what losing loop-filter/VCO-phase
        // continuity would look like. Mirrors HilbertFmDemodulator's own analogous
        // ProcessSample_IsNarrowFlipMidStream test shape.
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300);
        var warmup = GenerateTone(1900.0, SampleRate, count: SampleRate);
        foreach (var s in warmup)
        {
            demod.ProcessSample(s);
        }

        demod.SetWidth(isNarrow: true);
        var afterFlip = GenerateTone(2172.0, SampleRate, count: SampleRate / 2);

        var resettledIndex = -1;
        for (var i = 0; i < afterFlip.Length; i++)
        {
            var output = demod.ProcessSample(afterFlip[i]);
            if (Math.Abs(output - 2172.0) < 5.0)
            {
                resettledIndex = i;
                break;
            }
        }

        Assert.True(resettledIndex >= 0 && resettledIndex <= 2000, $"resettled at sample {resettledIndex}");
    }

    [Fact]
    public void SetWidth_BackToWide_SettlesToWideBandValue()
    {
        // The reverse direction of the transition (narrow -> wide) -- e.g. EndOfImage resetting back
        // to wide for the next scan -- must work too, not just wide -> narrow.
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300);
        demod.SetWidth(isNarrow: true);
        var narrowWarmup = GenerateTone(2172.0, SampleRate, count: SampleRate);
        foreach (var s in narrowWarmup)
        {
            demod.ProcessSample(s);
        }

        demod.SetWidth(isNarrow: false);
        var wideSamples = GenerateTone(1900.0, SampleRate, count: SampleRate);
        double last = 0;
        foreach (var s in wideSamples)
        {
            last = demod.ProcessSample(s);
        }

        Assert.True(Math.Abs(last - 1900.0) < 5.0, $"settled output after reverting to wide: {last}");
    }

    [Fact]
    public void SetWidth_RedundantCallMidStream_ProducesBitIdenticalOutput()
    {
        // Round-1 code-review finding: the two transient tests above allow up to a second to
        // resettle, so a SetWidth that actually RESET loop/output filter Z-state, _err, or the AGC
        // window (instead of retuning in place) would still pass them -- neither one actually proves
        // state is preserved, only that the class eventually reacquires either way. This is the real
        // discriminator: a no-op SetWidth call (same width as already active) recomputes IDENTICAL
        // center/bandwidth/VCO values and touches nothing else, so calling it mid-stream must
        // produce a BIT-IDENTICAL output sequence to never calling it at all. Any accidental state
        // reset breaks this immediately, unlike the bounded-transient tests.
        var withRedundantCall = new PllFmDemodulator(SampleRate, 1500, 2300);
        var withoutCall = new PllFmDemodulator(SampleRate, 1500, 2300);
        var samples = GenerateTone(1900.0, SampleRate, count: 1000);

        for (var i = 0; i < samples.Length; i++)
        {
            if (i == 500)
            {
                withRedundantCall.SetWidth(isNarrow: false);
            }

            var withCall = withRedundantCall.ProcessSample(samples[i]);
            var without = withoutCall.ProcessSample(samples[i]);

            // Exact equality, not a tolerance -- the recomputation is provably deterministic (same
            // expression, same operands, IEEE-754), so exact equality is the strict discriminator
            // this test is meant to be (round-2 code-review finding: precision:12 was weaker than
            // necessary and didn't match the "BIT-IDENTICAL" claim above).
            Assert.Equal(without, withCall);
        }
    }

    [Fact]
    public void ProcessSample_DigitalSilence_NeverProducesNaNOrThrows()
    {
        // Batch 7 chunk 7c correction: all-zero input never actually reaches the AGC's own
        // `5.0/agcRange` division in the first place -- `_prevInput` starts at (and stays) 0.0, so
        // the `input >= 0 && _prevInput < 0` zero-crossing condition never fires for silence. This
        // test proves real silence-robustness (no NaN/Infinity leaks through the filters either
        // way), but NOT the AGC floor mechanism -- see
        // ProcessSample_SubFloorAlternatingSignal_AgcPinsAtFloor_NoDivideByZero below for that.
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300);
        double last = 0;
        for (var i = 0; i < SampleRate; i++)
        {
            last = demod.ProcessSample(0.0);
        }

        Assert.False(double.IsNaN(last));
        Assert.False(double.IsInfinity(last));
    }

    [Fact]
    public void ProcessSample_SubFloorAlternatingSignal_AgcPinsAtFloor_NoDivideByZero()
    {
        // Closes a coverage gap flagged by Tier A Batch 7 chunk 7c (docs/functional-audit-playbook.md):
        // the digital-silence test above never actually exercises the `5.0 / (_max - _min)` AGC
        // division, since all-zero input never crosses zero. A tiny alternating signal DOES cross
        // zero every sample while staying inside legacy's own +-1.0 window floor (sstv.cpp:325-326),
        // so agcRange == 2.0 exactly and _agc settles at 5.0/2.0 = 2.5 -- CPLL::Do's own identical
        // below-floor behavior, not a port-specific quirk (see this class's own doc comment on the
        // int16-domain calling convention every real call site honors).
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300);
        double last = 0;
        for (var i = 0; i < SampleRate; i++)
        {
            last = demod.ProcessSample(i % 2 == 0 ? 1e-6 : -1e-6);
        }

        Assert.False(double.IsNaN(last));
        Assert.False(double.IsInfinity(last));
    }
}
