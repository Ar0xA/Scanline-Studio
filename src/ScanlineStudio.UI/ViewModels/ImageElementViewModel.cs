using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
public sealed partial class ImageElementViewModel : ObservableObject, ITemplateElementViewModel, IDisposable
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
    private bool _isSelected;

    /// <summary>Set by <see cref="TxImageEditorPaneViewModel.SetAsBackground"/> (Phase 6,
    /// spec/15-template-designer.md) -- NOT inferred structurally from full-frame bounds (a manual
    /// drag could coincidentally produce the same bounds without meaning "background"). Combined
    /// with <see cref="Locked"/> via <see cref="BlocksHitTesting"/> to let a locked background
    /// element stop intercepting canvas clicks, so the crop rect underneath becomes reachable again
    /// -- see that property's own doc comment. Persisted (<c>PersistedImageElement.IsBackground</c>)
    /// unlike most purely-interactive-editing state, because <see cref="Locked"/> already persists
    /// and leaving this one unpersisted would round-trip a loaded background element into a WORSE
    /// state than before this phase (locked AND hit-blocking again, with no easy way back).</summary>
    [ObservableProperty]
    private bool _isBackground;

    [ObservableProperty]
    private IImageSource _source;

    [ObservableProperty]
    private ImageFitMode _fit = ImageFitMode.Contain;

    /// <summary>TX workflow modernization plan, Phase 7 -- the source's own native pixel dimensions
    /// AS INSERTED, captured before <c>TxImageEditorPaneViewModel.DownsampleToBudget</c> caps
    /// <see cref="Source"/> to the working-copy budget, so this is genuinely "original size" and not
    /// the budget-capped copy this element actually composites with. 0 means unknown (a template
    /// saved before this field existed, whose asset dimensions couldn't be resolved) --
    /// <c>ResetImageElementToOriginalSizeCommand</c>'s own <c>CanExecute</c> gates on this rather
    /// than guessing.</summary>
    [ObservableProperty]
    private int _naturalPixelWidth;

    [ObservableProperty]
    private int _naturalPixelHeight;

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

    public IRelayCommand? BringToFrontCommand { get; init; }

    public IRelayCommand? SendToBackCommand { get; init; }

    public IRelayCommand? DuplicateCommand { get; init; }

    public IRelayCommand? AlignSelectedElementToCropCommand { get; init; }

    /// <summary>Only image elements can be "set as background" (Phase 2 scope) -- not part of
    /// <see cref="ITemplateElementViewModel"/> itself, so this lives here rather than as a no-op on
    /// text/box. Same parent-pushed pattern as <see cref="RemoveCommand"/> -- bound directly in XAML
    /// (<c>Command="{Binding SetAsBackgroundCommand}" CommandParameter="{Binding}"</c>), never via a
    /// <c>$parent[ItemsControl]</c> binding path.</summary>
    public IRelayCommand? SetAsBackgroundCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.CopyCommand"/>
    public IRelayCommand? CopyCommand { get; init; }

    public IRelayCommand? CutCommand { get; init; }

    public IRelayCommand? PasteCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 "Fit ▸" image-menu submenu -- image-only,
    /// same parent-pushed pattern as <see cref="SetAsBackgroundCommand"/>, string CommandParameter
    /// (the fit mode name) same shape as <see cref="AlignSelectedElementToCropCommand"/>.</summary>
    public IRelayCommand? FitCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 7 -- image-only, element-parameterized (NOT
    /// selection-implicit) same shape as <see cref="RemoveCommand"/>/<see cref="SetAsBackgroundCommand"/>
    /// -- bound with <c>CommandParameter="{Binding}"</c>, never a <c>$parent[ItemsControl]</c>
    /// binding path (<see cref="SetAsBackgroundCommand"/>'s own doc comment explains why that pattern
    /// is a real, previously-hit crash in this codebase, not a style preference).</summary>
    public IRelayCommand? ResetToOriginalSizeCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.FlattenCommand"/>
    public IRelayCommand? FlattenCommand { get; init; }

    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    /// <summary>Which of Phase 2's 3 sources this image was resolved from (Phase 5/persistence needs
    /// this to decide how to serialize the element -- see
    /// <see cref="TxImageEditorPaneViewModel.RawImageElementSnapshot"/>'s own doc comment) -- never
    /// mutated after construction, so this is a plain init property, not an
    /// <c>[ObservableProperty]</c>.</summary>
    public required TxImageEditorPaneViewModel.ImageSourceOrigin Origin { get; init; }

    /// <summary>Phase 6 (spec/15-template-designer.md): only a background element that's ALSO
    /// locked stops intercepting canvas clicks -- an ordinary locked non-background element keeps
    /// its existing click-to-select behavior untouched (only drag-start is gated by
    /// <see cref="Locked"/> in code-behind, same as before this property existed). Unlocking a
    /// background element restores normal click/drag on it, at the cost of it blocking the crop
    /// rect underneath again -- an acceptable, discoverable tradeoff since the operator unlocked it
    /// on purpose.</summary>
    public bool BlocksHitTesting => Locked && IsBackground;

    partial void OnLockedChanged(bool value) => OnPropertyChanged(nameof(BlocksHitTesting));

    partial void OnIsBackgroundChanged(bool value) => OnPropertyChanged(nameof(BlocksHitTesting));

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
        // T0-11 (production_audit.md): dispose the OLD CanvasBitmap, deferred -- same
        // Dispatcher.UIThread.Post-at-Background-priority pattern as the other bitmap-disposal
        // sites this fix touches (see RxHistoryPaneViewModel.OnPreviewImageChanged's own comment
        // for the full reasoning). Never fires at construction (the ctor assigns the backing
        // field directly, not through this property's setter), so `old` here is always a real,
        // previously-displayed bitmap, never the placeholder from before Source was ever set.
        var old = CanvasBitmap;
        CanvasBitmap = ImageSourceBitmapConverter.ToBitmap(value);
        OnPropertyChanged(nameof(CanvasBitmap));
        Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
    }

    private bool _disposed;

    // T0-11: disposes CanvasBitmap when this element is discarded wholesale (template reload,
    // undo/redo ApplyState, single-element Remove -- see TxImageEditorPaneViewModel's call sites)
    // rather than reassigned in place (OnSourceChanged above already handles that case). Deferred,
    // same reasoning as OnSourceChanged -- the corresponding Image control's own detach from the
    // visual tree is not guaranteed synchronous with this call. Code-review finding: guarded against
    // a second call (IDisposable's own contract, even though nothing calls this twice today).
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispatcher.UIThread.Post(CanvasBitmap.Dispose, DispatcherPriority.Background);
    }
}
