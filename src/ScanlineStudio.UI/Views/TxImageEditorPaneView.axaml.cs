using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

/// <summary>Code-behind owns only pointer/keyboard plumbing -- every actual crop/overlay mutation
/// goes through <see cref="TxImageEditorPaneViewModel"/>'s own already-tested public methods
/// (<c>DragCropMove</c>/<c>DragCropResize</c>/<c>NudgeCropMove</c>/<c>NudgeCropResize</c>), which take
/// normalized deltas -- this view's only job is converting real pointer pixels into those deltas.</summary>
public partial class TxImageEditorPaneView : UserControl
{
    private enum DragMode
    {
        None,
        CropMove,
        CropResize,
        Overlay,
        ElementResize,
    }

    /// <summary>Floor for element Width/Height during resize (code-review finding: an unclamped
    /// resize drag could drive Width/Height negative, which <c>ApplyTemplate</c> silently treats as
    /// "skip this element" -- a fast drag past the opposite corner made the element vanish from both
    /// the canvas and the transmitted image with no visible handle left to recover it, other than
    /// Undo). Mirrors <see cref="TxImageEditorPaneViewModel"/>'s own <c>MinNormalizedCropSize</c>
    /// (0.02) -- that constant is private to the VM, so this is a separate, deliberately identical
    /// value, not a shared reference.</summary>
    private const double MinNormalizedElementSize = 0.02;

    private DragMode _dragMode = DragMode.None;
    private Point _lastPointerPosition;
    private ITemplateElementViewModel? _draggedElement;

    // Undo/redo sub-piece: a gesture pushes ONE undo step, on the first real move, not on press
    // (a bare click that never moves shouldn't push a no-op step) -- reset in StartDrag, consumed
    // in OnCanvasPointerMoved.
    private bool _pushedUndoThisGesture;

    public TxImageEditorPaneView()
    {
        InitializeComponent();

        // Dock.Avalonia's ActiveDockable used to focus this pane for free when it opened; the fixed
        // shell's plain ContentControl swap (MainViewModel.ActiveEditor) does not, so arrow-key crop
        // nudge (OnCanvasKeyDown) would silently stop receiving key events without this.
        AttachedToVisualTree += (_, _) => EditorCanvas.Focus();

        // Fit needs the ScrollViewer's own real viewport size, which isn't known until after the
        // first layout pass -- AttachedToVisualTree (used above for focus) fires too early for
        // Bounds to be reliable; Loaded fires after layout completes.
        Loaded += (_, _) => ApplyFitFromViewport();

        // Task #23 (zoom slider addendum, plan-reviewed) -- Tunnel routing, not a plain XAML
        // PointerWheelChanged on EditorCanvas: at Fit zoom the working copy is usually SMALLER than
        // the viewport (WorkingCopyScaleFactor can put it well past the visible pane at 100%+), so
        // the pointer is very often over the ScrollViewer's own centering gutter, not the Canvas
        // itself -- a Canvas-attached handler would silently never fire there. Tunnel also runs
        // before ScrollContentPresenter's own wheel-scroll handling, which is what lets the
        // Ctrl/Cmd-gated branch below claim the event (e.Handled = true) ahead of native scroll.
        EditorScrollViewer.AddHandler(PointerWheelChangedEvent, OnEditorWheelChanged, RoutingStrategies.Tunnel);
    }

    private TxImageEditorPaneViewModel? ViewModel => DataContext as TxImageEditorPaneViewModel;

    private void OnFitButtonClick(object? sender, RoutedEventArgs e) => ApplyFitFromViewport();

    private void ApplyFitFromViewport() => ViewModel?.ApplyFit(EditorScrollViewer.Bounds.Width, EditorScrollViewer.Bounds.Height);

    // Backlog item (user request, 2026-08-17): 3 more Fit variants, same View-owns-viewport-size
    // Click-handler pattern as OnFitButtonClick above -- a plain Command binding can't reach
    // EditorScrollViewer.Bounds.
    private void OnFitSafeAreaClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyFitSafeArea(EditorScrollViewer.Bounds.Width, EditorScrollViewer.Bounds.Height);

    private void OnFitWidthClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyFitWidth(EditorScrollViewer.Bounds.Width);

