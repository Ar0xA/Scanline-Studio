using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Sound-file station ID (`docs/plans/sound-file-id-plan.md`'s variant A'): <see cref="StationIdTransmitOptions.SoundFileSamples"/>
/// is appended as a plain second loop AFTER <c>GenerateFrequencySegments</c>'s whole tone stream, in
/// both <see cref="AnalogFmSstvEncoder.EncodeAsync"/> and <see cref="AnalogFmSstvEncoder.EstimateSampleCount"/>
/// -- not interleaved into the segment stream at all. Mirrors
/// <c>AnalogFmSstvEncoderStationIdWiringTests</c>'s own CW-ID test shapes.
/// </summary>
public class AnalogFmSstvEncoderSoundFileIdTests
{
    private const int SampleRate = 11025;

    [Fact]
    public async Task EncodeAsync_SoundFileIdEnabled_AppendsExactSamplesAfterFooter_LeavesEverythingBeforeItUnchanged()
    {
        // txBpfEnabled: false isolates this feature's own new code from the (separately tested)
        // bandpass filter -- with the filter off, the raw samples pass straight through unmodified,
        // so an exact tail comparison is meaningful, not just "non-silent."
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        float[] rawSamples = [0.25f, -0.5f, 0.75f, -1.0f, 0.0f];

        var withoutSoundFile = await CollectAsync(encoder.EncodeAsync(mode, image, txBpfEnabled: false));
        var withSoundFile = await CollectAsync(encoder.EncodeAsync(
            mode, image, new StationIdTransmitOptions { SoundFileSamples = rawSamples }, txBpfEnabled: false));

        Assert.Equal(withoutSoundFile.Count + rawSamples.Length, withSoundFile.Count);
        Assert.Equal(withoutSoundFile, withSoundFile.Take(withoutSoundFile.Count));
        Assert.Equal(rawSamples, withSoundFile.Skip(withoutSoundFile.Count));
    }

    [Fact]
    public async Task EncodeAsync_SoundFileIdEnabled_BandpassFilterStillRunsOverRawSamples()
    {
        // Legacy's row-playback branch shares the output BPF with tone generation (sstv.cpp:2914) --
        // the one stage the two paths actually have in common. With the filter ON (default), a
        // sequence of large amplitude jumps should NOT survive completely unfiltered.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        float[] rawSamples = [1.0f, -1.0f, 1.0f, -1.0f, 1.0f, -1.0f, 1.0f, -1.0f];

        var withoutSoundFile = await CollectAsync(encoder.EncodeAsync(mode, image));
        var withSoundFile = await CollectAsync(
            encoder.EncodeAsync(mode, image, new StationIdTransmitOptions { SoundFileSamples = rawSamples }));

        var tail = withSoundFile.Skip(withoutSoundFile.Count).ToArray();
        Assert.Equal(rawSamples.Length, tail.Length);
        Assert.NotEqual(rawSamples, tail);
    }

    [Fact]
    public async Task EncodeAsync_CwAndSoundFileBothSet_CwWinsExclusivity()
    {
        // Main.cpp:7021-7025: sys.m_CWID is a single-value tri-state -- CW (==1) and sound-file
        // (==2) can never both be configured by the settings resolution path, but this type is
        // public and a caller COULD construct both fields set. The encoder-side guard must make CW
        // win deterministically, matching legacy's own `if(==1) ... else if(==2)` precedence.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var cwOnly = new StationIdTransmitOptions { CwEnabled = true, CwResolvedText = "TEST", CwToneFrequencyHz = 1000, CwWpm = 28 };
        var cwAndSoundFile = cwOnly with { SoundFileSamples = new float[] { 0.5f, -0.5f, 0.5f, -0.5f } };

        var withCwOnly = await CollectAsync(encoder.EncodeAsync(mode, image, cwOnly));
        var withBothSet = await CollectAsync(encoder.EncodeAsync(mode, image, cwAndSoundFile));

        Assert.Equal(withCwOnly, withBothSet);
    }

    [Fact]
    public void EstimateSampleCount_SoundFileIdEnabled_AddsRawSampleCountToBaseline()
    {
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        float[] rawSamples = [0.1f, 0.2f, 0.3f];

        var baseline = encoder.EstimateSampleCount(mode, image);
        var withSoundFile = encoder.EstimateSampleCount(mode, image, new StationIdTransmitOptions { SoundFileSamples = rawSamples });

        Assert.Equal(baseline + rawSamples.Length, withSoundFile);
    }

    [Fact]
    public void EstimateSampleCount_CwAndSoundFileBothSet_MatchesCwOnlyEstimate_AgreeingWithEncodeAsync()
    {
        // The two-sites-must-agree requirement (plan-review round 2, blocker): EstimateSampleCount
        // must reach the SAME "CW wins" conclusion EncodeAsync's own exclusivity test above does --
        // a guard written correctly at only one of the two sites would let the TX-progress estimate
        // silently disagree with what EncodeAsync actually emits.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);
        var cwOnly = new StationIdTransmitOptions { CwEnabled = true, CwResolvedText = "TEST", CwToneFrequencyHz = 1000, CwWpm = 28 };
        var cwAndSoundFile = cwOnly with { SoundFileSamples = new float[] { 0.5f, -0.5f, 0.5f, -0.5f } };

        var cwOnlyEstimate = encoder.EstimateSampleCount(mode, image, cwOnly);
        var bothSetEstimate = encoder.EstimateSampleCount(mode, image, cwAndSoundFile);

        Assert.Equal(cwOnlyEstimate, bothSetEstimate);
    }

    private static async Task<List<float>> CollectAsync(IAsyncEnumerable<float> samples)
    {
        var list = new List<float>();
        await foreach (var sample in samples)
        {
            list.Add(sample);
        }

        return list;
    }

    private static ArrayImageSource CreateSolidImage(int width, int height) =>
        new(width, height, Enumerable.Repeat(new Rgb24(128, 64, 200), width * height).ToArray());
}
