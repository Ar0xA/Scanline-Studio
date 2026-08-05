using System.Globalization;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>Centers a Canvas-positioned overlay element on its own anchor point: given the
/// element's own rendered width/height (via a self-referencing <c>Bounds</c> binding), returns
/// <c>-(size/2)</c> for use as a <c>TranslateTransform</c> offset -- an overlay element's X/Y are
/// the visual CENTER, but Canvas.Left/Top position a top-left corner.</summary>
public sealed class NegativeHalfConverter : IValueConverter
{
    public static readonly NegativeHalfConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d ? -d / 2 : 0d;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
