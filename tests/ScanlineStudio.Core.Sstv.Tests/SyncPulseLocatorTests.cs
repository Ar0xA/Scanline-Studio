namespace ScanlineStudio.Core.Sstv.Tests;

public class SyncPulseLocatorTests
{
    /// <summary>Phase-continuous FM: <paramref name="before"/> samples of beforeHz, then the pulse, then afterHz.</summary>
    private static float[] Pulse(int sampleRate, int before, int pulse, int after, double beforeHz, double pulseHz, double afterHz, double snrDb, Random random)
    {
        var total = before + pulse + after;
        var sigma = double.IsPositiveInfinity(snrDb) ? 0 : Math.Sqrt(0.5 / Math.Pow(10, snrDb / 10) * (sampleRate / 2.0) / SyncSnrEstimator.BandWidthHz);
        var samples = new float[total];
        var phase = random.NextDouble() * 2 * Math.PI;
        for (var i = 0; i < total; i++)
        {
            var f = i < before ? beforeHz : i < before + pulse ? pulseHz : afterHz;
            phase += 2 * Math.PI * f / sampleRate;
            samples[i] = (float)(Math.Sin(phase) + (sigma * SyncSnrEstimatorTests.Gaussian(random)));
        }

        return samples;
    }

    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(48000)]
    public void Noiseless_FindsTheExactStart(int sampleRate)
    {
        var pulse = (int)(0.004862 * sampleRate);
        var before = (int)(0.003 * sampleRate);
        var samples = Pulse(sampleRate, before, pulse, (int)(0.01 * sampleRate), 1900, 1200, 1500, double.PositiveInfinity, new Random(1));
        var positions = (int)(0.006 * sampleRate);

        var location = SyncPulseLocator.Locate(samples, positions, pulse, sampleRate, [1200.0]);

        Assert.True(location.IsValid);
        Assert.InRange(location.Start, before - 1, before + 1);
        Assert.False(location.AtEdge);
        Assert.True(location.Score > 0.99);
    }

    [Theory]
    [InlineData(5.0)]
    [InlineData(15.0)]
    public void Noisy_MedianStartIsWithinAQuarterMillisecond(double snrDb)
    {
        const int rate = 11025;
        var pulse = (int)(0.004862 * rate);
        var before = (int)(0.003 * rate);
        var random = new Random(9);
        var starts = new List<double>();
        for (var trial = 0; trial < 101; trial++)
        {
            var samples = Pulse(rate, before, pulse, (int)(0.01 * rate), 1500, 1200, 2300, snrDb, random);
            starts.Add(SyncPulseLocator.Locate(samples, (int)(0.006 * rate), pulse, rate, [1200.0]).Start);
        }

        starts.Sort();
        Assert.InRange((starts[50] - before) / rate * 1000.0, -0.25, 0.25);
    }

    [Fact]
    public void Mistuned_IsFoundOnAWideFrequencySet()
    {
        const int rate = 11025;
        var pulse = (int)(0.009 * rate);
        var before = 40;
        var samples = Pulse(rate, before, pulse, 200, 1500, 1293, 1500, double.PositiveInfinity, new Random(2));
        var frequencies = Enumerable.Range(-15, 31).Select(k => 1200.0 + (10 * k)).ToArray();

        var location = SyncPulseLocator.Locate(samples, 80, pulse, rate, frequencies);

        Assert.InRange(location.Start, before - 2, before + 2);
        Assert.Equal(1290.0, location.FrequencyHz);
    }

    [Fact]
    public void StartOutsideTheRange_IsFlaggedAtEdge()
    {
        const int rate = 11025;
        var pulse = 53;
        var samples = Pulse(rate, 100, pulse, 200, 2300, 1200, 2300, double.PositiveInfinity, new Random(4));

        var location = SyncPulseLocator.Locate(samples, 90, pulse, rate, [1200.0]);

        Assert.True(location.AtEdge);
    }

    [Fact]
    public void SpanTooShort_ReturnsNone()
    {
        Assert.False(SyncPulseLocator.Locate(new float[50], 10, 45, 11025, [1200.0]).IsValid);
    }
}
