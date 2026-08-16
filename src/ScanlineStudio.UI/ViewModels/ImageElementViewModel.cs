using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="TemplateImageElement"/> (Phase 2,
/// spec/15-template-designer.md) -- mirrors <see cref="BoxElementViewModel"/>'s own shape/wiring
/// pattern exactly (X/Y CENTER-anchored, same parent-pushed <see cref="RemoveCommand"/>/
/// <see cref="PushUndoSnapshotForGeometryChange"/> convention) since all three implement
/// <see cref="ITemplateElementViewModel"/> and share one remove/undo/reorder path in
/// <see cref="TxImageEditorPaneViewModel"/> -- see that class's own <c>CreateImageElement</c> for the
/// construction-time wiring.</summary>
public sealed partial class ImageElementViewModel : ObservableObject, ITemplateElementViewModel
{
    [ObservableProperty]
    private double _x = 0.5;

    [ObservableProperty]
    private double _y = 0.5;

    [ObservableProperty]
    private double _width = 0.3;

    [ObservableProperty]
    private double _height = 0.3;

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private IImageSource _source;

    [ObservableProperty]
    private ImageFitMode _fit = ImageFitMode.Contain;

    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    public ImageElementViewModel(IImageSource source)
    {
        _source = source;
        CanvasBitmap = ImageSourceBitmapConverter.ToBitmap(source);
    }

    public IRelayCommand? RemoveCommand { get; init; }

    public IRelayCommand? MoveUpCommand { get; init; }

    public IRelayCommand? MoveDownCommand { get; init; }

    /// <summary>Only image elements can be "set as background" (Phase 2 scope) -- not part of
    /// <see cref="ITemplateElementViewModel"/> itself, so this lives here rather than as a no-op on
    /// text/box. Same parent-pushed pattern as <see cref="RemoveCommand"/> -- bound directly in XAML
    /// (<c>Command="{Binding SetAsBackgroundCommand}" CommandParameter="{Binding}"</c>), never via a
    /// <c>$parent[ItemsControl]</c> binding path.</summary>
    public IRelayCommand? SetAsBackgroundCommand { get; init; }

    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    /// <summary>Which of Phase 2's 3 sources this image was resolved from (Phase 5/persistence needs
    /// this to decide how to serialize the element -- see
    /// <see cref="TxImageEditorPaneViewModel.RawImageElementSnapshot"/>'s own doc comment) -- never
    /// mutated after construction, so this is a plain init property, not an
    /// <c>[ObservableProperty]</c>.</summary>
    public required TxImageEditorPaneViewModel.ImageSourceOrigin Origin { get; init; }

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>Cached conversion of <see cref="Source"/> for canvas display -- rebuilt only when
    /// <see cref="Source"/> itself changes (plan-review finding: converting on every property-changed
    /// pass, e.g. every drag frame, would re-walk the whole source image's pixels for no reason since
    /// dragging/resizing never touches <see cref="Source"/>).</summary>
    public WriteableBitmap CanvasBitmap { get; private set; }

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
    }

    partial void OnSourceChanged(IImageSource value)
    {
        CanvasBitmap = ImageSourceBitmapConverter.ToBitmap(value);
        OnPropertyChanged(nameof(CanvasBitmap));
    }
}
