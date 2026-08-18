using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>Drives a meter's two-column <c>ColumnDefinition.Width</c>s from a single bound 0-100
/// percent value -- same "individually-bound ColumnDefinition.Width, not the whole ColumnDefinitions
/// shorthand string" technique as <see cref="WaterfallViewModeToColumnWidthConverter"/>.
/// <c>ConverterParameter="Remainder"</c> selects the trailing (100-percent) column; the default
/// (no parameter) selects the leading (percent) column -- both driven off the SAME source value so
/// they always sum to exactly 100 regardless of scale.</summary>
public sealed class DoubleToStarGridLengthConverter : IValueConverter
{
    public static readonly DoubleToStarGridLengthConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double percent)
        {
            return new GridLength(1, GridUnitType.Star);
        }

        var clamped = Math.Clamp(percent, 0.0, 100.0);
        var weight = string.Equals(parameter as string, "Remainder", StringComparison.Ordinal)
            ? 100.0 - clamped
            : clamped;

        // Zero-weight Star columns are valid in Avalonia (render at zero width, same as the
        // WaterfallViewModeToColumnWidthConverter precedent's own Zero case) -- no epsilon floor
        // needed here.
        return new GridLength(weight, GridUnitType.Star);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
