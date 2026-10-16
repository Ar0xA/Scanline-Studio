using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Tx;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P3 TX channel side against every QSSTV golden case. Exact: dispersal, conv-coded and
/// bit-interleaved bits. |Δ| ≤ 1e-12: QAM cells, symbol-interleaved cells, cell grid. ±1 LSB: the int16
/// output (full stream for the 6 full cases, head 3 / tail 6 frames for the others that store them), with at
/// most 0.1% of samples off by one, and the frame count everywhere. The stream SHA-256 is informative only.</summary>
public class TxChannelGoldenTests
{
    private const double CellTolerance = 1e-12;

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string id in GoldenFixtures.CaseIds())
            data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChannelSide_MatchesQsstv(string caseId)
    {
        CaseResult r = RunCase(caseId);
        Assert.True(r.BitStagesExact, $"{caseId}: {r.Failure}");
        Assert.True(r.MaxQamDelta <= CellTolerance, $"{caseId}: QAM Δ {r.MaxQamDelta}");
        Assert.True(r.MaxSymbolInterleavedDelta <= CellTolerance, $"{caseId}: symbol interleaver Δ {r.MaxSymbolInterleavedDelta}");
        Assert.True(r.MaxCellGridDelta <= CellTolerance, $"{caseId}: cell grid Δ {r.MaxCellGridDelta}");
        Assert.True(r.CellGridSymbolsCompared >= r.ExpectedFrames * r.SymbolsPerFrame, $"{caseId}: only {r.CellGridSymbolsCompared} cell-grid symbols compared");
        Assert.Equal(r.ExpectedFrames, r.ActualFrames);
        Assert.True(r.MaxSampleDelta <= 1, $"{caseId}: int16 Δ {r.MaxSampleDelta}");

        // ±1 alone cannot see rounding instead of truncation (also ≤ 1); FFT noise flips at most a few samples.
        Assert.True(r.SamplesOff * 1000 <= r.SamplesCompared, $"{caseId}: {r.SamplesOff} of {r.SamplesCompared} samples off by 1");
        if (r.Storage != "sha256")
            Assert.True(r.SamplesCompared > 0);
    }

    private static CaseResult RunCase(string caseId)
    {
        using JsonDocument doc = GoldenFixtures.LoadManifest(caseId);
        JsonElement m = doc.RootElement;
        (DrmTxSettings settings, List<DrmSourceFrame> frames) = TxGoldenCase.SourceFrames(caseId, m);

        var probe = new RecordingProbe();
        var channel = new DrmTxChannel(settings, probe);
        List<short[]> pcm = channel.Modulate(frames).ToList();

        var r = new CaseResult { CaseId = caseId };
        JsonElement stages = m.GetProperty("stages");

        // Bit stages, exact, one block per MLC frame.
        byte[][] disp = GoldenFixtures.LoadStageBlocks(caseId, m, "energy_dispersed");
        byte[][] conv = GoldenFixtures.LoadStageBlocks(caseId, m, "conv_coded");
        byte[][] bil = GoldenFixtures.LoadStageBlocks(caseId, m, "bit_interleaved");
        if (disp.Length != probe.Mlc.Count)
            r.Fail($"MLC frames {probe.Mlc.Count} vs golden {disp.Length}");
        for (int k = 0; k < Math.Min(disp.Length, probe.Mlc.Count); k++)
        {
            MlcFrame f = probe.Mlc[k];
            if (!disp[k].AsSpan().SequenceEqual(f.Dispersed))
                r.Fail($"dispersal frame {k}");
            if (!conv[k].AsSpan().SequenceEqual(f.ConvCoded.SelectMany(x => x).ToArray()))
                r.Fail($"conv frame {k}");
            if (!bil[k].AsSpan().SequenceEqual(f.Interleaved.SelectMany(x => x).ToArray()))
                r.Fail($"bit interleaver frame {k}");
        }

        Complex[][] qam = LoadComplexBlocks(caseId, stages.GetProperty("qam_cells"));
        Complex[][] si = LoadComplexBlocks(caseId, stages.GetProperty("symbol_interleaved"));
        Complex[][] grid = LoadComplexBlocks(caseId, stages.GetProperty("cell_grid"));
        if (qam.Length != probe.Mlc.Count || si.Length != probe.SymbolInterleaved.Count)
            r.Fail("cell frame count");
        r.MaxQamDelta = MaxDelta(qam, probe.Mlc.Select(f => f.Cells).ToList());
        r.MaxSymbolInterleavedDelta = MaxDelta(si, probe.SymbolInterleaved);
        r.CellGridSymbolsCompared = Math.Min(grid.Length, probe.Cells.Count);
        r.CellGridGoldenExtra = grid.Length - probe.Cells.Count;
        r.MaxCellGridDelta = MaxDelta(grid, probe.Cells);

        // int16 output.
        r.SymbolsPerFrame = m.GetProperty("parameters").GetProperty("iNumSymPerFrame").GetInt32();
        JsonElement o = stages.GetProperty("out48k");
        r.ExpectedFrames = o.GetProperty("blocks").GetInt32();
        r.ActualFrames = pcm.Count;
        int frameSamples = o.GetProperty("block_size").GetInt32();
        short[] ours = pcm.SelectMany(f => f).ToArray();
        r.ShaEqual = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(ours.AsSpan()))).Equals(o.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase);
        r.Storage = o.GetProperty("storage").GetString()!.StartsWith("full", StringComparison.Ordinal) ? "full"
            : o.GetProperty("stored").ValueKind == JsonValueKind.Array ? "headtail" : "sha256";
        if (r.Storage == "full")
        {
            CompareSamples(r, ReadInt16(caseId, o.GetProperty("stored").GetString()!), ours);
        }
        else if (r.Storage == "headtail")
        {
            short[] head = ReadInt16(caseId, "out48k_head.bin.gz");
            short[] tail = ReadInt16(caseId, "out48k_tail.bin.gz");
            CompareSamples(r, head, ours.AsSpan(0, Math.Min(ours.Length, head.Length)).ToArray());
            int tailStart = (r.ActualFrames - (tail.Length / frameSamples)) * frameSamples;
            if (tailStart >= 0)
                CompareSamples(r, tail, ours.AsSpan(tailStart).ToArray());
        }

        return r;
    }

    private static void CompareSamples(CaseResult r, short[] golden, short[] ours)
    {
        if (golden.Length != ours.Length)
        {
            r.MaxSampleDelta = int.MaxValue;
            return;
        }

        for (int i = 0; i < golden.Length; i++)
        {
            int d = Math.Abs(golden[i] - ours[i]);
            if (d > 0)
                r.SamplesOff++;
            r.MaxSampleDelta = Math.Max(r.MaxSampleDelta, d);
        }

        r.SamplesCompared += golden.Length;
    }

    private static double MaxDelta(Complex[][] golden, IReadOnlyList<Complex[]> ours)
    {
        double max = 0;
        for (int k = 0; k < Math.Min(golden.Length, ours.Count); k++)
        {
            if (golden[k].Length != ours[k].Length)
                return double.PositiveInfinity;
            for (int i = 0; i < golden[k].Length; i++)
            {
                max = Math.Max(max, Math.Abs(golden[k][i].Real - ours[k][i].Real));
                max = Math.Max(max, Math.Abs(golden[k][i].Imaginary - ours[k][i].Imaginary));
            }
        }

        return max;
    }

    private static Complex[][] LoadComplexBlocks(string caseId, JsonElement stage)
    {
        byte[] raw = GoldenFixtures.ReadStored(caseId, stage.GetProperty("stored").GetString()!);
        Complex[] all = MemoryMarshal.Cast<byte, Complex>(raw).ToArray();
        int size = stage.GetProperty("block_size").GetInt32();
        return all.Chunk(size).ToArray();
    }

    private static short[] ReadInt16(string caseId, string stored) =>
        MemoryMarshal.Cast<byte, short>(GoldenFixtures.ReadStored(caseId, stored)).ToArray();

    private sealed class CaseResult
    {
        public string CaseId { get; init; } = "";
        public bool BitStagesExact { get; private set; } = true;
        public string Failure { get; private set; } = "";
        public double MaxQamDelta { get; set; }
        public double MaxSymbolInterleavedDelta { get; set; }
        public double MaxCellGridDelta { get; set; }
        public int CellGridSymbolsCompared { get; set; }
        public int CellGridGoldenExtra { get; set; }
        public int SymbolsPerFrame { get; set; }
        public int ExpectedFrames { get; set; }
        public int ActualFrames { get; set; }
        public string Storage { get; set; } = "";
        public bool ShaEqual { get; set; }
        public long SamplesCompared { get; set; }
        public long SamplesOff { get; set; }
        public int MaxSampleDelta { get; set; }

        public void Fail(string what)
        {
            BitStagesExact = false;
            if (Failure.Length == 0)
                Failure = what;
        }
    }

    private sealed class RecordingProbe : IDrmTxChannelProbe
    {
        public List<MlcFrame> Mlc { get; } = [];
        public List<Complex[]> SymbolInterleaved { get; } = [];
        public List<Complex[]> Cells { get; } = [];

        public void OnMscFrame(MlcFrame frame) => Mlc.Add(frame);

        public void OnSymbolInterleaved(ReadOnlySpan<Complex> cells) => SymbolInterleaved.Add(cells.ToArray());

        public void OnCellSymbol(ReadOnlySpan<Complex> carriers) => Cells.Add(carriers.ToArray());
    }
}

