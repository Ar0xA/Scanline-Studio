using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Band-1 item 4a fix (pre-Phase-2 audit): <c>_demodulatedFrequencies</c> (and by extension the
/// shared <c>BandpassFilteredSampleAt</c> cache) used to be filled eagerly, inside
/// <c>PushSamples</c>' own per-sample loop, regardless of lock state -- for a bulk single-push
/// caller, this raced the cache all the way to the end of the buffer BEFORE <c>Commit()</c> ever got
/// a chance to run, measured directly (before this fix, via a throwaway spike) at ~115 SECONDS of
/// wrongly-filtered content, vs. single-digit milliseconds for small chunk sizes. Made lazy instead
/// (<c>DemodulatedFrequencyAt</c>), so the gap between the true lock anchor and how far the bandpass
/// cache has actually advanced by the time <c>Commit()</c> fires should now be small AND, crucially,
/// chunk-size-INVARIANT -- this is item 4a's own acceptance criterion (an auditor plan-review
/// recommendation), not just a nice-to-have measurement. This is the permanent regression form of the
/// spike that first measured it.
///
/// Also covers Band-1 item 4b (the actual H1/H2 filter switch): an auditor code-level review (round 4)
/// found the round-3 correction -- gating H1 selection on the CAPTURED lock-anchor index rather than
/// live `_mode` state, so the handful of samples strictly before the anchor that item 4a's own fix
/// means get computed after `Commit()` fires correctly stay H2 -- was protected only by a doc comment,
/// not a test. <see cref="FirstLockedFilterSample_EqualsTheLockAnchor"/> closes that gap.
/// </summary>
public class BandpassCacheChunkInvarianceTests
{
    [Fact]
    public async Task BandpassCacheGapAtLock_IsChunkSizeInvariant()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(44100);
        var transmissionSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            transmissionSamples.Add(sample);
        }

        var chunkSizes = new[] { 1, 500, 4096, int.MaxValue }; // int.MaxValue = bulk, whole file in one PushSamples call
        var results = new List<(int ChunkSize, int LockAnchor, int Gap)>();

        foreach (var chunkSize in chunkSizes)
        {
            var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
            int? lockAnchorSample = null;
            var bandpassCursorAtLock = 0;
            decoder.LockAnchorCommitted += anchor =>
            {
                if (lockAnchorSample is null)
                {
                    lockAnchorSample = anchor;
                    bandpassCursorAtLock = decoder.BandpassFilteredProcessedUpTo;
                }
            };

            var effectiveChunkSize = Math.Min(chunkSize, transmissionSamples.Count);
            for (var offset = 0; offset < transmissionSamples.Count; offset += effectiveChunkSize)
            {
                var length = Math.Min(effectiveChunkSize, transmissionSamples.Count - offset);
                decoder.PushSamples(transmissionSamples.GetRange(offset, length).ToArray());
            }

            Assert.NotNull(lockAnchorSample);
            results.Add((chunkSize, lockAnchorSample!.Value, bandpassCursorAtLock - lockAnchorSample.Value));
        }

        var (referenceChunkSize, referenceAnchor, referenceGap) = results[0];
        foreach (var (chunkSize, lockAnchor, gap) in results)
        {
            Assert.True(
                lockAnchor == referenceAnchor,
                $"Lock anchor sample differed by chunk size (chunkSize={chunkSize}: {lockAnchor} vs. chunkSize={referenceChunkSize}: {referenceAnchor}) -- header detection is supposed to already be chunk-invariant (Band-1 item 3).");
            Assert.True(
                gap == referenceGap,
                $"Bandpass-cache-to-lock-anchor gap differed by chunk size (chunkSize={chunkSize}: {gap} vs. chunkSize={referenceChunkSize}: {referenceGap}) -- item 4a's whole point was to make this chunk-invariant.");
        }

        // A concrete, regression-guarding bound, not just "equal to itself": AnchorWarmupSamples
        // (2000, AnalogFmSstvDecoder's own established order-of-magnitude for "how far a detector
        // might reasonably need to look back/settle") is the right scale to compare against -- the
        // real measured gap (the cache trailing slightly BEHIND the lock point, not ahead) sits
        // comfortably within a few multiples of that, nowhere near the ~5.08 MILLION sample gap this
        // fix replaced.
        Assert.True(
            Math.Abs(referenceGap) < 20_000,
            $"Bandpass-cache-to-lock-anchor gap ({referenceGap} samples) is far larger than expected -- item 4a may have regressed.");
    }

    [Fact]
    public async Task FirstLockedFilterSample_EqualsTheLockAnchor()
    {
        // Band-1 item 4b: pins the round-3 correction directly, rather than relying on
        // BandpassFilteredSampleAt's own doc comment alone (auditor code-level review, round 4). If
        // this ever regressed back to gating on live `_mode` state instead of the captured anchor
        // index, FirstLockedBandpassIndex would fall BEHIND LockAnchorCommitted's value (the naive gate
        // flips true for the first pre-anchor sample computed after Commit() fires, not at the anchor
        // itself) -- this test would then fail with a clear, specific mismatch.
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(44100);
        var transmissionSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            transmissionSamples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        int? lockAnchorSample = null;
        decoder.LockAnchorCommitted += anchor => lockAnchorSample ??= anchor;

        decoder.PushSamples(transmissionSamples.ToArray());

        Assert.NotNull(lockAnchorSample);
        Assert.NotNull(decoder.FirstLockedBandpassIndex);
        Assert.Equal(lockAnchorSample!.Value, decoder.FirstLockedBandpassIndex!.Value);
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }
}
