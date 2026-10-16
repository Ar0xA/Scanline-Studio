using System.Numerics;
using System.Runtime.InteropServices;

namespace ScanlineStudio.Core.Drm.Dsp;

/// <summary>Mixed-radix (2/3/4/5) Stockham autosort complex FFT on interleaved (re, im) data, for any
/// length 2^a·3^b·5^c. Conventions follow FFTW as QSSTV calls it: forward uses exp(-2πi·jk/n),
/// backward uses exp(+2πi·jk/n), and neither is scaled (FFTW_FORWARD/FFTW_BACKWARD,
/// drmtx/common/matlib/MatlibStdToolbox.cpp:774-775, drmrx/getofdm.cpp:103). QSSTV's TX Ifft then
/// multiplies by 1/n (MatlibStdToolbox.cpp:551-556) and OFDM.cpp:59 multiplies by n again; callers
/// reproduce that themselves. A plan owns its scratch buffers, so one instance is not thread-safe;
/// Transform allocates nothing.</summary>
internal sealed class MixedRadixFft<T>
    where T : unmanaged, IFloatingPointIeee754<T>
{
    private readonly int _n;
    private readonly int[] _radices;
    private readonly T[][] _twiddles;
    private readonly T[] _bufA;
    private readonly T[] _bufB;

    private static readonly T Half = T.CreateTruncating(0.5);
    private static readonly T Sin60 = T.CreateTruncating(-0.86602540378443864676); // sin(-2π/3)
    private static readonly T C1 = T.CreateTruncating(0.30901699437494742410);  // cos(2π/5)
    private static readonly T C2 = T.CreateTruncating(-0.80901699437494742410); // cos(4π/5)
    private static readonly T S1 = T.CreateTruncating(0.95105651629515357212);  // sin(2π/5)
    private static readonly T S2 = T.CreateTruncating(0.58778525229247312917);  // sin(4π/5)

    public MixedRadixFft(int n)
    {
        if (n < 1)
            throw new ArgumentOutOfRangeException(nameof(n), n, "FFT length must be positive.");

        _n = n;
        var radices = new List<int>();
        int rest = n;
        while (rest % 4 == 0) { radices.Add(4); rest /= 4; }
        while (rest % 2 == 0) { radices.Add(2); rest /= 2; }
        while (rest % 3 == 0) { radices.Add(3); rest /= 3; }
        while (rest % 5 == 0) { radices.Add(5); rest /= 5; }
        if (rest != 1)
            throw new ArgumentOutOfRangeException(nameof(n), n, "FFT length must be 2^a·3^b·5^c.");

        _radices = radices.ToArray();
        _twiddles = new T[_radices.Length][];
        int l = 1;
        for (int s = 0; s < _radices.Length; s++)
        {
            int p = _radices[s];
            int span = l * p;
            var tw = new T[2 * l * (p - 1)];
            for (int k = 0; k < l; k++)
            {
                for (int r = 1; r < p; r++)
                {
                    // Reduce k·r mod span first so the angle stays small and exact.
                    double angle = -2.0 * Math.PI * ((k * r) % span) / span;
                    int idx = 2 * ((k * (p - 1)) + (r - 1));
                    tw[idx] = T.CreateTruncating(Math.Cos(angle));
                    tw[idx + 1] = T.CreateTruncating(Math.Sin(angle));
                }
            }

            _twiddles[s] = tw;
            l *= p;
        }

        _bufA = new T[2 * n];
        _bufB = new T[2 * n];
    }

    public int Length => _n;

    /// <summary>Transforms interleaved complex data. <paramref name="input"/> and
    /// <paramref name="output"/> may be the same span.</summary>
    public void Transform(ReadOnlySpan<T> input, Span<T> output, bool backward)
    {
        if (input.Length != 2 * _n)
            throw new ArgumentException($"Expected {_n} complex values.", nameof(input));
        if (output.Length != 2 * _n)
            throw new ArgumentException($"Expected {_n} complex values.", nameof(output));

        // Backward = conj(forward(conj(x))): one butterfly set serves both signs.
        T[] src = _bufA;
        T[] dst = _bufB;
        input.CopyTo(src);
        if (backward)
            Conjugate(src);

        int l = 1;
        for (int s = 0; s < _radices.Length; s++)
        {
            int p = _radices[s];
            switch (p)
            {
                case 2: Radix2(src, dst, l, _twiddles[s]); break;
                case 3: Radix3(src, dst, l, _twiddles[s]); break;
                case 4: Radix4(src, dst, l, _twiddles[s]); break;
                default: Radix5(src, dst, l, _twiddles[s]); break;
            }

            (src, dst) = (dst, src);
            l *= p;
        }

        src.AsSpan().CopyTo(output);
        if (backward)
            Conjugate(output);
    }

    private static void Conjugate(Span<T> data)
    {
        for (int i = 1; i < data.Length; i += 2)
            data[i] = -data[i];
    }

    // Stockham stage: butterfly j = q·l + k reads x[j + r·n/p], twiddles by exp(-2πi·k·r/(l·p)), writes y[q·l·p + k + r·l].
    private void Radix2(T[] x, T[] y, int l, T[] tw)
    {
        int m = _n / 2;
        for (int q = 0; q < m / l; q++)
        {
            for (int k = 0; k < l; k++)
            {
                int j = (q * l) + k;
                int t = 2 * k;
                T a0r = x[2 * j], a0i = x[(2 * j) + 1];
                Mul(x[2 * (j + m)], x[(2 * (j + m)) + 1], tw[t], tw[t + 1], out T a1r, out T a1i);
                int d = (q * l * 2) + k;
                y[2 * d] = a0r + a1r;
                y[(2 * d) + 1] = a0i + a1i;
                y[2 * (d + l)] = a0r - a1r;
                y[(2 * (d + l)) + 1] = a0i - a1i;
            }
        }
    }

    private void Radix3(T[] x, T[] y, int l, T[] tw)
    {
        int m = _n / 3;
        for (int q = 0; q < m / l; q++)
        {
            for (int k = 0; k < l; k++)
            {
                int j = (q * l) + k;
                int t = 4 * k;
                T a0r = x[2 * j], a0i = x[(2 * j) + 1];
                Mul(x[2 * (j + m)], x[(2 * (j + m)) + 1], tw[t], tw[t + 1], out T a1r, out T a1i);
                Mul(x[2 * (j + (2 * m))], x[(2 * (j + (2 * m))) + 1], tw[t + 2], tw[t + 3], out T a2r, out T a2i);

                T t1r = a1r + a2r, t1i = a1i + a2i;
                T t2r = a0r - (Half * t1r), t2i = a0i - (Half * t1i);
                T t3r = Sin60 * (a1r - a2r), t3i = Sin60 * (a1i - a2i);

                int d = (q * l * 3) + k;
                y[2 * d] = a0r + t1r;
                y[(2 * d) + 1] = a0i + t1i;
                y[2 * (d + l)] = t2r - t3i;
                y[(2 * (d + l)) + 1] = t2i + t3r;
                y[2 * (d + (2 * l))] = t2r + t3i;
                y[(2 * (d + (2 * l))) + 1] = t2i - t3r;
            }
        }
    }

    private void Radix4(T[] x, T[] y, int l, T[] tw)
    {
        int m = _n / 4;
        for (int q = 0; q < m / l; q++)
        {
            for (int k = 0; k < l; k++)
            {
                int j = (q * l) + k;
                int t = 6 * k;
                T a0r = x[2 * j], a0i = x[(2 * j) + 1];
                Mul(x[2 * (j + m)], x[(2 * (j + m)) + 1], tw[t], tw[t + 1], out T a1r, out T a1i);
                Mul(x[2 * (j + (2 * m))], x[(2 * (j + (2 * m))) + 1], tw[t + 2], tw[t + 3], out T a2r, out T a2i);
                Mul(x[2 * (j + (3 * m))], x[(2 * (j + (3 * m))) + 1], tw[t + 4], tw[t + 5], out T a3r, out T a3i);

                T t0r = a0r + a2r, t0i = a0i + a2i;
                T t1r = a0r - a2r, t1i = a0i - a2i;
                T t2r = a1r + a3r, t2i = a1i + a3i;
                T t3r = a1r - a3r, t3i = a1i - a3i;

                int d = (q * l * 4) + k;
                y[2 * d] = t0r + t2r;
                y[(2 * d) + 1] = t0i + t2i;
                y[2 * (d + l)] = t1r + t3i;
                y[(2 * (d + l)) + 1] = t1i - t3r;
                y[2 * (d + (2 * l))] = t0r - t2r;
                y[(2 * (d + (2 * l))) + 1] = t0i - t2i;
                y[2 * (d + (3 * l))] = t1r - t3i;
                y[(2 * (d + (3 * l))) + 1] = t1i + t3r;
            }
        }
    }

    private void Radix5(T[] x, T[] y, int l, T[] tw)
    {
        int m = _n / 5;
        for (int q = 0; q < m / l; q++)
        {
            for (int k = 0; k < l; k++)
            {
                int j = (q * l) + k;
                int t = 8 * k;
                T a0r = x[2 * j], a0i = x[(2 * j) + 1];
                Mul(x[2 * (j + m)], x[(2 * (j + m)) + 1], tw[t], tw[t + 1], out T a1r, out T a1i);
                Mul(x[2 * (j + (2 * m))], x[(2 * (j + (2 * m))) + 1], tw[t + 2], tw[t + 3], out T a2r, out T a2i);
                Mul(x[2 * (j + (3 * m))], x[(2 * (j + (3 * m))) + 1], tw[t + 4], tw[t + 5], out T a3r, out T a3i);
                Mul(x[2 * (j + (4 * m))], x[(2 * (j + (4 * m))) + 1], tw[t + 6], tw[t + 7], out T a4r, out T a4i);

                T s1r = a1r + a4r, s1i = a1i + a4i;
                T b1r = a1r - a4r, b1i = a1i - a4i;
                T s2r = a2r + a3r, s2i = a2i + a3i;
                T b2r = a2r - a3r, b2i = a2i - a3i;

                T p1r = a0r + (C1 * s1r) + (C2 * s2r), p1i = a0i + (C1 * s1i) + (C2 * s2i);
                T q1r = (S1 * b1r) + (S2 * b2r), q1i = (S1 * b1i) + (S2 * b2i);
                T p2r = a0r + (C2 * s1r) + (C1 * s2r), p2i = a0i + (C2 * s1i) + (C1 * s2i);
                T q2r = (S2 * b1r) - (S1 * b2r), q2i = (S2 * b1i) - (S1 * b2i);

                int d = (q * l * 5) + k;
                y[2 * d] = a0r + s1r + s2r;
                y[(2 * d) + 1] = a0i + s1i + s2i;
                // y1 = p1 - i·q1, y4 = p1 + i·q1, y2 = p2 - i·q2, y3 = p2 + i·q2
                y[2 * (d + l)] = p1r + q1i;
                y[(2 * (d + l)) + 1] = p1i - q1r;
                y[2 * (d + (4 * l))] = p1r - q1i;
                y[(2 * (d + (4 * l))) + 1] = p1i + q1r;
                y[2 * (d + (2 * l))] = p2r + q2i;
                y[(2 * (d + (2 * l))) + 1] = p2i - q2r;
                y[2 * (d + (3 * l))] = p2r - q2i;
                y[(2 * (d + (3 * l))) + 1] = p2i + q2r;
            }
        }
    }

    private static void Mul(T xr, T xi, T wr, T wi, out T yr, out T yi)
    {
        yr = (xr * wr) - (xi * wi);
        yi = (xr * wi) + (xi * wr);
    }
}

