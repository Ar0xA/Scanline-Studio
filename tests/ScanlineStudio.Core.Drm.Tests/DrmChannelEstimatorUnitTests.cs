using System.Diagnostics;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5c pieces in isolation: our LU (pivot rule, accuracy, singular input), the FAC map, the
/// out-of-range occupancy code, the SNR resync rule on signal loss, and CPU of the P5a+P5b+P5c chain.</summary>
public sealed class DrmChannelEstimatorUnitTests(ITestOutputHelper output)
{
    [Fact]
    public void Lu_PicksTheLargestScaledPivot_LastRowOnTie()
    {
        // Column 0 scaled scores: row0 1/2, row1 1, row2 1 -> the last of the tied rows (2) is the pivot.
        float[] a = [1, 2, 0, 1, 1, 0, 1, 1, 1];
        var pivot = new int[3];
        DrmLuSolver.Decompose(a, 3, 3, pivot, new float[3]);
        Assert.Equal(2, pivot[0]);

        float[] swap = [0, 1, 1, 0];
        var p2 = new int[2];
        DrmLuSolver.Decompose(swap, 2, 2, p2, new float[2]);
        Assert.Equal([1, 1], p2);
    }

    [Fact]
    public void Lu_ZeroPivotIsReplaced_AndAZeroRowThrows()
    {
        float[] a = [1, 1, 1, 1];
        var pivot = new int[2];
        DrmLuSolver.Decompose(a, 2, 2, pivot, new float[2]);
        Assert.Equal(1.0e-20f, a[3]);

        float[] singular = [1, 2, 0, 0];
        Assert.Throws<InvalidOperationException>(() => DrmLuSolver.Decompose(singular, 2, 2, new int[2], new float[2]));
    }

    /// <summary>Float inverse of Wiener-like (sinc correlation + noise) and random general matrices against a
    /// double reference: max |A·X − I| stays at float rounding level.</summary>
    [Theory]
    [InlineData(28, true)]
    [InlineData(62, true)]
    [InlineData(40, false)]
    public void Lu_InverseMatchesDoubleReference(int n, bool wienerLike)
    {
        var rng = new Random(n);
        var a = new float[n * n];
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double v = wienerLike ? Sinc((i - j) * 0.17) * Sinc(((i * 7) % 5 - (j * 7) % 5) * 0.03) : rng.NextDouble() * 2 - 1;
                a[i * n + j] = (float)v;
            }

