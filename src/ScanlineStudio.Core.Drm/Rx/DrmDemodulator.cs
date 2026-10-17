namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's DRM receiver per-stripe path up to frame sync: <c>drmRx::run</c> (Hilbert, the empty
/// pre-call) and <c>demodulator::demodulate</c> with <c>init</c>, <c>timeSync</c>, <c>frequencySync</c> and
/// <c>frameSync</c> (drmrx/drmrx.cpp:20-74, drmrx/demodulator.cpp:40-520). Everything after frame sync is
/// the <see cref="IDrmFrameStage"/> (channelEstimation, channel decoding: <see cref="DrmRxFrameStage"/>).</summary>
/// <remarks>
/// <para><b>Stages.</b> timeSync: wait for 20×320 samples, <see cref="DrmModeDetector"/> fixes the mode,
/// fractional time/frequency offset and sample-rate offset, then the buffer is symbol-aligned.
/// frequencySync: 15 acquisition symbols (<see cref="DrmOfdmSymbolDemodulator"/>), the integer frequency
/// offset (<see cref="DrmPilotSearch.IntegerFrequencyOffset"/>) and the spectrum-occupancy estimate.
/// frameSync: one frame of symbols, the frame start (<see cref="DrmPilotSearch.FrameStartSymbol"/>), frame
/// alignment, and the first 2-D window of symbols for the channel estimator.</para>
/// <para><b>Not ported:</b> the PSD display (<c>psdmean</c> on every call, demodulator.cpp:135-142, and its
/// <c>rfft</c> from newfft.cpp) only feeds <c>drmpsdframe.cpp</c> and reads <c>rs_buffer</c> without writing
/// it, so it cannot change any decision. The avgSNR display and GUI events in drmRx::run (drmrx.cpp:48-58,
/// 69-73) are P7b.</para>
/// <para><b>Undefined behaviour kept defined:</b> if getmode's time offset rounded below zero, QSSTV's
/// symbol-align loop (demodulator.cpp:228-233) would read before <c>rs_buffer</c>; here those reads are 0. It
/// cannot trigger on finite input: with b[j] in [0, Ts) the least-squares intercept is boffs ≥ −93.8 (A/B) or
/// −70.3 (E), so the fmodf dividend (getmode.cpp:308) is ≥ 65.2 or 48.7. The guard only keeps C# in bounds.</para>
/// <para>Not thread-safe: one instance per receive worker. No allocation per stripe.</para>
/// </remarks>
internal sealed class DrmDemodulator
{
    /// <summary>RXSTRIPE (appdefs.h:13).</summary>
    public const int StripeSize = 1024;

    private const int ModeDetectionSamples = DrmRxTables.SymbolsModeDetection * DrmRxTables.ModeDetectionStride;

    private readonly IDrmFrameStage _frameStage;
    private readonly DrmHilbertFilter _iqFilter = new();
    private readonly DrmModeDetector _modeDetector = new(ModeDetectionSamples);
    private readonly DrmOfdmSymbolDemodulator _ofdm = new();
    private readonly float[] _resampSignal = new float[2 * StripeSize];
    private readonly float[] _symbolTemp = new float[2 * DrmRxTables.TuMax];

    /// <summary>The receiver with QSSTV's channel estimator and channel decoder as the frame stage.</summary>
    public DrmDemodulator()
        : this(new DrmRxFrameStage(new DrmChannelEstimator(), new DrmChannelDecoder()))
    {
    }

    public DrmDemodulator(IDrmFrameStage frameStage)
    {
        ArgumentNullException.ThrowIfNull(frameStage);
        _frameStage = frameStage;
        Reset();
    }

    public DrmDemodState State { get; } = new();

    /// <summary><c>drmRx::init</c> up to the source decoder (drmrx.cpp:20-35): <c>demodulator::init</c>, the
    /// sample-rate estimate, then the frame stage's channel-decoding reset. The Hilbert history is not touched, as in QSSTV.</summary>
    public void Reset()
    {
        Init();
        State.SamplerateOffsetEstimation = 0.0f;
        _frameStage.ResetReceiver(State);
    }

    /// <summary><c>drmRx::run</c> (drmrx.cpp:41-74) on one 1024-sample 12 kHz stripe at int16 scale.</summary>
    public void RunStripe(ReadOnlySpan<double> stripe)
    {
        if (stripe.Length != StripeSize)
            throw new ArgumentException($"A stripe is {StripeSize} samples.", nameof(stripe));
        if (State.InputSamplesBufferRequest == 0)
            Demodulate(_resampSignal, 0);
        _iqFilter.Process(stripe, _resampSignal);
        Demodulate(_resampSignal, StripeSize);
    }

