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
/// construction-time wiring.
///
/// <para>Perspective, mode-switched X/Y/Width/Height and the warped preview live in
/// <see cref="PerspectiveElementViewModelBase"/>; the warped preview uses the base's per-axis size clamp.</para></summary>
public sealed partial class ImageElementViewModel : PerspectiveElementViewModelBase, ITemplateElementViewModel, IDisposable
{
    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Set by <see cref="TxImageEditorPaneViewModel.SetAsBackdrop"/> (Phase 6,
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

    public ImageElementViewModel(IImageSource source)
        : base(naturalHeight: 0.3)
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

    /// <summary>Only image elements can be "set as backdrop" (Phase 2 scope) -- not part of
    /// <see cref="ITemplateElementViewModel"/> itself, so this lives here rather than as a no-op on
    /// text/box. Same parent-pushed pattern as <see cref="RemoveCommand"/> -- bound directly in XAML
    /// (<c>Command="{Binding SetAsBackdropCommand}" CommandParameter="{Binding}"</c>), never via a
    /// <c>$parent[ItemsControl]</c> binding path.</summary>
    public IRelayCommand? SetAsBackdropCommand { get; init; }

    /// <summary>User-requested (2026-09-15, background-vs-backdrop naming/promote-demote work):
    /// the reverse of <see cref="SetAsBackdropCommand"/> -- bakes THIS backdrop element's own pixels
    /// into the editor's background photo and removes the element. Same parent-pushed,
    /// element-parameterized pattern; gated in XAML via <c>IsEnabled="{Binding IsBackground}"</c>
    /// rather than a separate CanExecute check on the element itself, since the command's own
    /// <c>CanExecute</c> already pattern-matches <see cref="IsBackground"/> on whatever element is
    /// passed as <c>CommandParameter</c> -- the XAML gate exists purely so the menu item doesn't
    /// render enabled-but-inert for a non-backdrop image element.</summary>
    public IRelayCommand? DemoteToBackgroundCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.CopyCommand"/>
    public IRelayCommand? CopyCommand { get; init; }

    public IRelayCommand? CutCommand { get; init; }

    public IRelayCommand? PasteCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 "Fit ▸" image-menu submenu -- image-only,
    /// same parent-pushed pattern as <see cref="SetAsBackdropCommand"/>, string CommandParameter
    /// (the fit mode name) same shape as <see cref="AlignSelectedElementToCropCommand"/>.</summary>
    public IRelayCommand? FitCommand { get; init; }

    /// <summary>User-requested (2026-09-15): "fit safe area"/"fit width"/"fit height" -- a DIFFERENT
    /// concept from <see cref="FitCommand"/> right above (that one changes how the image's own pixels
    /// fill its EXISTING bounds; this one resizes the bounds themselves to match the safe-area guide).
    /// Same selection-implicit/string-parameter shape as <see cref="FitCommand"/>.</summary>
    public IRelayCommand? FitToSafeAreaCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 7 -- image-only, element-parameterized (NOT
    /// selection-implicit) same shape as <see cref="RemoveCommand"/>/<see cref="SetAsBackdropCommand"/>
    /// -- bound with <c>CommandParameter="{Binding}"</c>, never a <c>$parent[ItemsControl]</c>
    /// binding path (<see cref="SetAsBackdropCommand"/>'s own doc comment explains why that pattern
    /// is a real, previously-hit crash in this codebase, not a style preference).</summary>
    public IRelayCommand? ResetToOriginalSizeCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.FlattenCommand"/>
    public IRelayCommand? FlattenCommand { get; init; }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- toggles
    /// <see cref="PerspectiveEnabled"/>. Same parent-pushed pattern as <see cref="SetAsBackdropCommand"/>,
    /// element-parameterized. Lives in the PARENT VM (not a plain <c>[RelayCommand]</c> here) because
    /// enabling seeds the 4 corners from the current bbox and disabling writes the current bbox back
    /// into <see cref="NaturalX"/>/etc -- both real, undo-worthy mutations the parent VM's own
    /// non-coalesced <c>PushUndoSnapshot</c> wraps explicitly, not the property-changed-hook coalesced
    /// path every other geometry edit uses (a toggle click landing inside an open coalescing window
    /// from a preceding drag would otherwise be silently swallowed, making the toggle un-undoable).</summary>
    public IRelayCommand? TogglePerspectiveCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.RotateClockwise90Command"/>
    public IRelayCommand? RotateClockwise90Command { get; init; }

    public IRelayCommand? RotateCounterclockwise90Command { get; init; }

    public IRelayCommand? Rotate180Command { get; init; }

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

    /// <summary>Background/backdrop naming work (2026-09-15): the ELEMENTS layers-list row's
    /// promote/demote button pair needs a REACTIVE "not backdrop" flag -- a real-window test caught
    /// that a raw <c>IsVisible="{Binding !IsBackground}"</c> binding does NOT react to
    /// <see cref="IsBackground"/> changing inside this specific <c>ItemsControl.DataTemplates</c>
    /// context (the promote button stayed visible after becoming a backdrop, confirmed via
    /// <c>ImageElementLayersRow_BackdropToggleButtons_ResolveAgainstTheRightElementAndFlipVisibility</c>)
    /// -- the plain, non-negated <c>{Binding IsBackground}</c> the demote button uses DOES react
    /// correctly in the same context, so the negation itself is what's not reactive here, not the
    /// binding mechanism generally. Same "wrap in a computed property with an explicit re-notify"
    /// fix <see cref="BlocksHitTesting"/> already established -- not chasing whether other existing
    /// raw <c>!Property</c> bindings elsewhere in this file have the same gap, out of scope here.</summary>
    public bool IsNotBackground => !IsBackground;

    /// <inheritdoc/>
    public TxImageEditorPaneViewModel.RawElementSnapshot ToSnapshot() => new TxImageEditorPaneViewModel.RawImageElementSnapshot(
        X: X, Y: Y, Width: Width, Height: Height, Z: Z, Locked: Locked,
        Source: Source, Fit: Fit, Origin: Origin, IsBackground: IsBackground,
        NaturalPixelWidth: NaturalPixelWidth, NaturalPixelHeight: NaturalPixelHeight,
        PerspectiveEnabled: PerspectiveEnabled,
        Corner0X: Corner0X, Corner0Y: Corner0Y, Corner1X: Corner1X, Corner1Y: Corner1Y,
        Corner2X: Corner2X, Corner2Y: Corner2Y, Corner3X: Corner3X, Corner3Y: Corner3Y,
        RotationDegrees: RotationDegrees);

    partial void OnIsBackgroundChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksHitTesting));
        OnPropertyChanged(nameof(IsNotBackground));
    }

    /// <summary>Cached conversion of <see cref="Source"/> for canvas display -- rebuilt only when
    /// <see cref="Source"/> itself changes (plan-review finding: converting on every property-changed
    /// pass, e.g. every drag frame, would re-walk the whole source image's pixels for no reason since
    /// dragging/resizing never touches <see cref="Source"/>).</summary>
    public WriteableBitmap CanvasBitmap { get; private set; }

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

        // TX editor gap-items plan, item 3 -- Source is the image's own warp CONTENT (Fit is
        // deliberately ignored by the warp render, so no OnFitChanged trigger is needed here).
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    protected override void OnLockedChangedCore() => OnPropertyChanged(nameof(BlocksHitTesting));

    // Geometry undo: this VM has no style-undo hook, and adding one just for rotation would be a new threading site.
    protected override void OnRotationDegreesChangingCore() => PushUndoSnapshotForGeometryChange?.Invoke();

    // T0-11: disposes CanvasBitmap/WarpedCanvasBitmap when this element is discarded wholesale
    // (template reload, undo/redo ApplyState, single-element Remove -- see
    // TxImageEditorPaneViewModel's call sites) rather than reassigned in place (OnSourceChanged/
    // OnPerspectiveEnabledChanged already handle those cases). Deferred, same reasoning as
    // OnSourceChanged -- the corresponding Image control's own detach from the visual tree is not
    // guaranteed synchronous with this call. Code-review finding: guarded against a second call
    // (IDisposable's own contract, even though nothing calls this twice today).
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        Dispatcher.UIThread.Post(CanvasBitmap.Dispose, DispatcherPriority.Background);
        DisposeWarpedPreview();
    }
}
