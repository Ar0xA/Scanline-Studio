namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class AmateurBandLookupTests
{
    [Fact]
    public void BandFor_NullFrequency_ReturnsNull()
    {
        Assert.Null(AmateurBandLookup.BandFor(null));
    }

    [Fact]
    public void BandFor_FrequencyOutsideEveryBand_ReturnsNull()
    {
        // A shortwave broadcast frequency, not an amateur allocation.
        Assert.Null(AmateurBandLookup.BandFor(9_500_000));
    }

    [Theory]
    [InlineData(1_800_000, "160m")]
    [InlineData(3_500_000, "80m")]
    [InlineData(7_000_000, "40m")]
    [InlineData(14_000_000, "20m")]
    [InlineData(14_230_000, "20m")]
    [InlineData(21_000_000, "15m")]
    [InlineData(28_000_000, "10m")]
    [InlineData(50_000_000, "6m")]
    [InlineData(144_000_000, "2m")]
    [InlineData(432_000_000, "70cm")]
    public void BandFor_KnownFrequency_ReturnsExpectedBand(long frequencyHz, string expectedBand)
    {
        Assert.Equal(expectedBand, AmateurBandLookup.BandFor(frequencyHz));
    }

    [Theory]
    [InlineData(14_350_000, "20m")] // upper edge, inclusive
    [InlineData(14_000_000, "20m")] // lower edge, inclusive
    [InlineData(7_300_000, "40m")] // upper edge, inclusive
    [InlineData(7_000_000, "40m")] // lower edge, inclusive
    public void BandFor_ExactBandEdge_ResolvesToThatBand_NotToNoBand(long frequencyHz, string expectedBand)
    {
        Assert.Equal(expectedBand, AmateurBandLookup.BandFor(frequencyHz));
    }

    [Fact]
    public void BandFor_JustOutsideAnEdge_ReturnsNull()
    {
        // 14.350 MHz is 20m's own upper edge (inclusive) -- one Hz past it must NOT still read as 20m.
        Assert.Null(AmateurBandLookup.BandFor(14_350_001));
    }

    [Fact]
    public void BandFor_DifferentFrequenciesInTheSameBand_ReturnTheSameLabel()
    {
        Assert.Equal(AmateurBandLookup.BandFor(14_070_000), AmateurBandLookup.BandFor(14_230_000));
    }
}
