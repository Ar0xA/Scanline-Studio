using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;
using Yoniq.Core.Sstv;

namespace Yoniq.Core.Audio.MiniAudio.Tests;

/// <summary>
/// Piece Audio 6b: device-free (no real audio device involved), so it always runs in CI. Answers
/// a concrete question the user raised: miniaudio's data converter (pieces Audio 5/6's
/// resample/downmix on a real device) has exactly one built-in resampler, <c>ma_resample_algorithm_linear</c>
/// -- explicitly documented in the pinned miniaudio.h as "fastest, lowest quality." This measures
/// whether that resampler meaningfully degrades the existing SSTV encode/decode round trip
/// (<see cref="Yoniq.Core.Sstv.Tests.SstvRoundTripTests"/>, not duplicated here) when the signal is
/// forced through an upsample-then-downsample pair -- simulating "encoded at 44100Hz, played to
/// and captured back from a 48000Hz native device," a real scenario on many sound cards. Per the
/// staged plan: measure the default resampler first; only escalate (bump lpfOrder, or eventually
/// reach for an external resampler) if the measured degradation actually requires it.
/// </summary>
public class ResamplerQualityRoundTripTests
{
    private const int EncodeSampleRate = 44100;
    private const int SimulatedDeviceSampleRate = 48000;

    [Fact]
    public async Task EncodeThenDecode_ThroughUpsampleDownsamplePair_DegradesOnlySlightly()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(EncodeSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var baselineDelta = DecodeAndMeasureDelta(samples.ToArray(), EncodeSampleRate, mode, sourceImage);

        var upsampled = MiniAudioResampler.Resample(samples.ToArray(), EncodeSampleRate, SimulatedDeviceSampleRate);
        var roundTripped = MiniAudioResampler.Resample(upsampled, SimulatedDeviceSampleRate, EncodeSampleRate);

        var resampledDelta = DecodeAndMeasureDelta(roundTripped, EncodeSampleRate, mode, sourceImage);

        // Measured on this machine: baseline (no resampling) average per-channel delta is ~3.1;
        // after a real 44100->48000->44100 round trip through miniaudio's default linear
        // resampler (lpfOrder left at its own default of 4), delta only rises to ~3.2 -- the
        // resampler adds negligible (~0.07) degradation of its own on top of the existing
        // analog-FM decode noise floor. Per the staged plan (measure first, escalate only if
        // needed), this result means no lpfOrder bump or external resampler is warranted.
        //
        // Opus-review fix: the tolerance here used to be baselineDelta + 2.0 -- nearly 30x the
        // actual measured gap, loose enough that a real quality regression (e.g. the resampler
        // degrading to +1.0 or more) would still pass silently. Tightened to a margin with real
        // headroom above measurement noise but that still catches a genuine regression; if this
        // needs raising again, replace the number with a freshly measured one, not a guess.
        Assert.True(
            resampledDelta <= baselineDelta + 0.5,
            $"Resampled round trip's average per-channel delta ({resampledDelta:F2}) rose more than " +
            $"expected above the no-resampling baseline ({baselineDelta:F2}) -- the default linear " +
            $"resampler may be degrading the signal more than previously measured.");
        Assert.True(
            resampledDelta <= 10.0,
            $"Resampled round trip's average per-channel delta ({resampledDelta:F2}) exceeded the " +
            $"tolerance needed to still decode a usable image (baseline delta was {baselineDelta:F2}).");
    }

    private static double DecodeAndMeasureDelta(float[] samples, int sampleRate, SstvModeDefinition mode, IImageSource sourceImage)
    {
        var decoder = new AnalogFmSstvDecoder(sampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(samples);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        return AveragePerChannelDelta(sourceImage, decodedImage!);
    }

    private static double AveragePerChannelDelta(IImageSource expected, IImageSource actual)
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

        return totalDelta / sampleCount;
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
