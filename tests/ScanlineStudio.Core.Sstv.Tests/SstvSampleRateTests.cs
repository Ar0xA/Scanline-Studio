using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

public sealed class SstvSampleRateTests
{
    [Theory]
    [InlineData(4999, false)]
    [InlineData(5000, true)]
    [InlineData(48500, true)]
    [InlineData(48501, false)]
    public void IsSupported_UsesLegacyInclusiveIntegerRange(int sampleRate, bool expected) =>
        Assert.Equal(expected, SstvSampleRate.IsSupported(sampleRate));

    [Theory]
    [InlineData(4999, SstvSampleRate.Default)]
    [InlineData(5000, 5000)]
    [InlineData(48500, 48500)]
    [InlineData(48501, SstvSampleRate.Default)]
    public void NormalizePersisted_MatchesLegacyStartupFallback(int sampleRate, int expected) =>
        Assert.Equal(expected, SstvSampleRate.NormalizePersisted(sampleRate));

    [Fact]
    public void MaximumAutoSlantRate_IncludesLegacyPostClampRounding()
    {
        const int nominalRate = SstvSampleRate.Maximum;
        var rawClamp = (double)nominalRate * 1100d / 1060d;
        var expected = Math.Floor(rawClamp * 50d + 0.5d) / 50d;

        Assert.Equal(expected, SstvSampleRate.MaximumAutoSlantRate(nominalRate));
        Assert.InRange(Math.Abs(SstvSampleRate.MaximumAutoSlantRate(nominalRate) - rawClamp), 0d, 0.01d);
    }

    [Fact]
    public void MaximumAutoSlantRate_MatchesTheLiveSlantTrackerImplementationAtEverySupportedRate()
    {
        for (var nominalRate = SstvSampleRate.Minimum; nominalRate <= SstvSampleRate.Maximum; nominalRate++)
        {
            Assert.Equal(
                SlantTracker.ClampAndNormalizeAutoSlantRate(double.PositiveInfinity, nominalRate),
                SstvSampleRate.MaximumAutoSlantRate(nominalRate));
        }
    }
}
