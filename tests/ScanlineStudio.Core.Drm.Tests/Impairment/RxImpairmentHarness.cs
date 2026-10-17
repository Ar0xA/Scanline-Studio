using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;
using Xunit.Abstractions;
using PublicSettings = ScanlineStudio.Abstractions.Drm.DrmTxSettings;

namespace ScanlineStudio.Core.Drm.Tests.Impairment;

/// <summary>Runs only with <c>SCANLINE_RUN_DRM_SWEEP=1</c>: the sweep takes minutes to hours.</summary>
public sealed class RequiresDrmSweepFactAttribute : FactAttribute
{
    public RequiresDrmSweepFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCANLINE_RUN_DRM_SWEEP") != "1")
            Skip = "Set SCANLINE_RUN_DRM_SWEEP=1 to run the DRM RX impairment sweep (docs/drm-rx-measurements.md).";
    }
}

/// <summary>One TX configuration of the sweep, with a payload sized for it.</summary>
internal sealed record DrmSweepConfig(string Name, PublicSettings Settings, int PayloadLength);

/// <summary>What the TX sent, for the receive-side oracles: body segment count, body bytes, first segment size, RS level.</summary>
internal sealed record DrmSweepTruth(int Segments, long BodyLength, int DefaultSegmentSize, int RsLevel);

/// <summary>One receive run's outcome. <paramref name="Missing"/> lists the body segments (of the true count) not received
/// by the end of an incomplete run; empty for a complete one.</summary>
internal sealed record DrmRunResult(bool Complete, bool CompleteButWrong, double SecondsToComplete, int Segments, int TotalSegments,
    IReadOnlyList<int> Missing, bool HeaderSeen);

/// <summary>P6b-0: our QSSTV-identical TX → <see cref="HfChannelSimulator"/> → <see cref="DrmReceiver"/>, run paired with
/// the QSSTV baseline options and the improved receiver. Env: <c>SCANLINE_DRM_SWEEP_FILTER</c> (substring of
/// "config/channel"), <c>SCANLINE_DRM_SWEEP_SEEDS</c> (default 10), <c>SCANLINE_DRM_SWEEP_RATES</c> (default "48000"),
/// <c>SCANLINE_DRM_SWEEP_PARALLELISM</c> (default 4), <c>SCANLINE_DRM_SWEEP_OUT</c> (CSV path, default /tmp/drm-sweep.csv).</summary>
public sealed class RxImpairmentHarness(ITestOutputHelper output)
{
    private static readonly DateTimeOffset Stamp = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    private const int Block = 4096;

    /// <summary>The receiver before P6b: QSSTV's RX except the signed size-byte fix, which has its own recorded proof.</summary>
    internal static DrmRxOptions Baseline { get; } = DrmRxOptions.Qsstv with { ReproduceQsstvSignedSurplus = false };

