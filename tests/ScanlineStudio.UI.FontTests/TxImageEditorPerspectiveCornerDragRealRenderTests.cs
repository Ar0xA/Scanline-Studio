using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using static ScanlineStudio.UI.FontTests.RealWindowTestSupport;

namespace ScanlineStudio.UI.FontTests;

/// <summary>PROJECT_BRIEF.md's own tracked "known gap" for the perspective-transform feature (TX
/// editor gap-items epic, item 3): the corner-drag interaction had NEVER been exercised through a
/// real rendered window -- ScanlineStudio.UI.Tests's headless font stub can't load the full
/// production <see cref="TxImageEditorPaneView"/> (confirmed empirically, see
/// TxImageEditorQuickStyleFlyoutRealClickTests's own doc comment), so every prior verification of
/// this feature was either pure VM-level (no real AXAML binding/event-wiring involved) or pure
/// pixel-math (SolveHomography/IsConvexAndWellFormed, already mutation-tested elsewhere).
///
/// This project (real Skia, real embedded fonts, real <see cref="ScanlineStudio.UI.App"/> styles/
/// resources via <see cref="TestAppBuilder"/>) is the one place that CAN host the real View. Per the
/// auditor's own plan-review verdict: this is deliberately a WIRING test, not a rendering-correctness
/// test -- the warp math itself is already covered; what's uniquely reachable here is
/// <c>ShowPerspectiveCornerHandles</c> actually driving mutually-exclusive handle visibility, the
/// <c>sender is Control { DataContext: ... }</c> + Tag-parse in
/// <c>OnPerspectiveCornerHandlePointerPressed</c>, real hit-test reachability (nothing above the
/// handles swallows the press), and capture-on-<c>EditorCanvas</c> correctly routing the subsequent
/// move into the <c>PerspectiveCorner</c> drag case -- exactly the class of self-consistent-but-wrong
/// wiring bug this project has been burned by once before (the Scottie TX-channel-order incident,
/// CLAUDE.md §4) and that VM-only tests structurally cannot catch.
///
/// <b>Coverage, stated precisely (auditor code-review correction):</b> only <c>Corner0</c> is ever
/// pressed, on 2 of the 8 hand-copied handle declarations (image + box). The <c>Tag</c>-to-
/// <c>CanvasCornerNPoint</c> pairing for the OTHER 3 corner indices is NOT independently verified
/// here -- a <c>Corner1</c>&lt;-&gt;<c>Corner2</c> tag/binding swap on either template would slip past
/// both current tests undetected (confirmed: the pre-drag position is read back from the render, not
/// computed, so a swapped-but-internally-consistent pairing still produces a passing result).
/// Extending to all 4 corners (e.g. an <c>[AvaloniaTheory]</c> over the 4 tags, both element kinds) is
/// a real, tracked follow-up, not done in this pass -- see PROJECT_BRIEF.md.
///
/// <b>Coordinate strategy</b>: never independently re-derives the VM's own corner-to-pixel formula.
/// The corner handle's PRE-drag position is read back from the real render itself (via
/// <see cref="Visual.TranslatePoint(Point, Visual)"/>), not computed -- if the coordinate math were
/// ever wrong, computing it independently here would just reproduce the same bug and pass anyway. The
/// EXPECTED post-drag value uses the production code's own documented formula (<c>current.X /
/// vm.CanvasDisplayWidth</c>, see <c>TxImageEditorPaneView.axaml.cs</c>'s <c>OnPerspectiveCornerHandlePointerPressed</c>/
/// <c>TryWritePerspectiveCorner</c>) applied to a canvas-local target point this test itself chose and
/// then translated into window coordinates -- so the only thing under test is whether the real
/// press-drag-release pipeline reaches that formula at all, not whether the formula itself is
/// correct.
///
/// <b>Shared setup</b>: <see cref="RealWindowTestSupport"/> (extracted here first, reused by
/// <c>TxImageEditorRealUiSmokeTests</c>) handles <c>App.Services</c> init -- see that class's own doc
/// comment for why it's needed and why it's safe unguarded.</summary>
public sealed class TxImageEditorPerspectiveCornerDragRealRenderTests
{
    // Real 320x240 source (RealWindowTestSupport.TestMode/CreateSource default) -- NOT the 4x4
    // fixtures ScanlineStudio.UI.Tests's own VM-level perspective tests use. At 4x4 canvas scale the
    // four 10x10 corner handles fully overlap and a real hit-test would resolve to whichever was
    // declared last (Corner3), not the one actually pressed -- auditor plan-review finding.

