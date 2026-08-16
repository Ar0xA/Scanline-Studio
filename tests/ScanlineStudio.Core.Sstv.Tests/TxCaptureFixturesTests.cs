using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// spec/18-path-to-1.0.md Medium item: a CHARACTERIZATION/change-detector of
/// <see cref="AnalogFmSstvEncoder"/> against its own frozen prior output -- NOT a legacy-validation
/// test (that's <see cref="GoldenVectorTests"/>'s separate, human-dependent job, comparing against
/// real captured/decoded legacy audio). The previous version of this file only self-decoded the
/// checked-in <c>TxCapture/*.mmv</c> fixture with THIS PORT'S OWN decoder and never invoked the
/// encoder at all -- any encoder change, intentional or not, would leave every assertion here
/// unaffected, silently making "TX is golden-vector validated" an overstated claim. The load-
/// bearing assertion below is a direct live-encoder-vs-checked-in-fixture comparison; if it fails,
/// that means the live encoder no longer matches the checked-in fixture -- if this is an
/// INTENTIONAL encoder change, re-run <see cref="TxCaptureFixtureGenerator"/> (see its own doc
/// comment for the exact two-step invocation) and commit the result; if not, this is a real
/// regression, not something to "fix" by regenerating.
/// </summary>
public class TxCaptureFixturesTests
{
    private const string FixtureDir = "Fixtures/GoldenVectors";
    private const string TxCaptureDir = "Fixtures/GoldenVectors/TxCapture";

    // Tolerances re-measured against the regenerated (current-encoder) fixtures, not inherited
    // from the pre-BPF/pre-truncation-fix test they replace (spec/18-path-to-1.0.md's own
    // diagnosis: carrying old tolerances forward onto a different measurement would make this
    // check non-discriminating from day one, same convention GoldenVectorTests.cs already uses).
    // Measured (self-decode of the freshly-regenerated .mmv vs. its source bmp): robot-36 4.77,
    // martin-m1 2.10, scottie-s1 1.75, robot-72 4.42, pd90 3.75, rm8 7.52, mn110 4.05, avt 6.52,
    // scottie-dx 1.74, mr73 4.43, r24 4.67. Each tolerance below is ~2x its measured value.
    public static readonly TheoryData<string, string, double> Fixtures = new()
    {
        { "robot-36", "robot36.bmp", 9.5 },
        { "martin-m1", "martin-m1.bmp", 4.2 },
        { "scottie-s1", "scottie-s1.bmp", 3.5 },
        { "robot-72", "robot72.bmp", 8.8 },
        { "pd90", "pd90.bmp", 7.5 },
        { "rm8", "rm8.bmp", 15.0 },
        { "mn110", "mn110.bmp", 8.1 },
        { "avt", "avt.bmp", 13.0 },
        { "scottie-dx", "scottie-dx.bmp", 3.5 },
        { "mr73", "mr73.bmp", 8.9 },
        { "r24", "r24.bmp", 9.3 },
    };

