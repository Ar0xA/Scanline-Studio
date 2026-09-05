using System.Globalization;
using System.Text.Json;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// A reusable, persistent "did this change help or hurt decode quality" instrument -- decoupled
/// entirely from legacy parity. Measures this port's decode against the SOURCE IMAGE (the one ground
/// truth nobody disputes) at a sweep of controlled, exactly-known noise levels, across ALL 43
/// registered SSTV modes. Extends <see cref="NoiseRobustnessTests"/>'s own calibrated-noise-injection
/// technique (which this file duplicates rather than shares, matching this test suite's own
/// established small-duplication convention -- see that file's own
/// `AddNoiseAtSnr`/`ComputeRms`/`NextGaussian`).
///
/// Why this exists: the H3/HBPFN parity item (decoder_quality_improvement.md §5.1) shipped a full
/// review pass and only got measured against legacy's own decode after the fact -- and that
/// measurement, even when it existed, answered "does this match legacy," not "does this improve
/// reception." This harness answers the second question directly: run it before a DSP experiment,
/// run it again after, diff the two reports. A curve that moves toward zero at more SNR levels is a
/// real quality improvement; legacy's own behavior is not part of the comparison at all.
///
/// Full 43-mode coverage (decoder_quality_improvement.md §13 addendum, and direct user instruction):
/// the H3 item showed a whole mode FAMILY (MN/MC) can have a dramatically different noise-tolerance
/// profile than modes already covered -- a future DSP change could just as easily help or hurt one
/// family and not another, and there is no way to know without testing broadly. Real over-the-air
/// audio only exists for 8 modes (the ones with existing golden-vector fixtures) -- rare modes are
/// genuinely hard to capture on-air, so the other 35 use a synthetic gradient test image at each
/// mode's own canvas size instead (same technique <see cref="NoiseRobustnessTests"/> already
/// established), not a new methodology.
///
/// Two independent noise seeds per SNR point (§13 addendum: a single noise realization can misrepresent
/// a mode's real behavior), reported as the mean delta -- if either seed fails to decode correctly, the
/// whole point is recorded as a failure rather than averaging a real failure against a real success.
///
/// Noise-floor fix (§13 addendum, and confirmed directly in the pre-fix code): the sweep only updates
/// the recorded floor while every higher-SNR point in the same run was ALSO usable -- a later,
/// non-monotonic "pass" at a worse SNR can no longer silently overwrite an earlier real failure. The
/// full curve (including points after the first failure) is still recorded for visibility; only the
/// FLOOR calculation stops advancing.
///
/// Gated behind an explicit env var, not directory presence (unlike <see cref="OtaBaselineHarness"/>):
/// this harness is fully synthetic (no external local files needed) and deliberately slow (43 modes x
/// 10 SNR levels x 2 seeds = up to 860 encode+decode passes), so it must not silently run on every
/// ordinary test pass -- mirrors <c>RequiresTxFixtureRegenerationFactAttribute</c>'s explicit-opt-in
/// convention.
/// </summary>
public sealed class ImpairmentSweepHarness
{
    internal const string FixtureDir = "Fixtures/GoldenVectors";
    internal static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    // Real golden-vector source images -- more representative than a fresh gradient, and keeps this
    // harness's mode set intuitively cross-referenceable against GoldenVectorTests. PictureHeight is
    // the same "real content" crop each fixture already uses there (legacy padding/row-doubling
    // quirks some canvases have) -- see GoldenVectorTests.Fixtures for the same table.
    internal static readonly (string ModeId, string SourceBmp, int PictureHeight)[] RealFixtureModes =
    [
        ("robot-36", "robot36.bmp", 240),
        ("martin-m1", "martin-m1.bmp", 256),
        ("scottie-s1", "scottie-s1.bmp", 256),
        ("robot-72", "robot72.bmp", 240),
        ("pd90", "pd90.bmp", 256),
        ("rm8", "rm8.bmp", 240),
        ("mn110", "mn110.bmp", 256),
        ("avt", "avt.bmp", 240),
    ];

    // Same sweep as NoiseRobustnessTests -- descending SNR.
    internal static readonly double[] SnrLevelsDb = [40.0, 30.0, 25.0, 20.0, 16.0, 12.0, 9.0, 6.0, 3.0, 0.0];

