using System.Globalization;
using Avalonia.Data.Converters;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Converters;

/// <summary>Displays a <see cref="RadioModeOption"/> ComboBox item: <see cref="RadioModeOption.None"/>
/// (the explicit "(none)" entry -- see that type's own doc comment for why it's a real object, not a
/// bare <c>null</c> list entry) renders as the localized <c>noneText</c> value passed in as the
/// second binding, rather than a hardcoded literal (spec/10-localization.md); any real mode renders
/// via its own <see cref="object.ToString"/>.</summary>
public sealed class RadioModeDisplayConverter : IMultiValueConverter
{
    public static readonly RadioModeDisplayConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
        {
            return null;
        }

        return values[0] is RadioModeOption { Value: { } mode } ? mode.ToString() : values[1] as string;
    }
}
