using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Proves the real, previously-invisible bug <see cref="PllFmDemodulator"/>'s own doc comment
/// describes: without the `raw * 32768.0` scale bridge, this port's PLL AGC stays pinned at its
/// floor gain for any input quieter than full scale, instead of adapting like legacy's own
/// `CPLL::Do` does -- so decode accuracy should degrade sharply below full scale today (bug), and
/// stay comfortably within normal tolerance once the bridge is applied (fixed). Every other round-
/// trip test in this suite uses full-scale synthetic tones, which is exactly why this gap went
/// unnoticed: at full scale both the buggy and fixed paths converge to the same AGC value (see the
/// class's own doc comment for the derivation), so no existing test could have caught it either way.
/// </summary>
public class PllScaleBridgeTests
{
    [Fact]
    public async Task AttenuatedSignal_DecodesWithinTolerance()
    {
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var encoder = new AnalogFmSstvEncoder(44100);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        // -20dBFS (0.1x amplitude) -- comfortably below the ~-8dBFS point the class doc comment
        // derives as where the unfixed AGC's control range runs out, and a realistic level for real
        // captured audio (never full-scale in practice).
        var attenuated = samples.Select(s => s * 0.1f).ToArray();

        var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(attenuated);

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        // Same 10.0 tolerance every other full-header round-trip test in this suite uses -- the
        // whole point of the fix is that attenuated input should decode just as well as full-scale.
        // Confirmed this actually discriminates the bug, not just a theoretical concern: temporarily
        // reverting the scale bridge at its production call site measured 58.55 (badly over
        // tolerance) at this same -20dBFS level, vs. comfortably passing with the fix in place --
        // one of the cleanest-discriminating regression tests in this suite, unlike the AFC
        // double-correction fix earlier in this same review, which didn't cleanly discriminate at
        // realistic severity and shipped without a dedicated test as a result.
        AssertImagesMatchWithinTolerance(sourceImage, decodedImage!, maxAveragePerChannelDelta: 10.0);
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