    // Two seeds, not one (§13 addendum) -- averaged per point, not a bigger single sweep, to keep the
    // reported number meaning "typical," not "this exact noise draw."
    private static readonly int[] NoiseSeeds = [12345, 67890];

    [RequiresImpairmentSweepFact]
    public async Task RunImpairmentSweep_ProducesBaselineReport()
    {
        var runDir = Path.Combine(FindRepoRoot(), "impairment-reports", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);

        var realFixtureModeIds = RealFixtureModes.Select(m => m.ModeId).ToHashSet();
        var reports = new List<ImpairmentModeReport>();

        foreach (var (modeId, sourceBmp, pictureHeight) in RealFixtureModes)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] starting {modeId} (real fixture)...");
            var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
            reports.Add(await MeasureModeAsync(modeId, source, pictureHeight));
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] finished {modeId}.");
        }

        // Every other registered mode -- synthetic gradient at the mode's own full canvas size, no
        // crop needed since this harness generates the source image itself at exactly that size
        // (unlike the real fixtures above, whose bmp files predate this harness and were sized to
        // legacy's own "real content" height, not the full padded canvas).
        foreach (var mode in SstvModeRegistry.All.Where(m => !realFixtureModeIds.Contains(m.Id)))
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] starting {mode.Id} (synthetic)...");
            // A mono-only mode (rm8/rm12's ColorEncoding.MonoAveragedPaired) structurally cannot
            // reproduce a colored source -- decoding it correctly still collapses to R=G=B, so the
            // RGB color gradient used for every other mode would show a large, mostly noise-independent
            // delta that measures color loss, not decode/noise quality. A grayscale gradient keeps the
            // same per-channel delta metric meaningful for these modes too.
            var source = mode.ColorEncoding == ColorEncoding.MonoAveragedPaired
                ? CreateGrayscaleGradientTestImage(mode.ImageWidth, mode.ImageHeight)
                : CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
            reports.Add(await MeasureModeAsync(mode.Id, source, mode.ImageHeight));
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] finished {mode.Id}.");
        }

        var json = JsonSerializer.Serialize(reports, ReportJsonOptions);
        File.WriteAllText(Path.Combine(runDir, "report.json"), json);

        Console.WriteLine($"Impairment sweep: {reports.Count} modes, written to {runDir}.");
        foreach (var report in reports)
        {
            var floor = report.NoiseFloorDb is null ? "NEVER" : $"{report.NoiseFloorDb:F1}dB";
            Console.WriteLine($"  [{report.ModeId}] noise floor: {floor}");
        }
    }

    /// <summary>Diffs two prior sweep runs, per mode per SNR level -- a genuine "did quality change"
    /// comparison, since both sides are measured against the SAME known source image, not against each
    /// other. Unlike <see cref="OtaBaselineHarness"/>'s OTA compare mode, this delta-of-deltas IS an
    /// accuracy claim: a negative move (after &lt; before) at a given SNR is a real improvement, a
    /// positive move is a real regression -- there is no "self-referential, not accuracy" caveat here,
    /// because the source image ground truth never changes between runs.</summary>
    [RequiresImpairmentComparisonFact]
    public void CompareImpairmentSweepRuns_ReportsQualityDelta()
    {
        var beforeDir = Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_BEFORE")!;
        var afterDir = Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_AFTER")!;

        var before = ReadReport(beforeDir);
        var after = ReadReport(afterDir);

        // Before any comparison output exists, so a rejected compare cannot leave half a report on
        // screen. Comparability is a four-member tuple, not just the noise model: two runs of the
        // SAME model are still not comparable if they used a different calibration band or a
        // different floor estimator, and raising the seed count is an explicitly planned follow-up.
        AssertComparable(before, after);

        var afterByMode = after.ToDictionary(r => r.ModeId);

        var comparisons = new List<string>();
        foreach (var beforeReport in before)
        {
            if (!afterByMode.TryGetValue(beforeReport.ModeId, out var afterReport))
            {
                comparisons.Add($"{beforeReport.ModeId}: no matching mode in the after-run.");
                continue;
            }

            var beforeBySnr = beforeReport.Points.ToDictionary(p => p.SnrDb);
            var afterBySnr = afterReport.Points.ToDictionary(p => p.SnrDb);
            foreach (var snrDb in SnrLevelsDb)
            {
                var hasBefore = beforeBySnr.TryGetValue(snrDb, out var beforePoint);
                var hasAfter = afterBySnr.TryGetValue(snrDb, out var afterPoint);
                if (!hasBefore || !hasAfter)
                {
                    comparisons.Add($"{beforeReport.ModeId} @ {snrDb:F1}dB: missing on one side, not comparable.");
                    continue;
                }

                if (!beforePoint!.DecodedCorrectly || !afterPoint!.DecodedCorrectly)
                {
                    comparisons.Add(
                        $"{beforeReport.ModeId} @ {snrDb:F1}dB: decode outcome changed " +
                        $"(before decoded={beforePoint!.DecodedCorrectly}, after decoded={afterPoint!.DecodedCorrectly}).");
                    continue;
                }

                var change = afterPoint!.Delta - beforePoint!.Delta;
                var direction = change < -0.01 ? "IMPROVED" : change > 0.01 ? "WORSENED" : "unchanged";
                comparisons.Add(
                    $"{beforeReport.ModeId} @ {snrDb:F1}dB: {beforePoint.Delta:F2} -> {afterPoint.Delta:F2} ({direction})");
            }

            var beforeFloor = beforeReport.NoiseFloorDb;
            var afterFloor = afterReport.NoiseFloorDb;
            if (beforeFloor != afterFloor)
            {
                comparisons.Add(
                    $"{beforeReport.ModeId}: noise floor {(beforeFloor is null ? "NEVER" : $"{beforeFloor:F1}dB")} -> " +
                    $"{(afterFloor is null ? "NEVER" : $"{afterFloor:F1}dB")}.");
            }
        }

        foreach (var line in comparisons)
        {
            Console.WriteLine(line);
        }

        Assert.NotEmpty(comparisons);
    }

    /// <summary>Throws unless both runs share every property that makes their numbers mean the same
    /// thing. A report with no run metadata predates this work and is an <c>awgn-v1</c> run at 44100Hz
    /// calibrated on the total band with the original 2-seed rule.</summary>
    internal static void AssertComparable(IReadOnlyList<ImpairmentModeReport> before, IReadOnlyList<ImpairmentModeReport> after)
    {
        var beforeRun = DescribeRun(before);
        var afterRun = DescribeRun(after);
        if (beforeRun != afterRun)
        {
            throw new InvalidOperationException(
                $"Refusing to compare two impairment runs that are not on the same footing.{Environment.NewLine}" +
                $"  before: {beforeRun}{Environment.NewLine}" +
                $"  after:  {afterRun}");
        }
    }

    private static string DescribeRun(IReadOnlyList<ImpairmentModeReport> report)
    {
        var run = report.Select(r => r.Run).FirstOrDefault(r => r is not null);
        return run is null
            ? "awgn-v1 @44100Hz, calibration=total-band, seeds=2"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{run.NoiseModel} @{run.SampleRate}Hz, calibration={run.CalibrationBand}, seeds={run.SeedCount}");
    }

    internal static IReadOnlyList<ImpairmentModeReport> ReadReport(string runDir)
    {
        var json = File.ReadAllText(Path.Combine(runDir, "report.json"));
        return JsonSerializer.Deserialize<List<ImpairmentModeReport>>(json, ReportJsonOptions) ?? [];
    }

    private static async Task<ImpairmentModeReport> MeasureModeAsync(string modeId, IImageSource source, int pictureHeight)
    {
        var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);

        var encoder = new AnalogFmSstvEncoder(44100);
        var cleanSamples = new List<float>();
        await foreach (var sample in encoder.EncodeAsync(mode, source))
        {
            cleanSamples.Add(sample);
        }

        var points = new List<ImpairmentPoint>();
        double? noiseFloorDb = null;
        // Once a point fails, no LATER (worse-SNR) point may resurrect the floor, even if that later
        // point happens to decode correctly -- a real, previously-unfixed bug (§13 addendum): the
        // floor must be the lowest SNR in an UNBROKEN run of usability from the top, not just "the
        // last usable point seen while iterating." The full curve is still recorded either way.
        var stillUnbrokenFromTop = true;

        foreach (var snrDb in SnrLevelsDb)
        {
            double deltaSum = 0;
            var allSeedsDecodedCorrectly = true;
            foreach (var seed in NoiseSeeds)
            {
                var noisySamples = AddNoiseAtSnr(cleanSamples, snrDb, seed);

                var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
                SstvModeDefinition? detectedMode = null;
                IImageSource? decodedImage = null;
                decoder.ModeDetected += m => detectedMode = m;
                decoder.LineDecoded += update => decodedImage = update.Image;
                decoder.PushSamples(noisySamples);

                var decodedCorrectly = detectedMode is not null && detectedMode.Id == mode.Id && decodedImage is not null;
                if (!decodedCorrectly)
                {
                    allSeedsDecodedCorrectly = false;
                    break;
                }

                var actual = CropToTop(decodedImage!, pictureHeight);
                deltaSum += MeasureAveragePerChannelDelta(source, actual, pictureHeight);
            }

            if (!allSeedsDecodedCorrectly)
            {
                points.Add(new ImpairmentPoint(snrDb, double.NaN, false));
                stillUnbrokenFromTop = false;
                continue;
            }

            var delta = deltaSum / NoiseSeeds.Length;
            points.Add(new ImpairmentPoint(snrDb, delta, true));

            // Same "usable decode" bar as NoiseRobustnessTests, kept in sync deliberately -- this
            // harness's "noise floor" should mean the same thing that file's already-established one
            // does, not a second, differently-calibrated number with the same name.
            var usable = delta <= 30.0;
            if (!usable)
            {
                stillUnbrokenFromTop = false;
            }
            else if (stillUnbrokenFromTop)
            {
                noiseFloorDb = snrDb;
            }
        }

        return new ImpairmentModeReport(modeId, points, noiseFloorDb);
    }

    // SNR_dB = 20*log10(signalRms/noiseRms) -- identical formula to NoiseRobustnessTests.AddNoiseAtSnr,
    // duplicated per this suite's own small-duplication convention (see class doc comment).
    private static float[] AddNoiseAtSnr(IReadOnlyList<float> signal, double snrDb, int seed)
    {
        var signalRms = ComputeRms(signal);
        var noiseRms = signalRms / Math.Pow(10.0, snrDb / 20.0);
        var random = new Random(seed);

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

    private static double NextGaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    internal static IImageSource CropToTop(IImageSource image, int pictureHeight)
    {
        if (pictureHeight == image.Height)
        {
            return image;
        }

        var pixels = new Rgb24[image.Width * pictureHeight];
        for (var y = 0; y < pictureHeight; y++)
        {
            var line = image.GetScanline(y);
            for (var x = 0; x < image.Width; x++)
            {
                pixels[(y * image.Width) + x] = line[x];
            }
        }

        return new ArrayImageSource(image.Width, pictureHeight, pixels);
    }

    internal static double MeasureAveragePerChannelDelta(IImageSource expected, IImageSource actual, int height)
    {
        double totalDelta = 0;
        var sampleCount = 0;
        for (var y = 0; y < height; y++)
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

    // Identical formula to NoiseRobustnessTests.CreateGradientTestImage, duplicated per this suite's
    // own small-duplication convention -- used only for modes with no real golden-vector source bmp.
    internal static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    // Same shape/gradient as CreateGradientTestImage, but R=G=B -- fair for a mono-only encoding
    // (rm8/rm12), where a colored source would always show a large, noise-independent delta from
    // color loss alone.
    internal static ArrayImageSource CreateGrayscaleGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var gray = (byte)(((x * 255 / Math.Max(1, width - 1)) + (y * 255 / Math.Max(1, height - 1))) / 2);
                pixels[(y * width) + x] = new Rgb24(R: gray, G: gray, B: gray);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from test output directory.");
        }

        return dir.FullName;
    }
}

