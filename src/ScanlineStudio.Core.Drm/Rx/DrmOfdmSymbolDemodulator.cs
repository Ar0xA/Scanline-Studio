using ScanlineStudio.Core.Drm.Dsp;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's acquisition OFDM symbol demodulator, <c>getofdm</c> (drmrx/getofdm.cpp:38-334): guard-
/// interval time tracking, guard-correlation frequency tracking, frequency correction, Tu-point FFT and the
/// phase rotation that centres the symbol on <c>K_dc</c>. The tracking state lives in the caller's
/// <c>Zi[6]</c> array, exactly as QSSTV passes it.</summary>
/// <remarks>
/// <para>Precision follows QSSTV's g++ build: float throughout; the double sub-expressions of the source
/// (<c>EPSILON + 0.5 * (...)</c>, <c>theta -= 0.5 * (...)</c>, the <c>PI</c> terms, <c>floor(x + 0.5)</c>,
/// <c>term1</c>) are evaluated in double and stored to float as there. <c>cos</c>/<c>sin</c>/<c>tanh</c>/
/// <c>atan2</c>/<c>fmod</c> on float arguments bind to the float overloads under g++'s <c>&lt;math.h&gt;</c>.</para>
/// <para>QSSTV's FFT is <c>fftwf</c> (FFTW_ESTIMATE); this uses <see cref="MixedRadixFft{T}"/> in float, so
/// symbol values differ from QSSTV in the last bits (stated tolerance in the golden tests).</para>
/// <para>Two QSSTV quirks are kept: <c>temp8[1]</c> adds where it should multiply (getofdm.cpp:182), and
/// <c>(delta_time_offset_integer / Tu)</c> is integer division, always 0 (getofdm.cpp:213).</para>
/// <para>Not thread-safe; no allocation after <see cref="SetMode"/>.</para>
/// </remarks>
internal sealed class DrmOfdmSymbolDemodulator
{
    private const float Epsilon = (float)1.0E-10;
    private const float MaxTheta = (float)5.0;
    private const float KpSmallTiming = (float)0.01;
    private const float KpLargeTiming = (float)0.01;
    private const float ThresholdTimingSmallLarge = (float)2.0;
    private const float KiTiming = (float)0.00005;
    private const float KpSmallFreq = (float)0.05;
    private const float KpLargeFreq = (float)0.75;
    private const float ThresholdFreqSmallLarge = (float)0.5;
    private const float KiFreq = (float)0.0008;

    private readonly float[] _expTemp = new float[2 * DrmRxTables.TuMax];
    private readonly float[] _out1 = new float[2 * DrmRxTables.TuMax];
    private readonly float[] _s = new float[2 * DrmRxTables.TuMax];
    private readonly float[] _sf = new float[2 * DrmRxTables.TuMax];
    private MixedRadixFft<float>? _fft;
    private int _tu;

    /// <summary>The <c>init == 1</c> call (getofdm.cpp:80-106): a Tu-point forward FFT plan.</summary>
    public void SetMode(int tu)
    {
        if (_fft is null || _tu != tu)
            _fft = new MixedRadixFft<float>(tu);
        _tu = tu;
    }

