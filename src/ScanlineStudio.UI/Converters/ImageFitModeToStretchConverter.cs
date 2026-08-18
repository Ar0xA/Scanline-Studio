using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Converters;

/// <summary>Backlog item (auditor usability review, 2026-08-17): "Image elements' Fit mode isn't
/// editable and the canvas always renders Stretch="Fill" regardless of the VM's own Fit value --
/// canvas and transmitted output can visibly disagree." Maps <see cref="ImageFitMode"/> (the pipeline's
/// own enum, <c>Stretch</c>/<c>Contain</c>/<c>Cover</c>) to Avalonia's own <see cref="Stretch"/> enum so
/// the canvas <c>Image</c> control's own rendering actually matches
/// <see cref="ImageElementViewModel.Fit"/> instead of a hardcoded literal -- <c>Contain</c> means
/// "letterbox, keep the whole image visible" (Avalonia's <c>Uniform</c>), <c>Cover</c> means "fill the
/// box, crop overflow" (Avalonia's <c>UniformToFill</c>), matching
/// <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s own real per-mode pixel behavior for each.</summary>
public sealed class ImageFitModeToStretchConverter : IValueConverter
{
    public static readonly ImageFitModeToStretchConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ImageFitMode.Stretch => Stretch.Fill,
        ImageFitMode.Contain => Stretch.Uniform,
        ImageFitMode.Cover => Stretch.UniformToFill,
        null => Stretch.Fill,
        _ => throw new NotSupportedException($"Unrecognized {nameof(ImageFitMode)}: {value}."),
    };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
