using ScanlineStudio.Core.Drm.Dsp;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's tracking OFDM symbol demodulator, <c>getofdmsync</c> (drmrx/getofdmsync.cpp:57-369), used by
/// the channel estimator once frame sync holds: time tracking from the channel impulse response (the
/// zero-padded FFT of H, smoothed by a sine window, peak position, P-I controller), frequency tracking from the
/// pilot phase drift the estimator passes in, frequency correction, the Tu-point FFT and the phase rotation
/// that centres the symbol on <c>K_dc</c>. Tracking state lives in the caller's <c>Zi</c> (entries 1, 2, 4, 5).</summary>
/// <remarks>
/// <para>Precision follows QSSTV's g++ build (float; the double sub-expressions of the source are evaluated in
/// double and stored to float; float <c>cos</c>/<c>sin</c>/<c>tanh</c> bind to cosf/sinf/tanhf). The FFTs are
/// <see cref="MixedRadixFft{T}"/> in float where QSSTV uses fftwf, so values differ from QSSTV in the last bits.</para>
/// <para>QSSTV quirks kept: the output's last bin (index Tu−1) is never written (getofdmsync.cpp:338, 358-362),
/// so it keeps the scratch's content: 0 here after <see cref="Init"/>, uninitialized malloc memory in QSSTV; it
/// lies outside every mode's K_min..K_max. The controller gains are reassigned on every call
/// (:153-159). The plan size and the window come from <see cref="Init"/>, as QSSTV's statics do.</para>
/// <para>Not thread-safe; no allocation after <see cref="Init"/>.</para>
/// </remarks>
internal sealed class DrmOfdmSyncDemodulator
{
    private const int MaxDeltaTimeOffsetInteger = 3;
    private const float KpSmallTiming = (float)0.001;
    private const float KpLargeTiming = (float)0.001;
    private const float KiTiming = (float)2E-5;
    private const float KpFreq = (float)0.01;

    private readonly float[] _sinFilter = new float[256];
    private readonly float[] _hAbsq = new float[512];
    private readonly float[] _hAbsqFiltered = new float[512];
    private readonly float[] _s = new float[2 * 512];
    private readonly float[] _sf = new float[2 * 512];
    private readonly float[] _s1 = new float[2 * DrmRxTables.TuMax];
    private readonly float[] _sf1 = new float[2 * DrmRxTables.TuMax];
    private readonly float[] _expTemp = new float[2 * DrmRxTables.TuMax + 2];
    private readonly float[] _out1 = new float[2 * DrmRxTables.TuMax];
    private MixedRadixFft<float>? _fftTc;
    private MixedRadixFft<float>? _fftTu;
    private int _tc;
    private int _tgs;

    /// <summary>Receives the smoothed impulse-response power (2·Tc bins) before its peak is picked.</summary>
    internal delegate void ImpulseResponseObserver(ReadOnlySpan<float> filtered, int tc);

    /// <summary>Test hook: sees each symbol's smoothed impulse response; null in the product.</summary>
    internal ImpulseResponseObserver? Observer { get; set; }

    /// <summary>Test hook: on these call indices (counted since construction) the peak search takes the best bin
    /// at a different position mod Tc instead of the first maximum. Null in the product.</summary>
    internal IReadOnlySet<int>? ForceRunnerUpAtCalls { get; set; }

    /// <summary><see cref="Demodulate"/> calls since construction (the index <see cref="ForceRunnerUpAtCalls"/> uses).</summary>
    internal int Calls { get; private set; }

    /// <summary>The <c>init == 1</c> call (getofdmsync.cpp:91-143): the Tc-point and Tu-point plans and the
    /// impulse-response window, for <paramref name="lH"/> carriers. Clears the output scratch as a fresh malloc.</summary>
    public void Init(int ts, int tu, int lH)
    {
        (_tc, _tgs, _) = Geometry(ts, tu, lH);
        if (_fftTc is null || _fftTc.Length != _tc)
            _fftTc = new MixedRadixFft<float>(_tc);
        if (_fftTu is null || _fftTu.Length != tu)
            _fftTu = new MixedRadixFft<float>(tu);
        Array.Clear(_out1);
        Array.Clear(_expTemp);
        for (int i = 0; i < _tgs; i++)
            _sinFilter[i] = (float)Math.Pow(Math.Sin((float)(i + 1.0) / (float)(_tgs + 1.0) * Math.PI), 0.001);
    }

