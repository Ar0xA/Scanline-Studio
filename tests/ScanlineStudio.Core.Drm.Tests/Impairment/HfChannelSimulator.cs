using System.Numerics;
using ScanlineStudio.Core.Drm.Dsp;

namespace ScanlineStudio.Core.Drm.Tests.Impairment;

/// <summary>An HF propagation model: one path (AWGN) or two equal Watterson paths with Gaussian Doppler spectra.</summary>
/// <param name="Name">Short name for reports.</param>
/// <param name="DelayMs">Delay of the second path; 0 for a single path.</param>
/// <param name="SpreadHz">Doppler spread, defined as 2σ of the Gaussian spectrum (CCIR 520 / ITU-R F.1487).</param>
internal sealed record HfChannel(string Name, double DelayMs, double SpreadHz)
{
    public static HfChannel Awgn { get; } = new("awgn", 0, 0);

    public static HfChannel Good { get; } = new("good", 0.5, 0.1);

    public static HfChannel Moderate { get; } = new("moderate", 1.0, 0.5);

    public static HfChannel Poor { get; } = new("poor", 2.0, 1.0);

    public static HfChannel LongDelay { get; } = new("long", 4.0, 2.0);

    public static IReadOnlyList<HfChannel> All { get; } = [Awgn, Good, Moderate, Poor, LongDelay];

    public bool IsFading => DelayMs > 0;
}

/// <summary>Per-run impairments other than the channel itself.</summary>
/// <param name="FrequencyOffsetHz">Static tuning offset.</param>
/// <param name="DriftHzPerMinute">Linear frequency drift.</param>
/// <param name="ClockPpm">Receiver sample-clock error: the RX samples at rate / (1 + ppm·1e-6) of the TX clock.</param>
/// <param name="StartOffsetSamples">Extra silence before the lead-in, so frame timing differs per run.</param>
/// <param name="LeadSeconds">Noise-only time before and after the transmission.</param>
internal sealed record Impairments(double FrequencyOffsetHz, double DriftHzPerMinute, double ClockPpm, int StartOffsetSamples, double LeadSeconds)
{
    public static Impairments None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>A seeded draw: ±50 Hz offset, ±1 Hz/min drift, ±200 ppm clock, 0..1 s start offset, 5 s lead.</summary>
    public static Impairments Draw(Random rng) => new(
        (rng.NextDouble() * 100) - 50, (rng.NextDouble() * 2) - 1, (rng.NextDouble() * 400) - 200, rng.Next(HfChannelSimulator.Rate), 5);
}

/// <summary>Applies an <see cref="HfChannel"/>, <see cref="Impairments"/> and AWGN to a 48 kHz transmission.</summary>
internal static class HfChannelSimulator
{
    public const int Rate = 48000;

    /// <summary>Bandwidth the SNR is defined in.</summary>
    public const double NoiseBandwidthHz = 2500;

    /// <summary>Tap-process update rate; interpolated linearly to <see cref="Rate"/>.</summary>
    public const double TapRate = 100;

    /// <summary>The received 48 kHz signal. <paramref name="snrDb"/> is mean signal power over the transmission against
    /// noise power in <see cref="NoiseBandwidthHz"/>; the result also returns where the transmission starts.</summary>
    public static (float[] Samples, int SignalStart) Apply(ReadOnlySpan<float> tx, HfChannel channel, double snrDb, Impairments imp, Random rng)
    {
        int lead = (int)(imp.LeadSeconds * Rate);
        int start = lead + imp.StartOffsetSamples;
        int total = start + tx.Length + lead + (int)(channel.DelayMs * Rate / 1000) + 64;
        var x = new float[total];
        tx.CopyTo(x.AsSpan(start));

        Complex[] a = Analytic(x);
        Complex[] y = channel.IsFading ? TwoPath(a, channel, rng) : a;
        double phase = 0;
        for (int n = 0; n < y.Length; n++)
        {
            double t = n / (double)Rate;
            double f = imp.FrequencyOffsetHz + (imp.DriftHzPerMinute * t / 60);
            phase += 2 * Math.PI * f / Rate;
            y[n] *= Complex.FromPolarCoordinates(1, phase);
        }

        float[] real = [.. y.Select(c => (float)c.Real)];
        float[] clocked = imp.ClockPpm == 0 ? real : Reclock(real, 1 + (imp.ClockPpm * 1e-6));
        int signalStart = (int)(start / (1 + (imp.ClockPpm * 1e-6)));
        int signalLength = (int)(tx.Length / (1 + (imp.ClockPpm * 1e-6)));

        double power = 0;
        for (int n = signalStart; n < signalStart + signalLength; n++)
            power += clocked[n] * (double)clocked[n];
        power /= signalLength;
        double noiseDensity = power / (Math.Pow(10, snrDb / 10) * NoiseBandwidthHz);
        double sigma = Math.Sqrt(noiseDensity * Rate / 2);
        for (int n = 0; n < clocked.Length; n++)
            clocked[n] += (float)(sigma * Gaussian(rng));
        return (clocked, signalStart);
    }

