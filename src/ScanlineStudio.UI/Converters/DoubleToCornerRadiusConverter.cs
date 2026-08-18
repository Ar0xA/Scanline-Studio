using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>Converts a plain pixel-space <see cref="double"/> (e.g.
/// <see cref="ScanlineStudio.UI.ViewModels.BoxElementViewModel.CanvasCornerRadiusPixels"/>) into a
/// uniform <see cref="CornerRadius"/> for <c>Border.CornerRadius</c> -- same "no double-accepting
/// implicit/explicit operator, no runtime <c>TypeConverter</c> reachable from a value binding" gap
/// <see cref="DoubleToThicknessConverter"/> already documents for <c>Thickness</c> (confirmed via
/// reflection before use: no <c>CornerRadiusTypeConverter</c> exists anywhere in the installed
/// Avalonia assemblies) -- a bare <c>CornerRadius="{Binding SomeDouble}"</c> binding fails silently
/// and leaves the property at its default (zero, square corners).</summary>
public sealed class DoubleToCornerRadiusConverter : IValueConverter
{
    public static readonly DoubleToCornerRadiusConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double d => new CornerRadius(d),
        null => null,
        _ => throw new NotSupportedException($"Expected {nameof(Double)}, got {value.GetType()}."),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