    /// <summary>One <c>getofdmsync(rs, Ts, Tu, H, lH, delta_freq_offset, Zi, out, 0, 1, 1)</c> call
    /// (getofdmsync.cpp:147-368): demodulates the symbol at <paramref name="rs"/> into <paramref name="output"/>
    /// (Tu interleaved complex values, carrier k at K_dc + k) and returns the integer time step (−3..3).</summary>
    public int Demodulate(ReadOnlySpan<float> rs, int ts, int tu, ReadOnlySpan<float> h, int lH, float deltaFreqOffset,
        Span<float> zi, Span<float> output)
    {
        if (_fftTc is null || _fftTu is null || _fftTu.Length != tu)
            throw new InvalidOperationException("Init must be called for this Tu first.");

        (int tc, int tgs, int tgsh) = Geometry(ts, tu, lH);
        // QSSTV executes the Init-sized plan with the call's Tc; SO_0/SO_1 give the same Tc in every mode.
        if (tc != _tc || tgs != _tgs)
            throw new InvalidOperationException("The carrier count changed the impulse-response FFT size since Init.");

        int tg = ts - tu;
        int tgh = (int)Math.Floor(tg / 2 + 0.5);
        float thresholdTimingSmallLarge = tgh;
        int maxDeltaTheta = tgh;
        int maxSymbolPositionOffset = tgh - MaxDeltaTimeOffsetInteger;
        float phiFreqCorrectionLast = zi[1];
        float deltaTimeOffsetI = zi[2];
        float freqOffset = zi[4];
        float timeOffsetFractional = zi[5];

        float[] s = _s;
        for (int i = 0; i < lH; i++)
        {
            s[i * 2] = h[i * 2];
            s[i * 2 + 1] = h[i * 2 + 1];
        }

        Array.Clear(s, 2 * lH, 2 * (tc - lH));
        _fftTc.Transform(s.AsSpan(0, 2 * tc), _sf.AsSpan(0, 2 * tc), backward: false);
        float[] sf = _sf;
        for (int i = 0; i < tc; i++)
        {
            float p = sf[i * 2] * sf[i * 2] + sf[i * 2 + 1] * sf[i * 2 + 1];
            _hAbsq[i] = p;
            _hAbsq[i + tc] = p;
        }

        DrmModeDetector.Filter1(_hAbsq, _hAbsqFiltered, _sinFilter, 2 * tc, tgs);

        Observer?.Invoke(_hAbsqFiltered.AsSpan(0, 2 * tc), tc);
        float dummy = -1.0E30f;
        float deltaTheta = 0.0f;
        for (int i = 0; i < 2 * tc; i++)
        {
            if (_hAbsqFiltered[i] > dummy)
            {
                dummy = _hAbsqFiltered[i];
                deltaTheta = i;
            }
        }

        if (ForceRunnerUpAtCalls is not null && ForceRunnerUpAtCalls.Contains(Calls))
            deltaTheta = RunnerUp(_hAbsqFiltered.AsSpan(0, 2 * tc), tc, (int)deltaTheta);
        Calls++;

        deltaTheta = (((tc + tgsh - (int)deltaTheta + (int)(tc * 1.5)) % tc) - tc / 2) * tu / (float)tc;
        float deltaThetaTmp = deltaTheta >= maxDeltaTheta ? maxDeltaTheta : deltaTheta;
        deltaTheta = deltaThetaTmp > -maxDeltaTheta ? deltaThetaTmp : -maxDeltaTheta;

        float timeOffsetCtrl = deltaTheta - timeOffsetFractional;
        deltaTimeOffsetI += KiTiming * timeOffsetCtrl;
        float deltaTimeOffsetP = KpLargeTiming * timeOffsetCtrl
            + thresholdTimingSmallLarge * (KpSmallTiming - KpLargeTiming) * MathF.Tanh(timeOffsetCtrl / thresholdTimingSmallLarge);
        float deltaTimeOffset = deltaTimeOffsetP + deltaTimeOffsetI + timeOffsetFractional;
        int deltaTimeOffsetInteger = (int)Math.Floor(deltaTimeOffset + 0.5);
        int dftmp = deltaTimeOffsetInteger > -MaxDeltaTimeOffsetInteger ? deltaTimeOffsetInteger : -MaxDeltaTimeOffsetInteger;
        deltaTimeOffsetInteger = dftmp > MaxDeltaTimeOffsetInteger ? MaxDeltaTimeOffsetInteger : dftmp;
        timeOffsetFractional = deltaTimeOffset - deltaTimeOffsetInteger;

        int symbolPositionOffset = (int)Math.Floor(deltaTheta - deltaTimeOffsetInteger + 0.5);
        int spotmp = symbolPositionOffset > -maxSymbolPositionOffset ? symbolPositionOffset : -maxSymbolPositionOffset;
        symbolPositionOffset = spotmp < maxSymbolPositionOffset ? spotmp : maxSymbolPositionOffset;
        phiFreqCorrectionLast += ((float)deltaTimeOffsetInteger / tu) * freqOffset;

        freqOffset += KpFreq * deltaFreqOffset;

        float[] e = _expTemp;
        for (int i = 0; i < tu; i++)
        {
            float tmptheta = (freqOffset / tu) * (i + symbolPositionOffset) + phiFreqCorrectionLast;
            e[i * 2] = MathF.Cos(tmptheta);
            e[i * 2 + 1] = MathF.Sin(tmptheta);
        }

        float[] s1 = _s1;
        int o = 1 + deltaTimeOffsetInteger + tgh + symbolPositionOffset;
        for (int i = 0; i < tu; i++)
        {
            s1[i * 2] = rs[(o + i) * 2] * e[i * 2] - rs[(o + i) * 2 + 1] * e[i * 2 + 1];
            s1[i * 2 + 1] = rs[(o + i) * 2] * e[i * 2 + 1] + rs[(o + i) * 2 + 1] * e[i * 2];
        }

        phiFreqCorrectionLast = (phiFreqCorrectionLast + (float)ts / (float)tu * freqOffset) % (float)(2.0 * Math.PI);
        _fftTu.Transform(s1.AsSpan(0, 2 * tu), _sf1.AsSpan(0, 2 * tu), backward: false);
        float[] sf1 = _sf1;

        float shift = tgh + timeOffsetFractional - symbolPositionOffset;
        for (int i = 0; i <= tu / 2; i++)
        {
            float term1 = (float)((i * 2.0 * Math.PI / (float)tu) * shift);
            e[i * 2] = MathF.Cos(term1);
            e[i * 2 + 1] = MathF.Sin(term1);
        }

        float[] out1 = _out1;
        for (int i = 0; i < tu / 2; i++)
        {
            int k = tu - 1 - i;
            out1[i * 2] = sf1[k * 2] * e[(i + 1) * 2] + sf1[k * 2 + 1] * e[(i + 1) * 2 + 1];
            out1[i * 2 + 1] = sf1[k * 2 + 1] * e[(i + 1) * 2] - sf1[k * 2] * e[(i + 1) * 2 + 1];
        }

        for (int i = 0; i < tu / 2 - 1; i++)
        {
            out1[(tu / 2 + i) * 2] = sf1[i * 2] * e[i * 2] - sf1[i * 2 + 1] * e[i * 2 + 1];
            out1[(tu / 2 + i) * 2 + 1] = sf1[i * 2 + 1] * e[i * 2] + sf1[i * 2] * e[i * 2 + 1];
        }

        for (int i = 0; i < tu / 2; i++)
        {
            output[i * 2] = out1[(tu / 2 - 1 - i) * 2];
            output[i * 2 + 1] = out1[(tu / 2 - 1 - i) * 2 + 1];
        }

        out1.AsSpan(tu, tu).CopyTo(output[tu..(2 * tu)]);

        zi[1] = phiFreqCorrectionLast;
        zi[2] = deltaTimeOffsetI;
        zi[4] = freqOffset;
        zi[5] = timeOffsetFractional;
        return deltaTimeOffsetInteger;
    }

    // Test hook only: the first maximum among bins at a different position mod Tc from arg.
    private static int RunnerUp(ReadOnlySpan<float> filtered, int tc, int arg)
    {
        int best = -1;
        for (int i = 0; i < filtered.Length; i++)
        {
            if (i % tc != arg % tc && (best < 0 || filtered[i] > filtered[best]))
                best = i;
        }

        return best;
    }

    // Tc, Tgs, Tgsh (getofdmsync.cpp:96-99 and 151-154), with QSSTV's float/double evaluation order.
    private static (int Tc, int Tgs, int Tgsh) Geometry(int ts, int tu, int lH)
    {
        int tg = ts - tu;
        int tc = (int)Math.Pow(2, Math.Ceiling(Math.Log(lH) / Math.Log(2.0)));
        int tgs = (int)MathF.Floor((float)tg / (float)tu * (float)tc);
        int tgsh = (int)Math.Floor((float)tg / (float)tu / 2.0 * (float)tc);
        return (tc, tgs, tgsh);
    }
}
