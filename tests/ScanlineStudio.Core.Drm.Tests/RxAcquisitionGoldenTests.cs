using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5b: mode detection and time/frequency/frame acquisition against QSSTV, per stripe, all 79 cases,
/// run with the real frame stage (<see cref="DrmChannelEstimator"/>, P5c) through <see cref="RxGoldenRun"/>.</summary>
/// <remarks>
/// <para><b>Input.</b> The six cases with a stored full <c>out48k</c> use QSSTV's own TX stream. The other 73
/// use our P3 TX output, and the test requires its SHA-256 to equal QSSTV's <c>out48k</c> sha256, so every
/// case runs on QSSTV's exact input. Both run through our P5a front end (48 kHz path, 4096 blocks, 8 zero tail
/// blocks) as in the harness.</para>
/// <para><b>Compared exactly on every stripe</b> (rx_sync): time_sync, freq_sync, frame_sync,
/// robustness_mode, spectrum_occupancy_estimation, spectrum_occupancy. (rx_acq): iterationCounter, counter,
/// rsbufwidx, symbufwidx, symbol_counter, input_samples_buffer_request, time_offset_integer,
/// freq_offset_integer.</para>
/// <para><b>Float fields.</b> time_offset, samplerate_offset_estimation, frequency_offset_fractional_init,
/// freq_offset_init, time_offset_fractional_init, delta_time_offset_I_init, and Zi from frequencySync until the
/// first equalized frame, are bit-exact: none of them passes through the FFT. The FFT only reaches the
/// acquisition decisions, which are exact. Tolerance 0 assumes glibc libm on both sides (QSSTV's g++ build binds
/// float atan2/tanh/cos to atan2f/tanhf/cosf; .NET's MathF calls the C runtime); Windows UCRT or macOS libm may
/// differ by 1 ulp there. Once frames are equalized, Zi is the tracker's state and carries the FFTW-vs-ours
/// rounding: it is compared with <see cref="ZiTrackingTolerance"/>; freqOffset (|Δ| ≤ 0.01 Hz) and WMERFAC
/// (|Δ| ≤ 0.1 dB) use the fixture README's bounds.</para>
/// <para><b>Range.</b> Every stripe, including the zero tail, up to <see cref="RxGoldenRun.Result.LastStripe"/>:
/// B0_q4_high_long is knife-edge (probes/pre-p5.md) and is compared up to stripe 104 (its full run is checked
/// per stripe by <see cref="SnrResync_FullB0Run_MatchesQsstvPerStripe"/>). Two cases run with the tracker forced
/// at one bit-equal peak-search tie (see <see cref="RxChannelEstimationGoldenTests"/>) and are compared to the end.</para>
/// <para><b>Seam.</b> The re-acquisition test keeps the P5b stub stage (<see cref="GoldenResyncFrameStage"/>):
/// it injects QSSTV's only in-signal resync, which lies after B0_q4_high_long's knife edge.</para>
/// </remarks>
public sealed class RxAcquisitionGoldenTests(ITestOutputHelper output)
{
    /// <summary>Symbol handoff: |Δ| per component relative to the largest |component| of the window. FFTW
    /// codelets vs our mixed-radix FFT differ in float rounding only; measured worst 1.24e-7 (B1_q16_high_long).</summary>
    private const double HandoffSymbolRelTolerance = 1e-6;

    /// <summary>Zi while tracking, |Δ| per entry; Zi1 (a phase) uses its own bound modulo 2π. Measured in
    /// <see cref="RxChannelEstimationGoldenTests"/>.</summary>
    internal const double ZiTrackingTolerance = RxChannelEstimationGoldenTests.ZiTolerance;

    private const int Block48k = 4096;
    private const int TailZeroBlocks = 8;
    private const int KnifeEdgeLastStripe = RxGoldenRun.KnifeEdgeLastStripe;

