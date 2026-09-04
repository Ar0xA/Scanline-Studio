using System.Globalization;
using System.Text.Json;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// decoder_quality_improvement.md's Phase 0: decodes real over-the-air captures with today's
/// decoder and writes a report + the decoded images as a "before" reference, so a later DSP change
/// (Phase 1's parity tranche) can be compared against what the receiver produced before that change.
///
/// Deliberately NOT a pass/fail test. Unlike <see cref="GoldenVectorTests"/>, there is no known-good
/// expected image for any of these files -- they're real off-air recordings of unknown content, some
/// of them (per the operator who captured them) pure noise from an over-low squelch trigger. A
/// "NO LOCK" result on a noisy file is a correct, informative outcome, not a test failure. This file
/// only ever asserts that the run itself completed and produced output, never anything about decode
/// content or quality.
///
/// Gated behind <see cref="RequiresOtaRecordingsFactAttribute"/>: the `ota-recordings/` directory is
/// local-only and gitignored (real off-air recordings of unknown/private origin don't fit this
/// project's `LICENSES.md` "captured by running the legacy binary" fixture pattern, so they're never
/// committed) -- this test is invisible on any machine/CI leg without that directory present.
/// </summary>
public sealed class OtaBaselineHarness
{
    private static readonly JsonSerializerOptions ReportJsonOptions = new() { WriteIndented = true };

