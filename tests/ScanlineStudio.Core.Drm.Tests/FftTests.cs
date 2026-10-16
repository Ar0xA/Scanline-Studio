using System.Numerics;
using ScanlineStudio.Core.Drm.Dsp;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>Mixed-radix FFT against a direct O(N^2) DFT computed in double.
/// Tolerances (relative RMS error over the whole output): double ≤ 1e-9, float ≤ 1e-5.</summary>
public class FftTests
{
    private const double DoubleTolerance = 1e-9;
    private const double FloatTolerance = 1e-5;

    public static TheoryData<int> DrmSizes => new() { 1152, 1024, 640, 288, 256, 160 };

    public static TheoryData<int> OtherSizes => new() { 1, 2, 3, 4, 5, 6, 8, 9, 12, 15, 25, 27, 30, 45, 60, 75, 90, 120, 125, 180, 243, 375, 512, 2048 };

    private static Complex[] RandomSignal(int n, int seed)
    {
        var rng = new Random(seed);
        var x = new Complex[n];
        for (int i = 0; i < n; i++)
            x[i] = new Complex((rng.NextDouble() * 2) - 1, (rng.NextDouble() * 2) - 1);
        return x;
    }

    // Direct DFT; sign -1 = FFTW_FORWARD, +1 = FFTW_BACKWARD. (j*k) mod n keeps the angle exact.
    private static Complex[] Dft(Complex[] x, int sign)
    {
        int n = x.Length;
        var y = new Complex[n];
        for (int k = 0; k < n; k++)
        {
            double re = 0, im = 0;
            for (int j = 0; j < n; j++)
            {
                double a = sign * 2.0 * Math.PI * (int)((long)j * k % n) / n;
                double c = Math.Cos(a), s = Math.Sin(a);
                re += (x[j].Real * c) - (x[j].Imaginary * s);
                im += (x[j].Real * s) + (x[j].Imaginary * c);
            }

            y[k] = new Complex(re, im);
        }

        return y;
    }

    private static double RelativeRms(IReadOnlyList<Complex> got, IReadOnlyList<Complex> want)
    {
        double err = 0, norm = 0;
        for (int i = 0; i < want.Count; i++)
        {
            err += (got[i] - want[i]).Magnitude * (got[i] - want[i]).Magnitude;
            norm += want[i].Magnitude * want[i].Magnitude;
        }

        return Math.Sqrt(err / norm);
    }

    private static void AssertDoubleMatchesDft(int n)
    {
        Complex[] x = RandomSignal(n, n);
        var plan = new FftPlan(n);
        var fwd = new Complex[n];
        var bwd = new Complex[n];
        plan.Forward(x, fwd);
        plan.Backward(x, bwd);
        double ef = RelativeRms(fwd, Dft(x, -1));
        double eb = RelativeRms(bwd, Dft(x, +1));
        Assert.True(ef <= DoubleTolerance, $"n={n} forward rel RMS {ef:E3}");
        Assert.True(eb <= DoubleTolerance, $"n={n} backward rel RMS {eb:E3}");
    }

    private static void AssertFloatMatchesDft(int n)
    {
        Complex[] x = RandomSignal(n, n + 7);
        // The reference sees exactly the float-rounded input.
        var xf = x.Select(c => new ComplexF((float)c.Real, (float)c.Imaginary)).ToArray();
        Complex[] xr = xf.Select(c => new Complex(c.Real, c.Imaginary)).ToArray();
        var plan = new FftPlanF(n);
        var fwd = new ComplexF[n];
        var bwd = new ComplexF[n];
        plan.Forward(xf, fwd);
        plan.Backward(xf, bwd);
        double ef = RelativeRms(fwd.Select(c => new Complex(c.Real, c.Imaginary)).ToArray(), Dft(xr, -1));
        double eb = RelativeRms(bwd.Select(c => new Complex(c.Real, c.Imaginary)).ToArray(), Dft(xr, +1));
        Assert.True(ef <= FloatTolerance, $"n={n} forward rel RMS {ef:E3}");
        Assert.True(eb <= FloatTolerance, $"n={n} backward rel RMS {eb:E3}");
    }

