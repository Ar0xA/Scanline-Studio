namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's per-frame demodulation after frame sync: <c>demodulator::channelEstimation</c>
/// (drmrx/demodulator.cpp:524-1132) with its share of <c>init</c> (:50-52,63-65,71-84) and of
/// <c>frameSync</c> (:502-516). Per frame: the 2-D Wiener pilot interpolation (tables rebuilt when the
/// mode/occupancy code changes), equalization into <see cref="DrmDemodState.TransmissionFrameBuffer"/>,
/// symbol-by-symbol tracking with <see cref="DrmOfdmSyncDemodulator"/>, the FAC-based weighted MER and the
/// SNR resync rule.</summary>
/// <remarks>
/// <para><b>Precision.</b> Float as QSSTV (demodulator.h); the Wiener filter coefficients (sinc products) are
/// double and stored to float; the matrix inversion is float (<see cref="DrmLuSolver"/>, our own LU in place of
/// QSSTV's Numerical Recipes routines). The double sub-expressions of the source are evaluated in double.</para>
/// <para><b>Ported as-is.</b> <c>init</c> sets <c>SNR_time_out_counter = SNR_TIMEOUT</c> (:50) but the rule
/// reads <c>SNR_timeout_counter</c>, which init sets to 0 (:83): the first frame below 3 dB after any init
/// resyncs. <c>fac_not_valid_counter</c> is only reset by init or when it fires (:1099-1104). The Wiener tables
/// use <c>spectrum_occupancy_estimation</c> while the change test uses <c>spectrum_occupancy</c> (:579-627),
/// and the tracker's <c>Init</c> uses the latter's carrier count (:588-591).</para>
/// <para><b>Occupancy code index.</b> <c>mode_and_occupancy_code_table</c> has 14 entries (drm.cpp:107); :579
/// indexes it with 6·mode + occupancy, which stays ≤ 13: frequencySync only produces an estimate of 0 or 1, the
/// global starts at 0, and channel decoding stores only −1, 0 or 1 (channeldecode.cpp:381,414,437). A guard
/// returns the index itself past the table so the lookup can never read out of bounds.</para>
/// <para>Not thread-safe: one instance per receive worker. No allocation per frame; the Wiener tables are
/// rebuilt (allocation-free) only when the mode/occupancy code changes.</para>
/// </remarks>
internal sealed class DrmChannelEstimator : IDrmFrameStage
{
    // MIN_ABS_H (demodulator.cpp:522), a double constant.
    private const double MinAbsH = 8.0E-5 * 8.0E-5;

    // DBL_EPSILON (<float.h>); C#'s double.Epsilon is the smallest denormal instead.
    private const double DblEpsilon = 2.220446049250313E-16;
    private const int MaxNp = 208;
    private const int MaxLh = 229;
    private const int MaxNextPilots = 205;
    private const int MaxGainRefCells = 712;
    private const int MaxSubset = 209;

    private readonly DrmOfdmSyncDemodulator _sync = new();
    private readonly int[] _noOfUsedCellsPerFrameList = new int[24];
    private readonly float[] _sigmaqNoiseList = new float[4];

    private readonly int[] _gainRefCellsK = new int[MaxGainRefCells];
    private readonly int[] _gainRefCellsTheta1024 = new int[MaxGainRefCells];
    private readonly float[] _gainRefCellsA = new float[MaxGainRefCells];
    private readonly int[] _trainingCellsK = new int[5 * MaxGainRefCells];
    private readonly int[] _cntTrCells = new int[5];
    private readonly int[] _gainRefCellsSubset = new int[5 * MaxSubset];
    private readonly int[] _gainRefCellsSubsetIndex = new int[5 * MaxSubset];
    private readonly int[] _cntNextPilotCells = new int[5];
    private readonly int[] _nextPilotCellsKIndex = new int[5 * 40];
    private readonly int[] _nextPilotCellsK = new int[5 * 40];
    private readonly int[] _sortTmp = new int[1024];
    private readonly int[] _sortTmp2 = new int[1024];
    private readonly float[] _phi = new float[MaxNp * MaxNp];
    private readonly float[] _phiInv = new float[MaxNp * MaxNp];
    private readonly int[] _luPivot = new int[MaxNp];
    private readonly float[] _luScratch = new float[MaxNp];
    private readonly float[] _theta = new float[MaxNp];
    private readonly float[] _wSymbolBlk = new float[5 * MaxNp * MaxLh];
    private readonly float[] _wPilotsBlk = new float[5 * MaxNp * MaxNextPilots];
    private readonly float[] _wSymbol = new float[MaxNp];
    private readonly float[] _wPilots = new float[MaxNp];

