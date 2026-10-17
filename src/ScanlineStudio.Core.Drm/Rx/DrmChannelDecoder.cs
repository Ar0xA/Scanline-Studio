using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV RX's channel decoding, <c>channel_decoding</c> and <c>getfacchar</c> (drmrx/channeldecode.cpp:79-904;
/// M.Bos PA0MBO 2009, "GNU General Public License", no version stated): per equalized frame, FAC demapping and
/// decoding with its CRC8, superframe alignment from the FAC frame identity, the callsign from three FACs, the MSC
/// demapper/deinterleaver set-up on a parameter change, then MSC multi-stage decoding into the decoded MSC bits.</summary>
/// <remarks>
/// <para><b>Seam to source decoding (P7a).</b> QSSTV calls <c>channel_decoding()</c> and then
/// <c>srcDecoder-&gt;decode()</c> after every equalized frame (demodulator.cpp:158-162). From channel decoding the
/// source decoder reads <see cref="MscDataValid"/> (1 = decoded MSC bits), <see cref="AudioDataFlag"/>,
/// <see cref="ChannelDecodedData"/> of <see cref="LengthDecodedData"/> bits (sourcedecoder.cpp:78-91) and
/// <see cref="CallsignValid"/>/<see cref="Callsign"/> (:411); when it opens a new transport block it also reads
/// <c>robustness_mode</c> (<see cref="DrmDemodState.RobustnessMode"/>), <see cref="InterleaverDepthNew"/>,
/// <see cref="MscModeNew"/>, <see cref="PlPartB"/> and <see cref="SpectrumOccupancyNew"/> (:830-841), and clears
/// <see cref="CallsignValid"/> (:810, <see cref="InvalidateCallsign"/>). <see cref="DrmRxFrameStage"/> hands every
/// frame, with the demodulator state, to an <see cref="IDrmMscSink"/>.</para>
/// <para><b>Interpretation guard (QSSTV undefined behaviour).</b> A CRC-valid FAC can carry msc_mode 2 (bit 6 = 1,
/// bit 4 = 0, bit 9 = 1), for which channel_decoding has no MSC set-up branch (channeldecode.cpp:584-717): QSSTV then
/// runs msdhardmsc with no parameters (first set-up) or with those of a different N, reading past its tables. Here,
/// when no set-up matches the current lMSC, the MSC decode is skipped: <see cref="MscDataValid"/> keeps QSSTV's value,
/// <see cref="LengthDecodedData"/> is 0, so source decoding gets no packet.</para>
/// <para><b>Shared globals</b> (transmission/channel buffers and their write pointer, fac_valid, spectrum_occupancy,
/// FAC_cells_k/lFAC, the mode) live on <see cref="DrmDemodState"/>; QSSTV's other channel-decoding globals and
/// statics are fields here, zero-initialised as C statics are.</para>
/// <para><b>Not ported (write-only in QSSTV):</b> the audio/application/stream/time info structs reset at
/// channeldecode.cpp:190-299 and filled at :559-576 (nothing reads them), the constellation display copy
/// <c>MSC_cells_sequence</c>/<c>MSCAvailable</c> (drmconstellationframe.cpp only, P7b), the unused
/// <c>calc_weighted_variance</c> sums (:797-818) and <c>weighted_noise_power_density</c> (:824-831), and the VSPP
/// branch (:861-875, Lvspp is always 0). The noise-density SNR branch (:771-789) is unreachable because
/// <c>SNR_estimation_valid</c> is cleared just before it (:760); its state (<c>noise_power_density</c>) is still kept.</para>
/// <para><b>QSSTV quirks kept:</b> FAC cell 9 is zeroed before decoding (:356-357); a CRC failure or an occupancy
/// above 1 returns before the frame index and write pointer advance (:379-418), so the next frame overwrites the
/// slot; an interleaver-only change rebuilds the carrier usage from the stale symbol-wise map (:478-507);
/// <c>localDrmCallsign[9]</c> gets its terminator one past its end at identity 2 (:427), given a tenth byte here;
/// in mode E, <c>samples_resorted[288][15]</c> is indexed with 20 columns (:833-845) and aliases into the next row,
/// reproduced on a flat array.</para>
/// <para>Not thread-safe: one instance per receive worker. No allocation per frame, except the <see cref="Callsign"/>
/// string when a callsign completes.</para>
/// </remarks>
internal sealed class DrmChannelDecoder : IDrmMscFrame
{
    private const int MsdIter = 4;
    private const int FacDeinterleaverLength = 90;
    private const double DblEpsilon = 2.220446049250313E-16;
    private const int MaxCells = DrmMscCellMap.MaxCells;

