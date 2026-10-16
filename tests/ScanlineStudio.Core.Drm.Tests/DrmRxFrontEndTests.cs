using System.Diagnostics;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5a: the front-end facade (path choice, scaling, stripes, reset, streaming, allocation, CPU).</summary>
public sealed class DrmRxFrontEndTests(ITestOutputHelper output)
{
    public static readonly TheoryData<int> StreamRates = new() { 48000, 12000, 8000, 11025, 44100 };

    [Theory]
    [InlineData(48000, nameof(DrmRxFrontEndPath.Decimate48k))]
    [InlineData(12000, nameof(DrmRxFrontEndPath.Passthrough12k))]
    [InlineData(8000, nameof(DrmRxFrontEndPath.Resample))]
    [InlineData(11025, nameof(DrmRxFrontEndPath.Resample))]
    [InlineData(47999, nameof(DrmRxFrontEndPath.Resample))]
    [InlineData(48500, nameof(DrmRxFrontEndPath.Resample))]
    public void Constructor_SelectsPathByRate(int rate, string expected)
    {
        var fe = new DrmRxFrontEnd(rate);
        Assert.Equal(expected, fe.Path.ToString());
        Assert.Equal(rate, fe.CaptureRate);
        Assert.True(DrmRxFrontEnd.IsSupportedRate(rate));
    }

    [Theory]
    [InlineData(7999)]
    [InlineData(5000)]
    [InlineData(0)]
    [InlineData(-48000)]
    public void Constructor_BelowMinimumRate_Throws(int rate)
    {
        Assert.False(DrmRxFrontEnd.IsSupportedRate(rate));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DrmRxFrontEnd(rate));
    }

    [Fact]
    public void Passthrough_ScalesToInt16AndReblocksTo1024()
    {
        var fe = new DrmRxFrontEnd(12000);
        var x = new float[3000];
        for (int i = 0; i < x.Length; i++)
            x[i] = (i % 200 - 100) / 128f;
        var stripes = Collect(fe, x, 0);
        Assert.Equal(2, stripes.Count);
        for (int i = 0; i < 2048; i++)
            Assert.Equal(x[i] * 32768.0, stripes[i / 1024][i % 1024]);
    }

    [Fact]
    public void Decimator_IsFedAtInt16Scale()
    {
        // A full-scale DC input settles to 32768 × (Σ normalized taps = 1).
        var fe = new DrmRxFrontEnd(48000);
        var x = new float[8192];
        Array.Fill(x, 1f);
        var stripes = Collect(fe, x, 0);
        Assert.Equal(2, stripes.Count);
        Assert.Equal(32768.0, stripes[1][1023], 6);
    }

    [Theory]
    [MemberData(nameof(StreamRates))]
    public void RandomBlockSizes_GiveTheSameStripesAsOneBlock(int rate)
    {
        float[] x = NoiseF(rate, 4 * rate);
        var whole = Collect(new DrmRxFrontEnd(rate), x, 0);
        var split = Collect(new DrmRxFrontEnd(rate), x, rate);
        Assert.True(whole.Count >= 40);
        Assert.Equal(whole.Count, split.Count);
        for (int s = 0; s < whole.Count; s++)
            Assert.Equal(whole[s], split[s]);
    }

    [Theory]
    [MemberData(nameof(StreamRates))]
    public void Reset_GivesTheSameOutputAsAFreshInstance(int rate)
    {
        float[] x = NoiseF(rate + 1, 2 * rate + 777);
        var fe = new DrmRxFrontEnd(rate);
        _ = Collect(fe, NoiseF(rate + 2, rate + 333), 0);
        fe.Reset();
        var afterReset = Collect(fe, x, 0);
        var fresh = Collect(new DrmRxFrontEnd(rate), x, 0);
        Assert.Equal(fresh.Count, afterReset.Count);
        for (int s = 0; s < fresh.Count; s++)
            Assert.Equal(fresh[s], afterReset[s]);
    }

    [Theory]
    [MemberData(nameof(StreamRates))]
    public void Push_AfterWarmUp_DoesNotAllocate(int rate)
    {
        var fe = new DrmRxFrontEnd(rate);
        float[] x = NoiseF(rate, 10000);
        double sum = 0;
        DrmStripeHandler handler = s => sum += s[0];
        for (int i = 0; i < 4; i++)
            fe.Push(x.AsSpan(0, 512 + 1000 * i), handler);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
            fe.Push(x.AsSpan(i % 100, 401 + 37 * (i % 50)), handler);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.True(double.IsFinite(sum));
    }

    /// <summary>CPU cost per 1024-sample stripe at 11025 and 44100 Hz (reported; asserted below 1 % of one core in Release, 10 % in Debug).</summary>
    [Theory]
    [InlineData(11025)]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Cpu_PerStripe(int rate)
    {
        float[] x = NoiseF(rate, 30 * rate);
        int stripes = 0;
        DrmStripeHandler handler = _ => stripes++;
        // warm up past tiered JIT (call-count threshold plus its background delay) before timing
        for (int w = 0; w < 3; w++)
        {
            var warm = new DrmRxFrontEnd(rate);
            for (int pos = 0; pos < x.Length; pos += 2048)
                warm.Push(x.AsSpan(pos, Math.Min(2048, x.Length - pos)), handler);
            Thread.Sleep(150);
        }

        var fe = new DrmRxFrontEnd(rate);
        stripes = 0;
        var sw = Stopwatch.StartNew();
        for (int pos = 0; pos < x.Length; pos += 2048)
            fe.Push(x.AsSpan(pos, Math.Min(2048, x.Length - pos)), handler);
        sw.Stop();
        double usPerStripe = sw.Elapsed.TotalMicroseconds / stripes;
        double realTimeUs = 1024.0 / 12000 * 1e6;
        double load = usPerStripe / realTimeUs * 100;
        output.WriteLine($"R={rate}: {usPerStripe:F1} µs per 1024 output samples ({load:F3} % of one core)");
#if DEBUG
        const double limit = 10; // unoptimized JIT; the Release build is the real measurement
#else
        const double limit = 1;
#endif
        Assert.True(load < limit, $"{load:F2} % of one core");
    }

    private static List<double[]> Collect(DrmRxFrontEnd fe, float[] x, int seed)
    {
        var stripes = new List<double[]>();
        DrmStripeHandler handler = s => stripes.Add(s.ToArray());
        if (seed == 0)
        {
            fe.Push(x, handler);
            return stripes;
        }

        var rng = new Random(seed);
        for (int pos = 0; pos < x.Length;)
        {
            int n = Math.Min(x.Length - pos, rng.Next(1, 9000));
            fe.Push(x.AsSpan(pos, n), handler);
            pos += n;
        }

        return stripes;
    }

    private static float[] NoiseF(int seed, int n)
    {
        var rng = new Random(seed);
        var x = new float[n];
        for (int i = 0; i < n; i++)
            x[i] = (float)(rng.NextDouble() * 1.6 - 0.8);
        return x;
    }
}
