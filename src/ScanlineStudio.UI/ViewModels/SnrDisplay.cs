using ScanlineStudio.Abstractions.Localization;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>
/// Formats a sync-pulse SNR figure for display. The stored/published value is unclamped; the display
/// clamps to "&lt; −10 dB" / "&gt; 50 dB" and shows "—" when there is no figure (null or NaN).
/// </summary>
public static class SnrDisplay
{
    public const double DisplayFloorDb = -10.0;
    public const double DisplayCeilingDb = 50.0;

    public static string Format(ILocalizationService localization, double? snrDb)
    {
        if (snrDb is not { } value || double.IsNaN(value))
        {
            return localization.GetString("Snr.None");
        }

        if (value < DisplayFloorDb)
        {
            return localization.GetString("Snr.BelowFloor");
        }

        if (value > DisplayCeilingDb)
        {
            return localization.GetString("Snr.AboveCeiling");
        }

        return localization.GetString("Snr.ValueFormat", value);
    }
}
