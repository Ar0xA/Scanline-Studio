using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Opt-in gate for <see cref="SyncSnrValidationProbe"/>: minutes to an hour per probe, and each
/// reports through its failure message by design.</summary>
public sealed class RequiresSnrValidationFactAttribute : FactAttribute
{
    public const string OptInVariable = "SCANLINE_RUN_SNR_VALIDATION";

    public RequiresSnrValidationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(OptInVariable) != "1")
        {
            Skip = $"Set {OptInVariable}=1 to run the reception-SNR validation probes. They report "
                + "through the failure message by design, so they always 'fail' when run.";
        }
    }
}

/// <summary>
/// Step 7 of the sync-pulse SNR plan: the proof bar behind the measurement and its default. Truth SNR is
/// the H2 (400–2500 Hz) in-band ratio the real-noise sweep harness defines (1023-tap FIR band RMS of the
/// clean signal over that of the noise), NOT <c>NoiseRobustnessTests.AddNoiseAtSnr</c> (full band).
/// Placement truth is the encoder's own sample arithmetic (<see cref="SyncSnrTestSignals"/>). Every
/// probe writes its table under <c>$TMPDIR/snr-validation/</c>; docs/reception-snr-validation.md records
/// the numbers. <c>SCANLINE_SNR_TRIM_MS</c> overrides the edge trim for the accuracy/axes probes.
/// </summary>
public class SyncSnrValidationProbe
{
    private const int MeasurementTaps = 1023;
    private static readonly double[] SnrLevelsDb = [0, 5, 10, 15, 20, 25, 30];
    private static readonly int[] Seeds = [12345, 67890, 24680, 13579, 55555];
    private static readonly bool[] UnscaledOnly = [false];
    private static readonly bool[] BothTapVariants = [false, true];

