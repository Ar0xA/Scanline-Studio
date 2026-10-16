namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's DRM real-to-complex filter, <c>drmHilbertFilter</c> as <c>drmRx::run</c> applies it to
/// each 12 kHz stripe (drmrx/drmrx.cpp:9,64; dsp/filters.cpp:133-162; dsp/filter.cpp:292-312;
/// dsp/filterparam.h:68-69; dsp/filterparam.cpp:501-663).</summary>
/// <remarks>Precision matches QSSTV: history, taps and accumulation are double (<c>FILTERPARAMTYPE</c>),
/// and the result is stored as float32 into <c>resamp_signal</c> (drmrx.h:26). Output is interleaved
/// as QSSTV's: <c>[2k]</c> = Hilbert part <c>resQ/gain</c>, <c>[2k+1]</c> = the input delayed by 76.</remarks>
internal sealed class DrmHilbertFilter
{
    /// <summary>DRMHILBERTTAPS (filterparam.h:68).</summary>
    public const int TapCount = 153;

    /// <summary>The delay of the real branch, <c>(nZeroes+1)/2</c> (filter.cpp:309).</summary>
    public const int Delay = TapCount / 2;

    /// <summary>DRMHILBERTGAIN (filterparam.h:69).</summary>
    public const double Gain = 1.569749;

    // newest at index 0 (filter.cpp:303-304)
    private readonly double[] _history = new double[TapCount];

    /// <summary>Clears the history, as <c>drmHilbertFilter::init</c> (filters.cpp:142-150, filter.cpp:73-87).</summary>
    public void Reset() => Array.Clear(_history);

    /// <summary>Filters <paramref name="input"/> into <c>2 × input.Length</c> interleaved floats.</summary>
    public void Process(ReadOnlySpan<double> input, Span<float> output)
    {
        if (output.Length < 2 * input.Length)
            throw new ArgumentException("Output must hold two floats per input sample.", nameof(output));
        double[] s = _history;
        ReadOnlySpan<double> c = Coefficients;
        for (int k = 0; k < input.Length; k++)
        {
            s.AsSpan(0, TapCount - 1).CopyTo(s.AsSpan(1));
            s[0] = input[k];
            double resQ = 0;
            for (int i = 0; i < TapCount; i++)
                resQ += s[i] * c[i];
            output[2 * k + 1] = (float)s[Delay];
            output[2 * k] = (float)(resQ / Gain);
        }
    }

    /// <summary><c>drmHilbertCoef</c> (filterparam.cpp:501-663): blFIR Hilbert, 12 kHz, 153 taps, corner 1000 Hz,
    /// beta 0.5, windowed. Copied verbatim.</summary>
    internal static ReadOnlySpan<double> Coefficients =>
    [
        0, 0.00107190601522455, 0, 0.00114428127836931, 0, 0.00126465142463967,
        0, 0.00143657040680006, 0, 0.00166370297755838, 0, 0.00194984843584138,
        0, 0.00229897112682306, 0, 0.00271523918124115, 0, 0.00320307336657778,
        0, 0.00376720843252962, 0, 0.00441277001061758, 0, 0.00514537103613413,
        0, 0.00597123289056438, 0, 0.00689733814551449, 0, 0.00793162411788592,
        0, 0.00908322970767581, 0, 0.0103628126183723, 0, 0.0117829607223439,
        0, 0.0133587310729627, 0, 0.015108364544715, 0, 0.0170542460109686,
        0, 0.0192242138609088, 0, 0.0216533762307932, 0, 0.0243866781537711,
        0, 0.027482608550733, 0, 0.0310186849032487, 0, 0.035099797177604,
        0, 0.0398713166388587, 0, 0.0455404799655691, 0, 0.0524128630218771,
        0, 0.0609580273296193, 0, 0.0719357553084729, 0, 0.0866599865154828,
        0, 0.10761465561151, 0, 0.140125234561542, 0, 0.198041949770117,
        0, 0.332155822226347, 0, 0.999607048858159, 0, -0.999607048858159,
        0, -0.332155822226347, 0, -0.198041949770117, 0, -0.140125234561542,
        0, -0.10761465561151, 0, -0.0866599865154828, 0, -0.0719357553084729,
        0, -0.0609580273296193, 0, -0.0524128630218771, 0, -0.0455404799655691,
        0, -0.0398713166388587, 0, -0.035099797177604, 0, -0.0310186849032487,
        0, -0.027482608550733, 0, -0.0243866781537711, 0, -0.0216533762307932,
        0, -0.0192242138609088, 0, -0.0170542460109686, 0, -0.015108364544715,
        0, -0.0133587310729627, 0, -0.0117829607223439, 0, -0.0103628126183723,
        0, -0.00908322970767581, 0, -0.00793162411788592, 0, -0.00689733814551449,
        0, -0.00597123289056438, 0, -0.00514537103613413, 0, -0.00441277001061758,
        0, -0.00376720843252962, 0, -0.00320307336657778, 0, -0.00271523918124115,
        0, -0.00229897112682306, 0, -0.00194984843584138, 0, -0.00166370297755838,
        0, -0.00143657040680006, 0, -0.00126465142463967, 0, -0.00114428127836931,
        0, -0.00107190601522455, 0
    ];
}