    /// <summary>Two int16-quantization LSB on the same [-1,1] float scale <see cref="MmvFile.Read"/>
    /// hands back (i.e. <c>2.0f/32768.0f</c>) -- not byte-exact, since <c>Math.Sin</c> (the encoder's
    /// per-sample VCO call) is not guaranteed bit-identical across the 3 CI OS legs; 2 LSB absorbs
    /// that while still catching any real encoder change. Both comparison operands are quantized via
    /// the same MmvFile.Write/Read round trip (not raw-live-float-vs-quantized-fixture) specifically
    /// because a real, measured fraction of samples in every one of these 11 modes clip at
    /// <see cref="TxOutputBandpassFilter"/>'s output (6-8%, not a rare edge case -- see
    /// TxCaptureFixtureGenerator's own console output the last time these fixtures were
    /// regenerated) -- comparing a clamped fixture value against an unclamped raw live value would
    /// explode to hundreds of LSB of "difference" that's pure quantization/clamping artifact, not a
    /// real encoder divergence.</summary>
    private const float SampleToleranceLsb = 2.0f / 32768.0f;

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task LiveEncoderOutput_MatchesCheckedInFixture_WithinQuantizationTolerance(
        string modeId, string sourceBmpFileName, double _)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmpFileName));

        var encoder = new AnalogFmSstvEncoder(11025);
        var liveSamples = new List<float>();
        // Design point 6 (plan-review round 2): pinned to None explicitly, matching the generator --
        // a generator/test divergence here would change the segment stream and blow the exact-
        // sample-count assertion below for a reason unrelated to real encoder fidelity.
        await foreach (var sample in encoder.EncodeAsync(mode, source, StationIdTransmitOptions.None))
        {
            liveSamples.Add(sample);
        }

        // Round the live stream through the SAME MmvFile.Write/Read quantization the checked-in
        // fixture already went through (see SampleToleranceLsb's own doc comment for why both
        // sides must be equally quantized/clamped before comparing) -- also exercises the real
        // file-write path the human-facing artifact depends on, not just an in-memory float array.
        var tempPath = Path.GetTempFileName();
        try
        {
            MmvFile.Write(tempPath, liveSamples.ToArray(), encoder.SampleRate);
            var (liveQuantized, liveSampleRate) = MmvFile.Read(tempPath);

            var mmvPath = Path.Combine(TxCaptureDir, $"{modeId}_TX.mmv");
            Assert.True(File.Exists(mmvPath), $"Missing generated fixture: {mmvPath}");
            var (fixtureSamples, fixtureSampleRate) = MmvFile.Read(mmvPath);

            Assert.Equal(11025, liveSampleRate);
            Assert.Equal(11025, fixtureSampleRate);

            // Sample-count equality is exact, not +/-1: the encoder's own running-accumulator
            // duration math (idealSamplesSoFar) uses only +/-*/ on doubles -- IEEE-754 correctly-
            // rounded and reproducible across platforms, with no Math.Sin/Cos/transcendental call
            // anywhere in the duration-computation path (those only ever affect sample VALUES,
            // covered by the tolerance above, never the sample COUNT). A future reader should not
            // "helpfully" loosen this to +/-1 without re-deriving why it doesn't need to be.
            Assert.Equal(fixtureSamples.Length, liveQuantized.Length);

            var maxDiff = 0f;
            var maxDiffIndex = -1;
            for (var i = 0; i < fixtureSamples.Length; i++)
            {
                var diff = Math.Abs(liveQuantized[i] - fixtureSamples[i]);
                if (diff > maxDiff)
                {
                    maxDiff = diff;
                    maxDiffIndex = i;
                }
            }

            Assert.True(maxDiff <= SampleToleranceLsb,
                $"[{modeId}] live encoder output diverges from the checked-in fixture by {maxDiff} " +
                $"at sample {maxDiffIndex} (tolerance {SampleToleranceLsb}) -- the live encoder no " +
                "longer matches the checked-in fixture. If this is an INTENTIONAL encoder change, " +
                "re-run TxCaptureFixtureGenerator (see its own doc comment) and commit the result; " +
                "if not, this is a real regression.");
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void CheckedInFixture_SelfDecoded_MatchesSourceWithinTolerance(
        string modeId, string sourceBmpFileName, double tolerance)
    {
        // Decodes the FIXTURE read from disk (not a live-encoded stream) -- validates the actual
        // int16-quantized artifact a human would hand to a real legacy install, which is this
        // test's own real, non-duplicate value (SstvRoundTripTests already covers live-encode-then-
        // self-decode against an in-memory stream; decoding here too would just repeat that).
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
        decoder.LineDecoded += update => decodedImage = Snapshot(update.Image);
        decoder.DecodeRestarted += _ => restartCount++;

        decoder.PushSamples(samples);

        Assert.Equal(0, restartCount);
        Assert.NotNull(detectedMode);
        Assert.Equal(modeId, detectedMode!.Id);
        Assert.NotNull(decodedImage);

        var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmpFileName));
        var delta = MeasureAveragePerChannelDelta(source, decodedImage!);

        Assert.True(delta <= tolerance,
            $"[{modeId}] self-decode of the checked-in TX .mmv fixture diverged from its own source image by {delta:F2} (tolerance {tolerance}) -- the file handed to legacy may not be a valid transmission.");
    }

    private static ArrayImageSource Snapshot(IImageSource image)
    {
        var pixels = new Rgb24[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, image.Height, pixels);
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