    /// <summary>Locates the real corner-handle <see cref="Border"/> for <paramref name="cornerTag"/>
    /// (<c>"Corner0"</c>..<c>"Corner3"</c>) whose <c>DataContext</c> is <paramref name="element"/> --
    /// there are 2 competing DataTemplates (image + box), so filtering by DataContext, not just Tag,
    /// is required to get the right one.</summary>
    private static Border FindCornerHandle(Visual root, ITemplateElementViewModel element, string cornerTag)
        => root.GetVisualDescendants()
            .OfType<Border>()
            .Single(b => ReferenceEquals(b.DataContext, element) && b.Tag as string == cornerTag);

    /// <summary>Real press-drag-release of one corner handle, through Avalonia's actual raw-input
    /// pipeline (<see cref="HeadlessWindowExtensions.MouseDown"/>/<c>MouseMove</c>/<c>MouseUp</c>) --
    /// same technique TxImageEditorQuickStyleFlyoutRealClickTests already established, deliberately
    /// NOT OS-level screen-coordinate automation (this project's own standing caution). Asserts a real
    /// hit-test at the press point resolves to the handle itself first (auditor plan-review finding --
    /// without this, a handle scrolled/positioned off its expected spot would still yield a
    /// syntactically valid window point that silently presses something else, turning a real miss into
    /// a false pass).</summary>
    private static void DragCornerTo(Window window, Canvas editorCanvas, Border handle, Point targetInCanvas)
    {
        var pressPointInWindow = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Corner handle is not in the window's visual tree.");

        var hit = window.InputHitTest(pressPointInWindow);
        Assert.Same(handle, hit);

        var targetInWindow = editorCanvas.TranslatePoint(targetInCanvas, window)
            ?? throw new InvalidOperationException("EditorCanvas is not in the window's visual tree.");

        window.MouseDown(pressPointInWindow, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(targetInWindow);
        window.MouseUp(targetInWindow, MouseButton.Left, RawInputModifiers.None);
        PumpDispatcher();
    }

    [AvaloniaFact]
    public void DraggingAnImageElementsCorner0Handle_MovesOnlyThatCornerToTheExpectedPosition()
    {
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddImageFromFileCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);
            vm.TogglePerspectiveCommand.Execute(image);
            PumpDispatcher();
            Assert.True(image.PerspectiveEnabled);

            var (corner1Before, corner2Before, corner3Before) =
                ((image.Corner1X, image.Corner1Y), (image.Corner2X, image.Corner2Y), (image.Corner3X, image.Corner3Y));

            var handle = FindCornerHandle(window, image, "Corner0");
            // A small, clearly non-degenerate inward move -- keeps the quad convex (Corner0 is the
            // top-left corner; nudging it right+down toward the element's own interior can't cross
            // any other corner) without needing to reason about the element's exact absolute bounds.
            var preDragCanvasPos = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), editorCanvas)!.Value;
            var targetInCanvas = preDragCanvasPos + new Vector(24, 18);

            // Captured before the drag, not just asserted non-null after: PerspectiveEnabled's own
            // OnPerspectiveEnabledChanged already schedules a warped-preview rebuild (auditor
            // code-review finding), so WarpedCanvasBitmap is non-null well before any drag happens --
            // a bare post-drag NotNull check is not drag-specific signal. RebuildWarpedPreview always
            // allocates a FRESH WriteableBitmap (ImageElementViewModel's own contract), so NotSame here
            // is real signal that the drag's own corner change re-ran the warp pipeline.
            var warpedBitmapBeforeDrag = image.WarpedCanvasBitmap;
            Assert.NotNull(warpedBitmapBeforeDrag);

            DragCornerTo(window, editorCanvas, handle, targetInCanvas);

            var expectedX = targetInCanvas.X / vm.CanvasDisplayWidth;
            var expectedY = targetInCanvas.Y / vm.CanvasDisplayHeight;
            AssertClose(expectedX, image.Corner0X);
            AssertClose(expectedY, image.Corner0Y);

            // Mutation-verified (auditor code-review round): a SetCorner case-0/case-1 write-target
            // swap makes the position assertion above fail with a real value mismatch. This trio of
            // asserts is what catches it moving the WRONG corner while Corner0 alone looks fine.
            Assert.Equal(corner1Before, (image.Corner1X, image.Corner1Y));
            Assert.Equal(corner2Before, (image.Corner2X, image.Corner2Y));
            Assert.Equal(corner3Before, (image.Corner3X, image.Corner3Y));

            Assert.NotSame(warpedBitmapBeforeDrag, image.WarpedCanvasBitmap);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingAnImageElementsCorner0Handle_PastAnInvalidPosition_LeavesTheCornerUnchanged()
    {
        // Auditor plan-review finding: TryWritePerspectiveCorner's convexity clamp
        // (PerspectiveCorners.IsConvexAndWellFormed) rejects the write silently -- worth its own test,
        // reachable only through the real drag path (the VM-level math tests exercise the clamp
        // directly, not through a real pointer gesture).
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddImageFromFileCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);
            vm.TogglePerspectiveCommand.Execute(image);
            PumpDispatcher();

            var handle = FindCornerHandle(window, image, "Corner0");
            var before = (image.Corner0X, image.Corner0Y);

            var corner2Handle = FindCornerHandle(window, image, "Corner2");
            var corner2InCanvas = corner2Handle.TranslatePoint(
                new Point(corner2Handle.Bounds.Width / 2, corner2Handle.Bounds.Height / 2), editorCanvas)!.Value;
            // Drag Corner0 (top-left) well PAST Corner2 (bottom-right) -- degenerate/self-intersecting,
            // must be rejected.
            var targetInCanvas = corner2InCanvas + new Vector(40, 40);

            DragCornerTo(window, editorCanvas, handle, targetInCanvas);

            Assert.Equal(before, (image.Corner0X, image.Corner0Y));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingABoxElementsCorner0Handle_MovesOnlyThatCornerToTheExpectedPosition()
    {
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddBoxElementCommand.Execute(null);
            var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
            vm.TogglePerspectiveCommand.Execute(box);
            PumpDispatcher();
            Assert.True(box.PerspectiveEnabled);

            var (corner1Before, corner2Before, corner3Before) =
                ((box.Corner1X, box.Corner1Y), (box.Corner2X, box.Corner2Y), (box.Corner3X, box.Corner3Y));

            var handle = FindCornerHandle(window, box, "Corner0");
            var preDragCanvasPos = handle.TranslatePoint(new Point(handle.Bounds.Width / 2, handle.Bounds.Height / 2), editorCanvas)!.Value;
            var targetInCanvas = preDragCanvasPos + new Vector(15, 10);

            // See the image test's own comment on why this is captured before, not just asserted
            // non-null after, the drag.
            var warpedBitmapBeforeDrag = box.WarpedCanvasBitmap;
            Assert.NotNull(warpedBitmapBeforeDrag);

            DragCornerTo(window, editorCanvas, handle, targetInCanvas);

            var expectedX = targetInCanvas.X / vm.CanvasDisplayWidth;
            var expectedY = targetInCanvas.Y / vm.CanvasDisplayHeight;
            AssertClose(expectedX, box.Corner0X);
            AssertClose(expectedY, box.Corner0Y);

            Assert.Equal(corner1Before, (box.Corner1X, box.Corner1Y));
            Assert.Equal(corner2Before, (box.Corner2X, box.Corner2Y));
            Assert.Equal(corner3Before, (box.Corner3X, box.Corner3Y));

            Assert.NotSame(warpedBitmapBeforeDrag, box.WarpedCanvasBitmap);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) < 1e-6, $"Expected {expected}, got {actual}");
}
