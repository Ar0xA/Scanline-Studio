using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5b: mode detection and time/frequency/frame acquisition against QSSTV, per stripe, all 79 cases.</summary>
/// <remarks>
/// <para><b>Input.</b> The six cases with a stored full <c>out48k</c> use QSSTV's own TX stream. The other 73
/// use our P3 TX output, and the test requires its SHA-256 to equal QSSTV's <c>out48k</c> sha256, so every
/// case runs on QSSTV's exact input. Both run through our P5a front end (48 kHz path, 4096 blocks, 8 zero tail
/// blocks) as in the harness.</para>
/// <para><b>Compared exactly</b> (rx_sync): time_sync, freq_sync, frame_sync, robustness_mode,
/// spectrum_occupancy_estimation. (rx_acq): iterationCounter, counter, time_offset_integer, freq_offset_integer;
/// and, while QSSTV's channel estimator has not yet consumed samples (golden symbol_counter == 0): rsbufwidx,
/// symbufwidx, input_samples_buffer_request, and Zi once frequencySync has run; at frame-sync stripes up to
/// QSSTV's first equalized frame, symbol_counter (which also catches frame sync and the first frame landing in
/// the same call). Later symbol_counter values depend on P5c's tracking and are not compared.</para>
/// <para><b>Float fields</b> (time_offset, samplerate_offset_estimation, frequency_offset_fractional_init,
/// freq_offset_init, time_offset_fractional_init, delta_time_offset_I_init, and Zi)
/// are compared bit-exact: none of them passes through the FFT. The FFT only reaches the decisions
/// (integer frequency offset, occupancy, frame start), which are compared exactly. Tolerance 0 assumes glibc
/// libm on both sides (QSSTV's g++ build binds float atan2/tanh/cos to atan2f/tanhf/cosf; .NET's MathF calls
/// the C runtime). Windows UCRT or macOS libm may differ by 1 ulp in these fields; the product is unaffected,
/// only this bit-exact comparison would need a tolerance there.</para>
/// <para><b>Not compared here</b> (belong to P5c/P6): rx_sync spectrum_occupancy (set by channelEstimation
/// and channel_decoding), freqOffset and WMERFAC (channelEstimation's tracking and SNR), fac_valid (FAC decode).</para>
/// <para><b>Range.</b> Stripes up to the last frame QSSTV decoded; the zero tail after it is not compared
/// (fixtures README). B0_q4_high_long is knife-edge (probes/pre-p5.md) and is compared up to stripe 104.</para>
/// <para><b>Seam.</b> The frame stage is <see cref="GoldenResyncFrameStage"/>: it consumes samples like
/// channelEstimation's bookkeeping and requests re-acquisition exactly where QSSTV's did. QSSTV's resync is
/// its SNR rule (WMERFAC &lt; 3 dB, demodulator.cpp:1090-1105), not FAC validity, so the stub takes the
/// resync stripes from the golden time_sync drops and checks that WMERFAC there is below 3 dB.</para>
/// </remarks>
public sealed class RxAcquisitionGoldenTests(ITestOutputHelper output)
{
    /// <summary>Symbol handoff: |Δ| per component relative to the largest |component| of the window. FFTW
    /// codelets vs our mixed-radix FFT differ in float rounding only; measured worst 1.24e-7 (B1_q16_high_long).</summary>
    private const double HandoffSymbolRelTolerance = 1e-6;

    private const int Block48k = 4096;
    private const int TailZeroBlocks = 8;
    private const int KnifeEdgeLastStripe = 104;

    private static readonly string[] FullStreamCases =
        ["A0_q4_high_long", "A1_q4_high_long", "B0_q4_high_long", "B1_q16_high_long", "E0_q4_high_long", "E1_q4_high_long"];

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
        CaseRun run = RunCase(caseId);
        AcqStats s = Compare(caseId, run);
        output.WriteLine(s.ToString());
        Assert.True(s.Failures.Count == 0, $"{caseId}: {string.Join("; ", s.Failures.Take(8))}");
        Assert.True(s.StripesCompared > 50, $"{caseId}: only {s.StripesCompared} stripes compared");
        Assert.True(s.FrameSyncStripes > 0, $"{caseId}: frame sync never reached");
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