    /// <summary><c>demodulator::demodulate</c> (demodulator.cpp:87-170). Returns false until frame sync holds.</summary>
    public bool Demodulate(ReadOnlySpan<float> sigin, int numSamples)
    {
        DrmDemodState st = State;
        st.TransmissionFrameBufferDataValid = 0;
        _frameStage.BeginDemodulate();
        if (st.DoSynchronize)
        {
            st.DoSynchronize = false;
            Init();
        }

        if (numSamples > 0)
        {
            ++st.IterationCounter;
            if ((st.RsBufWidx + numSamples) * 2 > st.RsBuffer.Length)
                throw new InvalidOperationException("rs_buffer overflow: the frame stage is not consuming samples.");
            sigin[..(2 * numSamples)].CopyTo(st.RsBuffer.AsSpan(2 * st.RsBufWidx));
            st.RsBufWidx += numSamples;
        }

        if (!TimeSync())
            return false;
        if (!FrequencySync())
            return false;
        if (!FrameSync())
            return false;

        // The frame stage also runs channel_decoding() and srcDecoder->decode() (demodulator.cpp:158-162).
        _frameStage.ProcessFrame(st);
        return true;
    }

    // demodulator::init (demodulator.cpp:40-85); the estimator's own fields go to IDrmFrameStage.Reset.
    private void Init()
    {
        DrmDemodState st = State;
        st.SymbolCounter = 0;
        st.NSamplesNeeded = ModeDetectionSamples;
        st.InputSamplesBufferRequest = st.NSamplesNeeded;
        st.TimeSyncFlag = false;
        st.FrequencySyncFlag = false;
        st.FrameSyncFlag = false;
        st.DoSynchronize = false;
        st.RsBufWidx = 0;
        st.SymBufWidx = 0;
        st.IterationCounter = 0;
        st.SamplerateOffsetEstimation = 0;
        st.SamplerateOffset = 0;
        st.Counter = 0;
        st.FacAvailable = false;
        st.TransmissionFrameBufferDataValid = 0;
        _frameStage.Reset();
    }

    // demodulator::timeSync (demodulator.cpp:172-265).
    private bool TimeSync()
    {
        DrmDemodState st = State;
        st.SamplerateOffset = st.SamplerateOffsetEstimation;
        if (st.TimeSyncFlag)
            return true;

        st.FrequencySyncFlag = false;
        st.NSamplesNeeded = ModeDetectionSamples - st.RsBufWidx;
        if (st.NSamplesNeeded > 0)
        {
            st.InputSamplesBufferRequest = st.NSamplesNeeded;
            return false;
        }

        st.SpectrumOccupancy = -1;
        DrmModeEstimate mb = _modeDetector.Detect(st.RsBuffer, ModeDetectionSamples);
        st.RobustnessMode = mb.ModeIndex;
        st.TimeOffset = mb.TimeOffset;
        st.SamplerateOffsetEstimation = mb.SampleRateOffset;
        st.FrequencyOffsetFractionalInit = mb.FrequencyOffsetFractional;
        st.TimeOffsetInteger = (int)Math.Floor(st.TimeOffset + 0.5);

        if (MathF.Abs(st.SamplerateOffsetEstimation) > 200.0E-5 && st.RobustnessMode != DrmRxTables.NoMode)
        {
            st.NSamplesNeeded = ModeDetectionSamples;
            st.InputSamplesBufferRequest = st.NSamplesNeeded;
            st.RsBufWidx = 0;
            return false;
        }

        if (st.RobustnessMode != DrmRxTables.NoMode)
        {
            int m = st.RobustnessMode;
            st.TimeSyncFlag = true;
            st.Ts = DrmRxTables.Ts[m];
            st.Tu = DrmRxTables.Tu[m];
            st.Tg = DrmRxTables.Tg[m];
            st.Tgh = (int)Math.Floor(st.Tg / 2 + 0.5);
            st.SymbolsPerFrame = DrmRxTables.SymbolsPerFrame[m];
            st.KDc = st.Tu / 2;
            st.KModulo = st.Tu;
            DrmRxTables.TimeRefCellsK.Slice(m * DrmRxTables.TimeRefCellColumns, DrmRxTables.TimeRefCellColumns).CopyTo(st.TimeRefCellsK);
            DrmRxTables.TimeRefCellsTheta1024.Slice(m * DrmRxTables.TimeRefCellColumns, DrmRxTables.TimeRefCellColumns).CopyTo(st.TimeRefCellsTheta1024);
            st.Y = DrmRxTables.Y[m];
            st.SymbolsPer2DWindow = DrmRxTables.SymbolsPer2DWindow[m];
            st.SymbolsToDelay = DrmRxTables.SymbolsToDelay[m];

            // Element-wise forward copy as QSSTV; a negative offset smears instead of shifting (see remarks).
            int toi = st.TimeOffsetInteger;
            float[] rs = st.RsBuffer;
            st.RsBufWidx -= toi;
            for (int i = 0; i < st.RsBufWidx; i++)
            {
                int src = i + toi;
                rs[i * 2] = src >= 0 ? rs[src * 2] : 0.0f;
                rs[i * 2 + 1] = src >= 0 ? rs[src * 2 + 1] : 0.0f;
            }

            st.Counter++;
            _ofdm.SetMode(st.Tu);
            return true;
        }

        st.SamplerateOffsetEstimation = 0.0f;
        const int shift = ModeDetectionSamples;
        Array.Copy(st.RsBuffer, shift * 2, st.RsBuffer, 0, 2 * (st.RsBufWidx - shift));
        st.RsBufWidx -= shift;
        st.NSamplesNeeded = ModeDetectionSamples - st.RsBufWidx;
        st.InputSamplesBufferRequest = st.NSamplesNeeded > 0 ? st.NSamplesNeeded : 0;
        return false;
    }

