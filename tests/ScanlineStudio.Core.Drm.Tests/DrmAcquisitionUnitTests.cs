using System.Diagnostics;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P5b pieces in isolation: tables, the moving-sum filters, the demodulator state machine and its
/// frame-stage seam, robustness on no signal, and CPU of the P5a+P5b chain.</summary>
public sealed class DrmAcquisitionUnitTests(ITestOutputHelper output)
{
    [Fact]
    public void Tables_HaveQsstvShapes()
    {
        // Tg_C = 64 although Ts_C - Tu_C = 80 (drmdefs.h:30-32); only demodulator's Tg/Tgh members see it.
        for (int m = 0; m < DrmRxTables.ModeCount; m++)
            Assert.Equal(m == 2 ? 224 : DrmRxTables.Ts[m], DrmRxTables.Tu[m] + DrmRxTables.Tg[m]);
        Assert.Equal(DrmRxTables.ModeCount * DrmRxTables.TimeRefCellColumns, DrmRxTables.TimeRefCellsK.Length);
        Assert.Equal(DrmRxTables.TimeRefCellsK.Length, DrmRxTables.TimeRefCellsTheta1024.Length);
        Assert.Equal(24, DrmRxTables.KMin.Length);
        Assert.Equal(24, DrmRxTables.KMax.Length);
        // drm.cpp:111 time_ref_cells_cnt_list: the non-zero prefix of each row.
        int[] counts = [16, 15, 8, 16];
        for (int m = 0; m < DrmRxTables.ModeCount; m++)
        {
            ReadOnlySpan<int> row = DrmRxTables.TimeRefCellsK.Slice(m * 21, 21);
            Assert.Equal(counts[m], row.IndexOf(0) < 0 ? 21 : row.IndexOf(0));
        }
    }

    [Fact]
    public void Filter1AndFilter1c_AreCausalFirWithPartialStart()
    {
        float[] coef = [0.5f, 1f, 1f, 0.5f];
        float[] x = [1, 2, 3, 4, 5, 6];
        var y = new float[x.Length];
        DrmModeDetector.Filter1(x, y, coef, x.Length, coef.Length);
        Assert.Equal([0.5f, 2f, 4.5f, 7.5f, 10.5f, 13.5f], y);

        float[] xc = [1, -1, 2, -2, 3, -3];
        var yc = new float[xc.Length];
        DrmModeDetector.Filter1c(xc, yc, coef, 3, coef.Length);
        Assert.Equal([0.5f, -0.5f, 2f, -2f, 4.5f, -4.5f], yc);
    }

    /// <summary>getfoffsint scores each rotation by the three pilot positions 9/27/36·Tu/288 together: a decoy
    /// with two of the three coherent carriers (at +9, +26, +36) must lose to the true one.</summary>
    [Fact]
    public void IntegerFrequencyOffset_NeedsAllThreePilots()
    {
        const int tu = 288, nSym = 15, truth = 150, decoy = 60;
        var rng = new Random(3);
        var sb = new float[2 * tu * nSym];
        var coherent = new HashSet<int> { truth + 9, truth + 27, truth + 36, decoy + 9, decoy + 26, decoy + 36 };
        for (int j = 0; j < tu; j++)
        {
            double step = rng.NextDouble() * 2 * Math.PI;
            for (int i = 0; i < nSym; i++)
            {
                double ph = coherent.Contains(j) ? step * i : rng.NextDouble() * 2 * Math.PI;
                sb[(j + i * tu) * 2] = (float)Math.Cos(ph);
                sb[(j + i * tu) * 2 + 1] = (float)Math.Sin(ph);
            }
        }

        float f = DrmPilotSearch.IntegerFrequencyOffset(sb, nSym, tu / 2, tu, tu);
        Assert.Equal((float)(2 * Math.PI * (truth - tu / 2)), f);
    }