    internal static IReadOnlyList<DrmSweepConfig> Configs()
    {
        static PublicSettings S(DrmRobustnessMode m, DrmBandwidth bw, DrmQamOrder q, DrmProtectionLevel p, DrmInterleaving il, DrmReedSolomonLevel rs) =>
            new(m, bw, q, p, il, rs, "PA0TST", true, 400);

        var list = new List<DrmSweepConfig>
        {
            Sized("robust", S(DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam4, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.Rs2)),
            Sized("normal", S(DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam16, DrmProtectionLevel.Low, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.Rs1)),
            Sized("fast", S(DrmRobustnessMode.E, DrmBandwidth.Hz2500, DrmQamOrder.Qam64, DrmProtectionLevel.Low, DrmInterleaving.ShortInterleaver, DrmReedSolomonLevel.None)),
            Sized("e4high-so0", S(DrmRobustnessMode.E, DrmBandwidth.Hz2200, DrmQamOrder.Qam4, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.None)),
        };
        foreach (DrmBandwidth bw in new[] { DrmBandwidth.Hz2200, DrmBandwidth.Hz2500 })
        {
            foreach (DrmInterleaving il in new[] { DrmInterleaving.LongInterleaver, DrmInterleaving.ShortInterleaver })
            {
                foreach (DrmReedSolomonLevel rs in new[] { DrmReedSolomonLevel.None, DrmReedSolomonLevel.Rs1 })
                {
                    string name = $"a64low-{(bw == DrmBandwidth.Hz2200 ? "so0" : "so1")}-{(il == DrmInterleaving.LongInterleaver ? "long" : "short")}-{(rs == DrmReedSolomonLevel.None ? "rs0" : "rs1")}";
                    list.Add(PpiWindow(name, S(DrmRobustnessMode.A, bw, DrmQamOrder.Qam64, DrmProtectionLevel.Low, il, rs)));
                }
            }
        }

        foreach (DrmInterleaving il in new[] { DrmInterleaving.LongInterleaver, DrmInterleaving.ShortInterleaver })
        {
            string name = $"a64high-so1-{(il == DrmInterleaving.LongInterleaver ? "long" : "short")}-rs0";
            list.Add(PpiWindow(name, S(DrmRobustnessMode.A, DrmBandwidth.Hz2500, DrmQamOrder.Qam64, DrmProtectionLevel.High, il, DrmReedSolomonLevel.None)));
        }

        return list;
    }

    private static DrmSourceFrameGenerator Generator(PublicSettings settings, int payloadLength)
    {
        var core = DrmTransmitter.ToCoreSettings(settings);
        DrmTxFileName name = new DrmTxNaming(new FixedTimeProvider(Stamp)).GetTxFileName("sweep.bin", binary: true);
        return new DrmSourceFrameGenerator(DrmTxObject.ForFile(new byte[payloadLength], name, core.ReedSolomon), core, new BsrTransportIdCounter());
    }

    private static bool OutsideRsQuirk(PublicSettings s, int n) =>
        s.ReedSolomon == DrmReedSolomonLevel.None
        || n % RsFileEncoder.DataSize(DrmTransmitter.ToCoreSettings(s).ReedSolomon) <= RsFileEncoder.DataSize(DrmTransmitter.ToCoreSettings(s).ReedSolomon) - RsFileEncoder.HeaderSize;

    /// <summary>About 6 kB, outside the kept RS size quirk.</summary>
    private static DrmSweepConfig Sized(string name, PublicSettings s)
    {
        int n = Enumerable.Range(6000, 300).First(n => OutsideRsQuirk(s, n));
        return new DrmSweepConfig(name, s, n);
    }

    /// <summary>A payload whose last body segment carries 245..len−15 bytes (len = PacketLength + 3), so its PPI count wraps.</summary>
    private static DrmSweepConfig PpiWindow(string name, PublicSettings s)
    {
        int maxL = Generator(s, 100).PacketLength - 12;
        int n = Enumerable.Range(4000, 4000).First(n => OutsideRsQuirk(s, n) && Generator(s, n).BodySegments[^1].Length - 2 is var l && l >= 245 && l <= maxL);
        return new DrmSweepConfig(name, s, n);
    }

    internal static DrmSweepTruth Truth(DrmSweepConfig config)
    {
        DrmSourceFrameGenerator g = Generator(config.Settings, config.PayloadLength);
        int level = config.Settings.ReedSolomon switch
        {
            DrmReedSolomonLevel.Rs1 => 1,
            DrmReedSolomonLevel.Rs2 => 2,
            DrmReedSolomonLevel.Rs3 => 3,
            DrmReedSolomonLevel.Rs4 => 4,
            _ => 0,
        };
        return new DrmSweepTruth(g.BodySegments.Count, g.BodySegments.Sum(b => (long)b.Length - 2), g.BodySegments[0].Length - 2, level);
    }

