using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Shared perspective/geometry state of <see cref="BoxElementViewModel"/> and
/// <see cref="ImageElementViewModel"/>. A base class rather than a composed quad object, so every
/// property keeps its name and its <c>PropertyChanged</c> sender is still the element itself (AXAML
/// paths, the pane's <c>OnOverlayElementPropertyChanged</c> name filter, the <c>Changing</c> undo hooks
/// and the round-trip reflection inventory all depend on that). <see cref="LineElementViewModel"/> is
/// deliberately not a subclass: its endpoints, not a quad, are its truth.
///
/// <para><b>X/Y/Width/Height are MODE-SWITCHED, not plain backing fields</b> (TX editor gap-items
/// plan, item 3, perspective transform -- 3 rounds of plan-review settled this design).
/// <see cref="NaturalX"/>/<see cref="NaturalY"/>/<see cref="NaturalWidth"/>/<see cref="NaturalHeight"/>
/// are the real <c>[ObservableProperty]</c> fields for the ordinary (unwarped) case; <see cref="Corner0X"/>
/// .. <see cref="Corner3Y"/> are the real fields for the perspective case. <see cref="X"/>/<see cref="Y"/>/
/// <see cref="Width"/>/<see cref="Height"/> below are HAND-WRITTEN properties (not
/// <c>[ObservableProperty]</c> -- a source-generated property can't have a runtime mode branch
/// injected into its body) that delegate to whichever set is truth right now, get/set. Delegating to
/// a real <c>[ObservableProperty]</c> field either way means the generator's own equality-guard/undo-
/// push/notify sequence fires correctly with ZERO manual replication -- confirmed against
/// <see cref="LineElementViewModel"/>'s own identical mechanism (endpoints are truth, X/Y/Width/Height
/// are hand-written cascades over them) before this design was trusted.</para>
///
/// <para>Disposal: this base does not implement <see cref="IDisposable"/>. Each sealed subclass keeps
/// its own <c>Dispose()</c>, returns early when <see cref="IsDisposed"/>, releases its own resources,
/// then calls <see cref="DisposeWarpedPreview"/>.</para></summary>
public abstract partial class PerspectiveElementViewModelBase : ObservableObject
{
    [ObservableProperty]
    private double _naturalX = 0.5;

    [ObservableProperty]
    private double _naturalY = 0.5;

    [ObservableProperty]
    private double _naturalWidth = 0.3;

    [ObservableProperty]
    private double _naturalHeight;

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
    private bool _locked;

    /// <summary>Element rotation (2026-09-20) -- in-plane (2D) rotation, clockwise-positive, same
    /// convention as <see cref="OverlayElementViewModel.RotationDegrees"/>. Mutually exclusive with
    /// <see cref="PerspectiveEnabled"/> (see <see cref="Abstractions.Imaging.TemplateImageElement.RotationDegrees"/>'s
    /// doc comment for the full Perspective-wins precedence rule) -- entering perspective mode resets
    /// this to 0 (<c>TxImageEditorPaneViewModel.TogglePerspective</c>), and <see cref="CanRotate"/>
    /// gates the Rotate context-menu items off while perspective is active. Each subclass picks its
    /// own undo hook via <see cref="OnRotationDegreesChangingCore"/>.</summary>
    [ObservableProperty]
    private double _rotationDegrees;

    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    /// <summary>The subclasses' unwarped default height differs (box 0.2, image 0.3), so it is written
    /// straight to the backing field: no change hook or undo push fires at construction.</summary>
    protected PerspectiveElementViewModelBase(double naturalHeight)
    {
        _naturalHeight = naturalHeight;
    }

    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    /// <summary>TX editor gap-items plan, item 3 -- the 8 axis-aligned resize handles and the 4
    /// perspective-corner handles are mutually exclusive, both additionally gated on <see cref="Locked"/>
    /// (every existing handle already binds <c>IsVisible="{Binding !Locked}"</c> -- these preserve
    /// that, not just add a perspective gate on top).</summary>
    public bool ShowResizeHandles => !Locked && !PerspectiveEnabled;

    public bool ShowPerspectiveCornerHandles => !Locked && PerspectiveEnabled;

    /// <inheritdoc cref="OverlayElementViewModel.RotationTransform"/>
    public Transform? RotationTransform => RotationDegrees != 0 ? new RotateTransform(RotationDegrees) : null;

    /// <summary>Element rotation (2026-09-20) -- gates the Rotate context-menu items off while
    /// perspective is active, same Perspective-wins precedence <see cref="RotationDegrees"/>'s own
    /// doc comment states. <see cref="Locked"/> gate matches every other geometry command's own
    /// 3-gate convention (CanExecute + body backstop in <c>TxImageEditorPaneViewModel</c> + this
    /// AXAML-bound property).</summary>
    public bool CanRotate => !Locked && !PerspectiveEnabled;

