using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Regression coverage for a real bug independent review found in piece 6:
/// <c>TryDecodeNarrowModeHeader</c> used to duplicate <see cref="AnalogFmSstvDecoder.Commit"/>'s body
/// inline instead of calling it, which meant AFC was silently disabled for every narrow (MN/MC) mode
/// -- no existing test covered a mistuned-audio narrow-mode decode end to end, so nothing caught it.
///
/// Honestly measured, not assumed to be a strong regression guard: at the same realistic 500ppm clock
/// mismatch <c>SlantTests</c> uses, the buggy (AFC-disabled) and fixed versions are nearly
/// indistinguishable here (10.66 vs 10.35 average delta) -- Auto Slant alone already compensates for
/// most of a mismatch this small for this mode, so this test mainly proves "no regression at a
/// realistic severity," not "definitively catches AFC being disabled." Confirmed the bug *is* clearly
/// catchable at a much larger, deliberately synthetic 2000ppm mismatch (buggy: 24.73, fixed: 20.52 --
/// still elevated even fixed, consistent with MN73's tighter AFC band degrading similarly to how
/// AVT's tight bands degrade under severe mismatch, see AvtTrainingLockDecoderTests) -- not used as
/// this test's actual assertion since a mismatch that severe introduces its own confounding
/// degradation, the same tradeoff already documented on the AVT clock-mismatch test. The fix itself
/// is verified correct by direct code reasoning (parallels the exact pattern every other Commit()-
/// calling path in AnalogFmSstvDecoder already uses), not primarily by this test.
/// </summary>
public class NarrowModeAfcTests
{
    [Fact]
    public async Task Mn73_RealisticClockMismatch_DecodesWithinTolerance()
    {
        var mode = SstvModeRegistry.Mn73;
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        Array.Fill(pixels, new Rgb24(200, 120, 60));
        var sourceImage = new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);

        const int declaredSampleRate = 44100;
        const int trueSampleRate = (int)(declaredSampleRate * 1.0005);

        var encoder = new AnalogFmSstvEncoder(trueSampleRate);
        var samples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            samples.Add(sample);
        }

        var decoder = new AnalogFmSstvDecoder(declaredSampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;

        decoder.PushSamples(samples.ToArray());

        Assert.NotNull(detectedMode);
        Assert.Equal(mode.Id, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        // Measured: 10.35 (fixed) vs 10.66 (bug reverted) -- see class doc comment for why this
        // realistic severity doesn't cleanly discriminate the fix. 12.0 gives headroom above the
        // measured fixed-version value; this assertion mainly guards against a *worse* regression
        // than the one already found (e.g. a totally broken narrow-mode decode), not a precise proof
        // AFC is contributing at this specific severity.
        var averageDelta = ComputeAveragePerChannelDelta(sourceImage, decodedImage!);
        Assert.True(averageDelta <= 12.0, $"Average per-channel delta {averageDelta:F2} exceeded tolerance.");
    }

    private static double ComputeAveragePerChannelDelta(IImageSource expected, IImageSource actual)
    {
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
}
