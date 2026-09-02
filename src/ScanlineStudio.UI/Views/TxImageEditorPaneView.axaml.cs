using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScanlineStudio.Abstractions.Imaging;
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

        /// <summary>TX workflow modernization plan, Phase 3a -- draw-to-place. Active from the
        /// moment an armed placement tool (<see cref="_pendingPlacementKind"/>) receives a canvas
        /// press until release, at which point the actual element is created (see
        /// <see cref="OnCanvasPointerReleased"/>'s own Placing branch) -- unlike every other
        /// <see cref="DragMode"/>, nothing on the canvas exists yet while this is active.</summary>
        Placing,

        /// <summary>TX editor gap-items plan, line element (2026-09-01) -- dragging ONE of a
        /// selected line's 2 endpoint handles. Deliberately a NEW mode, not folded into
        /// <see cref="ElementResize"/> -- that mode's own math (<see cref="ComputeElementResize"/>)
        /// assumes a box's corner/edge semantics, not an arbitrary endpoint, and its own
        /// mid-drag write (<see cref="OnCanvasPointerMoved"/>'s <c>ElementResize</c> case) writes
        /// Width/Height directly, which for a line are DERIVED -- routing an endpoint drag through
        /// that path would corrupt the OTHER endpoint via the degenerate-extent setter rule instead
        /// of leaving it untouched. Excluded from BOTH the undo-push check right below (endpoint
        /// drags push via the endpoint's own <c>On*Changing</c>-coalesced hook instead, same as
        /// <see cref="Overlay"/>/<see cref="ElementResize"/>) and <see cref="OnCanvasPointerReleased"/>'s
        /// own snap-on-drop condition (that method's own <see cref="TxImageEditorPaneViewModel.ApplySnappedElementBounds"/>
        /// call snaps a WHOLE line's two endpoints together -- an endpoint drag needs its own
        /// single-endpoint snap instead, see <see cref="TxImageEditorPaneViewModel.ApplySnappedLineEndpoint"/>).</summary>
        LineEndpoint,

        /// <summary>TX editor gap-items plan, item 3 (perspective transform, 2026-09-02) -- dragging
        /// ONE of a perspective-enabled image/box element's 4 corner handles. Same shape as
        /// <see cref="LineEndpoint"/> above (a new mode, not folded into <see cref="ElementResize"/>
        /// -- that mode's own math assumes a box's corner/edge semantics against DERIVED Width/
        /// Height, not an independent corner field), and same exclusions for the same reasons: left
        /// OUT of the undo-push check right below (a corner drag pushes via the corner's own
        /// <c>On*Changing</c>-coalesced hook instead, same as <see cref="Overlay"/>/
        /// <see cref="ElementResize"/>/<see cref="LineEndpoint"/>) and out of
        /// <see cref="OnCanvasPointerReleased"/>'s own snap-on-drop condition (corner snap-on-drop is
        /// a deliberate v1 scope cut -- see <see cref="TxImageEditorPaneViewModel.ApplySnappedElementBounds"/>'s
        /// own perspective-enabled early-return).</summary>
        PerspectiveCorner,
    }

    /// <summary>Which toolbar "Add" button armed the placement tool currently pending on the canvas
    /// -- see <see cref="_pendingPlacementKind"/>'s own doc comment.</summary>
    private enum PlacementKind
    {
        Text,
        Box,

        /// <summary>TX editor gap-items plan, line element (2026-09-01).</summary>
        Line,
    }

    /// <summary>Which side(s) of the element the pressed handle drags -- public (not the private
    /// <see cref="DragMode"/> above) because it's a parameter of the public, unit-tested
    /// <see cref="ComputeElementResize"/>. Corner handles free both axes; edge handles free only one
    /// (the perpendicular axis is unaffected, matching every mainstream image editor's own resize-
    /// handle convention). Backlog item (auditor usability review, 2026-08-17): the earlier single
    /// bottom-right-only handle was a deliberate legacy-precedent choice at the time, but the "UI/
    /// editing work should be improved on, not replicated" project rule (CLAUDE.md §2) applies here --
    /// this is new interaction-model functionality, not a port.</summary>
    public enum ResizeHandle
    {
        TopLeft,
        Top,
        TopRight,
        Right,
        BottomRight,
        Bottom,
        BottomLeft,
        Left,
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
    private ResizeHandle _resizeHandle = ResizeHandle.BottomRight;

    /// <summary>Which of a dragged line's 2 endpoints <see cref="DragMode.LineEndpoint"/> is
    /// currently moving -- true = X1/Y1, false = X2/Y2. Set from the pressed handle's own AXAML
    /// <c>Tag</c> (<see cref="OnLineEndpointHandlePointerPressed"/>), same "read Tag, don't infer
    /// from geometry" convention <see cref="_resizeHandle"/> already uses.</summary>
    private bool _draggedIsFirstEndpoint;

    /// <summary>Which of a dragged perspective-enabled element's 4 corners
    /// <see cref="DragMode.PerspectiveCorner"/> is currently moving (0-3, matching
    /// <see cref="PerspectiveCorners"/>' own TopLeft/TopRight/BottomRight/BottomLeft winding). Set
    /// from the pressed handle's own AXAML <c>Tag</c> (<see cref="OnPerspectiveCornerHandlePointerPressed"/>),
    /// same "read Tag, don't infer from geometry" convention <see cref="_resizeHandle"/>/
    /// <see cref="_draggedIsFirstEndpoint"/> already use.</summary>
    private int _draggedCornerIndex;

    /// <summary>See <see cref="OnOpenElementQuickStyleFlyout"/>'s own doc comment for why this
    /// exists -- <see cref="ContextMenu.PlacementTarget"/> is never populated by Avalonia itself, so
    /// this records the most recently pressed element's own <see cref="Border"/> instead, captured
    /// in <see cref="OnOverlayElementPointerPressed"/>. The image element's own ContextMenu has no
    /// Quick Style/Fill &amp; Border flyout, so a press on it capturing this too is harmless -- the
    /// value is simply never read for that element type.</summary>
    private Border? _lastContextMenuAnchor;

    // Undo/redo sub-piece: a gesture pushes ONE undo step, on the first real move, not on press
    // (a bare click that never moves shouldn't push a no-op step) -- reset in StartDrag, consumed
    // in OnCanvasPointerMoved.
    private bool _pushedUndoThisGesture;

    /// <summary>TX workflow modernization plan, Phase 3a -- non-null while a placement tool is
    /// armed (an "Add Text"/"Add Box" toolbar button was pressed) but before the canvas press that
    /// actually starts <see cref="DragMode.Placing"/>. A crosshair cursor is shown on
    /// <see cref="EditorCanvas"/> for the whole armed duration, including this pre-press window --
    /// set/cleared everywhere this field is, so the two can't drift apart.</summary>
    private PlacementKind? _pendingPlacementKind;

    /// <summary>Shift held at arm time (TX workflow modernization plan, Phase 3a) -- the tool stays
    /// armed after a placement completes instead of disarming, for repeated placements. Captured
    /// once, at arm time, not re-read from live modifier state later -- so releasing Shift mid-drag
    /// doesn't change the outcome of the placement already in progress.</summary>
    private bool _pendingPlacementSticky;

    private Point _placementAnchorPoint;

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

        // TX workflow modernization plan, Phase 3a -- Tunnel, same reasoning as the wheel handler
        // above: crop-move/element-select/resize-handle are BUBBLING handlers on child controls
        // (OnCropBodyPointerPressed/OnOverlayElementPointerPressed/OnElementResizeHandlePointerPressed),
        // so they'd see a press before a Canvas-attached bubbling handler ever could. Tunneling here
        // runs first and marks the event Handled while a placement is armed, so an armed click over
        // an existing element starts a PLACEMENT, not a drag of that element.
        EditorCanvas.AddHandler(PointerPressedEvent, OnArmedPlacementPressed, RoutingStrategies.Tunnel);
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

    /// <summary>Macros help plan session, real-UI-smoke-test finding (2026-09-01): the ORIGINAL
    /// version of this handler read <c>menuItem.FindLogicalAncestorOfType&lt;ContextMenu&gt;().PlacementTarget</c>
    /// on the (incorrect) assumption that Avalonia sets it automatically on right-click. Confirmed
    /// FALSE by reading Avalonia 11.3.12's actual <c>ContextMenu.cs</c> source directly: a real
    /// right-click is handled by <c>ControlContextRequested</c>, which calls the PRIVATE 3-arg
    /// <c>Open(Control, Control, PlacementMode)</c> overload -- that overload only ever sets the
    /// underlying <c>Popup</c>'s OWN <c>PlacementTarget</c> (<c>_popup.PlacementTarget = placementTarget</c>),
    /// never <c>ContextMenu.PlacementTarget</c> itself (the public property this handler used to
    /// read). <c>ContextMenu.PlacementTarget</c> is NEVER populated by opening the menu, on any
    /// platform -- this is plain, platform-agnostic C# logic, not a headless-only artifact,
    /// independently reproduced via a real simulated right-click in
    /// <c>TxImageEditorQuickStyleFlyoutRealClickTests.RealRightClick_DoesNotAutomaticallyPopulateContextMenuPlacementTarget</c>.
    /// The practical consequence: this handler's old guard ALWAYS failed on a real click, so the
    /// Quick Style/Fill &amp; Border flyouts were silently non-functional (permanently disabled, not
    /// occasionally) since they shipped -- PROJECT_BRIEF.md's own "confirm these actually open on a
    /// real right-click" item, never actually confirmed until now.
    /// <para>Fixed by reading <see cref="_lastContextMenuAnchor"/>, captured in
    /// <see cref="OnOverlayElementPointerPressed"/> on PRESS (see that field's own doc comment).
    /// <b>Auditor code-review finding, round 1:</b> an earlier version of this fix captured the
    /// anchor via a SEPARATE <c>ContextRequested</c> handler subscribed on the same Border --
    /// correct in isolation, but a real headless test proved its correctness in PRODUCTION depended
    /// on an unverified assumption about whether XAML's compiler subscribes that handler before or
    /// after <c>Border.ContextMenu</c> is assigned (which is what wires Avalonia's own internal
    /// <c>ControlContextRequested</c> handler on the SAME event). Reversing that order in a test
    /// broke capture entirely -- confirming the risk was real, not hypothetical. Capturing on
    /// <c>PointerPressed</c> instead sidesteps the question altogether: press always precedes the
    /// release that opens a context menu, so there is no competing-subscription-order to reason
    /// about.</para>
    /// <para>Deferred via <see cref="Dispatcher.UIThread"/>.Post at Background priority (unchanged
    /// from the original design) -- MenuItem's own Click handler runs BEFORE the owning Popup
    /// finishes closing (confirmed against Avalonia 11.3.12's DefaultMenuInteractionHandler), so
    /// calling ShowAttachedFlyout synchronously here races the ContextMenu's own close/focus-restore
    /// and can dismiss the flyout the instant it opens.</para></summary>
    private void OnOpenElementQuickStyleFlyout(object? sender, RoutedEventArgs e)
    {
        if (_lastContextMenuAnchor is not { } target)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => FlyoutBase.ShowAttachedFlyout(target), DispatcherPriority.Background);
    }

    /// <summary>TX workflow modernization plan, Phase 3a -- "Add Text" toolbar button. Wired as
    /// <c>PointerPressed</c> (not <c>Click</c>/<c>Command</c>) specifically so <see cref="Shift"/>
    /// (sticky placement) is readable from <see cref="PointerPressedEventArgs.KeyModifiers"/> at arm
    /// time -- a plain <c>Click</c> event carries no modifier state. Re-pressing the SAME button
    /// while already armed with that kind disarms instead of re-arming (toggle), matching the
    /// design's "clicking the button again cancels" requirement.</summary>
    private void OnArmTextPlacementPressed(object? sender, PointerPressedEventArgs e) => TogglePlacementArm(PlacementKind.Text, e.KeyModifiers.HasFlag(KeyModifiers.Shift));

    /// <summary>Same reasoning as <see cref="OnArmTextPlacementPressed"/> right above, for "Add Box".</summary>
    private void OnArmBoxPlacementPressed(object? sender, PointerPressedEventArgs e) => TogglePlacementArm(PlacementKind.Box, e.KeyModifiers.HasFlag(KeyModifiers.Shift));

    /// <summary>Same reasoning as <see cref="OnArmTextPlacementPressed"/> right above, for "Add Line".</summary>
    private void OnArmLinePlacementPressed(object? sender, PointerPressedEventArgs e) => TogglePlacementArm(PlacementKind.Line, e.KeyModifiers.HasFlag(KeyModifiers.Shift));

    private void TogglePlacementArm(PlacementKind kind, bool sticky)
    {
        if (_pendingPlacementKind == kind)
        {
            DisarmPlacement();
            return;
        }

        _pendingPlacementKind = kind;
        _pendingPlacementSticky = sticky;
        EditorCanvas.Cursor = new Cursor(StandardCursorType.Cross);
    }

    private void DisarmPlacement()
    {
        _pendingPlacementKind = null;
        _pendingPlacementSticky = false;
        EditorCanvas.Cursor = Cursor.Default;
        // Right-click-mid-drag disarm (OnArmedPlacementPressed) leaves _dragMode stuck at Placing
        // until the eventual pointer release, so the rubber band has to clear HERE, immediately --
        // waiting for OnCanvasPointerReleased's own unconditional clear would leave it visibly
        // following the cursor (frozen, since OnCanvasPointerMoved's own Placing branch is gated on
        // _pendingPlacementKind, not _dragMode) for however long the button stays held after this.
        if (ViewModel is { } vm)
        {
            vm.PlacementPreviewRect = null;
            vm.PlacementPreviewLine = null;
        }
    }

    /// <summary>TX workflow modernization plan, Phase 3a -- the Tunnel handler registered in the
    /// constructor. No-ops (returns without marking <paramref name="e"/> handled) when nothing is
    /// armed, letting every existing bubbling handler (crop/element/resize-handle) run exactly as
    /// before -- this is the ONLY new code that runs on every canvas press regardless of armed
    /// state, so it has to stay cheap and clearly gated. Right-button while armed disarms rather
    /// than starting a placement, and deliberately does NOT mark the event handled -- the existing
    /// per-element/canvas <c>ContextMenu</c>s must still see the press and open normally.</summary>
    private void OnArmedPlacementPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_pendingPlacementKind is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(EditorCanvas);
        if (point.Properties.IsRightButtonPressed)
        {
            DisarmPlacement();
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _placementAnchorPoint = point.Position;
        _dragMode = DragMode.Placing;
        _lastPointerPosition = point.Position;
        e.Pointer.Capture(EditorCanvas);
        e.Handled = true;
    }

    /// <summary>Pure drag-to-rect math for <see cref="DragMode.Placing"/> (TX workflow modernization
    /// plan, Phase 3a) -- unit-testable without a real drag, same reasoning/precedent as
    /// <see cref="ComputeElementResize"/>/<see cref="SnapElementBoundsToGrid"/>. Anchor/current are
    /// un-ordered (the drag can go in any direction from the press point) -- normalizes both by the
    /// canvas's own display dimensions into the SAME [0,1] space
    /// <see cref="ITemplateElementViewModel.X"/>/<c>Y</c>/<c>Width</c>/<c>Height</c> already use
    /// (confirmed: dividing a display-pixel delta by <c>CanvasDisplayWidth</c>/<c>Height</c> cancels
    /// <see cref="TxImageEditorPaneViewModel.ZoomFactor"/> out, same as every other drag delta in
    /// this file), then derives a CENTER-anchored rect floored at <paramref name="minSize"/> per
    /// axis -- same floor reasoning as <see cref="ComputeElementResize"/>'s own doc comment (an
    /// unclamped near-zero size is invisible and <c>ApplyTemplate</c> skips it entirely).</summary>
    public static (double CenterX, double CenterY, double Width, double Height) ComputeRectFromDrag(
        Point anchor, Point current, double canvasDisplayWidth, double canvasDisplayHeight, double minSize = MinNormalizedElementSize)
    {
        var x1 = anchor.X / canvasDisplayWidth;
        var y1 = anchor.Y / canvasDisplayHeight;
        var x2 = current.X / canvasDisplayWidth;
        var y2 = current.Y / canvasDisplayHeight;

        var left = Math.Min(x1, x2);
        var top = Math.Min(y1, y2);
        var width = Math.Max(Math.Abs(x2 - x1), minSize);
        var height = Math.Max(Math.Abs(y2 - y1), minSize);

        return (left + (width / 2), top + (height / 2), width, height);
    }

    /// <summary>Line counterpart to <see cref="ComputeRectFromDrag"/> immediately above (TX editor
    /// gap-items plan, line element) -- deliberately NOT built on that method (which normalizes
    /// anchor/current into a min/max box, losing which point was the press and which was the
    /// release). A line's own endpoints ARE the drag's anchor and release points directly, in that
    /// order -- reversing the drag direction must draw the line in the reversed direction too, not
    /// silently normalize to the same box regardless of drag direction the way a rect placement
    /// does.</summary>
    public static (double X1, double Y1, double X2, double Y2) ComputeLineFromDrag(
        Point anchor, Point current, double canvasDisplayWidth, double canvasDisplayHeight)
        => (anchor.X / canvasDisplayWidth, anchor.Y / canvasDisplayHeight, current.X / canvasDisplayWidth, current.Y / canvasDisplayHeight);

    /// <summary>Shift-drag angle-snap (TX editor gap-items plan, line element) -- snaps
    /// <paramref name="cursor"/>'s direction FROM <paramref name="fixedPoint"/> to the nearest
    /// 45-degree increment (0/45/90/135/...), preserving the actual dragged DISTANCE exactly (only
    /// the angle is quantized) -- used by both an endpoint drag and the initial line placement drag,
    /// so a Shift-held line is always exactly horizontal/vertical/diagonal regardless of which end
    /// the operator is dragging. A near-zero distance (cursor at or extremely close to
    /// <paramref name="fixedPoint"/>) has no defined angle to snap to -- returns
    /// <paramref name="cursor"/> unchanged rather than dividing by ~zero.</summary>
    public static Point SnapPointToAngle(Point fixedPoint, Point cursor)
    {
        var dx = cursor.X - fixedPoint.X;
        var dy = cursor.Y - fixedPoint.Y;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        if (distance < 1e-6)
        {
            return cursor;
        }

        const double eighthTurn = Math.PI / 4;
        var snappedAngle = Math.Round(Math.Atan2(dy, dx) / eighthTurn) * eighthTurn;
        return new Point(fixedPoint.X + (distance * Math.Cos(snappedAngle)), fixedPoint.Y + (distance * Math.Sin(snappedAngle)));
    }

    /// <summary>Below this real-pixel distance (TX workflow modernization plan, Phase 3a), a
    /// placement press+release is treated as a plain CLICK (default-size element centered on the
    /// point) rather than a DRAG (sized to the dragged rect) -- Avalonia can report a few pixels of
    /// pointer jitter between press and release even for what a real user experiences as a single
    /// click, so a literal zero-distance check would misclassify most clicks as a drag.</summary>
    private const double PlacementClickThresholdPixels = 4;

    /// <summary>TX workflow modernization plan, Phase 3c -- how close (normalized, same [0,1] space
    /// as element X/Y/Width/Height) a dragged element's own edge/center has to land to another
    /// element's edge/center (or the crop's own center) to snap to it at drop. 0.01 = 1% of the
    /// working copy's width/height -- small enough that snapping only engages for a genuinely close
    /// drop, not a coincidentally-nearby one.</summary>
    private const double AlignmentSnapThresholdNormalized = 0.01;

    /// <summary>Pure alignment-guide-snap math (TX workflow modernization plan, Phase 3c) --
    /// unit-testable without a real drag, same reasoning/precedent as
    /// <see cref="ComputeElementResize"/>/<see cref="SnapElementBoundsToGrid"/>. For EACH axis
    /// independently: compares the dragged element's own left/center/right (X) or top/center/bottom
    /// (Y) against every candidate target (every OTHER element's corresponding left/center/right or
    /// top/center/bottom, plus <paramref name="cropCenter"/> for a center-only target) and returns
    /// the CENTER-anchored position that puts the single CLOSEST matching pair exactly on top of
    /// each other, or <see langword="null"/> for an axis with no match inside
    /// <paramref name="thresholdNormalized"/>. Independent per axis -- a drop can snap X to one
    /// element and Y to a completely different one, matching how alignment guides work in every
    /// mainstream design tool.</summary>
    public static (double? X, double? Y) ComputeAlignmentSnap(
        (double X, double Y, double Width, double Height) dragged,
        IReadOnlyList<(double X, double Y, double Width, double Height)> others,
        (double X, double Y) cropCenter,
        double thresholdNormalized = AlignmentSnapThresholdNormalized)
    {
        var (xTargets, yTargets, xPoints, yPoints) = BuildAlignmentCandidates(dragged, others, cropCenter);
        return (FindClosestSnap(xPoints, xTargets, thresholdNormalized).Center, FindClosestSnap(yPoints, yTargets, thresholdNormalized).Center);
    }

    /// <summary>Live guide-LINE counterpart to <see cref="ComputeAlignmentSnap"/> above (TX workflow
    /// modernization plan, follow-up visual-polish pass) -- same candidate set, same threshold, but
    /// returns the matched TARGET coordinate itself (where the shared edge/center line actually sits),
    /// not <see cref="ComputeAlignmentSnap"/>'s snapped dragged-element CENTER. Those two differ by
    /// <c>CenterOffset</c> for any edge match (only a center-to-center match has the two coincide) --
    /// binding <see cref="ComputeAlignmentSnap"/>'s own return value straight into a rendered guide
    /// line would draw it half the dragged element's width/height away from the edge it claims to
    /// align with, for every edge-to-edge case (plan-review blocker, caught before this was built:
    /// pin-checked against <see cref="ComputeAlignmentSnap"/>'s own existing left-edge test, which
    /// already proves the two values differ). Never applied to the element's actual position --
    /// display-only, the real drop-time snap this mirrors stays exactly as <see cref="ComputeAlignmentSnap"/>
    /// already implements it, untouched by this method's existence.</summary>
    public static (double? X, double? Y) ComputeAlignmentGuideLines(
        (double X, double Y, double Width, double Height) dragged,
        IReadOnlyList<(double X, double Y, double Width, double Height)> others,
        (double X, double Y) cropCenter,
        double thresholdNormalized = AlignmentSnapThresholdNormalized)
    {
        var (xTargets, yTargets, xPoints, yPoints) = BuildAlignmentCandidates(dragged, others, cropCenter);
        return (FindClosestSnap(xPoints, xTargets, thresholdNormalized).Target, FindClosestSnap(yPoints, yTargets, thresholdNormalized).Target);
    }

    /// <summary>Shared candidate-list construction for <see cref="ComputeAlignmentSnap"/>/
    /// <see cref="ComputeAlignmentGuideLines"/> -- extracted so the two can never silently diverge on
    /// what counts as a match (plan-review finding: two independent copies would let one gain, say, a
    /// "skip locked elements" filter without the other, so the live guide line would point at a
    /// target the eventual drop wouldn't actually snap to).</summary>
    private static (List<double> XTargets, List<double> YTargets, (double Point, double CenterOffset)[] XPoints, (double Point, double CenterOffset)[] YPoints)
        BuildAlignmentCandidates(
            (double X, double Y, double Width, double Height) dragged,
            IReadOnlyList<(double X, double Y, double Width, double Height)> others,
            (double X, double Y) cropCenter)
    {
        var xTargets = new List<double> { cropCenter.X };
        var yTargets = new List<double> { cropCenter.Y };
        foreach (var other in others)
        {
            xTargets.Add(other.X - (other.Width / 2));
            xTargets.Add(other.X);
            xTargets.Add(other.X + (other.Width / 2));
            yTargets.Add(other.Y - (other.Height / 2));
            yTargets.Add(other.Y);
            yTargets.Add(other.Y + (other.Height / 2));
        }

        var xPoints = new (double Point, double CenterOffset)[]
        {
            (dragged.X - (dragged.Width / 2), dragged.Width / 2),
            (dragged.X, 0),
            (dragged.X + (dragged.Width / 2), -(dragged.Width / 2)),
        };
        var yPoints = new (double Point, double CenterOffset)[]
        {
            (dragged.Y - (dragged.Height / 2), dragged.Height / 2),
            (dragged.Y, 0),
            (dragged.Y + (dragged.Height / 2), -(dragged.Height / 2)),
        };

        return (xTargets, yTargets, xPoints, yPoints);
    }

    /// <summary>Returns BOTH the snapped dragged-element center (<see cref="ComputeAlignmentSnap"/>'s
    /// own contract, unchanged) and the raw matched target coordinate (<see cref="ComputeAlignmentGuideLines"/>'s
    /// own contract) from a single closest-match search -- one pass, not two, and the two public
    /// callers can never disagree on WHICH match won even though they want different numbers out of
    /// it.</summary>
    private static (double? Center, double? Target) FindClosestSnap(
        IReadOnlyList<(double Point, double CenterOffset)> draggedPoints, IReadOnlyList<double> targets, double threshold)
    {
        double? bestTarget = null;
        var bestCenterOffset = 0.0;
        var bestDistance = threshold;
        foreach (var (point, centerOffset) in draggedPoints)
        {
            foreach (var target in targets)
            {
                var distance = Math.Abs(point - target);
                if (distance <= bestDistance)
                {
                    bestDistance = distance;
                    bestTarget = target;
                    bestCenterOffset = centerOffset;
                }
            }
        }

        return bestTarget is { } t ? (t + bestCenterOffset, t) : (null, null);
    }

    /// <summary>Shared "others in normalized element space, plus the crop's own center" candidate
    /// input for <see cref="ComputeAlignmentSnap"/>/<see cref="ComputeAlignmentGuideLines"/> -- used
    /// identically at the live per-frame preview call site (<see cref="OnCanvasPointerMoved"/>) and
    /// the drop-time snap-apply call site (<see cref="OnCanvasPointerReleased"/>) so the two can never
    /// silently diverge on what counts as a candidate (same reasoning as
    /// <see cref="BuildAlignmentCandidates"/>'s own doc comment, one level up the call chain).</summary>
    private static (List<(double X, double Y, double Width, double Height)> Others, (double X, double Y) CropCenter) BuildAlignmentInputs(
        TxImageEditorPaneViewModel vm, ITemplateElementViewModel element)
    {
        var others = vm.OverlayElements
            .Where(other => !ReferenceEquals(other, element))
            .Select(other => (other.X, other.Y, other.Width, other.Height))
            .ToList();
        var cropCenter = (vm.CropRect.X + (vm.CropRect.Width / 2), vm.CropRect.Y + (vm.CropRect.Height / 2));
        return (others, cropCenter);
    }

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

    /// <summary>TX workflow modernization plan, Phase 5 blocker fix -- left-button gate added (was
    /// missing here, unlike <see cref="OnOverlayElementPointerPressed"/>/
    /// <see cref="OnElementResizeHandlePointerPressed"/>, which both already have it). Without it, a
    /// right-click anywhere on the crop rect -- most of the visible canvas -- started a crop-move
    /// drag and captured the pointer on <see cref="EditorCanvas"/> before any context menu could
    /// show, silently eating the click a new canvas-level <c>ContextMenu</c> needs to reach.</summary>
    private void OnCropBodyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        StartDrag(DragMode.CropMove, e);
    }

    /// <summary>Same left-button gate and reasoning as <see cref="OnCropBodyPointerPressed"/> right
    /// above.</summary>
    private void OnCropHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        StartDrag(DragMode.CropResize, e);
    }

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

        // Macros help plan session, real-UI-smoke-test finding (2026-09-01): captures the Quick
        // Style/Fill & Border flyout anchor here, on PRESS, unconditionally (any button) -- see
        // OnOpenElementQuickStyleFlyout's own doc comment for why. An earlier version of this fix
        // captured via a SEPARATE ContextRequested handler subscribed on the same Border -- that
        // shape turned out to empirically depend on whether ContextMenu was assigned before or
        // after AddHandler ran (confirmed by a real headless test: reversing that order broke
        // capture entirely). PointerPressed always fires before the subsequent pointer release
        // that opens a context menu, for any button, so capturing here has no such ordering risk.
        _lastContextMenuAnchor = sender as Border;

        vm.SelectedOverlayElement = element;

        // Backlog item (auditor usability review, 2026-08-17): "No inline canvas text editing (no
        // double-click/F2)." A double-click on a selected, unlocked TEXT element enters edit mode
        // (see OverlayElementViewModel.IsEditingText's own doc comment) instead of starting a drag --
        // editing and dragging the same gesture would be confusing, and legacy creative-tool
        // convention (Figma/PowerPoint/etc.) reserves double-click for "start editing" specifically.
        if (e.ClickCount == 2 && element is OverlayElementViewModel text && !element.Locked)
        {
            text.IsEditingText = true;
            FocusInlineTextEditor(sender as Visual);
            e.Handled = true;
            return;
        }

        if (element.Locked || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // TX workflow modernization plan, Phase 3b -- Ctrl-drag-to-duplicate. Clones the pressed
        // element (its own +0.02 seed offset is immediately overridden by the drag below) and drags
        // the CLONE, leaving the original in place -- same muscle-memory gesture Illustrator/
        // Inkscape/Sketch use, deliberately Ctrl rather than Alt (see DuplicateElementForDrag's own
        // doc comment for why).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            element = vm.DuplicateElementForDrag(element);
        }

        _draggedElement = element;
        StartDrag(DragMode.Overlay, e);
    }

    /// <summary>Best-effort focus grab for the inline edit TextBox that
    /// <c>TxImageEditorPaneView.axaml</c>'s text DataTemplate reveals when
    /// <see cref="OverlayElementViewModel.IsEditingText"/> flips true (double-click above, F2 in
    /// <see cref="OnRootKeyDown"/>). Deferred to <see cref="DispatcherPriority.Loaded"/> -- the
    /// TextBox's own <c>IsVisible</c> binding hasn't necessarily applied/measured yet in the same
    /// synchronous callback that just set <c>IsEditingText</c> (same "binding write now, layout
    /// later" ordering this file's own <see cref="OnEditorWheelChanged"/> already has to account for
    /// via <see cref="ScrollViewer.UpdateLayout"/>). A missed focus grab (control not yet realized,
    /// or already dismissed by the time this runs) degrades to "click into the now-visible TextBox
    /// once" -- a minor UX wrinkle, not a broken feature.</summary>
    private static void FocusInlineTextEditor(Visual? elementRoot)
    {
        if (elementRoot is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (elementRoot.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } textBox)
                {
                    textBox.Focus();
                    textBox.SelectAll();
                }
            },
            DispatcherPriority.Loaded);
    }

    /// <summary>8 resize handles per element -- corners + edge midpoints (auditor usability review
    /// follow-up, 2026-08-18: the earlier single bottom-right-only handle was a legacy-precedent
    /// choice, not a DSP/protocol constraint -- see <see cref="ResizeHandle"/>'s own doc comment).
    /// Which handle was pressed is read from the pressed <see cref="Control"/>'s own AXAML-set
    /// <c>Tag</c> (one of <see cref="ResizeHandle"/>'s names) -- same
    /// <see cref="ITemplateElementViewModel.Locked"/> real-gate reasoning as
    /// <see cref="OnOverlayElementPointerPressed"/> above, same left-button gate too (task #24
    /// plan-review finding: a right-press here would otherwise start a resize drag instead of letting
    /// the parent element's own context menu handle the release).</summary>
    private void OnElementResizeHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ITemplateElementViewModel element } control || element.Locked
            || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed
            || control.Tag is not string tagValue || !Enum.TryParse<ResizeHandle>(tagValue, out var handle))
        {
            return;
        }

        _draggedElement = element;
        _resizeHandle = handle;
        StartDrag(DragMode.ElementResize, e);
    }

    /// <summary>TX editor gap-items plan, line element -- a line's 2 endpoint handles, NOT the 8
    /// box-resize handles above (see <see cref="DragMode.LineEndpoint"/>'s own doc comment for why
    /// this needs a genuinely separate handler/drag mode, not a reuse of
    /// <see cref="OnElementResizeHandlePointerPressed"/>'s own box-shaped math). Same
    /// Locked/left-button gates as that handler, same "read Tag" convention -- <c>Tag</c> is
    /// <c>"Endpoint1"</c> or <c>"Endpoint2"</c> (set in the line's own DataTemplate), parsed here
    /// into <see cref="_draggedIsFirstEndpoint"/> rather than a full enum (only 2 values, an enum
    /// would be one member for one caller).</summary>
    private void OnLineEndpointHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: LineElementViewModel line } control || line.Locked
            || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed
            || control.Tag is not string tagValue || tagValue is not ("Endpoint1" or "Endpoint2"))
        {
            return;
        }

        _draggedElement = line;
        _draggedIsFirstEndpoint = tagValue == "Endpoint1";
        StartDrag(DragMode.LineEndpoint, e);
    }

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- a perspective-enabled
    /// image/box element's 4 corner handles, NOT the 8 axis-aligned resize handles above (see
    /// <see cref="DragMode.PerspectiveCorner"/>'s own doc comment for why this needs a genuinely
    /// separate handler/drag mode). Same Locked/left-button gates as
    /// <see cref="OnLineEndpointHandlePointerPressed"/>, same "read Tag" convention -- <c>Tag</c> is
    /// <c>"Corner0"</c>..<c>"Corner3"</c> (set in the image/box DataTemplate's own corner-handle
    /// controls), parsed here into <see cref="_draggedCornerIndex"/>.</summary>
    private void OnPerspectiveCornerHandlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: ITemplateElementViewModel element } control || element.Locked
            || !e.GetCurrentPoint(sender as Visual).Properties.IsLeftButtonPressed
            || control.Tag is not string tagValue || tagValue is not ("Corner0" or "Corner1" or "Corner2" or "Corner3"))
        {
            return;
        }

        _draggedElement = element;
        _draggedCornerIndex = tagValue[^1] - '0';
        StartDrag(DragMode.PerspectiveCorner, e);
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
        // Backlog item (auditor usability review, 2026-08-17): _pushedUndoThisGesture is now tracked
        // for EVERY drag mode (previously only CropMove/CropResize) so Escape-cancel-drag
        // (OnRootKeyDown) has a reliable per-gesture "did anything actually change" signal to decide
        // whether a single Undo call is needed. The explicit PushUndoSnapshotForDragGesture() call
        // stays CropMove/CropResize-only -- Overlay/ElementResize still push through each element's
        // own On*Changing-coalesced PushUndoSnapshotForGeometryChange hook (unchanged), this only
        // adds bookkeeping, not a second push.
        // Placing (TX workflow modernization plan, Phase 3a) excluded here too -- nothing exists on
        // the canvas yet during a placement drag, so there is nothing to push an undo step FOR; the
        // element is created (and its own single undo step pushed) once, at release, by
        // AddOverlayElementAt/AddBoxElementAt. LineEndpoint (TX editor gap-items plan, line element)
        // excluded for the SAME "pushes via the element's own On*Changing-coalesced hook instead"
        // reason as Overlay/ElementResize -- round-3 plan-review's own finding: an earlier draft
        // omitted this and every endpoint drag double-pushed an undo step (this method's own
        // unconditional PushUndoSnapshotForDragGesture() call here, PLUS the endpoint setter's own
        // coalesced push), so the first Ctrl+Z after any endpoint drag silently did nothing -- the
        // exact bug class already fixed once at AlignSelectedElementToCrop.
        if (!_pushedUndoThisGesture && (dxNormalized != 0 || dyNormalized != 0))
        {
            // PerspectiveCorner excluded here for the SAME "pushes via the element's own
            // On*Changing-coalesced hook instead" reason as Overlay/ElementResize/LineEndpoint --
            // this file's own LineEndpoint comment above documents the exact double-push bug an
            // earlier omission caused; not repeating it here.
            if (_dragMode is not (DragMode.Overlay or DragMode.ElementResize or DragMode.Placing or DragMode.LineEndpoint or DragMode.PerspectiveCorner))
            {
                vm.PushUndoSnapshotForDragGesture();
            }

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

                // Live guide-line preview (follow-up visual-polish pass) -- READ-ONLY, never applied
                // to element.X/Y here; the actual snap stays exactly the drop-only behavior
                // OnCanvasPointerReleased's own Overlay branch already implements (see that branch's
                // own doc comment for why absolute mid-drag snapping would decouple the element from
                // the cursor). Skipped on a zero-delta move (this file already documents Avalonia can
                // raise one right after press) -- the guides can't have changed if the element didn't.
                if (dxNormalized != 0 || dyNormalized != 0)
                {
                    var (others, cropCenter) = BuildAlignmentInputs(vm, element);
                    var (guideX, guideY) = ComputeAlignmentGuideLines(
                        (element.X, element.Y, element.Width, element.Height), others, cropCenter);
                    vm.GuideLineXNormalized = guideX;
                    vm.GuideLineYNormalized = guideY;
                }

                break;
            case DragMode.ElementResize when _draggedElement is { } element:
                // Shift = preserve aspect ratio (auditor usability review follow-up, 2026-08-18) --
                // same modifier convention as Photoshop/Illustrator/PowerPoint's own corner-handle
                // proportional-scale gesture, not a persisted per-element setting (keeps this an
                // interaction-only change with no new template-schema/persistence surface).
                var preserveAspect = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                var (newWidth, newHeight, centerDeltaX, centerDeltaY) =
                    ComputeElementResize(element.Width, element.Height, dxNormalized, dyNormalized, _resizeHandle, preserveAspect);
                element.Width = newWidth;
                element.Height = newHeight;
                element.X += centerDeltaX;
                element.Y += centerDeltaY;
                break;
            case DragMode.Placing:
                // Live rubber-band preview (follow-up visual-polish pass). Gated on
                // _pendingPlacementKind, not just _dragMode == Placing -- OnArmedPlacementPressed's
                // right-click-to-disarm can leave _dragMode stuck at Placing until the eventual
                // pointer release (see that handler's own doc comment), and without this gate the
                // preview would keep tracking the cursor after the tool was already disarmed.
                // Below PlacementClickThresholdPixels of movement, OnCanvasPointerReleased's own
                // Placing branch creates a default-SIZED element regardless of where the cursor is
                // (a click, not a drag) -- suppressing the preview in that band avoids showing a
                // tiny 2%x2% rect that then pops to a much larger default size the instant the
                // threshold is crossed.
                if (_pendingPlacementKind is null)
                {
                    break;
                }

                if (Point.Distance(_placementAnchorPoint, current) < PlacementClickThresholdPixels)
                {
                    vm.PlacementPreviewRect = null;
                    vm.PlacementPreviewLine = null;
                    break;
                }

                // TX editor gap-items plan, line element -- a parallel preview field, not
                // PlacementPreviewRect (see that field's own doc comment: a rect can't represent a
                // line's own direction). Shift-angle-snap applies to the placement drag too (not
                // just an already-selected line's endpoint drag, see SnapPointToAngle's own doc
                // comment), same Shift-modifier convention ElementResize's own preserve-aspect uses.
                if (_pendingPlacementKind == PlacementKind.Line)
                {
                    var lineEnd = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SnapPointToAngle(_placementAnchorPoint, current) : current;
                    vm.PlacementPreviewLine = ComputeLineFromDrag(_placementAnchorPoint, lineEnd, vm.CanvasDisplayWidth, vm.CanvasDisplayHeight);
                    break;
                }

                var (previewCenterX, previewCenterY, previewWidth, previewHeight) =
                    ComputeRectFromDrag(_placementAnchorPoint, current, vm.CanvasDisplayWidth, vm.CanvasDisplayHeight);
                vm.PlacementPreviewRect = new NormalizedRect(
                    previewCenterX - (previewWidth / 2), previewCenterY - (previewHeight / 2), previewWidth, previewHeight);
                break;
            case DragMode.LineEndpoint when _draggedElement is LineElementViewModel line:
                // TX editor gap-items plan, line element -- writes an ABSOLUTE position every frame
                // (unlike Overlay/ElementResize's own incremental-delta writes), computed directly
                // from the cursor's own current canvas position -- an endpoint drag has no
                // "accumulate a delta from the last frame" need the way a whole-element move does,
                // and an absolute write avoids any possible incremental-rounding drift over a long
                // drag. Shift-angle-snap is relative to the OTHER (fixed) endpoint, not the drag's
                // own anchor point (unlike the Placing case above, an endpoint drag has no separate
                // "anchor" -- the line's other endpoint already IS the natural pivot).
                var fixedX = (_draggedIsFirstEndpoint ? line.X2 : line.X1) * vm.CanvasDisplayWidth;
                var fixedY = (_draggedIsFirstEndpoint ? line.Y2 : line.Y1) * vm.CanvasDisplayHeight;
                var draggedPoint = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    ? SnapPointToAngle(new Point(fixedX, fixedY), current)
                    : current;
                if (_draggedIsFirstEndpoint)
                {
                    line.X1 = draggedPoint.X / vm.CanvasDisplayWidth;
                    line.Y1 = draggedPoint.Y / vm.CanvasDisplayHeight;
                }
                else
                {
                    line.X2 = draggedPoint.X / vm.CanvasDisplayWidth;
                    line.Y2 = draggedPoint.Y / vm.CanvasDisplayHeight;
                }

                break;
            case DragMode.PerspectiveCorner when _draggedElement is { } draggedElement:
                // TX editor gap-items plan, item 3 -- writes an ABSOLUTE position every frame, same
                // "no delta-accumulation, no incremental-rounding drift" reasoning as LineEndpoint's
                // own identical convention above. Gated behind a real-time convexity clamp
                // (TryWritePerspectiveCorner's own doc comment) -- a rejected candidate position
                // simply doesn't move the corner further that frame, same silent-clamp UX as every
                // other real-time drag constraint in this editor.
                TryWritePerspectiveCorner(
                    draggedElement, _draggedCornerIndex, current.X / vm.CanvasDisplayWidth, current.Y / vm.CanvasDisplayHeight,
                    vm.WorkingCopyWidth, vm.WorkingCopyHeight);
                break;
        }
    }

    /// <summary>TX editor gap-items plan, item 3 -- writes corner <paramref name="cornerIndex"/> of
    /// <paramref name="element"/> to (<paramref name="x"/>, <paramref name="y"/>) ONLY if the
    /// resulting quad is still convex/well-formed, checked via
    /// <see cref="PerspectiveCorners.IsConvexAndWellFormed"/> -- the SAME shared check
    /// <c>TransmitImagePreparer</c>'s own render path uses (moved to <see cref="PerspectiveCorners"/>
    /// specifically so this UI-layer clamp and the render-time check can never independently drift
    /// apart -- <c>ScanlineStudio.UI</c> cannot reference <c>Core.Imaging</c>). Candidate corners are
    /// converted to WORKING-COPY pixel space first (not left in normalized [0,1] space) so the
    /// check's own absolute edge-length floor means something at a comparable scale to what the
    /// render path itself checks in destination-pixel space -- a stated approximation, not exact
    /// space-matching (the final TX-mode target size isn't fixed at edit time, so exact matching
    /// isn't possible client-side). Public, not private -- same "unit-testable without simulating
    /// real Avalonia pointer events" reasoning <see cref="ComputeElementResize"/>'s own doc comment
    /// gives (this project has no <c>InternalsVisibleTo</c> wired up anywhere).</summary>
    public static void TryWritePerspectiveCorner(
        ITemplateElementViewModel element, int cornerIndex, double x, double y, double workingCopyWidth, double workingCopyHeight)
    {
        var (c0x, c0y, c1x, c1y, c2x, c2y, c3x, c3y) = element switch
        {
            ImageElementViewModel image => (image.Corner0X, image.Corner0Y, image.Corner1X, image.Corner1Y, image.Corner2X, image.Corner2Y, image.Corner3X, image.Corner3Y),
            BoxElementViewModel box => (box.Corner0X, box.Corner0Y, box.Corner1X, box.Corner1Y, box.Corner2X, box.Corner2Y, box.Corner3X, box.Corner3Y),
            _ => (0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0),
        };

        switch (cornerIndex)
        {
            case 0: (c0x, c0y) = (x, y); break;
            case 1: (c1x, c1y) = (x, y); break;
            case 2: (c2x, c2y) = (x, y); break;
            case 3: (c3x, c3y) = (x, y); break;
            default: return;
        }

        var candidate = new PerspectiveCorners(
            c0x * workingCopyWidth, c0y * workingCopyHeight,
            c1x * workingCopyWidth, c1y * workingCopyHeight,
            c2x * workingCopyWidth, c2y * workingCopyHeight,
            c3x * workingCopyWidth, c3y * workingCopyHeight);
        if (!candidate.IsConvexAndWellFormed())
        {
            return;
        }

        switch (element)
        {
            case ImageElementViewModel image:
                SetCorner(image, cornerIndex, x, y);
                break;
            case BoxElementViewModel box:
                SetCorner(box, cornerIndex, x, y);
                break;
        }
    }

    private static void SetCorner(ImageElementViewModel image, int cornerIndex, double x, double y)
    {
        switch (cornerIndex)
        {
            case 0: image.Corner0X = x; image.Corner0Y = y; break;
            case 1: image.Corner1X = x; image.Corner1Y = y; break;
            case 2: image.Corner2X = x; image.Corner2Y = y; break;
            case 3: image.Corner3X = x; image.Corner3Y = y; break;
        }
    }

    private static void SetCorner(BoxElementViewModel box, int cornerIndex, double x, double y)
    {
        switch (cornerIndex)
        {
            case 0: box.Corner0X = x; box.Corner0Y = y; break;
            case 1: box.Corner1X = x; box.Corner1Y = y; break;
            case 2: box.Corner2X = x; box.Corner2Y = y; break;
            case 3: box.Corner3X = x; box.Corner3Y = y; break;
        }
    }

    /// <summary>Pure resize-with-floor math, split out from <see cref="OnCanvasPointerMoved"/> so it's
    /// unit-testable without simulating real Avalonia pointer events (code-review finding -- Phase 1
    /// shipped this logic with zero test coverage). Public, not internal -- this project has no
    /// <c>InternalsVisibleTo</c> wired up anywhere (same reasoning/precedent as
    /// <see cref="ScanlineStudio.UI.Controls.WaterfallPalette"/>'s own doc comment).
    /// <para><paramref name="handle"/> defaults to <see cref="ResizeHandle.BottomRight"/> so every
    /// pre-existing call site/test keeps compiling and behaving byte-for-byte identically. Each
    /// handle frees only the edge(s) it visually sits on -- a corner handle frees both its adjacent
    /// edges (both Width and Height change, center shifts diagonally, pinning the OPPOSITE corner); an
    /// edge-midpoint handle frees only its own axis (e.g. dragging the Right handle changes Width
    /// only, Height and the Y center are untouched) -- matches every mainstream image editor's own
    /// resize-handle convention.</para>
    /// <para>X/Y is CENTER-anchored: growing a free edge by the full delta while shifting the center
    /// by HALF that delta keeps the OPPOSITE (anchored) edge/corner pinned in place, which is what
    /// "drag this handle" actually means -- a naive Width/Height-only grow would expand symmetrically
    /// in all directions and visibly run away from the pointer.</para>
    /// <para>Floored at <see cref="MinNormalizedElementSize"/> (code-review finding, revising an
    /// earlier "no floor needed" call) -- Width/Height going negative isn't just visually odd,
    /// <c>ApplyTemplate</c> treats it as "skip this element," so an unclamped fast drag silently
    /// deleted content from the transmitted image. The returned center delta is HALF the
    /// ACTUALLY-APPLIED size delta (not the raw requested delta), which keeps the pinned-edge
    /// behavior correct once the floor engages, instead of the center continuing to drift past where
    /// the clamped edge actually stopped.</para>
    /// <para><paramref name="preserveAspect"/> (Shift-drag, auditor usability review follow-up,
    /// 2026-08-18) only applies at CORNER handles -- an edge-midpoint handle has no natural second
    /// pointer axis to derive a ratio from, so it stays a plain single-axis stretch even with Shift
    /// held, same convention mainstream editors use. At a corner, whichever of the two free axes the
    /// pointer moved further along (proportionally, i.e. |delta|/currentSize) drives a single scale
    /// factor; the other free axis is DERIVED from the original aspect ratio instead of following its
    /// own raw delta.</para></summary>
    public static (double Width, double Height, double CenterDeltaX, double CenterDeltaY) ComputeElementResize(
        double currentWidth, double currentHeight, double dxNormalized, double dyNormalized,
        ResizeHandle handle = ResizeHandle.BottomRight, bool preserveAspect = false)
    {
        var freeLeft = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        var freeRight = handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        var freeTop = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        var freeBottom = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;
        var isCorner = (freeLeft || freeRight) && (freeTop || freeBottom);

        var widthDelta = freeRight ? dxNormalized : freeLeft ? -dxNormalized : 0;
        var heightDelta = freeBottom ? dyNormalized : freeTop ? -dyNormalized : 0;

        if (preserveAspect && isCorner && currentWidth > 0 && currentHeight > 0)
        {
            var aspect = currentWidth / currentHeight;
            if (Math.Abs(widthDelta) / currentWidth >= Math.Abs(heightDelta) / currentHeight)
            {
                heightDelta = widthDelta / aspect;
            }
            else
            {
                widthDelta = heightDelta * aspect;
            }
        }

        var newWidth = Math.Max(currentWidth + widthDelta, MinNormalizedElementSize);
        var newHeight = Math.Max(currentHeight + heightDelta, MinNormalizedElementSize);
        var appliedDx = newWidth - currentWidth;
        var appliedDy = newHeight - currentHeight;

        var centerDeltaX = freeRight ? appliedDx / 2 : freeLeft ? -appliedDx / 2 : 0;
        var centerDeltaY = freeBottom ? appliedDy / 2 : freeTop ? -appliedDy / 2 : 0;

        return (newWidth, newHeight, centerDeltaX, centerDeltaY);
    }

    /// <summary>Phase 6 (spec/15-template-designer.md): snap-ON-DROP, not during the drag itself --
    /// see <see cref="TxImageEditorPaneViewModel.SnapToGrid"/>'s own doc comment for why continuous
    /// per-frame snapping is broken against this editor's incremental drag-delta model. Only element
    /// drags/resizes snap (never the crop rect); a bare click that never moved does nothing extra
    /// here beyond what already happens.</summary>
    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            if (_dragMode is DragMode.Overlay or DragMode.ElementResize && _draggedElement is { Locked: false } element)
            {
                var x = element.X;
                var y = element.Y;
                var width = element.Width;
                var height = element.Height;

                if (vm.SnapToGrid)
                {
                    (x, y, width, height) = SnapElementBoundsToGrid(x, y, width, height);
                }

                // TX workflow modernization plan, Phase 3c -- alignment-guide snap, drop-only (NOT
                // live-magnetic-during-drag: this editor's drag model is incremental deltas, not
                // absolute writes, and a mid-drag absolute snap would decouple the element from the
                // cursor -- same reasoning SnapToGrid's own doc comment already gives for grid snap
                // being drop-only). Scoped to Overlay (move) drags only, not ElementResize -- "align
                // this element with that one" is a move concept; resize-alignment is a different,
                // more involved feature not built here. Merged with grid-snap's own result into ONE
                // final position BEFORE the single ApplySnappedElementBounds call below (plan-review
                // finding: two sequential snap-then-apply calls would be two undo steps for one
                // drop) -- alignment takes priority per axis when both would apply; grid's result
                // stands on an axis alignment didn't touch.
                if (_dragMode == DragMode.Overlay)
                {
                    var (others, cropCenter) = BuildAlignmentInputs(vm, element);
                    var (alignX, alignY) = ComputeAlignmentSnap((element.X, element.Y, element.Width, element.Height), others, cropCenter);
                    x = alignX ?? x;
                    y = alignY ?? y;
                }

                // Code-review finding: apply as one atomic undo step via the VM, not 4 direct property
                // assignments here -- see ApplySnappedElementBounds' own doc comment for why 4 separate
                // assignments would need two Undos to fully revert a snapped drag.
                if (x != element.X || y != element.Y || width != element.Width || height != element.Height)
                {
                    vm.ApplySnappedElementBounds(element, x, y, width, height);
                }
            }

            // TX editor gap-items plan, line element -- a THIRD, separate case from the whole-element
            // snap block above, not folded into it (see DragMode.LineEndpoint's own doc comment for
            // why: that block's ApplySnappedElementBounds call snaps a whole line's two endpoints
            // together, wrong for a drag that only ever moves ONE of them). No alignment-guide snap
            // for an endpoint drag -- out of scope for a line in v1 (deliberate scope cut, matching
            // ApplySnappedLineEndpoint's own doc comment).
            if (_dragMode == DragMode.LineEndpoint && _draggedElement is LineElementViewModel { Locked: false } draggedLine && vm.SnapToGrid)
            {
                vm.ApplySnappedLineEndpoint(draggedLine, _draggedIsFirstEndpoint);
            }

            // TX workflow modernization plan, Phase 3a -- the actual element creation. Below
            // PlacementClickThresholdPixels of real movement is a plain click (default size,
            // centered on the release point); above it is a drag (sized to the dragged rect).
            if (_dragMode == DragMode.Placing && _pendingPlacementKind is { } kind)
            {
                var released = e.GetPosition(EditorCanvas);
                var distance = Point.Distance(_placementAnchorPoint, released);

                // TX editor gap-items plan, line element -- a genuinely different shape from
                // text/box below (endpoints, not center/width/height), so branched out first rather
                // than shoehorned into the same 4-double locals. Click ⇒ a default HORIZONTAL line
                // centered on the click point (matching AddLineElementCommand's own toolbar-button
                // default exactly, not a separate literal); drag ⇒ the endpoints ARE the drag's own
                // anchor/release points directly (ComputeLineFromDrag, not ComputeRectFromDrag --
                // see that method's own doc comment for why a line must not be run through the
                // rect-normalizing helper). Shift-angle-snap applies here too, same as the live
                // preview already showed during the drag.
                if (kind == PlacementKind.Line)
                {
                    if (distance < PlacementClickThresholdPixels)
                    {
                        var clickCenterX = released.X / vm.CanvasDisplayWidth;
                        var clickCenterY = released.Y / vm.CanvasDisplayHeight;
                        var halfWidth = TxImageEditorPaneViewModel.DefaultElementWidth / 2;
                        vm.AddLineElementAt(clickCenterX - halfWidth, clickCenterY, clickCenterX + halfWidth, clickCenterY);
                    }
                    else
                    {
                        var lineEnd = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? SnapPointToAngle(_placementAnchorPoint, released) : released;
                        var (lx1, ly1, lx2, ly2) = ComputeLineFromDrag(_placementAnchorPoint, lineEnd, vm.CanvasDisplayWidth, vm.CanvasDisplayHeight);
                        vm.AddLineElementAt(lx1, ly1, lx2, ly2);
                    }
                }
                else
                {
                    double centerX, centerY, width2, height2;
                    if (distance < PlacementClickThresholdPixels)
                    {
                        centerX = released.X / vm.CanvasDisplayWidth;
                        centerY = released.Y / vm.CanvasDisplayHeight;
                        width2 = TxImageEditorPaneViewModel.DefaultElementWidth;
                        height2 = kind == PlacementKind.Text
                            ? TxImageEditorPaneViewModel.DefaultTextElementHeight
                            : TxImageEditorPaneViewModel.DefaultBoxElementHeight;
                    }
                    else
                    {
                        (centerX, centerY, width2, height2) =
                            ComputeRectFromDrag(_placementAnchorPoint, released, vm.CanvasDisplayWidth, vm.CanvasDisplayHeight);
                    }

                    if (kind == PlacementKind.Text)
                    {
                        vm.AddOverlayElementAt(centerX, centerY, width2, height2);
                    }
                    else
                    {
                        vm.AddBoxElementAt(centerX, centerY, width2, height2);
                    }
                }

                // One-shot by default; Shift-held-at-arm-time (TX workflow modernization plan, Phase
                // 3a "sticky" flexibility addition) keeps the tool armed for repeated placements
                // instead of disarming here.
                if (!_pendingPlacementSticky)
                {
                    DisarmPlacement();
                }
            }

            // Unconditional -- every drag ends here (or at CancelActiveDrag/DisarmPlacement/
            // OnEditorCanvasPointerCaptureLost for the paths that don't reach a real release), and
            // none of these three should ever persist past the gesture that set them: a sticky
            // Placing repeat still needs the rubber band cleared between each individual placement,
            // and a resize/crop drag never set the guide lines in the first place, so clearing them
            // here too is a harmless no-op rather than a mode-gated special case.
            vm.PlacementPreviewRect = null;
            vm.PlacementPreviewLine = null;
            vm.GuideLineXNormalized = null;
            vm.GuideLineYNormalized = null;
        }

        _dragMode = DragMode.None;
        _draggedElement = null;
        e.Pointer.Capture(null);
    }

    /// <summary>Plan-review finding, follow-up visual-polish pass -- no handler existed anywhere in
    /// this file for a LOST pointer capture (window deactivation, a modal/flyout stealing it,
    /// Alt-Tab: Avalonia does not deliver <see cref="OnCanvasPointerReleased"/> to the old capture
    /// target in that case). Pre-existing latent gap this doesn't widen scope to fix (<see cref="_dragMode"/>
    /// itself is left as it was, same as before this pass) -- narrowly clears the rubber-band/guide-
    /// line preview state specifically, since those are new and would otherwise be able to get stuck
    /// visibly on screen with no gesture left to clear them, unlike every pre-existing piece of drag
    /// state this file already tolerates staying stale until the next real drag starts.</summary>
    private void OnEditorCanvasPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.PlacementPreviewRect = null;
            vm.PlacementPreviewLine = null;
            vm.GuideLineXNormalized = null;
            vm.GuideLineYNormalized = null;
        }
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
    /// Shift+arrow resizes instead of moving. Merged with the former, canvas-only <c>OnCanvasKeyDown</c>
    /// (auditor usability review, 2026-08-17, item 4: "editor keyboard shortcuts go dead after
    /// clicking any sidebar control") -- these shortcuts were wired on <c>EditorCanvas</c>'s own
    /// <c>KeyDown</c>, which only ever RECEIVES a routed key event while the canvas itself is the
    /// focused element or one of its own descendants; clicking a sidebar control (Lock toggle,
    /// GEOMETRY TextBox, a Templates button, ...) moves focus OUTSIDE that subtree entirely, so the
    /// event never reaches it again. Wiring everything here instead -- the root
    /// <see cref="UserControl"/>, already used for the ready-rack number-key recall below for the
    /// identical reason -- fixes it for free: a routed <c>KeyDown</c> bubbles from wherever focus
    /// actually is up through every ancestor, and the root is always an ancestor of every control
    /// on this whole pane. The established <c>e.Source is TextBox</c> guard (previously local to the
    /// number-key recall only) now covers every shortcut below for the same reason it already covered
    /// number keys: Delete/arrows/Ctrl+C/V/Z/Y are all real, expected TextBox-native operations (text
    /// deletion, caret movement, native text undo, OS clipboard) that must not be hijacked while the
    /// operator is typing into ANY of this editor's own name/X/Y/size/font-size/outline-width/QSO-fill
    /// fields.</summary>
    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel is not { } vm)
        {
            return;
        }

        // Backlog item (auditor usability review, 2026-08-17, item 14): Escape while inline-editing
        // text on the canvas (the TextBox OnOverlayElementPointerPressed's double-click branch/F2
        // below reveals) exits edit mode -- checked BEFORE the general TextBox guard just below,
        // since this IS the TextBox-focused case this branch exists to handle.
        if (e.Key == Key.Escape && e.Source is TextBox
            && vm.SelectedOverlayElement is OverlayElementViewModel { IsEditingText: true } editingText)
        {
            editingText.IsEditingText = false;
            e.Handled = true;
            return;
        }

        // Backlog item (auditor usability review, 2026-08-17, item 15): Escape cancels an
        // in-progress crop/element drag -- checked before the TextBox guard too (a real drag can
        // never be in progress while a TextBox has focus, so ordering is moot, but this keeps every
        // Escape-means-"cancel/dismiss the active thing" branch grouped together at the top).
        if (e.Key == Key.Escape && _dragMode != DragMode.None)
        {
            CancelActiveDrag(vm);
            e.Handled = true;
            return;
        }

        if (e.Source is TextBox)
        {
            return;
        }

        // TX workflow modernization plan, Phase 3a -- Esc disarms a placement tool that's armed but
        // not yet pressed on the canvas (the mid-drag case is handled by CancelActiveDrag above,
        // reached via the _dragMode != DragMode.None branch). Placed AFTER the TextBox guard
        // (plan-review finding) so pressing Esc while typing somewhere in the sidebar doesn't also
        // disarm an unrelated pending placement.
        if (e.Key == Key.Escape && _pendingPlacementKind is not null)
        {
            DisarmPlacement();
            e.Handled = true;
            return;
        }

        // Ready rack number-key recall (spec/15-template-designer.md Phase 5) -- Key.D1..Key.D9 AND
        // Key.NumPad1..Key.NumPad9. Backlog item (auditor usability review, 2026-08-17, item 17):
        // "Ready Rack number keys fire with modifiers held" -- e.g. Ctrl+1 could plausibly mean
        // something else entirely (a future tab-switch shortcut, a browser-style binding); requiring
        // NO modifiers here is the same "don't steal a chord that isn't unambiguously ours" discipline
        // Ctrl/Cmd+C/X/V/Z/Y below already apply in reverse (they DO require a modifier, specifically
        // so a bare keystroke doesn't get hijacked). Ctrl+1..Ctrl+9 is NOW claimed too -- Ready Rack
        // direct-fire plan (2026-09-01), see the ctrlOrCmd-gated digit branch further below, right
        // after ctrlOrCmd itself is computed -- ADDITIVE, not a replacement: this plain (no-modifier)
        // branch and its own load-only recall behavior are completely unchanged.
        if (e.KeyModifiers == KeyModifiers.None)
        {
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

            if (slot is { } slotNumber)
            {
                vm.ReadyRack.RecallSlotCommand.Execute(slotNumber);
                e.Handled = true;
                return;
            }
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

        // Control OR Meta (task #23's own Ctrl/Cmd+wheel precedent) -- Ctrl on Windows/Linux, Cmd on
        // macOS.
        var ctrlOrCmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);

        // Ready Rack direct-fire plan (2026-09-01): Ctrl+1..Ctrl+9 -- a deliberate two-key gesture
        // (Fable design review), never a bare number key, so an accidental keystroke can't fire RF.
        // Additive sibling to the plain-digit recall branch above (unchanged) -- checked separately
        // since ctrlOrCmd wasn't computed yet at that point in this method. No Shift/Alt gate needed
        // (Key.D1..D9/NumPad1..9 don't collide with anything else this method handles under Ctrl).
        if (ctrlOrCmd)
        {
            var directFireSlot = e.Key switch
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

            if (directFireSlot is { } slotNumber)
            {
                vm.ReadyRack.DirectFireSlotCommand.Execute(slotNumber);
                e.Handled = true;
                return;
            }
        }

        // Backlog item (auditor usability review, 2026-08-17, item 1): "Ctrl+Z/Ctrl+Y not wired
        // (Undo/Redo mouse-only, conspicuous next to the new Ctrl+C/X/V)." CanExecute-gated (matches
        // this editor's own established "check CanExecute before Execute for a keyboard shortcut"
        // convention, see the Paste branch below) rather than letting Execute silently no-op, so a
        // Ctrl+Z with nothing to undo falls through unhandled instead of eating the keystroke.
        if (ctrlOrCmd && e.Key == Key.Z && vm.UndoCommand.CanExecute(null))
        {
            vm.UndoCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrlOrCmd && e.Key == Key.Y && vm.RedoCommand.CanExecute(null))
        {
            vm.RedoCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Backlog item (auditor usability review, 2026-08-17, item 15): "No Apply keyboard shortcut."
        // Ctrl/Cmd+Enter -- plain Enter is left alone (native default-button/TextBox behavior
        // elsewhere on this pane, e.g. committing a QSO-fill/template-name TextBox), matching the
        // common "primary action" chord convention (email clients, chat apps, IDEs).
        if (ctrlOrCmd && e.Key == Key.Enter && vm.ApplyCommand.CanExecute(null))
        {
            vm.ApplyCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Backlog item (auditor usability review, 2026-08-17, item 14): F2 enters inline canvas text
        // editing for the selected, unlocked text element -- see
        // OverlayElementViewModel.IsEditingText's own doc comment. No auto-focus grab here (unlike
        // the double-click path in OnOverlayElementPointerPressed, which has a real Visual to search
        // from) -- F2 can fire from anywhere in this pane, including a sidebar control with no
        // relation to the canvas element's own visual subtree; the operator clicks into the now-
        // visible TextBox once, same documented minor wrinkle as FocusInlineTextEditor's own doc
        // comment.
        if (e.Key == Key.F2 && vm.SelectedOverlayElement is OverlayElementViewModel { Locked: false } textToEdit)
        {
            textToEdit.IsEditingText = true;
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

        // Backlog item (user request, 2026-08-17): in-editor Copy/Cut/Paste for the selected canvas
        // element. Shift excluded (TX workflow modernization plan round 2 finding): Ctrl+Shift+C/V
        // are reserved for Copy Style/Paste Style below and would otherwise be silently swallowed
        // here first.
        var shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrlOrCmd && !shiftHeld && e.Key == Key.C && vm.SelectedOverlayElement is not null)
        {
            vm.CopySelectedElementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrlOrCmd && !shiftHeld && e.Key == Key.X && vm.SelectedOverlayElement is not null)
        {
            vm.CutSelectedElementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrlOrCmd && !shiftHeld && e.Key == Key.V && vm.PasteElementCommand.CanExecute(null))
        {
            vm.PasteElementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // TX workflow modernization plan, Phase 1: Copy/Paste Style, Ctrl+Shift+C/V.
        if (ctrlOrCmd && shiftHeld && e.Key == Key.C && vm.CopySelectedElementStyleCommand.CanExecute(null))
        {
            vm.CopySelectedElementStyleCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (ctrlOrCmd && shiftHeld && e.Key == Key.V && vm.PasteSelectedElementStyleCommand.CanExecute(null))
        {
            vm.PasteSelectedElementStyleCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // TX workflow modernization plan, Phase 8: Duplicate = Ctrl+D. No Shift check needed here
        // (unlike C/X/V above) -- nothing else in this handler uses Ctrl+Shift+D.
        if (ctrlOrCmd && e.Key == Key.D && vm.DuplicateCommand.CanExecute(null))
        {
            vm.DuplicateCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Auditor usability review follow-up (2026-08-18): OS-clipboard image paste, the keyboard
        // counterpart to the "+ IMAGE" flyout's own FROM CLIPBOARD button. Falls through to here only
        // when the branch above didn't fire (the in-editor copy/cut element clipboard is empty) --
        // the internal element paste wins first, matching mainstream creative-tool convention (Ctrl+V
        // pastes whichever clipboard is actually relevant right now, element over raw image).
        // AddImageFromClipboardCommand has no CanExecute predicate to gate on here -- it's always
        // "executable," a real no-op only discovered ASYNCHRONOUSLY once it actually queries the OS
        // clipboard and finds nothing image-shaped there (same as clicking FROM CLIPBOARD with an
        // empty/non-image clipboard) -- so this fires unconditionally rather than trying to
        // synchronously pre-check clipboard content. Still marks Handled unconditionally: this pane
        // owns Ctrl+V within its own bounds regardless of outcome (same reasoning as this whole
        // method's own class doc comment for why these shortcuts are intercepted at the root).
        if (ctrlOrCmd && e.Key == Key.V)
        {
            vm.AddImageFromClipboardCommand.Execute(null);
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

        // Backlog item (auditor usability review, 2026-08-17, item 18): "no keyboard element-resize
        // path at all." Ctrl+Shift+arrow resizes the selected, unlocked element instead of the crop
        // rect -- checked BEFORE the bare-Shift branch below so it takes priority over Phase 6's own
        // "Shift+arrow always means crop-resize" rule (still true for bare Shift, unchanged).
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && ctrlOrCmd && vm.SelectedOverlayElement is { Locked: false })
        {
            vm.NudgeElementResize(dir);
            e.Handled = true;
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

    /// <summary>Backlog item (auditor usability review, 2026-08-17, item 15) -- reverts whatever this
    /// gesture already changed (a single Undo call, since every drag gesture -- crop or element --
    /// pushes exactly ONE coalesced undo step regardless of which of the two push mechanisms fired,
    /// see <see cref="OnCanvasPointerMoved"/>'s own updated comment) and stops responding to further
    /// pointer moves. Deliberately does NOT release pointer capture here -- <see cref="Pointer"/> isn't
    /// reachable from a <see cref="KeyEventArgs"/>; leaving capture in place is harmless, since
    /// <see cref="OnCanvasPointerMoved"/> already no-ops once <see cref="_dragMode"/> is
    /// <see cref="DragMode.None"/>, and the eventual real pointer-release still runs
    /// <see cref="OnCanvasPointerReleased"/> normally (whose own snap-to-grid branch is also gated on
    /// <see cref="_dragMode"/>, so it correctly does nothing for an already-cancelled gesture).</summary>
    private void CancelActiveDrag(TxImageEditorPaneViewModel vm)
    {
        if (_pushedUndoThisGesture && vm.UndoCommand.CanExecute(null))
        {
            vm.UndoCommand.Execute(null);
        }

        // TX workflow modernization plan, Phase 3a -- a mid-drag Esc during Placing fully disarms
        // (not just cancels this one drag), matching the "Esc disarms an armed tool" requirement.
        // No undo step to revert here regardless: Placing never pushes one until the element is
        // actually created (OnCanvasPointerMoved's own comment on excluding Placing from the
        // _pushedUndoThisGesture branch), so _pushedUndoThisGesture is always false for this mode.
        if (_dragMode == DragMode.Placing)
        {
            DisarmPlacement();
        }

        // Unconditional -- same unconditional-clear reasoning as OnCanvasPointerReleased's own tail;
        // an Overlay drag is the only mode that ever sets these, but clearing regardless keeps this
        // one call site correct even if that changes later.
        vm.GuideLineXNormalized = null;
        vm.GuideLineYNormalized = null;

        _dragMode = DragMode.None;
        _draggedElement = null;
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17, item 13): "ELEMENTS rows don't
    /// select or highlight on click." Selection stays UNGATED (any button, any child control inside
    /// the row, selects) -- same reasoning as <see cref="OnOverlayElementPointerPressed"/>'s own
    /// canvas-click selection (its own doc comment: clicking the row's Lock toggle or "x" delete
    /// button also selecting first is harmless, matching that established precedent exactly).
    /// Highlighting itself is driven by <see cref="ITemplateElementViewModel.IsSelected"/>, set by
    /// <c>TxImageEditorPaneViewModel.OnSelectedOverlayElementChanged</c> -- this handler only needs
    /// to write the selection, not the highlight.</summary>
    private void OnElementRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ITemplateElementViewModel element } && ViewModel is { } vm)
        {
            vm.SelectedOverlayElement = element;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17, item 14) -- commits and exits the
    /// canvas inline text editor when it loses focus (clicking elsewhere, tabbing away). No separate
    /// "commit" step needed -- the TextBox's own <c>Text</c> binding is two-way against
    /// <see cref="OverlayElementViewModel.Text"/> already, the same property the ELEMENTS-row TextBox
    /// commits through, so every keystroke is already live.</summary>
    private void OnElementTextEditLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: OverlayElementViewModel text })
        {
            text.IsEditingText = false;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17, item 14) -- Enter commits/exits
    /// the same way losing focus does (a single-line text element has no legitimate use for a literal
    /// newline); Escape is handled by <see cref="OnRootKeyDown"/>'s own dedicated branch instead (that
    /// one needs <c>e.Source is TextBox</c> visibility this handler already implies, kept in one place
    /// rather than duplicated).</summary>
    private void OnElementTextEditKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is Control { DataContext: OverlayElementViewModel text })
        {
            text.IsEditingText = false;
            e.Handled = true;
        }
    }

    /// <summary>Missing-feature sweep (2026-08-31): OS file drag-and-drop onto the TX editor -- see
    /// <see cref="TxImageEditorPaneViewModel.AddImagesFromDroppedFilesAsync"/>'s own doc comment for
    /// the full feature reasoning. Wired on <c>EditorWell</c> (the well <c>Border</c>, not
    /// <c>EditorScrollViewer</c> or <c>EditorCanvas</c>) -- the ScrollViewer's own un-backgrounded
    /// centering gutter (the common case at Fit zoom, see <c>OnEditorWheelChanged</c>'s own
    /// comment for the identical hit-testing hazard) is NOT hit-testable, so Avalonia's own
    /// ancestor walk for a drop landing there would never find <c>AllowDrop</c> if it were set on
    /// the ScrollViewer instead; the well IS backgrounded everywhere, so it's a reliable target
    /// regardless of zoom/scroll position.
    ///
    /// <c>DataFormat.File</c>/<c>TryGetFiles()</c> -- Avalonia 11.3's drag-drop API
    /// (<c>DataFormats.Files</c>/<see cref="IDataObject.GetFiles"/> are obsolete in this version and
    /// would not compile under this project's own <c>TreatWarningsAsErrors</c>). Only accepts
    /// <see cref="DragDropEffects.Copy"/> when the payload actually contains files (`&amp;=`, not a
    /// plain assignment, so a source only offering Move/Link is correctly rejected rather than
    /// forced into a Copy it never advertised).</summary>
    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? e.DragEffects & DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary><see cref="IDataTransfer"/>'s own items are only valid until it's disposed, which
    /// happens once this handler returns -- the local file paths are read out SYNCHRONOUSLY, before
    /// the single `await` below, not lazily inside
    /// <see cref="TxImageEditorPaneViewModel.AddImagesFromDroppedFilesAsync"/>. Folders are excluded
    /// (<see cref="IStorageItem"/> covers both files and folders; <c>OfType&lt;IStorageFile&gt;</c>
    /// keeps only files). The path-extraction itself is wrapped in its own try/catch, separate from
    /// the ViewModel's own outer guard around the rest of the work -- this code-behind has no
    /// logger of its own (unlike e.g. <c>MainWindow.axaml.cs</c>'s own code-behind handlers), so a
    /// failure here reports through <see cref="TxImageEditorPaneViewModel.ReportDroppedFilesUnreadable"/>
    /// instead of being silently swallowed (this is <c>async void</c> -- there is no caller to
    /// observe a fault otherwise).</summary>
    private async void OnEditorDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        // Code-review finding: this event's own DragEffects is a SEPARATE value from
        // OnEditorDragOver's -- it's seeded from the platform's allowed-effects mask (typically
        // Copy|Move|Link) and returned to the drag SOURCE unchanged if left alone. A source that
        // sees Move still set treats the drop as a move and deletes the original file -- this
        // editor only ever inserts a COPY of the dropped image, never takes ownership of the file.
        e.DragEffects &= DragDropEffects.Copy;
        if (ViewModel is not { } vm)
        {
            return;
        }

        List<string> paths;
        try
        {
            paths = (e.DataTransfer.TryGetFiles() ?? [])
                .OfType<IStorageFile>()
                .Select(f => f.TryGetLocalPath())
                .OfType<string>()
                .ToList();
        }
        catch (Exception ex)
        {
            vm.ReportDroppedFilesUnreadable(ex);
            return;
        }

        await vm.AddImagesFromDroppedFilesAsync(paths);
    }
}