    // demodulator::frequencySync (demodulator.cpp:269-419).
    private bool FrequencySync()
    {
        DrmDemodState st = State;
        if (st.FrequencySyncFlag)
            return true;

        st.FrameSyncFlag = false;
        st.NSamplesNeeded = (DrmRxTables.SymbolsFrequencyPilotSearch + 1) * st.Ts - st.RsBufWidx;
        if (st.NSamplesNeeded > 0)
        {
            st.InputSamplesBufferRequest = st.NSamplesNeeded;
            return false;
        }

        float[] zi = st.Zi;
        zi[0] = -1.0f;
        st.DeltaTimeOffsetInteger = 0;
        st.FreqOffsetInit = st.FrequencyOffsetFractionalInit;
        st.TimeOffsetFractionalInit = st.TimeOffset - st.TimeOffsetInteger;
        st.DeltaTimeOffsetIInit = st.SamplerateOffsetEstimation * st.Ts;

        int tSmp = 0;
        for (int i = 0; i < DrmRxTables.SymbolsFrequencyPilotSearch; i++)
        {
            st.DeltaTimeOffsetInteger = DemodulateSymbolInto(tSmp, i);
            tSmp = tSmp + st.Ts + st.DeltaTimeOffsetInteger;
        }

        st.FreqOffsetInteger = DrmPilotSearch.IntegerFrequencyOffset(st.SymbolBuffer, DrmRxTables.SymbolsFrequencyPilotSearch, st.KDc, st.KModulo, st.Tu);
        st.DeltaTimeOffsetInteger = 0;
        st.FreqOffsetInit = -st.FreqOffsetInteger + st.FrequencyOffsetFractionalInit;
        st.TimeOffsetFractionalInit = 0.0f;
        zi[0] = -1.0f;

        Span<float> spectrumOccupancyIndicator = stackalloc float[6];
        int tu = st.Tu;
        float[] sb = st.SymbolBuffer;
        for (int spIdx = 0; spIdx < 2; spIdx++)
        {
            int kMin = DrmRxTables.KMin[spIdx + st.RobustnessMode * 6];
            int kMax = DrmRxTables.KMax[spIdx + st.RobustnessMode * 6];
            if (kMin != kMax)
            {
                int kDcIndx = (int)Math.Floor(st.FreqOffsetInteger / (2.0 * Math.PI) + 0.5) + tu / 2;
                kDcIndx %= tu;
                int kMinIndx = (kDcIndx + kMin + tu) % tu;
                int kMinMinus4Indx = (kMinIndx - 4 + tu) % tu;
                int kMaxIndx = (kDcIndx + kMax + tu) % tu;
                int kMaxPlus1Indx = (kMaxIndx + 1 + tu) % tu;

                float tmp3 = 0.0f, tmp4 = 0.0f, tmp5 = 0.0f, tmp6 = 0.0f;
                for (int i = 0; i < DrmRxTables.SymbolsFrequencyPilotSearch; i++)
                {
                    tmp3 += Energy(sb, kMaxIndx, i, st.KModulo);
                    tmp4 += Energy(sb, kMaxPlus1Indx, i, st.KModulo);
                    tmp5 += Energy(sb, kMinIndx, i, st.KModulo);
                    tmp6 += Energy(sb, kMinMinus4Indx, i, st.KModulo);
                }

                float energyRatioKMaxToKMaxP1 = tmp3 / tmp4;
                float energyRatioKMinToKMinM4 = tmp5 / tmp6;
                spectrumOccupancyIndicator[spIdx] = energyRatioKMinToKMinM4 + energyRatioKMaxToKMaxP1;
            }
            else
            {
                spectrumOccupancyIndicator[spIdx] = 0.0f;
            }
        }

        float best = 0.0f;
        for (int spIdx = 0; spIdx < 2; spIdx++)
        {
            if (spectrumOccupancyIndicator[spIdx] > best)
            {
                best = spectrumOccupancyIndicator[spIdx];
                st.SpectrumOccupancyEstimation = spIdx;
            }
        }

        st.FrequencySyncFlag = true;
        return true;
    }

