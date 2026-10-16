namespace ScanlineStudio.Core.Drm.Util;

/// <summary>Reed-Solomon RS(255,k) over GF(2^8), port of Phil Karn's KA9Q coder as QSSTV ships it
/// (utils/rs.cpp, utils/rs.h): field polynomial 1+x^2+x^3+x^4+x^8, first root B0 = 1, PRIM = 1.
/// QSSTV keeps the generator polynomial in a static re-initialised by <c>init_rs(kk)</c>; here each
/// instance owns its own, so several data sizes can be in use at once. Encoder only for now.</summary>
internal sealed class KarnReedSolomon
{
    /// <summary>NN: the code word length (rs.h:41).</summary>
    public const int BlockSize = 255;

    private const int Mm = 8;
    private const int A0 = BlockSize;
    private const int B0 = 1;
    private const int Prim = 1;

    // Pp: 1+x^2+x^3+x^4+x^8 (rs.cpp:26).
    private static readonly int[] PrimitivePolynomial = [1, 0, 1, 1, 1, 0, 0, 0, 1];

    private static readonly int[] AlphaTo = new int[BlockSize + 1];
    private static readonly int[] IndexOf = new int[BlockSize + 1];

    // Generator polynomial in index form, NN-KK+1 entries (rs.cpp:52,168-191).
    private readonly int[] _generator;

    static KarnReedSolomon() => GenerateGf();

    /// <param name="dataSize">KK, the number of data bytes per block. QSSTV uses 224/192/160/128
    /// (rs.h:16-19); its generator array is sized for KK ≥ 128 (rs.cpp:52).</param>
    public KarnReedSolomon(int dataSize)
    {
        if (dataSize < 128 || dataSize >= BlockSize)
            throw new ArgumentOutOfRangeException(nameof(dataSize), dataSize, "QSSTV's Karn coder supports 128 <= k < 255.");

        DataSize = dataSize;
        _generator = GenPoly(dataSize);
    }

    /// <summary>KK.</summary>
    public int DataSize { get; }

    /// <summary>NN-KK.</summary>
    public int ParitySize => BlockSize - DataSize;

    /// <summary><c>encode_rs</c> (rs.cpp:202-227): systematic encode of <paramref name="data"/>
    /// (KK bytes) into <paramref name="parity"/> (NN-KK bytes).</summary>
    public void Encode(ReadOnlySpan<byte> data, Span<byte> parity)
    {
        int kk = DataSize;
        int nk = ParitySize;
        if (data.Length < kk)
            throw new ArgumentException($"Need {kk} data bytes.", nameof(data));
        if (parity.Length < nk)
            throw new ArgumentException($"Need {nk} parity bytes.", nameof(parity));

        Span<byte> bb = parity[..nk];
        bb.Clear();
        for (int i = kk - 1; i >= 0; i--)
        {
            int feedback = IndexOf[data[i] ^ bb[nk - 1]];
            if (feedback != A0)
            {
                for (int j = nk - 1; j > 0; j--)
                {
                    bb[j] = _generator[j] != A0
                        ? (byte)(bb[j - 1] ^ AlphaTo[Modnn(_generator[j] + feedback)])
                        : bb[j - 1];
                }

                bb[0] = (byte)AlphaTo[Modnn(_generator[0] + feedback)];
            }
            else
            {
                for (int j = nk - 1; j > 0; j--)
                    bb[j] = bb[j - 1];
                bb[0] = 0;
            }
        }
    }

    /// <summary>x % NN without a divide (rs.cpp:58-66).</summary>
    private static int Modnn(int x)
    {
        while (x >= BlockSize)
        {
            x -= BlockSize;
            x = (x >> Mm) + (x & BlockSize);
        }

        return x;
    }

    /// <summary><c>generate_gf</c> (rs.cpp:122-153).</summary>
    private static void GenerateGf()
    {
        int mask = 1;
        AlphaTo[Mm] = 0;
        for (int i = 0; i < Mm; i++)
        {
            AlphaTo[i] = mask;
            IndexOf[AlphaTo[i]] = i;
            if (PrimitivePolynomial[i] != 0)
                AlphaTo[Mm] ^= mask;
            mask <<= 1;
        }

        IndexOf[AlphaTo[Mm]] = Mm;
        mask >>= 1;
        for (int i = Mm + 1; i < BlockSize; i++)
        {
            AlphaTo[i] = AlphaTo[i - 1] >= mask
                ? AlphaTo[Mm] ^ ((AlphaTo[i - 1] ^ mask) << 1)
                : AlphaTo[i - 1] << 1;
            IndexOf[AlphaTo[i]] = i;
        }

        IndexOf[0] = A0;
        AlphaTo[BlockSize] = 0;
    }

    /// <summary><c>gen_poly</c> (rs.cpp:168-191).</summary>
    private static int[] GenPoly(int kk)
    {
        int nk = BlockSize - kk;
        var gg = new int[nk + 1];
        gg[0] = 1;
        for (int i = 0; i < nk; i++)
        {
            gg[i + 1] = 1;
            for (int j = i; j > 0; j--)
            {
                gg[j] = gg[j] != 0
                    ? gg[j - 1] ^ AlphaTo[Modnn(IndexOf[gg[j]] + ((B0 + i) * Prim))]
                    : gg[j - 1];
            }

            gg[0] = AlphaTo[Modnn(IndexOf[gg[0]] + ((B0 + i) * Prim))];
        }

        for (int i = 0; i <= nk; i++)
            gg[i] = IndexOf[gg[i]];
        return gg;
    }
}