    private readonly float[] _nextPilots = new float[2 * 70];
    private readonly float[] _actualPilots = new float[458];
    private readonly float[] _h = new float[458];
    private readonly float[] _normalizedTrainingCells = new float[1424];
    private readonly int[] _gainRefCellsSubsetNn = new int[MaxSubset];
    private readonly int[] _trainingCellsRelativeIndex = new int[MaxSubset];
    private readonly int[] _actualPilotsRelativeIndex = new int[MaxSubset];
    private readonly float[] _symbolTemp = new float[2 * DrmRxTables.TuMax];
    private readonly int[] _freqRefCellsK = new int[3];
    private readonly int[] _freqRefCellsTheta1024 = new int[3];
    private readonly int[] _powerBoost = new int[4];

    private int _snrTimeOutCounter;
    private int _snrTimeoutCounter;
    private int _facNotValidCounter;
    private int _modeAndOccupancyCode;
    private int _modeAndOccupancyCodeLast;
    private int _smpRateConvFftPhaseDiff;
    private float _smpRateConvFftPhaseOffset;
    private int _smpRateConvInOutDelay;
    private float _deltaFreqOffset;
    private int _kMin;
    private int _kMax;
    private int _carrierPerSymbol;
    private int _x;
    private int _y;
    private int _k0;
    private int _q1024;
    private int _gainRefCellsPerFrame;
    private int _gainRefCellsPerYSymbols;
    private float _deltaTimeOffsetI;
    private float _timeOffsetFractional;

    /// <summary>The tracking symbol demodulator (tests observe its peak search).</summary>
    internal DrmOfdmSyncDemodulator Tracker => _sync;

    /// <summary>Equalized frames since construction (diagnostics and tests).</summary>
    public int FramesProcessed { get; private set; }

    /// <summary><c>delta_freq_offset</c>: the pilot phase drift fed to the tracker.</summary>
    public float DeltaFreqOffset => _deltaFreqOffset;

    /// <summary>Start the read SNR counter full at init, as the source evidently meant (P6b-4 candidate; off = QSSTV).</summary>
    internal bool SnrTimeoutStartsFull { get; init; }

    /// <summary>Reset <c>fac_not_valid_counter</c> on every good frame (P6b-4 candidate; off = QSSTV).</summary>
    internal bool FacCounterResetsOnGoodFrame { get; init; }

    /// <summary><c>SNR_timeout_counter</c>, the counter the resync rule reads.</summary>
    public int SnrTimeoutCounter => _snrTimeoutCounter;

    /// <summary><c>SNR_time_out_counter</c>, written only by init (demodulator.cpp:50).</summary>
    public int SnrTimeOutCounter => _snrTimeOutCounter;

    public int FacNotValidCounter => _facNotValidCounter;

    public int ModeAndOccupancyCode => _modeAndOccupancyCode;

    public int ModeAndOccupancyCodeLast => _modeAndOccupancyCodeLast;

    public int SmpRateConvFftPhaseDiff => _smpRateConvFftPhaseDiff;

    public float SmpRateConvFftPhaseOffset => _smpRateConvFftPhaseOffset;

    public int SmpRateConvInOutDelay => _smpRateConvInOutDelay;

    /// <summary>K_min/K_max of the current Wiener tables (from <c>spectrum_occupancy_estimation</c>).</summary>
    public int KMin => _kMin;

    public int KMax => _kMax;

    /// <summary>The current Wiener tables (tests).</summary>
    internal WienerTableView Tables => new(this);

    /// <inheritdoc/>
    public void Reset()
    {
        _snrTimeOutCounter = DrmRxTables.SnrTimeout;
        _facNotValidCounter = DrmRxTables.FacValidCounter;
        _modeAndOccupancyCodeLast = -1;
        _smpRateConvFftPhaseDiff = 0;
        _smpRateConvFftPhaseOffset = 0;
        _smpRateConvInOutDelay = 0;
        int k = 0;
        for (int i = 0; i < 4; i++)
        {
            for (int j = 0; j < 6; j++)
            {
                _noOfUsedCellsPerFrameList[k++] = (DrmRxTables.KMax[j + i * 6] - DrmRxTables.KMin[j + i * 6] + 1 - DrmRxTables.UnusedCarriers[i])
                    * DrmRxTables.SymbolsPerFrame[i];
            }
        }

        _sigmaqNoiseList[0] = (float)Math.Pow(10.0, -16.0 / 10.0);
        _sigmaqNoiseList[1] = (float)Math.Pow(10.0, -14.0 / 10.0);
        _sigmaqNoiseList[2] = (float)Math.Pow(10.0, -14.0 / 10.0);
        _sigmaqNoiseList[3] = (float)Math.Pow(10.0, -12.0 / 10.0);
        _snrTimeoutCounter = SnrTimeoutStartsFull ? DrmRxTables.SnrTimeout : 0;
        _deltaFreqOffset = 0;
    }

