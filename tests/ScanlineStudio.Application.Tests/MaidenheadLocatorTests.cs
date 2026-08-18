namespace ScanlineStudio.Application.Tests;

public sealed class MaidenheadLocatorTests
{
    [Fact]
    public void TryComputeDistanceBearing_RealWorldGrids_MatchesKnownApproximateDistanceAndBearing()
    {
        // JN58tc ~= Frankfurt, Germany. FN31pr ~= New York area, USA. Real-world great-circle
        // distance is ~6200-6300 km, bearing ~290-300 (WNW) from Frankfurt -- verified against this
        // exact implementation via a standalone probe before wiring it into MacroTextResolver, not
        // assumed correct from the formula alone.
        var found = MaidenheadLocator.TryComputeDistanceBearing("JN58tc", "FN31pr", out var distanceKm, out var bearingDegrees);

        Assert.True(found);
        Assert.InRange(distanceKm, 6200, 6400);
        Assert.InRange(bearingDegrees, 290, 300);
    }

    [Fact]
    public void TryComputeDistanceBearing_SameGrid_IsZeroDistance()
    {
        var found = MaidenheadLocator.TryComputeDistanceBearing("JN58tc", "JN58tc", out var distanceKm, out _);

        Assert.True(found);
        Assert.Equal(0, distanceKm, precision: 6);
    }

    [Fact]
    public void TryComputeDistanceBearing_FourCharacterGrids_StillResolve()
    {
        // No subsquare -- centers on the whole 2°x1° square instead, a real, valid, commonly-used
        // shorter locator (many QSO exchanges only trade 4-character grids).
        var found = MaidenheadLocator.TryComputeDistanceBearing("JN58", "FN31", out var distanceKm, out _);

        Assert.True(found);
        Assert.True(distanceKm > 0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("JN5")] // odd length
    [InlineData("JN587")] // odd length
    [InlineData("11tc")] // field must be letters, not digits
    [InlineData("AAtc")] // square must be digits, not letters
    [InlineData("JN58zz")] // subsquare letters must be within a-x
    public void TryToLatLon_InvalidLocators_ReturnsFalse(string? locator)
    {
        Assert.False(MaidenheadLocator.TryToLatLon(locator, out _, out _));
    }

    [Fact]
    public void TryComputeDistanceBearing_EitherGridInvalid_ReturnsFalse()
    {
        Assert.False(MaidenheadLocator.TryComputeDistanceBearing("not a grid", "FN31pr", out _, out _));
        Assert.False(MaidenheadLocator.TryComputeDistanceBearing("JN58tc", null, out _, out _));
    }

    [Fact]
    public void FormatDistance_RoundsToWholeKilometers()
    {
        Assert.Equal("6338 km", MaidenheadLocator.FormatDistance(6337.6));
    }

    [Fact]
    public void FormatBearing_ZeroPadsToThreeDigits()
    {
        Assert.Equal("047°", MaidenheadLocator.FormatBearing(47.2));
        Assert.Equal("005°", MaidenheadLocator.FormatBearing(4.6));
    }
}
