using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Band-1 S2 fix (pre-Phase-2 audit): <see cref="AnalogFmSstvDecoder"/>'s 5 growing sample buffers
/// used to be trimmed never -- ~5.7GB/hr @44100Hz for the exact scenario (a receiver that never
/// locks, e.g. an open squelch with no SSTV activity) the fix exists for, per an auditor plan-review
/// finding that the first draft of this fix excluded that scenario by mistake. These tests verify
/// both halves: growth actually stays bounded, and trimming doesn't corrupt (or reduce the accuracy
/// of, beyond this port's own already-established fallback-path tolerance) a real decode that comes
/// after it.
/// </summary>
public class BufferTrimTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_NonPositiveSampleRate_Throws(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AnalogFmSstvDecoder(sampleRate));
    }

    [Fact]
    public void BufferedSampleCount_StaysBounded_ForLongNeverLockingStream()
    {
        const int sampleRate = 11025;
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var random = new Random(Seed: 12345);

        // 30 real seconds of low-level noise, pushed in small chunks -- deliberately never matches
        // any VIS header or sync-interval pattern, so this decoder stays unlocked the entire time,
        // exactly the scenario the auditor plan-review flagged as the real motivating case (not the
        // locked case, which is already naturally bounded by one image's own duration).
        const int totalSeconds = 30;
        const int totalSamples = sampleRate * totalSeconds;
        const int chunkSize = 512;

        var buffer = new float[chunkSize];
        for (var pushed = 0; pushed < totalSamples; pushed += chunkSize)
        {
            var length = Math.Min(chunkSize, totalSamples - pushed);
            for (var i = 0; i < length; i++)
            {
                buffer[i] = (float)(random.NextDouble() * 0.02 - 0.01); // small-amplitude noise, well under any real sync threshold
            }

            decoder.PushSamples(buffer.AsMemory(0, length));
        }

        // Unbounded growth would leave BufferedSampleCount == totalSamples (330750). The pre-lock
        // retention window (VisHeader.MaxSearchCeilingMs / SyncIntervalTracker.MaxIntervalSamples +
        // AnchorWarmupSamples, all @11025Hz) is under 10 real seconds -- asserting comfortably above
        // that (15s worth) but far below the full 30s pushed proves trimming ran and retained only a
        // bounded trailing window.
        var bufferedSeconds = decoder.BufferedSampleCount / (double)sampleRate;
        Assert.True(
            bufferedSeconds < 15.0,
            $"Expected buffered sample count to stay well below the full 30s pushed (bounded pre-lock retention), " +
            $"but {decoder.BufferedSampleCount} samples ({bufferedSeconds:F1}s) are still held -- trimming did not run.");

        // Band-2 item S5: BufferedSampleCount alone only covers _rawSamples -- an auditor code-level
        // review flagged that the 3 new persistent VIS-bit-detector caches (D11At/D12At/D19At) have
        // their own independent Lists that could silently fail to trim (the exact bug class Band-1
        // item 2/4a each hit once already) without this test noticing. Same bound as above: real
        // growth would leave this at totalSamples too.
        var visDataSeconds = decoder.VisDataDetectorBufferedSampleCount / (double)sampleRate;
        Assert.True(
            visDataSeconds < 15.0,
            $"Expected the VIS-bit-detector caches to stay well below the full 30s pushed too, " +
            $"but {decoder.VisDataDetectorBufferedSampleCount} combined samples ({visDataSeconds:F1}s) are still held -- trimming did not run.");

        // D2 round 5: the two checks above did not observe the other four persistent sample caches,
        // so any one of them could stop trimming while the test stayed green. The diagnostic reports
        // their maximum retained length, making one unbounded cache sufficient to fail this bound.
        var largestCoreCacheSeconds = decoder.LargestCoreSampleCacheBufferedCountForTests / (double)sampleRate;
        Assert.True(
            largestCoreCacheSeconds < 15.0,
            $"Expected every core sample cache to stay below 15s, but the largest retained " +
            $"{decoder.LargestCoreSampleCacheBufferedCountForTests} samples ({largestCoreCacheSeconds:F1}s).");
    }

    [Fact]
    public void Rel_Throws_WhenAbsoluteIndexIsBehindTheTrimWatermark()
    {
        // Functional-audit fix (chunk D1, round 1): Rel()'s own doc comment calls its below-base
        // throw "a `checked`-style guard, not just a convenience" -- the dangerous failure class
        // named there is a silently-wrong (not throwing) translation, not a loud one. Nothing
        // exercised that throw path directly before this test; only Rel's normal (non-throwing)
        // translation was ever indirectly reached via decode round-trips.
        const int sampleRate = 11025;
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        var random = new Random(Seed: 12345);

        const int totalSeconds = 30;
        const int totalSamples = sampleRate * totalSeconds;
        const int chunkSize = 512;

        var buffer = new float[chunkSize];
        for (var pushed = 0; pushed < totalSamples; pushed += chunkSize)
        {
            var length = Math.Min(chunkSize, totalSamples - pushed);
            for (var i = 0; i < length; i++)
            {
                buffer[i] = (float)(random.NextDouble() * 0.02 - 0.01);
            }

            decoder.PushSamples(buffer.AsMemory(0, length));
        }

        // Same 30s-noise setup as BufferedSampleCount_StaysBounded_ForLongNeverLockingStream above,
        // which already proves BufferedSampleCount ends up well below totalSamples -- meaning
        // _bufferBase (== TotalSamplesReceived - BufferedSampleCount) is genuinely > 0, so absolute
        // index 0 is guaranteed to be behind the trim watermark, not a coincidence of this test's own
        // arithmetic.
        Assert.True(decoder.BufferedSampleCount < totalSamples, "Expected trimming to have advanced _bufferBase past 0 -- test setup itself is wrong if this is false.");

        var ex = Assert.Throws<InvalidOperationException>(() => decoder.Rel(0));
        Assert.Contains("already been trimmed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PushSamples_ThrowsObjectDisposedException_AfterDispose()
    {
        // D2 round 2 fix: PushSamples' own ObjectDisposedException.ThrowIf guard (D2 round 1) had no
        // direct test -- every other such guard in this repo (MiniAudioEngineTests,
        // MiniAudioRingTests, HamlibRadioProtocolTests, FakeRadioTransportTests) has one.
        const int sampleRate = 11025;
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        decoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => decoder.PushSamples(new float[16]));
    }

    [Fact]
    public void BufferedSampleCount_StaysBounded_ForLongNeverLockingStream_WithRxBpfOff()
    {
        // RX BPF subsystem Phase 2 -- round-1 auditor plan-review blocker, verified directly: the
        // originally-planned Off-bypass shape (a method-level early return out of
        // BandpassFilteredSampleAt when _searchBandpassFilter is null) would have skipped the fill
        // loop entirely, pinning _bandpassFilteredProcessedUpTo at 0 for the whole session -- that
        // cursor is load-bearing in BOTH TrimBuffers watermark branches, so pinning it at 0 would make
        // TrimBuffers a permanent no-op under Off, i.e. exactly the unbounded-growth bug
        // BufferedSampleCount_StaysBounded_ForLongNeverLockingStream above exists to catch, just gated
        // behind a preset this port didn't have when that test was written. The actual fix (a
        // null-coalesce INSIDE the existing fill loop, see BandpassFilteredSampleAt's own doc comment)
        // keeps the cursor advancing regardless -- this test proves that end to end, not just that the
        // code compiles under Off.
        const int sampleRate = 11025;
        var decoder = new AnalogFmSstvDecoder(sampleRate, rxBpfPreset: RxBpfPreset.Off);
        var random = new Random(Seed: 12345);

        const int totalSeconds = 30;
        const int totalSamples = sampleRate * totalSeconds;
        const int chunkSize = 512;

        var buffer = new float[chunkSize];
        for (var pushed = 0; pushed < totalSamples; pushed += chunkSize)
        {
            var length = Math.Min(chunkSize, totalSamples - pushed);
            for (var i = 0; i < length; i++)
            {
                buffer[i] = (float)(random.NextDouble() * 0.02 - 0.01);
            }

            decoder.PushSamples(buffer.AsMemory(0, length));
        }

        var bufferedSeconds = decoder.BufferedSampleCount / (double)sampleRate;
        Assert.True(
            bufferedSeconds < 15.0,
            $"Expected buffered sample count to stay well below the full 30s pushed even with RxBpfPreset.Off selected, " +
            $"but {decoder.BufferedSampleCount} samples ({bufferedSeconds:F1}s) are still held -- the Off-bypass buffer-trim-cursor fix regressed.");
    }

    [Fact]
    public async Task DecodedImage_StillDecodesCorrectly_WhenPrecededByLongSilence_ThatTriggeredTrimming()
    {
        // NOT a pixel-identical/tight-tolerance comparison against a baseline with no silence lead-in
        // -- investigated when a first version of this test asserted that and found it doesn't hold,
        // for a real architectural reason rather than a bug: the fixed-window header path
        // (TryDecodeVisHeader) only gets ONE opportunity per epoch, anchored at whatever
        // _consumedSamples was when the epoch started (sample 0, here) -- once 20s of silence has
        // proven that opportunity dead (TrimBuffers' whole reason for existing: retaining 20s of
        // silence just to keep that stale opportunity "possible" forever is exactly the unbounded
        // growth this fix exists to prevent), any header arriving later is necessarily found via
        // TryInterleavedHeaderScan's fallback instead, at THAT path's own already-documented, already
        // -accepted looser anchor precision (see SyncBypassDetectionTests' own 29.0 tolerance for the
        // same mode via the same fallback) -- a pre-existing architectural property this fix doesn't
        // change or regress, not something introduced by trimming itself. What this test actually
        // verifies: detection still succeeds, decodes the right number of lines, and the image is
        // structurally correct (not corrupted/garbled) despite 20 real seconds of preceding silence
        // having triggered multiple trims first.
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(44100);
        var transmissionSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            transmissionSamples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        IImageSource? decodedImage = null;
        var lastDecodedLine = -1;
        decoder.LineDecoded += update =>
        {
            decodedImage = update.Image;
            lastDecodedLine = Math.Max(lastDecodedLine, update.Line);
        };
        SstvModeDefinition? detectedMode = null;
        decoder.ModeDetected += m => detectedMode = m;

        // 20s of silence (well past the pre-lock retention window's few seconds @44100Hz, so trimming
        // is guaranteed to have run at least once) pushed in small chunks, THEN the real transmission.
        const int chunkSize = 500;
        var silenceSamples = new float[encoder.SampleRate * 20];
        for (var offset = 0; offset < silenceSamples.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, silenceSamples.Length - offset);
            decoder.PushSamples(silenceSamples.AsMemory(offset, length));
        }

        Assert.True(decoder.BufferedSampleCount < silenceSamples.Length, "Expected trimming to have already reduced the buffered sample count below the full silence lead-in.");
        Assert.True(decoder.VisDataDetectorBufferedSampleCount < silenceSamples.Length, "Expected the VIS-bit-detector caches (Band-2 item S5) to have been trimmed below the full silence lead-in too.");

        for (var offset = 0; offset < transmissionSamples.Count; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, transmissionSamples.Count - offset);
            decoder.PushSamples(transmissionSamples.GetRange(offset, length).ToArray());
        }

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);
        Assert.Equal(mode.ImageHeight - 1, lastDecodedLine);
        AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 29.0);
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

    private static void AssertImagesMatchWithinTolerance(IImageSource expected, IImageSource actual, double maxAveragePerChannelDelta)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);

        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < expected.Height; y++)
        {
            var expectedLine = expected.GetScanline(y);
            var actualLine = actual.GetScanline(y);

            for (var x = 0; x < expected.Width; x++)
            {
                totalDelta += Math.Abs(expectedLine[x].R - actualLine[x].R);
                totalDelta += Math.Abs(expectedLine[x].G - actualLine[x].G);
                totalDelta += Math.Abs(expectedLine[x].B - actualLine[x].B);
                sampleCount += 3;
            }
        }

        var averageDelta = totalDelta / sampleCount;
        Assert.True(
            averageDelta <= maxAveragePerChannelDelta,
            $"Average per-channel delta {averageDelta:F2} exceeded tolerance {maxAveragePerChannelDelta}.");
    }
}