/// <summary>Builds a golden case's source frames with the P2 source side.</summary>
internal static class TxGoldenCase
{
    public static (DrmTxSettings Settings, List<DrmSourceFrame> Frames) SourceFrames(string caseId, JsonElement m)
    {
        JsonElement cfg = m.GetProperty("config");
        JsonElement p = m.GetProperty("drmTxParams");
        var qp = new QsstvDrmTxParams(
            p.GetProperty("robMode").GetInt32(),
            p.GetProperty("bandwith").GetInt32(),
            p.GetProperty("qam").GetInt32(),
            p.GetProperty("protection").GetInt32(),
            p.GetProperty("interleaver").GetInt32(),
            p.GetProperty("reedSolomon").GetInt32());
        DrmTxSettings settings = qp.ToSettings(cfg.GetProperty("fac_callsign_sent").GetString()!);
        byte[] payload = GoldenFixtures.ReadStored(caseId, m.GetProperty("stages").GetProperty("payload").GetProperty("stored").GetString()!);

        DrmTxObject obj;
        if (cfg.GetProperty("bsr").GetBoolean())
        {
            obj = DrmTxObject.ForBsr(payload);
        }
        else
        {
            var naming = new DrmTxNaming(new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));
            obj = DrmTxObject.ForFile(payload, naming.GetTxFileName(cfg.GetProperty("name").GetString()!, binary: true), settings.ReedSolomon);
        }

        var gen = new DrmSourceFrameGenerator(obj, settings, new BsrTransportIdCounter());
        return (settings, gen.Frames().ToList());
    }
}