/// <summary>Double-precision FFT plan (QSSTV TX: FFTW double). See <see cref="MixedRadixFft{T}"/>
/// for sign and scaling conventions.</summary>
internal sealed class FftPlan
{
    private readonly MixedRadixFft<double> _core;

    public FftPlan(int n) => _core = new MixedRadixFft<double>(n);

    public int Length => _core.Length;

    /// <summary>FFTW_FORWARD: X[k] = Σ x[j]·exp(-2πi·jk/n), unscaled.</summary>
    public void Forward(ReadOnlySpan<Complex> input, Span<Complex> output) =>
        _core.Transform(MemoryMarshal.Cast<Complex, double>(input), MemoryMarshal.Cast<Complex, double>(output), backward: false);

    /// <summary>FFTW_BACKWARD: x[j] = Σ X[k]·exp(+2πi·jk/n), unscaled.</summary>
    public void Backward(ReadOnlySpan<Complex> input, Span<Complex> output) =>
        _core.Transform(MemoryMarshal.Cast<Complex, double>(input), MemoryMarshal.Cast<Complex, double>(output), backward: true);
}

/// <summary>Single-precision FFT plan (QSSTV RX: fftwf). Same conventions as <see cref="FftPlan"/>.</summary>
internal sealed class FftPlanF
{
    private readonly MixedRadixFft<float> _core;

    public FftPlanF(int n) => _core = new MixedRadixFft<float>(n);

    public int Length => _core.Length;

    /// <summary>FFTW_FORWARD, unscaled.</summary>
    public void Forward(ReadOnlySpan<ComplexF> input, Span<ComplexF> output) =>
        _core.Transform(MemoryMarshal.Cast<ComplexF, float>(input), MemoryMarshal.Cast<ComplexF, float>(output), backward: false);

    /// <summary>FFTW_BACKWARD, unscaled.</summary>
    public void Backward(ReadOnlySpan<ComplexF> input, Span<ComplexF> output) =>
        _core.Transform(MemoryMarshal.Cast<ComplexF, float>(input), MemoryMarshal.Cast<ComplexF, float>(output), backward: true);
}
