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
/// <para><b>X/Y/Width/Height are MODE-SWITCHED, not plain backing fields</b> (TX editor gap-items
/// plan, item 3, perspective transform, 2026-09-02 -- 3 rounds of plan-review settled this design).
/// <see cref="NaturalX"/>/<see cref="NaturalY"/>/<see cref="NaturalWidth"/>/<see cref="NaturalHeight"/>
/// are the real <c>[ObservableProperty]</c> fields for the ordinary (unwarped) case; <see cref="Corner0X"/>
/// .. <see cref="Corner3Y"/> are the real fields for the perspective case. <see cref="X"/>/<see cref="Y"/>/
/// <see cref="Width"/>/<see cref="Height"/> below are HAND-WRITTEN properties (not
/// <c>[ObservableProperty]</c> -- a source-generated property can't have a runtime mode branch
/// injected into its body) that delegate to whichever set is truth right now, get/set. Delegating to
/// a real <c>[ObservableProperty]</c> field either way means the generator's own equality-guard/undo-
/// push/notify sequence fires correctly with ZERO manual replication -- confirmed against
/// <see cref="LineElementViewModel"/>'s own identical mechanism (endpoints are truth, X/Y/Width/Height
/// are hand-written cascades over them) before this design was trusted.</para></summary>
public sealed partial class ImageElementViewModel : ObservableObject, ITemplateElementViewModel, IDisposable
{
    [ObservableProperty]
    private double _naturalX = 0.5;

    [ObservableProperty]
    private double _naturalY = 0.5;

    [ObservableProperty]
    private double _naturalWidth = 0.3;

    [ObservableProperty]
    private double _naturalHeight = 0.3;

    [ObservableProperty]
    private bool _perspectiveEnabled;

    [ObservableProperty]
    private double _corner0X;

    [ObservableProperty]
    private double _corner0Y;

    [ObservableProperty]
    private double _corner1X;

    [ObservableProperty]
    private double _corner1Y;

    [ObservableProperty]
    private double _corner2X;

    [ObservableProperty]
    private double _corner2Y;

    [ObservableProperty]
    private double _corner3X;

    [ObservableProperty]
    private double _corner3Y;

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

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- toggles
    /// <see cref="PerspectiveEnabled"/>. Same parent-pushed pattern as <see cref="SetAsBackgroundCommand"/>,
    /// element-parameterized. Lives in the PARENT VM (not a plain <c>[RelayCommand]</c> here) because
    /// enabling seeds the 4 corners from the current bbox and disabling writes the current bbox back
    /// into <see cref="NaturalX"/>/etc -- both real, undo-worthy mutations the parent VM's own
    /// non-coalesced <c>PushUndoSnapshot</c> wraps explicitly, not the property-changed-hook coalesced
    /// path every other geometry edit uses (a toggle click landing inside an open coalescing window
    /// from a preceding drag would otherwise be silently swallowed, making the toggle un-undoable).</summary>
    public IRelayCommand? TogglePerspectiveCommand { get; init; }

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

    /// <summary>TX editor gap-items plan, item 3 -- the 8 axis-aligned resize handles and the 4
    /// perspective-corner handles are mutually exclusive, both additionally gated on <see cref="Locked"/>
    /// (every existing handle already binds <c>IsVisible="{Binding !Locked}"</c> -- these preserve
    /// that, not just add a perspective gate on top).</summary>
    public bool ShowResizeHandles => !Locked && !PerspectiveEnabled;

    public bool ShowPerspectiveCornerHandles => !Locked && PerspectiveEnabled;

    partial void OnLockedChanged(bool value)
    {
        OnPropertyChanged(nameof(BlocksHitTesting));
        OnPropertyChanged(nameof(ShowResizeHandles));
        OnPropertyChanged(nameof(ShowPerspectiveCornerHandles));
    }

    partial void OnIsBackgroundChanged(bool value) => OnPropertyChanged(nameof(BlocksHitTesting));