    [Theory]
    [MemberData(nameof(DrmSizes))]
    [MemberData(nameof(OtherSizes))]
    public void Double_MatchesDirectDft(int n) => AssertDoubleMatchesDft(n);

    [Theory]
    [MemberData(nameof(DrmSizes))]
    [MemberData(nameof(OtherSizes))]
    public void Float_MatchesDirectDft(int n) => AssertFloatMatchesDft(n);

    [Theory]
    [MemberData(nameof(DrmSizes))]
    public void ForwardThenBackward_IsIdentityTimesN(int n)
    {
        Complex[] x = RandomSignal(n, 99);
        var plan = new FftPlan(n);
        var y = new Complex[n];
        plan.Forward(x, y);
        plan.Backward(y, y);
        Assert.True(RelativeRms(y.Select(c => c / n).ToArray(), x) <= DoubleTolerance);

        var xf = x.Select(c => new ComplexF((float)c.Real, (float)c.Imaginary)).ToArray();
        var planF = new FftPlanF(n);
        var yf = new ComplexF[n];
        planF.Forward(xf, yf);
        planF.Backward(yf, yf);
        Complex[] back = yf.Select(c => new Complex(c.Real / n, c.Imaginary / n)).ToArray();
        Assert.True(RelativeRms(back, xf.Select(c => new Complex(c.Real, c.Imaginary)).ToArray()) <= FloatTolerance);
    }

    [Theory]
    [MemberData(nameof(DrmSizes))]
    public void Parseval_HoldsUnscaled(int n)
    {
        Complex[] x = RandomSignal(n, 3);
        var y = new Complex[n];
        new FftPlan(n).Forward(x, y);
        double ex = x.Sum(c => c.Magnitude * c.Magnitude);
        double ey = y.Sum(c => c.Magnitude * c.Magnitude);
        Assert.True(Math.Abs((ey / n) - ex) / ex <= 1e-12, $"n={n}: {ey / n} vs {ex}");
    }

    [Fact]
    public void SignConvention_MatchesFftw()
    {
        // FFTW_FORWARD maps exp(+2πi·3j/n) to bin 3; FFTW_BACKWARD maps a bin-3 impulse to exp(+2πi·3j/n).
        const int n = 160;
        var tone = new Complex[n];
        for (int j = 0; j < n; j++)
            tone[j] = Complex.FromPolarCoordinates(1, 2 * Math.PI * 3 * j / n);
        var plan = new FftPlan(n);
        var spec = new Complex[n];
        plan.Forward(tone, spec);
        Assert.Equal(n, spec[3].Real, 9);
        Assert.True(spec[n - 3].Magnitude < 1e-9);

        var impulse = new Complex[n];
        impulse[3] = Complex.One;
        var time = new Complex[n];
        plan.Backward(impulse, time);
        for (int j = 0; j < n; j++)
            Assert.True((time[j] - tone[j]).Magnitude < 1e-12);
    }

    [Fact]
    public void InPlace_GivesSameResultAsOutOfPlace()
    {
        Complex[] x = RandomSignal(1152, 5);
        var plan = new FftPlan(1152);
        var outOfPlace = new Complex[1152];
        plan.Forward(x, outOfPlace);
        plan.Forward(x, x);
        Assert.Equal(outOfPlace, x);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(176)]
    public void UnsupportedLength_Throws(int n)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FftPlan(n));
    }

    [Fact]
    public void Transform_DoesNotAllocate()
    {
        var plan = new FftPlan(1152);
        var x = RandomSignal(1152, 11);
        var y = new Complex[1152];
        plan.Forward(x, y);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++)
            plan.Forward(x, y);
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }
}
