using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>T2 + T3 of the per-mode demodulator programme, run as ONE sweep because they share the
/// same confound.
///
/// Round 1 of this harness reported zero-crossing winning 540 of 540 comparisons across 27 modes.
/// That result is RETRACTED: the +/-4 px alignment search saturated at its boundary in 322 of 540
/// zero-crossing arms, so no arm was scored at its own optimum and the arm that hit the boundary
/// most often was the one declared the winner. A follow-up probe with a +/-24 search showed the true
/// clean-signal shift is a CONSTANT TIME offset of roughly 0.7 ms -- 5 to 7 px on robot-36 at
/// 7273 px/s, 1 to 2 px on martin-m1 at 2185 px/s -- and that the per-arm search wanders at low SNR
/// because a noisy picture lets any shift look good.
///
/// TWO FIXES, both load-bearing:
///
/// 1. THE ALIGNMENT IS MEASURED ONCE PER ARM PER MODE, ON A CLEAN SIGNAL, then held FIXED at every
///    SNR. That still equalizes genuine group-delay differences between demodulators, which is why
///    round 1 searched at all, but it stops a noisy arm from fitting the noise.
///
/// 2. A FOURTH AND FIFTH ARM SEPARATE THE DEMODULATOR FROM ITS SMOOTHER CUTOFF. Round 1 could not:
///    zero-crossing and PLL both smooth at 900 Hz while Hilbert is fixed at 1800 Hz, and the ranking
///    followed the cutoff exactly. Hilbert now runs at 900 Hz and at a per-mode cutoff as well, and
///    zero-crossing runs at a per-mode cutoff too -- so "does the per-mode cutoff help" is answered
///    for the round-1 winner, not only for the default.
///
/// The per-mode cutoff is half the mode's own pixel rate. The decoder reads one sample per pixel, so
/// noise above that folds into the picture and anything below it discards real detail. It spans
/// 463 Hz (Scottie DX) to 3636 Hz (robot-36), and 1800 Hz sits near the middle -- Scottie S2 wants
/// 1817 Hz, which is very likely where legacy's constant came from. NOTE that a probe already argued
/// AGAINST this prescription on 4 of 5 modes, so it is here to be refuted, not confirmed.
///
/// Outcome map: Hilbert-at-900 catches up, so it was the cutoff. Zero-crossing still wins, so it is
/// the topology. Per-mode beats both fixed cutoffs, so the cutoff should be computed. Nothing
/// separates, so round 1 was the alignment artifact.
///
/// ORIGINAL ROUND-1 HEADER FOLLOWS.
///
/// T2 of the per-mode demodulator programme: does the demodulator ranking hold up at scale?
///
/// A one-realization probe found zero-crossing beating the Hilbert default on 4 of 5 modes and PLL
/// beating it by 30% on a narrow mode. That is the same n=1 shape that produced a retracted headline
/// finding earlier in this workstream, so it is replicated here before anything is concluded.
///
/// TWO CORRECTIONS THE PROBE NEEDED, both load-bearing:
///
/// 1. ALIGNMENT EQUALIZATION. The three demodulators have different group delays and only Hilbert
///    gets a sync-anchor correction, leaving a 2-4 sample differential — up to ~1.3 pixels on
///    robot-36. On natural imagery a one-pixel shift typically dominates moderate noise, so an
///    unaligned comparison can FLIP a ranking rather than merely offset it. Every arm is therefore
///    scored at its own best horizontal alignment, and the chosen shift is recorded.
///
/// 2. A DISTRIBUTION-AWARE METRIC. Zero-crossing hard-clamps its output before smoothing, which
///    mechanically caps its error tail under a mean-absolute metric. Reporting only the mean would
///    reward that clamp as if it were quality. Median and an outlier count are recorded alongside.
///
/// Also recorded per arm: which demodulator ran. AFC's measurement source forks by demodulator type,
/// so an AFC difference could otherwise be misread as a demodulator difference.
///
/// GATE: a demodulator counts as better for a mode only if it wins across a majority of realizations
/// at a majority of SNR points. A single-realization win is not evidence.</summary>
public sealed class DemodulatorRankingHarness
{
    private const int SampleRate = 44100;
    private const double H2LowHz = 400;
    private const double H2HighHz = 2500;
    private const int MeasurementTaps = 1023;

    // Searched ONLY on the clean signal, where the answer is a real group delay rather than a fit to
    // noise. Round 1's +/-4 saturated; the measured worst case is 5-7 px on robot-36, so this leaves
    // real headroom.
    private const int MaxAlignShift = 24;

