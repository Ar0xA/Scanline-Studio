using System.Globalization;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>User-reported gap (2026-08-18): dims a control while some bound bool is true, without
/// touching its actual enabled/checked/click-handling state -- unlike this app's existing
/// `IsEnabled="False" Opacity="0.45"` stub convention (which disables AND dims together), this is
/// for a control that stays fully live/clickable but needs a purely visual "temporarily not what it
/// looks like" cue (e.g. the Receiving toggle staying dim while capture is paused for an in-flight
/// local transmission, see `RadioStatusViewModel.IsCapturePausedForTx`'s own doc comment). Reuses
/// this app's own 0.45 dim value for visual consistency with that existing convention, not a new
/// number.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();

    private const double DimmedOpacity = 0.45;
    private const double NormalOpacity = 1.0;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        true => DimmedOpacity,
        false => NormalOpacity,
        _ => NormalOpacity,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
