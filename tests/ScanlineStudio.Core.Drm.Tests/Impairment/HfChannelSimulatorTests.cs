using System.Numerics;
using ScanlineStudio.Core.Drm.Dsp;

namespace ScanlineStudio.Core.Drm.Tests.Impairment;

/// <summary>The channel model the DRM RX measurements rest on: each property checked against its definition.</summary>
public sealed class HfChannelSimulatorTests
{
    private const int Rate = HfChannelSimulator.Rate;

    private static float[] Tone(double hz, double amplitude, int n) =>
        [.. Enumerable.Range(0, n).Select(i => (float)(amplitude * Math.Cos(2 * Math.PI * hz * i / Rate)))];

    [Fact]
    public void Analytic_OfACosine_HasConstantMagnitude_AndTheSineAsImaginaryPart()
    {
        float[] x = Tone(1000, 0.5, Rate);
        Complex[] a = HfChannelSimulator.Analytic(x);

        for (int i = 2000; i < Rate - 2000; i += 97)
        {
            Assert.Equal(0.5, a[i].Magnitude, 3);
            Assert.Equal(0.5 * Math.Sin(2 * Math.PI * 1000 * i / Rate), a[i].Imaginary, 3);
        }
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void TapProcess_HasUnitPower_AndA2SigmaDopplerSpreadEqualToTheSpread(double spreadHz)
    {
        // Many coherence times, at the tap rate to keep the spectrum small.
        const int Seconds = 4000;
        Complex[] slow = HfChannelSimulator.SlowTapProcess(Seconds * (int)HfChannelSimulator.TapRate, spreadHz, new Random(11));
        double fs = HfChannelSimulator.TapRate;

        Assert.InRange(slow.Average(c => c.Magnitude * c.Magnitude), 0.85, 1.15);

        int n = HfChannelSimulator.FastLength(slow.Length);
        var buf = new Complex[n];
        slow.CopyTo(buf, 0);
        var spec = new Complex[n];
        new FftPlan(n).Forward(buf, spec);
        double sum = 0, second = 0;
        for (int k = 0; k < n; k++)
        {
            double f = (k <= n / 2 ? k : k - n) * fs / n;
            if (Math.Abs(f) > 10 * spreadHz)
                continue;
            double p = spec[k].Magnitude * spec[k].Magnitude;
            sum += p;
            second += p * f * f;
        }

        double twoSigma = 2 * Math.Sqrt(second / sum);
        Assert.InRange(twoSigma, 0.85 * spreadHz, 1.15 * spreadHz);
    }

    [Fact]
    public void TwoPath_PutsTheSecondPathAtTheDelay()
    {
        var a = new Complex[Rate / 10];
        a[100] = 1;
        Complex[] y = HfChannelSimulator.TwoPath(a, HfChannel.Poor, new Random(3));
        int delay = 2 * Rate / 1000;

        int[] nonZero = [.. Enumerable.Range(0, y.Length).Where(i => y[i].Magnitude > 1e-12)];
        Assert.Equal([100, 100 + delay], nonZero);
    }

    [Fact]
    public void Snr_IsSignalPowerOverNoiseIn2500Hz()
    {
        float[] tx = Tone(1500, 0.3, 10 * Rate);
        const double SnrDb = 10;
        (float[] rx, int start) = HfChannelSimulator.Apply(tx, HfChannel.Awgn, SnrDb, Impairments.None, new Random(7));

        int n = HfChannelSimulator.FastLength(rx.Length);
        var buf = new Complex[n];
        for (int i = 0; i < rx.Length; i++)
            buf[i] = rx[i] - (i >= start && i - start < tx.Length ? tx[i - start] : 0);
        var spec = new Complex[n];
        new FftPlan(n).Forward(buf, spec);
        double noiseIn2500 = 0;
        for (int k = 0; k < n / 2; k++)
        {
            double f = k * (double)Rate / n;
            if (f is >= 300 and < 2800)
                noiseIn2500 += 2 * spec[k].Magnitude * spec[k].Magnitude / ((double)n * rx.Length);
        }

        double signal = 0.3 * 0.3 / 2;
        Assert.InRange(10 * Math.Log10(signal / noiseIn2500), SnrDb - 0.3, SnrDb + 0.3);
    }

    [Fact]
    public void FrequencyOffset_And_Clock_MoveAToneWhereTheyShould()
    {
        float[] tx = Tone(1000, 0.5, 4 * Rate);
        // 5000 ppm, so a sign error (1030·1.005 vs 1030/1.005) is about 10 Hz off.
        var imp = new Impairments(FrequencyOffsetHz: 30, DriftHzPerMinute: 0, ClockPpm: 5000, StartOffsetSamples: 0, LeadSeconds: 0);
        (float[] rx, _) = HfChannelSimulator.Apply(tx, HfChannel.Awgn, 80, imp, new Random(1));

        // The RX clock runs slow by 1 + ppm·1e-6, so 1030 Hz at the TX clock reads as 1030·(1 + 5000e-6).
        double expected = 1030 * (1 + 5000e-6);
        int n = HfChannelSimulator.FastLength(rx.Length);
        var buf = new Complex[n];
        for (int i = 0; i < rx.Length; i++)
            buf[i] = rx[i] * (0.5 - (0.5 * Math.Cos(2 * Math.PI * i / rx.Length)));
        var spec = new Complex[n];
        new FftPlan(n).Forward(buf, spec);
        int peak = Enumerable.Range(1, n / 2).MaxBy(k => spec[k].Magnitude);
        Assert.InRange(peak * (double)Rate / n, expected - 0.5, expected + 0.5);
    }
}