    /// <summary>The analytic signal x + j·H{x}, by FFT over the whole buffer.</summary>
    public static Complex[] Analytic(ReadOnlySpan<float> x)
    {
        int n = FastLength(x.Length);
        var buf = new Complex[n];
        for (int i = 0; i < x.Length; i++)
            buf[i] = x[i];
        var plan = new FftPlan(n);
        var spec = new Complex[n];
        plan.Forward(buf, spec);
        for (int k = 1; k < (n + 1) / 2; k++)
            spec[k] *= 2;
        for (int k = (n / 2) + 1; k < n; k++)
            spec[k] = 0;
        plan.Backward(spec, buf);
        var result = new Complex[x.Length];
        for (int i = 0; i < x.Length; i++)
            result[i] = buf[i] / n;
        return result;
    }

    /// <summary>Two equal-power paths, the second delayed, each scaled by its own unit-power tap process / √2.</summary>
    public static Complex[] TwoPath(Complex[] a, HfChannel channel, Random rng)
    {
        int delay = (int)Math.Round(channel.DelayMs * Rate / 1000);
        Complex[] g1 = TapProcess(a.Length, channel.SpreadHz, rng);
        Complex[] g2 = TapProcess(a.Length, channel.SpreadHz, rng);
        var y = new Complex[a.Length];
        double scale = 1 / Math.Sqrt(2);
        for (int n = 0; n < a.Length; n++)
        {
            Complex late = n >= delay ? a[n - delay] : Complex.Zero;
            y[n] = scale * ((g1[n] * a[n]) + (g2[n] * late));
        }

        return y;
    }

    /// <summary>A complex Gaussian tap process at <see cref="Rate"/> with E|g|² = 1 and a Gaussian Doppler spectrum of
    /// standard deviation spread/2: <see cref="SlowTapProcess"/> interpolated linearly.</summary>
    public static Complex[] TapProcess(int length, double spreadHz, Random rng)
    {
        Complex[] g = SlowTapProcess((int)Math.Ceiling(length / (double)Rate * TapRate) + 2, spreadHz, rng);
        var result = new Complex[length];
        for (int n = 0; n < length; n++)
        {
            double pos = n * TapRate / Rate;
            int i = (int)pos;
            double frac = pos - i;
            result[n] = (g[i] * (1 - frac)) + (g[i + 1] * frac);
        }

        return result;
    }

    /// <summary>The tap process at <see cref="TapRate"/>: complex white noise through a Gaussian FIR normalised to unit
    /// energy. A filter h(t) = exp(−t²/2τ²) with τ = 1/(2πσ√2) gives a power spectrum exp(−f²/2σ²).</summary>
    public static Complex[] SlowTapProcess(int length, double spreadHz, Random rng)
    {
        double sigma = spreadHz / 2;
        double tauH = 1 / (2 * Math.PI * sigma * Math.Sqrt(2));
        int half = (int)Math.Ceiling(4 * tauH * TapRate);
        var h = new double[(2 * half) + 1];
        double energy = 0;
        for (int k = -half; k <= half; k++)
        {
            double t = k / TapRate;
            h[k + half] = Math.Exp(-t * t / (2 * tauH * tauH));
            energy += h[k + half] * h[k + half];
        }

        double norm = 1 / Math.Sqrt(energy);
        var white = new Complex[length + h.Length];
        for (int i = 0; i < white.Length; i++)
            white[i] = new Complex(Gaussian(rng), Gaussian(rng)) / Math.Sqrt(2);
        var g = new Complex[length];
        for (int i = 0; i < length; i++)
        {
            Complex acc = 0;
            for (int k = 0; k < h.Length; k++)
                acc += h[k] * white[i + k];
            g[i] = acc * norm;
        }

        return g;
    }

    /// <summary>The signal as sampled by a clock 1/<paramref name="ratio"/> times the TX clock: y[n] = x(n·ratio) (cubic Lagrange).</summary>
    public static float[] Reclock(float[] x, double ratio)
    {
        var y = new float[(int)((x.Length - 3) / ratio)];
        for (int n = 0; n < y.Length; n++)
        {
            double pos = n * ratio;
            int i = (int)pos;
            double d = pos - i;
            float p0 = i > 0 ? x[i - 1] : 0, p1 = x[i], p2 = x[i + 1], p3 = x[i + 2];
            y[n] = (float)((p0 * (-d * (d - 1) * (d - 2) / 6)) + (p1 * ((d + 1) * (d - 1) * (d - 2) / 2))
                + (p2 * (-(d + 1) * d * (d - 2) / 2)) + (p3 * ((d + 1) * d * (d - 1) / 6)));
        }

        return y;
    }

    public static double Gaussian(Random rng)
    {
        double u1 = 1 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    /// <summary>The smallest 2^a·3^b·5^c ≥ <paramref name="n"/> (the FFT's supported lengths).</summary>
    public static int FastLength(int n)
    {
        for (int m = Math.Max(n, 1); ; m++)
        {
            int r = m;
            foreach (int p in new[] { 2, 3, 5 })
            {
                while (r % p == 0)
                    r /= p;
            }

            if (r == 1)
                return m;
        }
    }
}