    /// <inheritdoc/>
    public void BeginDemodulate()
    {
    }

    /// <inheritdoc/>
    public void OnFrameSync()
    {
        Array.Fill(_nextPilots, (float)1.1);
        Array.Clear(_actualPilots);
        Array.Clear(_wPilotsBlk);
    }

    /// <inheritdoc/>
    public bool ProcessFrame(DrmDemodState st)
    {
        st.NSymbolsNeeded = st.SymbolsPerFrame + st.SymbolsPer2DWindow - st.SymBufWidx;
        st.NSamplesNeeded = (st.NSymbolsNeeded + 1) * st.Ts - st.RsBufWidx;
        if (st.NSamplesNeeded > 0)
        {
            st.InputSamplesBufferRequest = st.NSamplesNeeded;
            return false;
        }

        if (st.SpectrumOccupancy <= 0)
            st.SpectrumOccupancy = st.SpectrumOccupancyEstimation < 0 ? 3 : st.SpectrumOccupancyEstimation;
        if (st.SpectrumOccupancy > 3)
            st.SpectrumOccupancy = 3;
        _modeAndOccupancyCode = ModeAndOccupancyCodeAt(st.RobustnessMode * 6 + st.SpectrumOccupancy);
        if (_modeAndOccupancyCode < 0)
        {
            st.SpectrumOccupancy = 1;
            _modeAndOccupancyCode = ModeAndOccupancyCodeAt(st.RobustnessMode * 6 + st.SpectrumOccupancy);
        }

        if (_modeAndOccupancyCode != _modeAndOccupancyCodeLast)
            BuildTables(st);

        EqualizeAndTrack(st, out int tSmp);
        MeasureFacSnr(st);
        ApplySnrRule(st, tSmp);
        FramesProcessed++;
        return true;
    }

    internal static int ModeAndOccupancyCodeAt(int index)
    {
        ReadOnlySpan<int> table = DrmRxTables.ModeAndOccupancyCodeTable;
        return index < table.Length ? table[index] : index;
    }

