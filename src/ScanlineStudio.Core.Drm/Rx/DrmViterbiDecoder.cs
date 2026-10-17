namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's Viterbi decoder for multi-stage decoding, <c>viterbi_decode</c> (drmrx/viterbi_decode.cpp:47-305;
/// Torsten Schorr, Univ. Kaiserslautern 2004, GPL-2.0-or-later): 64 states, float path metrics
/// (viterbi_decode.h:89), depuncturing on the fly through three run-length patterns (part A, part B, tail), then a
/// traceback that writes the information bits and re-encodes the code bits into bit <c>bitpos</c> of <c>cwout</c>
/// at their interleaved positions (the hard decisions of the next MSD level).</summary>
/// <remarks>Not thread-safe: one instance per channel decoder. Allocation-free after construction.</remarks>
internal sealed class DrmViterbiDecoder
{
    private const int States = 64;
    private const int NoOfBf = 32;
    private const float Inf = 1e20f;

    private readonly float[] _metricsA = new float[States];
    private readonly float[] _metricsB = new float[States];
    private readonly byte[] _pathReg;

    /// <param name="maxSymbols">The largest <c>N</c> (llr count) ever passed; QSSTV sizes path_reg as 2N·STATES (msdhardmsc.cpp:164-166).</param>
    public DrmViterbiDecoder(int maxSymbols)
    {
        _pathReg = new byte[maxSymbols * States];
    }

