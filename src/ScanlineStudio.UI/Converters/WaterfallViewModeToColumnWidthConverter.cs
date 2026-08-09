using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Converters;

/// <summary>Drives the Spectrum &amp; waterfall card's two plot <c>ColumnDefinition.Width</c>s
/// directly from <see cref="WaterfallViewMode"/> (batch 8b) -- auditor-caught: collapsing a plot via
/// <c>IsVisible</c> alone leaves its <c>*</c> column at half width, producing a half-empty card
/// instead of the other plot expanding to fill the space. <c>ConverterParameter</c> selects which
/// column this instance drives: <c>"Spectrum"</c> or <c>"Waterfall"</c>.</summary>
public sealed class WaterfallViewModeToColumnWidthConverter : IValueConverter
{
    public static readonly WaterfallViewModeToColumnWidthConverter Instance = new();

    private static readonly GridLength Star = new(1, GridUnitType.Star);
    private static readonly GridLength Zero = new(0, GridUnitType.Pixel);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not WaterfallViewMode mode || parameter is not string column)
        {
            return Star;
        }

        return (mode, column) switch
        {
            (WaterfallViewMode.Both, _) => Star,
            (WaterfallViewMode.SpectrumOnly, "Spectrum") => Star,
            (WaterfallViewMode.SpectrumOnly, "Waterfall") => Zero,
            (WaterfallViewMode.WaterfallOnly, "Waterfall") => Star,
            (WaterfallViewMode.WaterfallOnly, "Spectrum") => Zero,
            _ => Star,
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