    [RequiresOtaRecordingsFact]
    public void DecodeOtaRecordings_ProducesBaselineReport()
    {
        var recordingsDir = Path.Combine(FindRepoRoot(), "ota-recordings");
        var wavFiles = Directory.GetFiles(recordingsDir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToArray();

        // The attribute only confirms the directory exists -- an empty directory would otherwise
        // report a hollow "success" (zero files processed, zero problems found).
        Assert.NotEmpty(wavFiles);

        var runDir = Path.Combine(recordingsDir, "baseline-reports", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);

        var reports = wavFiles.Select(wavPath => DecodeAndReport(wavPath, runDir)).ToList();

        var json = JsonSerializer.Serialize(reports, ReportJsonOptions);
        File.WriteAllText(Path.Combine(runDir, "report.json"), json);

        Console.WriteLine(
            $"OTA baseline: {reports.Count} files, " +
            $"{reports.Count(r => r.Locks.Count == 0)} with no lock, " +
            $"{reports.Count(r => r.RxBufferEverDegraded)} with RxBufferDegraded, " +
            $"written to {runDir}.");
    }

    /// <summary>Compares two prior <see cref="DecodeOtaRecordings_ProducesBaselineReport"/> runs and
    /// reports a per-file pixel delta between matching decoded images. This is a SELF-REFERENTIAL
    /// regression delta, not an accuracy delta -- there is no ground truth for these files, so a
    /// small number here means "this change didn't move the decode much," never "the decode is
    /// correct." Read the JSON/BMP output yourself; this test only ever asserts the comparison ran.</summary>
    [RequiresOtaComparisonFact]
    public void CompareOtaBaselineRuns_ReportsSelfReferentialDelta()
    {
        var beforeDir = Environment.GetEnvironmentVariable("SCANLINE_OTA_BASELINE_BEFORE")!;
        var afterDir = Environment.GetEnvironmentVariable("SCANLINE_OTA_BASELINE_AFTER")!;

        var before = ReadReport(beforeDir);
        var after = ReadReport(afterDir);
        var afterByFile = after.ToDictionary(r => r.FileName);

        var comparisons = new List<string>();
        foreach (var beforeReport in before)
        {
            if (!afterByFile.TryGetValue(beforeReport.FileName, out var afterReport))
            {
                comparisons.Add($"{beforeReport.FileName}: no matching file in the after-run.");
                continue;
            }

            var pairCount = Math.Min(beforeReport.Locks.Count, afterReport.Locks.Count);
            for (var i = 0; i < pairCount; i++)
            {
                var beforeLock = beforeReport.Locks[i];
                var afterLock = afterReport.Locks[i];
                if (beforeLock.ModeId != afterLock.ModeId)
                {
                    comparisons.Add($"{beforeReport.FileName} lock {i}: mode changed {beforeLock.ModeId} -> {afterLock.ModeId} (not comparable pixel-for-pixel).");
                    continue;
                }

                if (beforeLock.ImageFileName is null || afterLock.ImageFileName is null)
                {
                    comparisons.Add($"{beforeReport.FileName} lock {i} ({beforeLock.ModeId}): no image on one side, not comparable.");
                    continue;
                }

                var beforeImage = BmpFile.Read(Path.Combine(beforeDir, beforeLock.ImageFileName));
                var afterImage = BmpFile.Read(Path.Combine(afterDir, afterLock.ImageFileName));
                if (beforeImage.Width != afterImage.Width || beforeImage.Height != afterImage.Height)
                {
                    comparisons.Add($"{beforeReport.FileName} lock {i} ({beforeLock.ModeId}): image size changed, not comparable.");
                    continue;
                }

                var delta = MeasureAveragePerChannelDelta(beforeImage, afterImage);
                comparisons.Add($"{beforeReport.FileName} lock {i} ({beforeLock.ModeId}): self-referential delta {delta:F2} (didn't move much vs. is-correct are NOT the same claim).");
            }

            if (beforeReport.Locks.Count != afterReport.Locks.Count)
            {
                comparisons.Add($"{beforeReport.FileName}: lock count changed {beforeReport.Locks.Count} -> {afterReport.Locks.Count}.");
            }
        }

        foreach (var line in comparisons)
        {
            Console.WriteLine(line);
        }

        Assert.NotEmpty(comparisons);
    }

    private static IReadOnlyList<OtaFileReport> ReadReport(string runDir)
    {
        var json = File.ReadAllText(Path.Combine(runDir, "report.json"));
        return JsonSerializer.Deserialize<List<OtaFileReport>>(json, ReportJsonOptions) ?? [];
    }

    private static OtaFileReport DecodeAndReport(string wavPath, string outputDir)
    {
        var (samples, sampleRate) = WavFile.Read(wavPath);
        var decoder = new AnalogFmSstvDecoder(sampleRate);

        var locks = new List<LockTracker>();
        var stationIds = new List<string>();
        var totalRestarts = 0;
        double peakLevel = 0;
        var overdrivenObservations = 0;
        var rxBufferEverDegraded = false;
        var syncTransitions = new List<SstvSyncSource>();

        decoder.ModeDetected += mode =>
        {
            locks.Add(new LockTracker(mode, decoder.TotalSamplesReceived));
        };

        decoder.DecodeRestarted += _ =>
        {
            totalRestarts++;
            if (locks.Count > 0)
            {
                locks[^1].Restarts++;
            }
        };

        decoder.StationIdDecoded += info =>
        {
            if (info.Callsign is not null)
            {
                stationIds.Add(info.Callsign);
            }
            else if (info.NrText is not null)
            {
                stationIds.Add(info.NrText);
            }
        };

        decoder.LineDecoded += update =>
        {
            if (locks.Count > 0)
            {
                var current = locks[^1];
                current.MaxLine = Math.Max(current.MaxLine, update.Line);
                current.LastImage = update.Image;
                current.LastSlantPpm = decoder.SlantPpm;
                current.LastAnchorLagSamples = decoder.AnchorLagSamples;
                current.LastSyncOffsetSamples = decoder.SyncOffsetSamples;
            }

            peakLevel = Math.Max(peakLevel, decoder.SignalPeakLevel);
            if (decoder.IsLevelOverdriven)
            {
                overdrivenObservations++;
            }

            if (decoder.RxBufferDegraded)
            {
                rxBufferEverDegraded = true;
            }

            if (syncTransitions.Count == 0 || syncTransitions[^1] != decoder.SyncSource)
            {
                syncTransitions.Add(decoder.SyncSource);
            }
        };

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        decoder.PushSamples(samples);
        stopwatch.Stop();

        var fileStem = Path.GetFileNameWithoutExtension(wavPath);
        var lockRecords = new List<OtaLockRecord>();
        for (var i = 0; i < locks.Count; i++)
        {
            var lockTracker = locks[i];
            string? imageFileName = null;
            if (lockTracker.LastImage is not null)
            {
                imageFileName = $"{fileStem}_lock{i}_{lockTracker.Mode.Id}.bmp";
                BmpFile.Write(Path.Combine(outputDir, imageFileName), Snapshot(lockTracker.LastImage));
            }

            // ImageHeight is an approximation of "expected total lines" for completion purposes --
            // several color encodings interleave/pair rows, so this is a rough diagnostic signal,
            // not a strict pass/fail measure (this harness never asserts on it).
            var completed = lockTracker.MaxLine + 1 >= lockTracker.Mode.ImageHeight;

            lockRecords.Add(new OtaLockRecord(
                lockTracker.Mode.Id,
                lockTracker.DetectedAtSample,
                lockTracker.MaxLine + 1,
                completed,
                lockTracker.Restarts,
                lockTracker.LastSlantPpm,
                lockTracker.LastAnchorLagSamples,
                lockTracker.LastSyncOffsetSamples,
                imageFileName));
        }

        var classification = lockRecords.Count == 0
            ? "NO LOCK"
            : string.Join("; ", lockRecords.Select(l => $"LOCKED ({l.ModeId}, {l.LinesDecoded} lines, {l.RestartsDuringThisLock} restarts{(l.Completed ? "" : ", incomplete")})"));

        return new OtaFileReport(
            Path.GetFileName(wavPath),
            samples.Length / (double)sampleRate,
            sampleRate,
            classification,
            lockRecords,
            totalRestarts,
            peakLevel,
            overdrivenObservations,
            rxBufferEverDegraded,
            stationIds,
            syncTransitions.Select(s => s.ToString()).ToList(),
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private sealed class LockTracker(SstvModeDefinition mode, long detectedAtSample)
    {
        public SstvModeDefinition Mode { get; } = mode;
        public long DetectedAtSample { get; } = detectedAtSample;
        public int MaxLine { get; set; } = -1;
        public int Restarts { get; set; }
        public IImageSource? LastImage { get; set; }
        public double? LastSlantPpm { get; set; }
        public int? LastAnchorLagSamples { get; set; }
        public int? LastSyncOffsetSamples { get; set; }
    }

    /// <summary>Deep-copies pixels: <see cref="DecodedImageUpdate.Image"/> is a live, mutable alias
    /// of the decoder's own buffer (same hazard <see cref="GoldenVectorTests.Snapshot"/> guards
    /// against), and this harness keeps writing to it across many more <c>LineDecoded</c> events
    /// than a single-transmission golden vector does.</summary>
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

    private static double MeasureAveragePerChannelDelta(IImageSource left, IImageSource right)
    {
        double totalDelta = 0;
        var sampleCount = 0;

        for (var y = 0; y < left.Height; y++)
        {
            var leftLine = left.GetScanline(y);
            var rightLine = right.GetScanline(y);

            for (var x = 0; x < left.Width; x++)
            {
                totalDelta += Math.Abs(leftLine[x].R - rightLine[x].R);
                totalDelta += Math.Abs(leftLine[x].G - rightLine[x].G);
                totalDelta += Math.Abs(leftLine[x].B - rightLine[x].B);
                sampleCount += 3;
            }
        }

        return totalDelta / sampleCount;
    }

    /// <summary>Mirrors <c>TxCaptureFixtureGenerator.FindRepoRoot</c> -- duplicated rather than
    /// shared, matching this test suite's own established convention of small per-file helper
    /// duplication (e.g. <c>NoiseRobustnessTests</c>' own copy of the pixel-delta measurement)
    /// over introducing a shared utility for a ~10-line method.</summary>
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

/// <summary>One decoded lock (a <c>ModeDetected</c> event) within a single OTA file's decode. A file
/// can produce zero, one, or several of these -- zero means the decoder never locked onto anything
/// (a correct outcome for a noise-only capture), and several means multiple lock attempts happened
/// within one recording (e.g. a spurious lock followed by the real one, or several images back to
/// back).</summary>
public sealed record OtaLockRecord(
    string ModeId,
    long DetectedAtSample,
    int LinesDecoded,
    bool Completed,
    int RestartsDuringThisLock,
    double? FinalSlantPpm,
    int? FinalAnchorLagSamples,
    int? FinalSyncOffsetSamples,
    string? ImageFileName);

public sealed record OtaFileReport(
    string FileName,
    double DurationSeconds,
    int SampleRate,
    string Classification,
    IReadOnlyList<OtaLockRecord> Locks,
    int TotalRestarts,
    double PeakSignalLevel,
    int OverdrivenObservations,
    bool RxBufferEverDegraded,
    IReadOnlyList<string> DecodedStationIds,
    IReadOnlyList<string> SyncSourceTransitions,
    double DecodeWallClockMs);

/// <summary>Skips <see cref="OtaBaselineHarness.DecodeOtaRecordings_ProducesBaselineReport"/> when
/// the local, gitignored `ota-recordings/` directory isn't present -- mirrors
/// <c>RequiresTxFixtureRegenerationFactAttribute</c>'s dynamic-<see cref="FactAttribute.Skip"/>
/// pattern. Directory presence IS the opt-in signal here (no env var needed): nobody but the
/// operator who captured these files has this directory, so an honest SKIP with an actionable
/// message is exactly right rather than a silent no-op green pass.</summary>
public sealed class RequiresOtaRecordingsFactAttribute : FactAttribute
{
    public RequiresOtaRecordingsFactAttribute()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ScanlineStudio.sln")))
        {
            dir = dir.Parent;
        }

        var recordingsDir = dir is null ? null : Path.Combine(dir.FullName, "ota-recordings");
        if (recordingsDir is null || !Directory.Exists(recordingsDir))
        {
            Skip = "ota-recordings/ not found at repo root -- this is a local-only, gitignored directory of real off-air captures, not a committed fixture. Nothing to decode on this machine.";
        }
    }
}

/// <summary>Skips <see cref="OtaBaselineHarness.CompareOtaBaselineRuns_ReportsSelfReferentialDelta"/>
/// unless both comparison directories are supplied via env var, matching
/// <c>RequiresTxFixtureRegenerationFactAttribute</c>'s explicit-opt-in convention -- an accidental
/// run with unset/wrong paths would otherwise fail with a raw <see cref="NullReferenceException"/>
/// instead of an honest, actionable skip.</summary>
public sealed class RequiresOtaComparisonFactAttribute : FactAttribute
{
    public RequiresOtaComparisonFactAttribute()
    {
        var before = Environment.GetEnvironmentVariable("SCANLINE_OTA_BASELINE_BEFORE");
        var after = Environment.GetEnvironmentVariable("SCANLINE_OTA_BASELINE_AFTER");
        if (string.IsNullOrEmpty(before) || string.IsNullOrEmpty(after) || !Directory.Exists(before) || !Directory.Exists(after))
        {
            Skip = "Set SCANLINE_OTA_BASELINE_BEFORE and SCANLINE_OTA_BASELINE_AFTER to two existing ota-recordings/baseline-reports/<timestamp> directories to compare.";
        }
    }
}