    private readonly DrmMscCellMap _mscMap = new();
    private readonly DrmMultiStageDecoder _msd = new(MaxCells);
    private readonly int[] _facDeinterleaver = new int[FacDeinterleaverLength];
    private readonly int[] _mscDemapperSymbolwise = new int[6 * MaxCells];
    private readonly double[] _snrEstimation = new double[MaxCells];
    private readonly double[] _squaredNoiseSignalBuffer = new double[121490];
    private readonly double[] _noisePowerDensity = new double[461];
    private readonly double[] _l = new double[6];
    private readonly double[] _lFac = new double[4];
    private readonly int[] _pl = new int[6];
    private readonly int[] _plFac = new int[4];
    private readonly double[] _facData = new double[72];
    private readonly double[] _channelParameters = new double[20];
    private readonly int[] _mscCarrierUsage = new int[288];
    private readonly int[] _mscUsedCarriers = new int[288];
    private readonly int[] _deinterleaver = new int[18000];
    private readonly int[] _partDeinterleaver = new int[18000];
    private readonly int[] _ratesA = new int[3];
    private readonly int[] _ratesB = new int[3];
    private readonly double[] _noiseSignal = new double[2 * MaxCells];
    private readonly double[] _squaredNoiseSignal = new double[MaxCells];
    private readonly double[] _channelDecodedData = new double[110000];
    private readonly byte[] _localCallsign = new byte[10];

    // Per-call locals of channel_decoding (channeldecode.cpp:106-143), kept to avoid allocation.
    private readonly double[] _receivedReal = new double[3000];
    private readonly double[] _receivedImag = new double[3000];
    private readonly double[] _snr = new double[3000];
    private readonly float[] _transferFunctionFac = new float[3000];
    private readonly float[] _transferFunctionMsc = new float[2 * MaxCells];
    private readonly double[] _facBlock = new double[72];
    private readonly double[] _sppHard = new double[10628];
    private readonly double[] _samplesResorted = new double[288 * 15];

    private int _frameIndex;
    private int _enoughFrames;
    private int _frameCount;
    private int _mscParametersValid;
    private int _robustnessModeOld;
    private int _mscMode;
    private int _interleaverDepth;
    private int _snrEstimationValid;
    private int _identity;
    private int _identityCount;
    private int _cntMscUsedCarriers;
    private int _minIndexEqualSamples;
    private int _maxIndexEqualSamples;
    private int _n1;
    private int _n2;
    private int _rowdimL;
    private int _setupNMux;
    private int _xin1;
    private int _xin2;

    /// <summary>The <c>RUN_STATE_POWER_ON</c> call (channeldecode.cpp:149-157): the FAC deinterleaver.
    /// The INIT fields are set too; <see cref="Reset"/> also sets the shared globals.</summary>
    public DrmChannelDecoder()
    {
        DrmDeinterleaver.Build(0, 1, FacDeinterleaverLength, 21, _facDeinterleaver);
        ResetFields();
    }

    /// <summary>Global <c>lMSC</c>: MSC cells per frame of the current map.</summary>
    public int LMsc { get; private set; }

    /// <summary>Global <c>spectrum_occupancy_new</c>: the FAC's occupancy of the last CRC-valid FAC.</summary>
    public int SpectrumOccupancyNew { get; private set; }

