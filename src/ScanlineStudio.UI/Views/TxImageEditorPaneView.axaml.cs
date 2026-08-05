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
    }

    private DragMode _dragMode = DragMode.None;
    private Point _lastPointerPosition;
    private OverlayElementViewModel? _draggedOverlayElement;

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

    private void OnOverlayElementPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: OverlayElementViewModel element })
        {
            return;
        }

        _draggedOverlayElement = element;
        StartDrag(DragMode.Overlay, e);
    }

    /// <summary>Captures on <see cref="EditorCanvas"/> itself (not the pressed sub-control) so every
    /// subsequent move/release during this drag routes through the canvas's own handlers below,
    /// regardless of which element (crop body, resize handle, an overlay element) was pressed.</summary>
    private void StartDrag(DragMode mode, PointerPressedEventArgs e)
    {
        _dragMode = mode;
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
        _lastPointerPosition = current;

        switch (_dragMode)
        {
            case DragMode.CropMove:
                vm.DragCropMove(dxNormalized, dyNormalized);
                break;
            case DragMode.CropResize:
                vm.DragCropResize(dxNormalized, dyNormalized);
                break;
            case DragMode.Overlay when _draggedOverlayElement is { } element:
                // Free drag -- overflow past the image bounds is allowed (clipped at render time
                // only), matching spec/07-image-pipeline.md's overlay-text overflow decision, so no
                // clamping here.
                element.X += dxNormalized;
                element.Y += dyNormalized;
                break;
        }
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragMode = DragMode.None;
        _draggedOverlayElement = null;
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
