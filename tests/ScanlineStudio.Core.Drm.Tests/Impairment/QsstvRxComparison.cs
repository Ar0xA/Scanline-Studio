using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests.Impairment;

/// <summary>Exports AWGN-impaired transmissions as PCM16 48 kHz WAVs for QSSTV's receiver (the qsstv-drm-harness
/// <c>rx</c> command) and receives the same quantized samples with our baseline and improved receivers, so both sides
/// decode identical input. Env: <c>SCANLINE_DRM_QSSTV_EXPORT_DIR</c> (required), <c>SCANLINE_DRM_SWEEP_FILTER</c>,
/// <c>SCANLINE_DRM_SWEEP_SEEDS</c>, <c>SCANLINE_DRM_SWEEP_PARALLELISM</c>. Writes <c>ours.csv</c> and
/// <c>truth.csv</c> (config, payload SHA-256) beside the WAVs; docs/drm-rx-measurements.md has the comparison.</summary>
public sealed class QsstvRxComparison(ITestOutputHelper output)
{
    [RequiresDrmSweepFact]
    public void ExportAwgn()
    {
        string dir = Environment.GetEnvironmentVariable("SCANLINE_DRM_QSSTV_EXPORT_DIR") ?? throw new InvalidOperationException("Set SCANLINE_DRM_QSSTV_EXPORT_DIR.");
        string filter = Environment.GetEnvironmentVariable("SCANLINE_DRM_SWEEP_FILTER") ?? string.Empty;
        int seeds = int.Parse(Environment.GetEnvironmentVariable("SCANLINE_DRM_SWEEP_SEEDS") ?? "10", CultureInfo.InvariantCulture);
        var po = new ParallelOptions { MaxDegreeOfParallelism = int.Parse(Environment.GetEnvironmentVariable("SCANLINE_DRM_SWEEP_PARALLELISM") ?? "4", CultureInfo.InvariantCulture) };
        Directory.CreateDirectory(dir);
        var rows = new ConcurrentBag<string>();
        var truth = new List<string>();
        HfChannel channel = HfChannel.Awgn;

        foreach (DrmSweepConfig config in RxImpairmentHarness.Configs().Where(c => c.Name.Contains(filter, StringComparison.Ordinal)))
        {
            byte[] payload = RxImpairmentHarness.Payload(config);
            float[] tx = RxImpairmentHarness.Transmit(config, payload);
            int segments = RxImpairmentHarness.Truth(config).Segments;
            truth.Add(config.Name + "," + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
            double[] grid = RxImpairmentHarness.SnrGrid(config, channel, tx, payload, segments, po);
            output.WriteLine($"{config.Name}: AWGN grid [{string.Join(", ", grid.Select(g => g.ToString("F1", CultureInfo.InvariantCulture)))}] dB");
            var work = (from snr in grid from seed in Enumerable.Range(1, seeds) select (snr, seed)).ToList();
            Parallel.ForEach(work, po, w =>
            {
                var rng = new Random(RxImpairmentHarness.Seed(config.Name, channel.Name, w.snr, w.seed));
                (float[] rx, int start) = HfChannelSimulator.Apply(tx, channel, w.snr, Impairments.Draw(rng), rng);
                (short[] pcm, int clipped) = Quantize(rx);
                string name = FormattableString.Invariant($"{config.Name}_{w.snr:F1}_{w.seed}");
                WriteWav(Path.Combine(dir, name + ".wav"), pcm);
                float[] quantized = [.. pcm.Select(s => s / 32768f)];
                DrmRunResult b = RxImpairmentHarness.Receive(quantized, HfChannelSimulator.Rate, start, payload, RxImpairmentHarness.Baseline, segments);
                DrmRunResult i = RxImpairmentHarness.Receive(quantized, HfChannelSimulator.Rate, start, payload, RxImpairmentHarness.Improved(), segments);
                rows.Add(string.Join(',', name, config.Name, w.snr.ToString("F1", CultureInfo.InvariantCulture), w.seed, clipped,
                    b.Complete ? 1 : 0, b.CompleteButWrong ? 1 : 0, i.Complete ? 1 : 0, i.CompleteButWrong ? 1 : 0));
            });
        }

        File.WriteAllLines(Path.Combine(dir, "ours.csv"), ["wav,config,snr,seed,clipped,baseline,baselineWrong,improved,improvedWrong", .. rows.OrderBy(r => r, StringComparer.Ordinal)]);
        File.WriteAllLines(Path.Combine(dir, "truth.csv"), ["config,sha256", .. truth]);
    }

    /// <summary>Full scale = 1.0, the app's capture scale; samples beyond it are clipped and counted.</summary>
    private static (short[] Pcm, int Clipped) Quantize(float[] x)
    {
        var pcm = new short[x.Length];
        int clipped = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = Math.Round(x[i] * 32768.0);
            if (v > short.MaxValue || v < short.MinValue)
                clipped++;
            pcm[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
        }

        return (pcm, clipped);
    }

    private static void WriteWav(string path, short[] pcm)
    {
        using var w = new BinaryWriter(File.Create(path));
        int bytes = pcm.Length * 2;
        w.Write("RIFF"u8);
        w.Write(36 + bytes);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(HfChannelSimulator.Rate);
        w.Write(HfChannelSimulator.Rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(bytes);
        foreach (short s in pcm)
            w.Write(s);
    }
}