    /// <summary>Global <c>msc_mode_new</c>: 0 = 64-QAM, 1 = 16-QAM, 3 = 4-QAM.</summary>
    public int MscModeNew { get; private set; }

    /// <summary>Global <c>interleaver_depth_new</c>: 0 = long, 1 = short.</summary>
    public int InterleaverDepthNew { get; private set; }

    /// <summary><c>multiplex_description.PL_PartA</c>; −1 after a frame without data-valid.</summary>
    public int PlPartA { get; private set; }

    /// <summary><c>multiplex_description.PL_PartB</c>; −1 after a frame without data-valid.</summary>
    public int PlPartB { get; private set; }

    /// <summary><c>multiplex_description.HM_length</c>.</summary>
    public int HmLength { get; private set; }

    /// <summary><c>multiplex_description.PL_HM</c>.</summary>
    public int PlHm { get; private set; }

    /// <summary>Global <c>audio_data_flag</c> (FAC bit 6): 1 = data service.</summary>
    public int AudioDataFlag { get; private set; }

    /// <summary>Global <c>callsignValid</c>: set when three FACs (identities 0, 1, 2) have delivered the callsign.</summary>
    public bool CallsignValid { get; private set; }

    /// <summary>Global <c>drmCallsign</c>: the last completed callsign.</summary>
    public string Callsign { get; private set; } = string.Empty;

    /// <summary>Global <c>channel_decoded_data_buffer_data_valid</c>: 0 = none, 1 = decoded MSC bits,
    /// 2 = decoded while the deinterleaver is still filling (not used by source decoding).</summary>
    public int MscDataValid { get; private set; }

    /// <summary>Global <c>length_decoded_data</c>: MSC bits of the last decode.</summary>
    public int LengthDecodedData { get; private set; }

    /// <summary>Global <c>channel_decoded_data_buffer</c>: the decoded MSC bits (0.0 or 1.0), <see cref="LengthDecodedData"/> long.</summary>
    public ReadOnlySpan<double> ChannelDecodedData => _channelDecodedData.AsSpan(0, LengthDecodedData);

    /// <summary>The MSD refinement passes of the last MSC decode (<c>res_iters</c>).</summary>
    public int MscIterations => _msd.Iterations;

    /// <summary>Global <c>MSC_Demapper[6][2959]</c>, row-major, for golden tests.</summary>
    internal ReadOnlySpan<int> MscDemapper => _mscMap.Demapper;

    /// <summary>The FAC frame identity of the last CRC-valid FAC (0..2).</summary>
    public int Identity => _identity;

    /// <summary>sourcedecoder.cpp:810: source decoding clears <c>callsignValid</c> when it creates a transport block.</summary>
    public void InvalidateCallsign() => CallsignValid = false;

    /// <summary>The <c>RUN_STATE_INIT</c> call (channeldecode.cpp:158-173), including its two shared globals (write
    /// pointer 0, fac_valid −1). drmRx::init makes it after <c>demodulator::init</c> (drmrx.cpp:26-33).</summary>
    public void Reset(DrmDemodState st)
    {
        ResetFields();
        st.TransmissionFrameBufferWptr = 0;
        st.FacValid = -1;
    }

    private void ResetFields()
    {
        _frameIndex = 1;
        _enoughFrames = 0;
        _frameCount = 0;
        _mscParametersValid = 0;
        _robustnessModeOld = -1;
        _mscMode = -1;
        _interleaverDepth = -1;
        HmLength = 0;
        CallsignValid = false;
    }

