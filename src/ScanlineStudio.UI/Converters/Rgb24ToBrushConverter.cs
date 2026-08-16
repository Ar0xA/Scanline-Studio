using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Converters;

/// <summary>Converts the pipeline's own <see cref="Rgb24"/> pixel type (deliberately not an
/// Avalonia/System.Drawing color -- see that type's own doc comment on why
/// <c>ScanlineStudio.Abstractions</c> stays UI-framework-free) into a real <see cref="IBrush"/> for
/// canvas display -- Phase 1's <see cref="ScanlineStudio.UI.ViewModels.BoxElementViewModel"/>
/// fill/border and <see cref="ScanlineStudio.UI.ViewModels.OverlayElementViewModel"/> text color are
/// the first consumers; the text element's own <c>Color</c> was never actually wired to canvas
/// display before Phase 1 (hardcoded white) despite being a real, editable, undo-tracked property --
/// fixed here as the same kind of fix, not a separate concern.</summary>
public sealed class Rgb24ToBrushConverter : IValueConverter
{
    public static readonly Rgb24ToBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Rgb24 color => new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
        null => null,
        _ => throw new NotSupportedException($"Expected {nameof(Rgb24)}, got {value.GetType()}."),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
