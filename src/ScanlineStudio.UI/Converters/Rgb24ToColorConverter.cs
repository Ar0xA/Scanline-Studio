using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Converters;

/// <summary>Two-way counterpart to <see cref="Rgb24ToBrushConverter"/> (which is deliberately
/// one-way, for display-only <c>Background</c>/<c>Foreground</c> bindings) -- Phase 4's
/// <c>ColorPicker</c>/<c>ColorView</c> controls bind their own <c>Color</c> property both ways, so
/// this needs a real <see cref="ConvertBack"/>, not <see cref="System.NotSupportedException"/>.
/// Always fully opaque both directions (<c>A</c> = 255 in, alpha dropped on the way back) -- this
/// project's own <see cref="Rgb24"/> pipeline type has no alpha channel at all (element opacity is
/// its own separate, already-existing property on box/text elements).</summary>
public sealed class Rgb24ToColorConverter : IValueConverter
{
    public static readonly Rgb24ToColorConverter Instance = new();

    // Code-review nit, fixed here: returning null into ColorPicker/ColorView's non-nullable Color
    // target (e.g. a not-yet-stroked text element's null StrokeColor) made Avalonia log a binding
    // error and revert the picker to its own default on every outline toggle-off/selection change
    // -- functionally harmless (the picker sits IsEnabled=False in that state) but a real, avoidable
    // binding-error-log spam. Black is an arbitrary but sensible placeholder; the picker is disabled
    // whenever this path is hit, so its exact color is never user-visible.
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Rgb24 color => Color.FromRgb(color.R, color.G, color.B),
        null => Colors.Black,
        _ => throw new NotSupportedException($"Expected {nameof(Rgb24)}, got {value.GetType()}."),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Color color => new Rgb24(color.R, color.G, color.B),
        _ => throw new NotSupportedException($"Expected {nameof(Color)}, got {value?.GetType().ToString() ?? "null"}."),
    };
}
