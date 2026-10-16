using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5a: the capture-rate resampler's measured response, with swept tones.</summary>
public sealed class PolyphaseResamplerTests(ITestOutputHelper output)
{
    public static readonly TheoryData<int> Rates = new() { 8000, 11025, 11111, 22050, 44100, 47999 };

    private const int OutRate = 12000;
    private const double SecondsPerTone = 0.25;

    /// <summary>Pass band 0-2.8 kHz ripple ≤ 0.1 dB; images/aliases of pass-band tones ≥ 60 dB down;
    /// stop band from 3.5 kHz (aliases included) ≥ 60 dB down; phase on the output timeline constant
    /// (zero), i.e. a constant group delay.</summary>
    [Theory]
    [MemberData(nameof(Rates))]
    public void SweptTones_MeetPassbandStopbandImageAndDelaySpec(int rate)
    {
        double maxGainDb = double.MinValue, minGainDb = double.MaxValue, worstImageDb = double.MinValue, maxPhase = 0;
        for (double f = 25; f <= 2800; f += 25)
        {
            (double gain, double phase, double residual) = Measure(rate, f);
            double g = 20 * Math.Log10(gain);
            maxGainDb = Math.Max(maxGainDb, g);
            minGainDb = Math.Min(minGainDb, g);
            worstImageDb = Math.Max(worstImageDb, 20 * Math.Log10(residual / gain));
            maxPhase = Math.Max(maxPhase, Math.Abs(phase));
        }

        double worstStopDb = double.MinValue, worstStopF = 0;
        double nyquist = rate / 2.0;
        double step = Math.Max(23, (nyquist - 3500) / 150);
        for (double f = 3500; f < nyquist - 1; f += step)
        {
            double leak = 20 * Math.Log10(OutputRms(rate, f) / Math.Sqrt(0.5));
            if (leak > worstStopDb)
            {
                worstStopDb = leak;
                worstStopF = f;
            }
        }

        // Phase φ(f) ≈ 0 for all f means −dφ/dω, the group delay, is a constant (here 0 on the output timeline).
        double delaySpreadUs = maxPhase / (2 * Math.PI * 25) * 1e6;
        output.WriteLine($"R={rate}: taps={new PolyphaseResampler(rate, OutRate).TapCount}, passband {minGainDb:F5}..{maxGainDb:F5} dB, " +
                         $"image/alias of passband tones {worstImageDb:F1} dB, stopband worst {worstStopDb:F1} dB @ {worstStopF:F0} Hz, " +
                         $"max |phase| {maxPhase:E2} rad (≤ {delaySpreadUs:F3} µs delay spread)");
        Assert.True(maxGainDb <= 0.1 && minGainDb >= -0.1, $"ripple {minGainDb}..{maxGainDb} dB");
        Assert.True(worstImageDb <= -60, $"image {worstImageDb} dB");
        Assert.True(worstStopDb <= -60, $"stopband {worstStopDb} dB at {worstStopF} Hz");
        Assert.True(maxPhase <= 1e-3, $"phase {maxPhase} rad");
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void RandomBlockSizes_GiveTheSameOutputAsOneBlock(int rate)
    {
        double[] x = Noise(rate, 3 * rate);
        var one = new PolyphaseResampler(rate, OutRate);
        var a = new double[one.MaxOutput(x.Length)];
        int na = one.Process(x, a);

        var split = new PolyphaseResampler(rate, OutRate);
        var b = new List<double>();
        var rng = new Random(rate);
        var buf = new double[split.MaxOutput(20000)];
        for (int pos = 0; pos < x.Length;)
        {
            int n = Math.Min(x.Length - pos, rng.Next(1, 20000));
            int m = split.Process(x.AsSpan(pos, n), buf);
            b.AddRange(buf.AsSpan(0, m).ToArray());
            pos += n;
        }

        Assert.Equal(na, b.Count);
        Assert.Equal(a.AsSpan(0, na).ToArray(), b.ToArray());
        // Output count follows the exact rate ratio.
        Assert.InRange(na, (long)x.Length * OutRate / rate - ((long)one.TapCount * OutRate / rate + 2), (long)x.Length * OutRate / rate + 1);
    }

    [Fact]
    public void Reset_RestartsTheTimeline()
    {
        double[] x = Noise(11025, 20000);
        var r = new PolyphaseResampler(11025, OutRate);
        var a = new double[r.MaxOutput(x.Length)];
        int na = r.Process(x, a);
        r.Reset();
        var b = new double[a.Length];
        int nb = r.Process(x, b);
        Assert.Equal(na, nb);
        Assert.Equal(a, b);
    }

    /// <summary>Cross-check (reported, loosely asserted): a golden 48 kHz TX stream through (a) the ported
    /// QSSTV decimator and (b) an independent 48000→11025 conversion followed by our 11025→12000 resampler.</summary>
    [Fact]
    public void CrossCheck_DecimatorVersus11025Resampled_HighSnr()
    {
        const string caseId = "A1_q4_high_long";
        short[] pcm = RxFrontEndGoldenTests.LoadOut48k(caseId);
        // 6 s of the stream keeps the table-free reference conversion fast
        var x48 = new double[6 * 48000];
        for (int i = 0; i < x48.Length; i++)
            x48[i] = pcm[i];

        var dec = new QsstvDecimator4();
        var a = new double[QsstvDecimator4.MaxOutput(x48.Length)];
        int na = dec.Process(x48, a);

        // QSSTV's output m is Σ h[i]·x[4m+3−i]; its delay in 48 kHz samples is the taps' group delay.
        double delay48 = GroupDelaySamples(dec.Taps, 1000.0 / 48000);
        double offset = (3 - delay48) / 48000.0;
        double[] x11 = ReferenceResample(x48, 48000, 11025, offset);

        var res = new PolyphaseResampler(11025, OutRate);
        var b = new double[res.MaxOutput(x11.Length)];
        int nb = res.Process(x11, b);

        int n = Math.Min(na, nb);
        const int edge = 2000;
        double snrFull = Snr(a, b, edge, n - edge);

        // The two paths' filters differ only near and above 2.8 kHz (QSSTV: −1 dB at 2.8 kHz, −26.5 dB at
        // 3.5 kHz; ours: flat to 2.8 kHz, −80 dB from 3.5 kHz) and the TX signal carries about −26 dB of its
        // power at 2.8-3.5 kHz, so the in-band SNR applies one common zero-phase 2.3 kHz low-pass to both.
        double[] la = ZeroPhaseLowPass(a.AsSpan(0, n), 2300.0 / OutRate);
        double[] lb = ZeroPhaseLowPass(b.AsSpan(0, n), 2300.0 / OutRate);
        double snrInBand = Snr(la, lb, edge, n - edge);

        // QSSTV's taps are not exactly linear phase (group delay 89.46-89.51 samples over 0.5-2.5 kHz), which
        // bounds (a) vs (b). The resampler chain alone is checked against the same reference run 48000→12000.
        double[] c = ReferenceResample(x48, 48000, OutRate, offset);
        double[] lc = ZeroPhaseLowPass(c.AsSpan(0, n), 2300.0 / OutRate);
        double snrChain = Snr(lc, lb, edge, n - edge);
        output.WriteLine($"cross-check {caseId}: QSSTV delay {delay48:F3} samples @48k, {n - 2 * edge} samples; " +
                         $"(a) vs (b): SNR full band {snrFull:F1} dB, in band (0-2.3 kHz) {snrInBand:F1} dB; " +
                         $"reference 48k→12k vs (b), in band: {snrChain:F1} dB");
        Assert.True(snrFull > 30, $"full-band SNR {snrFull:F1} dB");
        Assert.True(snrInBand > 40, $"in-band SNR {snrInBand:F1} dB");
        Assert.True(snrChain > 60, $"chain SNR {snrChain:F1} dB");
    }

    private static double Snr(double[] a, double[] b, int from, int to)
    {
        double sig = 0, err = 0;
        for (int m = from; m < to; m++)
        {
            sig += a[m] * a[m];
            err += (a[m] - b[m]) * (a[m] - b[m]);
        }

        return 10 * Math.Log10(sig / err);
    }

    // Symmetric 801-tap Kaiser (β 10) low-pass at normalized cut-off fc, applied centred (zero phase).
    private static double[] ZeroPhaseLowPass(ReadOnlySpan<double> x, double fc)
    {
        const int half = 400;
        var h = new double[2 * half + 1];
        double i0b = I0(10);
        for (int k = -half; k <= half; k++)
        {
            double r = (double)k / (half + 1);
            double sinc = k == 0 ? 1 : Math.Sin(Math.PI * 2 * fc * k) / (Math.PI * 2 * fc * k);
            h[k + half] = 2 * fc * sinc * I0(10 * Math.Sqrt(1 - r * r)) / i0b;
        }

        var y = new double[x.Length];
        for (int m = half; m < x.Length - half; m++)
        {
            double s = 0;
            for (int k = -half; k <= half; k++)
                s += h[k + half] * x[m - k];
            y[m] = s;
        }

        return y;
    }

    // Group delay of an FIR (index 0 = newest), from the phase slope at normalized frequency f0 (cycles/sample).
    private static double GroupDelaySamples(ReadOnlySpan<double> h, double f0)
    {
        double d = 1e-4;
        return -(Phase(h, f0 + d) - Phase(h, f0 - d)) / (2 * Math.PI * 2 * d);
    }

    private static double Phase(ReadOnlySpan<double> h, double f)
    {
        double re = 0, im = 0;
        for (int i = 0; i < h.Length; i++)
        {
            re += h[i] * Math.Cos(2 * Math.PI * f * i);
            im -= h[i] * Math.Sin(2 * Math.PI * f * i);
        }

        return Math.Atan2(im, re);
    }

    // Independent reference: direct Kaiser-windowed sinc (β 10, ±64 output periods, cut-off 4.5 kHz), evaluated
    // per output sample with no tables; output m is the band-limited input at time m/outRate + offset.
    private static double[] ReferenceResample(double[] x, int inRate, int outRate, double offset)
    {
        const double fc = 4500, beta = 10;
        double half = 64.0 / outRate * inRate;
        int count = (int)((long)x.Length * outRate / inRate);
        var y = new double[count];
        double i0b = I0(beta);
        for (int m = 0; m < count; m++)
        {
            double t = (m / (double)outRate + offset) * inRate;
            int lo = Math.Max(0, (int)Math.Ceiling(t - half));
            int hi = Math.Min(x.Length - 1, (int)Math.Floor(t + half));
            double s = 0;
            for (int n = lo; n <= hi; n++)
            {
                double u = t - n;
                double r = u / half;
                double w = I0(beta * Math.Sqrt(Math.Max(0, 1 - r * r))) / i0b;
                double arg = 2 * fc / inRate * u;
                double sinc = arg == 0 ? 1 : Math.Sin(Math.PI * arg) / (Math.PI * arg);
                s += x[n] * 2 * fc / inRate * sinc * w;
            }

            y[m] = s;
        }

        return y;
    }

    private static double I0(double x)
    {
        double sum = 1, term = 1, q = x * x / 4;
        for (int k = 1; k < 100 && term > sum * 1e-17; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
        }

        return sum;
    }

    // Unit-amplitude tone at f: least-squares fit at f on the output timeline (t = m/12000), after the warm-up.
    private static (double Gain, double Phase, double Residual) Measure(int rate, double f)
    {
        double[] y = RunTone(rate, f, out int skip);
        double cc = 0, ss = 0, cs = 0, yc = 0, ys = 0;
        for (int m = skip; m < y.Length; m++)
        {
            double w = 2 * Math.PI * f * m / OutRate;
            double c = Math.Cos(w), s = Math.Sin(w);
            cc += c * c;
            ss += s * s;
            cs += c * s;
            yc += y[m] * c;
            ys += y[m] * s;
        }

        double det = cc * ss - cs * cs;
        double a = (yc * ss - ys * cs) / det;
        double b = (ys * cc - yc * cs) / det;
        double res = 0;
        for (int m = skip; m < y.Length; m++)
        {
            double w = 2 * Math.PI * f * m / OutRate;
            double e = y[m] - a * Math.Cos(w) - b * Math.Sin(w);
            res += e * e;
        }

        // input cos(ωt) → output A·cos(ωt + φ) = a·cos − b·sin with a = A cos φ, b = −A sin φ
        return (Math.Sqrt(a * a + b * b), Math.Atan2(-b, a), Math.Sqrt(res / (y.Length - skip)) * Math.Sqrt(2));
    }

    private static double OutputRms(int rate, double f)
    {
        double[] y = RunTone(rate, f, out int skip);
        double p = 0;
        for (int m = skip; m < y.Length; m++)
            p += y[m] * y[m];
        return Math.Sqrt(p / (y.Length - skip));
    }

    private static double[] RunTone(int rate, double f, out int skip)
    {
        int n = (int)(rate * SecondsPerTone);
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = Math.Cos(2 * Math.PI * f * i / rate);
        var r = new PolyphaseResampler(rate, OutRate);
        var y = new double[r.MaxOutput(n)];
        int count = r.Process(x, y);
        // skip the start-up transient (one kernel length) and stop before the not-yet-filled tail
        skip = (int)Math.Ceiling((double)r.TapCount * OutRate / rate) + 2;
        return y[..count];
    }

    internal static double[] Noise(int seed, int n)
    {
        var rng = new Random(seed);
        var x = new double[n];
        for (int i = 0; i < n; i++)
            x[i] = rng.NextDouble() * 2 - 1;
        return x;
    }
}