    // A line counts as an outlier when its own mean delta exceeds this. Chosen as the usability bar
    // used elsewhere in the bench so the two numbers mean the same thing.
    private const double OutlierLineDelta = 30.0;

    /// <param name="CutoffHz">Output smoother cutoff. <see langword="null"/> means "half this mode's
    /// own pixel rate", computed per mode.</param>
    private sealed record Arm(string Label, DemodType Demod, double? CutoffHz);

    private static readonly Arm[] Arms =
    [
        new("hilbert-1800", DemodType.Hilbert, 1800.0),   // today's default
        new("hilbert-900", DemodType.Hilbert, 900.0),     // the cutoff-vs-topology control
        new("hilbert-permode", DemodType.Hilbert, null),  // half the mode's own pixel rate
        new("pll-900", DemodType.Pll, 900.0),
        new("zerocross-900", DemodType.ZeroCrossing, 900.0),
        new("zerocross-permode", DemodType.ZeroCrossing, null),
    ];
    private static readonly double[] SnrLevelsDb = [20.0, 16.0, 12.0, 9.0];
    private static readonly int[] Seeds = [12345, 67890, 24680, 13579, 55555];

    [RequiresDemodulatorRankingFact]
    public async Task RankDemodulatorsAcrossModes()
    {
        var corpus = RealNoiseCorpus.Load(Environment.GetEnvironmentVariable("SCANLINE_REAL_NOISE_DIR")!);
        var h2 = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SampleRate, MeasurementTaps);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var runDir = Path.Combine(
            ImpairmentSweepHarness.FindRepoRoot(),
            "impairment-reports",
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-demod-ranking"));
        Directory.CreateDirectory(runDir);
        var progress = Path.Combine(runDir, "progress.log");

        // Capped by memory, not by cores, and re-derived PER MODE below. Each in-flight item holds a
        // noisy copy of the whole reception plus a decoder that retains it. Round 1 ran a flat 4 and
        // was OOM-killed at 11 GB resident on a long PD mode.
        var requestedParallelism = int.TryParse(
            Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_PARALLELISM"), out var requested) && requested > 0
            ? requested
            : 4;

        // Measured from the round-1 kill: 4 concurrent items on a ~13.2M-sample mode reached ~11 GB,
        // so one item costs roughly 60x its own sample count in bytes. This budget keeps the peak
        // near 9 GB, which left real headroom on the machine that died.
        const long MemoryBudgetBytes = 9L * 1024 * 1024 * 1024;
        const long BytesPerSamplePerItem = 60 * sizeof(float);

        var modeIds = (Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_MODES")
                       ?? string.Join(',', SstvModeRegistry.All.Select(m => m.Id)))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Log(progress, $"{modeIds.Length} modes x {Seeds.Length} seeds x {SnrLevelsDb.Length} SNR x {Arms.Length} arms");

        var longest = SstvModeRegistry.All.Where(m => modeIds.Contains(m.Id)).Max(m => m.LineDurationMs * m.ImageHeight / 1000.0);
        var noiseSeconds = (longest * 1.25) + 15.0;
        Log(progress, $"preparing {Seeds.Length} noise realizations of {noiseSeconds:F0}s...");
        var noise = Seeds
            .Select((seed, ordinal) => RealNoiseStreamBuilder.Build(corpus, seed, ordinal, noiseSeconds))
            .ToList();
        Log(progress, "noise ready.");

        var results = new List<DemodModeResult>();
        foreach (var modeId in modeIds)
        {
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            var fixture = ImpairmentSweepHarness.RealFixtureModes.FirstOrDefault(f => f.ModeId == modeId);
            IImageSource source = fixture.SourceBmp is not null
                ? BmpFile.Read(Path.Combine(ImpairmentSweepHarness.FixtureDir, fixture.SourceBmp))
                : mode.ColorEncoding == ColorEncoding.MonoAveragedPaired
                    ? ImpairmentSweepHarness.CreateGrayscaleGradientTestImage(mode.ImageWidth, mode.ImageHeight)
                    : ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
            var height = fixture.SourceBmp is not null ? fixture.PictureHeight : mode.ImageHeight;

            var encoded = new List<float>();
            await foreach (var sample in encoder.EncodeAsync(mode, source))
            {
                encoded.Add(sample);
            }

            var clean = encoded.ToArray();
            encoded.Clear();
            var signalInBand = FirFilter.BandRms(clean, h2);

            // Half the mode's own pixel rate. The shortest scan segment is the right one: robot-36's
            // chroma channels are half-width, so its luma segment would understate the real rate.
            var shortestScanMs = mode.LineSegments.OfType<ScanSegment>().Min(seg => seg.DurationMs);
            var perModeCutoffHz = mode.ImageWidth / (shortestScanMs / 1000.0) / 2.0;

            // Measured ONCE, on the clean signal, then held fixed at every SNR below. This is the arm's
            // real group delay; searching per point instead lets a noisy arm fit the noise.
            var alignment = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var arm in Arms)
            {
                var cut = arm.CutoffHz ?? perModeCutoffHz;
                alignment[arm.Label] = MeasureCleanAlignment(arm, cut, clean, source, height, mode.Id);
            }

            Log(progress, $"{modeId}: per-mode cutoff {perModeCutoffHz:F0}Hz, clean shifts " +
                          string.Join(" ", Arms.Select(a => $"{a.Label}={alignment[a.Label]}")));

            // The three demodulators share one noisy buffer, so the only thing that differs between
            // them is the demodulator itself.
            var work = (from snrDb in SnrLevelsDb
                        from pair in noise.Zip(Seeds)
                        select (SnrDb: snrDb, Stream: pair.First, Seed: pair.Second)).ToList();

            var affordable = (int)Math.Max(1, MemoryBudgetBytes / Math.Max(1, clean.Length * BytesPerSamplePerItem));
            var parallelism = Math.Min(requestedParallelism, affordable);
            if (parallelism < requestedParallelism)
            {
                Log(progress, $"{modeId}: parallelism {parallelism} (memory-capped, {clean.Length / 1_000_000.0:F1}M samples)");
            }

            var collected = new ConcurrentBag<DemodPoint>();
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = parallelism }, item =>
            {
                if (item.Stream.Samples.Length < clean.Length)
                {
                    throw new InvalidOperationException($"{modeId} needs a longer noise realization.");
                }

                var scale = signalInBand / Math.Pow(10.0, item.SnrDb / 20.0)
                            / FirFilter.BandRms(item.Stream.Samples[..clean.Length], h2);
                var noisy = new float[clean.Length];
                for (var i = 0; i < clean.Length; i++)
                {
                    noisy[i] = (float)(clean[i] + (item.Stream.Samples[i] * scale));
                }

                foreach (var arm in Arms)
                {
                    var cut = arm.CutoffHz ?? perModeCutoffHz;
                    collected.Add(Measure(modeId, arm, cut, alignment[arm.Label], item.Seed, item.SnrDb, noisy, source, height, mode.Id));
                }
            });