    /// <summary>The 4 corner fields as one value, in the element's own normalized space.</summary>
    public PerspectiveCorners Corners => new(Corner0X, Corner0Y, Corner1X, Corner1Y, Corner2X, Corner2Y, Corner3X, Corner3Y);

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

            var bbox = Corners.ToBoundingBox();
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

            var bbox = Corners.ToBoundingBox();
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

            return Corners.ToBoundingBox().Width;
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
                var bbox = Corners.ToBoundingBox();
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

            return Corners.ToBoundingBox().Height;
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
                var bbox = Corners.ToBoundingBox();
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

    /// <summary>Seeds the 4 corners clockwise from top-left out of the CURRENT X/Y/Width/Height bbox.
    /// Leaves <see cref="PerspectiveEnabled"/> alone; the caller owns the undo step.</summary>
    public void SeedCornersFromBounds()
    {
        var left = X - (Width / 2);
        var top = Y - (Height / 2);
        var right = X + (Width / 2);
        var bottom = Y + (Height / 2);
        Corner0X = left;
        Corner0Y = top;
        Corner1X = right;
        Corner1Y = top;
        Corner2X = right;
        Corner2Y = bottom;
        Corner3X = left;
        Corner3Y = bottom;
    }

    /// <summary>Writes the CURRENT corners' bbox back into the Natural fields, then turns perspective
    /// off, so the element keeps its on-canvas bounds. The caller owns the undo step.</summary>
    public void DisablePerspectiveKeepingBounds()
    {
        NaturalX = X;
        NaturalY = Y;
        NaturalWidth = Width;
        NaturalHeight = Height;
        PerspectiveEnabled = false;
    }

    /// <summary>TX editor gap-items plan, item 3 -- see <c>TxImageEditorPaneViewModel.Rotate</c>'s own
    /// call-site comment for why this bypasses the derived X/Y/Width/Height setters entirely, applying
    /// the SAME (x,y) -&gt; (1-y,x) 90°-clockwise transform every other element kind's own generic path
    /// uses, directly to all 4 corner fields.</summary>
    public void RotateCornersInFrameClockwise()
    {
        var (x0, y0) = (Corner0X, Corner0Y);
        var (x1, y1) = (Corner1X, Corner1Y);
        var (x2, y2) = (Corner2X, Corner2Y);
        var (x3, y3) = (Corner3X, Corner3Y);
        Corner0X = 1 - y0;
        Corner0Y = x0;
        Corner1X = 1 - y1;
        Corner1Y = x1;
        Corner2X = 1 - y2;
        Corner2Y = x2;
        Corner3X = 1 - y3;
        Corner3Y = x3;
    }

    /// <summary>Writes corner <paramref name="cornerIndex"/> (0-3, X then Y); any other index is a no-op.</summary>
    public void SetCorner(int cornerIndex, double x, double y)
    {
        switch (cornerIndex)
        {
            case 0: Corner0X = x; Corner0Y = y; break;
            case 1: Corner1X = x; Corner1Y = y; break;
            case 2: Corner2X = x; Corner2Y = y; break;
            case 3: Corner3X = x; Corner3Y = y; break;
        }
    }

    /// <summary>TX editor gap-items plan, item 3 -- the live warped-preview bitmap, real PREMULTIPLIED
    /// alpha (unlike <see cref="ImageElementViewModel.CanvasBitmap"/>, which is always opaque) so a warped
    /// rounded-corner box's true silhouette (warped ARCS, not a plain quadrilateral) renders correctly
    /// with zero clip-geometry code on the AXAML side. Null whenever <see cref="PerspectiveEnabled"/> is
    /// false.</summary>
    public WriteableBitmap? WarpedCanvasBitmap { get; private set; }

    /// <summary>Parent-pushed (mirrors <see cref="PushUndoSnapshotForGeometryChange"/>'s own "parent
    /// pushes a delegate, element doesn't reach back into the parent" convention) -- builds this
    /// element's own <see cref="TemplateElement"/> at CALL time (reading the element's live current
    /// state, not a snapshot captured when the delegate was assigned) and renders it warped via
    /// <c>ITransmitImagePreparer.RenderWarpedElementPreview</c>. HAND-WRITTEN (not
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
    protected const int PreviewSizeCeilingPx = 2048;

    /// <summary>True once the subclass's <c>Dispose()</c> has run <see cref="DisposeWarpedPreview"/>.</summary>
    protected bool IsDisposed { get; private set; }

