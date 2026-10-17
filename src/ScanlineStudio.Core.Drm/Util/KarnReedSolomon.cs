namespace ScanlineStudio.Core.Drm.Util;

/// <summary>Reed-Solomon RS(255,k) over GF(2^8), port of Phil Karn's KA9Q coder as QSSTV ships it
/// (utils/rs.cpp, utils/rs.h): field polynomial 1+x^2+x^3+x^4+x^8, first root B0 = 1, PRIM = 1.
/// QSSTV keeps the generator polynomial in a static re-initialised by <c>init_rs(kk)</c>; here each
/// instance owns its own, so several data sizes can be in use at once. Encoder (TX) and errors-and-erasures
/// decoder (RX). Instances are immutable after construction and safe to share.</summary>
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

    /// <summary><c>eras_dec_rs</c> (rs.cpp:247-454): errors-and-erasures decode of one code word, in place.
    /// <paramref name="data"/> is the NN-byte code word (data then parity, as <see cref="Encode"/> lays it out);
    /// <paramref name="erasurePositions"/> holds <paramref name="erasureCount"/> distinct erased positions (0..NN-1)
    /// and must be at least NN-KK long, because the found error locations are written back into it as QSSTV does.
    /// Returns the number of corrected symbols, or −1 when the word is uncorrectable. Like QSSTV, a failure
    /// found while applying corrections (den == 0) leaves the corrections made so far in place.</summary>
    public int DecodeErasures(Span<byte> data, Span<int> erasurePositions, int erasureCount)
    {
        int nk = ParitySize;
        if (data.Length < BlockSize)
            throw new ArgumentException($"Need {BlockSize} bytes.", nameof(data));
        if (erasureCount < 0 || erasureCount > nk || erasurePositions.Length < nk)
            throw new ArgumentOutOfRangeException(nameof(erasureCount), erasureCount, "Erasures must fit NN-KK.");

        Span<int> lambda = stackalloc int[nk + 1];
        Span<int> s = stackalloc int[nk + 1];
        Span<int> b = stackalloc int[nk + 1];
        Span<int> t = stackalloc int[nk + 1];
        Span<int> omega = stackalloc int[nk + 1];
        Span<int> root = stackalloc int[nk];
        Span<int> reg = stackalloc int[nk + 1];
        Span<int> loc = stackalloc int[nk];
        int count;

        // Syndromes: data(x) at the roots of g(x), alpha^(B0+i-1) (rs.cpp:264-283).
        for (int i = 1; i <= nk; i++)
            s[i] = data[0];
        for (int j = 1; j < BlockSize; j++)
        {
            if (data[j] == 0)
                continue;
            int tmp = IndexOf[data[j]];
            for (int i = 1; i <= nk; i++)
                s[i] ^= AlphaTo[Modnn(tmp + ((B0 + i - 1) * Prim * j))];
        }

        int synError = 0;
        for (int i = 1; i <= nk; i++)
        {
            synError |= s[i];
            s[i] = IndexOf[s[i]];
        }

        if (synError == 0)
        {
            count = 0;
            goto Finish;
        }

        lambda[1..].Clear();
        lambda[0] = 1;
        if (erasureCount > 0)
        {
            // The erasure locator polynomial (rs.cpp:295-310).
            lambda[1] = AlphaTo[Modnn(Prim * erasurePositions[0])];
            for (int i = 1; i < erasureCount; i++)
            {
                int u = Modnn(Prim * erasurePositions[i]);
                for (int j = i + 1; j > 0; j--)
                {
                    int tmp = IndexOf[lambda[j - 1]];
                    if (tmp != A0)
                        lambda[j] ^= AlphaTo[Modnn(u + tmp)];
                }
            }
        }

        for (int i = 0; i < nk + 1; i++)
            b[i] = IndexOf[lambda[i]];

        // Berlekamp-Massey (rs.cpp:318-357).
        int r = erasureCount;
        int el = erasureCount;
        while (++r <= nk)
        {
            int discrR = 0;
            for (int i = 0; i < r; i++)
            {
                if (lambda[i] != 0 && s[r - i] != A0)
                    discrR ^= AlphaTo[Modnn(IndexOf[lambda[i]] + s[r - i])];
            }

            discrR = IndexOf[discrR];
            if (discrR == A0)
            {
                ShiftUp(b, nk);
            }
            else
            {
                t[0] = lambda[0];
                for (int i = 0; i < nk; i++)
                    t[i + 1] = b[i] != A0 ? lambda[i + 1] ^ AlphaTo[Modnn(discrR + b[i])] : lambda[i + 1];

                if (2 * el <= r + erasureCount - 1)
                {
                    el = r + erasureCount - el;
                    for (int i = 0; i <= nk; i++)
                        b[i] = lambda[i] == 0 ? A0 : Modnn(IndexOf[lambda[i]] - discrR + BlockSize);
                }
                else
                {
                    ShiftUp(b, nk);
                }

                t[..(nk + 1)].CopyTo(lambda);
            }
        }

        int degLambda = 0;
        for (int i = 0; i < nk + 1; i++)
        {
            lambda[i] = IndexOf[lambda[i]];
            if (lambda[i] != A0)
                degLambda = i;
        }

        // Chien search (rs.cpp:370-398).
        lambda.Slice(1, nk).CopyTo(reg[1..]);
        count = 0;
        for (int i = 1, k = BlockSize - 1; i <= BlockSize; i++, k = Modnn(BlockSize + k - 1))
        {
            int q = 1;
            for (int j = degLambda; j > 0; j--)
            {
                if (reg[j] != A0)
                {
                    reg[j] = Modnn(reg[j] + j);
                    q ^= AlphaTo[reg[j]];
                }
            }

            if (q != 0)
                continue;
            root[count] = i;
            loc[count] = k;
            if (++count == degLambda)
                break;
        }

        if (degLambda != count)
        {
            count = -1;
            goto Finish;
        }

        // Error+erasure evaluator omega(x) = s(x)·lambda(x) mod x^(NN-KK) (rs.cpp:403-415).
        int degOmega = 0;
        for (int i = 0; i < nk; i++)
        {
            int tmp = 0;
            for (int j = degLambda < i ? degLambda : i; j >= 0; j--)
            {
                if (s[i + 1 - j] != A0 && lambda[j] != A0)
                    tmp ^= AlphaTo[Modnn(s[i + 1 - j] + lambda[j])];
            }

            if (tmp != 0)
                degOmega = i;
            omega[i] = IndexOf[tmp];
        }

        omega[nk] = A0;

        // Forney: error values (rs.cpp:421-445).
        for (int j = count - 1; j >= 0; j--)
        {
            int num1 = 0;
            for (int i = degOmega; i >= 0; i--)
            {
                if (omega[i] != A0)
                    num1 ^= AlphaTo[Modnn(omega[i] + (i * root[j]))];
            }

            int num2 = AlphaTo[Modnn((root[j] * (B0 - 1)) + BlockSize)];
            int den = 0;
            for (int i = Math.Min(degLambda, nk - 1) & ~1; i >= 0; i -= 2)
            {
                if (lambda[i + 1] != A0)
                    den ^= AlphaTo[Modnn(lambda[i + 1] + (i * root[j]))];
            }

            if (den == 0)
            {
                count = -1;
                goto Finish;
            }

            if (num1 != 0)
                data[loc[j]] ^= (byte)AlphaTo[Modnn(IndexOf[num1] + IndexOf[num2] + BlockSize - IndexOf[den])];
        }

    Finish:
        for (int i = 0; i < count; i++)
            erasurePositions[i] = loc[i];
        return count;
    }

    /// <summary>COPYDOWN(&amp;b[1], b, NN-KK); b[0] = A0 (rs.cpp:82-86,331-332).</summary>
    private static void ShiftUp(Span<int> b, int nk)
    {
        for (int ci = nk - 1; ci >= 0; ci--)
            b[ci + 1] = b[ci];
        b[0] = A0;
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