    /// <summary>Get: <see cref="NaturalX"/> in the ordinary case, or the CURRENT corners' own bbox
    /// center once <see cref="PerspectiveEnabled"/> (bbox recomputed fresh each read -- corners can
    /// change between reads via a drag). Set: writes <see cref="NaturalX"/> directly, or translates
    /// all 4 corners by the same delta -- every generic caller that moves an element via this shared
    /// interface property (drag-move, align-to-crop, clone/paste offset, arrow-key nudge, sidebar
    /// GEOMETRY fields) gets correct behavior in EITHER mode with no mode-awareness of its own,
    /// which is the entire point of this design.</summary>
    public double X
    {
        get
        {
            if (!PerspectiveEnabled)
            {
                return NaturalX;
            }

            var bbox = CornersBoundingBox();
            return bbox.X + (bbox.Width / 2);
        }
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            if (PerspectiveEnabled)
            {
                var delta = value - X;
                Corner0X += delta;
                Corner1X += delta;
                Corner2X += delta;
                Corner3X += delta;
            }
            else
            {
                NaturalX = value;
            }
        }
    }

    /// <inheritdoc cref="X"/>
    public double Y
    {
        get
        {
            if (!PerspectiveEnabled)
            {
                return NaturalY;
            }

            var bbox = CornersBoundingBox();
            return bbox.Y + (bbox.Height / 2);
        }
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            if (PerspectiveEnabled)
            {
                var delta = value - Y;
                Corner0Y += delta;
                Corner1Y += delta;
                Corner2Y += delta;
                Corner3Y += delta;
            }
            else
            {
                NaturalY = value;
            }
        }
    }

    /// <summary>Get: <see cref="NaturalWidth"/>, or the corners' own bbox width once
    /// <see cref="PerspectiveEnabled"/>. Set: rejects non-finite input, clamps to non-negative, and
    /// (perspective mode) scales each corner's X-offset from the bbox CENTER by
    /// <c>newWidth / currentWidth</c> -- the natural generalization of "scale about center" from a
    /// rectangle's 2 dimensions to an arbitrary quad's 4 corners. A currently-zero bbox width (a
    /// degenerate quad) has no meaningful scale factor to preserve -- falls back to a no-op rather
    /// than dividing by zero, same "never throw" convention as every other decorative/edge-case path
    /// in this feature.</summary>
    public double Width
    {
        get
        {
            if (!PerspectiveEnabled)
            {
                return NaturalWidth;
            }

            return CornersBoundingBox().Width;
        }
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            var w = Math.Max(0, value);
            if (PerspectiveEnabled)
            {
                var bbox = CornersBoundingBox();
                var centerX = bbox.X + (bbox.Width / 2);
                if (bbox.Width <= 1e-9)
                {
                    return;
                }

                var scale = w / bbox.Width;
                Corner0X = centerX + ((Corner0X - centerX) * scale);
                Corner1X = centerX + ((Corner1X - centerX) * scale);
                Corner2X = centerX + ((Corner2X - centerX) * scale);
                Corner3X = centerX + ((Corner3X - centerX) * scale);
            }
            else
            {
                NaturalWidth = w;
            }
        }
    }

    /// <inheritdoc cref="Width"/>
    public double Height
    {
        get
        {
            if (!PerspectiveEnabled)
            {
                return NaturalHeight;
            }

            return CornersBoundingBox().Height;
        }
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            var h = Math.Max(0, value);
            if (PerspectiveEnabled)
            {
                var bbox = CornersBoundingBox();
                var centerY = bbox.Y + (bbox.Height / 2);
                if (bbox.Height <= 1e-9)
                {
                    return;
                }

                var scale = h / bbox.Height;
                Corner0Y = centerY + ((Corner0Y - centerY) * scale);
                Corner1Y = centerY + ((Corner1Y - centerY) * scale);
                Corner2Y = centerY + ((Corner2Y - centerY) * scale);
                Corner3Y = centerY + ((Corner3Y - centerY) * scale);
            }
            else
            {
                NaturalHeight = h;
            }
        }
    }

    private NormalizedRect CornersBoundingBox() =>
        new PerspectiveCorners(Corner0X, Corner0Y, Corner1X, Corner1Y, Corner2X, Corner2Y, Corner3X, Corner3Y).ToBoundingBox();

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>Corner N in CONTAINER-relative canvas pixels (relative to this element's own
    /// <see cref="LeftPixels"/>/<see cref="TopPixels"/>) -- what a perspective-corner handle's own
    /// position binds to, same convention as <see cref="LineElementViewModel.CanvasStartPoint"/>.</summary>
    public Avalonia.Point CanvasCorner0Point => new((Corner0X * ImageWidth) - LeftPixels, (Corner0Y * ImageHeight) - TopPixels);

    public Avalonia.Point CanvasCorner1Point => new((Corner1X * ImageWidth) - LeftPixels, (Corner1Y * ImageHeight) - TopPixels);

    public Avalonia.Point CanvasCorner2Point => new((Corner2X * ImageWidth) - LeftPixels, (Corner2Y * ImageHeight) - TopPixels);

    public Avalonia.Point CanvasCorner3Point => new((Corner3X * ImageWidth) - LeftPixels, (Corner3Y * ImageHeight) - TopPixels);

    /// <summary>Cached conversion of <see cref="Source"/> for canvas display -- rebuilt only when
    /// <see cref="Source"/> itself changes (plan-review finding: converting on every property-changed
    /// pass, e.g. every drag frame, would re-walk the whole source image's pixels for no reason since
    /// dragging/resizing never touches <see cref="Source"/>).</summary>
    public WriteableBitmap CanvasBitmap { get; private set; }

    /// <summary>TX editor gap-items plan, item 3 -- the live warped-preview bitmap, real PREMULTIPLIED
    /// alpha (unlike <see cref="CanvasBitmap"/>, which is always opaque) so a warped rounded-corner
    /// box's true silhouette (warped ARCS, not a plain quadrilateral) renders correctly with zero
    /// clip-geometry code on the AXAML side. Null whenever <see cref="PerspectiveEnabled"/> is false.</summary>
    public WriteableBitmap? WarpedCanvasBitmap { get; private set; }

    /// <summary>Parent-pushed (mirrors <see cref="PushUndoSnapshotForGeometryChange"/>'s own "parent
    /// pushes a delegate, element doesn't reach back into the parent" convention) -- builds this
    /// element's own <see cref="TemplateImageElement"/> at CALL time (reading the element's live
    /// current state, not a snapshot captured when the delegate was assigned) and renders it warped
    /// via <c>ITransmitImagePreparer.RenderWarpedElementPreview</c>. HAND-WRITTEN (not
    /// <c>{ get; init; }</c>) because assigning it must itself trigger a rebuild if perspective is
    /// ALREADY enabled at assignment time -- without this, an element restored from undo/redo or a
    /// loaded template with <c>PerspectiveEnabled: true</c> would silently stay invisible (a real,
    /// found gap: the delegate can only be assigned AFTER construction, since it must close over the
    /// constructed element itself, which isn't in scope inside its own object initializer -- so by
    /// the time it's assigned, the property-changed hook that would normally trigger the first
    /// rebuild has already fired and gone).</summary>
    public Func<int, int, BgraPixelBuffer>? RenderWarpedPreview
    {
        get => _renderWarpedPreview;
        set
        {
            _renderWarpedPreview = value;
            if (PerspectiveEnabled)
            {
                ScheduleWarpedPreviewRebuild();
            }
        }
    }

    private Func<int, int, BgraPixelBuffer>? _renderWarpedPreview;

    private bool _warpedPreviewRebuildScheduled;

    // Display-oriented ceiling, not the final transmitted render's own (much larger)
    // MaxElementResizeDimensionPx -- this is a ZOOM-scaled on-screen preview bitmap, plenty at 2048px
    // for any reasonable on-screen element size regardless of how far the working copy is zoomed in.
    private const int PreviewSizeCeilingPx = 2048;

    private void ScheduleWarpedPreviewRebuild()
    {
        if (_warpedPreviewRebuildScheduled)
        {
            return;
        }

        _warpedPreviewRebuildScheduled = true;
        // SAME DispatcherPriority.Input (not Background) as RecomputePreviewCoalesced already uses --
        // this codebase already measured Background-priority work starving during a dense sustained
        // drag.
        Dispatcher.UIThread.Post(RebuildWarpedPreview, DispatcherPriority.Input);
    }

    private void RebuildWarpedPreview()
    {
        _warpedPreviewRebuildScheduled = false;
        if (_disposed || !PerspectiveEnabled || RenderWarpedPreview is null)
        {
            return;
        }

        var targetWidth = Math.Max(1, (int)Math.Round(Math.Min(CanvasWidthPixels, PreviewSizeCeilingPx)));
        var targetHeight = Math.Max(1, (int)Math.Round(Math.Min(CanvasHeightPixels, PreviewSizeCeilingPx)));
        var buffer = RenderWarpedPreview(targetWidth, targetHeight);

        var old = WarpedCanvasBitmap;
        WarpedCanvasBitmap = BgraPixelBufferConverter.ToBitmap(buffer);
        OnPropertyChanged(nameof(WarpedCanvasBitmap));
        if (old is not null)
        {
            Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
    }

    partial void OnNaturalXChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnNaturalYChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnNaturalWidthChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnNaturalHeightChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnNaturalXChanged(double value) => RaiseGeometryChanged();

    partial void OnNaturalYChanged(double value) => RaiseGeometryChanged();

    partial void OnNaturalWidthChanged(double value) => RaiseGeometryChanged();

    partial void OnNaturalHeightChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner0XChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner0YChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner1XChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner1YChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner2XChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner2YChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner3XChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner3YChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnCorner0XChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner0YChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner1XChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner1YChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner2XChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner2YChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner3XChanged(double value) => RaiseGeometryChanged();

    partial void OnCorner3YChanged(double value) => RaiseGeometryChanged();

    /// <summary>Every derived property that could have changed from a Natural*/corner write, plus
    /// the mode flag itself -- same "one shared cascade" shape as
    /// <see cref="LineElementViewModel.RaiseGeometryChanged"/>. The parent VM's own recompute-
    /// notification filter (<c>OnOverlayElementPropertyChanged</c>) filters <see cref="X"/>/<see cref="Y"/>/
    /// <see cref="Width"/>/<see cref="Height"/> back out for a perspective-enabled sender specifically
    /// (mirroring the existing <see cref="LineElementViewModel"/> filter entry), so this drives
    /// exactly one recompute pass per change, plus schedules a warped-preview rebuild when
    /// perspective is on.</summary>
    private void RaiseGeometryChanged()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(CanvasCorner0Point));
        OnPropertyChanged(nameof(CanvasCorner1Point));
        OnPropertyChanged(nameof(CanvasCorner2Point));
        OnPropertyChanged(nameof(CanvasCorner3Point));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    /// <summary>Toggling the mode itself changes what <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/
    /// <see cref="Height"/> compute -- no seeding/bbox-writeback logic here (that belongs at the
    /// interactive-command tier, see <see cref="TogglePerspectiveCommand"/>'s own doc comment: doing
    /// it in this hook would silently re-seed/wipe a restored or loaded warp, since a restore path
    /// sets this flag via plain object-initializer assignment). Purely reactive bookkeeping: raise
    /// the cascade, raise the handle-visibility gates, and manage <see cref="WarpedCanvasBitmap"/>'s
    /// own lifetime.</summary>
    partial void OnPerspectiveEnabledChanged(bool value)
    {
        RaiseGeometryChanged();
        OnPropertyChanged(nameof(ShowResizeHandles));
        OnPropertyChanged(nameof(ShowPerspectiveCornerHandles));
        if (!value)
        {
            var old = WarpedCanvasBitmap;
            WarpedCanvasBitmap = null;
            OnPropertyChanged(nameof(WarpedCanvasBitmap));
            if (old is not null)
            {
                Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
            }
        }
    }

    partial void OnImageWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
        OnPropertyChanged(nameof(CanvasCorner0Point));
        OnPropertyChanged(nameof(CanvasCorner1Point));
        OnPropertyChanged(nameof(CanvasCorner2Point));
        OnPropertyChanged(nameof(CanvasCorner3Point));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnImageHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(CanvasCorner0Point));
        OnPropertyChanged(nameof(CanvasCorner1Point));
        OnPropertyChanged(nameof(CanvasCorner2Point));
        OnPropertyChanged(nameof(CanvasCorner3Point));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
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

        // TX editor gap-items plan, item 3 -- Source is the image's own warp CONTENT (Fit is
        // deliberately ignored by the warp render, so no OnFitChanged trigger is needed here).
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    private bool _disposed;

    // T0-11: disposes CanvasBitmap/WarpedCanvasBitmap when this element is discarded wholesale
    // (template reload, undo/redo ApplyState, single-element Remove -- see
    // TxImageEditorPaneViewModel's call sites) rather than reassigned in place (OnSourceChanged/
    // OnPerspectiveEnabledChanged above already handle those cases). Deferred, same reasoning as
    // OnSourceChanged -- the corresponding Image control's own detach from the visual tree is not
    // guaranteed synchronous with this call. Code-review finding: guarded against a second call
    // (IDisposable's own contract, even though nothing calls this twice today).
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Dispatcher.UIThread.Post(CanvasBitmap.Dispose, DispatcherPriority.Background);
        if (WarpedCanvasBitmap is { } warped)
        {
            Dispatcher.UIThread.Post(warped.Dispose, DispatcherPriority.Background);
        }
    }
}