            // Sorted so the report is byte-identical whatever order the arms finished in.
            var points = collected
                .OrderByDescending(p => p.SnrDb)
                .ThenBy(p => p.Seed)
                .ThenBy(p => p.Demodulator, StringComparer.Ordinal)
                .ToList();

            results.Add(new DemodModeResult(modeId, points));
            File.WriteAllText(
                Path.Combine(runDir, "report.json"),
                JsonSerializer.Serialize(results, ImpairmentSweepHarness.ReportJsonOptions));
            Log(progress, $"finished {modeId} ({results.Count}/{modeIds.Length}): {Summarize(points)}");
        }

        Log(progress, $"done: {results.Count} modes -> {runDir}");
    }

    private static DemodPoint Measure(
        string modeId, Arm arm, double cutoffHz, int shift, int seed, double snrDb,
        float[] noisy, IImageSource source, int height, string expectedModeId)
    {
        var image = Decode(arm, cutoffHz, noisy, expectedModeId);
        if (image is null)
        {
            return new DemodPoint(modeId, arm.Label, seed, snrDb, false, null, null, null, null, cutoffHz);
        }

        var actual = ImpairmentSweepHarness.CropToTop(image, height);
        var (mean, median, outliers) = ScoreAtShift(source, actual, height, shift);
        return new DemodPoint(modeId, arm.Label, seed, snrDb, true, mean, median, outliers, shift, cutoffHz);
    }

    private static IImageSource? Decode(Arm arm, double cutoffHz, float[] samples, string expectedModeId)
    {
        // Only Hilbert takes hilbertOutputCutoffHz; PLL and zero-crossing have their own parameters.
        var decoder = arm.Demod switch
        {
            DemodType.Hilbert => new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.Hilbert, hilbertOutputCutoffHz: cutoffHz),
            DemodType.Pll => new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.Pll, pllOutputCutoffHz: cutoffHz),
            _ => new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.ZeroCrossing, zeroCrossingOutputCutoffHz: cutoffHz),
        };

        SstvModeDefinition? detected = null;
        IImageSource? image = null;
        decoder.ModeDetected += m => detected = m;
        decoder.LineDecoded += u => image = u.Image;
        decoder.PushSamples(samples);
        return detected?.Id == expectedModeId && image is not null ? image : null;
    }

    /// <summary>The arm's own group delay, in pixels, measured on the clean signal. Returns 0 if the
    /// clean signal somehow fails to decode -- that is a harness fault worth seeing in the report as a
    /// zero shift rather than a crash three hours in.</summary>
    private static int MeasureCleanAlignment(Arm arm, double cutoffHz, float[] clean, IImageSource source, int height, string expectedModeId)
    {
        var image = Decode(arm, cutoffHz, clean, expectedModeId);
        if (image is null)
        {
            return 0;
        }

        var actual = ImpairmentSweepHarness.CropToTop(image, height);
        var best = 0;
        var bestMean = double.MaxValue;
        for (var shift = -MaxAlignShift; shift <= MaxAlignShift; shift++)
        {
            var (mean, _, _) = ScoreAtShift(source, actual, height, shift);
            if (mean < bestMean)
            {
                bestMean = mean;
                best = shift;
            }
        }

        return best;
    }

    /// <summary>Scores at one GIVEN shift. Round 1 searched here instead, which let a noisy arm pick
    /// whichever alignment flattered it; the shift now comes from the clean-signal pass.</summary>
    private static (double Mean, double Median, int OutlierLines) ScoreAtShift(
        IImageSource source, IImageSource actual, int height, int shift)
    {
        var perLine = new double[height];
        for (var y = 0; y < height; y++)
        {
            var src = source.GetScanline(y);
            var got = actual.GetScanline(y);
            double total = 0;
            var counted = 0;
            for (var x = 0; x < source.Width; x++)
            {
                var sx = x + shift;
                if (sx < 0 || sx >= source.Width)
                {
                    continue;
                }

                total += Math.Abs(src[x].R - got[sx].R) + Math.Abs(src[x].G - got[sx].G) + Math.Abs(src[x].B - got[sx].B);
                counted += 3;
            }

            perLine[y] = counted == 0 ? 0 : total / counted;
        }

        var sorted = (double[])perLine.Clone();
        Array.Sort(sorted);
        return (perLine.Average(), sorted[sorted.Length / 2], perLine.Count(v => v > OutlierLineDelta));
    }

    private static string Summarize(List<DemodPoint> points)
    {
        var byDemod = points.Where(p => p.Decoded).GroupBy(p => p.Demodulator)
            .Select(g => $"{g.Key}={g.Average(p => p.MeanDelta!.Value):F1}");
        return string.Join(" ", byDemod);
    }

    private static void Log(string path, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        File.AppendAllText(path, line + Environment.NewLine);
        Console.WriteLine(line);
    }
}

