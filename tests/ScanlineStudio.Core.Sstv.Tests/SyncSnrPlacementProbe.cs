using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Step 0 of the sync-pulse SNR plan: for every mode with a sync segment × every RxBpfPreset × every
/// DemodType × {8000, 11025, 22050, 44100, 48000} Hz, noiseless, compares the chain-delay formula
/// (<see cref="SyncSnrPlacement"/>'s search centre minus its bias) against the encoder's exact sync onset.
/// Reports formula − true start per mode (positive: the formula is late) and how far one value per mode misses. This
/// is the measurement that ruled out formula placement. Gated: it takes minutes and always reports
/// through its failure message.
/// </summary>
public class SyncSnrPlacementProbe
{
    internal static readonly int[] Rates = [8000, 11025, 22050, 44100, 48000];
    internal const int LinesPerRun = 16;

    [RequiresDecodeMeasurementFact]
    public void Probe_PlacementErrorAcrossPresetDemodRate()
    {
        var configs = (from mode in SyncSnrTestSignals.ModesWithSync
                       from rate in Rates
                       select (mode, rate)).ToList();
        var results = new ConcurrentBag<(string ModeId, RxBpfPreset Preset, DemodType Demod, int Rate, double MedianMs, double MinMs, double MaxMs, int Lines)>();

        Parallel.ForEach(configs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, cfg =>
        {
            var (samples, truth) = SyncSnrTestSignals.EncodeAsync(cfg.mode, cfg.rate, LinesPerRun).GetAwaiter().GetResult();
            foreach (var preset in Enum.GetValues<RxBpfPreset>())
            {
                foreach (var demod in Enum.GetValues<DemodType>())
                {
                    var errors = MeasureErrorsMs(cfg.mode, cfg.rate, preset, demod, samples, truth, residualMs: 0.0);
                    if (errors.Count == 0)
                    {
                        results.Add((cfg.mode.Id, preset, demod, cfg.rate, double.NaN, double.NaN, double.NaN, 0));
                        continue;
                    }

                    errors.Sort();
                    results.Add((cfg.mode.Id, preset, demod, cfg.rate, errors[errors.Count / 2], errors[0], errors[^1], errors.Count));
                }
            }
        });

        var report = new StringBuilder();
        report.AppendLine("mode | formula − true start, ms (median over configs) | config median spread ms | max |err − median| ms | worst config | lines");
        var worstOverall = 0.0;
        foreach (var group in results.GroupBy(r => r.ModeId).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var valid = group.Where(r => r.Lines > 0).ToList();
            var missing = group.Count(r => r.Lines == 0);
            if (valid.Count == 0)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{group.Key} | NO LINES in any config");
                continue;
            }

            var medians = valid.Select(r => r.MedianMs).OrderBy(x => x).ToList();
            var r = medians[medians.Count / 2];
            var worst = valid.MaxBy(v => Math.Max(Math.Abs(v.MinMs - r), Math.Abs(v.MaxMs - r)));
            var worstErr = Math.Max(Math.Abs(worst.MinMs - r), Math.Abs(worst.MaxMs - r));
            worstOverall = Math.Max(worstOverall, worstErr);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{group.Key} | {r:F4} | {medians[0]:F4}..{medians[^1]:F4} | {worstErr:F4} | {worst.Preset}/{worst.Demod}/{worst.Rate} | {valid.Sum(v => v.Lines)}{(missing > 0 ? $" ({missing} configs no lines)" : string.Empty)}");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"WORST |error| after per-mode r: {worstOverall:F4} ms");
        report.AppendLine("Per-config detail (mode preset demod rate median min max):");
        foreach (var r in results.OrderBy(r => r.ModeId, StringComparer.Ordinal).ThenBy(r => r.Preset).ThenBy(r => r.Demod).ThenBy(r => r.Rate))
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"{r.ModeId} {r.Preset} {r.Demod} {r.Rate} {r.MedianMs:F4} {r.MinMs:F4} {r.MaxMs:F4}");
        }

        var path = Path.Combine(Path.GetTempPath(), "sync-snr-placement-probe.txt");
        File.WriteAllText(path, report.ToString());
        Assert.Fail($"Report written to {path}\n" + report.ToString().Split("Per-config detail")[0]);
    }

    /// <summary>Per decoded line: placed raw sync start − true onset, in ms, with <paramref name="residualMs"/>
    /// already inside the placement (pass 0 to measure r itself).</summary>
    internal static List<double> MeasureErrorsMs(
        SstvModeDefinition mode, int rate, RxBpfPreset preset, DemodType demod, float[] samples, double[] truth, double residualMs)
    {
        var decoder = new AnalogFmSstvDecoder(rate, demodType: demod, rxBpfPreset: preset);
        var errors = new List<double>();
        SstvModeDefinition? detected = null;
        decoder.ModeDetected += m => detected = m;
        decoder.SyncWindowObservedForTests = (line, rawStart) =>
        {
            if (line < truth.Length)
            {
                var placed = rawStart - (SyncSnrPlacement.CentreBiasMs / 1000.0 * rate) + (residualMs / 1000.0 * rate);
                errors.Add((placed - truth[line]) / rate * 1000.0);
            }
        };
        SyncSnrTestSignals.PushChunked(decoder, samples);
        return detected?.Id == mode.Id ? errors : [];
    }
}
