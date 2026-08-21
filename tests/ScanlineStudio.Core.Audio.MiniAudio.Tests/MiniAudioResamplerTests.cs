namespace ScanlineStudio.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Isolated tests for <see cref="MiniAudioResampler"/>'s own boundary/guard logic -- device-free (no
/// real audio hardware involved, same reason <see cref="ResamplerQualityRoundTripTests"/> always runs
/// in CI), so these always run too. Fills a coverage gap flagged by Tier A Batch 9 chunk 9c
/// (docs/functional-audit-playbook.md): before this file, neither <see cref="MiniAudioResampler"/>'s
/// documented overflow guard nor its rate-validation guard had any test at all.
/// </summary>
public class MiniAudioResamplerTests
{
    [Fact]
    public void Resample_OutputWouldExceedIntMaxValue_ThrowsBeforeAllocating()
    {
        // Closes the coverage gap the auditor called "the most actionable item in this chunk":
        // the overflow guard (MiniAudioResampler.cs) itself had zero test coverage. A tiny input
        // upsampled by a factor of 1,000,000 comfortably exceeds int.MaxValue frames of expected
        // output without needing a genuinely huge input array.
        var input = new float[3000];

        var ex = Assert.Throws<ArgumentException>(() => MiniAudioResampler.Resample(input, sampleRateIn: 1, sampleRateOut: 1_000_000));
        Assert.Equal("input", ex.ParamName);
    }

    [Theory]
    [InlineData(0, 44100)]
    [InlineData(44100, 0)]
    [InlineData(-1, 44100)]
    [InlineData(44100, -1)]
    public void Resample_NonPositiveSampleRate_ThrowsBeforeArithmeticCanWrapNegative(int sampleRateIn, int sampleRateOut)
    {
        // Closes a real gap the auditor's chunk 9c round found: the overflow guard above only ever
        // caught an OVER-large output -- a non-positive rate makes the internal capacity arithmetic
        // go negative, which used to pass that guard silently and would have thrown an opaque
        // exception from `new float[negativeCapacity]` instead of this explicit, actionable one.
        var input = new float[16];

        Assert.Throws<ArgumentException>(() => MiniAudioResampler.Resample(input, sampleRateIn, sampleRateOut));
    }
}
