namespace Yoniq.Core.Sstv.Tests;

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
                last = demod.ProcessSample(s);
            }

            Assert.True(Math.Abs(last - freq) < 2.0, $"sampleRate={sampleRate} freq={freq}: settled output {last}");
        }
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
            last = demod.ProcessSample(0.0);
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
            demod.ProcessSample(s);
        }

        var stepSamples = GenerateTone(1520.0, sampleRate, count: sampleRate / 10); // step to a nearby tone
        var settledIndex = -1;
        for (var i = 0; i < stepSamples.Length; i++)
        {
            var output = demod.ProcessSample(stepSamples[i]);
            if (settledIndex < 0 && Math.Abs(output - 1520.0) < 1.0)
            {
                settledIndex = i;
            }
        }

        Assert.True(settledIndex >= 0 && settledIndex <= maxSamplesToSettle, $"sampleRate={sampleRate}: settled at sample {settledIndex}");
    }
}
