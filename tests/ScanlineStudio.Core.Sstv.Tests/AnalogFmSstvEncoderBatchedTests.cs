using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// T1-3 (production_audit.md): <see cref="AnalogFmSstvEncoder.EncodeAsync"/> is DERIVED from
/// <see cref="AnalogFmSstvEncoder.EncodeBatchedAsync"/> (via <c>FlattenBatches</c> over
/// <c>EncodeBatchedAsyncCore</c> -- see those methods' own doc comments), not a second, independent
/// implementation -- so the two cannot diverge in the actual DSP synthesis by construction; every
/// existing golden-vector/round-trip test in this project already covers that shared core through
/// <see cref="AnalogFmSstvEncoder.EncodeAsync"/>. Auditor code-review correction (2026-08-31): this
/// file's own equality test below therefore does NOT guard against a straddle/flush bug in the
/// shared core (both sides would be identically wrong) -- what it DOES guard is
/// <c>FlattenBatches</c> itself, the one piece of code that only runs on the
/// <see cref="AnalogFmSstvEncoder.EncodeAsync"/> side.
/// </summary>
public class AnalogFmSstvEncoderBatchedTests
{
    private const int SampleRate = 11025;

    [Fact]
    public async Task EncodeBatchedAsync_ConcatenatedBatches_ExactlyMatchesEncodeAsync_ForATransmissionWithSoundFileId()
    {
        // Sound-file ID set so this test's own input shape isn't trivially narrower than a real
        // transmission (straddles the tone-segment loop and the sound-file/CW-ID tail loop, which
        // share the same buffer/count inside EncodeBatchedAsyncCore). Auditor code-review correction
        // (2026-08-31): this test can NOT actually catch a straddle/flush bug in that shared
        // buffer/count logic -- both sides here derive from the SAME EncodeBatchedAsyncCore, so such
        // a bug would corrupt them identically and this comparison would still pass. What this test
        // DOES catch is a bug specifically in FlattenBatches (the one piece of code that's only on
        // the EncodeAsync side). The real straddle/tail regression gate is
        // AnalogFmSstvEncoderSoundFileIdTests.cs's own exact-count/exact-tail assertions, which now
        // run through the batched core too.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        float[] rawSamples = [0.25f, -0.5f, 0.75f, -1.0f, 0.0f, 0.33f, -0.66f];
        var stationId = new StationIdTransmitOptions { SoundFileSamples = rawSamples };
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var viaEncodeAsync = await CollectFloatsAsync(encoder.EncodeAsync(mode, image, stationId));
        var viaBatchedFlattened = await CollectFlattenedBatchesAsync(encoder.EncodeBatchedAsync(mode, image, stationId));

        Assert.Equal(viaEncodeAsync, viaBatchedFlattened);
    }

    [Fact]
    public async Task EncodeBatchedAsync_NoBatchIsEverEmpty_AndEveryNonFinalBatchIsTheSameSize()
    {
        // ISstvEncoder.EncodeBatchedAsync's own contract: never an empty batch, and callers must not
        // assume any specific batch size -- but every NON-FINAL batch must still be the SAME size as
        // each other (only the last one may be a shorter remainder). Deliberately does not assert
        // what that size IS (an implementation detail, per the interface's own doc comment) -- it's
        // inferred from the first batch instead.
        var mode = SstvModeRegistry.Robot36;
        var image = CreateSolidImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var batches = new List<ReadOnlyMemory<float>>();
        await foreach (var batch in encoder.EncodeBatchedAsync(mode, image))
        {
            batches.Add(batch);
        }

        Assert.True(batches.Count > 1, "Test setup problem: need at least 2 batches (one full, one final) to exercise this property.");
        Assert.All(batches, b => Assert.True(b.Length > 0, "ISstvEncoder.EncodeBatchedAsync's own contract: never an empty batch."));

        var impliedBatchSize = batches[0].Length;
        for (var i = 0; i < batches.Count - 1; i++)
        {
            Assert.Equal(impliedBatchSize, batches[i].Length);
        }

        Assert.True(batches[^1].Length <= impliedBatchSize);
    }

    private static async Task<List<float>> CollectFloatsAsync(IAsyncEnumerable<float> samples)
    {
        var result = new List<float>();
        await foreach (var sample in samples)
        {
            result.Add(sample);
        }

        return result;
    }

    private static async Task<List<float>> CollectFlattenedBatchesAsync(IAsyncEnumerable<ReadOnlyMemory<float>> batches)
    {
        var result = new List<float>();
        await foreach (var batch in batches)
        {
            result.AddRange(batch.ToArray());
        }

        return result;
    }

    private static ArrayImageSource CreateSolidImage(int width, int height) =>
        new(width, height, new Rgb24[width * height]);
}
