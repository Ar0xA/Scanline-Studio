using ScanlineStudio.Abstractions.Drm;
using System.Collections.Concurrent;
using System.Globalization;
using ScanlineStudio.Core.Drm.Rx;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>One golden case run through our P5a front end and the real receiver (acquisition plus
/// <see cref="DrmChannelEstimator"/>), recording the state after every stripe and every equalized frame. Runs
/// are cached per case so the P5b and P5c golden tests share them.</summary>
/// <remarks>The frame stage is the real <see cref="DrmRxFrameStage"/> (channel estimation, channel decoding and source
/// decoding). Two source decoders share the frames (<see cref="TeeSink"/>): <see cref="Result.Legacy"/> reads the RS
/// size byte signed as x86 QSSTV does, so every output can be compared with the golden byte for byte;
/// <see cref="Result.Fixed"/> is the production setting, with that bug fixed.</remarks>
internal static class RxGoldenRun
{
    /// <summary>B0_q4_high_long is knife-edge (probes/pre-p5.md): frame-by-frame comparison stops here.</summary>
    public const int KnifeEdgeLastStripe = 104;

    private static readonly ConcurrentDictionary<string, Lazy<Result>> Cache = new();

    public static readonly string[] FullStreamCases =
        ["A0_q4_high_long", "A1_q4_high_long", "B0_q4_high_long", "B1_q16_high_long", "E0_q4_high_long", "E1_q4_high_long"];

    public static readonly string[] FullCellCases = ["A0_q4_high_long", "B1_q16_high_long", "E1_q4_high_long"];

    public static Result Get(string caseId) =>
        Cache.GetOrAdd(caseId, id => new Lazy<Result>(() => RunResolvingTies(id), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>Runs the case; where the run leaves QSSTV right after a peak-search near-tie, reruns it with the
    /// tracker forced to the competing bin at that symbol (<see cref="DrmOfdmSyncDemodulator.ForceRunnerUpAtCalls"/>),
    /// so the rest of the run is compared against QSSTV instead of being cut. A tie that forcing does not resolve
    /// stays as <see cref="Result.TieCutReason"/>.</summary>
    private static Result RunResolvingTies(string caseId)
    {
        var forced = new SortedSet<int>();
        Result r = Run(caseId, forced);
        for (int i = 0; i < 4 && r.TieCall is int call && !forced.Contains(call); i++)
        {
            forced.Add(call);
            r = Run(caseId, forced);
        }

        return r;
    }

    /// <summary>A peak-search near-tie: the best and the best competing bin of the smoothed impulse response
    /// (a different position mod Tc) within this relative margin. FFTW's and our float FFTs differ by ~1e-7
    /// relative per bin, so below this margin QSSTV may pick the other bin; only a rerun with that bin forced
    /// shows whether it did.</summary>
    public const double TieMargin = 1e-6;

    /// <summary>The planned compare range: all stripes, except B0_q4_high_long (knife edge).</summary>
    public static int PlannedLastStripe(string caseId, int stripes) =>
        caseId == "B0_q4_high_long" ? KnifeEdgeLastStripe : stripes - 1;

    private static Result Run(string caseId, IReadOnlySet<int> forcedCalls)
    {
        string[][] sync = Csv(caseId, "rx/rx_sync.csv.gz");
        short[] input = RxAcquisitionGoldenTests.LoadInput(caseId, out bool txShaEqual);
        double[] stripes = RxAcquisitionGoldenTests.FrontEnd(input);
        if (stripes.Length != sync.Length * 1024)
            throw new InvalidDataException($"{caseId}: {stripes.Length / 1024} stripes, QSSTV {sync.Length}");

        int planned = PlannedLastStripe(caseId, sync.Length);
        bool full = FullCellCases.Contains(caseId);
        var estimator = new DrmChannelEstimator();
        double frameMinMargin = double.MaxValue;
        var margins = new List<double>();
        estimator.Tracker.Observer = (filtered, tc) =>
        {
            double m = PeakMargin(filtered, tc);
            margins.Add(m);
            frameMinMargin = Math.Min(frameMinMargin, m);
        };
        estimator.Tracker.ForceRunnerUpAtCalls = forcedCalls.Count > 0 ? forcedCalls : null;
        var decoder = new DrmChannelDecoder();
        var legacy = new SourceRecorder(new DrmSourceDecoder { ReproduceQsstvSignedSurplus = true });
        var fixedSource = new SourceRecorder(new DrmSourceDecoder());
        var stage = new CountingStage(new DrmRxFrameStage(estimator, decoder, new TeeSink(legacy.Decoder, fixedSource.Decoder)));
        var demod = new DrmDemodulator(stage);
        var snaps = new List<StripeSnapshot>(sync.Length);
        var frames = new List<FrameRecord>();
        var decoded = new List<DecodedFrame>();
        for (int b = 0; b < sync.Length; b++)
        {
            int before = estimator.FramesProcessed;
            int firstCall = estimator.Tracker.Calls;
            int wptrBefore = demod.State.TransmissionFrameBufferWptr;
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
            DrmDemodState st = demod.State;
            if (estimator.FramesProcessed != before)
            {
                if (estimator.FramesProcessed != before + 1)
                    throw new InvalidOperationException($"{caseId}: two frames in stripe {b}");
                frames.Add(FrameRecord.Capture(b, wptrBefore, st, estimator, full, frameMinMargin, firstCall));
                frameMinMargin = double.MaxValue;
            }

            if (st.TransmissionFrameBufferDataValid == 1)
            {
                decoded.Add(DecodedFrame.Capture(b, st, decoder, legacy.Decoder));
            }

            snaps.Add(new StripeSnapshot(st.TimeSyncFlag, st.FrequencySyncFlag, st.FrameSyncFlag, st.RobustnessMode, st.SpectrumOccupancyEstimation,
                st.SpectrumOccupancy, st.FreqOffset, st.WMerFac,
                st.IterationCounter, st.Counter, st.RsBufWidx, st.SymBufWidx, st.SymbolCounter, st.InputSamplesBufferRequest,
                st.TimeOffset, st.TimeOffsetInteger, st.SamplerateOffsetEstimation, st.FrequencyOffsetFractionalInit, st.FreqOffsetInteger,
                st.FreqOffsetInit, st.TimeOffsetFractionalInit, st.DeltaTimeOffsetIInit, (float[])st.Zi.Clone(), estimator.FramesProcessed, st.FacValid));
        }

        (int last, string? cut, int? tieCall) = TieCut(caseId, planned, frames, margins);
        return new Result(caseId, FullStreamCases.Contains(caseId), txShaEqual, snaps, frames, stage.ZeroRequestCalls, last, cut, tieCall,
            forcedCalls.ToArray(), frames.Count(f => f.Stripe <= last && f.MinPeakMargin <= TieMargin), decoded, legacy, fixedSource);
    }

    /// <summary>The compare range after the tie rule: frames are followed until the first frame whose tracking
    /// leaves the golden (stripe, symbol_counter, rsbufwidx, data-valid, Zi4/Zi5); if that frame or the one
    /// before had a peak-search near-tie (<see cref="TieMargin"/>), comparison ends at the stripe before it and the
    /// tracker call with the smallest margin there is returned. A divergence without a near-tie keeps the planned
    /// range, so the golden tests report it as a failure.</summary>
    private static (int Last, string? Reason, int? TieCall) TieCut(string caseId, int planned, List<FrameRecord> frames, List<double> margins)
    {
        string[][] g = Csv(Path.Combine("rx_cells", caseId), "rx_cells.csv.gz");
        int n = Math.Min(frames.Count, g.Length);
        for (int f = 0; f < n; f++)
        {
            FrameRecord o = frames[f];
            if (o.Stripe > planned)
                break;
            bool same = I(g[f][1]) == o.Stripe && I(g[f][3]) == o.SymbolCounter && I(g[f][4]) == o.RsBufWidx && I(g[f][27]) == o.DataValid
                && Math.Abs(F(g[f][12]) - o.Zi[4]) <= RxChannelEstimationGoldenTests.ZiTolerance
                && Math.Abs(F(g[f][13]) - o.Zi[5]) <= RxChannelEstimationGoldenTests.ZiTolerance;
            if (same)
                continue;
            int from = f > 0 ? frames[f - 1].FirstCall : o.FirstCall;
            int to = f + 1 < frames.Count ? frames[f + 1].FirstCall : margins.Count;
            int call = Enumerable.Range(from, to - from).MinBy(c => margins[c]);
            if (margins[call] <= TieMargin)
                return (Math.Min(planned, o.Stripe - 1), $"frame {f} (stripe {o.Stripe}) follows a peak-search near-tie at tracker call {call}, relative margin {margins[call]:E2}", call);
            return (planned, null, null);
        }

        return (planned, null, null);
    }

    /// <summary>Relative margin between the first maximum of the smoothed impulse response and the best bin at a
    /// different position mod Tc (the duplicated half maps to the same timing, getofdmsync.cpp:192-231).</summary>
    private static double PeakMargin(ReadOnlySpan<float> filtered, int tc)
    {
        int arg = 0;
        float best = float.MinValue;
        for (int i = 0; i < filtered.Length; i++)
        {
            if (filtered[i] > best)
            {
                best = filtered[i];
                arg = i;
            }
        }

        float second = float.MinValue;
        for (int i = 0; i < filtered.Length; i++)
        {
            if (i % tc != arg % tc && filtered[i] > second)
                second = filtered[i];
        }

        return best > 0 ? ((double)best - second) / best : double.MaxValue;
    }

    private static int I(string v) => int.Parse(v, CultureInfo.InvariantCulture);

    private static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);

    public static string[][] Csv(string dir, string file)
    {
        string text = System.Text.Encoding.ASCII.GetString(GoldenFixtures.ReadStored(dir, file));
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')).ToArray();
    }

    public sealed record Result(string CaseId, bool ExactInput, bool TxShaEqual, List<StripeSnapshot> Stripes, List<FrameRecord> Frames, int ZeroRequestCalls,
        int LastStripe, string? TieCutReason, int? TieCall, int[] ForcedCalls, int NearTieFrames, List<DecodedFrame> Decoded,
        SourceRecorder Legacy, SourceRecorder Fixed);

    public readonly record struct StripeSnapshot(
        bool TimeSync, bool FreqSync, bool FrameSync, int Mode, int SoEstimate, int SpectrumOccupancy, float FreqOffset, float WMerFac,
        int IterationCounter, int Counter, int RsBufWidx, int SymBufWidx, int SymbolCounter, int Request,
        float TimeOffset, int TimeOffsetInteger, float SroEstimate, float FreqFractInit, float FreqOffsetInteger,
        float FreqOffsetInit, float TimeFractInit, float DtoIInit, float[] Zi, int FramesProcessed, int FacValid);

    public sealed record FrameRecord(
        int Stripe, int WptrBefore, int SymbolCounter, int RsBufWidx, int SymBufWidx, int Request, int IterationCounter, float[] Zi,
        float DeltaFreqOffset, float FreqOffset, float DeltaFs, float SamplerateOffset, float WMerFac, float MeanEnergy, int LFac,
        int Moc, int MocLast, int SnrTimeoutCounter, int SnrTimeOutCounter, int FacNotValidCounter, bool DoSynchronize, int DataValid,
        int PhaseDiff, float PhaseOffset, int InOutDelay, int KMin, int KMax, int SpectrumOccupancy, double CellsEnergy, double HEnergy,
        float[] FacCells, float[]? Cells, float[]? H, double MinPeakMargin, int FirstCall)
    {
        public static FrameRecord Capture(int stripe, int wptrBefore, DrmDemodState st, DrmChannelEstimator ce, bool full, double minPeakMargin, int firstCall)
        {
            int lH = ce.KMax - ce.KMin + 1;
            var cells = new float[2 * st.SymbolsPerFrame * lH];
            var h = new float[cells.Length];
            double ec = 0, eh = 0;
            int n = 0;
            for (int i = 0; i < st.SymbolsPerFrame; i++)
            {
                for (int j = ce.KMin; j <= ce.KMax; j++)
                {
                    int t = wptrBefore + i * st.KModulo + st.KDc + j;
                    float cr = st.TransmissionFrameBuffer[2 * t], ci = st.TransmissionFrameBuffer[2 * t + 1];
                    float hr = st.ChannelTransferFunctionBuffer[2 * t], hi = st.ChannelTransferFunctionBuffer[2 * t + 1];
                    ec += (double)cr * cr + (double)ci * ci;
                    eh += (double)hr * hr + (double)hi * hi;
                    cells[n] = cr;
                    cells[n + 1] = ci;
                    h[n] = hr;
                    h[n + 1] = hi;
                    n += 2;
                }
            }

            return new FrameRecord(stripe, wptrBefore, st.SymbolCounter, st.RsBufWidx, st.SymBufWidx, st.InputSamplesBufferRequest, st.IterationCounter,
                (float[])st.Zi.Clone(), ce.DeltaFreqOffset, st.FreqOffset, st.DeltaFs, st.SamplerateOffset, st.WMerFac, st.MeanEnergyOfUsedCells,
                st.LFac, ce.ModeAndOccupancyCode, ce.ModeAndOccupancyCodeLast, ce.SnrTimeoutCounter, ce.SnrTimeOutCounter, ce.FacNotValidCounter,
                st.DoSynchronize, st.TransmissionFrameBufferDataValid, ce.SmpRateConvFftPhaseDiff, ce.SmpRateConvFftPhaseOffset,
                ce.SmpRateConvInOutDelay, ce.KMin, ce.KMax, st.SpectrumOccupancy, ec, eh, st.FacCellsSequence[..(2 * st.LFac)],
                full ? cells : null, full ? h : null, minPeakMargin, firstCall);
        }
    }

    /// <summary>One rx_frames row: the state after a stripe whose frame was data-valid (harness rx.cpp:340-372).</summary>
    public sealed record DecodedFrame(
        int Stripe, int FacValid, int Mode, int SoNew, int MscModeNew, int InterleaverDepthNew, int PlPartA, int PlPartB, int HmLength,
        bool CallsignValid, string Callsign, int MscDataValid, int Length, int CrcOk, int First, int Last, byte[]? Bits,
        int MscValid, string DataGroup, int CurrentSegmentNumber, uint RxTransportId, int BodyTotalSegments, int RxSegments)
    {
        public static DecodedFrame Capture(int stripe, DrmDemodState st, DrmChannelDecoder d, DrmSourceDecoder src)
        {
            int crcOk = -1, first = -1, last = -1;
            byte[]? bits = null;
            if (d.MscDataValid == 1)
            {
                ReadOnlySpan<double> data = d.ChannelDecodedData;
                bits = new byte[data.Length];
                for (int i = 0; i < data.Length; i++)
                    bits[i] = data[i] != 0 ? (byte)1 : (byte)0;
                int nb = data.Length / 8;
                var bytes = new byte[nb + 1];
                DrmRxBits.BitsToBytes(data[..(nb * 8)], bytes);
                crcOk = ScanlineStudio.Core.Drm.Util.RxCrc.Crc16Bytewise(bytes.AsSpan(0, nb)) == 0 ? 1 : 0;
                first = (bytes[0] & 0x80) != 0 ? 1 : 0;
                last = (bytes[0] & 0x40) != 0 ? 1 : 0;
            }

            // The harness prints currentDataPacket only for a CRC-valid last packet (rx.cpp:356-361), stale or not.
            string dg = ",,,,,";
            if (crcOk == 1 && last == 1)
            {
                DrmDataGroup p = src.CurrentDataGroup;
                dg = string.Join(',', S(p.DataGroupType), S(p.SegmentNumber), p.LastSegment ? "1" : "0", S(p.TransportId), S(p.SegmentSize), S(p.Lenght));
            }

            return new DecodedFrame(stripe, st.FacValid, st.RobustnessMode, d.SpectrumOccupancyNew, d.MscModeNew, d.InterleaverDepthNew,
                d.PlPartA, d.PlPartB, d.HmLength, d.CallsignValid, d.Callsign, d.MscDataValid, d.LengthDecodedData, crcOk, first, last, bits,
                (int)src.MscValid, dg, src.CurrentSegmentNumber, src.RxTransportId, src.BodyTotalSegments, src.RxSegments);
        }

        private static string S(int v) => v.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Feeds one frame to several source decoders. Each clears <c>callsignValid</c> when it opens a transport
    /// block; they open the same blocks at the same frame, so the decoder state matches a single one.</summary>
    internal sealed class TeeSink(params IDrmMscSink[] sinks) : IDrmMscSink
    {
        public void OnChannelDecoded(DrmChannelDecoder decoder, DrmDemodState state)
        {
            foreach (IDrmMscSink s in sinks)
                s.OnChannelDecoded(decoder, state);
        }

        public void Init()
        {
            foreach (IDrmMscSink s in sinks)
                s.Init();
        }
    }

    /// <summary>A source decoder and everything it raised, as harness-style event strings (harness main.cpp:16-40):
    /// "rxDRMStatus:&lt;name&gt;", "file:&lt;QSSTV name&gt;", "prepareFix:&lt;bytes&gt;", "alreadyReceived:&lt;name&gt;".</summary>
    internal sealed class SourceRecorder
    {
        public SourceRecorder(DrmSourceDecoder decoder)
        {
            Decoder = decoder;
            decoder.ReceivingNameChanged += n => Events.Add("rxDRMStatus:" + n);
            decoder.FileReceived += f =>
            {
                Files.Add(f);
                Events.Add("file:" + f.QsstvFileName);
            };
            decoder.BsrReceived += b =>
            {
                Bsr.Add(b.ToArray());
                Events.Add("prepareFix:" + b.Length.ToString(CultureInfo.InvariantCulture));
            };
            decoder.AlreadyReceived += n => Events.Add("alreadyReceived:" + n);
        }

        public DrmSourceDecoder Decoder { get; }

        public List<string> Events { get; } = [];

        public List<DrmReceivedFile> Files { get; } = [];

        public List<byte[]> Bsr { get; } = [];
    }

    /// <summary>The real frame stage, counting frames that leave input_samples_buffer_request at 0.</summary>
    internal sealed class CountingStage(DrmRxFrameStage inner) : IDrmFrameStage
    {
        public int ZeroRequestCalls { get; private set; }

        public void Reset() => inner.Reset();

        public void BeginDemodulate() => inner.BeginDemodulate();

        public void OnFrameSync() => inner.OnFrameSync();

        public void ResetReceiver(DrmDemodState state) => inner.ResetReceiver(state);

        public bool ProcessFrame(DrmDemodState st)
        {
            bool frame = inner.ProcessFrame(st);
            if (frame && st.InputSamplesBufferRequest == 0)
                ZeroRequestCalls++;
            return frame;
        }
    }
}