    /// <summary>getofdm.cpp:213 adds <c>(delta_time_offset_integer / Tu) * freq_offset</c> with integer
    /// division, so a ±1 time step never moves the phase: Zi[1] is only advanced by Ts/Tu · freq_offset.</summary>
    [Theory]
    [InlineData(0.9f, 1)]
    [InlineData(-0.9f, -1)]
    public void TimeStep_DoesNotMovePhase_IntegerDivisionQuirk(float timeOffsetFractional, int expectedStep)
    {
        const int ts = 320, tu = 288;
        var ofdm = new DrmOfdmSymbolDemodulator();
        ofdm.SetMode(tu);
        float[] zi = [1.0f, 0.3f, 0.0f, 0.0f, 2.0f, timeOffsetFractional];
        var rs = new float[2 * (2 * ts + 4)];
        var symbol = new float[2 * tu];

        int step = ofdm.Demodulate(rs, 0, 0, 0, ts, tu, zi, symbol, timeSyncEnable: true, freqSyncEnable: false);

        Assert.Equal(expectedStep, step);
        Assert.Equal((0.3f + (float)ts / (float)tu * 2.0f) % (float)(2.0 * Math.PI), zi[1]);
        Assert.Equal(2.0f, zi[4]);
    }

    [Fact]
    public void Noise_NeverSyncs_AndBufferStaysBounded()
    {
        var stage = new GoldenResyncFrameStage(new HashSet<int>());
        var demod = new DrmDemodulator(stage);
        var rng = new Random(5);
        var stripe = new double[1024];
        int maxRs = 0, detections = 0, lastCounter = 0;
        for (int b = 0; b < 600; b++)
        {
            for (int i = 0; i < stripe.Length; i++)
                stripe[i] = (rng.NextDouble() - 0.5) * 20000;
            demod.RunStripe(stripe);
            DrmDemodState st = demod.State;
            Assert.False(st.TimeSyncFlag);
            maxRs = Math.Max(maxRs, st.RsBufWidx);
            if (st.IterationCounter != lastCounter + 1)
                Assert.Fail("iterationCounter must count every stripe while unsynced");
            lastCounter = st.IterationCounter;
            if (st.RobustnessMode == DrmRxTables.NoMode)
                detections++;
        }

        Assert.True(maxRs < 6400 + 1024, $"rs_buffer grew to {maxRs}");
        Assert.True(detections > 0);
        Assert.Equal(0, stage.FrameSyncCalls);
    }

    [Fact]
    public void Silence_NeverSyncs()
    {
        var demod = new DrmDemodulator(new GoldenResyncFrameStage(new HashSet<int>()));
        var stripe = new double[1024];
        for (int b = 0; b < 100; b++)
            demod.RunStripe(stripe);
        Assert.False(demod.State.TimeSyncFlag);
        Assert.Equal(DrmRxTables.NoMode, demod.State.RobustnessMode);
    }

    /// <summary>A resync request from the frame stage re-runs init on the next call (demodulator.cpp:116-120):
    /// flags cleared, buffer restarted with the new stripe, the frame stage reset, the mode kept.</summary>
    [Fact]
    public void FrameStageResync_ReinitialisesOnNextCall()
    {
        double[] stripes = RxAcquisitionGoldenTests.FrontEnd(RxAcquisitionGoldenTests.LoadInput("B1_q16_high_long", out _));
        var stage = new GoldenResyncFrameStage(new HashSet<int> { 20 });
        var demod = new DrmDemodulator(stage);
        int resetsBefore = 0;
        for (int b = 0; b <= 21; b++)
        {
            stage.CurrentStripe = b;
            if (b == 21)
            {
                Assert.True(demod.State.DoSynchronize);
                Assert.True(demod.State.FrameSyncFlag);
                resetsBefore = stage.ResetCalls;
            }

            demod.RunStripe(stripes.AsSpan(b * 1024, 1024));
        }

        DrmDemodState st = demod.State;
        Assert.Equal(resetsBefore + 1, stage.ResetCalls);
        Assert.False(st.DoSynchronize);
        Assert.False(st.TimeSyncFlag);
        Assert.False(st.FrequencySyncFlag);
        Assert.False(st.FrameSyncFlag);
        Assert.Equal(1024, st.RsBufWidx);
        Assert.Equal(1, st.IterationCounter);
        Assert.Equal(1, st.RobustnessMode);
        Assert.Equal(6400 - 1024, st.InputSamplesBufferRequest);
    }

    /// <summary>drmRx::run's empty demodulate call when the request is 0 (drmrx.cpp:60-63).</summary>
    [Fact]
    public void ZeroRequest_TriggersEmptyPreCall()
    {
        var stage = new CountingStage();
        var demod = new DrmDemodulator(stage);
        var stripe = new double[1024];
        demod.RunStripe(stripe);
        Assert.Equal(1, stage.Begins);
        demod.State.InputSamplesBufferRequest = 0;
        demod.RunStripe(stripe);
        Assert.Equal(3, stage.Begins);
        Assert.Equal(2, demod.State.IterationCounter);
    }

