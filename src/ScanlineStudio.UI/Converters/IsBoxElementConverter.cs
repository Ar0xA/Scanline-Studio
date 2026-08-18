using System.Globalization;
using Avalonia.Data.Converters;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Converters;

/// <summary>Backlog item (auditor usability review, 2026-08-17): the GEOMETRY tab's new BOX STYLE
/// block (see <c>TxImageEditorPaneView.axaml</c>'s own doc comment on that block) needs an
/// <c>IsVisible</c> gate on "the currently selected element is a box," which the shared
/// <see cref="ITemplateElementViewModel"/>-typed <c>SelectedOverlayElement</c> binding target has no
/// built-in type-check operator for -- same reasoning <see cref="ObjectConverters.IsNotNull"/> already
/// covers for the IMAGE-only Fit block's null-check, just narrowed to a concrete type instead of
/// non-null.</summary>
public sealed class IsBoxElementConverter : IValueConverter
{
    public static readonly IsBoxElementConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is BoxElementViewModel;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
