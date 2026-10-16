namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>getmode's result, QSSTV <c>smode_info</c> (drmrx/structtemplates.h; getmode.cpp:223-310).</summary>
/// <param name="ModeIndex">0/1/2 = A/B/E, or <see cref="DrmRxTables.NoMode"/>.</param>
/// <param name="TimeOffset">Guard-interval start within the first symbol, in samples.</param>
/// <param name="SampleRateOffset">Least-squares slope of the per-symbol time offset over Ts.</param>
/// <param name="FrequencyOffsetFractional">The guard-correlation phase (radians per Tu).</param>
internal readonly record struct DrmModeEstimate(int ModeIndex, float TimeOffset, float SampleRateOffset, float FrequencyOffsetFractional);

/// <summary>QSSTV's robustness-mode and coarse time/frequency estimator, <c>getmode</c> with its two
/// moving-sum filters (drmrx/getmode.cpp:43-313, drmrx/filter1.cpp:35-50, drmrx/filter1c.cpp:35-50).</summary>
/// <remarks>
/// <para>Precision follows QSSTV's g++ build: every array and accumulator is float; the few double
/// sub-expressions (<c>0.5 * (...)</c>, the SNR constant) are evaluated in double and stored to float as
/// there. <c>sqrt</c>/<c>atan2</c> on float arguments bind to the float overloads under g++'s
/// <c>&lt;math.h&gt;</c>, so they are <see cref="MathF"/> here.</para>
/// <para>Only modes 0-2 are scored (getmode.cpp:114, "ON4QZ 3 was 4"); mode 3's score stays 0.0 but still
/// takes part in the arg-max (getmode.cpp:198-205), as in QSSTV.</para>
/// <para>Scratch is allocated once for the largest window (<c>initGetmode</c>, getmode.cpp:47-88); not thread-safe.</para>
/// </remarks>
internal sealed class DrmModeDetector
{
    private const float Epsilon = (float)1.0E-10;
    private const int TuMin = 112; // Tu_D: initGetmode sizes the lag arrays n - Tu_D (getmode.cpp:52)

    private readonly int _maxSamples;
    private readonly float[] _in;
    private readonly float[] _convIn;
    private readonly float[] _absIn;
    private readonly float[] _absInIn;
    private readonly float[] _convAbsInIn;
    private readonly float[] _gamma = new float[2 * 320];
    private readonly float[] _phi = new float[320];
    private readonly float[] _rect = new float[200 - 112];
    private readonly int[] _b = new int[20];