    private static readonly string[] FullStreamCases = RxGoldenRun.FullStreamCases;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string id in GoldenFixtures.CaseIds())
            data.Add(id);
        return data;
    }

    public static TheoryData<string> HandoffCases()
    {
        var data = new TheoryData<string>();
        foreach (string id in FullStreamCases)
            data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Acquisition_MatchesQsstvPerStripe(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        int last = run.LastStripe;
        AcqStats s = Compare(caseId, run.Stripes, run.ExactInput, run.TxShaEqual, 0, last, realStage: true);
        output.WriteLine(s.ToString());
        Assert.Equal(0, run.ZeroRequestCalls);
        Assert.True(s.Failures.Count == 0, $"{caseId}: {string.Join("; ", s.Failures.Take(8))}");
        Assert.True(s.StripesCompared > 50, $"{caseId}: only {s.StripesCompared} stripes compared");
        Assert.True(s.FrameSyncStripes > 0, $"{caseId}: frame sync never reached");
        if (run.TieCutReason is not null)
            output.WriteLine($"compared to stripe {last}: {run.TieCutReason}");
        if (last < run.Stripes.Count - 1)
            RxChannelEstimationGoldenTests.AssertKeepsDecodingAfter(caseId, run, last, output);
    }

    [Theory]
    [MemberData(nameof(HandoffCases))]
    public void FrameSyncHandoff_MatchesQsstv(string caseId)
    {
        string dir = Path.Combine("rx_acq", caseId);
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFixtures.GoldenDir, dir, "handoff.json")));
        JsonElement h = meta.RootElement;
        int stripe = h.GetProperty("stripe").GetInt32();
        int nSym = h.GetProperty("symbol_complex").GetInt32();
        int nRs = h.GetProperty("rs_complex").GetInt32();
        float[] golden = MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, "handoff.f32.gz")).ToArray();
        Assert.Equal(2 * (nSym + nRs), golden.Length);

        var demod = new DrmDemodulator();
        double[] stripes = FrontEnd(LoadInput(caseId, out _));
        for (int b = 0; b <= stripe; b++)
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));

        DrmDemodState st = demod.State;
        Assert.True(st.FrameSyncFlag);
        Assert.Equal(0, st.SymbolCounter);
        Assert.Equal(h.GetProperty("K_modulo").GetInt32(), st.KModulo);
        Assert.Equal(h.GetProperty("symbols_per_2D_window").GetInt32() * st.KModulo, nSym);
        Assert.Equal(nRs, st.RsBufWidx);

        double maxAbs = 0, maxDelta = 0;
        for (int i = 0; i < 2 * nSym; i++)
        {
            maxAbs = Math.Max(maxAbs, Math.Abs(golden[i]));
            maxDelta = Math.Max(maxDelta, Math.Abs((double)golden[i] - st.SymbolBuffer[i]));
        }

        int rsUlp = MaxUlp(golden.AsSpan(2 * nSym), st.RsBuffer.AsSpan(0, 2 * nRs));
        output.WriteLine($"{caseId}: handoff stripe {stripe}, {nSym} symbol cells max|Δ| {maxDelta:E3} (rel {maxDelta / maxAbs:E3}, max|S| {maxAbs:E3}); rs residual {nRs} max ulp {rsUlp} (measured 0 on all six)");
        Assert.True(maxDelta <= HandoffSymbolRelTolerance * maxAbs, $"symbols rel Δ {maxDelta / maxAbs:E3}");
        Assert.True(rsUlp == 0, $"rs residual {rsUlp} ulp");
    }

    /// <summary>The WMERFAC &lt; 3 dB resync rule in situ (demodulator.cpp:1090-1112): B0_q4_high_long's whole run,
    /// beyond the planned knife-edge range, matches QSSTV per stripe on identical input, including QSSTV's only
    /// in-signal resync (stripe 170).</summary>
    [Fact]
    public void SnrResync_FullB0Run_MatchesQsstvPerStripe()
    {
        const string caseId = "B0_q4_high_long";
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        AcqStats s = Compare(caseId, run.Stripes, run.ExactInput, run.TxShaEqual, 0, run.Stripes.Count - 1, realStage: true);
        output.WriteLine(s.ToString());
        Assert.True(s.Failures.Count == 0, string.Join("; ", s.Failures.Take(8)));
        int[] resyncs = Enumerable.Range(0, sync.Length - 1).Where(b => sync[b][1] == "1" && sync[b][3] == "1" && sync[b + 1][1] == "0").ToArray();
        output.WriteLine($"QSSTV resyncs after stripes {string.Join(", ", resyncs)}");
        Assert.Contains(170, resyncs);
        foreach (int b in resyncs)
        {
            RxGoldenRun.FrameRecord f = run.Frames.Single(x => x.Stripe == b);
            Assert.True(f.DoSynchronize, $"stripe {b}");
            Assert.True(f.WMerFac < DrmRxTables.SnrMinDb, $"stripe {b}: WMERFAC {f.WMerFac}");
        }
    }

    /// <summary>The re-acquisition path: QSSTV's only in-signal resync (B0_q4_high_long, SNR drop at stripe 170,
    /// pre-p5.md) is injected through the seam with the P5b stub stage; from there to the next frame-sync handoff
    /// everything is acquisition on the input alone and must match exactly, although frame-by-frame comparison of
    /// this knife-edge case stops at stripe 104.</summary>
    [Fact]
    public void Reacquisition_AfterInjectedResync_MatchesQsstv()
    {
        const string caseId = "B0_q4_high_long";
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        string[][] acq = Csv(Path.Combine("rx_acq", caseId), "rx_acq.csv.gz");
        (List<RxGoldenRun.StripeSnapshot> snaps, int resyncs) = RunWithStub(caseId);
        Assert.Equal(1, resyncs);
        int resync = Enumerable.Range(0, sync.Length - 1).Single(s => sync[s][1] == "1" && sync[s][3] == "1" && sync[s + 1][1] == "0");
        int handoff = Enumerable.Range(resync + 1, sync.Length - resync - 1).First(s => sync[s][3] == "1");
        int end = handoff;
        while (end + 1 < acq.Length && acq[end + 1][5] == "0")
            end++;
        AcqStats s = Compare(caseId, snaps, true, true, resync + 1, end, realStage: false);
        output.WriteLine($"resync at stripe {resync}, frame sync again at {handoff}, compared {resync + 1}..{end}: {s}");
        Assert.Empty(s.Failures);
        Assert.True(s.FrameSyncStripes > 0);
        Assert.True(handoff - resync > 5, "re-acquisition must wait for a full 20-symbol window");
    }

    private static (List<RxGoldenRun.StripeSnapshot> Snaps, int Resyncs) RunWithStub(string caseId)
    {
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        var resync = new HashSet<int>();
        for (int s = 0; s + 1 < sync.Length; s++)
        {
            if (sync[s][1] == "1" && sync[s][3] == "1" && sync[s + 1][1] == "0")
            {
                resync.Add(s);
                float wmer = float.Parse(sync[s][9], CultureInfo.InvariantCulture);
                Assert.True(wmer < DrmRxTables.SnrMinDb, $"{caseId}: QSSTV resynced at stripe {s} with WMERFAC {wmer}");
            }
        }

        double[] stripes = FrontEnd(LoadInput(caseId, out _));
        var stage = new GoldenResyncFrameStage(resync);
        var demod = new DrmDemodulator(stage);
        var snaps = new List<RxGoldenRun.StripeSnapshot>(sync.Length);
        for (int b = 0; b < sync.Length; b++)
        {
            stage.CurrentStripe = b;
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
            DrmDemodState st = demod.State;
            snaps.Add(new RxGoldenRun.StripeSnapshot(st.TimeSyncFlag, st.FrequencySyncFlag, st.FrameSyncFlag, st.RobustnessMode,
                st.SpectrumOccupancyEstimation, st.SpectrumOccupancy, st.FreqOffset, st.WMerFac,
                st.IterationCounter, st.Counter, st.RsBufWidx, st.SymBufWidx, st.SymbolCounter, st.InputSamplesBufferRequest,
                st.TimeOffset, st.TimeOffsetInteger, st.SamplerateOffsetEstimation, st.FrequencyOffsetFractionalInit, st.FreqOffsetInteger,
                st.FreqOffsetInit, st.TimeOffsetFractionalInit, st.DeltaTimeOffsetIInit, (float[])st.Zi.Clone(), stage.Frames));
        }

        Assert.Equal(0, stage.ZeroRequestCalls);
        return (snaps, resync.Count);
    }

    private static AcqStats Compare(string caseId, List<RxGoldenRun.StripeSnapshot> snaps, bool exactInput, bool txShaEqual, int first, int last,
        bool realStage)
    {
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        string[][] acq = Csv(Path.Combine("rx_acq", caseId), "rx_acq.csv.gz");
        var s = new AcqStats(caseId, exactInput, txShaEqual);
        const float tol = 0f;
        for (int b = first; b <= last; b++)
        {
            RxGoldenRun.StripeSnapshot o = snaps[b];
            string[] g = sync[b];
            string[] a = acq[b];
            Exact(s, b, "time_sync", I(g[1]), o.TimeSync ? 1 : 0);
            Exact(s, b, "freq_sync", I(g[2]), o.FreqSync ? 1 : 0);
            Exact(s, b, "frame_sync", I(g[3]), o.FrameSync ? 1 : 0);
            Exact(s, b, "robustness_mode", I(g[4]), o.Mode);
            Exact(s, b, "spectrum_occupancy_estimation", I(g[5]), o.SoEstimate);
            Exact(s, b, "iterationCounter", I(a[1]), o.IterationCounter);
            Exact(s, b, "counter", I(a[2]), o.Counter);
            Exact(s, b, "time_offset_integer", I(a[8]), o.TimeOffsetInteger);
            Float(s, b, "freq_offset_integer", F(a[11]), o.FreqOffsetInteger, 0f);
            Float(s, b, "time_offset", F(a[7]), o.TimeOffset, tol);
            Float(s, b, "samplerate_offset_estimation", F(a[9]), o.SroEstimate, tol);
            Float(s, b, "frequency_offset_fractional_init", F(a[10]), o.FreqFractInit, tol);
            Float(s, b, "freq_offset_init", F(a[12]), o.FreqOffsetInit, tol);
            Float(s, b, "time_offset_fractional_init", F(a[13]), o.TimeFractInit, tol);
            Float(s, b, "delta_time_offset_I_init", F(a[14]), o.DtoIInit, tol);
            bool acquiring = I(a[5]) == 0;
            if (realStage || acquiring)
            {
                Exact(s, b, "rsbufwidx", I(a[3]), o.RsBufWidx);
                Exact(s, b, "symbufwidx", I(a[4]), o.SymBufWidx);
                Exact(s, b, "symbol_counter", I(a[5]), o.SymbolCounter);
                Exact(s, b, "input_samples_buffer_request", I(a[6]), o.Request);
            }

            // Zi is not reset by init(); before this acquisition's frequencySync it holds the tracker's state.
            if (acquiring && I(g[2]) == 1)
            {
                for (int z = 0; z < 6; z++)
                    Float(s, b, $"Zi{z}", F(a[15 + z]), o.Zi[z], tol);
            }
            else if (realStage)
            {
                for (int z = 0; z < 6; z++)
                    s.Tracking(b, $"Zi{z}", F(a[15 + z]), o.Zi[z], z == 1 ? RxChannelEstimationGoldenTests.ZiPhaseTolerance : ZiTrackingTolerance, z == 1);
            }

            if (realStage)
            {
                Exact(s, b, "spectrum_occupancy", I(g[6]), o.SpectrumOccupancy);
                s.Tracking(b, "freqOffset", F(g[7]), o.FreqOffset, RxChannelEstimationGoldenTests.FreqOffsetToleranceHz, false);
                s.Tracking(b, "WMERFAC", F(g[9]), o.WMerFac, RxChannelEstimationGoldenTests.WMerFacToleranceDb, false);
            }
            else if (I(g[3]) == 1 && I(a[5]) <= DrmRxTables.SymbolsPerFrame[o.Mode])
            {
                // The stub adds symbols_per_frame per frame without the tracker's ±1 steps: checked through the first frame only.
                Exact(s, b, "symbol_counter", I(a[5]), o.SymbolCounter);
            }

            if (I(a[5]) != 0 && I(g[3]) == 1 && (b == 0 || I(sync[b - 1][3]) == 0))
                s.OnsetsWithFrameInSameCall++;
            s.StripesCompared++;
            if (o.FrameSync)
                s.FrameSyncStripes++;
        }

        return s;
    }

    private static void Exact(AcqStats s, int stripe, string field, int golden, int ours)
    {
        if (golden != ours)
            s.Failures.Add($"stripe {stripe} {field}: QSSTV {golden}, ours {ours}");
    }

    private static void Float(AcqStats s, int stripe, string field, float golden, float ours, float tol)
    {
        if (golden.Equals(ours))
            return;
        double d = Math.Abs((double)golden - ours);
        if (!(d <= tol))
        {
            s.Failures.Add($"stripe {stripe} {field}: QSSTV {golden:R}, ours {ours:R}");
            return;
        }

        if (d > s.MaxFloatDelta)
        {
            s.MaxFloatDelta = d;
            s.MaxFloatDeltaField = field;
        }
    }

    private static int I(string v) => int.Parse(v, CultureInfo.InvariantCulture);

    private static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);

    private static string[][] Csv(string dir, string file) => RxGoldenRun.Csv(dir, file);

    /// <summary>The case's 48 kHz int16 TX stream: QSSTV's when stored in full, else our P3 TX output proven sha-equal to it.</summary>
    internal static short[] LoadInput(string caseId, out bool shaEqualToQsstv)
    {
        using JsonDocument doc = GoldenFixtures.LoadManifest(caseId);
        JsonElement out48 = doc.RootElement.GetProperty("stages").GetProperty("out48k");
        if (FullStreamCases.Contains(caseId))
        {
            shaEqualToQsstv = true;
            return MemoryMarshal.Cast<byte, short>(GoldenFixtures.ReadStored(caseId, "out48k.bin.gz")).ToArray();
        }

        (DrmTxSettings settings, List<DrmSourceFrame> frames) = TxGoldenCase.SourceFrames(caseId, doc.RootElement);
        short[] ours = new DrmTxChannel(settings, null).Modulate(frames).SelectMany(f => f).ToArray();
        shaEqualToQsstv = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(ours.AsSpan())))
            .Equals(out48.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(shaEqualToQsstv, $"{caseId}: our TX stream is not QSSTV's, so it is not the golden RX input");
        return ours;
    }

    /// <summary>Our P5a front end on the stream as the harness feeds it: 4096-sample blocks, last one zero-padded, 8 zero blocks.</summary>
    internal static double[] FrontEnd(short[] pcm)
    {
        int blocks = (pcm.Length + Block48k - 1) / Block48k + TailZeroBlocks;
        var input = new float[blocks * Block48k];
        for (int i = 0; i < pcm.Length; i++)
            input[i] = pcm[i] / 32768f;
        var fe = new DrmRxFrontEnd(48000);
        var stripes = new List<double>(blocks * 1024);
        fe.Push(input, s => stripes.AddRange(s.ToArray()));
        return stripes.ToArray();
    }

    private static int MaxUlp(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Assert.Equal(a.Length, b.Length);
        int max = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i])
                continue;
            int ia = BitConverter.SingleToInt32Bits(a[i]), ib = BitConverter.SingleToInt32Bits(b[i]);
            if ((ia < 0) != (ib < 0))
                return int.MaxValue;
            max = Math.Max(max, Math.Abs(ia - ib));
        }

        return max;
    }

    internal sealed class AcqStats(string caseId, bool exactInput, bool txShaEqual)
    {
        public string CaseId { get; } = caseId;
        public bool ExactInput { get; } = exactInput;
        public bool TxShaEqual { get; } = txShaEqual;
        public List<string> Failures { get; } = [];
        public int StripesCompared { get; set; }
        public int FrameSyncStripes { get; set; }
        public int OnsetsWithFrameInSameCall { get; set; }
        public double MaxFloatDelta { get; set; }
        public string MaxFloatDeltaField { get; set; } = "-";
        public Dictionary<string, double> MaxTrackingDelta { get; } = [];

        /// <summary>A tracking-stage float: |Δ| within <paramref name="tol"/> (modulo 2π for a phase); NaN/Inf only match their class.</summary>
        public void Tracking(int stripe, string field, float golden, float ours, double tol, bool phase)
        {
            if (golden.Equals(ours))
            {
                MaxTrackingDelta.TryAdd(field, 0);
                return;
            }

            double d = Math.Abs((double)golden - ours);
            if (phase)
                d = Math.Min(d, Math.Abs(d - 2 * Math.PI));
            if (!(d <= tol))
            {
                Failures.Add($"stripe {stripe} {field}: QSSTV {golden:R}, ours {ours:R}");
                return;
            }

            MaxTrackingDelta[field] = Math.Max(MaxTrackingDelta.GetValueOrDefault(field), d);
        }

        public override string ToString() =>
            $"{CaseId}: input {(ExactInput ? "QSSTV" : TxShaEqual ? "ourTX(sha=)" : "ourTX")}, stripes {StripesCompared}, frame-sync stripes {FrameSyncStripes} (onsets with a frame in the same call {OnsetsWithFrameInSameCall}), max acquisition float Δ {MaxFloatDelta:E3} ({MaxFloatDeltaField}), tracking max |Δ| {string.Join(" ", MaxTrackingDelta.Select(kv => $"{kv.Key}={kv.Value:E2}"))}, failures {Failures.Count}"
            + (Failures.Count > 0 ? " first: " + Failures[0] : string.Empty);
    }
}