        var stage = new GoldenResyncFrameStage(new HashSet<int>());
        var demod = new DrmDemodulator(stage);
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

    /// <summary>The re-acquisition path: QSSTV's only in-signal resync (B0_q4_high_long, SNR drop at stripe 170,
    /// pre-p5.md) is injected through the seam; from there to the next frame-sync handoff everything is
    /// acquisition on the input alone and must match exactly, although frame-by-frame comparison of this
    /// knife-edge case stops at stripe 104.</summary>
    [Fact]
    public void Reacquisition_AfterInjectedResync_MatchesQsstv()
    {
        const string caseId = "B0_q4_high_long";
        CaseRun run = RunCase(caseId);
        Assert.Equal(1, run.Resyncs);
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        string[][] acq = Csv(Path.Combine("rx_acq", caseId), "rx_acq.csv.gz");
        int resync = Enumerable.Range(0, sync.Length - 1).Single(s => sync[s][1] == "1" && sync[s][3] == "1" && sync[s + 1][1] == "0");
        int handoff = Enumerable.Range(resync + 1, sync.Length - resync - 1).First(s => sync[s][3] == "1");
        int end = handoff;
        while (end + 1 < acq.Length && acq[end + 1][5] == "0")
            end++;
        AcqStats s = Compare(caseId, run, resync + 1, end);
        output.WriteLine($"resync at stripe {resync}, frame sync again at {handoff}, compared {resync + 1}..{end}: {s}");
        Assert.Empty(s.Failures);
        Assert.True(s.FrameSyncStripes > 0);
        Assert.True(handoff - resync > 5, "re-acquisition must wait for a full 20-symbol window");
    }

    private sealed record CaseRun(string CaseId, bool ExactInput, bool TxShaEqual, List<Snapshot> Snapshots, int Resyncs);

    private readonly record struct Snapshot(
        bool TimeSync, bool FreqSync, bool FrameSync, int Mode, int SoEstimate,
        int IterationCounter, int Counter, int RsBufWidx, int SymBufWidx, int SymbolCounter, int Request,
        float TimeOffset, int TimeOffsetInteger, float SroEstimate, float FreqFractInit, float FreqOffsetInteger,
        float FreqOffsetInit, float TimeFractInit, float DtoIInit, float[] Zi);

    private static CaseRun RunCase(string caseId)
    {
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        int[] timeSync = sync.Select(r => int.Parse(r[1], CultureInfo.InvariantCulture)).ToArray();
        int[] frameSync = sync.Select(r => int.Parse(r[3], CultureInfo.InvariantCulture)).ToArray();
        var resync = new HashSet<int>();
        for (int s = 0; s + 1 < timeSync.Length; s++)
        {
            if (timeSync[s] == 1 && frameSync[s] == 1 && timeSync[s + 1] == 0)
            {
                resync.Add(s);
                float wmer = float.Parse(sync[s][9], CultureInfo.InvariantCulture);
                Assert.True(wmer < DrmRxTables.SnrMinDb, $"{caseId}: QSSTV resynced at stripe {s} with WMERFAC {wmer}");
            }
        }

        short[] input = LoadInput(caseId, out bool txShaEqual);
        bool exact = FullStreamCases.Contains(caseId);
        double[] stripes = FrontEnd(input);
        Assert.Equal(sync.Length * 1024, stripes.Length);

        var stage = new GoldenResyncFrameStage(resync);
        var demod = new DrmDemodulator(stage);
        var snaps = new List<Snapshot>(sync.Length);
        for (int b = 0; b < sync.Length; b++)
        {
            stage.CurrentStripe = b;
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
            DrmDemodState st = demod.State;
            snaps.Add(new Snapshot(st.TimeSyncFlag, st.FrequencySyncFlag, st.FrameSyncFlag, st.RobustnessMode, st.SpectrumOccupancyEstimation,
                st.IterationCounter, st.Counter, st.RsBufWidx, st.SymBufWidx, st.SymbolCounter, st.InputSamplesBufferRequest,
                st.TimeOffset, st.TimeOffsetInteger, st.SamplerateOffsetEstimation, st.FrequencyOffsetFractionalInit, st.FreqOffsetInteger,
                st.FreqOffsetInit, st.TimeOffsetFractionalInit, st.DeltaTimeOffsetIInit, (float[])st.Zi.Clone()));
        }

        Assert.Equal(0, stage.ZeroRequestCalls);
        return new CaseRun(caseId, exact, txShaEqual, snaps, resync.Count);
    }

