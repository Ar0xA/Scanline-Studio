using ScanlineStudio.Core.Drm.Tables;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>MLC channel type: the MSC, or the FAC, which keeps its part-B pattern over the tail bits.</summary>
internal enum DrmChannelType
{
    Msc,
    Fac,
}

/// <summary>DRM K=7 convolutional encoder with puncturing, the hard-decision build of CConvEncoder
/// (drmtx/common/mlc/ConvEncoder.cpp:34-236, USE_MAX_LOG_MAP is undefined, GlobalDefinitions.h:70)
/// and CChannelCode (mlc/ChannelCode.cpp). One instance per MLC level; Encode is stateless.</summary>
internal sealed class ConvolutionalEncoder
{
    private static readonly byte[] Parity = MakeParityTable();

    private readonly int[] _puncturing;

    /// <param name="n2">Cells of protection part B (iN[1]); sets the tail-bit pattern.</param>
    /// <param name="bitsPartA">iM[level][0].</param>
    /// <param name="bitsPartB">iM[level][1].</param>
    /// <param name="codeRatePartA">Puncturing pattern row for part A.</param>
    /// <param name="codeRatePartB">Puncturing pattern row for part B.</param>
    public ConvolutionalEncoder(DrmChannelType channelType, int n2, int bitsPartA, int bitsPartB, int codeRatePartA, int codeRatePartB)
    {
        NumInBits = bitsPartA + bitsPartB;
        _puncturing = MakePuncturingTable(channelType, n2, bitsPartA, bitsPartB, codeRatePartA, codeRatePartB);
        int count = 0;
        foreach (int p in _puncturing)
            count += OutputBitsFor(p);
        NumOutBits = count;
    }

    /// <summary>Input bits per frame, excluding the 6 tail bits.</summary>
    public int NumInBits { get; }

    /// <summary>Punctured output bits per frame, including the tail.</summary>
    public int NumOutBits { get; }

    /// <summary>The puncturing pattern type per input step, tail included.</summary>
    public ReadOnlySpan<int> PuncturingTable => _puncturing;

    /// <summary>CConvEncoder::Encode: encodes one bit per byte and returns the output bit count.</summary>
    public int Encode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        if (input.Length < NumInBits)
            throw new ArgumentException($"Expected {NumInBits} input bits.", nameof(input));
        if (output.Length < NumOutBits)
            throw new ArgumentException($"Expected room for {NumOutBits} output bits.", nameof(output));

        int count = 0;
        byte reg = 0;
        for (int i = 0; i < _puncturing.Length; i++)
        {
            reg <<= 1;
            if (i < NumInBits && input[i] != 0)
                reg |= 1;

            switch (_puncturing[i])
            {
                case MlcTables.PpType0001:
                    output[count++] = Convolution(reg, 0);
                    break;
                case MlcTables.PpType0101:
                    output[count++] = Convolution(reg, 0);
                    output[count++] = Convolution(reg, 2);
                    break;
                case MlcTables.PpType0011:
                    output[count++] = Convolution(reg, 0);
                    output[count++] = Convolution(reg, 1);
                    break;
                case MlcTables.PpType0111:
                    output[count++] = Convolution(reg, 0);
                    output[count++] = Convolution(reg, 1);
                    output[count++] = Convolution(reg, 2);
                    break;
                case MlcTables.PpType1111:
                    output[count++] = Convolution(reg, 0);
                    output[count++] = Convolution(reg, 1);
                    output[count++] = Convolution(reg, 2);
                    output[count++] = Convolution(reg, 3);
                    break;
            }
        }

        return count;
    }

    // ChannelCode.h:45-54: parity of the register masked by the generator polynomial.
    private static byte Convolution(byte reg, int generator) => Parity[reg & MlcTables.GeneratorMatrix[generator]];

    private static int OutputBitsFor(int pattern) => pattern switch
    {
        MlcTables.PpType0001 => 1,
        MlcTables.PpType0101 => 2,
        MlcTables.PpType0011 => 2,
        MlcTables.PpType0111 => 3,
        MlcTables.PpType1111 => 4,
        _ => 0,
    };

    /// <summary>CChannelCode::GenPuncPatTable (ChannelCode.cpp:33-172) for the standard-mapping schemes
    /// (tail parameter 2·N2 for both levels).</summary>
    internal static int[] MakePuncturingTable(DrmChannelType channelType, int n2, int bitsPartA, int bitsPartB, int codeRatePartA, int codeRatePartB)
    {
        int numOutBits = bitsPartA + bitsPartB;
        int numWithMemory = numOutBits + MlcTables.ConstraintLength - 1;
        ReadOnlySpan<int> pp = MlcTables.PuncturingPatterns;
        const int row = MlcTables.PuncturingPatternRowLength;

        // The standard-mapping schemes use 2·N2 for every level (ChannelCode.cpp:80-82); HMMIX/HMSYM are not used.
        int tailParam = 2 * n2;
        int ry = pp[(codeRatePartB * row) + 1];
        int tailPattern = tailParam - 12 - (ry * ((tailParam - 12) / ry));

        int lenA = pp[codeRatePartA * row];
        int lenB = pp[codeRatePartB * row];
        var table = new int[numWithMemory];
        int counter = 0;
        for (int i = 0; i < numWithMemory; i++)
        {
            if (i < bitsPartA)
            {
                table[i] = pp[(codeRatePartA * row) + 2 + counter];
                if (++counter == lenA)
                    counter = 0;
            }
            else if (i < numOutBits || channelType == DrmChannelType.Fac)
            {
                if (i == bitsPartA)
                    counter = 0;
                table[i] = pp[(codeRatePartB * row) + 2 + counter];
                if (++counter == lenB)
                    counter = 0;
            }
            else
            {
                if (i == numOutBits)
                    counter = 0;
                table[i] = MlcTables.TailBitPatterns[(tailPattern * MlcTables.TailBitPatternLength) + counter];
                counter++;
            }
        }

        return table;
    }

    private static byte[] MakeParityTable()
    {
        // CChannelCode::CChannelCode (ChannelCode.cpp:175-189): XOR of the low 7 bits.
        var parity = new byte[256];
        for (int j = 0; j < 256; j++)
        {
            int p = 0;
            for (int i = 0; i < MlcTables.ConstraintLength; i++)
                p ^= (j >> i) & 1;
            parity[j] = (byte)p;
        }

        return parity;
    }
}
