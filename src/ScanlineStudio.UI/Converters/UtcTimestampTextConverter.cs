using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace ScanlineStudio.UI.Converters;

/// <summary>TwoWay <c>DateTimeOffset</c>/<c>DateTimeOffset?</c> &lt;-&gt; text for the Logbook
/// pane's Start/End fields -- NOT <c>StringFormat</c> on a plain <c>TextBox</c> binding, which only
/// formats the forward direction: Avalonia's <c>StringFormatValueConverter.ConvertBack</c> does not
/// reverse the format, so the typed string falls through to the default
/// <c>DateTimeOffset</c> type converter, which parses an offset-less string
/// ("2026-08-08 12:00:00") using the MACHINE'S LOCAL offset -- corrupting the stored instant on any
/// non-UTC machine and breaking <c>SqliteLogbookRepository</c>'s lexicographic <c>StartUtc</c>
/// ordering/range comparisons the moment a user edits an existing QSO's timestamp. This converter
/// always treats the text as UTC in both directions, matching the pane's own "no local-time
/// conversion anywhere" rule.</summary>
public sealed class UtcTimestampTextConverter : IValueConverter
{
    public static readonly UtcTimestampTextConverter Instance = new();

    private const string Format = "yyyy-MM-dd HH:mm:ss";

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset dto => dto.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture),
        _ => string.Empty,
    };

    /// <summary>Pass <c>ConverterParameter=AllowNull</c> for an optional field (e.g. End) so blank
    /// text clears it to <see langword="null"/>; without that parameter (e.g. Start, which is
    /// required), blank/unparseable text is rejected via <see cref="BindingOperations.DoNothing"/>,
    /// leaving the last valid value in place rather than crashing the binding or silently defaulting
    /// to "now."</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var allowNull = string.Equals(parameter as string, "AllowNull", StringComparison.Ordinal);

        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return allowNull ? null : BindingOperations.DoNothing;
        }

        if (!DateTime.TryParseExact(text.Trim(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return BindingOperations.DoNothing;
        }

        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
    }
}