/// <summary>One seed's own outcome at one SNR point. Recorded rather than collapsed, because under
/// heavy-tailed noise a "first failure wins" rule turns the floor into a min-of-N order statistic:
/// pessimistically biased, high variance, and liable to move several dB run-to-run on clip luck
/// alone -- which would then be read as a DSP effect.</summary>
public sealed record ImpairmentSeedOutcome(int Seed, bool DecodedCorrectly, double? Delta, double? PerLineDelta95, bool Usable);

/// <param name="SnrDb">The nominal sweep-grid index, NOT a measurement. Its physical meaning differs
/// by noise model -- see <paramref name="TotalBandSnrDb"/> and <paramref name="InBandSnrDb"/>.</param>
public sealed record ImpairmentPoint(
    double SnrDb,
    double Delta,
    bool DecodedCorrectly,
    double? TotalBandSnrDb = null,
    double? InBandSnrDb = null,
    double? NoiseBandPowerH1Db = null,
    double? NoiseBandPowerH2Db = null,
    double? NoiseBandPowerH3Db = null,
    double? NoiseBandPowerTotalDb = null,
    double? PerLineDelta95 = null,
    bool? UsableByPercentile = null,
    IReadOnlyList<ImpairmentSeedOutcome>? SeedOutcomes = null);

