using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests.Impairment;

/// <summary>P6b-3: each golden's TX stream with its first k samples dropped (k = 0..N in steps), through the receiver;
/// counts decoded frames and whether the file completes. Env: <c>SCANLINE_DRM_SWEEP_FILTER</c> (case id substring),
/// <c>SCANLINE_DRM_OFFSET_MAX</c> (default 1280, one mode A/B symbol at 48 kHz), <c>SCANLINE_DRM_OFFSET_STEP</c> (default 1),
/// <c>SCANLINE_DRM_SWEEP_PARALLELISM</c> (default 4), <c>SCANLINE_DRM_SWEEP_OUT</c> (CSV, default /tmp/drm-offset.csv).</summary>
public sealed class SyncOffsetSweep(ITestOutputHelper output)
{
    /// <summary>Frames the estimator passed on (<c>transmission_frame_buffer_data_valid</c>, not an MSC CRC pass) and files completed for one run.</summary>
    internal static (int Frames, int Files) Run(short[] pcm, int drop, DrmRxOptions options)
    {
        short[] input = pcm[drop..];
        double[] stripes = RxAcquisitionGoldenTests.FrontEnd(input);
        var source = new DrmSourceDecoder
        {
            ReproduceQsstvPpiWrap = options.ReproduceQsstvPpiWrap,
            ReproduceQsstvSignedSurplus = options.ReproduceQsstvSignedSurplus,
        };
        int files = 0;
        source.FileReceived += _ => files++;
        var demod = new DrmDemodulator(new DrmRxFrameStage(
            new DrmChannelEstimator { SnrTimeoutStartsFull = options.SnrTimeoutStartsFull, FacCounterResetsOnGoodFrame = options.FacCounterResetsOnGoodFrame },
            new DrmChannelDecoder(),
            source));
        int frames = 0;
        for (int b = 0; b + 1024 <= stripes.Length; b += 1024)
        {
            demod.RunStripe(stripes.AsSpan(b, 1024));
            if (demod.State.TransmissionFrameBufferDataValid == 1)
                frames++;
        }

        return (frames, files);
    }

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    [RequiresDrmSweepFact]
    public void Sweep()
    {
        string filter = Env("SCANLINE_DRM_SWEEP_FILTER", string.Empty);
        int max = int.Parse(Env("SCANLINE_DRM_OFFSET_MAX", "1280"), CultureInfo.InvariantCulture);
        int step = int.Parse(Env("SCANLINE_DRM_OFFSET_STEP", "1"), CultureInfo.InvariantCulture);
        var po = new ParallelOptions { MaxDegreeOfParallelism = int.Parse(Env("SCANLINE_DRM_SWEEP_PARALLELISM", "4"), CultureInfo.InvariantCulture) };
        string outPath = Env("SCANLINE_DRM_SWEEP_OUT", "/tmp/drm-offset.csv");
        var rows = new ConcurrentBag<string>();
        var summary = new StringBuilder();

        foreach (string caseId in GoldenFixtures.CaseIds().Where(c => c.Contains(filter, StringComparison.Ordinal)))
        {
            short[] pcm = RxAcquisitionGoldenTests.LoadInput(caseId, out _);
            int[] offsets = [.. Enumerable.Range(0, (max / step) + 1).Select(i => i * step)];
            var result = new ConcurrentDictionary<int, (int Frames, int Files)>();
            Parallel.ForEach(offsets, po, k =>
            {
                result[k] = Run(pcm, k, RxImpairmentHarness.Baseline);
                rows.Add(string.Join(',', caseId, k, result[k].Frames, result[k].Files));
            });
            int[] frames = [.. offsets.Select(k => result[k].Frames)];
            int best = frames.Max();
            int[] low = [.. offsets.Where(k => result[k].Frames < best - 2)];
            int noFile = offsets.Count(k => result[k].Files == 0);
            summary.AppendLine(CultureInfo.InvariantCulture,
                $"{caseId}: frames min {frames.Min()} max {best} at offset 0 {result[0].Frames}; offsets more than 2 frames below max {low.Length}/{offsets.Length} (first {string.Join(' ', low.Take(8))}); no file {noFile}");
        }

        File.WriteAllLines(outPath, ["case,drop,frames,files", .. rows.OrderBy(r => r, StringComparer.Ordinal)]);
        output.WriteLine(summary.ToString());
        File.WriteAllText(Path.ChangeExtension(outPath, ".summary.txt"), summary.ToString());
    }
}