    /// <summary>P6b-2's oracle. Every on-air frame carries one data group and the last on-air frame a body group, so
    /// decoding one more frame adds at most one body segment; only a body segment triggers a save. True when the header
    /// is in and adding back some one missing segment passes the decoder's save rules (last segment, 63 %, RS erasure
    /// capacity): an upper bound on what any lost-frame fix can rescue.</summary>
    internal static bool OneItemShort(DrmRunResult r, DrmSweepTruth t)
    {
        if (r.Complete || !r.HeaderSeen)
            return false;
        foreach (int x in r.Missing)
        {
            int[] rest = [.. r.Missing.Where(m => m != x)];
            if (rest.Contains(t.Segments - 1) || (t.Segments - rest.Length) * 100 / t.Segments < 63)
                continue;
            if (t.RsLevel == 0 ? rest.Length == 0 : RsFileDecoder.ErasuresDecodable(t.BodyLength, t.RsLevel, [t.Segments, t.DefaultSegmentSize, .. rest]))
                return true;
        }

        return false;
    }

    internal static byte[] Payload(DrmSweepConfig config)
    {
        var payload = new byte[config.PayloadLength];
        new Random(config.PayloadLength).NextBytes(payload);
        return payload;
    }

    internal static float[] Transmit(DrmSweepConfig config, byte[] payload)
    {
        var tx = new DrmTransmitter(new DrmTxNaming(new FixedTimeProvider(Stamp)), new BsrTransportIdCounter());
        IDrmTransmission t = tx.Prepare(payload, "sweep.bin", isImage: false, config.Settings, newTxFileName: true);
        var audio = new List<float>();
        foreach (ReadOnlyMemory<float> chunk in t.ReadSamplesAsync().ToBlockingEnumerable())
            audio.AddRange(chunk.Span);
        return [.. audio];
    }

    /// <summary>One receive of <paramref name="samples"/> at <paramref name="rate"/>; time counts from the signal start.</summary>
    internal static DrmRunResult Receive(float[] samples, int rate, int signalStart, byte[] payload, DrmRxOptions options, int bodySegments)
    {
        var receiver = new DrmReceiver(rate, options);
        long pushed = 0;
        double completedAt = double.NaN;
        bool complete = false, wrong = false, headerSeen = false;
        receiver.ReceivingNameChanged += n => headerSeen |= n.Length > 0;
        int segments = 0, total = 0;
        receiver.FileReceived += f =>
        {
            if (f.Data.Span.SequenceEqual(payload))
            {
                if (!complete)
                    completedAt = (pushed - signalStart) / (double)rate;
                complete = true;
            }
            else
            {
                wrong = true;
            }
        };
        receiver.SignalStateChanged += s =>
        {
            if (s.TotalSegments > 0)
                (segments, total) = (s.SegmentsReceived, s.TotalSegments);
        };
        for (int i = 0; i < samples.Length; i += Block)
        {
            int n = Math.Min(Block, samples.Length - i);
            pushed = i + n;
            receiver.Push(samples.AsSpan(i, n));
        }

        receiver.ZeroFill(2 * rate);
        DrmSourceDecoder source = receiver.SourceDecoder;
        int[] missing = complete ? [] : [.. Enumerable.Range(0, bodySegments).Except(source.BlockList.Select(b => (int)b))];
        return new DrmRunResult(complete, wrong, completedAt, complete ? Math.Max(segments, total) : segments, total, missing, headerSeen);
    }

    internal static float[] ToRate(float[] x48, int rate) => rate == HfChannelSimulator.Rate ? x48 : Resample(x48, HfChannelSimulator.Rate, rate);

    /// <summary>Blackman-windowed sinc, cut off at 45 % of the output rate (the signal is below 3 kHz).</summary>
    internal static float[] Resample(float[] input, int inRate, int outRate)
    {
        const int Half = 64;
        double ratio = (double)outRate / inRate;
        double fc = 0.45 * Math.Min(1.0, ratio);
        var output = new float[(int)(input.Length * ratio)];
        Parallel.For(0, output.Length, n =>
        {
            double t = n / ratio;
            int center = (int)Math.Floor(t);
            double acc = 0;
            for (int k = center - Half + 1; k <= center + Half; k++)
            {
                if ((uint)k >= (uint)input.Length)
                    continue;
                double x = t - k;
                double sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
                double w = 0.42 + (0.5 * Math.Cos(Math.PI * x / Half)) + (0.08 * Math.Cos(2 * Math.PI * x / Half));
                acc += input[k] * sinc * w;
            }

            output[n] = (float)acc;
        });
        return output;
    }