    // demodulator.cpp:586-903: pilot lists, the 2-D Wiener matrices, the FAC map.
    private void BuildTables(DrmDemodState st)
    {
        int rm = st.RobustnessMode;
        _kMin = DrmRxTables.KMin[st.SpectrumOccupancy + rm * 6];
        _kMax = DrmRxTables.KMax[st.SpectrumOccupancy + rm * 6];
        _carrierPerSymbol = _kMax - _kMin + 1;
        _sync.Init(st.Ts, st.Tu, _kMax - _kMin + 1);

        st.Tu = DrmRxTables.Tu[rm];
        st.Ts = DrmRxTables.Ts[rm];
        st.Tg = st.Ts - st.Tu;
        float sigmaqNoise = _sigmaqNoiseList[rm];
        st.SymbolsPerFrame = DrmRxTables.SymbolsPerFrame[rm];
        for (int i = 0; i < 3; i++)
        {
            _freqRefCellsK[i] = DrmRxTables.FreqRefCellsK[rm * 3 + i];
            _freqRefCellsTheta1024[i] = DrmRxTables.FreqRefCellsTheta1024[rm * 3 + i];
        }

        int cntTimeRefCells = DrmRxTables.TimeRefCellsCount[rm];
        for (int i = 0; i < cntTimeRefCells; i++)
        {
            st.TimeRefCellsK[i] = DrmRxTables.TimeRefCellsK[rm * DrmRxTables.TimeRefCellColumns + i];
            st.TimeRefCellsTheta1024[i] = DrmRxTables.TimeRefCellsTheta1024[rm * DrmRxTables.TimeRefCellColumns + i];
        }

        int soe = st.SpectrumOccupancyEstimation;
        _kMin = DrmRxTables.KMin[soe + rm * 6];
        _kMax = DrmRxTables.KMax[soe + rm * 6];
        int cps = _carrierPerSymbol = _kMax - _kMin + 1;
        for (int i = 0; i < 4; i++)
            _powerBoost[i] = DrmRxTables.PowerBoost[(rm * 6 + soe) * 4 + i];
        _x = DrmRxTables.X[rm];
        _y = DrmRxTables.Y[rm];
        _k0 = DrmRxTables.K0[rm];
        _q1024 = DrmRxTables.Q1024[rm];
        int usedCells = _noOfUsedCellsPerFrameList[soe + rm * 6];
        float meanEnergy = usedCells + 3 + cntTimeRefCells;
        int spf = st.SymbolsPerFrame;
        int x = _x, y = _y;
        int rndcnt = 0;
        float sqrt2 = MathF.Sqrt(2.0f);
        for (int s = 0; s < spf; s++)
        {
            int nnn = s % y;
            int m = s / y;
            int pMin = (_kMin - _k0 - x * nnn) / (x * y);
            int pMax = (_kMax - _k0 - x * nnn) / (x * y);
            for (int p = pMin; p <= pMax; p++)
            {
                int k = _k0 + x * nnn + x * y * p;
                int zw = rm * 50 + nnn * 10 + m;
                int theta1024 = (4 * DrmRxTables.Z256[zw] + p * DrmRxTables.W1024[zw] + p * p * (1 + s) * _q1024) % 1024;
                float a = sqrt2;
                for (int i = 0; i < 4; i++)
                {
                    if (k == _powerBoost[i])
                        a = 2;
                }

                if (s == 0)
                {
                    for (int i = 0; i < cntTimeRefCells; i++)
                    {
                        if (k == st.TimeRefCellsK[i])
                        {
                            theta1024 = st.TimeRefCellsTheta1024[i];
                            a = sqrt2;
                            meanEnergy = (float)(meanEnergy - 1.0);
                        }
                    }
                }

                for (int i = 0; i < 3; i++)
                {
                    if (k == _freqRefCellsK[i])
                    {
                        theta1024 = _freqRefCellsTheta1024[i];
                        if (rm == 3)
                            theta1024 = (theta1024 + 512 * s) % 1024;
                        a = sqrt2;
                        meanEnergy = (float)(meanEnergy - 1.0);
                    }
                }

                _gainRefCellsK[rndcnt] = k + s * cps;
                _gainRefCellsTheta1024[rndcnt] = theta1024;
                _gainRefCellsA[rndcnt++] = a;
                meanEnergy = (float)(meanEnergy - 1.0 + a * a);
            }
        }

        st.MeanEnergyOfUsedCells = meanEnergy / usedCells;

        st.SymbolsPer2DWindow = DrmRxTables.SymbolsPer2DWindow[rm];
        st.SymbolsToDelay = DrmRxTables.SymbolsToDelay[rm];
        int s2d = st.SymbolsPer2DWindow;
        double fCutT = 0.0675 / (1.0 * (double)y);
        double fCutK = 1.75 * (float)st.Tg / (float)st.Tu;

        Array.Clear(_cntTrCells);
        for (int nnn = 0; nnn < y; nnn++)
        {
            Span<int> trK = _trainingCellsK.AsSpan(nnn * MaxGainRefCells, MaxGainRefCells);
            Span<int> subset = _gainRefCellsSubset.AsSpan(nnn * MaxSubset, MaxSubset);
            Span<int> subsetIndex = _gainRefCellsSubsetIndex.AsSpan(nnn * MaxSubset, MaxSubset);
            for (int i = 0; i < rndcnt; i++)
                trK[i] = (_gainRefCellsK[i] - _kMin + (spf - nnn) * cps) % (spf * cps) + _kMin;

            for (int i = 0; i < rndcnt; i++)
            {
                if (trK[i] - _kMin < cps * s2d)
                {
                    subsetIndex[_cntTrCells[nnn]] = i;
                    subset[_cntTrCells[nnn]++] = trK[i];
                }
            }

            _cntNextPilotCells[nnn] = 0;
            for (int i = 0; i < rndcnt; i++)
            {
                if (trK[i] - _kMin >= cps * s2d && trK[i] - _kMin < cps * (s2d + 1))
                {
                    _nextPilotCellsKIndex[nnn * 40 + _cntNextPilotCells[nnn]] = i;
                    _nextPilotCellsK[nnn * 40 + _cntNextPilotCells[nnn]++] = ((trK[i] - _kMin) % cps) + _kMin;
                }
            }

            int np = _cntTrCells[nnn];
            // Rotates at the last descent (demodulator.cpp:727-755); never fires for A/B/E, whose wrapped cells lie outside the window.
            int sortbrkpnt = 0;
            for (int i = 1; i < np; i++)
            {
                if (subset[i] < subset[i - 1])
                    sortbrkpnt = i;
            }

            if (sortbrkpnt > 0)
            {
                for (int i = 0; i < sortbrkpnt; i++)
                {
                    _sortTmp[i] = subset[i];
                    _sortTmp2[i] = subsetIndex[i];
                }

                for (int i = 0; i < np - sortbrkpnt; i++)
                {
                    subset[i] = subset[i + sortbrkpnt];
                    subsetIndex[i] = subsetIndex[i + sortbrkpnt];
                }

                for (int i = np - sortbrkpnt; i < np; i++)
                {
                    subset[i] = _sortTmp[i + sortbrkpnt - np];
                    subsetIndex[i] = _sortTmp2[i + sortbrkpnt - np];
                }
            }

            for (int i = 0; i < np; i++)
                trK[i] = subset[i];

            for (int k1 = 0; k1 < np; k1++)
            {
                int k1Pos = ((subset[k1] - _kMin) % cps) + _kMin;
                int t1Pos = (subset[k1] - _kMin) / cps;
                for (int k2 = 0; k2 < np; k2++)
                {
                    int k2Pos = ((subset[k2] - _kMin) % cps) + _kMin;
                    int t2Pos = (subset[k2] - _kMin) / cps;
                    double xsinc1 = (k1Pos - k2Pos) * fCutK;
                    double xsinc2 = (t1Pos - t2Pos) * fCutT;
                    xsinc1 = k1Pos == k2Pos ? 1.0 : Sinc(xsinc1);
                    xsinc2 = Math.Abs(xsinc2) < DblEpsilon ? 1.0 : Sinc(xsinc2);
                    _phi[k1 * MaxNp + k2] = (float)(xsinc1 * xsinc2);
                }
            }

            for (int i = 0; i < np; i++)
            {
                float a = _gainRefCellsA[subsetIndex[i]];
                _phi[i * MaxNp + i] = (float)(_phi[i * MaxNp + i] + sigmaqNoise * 2.0 / (a * a));
            }

            DrmLuSolver.Invert(_phi, np, MaxNp, _phiInv, _luPivot, _luScratch);

            int lH = _kMax - _kMin + 1;
            for (int k1 = 0; k1 < lH; k1++)
            {
                int k1Pos = k1 + _kMin;
                int t1Pos = st.SymbolsToDelay;
                for (int k2 = 0; k2 < np; k2++)
                    _theta[k2] = WienerCoefficient(k1Pos, t1Pos, trK[k2], fCutK, fCutT);
                MultiplyThetaPhiInv(np, _wSymbol);
                for (int j = 0; j < np; j++)
                    _wSymbolBlk[(nnn * MaxNp + j) * MaxLh + k1] = _wSymbol[j];
            }

            for (int k1 = 0; k1 < _cntNextPilotCells[nnn]; k1++)
            {
                int k1Pos = _nextPilotCellsK[nnn * 40 + k1];
                int t1Pos = s2d - 1;
                for (int k2 = 0; k2 < np; k2++)
                    _theta[k2] = WienerCoefficient(k1Pos, t1Pos, trK[k2], fCutK, fCutT);
                MultiplyThetaPhiInv(np, _wPilots);
                for (int j = 0; j < np; j++)
                    _wPilotsBlk[(nnn * MaxNp + j) * MaxNextPilots + k1] = _wPilots[j];
            }
        }

        for (int i = 0; i < rndcnt; i++)
        {
            int temp = ((_gainRefCellsK[i] - _kMin) / cps) * (st.KModulo - cps);
            _gainRefCellsK[i] += temp + st.KDc - 1;
        }

        st.LFac = DrmFacCellMap.Build(rm, st.KDc, st.KModulo, st.FacCellsK);
        _modeAndOccupancyCodeLast = _modeAndOccupancyCode;
        for (int i = 0; i < _cntTrCells[y - 1]; i++)
        {
            _nextPilots[2 * i] = 0.0f;
            _nextPilots[2 * i + 1] = 0.0f;
        }

        _gainRefCellsPerFrame = rndcnt;
        _gainRefCellsPerYSymbols = rndcnt / (spf / y);
    }

