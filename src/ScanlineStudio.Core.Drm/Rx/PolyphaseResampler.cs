using System.Numerics;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>Arbitrary-ratio resampler to the DRM receiver's 12 kHz (our own code, not a QSSTV port: QSSTV
/// only accepts 48 kHz capture, sound/soundbase.cpp:42,187-189).</summary>
/// <remarks>
/// <para>A Kaiser-windowed sinc low-pass defined in continuous time at the input rate, tabulated in
/// <see cref="Phases"/> polyphase rows with linear interpolation between adjacent rows. Pass band
/// 0-<see cref="PassbandEdgeHz"/>, stop band from <see cref="StopbandEdgeHz"/>, designed for
/// <see cref="DesignAttenuationDb"/>. The same absolute low-pass serves up- and down-sampling, so input
/// content above 3.5 kHz, its aliases and the up-sampling images all fall in the stop band.</para>
/// <para>Output m is the filtered input at input time m·R/12000 exactly (zero-phase kernel, so the group
/// delay is constant and zero on that timeline); the latency is <see cref="TapCount"/>/2 input samples.
/// The position is tracked as an exact integer fraction, so there is no drift at any integer rate.</para>
/// <para>Double precision. Streaming with state across calls of any length; no allocation after
/// construction. Not thread-safe: one instance per receive worker.</para>
/// </remarks>
internal sealed class PolyphaseResampler
{
    /// <summary>Pass-band edge, QSSTV's own decimator band limit (dsp/downsamplefilter.cpp:186-193).</summary>
    public const double PassbandEdgeHz = 2800;

    /// <summary>Stop-band edge, as QSSTV's decimator (dsp/downsamplefilter.cpp:186-193).</summary>
    public const double StopbandEdgeHz = 3500;

    /// <summary>Kaiser design target; the requirement is 60 dB, the margin covers row interpolation.</summary>
    public const double DesignAttenuationDb = 80;

    /// <summary>Polyphase rows per input sample.</summary>
    public const int Phases = 128;

    private const int ChunkSize = 4096;

    private readonly int _inRate;
    private readonly int _outRate;
    private readonly int _taps;
    private readonly double[] _table;
    private readonly double[] _buf;
    private int _count;
    private int _pos;
    private int _rem;

    public PolyphaseResampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 2 * (int)StopbandEdgeHz + 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 2 * (int)StopbandEdgeHz + 1);
        _inRate = inputRate;
        _outRate = outputRate;
        _taps = TapCountFor(inputRate);
        _table = BuildTable(inputRate, _taps);
        _buf = new double[_taps + ChunkSize];
        Reset();
    }

    /// <summary>Kernel length in input samples (even).</summary>
    public int TapCount => _taps;

    public int InputRate => _inRate;

    public int OutputRate => _outRate;

    /// <summary>Kaiser's length estimate N = (A − 8) / (2.285 · Δω), Δω the transition in rad/input sample.</summary>
    internal static int TapCountFor(int inputRate)
    {
        double dw = 2 * Math.PI * (StopbandEdgeHz - PassbandEdgeHz) / inputRate;
        int n = (int)Math.Ceiling((DesignAttenuationDb - 8) / (2.285 * dw));
        return n + (n & 1);
    }

    /// <summary>Clears the history and restarts the output timeline at input sample 0.</summary>
    public void Reset()
    {
        Array.Clear(_buf);
        // N/2 − 1 zeros stand for the input before sample 0, so output 0 sits at input time 0.
        _count = _taps / 2 - 1;
        _pos = _taps / 2 - 1;
        _rem = 0;
    }

    /// <summary>The most outputs <see cref="Process"/> can write for <paramref name="inputCount"/> samples.</summary>
    public int MaxOutput(int inputCount) => (int)(((long)inputCount * _outRate + _inRate - 1) / _inRate) + 1;

    /// <summary>Resamples <paramref name="input"/>; returns the number of outputs written.</summary>
    public int Process(ReadOnlySpan<double> input, Span<double> output)
    {
        int produced = 0;
        while (!input.IsEmpty)
        {
            int take = Math.Min(input.Length, _buf.Length - _count);
            input[..take].CopyTo(_buf.AsSpan(_count));
            _count += take;
            input = input[take..];
            produced += Drain(output[produced..]);
            Compact();
        }

        return produced;
    }

    private int Drain(Span<double> output)
    {
        int half = _taps / 2;
        int produced = 0;
        double[] buf = _buf;
        double[] table = _table;
        int taps = _taps;
        // the window for input position _pos spans [_pos − N/2 + 1, _pos + N/2]
        while (_pos + half < _count)
        {
            int scaled = _rem * Phases;
            int p = scaled / _outRate;
            double mu = (double)(scaled - p * _outRate) / _outRate;
            int start = _pos - half + 1;
            int r0 = p * taps;
            ReadOnlySpan<double> x = buf.AsSpan(start, taps);
            double s0 = Dot(x, table.AsSpan(r0, taps));
            double s1 = Dot(x, table.AsSpan(r0 + taps, taps));
            output[produced++] = s0 + mu * (s1 - s0);
            _rem += _inRate;
            while (_rem >= _outRate)
            {
                _rem -= _outRate;
                _pos++;
            }
        }

        return produced;
    }

    // SIMD dot product; order depends on length and Vector<double>.Count, so block-size independent but ulp-level CPU-dependent.
    private static double Dot(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        int n = a.Length;
        int j = 0;
        var acc = Vector<double>.Zero;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<double>.Count;
            for (; j <= n - w; j += w)
                acc += new Vector<double>(a.Slice(j, w)) * new Vector<double>(b.Slice(j, w));
        }

        double s = Vector.Sum(acc);
        for (; j < n; j++)
            s += a[j] * b[j];
        return s;
    }

    // Drops samples no future window needs.
    private void Compact()
    {
        int drop = _pos - (_taps / 2 - 1);
        if (drop <= 0)
            return;
        drop = Math.Min(drop, _count);
        _buf.AsSpan(drop, _count - drop).CopyTo(_buf);
        _count -= drop;
        _pos -= drop;
    }

    // Row p holds g(p/L + N/2 − 1 − j) for j = 0..N−1, rows 0..L; each row normalized to unit DC gain.
    private static double[] BuildTable(int inputRate, int taps)
    {
        double fc = (PassbandEdgeHz + StopbandEdgeHz) / 2 / inputRate;
        double beta = 0.1102 * (DesignAttenuationDb - 8.7);
        double halfWidth = taps / 2.0;
        double i0Beta = BesselI0(beta);
        var table = new double[(Phases + 1) * taps];
        for (int p = 0; p <= Phases; p++)
        {
            double sum = 0;
            for (int j = 0; j < taps; j++)
            {
                double u = (double)p / Phases + halfWidth - 1 - j;
                double r = u / halfWidth;
                double w = Math.Abs(r) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - r * r)) / i0Beta;
                double x = 2 * fc * u;
                double sinc = x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
                double g = 2 * fc * sinc * w;
                table[p * taps + j] = g;
                sum += g;
            }

            for (int j = 0; j < taps; j++)
                table[p * taps + j] /= sum;
        }

        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 200 && term > sum * 1e-17; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
        }

        return sum;
    }
}