    /// <summary><c>channel_decoding()</c> in normal run state (channeldecode.cpp:174-886), after a frame was equalized.</summary>
    public void Decode(DrmDemodState st)
    {
        MscDataValid = 0;
        if (st.TransmissionFrameBufferDataValid == 0)
        {
            ClearOnInvalidFrame(st);
            return;
        }

        int robustnessMode = st.RobustnessMode;
        int symbolPeriod = DrmRxTables.Tu[robustnessMode];
        int symbolsPerFrame = st.SymbolsPerFrame;
        int frameCells = symbolPeriod * symbolsPerFrame;
        float[] tfb = st.TransmissionFrameBuffer;
        float[] ctf = st.ChannelTransferFunctionBuffer;
        bool mscParametersChanged = false;

        if (robustnessMode != _robustnessModeOld)
        {
            if (robustnessMode < 0)
                return;
            st.LFac = DrmFacCellMap.Build(robustnessMode, st.KDc, st.KModulo, st.FacCellsK);
        }

        // FAC decoding (channeldecode.cpp:341-386).
        st.FacValid = 1;
        int lFac = st.LFac;
        for (int i = 0; i < lFac; i++)
        {
            int ptr = (_frameIndex - 1) * frameCells + st.FacCellsK[i];
            _receivedReal[i] = tfb[2 * ptr];
            _receivedImag[i] = tfb[2 * ptr + 1];
            float hr = ctf[2 * ptr];
            float hi = ctf[2 * ptr + 1];
            _transferFunctionFac[i * 2] = hr;
            _transferFunctionFac[i * 2 + 1] = hi;
            _snr[i] = Math.Sqrt(hr * hr + hi * hi);
        }

        _receivedReal[9] = 0.0;
        _receivedImag[9] = 0.0;
        _lFac[0] = 0.0;
        _lFac[1] = 48.0;
        _plFac[0] = 0;
        _plFac[1] = 6;
        _msd.DecodeFac(_receivedReal, _receivedImag, lFac, _snr, _lFac, _facDeinterleaver, _plFac, 4, false, _facData);
        for (int i = 0; i < 40; i++)
            _facBlock[i] = _facData[i];
        for (int i = 40; i < 48; i++)
            _facBlock[i] = 1.0 - _facData[i];
        for (int i = 0; i < 10; i++)
            _channelParameters[i] = _facData[i];

        double checksum = RxCrc.Crc8Bits(_facBlock.AsSpan(0, 48));
        if (Math.Abs(checksum) > DblEpsilon)
        {
            st.SpectrumOccupancy = -1;
            st.FacValid = 0;
            _identityCount = 0;
            _mscParametersValid = 0;
            return;
        }

        _mscParametersValid = 1;

        // Frame alignment (channeldecode.cpp:388-404): move the frame to the slot of its FAC identity.
        double temp = 2.0 * _channelParameters[0] + _channelParameters[1];
        _identity = (int)temp % 3;
        if (_identity != (_frameIndex - 1) % 3)
        {
            int oldPtr = st.TransmissionFrameBufferWptr;
            st.TransmissionFrameBufferWptr = _identity * frameCells;
            int w = st.TransmissionFrameBufferWptr;
            for (int i = 0; i < frameCells; i++)
            {
                tfb[2 * (w + i)] = tfb[2 * (oldPtr + i)];
                tfb[2 * (w + i) + 1] = tfb[2 * (oldPtr + i) + 1];
                ctf[2 * (w + i)] = ctf[2 * (oldPtr + i)];
                ctf[2 * (w + i) + 1] = ctf[2 * (oldPtr + i) + 1];
            }

            _frameIndex = _identity + 1;
        }

        InterleaverDepthNew = (int)_channelParameters[3];
        MscModeNew = (int)_channelParameters[4];
        if (Math.Abs(_channelParameters[6] - 1.0) < DblEpsilon)
            MscModeNew += 2 * (int)_channelParameters[9];
        SpectrumOccupancyNew = (int)_channelParameters[2];
        if (SpectrumOccupancyNew > 1)
        {
            st.SpectrumOccupancy = -1;
            st.FacValid = 0;
            _identityCount = 0;
            return;
        }

        _identityCount++;
        AudioDataFlag = (int)_channelParameters[6];

        // The callsign, 3 characters per FAC (channeldecode.cpp:423-433).
        _localCallsign[3 * _identity] = GetFacChar(_facBlock.AsSpan(10, 7));
        _localCallsign[3 * _identity + 1] = GetFacChar(_facBlock.AsSpan(17, 7));
        _localCallsign[3 * _identity + 2] = GetFacChar(_facBlock.AsSpan(24, 7));
        _localCallsign[3 * _identity + 3] = 0;
        if (_identity == 2 && _identityCount >= 3)
        {
            _identityCount = 0;
            Callsign = CString(_localCallsign);
            CallsignValid = true;
        }

        // MSC map on an occupancy, mode or interleaver change (channeldecode.cpp:435-512).
        if (st.SpectrumOccupancy != SpectrumOccupancyNew || _robustnessModeOld != robustnessMode)
        {
            st.SpectrumOccupancy = SpectrumOccupancyNew;
            _interleaverDepth = InterleaverDepthNew;
            LMsc = _mscMap.Build(robustnessMode, st.SpectrumOccupancy, _interleaverDepth, st.KDc, st.KModulo, out int lf);
            st.LFac = lf;
            int[] dm = _mscMap.Demapper;
            for (int j = 0; j < 6; j++)
            {
                for (int i = 0; i < LMsc; i++)
                    _mscDemapperSymbolwise[j * MaxCells + i] = dm[j * MaxCells + i] % symbolPeriod;
            }

            CarrierUsage(symbolPeriod);
            mscParametersChanged = true;
        }
        else if (_interleaverDepth != InterleaverDepthNew)
        {
            _interleaverDepth = InterleaverDepthNew;
            LMsc = _mscMap.Build(robustnessMode, st.SpectrumOccupancy, _interleaverDepth, st.KDc, st.KModulo, out int lf);
            st.LFac = lf;
            CarrierUsage(symbolPeriod);
            mscParametersChanged = true;
        }

        _robustnessModeOld = robustnessMode;
        _interleaverDepth = InterleaverDepthNew;

        // Deinterleaving needs 2 frames (short) or 6 (long) (channeldecode.cpp:514-539).
        _frameCount++;
        int wptr = st.TransmissionFrameBufferWptr;
        if (_frameCount >= 6 - 4 * _interleaverDepth)
        {
            _enoughFrames = 1;
        }
        else if (_frameCount == 1)
        {
            _minIndexEqualSamples = wptr;
            _maxIndexEqualSamples = wptr + frameCells;
        }
        else
        {
            if (wptr < _minIndexEqualSamples)
                _minIndexEqualSamples = wptr;
            if (wptr + frameCells > _maxIndexEqualSamples)
                _maxIndexEqualSamples = wptr + frameCells;
        }

        if (_mscMode != MscModeNew)
        {
            _mscMode = MscModeNew;
            mscParametersChanged = true;
            _mscParametersValid = 1;
        }

        HmLength = 0;
        PlHm = 0;
        if (Math.Abs(_facData[5] - 1.0) < DblEpsilon)
        {
            PlPartA = 1;
            PlPartB = 1;
        }
        else
        {
            PlPartA = 0;
            PlPartB = 0;
        }

        int nMux = LMsc;
        if (mscParametersChanged && _mscParametersValid == 1)
            SetUpMsc(nMux, robustnessMode, st.SpectrumOccupancy);

        if (_mscParametersValid != 0)
            DecodeMsc(st, symbolPeriod, symbolsPerFrame);

        _frameIndex = (_frameIndex % 6) + 1;
        st.TransmissionFrameBufferWptr = (st.TransmissionFrameBufferWptr + frameCells) % (frameCells * 6);
    }

