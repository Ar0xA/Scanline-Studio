using AvaloniaColor = Avalonia.Media.Color;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="TemplateBoxElement"/> (Phase 1,
/// spec/15-template-designer.md) -- the immutable pipeline record gets replaced wholesale on every
/// drag-frame otherwise. Mirrors <see cref="OverlayElementViewModel"/>'s own shape/wiring pattern
/// exactly (X/Y CENTER-anchored, same parent-pushed <see cref="RemoveCommand"/>/
/// <see cref="PushUndoSnapshotForGeometryChange"/> convention) since both implement
/// <see cref="ITemplateElementViewModel"/> and share one remove/undo/rotate path in
/// <see cref="TxImageEditorPaneViewModel"/> -- see that class's own <c>CreateBoxElement</c> for the
/// construction-time wiring.</summary>
public sealed partial class BoxElementViewModel : ObservableObject, ITemplateElementViewModel
{
    [ObservableProperty]
    private double _x = 0.5;

    [ObservableProperty]
    private double _y = 0.5;

    [ObservableProperty]
    private double _width = 0.3;

    [ObservableProperty]
    private double _height = 0.2;

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private Rgb24 _fillColor = new(64, 64, 64);

    [ObservableProperty]
    private Rgb24? _borderColor;

    [ObservableProperty]
    private double _borderThickness;

    [ObservableProperty]
    private double _opacity = 1.0;

    /// <summary>Auditor usability review follow-up (2026-08-18) -- "missing item" against the
    /// original box-elements friction risk in spec/15-template-designer.md ("need border,
    /// corner-radius, and opacity"). Relative to image HEIGHT, same convention as
    /// <see cref="BorderThickness"/>. 0 (default) renders identically to a plain square-cornered box.</summary>
    [ObservableProperty]
    private double _cornerRadius;

    /// <summary>TX editor gap-items plan (2026-09-01, box gradient fill) -- SAME simplified 2-stop
    /// shape <see cref="OverlayElementViewModel.GradientEnabled"/> already established for text, on
    /// the SAME "text gradients already shipped, boxes only had flat fill" gap Fable's comparative
    /// review flagged. <see cref="TxImageEditorPaneViewModel.BuildTemplateElement"/> composes these
    /// four fields into a real <see cref="TextGradient"/> only when <see cref="GradientEnabled"/> is
    /// true, mirroring the text element's own composition exactly.</summary>
    [ObservableProperty]
    private bool _gradientEnabled;

    [ObservableProperty]
    private TextGradientKind _gradientKind = TextGradientKind.Horizontal;

    [ObservableProperty]
    private Rgb24 _gradientStartColor = new(255, 0, 0);

    [ObservableProperty]
    private Rgb24 _gradientEndColor = new(0, 0, 255);

    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    public IRelayCommand? RemoveCommand { get; init; }

    public IRelayCommand? MoveUpCommand { get; init; }

    public IRelayCommand? MoveDownCommand { get; init; }

    public IRelayCommand? BringToFrontCommand { get; init; }

    public IRelayCommand? SendToBackCommand { get; init; }

    public IRelayCommand? DuplicateCommand { get; init; }

    public IRelayCommand? AlignSelectedElementToCropCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.CopyCommand"/>
    public IRelayCommand? CopyCommand { get; init; }

    public IRelayCommand? CutCommand { get; init; }

    public IRelayCommand? PasteCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.FlattenCommand"/>
    public IRelayCommand? FlattenCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 -- box-only (matches
    /// <see cref="OverlayElementViewModel.CopyStyleCommand"/>'s own text-only precedent: box style
    /// is fill/border/corner-radius, not the text one).</summary>
    public IRelayCommand? CopyStyleCommand { get; init; }

    public IRelayCommand? PasteStyleCommand { get; init; }

    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 (Fill &amp; Border flyout) -- box's own
    /// counterpart to <see cref="OverlayElementViewModel.PushUndoSnapshotForStyleChange"/>. Real
    /// pre-existing gap closed here, not new scope: before this, NONE of FillColor/BorderColor/
    /// BorderThickness/Opacity/CornerRadius had any undo hook at all, on ANY entry point --
    /// including the already-shipped BOX STYLE sidebar block, which has silently produced zero undo
    /// steps since it shipped. Discovered only because the flyout adds a second entry point onto
    /// these same properties; fixed at the source instead of shipping a second silently-broken-undo
    /// instance of the identical bug class <see cref="OverlayElementViewModel.FontSizeRelative"/>/
    /// <see cref="OverlayElementViewModel.Color"/> already hit once (see that field's own doc
    /// comment).</summary>
    public Action? PushUndoSnapshotForStyleChange { get; init; }

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>BorderThickness is normalized to image height (same convention as
    /// <see cref="OverlayElementViewModel.FontSizeRelative"/>, per spec/15-template-designer.md) --
    /// this converts it to the canvas's own pixel space, mirroring <see cref="TemplateBoxElement"/>'s
    /// pipeline-side rendering so the editor canvas is actually WYSIWYG for a bordered box.</summary>
    public double CanvasBorderThicknessPixels => BorderThickness * ImageHeight;

    /// <summary>Same image-height-relative-to-canvas-pixel conversion as
    /// <see cref="CanvasBorderThicknessPixels"/>, for <see cref="CornerRadius"/> -- Avalonia's
    /// <c>Border.CornerRadius</c> has the SAME "no double-accepting implicit/explicit operator, no
    /// runtime <c>TypeConverter</c> reachable from a value binding" gap <see cref="CanvasBorderThicknessPixels"/>'s
    /// own doc comment already documents for <c>BorderThickness</c>/<c>Thickness</c> (checked via
    /// reflection before use, not assumed identical just because both are Avalonia struct types --
    /// confirmed no <c>CornerRadiusTypeConverter</c> exists anywhere in the installed Avalonia
    /// assemblies, same absence that motivated <see cref="Converters.DoubleToThicknessConverter"/>
    /// originally) -- the canvas binding uses a new, analogous
    /// <see cref="Converters.DoubleToCornerRadiusConverter"/>, not a bare binding.</summary>
    public double CanvasCornerRadiusPixels => CornerRadius * ImageHeight;

    /// <summary>TX workflow modernization plan, Phase 1 (Fill &amp; Border flyout) -- same
    /// element-level px-conversion pattern as <see cref="OverlayElementViewModel.TargetModeHeightPx"/>/
    /// <see cref="OverlayElementViewModel.FontSizePx"/>, see that pair's own doc comments for why
    /// this needs to live on the element itself rather than the parent VM's existing
    /// <c>SelectedBoxElementBorderThicknessPx</c>/<c>SelectedBoxElementCornerRadiusPx</c>.</summary>
    public double TargetModeHeightPx { get; init; }

    /// <inheritdoc cref="OverlayElementViewModel.FontSizePx"/>
    public double BorderThicknessPx
    {
        get => BorderThickness * TargetModeHeightPx;
        set
        {
            if (TargetModeHeightPx <= 0)
            {
                return;
            }

            BorderThickness = value / TargetModeHeightPx;
        }
    }

    /// <inheritdoc cref="OverlayElementViewModel.FontSizePx"/>
    public double CornerRadiusPx
    {
        get => CornerRadius * TargetModeHeightPx;
        set
        {
            if (TargetModeHeightPx <= 0)
            {
                return;
            }

            CornerRadius = value / TargetModeHeightPx;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17) -- same nullable-color/checkbox
    /// bridge as <see cref="OverlayElementViewModel.HasStroke"/>, so the new BOX STYLE inspector
    /// block can bind a plain CheckBox to a <c>Rgb24?</c> the same way TEXT STYLE's own Outline row
    /// already does, including that row's own "ColorPicker un-nulls a disabled nullable color" fix
    /// (see <see cref="BorderColorForPicker"/> below).</summary>
    public bool HasBorder
    {
        get => BorderColor is not null;
        set => BorderColor = value ? (BorderColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <inheritdoc cref="OverlayElementViewModel.StrokeColorForPicker"/>
    public Rgb24 BorderColorForPicker
    {
        get => BorderColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasBorder)
            {
                BorderColor = value;
            }
        }
    }

    /// <summary>Canvas-preview counterpart to <see cref="OverlayElementViewModel.ForegroundBrush"/> --
    /// same shape, shared <see cref="Imaging.GradientBrushFactory"/> implementation (code-review
    /// finding: an earlier version of this duplicated the brush-building logic locally to avoid any
    /// risk of touching the already-shipped text-gradient render path; the shared factory is a pure
    /// function of its own arguments with no VM state, so it can't regress that path by
    /// construction). The real pipeline side (<c>TransmitImagePreparer.BuildGradientBrush</c>) is
    /// ALREADY shared between text and box -- this brings the canvas-preview side in line too.</summary>
    public IBrush FillBrush => GradientEnabled
        ? GradientBrushFactory.Build(GradientKind, GradientStartColor, GradientEndColor)
        : new SolidColorBrush(ToAvaloniaColor(FillColor));

    private static AvaloniaColor ToAvaloniaColor(Rgb24 color) => AvaloniaColor.FromRgb(color.R, color.G, color.B);

    partial void OnXChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnYChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnWidthChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnHeightChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(LeftPixels));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(TopPixels));

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
    }

    partial void OnHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
    }

    partial void OnImageWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
    }

    partial void OnImageHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(CanvasBorderThicknessPixels));
        OnPropertyChanged(nameof(CanvasCornerRadiusPixels));
    }

    partial void OnBorderThicknessChanged(double value)
    {
        OnPropertyChanged(nameof(CanvasBorderThicknessPixels));
        OnPropertyChanged(nameof(BorderThicknessPx));
    }

    partial void OnCornerRadiusChanged(double value)
    {
        OnPropertyChanged(nameof(CanvasCornerRadiusPixels));
        OnPropertyChanged(nameof(CornerRadiusPx));
    }

    partial void OnBorderColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasBorder));
        OnPropertyChanged(nameof(BorderColorForPicker));
    }

    // Fill & Border flyout undo wiring -- see PushUndoSnapshotForStyleChange's own doc comment for
    // why these five didn't have one before this change.
    partial void OnFillColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnFillColorChanged(Rgb24 value) => OnPropertyChanged(nameof(FillBrush));

    partial void OnBorderColorChanging(Rgb24? value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnBorderThicknessChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnOpacityChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnCornerRadiusChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    // Gradient fill undo wiring -- same PushUndoSnapshotForStyleChange convention as
    // FillColor/BorderColor/etc. above, plus a FillBrush re-notify so the canvas preview updates.
    partial void OnGradientEnabledChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientEnabledChanged(bool value) => OnPropertyChanged(nameof(FillBrush));

    partial void OnGradientKindChanging(TextGradientKind value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientKindChanged(TextGradientKind value) => OnPropertyChanged(nameof(FillBrush));

    partial void OnGradientStartColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientStartColorChanged(Rgb24 value) => OnPropertyChanged(nameof(FillBrush));

    partial void OnGradientEndColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientEndColorChanged(Rgb24 value) => OnPropertyChanged(nameof(FillBrush));
}
