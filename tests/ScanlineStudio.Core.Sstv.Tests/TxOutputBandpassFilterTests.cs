using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Isolated unit tests for <see cref="TxOutputBandpassFilter"/> (ultracode audit finding #26),
/// mirroring <see cref="SearchBandpassFilterTests"/>'s discipline. Coefficient fixtures below are
/// independently computed (Python, translated directly from `fir.cpp`'s C++ source, NOT from this
/// class's own C# implementation) using the SAME truncated <c>I0</c> series legacy uses
/// (`fir.cpp:310-325`'s convergence break, not an exact modified-Bessel-function implementation) --
/// this specific choice matters: at `att=40` the truncated series leaves ~1e-11 absolute error,
/// which is why the fixture tolerance below is legitimately 1e-12 (matching legacy's own real
/// precision) rather than something looser.
/// </summary>
public class TxOutputBandpassFilterTests
{
    private static readonly double[] ExpectedHAt11025Hz =
    [
        0.005121186920306622, -9.635223207208536e-05, 0.00501168075315226, 0.020242741914524496,
        0.004966550424810503, -0.03625007531197903, -0.03057832195094131, 0.00402197655345707,
        -0.061575607172844, -0.18887417819596716, -0.11829767693960513, 0.19457268023171742,
        0.3847367638875464, 0.19457268023171742, -0.11829767693960513, -0.18887417819596716,
        -0.061575607172844, 0.00402197655345707, -0.03057832195094131, -0.03625007531197903,
        0.004966550424810503, 0.020242741914524496, 0.00501168075315226, -9.635223207208536e-05,
        0.005121186920306622,
    ];

    private static readonly double[] ExpectedHAt44100Hz =
    [
        -0.011324848781340687, -0.017853364603688658, -0.0234706603508339, -0.025884171401804525,
        -0.0227231746710419, -0.012176183539530637, 0.00638515115467641, 0.03198254329856529,
        0.06196060426754986, 0.09233623811078069, 0.11852006751649873, 0.13625226729440798,
        0.14252455891409896, 0.13625226729440798, 0.11852006751649873, 0.09233623811078069,
        0.06196060426754986, 0.03198254329856529, 0.00638515115467641, -0.012176183539530637,
        -0.0227231746710419, -0.025884171401804525, -0.0234706603508339, -0.017853364603688658,
        -0.011324848781340687,
    ];

    private static readonly double[] ExpectedHAt48000Hz =
    [
        -0.011574374223509562, -0.01686521521452919, -0.02042599281076546, -0.02028835149333552,
        -0.01464183010787147, -0.002325000107809045, 0.016748278923696354, 0.0413511985651465,
        0.06898282852509045, 0.09620703831259109, 0.11923859733355249, 0.13465571282004032,
        0.14007872301622942, 0.13465571282004032, 0.11923859733355249, 0.09620703831259109,
        0.06898282852509045, 0.0413511985651465, 0.016748278923696354, -0.002325000107809045,
        -0.01464183010787147, -0.02028835149333552, -0.02042599281076546, -0.01686521521452919,
        -0.011574374223509562,
    ];

    [Theory]
    [InlineData(11025.0)]
    [InlineData(44100.0)]
    [InlineData(48000.0)]
    public void MakeFilter_TapCountAndLength_IsFixed24_RegardlessOfSampleRate(double sampleRate)
    {
        // ultracode audit finding #26 (B2): unlike SearchBandpassFilter's RX-side tap scaling
        // (fs*24/11025), legacy's TX tap count (sstv.cpp:2764) is hardcoded at 24 for every sample
        // rate -- a regression to sample-rate-scaled tap count would give 96 taps at 44100Hz.
        var h = TxOutputBandpassFilter.MakeFilter(sampleRate);

        Assert.Equal(25, h.Length);
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_At11025Hz()
    {
        var h = TxOutputBandpassFilter.MakeFilter(11025.0);

        Assert.Equal(ExpectedHAt11025Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedHAt11025Hz[i]) < 1e-12, $"index {i}: expected {ExpectedHAt11025Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_At44100Hz()
    {
        var h = TxOutputBandpassFilter.MakeFilter(44100.0);

        Assert.Equal(ExpectedHAt44100Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedHAt44100Hz[i]) < 1e-12, $"index {i}: expected {ExpectedHAt44100Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_FullArray_MatchesIndependentlyComputedFixture_At48000Hz()
    {
        var h = TxOutputBandpassFilter.MakeFilter(48000.0);

        Assert.Equal(ExpectedHAt48000Hz.Length, h.Length);
        for (var i = 0; i < h.Length; i++)
        {
            Assert.True(Math.Abs(h[i] - ExpectedHAt48000Hz[i]) < 1e-12, $"index {i}: expected {ExpectedHAt48000Hz[i]}, got {h[i]}");
        }
    }

    [Fact]
    public void MakeFilter_IsSymmetric_ForEvenTap()
    {
        // Kaiser windowing preserves the same even-tap symmetry SearchBandpassFilter's rectangular
        // window has -- this filter's only reachable tap count (24) is even.
        var h = TxOutputBandpassFilter.MakeFilter(11025.0);

        for (var n = 0; n <= 24; n++)
        {
            Assert.True(Math.Abs(h[24 - n] - h[n]) < 1e-12, $"n={n}: H[{24 - n}]={h[24 - n]}, H[{n}]={h[n]} -- not symmetric");
        }
    }

    [Fact]
    public void ProcessSample_ImpulseResponse_IsCausal_NotCentered_AndGroupDelayIs12SamplesAt11025Hz()
    {
        // Same causal-not-centered property SearchBandpassFilter's own test pins (H[0] pairs with the
        // NEWEST sample) -- also directly demonstrates the 12-sample (Tap/2) group delay: the peak
        // (center) coefficient H[12] lands 12 calls after the impulse.
        const int tap = 24;
        var h = TxOutputBandpassFilter.MakeFilter(11025.0);
        var filter = new TxOutputBandpassFilter(11025.0);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0);
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0);
        }

        for (var n = 0; n <= tap; n++)
        {
            Assert.True(Math.Abs(outputs[n] - h[n]) < 1e-12, $"n={n}: expected h[{n}]={h[n]}, got {outputs[n]}");
        }

        var peakIndex = Array.IndexOf(outputs, outputs.Max());
        Assert.Equal(12, peakIndex);
    }

    [Fact]
    public void ProcessSample_ImpulseResponse_GroupDelayIs12SamplesAt44100Hz()
    {
        // ultracode audit finding #26 (B2): pinning group delay at a SECOND sample rate specifically
        // catches a regression to sample-rate-scaled tap count -- a scaled 96-tap filter would show a
        // 48-sample delay here instead of the fixed 12.
        const int tap = 24;
        var filter = new TxOutputBandpassFilter(44100.0);

        var outputs = new double[tap + 1];
        outputs[0] = filter.ProcessSample(1.0);
        for (var n = 1; n <= tap; n++)
        {
            outputs[n] = filter.ProcessSample(0.0);
        }

        var peakIndex = Array.IndexOf(outputs, outputs.Max());
        Assert.Equal(12, peakIndex);
    }

    [Theory]
    [InlineData(100.0, 0.010302790123725203)]
    [InlineData(700.0, 0.5004810679530551)]
    [InlineData(1200.0, 1.0002073485920964)]
    [InlineData(1900.0, 1.008843134154891)]
    [InlineData(2300.0, 0.9947457581665641)]
    [InlineData(2800.0, 0.5080428268322147)]
    [InlineData(4000.0, 0.0011970085190151608)]
    [InlineData(5000.0, 0.005819501808594065)]
    public void FrequencyResponse_MatchesIndependentlyComputedMagnitude_At11025HzOnly(double toneHz, double expectedGain)
    {
        // ultracode audit finding #26: this passband/stopband check is SCOPED TO 11025Hz ONLY,
        // deliberately -- at 44100/48000Hz a 24-tap/att=40 Kaiser design's transition width
        // (~4.1kHz at 44100Hz) is wider than the entire 700-2800Hz passband, so there is effectively
        // no measurable stopband at those rates (confirmed by the coefficient fixtures above: their
        // DC-ish response is far from zero, unlike the 11025Hz case). That's legacy's own real
        // behavior at this tap count, not a porting error -- asserting a stopband at 44100Hz here
        // would fail for a CORRECT implementation and reward "fixing" it by scaling taps, which
        // regresses straight back to finding #26's B2 bug. 1200/1900/2300Hz are real SSTV tones
        // (VIS/sync/narrow); 100/4000/5000Hz are clearly out-of-band; 700/2800 are the passband edges
        // (~-6dB, as expected).
        const int sampleRate = 11025;
        const double amplitude = 1000.0;
        var filter = new TxOutputBandpassFilter(sampleRate);

        var sampleCount = sampleRate;
        const int settleSamples = 100;
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

    [Fact]
    public async Task AnalogFmSstvEncoder_SameInstance_TwoSequentialEncodes_ProduceBitIdenticalOutput()
    {
        // ultracode audit finding #26 (B3): AnalogFmSstvEncoder is registered as a DI singleton
        // (Program.cs) -- the bandpass filter must be constructed LOCALLY per EncodeAsyncCore call,
        // not held as a constructor field, or its delay line would carry the first call's tail state
        // into the second call. None of the tests above can catch this (they all construct their own
        // TxOutputBandpassFilter directly) -- this is the one test that actually pins B3: a ctor-field
        // filter would diverge for roughly the first 24 samples of the SECOND call (its delay line
        // still holds real content from the first call's tail), while a correctly-local filter gives
        // bit-identical output both times, since it starts from a fresh zeroed delay line either way.
        var mode = SstvModeRegistry.Robot36;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(100, 150, 200));
        var image = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        var encoder = new AnalogFmSstvEncoder(11025); // ONE instance, reused for both calls

        var firstRun = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            firstRun.Add(sample);
        }

        var secondRun = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image))
        {
            secondRun.Add(sample);
        }

        Assert.Equal(firstRun.Count, secondRun.Count);
        for (var i = 0; i < firstRun.Count; i++)
        {
            Assert.Equal(firstRun[i], secondRun[i]);
        }
    }
}
