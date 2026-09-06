using System.Collections.Concurrent;
using System.Globalization;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Where is the output smoother's real optimum, and does it move with SNR?
///
/// The six-arm ranking run showed Hilbert improving from 1800 Hz to 900 Hz on robot-36, and getting
/// WORSE at the mode's own anti-alias limit of 3636 Hz. That kills the "cutoff = half the pixel rate"
/// prescription but leaves the obvious follow-up open: does narrowing keep helping below 900 Hz?
///
/// Two effects should eventually stop it. Narrowing deletes real detail. And a third-order
/// Butterworth's settling time scales with the inverse of its cutoff, so once settling exceeds the
/// 2-12 sample pixel dwell, each pixel carries its neighbours. The SECOND effect is directly
/// observable here: the clean-signal alignment shift is measured per cutoff, and a steep climb means
/// real delay is being paid.
///
/// The optimum is expected to MOVE WITH SNR -- narrower at 9 dB than at 20 dB -- which would itself
/// be an argument against any single fixed number.</summary>
public sealed class CutoffLadderHarness
{
    private const int SampleRate = 44100;
    private const double H2LowHz = 400;
    private const double H2HighHz = 2500;
    private const int MeasurementTaps = 1023;
    private const int MaxAlignShift = 24;
    private const double OutlierLineDelta = 30.0;

    private static readonly double[] CutoffsHz = ParseList("SCANLINE_CUTOFFS", [300, 450, 600, 750, 900, 1200, 1800, 2400]);
    private static readonly double[] SnrLevelsDb = ParseList("SCANLINE_SNRS", [20.0, 16.0, 12.0, 9.0]);

    private static double[] ParseList(string variable, double[] fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(raw)
            ? fallback
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    }
    private static readonly int[] Seeds = [12345, 67890, 24680];