/// <param name="AlignmentShift">Horizontal shift, in pixels, applied to this arm. Measured once per
/// arm per mode on the CLEAN signal, then held fixed at every SNR.</param>
/// <param name="OutputCutoffHz">The smoother cutoff this arm actually ran at, so a per-mode arm's
/// computed value is in the report rather than only in the log.</param>
public sealed record DemodPoint(
    string ModeId,
    string Demodulator,
    int Seed,
    double SnrDb,
    bool Decoded,
    double? MeanDelta,
    double? MedianLineDelta,
    int? OutlierLines,
    int? AlignmentShift,
    double? OutputCutoffHz);

public sealed record DemodModeResult(string ModeId, IReadOnlyList<DemodPoint> Points);

public sealed class RequiresDemodulatorRankingFactAttribute : FactAttribute
{
    public RequiresDemodulatorRankingFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable("SCANLINE_REAL_NOISE_DIR");
        if (Environment.GetEnvironmentVariable("SCANLINE_RUN_DEMOD_RANKING") != "1")
        {
            Skip = "Set SCANLINE_RUN_DEMOD_RANKING=1 (and SCANLINE_REAL_NOISE_DIR) to run the demodulator ranking sweep.";
        }
        else if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            Skip = "Set SCANLINE_REAL_NOISE_DIR to the recorded HF noise corpus directory.";
        }
    }
}