    // channeldecode.cpp:176-311: a frame without data-valid restarts superframe and deinterleaver state.
    private void ClearOnInvalidFrame(DrmDemodState st)
    {
        _frameIndex = 1;
        _enoughFrames = 0;
        _frameCount = 0;
        _mscParametersValid = 0;
        st.TransmissionFrameBufferWptr = 0;
        PlPartA = -1;
        PlPartB = -1;
        HmLength = 0;
        PlHm = -1;
        _mscMode = -1;
        _robustnessModeOld = -1;
        _interleaverDepth = -1;

        // channeldecode.cpp:304-305 clears only the first 41490 of 121490 entries.
        Array.Clear(_squaredNoiseSignalBuffer, 0, 41490);
        Array.Clear(_noisePowerDensity);
        _snrEstimationValid = 0;
        st.FacValid = -1;
    }

    // channeldecode.cpp:448-470: MSC cells per carrier over all 6 slots, from the symbol-wise map.
    private void CarrierUsage(int symbolPeriod)
    {
        Array.Clear(_mscCarrierUsage, 0, symbolPeriod);
        for (int j = 0; j < 6; j++)
        {
            for (int k = 0; k < LMsc; k++)
            {
                int c = _mscDemapperSymbolwise[j * MaxCells + k];
                if ((uint)c < (uint)symbolPeriod)
                    _mscCarrierUsage[c]++;
            }
        }

        _cntMscUsedCarriers = 0;
        for (int i = 0; i < symbolPeriod; i++)
        {
            if (_mscCarrierUsage[i] != 0)
                _mscUsedCarriers[_cntMscUsedCarriers++] = i;
        }
    }

