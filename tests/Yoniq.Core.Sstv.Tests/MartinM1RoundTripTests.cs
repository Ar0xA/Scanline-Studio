using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Audio;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Phase 1 milestone per spec/14-roadmap.md: "a console/test harness encodes a test image to a
/// .wav, decodes it back, and the round-trip image matches within tolerance." This is a
/// self-consistency proof of the DSP pipeline (encoder and decoder agree with each other) — it is
/// NOT yet a golden-vector match against the legacy MMSSTV binary's actual output. See the parity
/// caveat on <see cref="SstvModeDefinition"/> and spec/13-testing.md's golden-vector section.
/// </summary>
public class MartinM1RoundTripTests
{
    [Fact]
    public async Task EncodeThenDecode_ViaWavFile_RoundTripsWithinTolerance()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder();
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var wavPath = Path.Combine(Path.GetTempPath(), $"yoniq-martin-m1-{Guid.NewGuid():N}.wav");
        try
        {
            WavFile.Write(wavPath, samples.ToArray(), encoder.SampleRate);
            var (readSamples, readSampleRate) = WavFile.Read(wavPath);

            Assert.Equal(encoder.SampleRate, readSampleRate);

            var decoder = new AnalogFmSstvDecoder(readSampleRate);
            SstvModeDefinition? detectedMode = null;
            IImageSource? decodedImage = null;
            decoder.ModeDetected += m => detectedMode = m;
            decoder.LineDecoded += update => decodedImage = update.Image;

            decoder.PushSamples(readSamples);

            Assert.NotNull(detectedMode);
            Assert.Equal(mode.Id, detectedMode!.Id);
            Assert.NotNull(decodedImage);

            AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 10.0);
        }
        finally
        {
            File.Delete(wavPath);
        }
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
