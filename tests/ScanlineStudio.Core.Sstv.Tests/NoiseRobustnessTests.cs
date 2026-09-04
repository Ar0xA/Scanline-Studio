using Xunit.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Noise-robustness harness -- NOT a legacy port (legacy has no synthetic noise-injection concept of
/// its own; this is new test infrastructure built specifically for this project's own verification
/// needs). Built per spec/14-roadmap.md's bandpass-filter-chain scoping discussion: an auditor second
/// opinion recommended deferring the Kaiser bandpass filter (Piece B) behind a noise-injection harness
/// that converts "we think front-end filtering helps real-world noisy reception" from a judgment call
/// into an actual measurement, rather than deciding on theory alone.
///
/// This establishes THIS PORT'S CURRENT noise-tolerance BASELINE (Hilbert demodulator, piece 14, plus
/// the always-on 2-tap moving-average pre-filter, piece 15/"Piece A" -- no Kaiser bandpass filter yet).
/// Re-run this same harness once Piece B exists to see whether it measurably improves the noise floor
/// reported below -- that comparison, not this baseline alone, is what actually answers the question.
///
/// Noise is injected into the ENCODED AUDIO SAMPLES (post-encode, pre-decode) -- the domain a real
/// receiver's front-end noise actually occupies -- as additive Gaussian noise, calibrated to a target
/// SNR by measuring the real encoded signal's own RMS power first (not an assumed/fixed amplitude).
/// Deterministic (fixed seed) so results are reproducible and genuinely comparable run to run, matching
/// this project's own encoder/decoder determinism.
/// </summary>
public class NoiseRobustnessTests
{
    private readonly ITestOutputHelper _output;

    public NoiseRobustnessTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // "Still a usable decode" bar for the noise-floor search below -- deliberately looser than
    // SstvRoundTripTests' own noiseless-signal tolerances (10.0/25.0/55.0): those characterize how
    // faithfully a CLEAN signal round-trips through this port's own DSP, not how much real noise a
    // decode can tolerate before becoming useless. 30.0 is a judgment call (not derived from source --
    // there is no legacy equivalent to derive it from), chosen as "clearly still a recognizable
    // picture, not noise-dominated garbage" -- documented here so it can be revisited, not silently
    // assumed correct.
    private const double UsableDecodeThreshold = 30.0;

    // Test-suite fixes phase 1, item 7: these two bounds pin the noise floor measured (and printed
    // via ITestOutputHelper below) on 2026-08-30, so a DSP regression that measurably worsens noise
    // tolerance fails loudly instead of only ever being visible in test output nobody reads. Each
    // bound is the measured floor PLUS one step of `snrLevelsDb` slack (not the bare measured value)
    // -- the sweep is discrete and seeded, so flake risk from exact-value pinning is low, but the CI
    // matrix includes Windows/macOS legs where float rounding could shift a borderline mode by one
    // step. Re-measure and update deliberately (re-run this file, read the new floor from test
    // output) if a real DSP change legitimately moves these -- never loosen without re-measuring.
    private const double MartinM1MaxAcceptableNoiseFloorDb = 3.0; // measured 0.0dB, next sweep step is 3.0dB
    private const double Robot36MaxAcceptableNoiseFloorDb = 6.0; // measured 3.0dB, next sweep step is 6.0dB

    [Fact]
    public async Task MartinM1_NoiseFloor_Baseline()
    {
        await MeasureAndReportNoiseFloor(SstvModeRegistry.MartinM1, MartinM1MaxAcceptableNoiseFloorDb);
    }

    [Fact]
    public async Task Robot36_NoiseFloor_Baseline()
    {
        // Robot-36 specifically: already-documented as this port's most fragile mode to small timing
        // perturbations (RobotScanlineDecoder's tone-selector read sits directly against an ambiguity
        // boundary, spec/14-roadmap.md's piece 8 entry) -- a useful second data point alongside
        // Martin M1's more "typical" behavior, not assumed to generalize from one mode alone.
        await MeasureAndReportNoiseFloor(SstvModeRegistry.Robot36, Robot36MaxAcceptableNoiseFloorDb);
    }

    private async Task MeasureAndReportNoiseFloor(SstvModeDefinition mode, double maxAcceptableNoiseFloorDb)
    {
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
        var encoder = new AnalogFmSstvEncoder(44100);
        var cleanSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, sourceImage))
        {
            cleanSamples.Add(sample);
        }

        // Descending sweep. Decode quality degrades monotonically as noise increases, informally --
        // true for every SNR tested during this harness's own development, but not formally proven,
        // so the floor calculation below does not simply assume it and trust whichever pass happened
        // last (decoder_quality_improvement.md §13 addendum: a previously-real bug here -- a later,
        // non-monotonic "pass" at a WORSE SNR could silently overwrite an earlier real failure,
        // masking it, since this loop never actually stopped on failure despite this comment's own
        // prior claim that it does). `stillUnbrokenFromTop` fixes that: the floor is the lowest SNR in
        // an UNBROKEN run of usability starting from the highest SNR, not just "the last usable point
        // seen." The sweep itself still runs every level (not an early `break`) so the full curve
        // stays visible in the console output even past the first failure.
        double[] snrLevelsDb = [40.0, 30.0, 25.0, 20.0, 16.0, 12.0, 9.0, 6.0, 3.0, 0.0];
        double? noiseFloorDb = null;
        var stillUnbrokenFromTop = true;

        foreach (var snrDb in snrLevelsDb)
        {
            var noisySamples = AddNoiseAtSnr(cleanSamples, snrDb, seed: 12345);

            var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
            SstvModeDefinition? detectedMode = null;
            IImageSource? decodedImage = null;
            decoder.ModeDetected += m => detectedMode = m;
            decoder.LineDecoded += update => decodedImage = update.Image;
            decoder.PushSamples(noisySamples);

            if (detectedMode is null || detectedMode.Id != mode.Id || decodedImage is null)
            {
                _output.WriteLine($"[{mode.Id}] SNR={snrDb,5:F1}dB: mode not detected (or wrong mode) -- decode failed outright");
                stillUnbrokenFromTop = false;
                continue;
            }

            var delta = MeasureAverageDelta(sourceImage, decodedImage);
            var usable = delta <= UsableDecodeThreshold;
            _output.WriteLine($"[{mode.Id}] SNR={snrDb,5:F1}dB: delta={delta,7:F2} usable={usable}");

            if (!usable)
            {
                stillUnbrokenFromTop = false;
            }
            else if (stillUnbrokenFromTop)
            {
                noiseFloorDb = snrDb;
            }
        }

        _output.WriteLine($"[{mode.Id}] noise floor (lowest SNR still meeting the usable-decode bar): " +
            (noiseFloorDb is null ? "NEVER (failed at every tested SNR down to 0dB)" : $"{noiseFloorDb:F1}dB"));

        // Regression guard: a lifted `null <= x` is `false`, so this correctly fails if the mode
        // stopped decoding usably at every tested SNR, not just if it merely got worse.
        Assert.True(noiseFloorDb <= maxAcceptableNoiseFloorDb,
            $"[{mode.Id}] noise floor regressed: {(noiseFloorDb is null ? "NEVER" : $"{noiseFloorDb:F1}dB")} " +
            $"(must be <= {maxAcceptableNoiseFloorDb:F1}dB).");

        // Sanity/regression guard only -- the clean encoded signal (no noise added at all) must still
        // decode correctly. This is what would catch the harness itself being broken (e.g. a noise-
        // generation bug that corrupts the signal even at nominally-infinite SNR), not a claim about
        // real noise tolerance.
        var cleanDecoder = new AnalogFmSstvDecoder(encoder.SampleRate);
        SstvModeDefinition? cleanDetectedMode = null;
        IImageSource? cleanDecodedImage = null;
        cleanDecoder.ModeDetected += m => cleanDetectedMode = m;
        cleanDecoder.LineDecoded += update => cleanDecodedImage = update.Image;
        cleanDecoder.PushSamples(cleanSamples.ToArray());

        Assert.NotNull(cleanDetectedMode);
        Assert.Equal(mode.Id, cleanDetectedMode!.Id);
        Assert.NotNull(cleanDecodedImage);
        Assert.True(MeasureAverageDelta(sourceImage, cleanDecodedImage!) <= UsableDecodeThreshold);
    }

    // SNR_dB = 20*log10(signalRms/noiseRms) for amplitude (RMS) ratios -- calibrates against the
    // REAL encoded signal's own measured RMS power, not an assumed/fixed noise amplitude, so the
    // resulting SNR is accurate regardless of a mode's own duration/duty-cycle/amplitude profile.
    private static float[] AddNoiseAtSnr(IReadOnlyList<float> signal, double snrDb, int seed)
    {
        var signalRms = ComputeRms(signal);
        var noiseRms = signalRms / Math.Pow(10.0, snrDb / 20.0);
        var random = new Random(seed); // fixed seed -- deterministic/reproducible across runs

        var noisy = new float[signal.Count];
        for (var i = 0; i < signal.Count; i++)
        {
            noisy[i] = signal[i] + (float)(NextGaussian(random) * noiseRms);
        }

        return noisy;
    }

    private static double ComputeRms(IReadOnlyList<float> samples)
    {
        double sumSquares = 0;
        foreach (var s in samples)
        {
            sumSquares += (double)s * s;
        }

        return Math.Sqrt(sumSquares / samples.Count);
    }

    // Box-Muller transform -- standard normal (mean 0, stddev 1); caller scales by the target stddev.
    private static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble(); // (0,1], avoids log(0)
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double MeasureAverageDelta(IImageSource expected, IImageSource actual)
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