    // THETA entry for an estimate at (k1Pos, t1Pos) from a training cell (demodulator.cpp:816-834, 853-871).
    private float WienerCoefficient(int k1Pos, int t1Pos, int trainingCellK, double fCutK, double fCutT)
    {
        int k2Pos = (trainingCellK - _kMin) % (_kMax - _kMin + 1) + _kMin;
        int t2Pos = (trainingCellK - _kMin) / (_kMax - _kMin + 1);
        double xsinc1 = k1Pos == k2Pos ? 1.0 : Sinc((k1Pos - k2Pos) * fCutK);
        double xsinc2 = t1Pos == t2Pos ? 1.0 : Sinc((t1Pos - t2Pos) * fCutT);
        return (float)(xsinc1 * xsinc2);
    }

    // W[j] = Σ_k THETA[k]·PHI_INV[k][j] in float (demodulator.cpp:837-842).
    private void MultiplyThetaPhiInv(int np, float[] w)
    {
        for (int j = 0; j < np; j++)
        {
            float sum = 0.0f;
            for (int k = 0; k < np; k++)
                sum += _theta[k] * _phiInv[k * MaxNp + j];
            w[j] = sum;
        }
    }

    private static double Sinc(double x)
    {
        double rest = Math.Sin(Math.PI * x);
        return rest / (Math.PI * x);
    }

