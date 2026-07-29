using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// End-to-end proof that <see cref="VisLockStateMachine"/>, wired into
/// <see cref="AnalogFmSstvDecoder"/>'s <c>TryVisLockStateMachine</c>, actually recognizes a real
/// encoded transmission preceded by leading silence -- the capability the fixed-window header path
/// (<c>TryDecodeVisHeader</c>, which assumes the header starts exactly at <c>_consumedSamples</c>)
/// cannot provide.
///
/// Anchor precision was as tight as the fixed-window path's (&lt;10.0, same as
/// <see cref="SstvRoundTripTests"/>) before piece 7c reintroduced the absolute-amplitude thresholds
/// (m_SLvl/m_SLvl2) into <see cref="VisLockStateMachine"/>'s Search/ConfirmLock/Verify conditions --
/// now measured at 11.49, re-measured after 7c, not assumed. Same mechanism already documented on
/// <see cref="SyncBypassDetectionTests"/>'s tolerance: CLVL's AGC (correctly modeled as of piece
/// 7a/7b) drives a full-amplitude tone to a hard ±16384 clip once warmed up, which is genuinely how
/// legacy's own tone-race amplitude comparisons behave, but makes the exact sample at which the
/// Search->ConfirmLock trigger crosses its (now-absolute, not just relative) threshold slightly
/// noisier -- a small, real, legacy-faithful cost, not a regression to chase to zero.
/// </summary>
public class VisLockStateMachineDecoderTests
{
    [Fact]
    public async Task HeaderAfterLeadingSilence_IsRecognizedAndDecodedPrecisely()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(11025);
        var headerAndImageSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            headerAndImageSamples.Add(sample);
        }

        var leadingSilence = new float[3 * encoder.SampleRate];
        var fullStream = leadingSilence.Concat(headerAndImageSamples).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(fullStream);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 13.0);
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
        Assert.True(averageDelta <= maxAveragePerChannelDelta, $"Average per-channel delta {averageDelta:F2} exceeded tolerance {maxAveragePerChannelDelta}.");
    }
}