/// <summary>Test frame stage: QSSTV channelEstimation's sample bookkeeping (demodulator.cpp:565-571, 1112-1130)
/// without the estimation itself, consuming one frame of <c>symbols_per_frame · Ts</c> samples (QSSTV adds the
/// tracker's ±1 steps); requests re-acquisition at the given stripes.</summary>
internal sealed class GoldenResyncFrameStage(IReadOnlySet<int> resyncStripes) : IDrmFrameStage
{
    public int CurrentStripe { get; set; }

    public int ResetCalls { get; private set; }

    public int FrameSyncCalls { get; private set; }

    public int Frames { get; private set; }

    public int ZeroRequestCalls { get; private set; }

    public void Reset() => ResetCalls++;

    public void BeginDemodulate()
    {
    }

    public void OnFrameSync() => FrameSyncCalls++;

    public bool ProcessFrame(DrmDemodState st)
    {
        if (resyncStripes.Contains(CurrentStripe))
        {
            st.DoSynchronize = true;
            st.NSamplesNeeded = DrmRxTables.SymbolsModeDetection * DrmRxTables.ModeDetectionStride - st.RsBufWidx;
            st.InputSamplesBufferRequest = st.NSamplesNeeded > 0 ? st.NSamplesNeeded : 0;
            return true;
        }

        st.NSymbolsNeeded = st.SymbolsPerFrame + st.SymbolsPer2DWindow - st.SymBufWidx;
        st.NSamplesNeeded = (st.NSymbolsNeeded + 1) * st.Ts - st.RsBufWidx;
        if (st.NSamplesNeeded > 0)
        {
            st.InputSamplesBufferRequest = st.NSamplesNeeded;
            return false;
        }

        int tSmp = st.SymbolsPerFrame * st.Ts;
        st.SymbolCounter += st.SymbolsPerFrame;
        st.RsBufWidx -= tSmp;
        Array.Copy(st.RsBuffer, 2 * tSmp, st.RsBuffer, 0, 2 * st.RsBufWidx);
        st.NSamplesNeeded = (st.SymbolsPerFrame + 1) * st.Ts - st.RsBufWidx;
        st.InputSamplesBufferRequest = st.NSamplesNeeded > 0 ? st.NSamplesNeeded : 0;
        if (st.InputSamplesBufferRequest == 0)
            ZeroRequestCalls++;
        Frames++;
        return true;
    }
}