    // demodulator.cpp:906-1048: per symbol, pilot normalization, Wiener estimate, equalization, tracking.
    private void EqualizeAndTrack(DrmDemodState st, out int tSmp)
    {
        int spf = st.SymbolsPerFrame;
        int std = st.SymbolsToDelay;
        int s2d = st.SymbolsPer2DWindow;
        int kMod = st.KModulo;
        int kDc = st.KDc;
        int y = _y;
        int lH = _kMax - _kMin + 1;
        float[] sb = st.SymbolBuffer;
        float[] tfb = st.TransmissionFrameBuffer;
        float[] ctf = st.ChannelTransferFunctionBuffer;
        float[] h = _h;
        float[] ntc = _normalizedTrainingCells;
        tSmp = 0;
        for (int i = 0; i < spf; i++)
        {
            st.SymbolCounter++;
            int nn = (i - std + spf) % spf;
            int ntwee = nn % y;
            int mtwee = nn / y;
            int cnt = _cntTrCells[ntwee];
            ReadOnlySpan<int> subsetIndex = _gainRefCellsSubsetIndex.AsSpan(ntwee * MaxSubset, cnt);
            for (int j = 0; j < cnt; j++)
            {
                _gainRefCellsSubsetNn[j] = (mtwee * _gainRefCellsPerYSymbols + subsetIndex[j]) % _gainRefCellsPerFrame;
                _trainingCellsRelativeIndex[j] = (_gainRefCellsK[_gainRefCellsSubsetNn[j]] + (spf - nn) * kMod) % (kMod * spf);
            }

            int cntActual = 0;
            for (int j = 0; j < cnt; j++)
            {
                if (_trainingCellsRelativeIndex[j] - (s2d - 1) * kMod >= 0)
                    _actualPilotsRelativeIndex[cntActual++] = j;
                int ntcIndx = _trainingCellsRelativeIndex[j] + i * kMod + 1;
                int g = _gainRefCellsSubsetNn[j];
                float hoek = (float)(2.0 * Math.PI * (float)_gainRefCellsTheta1024[g] / 1024.0);
                float tmpreal = MathF.Cos(hoek) / _gainRefCellsA[g];
                float tmpimag = -MathF.Sin(hoek) / _gainRefCellsA[g];
                ntc[2 * j] = sb[2 * ntcIndx] * tmpreal - sb[2 * ntcIndx + 1] * tmpimag;
                ntc[2 * j + 1] = sb[2 * ntcIndx + 1] * tmpreal + sb[2 * ntcIndx] * tmpimag;
            }

            for (int j = 0; j < cntActual; j++)
            {
                _actualPilots[2 * j] = ntc[2 * _actualPilotsRelativeIndex[j]];
                _actualPilots[2 * j + 1] = ntc[2 * _actualPilotsRelativeIndex[j] + 1];
            }

            float temp1 = 0.0f, temp2 = 0.0f;
            for (int j = 0; j < cntActual; j++)
            {
                temp1 += _actualPilots[2 * j] * _nextPilots[2 * j] + _actualPilots[2 * j + 1] * _nextPilots[2 * j + 1];
                temp2 += _actualPilots[2 * j] * _nextPilots[2 * j + 1] - _actualPilots[2 * j + 1] * _nextPilots[2 * j];
            }

            // The first symbol of a frame keeps the previous frame's drift (demodulator.cpp:953).
            if (i != 0)
                _deltaFreqOffset = MathF.Atan2(temp2, (float)(temp1 + MinAbsH));

            for (int j = 0; j < lH; j++)
            {
                float re = 0.0f, im = 0.0f;
                for (int k = 0; k < cnt; k++)
                {
                    float w = _wSymbolBlk[(ntwee * MaxNp + k) * MaxLh + j];
                    re += ntc[2 * k] * w;
                    im += ntc[2 * k + 1] * w;
                }

                h[2 * j] = re;
                h[2 * j + 1] = im;
            }

            for (int j = 0; j < cntActual; j++)
            {
                float re = 0.0f, im = 0.0f;
                for (int k = 0; k < cnt; k++)
                {
                    float w = _wPilotsBlk[(ntwee * MaxNp + k) * MaxNextPilots + j];
                    re += ntc[2 * k] * w;
                    im += ntc[2 * k + 1] * w;
                }

                _nextPilots[2 * j] = re;
                _nextPilots[2 * j + 1] = im;
            }

            for (int j = _kMin; j <= _kMax; j++)
            {
                int trx = st.TransmissionFrameBufferWptr + i * kMod + kDc + j;
                int sbi = (i + std) * kMod + kDc + j;
                float hr = h[2 * (j - _kMin)], hi = h[2 * (j - _kMin) + 1];
                float tmp1 = (float)(hr * hr + hi * hi + MinAbsH);
                float tmp2 = hr / tmp1;
                float tmp3 = -hi / tmp1;
                tfb[2 * trx] = sb[2 * sbi] * tmp2 - sb[2 * sbi + 1] * tmp3;
                tfb[2 * trx + 1] = sb[2 * sbi] * tmp3 + sb[2 * sbi + 1] * tmp2;
                ctf[2 * trx] = hr;
                ctf[2 * trx + 1] = hi;
            }

            st.DeltaTimeOffsetInteger = _sync.Demodulate(st.RsBuffer.AsSpan(2 * tSmp), st.Ts, st.Tu, h, lH, _deltaFreqOffset, st.Zi, _symbolTemp);
            _symbolTemp.AsSpan(0, 2 * kMod).CopyTo(sb.AsSpan(st.SymBufWidx * kMod * 2));
            st.SymBufWidx++;
            tSmp += st.Ts + st.DeltaTimeOffsetInteger;
        }

        float freqOffset = st.Zi[4];
        _timeOffsetFractional = st.Zi[5];
        _deltaTimeOffsetI = st.Zi[2];

        _smpRateConvFftPhaseDiff = 4 * (tSmp - st.NSymbolsNeeded * st.Ts);
        _smpRateConvFftPhaseOffset = 4 * _timeOffsetFractional;
        _smpRateConvInOutDelay += _smpRateConvFftPhaseDiff;

        st.FreqOffset = (float)(-freqOffset * 12000.0 / (float)st.Tu / (2.0 * Math.PI));
        st.DeltaFs = (float)(1.0 / (_deltaTimeOffsetI / st.Ts + 1.0) - 1.0);
        st.SamplerateOffset = _deltaTimeOffsetI / st.Ts;

        st.SymBufWidx -= spf;
        Array.Copy(sb, 2 * spf * kMod, sb, 0, 2 * st.SymBufWidx * kMod);
    }

