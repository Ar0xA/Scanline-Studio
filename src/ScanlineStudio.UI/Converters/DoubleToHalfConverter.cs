using System.Globalization;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>Halves a plain pixel-space <see cref="double"/> -- used to place an edge-midpoint resize
/// handle's <c>Canvas.Left</c>/<c>Canvas.Top</c> at the middle of <c>CanvasWidthPixels</c>/
/// <c>CanvasHeightPixels</c>. Avalonia bindings have no arithmetic expression syntax, so this is the
/// same "no implicit conversion path, needs an explicit converter" gap
/// <see cref="DoubleToCornerRadiusConverter"/>/<see cref="DoubleToThicknessConverter"/> already
/// document for their own target types.</summary>
public sealed class DoubleToHalfConverter : IValueConverter
{
    public static readonly DoubleToHalfConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double d => d / 2,
        null => null,
        _ => throw new NotSupportedException($"Expected {nameof(Double)}, got {value.GetType()}."),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