    [RequiresDemodulatorRankingFact]
    public async Task SweepOutputCutoff()
    {
        var corpus = RealNoiseCorpus.Load(Environment.GetEnvironmentVariable("SCANLINE_REAL_NOISE_DIR")!);
        var h2 = FirFilter.DesignBandPass(H2LowHz, H2HighHz, SampleRate, MeasurementTaps);
        var encoder = new AnalogFmSstvEncoder(SampleRate);

        var runDir = Path.Combine(
            ImpairmentSweepHarness.FindRepoRoot(),
            "impairment-reports",
            string.Create(CultureInfo.InvariantCulture, $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}-cutoff-ladder"));
        Directory.CreateDirectory(runDir);
        var progress = Path.Combine(runDir, "progress.log");

        // Fast, mid and slow: 3636 / 1157 / 463 Hz of pixel-rate Nyquist. If the optimum tracks the
        // mode at all, these three are where it shows.
        var modeIds = (Environment.GetEnvironmentVariable("SCANLINE_IMPAIRMENT_MODES") ?? "robot-36,scottie-s1,scottie-dx")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var longest = SstvModeRegistry.All.Where(m => modeIds.Contains(m.Id)).Max(m => m.LineDurationMs * m.ImageHeight / 1000.0);
        Log(progress, $"{modeIds.Length} modes x {CutoffsHz.Length} cutoffs x {Seeds.Length} seeds x {SnrLevelsDb.Length} SNR, both topologies");
        var noise = Seeds.Select((seed, ordinal) => RealNoiseStreamBuilder.Build(corpus, seed, ordinal, (longest * 1.25) + 15.0)).ToList();
        Log(progress, "noise ready.");

        var results = new List<CutoffPoint>();
        foreach (var modeId in modeIds)
        {
            var mode = SstvModeRegistry.All.Single(m => m.Id == modeId);
            // SCANLINE_SOURCE_BMP overrides the per-mode fixture. It exists because EVERY
            // golden-vector fixture is the same smooth gradient (mean horizontal pixel delta 0.24,
            // against 43-54 for a real received picture), so a change that merely blurs scores better
            // and the metric cannot tell smooth from correct. Point this at testcard.bmp to measure
            // resolution loss instead of noise removal.
            var overrideBmp = Environment.GetEnvironmentVariable("SCANLINE_SOURCE_BMP");
            var fixture = ImpairmentSweepHarness.RealFixtureModes.FirstOrDefault(f => f.ModeId == modeId);
            // SCANLINE_SOURCE_BMP is a STEM, not a filename: "testcard" resolves to
            // testcard-320x240.bmp for a 240-line mode and testcard-320x256.bmp for a 256-line one.
            // Each size is generated at its own dimensions rather than resampled from one master,
            // because resampling would move the card's gratings off their exact 2/3/4/6/8/12-pixel
            // periods -- and those exact periods are the entire reason the card exists.
            // A rooted stem is used as-is, so a photographic source can live OUTSIDE the repo, the
            // same posture as the HF noise corpus. Nothing whose licence needs auditing is bundled.
            var sourcePath = string.IsNullOrEmpty(overrideBmp)
                ? null
                : $"{overrideBmp}-{mode.ImageWidth}x{mode.ImageHeight}.bmp";
            IImageSource source = sourcePath is not null
                ? BmpFile.Read(Path.IsPathRooted(sourcePath) ? sourcePath : Path.Combine(ImpairmentSweepHarness.FixtureDir, sourcePath))
                : fixture.SourceBmp is not null
                    ? BmpFile.Read(Path.Combine(ImpairmentSweepHarness.FixtureDir, fixture.SourceBmp))
                    : ImpairmentSweepHarness.CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);
            var height = sourcePath is not null ? source.Height
                : fixture.SourceBmp is not null ? fixture.PictureHeight : mode.ImageHeight;

            var encoded = new List<float>();
            await foreach (var sample in encoder.EncodeAsync(mode, source))
            {
                encoded.Add(sample);
            }

            var clean = encoded.ToArray();
            encoded.Clear();
            var signalInBand = FirFilter.BandRms(clean, h2);
            var nyquistHz = mode.ImageWidth / (mode.LineSegments.OfType<ScanSegment>().Min(seg => seg.DurationMs) / 1000.0) / 2.0;

            // Clean-signal shift per (topology, cutoff): the arm's real group delay, and the direct
            // read-out of how much delay a narrow cutoff is costing.
            var shifts = new Dictionary<(DemodType, double), int>();
            foreach (var demod in new[] { DemodType.Hilbert, DemodType.ZeroCrossing })
            {
                foreach (var cutoff in CutoffsHz)
                {
                    shifts[(demod, cutoff)] = MeasureCleanAlignment(demod, cutoff, clean, source, height, mode.Id);
                }
            }

            Log(progress, $"{modeId}: pixel-rate Nyquist {nyquistHz:F0}Hz, clean shifts hilbert " +
                          string.Join(" ", CutoffsHz.Select(c => $"{c:F0}={shifts[(DemodType.Hilbert, c)]}")));

            var work = (from snrDb in SnrLevelsDb from pair in noise.Zip(Seeds) select (SnrDb: snrDb, Stream: pair.First, Seed: pair.Second)).ToList();
            var affordable = (int)Math.Max(1, 9L * 1024 * 1024 * 1024 / Math.Max(1, clean.Length * 60L * sizeof(float)));
            var collected = new ConcurrentBag<CutoffPoint>();
            Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, affordable) }, item =>
            {
                var scale = signalInBand / Math.Pow(10.0, item.SnrDb / 20.0) / FirFilter.BandRms(item.Stream.Samples[..clean.Length], h2);
                var noisy = new float[clean.Length];
                for (var i = 0; i < clean.Length; i++)
                {
                    noisy[i] = (float)(clean[i] + (item.Stream.Samples[i] * scale));
                }

                foreach (var demod in new[] { DemodType.Hilbert, DemodType.ZeroCrossing })
                {
                    foreach (var cutoff in CutoffsHz)
                    {
                        var image = Decode(demod, cutoff, noisy, mode.Id);
                        if (image is null)
                        {
                            collected.Add(new CutoffPoint(modeId, demod.ToString(), cutoff, item.Seed, item.SnrDb, false, null, null, null, shifts[(demod, cutoff)], nyquistHz));
                            continue;
                        }

                        var (mean, median, outliers) = ScoreAtShift(source, ImpairmentSweepHarness.CropToTop(image, height), height, shifts[(demod, cutoff)]);
                        collected.Add(new CutoffPoint(modeId, demod.ToString(), cutoff, item.Seed, item.SnrDb, true, mean, median, outliers, shifts[(demod, cutoff)], nyquistHz));
                    }
                }
            });

            results.AddRange(collected.OrderByDescending(p => p.SnrDb).ThenBy(p => p.Seed).ThenBy(p => p.Demodulator, StringComparer.Ordinal).ThenBy(p => p.CutoffHz));
            File.WriteAllText(Path.Combine(runDir, "report.json"), System.Text.Json.JsonSerializer.Serialize(results, ImpairmentSweepHarness.ReportJsonOptions));

            foreach (var demod in new[] { "Hilbert", "ZeroCrossing" })
            {
                var row = CutoffsHz.Select(c =>
                {
                    var v = collected.Where(p => p.Demodulator == demod && p.CutoffHz == c && p.Decoded).Select(p => p.MeanDelta!.Value).ToList();
                    return v.Count == 0 ? "nolock" : $"{c:F0}={v.Average():F1}";
                });
                Log(progress, $"{modeId} {demod,-13} {string.Join("  ", row)}");
            }
        }

        Log(progress, $"done -> {runDir}");
    }

    private static IImageSource? Decode(DemodType demod, double cutoffHz, float[] samples, string expectedModeId)
    {
        var decoder = demod == DemodType.Hilbert
            ? new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.Hilbert, hilbertOutputCutoffHz: cutoffHz)
            : new AnalogFmSstvDecoder(SampleRate, demodType: DemodType.ZeroCrossing, zeroCrossingOutputCutoffHz: cutoffHz);

        SstvModeDefinition? detected = null;
        IImageSource? image = null;
        decoder.ModeDetected += m => detected = m;
        decoder.LineDecoded += u => image = u.Image;
        decoder.PushSamples(samples);
        return detected?.Id == expectedModeId && image is not null ? image : null;
    }

    private static int MeasureCleanAlignment(DemodType demod, double cutoffHz, float[] clean, IImageSource source, int height, string expectedModeId)
    {
        var image = Decode(demod, cutoffHz, clean, expectedModeId);
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

    private static (double Mean, double Median, int OutlierLines) ScoreAtShift(IImageSource source, IImageSource actual, int height, int shift)
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

    private static void Log(string path, string message)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:HH:mm:ss}] {message}");
        File.AppendAllText(path, line + Environment.NewLine);
        Console.WriteLine(line);
    }
}

/// <param name="PixelRateNyquistHz">Half the mode's own pixel rate -- the anti-alias limit the
/// per-mode arm was built on, recorded so the measured optimum can be compared against it.</param>
public sealed record CutoffPoint(
    string ModeId,
    string Demodulator,
    double CutoffHz,
    int Seed,
    double SnrDb,
    bool Decoded,
    double? MeanDelta,
    double? MedianLineDelta,
    int? OutlierLines,
    int AlignmentShift,
    double PixelRateNyquistHz);
