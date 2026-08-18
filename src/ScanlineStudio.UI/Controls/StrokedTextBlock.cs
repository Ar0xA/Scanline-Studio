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

    /// <summary>Auditor usability review follow-up (2026-08-18) -- Bold/Italic canvas WYSIWYG, same
    /// "canvas must actually show the effect, not just the mode-exact mini-preview" discipline this
    /// control's own class doc comment already established for stroke.</summary>
    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<StrokedTextBlock, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<FontStyle> FontStyleProperty =
        AvaloniaProperty.Register<StrokedTextBlock, FontStyle>(nameof(FontStyle), FontStyle.Normal);

    /// <summary>Auditor usability review follow-up (2026-08-18): the "3D"/Stack text effect's canvas
    /// WYSIWYG (legacy YONIQ's real <c>CBStack</c>/<c>m_StackPara</c> mechanism -- a stepped stack of
    /// offset solid-color copies, not a real 3D transform; see
    /// <see cref="ScanlineStudio.UI.ViewModels.OverlayElementViewModel.CanvasStackStepXPixels"/>'s own
    /// doc comment for why this lives here, as plain StyledProperties on ONE control, rather than an
    /// <c>ItemsControl</c> of generated copies). Null <see cref="StackFill"/> means no stack, same
    /// null-means-none convention as <see cref="Stroke"/>.</summary>
    public static readonly StyledProperty<IBrush?> StackFillProperty =
        AvaloniaProperty.Register<StrokedTextBlock, IBrush?>(nameof(StackFill));

    /// <summary>Canvas-display PIXEL space (same space <see cref="FontSize"/> is already in), NOT the
    /// raw relative <c>StackStepX</c>/Y the VM stores -- the caller (see
    /// <c>OverlayElementViewModel.CanvasStackStepXPixels</c>) has already multiplied by ImageHeight,
    /// since Avalonia bindings can't do that arithmetic themselves.</summary>
    public static readonly StyledProperty<double> StackStepXPixelsProperty =
        AvaloniaProperty.Register<StrokedTextBlock, double>(nameof(StackStepXPixels));

    public static readonly StyledProperty<double> StackStepYPixelsProperty =
        AvaloniaProperty.Register<StrokedTextBlock, double>(nameof(StackStepYPixels));

    static StrokedTextBlock()
    {
        AffectsRender<StrokedTextBlock>(
            TextProperty, FontFamilyProperty, FontSizeProperty, FillProperty, StrokeProperty, StrokeThicknessProperty, FontWeightProperty, FontStyleProperty,
            StackFillProperty, StackStepXPixelsProperty, StackStepYPixelsProperty);
        AffectsMeasure<StrokedTextBlock>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty, FontStyleProperty);
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

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public FontStyle FontStyle
    {
        get => GetValue(FontStyleProperty);
        set => SetValue(FontStyleProperty, value);
    }

    public IBrush? StackFill
    {
        get => GetValue(StackFillProperty);
        set => SetValue(StackFillProperty, value);
    }

    public double StackStepXPixels
    {
        get => GetValue(StackStepXPixelsProperty);
        set => SetValue(StackStepXPixelsProperty, value);
    }

    public double StackStepYPixels
    {
        get => GetValue(StackStepYPixelsProperty);
        set => SetValue(StackStepYPixelsProperty, value);
    }

    /// <summary>Same clamp/count formula as <c>TransmitImagePreparer.DrawGlyphs</c>'s real stack
    /// pass (one copy per pixel of the dominant step axis) -- a separate constant, not a
    /// cross-project shared reference, since <c>ScanlineStudio.UI</c> has no dependency on
    /// <c>ScanlineStudio.Core.Imaging</c>'s internals.</summary>
    private const int MaxStackCopies = 128;

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
            new Typeface(FontFamily, FontStyle, FontWeight),
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

        // Stack draws FIRST (furthest back), behind stroke/fill -- same draw-order convention as the
        // real pipeline's own stack-then-shadow-then-stroke-then-fill pass (see
        // TransmitImagePreparer.DrawGlyphs's own doc comment; the shadow copy itself is a separate
        // sibling TextBlock in the DataTemplate, not part of this control).
        if (StackFill is not null && (StackStepXPixels != 0 || StackStepYPixels != 0))
        {
            var copies = Math.Min(MaxStackCopies, (int)Math.Round(Math.Max(Math.Abs(StackStepXPixels), Math.Abs(StackStepYPixels))));
            for (var f = copies; f >= 1; f--)
            {
                using (context.PushTransform(Matrix.CreateTranslation(StackStepXPixels * f / copies, StackStepYPixels * f / copies)))
                {
                    context.DrawGeometry(StackFill, null, geometry);
                }
            }
        }

        if (Stroke is not null && StrokeThickness > 0)
        {
            context.DrawGeometry(null, new Pen(Stroke, StrokeThickness), geometry);
        }

        context.DrawGeometry(Fill, null, geometry);
    }
}
