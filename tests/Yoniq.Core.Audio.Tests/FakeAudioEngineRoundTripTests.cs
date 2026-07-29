using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;
using Yoniq.Core.Sstv;

namespace Yoniq.Core.Audio.Tests;

/// <summary>
/// Closes spec/05-audio-engine.md's Definition of Done bullet ("FakeAudioEngine implemented and
/// used by at least one round-trip test") for real -- this was previously believed done but wasn't:
/// no test anywhere actually drove <see cref="FakeAudioEngine"/> before this one (confirmed by an
/// Opus plan-verification review; the only pre-existing reference to <c>Yoniq.Core.Audio</c> in the
/// SSTV test suite was for <c>WavFile</c>, unrelated).
///
/// Exercises the actual <see cref="IAudioEngine"/> boundary an audio engine implementation
/// (`spec/05-audio-engine.md`'s planned <c>PortAudioEngine</c>) will sit behind, rather than the
/// direct float-array passing every other round-trip test in <c>Yoniq.Core.Sstv.Tests</c> uses --
/// specifically, settles the <see cref="IAudioEngine.SamplesCaptured"/> chunking contract: samples
/// arrive in small, realistic real-time-callback-sized buffers (not the whole waveform in one call),
/// since that's what a real backend's capture callback will actually look like, and this port's own
/// <c>AnalogFmSstvDecoder.PushSamples</c> needs to already behave correctly against that shape (it
/// does -- <c>TryProcessBuffer</c>'s own incremental design already assumes samples can arrive in
/// pieces across multiple calls, but this is the first test that actually exercises many small calls
/// end-to-end rather than one bulk call).
/// </summary>
public class FakeAudioEngineRoundTripTests
{
    // A realistic real-time audio callback buffer size (e.g. PortAudio's own typical default
    // frames-per-buffer at 11025Hz), not the whole transmission at once.
    private const int CallbackBufferSize = 512;

    [Fact]
    public async Task EncodeThenDecode_ThroughFakeAudioEngine_RoundTripsWithinTolerance()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(11025);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        await using var engine = new FakeAudioEngine();

        // Round-2-engine-review fix: FakeAudioEngine now enforces the same Enqueue-before-Start
        // contract MiniAudioEngine does, so this call is required, not optional -- see
        // FakeAudioEngine's own doc comment for why the fake was tightened rather than left
        // permissive.
        await engine.StartPlaybackAsync(FakeDevice, encoder.SampleRate, CancellationToken.None);

        // TX side: the encoder's own output is what a real caller would hand to
        // EnqueuePlaybackSamples for the engine to play out. Verifies that path is a faithful
        // pass-through, not just assumed.
        engine.EnqueuePlaybackSamples(samples.ToArray());
        Assert.Equal(samples.Count, engine.PlaybackSamples.Count);
        Assert.Equal(samples, engine.PlaybackSamples);

        // RX side: wire a decoder to SamplesCaptured exactly as a real consumer would, then feed
        // the same audio back in as if it had been received -- in small, realistic chunks, not one
        // bulk call, to genuinely exercise the streaming contract rather than assume it works.
        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;
        engine.SamplesCaptured += chunk => decoder.PushSamples(chunk);

        await engine.StartCaptureAsync(FakeDevice, encoder.SampleRate, CancellationToken.None);
        for (var offset = 0; offset < samples.Count; offset += CallbackBufferSize)
        {
            var length = Math.Min(CallbackBufferSize, samples.Count - offset);
            engine.PushCapturedSamples(samples.ToArray().AsMemory(offset, length));
        }
        await engine.StopCaptureAsync();

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 10.0);
    }

    private static readonly Yoniq.Abstractions.Audio.AudioDeviceInfo FakeDevice =
        new("fake", "Fake Device", MaxInputChannels: 1, MaxOutputChannels: 1, SupportedSampleRates: [11025]);

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
        Assert.True(averageDelta <= maxAveragePerChannelDelta, $"Average per-channel delta {averageDelta:F2} exceeded tolerance {maxAveragePerChannelDelta}.");
    }
}