    // channeldecode.cpp:578-717: code rates, bit counts and the level deinterleavers per QAM.
    private void SetUpMsc(int nMux, int robustnessMode, int spectrumOccupancy)
    {
        ReadOnlySpan<double> rx = DrmMsdTables.Rx;
        ReadOnlySpan<double> ry = DrmMsdTables.Ry;
        if (_mscMode == 0)
        {
            ReadOnlySpan<int> rates = DrmMsdTables.RatesSm64;
            for (int i = 0; i < 3; i++)
                _ratesA[i] = rates[PlPartA * 3 + i] - 1;
            _n1 = 0;
            _n2 = nMux - _n1;
            for (int i = 0; i < 3; i++)
                _ratesB[i] = rates[PlPartB * 3 + i] - 1;
            for (int i = 0; i < 3; i++)
            {
                _l[i] = 2 * _n1 * (rx[_ratesA[i]] / ry[_ratesA[i]]);
                _l[i + 3] = rx[_ratesB[i]] * Math.Floor((2 * _n2 - 12) / ry[_ratesB[i]]);
            }

            _rowdimL = 3;
            _setupNMux = nMux;
            _xin1 = 2 * _n1;
            _xin2 = 2 * _n2;
            int len = _xin1 + _xin2;
            for (int i = 0; i < len; i++)
                _deinterleaver[i] = i;
            DrmDeinterleaver.Build(_xin1, 13, _xin2, 13, _partDeinterleaver);
            _partDeinterleaver.AsSpan(0, len).CopyTo(_deinterleaver.AsSpan(len));
            DrmDeinterleaver.Build(_xin1, 21, _xin2, 21, _partDeinterleaver);
            _partDeinterleaver.AsSpan(0, len).CopyTo(_deinterleaver.AsSpan(2 * len));
            for (int i = 0; i < 3; i++)
            {
                _pl[i] = _ratesA[i];
                _pl[i + 3] = _ratesB[i];
            }
        }
        else if (_mscMode == 1)
        {
            ReadOnlySpan<int> rates = DrmMsdTables.RatesSm16;
            for (int i = 0; i < 2; i++)
                _ratesA[i] = rates[PlPartA * 2 + i] - 1;
            _n1 = 0;
            _n2 = nMux - _n1;
            for (int i = 0; i < 2; i++)
                _ratesB[i] = rates[PlPartB * 2 + i] - 1;
            for (int i = 0; i < 2; i++)
            {
                _l[i] = 2 * _n1 * (rx[_ratesA[i]] / ry[_ratesA[i]]);
                _l[i + 2] = rx[_ratesB[i]] * Math.Floor((2 * _n2 - 12) / ry[_ratesB[i]]);
            }

            _rowdimL = 2;
            _setupNMux = nMux;
            _xin1 = 2 * _n1;
            _xin2 = 2 * _n2;
            int len = _xin1 + _xin2;
            DrmDeinterleaver.Build(_xin1, 13, _xin2, 13, _partDeinterleaver);
            _partDeinterleaver.AsSpan(0, len).CopyTo(_deinterleaver);
            DrmDeinterleaver.Build(_xin1, 21, _xin2, 21, _partDeinterleaver);
            _partDeinterleaver.AsSpan(0, len).CopyTo(_deinterleaver.AsSpan(len));
            for (int i = 0; i < 2; i++)
            {
                _pl[i] = _ratesA[i];
                _pl[i + 2] = _ratesB[i];
            }
        }
        else if (_mscMode == 3)
        {
            // 4-QAM: fixed bit counts per mode/occupancy and rate index 6 (channeldecode.cpp:658-716); protection is ignored.
            _ratesA[0] = (int)rx[PlPartA] - 1;
            _n1 = 0;
            _n2 = nMux - _n1;
            _ratesB[0] = (int)rx[PlPartB] - 1;
            if (robustnessMode == 0)
            {
                _l[0] = 0;
                _l[1] = spectrumOccupancy == 0 ? 768 : 837;
            }

            if (robustnessMode == 1)
            {
                _l[0] = 0;
                _l[1] = spectrumOccupancy == 0 ? 537 : 627;
            }

            if (robustnessMode == 2)
            {
                _l[0] = 0;
                _l[1] = spectrumOccupancy == 0 ? 399 : 435;
            }

            _xin1 = 2 * _n1;
            _xin2 = 2 * _n2;
            DrmDeinterleaver.Build(_xin1, 21, _xin2, 21, _partDeinterleaver);
            _partDeinterleaver.AsSpan(0, _xin1 + _xin2).CopyTo(_deinterleaver);
            _pl[0] = 0;
            _pl[1] = 6;
            _rowdimL = 1;
            _setupNMux = nMux;
        }
    }

