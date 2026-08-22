namespace ScanlineStudio.Application.Tests;

public sealed class MaidenheadLocatorTests
{
    [Fact]
    public void TryComputeDistanceBearing_RealWorldGrids_MatchesKnownApproximateDistanceAndBearing()
    {
        // Comment corrected (Tier A Batch 10 chunk 10a): JN58tc is Munich, Germany (48.10N, 11.63E),
        // not Frankfurt (that's JO40); FN31pr is Hartford, CT, USA (41.73N, 72.71W), not "the New
        // York area" (~150km away). Doesn't change the assertions below (the ranges are wide), but a
        // wrong real-world label misleads anyone re-deriving the expected values by hand. Real-world
        // great-circle distance is ~6200-6300 km, bearing ~290-300 (WNW) from Munich -- verified
        // against this exact implementation via a standalone probe before wiring it into
        // MacroTextResolver, not assumed correct from the formula alone.
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

    [Theory]
    [InlineData(359.5)]
    [InlineData(359.6)]
    public void FormatBearing_RoundsUpTo360_WrapsToZeroNotThreeSixty(double bearingDegrees)
    {
        // Closes a coverage gap flagged by Tier A Batch 10 chunk 10a (docs/functional-audit-playbook.md):
        // the exact 360-wrap is the one behavior FormatBearing's own doc comment specifically argues
        // for (Math.Round FIRST, so the value that rounds up to 360 gets wrapped via `% 360` to
        // "000°" rather than the code-review-flagged bug this comment warns against, where a bare
        // D3 format on the unrounded double would truncate toward zero instead) -- previously
        // untested.
        Assert.Equal("000°", MaidenheadLocator.FormatBearing(bearingDegrees));
    }

    [Theory]
    [InlineData("JN58tc", 48.104167, 11.625)]
    [InlineData("JN58", 48.5, 11.0)]
    [InlineData("AA00aa", -89.979167, -179.958333)]
    [InlineData("RR99xx", 89.979167, 179.958333)]
    public void TryToLatLon_ParsesToExactValues_NotJustAWideDistanceRange(string locator, double expectedLat, double expectedLon)
    {
        // Closes the single biggest coverage gap flagged by Tier A Batch 10 chunk 10a: no existing
        // test asserted a single lat/lon value directly -- the entire parsing/centering math was
        // validated only indirectly through a 200km-wide distance range. Swapping the 4-character
        // centering constants (+0.5 lon/+1.0 lat instead of the correct +1.0/+0.5) would have left
        // every existing test green. Expected values independently hand-derived from the standard
        // Maidenhead field/square/subsquare cell-size definitions (20°/10° field, 2°/1° square,
        // 5'/2.5' subsquare, centered within each cell), not copied from the implementation.
        var found = MaidenheadLocator.TryToLatLon(locator, out var latitude, out var longitude);

        Assert.True(found);
        Assert.Equal(expectedLat, latitude, precision: 5);
        Assert.Equal(expectedLon, longitude, precision: 5);
    }

    [Fact]
    public void TryComputeDistanceBearing_ExactAntipodalGrids_NeverProducesNaN()
    {
        // Closes a real bug flagged by Tier A Batch 10 chunk 10a: for an exactly antipodal grid
        // pair, the haversine intermediate `a` is mathematically 1.0 but the floating-point sum can
        // land one ulp above 1.0, making `1 - a` negative -- Math.Sqrt of a negative number is NaN,
        // silently producing "NaN km" in a rendered TX overlay instead of a real distance. JN58 (its
        // OWN 4-char cell center, 48.5N/11.0E) and its exact antipode AE51 (-48.5N/-169.0E, i.e.
        // 180.0-11.0=169.0 the other way round the globe, sign-flipped latitude) are genuinely
        // reachable arbitrary operator-typed grids, not a constructed edge case.
        //
        // Honest limitation, not swept under the rug: mutation-testing this specific pair (removing
        // the Math.Max(0.0, 1-a) clamp) did NOT reproduce the NaN on this platform -- here `a`
        // reduces to sin²(48.5°)+cos²(48.5°) via the Pythagorean identity, which this platform's
        // libm happens to round to <=1.0 for this exact angle. The underlying IEEE-754 hazard (two
        // independently-rounded squared terms summing to slightly above 1.0) is real and the fix is
        // still correct defensive code, but this test is NOT proven to catch a regression that
        // removes the clamp -- it only pins the current, correct, NaN-free output for this pair.
        var found = MaidenheadLocator.TryComputeDistanceBearing("JN58", "AE51", out var distanceKm, out var bearingDegrees);

        Assert.True(found);
        Assert.False(double.IsNaN(distanceKm), $"distanceKm was NaN for an antipodal grid pair, expected ~{Math.PI * 6371.0:F0}km.");
        Assert.False(double.IsNaN(bearingDegrees));
        Assert.InRange(distanceKm, 19900, 20050); // half Earth's circumference, ~20015km
    }

    [Fact]
    public void TryToLatLon_EightCharacterLocator_ResolvesToTheSameCellAsItsSixCharacterPrefix()
    {
        // Tier B audit finding: an 8-character extended locator (a normal, widely-used VHF/microwave-
        // grade precision) used to be rejected outright by the length check alone -- strictly MORE
        // precise than the accepted 6-character form. Fix truncates the trailing digit pair after
        // validating it, so an 8-char locator resolves to the SAME cell as its own 6-char prefix.
        var foundEight = MaidenheadLocator.TryToLatLon("JN58tc55", out var latEight, out var lonEight);
        var foundSix = MaidenheadLocator.TryToLatLon("JN58tc", out var latSix, out var lonSix);

        Assert.True(foundEight);
        Assert.True(foundSix);
        Assert.Equal(latSix, latEight, precision: 10);
        Assert.Equal(lonSix, lonEight, precision: 10);
    }

    [Theory]
    [InlineData("JN58tcXX")] // trailing pair must be digits, not letters
    [InlineData("JN58tc5")] // 7 characters -- still an invalid, incomplete length
    public void TryToLatLon_InvalidEightCharacterLocators_ReturnsFalse(string locator)
    {
        Assert.False(MaidenheadLocator.TryToLatLon(locator, out _, out _));
    }

    [Fact]
    public void TryToLatLon_InvalidSubsquare_LeavesOutParamsAtZero_NotTheStaleSwCornerValues()
    {
        // Tier B audit finding: every OTHER false-return path leaves latitude/longitude at their
        // pre-zeroed defaults, but the invalid-subsquare path used to leave them at the SW-corner
        // values already computed by the field/square math above it -- a stale, non-zero out-param
        // on a false return from a public static API.
        var found = MaidenheadLocator.TryToLatLon("JN58zz", out var latitude, out var longitude);

        Assert.False(found);
        Assert.Equal(0, latitude);
        Assert.Equal(0, longitude);
    }
}