    // demodulator.cpp:1050-1085: MER over the FAC cells, weighted by |H|².
    private static void MeasureFacSnr(DrmDemodState st)
    {
        float sumMerFac = 0.0f, sumWMerFac = 0.0f, sumWeightFac = 0.0f;
        float[] tfb = st.TransmissionFrameBuffer;
        float[] ctf = st.ChannelTransferFunctionBuffer;
        float[] fac = st.FacCellsSequence;
        int lFac = st.LFac;
        double sqrtHalf = Math.Sqrt(0.5);
        for (int i = 0; i < lFac; i++)
        {
            int trx = st.TransmissionFrameBufferWptr + st.FacCellsK[i];
            fac[i * 2] = tfb[2 * trx];
            fac[i * 2 + 1] = tfb[2 * trx + 1];
            float t1 = (float)(MathF.Abs(fac[i * 2]) - sqrtHalf);
            t1 *= t1;
            float t2 = (float)(MathF.Abs(fac[i * 2 + 1]) - sqrtHalf);
            t2 *= t2;
            float noise = t1 + t2;
            t1 = ctf[trx * 2];
            t1 *= t1;
            t2 = ctf[trx * 2 + 1];
            t2 *= t2;
            float weight = t1 + t2;
            sumMerFac += noise;
            sumWMerFac = (float)(sumWMerFac + noise * (weight + 1.0E-10));
            sumWeightFac += weight;
        }

        st.FacAvailable = true;
        float wmerfac = (float)Math.Log(sumWMerFac / (st.MeanEnergyOfUsedCells * (sumWeightFac + lFac * 1.0E-10)));
        wmerfac = (float)(wmerfac / Math.Log(10.0));
        wmerfac = (float)(wmerfac * -10.0);
        st.WMerFac = wmerfac;
    }