    private void OnFitHeightClick(object? sender, RoutedEventArgs e) => ViewModel?.ApplyFitHeight(EditorScrollViewer.Bounds.Height);

    /// <summary>Task #23 (zoom slider addendum, plan-reviewed) -- Ctrl/Cmd+wheel zooms, anchored so
    /// the canvas pixel under the pointer stays under the pointer (near-universal convention for
    /// scroll-wheel zoom in image editors/maps/browsers; plain center-anchored zoom would lose track
    /// of whatever the operator was looking at on this canvas's typically-larger-than-viewport working
    /// copy). Plain wheel (no modifier) is left completely alone -- unhandled, falls through to
    /// EditorScrollViewer's own native scroll/pan, matching every other app's own convention.
    /// <para>Reads pointer position via <c>e.GetPosition(EditorCanvas)</c>, NOT
    /// <c>EditorScrollViewer.Offset</c> arithmetic (plan-review correction to an earlier draft): the
    /// ScrollViewer's content is a Panel that STRETCHES to the viewport, and the zoomed Canvas inside
    /// it (explicit bound Width/Height) is centered within that Panel whenever the content is smaller
    /// than the viewport (the normal state right after Fit) -- Offset alone doesn't account for that
    /// centering gutter, but GetPosition is transform-correct in every regime regardless.</para>
    /// <para><see cref="ScrollViewer.UpdateLayout"/> between the <see cref="TxImageEditorPaneViewModel.ZoomBy"/>
    /// call and reading the post-zoom pointer position (plan-review finding): the ZoomFactor write
    /// updates Canvas.Width/Height synchronously via data binding, but no layout pass has run yet, so
    /// Extent/Viewport (and therefore anything GetPosition or Offset would report) are still the
    /// PRE-zoom values without this -- an omitted UpdateLayout here would silently anchor against
    /// stale geometry.</para></summary>
    private void OnEditorWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0 || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || ViewModel is not { } vm
            || vm.CanvasDisplayWidth <= 0 || vm.CanvasDisplayHeight <= 0)
        {
            return;
        }

        var before = e.GetPosition(EditorCanvas);
        var fractionX = before.X / vm.CanvasDisplayWidth;
        var fractionY = before.Y / vm.CanvasDisplayHeight;

        vm.ZoomBy(Math.Pow(1.1, e.Delta.Y));
        EditorScrollViewer.UpdateLayout();

        var after = e.GetPosition(EditorCanvas);
        var offset = EditorScrollViewer.Offset;
        EditorScrollViewer.Offset = new Vector(
            ComputeAnchoredOffset(offset.X, fractionX, vm.CanvasDisplayWidth, after.X),
            ComputeAnchoredOffset(offset.Y, fractionY, vm.CanvasDisplayHeight, after.Y));
        e.Handled = true;
    }

    /// <summary>Pure pointer-anchor math, split out from <see cref="OnEditorWheelChanged"/> so it's
    /// unit-testable without simulating real Avalonia pointer/scroll events -- same
    /// no-<c>InternalsVisibleTo</c>/public-not-internal precedent as <see cref="ComputeElementResize"/>.
    /// Raising <paramref name="currentOffset"/> by <c>d</c> increases the content coordinate under a
    /// FIXED screen point by <c>d</c> too (scrolling right/down moves content coordinates left/up
    /// relative to the viewport in the usual sense, but Avalonia's <c>ScrollViewer.Offset</c> is
    /// defined the other way: it's how far the TOP-LEFT of the viewport has moved INTO the content),
    /// so the desired new offset is <c>currentOffset + (desired - actual)</c>, where
    /// <paramref name="fraction"/> * <paramref name="newContentSize"/> is the anchor point's new
    /// content-space coordinate (desired) and <paramref name="anchorAfter"/> is where that point
    /// currently reads in VIEWPORT space post-zoom (actual, from a fresh <c>GetPosition</c> call after
    /// the layout pass forced by <see cref="ScrollViewer.UpdateLayout"/>).</summary>
    public static double ComputeAnchoredOffset(double currentOffset, double fraction, double newContentSize, double anchorAfter) =>
        currentOffset + (fraction * newContentSize) - anchorAfter;

    private void OnCropBodyPointerPressed(object? sender, PointerPressedEventArgs e) => StartDrag(DragMode.CropMove, e);

    private void OnCropHandlePointerPressed(object? sender, PointerPressedEventArgs e) => StartDrag(DragMode.CropResize, e);

    /// <summary>Phase 1 (spec/15-template-designer.md): <see cref="ITemplateElementViewModel.Locked"/>
    /// is the REAL gate here -- the resize handle's own <c>IsVisible="{Binding !Locked}"</c> binding
    /// in XAML is only the visual cue, not something this method relies on for correctness (a
    /// locked-but-still-technically-hit-testable edge case shouldn't silently let a drag start).
    /// <para>Also selects the element (code-review finding: nothing else ever set
    /// <c>SelectedOverlayElement</c> except the "Add" commands, so once a second element was added,
    /// the insert-field chips -- gated on a TEXT element being selected -- became permanently
    /// unreachable with no way to re-select the first one).</para></summary>
    /// <summary>Task #24 (right-click context menu addendum, plan-reviewed) -- selection stays
    /// UNGATED (any button, including right, selects this element) since that's what lets the
    /// context menu's own bindings/CanExecute states already be correct by the time it opens
    /// (PointerPressed fires before the native ContextMenu opens on PointerReleased). The DRAG start
    /// below is gated to the LEFT button only -- a real blocker the plan-review caught: without this
    /// gate, a right-press here calls StartDrag, which captures the pointer on EditorCanvas; Avalonia
    /// then routes PointerReleased to EditorCanvas (the capturing element), never to this element's
    /// own Border, so the native ContextMenu (which opens off PointerReleased when the INITIAL press
    /// was the right button) never gets a chance to fire at all -- right-click silently did a
    /// (right-button) drag instead of opening a menu.</summary>
    private void OnOverlayElementPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ITemplateElementViewModel element } || ViewModel is not { } vm)
        {
            return;
        }

        vm.SelectedOverlayElement = element;

        if (element.Locked || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _draggedElement = element;
        StartDrag(DragMode.Overlay, e);
    }

    /// <summary>Single bottom-right resize handle per element (Phase 1 plan-review finding: mirrors
    /// the crop rect's own existing single-corner-handle convention rather than a 4-corner/8-handle
    /// system). Same <see cref="ITemplateElementViewModel.Locked"/> real-gate reasoning as
    /// <see cref="OnOverlayElementPointerPressed"/> above -- same left-button gate too (task #24
    /// plan-review finding: a right-press here would otherwise start a resize drag instead of letting
    /// the parent element's own context menu handle the release).</summary>
    private void OnElementResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ITemplateElementViewModel element } || element.Locked
            || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _draggedElement = element;
        StartDrag(DragMode.ElementResize, e);
    }

    /// <summary>Captures on <see cref="EditorCanvas"/> itself (not the pressed sub-control) so every
    /// subsequent move/release during this drag routes through the canvas's own handlers below,
    /// regardless of which element (crop body, resize handle, a canvas element, an element's own
    /// resize handle) was pressed.</summary>
    private void StartDrag(DragMode mode, PointerPressedEventArgs e)
    {
        _dragMode = mode;
        _pushedUndoThisGesture = false;
        _lastPointerPosition = e.GetPosition(EditorCanvas);
        e.Pointer.Capture(EditorCanvas);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        // [Code-review risk, fixed here] ZoomFactor <= 0 added to the guard -- ZoomFactor is a public
        // settable property (tests write it directly, e.g. via the VM's own [ObservableProperty]);
        // ApplyFit/ZoomActual both clamp it, but nothing stops a 0/negative value being assigned some
        // other way, which would otherwise divide-by-zero below into Infinity/NaN feeding straight
        // into an element's X/Y.
        if (_dragMode == DragMode.None || ViewModel is not { } vm || vm.WorkingCopyWidth <= 0 || vm.WorkingCopyHeight <= 0 || vm.ZoomFactor <= 0)
        {
            return;
        }

        // Phase 7 rearchitecture: EditorCanvas is now sized (and rendered) at CanvasDisplayWidth/
        // Height = WorkingCopyWidth/Height * ZoomFactor -- no ancestor render/layout transform is
        // involved anymore, so GetPosition returns real on-screen pixels INCLUDING zoom, and the
        // normalize-by-image-size divisor must include ZoomFactor too, or a drag at any zoom other
        // than 100% moves the element by the wrong (zoom-multiplied) amount -- this is the one spot
        // that has to change for pointer math to stay correct; everything downstream (DragCropMove/
        // DragCropResize/element X/Y/Width/Height math) already operates in normalized [0,1] space
        // and needs no zoom-awareness of its own. Divides by CanvasDisplayWidth/Height directly
        // (code-review nit) rather than recomputing WorkingCopyWidth * ZoomFactor here -- one less
        // place to desync if that definition ever changes.
        var current = e.GetPosition(EditorCanvas);
        var dxNormalized = (current.X - _lastPointerPosition.X) / vm.CanvasDisplayWidth;
        var dyNormalized = (current.Y - _lastPointerPosition.Y) / vm.CanvasDisplayHeight;

        // Crop-move/crop-resize push their own undo step here, lazily, on the FIRST REAL (nonzero)
        // move of this gesture -- not on PointerPressed (a bare click that never moves shouldn't
        // push a no-op step) and not on a zero-delta move event Avalonia can raise right after
        // press (code-review finding on an earlier draft). Element drags (move AND resize) do NOT
        // push here -- they push via ITemplateElementViewModel's own On*Changing hooks instead (see
        // PushUndoSnapshotForGeometryChange's own doc comment), a path that ALSO covers the X/Y/
        // Width/Height sidebar TextBoxes, which a View-level drag-only push here never would have.
        if (!_pushedUndoThisGesture && _dragMode is not (DragMode.Overlay or DragMode.ElementResize) && (dxNormalized != 0 || dyNormalized != 0))
        {
            vm.PushUndoSnapshotForDragGesture();
            _pushedUndoThisGesture = true;
        }

        _lastPointerPosition = current;

        switch (_dragMode)
        {
            case DragMode.CropMove:
                vm.DragCropMove(dxNormalized, dyNormalized);
                break;
            case DragMode.CropResize:
                vm.DragCropResize(dxNormalized, dyNormalized);
                break;
            case DragMode.Overlay when _draggedElement is { } element:
                // Free drag -- overflow past the image bounds is allowed (clipped at render time
                // only), matching spec/07-image-pipeline.md's overlay-text overflow decision, so no
                // clamping here.
                element.X += dxNormalized;
                element.Y += dyNormalized;
                break;
            case DragMode.ElementResize when _draggedElement is { } element:
                var (newWidth, newHeight, centerDeltaX, centerDeltaY) =
                    ComputeElementResize(element.Width, element.Height, dxNormalized, dyNormalized);
                element.Width = newWidth;
                element.Height = newHeight;
                element.X += centerDeltaX;
                element.Y += centerDeltaY;
                break;
        }
    }

    /// <summary>Pure resize-with-floor math, split out from <see cref="OnCanvasPointerMoved"/> so it's
    /// unit-testable without simulating real Avalonia pointer events (code-review finding -- Phase 1
    /// shipped this logic with zero test coverage). Public, not internal -- this project has no
    /// <c>InternalsVisibleTo</c> wired up anywhere (same reasoning/precedent as
    /// <see cref="ScanlineStudio.UI.Controls.WaterfallPalette"/>'s own doc comment).
    /// <para>X/Y is CENTER-anchored, so a bottom-right handle that only grew Width/Height would grow
    /// the box symmetrically in all four directions -- the top-left corner would visibly run away
    /// from the pointer, reading as broken. Growing by the full delta while shifting the center by
    /// HALF the delta keeps the OPPOSITE (top-left) corner pinned, which is what "drag the
    /// bottom-right corner" actually means.</para>
    /// <para>Floored at <see cref="MinNormalizedElementSize"/> (code-review finding, revising an
    /// earlier "no floor needed" call) -- Width/Height going negative isn't just visually odd,
    /// <c>ApplyTemplate</c> treats it as "skip this element," so an unclamped fast drag silently
    /// deleted content from the transmitted image. The returned center delta is HALF the
    /// ACTUALLY-APPLIED size delta (not the raw requested delta), which keeps the pinned-corner
    /// behavior correct once the floor engages, instead of the center continuing to drift past where
    /// the clamped edge actually stopped.</para></summary>
    public static (double Width, double Height, double CenterDeltaX, double CenterDeltaY) ComputeElementResize(
        double currentWidth, double currentHeight, double dxNormalized, double dyNormalized)
    {
        var newWidth = Math.Max(currentWidth + dxNormalized, MinNormalizedElementSize);
        var newHeight = Math.Max(currentHeight + dyNormalized, MinNormalizedElementSize);
        var appliedDx = newWidth - currentWidth;
        var appliedDy = newHeight - currentHeight;
        return (newWidth, newHeight, appliedDx / 2, appliedDy / 2);
    }

    /// <summary>Phase 6 (spec/15-template-designer.md): snap-ON-DROP, not during the drag itself --
    /// see <see cref="TxImageEditorPaneViewModel.SnapToGrid"/>'s own doc comment for why continuous
    /// per-frame snapping is broken against this editor's incremental drag-delta model. Only element
    /// drags/resizes snap (never the crop rect); a bare click that never moved does nothing extra
    /// here beyond what already happens.</summary>
    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ViewModel is { SnapToGrid: true } vm && _dragMode is DragMode.Overlay or DragMode.ElementResize
            && _draggedElement is { Locked: false } element)
        {
            var (x, y, width, height) = SnapElementBoundsToGrid(element.X, element.Y, element.Width, element.Height);
            // Code-review finding: apply as one atomic undo step via the VM, not 4 direct property
            // assignments here -- see ApplySnappedElementBounds' own doc comment for why 4 separate
            // assignments would need two Undos to fully revert a snapped drag.
            vm.ApplySnappedElementBounds(element, x, y, width, height);
        }

        _dragMode = DragMode.None;
        _draggedElement = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Pure grid-snap math (unit-testable without a real drag, same reasoning as
    /// <see cref="ComputeElementResize"/> just above) -- computed in EDGE space
    /// (left/top/right/bottom), NOT by rounding center-X/Y and Width/Height independently
    /// (plan-review blocker on an earlier draft): elements are center-anchored, so rounding
    /// center/size separately puts edges on inconsistent half-grid multiples and two differently-
    /// sized snapped elements never actually align -- the entire point of a snap feature. Each edge
    /// is rounded to the nearest <paramref name="gridSize"/> line independently, then width/height
    /// are DERIVED from the snapped edges (not rounded on their own). If that derivation collapses
    /// width or height below <see cref="MinNormalizedElementSize"/> (a real risk: an element sized
    /// close to one grid cell can snap both edges to the SAME line), the LEFT/TOP edge is kept fixed
    /// and the floor is restored by expanding right/down instead -- deterministic and simple, since a
    /// post-hoc snap (unlike a live resize-from-a-handle) has no "which corner is the user dragging"
    /// context to prefer a different anchor.</summary>
    public static (double X, double Y, double Width, double Height) SnapElementBoundsToGrid(
        double x, double y, double width, double height, double gridSize = 0.05)
    {
        var left = Round(x - (width / 2), gridSize);
        var top = Round(y - (height / 2), gridSize);
        var right = Round(x + (width / 2), gridSize);
        var bottom = Round(y + (height / 2), gridSize);

        var newWidth = right - left;
        if (newWidth < MinNormalizedElementSize)
        {
            newWidth = MinNormalizedElementSize;
            right = left + newWidth;
        }

        var newHeight = bottom - top;
        if (newHeight < MinNormalizedElementSize)
        {
            newHeight = MinNormalizedElementSize;
            bottom = top + newHeight;
        }

        return (left + (newWidth / 2), top + (newHeight / 2), newWidth, newHeight);

        // AwayFromZero, not the default banker's rounding (code-review nit): makes "nearest grid
        // line" an explicit, stated intent rather than an implicit default. No live effect at the
        // production gridSize (0.05) -- binary floating point means an exact .5 tie essentially
        // never occurs there -- but a caller passing an exactly-representable grid (e.g. 0.25) could
        // otherwise hit a real midpoint tie.
        static double Round(double value, double step) => Math.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    /// <summary>Legacy's own real precision mechanism (verified in <c>TxImageEditorPaneViewModel</c>'s
    /// own doc comment against `PicRect.cpp:925-1001`): plain arrow moves, Ctrl+arrow moves faster,
    /// Shift+arrow resizes instead of moving.</summary>
    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // Phase 6 (spec/15-template-designer.md, plan-review blocker): the ONLY deselect affordance
        // anywhere in this editor -- SelectedOverlayElement is set on every element click/Add
        // command and otherwise only cleared on remove/template-load/dispose. Without this, once any
        // element exists, arrow-key crop-rect nudge (the branch below) becomes permanently
        // unreachable -- including as the recovery path for item 3's own hit-testing fix.
        if (e.Key == Key.Escape && vm.SelectedOverlayElement is not null)
        {
            vm.SelectedOverlayElement = null;
            e.Handled = true;
            return;
        }

        // Backlog item (user request, 2026-08-17): keyboard Delete for the selected canvas element
        // -- Delete AND Back (Backspace also removes on macOS keyboards, which have no dedicated
        // forward-delete key without Fn; matches other creative-tool conventions, e.g. Figma treats
        // both the same way). RemoveOverlayElementCommand already no-ops on a null element, but the
        // explicit guard here avoids marking a keypress Handled when nothing is selected, letting it
        // fall through to whatever Avalonia's own default handling would otherwise be.
        if ((e.Key == Key.Delete || e.Key == Key.Back) && vm.SelectedOverlayElement is not null)
        {
            vm.RemoveOverlayElementCommand.Execute(vm.SelectedOverlayElement);
            e.Handled = true;
            return;
        }

        var direction = e.Key switch
        {
            Key.Up => NudgeDirection.Up,
            Key.Down => NudgeDirection.Down,
            Key.Left => NudgeDirection.Left,
            Key.Right => NudgeDirection.Right,
            _ => (NudgeDirection?)null,
        };

        if (direction is not { } dir)
        {
            return;
        }

        // Phase 6: Shift+arrow stays bound to crop-RESIZE unconditionally (plan-review-scoped
        // decision -- this phase does NOT add an element-resize-by-nudge counterpart, only move).
        // Plain arrow nudges the selected element instead of the crop rect when one is selected and
        // unlocked; otherwise falls through to today's crop-rect nudge unchanged.
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && vm.SelectedOverlayElement is { Locked: false })
        {
            vm.NudgeElement(dir, ctrl: e.KeyModifiers.HasFlag(KeyModifiers.Control));
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            vm.NudgeCropResize(dir);
        }
        else
        {
            vm.NudgeCropMove(dir, ctrl: e.KeyModifiers.HasFlag(KeyModifiers.Control));
        }

        e.Handled = true;
    }

    /// <summary>Ready rack number-key recall (spec/15-template-designer.md Phase 5) --
    /// <c>Key.D1</c>..<c>Key.D9</c> AND <c>Key.NumPad1</c>..<c>Key.NumPad9</c> (plan-review-decided
    /// plain <c>KeyDown</c> handler, no keybinding-registration service exists anywhere in this
    /// codebase -- same pattern as <see cref="OnCanvasKeyDown"/>'s own arrow-key nudge). Wired on
    /// the root <see cref="UserControl"/> itself, not a sub-control, so it fires regardless of which
    /// child currently holds focus (routed KeyDown events bubble up the visual tree) -- EXCEPT a
    /// focused <see cref="TextBox"/> (code-review finding): Avalonia's <see cref="TextBox"/> doesn't
    /// mark a plain digit <c>KeyDown</c> as handled (character insertion happens on a separate
    /// <c>TextInput</c> event), so without this guard typing an ordinary digit into this editor's own
    /// name/X/Y/size/font-size/outline-width fields would bubble up and silently replace the entire
    /// overlay layout mid-edit.</summary>
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm || e.Source is TextBox)
        {
            return;
        }

        var slot = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => (int?)null,
        };

        if (slot is not { } slotNumber)
        {
            return;
        }

        vm.ReadyRack.RecallSlotCommand.Execute(slotNumber);
        e.Handled = true;
    }
}