    protected void ScheduleWarpedPreviewRebuild()
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
        if (IsDisposed || !PerspectiveEnabled || RenderWarpedPreview is null)
        {
            return;
        }

        var (targetWidth, targetHeight) = ComputeWarpedPreviewSize();
        var buffer = RenderWarpedPreview(targetWidth, targetHeight);

        var old = WarpedCanvasBitmap;
        WarpedCanvasBitmap = BgraPixelBufferConverter.ToBitmap(buffer);
        OnPropertyChanged(nameof(WarpedCanvasBitmap));
        if (old is not null)
        {
            Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
    }

    /// <summary>Warped-preview bitmap size in pixels: each axis clamped to
    /// <see cref="PreviewSizeCeilingPx"/> on its own (the image is drawn with <c>Stretch="Fill"</c> and
    /// has no border or radius to distort).</summary>
    protected virtual (int Width, int Height) ComputeWarpedPreviewSize() =>
        (Math.Max(1, (int)Math.Round(Math.Min(CanvasWidthPixels, PreviewSizeCeilingPx))),
         Math.Max(1, (int)Math.Round(Math.Min(CanvasHeightPixels, PreviewSizeCeilingPx))));

    /// <summary>Marks the element disposed (no further preview rebuilds) and disposes
    /// <see cref="WarpedCanvasBitmap"/>, deferred: the Image control showing it may not have detached
    /// from the visual tree yet.</summary>
    protected void DisposeWarpedPreview()
    {
        IsDisposed = true;
        if (WarpedCanvasBitmap is { } warped)
        {
            Dispatcher.UIThread.Post(warped.Dispose, DispatcherPriority.Background);
        }
    }

    /// <summary>Extra notifications after the shared geometry cascade.</summary>
    protected virtual void OnGeometryChangedCore()
    {
    }

    /// <summary>Extra notifications after the handle-visibility gates, before <see cref="CanRotate"/>.</summary>
    protected virtual void OnPerspectiveEnabledChangedCore()
    {
    }

    /// <summary>Extra notifications after <see cref="LeftPixels"/>/<see cref="CanvasWidthPixels"/>.</summary>
    protected virtual void OnImageWidthChangedCore()
    {
    }

    /// <summary>Extra notifications after <see cref="TopPixels"/>/<see cref="CanvasHeightPixels"/>.</summary>
    protected virtual void OnImageHeightChangedCore()
    {
    }

    /// <summary>Extra notifications before the handle-visibility gates.</summary>
    protected virtual void OnLockedChangedCore()
    {
    }

    /// <summary>Pushes the subclass's undo step for a <see cref="RotationDegrees"/> change.</summary>
    protected abstract void OnRotationDegreesChangingCore();

    partial void OnLockedChanged(bool value)
    {
        OnLockedChangedCore();
        OnPropertyChanged(nameof(ShowResizeHandles));
        OnPropertyChanged(nameof(ShowPerspectiveCornerHandles));
        OnPropertyChanged(nameof(CanRotate));
    }

    partial void OnRotationDegreesChanging(double value) => OnRotationDegreesChangingCore();

    partial void OnRotationDegreesChanged(double value) => OnPropertyChanged(nameof(RotationTransform));

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
        OnGeometryChangedCore();
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    /// <summary>Toggling the mode itself changes what <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/
    /// <see cref="Height"/> compute -- no seeding/bbox-writeback logic here (that belongs at the
    /// interactive-command tier, see <see cref="ImageElementViewModel.TogglePerspectiveCommand"/>'s own
    /// doc comment: doing it in this hook would silently re-seed/wipe a restored or loaded warp, since
    /// a restore path sets this flag via plain object-initializer assignment). Purely reactive
    /// bookkeeping: raise the cascade, raise the handle-visibility gates, and manage
    /// <see cref="WarpedCanvasBitmap"/>'s own lifetime.</summary>
    partial void OnPerspectiveEnabledChanged(bool value)
    {
        RaiseGeometryChanged();
        OnPropertyChanged(nameof(ShowResizeHandles));
        OnPropertyChanged(nameof(ShowPerspectiveCornerHandles));
        OnPerspectiveEnabledChangedCore();
        OnPropertyChanged(nameof(CanRotate));
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
        OnImageWidthChangedCore();
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
        OnImageHeightChangedCore();
        OnPropertyChanged(nameof(CanvasCorner0Point));
        OnPropertyChanged(nameof(CanvasCorner1Point));
        OnPropertyChanged(nameof(CanvasCorner2Point));
        OnPropertyChanged(nameof(CanvasCorner3Point));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }
}
