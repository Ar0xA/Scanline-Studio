using System.Numerics;
using ScanlineStudio.Core.Drm.Tables;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P3 channel pieces in isolation. The golden comparison is in <see cref="TxChannelGoldenTests"/>.</summary>
public class TxChannelUnitTests
{
    [Fact]
    public void BlockInterleaver_PowerOfTwoSize_UsesQsstvDoubledModulus()
    {
        // N = 8 gives s = 16, not the standard's 8 (BlockInterleaver.cpp:49-55); worked by hand.
        Assert.Equal([0, 3, 2, 4, 7, 6, 1, 5], BlockInterleaverTable.Make(8, 5));
    }

    [Theory]
    [InlineData(647, 5)]
    [InlineData(1294, 13)]
    [InlineData(1294, 21)]
    [InlineData(90, 21)]
    [InlineData(1024, 5)]
    public void BlockInterleaver_IsAPermutation(int n, int t0)
    {
        int[] table = BlockInterleaverTable.Make(n, t0);
        Assert.Equal(Enumerable.Range(0, n), table.Order());
    }

    [Fact]
    public void ConvEncoder_Fac_Codes48BitsInto90()
    {
        var enc = new ConvolutionalEncoder(DrmChannelType.Fac, 45, 0, 48, 0, MlcTables.CodeRateCombFdc4Sm);
        Assert.Equal(90, enc.NumOutBits);
        var output = new byte[90];
        Assert.Equal(90, enc.Encode(new byte[48], output));
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ConvEncoder_ImpulseGivesTheGeneratorTaps()
    {
        // Rate 1/4 (row 0, pattern 1111) everywhere: an impulse reads the 4 generators bit by bit, MSB = newest.
        var enc = new ConvolutionalEncoder(DrmChannelType.Fac, 10, 0, 1, 0, 0);
        var output = new byte[enc.NumOutBits];
        enc.Encode([1], output);
        for (int step = 0; step < 7; step++)
        {
            for (int g = 0; g < 4; g++)
                Assert.Equal((MlcTables.GeneratorMatrix[g] >> step) & 1, output[(4 * step) + g]);
        }
    }

    [Fact]
    public void Msc4Qam_IgnoresProtection()
    {
        var table = new CellMappingTable(RobustnessMode.B, SpectrumOccupancy.So1);
        MlcEncoder high = MlcEncoder.ForMsc(table, DrmQam.Qam4, DrmProtection.High);
        MlcEncoder low = MlcEncoder.ForMsc(table, DrmQam.Qam4, DrmProtection.Low);
        byte[] bits = Enumerable.Range(0, high.NumInBits).Select(i => (byte)((i * 7 / 3) & 1)).ToArray();
        Assert.Equal(high.NumInBits, low.NumInBits);
        Assert.Equal(high.Encode(bits).Cells, low.Encode(bits).Cells);
    }

    [Fact]
    public void Msc16Qam_ProtectionChangesTheCode()
    {
        var table = new CellMappingTable(RobustnessMode.B, SpectrumOccupancy.So1);
        Assert.NotEqual(
            MlcEncoder.ForMsc(table, DrmQam.Qam16, DrmProtection.High).NumInBits,
            MlcEncoder.ForMsc(table, DrmQam.Qam16, DrmProtection.Low).NumInBits);
    }

    [Fact]
    public void MlcEncoder_RejectsWrongLength()
    {
        MlcEncoder fac = MlcEncoder.ForFac();
        Assert.Throws<ArgumentException>(() => fac.Encode(new byte[49]));
    }

    [Fact]
    public void SymbolInterleaver_Short_HasNoDelay()
    {
        var si = new SymbolInterleaver(647, DrmInterleave.Short);
        Assert.Equal(1, si.Depth);
        Complex[] input = Enumerable.Range(1, 647).Select(i => new Complex(i, -i)).ToArray();
        var output = new Complex[647];
        si.Process(input, output);
        int[] table = BlockInterleaverTable.Make(647, 5);
        for (int i = 0; i < 647; i++)
            Assert.Equal(input[table[i]], output[i]);
    }

    [Fact]
    public void SymbolInterleaver_Long_ReadsCellIFromFrameIMod5Ago()
    {
        const int n = 647;
        var si = new SymbolInterleaver(n, DrmInterleave.Long);
        Assert.Equal(5, si.Depth);
        int[] table = BlockInterleaverTable.Make(n, 5);
        var output = new Complex[n];
        for (int frame = 0; frame < 8; frame++)
        {
            Complex[] input = Enumerable.Range(0, n).Select(i => new Complex(frame + 1, i)).ToArray();
            si.Process(input, output);
            for (int i = 0; i < n; i++)
            {
                int source = frame - (i % 5);
                Complex expected = source < 0 ? Complex.Zero : new Complex(source + 1, table[i]);
                Assert.Equal(expected, output[i]);
            }
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    public void CellTable_HasNoMscDummyCells(int mode, int so)
    {
        // The FIFO mapping and the stop-frame count rely on each frame taking exactly N_mux MSC cells (CellMappingTable.cpp:491-500).
        var table = new CellMappingTable((RobustnessMode)mode, (SpectrumOccupancy)so);
        int flagged = 0;
        for (int sym = 0; sym < table.NumSymbolsPerSuperframe; sym++)
        {
            for (int car = 0; car < table.NumCarriers; car++)
            {
                if ((table.Cell(sym, car) & CellFlags.Msc) != 0)
                    flagged++;
            }
        }

        int perSuperframe = DrmGlobal.NumFramesInSuperframe * table.NumUsefulMscCellsPerFrame;
        Assert.Equal(perSuperframe, flagged);
        int counted = 0;
        foreach (int c in table.NumMscPerSymbol)
            counted += c;
        Assert.Equal(perSuperframe, counted);
    }

    [Fact]
    public void CellMapper_WaitsForFacCells()
    {
        var table = new CellMappingTable(RobustnessMode.B, SpectrumOccupancy.So1);
        var mapper = new OfdmCellMapper(table, DrmQam.Qam4);
        mapper.EnqueueMsc(new Complex[table.NumUsefulMscCellsPerFrame * 2]);
        var carriers = new Complex[table.NumCarriers];
        int mapped = 0;
        while (mapper.TryMapSymbol(carriers))
            mapped++;

        // Stops at the first symbol that carries FAC cells.
        int firstFac = 0;
        while (table.NumFacPerSymbol[firstFac] == 0)
            firstFac++;
        Assert.Equal(firstFac, mapped);
    }

    [Fact]
    public void OfdmModulator_CarriesThePhaseAcrossSymbols()
    {
        var table = new CellMappingTable(RobustnessMode.B, SpectrumOccupancy.So1);
        var mod = new OfdmModulator(table);
        Complex[] cells = Enumerable.Range(0, table.NumCarriers).Select(i => new Complex(Math.Cos(i), Math.Sin(2 * i))).ToArray();
        var first = new Complex[table.SymbolBlockSize];
        var second = new Complex[table.SymbolBlockSize];
        mod.Modulate(cells, first);
        mod.Modulate(cells, second);

        // Same baseband symbol, so the second is the first rotated by −5650 Hz over one symbol length.
        Complex rot = Complex.FromPolarCoordinates(1, -2 * Math.PI * 5650.0 * table.SymbolBlockSize / 48000);
        for (int i = 0; i < first.Length; i++)
            Assert.True(Complex.Abs((first[i] * rot) - second[i]) < 1e-9, $"sample {i}");
    }

    [Fact]
    public void OfdmModulator_PutsCarrierKAt350HzPlusKSpacing()
    {
        var table = new CellMappingTable(RobustnessMode.B, SpectrumOccupancy.So1);
        var mod = new OfdmModulator(table);
        var cells = new Complex[table.NumCarriers];
        cells[0] = Complex.One;
        double hz = 350.0 + (table.CarrierKmin * 48000.0 / table.FftSize);
        var output = new Complex[table.SymbolBlockSize];
        mod.Modulate(cells, output);
        for (int i = 1; i < output.Length; i++)
        {
            double phase = (output[i] / output[i - 1]).Phase;
            Assert.Equal(2 * Math.PI * hz / 48000, phase, 9);
        }
    }

    [Theory]
    [InlineData(1.9, 1)]
    [InlineData(-1.9, -1)]
    [InlineData(-0.5, 0)]
    [InlineData(32767.99, 32767)]
    [InlineData(32768.5, -32768)]
    [InlineData(40000.0, -25536)]
    [InlineData(-32769.5, 32767)]
    public void SampleConverter_TruncatesTowardZeroAndWrapsLikeX86(double value, short expected) =>
        Assert.Equal(expected, DrmTxSampleConverter.ToInt16(value));

    [Fact]
    public void SampleConverter_FloatIsInt16Over32768()
    {
        float[] f = DrmTxChannel.ToFloat([short.MinValue, 0, 16384, short.MaxValue]);
        Assert.Equal([-1f, 0f, 0.5f, 32767f / 32768f], f);
    }

    [Fact]
    public void Channel_RunsOnce()
    {
        var settings = new DrmTxSettings(RobustnessMode.B, SpectrumOccupancy.So1, DrmQam.Qam4, DrmProtection.High, DrmInterleave.Long, DrmReedSolomon.None, "T");
        var channel = new DrmTxChannel(settings);
        Assert.Equal(19200, channel.SamplesPerFrame);
        _ = channel.Modulate([]);
        Assert.Throws<InvalidOperationException>(() => channel.Modulate([]));
    }
}
