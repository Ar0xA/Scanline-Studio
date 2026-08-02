namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="SearchBandpassFilter"/>, tested independently before any wiring
/// into <see cref="AnalogFmSstvDecoder"/>, per this project's chop-into-pieces methodology.
/// </summary>
public class SearchBandpassFilterTests
{
    // Independently computed (Python, NOT derived from or captured against the C# implementation --
    // same discipline as HilbertFmDemodulatorTests' own MakeHilbert fixtures, for the same reason:
    // a self-consistent transcription error wouldn't be caught by comparing code against itself).
    private static readonly double[] ExpectedH24At11025Hz =
    [
        -0.038096964571744996, -0.01678574993448184, 0.00778593709189614, -0.023322606600573854,
        -0.07827624688574886, -0.07206435465060877, -0.011669582976266994, -0.010844587542183507,
        -0.11127234745613829, -0.169945136839944, -0.025262005072730728, 0.2531541785449182,
        0.39689385795922316, 0.2531541785449182, -0.025262005072730728, -0.169945136839944,
        -0.11127234745613829, -0.010844587542183507, -0.011669582976266994, -0.07206435465060877,
        -0.07827624688574886, -0.023322606600573854, 0.00778593709189614, -0.01678574993448184,
        -0.038096964571744996,
    ];

