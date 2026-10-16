using System.Numerics;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>Compares the ported cell map against QSSTV's runtime-built CCellMappingTable, dumped by
/// qsstv-drm-harness `tables`. Cell flags, counts and geometry must match exactly; pilot values
/// within 1e-12 absolute per component.</summary>
public class CellMappingTableTests
{
    private const double PilotTolerance = 1e-12;

    public static TheoryData<string> Cases => new() { "A0", "A1", "B0", "B1", "E0", "E1" };

    private static CellMappingTable Build(string tag) => new(
        tag[0] switch { 'A' => RobustnessMode.A, 'B' => RobustnessMode.B, _ => RobustnessMode.E },
        tag[1] == '0' ? SpectrumOccupancy.So0 : SpectrumOccupancy.So1);

    [Theory]
    [MemberData(nameof(Cases))]
    public void Scalars_MatchQsstv(string tag)
    {
        using JsonDocument doc = GoldenFixtures.LoadTable($"cellmap_{tag}.json");
        JsonElement g = doc.RootElement;
        CellMappingTable t = Build(tag);

        Assert.Equal(g.GetProperty("iNumSymbolsPerSuperframe").GetInt32(), t.NumSymbolsPerSuperframe);
        Assert.Equal(g.GetProperty("iNumSymPerFrame").GetInt32(), t.NumSymbolsPerFrame);
        Assert.Equal(g.GetProperty("iNumCarrier").GetInt32(), t.NumCarriers);
        Assert.Equal(g.GetProperty("iScatPilTimeInt").GetInt32(), t.ScatPilotTimeInterval);
        Assert.Equal(g.GetProperty("iScatPilFreqInt").GetInt32(), t.ScatPilotFreqInterval);
        Assert.Equal(g.GetProperty("iMaxNumMSCSym").GetInt32(), t.MaxNumMscPerSymbol);
        Assert.Equal(g.GetProperty("iFFTSizeN").GetInt32(), t.FftSize);
        Assert.Equal(g.GetProperty("iCarrierKmin").GetInt32(), t.CarrierKmin);
        Assert.Equal(g.GetProperty("iCarrierKmax").GetInt32(), t.CarrierKmax);
        Assert.Equal(g.GetProperty("iIndexDCFreq").GetInt32(), t.IndexDcFreq);
        Assert.Equal(g.GetProperty("iShiftedKmin").GetInt32(), t.ShiftedKmin);
        Assert.Equal(g.GetProperty("iShiftedKmax").GetInt32(), t.ShiftedKmax);
        Assert.Equal(g.GetProperty("RatioTgTu").Ints(), new[] { t.GuardRatioEnum, t.GuardRatioDenom });
        Assert.Equal(g.GetProperty("iGuardSize").GetInt32(), t.GuardSize);
        Assert.Equal(g.GetProperty("iSymbolBlockSize").GetInt32(), t.SymbolBlockSize);
        Assert.Equal(g.GetProperty("iNumIntpFreqPil").GetInt32(), t.NumInterpolatedFreqPilots);
        Assert.Equal(g.GetProperty("iNumUsefMSCCellsPerFrame").GetInt32(), t.NumUsefulMscCellsPerFrame);
        Assert.Equal(g.GetProperty("rAvPowPerSymbol").GetDouble(), t.AveragePowerPerSymbol);
        Assert.Equal(g.GetProperty("rAvPilPowPerSym").GetDouble(), t.AveragePilotPowerPerSymbol);
        Assert.Equal(g.GetProperty("veciNumMSCSym").Ints(), t.NumMscPerSymbol.ToArray());
        Assert.Equal(g.GetProperty("veciNumFACSym").Ints(), t.NumFacPerSymbol.ToArray());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CellFlags_MatchQsstvExactly(string tag)
    {
        using JsonDocument doc = GoldenFixtures.LoadTable($"cellmap_{tag}.json");
        CellMappingTable t = Build(tag);
        JsonElement rows = doc.RootElement.GetProperty("matiMapTab");
        Assert.Equal(t.NumSymbolsPerSuperframe, rows.GetArrayLength());
        int sym = 0;
        foreach (JsonElement row in rows.EnumerateArray())
        {
            int[] expected = row.Ints();
            Assert.Equal(t.NumCarriers, expected.Length);
            for (int c = 0; c < expected.Length; c++)
                Assert.True(expected[c] == (int)t.Cell(sym, c), $"{tag} sym {sym} car {c}: QSSTV {expected[c]}, ours {(int)t.Cell(sym, c)}");
            sym++;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PilotValues_MatchQsstvWithin1e12(string tag)
    {
        using JsonDocument doc = GoldenFixtures.LoadTable($"cellmap_{tag}.json");
        CellMappingTable t = Build(tag);
        var expected = new Dictionary<(int, int), Complex>();
        foreach (JsonElement p in doc.RootElement.GetProperty("pilots").EnumerateArray())
            expected[(p[0].GetInt32(), p[1].GetInt32())] = new Complex(p[2].GetDouble(), p[3].GetDouble());

        int nonZero = 0;
        for (int s = 0; s < t.NumSymbolsPerSuperframe; s++)
        {
            for (int c = 0; c < t.NumCarriers; c++)
            {
                Complex ours = t.Pilot(s, c);
                Complex want = expected.TryGetValue((s, c), out Complex v) ? v : Complex.Zero;
                if (ours != Complex.Zero)
                    nonZero++;
                Assert.True(Math.Abs(ours.Real - want.Real) <= PilotTolerance && Math.Abs(ours.Imaginary - want.Imaginary) <= PilotTolerance,
                    $"{tag} sym {s} car {c}: QSSTV {want}, ours {ours}");
            }
        }

        Assert.Equal(expected.Count, nonZero);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryFrame_Has45FacCells(string tag)
    {
        CellMappingTable t = Build(tag);
        for (int frame = 0; frame < 3; frame++)
        {
            int sum = 0;
            for (int s = 0; s < t.NumSymbolsPerFrame; s++)
                sum += t.NumFacPerSymbol[(frame * t.NumSymbolsPerFrame) + s];
            Assert.Equal(CarrierTables.NumFacCells, sum);
        }
    }
}
