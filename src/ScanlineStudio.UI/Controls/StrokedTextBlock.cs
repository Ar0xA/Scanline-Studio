using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ScanlineStudio.UI.Controls;

/// <summary>Backlog item (user request, 2026-08-17): renders text with a real stroke behind the fill
/// -- Avalonia's plain <see cref="TextBlock"/> has no native stroke API, so the TX Image Editor's
/// canvas preview previously showed the outline effect only in the mode-exact mini-preview (the real
/// <c>ApplyTemplate</c> pipeline), not on the interactive canvas itself (a stated, accepted Phase 4
/// scope gap at the time -- see the canvas DataTemplate's own prior comment, now superseded by this
/// control). Mirrors <c>TransmitImagePreparer.DrawGlyphs</c>'s own Phase 5 fix exactly: a
/// <see cref="Pen"/> centers its stroke ON the geometry outline (inward as well as outward), so at
/// this app's normal stroke/font-size ratio a single combined
/// <c>DrawGeometry(fillBrush, strokePen, geometry)</c> call lets the inward half of the stroke
/// overdraw the fill entirely -- fixed the identical way, via two SEPARATE <see cref="DrawingContext.DrawGeometry"/>
/// calls (stroke first, fill drawn on top).</summary>
public sealed class StrokedTextBlock : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StrokedTextBlock, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<StrokedTextBlock, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<StrokedTextBlock, double>(nameof(FontSize), 12.0);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<StrokedTextBlock, IBrush?>(nameof(Fill));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<StrokedTextBlock, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokedTextBlock, double>(nameof(StrokeThickness));

    static StrokedTextBlock()
    {
        AffectsRender<StrokedTextBlock>(TextProperty, FontFamilyProperty, FontSizeProperty, FillProperty, StrokeProperty, StrokeThicknessProperty);
        AffectsMeasure<StrokedTextBlock>(TextProperty, FontFamilyProperty, FontSizeProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    private FormattedText? BuildFormattedText()
    {
        if (string.IsNullOrEmpty(Text) || FontSize <= 0)
        {
            return null;
        }

        return new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily),
            FontSize,
            Fill);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        BuildFormattedText() is { } formatted ? new Size(formatted.Width, formatted.Height) : default;

    public override void Render(DrawingContext context)
    {
        if (BuildFormattedText() is not { } formatted)
        {
            return;
        }

        var geometry = formatted.BuildGeometry(default);
        if (geometry is null)
        {
            return;
        }

        if (Stroke is not null && StrokeThickness > 0)
        {
            context.DrawGeometry(null, new Pen(Stroke, StrokeThickness), geometry);
        }

        context.DrawGeometry(Fill, null, geometry);
    }
}
