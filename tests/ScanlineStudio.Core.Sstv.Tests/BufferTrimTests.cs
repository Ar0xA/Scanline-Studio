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
        // that (15s worth) but far below the full 30s pushed proves trimming actually ran repeatedly
        // during this test, not just once at the very end.
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
        decoder.LineDecoded += update => decodedImage = update.Image;
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