    private static double TrimMs =>
        double.TryParse(Environment.GetEnvironmentVariable("SCANLINE_SNR_TRIM_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)
            ? ms
            : AnalogFmSstvDecoder.SyncSnrTrimDefaultMs;

    private static int Parallelism => Math.Max(1, Environment.ProcessorCount - 1);

    /// <summary>One representative mode per family, for the spot-check axes.</summary>
    private static readonly string[] FamilyModes =
        ["martin-m1", "scottie-s1", "scottie-dx", "robot-36", "r24", "pd120", "p3", "mr73", "ml180", "mp73", "mn73", "mc110", "sc2-120", "rm8"];

    internal sealed record RunResult(
        bool Detected,
        double SnrDb,
        List<double> PlacementErrorsMs,
        int Seen,
        int Contributed,
        int Edge,
        double WideStageDb,
        double NarrowStageDb);

    private static SstvModeDefinition Mode(string id) => SstvModeRegistry.All.Single(m => m.Id == id);

    internal static float[] Gaussian(int length, int seed)
    {
        var random = new Random(seed);
        var noise = new float[length];
        for (var i = 0; i < length; i++)
        {
            noise[i] = (float)SyncSnrEstimatorTests.Gaussian(random);
        }

        return noise;
    }

    /// <summary>Clean + noise scaled so the H2 in-band ratio is exactly <paramref name="snrDb"/>.</summary>
    internal static float[] Mix(float[] clean, double signalInBandRms, float[] noise, double noiseInBandRms, double snrDb)
    {
        var scale = signalInBandRms / Math.Pow(10.0, snrDb / 20.0) / noiseInBandRms;
        var mixed = new float[clean.Length];
        for (var i = 0; i < clean.Length; i++)
        {
            mixed[i] = (float)(clean[i] + (scale * noise[i]));
        }

        return mixed;
    }

    /// <summary>Decodes with measurement on and returns what the first reception of
    /// <paramref name="mode"/> measured, sampled at its last decoded line (as the recorder does).</summary>
    internal static RunResult Decode(
        SstvModeDefinition mode,
        float[] samples,
        double[] truth,
        int rate,
        double trimMs,
        Func<AnalogFmSstvDecoder>? factory = null,
        int chunk = 4096)
    {
        var decoder = factory?.Invoke() ?? new AnalogFmSstvDecoder(rate);
        decoder.SnrMeasurementEnabled = true;
        decoder.SyncSnrTrimMs = trimMs;
        var receptions = 0;
        var detected = false;
        var snr = double.NaN;
        var seen = 0;
        var contributed = 0;
        var edge = 0;
        var placement = new List<double>();
        var lines = new List<(double Tone, double Noise)>();

        decoder.ModeDetected += m =>
        {
            receptions++;
            if (receptions == 1 && m.Id == mode.Id)
            {
                detected = true;
            }
        };
        decoder.SyncSnrLineObservedForTests = (line, placed, estimate) =>
        {
            if (receptions != 1 || double.IsNaN(placed))
            {
                return;
            }

            // Nearest true sync start, not truth[line]: a replay or restart can renumber lines, and
            // the question is whether the window sits on a sync pulse at all.
            var index = Array.BinarySearch(truth, placed);
            index = index < 0 ? ~index : index;
            var nearest = double.PositiveInfinity;
            foreach (var candidate in new[] { index - 1, index })
            {
                if (candidate >= 0 && candidate < truth.Length && Math.Abs(placed - truth[candidate]) < Math.Abs(nearest))
                {
                    nearest = placed - truth[candidate];
                }
            }

            placement.Add(nearest / rate * 1000.0);

            if (estimate.IsValid)
            {
                lines.Add((estimate.TonePower, estimate.NoisePower));
            }
        };
        decoder.LineDecoded += _ =>
        {
            if (receptions == 1)
            {
                snr = decoder.ReceptionSnrDb;
                var tracker = decoder.SyncSnrTrackerForTests;
                seen = tracker.LinesSeen;
                contributed = tracker.LinesContributed;
                edge = tracker.EdgeLines;
            }
        };
        SyncSnrTestSignals.PushChunked(decoder, samples, chunk);

        return new RunResult(detected, snr, placement, seen, contributed, edge, StageDb(lines, wide: true), StageDb(lines, wide: false));
    }

    private static double StageDb(List<(double Tone, double Noise)> lines, bool wide)
    {
        var stage = wide ? lines.Take(SyncSnrTracker.WideStageLines).ToList() : lines.Skip(SyncSnrTracker.WideStageLines).ToList();
        if (stage.Count == 0)
        {
            return double.NaN;
        }

        return SyncSnrTracker.ToDb(SyncSnrTracker.Median(stage.Select(l => l.Tone).ToList()), SyncSnrTracker.Median(stage.Select(l => l.Noise).ToList()));
    }

    internal static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return double.NaN;
        }

        var rank = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (rank - lo));
    }

    private static string F(double v, string format = "F2") => double.IsNaN(v) ? "—" : v.ToString(format, CultureInfo.InvariantCulture);

    private static void Report(string name, StringBuilder report)
    {
        var dir = Path.Combine(Path.GetTempPath(), "snr-validation");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".md");
        File.WriteAllText(path, report.ToString());
        Assert.Fail($"Report written to {path}\n{report}");
    }

    /// <summary>R3: the smallest edge trim that keeps every mode ≥ 35 dB noiseless, at every rate, with our
    /// default TX BPF and with one scaled as an 11025 Hz transmitter would be (24 × rate / 11025 taps).</summary>
    [RequiresSnrValidationFact]
    public void Probe_TrimSweep_NoiselessCeiling()
    {
        double[] trims = [0.75, 1.0, 1.25];
        int[] rates = [8000, 11025, 22050, 44100, 48000];
        var configs = (from mode in SyncSnrTestSignals.ModesWithSync
                       from rate in rates
                       from scaled in rate == 11025 ? UnscaledOnly : BothTapVariants
                       select (mode, rate, scaled)).ToList();
        var results = new ConcurrentBag<(double Trim, string Mode, int Rate, bool Scaled, double Snr, double Fraction, bool Detected)>();

        Parallel.ForEach(configs, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, cfg =>
        {
            var taps = cfg.scaled ? 24 * cfg.rate / 11025 : TxOutputBandpassFilter.DefaultTapCount;
            var (samples, truth) = SyncSnrTestSignals.EncodeAsync(cfg.mode, cfg.rate, 40, txBpfTapCount: taps).GetAwaiter().GetResult();
            foreach (var trim in trims)
            {
                var run = Decode(cfg.mode, samples, truth, cfg.rate, trim, () => new AnalogFmSstvDecoder(cfg.rate));
                results.Add((trim, cfg.mode.Id, cfg.rate, cfg.scaled, run.SnrDb, run.Seen == 0 ? 0 : run.Contributed / (double)run.Seen, run.Detected));
            }
        });

        var report = new StringBuilder("# Trim sweep: noiseless ceiling (first 40 lines)\n\n| trim ms | runs | not detected | min SNR dB | worst (mode/rate/scaled taps) | runs < 35 dB | min contributing fraction |\n|---|---|---|---|---|---|---|\n");
        foreach (var trim in trims)
        {
            var rows = results.Where(r => r.Trim == trim).ToList();
            var detectedRows = rows.Where(r => r.Detected).ToList();
            var worst = detectedRows.MinBy(r => double.IsNaN(r.Snr) ? double.NegativeInfinity : r.Snr);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {trim:F2} | {rows.Count} | {rows.Count - detectedRows.Count} | {F(worst.Snr, "F1")} | {worst.Mode}/{worst.Rate}/{worst.Scaled} | {detectedRows.Count(r => !(r.Snr >= 35))} | {F(detectedRows.Min(r => r.Fraction))} |");
        }

        foreach (var trim in trims)
        {
            var failing = results.Where(r => r.Trim == trim && r.Detected && !(r.Snr >= 35)).OrderBy(r => r.Snr).Take(15).ToList();
            if (failing.Count > 0)
            {
                var list = string.Join(", ", failing.Select(f => $"{f.Mode}@{f.Rate}{(f.Scaled ? "s" : string.Empty)}={F(f.Snr, "F1")}"));
                report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"\nBelow 35 dB at trim {trim:F2}: ") + list);
            }
        }

        Report("trim-sweep", report);
    }

    /// <summary>R8/N7 accuracy: all 42 modes × 7 SNRs × 5 seeds at 11025 Hz, whole pictures, in-band AWGN.</summary>
    [RequiresSnrValidationFact]
    public void Probe_AwgnAccuracy_AllModes()
    {
        const int rate = 11025;
        var h2 = FirFilter.DesignBandPass(400, 2500, rate, MeasurementTaps);
        var trim = TrimMs;
        var results = new ConcurrentBag<(string Mode, double Snr, int Seed, RunResult Run)>();

        Parallel.ForEach(SyncSnrTestSignals.ModesWithSync, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, mode =>
        {
            var (clean, truth) = SyncSnrTestSignals.EncodeAsync(mode, rate, null).GetAwaiter().GetResult();
            var signalRms = FirFilter.BandRms(clean, h2);
            foreach (var seed in Seeds)
            {
                var noise = Gaussian(clean.Length, seed);
                var noiseRms = FirFilter.BandRms(noise, h2);
                foreach (var snr in SnrLevelsDb)
                {
                    results.Add((mode.Id, snr, seed, Decode(mode, Mix(clean, signalRms, noise, noiseRms, snr), truth, rate, trim)));
                }
            }
        });

        var report = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"# In-band AWGN accuracy, 11025 Hz, whole pictures, trim {trim:F2} ms\n\n"));
        report.AppendLine("| true SNR dB | runs | not detected / no figure | median error dB | 90th pct abs error dB | worst mode (its max abs error) | placement median abs ms | placement p90 abs ms | contributing fraction (mean) | wide-stage minus narrow-stage dB (median) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var snr in SnrLevelsDb)
        {
            var rows = results.Where(r => r.Snr == snr).ToList();
            var valid = rows.Where(r => r.Run.Detected && double.IsFinite(r.Run.SnrDb)).ToList();
            var errors = valid.Select(r => r.Run.SnrDb - snr).ToList();
            var worst = valid.GroupBy(r => r.Mode).Select(g => (Mode: g.Key, Max: g.Max(r => Math.Abs(r.Run.SnrDb - snr)))).MaxBy(g => g.Max);
            var placement = valid.SelectMany(r => r.Run.PlacementErrorsMs).Select(Math.Abs).ToList();
            var stageBias = valid.Select(r => r.Run.WideStageDb - r.Run.NarrowStageDb);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {snr:F0} | {rows.Count} | {rows.Count - valid.Count} | {F(Percentile(errors, 0.5))} | {F(Percentile(errors.Select(Math.Abs), 0.9))} | {worst.Mode} ({F(worst.Max)}) | {F(Percentile(placement, 0.5), "F3")} | {F(Percentile(placement, 0.9), "F3")} | {F(valid.Count == 0 ? double.NaN : valid.Average(r => r.Run.Seen == 0 ? 0 : r.Run.Contributed / (double)r.Run.Seen))} | {F(Percentile(stageBias, 0.5))} |");
        }

        report.AppendLine("\n## Monotonicity (mean over seeds must rise with true SNR)\n");
        var violations = new List<string>();
        var perMode = new StringBuilder("| mode | " + string.Join(" | ", SnrLevelsDb.Select(s => s.ToString("F0", CultureInfo.InvariantCulture))) + " |\n|---|" + string.Concat(SnrLevelsDb.Select(_ => "---|")) + "\n");
        foreach (var group in results.GroupBy(r => r.Mode).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var means = SnrLevelsDb.Select(s => group.Where(r => r.Snr == s && double.IsFinite(r.Run.SnrDb)).Select(r => r.Run.SnrDb).DefaultIfEmpty(double.NaN).Average()).ToList();
            perMode.AppendLine(CultureInfo.InvariantCulture, $"| {group.Key} | {string.Join(" | ", means.Select(m => F(m, "F1")))} |");
            for (var i = 1; i < means.Count; i++)
            {
                if (!(means[i] > means[i - 1]))
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture, $"{group.Key} {SnrLevelsDb[i - 1]:F0}→{SnrLevelsDb[i]:F0} dB"));
                }
            }
        }

        report.AppendLine(violations.Count == 0 ? "All modes monotonic." : "Violations: " + string.Join(", ", violations));
        report.AppendLine("\n## Per-mode mean measured SNR (dB)\n");
        report.Append(perMode);
        Report("awgn-accuracy", report);
    }

    private static IImageSource DarkLeftEdgeSource(SstvModeDefinition mode)
    {
        var pixels = new Rgb24[mode.ImageWidth * mode.ImageHeight];
        for (var y = 0; y < mode.ImageHeight; y++)
        {
            for (var x = 0; x < mode.ImageWidth; x++)
            {
                pixels[(y * mode.ImageWidth) + x] = x < mode.ImageWidth / 8 ? new Rgb24(0, 0, 0) : new Rgb24((byte)(x * 255 / mode.ImageWidth), 128, 200);
            }
        }

        return new ArrayImageSource(mode.ImageWidth, mode.ImageHeight, pixels);
    }

    private sealed record AxisCase(string Axis, string Value, SstvModeDefinition Mode, int Rate, int? Lines, Func<AnalogFmSstvDecoder> Factory, double SampleRateOffsetHz = 0, double CarrierOffsetHz = 0, IImageSource? Source = null);

    /// <summary>Spot checks (N7): one mode per family at SNR {5, 15, 25}, one seed, across mistuning × AFC,
    /// BPF preset × demod × rate, clock error × Auto Slant and RxBufferMode (whole pictures), and MN/MC
    /// with a dark left edge.</summary>
    [RequiresSnrValidationFact]
    public void Probe_SpotAxes()
    {
        double[] snrs = [5, 15, 25];
        var cases = new List<AxisCase>();
        foreach (var id in FamilyModes)
        {
            var mode = Mode(id);
            foreach (var offset in new[] { 0, 30, -30, 100, -100 })
            {
                foreach (var afc in new[] { true, false })
                {
                    cases.Add(new AxisCase("mistuning × AFC", $"{offset:+0;-0;0} Hz AFC {(afc ? "on" : "off")}", mode, 11025, 60, () => new AnalogFmSstvDecoder(11025, afcEnabled: afc), CarrierOffsetHz: offset));
                }
            }

            foreach (var rate in new[] { 11025, 44100 })
            {
                foreach (var preset in Enum.GetValues<RxBpfPreset>())
                {
                    foreach (var demod in Enum.GetValues<DemodType>())
                    {
                        var r = rate;
                        cases.Add(new AxisCase("preset × demod × rate", $"{preset}/{demod}/{rate}", mode, rate, 60, () => new AnalogFmSstvDecoder(r, demodType: demod, rxBpfPreset: preset)));
                    }
                }
            }

            foreach (var ppm in new[] { 100, -100, 500, -500 })
            {
                foreach (var slant in new[] { true, false })
                {
                    cases.Add(new AxisCase("clock error × Auto Slant", $"{ppm:+0;-0} ppm slant {(slant ? "on" : "off")}", mode, 11025, null, () => new AnalogFmSstvDecoder(11025, autoSlantEnabled: slant), SampleRateOffsetHz: 11025 * ppm / 1e6));
                }
            }

            foreach (var buffer in Enum.GetValues<RxBufferMode>())
            {
                cases.Add(new AxisCase("RxBufferMode", buffer.ToString(), mode, 11025, null, () => new AnalogFmSstvDecoder(11025, rxBufferMode: buffer)));
            }
        }

        foreach (var id in new[] { "mn73", "mn110", "mn140", "mc110", "mc140", "mc180" })
        {
            var mode = Mode(id);
            cases.Add(new AxisCase("MN/MC dark left edge", id, mode, 11025, null, () => new AnalogFmSstvDecoder(11025), Source: DarkLeftEdgeSource(mode)));
        }

        var axisFilter = Environment.GetEnvironmentVariable("SCANLINE_SNR_AXES");
        if (!string.IsNullOrWhiteSpace(axisFilter))
        {
            cases = cases.Where(c => c.Axis.Contains(axisFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var trim = TrimMs;
        var results = new ConcurrentBag<(AxisCase Case, double Snr, RunResult Run)>();
        Parallel.ForEach(cases, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, c =>
        {
            var (clean, truth) = SyncSnrTestSignals.EncodeAsync(c.Mode, c.Rate, c.Lines, c.Source, c.SampleRateOffsetHz, carrierOffsetHz: c.CarrierOffsetHz).GetAwaiter().GetResult();
            var h2 = FirFilter.DesignBandPass(400, 2500, c.Rate, MeasurementTaps);
            var signalRms = FirFilter.BandRms(clean, h2);
            var noise = Gaussian(clean.Length, Seeds[0]);
            var noiseRms = FirFilter.BandRms(noise, h2);
            foreach (var snr in snrs)
            {
                results.Add((c, snr, Decode(c.Mode, Mix(clean, signalRms, noise, noiseRms, snr), truth, c.Rate, trim, c.Factory)));
            }
        });

        var report = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"# Spot axes (one mode per family, seed {Seeds[0]}, trim {trim:F2} ms)\n\n"));
        report.AppendLine("| axis | value | runs | no figure | abs error p90 @5 / 15 / 25 dB | worst (mode, dB error) | placement median / p90 abs ms | contributing fraction (min / mean) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var group in results.GroupBy(r => (r.Case.Axis, r.Case.Value)).OrderBy(g => g.Key.Axis, StringComparer.Ordinal))
        {
            var rows = group.ToList();
            var valid = rows.Where(r => r.Run.Detected && double.IsFinite(r.Run.SnrDb)).ToList();
            var p90 = snrs.Select(s => Percentile(valid.Where(r => r.Snr == s).Select(r => Math.Abs(r.Run.SnrDb - s)), 0.9)).ToList();
            var worst = valid.Count == 0 ? default : valid.MaxBy(r => Math.Abs(r.Run.SnrDb - r.Snr));
            var placement = valid.SelectMany(r => r.Run.PlacementErrorsMs).Select(Math.Abs).ToList();
            var fractions = valid.Select(r => r.Run.Seen == 0 ? 0 : r.Run.Contributed / (double)r.Run.Seen).ToList();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key.Axis} | {group.Key.Value} | {rows.Count} | {rows.Count - valid.Count} | {string.Join(" / ", p90.Select(v => F(v)))} | {(valid.Count == 0 ? "—" : $"{worst.Case.Mode.Id}@{worst.Snr:F0} {F(worst.Run.SnrDb - worst.Snr)}")} | {F(Percentile(placement, 0.5), "F3")} / {F(Percentile(placement, 0.9), "F3")} | {F(fractions.DefaultIfEmpty(double.NaN).Min())} / {F(fractions.DefaultIfEmpty(double.NaN).Average())} |");
        }

        var noFigure = results.Where(r => !(r.Run.Detected && double.IsFinite(r.Run.SnrDb))).Select(r => $"{r.Case.Axis}:{r.Case.Value}:{r.Case.Mode.Id}@{r.Snr:F0}{(r.Run.Detected ? string.Empty : "(not detected)")}").Take(40).ToList();
        if (noFigure.Count > 0)
        {
            report.AppendLine("\nRuns without a figure (first 40): " + string.Join(", ", noFigure));
        }

        Report("spot-axes", report);
    }

    private static (List<int> Lines, Rgb24[]? Pixels, string Modes) DecodeImage(float[] samples, int rate, bool snrOn, RxBufferMode buffer, int chunk, SstvModeDefinition? force)
    {
        var decoder = new AnalogFmSstvDecoder(rate, rxBufferMode: buffer) { SnrMeasurementEnabled = snrOn };
        var lines = new List<int>();
        Rgb24[]? last = null;
        var modes = new StringBuilder();
        decoder.ModeDetected += m => modes.Append(m.Id).Append(';');
        decoder.LineDecoded += u =>
        {
            lines.Add(u.Line);
            var pixels = new Rgb24[u.Image.Width * u.Image.Height];
            for (var y = 0; y < u.Image.Height; y++)
            {
                u.Image.GetScanline(y).CopyTo(pixels.AsSpan(y * u.Image.Width, u.Image.Width));
            }

            last = pixels;
        };
        if (force is not null)
        {
            decoder.ForceMode(force);
        }

        SyncSnrTestSignals.PushChunked(decoder, samples, chunk);
        return (lines, last, modes.ToString());
    }

    /// <summary>Decoded pixels, line sequence and mode sequence identical with measurement on and off:
    /// all 43 modes × RxBufferMode Off/On/Extended × two chunk sizes, noisy (10 dB) so replay and restarts
    /// run, plus ForceMode at stream start.</summary>
    [RequiresSnrValidationFact]
    public void Probe_BitIdentity_AllModes()
    {
        const int rate = 11025;
        var h2 = FirFilter.DesignBandPass(400, 2500, rate, MeasurementTaps);
        var mismatches = new ConcurrentBag<string>();
        var compared = 0;
        Parallel.ForEach(SstvModeRegistry.All, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, mode =>
        {
            var encoder = new AnalogFmSstvEncoder(rate);
            var clean = new List<float>();
            foreach (var batch in encoder.EncodeBatchedAsync(mode, SyncSnrTestSignals.SourceFor(mode)).ToBlockingEnumerable())
            {
                clean.AddRange(batch.ToArray());
            }

            var cleanArray = clean.ToArray();
            var noise = Gaussian(cleanArray.Length, 777);
            var noisy = Mix(cleanArray, FirFilter.BandRms(cleanArray, h2), noise, FirFilter.BandRms(noise, h2), 10);
            var variants = new List<(string Name, RxBufferMode Buffer, int Chunk, SstvModeDefinition? Force)>();
            foreach (var buffer in Enum.GetValues<RxBufferMode>())
            {
                variants.Add(($"{buffer}/4096", buffer, 4096, null));
                variants.Add(($"{buffer}/997", buffer, 997, null));
            }

            variants.Add(("On/ForceMode", RxBufferMode.On, 4096, mode));
            foreach (var v in variants)
            {
                var off = DecodeImage(noisy, rate, false, v.Buffer, v.Chunk, v.Force);
                var on = DecodeImage(noisy, rate, true, v.Buffer, v.Chunk, v.Force);
                Interlocked.Increment(ref compared);
                var same = off.Modes == on.Modes
                    && off.Lines.SequenceEqual(on.Lines)
                    && ((off.Pixels is null && on.Pixels is null) || (off.Pixels is not null && on.Pixels is not null && off.Pixels.AsSpan().SequenceEqual(on.Pixels)));
                if (!same)
                {
                    mismatches.Add($"{mode.Id} {v.Name}");
                }

                if (off.Lines.Count == 0)
                {
                    mismatches.Add($"{mode.Id} {v.Name}: nothing decoded (comparison vacuous)");
                }
            }
        });

        var report = new StringBuilder("# Bit-identity, measurement on vs off (11025 Hz, 10 dB in-band AWGN, whole pictures)\n\n");
        report.AppendLine(CultureInfo.InvariantCulture, $"Comparisons: {compared} (43 modes × Off/On/Extended × chunk 4096/997, + ForceMode at stream start)");
        report.AppendLine(mismatches.IsEmpty ? "Result: IDENTICAL in every comparison (pixels, line sequence, mode sequence)." : "MISMATCHES: " + string.Join(", ", mismatches.OrderBy(m => m, StringComparer.Ordinal)));
        Report("bit-identity", report);
    }

    /// <summary>Cost: decode time with measurement on vs off on the same stream, single-threaded,
    /// alternating repeats; "negligible" is &lt; 2% of decode time per line.</summary>
    [RequiresSnrValidationFact]
    public void Probe_Cost()
    {
        string[] modes = ["martin-m1", "scottie-s1", "robot-36", "pd120", "mn73", "pd290"];
        var report = new StringBuilder("# Cost (single thread, median of 5 alternating repeats, whole pictures at 15 dB)\n\n| mode | rate | lines | off ms | on ms | overhead % | overhead per line µs |\n|---|---|---|---|---|---|---|\n");
        foreach (var rate in new[] { 11025, 44100 })
        {
            foreach (var id in modes)
            {
                var mode = Mode(id);
                var (clean, _) = SyncSnrTestSignals.EncodeAsync(mode, rate, null).GetAwaiter().GetResult();
                var noisy = SyncSnrDecoderTests.AddInBandNoise(clean, rate, 15, 5);
                var off = new List<double>();
                var on = new List<double>();
                var lines = 0;
                for (var repeat = 0; repeat < 6; repeat++)
                {
                    foreach (var snrOn in new[] { false, true })
                    {
                        var decoder = new AnalogFmSstvDecoder(rate) { SnrMeasurementEnabled = snrOn };
                        var count = 0;
                        decoder.LineDecoded += _ => count++;
                        var stopwatch = Stopwatch.StartNew();
                        SyncSnrTestSignals.PushChunked(decoder, noisy);
                        stopwatch.Stop();
                        if (repeat == 0)
                        {
                            continue; // warm-up
                        }

                        (snrOn ? on : off).Add(stopwatch.Elapsed.TotalMilliseconds);
                        lines = count;
                    }
                }

                var offMs = Percentile(off, 0.5);
                var onMs = Percentile(on, 0.5);
                report.AppendLine(CultureInfo.InvariantCulture, $"| {id} | {rate} | {lines} | {offMs:F0} | {onMs:F0} | {(onMs - offMs) / offMs * 100:F2} | {(onMs - offMs) * 1000 / Math.Max(1, lines):F1} |");
            }
        }

        Report("cost", report);
    }

    /// <summary>OTA sanity: every ota-recordings/*.wav decoded with measurement on — per-picture SNR next to
    /// each decoded picture, plus the same on/off bit-identity check on real audio.</summary>
    [RequiresSnrValidationFact]
    public void Probe_Ota()
    {
        var dir = Environment.GetEnvironmentVariable("SCANLINE_OTA_DIR") ?? "/home/artien/code/Yoniq-reborn/ota-recordings";
        var files = Directory.GetFiles(dir, "*.wav").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        var rows = new ConcurrentBag<string>();
        var identity = new ConcurrentBag<string>();
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Parallelism }, path =>
        {
            var (samples, rate) = WavFile.Read(path);
            var decoder = new AnalogFmSstvDecoder(rate) { SnrMeasurementEnabled = true };
            var receptions = new List<(string Mode, int MaxLine, double Snr, int Seen, int Contributed)>();
            decoder.ModeDetected += m => receptions.Add((m.Id, -1, double.NaN, 0, 0));
            decoder.LineDecoded += u =>
            {
                if (receptions.Count > 0)
                {
                    var t = decoder.SyncSnrTrackerForTests;
                    receptions[^1] = (receptions[^1].Mode, u.Line, decoder.ReceptionSnrDb, t.LinesSeen, t.LinesContributed);
                }
            };
            decoder.PushSamples(samples);
            foreach (var r in receptions.Where(r => r.MaxLine >= 0))
            {
                rows.Add(string.Create(CultureInfo.InvariantCulture, $"| {Path.GetFileNameWithoutExtension(path)[..8]} | {rate} | {r.Mode} | {r.MaxLine + 1} | {F(r.Snr, "F1")} | {r.Contributed}/{r.Seen} |"));
            }

            var off = DecodeImage(samples, rate, false, RxBufferMode.On, 4096, null);
            var on = DecodeImage(samples, rate, true, RxBufferMode.On, 4096, null);
            var same = off.Modes == on.Modes && off.Lines.SequenceEqual(on.Lines)
                && ((off.Pixels is null && on.Pixels is null) || (off.Pixels is not null && on.Pixels is not null && off.Pixels.AsSpan().SequenceEqual(on.Pixels)));
            identity.Add(same ? "same" : Path.GetFileName(path));
        });

        var report = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"# OTA recordings ({files.Length} files)\n\n| file | rate | mode | lines | per-picture SNR dB | contributing/seen lines |\n|---|---|---|---|---|---|\n"));
        foreach (var row in rows.OrderBy(r => r, StringComparer.Ordinal))
        {
            report.AppendLine(row);
        }

        var differing = identity.Where(i => i != "same").ToList();
        report.AppendLine(CultureInfo.InvariantCulture, $"\nBit-identity on real audio (RxBufferMode On, chunk 4096): {(differing.Count == 0 ? $"identical in all {identity.Count} files" : "DIFFERS: " + string.Join(", ", differing))}");
        Report("ota", report);
    }
}