    // channeldecode.cpp:719-882: MSC demapping, SNR weights, multi-stage decoding and the noise bookkeeping.
    private void DecodeMsc(DrmDemodState st, int symbolPeriod, int symbolsPerFrame)
    {
        float[] tfb = st.TransmissionFrameBuffer;
        float[] ctf = st.ChannelTransferFunctionBuffer;
        int lMsc = LMsc;
        int slot = (_frameIndex - 1) * MaxCells;
        int[] dm = _mscMap.Demapper;
        float[] tf = _transferFunctionMsc;
        for (int i = 0; i < lMsc; i++)
        {
            int ptr = dm[slot + i];
            _receivedReal[i] = tfb[2 * ptr];
            _receivedImag[i] = tfb[2 * ptr + 1];
            tf[i * 2] = ctf[2 * ptr];
            tf[i * 2 + 1] = ctf[2 * ptr + 1];
        }

        // No MSC set-up for this N (msc_mode 2, see remarks): QSSTV's decode here is undefined, so no bits come out.
        if (_rowdimL == 0 || _setupNMux != lMsc)
        {
            MscDataValid = _enoughFrames == 0 ? 2 : 1;
            LengthDecodedData = 0;
            return;
        }

        int nSpp;
        if (_enoughFrames == 0)
        {
            // Cells outside the frames received so far get zero weight (channeldecode.cpp:732-755).
            for (int i = 0; i < lMsc; i++)
            {
                int ptr = dm[slot + i];
                if (ptr > _maxIndexEqualSamples || ptr < _minIndexEqualSamples)
                {
                    tf[2 * i] = 0.0f;
                    tf[2 * i + 1] = 0.0f;
                }

                _snrEstimation[i] = Math.Sqrt(tf[2 * i] * tf[i * 2] + tf[i * 2 + 1] * tf[i * 2 + 1]);
            }

            nSpp = _msd.DecodeMsc(_receivedReal, _receivedImag, lMsc, _snrEstimation, _l, _rowdimL, _deinterleaver, _pl, MsdIter, true,
                _sppHard, _noiseSignal);
            MscDataValid = 2;
            LengthDecodedData = nSpp;
        }
        else
        {
            // SNR_estimation_valid is cleared first (channeldecode.cpp:760), so the weight is always |H|.
            _snrEstimationValid = 0;
            for (int i = 0; i < lMsc; i++)
                _snrEstimation[i] = Math.Sqrt(tf[2 * i] * tf[i * 2] + tf[i * 2 + 1] * tf[i * 2 + 1]);

            nSpp = _msd.DecodeMsc(_receivedReal, _receivedImag, lMsc, _snrEstimation, _l, _rowdimL, _deinterleaver, _pl, MsdIter, true,
                _sppHard, _noiseSignal);
            LengthDecodedData = nSpp;
            for (int i = 0; i < lMsc; i++)
            {
                _squaredNoiseSignal[i] = _noiseSignal[2 * i] * _noiseSignal[2 * i] + _noiseSignal[2 * i + 1] * _noiseSignal[2 * i + 1];
                _squaredNoiseSignalBuffer[dm[slot + i]] = _squaredNoiseSignal[i];
            }

            UpdateNoisePowerDensity(dm, slot, lMsc, symbolPeriod, symbolsPerFrame);
            if (_snrEstimationValid < 1)
                _snrEstimationValid++;
            MscDataValid = 1;
        }

        _sppHard.AsSpan(0, nSpp).CopyTo(_channelDecodedData);
    }

