using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class WaterfallSourceTests
{
    [Fact]
    public void PushSamples_FewerThanOneWindow_EmitsNoFrame()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        source.PushSamples(new float[32]);

        Assert.Empty(frames);
    }

    [Fact]
    public void PushSamples_ExactlyOneWindow_EmitsExactlyOneFrame()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        source.PushSamples(new float[64]);

        Assert.Single(frames);
        Assert.Equal(32, frames[0].MagnitudesDb.Count);
    }

    [Fact]
    public void PushSamples_AcrossMultipleSmallChunks_StillFillsWindowAndEmits()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        for (var i = 0; i < 8; i++)
        {
            source.PushSamples(new float[8]);
        }

        Assert.Single(frames);
    }

    [Fact]
    public void PushSamples_LongerThanOneWindow_EmitsFramesAtHopSpacing()
    {
        using var source = new WaterfallSource(sampleRate: 8000, windowSize: 64, hopSize: 32);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        // 64 (first frame) + 3*32 (three more hops) = 160 samples -> frames at 64, 96, 128, 160.
        source.PushSamples(new float[160]);

        Assert.Equal(4, frames.Count);
    }

    [Fact]
    public void PushSamples_PureToneAtKnownFrequency_PeaksAtExpectedBin()
    {
        const int sampleRate = 8000;
        const int windowSize = 64;
        const int targetBin = 8; // 8 * (8000/64) = 1000 Hz
        using var source = new WaterfallSource(sampleRate, windowSize);
        var frames = new List<WaterfallFrame>();
        source.Frames.Subscribe(frames.Add);

        var samples = new float[windowSize];
        for (var i = 0; i < windowSize; i++)
        {
            samples[i] = MathF.Sin(2f * MathF.PI * targetBin * i / windowSize);
        }

        source.PushSamples(samples);

        Assert.Single(frames);
        var magnitudes = frames[0].MagnitudesDb;
        var peakBin = 0;
        for (var k = 1; k < magnitudes.Count; k++)
        {
            if (magnitudes[k] > magnitudes[peakBin])
            {
                peakBin = k;
            }
        }

        Assert.Equal(targetBin, peakBin);
        Assert.Equal((double)sampleRate / windowSize, frames[0].BinWidthHz, precision: 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveSampleRate_Throws(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaterfallSource(sampleRate));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)] // not a power of two
    [InlineData(-8)]
    public void Constructor_InvalidWindowSize_Throws(int windowSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaterfallSource(sampleRate: 8000, windowSize: windowSize));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(128)] // larger than windowSize (64)
    public void Constructor_InvalidHopSize_Throws(int hopSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WaterfallSource(sampleRate: 8000, windowSize: 64, hopSize: hopSize));
    }
}
