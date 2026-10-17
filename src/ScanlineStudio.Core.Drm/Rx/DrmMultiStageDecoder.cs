using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's multi-stage decoder with hard-decision iterations, <c>msdhardfac</c> (drmrx/msdhardfac.cpp:145-736)
/// and <c>msdhardmsc</c> (drmrx/msdhardmsc.cpp:51-700): per QAM level, bit metrics against the set partitioning,
/// a Viterbi decode whose re-encoded bits become the hard decisions of the next level, up to <c>maxiter</c>
/// refinement passes, then energy de-dispersal of the information bits. Both files are Schorr's msd_hard.c
/// (Univ. Kaiserslautern 2004, GPL-2.0-or-later) as adapted by M.Bos PA0MBO; msdhardmsc.cpp carries only
/// PA0MBO's change note ("almost fully copied from msd_hard.c").</summary>
/// <remarks>
/// <para><b>Specialised to QSSTV's calls.</b> HMmix and HMsym are local constants 0 in both files, and
/// channel_decoding always passes Lvspp = 0 and N1 = 0 (channeldecode.cpp:362,602-716), so the hierarchical and
/// VSPP branches are unreachable and not ported; <see cref="ArgumentException"/> guards those inputs.</para>
/// <para><b>FAC vs MSC.</b> The two files differ only in the tail pattern of the first pass (FAC
/// <c>tailpuncturing[rp+12]</c>, msdhardfac.cpp:512; MSC <c>tailpuncturing[rp]</c>, msdhardmsc.cpp:427) and in the
/// MSC-only variance and noise outputs. The FAC refinement passes index <c>tailpuncturing[-12]</c>
/// (msdhardfac.cpp:615), before the table; with N_tail = rp + 12 = 0 the Viterbi decoder never reads that row,
/// it only forms a pointer from its first byte, so row 0 stands in for it here.</para>
/// <para><b>Precision</b> as QSSTV: received samples and SNR double, cast to float; distances, bit metrics and
/// Viterbi metrics float; the variance accumulates in double.</para>
/// <para>Not thread-safe: one instance per channel decoder. Allocation-free after construction.</para>
/// </remarks>
internal sealed class DrmMultiStageDecoder
{
    private const int MaxLevels = 3;

    private readonly float[] _llr;
    private readonly byte[] _hardpoints;
    private readonly byte[] _lastiter;
    private readonly byte[] _infoout;
    private readonly int[] _infoOffset = new int[MaxLevels];
    private readonly int[] _l2 = new int[MaxLevels];
    private readonly int[] _pl1 = new int[MaxLevels];
    private readonly int[] _pl2 = new int[MaxLevels];
    private readonly int[] _rp = new int[MaxLevels];
    private readonly DrmViterbiDecoder _viterbi;

    /// <param name="maxCells">The largest <c>Lrxdata</c> (QAM cells) ever passed.</param>
    public DrmMultiStageDecoder(int maxCells)
    {
        _llr = new float[2 * maxCells];
        _hardpoints = new byte[2 * maxCells];
        _lastiter = new byte[2 * maxCells];
        _infoout = new byte[MaxLevels * (2 * maxCells + 12)];
        _viterbi = new DrmViterbiDecoder(2 * maxCells);
    }

    /// <summary>The refinement passes run by the last call (msdhardmsc.cpp:667-668, <c>*iterations</c>).</summary>
    public int Iterations { get; private set; }

    /// <summary>The last MSC call's <c>*calc_variance</c> (msdhardmsc.cpp:669-685): mean squared distance to the decided points.</summary>
    public double CalcVariance { get; private set; }

    /// <summary><c>msdhardfac(received_real, received_imag, Lrxdata, snr, N1, L, dimL, Lvspp, Deinterleaver, PL,
    /// maxiter, SDCorMSC, facdata)</c>: one level (4-QAM), writes <c>L[1]</c> dispersal-removed bits.</summary>
    public void DecodeFac(ReadOnlySpan<double> receivedReal, ReadOnlySpan<double> receivedImag, int lrxdata, ReadOnlySpan<double> snr,
        ReadOnlySpan<double> l, ReadOnlySpan<int> deinterleaver, ReadOnlySpan<int> pl, int maxiter, bool sdcOrMsc, Span<double> facdata)
    {
        // msdhardfac.cpp:177-195: no_of_levels = 1, so L1 = L[0], L2 = L[1], PL1 = PL[0], PL2 = PL[1].
        Run(receivedReal, receivedImag, lrxdata, snr, 1, l, pl, deinterleaver, maxiter, sdcOrMsc, 12, facdata, default);
    }