    // channeldecode.cpp:833-854: per-carrier noise power, smoothed 0.8/0.2 (read only by the unreachable branch).
    private void UpdateNoisePowerDensity(int[] dm, int slot, int lMsc, int symbolPeriod, int symbolsPerFrame)
    {
        double[] rs = _samplesResorted;
        float[] tf = _transferFunctionMsc;
        for (int i = 0; i < symbolPeriod; i++)
        {
            for (int j = 0; j < symbolsPerFrame; j++)
                rs[i * 15 + j] = 0.0;
        }

        for (int i = 0; i < lMsc; i++)
        {
            int totindex = dm[slot + i] % (symbolPeriod * symbolsPerFrame);
            int posrow = totindex % symbolPeriod;
            int poscolumn = totindex / symbolPeriod;
            rs[posrow * 15 + poscolumn] = _squaredNoiseSignal[i] * (tf[i * 2] * tf[i * 2] + tf[i * 2 + 1] * tf[i * 2 + 1]);
        }

        for (int i = 0; i < _cntMscUsedCarriers; i++)
        {
            int c = _mscUsedCarriers[i];
            float sum1 = 0.0f;
            for (int j = 0; j < symbolsPerFrame; j++)
                sum1 = (float)(sum1 + rs[c * 15 + j]);
            _noisePowerDensity[c] = _noisePowerDensity[c] * (1.0 - 0.2) + 0.2 * sum1 / _mscCarrierUsage[c];
        }
    }

    // getfacchar (channeldecode.cpp:889-904): 7 bits MSB first, each compared exactly with 1.0.
    private static byte GetFacChar(ReadOnlySpan<double> facdata)
    {
        int karakter = 0;
        int macht = 64;
        for (int i = 0; i < 7; i++)
        {
            if (facdata[i] == 1.0)
                karakter += macht;
            macht /= 2;
        }

        return (byte)karakter;
    }

    // drmCallsign = localDrmCallsign: QString(const char*) up to the first NUL; FAC characters are 7-bit, so UTF-8 is ASCII.
    private static string CString(ReadOnlySpan<byte> s)
    {
        int n = s.IndexOf((byte)0);
        if (n < 0)
            n = s.Length;
        Span<char> chars = stackalloc char[n];
        for (int i = 0; i < n; i++)
            chars[i] = (char)s[i];
        return new string(chars);
    }
}