    // demodulator::frameSync (demodulator.cpp:421-520).
    private bool FrameSync()
    {
        DrmDemodState st = State;
        if (st.FrameSyncFlag)
            return true;

        st.NSymbolsNeeded = st.SymbolsPerFrame + st.SymbolsPer2DWindow - 1;
        st.NSamplesNeeded = (st.NSymbolsNeeded + 1) * st.Ts - st.RsBufWidx;
        if (st.NSamplesNeeded > 0)
        {
            st.InputSamplesBufferRequest = st.NSamplesNeeded;
            return false;
        }

        int tSmp = 0;
        for (int i = 0; i < st.SymbolsPerFrame; i++)
        {
            st.DeltaTimeOffsetInteger = DemodulateSymbolInto(tSmp, i);
            tSmp = tSmp + st.Ts + st.DeltaTimeOffsetInteger;
        }

        int symbol0 = DrmPilotSearch.FrameStartSymbol(st.SymbolBuffer, st.SymbolsPerFrame, st.TimeRefCellsK, st.TimeRefCellsTheta1024,
            st.KDc, st.KModulo, DrmRxTables.TimeRefCellColumns);
        int symbolNoToEqualize = ((symbol0 - st.SymbolsToDelay + st.SymbolsPerFrame) % st.SymbolsPerFrame) + 1;

        st.FrameSyncFlag = true;
        st.SymbolCounter = 0;
        float[] rs = st.RsBuffer;
        if (symbolNoToEqualize != 1)
        {
            int shift = (symbolNoToEqualize - 1) * st.Ts;
            st.RsBufWidx -= shift;
            for (int j = 0; j < st.RsBufWidx; j++)
            {
                rs[j * 2] = rs[(shift + j) * 2];
                rs[j * 2 + 1] = rs[(shift + j) * 2 + 1];
            }
        }

        st.SymBufWidx = 0;
        st.Zi[0] = -1.0f;
        tSmp = 0;
        Array.Clear(st.SymbolBuffer);
        for (int i = 0; i < st.SymbolsPer2DWindow; i++)
        {
            st.DeltaTimeOffsetInteger = DemodulateSymbolInto(tSmp, i);
            st.SymBufWidx++;
            tSmp += st.Ts + st.DeltaTimeOffsetInteger;
        }

        st.RsBufWidx -= tSmp;
        for (int i = 0; i < st.RsBufWidx; i++)
        {
            rs[i * 2] = rs[(i + tSmp) * 2];
            rs[i * 2 + 1] = rs[(i + tSmp) * 2 + 1];
        }

        _frameStage.OnFrameSync();
        return true;
    }

    // getofdm on rs_buffer[t_smp..] into symbol_buffer row `row` (demodulator.cpp:302-308, 443-449, 482-488).
    private int DemodulateSymbolInto(int tSmp, int row)
    {
        DrmDemodState st = State;
        int dtoi = _ofdm.Demodulate(st.RsBuffer.AsSpan(2 * tSmp), st.TimeOffsetFractionalInit, st.FreqOffsetInit,
            st.DeltaTimeOffsetIInit, st.Ts, st.Tu, st.Zi, _symbolTemp);
        _symbolTemp.AsSpan(0, 2 * st.KModulo).CopyTo(st.SymbolBuffer.AsSpan(row * st.KModulo * 2));
        return dtoi;
    }

    private static float Energy(float[] sb, int k, int symbol, int kModulo)
    {
        float re = sb[(k + symbol * kModulo) * 2], im = sb[(k + symbol * kModulo) * 2 + 1];
        return re * re + im * im;
    }
}