    // H1 (Band-1 item 4b, locked/normal variant, 1100-2600Hz) -- independently computed the same way,
    // same discipline as ExpectedH24At11025Hz above.
    private static readonly double[] ExpectedH1_24At11025Hz =
    [
        -0.05096687968271917, -0.0345552361313546, 0.02657058339308721, 0.048251208785099066,
        0.012623798255956765, 0.006520677333613534, 0.06098040885923243, 0.06010707964532414,
        -0.07896923273412564, -0.21446854318153516, -0.12973139123562366, 0.13741684586246364,
        0.2869316211841456, 0.13741684586246364, -0.12973139123562366, -0.21446854318153516,
        -0.07896923273412564, 0.06010707964532414, 0.06098040885923243, 0.006520677333613534,
        0.012623798255956765, 0.048251208785099066, 0.02657058339308721, -0.0345552361313546,
        -0.05096687968271917,
    ];

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_Tap24At11025Hz()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 400.0, 2500.0);

        Assert.Equal(ExpectedH24At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH24At11025Hz[i]) < 1e-12, $"index {i}: expected {ExpectedH24At11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_H1_Tap24At11025Hz()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 1100.0, 2600.0);

        Assert.Equal(ExpectedH1_24At11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedH1_24At11025Hz[i]) < 1e-12, $"index {i}: expected {ExpectedH1_24At11025Hz[i]}, got {h[i]}");
        }
    }

    [Theory]
    [InlineData(0, -0.009694264305853777)]
    [InlineData(1, -0.009375920786516312)]
    [InlineData(24, -0.002969475993237702)]
    [InlineData(48, 0.10099476437764031)]
    [InlineData(72, -0.002969475993237702)]
    [InlineData(95, -0.009375920786516312)]
    [InlineData(96, -0.009694264305853777)]
    public void MakeFilter_SelectedValues_MatchIndependentlyComputedFixture_Tap96At44100Hz(int index, double expected)
    {
        var h = SearchBandpassFilter.MakeFilter(96, 44100.0, 400.0, 2500.0);

        Assert.True(Math.Abs(h[index] - expected) < 1e-12, $"index {index}: expected {expected}, got {h[index]}");
    }

    [Theory]
    [InlineData(0, -0.012515335642196635)]
    [InlineData(1, -0.013149626347786018)]
    [InlineData(24, 0.014974239922528401)]
    [InlineData(48, 0.07045841471626878)]
    [InlineData(72, 0.014974239922528401)]
    [InlineData(95, -0.013149626347786018)]
    [InlineData(96, -0.012515335642196635)]
    public void MakeFilter_SelectedValues_MatchIndependentlyComputedFixture_H1_Tap96At44100Hz(int index, double expected)
    {
        var h = SearchBandpassFilter.MakeFilter(96, 44100.0, 1100.0, 2600.0);

        Assert.True(Math.Abs(h[index] - expected) < 1e-12, $"index {index}: expected {expected}, got {h[index]}");
    }

    [Fact]
    public void MakeFilter_ArrayLength_IsTapPlusOne()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 400.0, 2500.0);

        Assert.Equal(25, h.Length);
    }

    [Theory]
    [InlineData(24, 11025.0, 400.0, 2500.0)]
    [InlineData(96, 44100.0, 400.0, 2500.0)]
    [InlineData(24, 11025.0, 1100.0, 2600.0)]
    [InlineData(96, 44100.0, 1100.0, 2600.0)]
    public void MakeFilter_IsSymmetric_ForEvenTap(int tap, double sampleRate, double fcl, double fch)
    {
        // Round-1 correction: symmetry is only structurally guaranteed for EVEN tap (this port's only
        // reachable tap counts are both even) -- see class doc comment for the odd-tap trailing-zero
        // case this deliberately does NOT claim to hold.
        var h = SearchBandpassFilter.MakeFilter(tap, sampleRate, fcl, fch);

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(h[tap - n] - h[n]) < 1e-12, $"n={n}: H[{tap - n}]={h[tap - n]}, H[{n}]={h[n]} -- not symmetric");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessSample_ImpulseResponse_IsCausal_NotCentered(bool useLocked)
    {
        // Round-1 finding, the single most important test in this file: the convolution window is
        // CAUSAL -- H[0] pairs with the NEWEST sample -- not centered. A centered window would pass
        // every other test in this file identically while silently introducing a tap/2-sample anchor
        // error. Feed a unit impulse, then zeros: output[n] must equal h[n] exactly (H[0] on the
        // impulse's own call, H[tap] tap calls later), confirming H[0] pairs with the sample just fed
        // in, not tap/2 samples in the past or future. Parametrized over both H1/H2 (Band-1 item 4b) --
        // both share the SAME delay line, so this same causal-addressing property must hold for either
        // coefficient table.
        const int tap = 24;
        var h = SearchBandpassFilter.MakeFilter(tap, 11025.0, useLocked ? 1100.0 : 400.0, useLocked ? 2600.0 : 2500.0);
        var filter = new SearchBandpassFilter(11025);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0, useLocked); // impulse
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0, useLocked); // then zeros
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - h[n]) < 1e-12, $"useLocked={useLocked}, n={n}: expected h[{n}]={h[n]}, got {outputs[n]}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProcessSample_BeforeStreamStart_IsZeroPadded(bool useLocked)
    {
        // The delay line starts zero-initialized (matching CFIR2::Create's own zero-memset delay line,
        // confirmed never reset mid-stream on RX) -- the very first ProcessSample call sees only the
        // one real input sample, with every "prior" delay-line slot still at its zero-init value.
        var filter = new SearchBandpassFilter(11025);

        var output = filter.ProcessSample(5.0, useLocked);
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, useLocked ? 1100.0 : 400.0, useLocked ? 2600.0 : 2500.0);
        Assert.True(Math.Abs(output - h[0] * 5.0) < 1e-9, $"useLocked={useLocked}: expected {h[0] * 5.0}, got {output}");
    }

    [Fact]
    public void ProcessSample_SwitchingToLocked_ReusesExistingDelayLineHistory_NoSeparateWarmUp()
    {
        // Band-1 item 4b's central design claim, verified directly rather than just asserted in a
        // comment: legacy's CFIR2::Do(d, hp) maintains ONE shared delay line and picks which
        // coefficient table to dot-product against per call -- it is NOT two independent filter
        // objects, so switching to H1 mid-stream needs no separate warm-up. Proof: feed several
        // H2-selected samples (building up real delay-line history), then switch to H1 for one sample
        // -- the result must match independently convolving H1's OWN coefficients against that SAME
        // accumulated history (computed here by hand from the raw inputs, not by calling the filter
        // again), not a fresh/zeroed delay line's worth of H1 output.
        const int tap = 24;
        var rawInputs = new double[] { 3.0, -1.5, 2.25, 0.5, -4.0, 1.0, 0.75 }; // arbitrary, fewer than tap+1
        var filter = new SearchBandpassFilter(11025);

        foreach (var input in rawInputs)
        {
            filter.ProcessSample(input, useLocked: false); // H2, building up shared delay-line history
        }

        var switchInput = 2.0;
        var actual = filter.ProcessSample(switchInput, useLocked: true); // switch to H1 for this one sample

        // Hand-reconstruct the shared delay line's contents as of this call: newest-first, switchInput
        // then rawInputs reversed, zero-padded to tap+1 -- matches CFIR2::Do's own addressing exactly.
        var expectedDelayLine = new double[tap + 1];
        expectedDelayLine[0] = switchInput;
        for (var i = 0; i < rawInputs.Length; i++)
        {
            expectedDelayLine[i + 1] = rawInputs[rawInputs.Length - 1 - i];
        }

        var h1 = SearchBandpassFilter.MakeFilter(tap, 11025.0, 1100.0, 2600.0);
        var expected = 0.0;
        for (var i = 0; i <= tap; i++)
        {
            expected += expectedDelayLine[i] * h1[i];
        }

        Assert.True(Math.Abs(actual - expected) < 1e-9, $"expected {expected} (H1 against the H2-built-up shared history), got {actual}");
    }

    [Theory]
    [InlineData(100.0, 0.12949)]
    [InlineData(400.0, 0.58250)]
    [InlineData(1100.0, 1.03998)]
    [InlineData(1200.0, 1.03892)]
    [InlineData(1900.0, 1.06642)]
    [InlineData(2500.0, 0.54012)]
    [InlineData(3000.0, 0.09498)]
    [InlineData(4000.0, 0.00241)]
    public void FrequencyResponse_MatchesIndependentlyComputedMagnitude(double toneHz, double expectedGain)
    {
        // Independently computed (Python DFT-style summation over the SAME independently-computed
        // coefficients used above, not derived from the C# convolution implementation) -- confirms the
        // filter actually behaves as a bandpass (near-unity in-band, real attenuation out-of-band),
        // not just "matches the coefficient formula" in isolation. 400-2500Hz is the passband; 1100/
        // 1200/1900Hz are the real VIS-bit/sync/leader tones this port needs to pass through.
        const int sampleRate = 11025;
        const double amplitude = 1000.0;
        var filter = new SearchBandpassFilter(sampleRate);

        // Long enough for the FIR's own causal window to fully fill with steady-tone content (tap=24
        // samples) plus a full cycle margin at the lowest tested frequency, so the measured peak is
        // genuine steady-state, not a startup transient.
        var sampleCount = sampleRate; // 1 second, generous
        var settleSamples = 100; // comfortably past the tap=24 window fill
        var peak = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            var input = amplitude * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            var output = Math.Abs(filter.ProcessSample(input, useLocked: false));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        var measuredGain = peak / amplitude;
        Assert.True(Math.Abs(measuredGain - expectedGain) < 0.02, $"toneHz={toneHz}: expected gain {expectedGain}, measured {measuredGain}");
    }

    [Theory]
    [InlineData(100.0, 0.01420)]
    [InlineData(700.0, 0.07779)]
    [InlineData(1100.0, 0.51134)]
    [InlineData(1200.0, 0.77769)]
    [InlineData(1900.0, 0.98048)]
    [InlineData(2600.0, 0.53444)]
    [InlineData(3200.0, 0.00611)]
    [InlineData(4000.0, 0.01027)]
    public void FrequencyResponse_MatchesIndependentlyComputedMagnitude_H1(double toneHz, double expectedGain)
    {
        // Same discipline as the H2 test above, for H1's own passband (1100-2600Hz). 1200/1900Hz are
        // the same real VIS-bit/sync/leader tones; 1100/2600Hz are H1's own passband edges (H2's are
        // 400/2500Hz -- deliberately different, not a copy-paste of the same edge frequencies).
        const int sampleRate = 11025;
        const double amplitude = 1000.0;
        var filter = new SearchBandpassFilter(sampleRate);

        var sampleCount = sampleRate;
        var settleSamples = 100;
        var peak = 0.0;
        for (var i = 0; i < sampleCount; i++)
        {
            var input = amplitude * Math.Sin(2 * Math.PI * toneHz * i / sampleRate);
            var output = Math.Abs(filter.ProcessSample(input, useLocked: true));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        var measuredGain = peak / amplitude;
        Assert.True(Math.Abs(measuredGain - expectedGain) < 0.02, $"toneHz={toneHz}: expected gain {expectedGain}, measured {measuredGain}");
    }
}