    /// <summary>One <c>getofdm(rs, ..., Zi, out, 0, 1, 1)</c> call (getofdm.cpp:108-333): demodulates the symbol
    /// starting at <paramref name="rs"/> into <paramref name="output"/> (Tu interleaved complex values, carrier
    /// <c>k</c> at index <c>K_dc + k</c>) and returns the integer time step (−1, 0 or +1) to the next symbol.</summary>
    public int Demodulate(ReadOnlySpan<float> rs, float timeOffsetFractionalInit, float freqOffsetInit, float deltaTimeOffsetIInit,
        int ts, int tu, Span<float> zi, Span<float> output, bool timeSyncEnable = true, bool freqSyncEnable = true)
    {
        if (_fft is null || tu != _tu)
            throw new InvalidOperationException("SetMode(Tu) must be called for this Tu first.");

        int tg = ts - tu;
        int tgh = (int)Math.Floor(tg / 2 + 0.5);
        float phiFreqCorrectionLast, deltaTimeOffsetI, dfreqOffsetI, freqOffset, timeOffsetFractional;
        if (zi[0] < 0.0)
        {
            phiFreqCorrectionLast = 0.0f;
            deltaTimeOffsetI = deltaTimeOffsetIInit;
            dfreqOffsetI = 0.0f;
            freqOffset = freqOffsetInit;
            timeOffsetFractional = timeOffsetFractionalInit;
        }
        else
        {
            phiFreqCorrectionLast = zi[1];
            deltaTimeOffsetI = zi[2];
            dfreqOffsetI = zi[3];
            freqOffset = zi[4];
            timeOffsetFractional = zi[5];
        }

        int deltaTimeOffsetInteger;
        if (timeSyncEnable)
        {
            float t1Re = 0.0f, t1Im = 0.0f, temp2 = 0.0f, temp3 = 0.0f;
            float t5Re = 0.0f, t5Im = 0.0f, temp6 = 0.0f, temp7 = 0.0f;
            float t8Re = 0.0f, t8Im = 0.0f, temp9 = 0.0f, temp10 = 0.0f;
            for (int i = 0; i < tg + 2; i++)
            {
                t1Re += rs[i * 2] * rs[(tu + i) * 2] + rs[i * 2 + 1] * rs[(tu + i) * 2 + 1];
                t1Im += -rs[i * 2] * rs[(tu + i) * 2 + 1] + rs[i * 2 + 1] * rs[(tu + i) * 2];
                temp2 += rs[i * 2] * rs[i * 2] + rs[i * 2 + 1] * rs[i * 2 + 1];
                temp3 += rs[(i + tu) * 2] * rs[(i + tu) * 2] + rs[(i + tu) * 2 + 1] * rs[(i + tu) * 2 + 1];
            }

            float temp4 = (float)(Epsilon + 0.5 * (temp2 + temp3));
            for (int i = 0; i < 5; i++)
            {
                t5Re += rs[i * 2] * rs[(tu + i) * 2] + rs[i * 2 + 1] * rs[(tu + i) * 2 + 1];
                t5Im += -rs[i * 2] * rs[(tu + i) * 2 + 1] + rs[i * 2 + 1] * rs[(tu + i) * 2];
                temp6 += rs[i * 2] * rs[i * 2] + rs[i * 2 + 1] * rs[i * 2 + 1];
                temp7 += rs[(i + tu) * 2] * rs[(i + tu) * 2] + rs[(i + tu) * 2 + 1] * rs[(i + tu) * 2 + 1];
                t8Re += rs[(i + tg - 3) * 2] * rs[(i + tu + tg - 3) * 2] + rs[(i + tg - 3) * 2 + 1] * rs[(i + tu + tg - 3) * 2 + 1];
                // QSSTV adds the last two terms instead of multiplying them (getofdm.cpp:182); kept.
                t8Im += -rs[(i + tg - 3) * 2] * rs[(i + tu + tg - 3) * 2 + 1] + rs[(i + tg - 3) * 2 + 1] + rs[(i + tu + tg - 3) * 2];
                temp9 += rs[(i + tg - 3) * 2] * rs[(i + tg - 3) * 2] + rs[(i + tg - 3) * 2 + 1] * rs[(i + tg - 3) * 2 + 1];
                temp10 += rs[(i + tg + tu - 3) * 2] * rs[(i + tg + tu - 3) * 2] + rs[(i + tg + tu - 3) * 2 + 1] * rs[(i + tg + tu - 3) * 2 + 1];
            }

            float thetaPlus = MathF.Sqrt((t1Re - t5Re) * (t1Re - t5Re) + (t1Im - t5Im) * (t1Im - t5Im));
            thetaPlus = (float)(thetaPlus - (0.5 * (-temp6 - temp7)));
            float thetaMinus = MathF.Sqrt((t1Re - t8Re) * (t1Re - t8Re) + (t1Im - t8Im) * (t1Im - t8Im));
            thetaMinus = (float)(thetaMinus - (0.5 * (temp9 - temp10)));
            float deltaTheta = (thetaPlus - thetaMinus) * tgh / temp4;
            if (deltaTheta < -MaxTheta)
                deltaTheta = -MaxTheta;
            if (deltaTheta > MaxTheta)
                deltaTheta = MaxTheta;

            float timeOffsetCtrl = deltaTheta - timeOffsetFractional;
            deltaTimeOffsetI += KiTiming * timeOffsetCtrl;
            float deltaTimeOffsetP = KpLargeTiming * timeOffsetCtrl
                + ThresholdTimingSmallLarge * (KpSmallTiming - KpLargeTiming) * MathF.Tanh(timeOffsetCtrl / ThresholdTimingSmallLarge);
            float deltaTimeOffset = deltaTimeOffsetP + deltaTimeOffsetI + timeOffsetFractional;
            deltaTimeOffsetInteger = (int)Math.Floor(deltaTimeOffset + 0.5);
            if (deltaTimeOffsetInteger < -1)
                deltaTimeOffsetInteger = -1;
            if (deltaTimeOffsetInteger > 1)
                deltaTimeOffsetInteger = 1;
            timeOffsetFractional = deltaTimeOffset - deltaTimeOffsetInteger;
            phiFreqCorrectionLast += (deltaTimeOffsetInteger / tu) * freqOffset;
        }
        else
        {
            deltaTimeOffsetInteger = 0;
            timeOffsetFractional = 0.0f;
        }

        if (freqSyncEnable)
        {
            float tRe = 0.0f, tIm = 0.0f;
            int d = 1 + deltaTimeOffsetInteger;
            for (int i = 0; i < tg; i++)
            {
                tRe += rs[(i + d) * 2] * rs[(i + d + tu) * 2] + rs[(i + d) * 2 + 1] * rs[(i + d + tu) * 2 + 1];
                tIm += rs[(i + d) * 2 + 1] * rs[(i + d + tu) * 2] - rs[(i + d) * 2] * rs[(i + d + tu) * 2 + 1];
            }

            float epsilonMl = MathF.Atan2(tIm, tRe);
            float freqOffsetCtrl = (float)(((float)(epsilonMl - freqOffset + Math.PI + 100.0 * Math.PI) % (float)(2 * Math.PI)) - Math.PI);
            dfreqOffsetI += KiFreq * freqOffsetCtrl;
            float dfreqOffsetP = KpLargeFreq * freqOffsetCtrl
                + ThresholdFreqSmallLarge * (KpSmallFreq - KpLargeFreq) * MathF.Tanh(freqOffsetCtrl / ThresholdFreqSmallLarge);
            float dfreqOffset = dfreqOffsetP + dfreqOffsetI;
            freqOffset += dfreqOffset;
        }

        float[] e = _expTemp;
        for (int i = 0; i < tu; i++)
        {
            float tmptheta = (freqOffset / tu) * i + phiFreqCorrectionLast;
            e[i * 2] = MathF.Cos(tmptheta);
            e[i * 2 + 1] = MathF.Sin(tmptheta);
        }

        float[] s = _s;
        int o = 1 + deltaTimeOffsetInteger + tgh;
        for (int i = 0; i < tu; i++)
        {
            s[i * 2] = rs[(o + i) * 2] * e[i * 2] - rs[(o + i) * 2 + 1] * e[i * 2 + 1];
            s[i * 2 + 1] = rs[(o + i) * 2] * e[i * 2 + 1] + rs[(o + i) * 2 + 1] * e[i * 2];
        }

        phiFreqCorrectionLast = (phiFreqCorrectionLast + (float)ts / (float)tu * freqOffset) % (float)(2.0 * Math.PI);
        float[] sf = _sf;
        _fft.Transform(s.AsSpan(0, 2 * tu), sf.AsSpan(0, 2 * tu), backward: false);
        for (int i = 0; i <= tu / 2; i++)
        {
            float term1 = (float)(i * 2.0 * Math.PI * (tgh + timeOffsetFractional) / tu);
            e[i * 2] = MathF.Cos(term1);
            e[i * 2 + 1] = MathF.Sin(term1);
        }

        float[] out1 = _out1;
        for (int i = 0; i < tu / 2; i++)
        {
            int k = tu - 1 - i;
            out1[i * 2] = sf[k * 2] * e[(i + 1) * 2] + sf[k * 2 + 1] * e[(i + 1) * 2 + 1];
            out1[i * 2 + 1] = sf[k * 2 + 1] * e[(i + 1) * 2] - sf[k * 2] * e[(i + 1) * 2 + 1];
        }

        for (int i = 0; i < tu / 2; i++)
        {
            output[i * 2] = out1[(tu / 2 - 1 - i) * 2];
            output[i * 2 + 1] = out1[(tu / 2 - i - 1) * 2 + 1];
            output[(tu / 2 + i) * 2] = sf[i * 2] * e[i * 2] - sf[i * 2 + 1] * e[i * 2 + 1];
            output[(tu / 2 + i) * 2 + 1] = sf[i * 2 + 1] * e[i * 2] + sf[i * 2] * e[i * 2 + 1];
        }

        zi[0] = 1.0f;
        zi[1] = phiFreqCorrectionLast;
        zi[2] = deltaTimeOffsetI;
        zi[3] = dfreqOffsetI;
        zi[4] = freqOffset;
        zi[5] = timeOffsetFractional;
        return deltaTimeOffsetInteger;
    }
}
