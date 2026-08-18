using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;

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

    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>BorderThickness is normalized to image height (same convention as
    /// <see cref="OverlayElementViewModel.FontSizeRelative"/>, per spec/15-template-designer.md) --
    /// this converts it to the canvas's own pixel space, mirroring <see cref="TemplateBoxElement"/>'s
    /// pipeline-side rendering so the editor canvas is actually WYSIWYG for a bordered box.</summary>
    public double CanvasBorderThicknessPixels => BorderThickness * ImageHeight;

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
    }

    partial void OnBorderThicknessChanged(double value) => OnPropertyChanged(nameof(CanvasBorderThicknessPixels));

    partial void OnBorderColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasBorder));
        OnPropertyChanged(nameof(BorderColorForPicker));
    }
}
