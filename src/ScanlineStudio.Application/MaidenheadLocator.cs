using System.Globalization;

namespace ScanlineStudio.Application;

/// <summary>Maidenhead grid-square locator math -- great-circle distance/bearing between two grid
/// squares, for <see cref="MacroTextResolver"/>'s <c>{dist}</c>/<c>{bearing}</c> tokens (auditor
/// usability review follow-up, 2026-08-18: the TX Image Editor's own DIST/BEAM insert-field chips
/// were a stub with no `Command` at all, blocked on "no current-QSO concept" per an earlier session's
/// wiring survey -- resolved by combining <see cref="OperatorSettings.Grid"/> (MY grid, already a
/// real settings-tier value) with the <c>{his_grid}</c> fill-bar VARIABLE the operator types in
/// per-QSO, not a new "current QSO" model). No legacy YONIQ/MMSSTV precedent exists for this feature
/// (confirmed via a real grep across the whole legacy tree -- zero DIST/BEAM/Distance/Bearing
/// references anywhere) -- this is new functionality inspired by a common ham-radio logging
/// convention, not a port, matching this project's own "UI/editing work should be improved on, not
/// replicated" standing rule.</summary>
public static class MaidenheadLocator
{
    /// <summary>Field (A-R, 20°lon/10°lat each) + Square (0-9, 2°lon/1°lat each) + optional Subsquare
    /// (a-x, 5'lon/2.5'lat each) -- the standard 3-tier Maidenhead locator system. A 2-character
    /// (field only) or odd-length locator is rejected (never a real, complete grid reference) rather
    /// than silently guessing at a partial position. Case-insensitive (both the field letters and the
    /// subsquare letters) -- operators type grids in either case interchangeably in practice.</summary>
    public static bool TryToLatLon(string? locator, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;
        if (string.IsNullOrWhiteSpace(locator))
        {
            return false;
        }

        var g = locator.Trim();
        if (g.Length is not (4 or 6)) // 4 and 6 are both even, so a separate parity check is redundant
        {
            return false;
        }

        var upper = g.ToUpperInvariant();
        if (upper[0] is < 'A' or > 'R' || upper[1] is < 'A' or > 'R'
            || !char.IsAsciiDigit(upper[2]) || !char.IsAsciiDigit(upper[3]))
        {
            return false;
        }

        longitude = ((upper[0] - 'A') * 20.0) - 180.0 + ((upper[2] - '0') * 2.0);
        latitude = ((upper[1] - 'A') * 10.0) - 90.0 + ((upper[3] - '0') * 1.0);

        if (g.Length == 6)
        {
            if (upper[4] is < 'A' or > 'X' || upper[5] is < 'A' or > 'X')
            {
                return false;
            }

            // Center of the subsquare cell (+0.5 cell), not its corner. Doc correction (Tier A
            // Batch 10 chunk 10a): the field/square math above (lines computing longitude/latitude
            // before this block) computes the cell's SW CORNER, not a centered value -- this block
            // (and the else-branch below) is where ALL centering actually happens, not a match to
            // an already-centered upstream value. A bare corner reference would bias distance/
            // bearing systematically toward one edge of the locator's real coverage area, worse for
            // a 4-character locator's much larger ~100km-wide cell.
            longitude += (upper[4] - 'A' + 0.5) * (2.0 / 24.0);
            latitude += (upper[5] - 'A' + 0.5) * (1.0 / 24.0);
        }
        else
        {
            // 4-character locator: center of the whole 2°x1° square (same "+half a cell" reasoning).
            longitude += 1.0;
            latitude += 0.5;
        }

        return true;
    }

    private const double EarthRadiusKm = 6371.0;

    /// <summary>Great-circle (haversine) distance in km + initial bearing in degrees (0-360, 0=North,
    /// clockwise) from <paramref name="fromLocator"/> to <paramref name="toLocator"/>. False if
    /// either locator doesn't parse -- see <see cref="TryToLatLon"/>'s own doc comment for exactly
    /// what "doesn't parse" covers.</summary>
    public static bool TryComputeDistanceBearing(string? fromLocator, string? toLocator, out double distanceKm, out double bearingDegrees)
    {
        distanceKm = 0;
        bearingDegrees = 0;
        if (!TryToLatLon(fromLocator, out var lat1, out var lon1) || !TryToLatLon(toLocator, out var lat2, out var lon2))
        {
            return false;
        }

        var lat1Rad = lat1 * Math.PI / 180.0;
        var lat2Rad = lat2 * Math.PI / 180.0;
        var deltaLatRad = (lat2 - lat1) * Math.PI / 180.0;
        var deltaLonRad = (lon2 - lon1) * Math.PI / 180.0;

        var a = (Math.Sin(deltaLatRad / 2) * Math.Sin(deltaLatRad / 2))
            + (Math.Cos(lat1Rad) * Math.Cos(lat2Rad) * Math.Sin(deltaLonRad / 2) * Math.Sin(deltaLonRad / 2));
        // Round-1 code-review finding (Tier A Batch 10 chunk 10a, real bug fixed): for an exactly
        // antipodal grid pair, `a` is mathematically 1.0 but the floating-point sum of the two
        // squared terms can land one ulp ABOVE 1.0, making `1 - a` negative -- Math.Sqrt of a
        // negative number is NaN, silently propagating into distanceKm and rendering "NaN km" in a
        // TX overlay instead of throwing or clamping. Antipodal grid pairs are genuinely reachable
        // (arbitrary operator-typed grids, e.g. JN58/AE51), not a theoretical corner.
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0.0, 1 - a)));
        distanceKm = EarthRadiusKm * c;

        var y = Math.Sin(deltaLonRad) * Math.Cos(lat2Rad);
        var x = (Math.Cos(lat1Rad) * Math.Sin(lat2Rad)) - (Math.Sin(lat1Rad) * Math.Cos(lat2Rad) * Math.Cos(deltaLonRad));
        var bearingRad = Math.Atan2(y, x);
        bearingDegrees = ((bearingRad * 180.0 / Math.PI) + 360.0) % 360.0;

        return true;
    }

    /// <summary><c>{dist}</c>'s own display format -- whole kilometers, e.g. "8047 km". Ham-radio
    /// convention is mixed (km vs. miles); km chosen as the international/metric default matching
    /// the Maidenhead system's own metric heritage, same as <c>MacroTextResolver.FormatFrequency</c>'s
    /// own "pick the one common format, no per-user setting" precedent for a v1 pass.</summary>
    public static string FormatDistance(double distanceKm) =>
        Math.Round(distanceKm).ToString("0", CultureInfo.InvariantCulture) + " km";

    /// <summary><c>{bearing}</c>'s own display format -- whole degrees, zero-padded to 3 digits with
    /// a trailing ° (e.g. "047°"), matching real-world ham logging convention (a bearing is always
    /// read/written as exactly 3 digits). <c>Math.Round</c> first (code-review nit) -- a bare
    /// <c>D3</c> format on the unrounded double would truncate toward zero via the implicit
    /// double-to-int conversion instead of rounding to nearest, e.g. 359.6° would read "359°" not
    /// the correct "360°"/"000°".</summary>
    public static string FormatBearing(double bearingDegrees) =>
        ((int)Math.Round(bearingDegrees) % 360).ToString("D3", CultureInfo.InvariantCulture) + "°";
}