    /// <summary>The receiver under test: production, plus the P6b-4 candidates named in <c>SCANLINE_DRM_SWEEP_VARIANT</c>
    /// ("snrfull", "facreset", comma-separated).</summary>
    internal static DrmRxOptions Improved()
    {
        string[] v = Env("SCANLINE_DRM_SWEEP_VARIANT", string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (v.Except(["snrfull", "facreset"]).FirstOrDefault() is string unknown)
            throw new ArgumentException($"unknown SCANLINE_DRM_SWEEP_VARIANT token '{unknown}'");
        return DrmRxOptions.Default with { SnrTimeoutStartsFull = v.Contains("snrfull"), FacCounterResetsOnGoodFrame = v.Contains("facreset") };
    }

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : fallback;

    [Fact]
    public void Smoke_OneCleanRun_CompletesWithBothOptionSets()
    {
        DrmSweepConfig config = Configs().Single(c => c.Name == "fast");
        byte[] payload = Payload(config);
        float[] tx = Transmit(config, payload);
        (float[] rx, int start) = HfChannelSimulator.Apply(tx, HfChannel.Awgn, 40, Impairments.Draw(new Random(1)), new Random(2));

        int n = Truth(config).Segments;
        Assert.True(Receive(rx, 48000, start, payload, Baseline, n).Complete);
        Assert.True(Receive(rx, 48000, start, payload, DrmRxOptions.Default, n).Complete);
    }

    [Fact]
    public void PpiConfigs_PutTheLastSegmentInTheWrapWindow()
    {
        foreach (DrmSweepConfig c in Configs().Where(c => c.Name.StartsWith("a64", StringComparison.Ordinal)))
        {
            DrmSourceFrameGenerator g = Generator(c.Settings, c.PayloadLength);
            Assert.InRange(g.BodySegments[^1].Length - 2, 245, g.PacketLength - 12);
        }
    }

    [Fact]
    public void OneItemShort_MatchesTheDecoderSaveRules()
    {
        static DrmRunResult R(bool header, params int[] missing) => new(false, false, double.NaN, 0, 0, missing, header);
        var rs0 = new DrmSweepTruth(40, 40 * 300, 300, 0);
        Assert.True(OneItemShort(R(true, 7), rs0));
        Assert.False(OneItemShort(R(false), rs0));
        Assert.False(OneItemShort(R(true, 7, 8), rs0));
        Assert.False(OneItemShort(R(false, 7), rs0));
        Assert.True(OneItemShort(R(true, 39), rs0));
        Assert.False(OneItemShort(new DrmRunResult(true, false, 1, 40, 40, [], true), rs0));

        // RS2 parity is 63 of 255 per code word; segment-interleaved, so the bound is the decoder's own.
        DrmSweepTruth rs2 = Truth(Configs().Single(c => c.Name == "robust"));
        int[] Decodable(int k) => [.. Enumerable.Range(0, k)];
        int most = Enumerable.Range(0, rs2.Segments).Last(k => RsFileDecoder.ErasuresDecodable(rs2.BodyLength, 2, [rs2.Segments, rs2.DefaultSegmentSize, .. Decodable(k)]));
        Assert.True(OneItemShort(R(true, Decodable(most + 1)), rs2));
        Assert.False(OneItemShort(R(true, Decodable(most + 2)), rs2));
    }

    [RequiresDrmSweepFact]
    public void Sweep()
    {
        string filter = Env("SCANLINE_DRM_SWEEP_FILTER", string.Empty);
        int seeds = int.Parse(Env("SCANLINE_DRM_SWEEP_SEEDS", "10"), CultureInfo.InvariantCulture);
        int[] rates = [.. Env("SCANLINE_DRM_SWEEP_RATES", "48000").Split(',').Select(r => int.Parse(r, CultureInfo.InvariantCulture))];
        int parallelism = int.Parse(Env("SCANLINE_DRM_SWEEP_PARALLELISM", "4"), CultureInfo.InvariantCulture);
        string outPath = Env("SCANLINE_DRM_SWEEP_OUT", "/tmp/drm-sweep.csv");
        var po = new ParallelOptions { MaxDegreeOfParallelism = parallelism };
        var rows = new ConcurrentBag<string>();

        foreach (DrmSweepConfig config in Configs())
        {
            byte[] payload = Payload(config);
            float[] tx = Transmit(config, payload);
            DrmSweepTruth truth = Truth(config);
            foreach (HfChannel channel in HfChannel.All)
            {
                string key = config.Name + "/" + channel.Name;
                // The 4 ms delay is longer than mode A's 2.7 ms guard; the plan runs it for B and E only.
                if (!key.Contains(filter, StringComparison.Ordinal) || (channel == HfChannel.LongDelay && config.Settings.Mode == DrmRobustnessMode.A))
                    continue;

                double[] grid = SnrGrid(config, channel, tx, payload, truth.Segments, po);
                output.WriteLine($"{key}: SNR grid [{string.Join(", ", grid.Select(g => g.ToString("F1", CultureInfo.InvariantCulture)))}] dB");
                var work = (from snr in grid from seed in Enumerable.Range(1, seeds) from rate in rates select (snr, seed, rate)).ToList();
                Parallel.ForEach(work, po, w =>
                {
                    int realization = Seed(config.Name, channel.Name, w.snr, w.seed);
                    var rng = new Random(realization);
                    (float[] rx48, int start48) = HfChannelSimulator.Apply(tx, channel, w.snr, Impairments.Draw(rng), rng);
                    float[] rx = ToRate(rx48, w.rate);
                    int start = (int)((long)start48 * w.rate / HfChannelSimulator.Rate);
                    foreach ((string label, DrmRxOptions options) in new[] { ("baseline", Baseline), ("improved", Improved()) })
                    {
                        DrmRunResult r = Receive(rx, w.rate, start, payload, options, truth.Segments);
                        rows.Add(string.Join(',', config.Name, channel.Name, w.rate, w.snr.ToString("F1", CultureInfo.InvariantCulture), w.seed, label,
                            r.Complete ? 1 : 0, r.CompleteButWrong ? 1 : 0, double.IsNaN(r.SecondsToComplete) ? string.Empty : r.SecondsToComplete.ToString("F1", CultureInfo.InvariantCulture),
                            r.Segments, r.TotalSegments, r.Missing.Count, r.HeaderSeen ? 1 : 0, OneItemShort(r, truth) ? 1 : 0,
                            r.Missing.Count is > 0 and <= 40 ? string.Join(';', r.Missing) : string.Empty));
                    }
                });
            }
        }

        string[] sorted = [.. rows.OrderBy(r => r, StringComparer.Ordinal)];
        File.WriteAllLines(outPath, ["config,channel,rate,snr,seed,rx,complete,wrong,seconds,segments,total,missing,header,oneItemShort,missingIds", .. sorted]);
        string summary = Summarize(sorted);
        output.WriteLine(summary);
        File.WriteAllText(Path.ChangeExtension(outPath, ".summary.txt"), summary);
        Assert.DoesNotContain(sorted, r => r.Split(',') is var f && f[5] == "improved" && f[7] == "1");
    }

    /// <summary>Pre-scan with the receiver under test at 48 kHz (the baseline never completes a wrapped file), 2 seeds:
    /// 0..40 dB in 4 dB steps, then 1 dB steps inside the bracket. Six points from the highest SNR with no completion to
    /// the lowest with full completion; distinct, so a collapsed bracket gives one point, not six copies of one.</summary>
    internal static double[] SnrGrid(DrmSweepConfig config, HfChannel channel, float[] tx, byte[] payload, int segments, ParallelOptions po)
    {
        (double lo, double hi) = Bracket(config, channel, tx, payload, segments, po, [.. Enumerable.Range(0, 11).Select(i => i * 4.0)], 0, 40);
        (lo, hi) = Bracket(config, channel, tx, payload, segments, po, [.. Enumerable.Range(0, (int)(hi - lo) + 1).Select(i => lo + i)], lo, hi);
        return [.. Enumerable.Range(0, 6).Select(i => Math.Round(lo + ((hi - lo) * i / 5.0), 2)).Distinct()];
    }

    private static (double Lo, double Hi) Bracket(DrmSweepConfig config, HfChannel channel, float[] tx, byte[] payload, int segments,
        ParallelOptions po, double[] snrs, double lo, double hi)
    {
        var rate = new ConcurrentDictionary<double, double>();
        Parallel.ForEach(snrs, po, snr =>
        {
            int ok = 0;
            for (int seed = 101; seed <= 102; seed++)
            {
                var rng = new Random(Seed(config.Name, channel.Name, snr, seed));
                (float[] rx, int start) = HfChannelSimulator.Apply(tx, channel, snr, Impairments.Draw(rng), rng);
                if (Receive(rx, HfChannelSimulator.Rate, start, payload, Improved(), segments).Complete)
                    ok++;
            }

            rate[snr] = ok / 2.0;
        });
        double newLo = snrs.Where(s => rate[s] == 0).DefaultIfEmpty(lo).Max();
        double newHi = snrs.Where(s => rate[s] == 1 && s > newLo).DefaultIfEmpty(hi).Min();
        return (newLo, newHi);
    }

    /// <summary>A run seed that is the same in every process (string.GetHashCode is randomized per process).</summary>
    internal static int Seed(string config, string channel, double snr, int seed)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{config}|{channel}|{snr:F2}|{seed}")));
        return BitConverter.ToInt32(hash, 0) & int.MaxValue;
    }