    // demodulator.cpp:1090-1130: the SNR resync rule and the rs_buffer consumption.
    private void ApplySnrRule(DrmDemodState st, int tSmp)
    {
        if (st.WMerFac < DrmRxTables.SnrMinDb)
        {
            _snrTimeoutCounter--;
            if (_snrTimeoutCounter <= 0)
            {
                st.DoSynchronize = true;
                _snrTimeoutCounter = DrmRxTables.SnrTimeout;
            }

            st.TransmissionFrameBufferDataValid = 0;
            _facNotValidCounter--;
            if (_facNotValidCounter <= 0)
            {
                st.DoSynchronize = true;
                _facNotValidCounter = DrmRxTables.FacValidCounter;
            }
        }
        else
        {
            _snrTimeoutCounter = DrmRxTables.SnrTimeout;
            if (FacCounterResetsOnGoodFrame)
                _facNotValidCounter = DrmRxTables.FacValidCounter;
            st.TransmissionFrameBufferDataValid = 1;
            st.FacValid = 1;
        }

        if (st.DoSynchronize)
        {
            st.NSamplesNeeded = DrmRxTables.SymbolsModeDetection * DrmRxTables.ModeDetectionStride - st.RsBufWidx;
        }
        else
        {
            st.RsBufWidx -= tSmp;
            Array.Copy(st.RsBuffer, 2 * tSmp, st.RsBuffer, 0, 2 * st.RsBufWidx);
            st.NSamplesNeeded = (st.SymbolsPerFrame + 1) * st.Ts - st.RsBufWidx;
        }

        st.InputSamplesBufferRequest = st.NSamplesNeeded > 0 ? st.NSamplesNeeded : 0;
    }

    /// <summary>Read-only view of the current Wiener tables for golden tests.</summary>
    internal readonly struct WienerTableView(DrmChannelEstimator owner)
    {
        public int Y => owner._y;

        public int GainRefCellsPerFrame => owner._gainRefCellsPerFrame;

        public int GainRefCellsPerYSymbols => owner._gainRefCellsPerYSymbols;

        public ReadOnlySpan<int> GainRefCellsK => owner._gainRefCellsK.AsSpan(0, owner._gainRefCellsPerFrame);

        public ReadOnlySpan<int> GainRefCellsTheta1024 => owner._gainRefCellsTheta1024.AsSpan(0, owner._gainRefCellsPerFrame);

        public ReadOnlySpan<float> GainRefCellsA => owner._gainRefCellsA.AsSpan(0, owner._gainRefCellsPerFrame);

        public int CntTrCells(int nnn) => owner._cntTrCells[nnn];

        public int CntNextPilotCells(int nnn) => owner._cntNextPilotCells[nnn];

        public ReadOnlySpan<int> SubsetIndex(int nnn) => owner._gainRefCellsSubsetIndex.AsSpan(nnn * MaxSubset, owner._cntTrCells[nnn]);

        public ReadOnlySpan<int> TrainingCellsK(int nnn) => owner._trainingCellsK.AsSpan(nnn * MaxGainRefCells, owner._cntTrCells[nnn]);

        public ReadOnlySpan<int> NextPilotCellsK(int nnn) => owner._nextPilotCellsK.AsSpan(nnn * 40, owner._cntNextPilotCells[nnn]);

        public float WSymbol(int nnn, int j, int k) => owner._wSymbolBlk[(nnn * MaxNp + j) * MaxLh + k];

        public float WPilots(int nnn, int j, int k) => owner._wPilotsBlk[(nnn * MaxNp + j) * MaxNextPilots + k];
    }
}
