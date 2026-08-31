using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Restart-required-settings backlog item 4 (2026-08-27): <see cref="RestartableSstvEncoder"/> wraps
/// <see cref="AnalogFmSstvEncoder"/> with a live-swappable sample rate, gated by a reference-counted
/// <see cref="ISstvEncoderReconfiguration.BeginTransmission"/>/<see cref="ISstvEncoderReconfiguration.EndTransmission"/>
/// bracket (round-4 plan-review finding C3: a bool was insufficient -- a real transmission and the
/// RX-loopback self-test can have overlapping brackets).
/// </summary>
public class RestartableSstvEncoderTests
{
    [Fact]
    public void SampleRate_ReflectsConstructorValue()
    {
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        Assert.Equal(11025, encoder.SampleRate);
    }

    [Fact]
    public void RequestSampleRate_NoBracketOpen_AppliesImmediately()
    {
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var reconfig = Assert.IsAssignableFrom<ISstvEncoderReconfiguration>(encoder);

        reconfig.RequestSampleRate(8000);

        Assert.Equal(8000, encoder.SampleRate);
    }

    [Fact]
    public void RequestSampleRate_BracketOpen_Defers_AppliesOnlyOnceCountReachesZero()
    {
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var reconfig = Assert.IsAssignableFrom<ISstvEncoderReconfiguration>(encoder);

        reconfig.BeginTransmission();
        reconfig.RequestSampleRate(8000);

        Assert.Equal(11025, encoder.SampleRate); // still the old rate -- deferred

        reconfig.EndTransmission();

        Assert.Equal(8000, encoder.SampleRate); // applied now the bracket is closed
    }

    [Fact]
    public void RequestSampleRate_OverlappingBrackets_DefersUntilTheOUTERMOSTCloses()
    {
        // Round-4 finding C3's actual scenario: a real transmission's bracket opens, then (in the
        // window before it takes its OWN _transmitInFlight guard) the loopback self-test's bracket
        // also opens -- a bool-based "am I bracketed" would flip to false when the FIRST one closes,
        // even though the second is still open.
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var reconfig = Assert.IsAssignableFrom<ISstvEncoderReconfiguration>(encoder);

        reconfig.BeginTransmission(); // real transmission
        reconfig.BeginTransmission(); // overlapping self-test
        reconfig.RequestSampleRate(8000);

        reconfig.EndTransmission(); // real transmission finishes first
        Assert.Equal(11025, encoder.SampleRate); // still deferred -- self-test's bracket is still open

        reconfig.EndTransmission(); // self-test finishes
        Assert.Equal(8000, encoder.SampleRate); // now applied
    }

    [Fact]
    public void RequestSampleRate_EqualToCurrentValue_ClearsAnyPendingRequest()
    {
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var reconfig = Assert.IsAssignableFrom<ISstvEncoderReconfiguration>(encoder);

        reconfig.BeginTransmission();
        reconfig.RequestSampleRate(8000);
        reconfig.RequestSampleRate(11025); // reverts to the currently-committed value

        reconfig.EndTransmission();

        Assert.Equal(11025, encoder.SampleRate); // the 8000 request was cancelled, not re-applied
    }

    [Theory]
    [InlineData(4999)] // below SstvSampleRate.Minimum
    [InlineData(48501)] // above SstvSampleRate.Maximum
    public void RequestSampleRate_Unsupported_ThrowsImmediately_NoBracketStateChanged(int sampleRate)
    {
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var reconfig = Assert.IsAssignableFrom<ISstvEncoderReconfiguration>(encoder);

        Assert.Throws<ArgumentOutOfRangeException>(() => reconfig.RequestSampleRate(sampleRate));
        Assert.Equal(11025, encoder.SampleRate);
    }

    [Fact]
    public async Task EncodeAsync_DimensionMismatch_ThrowsSynchronouslyFromTheCall_NotDeferredToEnumeration()
    {
        // Round-5 go-ahead spec: the wrapper must stay a plain delegating method, not an iterator --
        // otherwise this throw would move to the first MoveNextAsync instead of the call itself.
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var mode = SstvModeRegistry.MartinM1;
        var wrongSizedImage = new ArrayImageSource(1, 1, new Rgb24[1]);

        var ex = Record.Exception(() => encoder.EncodeAsync(mode, wrongSizedImage));

        Assert.IsType<ArgumentException>(ex); // thrown from the call itself, not from awaiting/enumerating
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EncodeAsync_ProducesSameOutputAsDirectAnalogFmSstvEncoder_AtTheSameRate()
    {
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.MartinM1;
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var wrapped = new RestartableSstvEncoder(sampleRate);
        var direct = new AnalogFmSstvEncoder(sampleRate);

        var wrappedSamples = await CollectAsync(wrapped.EncodeAsync(mode, image));
        var directSamples = await CollectAsync(direct.EncodeAsync(mode, image));

        Assert.Equal(directSamples, wrappedSamples);
    }

    // T1-3 (production_audit.md): mirrors the two EncodeAsync tests immediately above -- same
    // "plain delegating method, not iterator" contract and same delegate-to-the-real-encoder
    // correctness, now also for EncodeBatchedAsync.
    [Fact]
    public async Task EncodeBatchedAsync_DimensionMismatch_ThrowsSynchronouslyFromTheCall_NotDeferredToEnumeration()
    {
        var encoder = new RestartableSstvEncoder(sampleRate: 11025);
        var mode = SstvModeRegistry.MartinM1;
        var wrongSizedImage = new ArrayImageSource(1, 1, new Rgb24[1]);

        var ex = Record.Exception(() => encoder.EncodeBatchedAsync(mode, wrongSizedImage));

        Assert.IsType<ArgumentException>(ex); // thrown from the call itself, not from awaiting/enumerating
        await Task.CompletedTask;
    }

    [Fact]
    public async Task EncodeBatchedAsync_ProducesSameOutputAsDirectAnalogFmSstvEncoder_AtTheSameRate()
    {
        const int sampleRate = 11025;
        var mode = SstvModeRegistry.MartinM1;
        var image = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var wrapped = new RestartableSstvEncoder(sampleRate);
        var direct = new AnalogFmSstvEncoder(sampleRate);

        var wrappedSamples = await CollectBatchedAsync(wrapped.EncodeBatchedAsync(mode, image));
        var directSamples = await CollectBatchedAsync(direct.EncodeBatchedAsync(mode, image));

        Assert.Equal(directSamples, wrappedSamples);
    }

    private static async Task<List<float>> CollectAsync(IAsyncEnumerable<float> samples)
    {
        var destination = new List<float>();
        await foreach (var sample in samples)
        {
            destination.Add(sample);
        }

        return destination;
    }

    private static async Task<List<float>> CollectBatchedAsync(IAsyncEnumerable<ReadOnlyMemory<float>> batches)
    {
        var destination = new List<float>();
        await foreach (var batch in batches)
        {
            destination.AddRange(batch.ToArray());
        }

        return destination;
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
