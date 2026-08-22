using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>Converts a plain pixel-space <see cref="double"/> (e.g.
/// <see cref="ScanlineStudio.UI.ViewModels.BoxElementViewModel.CanvasBorderThicknessPixels"/>) into a
/// uniform <see cref="Thickness"/> for <c>Border.BorderThickness</c> -- round-3 code-review finding:
/// <c>Thickness</c> has no <c>double</c>-accepting implicit/explicit operator and no runtime
/// <c>TypeConverter</c> reachable from a value binding (verified against the installed Avalonia
/// package's own XML docs, not assumed), so a bare <c>BorderThickness="{Binding SomeDouble}"</c>
/// binding fails silently and leaves the property at its default (zero, no border) -- exactly the
/// kind of invisible-in-app regression a border-thickness feature would otherwise ship with the
/// moment Phase 4's style panel makes it editable.</summary>
public sealed class DoubleToThicknessConverter : IValueConverter
{
    public static readonly DoubleToThicknessConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double d => new Thickness(d),
        null => null,
        // Tier C audit finding (risk): was `throw new NotSupportedException(...)` -- reachable via
        // Avalonia's own AvaloniaProperty.UnsetValue on a broken/not-yet-resolved binding path. See
        // Rgb24ToColorConverter's own comment for the fuller reasoning (same fix, same finding).
        _ => AvaloniaProperty.UnsetValue,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
