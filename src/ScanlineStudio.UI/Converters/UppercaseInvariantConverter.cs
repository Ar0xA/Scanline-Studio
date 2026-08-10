using System.Globalization;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>Display-only uppercasing for a localized string (mockup's CSS <c>text-transform:uppercase</c>
/// group-box titles) -- applied via <c>{loc:Translate Key, Converter={x:Static Instance}}</c> so the
/// underlying localized/stored string is untouched, matching the same display-only-uppercase
/// precedent as <c>FrequencyPresetButtonViewModel.Label</c>.</summary>
public sealed class UppercaseInvariantConverter : IValueConverter
{
    public static readonly UppercaseInvariantConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s ? s.ToUpperInvariant() : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