    /// <summary><c>msdhardmsc(..., rowdimL, coldimL, Lvspp, Deinterleaver, PL, maxiter, SDCorMSC, SPPhard, VSPPhard,
    /// iterations, calc_variance, noise_signal)</c>: returns the number of SPP bits written to <paramref name="sppHard"/>;
    /// <paramref name="noiseSignal"/> gets the interleaved complex distance of each cell to its decided point.</summary>
    public int DecodeMsc(ReadOnlySpan<double> receivedReal, ReadOnlySpan<double> receivedImag, int lrxdata, ReadOnlySpan<double> snr,
        ReadOnlySpan<double> l, int rowdimL, ReadOnlySpan<int> deinterleaver, ReadOnlySpan<int> pl, int maxiter, bool sdcOrMsc,
        Span<double> sppHard, Span<double> noiseSignal)
    {
        return Run(receivedReal, receivedImag, lrxdata, snr, rowdimL, l, pl, deinterleaver, maxiter, sdcOrMsc, 0, sppHard, noiseSignal);
    }

    private int Run(ReadOnlySpan<double> rr, ReadOnlySpan<double> ri, int n, ReadOnlySpan<double> snr, int levels, ReadOnlySpan<double> l,
        ReadOnlySpan<int> pl, ReadOnlySpan<int> deinterleaver, int maxiter, bool sdcOrMsc, int firstPassTailShift, Span<double> output,
        Span<double> noiseSignal)
    {
        if (n < 20)
            throw new ArgumentException("msd_hard: N has to be >= 20.", nameof(n));
        ArgumentOutOfRangeException.ThrowIfNegative(maxiter);
        if (levels is < 1 or > MaxLevels)
            throw new ArgumentOutOfRangeException(nameof(levels));
        for (int lv = 0; lv < levels; lv++)
        {
            if ((int)l[lv] != 0)
                throw new ArgumentException("Part A (N1 > 0) is not used by QSSTV and not ported.", nameof(l));
        }

        // msdhardmsc.cpp:85-105,152-214: L1_real = L[0..levels), L2_real = L1_real + levels; infoout per level.
        int offset = 0;
        for (int lv = 0; lv < levels; lv++)
        {
            _l2[lv] = (int)l[levels + lv];
            _pl1[lv] = pl[lv];
            _pl2[lv] = pl[levels + lv];
            _infoOffset[lv] = offset;
            offset += _l2[lv] + 6 + 6;
        }

        int twoN = 2 * n;
        Span<byte> hardpoints = _hardpoints.AsSpan(0, twoN);
        hardpoints.Clear();

        // msdhardmsc.cpp:216-301: SM partitioning per level count; rp = (2N - 12) mod RY of part B's rate, -12 for FAC.
        ReadOnlySpan<float> metric = DrmMsdTables.Partitioning.AsSpan((levels == 3 ? 0 : levels == 2 ? 2 : 3) * 8, 8);
        ReadOnlySpan<int> ry = DrmMsdTables.RyInt;
        for (int lv = 0; lv < levels; lv++)
        {
            int r = ry[_pl2[lv]];
            _rp[lv] = sdcOrMsc ? (twoN - 12) - r * ((twoN - 12) / r) : -12;
        }

        Span<float> llr = _llr.AsSpan(0, twoN);

        // First decoding (msdhardmsc.cpp:331-458): closest subset point with this level's bit 0 vs 1.
        for (int level = 0; level < levels; level++)
        {
            int step = 1 << (level + 1);
            int top = 1 << levels;
            for (int m = 0; m < 2; m++)
            {
                ReadOnlySpan<double> received = m == 0 ? rr : ri;
                for (int si = m; si < twoN; si += 2)
                {
                    float sample = (float)received[si >> 1];
                    int hp = hardpoints[si];
                    float closestZero = MathF.Abs(sample - metric[hp]);
                    for (int sp = step; sp < top; sp += step)
                    {
                        float dist = MathF.Abs(sample - metric[hp + sp]);
                        if (dist < closestZero)
                            closestZero = dist;
                    }

                    float closestOne = MathF.Abs(sample - metric[hp + (1 << level)]);
                    for (int sp = 3 << level; sp < top; sp += step)
                    {
                        float dist = MathF.Abs(sample - metric[hp + sp]);
                        if (dist < closestOne)
                            closestOne = dist;
                    }

                    float snrF = (float)snr[si >> 1];
                    llr[si] = (closestZero - closestOne) * snrF;
                }
            }

            Viterbi(level, twoN, deinterleaver, _rp[level] + firstPassTailShift);
        }

        // Iterations (msdhardmsc.cpp:459-587): re-decide each level given the others; stop when level 0 is unchanged.
        int iteration = 0;
        bool diff = true;
        while (iteration < maxiter)
        {
            hardpoints.CopyTo(_lastiter);
            for (int level = 0; level < levels; level++)
            {
                int bit = 1 << level;
                for (int m = 0; m < 2; m++)
                {
                    ReadOnlySpan<double> received = m == 0 ? rr : ri;
                    for (int si = m; si < twoN; si += 2)
                    {
                        float sample = (float)received[si >> 1];
                        int hp = hardpoints[si];
                        float closestZero = MathF.Abs(sample - metric[hp & ~bit]);
                        float closestOne = MathF.Abs(sample - metric[hp | bit]);
                        float snrF = (float)snr[si >> 1];
                        llr[si] = (closestZero - closestOne) * snrF;
                    }
                }

                Viterbi(level, twoN, deinterleaver, _rp[level]);

                if (level == 0)
                {
                    diff = !HardpointsUnchanged(hardpoints, _lastiter, twoN);
                    if (!diff)
                        break;
                }
            }

            if (!diff)
                break;
            iteration++;
        }

        Iterations = iteration;

        // Energy dispersal (msdhardmsc.cpp:589-653): part B bits of every level in turn, one continuous PRBS.
        var prbs = RxPrbs.Create();
        int nOut = 0;
        for (int lv = 0; lv < levels; lv++)
        {
            ReadOnlySpan<byte> info = _infoout.AsSpan(_infoOffset[lv]);
            for (int m = 0; m < _l2[lv]; m++)
            {
                output[nOut++] = info[m] ^ prbs.Bit;
                prbs.Shift();
            }
        }

        if (!noiseSignal.IsEmpty)
        {
            // msdhardmsc.cpp:669-697: distance of each component to the decided constellation point.
            double variance = 0.0;
            for (int si = 0; si < n; si++)
            {
                float dist = (float)rr[si] - metric[hardpoints[2 * si]];
                variance += (double)dist * dist;
                dist = (float)ri[si] - metric[hardpoints[2 * si + 1]];
                variance += (double)dist * dist;
            }

            CalcVariance = variance / n;
            for (int si = 0; si < n; si++)
            {
                noiseSignal[2 * si] = (float)rr[si] - metric[hardpoints[2 * si]];
                noiseSignal[2 * si + 1] = (float)ri[si] - metric[hardpoints[2 * si + 1]];
            }
        }

        return nOut;
    }

