namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's MSC demapper, <c>mkmscmap</c> (drmrx/mkmscmap.cpp:49-422; M.Bos PA0MBO after Schorr's
/// create_MSC_demapper.m, "GNU General Public License", no version stated). For each of the 6 frame slots of
/// <c>transmission_frame_buffer</c> it lists the absolute buffer cells of one MSC frame in deinterleaved order:
/// the cell interleaver inverse, and for long interleaving (depth 0) the convolutional deinterleaver over D = 5
/// frames. Ported from the RX source, not derived from the TX cell table.</summary>
/// <remarks>Not thread-safe: one instance per channel decoder. Allocation-free after construction.</remarks>
internal sealed class DrmMscCellMap
{
    /// <summary>The second dimension of <c>MSC_Demapper[6][2959]</c> (channeldecode.cpp:67).</summary>
    public const int MaxCells = 2959;

    private const int FramesPerSuperframe = 3;

    private readonly int[] _gainRefCellsK = new int[712];
    private readonly int[] _controlCellsK = new int[65];
    private readonly int[] _pilotCellsK = new int[600];
    private readonly int[] _facCellsK = new int[65];
    private readonly int[] _mscCellsK = new int[8877];
    private readonly int[] _mscCells3Superframes = new int[3 * 8877];
    private readonly int[] _picMscInv = new int[MaxCells];
    private readonly int[] _gimscInv = new int[5000];
    private readonly int[] _cellDeinterleaver = new int[5000];

    /// <summary>Global <c>MSC_Demapper[6][2959]</c>, row-major [frame slot][cell]; zero until the first build, as a C static.</summary>
    public int[] Demapper { get; } = new int[6 * MaxCells];

