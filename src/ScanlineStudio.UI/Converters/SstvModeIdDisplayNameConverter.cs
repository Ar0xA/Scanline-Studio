using System.Globalization;
using Avalonia.Data.Converters;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.UI.Converters;

/// <summary>Maps a raw <c>SstvModeId</c> (e.g. <c>"martin-m1"</c>) to its human-readable
/// <see cref="SstvModeDefinition.DisplayName"/> (e.g. <c>"Martin M1"</c>) -- Fable UX-review
/// finding, 2026-08-30: the Logbook results grid bound the raw id directly, which reads as an
/// internal identifier, not a mode name an operator recognizes.
///
/// <c>IMultiValueConverter</c>, not a plain single-value converter, deliberately: the full mode
/// catalog (<c>SstvModeRegistry</c>) lives in <c>ScanlineStudio.Core.Sstv</c>, which
/// <c>ScanlineStudio.UI</c> may never reference directly (spec/09-ui.md's Definition of done,
/// enforced by <c>UiLayeringArchitectureTests</c>) -- so the second bound value must be the mode
/// list already reached through the Application layer (<c>ISstvSessionService.AvailableModes</c>,
/// already surfaced as <c>LogbookPaneViewModel.AvailableSstvModes</c>) instead. The radio-mode picker
/// solved the analogous problem differently: it now resolves each item's display text in the
/// view-model at construction (<c>RadioModeOption.Display</c>) and binds it directly, which a
/// stateless converter cannot do because it has no route to <c>ILocalizationService</c>.
///
/// values[0]: the raw id (<see langword="string"/>). values[1]: the available-modes list
/// (<see cref="IReadOnlyList{T}"/> of <see cref="SstvModeDefinition"/>). An id with no match in
/// that list (a QSO logged before a mode existed in the registry, or hand-edited data) falls back
/// to the raw id rather than throwing or showing blank.</summary>
public sealed class SstvModeIdDisplayNameConverter : IMultiValueConverter
{
    public static readonly SstvModeIdDisplayNameConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 || values[0] is not string id || string.IsNullOrEmpty(id))
        {
            return values.Count > 0 ? values[0] : null;
        }

        if (values[1] is IReadOnlyList<SstvModeDefinition> modes)
        {
            foreach (var mode in modes)
            {
                if (mode.Id == id)
                {
                    return mode.DisplayName;
                }
            }
        }

        return id;
    }
}
