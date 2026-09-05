namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>Pins the filter arithmetic against values computed by hand and against a deliberately
/// naive reference, rather than only against its own sibling.
///
/// The gap this closes: <see cref="FirFilter.BandRms(float[], double[])"/> and
/// <see cref="FirFilter.Apply(float[], double[])"/> share the same <c>start = i + delay</c>
/// addressing, so comparing them to each other passes even if that offset is wrong -- RMS is
/// shift-invariant. And the optimized loops drop a bounds guard that the naive version keeps, so
/// "bit-identical" needs something independent to be identical to.</summary>
public sealed class RealNoiseSupportArithmeticTests
{
    // Unity DC gain, so a hand-computed expectation is easy to read.
    private static readonly double[] ThreeTap = [0.25, 0.5, 0.25];

    [Fact]
    public void Apply_MatchesAHandComputedConvolution()
    {
        float[] x = [1, 2, 3, 4, 5, 6, 7];

        var y = FirFilter.Apply(x, ThreeTap);

        // delay = 1, so output[i] = 0.25*x[i+1] + 0.5*x[i] + 0.25*x[i-1], with out-of-range terms
        // dropped at the two edges.
        Assert.Equal(1.0f, y[0]);   // 0.25*2 + 0.5*1
        Assert.Equal(2.0f, y[1]);
        Assert.Equal(3.0f, y[2]);
        Assert.Equal(4.0f, y[3]);
        Assert.Equal(5.0f, y[4]);
        Assert.Equal(6.0f, y[5]);
        Assert.Equal(5.0f, y[6]);   // 0.5*7 + 0.25*6, the x[7] term dropped
    }

    [Fact]
    public void BandRms_MatchesAHandComputedValue()
    {
        float[] x = [1, 2, 3, 4, 5, 6, 7];

        var rms = FirFilter.BandRms(x, ThreeTap);

        // Edges excluded, so this is the RMS of exactly [2,3,4,5,6].
        Assert.Equal(Math.Sqrt((4.0 + 9 + 16 + 25 + 36) / 5), rms, 12);
    }

    [Theory]
    [InlineData(0)]      // empty
    [InlineData(1)]
    [InlineData(200)]    // shorter than the group delay
    [InlineData(511)]    // exactly the group delay
    [InlineData(700)]    // between one and two group delays: head and tail, no middle
    [InlineData(1022)]
    [InlineData(1023)]   // exactly the tap count
    [InlineData(5000)]   // the ordinary case, with a real middle region
    public void Apply_IsBitIdenticalToTheNaiveReference_AtEveryLengthRegime(int length)
    {
        var h = FirFilter.DesignBandPass(400, 2500, 44100, 1023);
        var x = Noise(length, seed: 31);

        var optimized = FirFilter.Apply(x, h);
        var reference = NaiveApply(x, h);

        Assert.Equal(reference.Length, optimized.Length);
        for (var i = 0; i < reference.Length; i++)
        {
            // Bit-identical, not merely close: the optimization only removes a branch that the loop
            // bounds already made vacuous. It changes no term and no summation order.
            Assert.Equal(reference[i], optimized[i]);
        }
    }

    [Fact]
    public void BandRms_IsBitIdenticalToTheNaiveReference()
    {
        var h = FirFilter.DesignBandPass(400, 2500, 44100, 1023);
        var x = Noise(5000, seed: 77);
        var delay = (h.Length - 1) / 2;

        var optimized = FirFilter.BandRms(x, h);

        var naive = NaiveApply(x, h);
        double sumSquares = 0;
        for (var i = delay; i < x.Length - delay; i++)
        {
            sumSquares += (double)naive[i] * naive[i];
        }

        // NaiveApply returns float, so squaring its output is not the same arithmetic as BandRms's
        // double accumulator -- close, not identical, and that difference is the float round-trip.
        Assert.Equal(Math.Sqrt(sumSquares / (x.Length - (2 * delay))), optimized, 5);
    }

    [Fact]
    public void BandRms_RejectsAnEvenTapCount()
    {
        // Not a style preference: the unguarded inner loop is in range only because taps-1 == 2*delay,
        // which holds for odd taps alone.
        Assert.Throws<ArgumentOutOfRangeException>(() => FirFilter.BandRms(new float[100], new double[4]));
    }

    // Deliberately the slow, obvious version: guard every tap, no region split, no array fast path.
    private static float[] NaiveApply(float[] samples, double[] coefficients)
    {
        var delay = (coefficients.Length - 1) / 2;
        var output = new float[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            double acc = 0;
            for (var k = 0; k < coefficients.Length; k++)
            {
                var idx = i + delay - k;
                if (idx >= 0 && idx < samples.Length)
                {
                    acc += coefficients[k] * samples[idx];
                }
            }

            output[i] = (float)acc;
        }

        return output;
    }

    private static float[] Noise(int count, int seed)
    {
        var random = new Random(seed);
        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2.0) - 1.0);
        }

        return samples;
    }
}