    /// <summary><c>mkmscmap(robustness_mode, spectrum_occupancy, interleaver_depth, K_dc, K_modulo)</c>: rebuilds
    /// <see cref="Demapper"/> and returns N_MUX (MSC cells per frame). Also returns the global lFAC it rewrites
    /// (mkmscmap.cpp:140).</summary>
    public int Build(int robustnessMode, int spectrumOccupancy, int interleaverDepth, int kDc, int kModulo, out int lFac)
    {
        int kMin = DrmRxTables.KMin[spectrumOccupancy + robustnessMode * 6];
        int kMax = DrmRxTables.KMax[spectrumOccupancy + robustnessMode * 6];
        int symbolsPerFrame = DrmRxTables.SymbolsPerFrame[robustnessMode];
        int x = DrmRxTables.X[robustnessMode];
        int y = DrmRxTables.Y[robustnessMode];
        int k0 = DrmRxTables.K0[robustnessMode];

        // mkmscmap.cpp:106-122: gain reference cells of one frame; p_min is ceil of an already-truncated quotient.
        int rndcnt = 0;
        for (int s = 0; s < symbolsPerFrame; s++)
        {
            int n = s % y;
            int pMin = (kMin - k0 - x * n) / (x * y);
            int pMax = (kMax - k0 - x * n) / (x * y);
            for (int p = pMin; p <= pMax; p++)
            {
                int k = k0 + x * n + x * y * p;
                _gainRefCellsK[rndcnt++] = k + s * kModulo;
            }
        }

        ReadOnlySpan<int> freqRef = DrmRxTables.FreqRefCellsK.Slice(robustnessMode * 3, 3);
        int cntTimeRef = DrmRxTables.TimeRefCellsCount[robustnessMode];
        ReadOnlySpan<int> timeRef = DrmRxTables.TimeRefCellsK.Slice(robustnessMode * DrmRxTables.TimeRefCellColumns, cntTimeRef);
        lFac = DrmFacCellMap.Build(robustnessMode, 0, kModulo, _facCellsK);

        // mkmscmap.cpp:143-245: every cell of a superframe that is no pilot, FAC or DC cell, in (frame, symbol, k) order.
        int cntMscCells = 0;
        int frameCells = kModulo * symbolsPerFrame;
        int nPilots = 3 + cntTimeRef + rndcnt;
        for (int m = 0; m < FramesPerSuperframe; m++)
        {
            for (int i = 0; i < lFac; i++)
                _controlCellsK[i] = _facCellsK[i] + frameCells * m;
            for (int j = 3; j < cntTimeRef + 3; j++)
                _pilotCellsK[j] = frameCells * m + timeRef[j - 3];
            for (int j = 3 + cntTimeRef; j < nPilots; j++)
                _pilotCellsK[j] = frameCells * m + _gainRefCellsK[j - 3 - cntTimeRef];
            for (int s = 0; s < symbolsPerFrame; s++)
            {
                for (int j = 0; j < 3; j++)
                    _pilotCellsK[j] = frameCells * m + kModulo * s + freqRef[j];
                for (int k = kMin; k <= kMax; k++)
                {
                    int term = k + frameCells * m + kModulo * s;
                    if (Contains(_pilotCellsK.AsSpan(0, nPilots), term) || Contains(_controlCellsK.AsSpan(0, lFac), term))
                        continue;

                    // mkmscmap.cpp:207-233: both branches exclude the DC carrier k = 0 (unused_carriers_k[0]).
                    if (term == frameCells * m + kModulo * s)
                        continue;
                    _mscCellsK[cntMscCells++] = term;
                }
            }
        }

        int nSfa = cntMscCells;
        int nL = nSfa % FramesPerSuperframe;
        int nMux = (nSfa - nL) / FramesPerSuperframe;
        int superframeCells = symbolsPerFrame * FramesPerSuperframe * kModulo;

        int c = 0;
        for (int i = 0; i < nSfa - nL; i++)
            _mscCells3Superframes[c++] = kDc + _mscCellsK[i];
        for (int i = 0; i < nSfa - nL; i++)
            _mscCells3Superframes[c++] = kDc + superframeCells + _mscCellsK[i];
        for (int i = 0; i < nSfa - nL; i++)
            _mscCells3Superframes[c++] = kDc + 2 * superframeCells + _mscCellsK[i];

        DrmDeinterleaver.Build(0, 1, nMux, 5, _picMscInv);
        int[] dm = Demapper;
        int mod = 2 * superframeCells;
        int[] cells3 = _mscCells3Superframes;
        if (interleaverDepth == 0)
        {
            // mkmscmap.cpp:280-363: convolutional deinterleaver (ETSI ES 201 980 7.6), D = 5; GIMSC_inv is 1-based.
            const int d = 5;
            int step = nMux + 1;
            int ncGimsc = (int)Math.Ceiling((float)nMux / d);
            int cnt = 0;
            for (int j = 0; j < ncGimsc; j++)
            {
                for (int i = 0; i < d; i++)
                    _gimscInv[cnt++] = 1 + i * step + j * d;
            }

            for (int i = 0; i < nMux; i++)
                _cellDeinterleaver[i] = _gimscInv[_picMscInv[i]];
            for (int i = 0; i < nMux; i++)
            {
                int cd = _cellDeinterleaver[i];
                dm[5 * MaxCells + i] = cells3[cd - 1];
                dm[0 * MaxCells + i] = ((cells3[nMux + cd - 1] + 1) % mod) - 1;
                dm[1 * MaxCells + i] = ((cells3[2 * nMux + cd - 1] + 1) % mod) - 1;
                dm[2 * MaxCells + i] = ((superframeCells + cells3[cd - 1] + 1) % mod) - 1;
                dm[3 * MaxCells + i] = ((superframeCells + cells3[nMux + cd - 1] + 1) % mod) - 1;
                dm[4 * MaxCells + i] = ((superframeCells + cells3[2 * nMux + cd - 1] + 1) % mod) - 1;
            }
        }
        else
        {
            // mkmscmap.cpp:365-420: short interleaving, the cell interleaver inverse only.
            for (int i = 0; i < nMux; i++)
                _cellDeinterleaver[i] = _picMscInv[i];
            for (int i = 0; i < nMux; i++)
            {
                int cd = _cellDeinterleaver[i];
                dm[1 * MaxCells + i] = cells3[cd];
                dm[2 * MaxCells + i] = cells3[nMux + cd] % mod;
                dm[3 * MaxCells + i] = cells3[2 * nMux + cd] % mod;
                dm[4 * MaxCells + i] = (superframeCells + cells3[cd]) % mod;
                dm[5 * MaxCells + i] = (superframeCells + cells3[nMux + cd]) % mod;
                dm[0 * MaxCells + i] = (superframeCells + cells3[2 * nMux + cd]) % mod;
            }
        }

        return nMux;
    }

    private static bool Contains(ReadOnlySpan<int> list, int value)
    {
        for (int j = 0; j < list.Length; j++)
        {
            if (list[j] == value)
                return true;
        }

        return false;
    }
}
