namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="HilbertFmDemodulator"/>, its <c>MakeHilbert</c> coefficient
/// generator, its <c>DoFir</c> tap-delay line, and its <c>ComputePhaseDifference</c> lag logic --
/// each tested independently before any wiring into <see cref="AnalogFmSstvDecoder"/>, per this
/// project's chop-into-pieces methodology.
/// </summary>
public class HilbertFmDemodulatorTests
{
    // Independently computed (Python, NOT derived from or captured against the C# implementation --
    // see spec/14-roadmap.md's plan discussion for why this matters: DoFir's reversed-kernel indexing
    // and MakeHilbert's antisymmetry can compound into a silent, self-consistent sign error if the test
    // fixture is captured from the same code it's meant to check). Full array for tap=12/11025Hz.
    private static readonly double[] ExpectedH12At11025Hz =
    [
        -5.551115123125784e-18, -0.017305513434848763, -0.0, -0.11292081654269369,
        2.137179322403426e-17, -0.5964161068150665, -0.0, 0.5964161068150666,
        -2.137179322403427e-17, 0.11292081654269372, -0.0, 0.017305513434848777,
        5.551115123125784e-18,
    ];

    [Fact]
    public void MakeHilbert_FullArray_MatchesIndependentlyComputedFixture_Tap12At11025Hz()
    {
        var h = HilbertFmDemodulator.MakeHilbert(12, 11025.0, 100.0, 11025.0 / 2.0 - 100.0);

        Assert.Equal(ExpectedH12At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH12At11025Hz[i]) < 1e-12, $"index {i}: expected {ExpectedH12At11025Hz[i]}, got {h[i]}");
        }
    }

    [Theory]
    [InlineData(0, 2.6367796834847472e-18)]
    [InlineData(1, -0.0021996293851929893)]
    [InlineData(12, 9.367506770274759e-18)]
    [InlineData(47, 0.0021996293851929893)]
    [InlineData(48, -2.6367796834847472e-18)]
    public void MakeHilbert_SelectedValues_MatchIndependentlyComputedFixture_Tap48At44100Hz(int index, double expected)
    {
        var h = HilbertFmDemodulator.MakeHilbert(48, 44100.0, 100.0, 44100.0 / 2.0 - 100.0);

        Assert.True(Math.Abs(h[index] - expected) < 1e-12, $"index {index}: expected {expected}, got {h[index]}");
    }

    [Theory]
    [InlineData(12, 11025.0)]
    [InlineData(24, 22050.0)]
    [InlineData(48, 44100.0)]
    public void MakeHilbert_CenterTap_IsZero(int tap, double sampleRate)
    {
        var h = HilbertFmDemodulator.MakeHilbert(tap, sampleRate, 100.0, sampleRate / 2.0 - 100.0);

        Assert.Equal(0.0, h[tap / 2], precision: 15);
    }

    [Theory]
    [InlineData(12, 11025.0)]
    [InlineData(24, 22050.0)]
    [InlineData(48, 44100.0)]
    public void MakeHilbert_IsAntisymmetricAboutCenterTap(int tap, double sampleRate)
    {
        var h = HilbertFmDemodulator.MakeHilbert(tap, sampleRate, 100.0, sampleRate / 2.0 - 100.0);

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(h[tap - n] + h[n]) < 1e-12, $"n={n}: H[{tap - n}]={h[tap - n]}, H[{n}]={h[n]} -- not antisymmetric");
        }
    }

    [Fact]
    public void MakeHilbert_ArrayLength_IsTapPlusOne_NotTap()
    {
        // Round-2 correction: sstv.h declares Z[HILLTAP+1]/H[HILLTAP+1], both legacy loops use
        // inclusive <= bounds -- 13 coefficients at tap=12, not 12.
        var h = HilbertFmDemodulator.MakeHilbert(12, 11025.0, 100.0, 11025.0 / 2.0 - 100.0);

        Assert.Equal(13, h.Length);
    }

    [Fact]
    public void DoFir_ImpulseResponse_IsReversedCoefficientArray()
    {
        // Round-1 correction: DoFir's newest sample lands at the LAST buffer index, so H[0] pairs
        // with the OLDEST sample -- a unit impulse fed through, followed by zeros, must read back the
        // coefficient array in REVERSE order, not forward.
        const int tap = 5;
        var h = new double[] { 1, 2, 3, 4, 5, 6 }; // tap+1 = 6 coefficients
        var z = new double[tap + 1];

        var outputs = new double[tap + 1];
        outputs[0] = HilbertFmDemodulator.DoFir(h, z, 1.0, tap); // impulse
        for (var i = 1; i <= tap; i++)
        {
            outputs[i] = HilbertFmDemodulator.DoFir(h, z, 0.0, tap); // then zeros
        }

        // After the impulse, as zeros flush it out, the impulse walks from the newest position
        // (index tap) down to index 0 -- so DoFir returns h[tap], h[tap-1], ..., h[0] in that order
        // (the FIRST call, with the impulse itself, returns h[tap] since the impulse just landed at
        // the last index and every other slot is still zero from construction).
        var expectedReversed = new double[] { 6, 5, 4, 3, 2, 1 };
        Assert.Equal(expectedReversed, outputs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ComputePhaseDifference_WarmUp_MatchesExpectedLag(int df)
    {
        // lag = 2^df: df=0 -> 1-sample lag (from call 2); df=1 -> 2-sample lag, steady from call 3
        // (2 warm-up calls); df=2 -> 4-sample lag, steady from call 5 (4 warm-up calls). Confirmed by
        // hand-simulation and independently re-confirmed by auditor review, see class doc comment.
        var a = new double[4];
        var phases = new double[] { 0.1, 0.3, 0.7, 1.2, 1.9, 2.5, 3.0 };
        var diffs = new double[phases.Length];
        for (var i = 0; i < phases.Length; i++)
        {
            diffs[i] = HilbertFmDemodulator.ComputePhaseDifference(phases[i], a, df);
        }

        var lag = 1 << df; // 2^df
        for (var i = lag; i < phases.Length; i++)
        {
            Assert.Equal(phases[i] - phases[i - lag], diffs[i], precision: 12);
        }
    }

    [Fact]
    public void ComputePhaseDifference_Df0_FirstCallDiffsAgainstZero()
    {
        var a = new double[4];
        var diff = HilbertFmDemodulator.ComputePhaseDifference(1.234, a, 0);

        Assert.Equal(1.234, diff, precision: 12); // a[0] starts at 0.0
    }

    private static double[] GenerateTone(double frequencyHz, double sampleRate, int count, double amplitude = 20000.0)
    {
        var samples = new double[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate);
        }

        return samples;
    }

    [Theory]
    [InlineData(11025)]
    [InlineData(44100)]
    public void SettledOutput_SteadyTone_ReadsBackCorrectFrequency(int sampleRate)
    {
        // 1500Hz and 2300Hz (not just the center 1900Hz) -- round-1 finding: a center-frequency-only
        // test cannot catch a sign inversion, since 1900Hz maps to the same scaled-domain zero
        // regardless of sign.
        foreach (var freq in new[] { 1500.0, 1900.0, 2300.0 })
        {
            var demod = new HilbertFmDemodulator(sampleRate);
            var samples = GenerateTone(freq, sampleRate, count: sampleRate); // 1 full second, generous settle
            double last = 0;
            foreach (var s in samples)
            {
                last = demod.ProcessSample(s, isNarrow: false);
            }

            Assert.True(Math.Abs(last - freq) < 2.0, $"sampleRate={sampleRate} freq={freq}: settled output {last}");
        }
    }

    [Fact]
    public void Constructor_MiddleDecimationTier_16To40kHz_SelectsTap24Df1_AndDecodesCorrectly()
    {
        // S30 (spec/14-roadmap.md): CHILL::SetWidth's middle decimation tier (16-40kHz -> 24 taps,
        // m_df=1, sstv.cpp:3032-3047) is implemented in this class's own constructor (the
        // `sampleRate >= 16000` branch) but, until now, never exercised by any test at the INSTANCE
        // level -- MakeHilbert_SelectedValues_MatchIndependentlyComputedFixture_Tap48At44100Hz and its
        // siblings above exercise the raw tap=24 coefficient generator at 22050Hz already (via
        // MakeHilbert's own [InlineData(24, 22050.0)] cases), but every actual HilbertFmDemodulator
        // instance constructed anywhere else in this test file (and in AnalogFmSstvDecoder) uses only
        // 11025Hz (tap=12/df=0, the low tier) or 44100Hz (tap=48/df=2, the high tier) -- the middle
        // tier's own tierMultiplier=2.0 wiring (_offWide/_outWide/_offNarrow/_outNarrow) had no
        // end-to-end proof it actually decodes correctly, only that its raw filter kernel is right in
        // isolation. 22050Hz sits inside [16000, 40000), selecting tap=24/df=1.
        const int sampleRate = 22050;

        Assert.Equal(12, new HilbertFmDemodulator(sampleRate).HalfTap); // tap=24/2 -- directly pins tier selection, not just its downstream effect

        foreach (var freq in new[] { 1500.0, 1900.0, 2300.0 })
        {
            var demod = new HilbertFmDemodulator(sampleRate);
            var samples = GenerateTone(freq, sampleRate, count: sampleRate); // 1 full second, generous settle
            double last = 0;
            foreach (var s in samples)
            {
                last = demod.ProcessSample(s, isNarrow: false);
            }

            Assert.True(Math.Abs(last - freq) < 2.0, $"middle tier (22050Hz) freq={freq}: settled output {last}");
        }
    }

    [Theory]
    [InlineData(11025)]
    [InlineData(44100)]
    public void ProcessSample_IsNarrowSelection_IsRepresentationallyInert_AtSteadyState(int sampleRate)
    {
        // Band-2 item S6 -- auditor-confirmed algebraic finding: the off/out encode and the return
        // statement's descale are exact algebraic inverses for ANY (centerHz, bandwidthHz) pair, so
        // isNarrow has NO effect on the settled Hz readout in THIS PORT'S representation (legacy
        // itself never converts to Hz -- CHILL::Do returns the scaled domain value directly, so ITS
        // m_OFF/m_OUT genuinely matter there; this port's Hz conversion is its own representational
        // choice, and that conversion is exactly what makes the selection cancel here). An earlier
        // version of this test asserted a specific narrow-range frequency reads back correctly, which
        // turned out to be tautological -- it would have passed with isNarrow silently ignored too,
        // since the SAME pre-existing (pre-S6) test at 2300Hz already proved this cancellation held.
        // This pins the real, executable fact instead: if a future representation change (e.g. a
        // scaled/integer domain) makes isNarrow start mattering at steady state, THIS is the test that
        // should start failing, prompting a fresh look at S6.
        foreach (var freq in new[] { 1900.0, 2172.0 })
        {
            var wideDemod = new HilbertFmDemodulator(sampleRate);
            var narrowDemod = new HilbertFmDemodulator(sampleRate);
            var samples = GenerateTone(freq, sampleRate, count: sampleRate);

            double lastWide = 0;
            double lastNarrow = 0;
            foreach (var s in samples)
            {
                lastWide = wideDemod.ProcessSample(s, isNarrow: false);
                lastNarrow = narrowDemod.ProcessSample(s, isNarrow: true);
            }

            Assert.True(
                Math.Abs(lastWide - lastNarrow) < 1e-6,
                $"sampleRate={sampleRate} freq={freq}: wide={lastWide} narrow={lastNarrow} -- expected representationally identical settled output");
        }
    }

    [Theory]
    [InlineData(11025, 30)] // measured resettle at 22 samples; generous margin above SettlingTime_AfterFrequencyStep's own ~16-sample prediction
    [InlineData(44100, 90)] // margin above its own ~62-sample prediction, same proportion as the 11025Hz tier
    public void ProcessSample_IsNarrowFlipMidStream_CausesBoundedTransient_ThenResettlesToSameValue(int sampleRate, int maxSamplesToResettle)
    {
        // The one genuinely new, real behavior Band-2 item S6 introduces: the smoothing IIR's stored
        // state is in the OLD scale at the moment isNarrow flips (the phase-history register `_a` is
        // structurally immune -- raw atan2 phases, off is added strictly after the phase difference --
        // but the smoothing filter is not). Legacy has this identical transient (CHILL::SetWidth does
        // no Clear()/reset of m_iir) and does nothing to compensate it, so this port doesn't either.
        // Pins both halves: a real deviation happens right at the flip, and it's bounded/fast (not
        // open-ended), resettling to the SAME value the invariance test above proves isNarrow doesn't
        // otherwise affect.
        const double freq = 1900.0;
        var demod = new HilbertFmDemodulator(sampleRate);
        var warmup = GenerateTone(freq, sampleRate, count: sampleRate);
        double settledBeforeFlip = 0;
        foreach (var s in warmup)
        {
            settledBeforeFlip = demod.ProcessSample(s, isNarrow: false);
        }

        var afterFlip = GenerateTone(freq, sampleRate, count: sampleRate / 10);
        var immediateOutput = demod.ProcessSample(afterFlip[0], isNarrow: true);
        Assert.True(
            Math.Abs(immediateOutput - settledBeforeFlip) > 1.0,
            $"sampleRate={sampleRate}: expected a real transient immediately after the isNarrow flip, but output barely moved ({immediateOutput} vs {settledBeforeFlip})");

        var resettledIndex = -1;
        for (var i = 1; i < afterFlip.Length; i++)
        {
            var output = demod.ProcessSample(afterFlip[i], isNarrow: true);
            if (Math.Abs(output - settledBeforeFlip) < 1.0)
            {
                resettledIndex = i;
                break;
            }
        }

        Assert.True(resettledIndex >= 0 && resettledIndex <= maxSamplesToResettle, $"sampleRate={sampleRate}: resettled at sample {resettledIndex}");
    }

    [Fact]
    public void ProcessSample_ExactZeroRealComponent_UsesPhaseZero_NotAtan2()
    {
        // Round-1 correction: a==0.0 is a real, reachable, meaningful state (phase=0.0 rad used
        // directly), not a stale/skip case. Feed digital silence (all zeros) -- the delayed real
        // component stays exactly 0.0 throughout, so phase is pinned at 0.0 rad every call (never
        // atan2'd) and never changes -- diff stays exactly 0 (no oscillation), which correctly reads
        // out as 0Hz, NOT the center frequency: a discriminator's whole job is measuring
        // sample-to-sample phase CHANGE, and a non-oscillating signal genuinely has no instantaneous
        // frequency to report. (The center-frequency-as-zero-state fact is about the SMOOTHING
        // FILTER's own cold-start value, a separate concept from what a silent/DC input demodulates
        // to -- conflating the two was an error in an earlier draft of this test, not in the class.)
        var demod = new HilbertFmDemodulator(11025);
        double last = 0;
        for (var i = 0; i < 11025; i++)
        {
            last = demod.ProcessSample(0.0, isNarrow: false);
        }

        Assert.False(double.IsNaN(last));
        Assert.True(Math.Abs(last - 0.0) < 2.0, $"settled output on digital silence: {last}");
    }

    [Theory]
    [InlineData(11025, 100)] // ~16 samples predicted (13 FIR + 3 IIR) -- generous margin
    [InlineData(44100, 200)] // ~62 samples predicted (49 FIR + 14 IIR) -- generous margin
    public void SettlingTime_AfterFrequencyStep_IsBoundedAndFast(int sampleRate, int maxSamplesToSettle)
    {
        // Regression check against the numbers already logged in spec/14-roadmap.md's Hilbert scoping
        // pass (measured via a throwaway diagnostic against the isolated IirFilter stage only) -- now
        // measured against the REAL, assembled class. Not a strict pass/fail on the exact predicted
        // count (this is an end-to-end measurement, not the isolated IIR-only one the scoping pass
        // used), just confirms the transient stays bounded and fast, not PLL-like (30+ samples,
        // open-ended).
        var demod = new HilbertFmDemodulator(sampleRate);
        var warmupSamples = GenerateTone(1500.0, sampleRate, count: sampleRate / 2);
        foreach (var s in warmupSamples)
        {
            demod.ProcessSample(s, isNarrow: false);
        }

        var stepSamples = GenerateTone(1520.0, sampleRate, count: sampleRate / 10); // step to a nearby tone
        var settledIndex = -1;
        for (var i = 0; i < stepSamples.Length; i++)
        {
            var output = demod.ProcessSample(stepSamples[i], isNarrow: false);
            if (settledIndex < 0 && Math.Abs(output - 1520.0) < 1.0)
            {
                settledIndex = i;
            }
        }

        Assert.True(settledIndex >= 0 && settledIndex <= maxSamplesToSettle, $"sampleRate={sampleRate}: settled at sample {settledIndex}");
    }
}
