namespace ScanlineStudio.Core.Logbook;

/// <summary>ui_transition_plan.md step 15, piece (c) (duplicate-QSO detection) -- maps a frequency
/// to its amateur band label, e.g. <c>"20m"</c>. Band edges are the ADIF specification's own
/// region-independent "Band" enumeration (ADIF 3.1.4) -- NOT a per-country/IARU-region table
/// (auditor plan-review finding: 80m/40m upper edges genuinely differ by IARU region, e.g. 3.5-3.8
/// MHz in Region 1 vs. 3.5-4.0 MHz in Region 2 -- using a single region's own band plan here would
/// misclassify an operator on a frequency legal in their own region but outside that region's
/// table). Boundary rule is INCLUSIVE on both ends (<c>low &lt;= frequencyMhz &lt;= high</c>), so an
/// edge frequency (e.g. 14.350 MHz, 7.300 MHz) always resolves to a real band, never "no band."
/// <see langword="null"/> input or a frequency outside every listed band both return
/// <see langword="null"/> (no band) -- <see cref="LogbookSessionService.FindLikelyDuplicateAsync"/>'s
/// own doc comment covers what "no band" means for matching.</summary>
public static class AmateurBandLookup
{
    private static readonly (string Label, double LowMhz, double HighMhz)[] Bands =
    [
        ("2190m", 0.1357, 0.1378),
        ("630m", 0.472, 0.479),
        ("560m", 0.501, 0.504),
        ("160m", 1.8, 2.0),
        ("80m", 3.5, 4.0),
        ("60m", 5.06, 5.45),
        ("40m", 7.0, 7.3),
        ("30m", 10.1, 10.15),
        ("20m", 14.0, 14.35),
        ("17m", 18.068, 18.168),
        ("15m", 21.0, 21.45),
        ("12m", 24.89, 24.99),
        ("10m", 28.0, 29.7),
        ("8m", 40.0, 45.0),
        ("6m", 50.0, 54.0),
        ("5m", 54.000001, 69.9),
        ("4m", 70.0, 71.0),
        ("2m", 144.0, 148.0),
        ("1.25m", 222.0, 225.0),
        ("70cm", 420.0, 450.0),
        ("33cm", 902.0, 928.0),
        ("23cm", 1240.0, 1300.0),
        ("13cm", 2300.0, 2450.0),
        ("9cm", 3300.0, 3500.0),
        ("6cm", 5650.0, 5925.0),
        ("3cm", 10_000.0, 10_500.0),
        ("1.25cm", 24_000.0, 24_250.0),
        ("6mm", 47_000.0, 47_200.0),
        ("4mm", 75_500.0, 81_000.0),
        ("2.5mm", 119_980.0, 123_000.0),
        ("2mm", 134_000.0, 149_000.0),
        ("1mm", 241_000.0, 250_000.0),
    ];

    /// <summary>Returns a band label (e.g. <c>"20m"</c>), or <see langword="null"/> if
    /// <paramref name="frequencyHz"/> is <see langword="null"/> or falls outside every known band
    /// (e.g. a shortwave broadcast frequency, or a genuinely out-of-band operation).</summary>
    public static string? BandFor(long? frequencyHz)
    {
        if (frequencyHz is not { } hz)
        {
            return null;
        }

        var mhz = hz / 1_000_000.0;
        foreach (var (label, lowMhz, highMhz) in Bands)
        {
            if (mhz >= lowMhz && mhz <= highMhz)
            {
                return label;
            }
        }

        return null;
    }
}