/// <summary>Run-level metadata, repeated on every mode record rather than hoisted to a new JSON root
/// object -- the root is a bare array in every existing report, and keeping it that way means old
/// reports still deserialize without sniffing the first token.</summary>
public sealed record ImpairmentRunMetadata(
    string NoiseModel,
    int SampleRate,
    string CalibrationBand,
    int SeedCount,
    string FloorAxis,
    string FloorRule,
    double MeanUsableDeltaBar,
    double PercentileUsableDeltaBar,
    IReadOnlyList<string> FilterSpecs,
    IReadOnlyList<string>? NoiseStatistics = null,
    string? CorpusDirectory = null,
    string? CorpusIdentityHash = null,
    int? CorpusClipCount = null,
    double? CorpusTotalSeconds = null,
    IReadOnlyList<string>? CorpusCaveats = null,
    IReadOnlyList<string>? CorpusSkippedFiles = null);

/// <param name="Run">Null on any report written before the real-noise work; such a report is an
/// <c>awgn-v1</c> run at 44100Hz on the total-band axis.</param>
public sealed record ImpairmentModeReport(
    string ModeId,
    IReadOnlyList<ImpairmentPoint> Points,
    double? NoiseFloorDb,
    ImpairmentRunMetadata? Run = null,
    IReadOnlyList<string>? NoiseStreamFiles = null,
    IReadOnlyList<double>? NoiseStreamJoinOffsetsSeconds = null,
    double? NoiseStreamClipRmsSpreadDb = null,
    bool? NoiseStreamClipRmsSpreadWarning = null,
    string? NoiseStreamDay = null,
    string? NoiseStreamReceiver = null);

/// <summary>Explicit opt-in, not directory-presence-gated (unlike <see cref="RequiresOtaRecordingsFactAttribute"/>)
/// -- this harness is fully synthetic and deliberately slow, so it must never silently run on an
/// ordinary test pass. Mirrors <c>RequiresTxFixtureRegenerationFactAttribute</c>'s dynamic-Skip
/// pattern.</summary>
public sealed class RequiresImpairmentSweepFactAttribute : FactAttribute
{
    public RequiresImpairmentSweepFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCANLINE_RUN_IMPAIRMENT_SWEEP") != "1")
        {
            Skip = "Set SCANLINE_RUN_IMPAIRMENT_SWEEP=1 to run the (slow, up to 860 encode+decode passes) impairment sweep.";
        }
    }
}

public sealed class RequiresImpairmentComparisonFactAttribute : FactAttribute
{
    public RequiresImpairmentComparisonFactAttribute()
    {
        var before = Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_BEFORE");
        var after = Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_AFTER");
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after) || !Directory.Exists(before) || !Directory.Exists(after))
        {
            Skip = "Set SCANLINE_IMPAIRMENT_BEFORE and SCANLINE_IMPAIRMENT_AFTER to two existing impairment-reports/<timestamp> directories to compare.";
        }
    }
}