    internal static string Summarize(IEnumerable<string> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("config/channel/rate/snr: completed baseline vs improved (of n), wrong baseline/improved, mean s to complete baseline/improved");
        foreach (var g in rows.Select(r => r.Split(',')).GroupBy(f => (f[0], f[1], f[2], f[3])).OrderBy(g => g.Key.Item1, StringComparer.Ordinal)
                     .ThenBy(g => g.Key.Item2, StringComparer.Ordinal).ThenBy(g => g.Key.Item3, StringComparer.Ordinal)
                     .ThenBy(g => double.Parse(g.Key.Item4, CultureInfo.InvariantCulture)))
        {
            string[][] q = [.. g.Where(f => f[5] == "baseline")], imp = [.. g.Where(f => f[5] == "improved")];
            static string Mean(string[][] runs) => runs.Where(f => f[8].Length > 0).Select(f => double.Parse(f[8], CultureInfo.InvariantCulture)).DefaultIfEmpty(double.NaN).Average().ToString("F1", CultureInfo.InvariantCulture);
            sb.AppendLine(CultureInfo.InvariantCulture, $"{g.Key.Item1}/{g.Key.Item2}/{g.Key.Item3}/{g.Key.Item4} dB: {q.Count(f => f[6] == "1")} vs {imp.Count(f => f[6] == "1")} (of {q.Length}), wrong {q.Count(f => f[7] == "1")}/{imp.Count(f => f[7] == "1")}, {Mean(q)}/{Mean(imp)} s, one item short {imp.Count(f => f[13] == "1")}");
        }

        string[][] all = [.. rows.Select(r => r.Split(',')).Where(f => f[5] == "improved")];
        sb.AppendLine(CultureInfo.InvariantCulture, $"improved runs: {all.Length}, incomplete {all.Count(f => f[6] == "0")}, one item short of saving {all.Count(f => f[13] == "1")}");
        return sb.ToString();
    }
}
