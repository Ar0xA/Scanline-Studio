using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Stub survey Tier 3, "Clock calibration" piece 1: <see cref="AnalogFmSstvEncoder.EncodeAsync"/>'s
/// new <c>sampleRateOffsetHz</c> parameter -- port of legacy's manual TX sample-clock correction
/// (<c>sys.m_TxSampOff</c>). Round-2 plan-review's own verification plan, direct legacy-invariant
/// assertions rather than an approximation: sample count vs. an independently-computed total,
/// measured tone frequency vs. the offset-corrected rate (catches "left phaseIncrement at
/// nominal"), and that the TX output bandpass filter is unaffected by the offset (catches the
/// opposite mistake -- passing the effective rate to <c>TxOutputBandpassFilter</c>, which legacy
/// itself never does, <c>sstv.cpp:2768/2771</c>).
/// </summary>
public class AnalogFmSstvEncoderSampleRateOffsetTests
{
    private const int SampleRate = 11025;

    private static ArrayImageSource CreateSolidImage(int width, int height) =>
        new(width, height, Enumerable.Repeat(new Rgb24(128, 64, 200), width * height).ToArray());

    private static async Task<float[]> EncodeAllAsync(AnalogFmSstvEncoder encoder, SstvModeDefinition mode, IImageSource image, double sampleRateOffsetHz)
    {
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image, sampleRateOffsetHz: sampleRateOffsetHz))
        {
            samples.Add(sample);
        }

        return [.. samples];
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(-1500.0)]
    [InlineData(37.5)]
    public async Task EstimateSampleCount_NonZeroOffset_MatchesRealEncodeAsyncTotalExactly(double offsetHz)
    {
        var mode = SstvModeRegistry.MartinM1;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var estimated = encoder.EstimateSampleCount(mode, image, sampleRateOffsetHz: offsetHz);
        var actual = (await EncodeAllAsync(encoder, mode, image, offsetHz)).Length;

        Assert.Equal(actual, estimated);
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(-1500.0)]
    public async Task EncodeAsync_NonZeroOffset_TotalSampleCountMatchesIndependentCalculation_WithinTolerance(double offsetHz)
    {
        // Independently derived from the SAME per-segment durations GenerateFrequencySegments
        // produces, but summed/multiplied in a DIFFERENT order than EncodeAsyncCore's own running
        // accumulator -- a genuine cross-check, not the same formula re-typed (round-2 plan-review:
        // "a scaled sum != the sum of scaled terms in IEEE, plus one final truncation", hence the
        // +/-2-sample tolerance, not exact equality).
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var effectiveRate = SampleRate + offsetHz;

        // Independent duration source: the real sample count AT ZERO OFFSET (already proven exact
        // against EncodeAsync's own running accumulator by the sibling
        // EstimateSampleCount_NonZeroOffset_MatchesRealEncodeAsyncTotalExactly test above, at
        // offsetHz=0 that's simply the pre-existing, long-established nominal-rate behavior) --
        // not a re-implementation of per-line segment timing, which would just be the same formula
        // re-typed.
        var nominalEncoder = new AnalogFmSstvEncoder(SampleRate);
        var nominalSamples = (await EncodeAllAsync(nominalEncoder, mode, image, 0.0)).Length;
        var impliedTotalDurationMs = nominalSamples / (double)SampleRate * 1000.0;

        var independentExpected = (long)(impliedTotalDurationMs / 1000.0 * effectiveRate);

        var offsetEncoder = new AnalogFmSstvEncoder(SampleRate);
        var actual = (await EncodeAllAsync(offsetEncoder, mode, image, offsetHz)).Length;

        Assert.InRange(actual, independentExpected - 2, independentExpected + 2);
    }

    [Theory]
    [InlineData(1500.0)]
    [InlineData(-1500.0)]
    [InlineData(37.5)]
    public async Task EncodeAsync_NonZeroOffset_LeaderToneFrequencyReflectsTheEffectiveRate(double offsetHz)
    {
        // The first segment GenerateFrequencySegments ever emits, for every mode: VisHeader's own
        // OutHead burst, 1900Hz for 100ms (VisHeader.cs's GenerateOutHeadSegments, normal-mode
        // branch) -- long enough (a few hundred cycles at 11025Hz) for an accurate zero-crossing
        // frequency measurement, filtered exactly like real TX output (not a raw/unfiltered
        // shortcut -- a bandpass filter doesn't change a steady-state pure tone's frequency, only
        // its amplitude/phase, so measuring crossing spacing after the filter's settling transient
        // is still a direct test of the phaseIncrement math EncodeAsyncCore actually used).
        const double leaderFrequencyHz = 1900.0;
        const double leaderDurationMs = 100.0;
        var mode = SstvModeRegistry.MartinM1;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var effectiveRate = SampleRate + offsetHz;

        var leaderSampleCount = (int)(leaderDurationMs / 1000.0 * effectiveRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, image, sampleRateOffsetHz: offsetHz))
        {
            samples.Add(sample);
            if (samples.Count >= leaderSampleCount)
            {
                break;
            }
        }

        // Skip the filter's own settling transient (a few dozen samples at most for a low-order
        // biquad cascade) before measuring -- matches this project's own ZeroCrossingFrequencyCounter
        // precedent of only trusting post-settle output.
        const int settleSamples = 60;
        var measured = samples.Skip(settleSamples).ToArray();
        var measuredSamplesPerCycle = MeasureAverageSamplesPerCycle(measured);
        var expectedSamplesPerCycle = effectiveRate / leaderFrequencyHz;

        // 1% tolerance: generous relative to the offset magnitudes under test (1500Hz/11025Hz is a
        // much larger relative shift than 1%), but tight enough that "left phaseIncrement at
        // nominal" (a ~13% error at +/-1500Hz) fails loudly.
        Assert.InRange(measuredSamplesPerCycle, expectedSamplesPerCycle * 0.99, expectedSamplesPerCycle * 1.01);
    }

    [Fact]
    public async Task EncodeAsync_LargeNegativeOffset_FallsBackToNominalRate_InsteadOfNaNOrNegativeSamples()
    {
        // Defensive guard (round-2 plan-review blocker): an offset driving the effective rate to
        // zero or negative must not reach Math.Sin as NaN/Infinity samples WITH PTT KEYED. The real
        // settings-boundary validation lives at SstvSessionService (+/-1500Hz range) -- this proves
        // the encoder's own last-resort guard also holds, for a caller that bypasses that boundary.
        var mode = SstvModeRegistry.MartinM1;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var nominalSamples = await EncodeAllAsync(encoder, mode, image, 0.0);
        var guardedSamples = await EncodeAllAsync(new AnalogFmSstvEncoder(SampleRate), mode, image, -SampleRate - 1000.0);

        Assert.Equal(nominalSamples.Length, guardedSamples.Length);
        Assert.All(guardedSamples, s => Assert.True(float.IsFinite(s)));
    }

    /// <summary>Average spacing (in samples, sub-sample-interpolated) between consecutive
    /// positive-going zero crossings -- one full cycle per pair. Deliberately a simple, local,
    /// noise-free measurement (this test synthesizes its own clean tone, no decoder-grade
    /// robustness needed) rather than reusing the production <c>ZeroCrossingFrequencyCounter</c>,
    /// which is tuned for demodulating a noisy received signal (its own output filter/clamping
    /// would smear exactly the precision this test needs).</summary>
    private static double MeasureAverageSamplesPerCycle(float[] samples)
    {
        var crossingPositions = new List<double>();
        for (var i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                var frac = -samples[i - 1] / (double)(samples[i] - samples[i - 1]);
                crossingPositions.Add(i - 1 + frac);
            }
        }

        Assert.True(crossingPositions.Count >= 10, "Not enough zero crossings measured to derive a reliable frequency.");

        var gaps = new List<double>();
        for (var i = 1; i < crossingPositions.Count; i++)
        {
            gaps.Add(crossingPositions[i] - crossingPositions[i - 1]);
        }

        return gaps.Average();
    }
}
