using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5c: QSSTV's channelEstimation (demodulator.cpp:524-1132) against the rx_cells golden, frame by
/// frame, all 79 cases (runs shared with <see cref="RxAcquisitionGoldenTests"/> via <see cref="RxGoldenRun"/>).</summary>
/// <remarks>
/// <para><b>Exact:</b> the frame's stripe, symbol_counter, rsbufwidx, symbufwidx, input_samples_buffer_request,
/// iterationCounter, mean_energy_of_used_cells, lFAC, mode_and_occupancy_code(_last), both SNR counters,
/// fac_not_valid_counter, doSynchronize, transmission_frame_buffer_data_valid, the integer sample-rate
/// converter fields, K_min/K_max, spectrum_occupancy; the Wiener tables' integer pilot lists and FAC map.</para>
/// <para><b>Tolerances</b> (float, FFTW codelets vs our mixed-radix FFT, both float32; nothing else differs):
/// the symbols entering the estimator already differ by ~1e-7 relative (P5b handoff), and the tracker's
/// impulse-response FFT and the Tu-point FFT add the same order per symbol; the frequency loop integrates it.
/// Bounds are about 10× the worst value measured over all 79 cases (stated per constant);
/// <see cref="FreqOffsetToleranceHz"/>/<see cref="WMerFacToleranceDb"/> are the fixture README's (measured
/// worst 3.05e-5 Hz and 4.46e-4 dB).</para>
/// <para><b>Ties.</b> The tracker picks the peak of the smoothed impulse response (getofdmsync.cpp:205-216).
/// With one dominant path the window sum is flat over several bins, so near-ties (relative margin ≤ 1e-6) are
/// common: 55 of 79 cases have at least one (pinned per case in <see cref="NearTieFrames"/>). QSSTV and we pick
/// the same bin everywhere except at one bit-equal tie in two cases (B1_q4_high_long_rs2 call 249,
/// A1_q64_high_long call 735). <see cref="RxGoldenRun"/> reruns those with the tracker forced to the competing
/// bin at that single call; the rest of both runs then matches QSSTV within the tolerances above to the end,
/// which shows the divergence is that tie decision alone. A divergence that forcing does not resolve fails
/// <see cref="PeakSearchTies_AsMeasured"/>.</para>
/// <para><b>Out of range by design.</b> B0_q4_high_long beyond stripe 104 is not compared frame by frame (the
/// plan's knife-edge rule); there frame 22's cells_energy differs by 1.1e-5 relative in a degraded frame, where
/// equalizing by a small |H| amplifies rounding (inferred, not traced).</para>
/// <para><b>Range.</b> Every frame, including the zero tail; B0_q4_high_long up to stripe 104 (knife edge),
/// after which it must keep at least QSSTV's data-valid frame count.</para>
/// </remarks>
public sealed class RxChannelEstimationGoldenTests(ITestOutputHelper output)
{
    /// <summary>freqOffset (Hz), |Δ|; it is −Zi4·12000/(2π·Tu). Measured worst 3.05e-5 (B1_q64_low_short).</summary>
    internal const double FreqOffsetToleranceHz = 3e-4;

    /// <summary>WMERFAC (dB), |Δ|. Measured worst 4.46e-4 (A1_q4_high_long).</summary>
    internal const double WMerFacToleranceDb = 5e-3;