            if (wienerLike)
                a[i * n + i] += 0.05f;
        }

        float[] work = (float[])a.Clone();
        var inv = new float[n * n];
        DrmLuSolver.Invert(work, n, n, inv, new int[n], new float[n]);
        double worst = 0;
        for (int i = 0; i < n; i++)
        {
            for (int j = 0; j < n; j++)
            {
                double s = 0;
                for (int k = 0; k < n; k++)
                    s += (double)a[i * n + k] * inv[k * n + j];
                worst = Math.Max(worst, Math.Abs(s - (i == j ? 1 : 0)));
            }
        }

        output.WriteLine($"n={n} wiener={wienerLike}: max |A·inv - I| = {worst:E2}");
        Assert.True(worst < 1e-3, $"{worst:E2}");
    }

    [Fact]
    public void ModeAndOccupancyCode_PastTheTableContinuesTheIdentity()
    {
        for (int i = 0; i < 14; i++)
            Assert.Equal(i, DrmChannelEstimator.ModeAndOccupancyCodeAt(i));
        Assert.Equal(14, DrmChannelEstimator.ModeAndOccupancyCodeAt(14));
        Assert.Equal(15, DrmChannelEstimator.ModeAndOccupancyCodeAt(15));
    }

    [Fact]
    public void FacCellMap_HasFortyFiveCellsPerMode_AndRejectsModeD()
    {
        var map = new int[65];
        for (int m = 0; m < 3; m++)
        {
            int tu = DrmRxTables.Tu[m];
            Assert.Equal(45, DrmFacCellMap.Build(m, tu / 2, tu, map));
            Assert.True(map.Take(45).Zip(map.Skip(1).Take(44)).All(p => p.First < p.Second), $"mode {m} not ascending");
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => DrmFacCellMap.Build(3, 56, 112, map));
    }

    /// <summary>The SNR rule on signal loss: init leaves SNR_timeout_counter at 0 (demodulator.cpp:83), so the first
    /// frame below 3 dB already requests re-acquisition, and the next stripe starts over (init, no consumption).</summary>
    [Fact]
    public void SignalLoss_FirstFrameBelow3dB_Resyncs()
    {
        double[] stripes = RxAcquisitionGoldenTests.FrontEnd(RxAcquisitionGoldenTests.LoadInput("A1_q4_high_long", out _));
        var estimator = new DrmChannelEstimator();
        var demod = new DrmDemodulator(estimator);
        int framesBefore = 0;
        for (int b = 0; b < 60 || estimator.FramesProcessed == framesBefore; b++)
        {
            framesBefore = estimator.FramesProcessed;
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
        }

        DrmDemodState st = demod.State;
        Assert.True(st.FrameSyncFlag);
        Assert.Equal(1, st.TransmissionFrameBufferDataValid);
        Assert.Equal(DrmRxTables.SnrTimeout, estimator.SnrTimeoutCounter);

        // Re-init through the frame stage's own reset, then feed silence: the next frames fall below 3 dB.
        demod.Reset();
        Assert.Equal(0, estimator.SnrTimeoutCounter);
        Assert.Equal(DrmRxTables.SnrTimeout, estimator.SnrTimeOutCounter);
        for (int b = 60; b < 120 && !st.FrameSyncFlag; b++)
            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
        Assert.True(st.FrameSyncFlag);

        int frames = estimator.FramesProcessed;
        var zero = new double[1024];
        int stripe = 0;
        while (estimator.FramesProcessed == frames || st.WMerFac >= DrmRxTables.SnrMinDb)
        {
            frames = estimator.FramesProcessed;
            demod.RunStripe(stripe++ < 40 ? stripes.AsSpan((120 + stripe) * 1024, 1024) : zero);
            Assert.True(stripe < 80, "no frame below 3 dB");
        }

        Assert.True(st.DoSynchronize);
        Assert.Equal(0, st.TransmissionFrameBufferDataValid);
        Assert.Equal(DrmRxTables.SnrTimeout, estimator.SnrTimeoutCounter);
        Assert.Equal(DrmRxTables.FacValidCounter - 1, estimator.FacNotValidCounter);
        Assert.Equal(Math.Max(0, DrmRxTables.SymbolsModeDetection * DrmRxTables.ModeDetectionStride - st.RsBufWidx), st.InputSamplesBufferRequest);
        demod.RunStripe(zero);
        Assert.False(st.TimeSyncFlag);
        Assert.False(st.DoSynchronize);
        Assert.Equal(1024, st.RsBufWidx);
    }

    /// <summary>CPU of the whole RX so far (P5a front end, P5b acquisition, P5c channel estimation, P6 channel decoding) per 1024-sample
    /// stripe at 11025 and 44100 Hz, on a DRM signal and on noise. Plan budget: below 10 % of one core.</summary>
    [Theory]
    [InlineData(11025)]
    [InlineData(44100)]
    public void Cpu_WholeRxThroughChannelDecoding(int rate)
    {
        short[] pcm = RxAcquisitionGoldenTests.LoadInput("B1_q16_high_long", out _);
        var toRate = new PolyphaseResampler(48000, rate);
        var src = new double[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            src[i] = pcm[i] / 32768.0;
        var y = new double[toRate.MaxOutput(src.Length)];
        int ny = toRate.Process(src, y);
        float[] drm = y.AsSpan(0, ny).ToArray().Select(v => (float)v).ToArray();
        var rng = new Random(9);
        float[] noise = Enumerable.Range(0, ny).Select(_ => (float)(rng.NextDouble() * 0.6 - 0.3)).ToArray();

        foreach ((string name, float[] x) in new[] { ("drm", drm), ("noise", noise) })
        {
            for (int w = 0; w < 3; w++)
            {
                Measure(rate, x, out _, out _, out _, out _);
                Thread.Sleep(150);
            }

            Measure(rate, x, out double meanUs, out double maxUs, out double maxFrameUs, out int valid);
            double stripeUs = 1024.0 / 12000 * 1e6;
            output.WriteLine($"R={rate} {name}: mean {meanUs:F1} µs/stripe ({meanUs / stripeUs * 100:F3} % of one core), worst stripe {maxUs:F1} µs ({maxUs / stripeUs * 100:F2} %), worst stripe with a frame {maxFrameUs:F1} µs, decoded MSC frames {valid}");
#if DEBUG
            const double limit = 30; // unoptimized JIT; the Release build is the real measurement
#else
            const double limit = 10;
#endif
            Assert.True(meanUs / stripeUs * 100 < limit, $"{name}: {meanUs / stripeUs * 100:F2} % of one core");
            if (name == "drm")
                Assert.True(valid > 30, $"only {valid} decoded MSC frames after resampling to {rate} Hz");
        }
    }

    private static void Measure(int rate, float[] x, out double meanUs, out double maxUs, out double maxFrameUs, out int valid)
    {
        var fe = new DrmRxFrontEnd(rate);
        var estimator = new DrmChannelEstimator();
        var decoder = new DrmChannelDecoder();
        var demod = new DrmDemodulator(new DrmRxFrameStage(estimator, decoder));
        double max = 0, maxFrame = 0;
        int stripes = 0, v = 0;
        DrmStripeHandler h = s =>
        {
            int frames = estimator.FramesProcessed;
            long t0 = Stopwatch.GetTimestamp();
            demod.RunStripe(s);
            double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            max = Math.Max(max, us);
            if (estimator.FramesProcessed != frames)
            {
                maxFrame = Math.Max(maxFrame, us);
                v += decoder.MscDataValid == 1 ? 1 : 0;
            }

            stripes++;
        };
        long start = Stopwatch.GetTimestamp();
        for (int pos = 0; pos < x.Length; pos += 2048)
            fe.Push(x.AsSpan(pos, Math.Min(2048, x.Length - pos)), h);
        meanUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds / stripes;
        maxUs = max;
        maxFrameUs = maxFrame;
        valid = v;
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);
}