    /// <summary>ITER_BREAK's test (msdhardmsc.cpp:552, msdhardfac.cpp:635): the hard decisions are compared as
    /// <c>(2N)/sizeof(int)</c> whole ints, so with N odd the last 2 bytes are ignored.</summary>
    internal static bool HardpointsUnchanged(ReadOnlySpan<byte> hardpoints, ReadOnlySpan<byte> lastiter, int twoN)
    {
        int compareBytes = twoN / sizeof(int) * sizeof(int);
        return hardpoints[..compareBytes].SequenceEqual(lastiter[..compareBytes]);
    }

    // viterbi_decode with QSSTV's arguments: N_PartA = 2·N1 = 0, the level's deinterleaver, L2 + 6 bits (msdhardmsc.cpp:420-434,523-538).
    private void Viterbi(int level, int twoN, ReadOnlySpan<int> deinterleaver, int tailRow)
    {
        ReadOnlySpan<sbyte> punct = DrmMsdTables.Puncturing;
        ReadOnlySpan<sbyte> tail = DrmMsdTables.TailPuncturing;
        const int pr = DrmMsdTables.PuncturingRowLength;
        const int tr = DrmMsdTables.TailPuncturingRowLength;
        int row = tailRow < 0 ? 0 : tailRow;
        _viterbi.Decode(_llr, twoN, 0, punct.Slice(_pl1[level] * pr, pr), punct.Slice(_pl2[level] * pr, pr), tail.Slice(row * tr, tr),
            _infoout.AsSpan(_infoOffset[level]), _hardpoints, level, deinterleaver[(twoN * level)..], _l2[level] + 6, _rp[level] + 12);
    }
}