    /// <summary>Frames per case (in the compared range) with a peak-search near-tie, margin ≤
    /// <see cref="RxGoldenRun.TieMargin"/>, as measured; 55 of 79 cases have at least one, E1 up to 26.</summary>
    private static readonly Dictionary<string, int> NearTieFrames = new()
    {
        ["A0_q16_high_long"] = 0, ["A0_q16_high_short"] = 0, ["A0_q16_low_long"] = 2, ["A0_q16_low_short"] = 1,
        ["A0_q4_high_long"] = 1, ["A0_q4_high_short"] = 2, ["A0_q4_low_long"] = 1, ["A0_q4_low_short"] = 2,
        ["A0_q64_high_long"] = 0, ["A0_q64_high_short"] = 0, ["A0_q64_low_long"] = 0, ["A0_q64_low_short"] = 2,
        ["A1_q16_high_long"] = 2, ["A1_q16_high_short"] = 0, ["A1_q16_low_long"] = 1, ["A1_q16_low_short"] = 1,
        ["A1_q4_high_long"] = 0, ["A1_q4_high_short"] = 0, ["A1_q4_low_long"] = 1, ["A1_q4_low_short"] = 1,
        ["A1_q64_high_long"] = 1, ["A1_q64_high_short"] = 1, ["A1_q64_low_long"] = 0, ["A1_q64_low_short"] = 1,
        ["B0_q16_high_long"] = 4, ["B0_q16_high_short"] = 14, ["B0_q16_low_long"] = 0, ["B0_q16_low_short"] = 2,
        ["B0_q4_high_long"] = 0, ["B0_q4_high_short"] = 2, ["B0_q4_low_long"] = 1, ["B0_q4_low_short"] = 12,
        ["B0_q64_high_long"] = 0, ["B0_q64_high_short"] = 0, ["B0_q64_low_long"] = 1, ["B0_q64_low_short"] = 1,
        ["B1_q16_high_long"] = 4, ["B1_q16_high_long_jp2"] = 3, ["B1_q16_high_short"] = 1, ["B1_q16_low_long"] = 0,
        ["B1_q16_low_short"] = 0, ["B1_q4_high_long"] = 1, ["B1_q4_high_long_bsr"] = 2, ["B1_q4_high_long_rs1"] = 0,
        ["B1_q4_high_long_rs1_jp2"] = 2, ["B1_q4_high_long_rs2"] = 12, ["B1_q4_high_long_rs3"] = 3, ["B1_q4_high_long_rs4"] = 5,
        ["B1_q4_high_short"] = 12, ["B1_q4_low_long"] = 8, ["B1_q4_low_short"] = 1, ["B1_q64_high_long"] = 0,
        ["B1_q64_high_short"] = 8, ["B1_q64_low_long"] = 2, ["B1_q64_low_short"] = 1, ["E0_q16_high_long"] = 1,
        ["E0_q16_high_short"] = 1, ["E0_q16_low_long"] = 3, ["E0_q16_low_short"] = 0, ["E0_q4_high_long"] = 1,
        ["E0_q4_high_short"] = 1, ["E0_q4_low_long"] = 0, ["E0_q4_low_short"] = 0, ["E0_q64_high_long"] = 0,
        ["E0_q64_high_short"] = 0, ["E0_q64_low_long"] = 0, ["E0_q64_low_short"] = 0, ["E1_q16_high_long"] = 24,
        ["E1_q16_high_short"] = 22, ["E1_q16_low_long"] = 8, ["E1_q16_low_short"] = 6, ["E1_q4_high_long"] = 4,
        ["E1_q4_high_short"] = 22, ["E1_q4_low_long"] = 22, ["E1_q4_low_short"] = 18, ["E1_q64_high_long"] = 25,
        ["E1_q64_high_short"] = 26, ["E1_q64_low_long"] = 9, ["E1_q64_low_short"] = 23,
    };

    /// <summary>The tracker calls where QSSTV took the competing bin of a bit-equal tie (measured; all others none).</summary>
    private static readonly Dictionary<string, int[]> ForcedTieCalls = new()
    {
        ["B1_q4_high_long_rs2"] = [249],
        ["A1_q64_high_long"] = [735],
    };

    /// <summary>Zi (other than the phase Zi1) while tracking, |Δ|. Measured worst 3.81e-6 (Zi4, B1_q64_low_short);
    /// Zi2 and Zi5 measured equal: the timing loop sees the FFT only through its peak decision.</summary>
    internal const double ZiTolerance = 4e-5;

    /// <summary>Zi1, the carried phase (rad, modulo 2π), |Δ|. Measured worst 2.67e-4 (B1_q4_high_long_rs4).</summary>
    internal const double ZiPhaseTolerance = 3e-3;