    /// <summary>getmode's sample-rate gate (demodulator.cpp:200-208): a 0.5 % clock error is detected as a mode
    /// but rejected, and the buffer restarts.</summary>
    [Fact]
    public void LargeSampleRateOffset_IsRejected()
    {
        short[] pcm = RxAcquisitionGoldenTests.LoadInput("A1_q4_high_long", out _);
        var resampler = new PolyphaseResampler(48240, 12000);
        var input = new double[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
            input[i] = pcm[i];
        var x = new double[resampler.MaxOutput(input.Length)];
        int n = resampler.Process(input, x);
        var demod = new DrmDemodulator(new GoldenResyncFrameStage(new HashSet<int>()));
        bool rejected = false;
        for (int b = 0; (b + 1) * 1024 <= n && b < 60 && !rejected; b++)
        {
            demod.RunStripe(x.AsSpan(b * 1024, 1024));
            DrmDemodState st = demod.State;
            Assert.False(st.TimeSyncFlag);
            if (st.RsBufWidx == 0)
            {
                rejected = true;
                Assert.NotEqual(DrmRxTables.NoMode, st.RobustnessMode);
                Assert.True(MathF.Abs(st.SamplerateOffsetEstimation) > 200.0E-5);
                Assert.Equal(6400, st.InputSamplesBufferRequest);
            }
        }

        Assert.True(rejected);
    }

    /// <summary>CPU of the P5a front end plus P5b acquisition per 1024-sample stripe at 11025 and 44100 Hz, on a
    /// DRM signal (acquires, then the stub frame stage) and on noise (getmode on every 20-symbol window, the
    /// acquisition worst case). Plan budget: the whole RX below 10 % of one core.</summary>
    [Theory]
    [InlineData(11025)]
    [InlineData(44100)]
    public void Cpu_FrontEndPlusAcquisition(int rate)
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
                Measure(rate, x, out _, out _, out _);
                Thread.Sleep(150);
            }

            Measure(rate, x, out double meanUs, out double maxUs, out bool synced);
            double stripeUs = 1024.0 / 12000 * 1e6;
            output.WriteLine($"R={rate} {name}: mean {meanUs:F1} µs/stripe ({meanUs / stripeUs * 100:F3} % of one core), worst stripe (acquisition only) {maxUs:F1} µs ({maxUs / stripeUs * 100:F2} %), frame sync {synced}");
#if DEBUG
            const double limit = 20; // unoptimized JIT; the Release build is the real measurement
#else
            const double limit = 5;
#endif
            Assert.True(meanUs / stripeUs * 100 < limit, $"{name}: {meanUs / stripeUs * 100:F2} % of one core");
            if (name == "drm")
                Assert.True(synced, "the resampled DRM signal must reach frame sync");
        }
    }

    private static void Measure(int rate, float[] x, out double meanUs, out double maxUs, out bool synced)
    {
        var fe = new DrmRxFrontEnd(rate);
        var demod = new DrmDemodulator(new GoldenResyncFrameStage(new HashSet<int>()));
        double max = 0;
        int stripes = 0;
        bool sync = false;
        DrmStripeHandler h = s =>
        {
            long t0 = Stopwatch.GetTimestamp();
            demod.RunStripe(s);
            double us = Stopwatch.GetElapsedTime(t0).TotalMicroseconds;
            max = Math.Max(max, us);
            sync |= demod.State.FrameSyncFlag;
            stripes++;
        };
        long start = Stopwatch.GetTimestamp();
        for (int pos = 0; pos < x.Length; pos += 2048)
            fe.Push(x.AsSpan(pos, Math.Min(2048, x.Length - pos)), h);
        meanUs = Stopwatch.GetElapsedTime(start).TotalMicroseconds / stripes;
        maxUs = max;
        synced = sync;
    }

    private sealed class CountingStage : IDrmFrameStage
    {
        public int Begins { get; private set; }

        public void Reset()
        {
        }

        public void BeginDemodulate() => Begins++;

        public void OnFrameSync()
        {
        }

        public bool ProcessFrame(DrmDemodState state) => false;
    }
}
