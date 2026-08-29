using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Views;

public partial class ImageViewerWindowView : Window
{
    /// <summary>Non-null only while a left-button drag is in progress -- the same
    /// "record start point + start value, diff on move" pan pattern used throughout this codebase
    /// for pointer-driven dragging (e.g. TxImageEditorPaneView.axaml.cs's own crop-handle drag).
    /// </summary>
    private Point? _panStartPointerPosition;
    private Vector _panStartOffset;

    public ImageViewerWindowView()
    {
        InitializeComponent();

        // Same RequestClose/unsubscribe-on-Closed shape as QsoLinkWindowView.axaml.cs -- guards
        // against CloseRequested firing after this window already closed some other way (e.g. the
        // title-bar X), which would otherwise call Close() on an already-closed window.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ImageViewerWindowViewModel vm)
            {
                vm.CloseRequested += Close;
                Closed += (_, _) => vm.CloseRequested -= Close;
            }
        };

        // Auditor-caught (2026-08-29): a plain XAML PointerWheelChanged attribute is Bubble routing,
        // which runs AFTER ScrollContentPresenter's own wheel-scroll handling inside the ScrollViewer
        // template -- ScrollContentPresenter claims the event (Handled = true) whenever the content
        // overflows the viewport and the offset actually changes, so this handler would silently
        // never fire in exactly the case zooming is most needed (an already-zoomed, overflowing
        // image). Same fix, same reasoning as TxImageEditorPaneView.axaml.cs's own
        // OnEditorWheelChanged wiring: Tunnel runs BEFORE ScrollContentPresenter, letting this
        // handler claim the event first.
        ZoomScrollViewer.AddHandler(PointerWheelChangedEvent, OnZoomScrollViewerPointerWheelChanged, RoutingStrategies.Tunnel);
    }

    private void OnZoomScrollViewerPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not ImageViewerWindowViewModel vm)
        {
            return;
        }

        // Only claims the event for an actual vertical-wheel zoom step -- a horizontal/tilt-wheel
        // gesture (Delta.Y == 0) is left unhandled so it still falls through to the ScrollViewer's
        // own native horizontal scroll, now that Tunnel routing runs ahead of that native handling.
        if (e.Delta.Y > 0)
        {
            // Reuses the same command the toolbar +/- buttons call, rather than duplicating the
            // clamp/fit-mode-exit logic here -- one place decides what "one zoom step" means.
            vm.ZoomInCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Delta.Y < 0)
        {
            vm.ZoomOutCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnZoomImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || !e.GetCurrentPoint(control).Properties.IsLeftButtonPressed || ZoomScrollViewer is null)
        {
            return;
        }

        _panStartPointerPosition = e.GetPosition(ZoomScrollViewer);
        _panStartOffset = ZoomScrollViewer.Offset;
        e.Pointer.Capture(control);
    }

    private void OnZoomImagePointerMoved(object? sender, PointerEventArgs e)
    {
        // Auditor-caught (2026-08-29): re-checks the left button is still down on every move, not
        // just at press -- capture can be lost without a matching Released (a touch scroll-gesture
        // recognizer stealing the pointer, window deactivation mid-drag), and without this a stale
        // _panStartPointerPosition would keep panning on plain hover once the button is no longer
        // held. OnPointerCaptureLost below covers the same gap from the other end.
        if (_panStartPointerPosition is not { } start || ZoomScrollViewer is null || !e.GetCurrentPoint(ZoomScrollViewer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var current = e.GetPosition(ZoomScrollViewer);
        var dragged = current - start;
        // Dragging the image right should reveal content to the LEFT (scroll offset decreases) --
        // subtracting the drag delta from the start offset, not adding it.
        ZoomScrollViewer.Offset = _panStartOffset - new Vector(dragged.X, dragged.Y);
    }

    private void OnZoomImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Auditor-caught (nit, 2026-08-29): only a LEFT-button release ends the pan -- a right-click
        // (e.g. opening a context menu) mid-drag no longer aborts it.
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        _panStartPointerPosition = null;
        e.Pointer.Capture(null);
    }

    private void OnZoomImagePointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _panStartPointerPosition = null;
}
