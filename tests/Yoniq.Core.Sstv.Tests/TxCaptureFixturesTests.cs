using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Sstv;
using Yoniq.Core.Imaging;

namespace Yoniq.Core.Sstv.Tests;

/// <summary>
/// Verifies the actual generated `TxCapture/*_TX.mmv` files (spec/14-roadmap.md, "Milestone audit,
/// Phase 1+2" -> "Explicit prerequisite before Phase 3"), not just <see cref="MmvFile.Write"/>'s own
/// abstract byte-format correctness (that's <see cref="FixtureFileFormatTests"/>'s job -- arbitrary
/// float values round-tripping through the file format). This closes the loop the file-format tests
/// deliberately don't: read each real generated file back exactly as a human would hand it to legacy,
/// decode it with THIS PORT'S OWN decoder, and confirm the result is a valid, correctly-identified,
/// correctly-decoded transmission -- before asking anyone to spend real time feeding these into a real
/// legacy install. A bug reachable only by real encoded SSTV audio (not the synthetic patterns
/// FixtureFileFormatTests exercises) would show up here and nowhere else in this port's own test suite.
/// </summary>
public class TxCaptureFixturesTests
{
    private const string FixtureDir = "Fixtures/GoldenVectors";
    private const string TxCaptureDir = "Fixtures/GoldenVectors/TxCapture";

    public static readonly TheoryData<string, string, double> Fixtures = new()
    {
        { "robot-36", "robot36.bmp", 20.0 },
        { "martin-m1", "martin-m1.bmp", 15.0 },
        { "scottie-s1", "scottie-s1.bmp", 15.0 },
        { "robot-72", "robot72.bmp", 20.0 },
        { "pd90", "pd90.bmp", 15.0 },
        { "rm8", "rm8.bmp", 20.0 },
        { "mn110", "mn110.bmp", 20.0 },
        { "avt", "avt.bmp", 20.0 },
        { "scottie-dx", "scottie-dx.bmp", 15.0 },
        { "mr73", "mr73.bmp", 20.0 },
        { "r24", "r24.bmp", 20.0 },
    };

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void GeneratedTxMmvFile_ReadBackAndSelfDecoded_MatchesSourceWithinTolerance(
        string modeId, string sourceBmpFileName, double tolerance)
    {
        var mmvPath = Path.Combine(TxCaptureDir, $"{modeId}_TX.mmv");
        Assert.True(File.Exists(mmvPath), $"Missing generated fixture: {mmvPath}");

        var (samples, sampleRate) = MmvFile.Read(mmvPath);
        Assert.Equal(11025, sampleRate);
        Assert.True(samples.Length > 0, $"{modeId}_TX.mmv read back zero samples.");

        var decoder = new AnalogFmSstvDecoder(sampleRate);
        SstvModeDefinition? detectedMode = null;
        IImageSource? decodedImage = null;
        var restartCount = 0;
        decoder.ModeDetected += m => detectedMode = m;
        decoder.LineDecoded += update => decodedImage = update.Image;
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(samples);

        Assert.Equal(0, restartCount);
        Assert.NotNull(detectedMode);
        Assert.Equal(modeId, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmpFileName));
        var delta = MeasureAveragePerChannelDelta(source, decodedImage!);

        Assert.True(delta <= tolerance,
            $"[{modeId}] self-decode of the generated TX .mmv file diverged from its own source image by {delta:F2} (tolerance {tolerance}) -- the file handed to legacy may not be a valid transmission.");
    }

    private static double MeasureAveragePerChannelDelta(IImageSource expected, IImageSource actual)
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
}
