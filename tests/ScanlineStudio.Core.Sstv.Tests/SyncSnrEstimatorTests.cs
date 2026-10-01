namespace ScanlineStudio.Core.Sstv.Tests;

public class SyncSnrEstimatorTests
{
    private const int Trials = 400;

    /// <summary>Tone of amplitude 1 at <paramref name="toneHz"/>, plus white Gaussian noise whose
    /// power INSIDE 400–2500 Hz sits <paramref name="snrDb"/> below the tone's.</summary>
    internal static float[] ToneWithNoise(Random random, int sampleRate, int length, double toneHz, double snrDb)
    {
        var tonePower = 0.5;
        var inBandNoisePower = tonePower / Math.Pow(10, snrDb / 10);
        var sigma = Math.Sqrt(inBandNoisePower * (sampleRate / 2.0) / SyncSnrEstimator.BandWidthHz);
        var phase = random.NextDouble() * 2 * Math.PI;
        var samples = new float[length];
        for (var i = 0; i < length; i++)
        {
            samples[i] = (float)(Math.Cos((2 * Math.PI * toneHz * i / sampleRate) + phase) + (sigma * Gaussian(random)));
        }

        return samples;
    }

    internal static double Gaussian(Random random)
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static double MeanSnrDb(int sampleRate, double windowMs, double toneHz, double snrDb, double centreHz, double halfSpanHz, int seed)
    {
        var random = new Random(seed);
        var length = (int)(windowMs / 1000.0 * sampleRate);
        double tone = 0, noise = 0;
        for (var t = 0; t < Trials; t++)
        {
            var estimate = SyncSnrEstimator.Measure(ToneWithNoise(random, sampleRate, length, toneHz, snrDb), sampleRate, centreHz, halfSpanHz);
            Assert.True(estimate.IsValid);
            tone += estimate.TonePower;
            noise += estimate.NoisePower;
        }

        return 10 * Math.Log10(tone / noise);
    }

    [Theory]
    [InlineData(11025, 20.0, 0.0)]
    [InlineData(11025, 20.0, 10.0)]
    [InlineData(11025, 20.0, 25.0)]
    [InlineData(11025, 2.86, 5.0)]
    [InlineData(11025, 2.86, 15.0)]
    [InlineData(11025, 2.86, 25.0)]
    [InlineData(48000, 2.86, 10.0)]
    [InlineData(8000, 7.5, 20.0)]
    public void KnownSnr_IsRecoveredWithinHalfADb(int sampleRate, double windowMs, double snrDb)
    {
        var measured = MeanSnrDb(sampleRate, windowMs, 1200, snrDb, 1200, 20, seed: 1234);
        Assert.InRange(measured - snrDb, -0.5, 0.5);
    }

    // 22 samples at 8000 Hz: the ±20 Hz search picks the frequency that best fits noise too (~+0.5 dB).
    [Theory]
    [InlineData(20.0, 0.5)]
    [InlineData(0.0, 0.25)]
    public void ShortestWindowAt8000Hz_SearchBiasIsBounded(double halfSpanHz, double tolerance)
    {
        var measured = MeanSnrDb(8000, 2.86, 1200, 10, 1200, halfSpanHz, seed: 1234);
        Assert.InRange(measured - 10, -tolerance, 0.75);
    }

    [Fact]
    public void FixedFrequency_IsUnbiased_EvenOnTheShortestWindow()
    {
        var measured = MeanSnrDb(8000, 2.86, 1200, 10, 1200, 0, seed: 77);
        Assert.InRange(measured - 10, -0.3, 0.3);
    }

    [Theory]
    [InlineData(1260.0, 150.0)]
    [InlineData(1087.0, 150.0)]
    [InlineData(1213.0, 20.0)]
    public void FrequencyOffset_IsFittedAndSnrStaysAccurate(double toneHz, double halfSpanHz)
    {
        var noiseless = SyncSnrEstimator.Measure(ToneWithNoise(new Random(7), 11025, 60, toneHz, 200), 11025, 1200, halfSpanHz);
        Assert.InRange(noiseless.FittedHz, toneHz - 1.0, toneHz + 1.0);
        Assert.False(noiseless.AtGridEdge);

        var measured = MeanSnrDb(11025, 5.0, toneHz, 15, 1200, halfSpanHz, seed: 99);
        Assert.InRange(measured - 15, -0.5, 0.5);
    }

    [Fact]
    public void Narrow_1900HzTone_IsMeasuredAroundItsOwnCentre()
    {
        var measured = MeanSnrDb(11025, 5.0, 1900, 12, 1900, 20, seed: 5);
        Assert.InRange(measured - 12, -0.5, 0.5);
    }

    [Fact]
    public void Noiseless_ReadsFarAboveTheDisplayCeiling()
    {
        var estimate = SyncSnrEstimator.Measure(ToneWithNoise(new Random(1), 11025, 31, 1200, 300), 11025, 1200, 150);
        Assert.True(estimate.IsValid);
        Assert.True(estimate.SnrDb > 60, $"noiseless read {estimate.SnrDb:F1} dB");
    }

    [Fact]
    public void Monotonic_InTrueSnr()
    {
        var previous = double.NegativeInfinity;
        foreach (var snr in new[] { 0.0, 5, 10, 15, 20, 25, 30 })
        {
            var measured = MeanSnrDb(11025, 2.86, 1200, snr, 1200, 20, seed: 42);
            Assert.True(measured > previous, $"{snr} dB read {measured:F2}, not above {previous:F2}");
            previous = measured;
        }
    }

    [Fact]
    public void ToneOutsideTheSearchSpan_IsFlaggedAtGridEdge()
    {
        var estimate = SyncSnrEstimator.Measure(ToneWithNoise(new Random(3), 11025, 60, 1300, 200), 11025, 1200, 20);
        Assert.True(estimate.AtGridEdge);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(7)]
    public void TooShortWindow_IsInvalid(int length)
    {
        Assert.False(SyncSnrEstimator.Measure(new float[length], 11025, 1200, 20).IsValid);
    }

    [Fact]
    public void AllZeroWindow_IsInvalid_NotAnException()
    {
        Assert.False(SyncSnrEstimator.Measure(new float[64], 11025, 1200, 20).IsValid);
    }
}
