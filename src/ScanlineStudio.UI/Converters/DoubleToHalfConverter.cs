using System.Globalization;
using Avalonia;
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
        // Tier C audit finding (risk): was `throw new NotSupportedException(...)` -- reachable via
        // Avalonia's own AvaloniaProperty.UnsetValue on a broken/not-yet-resolved binding path. See
        // Rgb24ToColorConverter's own comment for the fuller reasoning (same fix, same finding).
        _ => AvaloniaProperty.UnsetValue,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
