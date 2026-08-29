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

    // Options stub backlog item 1 (docs/plans/options-stub-item1-pll-tuning-plan.md). Round-1
    // plan-review blocker: applying a VcoGain multiplier to the VCO gain ALONE (not also the output
    // Hz-conversion step) makes every demodulated frequency wrong by a factor of VcoGain -- legacy's
    // own CPLL::SetVcoGain sets BOTH vco.SetGain AND m_outgain together (sstv.cpp:281-286), so the
    // two cancel at lock and the reported frequency is invariant to VcoGain, only loop DYNAMICS
    // change. Tone/gain combinations below are chosen to stay inside the +-1.5*bandwidthHz*vcoGain
    // capture window (bandwidth=800 here) -- a combination outside that window would fail even
    // against a CORRECT implementation, misreading as the bug still being present.
    [Theory]
    [InlineData(0.5, 1700.0)]
    [InlineData(0.5, 1900.0)]
    [InlineData(1.0, 1900.0)]
    [InlineData(1.0, 2300.0)]
    [InlineData(2.0, 1700.0)]
    [InlineData(2.0, 2300.0)]
    public void ProcessSample_DemodulatedFrequency_InvariantAcrossVcoGain(double vcoGain, double freqHz)
    {
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300, vcoGain);
        var samples = GenerateTone(freqHz, SampleRate, count: SampleRate);
        var mean = SettleAndReadMean(demod, samples, tailCount: 200);

        Assert.True(Math.Abs(mean - freqHz) < 5.0, $"vcoGain={vcoGain}, freq={freqHz}: settled mean {mean}");
    }

    /// <summary>Mutation guard for the exact blocker round-1 plan-review caught: reverting the fix to
    /// apply VcoGain to the VCO gain ONLY (not the Hz-conversion step too) must make this fail.
    /// Verified by hand -- temporarily changed ProcessSample's return to
    /// <c>_centerFrequencyHz - filteredOut * _bandwidthHz</c> (dropping the <c>* _vcoGain</c> factor),
    /// confirmed this test fails, restored.</summary>
    [Fact]
    public void ProcessSample_DemodulatedFrequency_InvariantAcrossVcoGain_MutationGuard()
    {
        var unityGain = new PllFmDemodulator(SampleRate, 1500, 2300, vcoGain: 1.0);
        var doubleGain = new PllFmDemodulator(SampleRate, 1500, 2300, vcoGain: 2.0);
        var samples = GenerateTone(2100.0, SampleRate, count: SampleRate);

        var unityMean = SettleAndReadMean(unityGain, samples, tailCount: 200);
        var doubleMean = SettleAndReadMean(doubleGain, GenerateTone(2100.0, SampleRate, count: SampleRate), tailCount: 200);

        Assert.True(Math.Abs(unityMean - doubleMean) < 5.0, $"unity={unityMean}, double={doubleMean} -- should be nearly identical, both ~2100 Hz");
    }

    /// <summary>Round-3 plan-review correction: <see cref="PllFmDemodulator.SetTuning"/> must push the
    /// new VcoGain into the VCO IMMEDIATELY (matching legacy's own synchronous <c>SetVcoGain</c>
    /// call), not just update a field and wait for the next <see cref="PllFmDemodulator.SetWidth"/> --
    /// a version that only updates the field breaks the g-cancellation the whole blocker fix depends
    /// on until SOME LATER SetWidth call happens to occur, which may never happen mid-reception. Round
    /// 1 code-review nit: 1900 Hz (the PLL's own center frequency) makes loopOut converge to exactly
    /// 0 at lock regardless of VcoGain, so a version of SetTuning that dropped the immediate
    /// _vco.SetGain call entirely would STILL pass at that frequency -- an off-center tone (2200 Hz)
    /// is required to actually exercise the fix.</summary>
    [Fact]
    public void SetTuning_VcoGainChange_WithNoInterveningSetWidth_StillReportsCorrectFrequency()
    {
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300, vcoGain: 1.0);
        var warmup = GenerateTone(2200.0, SampleRate, count: SampleRate);
        foreach (var s in warmup)
        {
            demod.ProcessSample(s);
        }

        // No SetWidth call between here and the tuning change below.
        demod.SetTuning(vcoGain: 3.0, loopOrder: 1, loopCutoffHz: 1500, outputOrder: 3, outputCutoffHz: 900);

        var afterRetune = GenerateTone(2200.0, SampleRate, count: SampleRate);
        var mean = SettleAndReadMean(demod, afterRetune, tailCount: 200);

        Assert.True(Math.Abs(mean - 2200.0) < 5.0, $"settled mean after live VcoGain change: {mean}");
    }

    /// <summary>Round-3 plan-review finding: legacy has no cutoff ceiling at all (`Option.cpp:517-524`,
    /// only `&gt; 0.0`) -- this port deliberately clamps below Nyquist, decoder-side, inside
    /// <see cref="PllFmDemodulator.SetTuning"/> AND its constructor (round-1 code-review finding --
    /// the constructor's own cutoff params are reachable, unclamped, from every production
    /// construction site, not just this method). Proven by requesting an absurdly high cutoff and
    /// confirming it behaves identically to requesting the expected-clamped ceiling directly, rather
    /// than destabilizing (<c>Math.Tan</c> at/above Nyquist). Round 1 code-review nit: 1900 Hz (the
    /// PLL's own center frequency) makes loopOut converge to 0 at lock regardless of whether the
    /// filter is even correctly designed at all, so an unclamped, genuinely-unstable filter could
    /// still coincidentally read ~1900 Hz for a zero-valued steady input -- an off-center tone
    /// (2200 Hz) forces a NONZERO loopOut through the filter, which only a correctly-clamped,
    /// correctly-functioning filter converges through accurately.</summary>
    [Fact]
    public void SetTuning_CutoffAboveNyquist_ClampsInsteadOfDestabilizing()
    {
        var clamped = new PllFmDemodulator(SampleRate, 1500, 2300);
        clamped.SetTuning(vcoGain: 1.0, loopOrder: 1, loopCutoffHz: SampleRate * 0.45, outputOrder: 3, outputCutoffHz: SampleRate * 0.45);

        var absurd = new PllFmDemodulator(SampleRate, 1500, 2300);
        absurd.SetTuning(vcoGain: 1.0, loopOrder: 1, loopCutoffHz: SampleRate * 5.0, outputOrder: 3, outputCutoffHz: SampleRate * 5.0);

        var samples = GenerateTone(2200.0, SampleRate, count: SampleRate);
        var clampedMean = SettleAndReadMean(clamped, samples, tailCount: 200);
        var absurdMean = SettleAndReadMean(absurd, GenerateTone(2200.0, SampleRate, count: SampleRate), tailCount: 200);

        Assert.False(double.IsNaN(absurdMean));
        Assert.False(double.IsInfinity(absurdMean));
        Assert.True(Math.Abs(clampedMean - absurdMean) < 5.0, $"clamped={clampedMean}, absurd={absurdMean} -- should behave identically, both clamped to the same ceiling");
    }

    /// <summary>Round-1 code-review finding: the Nyquist clamp used to live ONLY in
    /// <see cref="PllFmDemodulator.SetTuning"/>, not the constructor -- every production construction
    /// site (composition root, periodic decoder rebuild, a live sample-rate change's own rebuild, a
    /// freshly-started AVT training attempt) goes through the constructor, not SetTuning, so an
    /// unclamped constructor path meant a low sample rate + a stale-but-high persisted cutoff could
    /// produce a permanently NaN decoder that survived every restart with no error. Same
    /// off-center-tone reasoning as the SetTuning-side test above.</summary>
    [Fact]
    public void Constructor_CutoffAboveNyquist_ClampsInsteadOfDestabilizing()
    {
        var clamped = new PllFmDemodulator(SampleRate, 1500, 2300, loopCutoffHz: SampleRate * 0.45, outputCutoffHz: SampleRate * 0.45);
        var absurd = new PllFmDemodulator(SampleRate, 1500, 2300, loopCutoffHz: SampleRate * 5.0, outputCutoffHz: SampleRate * 5.0);

        var clampedMean = SettleAndReadMean(clamped, GenerateTone(2200.0, SampleRate, count: SampleRate), tailCount: 200);
        var absurdMean = SettleAndReadMean(absurd, GenerateTone(2200.0, SampleRate, count: SampleRate), tailCount: 200);

        Assert.False(double.IsNaN(absurdMean));
        Assert.False(double.IsInfinity(absurdMean));
        Assert.True(Math.Abs(clampedMean - absurdMean) < 5.0, $"clamped={clampedMean}, absurd={absurdMean} -- should behave identically, both clamped to the same ceiling");
    }

    /// <summary>Round-2 code-review finding: the constructor/SetTuning clamp originally guarded the
    /// CEILING only (<c>Math.Min</c>) -- the exact same failure class round 1 fixed for the ceiling
    /// side, just the floor: a zero or negative cutoff (reachable the identical way -- a hand-edited
    /// settings.json/preset) makes <c>Math.Tan</c>'s argument go negative, producing a divergent
    /// filter pole and a permanently NaN decoder surviving every restart. Legacy guards this
    /// `&gt; 0.0` at its own apply site too (`Option.cpp:517-518,523-524`).</summary>
    [Fact]
    public void Constructor_CutoffAtOrBelowZero_ClampsInsteadOfDestabilizing()
    {
        var clamped = new PllFmDemodulator(SampleRate, 1500, 2300, loopCutoffHz: 1.0, outputCutoffHz: 1.0);
        var negative = new PllFmDemodulator(SampleRate, 1500, 2300, loopCutoffHz: -1000.0, outputCutoffHz: -1000.0);

        var clampedMean = SettleAndReadMean(clamped, GenerateTone(2200.0, SampleRate, count: SampleRate), tailCount: 200);
        var negativeMean = SettleAndReadMean(negative, GenerateTone(2200.0, SampleRate, count: SampleRate), tailCount: 200);

        Assert.False(double.IsNaN(negativeMean));
        Assert.False(double.IsInfinity(negativeMean));
        Assert.True(Math.Abs(clampedMean - negativeMean) < 5.0, $"clamped={clampedMean}, negative={negativeMean} -- should behave identically, both clamped to the same floor");
    }

    /// <summary>Round-1 code-review finding: <c>IirFilter.Design</c>'s own <c>new double[order*3]</c>
    /// has no validation -- a negative order throws <see cref="OverflowException"/>. Proven at the
    /// constructor (reachable from every production construction site) and would previously have
    /// thrown before this fix. Round-2 code-review nit: order=0 gets its own behavioral assertion
    /// (clamped to 1, still demodulates correctly), not just "didn't throw" -- an unclamped order=0
    /// gives `new double[0]`, no exception, but silently disables the filter (a constant, wrong
    /// reading), which the other two InlineData cases' shared "no exception" assertion can't catch.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void Constructor_OutOfRangeFilterOrder_ClampsInsteadOfThrowing(int order)
    {
        var exception = Record.Exception(() => new PllFmDemodulator(SampleRate, 1500, 2300, loopOrder: order, outputOrder: order));
        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_ZeroFilterOrder_ClampsToOne_StillDemodulatesCorrectly()
    {
        var demod = new PllFmDemodulator(SampleRate, 1500, 2300, loopOrder: 0, outputOrder: 0);
        var mean = SettleAndReadMean(demod, GenerateTone(2200.0, SampleRate, count: SampleRate), tailCount: 200);

        Assert.True(Math.Abs(mean - 2200.0) < 5.0, $"order=0 should clamp to 1 and still lock correctly: settled mean {mean}");
    }
}