    /// <summary>delta_freq_offset (rad), |Δ|. Measured worst 2.67e-5 (A1_q4_high_long).</summary>
    private const double DeltaFreqOffsetTolerance = 3e-4;

    /// <summary>samplerate_offset and deltaFS, |Δ|. Measured equal (both follow Zi2).</summary>
    private const double SampleRateOffsetTolerance = 1e-7;

    /// <summary>Equalized cells (FAC and full frame), |Δ| per component; QAM cells are near unit amplitude.
    /// Measured worst 8.82e-6 (FAC, A1_q4_high_long), 3.34e-6 (full frames, E1_q4_high_long).</summary>
    internal const double CellTolerance = 1e-4;

    /// <summary>Channel estimate H, |Δ| per component relative to the frame's largest |H| component. Measured worst 3.95e-7.</summary>
    private const double HRelTolerance = 4e-6;

    /// <summary>Frame energy sums (cells, H), relative. Measured worst 3.04e-7.</summary>
    private const double EnergyRelTolerance = 3e-6;

    private static readonly string[] WCases =
        ["A0_q4_high_long", "A1_q4_high_long", "B0_q4_high_long", "B1_q16_high_long", "E0_q4_high_long", "E1_q4_high_long"];

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string id in GoldenFixtures.CaseIds())
            data.Add(id);
        return data;
    }

    public static TheoryData<string> WienerCases()
    {
        var data = new TheoryData<string>();
        foreach (string id in WCases)
            data.Add(id);
        return data;
    }

    public static TheoryData<string> FullCellCases()
    {
        var data = new TheoryData<string>();
        foreach (string id in RxGoldenRun.FullCellCases)
            data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Frames_MatchQsstv(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        string dir = Path.Combine("rx_cells", caseId);
        string[][] golden = RxGoldenRun.Csv(dir, "rx_cells.csv.gz");
        float[] goldenFac = MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, "fac_cells.f32.gz")).ToArray();
        int last = run.LastStripe;
        var s = new RxAcquisitionGoldenTests.AcqStats(caseId, run.ExactInput, run.TxShaEqual);
        int compared = 0, facPos = 0, valid = 0;
        double facWorst = 0;
        List<RxGoldenRun.FrameRecord> ours = run.Frames.Where(f => f.Stripe <= last).ToList();
        string[][] gRows = golden.Where(r => I(r[1]) <= last).ToArray();
        if (ours.Count != gRows.Length)
            s.Failures.Add($"frames: QSSTV {gRows.Length}, ours {ours.Count}");
        for (int f = 0; f < Math.Min(ours.Count, gRows.Length); f++)
        {
            RxGoldenRun.FrameRecord o = ours[f];
            string[] g = gRows[f];
            int b = I(g[1]);
            Ex(s, f, "stripe", b, o.Stripe);
            Ex(s, f, "transmission_frame_buffer_wptr", I(g[2]), o.WptrBefore);
            Ex(s, f, "symbol_counter", I(g[3]), o.SymbolCounter);
            Ex(s, f, "rsbufwidx", I(g[4]), o.RsBufWidx);
            Ex(s, f, "symbufwidx", I(g[5]), o.SymBufWidx);
            Ex(s, f, "input_samples_buffer_request", I(g[6]), o.Request);
            Ex(s, f, "iterationCounter", I(g[7]), o.IterationCounter);
            for (int z = 0; z < 6; z++)
                s.Tracking(f, $"Zi{z}", F(g[8 + z]), o.Zi[z], z == 1 ? ZiPhaseTolerance : ZiTolerance, z == 1);
            s.Tracking(f, "delta_freq_offset", F(g[14]), o.DeltaFreqOffset, DeltaFreqOffsetTolerance, true);
            s.Tracking(f, "freqOffset", F(g[15]), o.FreqOffset, FreqOffsetToleranceHz, false);
            s.Tracking(f, "deltaFS", F(g[16]), o.DeltaFs, SampleRateOffsetTolerance, false);
            s.Tracking(f, "samplerate_offset", F(g[17]), o.SamplerateOffset, SampleRateOffsetTolerance, false);
            s.Tracking(f, "WMERFAC", F(g[18]), o.WMerFac, WMerFacToleranceDb, false);
            s.Tracking(f, "mean_energy_of_used_cells", F(g[19]), o.MeanEnergy, 0, false);
            Ex(s, f, "lFAC", I(g[20]), o.LFac);
            Ex(s, f, "mode_and_occupancy_code", I(g[21]), o.Moc);
            Ex(s, f, "mode_and_occupancy_code_last", I(g[22]), o.MocLast);
            Ex(s, f, "SNR_timeout_counter", I(g[23]), o.SnrTimeoutCounter);
            Ex(s, f, "SNR_time_out_counter", I(g[24]), o.SnrTimeOutCounter);
            Ex(s, f, "fac_not_valid_counter", I(g[25]), o.FacNotValidCounter);
            Ex(s, f, "doSynchronize", I(g[26]), o.DoSynchronize ? 1 : 0);
            Ex(s, f, "transmission_frame_buffer_data_valid", I(g[27]), o.DataValid);
            Ex(s, f, "smp_rate_conv_fft_phase_diff", I(g[28]), o.PhaseDiff);
            s.Tracking(f, "smp_rate_conv_fft_phase_offset", F(g[29]), o.PhaseOffset, 4 * ZiTolerance, false);
            Ex(s, f, "smp_rate_conv_in_out_delay", I(g[30]), o.InOutDelay);
            Ex(s, f, "K_min", I(g[31]), o.KMin);
            Ex(s, f, "K_max", I(g[32]), o.KMax);
            Ex(s, f, "spectrum_occupancy", I(g[33]), o.SpectrumOccupancy);
            RelTrack(s, f, "cells_energy", double.Parse(g[34], CultureInfo.InvariantCulture), o.CellsEnergy);
            RelTrack(s, f, "h_energy", double.Parse(g[35], CultureInfo.InvariantCulture), o.HEnergy);
            int lFac = I(g[20]);
            for (int i = 0; i < 2 * lFac; i++)
            {
                double d = Math.Abs((double)goldenFac[facPos + i] - o.FacCells[i]);
                facWorst = Math.Max(facWorst, d);
                if (!(d <= CellTolerance))
                {
                    s.Failures.Add($"frame {f} FAC cell {i / 2}: QSSTV {goldenFac[facPos + i]:R}, ours {o.FacCells[i]:R}");
                    break;
                }
            }

            facPos += 2 * lFac;
            if (o.DataValid == 1)
                valid++;
            compared++;
        }

        output.WriteLine($"{caseId}: frames {compared} (data-valid {valid}) to stripe {last}, FAC cell max|Δ| {facWorst:E2}; {s}");
        output.WriteLine($"near-tie frames (margin <= {RxGoldenRun.TieMargin:E0}) in range: {run.NearTieFrames}; forced runner-up at tracker calls [{string.Join(",", run.ForcedCalls)}]");
        if (run.TieCutReason is not null)
            output.WriteLine($"tie rule: {run.TieCutReason}");
        Assert.True(s.Failures.Count == 0, $"{caseId}: {string.Join("; ", s.Failures.Take(8))}");
        Assert.True(valid > 5, $"{caseId}: only {valid} data-valid frames");
        if (last < run.Stripes.Count - 1)
            AssertKeepsDecodingAfter(caseId, run, last, output);
    }

    /// <summary>After the compare range ends early (knife edge, or a tie that forcing did not resolve) our receiver
    /// must keep equalizing: at least as many data-valid frames as QSSTV had after that stripe.</summary>
    internal static void AssertKeepsDecodingAfter(string caseId, RxGoldenRun.Result run, int last, ITestOutputHelper output)
    {
        string[][] g = RxGoldenRun.Csv(Path.Combine("rx_cells", caseId), "rx_cells.csv.gz");
        int qsstv = g.Count(r => I(r[1]) > last && I(r[27]) == 1);
        int ours = run.Frames.Count(f => f.Stripe > last && f.DataValid == 1);
        output.WriteLine($"after stripe {last}: data-valid frames QSSTV {qsstv}, ours {ours}");
        Assert.True(ours >= qsstv, $"{caseId}: after stripe {last} only {ours} data-valid frames, QSSTV {qsstv}");
    }

    /// <summary>Pins the tie picture: the near-tie count per case, and that a divergence happens only where forcing
    /// the competing bin at that one symbol makes the rest of the run match QSSTV (no unresolved cut).</summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void PeakSearchTies_AsMeasured(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        Assert.Null(run.TieCutReason);
        Assert.Equal(ForcedTieCalls.GetValueOrDefault(caseId, []), run.ForcedCalls);
        Assert.Equal(NearTieFrames[caseId], run.NearTieFrames);
        Assert.Equal(RxGoldenRun.PlannedLastStripe(caseId, run.Stripes.Count), run.LastStripe);
    }

    [Theory]
    [MemberData(nameof(FullCellCases))]
    public void CellsAndChannelEstimate_MatchQsstv(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        string dir = Path.Combine("rx_cells", caseId);
        float[] gCells = MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, "cells.f32.gz")).ToArray();
        float[] gH = MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, "h.f32.gz")).ToArray();
        int pos = 0;
        double cellWorst = 0, hWorstRel = 0;
        var failures = new List<string>();
        int expected = 0;
        string[][] rows = RxGoldenRun.Csv(dir, "rx_cells.csv.gz");
        foreach (string[] r in rows.Where(r => I(r[1]) <= run.LastStripe))
            expected += 2 * DrmRxTables.SymbolsPerFrame[ModeOf(caseId)] * (I(r[32]) - I(r[31]) + 1);
        foreach (RxGoldenRun.FrameRecord o in run.Frames.Where(f => f.Stripe <= run.LastStripe))
        {
            float[] c = o.Cells!, h = o.H!;
            if (pos + c.Length > gCells.Length)
            {
                failures.Add($"ours has more cells than QSSTV at stripe {o.Stripe}");
                break;
            }

            double hMax = 0;
            for (int i = 0; i < h.Length; i++)
                hMax = Math.Max(hMax, Math.Abs(gH[pos + i]));
            for (int i = 0; i < c.Length; i++)
            {
                double dc = Math.Abs((double)gCells[pos + i] - c[i]);
                double dh = Math.Abs((double)gH[pos + i] - h[i]) / hMax;
                cellWorst = Math.Max(cellWorst, dc);
                hWorstRel = Math.Max(hWorstRel, dh);
                if (!(dc <= CellTolerance) || !(dh <= HRelTolerance))
                {
                    failures.Add($"stripe {o.Stripe} value {i}: cell Δ {dc:E2}, H rel Δ {dh:E2}");
                    break;
                }
            }

            pos += c.Length;
        }

        output.WriteLine($"{caseId}: {run.Frames.Count} frames, {pos / 2} cells; cell max|Δ| {cellWorst:E2} (tol {CellTolerance:E0}), H max rel|Δ| {hWorstRel:E2} (tol {HRelTolerance:E0})");
        Assert.Empty(failures);
        Assert.Equal(expected, pos);
    }

    /// <summary>The Wiener tables after the first frame: integer pilot lists exact, and the W weights from our LU
    /// bit-equal to QSSTV's (Numerical Recipes ludcmp/lubksb), for each mode × occupancy.</summary>
    [Theory]
    [MemberData(nameof(WienerCases))]
    public void WienerTables_MatchQsstvBitExact(string caseId)
    {
        string dir = Path.Combine("rx_cells", caseId);
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFixtures.GoldenDir, dir, "w.json")));
        JsonElement j = doc.RootElement;
        float[] w = MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, "w.f32.gz")).ToArray();
        int stripe = j.GetProperty("stripe").GetInt32();

        var estimator = new DrmChannelEstimator();
        var demod = new DrmDemodulator(estimator);
        double[] stripes = RxAcquisitionGoldenTests.FrontEnd(RxAcquisitionGoldenTests.LoadInput(caseId, out _));
        for (int b = 0; b <= stripe; b++)
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
        Assert.Equal(1, estimator.FramesProcessed);

        DrmChannelEstimator.WienerTableView t = estimator.Tables;
        DrmDemodState st = demod.State;
        Assert.Equal(j.GetProperty("K_min").GetInt32(), estimator.KMin);
        Assert.Equal(j.GetProperty("K_max").GetInt32(), estimator.KMax);
        Assert.Equal(j.GetProperty("y").GetInt32(), t.Y);
        Assert.Equal(j.GetProperty("gain_ref_cells_per_frame").GetInt32(), t.GainRefCellsPerFrame);
        Assert.Equal(j.GetProperty("gain_ref_cells_per_y_symbols").GetInt32(), t.GainRefCellsPerYSymbols);
        Assert.Equal(j.GetProperty("gain_ref_cells_k").Ints(), t.GainRefCellsK.ToArray());
        Assert.Equal(j.GetProperty("gain_ref_cells_theta_1024").Ints(), t.GainRefCellsTheta1024.ToArray());
        Assert.Equal(j.GetProperty("gain_ref_cells_a").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray(), t.GainRefCellsA.ToArray());
        Assert.Equal(j.GetProperty("FAC_cells_k").Ints(), st.FacCellsK.AsSpan(0, st.LFac).ToArray());

        int lH = estimator.KMax - estimator.KMin + 1, pos = 0, values = 0, differing = 0;
        JsonElement[] perNnn = j.GetProperty("per_nnn").EnumerateArray().ToArray();
        for (int n = 0; n < t.Y; n++)
        {
            int np = t.CntTrCells(n), nnp = t.CntNextPilotCells(n);
            Assert.Equal(j.GetProperty("cnt_tr_cells")[n].GetInt32(), np);
            Assert.Equal(j.GetProperty("cnt_next_pilot_cells")[n].GetInt32(), nnp);
            Assert.Equal(perNnn[n].GetProperty("gain_ref_cells_subset_index").Ints(), t.SubsetIndex(n).ToArray());
            Assert.Equal(perNnn[n].GetProperty("training_cells_k").Ints(), t.TrainingCellsK(n).ToArray());
            Assert.Equal(perNnn[n].GetProperty("next_pilot_cells_k").Ints(), t.NextPilotCellsK(n).ToArray());
            for (int r = 0; r < np; r++)
            {
                for (int k = 0; k < lH; k++, values++)
                {
                    if (BitConverter.SingleToInt32Bits(w[pos++]) != BitConverter.SingleToInt32Bits(t.WSymbol(n, r, k)))
                        differing++;
                }
            }

            for (int r = 0; r < np; r++)
            {
                for (int k = 0; k < nnp; k++, values++)
                {
                    if (BitConverter.SingleToInt32Bits(w[pos++]) != BitConverter.SingleToInt32Bits(t.WPilots(n, r, k)))
                        differing++;
                }
            }
        }

        output.WriteLine($"{caseId}: {values} Wiener weights, {differing} differ from QSSTV's NR LU");
        Assert.Equal(w.Length, pos);
        Assert.Equal(0, differing);
    }

    private static void Ex(RxAcquisitionGoldenTests.AcqStats s, int frame, string field, int golden, int ours)
    {
        if (golden != ours)
            s.Failures.Add($"frame {frame} {field}: QSSTV {golden}, ours {ours}");
    }

    private static void RelTrack(RxAcquisitionGoldenTests.AcqStats s, int frame, string field, double golden, double ours)
    {
        double d = Math.Abs(golden - ours) / Math.Max(Math.Abs(golden), 1e-30);
        if (!(d <= EnergyRelTolerance))
            s.Failures.Add($"frame {frame} {field}: QSSTV {golden:R}, ours {ours:R}");
        else
            s.MaxTrackingDelta[field] = Math.Max(s.MaxTrackingDelta.GetValueOrDefault(field), d);
    }

    private static int ModeOf(string caseId) => caseId[0] switch { 'A' => 0, 'B' => 1, _ => 2 };

    private static int I(string v) => int.Parse(v, CultureInfo.InvariantCulture);

    private static float F(string v) => float.Parse(v, CultureInfo.InvariantCulture);
}
