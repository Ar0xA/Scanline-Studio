using System.Text.Json;
using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>Compares the ported TableMLC.h / TableFAC.h data with the arrays as compiled into
/// qsstv-drm-harness (tables/mlc.json).</summary>
public class MlcTablesTests
{
    [Fact]
    public void AllTables_MatchQsstvExactly()
    {
        using JsonDocument doc = GoldenFixtures.LoadTable("mlc.json");
        JsonElement g = doc.RootElement;

        Assert.Equal(g.GetProperty("byGeneratorMatrix").Ints(), MlcTables.GeneratorMatrix.ToArray().Select(b => (int)b).ToArray());
        Assert.Equal(g.GetProperty("iPuncturingPatterns").Ints(), MlcTables.PuncturingPatterns.ToArray());
        Assert.Equal(MlcTables.NumPuncturingPatterns * MlcTables.PuncturingPatternRowLength, MlcTables.PuncturingPatterns.Length);
        Assert.Equal(g.GetProperty("iPunctPatTailbits").Ints(), MlcTables.TailBitPatterns.ToArray());
        Assert.Equal(MlcTables.NumTailBitPatterns * MlcTables.TailBitPatternLength, MlcTables.TailBitPatterns.Length);
        Same(g.GetProperty("iCodRateCombMSC4SM").GetInt32(), MlcTables.CodeRateCombMsc4Sm);
        Same(g.GetProperty("iCodRateCombFDC4SM").GetInt32(), MlcTables.CodeRateCombFdc4Sm);
        Assert.Equal(g.GetProperty("iCodRateCombMSC16SM").Ints(), MlcTables.CodeRateCombMsc16Sm.ToArray());
        Assert.Equal(g.GetProperty("iCodRateCombMSC64SM").Ints(), MlcTables.CodeRateCombMsc64Sm.ToArray());
        Assert.Equal(g.GetProperty("iInterlSequ4SM").Ints(), MlcTables.InterleaverSequence4Sm.ToArray());
        Assert.Equal(g.GetProperty("iInterlSequ16SM").Ints(), MlcTables.InterleaverSequence16Sm.ToArray());
        Assert.Equal(g.GetProperty("iInterlSequ64SM").Ints(), MlcTables.InterleaverSequence64Sm.ToArray());
        Same(g.GetProperty("MC_CONSTRAINT_LENGTH").GetInt32(), MlcTables.ConstraintLength);
        Same(g.GetProperty("NUM_FAC_BITS_PER_BLOCK").GetInt32(), FacTables.NumFacBitsPerBlock);
    }

    // Ours are constants; a helper keeps QSSTV's value in the expected slot.
    private static void Same(int qsstv, int ours) => Assert.Equal(qsstv, ours);

    [Fact]
    public void PuncturingRows_OnesCountMatchesPatterns()
    {
        // Column b is the number of "1"s across the a groups (TableMLC.h:75).
        ReadOnlySpan<int> ones = [0, 4, 3, 2, 1, 2];
        for (int row = 0; row < MlcTables.NumPuncturingPatterns; row++)
        {
            ReadOnlySpan<int> r = MlcTables.PuncturingPatterns.Slice(row * MlcTables.PuncturingPatternRowLength, MlcTables.PuncturingPatternRowLength);
            int sum = 0;
            for (int gIdx = 0; gIdx < r[0]; gIdx++)
                sum += ones[r[2 + gIdx]];
            Assert.Equal(r[1], sum);
        }
    }
}