    /// <param name="maxSamples">The largest window <see cref="Detect"/> will be given (complex samples).</param>
    public DrmModeDetector(int maxSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, 2 * 320);
        _maxSamples = maxSamples;
        _in = new float[(maxSamples - TuMin) * 2];
        _convIn = new float[(maxSamples - TuMin) * 2];
        _absIn = new float[maxSamples];
        _absInIn = new float[maxSamples - TuMin];
        _convAbsInIn = new float[maxSamples - TuMin];
    }

    /// <summary><c>getmode(input, n, result)</c> (getmode.cpp:91-313) on <paramref name="n"/> interleaved
    /// complex float samples.</summary>
    public DrmModeEstimate Detect(ReadOnlySpan<float> input, int n)
    {
        if (n > _maxSamples || input.Length < 2 * n)
            throw new ArgumentOutOfRangeException(nameof(n), n, "Window exceeds the detector's capacity or the input.");

        float snrModeDet = (float)Math.Exp(15.0 * Math.Log(10) / 10.0);
        Span<float> maxAbsGammaRelList = stackalloc float[4];
        Span<int> thetaList = stackalloc int[4];
        Span<float> epsilonMlList = stackalloc float[4];
        maxAbsGammaRelList.Clear();
        thetaList.Clear();
        epsilonMlList.Clear();
        float[] gamma = _gamma, phi = _phi, rect = _rect;

        float rho = snrModeDet / (snrModeDet + 1);
        int nSymbolsModeDet = ((n + 1) / DrmRxTables.Ts[0]) - 1;
        int mode, ts, tu, tg, tSmp;
        float maxAbsGammaRel, tmpmax;
        for (mode = 0; mode < 3; mode++)
        {
            ts = DrmRxTables.Ts[mode];
            tu = DrmRxTables.Tu[mode];
            tg = ts - tu;
            tSmp = 0;
            for (int i = 0; i < ts; i++)
            {
                gamma[i * 2] = 0.0f;
                gamma[i * 2 + 1] = 0.0f;
                phi[i] = 0.0f;
            }

            LagProduct(input, n, tu);
            rect[0] = 0.5f;
            for (int i = 1; i < tg; i++)
                rect[i] = 1.0f;
            Filter1c(_in, _convIn, rect, n - tu, tg);
            PowerSum(input, n, tu);
            Filter1(_absInIn, _convAbsInIn, rect, n - tu, tg);
            for (int j = 0; j < nSymbolsModeDet; j++)
            {
                for (int i = 0; i < ts; i++)
                {
                    gamma[i * 2] = gamma[i * 2] + _convIn[(tSmp + tg + i - 1) * 2];
                    gamma[i * 2 + 1] = gamma[i * 2 + 1] + _convIn[(tSmp + tg + i - 1) * 2 + 1];
                    phi[i] = phi[i] + (float)(0.5 * (Epsilon + _convAbsInIn[tSmp + tg + i - 1]));
                }

                tSmp += ts;
            }

            int theta = 0;
            maxAbsGammaRel = (float)-1.0E20;
            for (int i = 0; i < ts; i++)
            {
                tmpmax = MathF.Sqrt(gamma[2 * i] * gamma[2 * i] + gamma[2 * i + 1] * gamma[2 * i + 1]);
                tmpmax -= rho * phi[i];
                if (tmpmax > maxAbsGammaRel)
                {
                    maxAbsGammaRel = tmpmax;
                    theta = i;
                }
            }

            maxAbsGammaRel = MathF.Sqrt(gamma[theta * 2] * gamma[theta * 2] + gamma[theta * 2 + 1] * gamma[theta * 2 + 1]) / (rho * phi[theta]);
            float epsilonMl = MathF.Atan2(gamma[2 * theta], gamma[2 * theta + 1]);
            maxAbsGammaRelList[mode] = maxAbsGammaRel;
            thetaList[mode] = theta;
            epsilonMlList[mode] = epsilonMl;
        }

        maxAbsGammaRel = (float)-1.0E20;
        for (int i = 0; i < 4; i++)
        {
            if (maxAbsGammaRelList[i] > maxAbsGammaRel)
            {
                maxAbsGammaRel = maxAbsGammaRelList[i];
                mode = i;
            }
        }

        bool maxOk = true;
        if (maxAbsGammaRel > 0.6)
        {
            for (int i = 0; i < 3; i++)
            {
                if (i != mode && maxAbsGammaRelList[i] > 0.6)
                    maxOk = false;
            }
        }
        else
        {
            maxOk = false;
        }

        if (!maxOk)
            return new DrmModeEstimate(DrmRxTables.NoMode, 0.0f, 0.0f, 0.0f);

        ts = DrmRxTables.Ts[mode];
        tu = DrmRxTables.Tu[mode];
        tg = ts - tu;
        int timeOffsetMean = thetaList[mode];
        LagProduct(input, n, tu);
        rect[0] = 0.5f;
        for (int i = 1; i < tg; i++)
            rect[i] = 1.0f;
        rect[tg - 1] = 0.5f;
        Filter1c(_in, _convIn, rect, n - tu, tg);
        PowerSum(input, n, tu);
        Filter1(_absInIn, _convAbsInIn, rect, n - tu, tg);
        tSmp = tg + timeOffsetMean + ts / 2;
        int[] b = _b;
        for (int j = 0; j < nSymbolsModeDet - 2; j++)
        {
            maxAbsGammaRel = (float)-1.0E20;
            for (int i = 0; i < ts; i++)
            {
                gamma[i * 2] = _convIn[(tSmp + i) * 2];
                gamma[i * 2 + 1] = _convIn[(tSmp + i) * 2 + 1];
                phi[i] = (float)(0.5 * (Epsilon + _convAbsInIn[tSmp + i]));
                tmpmax = MathF.Sqrt(gamma[2 * i] * gamma[2 * i] + gamma[2 * i + 1] * gamma[2 * i + 1]) - rho * phi[i];
                if (tmpmax > maxAbsGammaRel)
                {
                    maxAbsGammaRel = tmpmax;
                    b[j] = i;
                }
            }

            tSmp += ts;
        }

        float sumx = 0.0f, sumy = 0.0f, sumxx = 0.0f, sumxy = 0.0f;
        for (int i = 0; i < nSymbolsModeDet - 2; i++)
        {
            sumx += i;
            sumy += b[i];
            sumxx += (float)i * (float)i;
            sumxy += (float)i * (float)b[i];
        }

        int m2 = nSymbolsModeDet - 2;
        float slope = ((m2 * sumxy) - (sumx * sumy)) / ((m2 * sumxx) - (sumx * sumx));
        float boffs = ((sumy * sumxx) - (sumx * sumxy)) / ((m2 * sumxx) - (sumx * sumx));

        float timeOffset = (boffs + ts / 2 + timeOffsetMean - 1) % (float)ts;
        return new DrmModeEstimate(mode, timeOffset, slope / (float)ts, epsilonMlList[mode]);
    }

    // in_[i] = input[i] · conj(input[i + Tu]) (getmode.cpp:128-132, 240-244).
    private void LagProduct(ReadOnlySpan<float> input, int n, int tu)
    {
        float[] o = _in;
        for (int i = 0; i < n - tu; i++)
        {
            o[2 * i] = input[2 * i] * input[(i + tu) * 2] + input[2 * i + 1] * input[(i + tu) * 2 + 1];
            o[2 * i + 1] = -input[2 * i] * input[(i + tu) * 2 + 1] + input[2 * i + 1] * input[(i + tu) * 2];
        }
    }

    // abs_in_in_[i] = |input[i]|² + |input[i + Tu]|² (getmode.cpp:142-149, 252-259).
    private void PowerSum(ReadOnlySpan<float> input, int n, int tu)
    {
        for (int i = 0; i < n; i++)
            _absIn[i] = input[i * 2] * input[i * 2] + input[i * 2 + 1] * input[i * 2 + 1];
        for (int i = 0; i < n - tu; i++)
            _absInIn[i] = _absIn[i] + _absIn[i + tu];
    }

    /// <summary><c>drmfilter1c</c> (filter1c.cpp:35-50): causal complex FIR, float accumulation from j = 0.</summary>
    internal static void Filter1c(ReadOnlySpan<float> sigin, Span<float> y, ReadOnlySpan<float> coef, int dataLen, int coefLen)
    {
        for (int i = 0; i < dataLen; i++)
        {
            float re = 0.0f, im = 0.0f;
            for (int j = 0; j <= i && j < coefLen; j++)
            {
                re += coef[j] * sigin[(i - j) * 2];
                im += coef[j] * sigin[(i - j) * 2 + 1];
            }

            y[i * 2] = re;
            y[i * 2 + 1] = im;
        }
    }

    /// <summary><c>drmfilter1</c> (filter1.cpp:35-50): causal real FIR, float accumulation from j = 0.</summary>
    internal static void Filter1(ReadOnlySpan<float> sigin, Span<float> output, ReadOnlySpan<float> coef, int dataLen, int coefLen)
    {
        for (int i = 0; i < dataLen; i++)
        {
            float acc = 0.0f;
            for (int j = 0; j <= i && j < coefLen; j++)
                acc += coef[j] * sigin[i - j];
            output[i] = acc;
        }
    }
}
