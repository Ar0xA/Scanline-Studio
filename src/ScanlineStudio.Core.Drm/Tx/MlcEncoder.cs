using System.Numerics;
using ScanlineStudio.Core.Drm.Tables;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>One frame through the MLC: every intermediate stage, for golden comparison.</summary>
/// <param name="Dispersed">The input after energy dispersal.</param>
/// <param name="ConvCoded">Punctured coder output per level, before bit interleaving.</param>
/// <param name="Interleaved">Per level after the bit interleaver (a copy of ConvCoded where the level has none).</param>
/// <param name="Cells">The QAM cells.</param>
internal sealed record MlcFrame(byte[] Dispersed, byte[][] ConvCoded, byte[][] Interleaved, Complex[] Cells);

/// <summary>Multi-level coder for the MSC or the FAC, port of CMLCEncoder and CMLC::CalculateParam
/// (drmtx/common/mlc/MLC.cpp:41-492) for HamDRM: equal error protection, part A forced empty
/// (MLC.cpp:330,415), no VSPP. Energy dispersal, partitioning, convolutional coding with puncturing,
/// bit interleaving and QAM mapping. Stateless between frames.</summary>
internal sealed class MlcEncoder
{
    private const int NumFacCells = 45;

    private readonly DrmQam _qam;
    private readonly ConvolutionalEncoder[] _encoders;
    private readonly BitInterleaver?[] _interleavers;
    private readonly int _numEncBits;

    private MlcEncoder(DrmQam qam, int nMux, int[] codeRatesPartB, ReadOnlySpan<int> interleaverSequence, int numInBitsFac, DrmChannelType channelType)
    {
        _qam = qam;
        NumCells = nMux;
        _numEncBits = 2 * nMux;
        int levels = codeRatesPartB.Length;
        _encoders = new ConvolutionalEncoder[levels];
        int total = 0;
        for (int j = 0; j < levels; j++)
        {
            int bitsPartB = channelType == DrmChannelType.Fac ? numInBitsFac : LevelBits(codeRatesPartB[j], nMux);
            _encoders[j] = new ConvolutionalEncoder(channelType, nMux, 0, bitsPartB, 0, codeRatesPartB[j]);
            if (_encoders[j].NumOutBits != _numEncBits)
                throw new InvalidOperationException($"Level {j} codes {_encoders[j].NumOutBits} bits, expected {_numEncBits}.");
            total += bitsPartB;
        }

        NumInBits = total;

        // Interleaver 0 has t0 = 13, interleaver 1 t0 = 21, both over 2·N1 and 2·N2 bits (MLC.cpp:180-184).
        var shared = new[] { new BitInterleaver(0, _numEncBits, 13), new BitInterleaver(0, _numEncBits, 21) };
        _interleavers = new BitInterleaver?[levels];
        for (int j = 0; j < levels; j++)
            _interleavers[j] = interleaverSequence[j] == -1 ? null : shared[interleaverSequence[j]];
    }

    /// <summary>iNumInBits: input bits per frame (iNumDecodedBitsMSC for the MSC, 48 for the FAC).</summary>
    public int NumInBits { get; }

    /// <summary>iN_mux: output cells per frame.</summary>
    public int NumCells { get; }

    /// <summary>The number of MLC levels.</summary>
    public int Levels => _encoders.Length;

    /// <summary>CT_MSC parameters (MLC.cpp:266-487). With 4-QAM, protection is not used.</summary>
    public static MlcEncoder ForMsc(CellMappingTable table, DrmQam qam, DrmProtection protection)
    {
        ArgumentNullException.ThrowIfNull(table);
        int nMux = table.NumUsefulMscCellsPerFrame;
        int partB = protection == DrmProtection.Low ? 1 : 0;
        return qam switch
        {
            DrmQam.Qam4 => new MlcEncoder(qam, nMux, [MlcTables.CodeRateCombMsc4Sm], MlcTables.InterleaverSequence4Sm, 0, DrmChannelType.Msc),
            DrmQam.Qam16 => new MlcEncoder(qam, nMux, MlcTables.CodeRateCombMsc16Sm.Slice(partB * 3, 2).ToArray(), MlcTables.InterleaverSequence16Sm, 0, DrmChannelType.Msc),
            DrmQam.Qam64 => new MlcEncoder(qam, nMux, MlcTables.CodeRateCombMsc64Sm.Slice(partB * 4, 3).ToArray(), MlcTables.InterleaverSequence64Sm, 0, DrmChannelType.Msc),
            _ => throw new ArgumentOutOfRangeException(nameof(qam), qam, null),
        };
    }

    /// <summary>CT_FAC parameters (MLC.cpp:223-264): 4-QAM, 48 bits onto 45 cells.</summary>
    public static MlcEncoder ForFac() =>
        new(DrmQam.Qam4, NumFacCells, [MlcTables.CodeRateCombFdc4Sm], MlcTables.InterleaverSequence4Sm, FacTables.NumFacBitsPerBlock, DrmChannelType.Fac);

    /// <summary>CMLCEncoder::ProcessDataInternal for one frame. <paramref name="bits"/> must hold exactly
    /// <see cref="NumInBits"/> bits, one per byte; it is not modified.</summary>
    public MlcFrame Encode(ReadOnlySpan<byte> bits)
    {
        if (bits.Length != NumInBits)
            throw new ArgumentException($"Expected exactly {NumInBits} bits, got {bits.Length}.", nameof(bits));

        byte[] dispersed = bits.ToArray();
        EnergyDispersal.Apply(dispersed, 0);

        int levels = _encoders.Length;
        var conv = new byte[levels][];
        var interleaved = new byte[levels][];
        int offset = 0;
        for (int j = 0; j < levels; j++)
        {
            // Part A is empty, so level j takes the next iM[j][1] bits (MLC.cpp:51-80).
            ConvolutionalEncoder enc = _encoders[j];
            conv[j] = new byte[_numEncBits];
            enc.Encode(dispersed.AsSpan(offset, enc.NumInBits), conv[j]);
            offset += enc.NumInBits;
            interleaved[j] = (byte[])conv[j].Clone();
            _interleavers[j]?.Interleave(interleaved[j]);
        }

        var cells = new Complex[NumCells];
        QamMapper.Map(_qam, interleaved, cells);
        return new MlcFrame(dispersed, conv, interleaved, cells);
    }

    // M_p,2 = RX_p · floor((2·N_2 − 12) / RY_p) (MLC.cpp:303-306).
    private static int LevelBits(int codeRate, int nMux)
    {
        int rx = MlcTables.PuncturingPatterns[codeRate * MlcTables.PuncturingPatternRowLength];
        int ry = MlcTables.PuncturingPatterns[(codeRate * MlcTables.PuncturingPatternRowLength) + 1];
        return rx * (((2 * nMux) - 12) / ry);
    }
}