    /// <summary><c>viterbi_decode(llr, N, N_PartA, puncturing1, puncturing2, puncturing3, infoout, cwout, bitpos,
    /// Deinterleaver, L, N_tail, memory_ptr)</c>. Each puncturing pattern is a row of <see cref="DrmMsdTables"/>.</summary>
    public void Decode(ReadOnlySpan<float> llr, int n, int nPartA, ReadOnlySpan<sbyte> puncturing1, ReadOnlySpan<sbyte> puncturing2,
        ReadOnlySpan<sbyte> puncturing3, Span<byte> infoout, Span<byte> cwout, int bitpos, ReadOnlySpan<int> deinterleaver, int l, int nTail)
    {
        float[] oldMetrics = _metricsA;
        float[] newMetrics = _metricsB;
        byte[] pathReg = _pathReg;
        ReadOnlySpan<byte> coderOutput = DrmMsdTables.CoderOutput;
        Span<float> acc = stackalloc float[8];
        for (int j = 1; j < States; j++)
            oldMetrics[j] = -Inf;
        oldMetrics[0] = 0;
        int symbolIndex = 0;
        int pathRegIndex = 0;
        ReadOnlySpan<sbyte> punct = puncturing1;
        int pp = 1;
        int nPart = nPartA;
        for (int part = 0; part < 3; part++)
        {
            while (symbolIndex < nPart)
            {
                int pos = deinterleaver[symbolIndex];
                acc[0] = -llr[pos];
                acc[1] = llr[pos];
                acc[2] = -llr[pos];
                acc[3] = llr[pos];
                switch (punct[pp])
                {
                    case 3:
                        pos = deinterleaver[symbolIndex + 1];
                        acc[0] += -llr[pos];
                        acc[1] += -llr[pos];
                        acc[2] += llr[pos];
                        acc[3] += llr[pos];
                        symbolIndex += 2;
                        break;
                    case 5:
                        pos = deinterleaver[symbolIndex + 1];
                        acc[0] += -llr[pos];
                        acc[1] += -llr[pos];
                        acc[2] += -llr[pos];
                        acc[3] += -llr[pos];
                        symbolIndex += 2;
                        break;
                    case 7:
                    {
                        pos = deinterleaver[symbolIndex + 1];
                        int pos2 = deinterleaver[symbolIndex + 2];
                        acc[0] += -llr[pos] - llr[pos2];
                        acc[1] += -llr[pos] - llr[pos2];
                        acc[2] += llr[pos] - llr[pos2];
                        acc[3] += llr[pos] - llr[pos2];
                        symbolIndex += 3;
                        break;
                    }

                    case 15:
                    {
                        pos = deinterleaver[symbolIndex + 1];
                        int pos2 = deinterleaver[symbolIndex + 2];
                        int pos3 = deinterleaver[symbolIndex + 3];
                        acc[0] += -llr[pos] - llr[pos2] - llr[pos3];
                        acc[1] += -llr[pos] - llr[pos2] + llr[pos3];
                        acc[2] += llr[pos] - llr[pos2] - llr[pos3];
                        acc[3] += llr[pos] - llr[pos2] + llr[pos3];
                        symbolIndex += 4;
                        break;
                    }

                    default:
                        symbolIndex++;
                        break;
                }

                pp += punct[pp + 1];
                acc[7] = -acc[0];
                acc[6] = -acc[1];
                acc[5] = -acc[2];
                acc[4] = -acc[3];
                for (int bf = 0; bf < NoOfBf; bf++)
                {
                    float s1 = oldMetrics[bf];
                    float s2 = oldMetrics[bf + NoOfBf];
                    float inc = acc[coderOutput[bf]];
                    int pr = pathRegIndex + 2 * bf;
                    newMetrics[2 * bf] = s1 + inc;
                    pathReg[pr] = 0;
                    float path2 = s2 - inc;
                    if (path2 > newMetrics[2 * bf])
                    {
                        newMetrics[2 * bf] = path2;
                        pathReg[pr] = 1;
                    }

                    newMetrics[2 * bf + 1] = s1 - inc;
                    pathReg[pr + 1] = 0;
                    path2 = s2 + inc;
                    if (path2 > newMetrics[2 * bf + 1])
                    {
                        newMetrics[2 * bf + 1] = path2;
                        pathReg[pr + 1] = 1;
                    }
                }

                pathRegIndex += States;
                (oldMetrics, newMetrics) = (newMetrics, oldMetrics);
            }

            if (part == 0)
            {
                punct = puncturing2;
                pp = 1;
                nPart = n - nTail;
            }
            else
            {
                punct = puncturing3;
                pp = 1;
                nPart = n;
            }
        }

        // Traceback (viterbi_decode.cpp:223-303); the pattern index walks backwards from 1 − pattern[0].
        int state = 0;
        symbolIndex = n - 1;
        int infoIndex = l - 1;
        nPart = n - nTail;
        pathRegIndex -= States;
        punct = puncturing3;
        pp = 1 - punct[0];
        byte keep = (byte)~(1 << bitpos);
        for (int part = 2; part >= 0; part--)
        {
            while (symbolIndex >= nPart)
            {
                infoout[infoIndex] = (byte)(state & 0x1);
                infoIndex--;
                int sel = pathReg[pathRegIndex + state];
                int mask = -((state & 0x1) ^ sel);
                state = (state >> 1) | (sel * NoOfBf);
                int codebits = coderOutput[state & ~NoOfBf] ^ mask;
                switch (punct[pp])
                {
                    case 3:
                        Put(cwout, deinterleaver[symbolIndex], keep, ((codebits & 0x2) >> 1) << bitpos);
                        symbolIndex -= 2;
                        break;
                    case 5:
                        Put(cwout, deinterleaver[symbolIndex], keep, ((codebits & 0x4) >> 2) << bitpos);
                        symbolIndex -= 2;
                        break;
                    case 7:
                        Put(cwout, deinterleaver[symbolIndex], keep, ((codebits & 0x4) >> 2) << bitpos);
                        Put(cwout, deinterleaver[symbolIndex - 1], keep, ((codebits & 0x2) >> 1) << bitpos);
                        symbolIndex -= 3;
                        break;
                    case 15:
                        Put(cwout, deinterleaver[symbolIndex], keep, (codebits & 0x1) << bitpos);
                        Put(cwout, deinterleaver[symbolIndex - 1], keep, ((codebits & 0x4) >> 2) << bitpos);
                        Put(cwout, deinterleaver[symbolIndex - 2], keep, ((codebits & 0x2) >> 1) << bitpos);
                        symbolIndex -= 4;
                        break;
                    default:
                        symbolIndex--;
                        break;
                }

                Put(cwout, deinterleaver[symbolIndex + 1], keep, (codebits & 0x1) << bitpos);
                pathRegIndex -= States;
                pp -= punct[pp - 1];
            }

            if (part == 2)
            {
                punct = puncturing2;
                pp = 1 - punct[0];
                nPart = nPartA;
            }
            else
            {
                punct = puncturing1;
                pp = 1 - punct[0];
                nPart = 0;
            }
        }
    }

    private static void Put(Span<byte> cwout, int index, byte keep, int bit) =>
        cwout[index] = (byte)((cwout[index] & keep) | bit);
}
