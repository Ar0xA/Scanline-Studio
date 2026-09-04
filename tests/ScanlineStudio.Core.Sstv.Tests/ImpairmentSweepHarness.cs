using System.Globalization;
using System.Text.Json;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// A reusable, persistent "did this change help or hurt decode quality" instrument -- decoupled
/// entirely from legacy parity. Measures this port's decode against the SOURCE IMAGE (the one ground
/// truth nobody disputes) at a sweep of controlled, exactly-known noise levels, for every mode that
/// has a real golden-vector source image already in this repo. Extends
/// <see cref="NoiseRobustnessTests"/>'s own calibrated-noise-injection technique (which this file
/// duplicates rather than shares, matching this test suite's own established small-duplication
/// convention -- see that file's own `AddNoiseAtSnr`/`ComputeRms`/`NextGaussian`) from 2 modes and
/// console-only output to 13 modes (8 with real golden-vector source images, 5 synthetic -- the other
/// narrow MN/MC modes, added after mn110's own noise floor came back dramatically worse than every
/// other tested mode) and a persisted, diffable report.
///
/// Why this exists: the H3/HBPFN parity item (decoder_quality_improvement.md §5.1) shipped a full
/// review pass and only got measured against legacy's own decode after the fact -- and that
/// measurement, even when it existed, answered "does this match legacy," not "does this improve
/// reception." This harness answers the second question directly: run it before a DSP experiment,
/// run it again after, diff the two reports. A curve that moves toward zero at more SNR levels is a
/// real quality improvement; legacy's own behavior is not part of the comparison at all.
///
/// Gated behind an explicit env var, not directory presence (unlike <see cref="OtaBaselineHarness"/>):
/// this harness is fully synthetic (no external local files needed) and deliberately slow (8 modes x
/// 10 SNR levels = up to 80 encode+decode passes), so it must not silently run on every ordinary test
/// pass -- mirrors <c>RequiresTxFixtureRegenerationFactAttribute</c>'s explicit-opt-in convention.
/// </summary>
public sealed class ImpairmentSweepHarness
{
    private const string FixtureDir = "Fixtures/GoldenVectors";
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    // Same 8 modes as GoldenVectorTests.Fixtures -- real source images already exist in this repo at
    // the exact canvas size each mode needs, more representative than a fresh gradient per mode, and
    // keeps this harness's mode set intuitively cross-referenceable against the golden-vector suite.
    private static readonly (string ModeId, string SourceBmp, int PictureHeight)[] Modes =
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

    // The other 5 narrow (MN/MC) modes -- mn110's noise floor came back dramatically worse than every
    // other tested mode (20dB vs 0-3dB), and every mode in this family shares the same current code
    // path (no H3/HBPFN, permanently on the wide H2 search filter even post-lock), so the natural
    // question is whether this is an mn110-specific artifact or a whole-family characteristic. No real
    // golden-vector source bmp exists for any of these 5 (only mn110 has a real capture), so these use
    // a synthetic gradient image at each mode's own canvas size instead -- same technique
    // NoiseRobustnessTests already uses, not a new methodology.
    // All 5 share ImageWidth=320/ImageHeight=256 (SstvModeRegistry's CreateMnFamilyMode/
    // CreateMcFamilyMode -- confirmed directly, not assumed), full canvas is real content, no crop.
    private static readonly string[] SyntheticNarrowModeIds = ["mn73", "mn140", "mc110", "mc140", "mc180"];

    // Same sweep as NoiseRobustnessTests -- descending SNR, deterministic seed.
    private static readonly double[] SnrLevelsDb = [40.0, 30.0, 25.0, 20.0, 16.0, 12.0, 9.0, 6.0, 3.0, 0.0];
    private const int NoiseSeed = 12345;

    [RequiresImpairmentSweepFact]
    public async Task RunImpairmentSweep_ProducesBaselineReport()
    {
        var runDir = Path.Combine(FindRepoRoot(), "impairment-reports", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);

        var reports = new List<ImpairmentModeReport>();
        foreach (var (modeId, sourceBmp, pictureHeight) in Modes)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] starting {modeId}...");
            var source = BmpFile.Read(Path.Combine(FixtureDir, sourceBmp));
            reports.Add(await MeasureModeAsync(modeId, source, pictureHeight));
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] finished {modeId}.");
        }

        foreach (var modeId in SyntheticNarrowModeIds)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] starting {modeId}...");
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            var source = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
            reports.Add(await MeasureModeAsync(modeId, source, mode.ImageHeight));
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] finished {modeId}.");
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

    private static IReadOnlyList<ImpairmentModeReport> ReadReport(string runDir)
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
        foreach (var snrDb in SnrLevelsDb)
        {
            var noisySamples = AddNoiseAtSnr(cleanSamples, snrDb, NoiseSeed);

            var decoder = new AnalogFmSstvDecoder(encoder.SampleRate);
            SstvModeDefinition? detectedMode = null;
            IImageSource? decodedImage = null;
            decoder.ModeDetected += m => detectedMode = m;
            decoder.LineDecoded += update => decodedImage = update.Image;
            decoder.PushSamples(noisySamples);

            var decodedCorrectly = detectedMode is not null && detectedMode.Id == mode.Id && decodedImage is not null;
            if (!decodedCorrectly)
            {
                points.Add(new ImpairmentPoint(snrDb, double.NaN, false));
                continue;
            }

            var actual = CropToTop(decodedImage!, pictureHeight);
            var delta = MeasureAveragePerChannelDelta(source, actual, pictureHeight);
            points.Add(new ImpairmentPoint(snrDb, delta, true));

            // Same "usable decode" bar as NoiseRobustnessTests, kept in sync deliberately -- this
            // harness's "noise floor" should mean the same thing that file's already-established one
            // does, not a second, differently-calibrated number with the same name.
            if (delta <= 30.0)
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

    private static IImageSource CropToTop(IImageSource image, int pictureHeight)
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

    private static double MeasureAveragePerChannelDelta(IImageSource expected, IImageSource actual, int height)
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
    private static ArrayImageSource CreateGradientTestImage(int width, int height)
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

    private static string FindRepoRoot()
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

public sealed record ImpairmentPoint(double SnrDb, double Delta, bool DecodedCorrectly);

public sealed record ImpairmentModeReport(string ModeId, IReadOnlyList<ImpairmentPoint> Points, double? NoiseFloorDb);

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
            Skip = "Set SCANLINE_RUN_IMPAIRMENT_SWEEP=1 to run the (slow, ~80 encode+decode passes) impairment sweep.";
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
