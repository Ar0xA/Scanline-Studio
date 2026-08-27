using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Converters;

/// <summary>Renders a mode's total transmit duration as a whole-second string (e.g. "113s") for the
/// TX mode ComboBox and its quick-mode-grid right-click popup (direct user request, 2026-08-27) --
/// reuses <see cref="TxControlsPaneViewModel.GetFrameSeconds"/>, the same formula the TX Controls
/// card's own "Duration" row already uses, not a new computation. Rounded to whole seconds
/// (as asked), unlike that row's own one-decimal display -- a compact list of 43 entries reads
/// better rounded than with a decimal point in every row. Not loc-routed: "s" here is a technical
/// unit abbreviation, same category as this folder's own <see cref="UtcTimestampTextConverter"/>
/// hardcoding a date format, not translatable UI prose.</summary>
public sealed class SstvModeDurationSecondsConverter : IValueConverter
{
    public static readonly SstvModeDurationSecondsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        SstvModeDefinition mode => $"{(int)Math.Round(TxControlsPaneViewModel.GetFrameSeconds(mode))}s",
        null => null,
        _ => AvaloniaProperty.UnsetValue,
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
