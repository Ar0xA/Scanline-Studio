using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    }

    private TxImageEditorPaneViewModel? ViewModel => DataContext as TxImageEditorPaneViewModel;

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
    private void OnOverlayElementPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ITemplateElementViewModel element } || ViewModel is not { } vm)
        {
            return;
        }

        vm.SelectedOverlayElement = element;

        if (element.Locked)
        {
            return;
        }

        _draggedElement = element;
        StartDrag(DragMode.Overlay, e);
    }

    /// <summary>Single bottom-right resize handle per element (Phase 1 plan-review finding: mirrors
    /// the crop rect's own existing single-corner-handle convention rather than a 4-corner/8-handle
    /// system). Same <see cref="ITemplateElementViewModel.Locked"/> real-gate reasoning as
    /// <see cref="OnOverlayElementPointerPressed"/> above.</summary>
    private void OnElementResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ITemplateElementViewModel element } || element.Locked)
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
        if (_dragMode == DragMode.None || ViewModel is not { } vm || vm.WorkingCopyWidth <= 0 || vm.WorkingCopyHeight <= 0)
        {
            return;
        }

        var current = e.GetPosition(EditorCanvas);
        var dxNormalized = (current.X - _lastPointerPosition.X) / vm.WorkingCopyWidth;
        var dyNormalized = (current.Y - _lastPointerPosition.Y) / vm.WorkingCopyHeight;

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

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragMode = DragMode.None;
        _draggedElement = null;
        e.Pointer.Capture(null);
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
}
