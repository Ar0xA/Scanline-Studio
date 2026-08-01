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

    [Fact]
    public void MakeFilter_ArrayLength_IsTapPlusOne()
    {
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 400.0, 2500.0);

        Assert.Equal(25, h.Length);
    }

    [Theory]
    [InlineData(24, 11025.0)]
    [InlineData(96, 44100.0)]
    public void MakeFilter_IsSymmetric_ForEvenTap(int tap, double sampleRate)
    {
        // Round-1 correction: symmetry is only structurally guaranteed for EVEN tap (this port's only
        // reachable tap counts are both even) -- see class doc comment for the odd-tap trailing-zero
        // case this deliberately does NOT claim to hold.
        var h = SearchBandpassFilter.MakeFilter(tap, sampleRate, 400.0, 2500.0);

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(h[tap - n] - h[n]) < 1e-12, $"n={n}: H[{tap - n}]={h[tap - n]}, H[{n}]={h[n]} -- not symmetric");
        }
    }

    [Fact]
    public void ProcessSample_ImpulseResponse_IsCausal_NotCentered()
    {
        // Round-1 finding, the single most important test in this file: the convolution window is
        // CAUSAL -- H[0] pairs with the NEWEST sample -- not centered. A centered window would pass
        // every other test in this file identically while silently introducing a tap/2-sample anchor
        // error. Feed a unit impulse, then zeros: output[n] must equal h[n] exactly (H[0] on the
        // impulse's own call, H[tap] tap calls later), confirming H[0] pairs with the sample just fed
        // in, not tap/2 samples in the past or future.
        const int tap = 24;
        var h = SearchBandpassFilter.MakeFilter(tap, 11025.0, 400.0, 2500.0);
        var filter = new SearchBandpassFilter(11025);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0); // impulse
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0); // then zeros
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - h[n]) < 1e-12, $"n={n}: expected h[{n}]={h[n]}, got {outputs[n]}");
        }
    }

    [Fact]
    public void ProcessSample_BeforeStreamStart_IsZeroPadded()
    {
        // The delay line starts zero-initialized (matching CFIR2::Create's own zero-memset delay line,
        // confirmed never reset mid-stream on RX) -- the very first ProcessSample call sees only the
        // one real input sample, with every "prior" delay-line slot still at its zero-init value.
        var filter = new SearchBandpassFilter(11025);

        var output = filter.ProcessSample(5.0);
        var h = SearchBandpassFilter.MakeFilter(24, 11025.0, 400.0, 2500.0);
        Assert.True(Math.Abs(output - h[0] * 5.0) < 1e-9, $"expected {h[0] * 5.0}, got {output}");
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
            var output = Math.Abs(filter.ProcessSample(input));
            if (i >= settleSamples && output > peak)
            {
                peak = output;
            }
        }

        var measuredGain = peak / amplitude;
        Assert.True(Math.Abs(measuredGain - expectedGain) < 0.02, $"toneHz={toneHz}: expected gain {expectedGain}, measured {measuredGain}");
    }
}