    private static AcqStats Compare(string caseId, CaseRun run, int first = 0, int? lastStripe = null)
    {
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        string[][] acq = Csv(Path.Combine("rx_acq", caseId), "rx_acq.csv.gz");
        string[][] frames = Csv(caseId, "rx/rx_frames.csv.gz");
        int last = frames.Length == 0 ? sync.Length - 1 : frames.Max(r => int.Parse(r[1], CultureInfo.InvariantCulture));
        if (caseId == "B0_q4_high_long")
            last = Math.Min(last, KnifeEdgeLastStripe);
        if (lastStripe is int ls)
            last = ls;

        var s = new AcqStats(caseId, run.ExactInput, run.TxShaEqual) { Resyncs = run.Resyncs };
        const float tol = 0f;
        for (int b = first; b <= last; b++)
        {
            Snapshot o = run.Snapshots[b];
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
            if (I(a[5]) == 0)
            {
                Exact(s, b, "rsbufwidx", I(a[3]), o.RsBufWidx);
                Exact(s, b, "symbufwidx", I(a[4]), o.SymBufWidx);
                Exact(s, b, "symbol_counter", 0, o.SymbolCounter);
                Exact(s, b, "input_samples_buffer_request", I(a[6]), o.Request);
                // Zi is not reset by init(); before this acquisition's frequencySync it holds P5c's tracking state.
                if (I(g[2]) == 1)
                {
                    for (int z = 0; z < 6; z++)
                        Float(s, b, $"Zi{z}", F(a[15 + z]), o.Zi[z], tol);
                }
            }

            // symbol_counter += 1 per equalized symbol (demodulator.cpp:910); the stub adds symbols_per_frame per frame.
            // Checked through the first frame only: later frame timing follows getofdmsync's ±1 steps (P5c), which the stub omits.
            if (I(g[3]) == 1 && I(a[5]) <= DrmRxTables.SymbolsPerFrame[o.Mode])
            {
                Exact(s, b, "symbol_counter", I(a[5]), o.SymbolCounter);
                if (I(a[5]) != 0 && (b == 0 || I(sync[b - 1][3]) == 0))
                    s.OnsetsWithFrameInSameCall++;
            }

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

    private static string[][] Csv(string dir, string file)
    {
        string text = System.Text.Encoding.ASCII.GetString(GoldenFixtures.ReadStored(dir, file));
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).ToArray();
    }

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

    private sealed class AcqStats(string caseId, bool exactInput, bool txShaEqual)
    {
        public string CaseId { get; } = caseId;
        public bool ExactInput { get; } = exactInput;
        public bool TxShaEqual { get; } = txShaEqual;
        public List<string> Failures { get; } = [];
        public int StripesCompared { get; set; }
        public int FrameSyncStripes { get; set; }
        public int OnsetsWithFrameInSameCall { get; set; }
        public int Resyncs { get; set; }
        public double MaxFloatDelta { get; set; }
        public string MaxFloatDeltaField { get; set; } = "-";

        public override string ToString() =>
            $"{CaseId}: input {(ExactInput ? "QSSTV" : TxShaEqual ? "ourTX(sha=)" : "ourTX")}, stripes {StripesCompared}, frame-sync stripes {FrameSyncStripes} (onsets with a frame in the same call {OnsetsWithFrameInSameCall}), resyncs {Resyncs}, max float Δ {MaxFloatDelta:E3} ({MaxFloatDeltaField}), failures {Failures.Count}"
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
