using System.Collections;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Tests;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;
using static ScanlineStudio.UI.FontTests.RealWindowTestSupport;

namespace ScanlineStudio.UI.FontTests;

/// <summary>PROJECT_BRIEF.md's own tracked "Still open" item 1 for the TX Template Editor workflow
/// modernization plan: "A real UI smoke test -- the broader click-through (every menu item, the
/// draw-to-place gesture, Ctrl-drag-duplicate, one real flatten) is still not done ... Explicitly
/// deferred by the user on 2026-08-31 and again on 2026-09-01." The concrete reason it matters (also
/// from that entry): every per-element context-menu command shipped bound directly to a bare
/// <c>{Binding XCommand}</c> that resolved against the wrong DataContext for a while -- a SILENT
/// failure (Avalonia binding errors don't fail the build or a VM-level unit test, they just leave the
/// menu item permanently disabled at runtime) that was found and fixed mid-Phase-7, but never actually
/// regression-tested through a real click until this file. See
/// <c>TxImageEditorPerspectiveCornerDragRealRenderTests</c>'s own doc comment for why this project
/// (real Skia, real fonts, the real production View) is the only place that can do this, and
/// <see cref="RealWindowTestSupport"/> for the shared setup both files use.
///
/// <b>Scope, stated precisely</b>: "every menu item" is covered as MECHANISM (every bound
/// <c>Command</c> in the text/box/image context menus resolves non-null, and where the AXAML backs
/// it with <c>CommandParameter="{Binding}"</c> also resolves to the right element by reference,
/// through a REAL right-click -- not a VM-level property check) rather than individually clicking all
/// ~30 items. The canvas's own context menu is checked separately (<c>Paste</c> only, by design --
/// see that test's own doc comment); <see cref="LineElementViewModel"/>'s context menu is NOT swept
/// here (known coverage gap, same inline-<c>{Binding}</c> shape as the other 3, not yet a suspected
/// bug). <c>TxImageEditorPaneViewModelTests</c>'s own
/// <c>*ContextMenu_EveryPushedCommand_ResolvesToTheElementsOwnInstance</c> tests already prove each
/// command instance is wired correctly at the VM layer; what only a real click can prove is that the
/// AXAML <c>Command="{Binding ...}"</c> binding itself resolves against the right DataContext.
/// Duplicate is also driven through a real popup <c>MenuItem</c> for real observed behavior (a
/// synthesized <c>Click</c> routed event, not a pointer click through the open popup); Flatten is
/// driven directly via <c>ExecuteAsync</c> instead, per that test's own doc comment. Plus the 2
/// gestures PROJECT_BRIEF.md names explicitly (draw-to-place, Ctrl-drag-duplicate).
///
/// <b>2 real bugs, not harness quirks, found and fixed by an auditor code-review pass over an earlier
/// draft of this file (2026-09-02)</b>: the image element's own body <see cref="Border"/> had no
/// <c>Background</c>, so its interior didn't hit-test (a real right-click/drag fell through to the
/// crop body underneath) -- fixed in <c>TxImageEditorPaneView.axaml</c>, same
/// <c>Background="Transparent"</c> precedent the crop body itself already used. The "Add Text"/"Add
/// Box"/"Add Line" toolbar buttons' <c>PointerPressed</c> handlers never fired for a real click --
/// confirmed via decompiling the exact Avalonia 11.3.12 <c>Button.OnPointerPressed</c> in use, which
/// unconditionally marks the event <c>Handled</c> before a same-node bubbling instance handler runs --
/// fixed by moving those 3 handlers to <c>AddHandler(..., RoutingStrategies.Tunnel)</c> in
/// <c>TxImageEditorPaneView.axaml.cs</c>'s constructor, the same pattern the canvas's own
/// <c>OnArmedPlacementPressed</c> already used. Both are now exercised via real clicks below, not
/// worked around.</summary>
public sealed class TxImageEditorRealUiSmokeTests
{
    /// <summary>Loc keys for the small number of context-menu items that are legitimately NOT
    /// <c>Command</c>-bound by design (a code-behind <c>Click</c> handler instead) -- everything else
    /// reachable in <see cref="AssertEveryLeafCommandResolves"/> is expected to have a non-null
    /// <c>Command</c>. <c>FakeLocalizationService.GetString</c> returns the raw key unresolved (this
    /// project's own established convention), so a MenuItem's real rendered <c>Header</c> IS the loc
    /// key string under this fake.</summary>
    private static readonly HashSet<string> ClickBasedMenuItemHeaders =
    [
        "Panes.TxImageEditor.QuickStyleMenu",
        "Panes.TxImageEditor.FillBorderMenu",
        "Panes.TxImageEditor.FitSafeArea",
        "Panes.TxImageEditor.FitWidth",
        "Panes.TxImageEditor.FitHeight",
    ];

    [AvaloniaFact]
    public void RightClickingATextElement_OpensItsContextMenu_AndEveryBoundCommandResolves()
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddOverlayElementCommand.Execute(null);
            PumpDispatcher();
            var text = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);

            var contextMenu = RightClickElementBody(window, text);

            AssertEveryLeafCommandResolves(contextMenu, text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightClickingABoxElement_OpensItsContextMenu_AndEveryBoundCommandResolves()
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddBoxElementCommand.Execute(null);
            PumpDispatcher();
            var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);

            var contextMenu = RightClickElementBody(window, box);

            AssertEveryLeafCommandResolves(contextMenu, box);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RightClickingAnImageElement_OpensItsContextMenu_AndEveryBoundCommandResolves()
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            await vm.AddImageFromFileCommand.ExecuteAsync(null);
            PumpDispatcher();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);

            var contextMenu = RightClickElementBody(window, image);

            AssertEveryLeafCommandResolves(contextMenu, image);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task RightClickingALockedBackdrop_OpensItsOwnContextMenu_NotTheCanvasEmptyAreaOne()
    {
        // User-reported bug (2026-09-15): "load a background, then add an image, and set that image
        // as backdrop; the right click menu is that of the background... not the backdrop, even if
        // the mouse is ON the backdrop." A locked backdrop's own canvas Border used to have
        // IsHitTestVisible="{Binding !BlocksHitTesting}" = False, so ALL pointer events -- including
        // right-click -- fell through to whatever was underneath instead of opening its own menu.
        // BuildRealWindow's own source (a real ArrayImageSource, not BlankImageSource) is already a
        // real, non-blank background -- exactly the "load a background" half of the repro.
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            await vm.AddImageFromFileCommand.ExecuteAsync(null);
            PumpDispatcher();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);

            vm.SetAsBackdropCommand.Execute(image);
            PumpDispatcher();
            Assert.True(image.BlocksHitTesting);

            // RightClickElementBody finds THIS element's own Border/ContextMenu by DataContext
            // identity, then asserts THAT specific ContextMenu instance actually opened -- if the
            // click had instead fallen through to a DIFFERENT control (the canvas's own empty-area
            // one, the exact bug reported), this element's own ContextMenu would correctly read
            // IsOpen=false and the assertion inside RightClickElementBody itself would fail.
            var contextMenu = RightClickElementBody(window, image);

            AssertEveryLeafCommandResolves(contextMenu, image);
        }
        finally
        {
            window.Close();
        }
    }

    // Reads a single Bgra8888 pixel back out of a real, Skia-rendered WriteableBitmap. Deliberately
    // NOT done this way in ScanlineStudio.UI.Tests: that project's headless renderer is documented
    // (feedback_verify_avalonia_rendering_with_real_window memory) to NOT reliably round-trip raw
    // pixel bytes through WriteableBitmap.Lock() -- a real diagnostic there wrote known BGRA bytes
    // and read back different, non-matching values. This project's TestAppBuilder uses a real Skia
    // backend (UseHeadlessDrawing = false, see IndustryFontResolutionTests' own doc comment for why),
    // which is the whole reason it exists as a separate project -- exactly the place a pixel-content
    // assertion like this one belongs.
    private static Rgb24 SamplePreviewPixel(Bitmap bitmap, int x, int y)
    {
        var writeable = Assert.IsType<WriteableBitmap>(bitmap);
        using var frameBuffer = writeable.Lock();
        var offset = frameBuffer.Address + (y * frameBuffer.RowBytes) + (x * 4);
        var b = Marshal.ReadByte(offset);
        var g = Marshal.ReadByte(offset + 1);
        var r = Marshal.ReadByte(offset + 2);
        return new Rgb24(r, g, b);
    }

    [AvaloniaFact]
    public async Task SetAsBackdropCommand_RealPipeline_PreviewShowsTheBackdropCoveringTheWholeFrame()
    {
        // User-reported bug (2026-09-15): "also if i set as backdrop an image...the preview window
        // no longer displays the background image information." Investigation (code-reading): the
        // editor's own PREVIEW panel (TxImageEditorPaneViewModel.PreviewImage, distinct from
        // TxControlsPaneViewModel's Apply/Transmit-only thumbnail) recomputes via
        // RecomputePreviewPipeline -> ComposePreview, which composites EVERY OverlayElements member
        // (backdrop included, via BuildTemplateDocument) ON TOP of the cropped/resized background --
        // so a full-frame, opaque backdrop is EXPECTED to visually cover the loaded background in
        // this same preview, by design (the same "only one of background/backdrop is ever the
        // visible base layer" invariant Promote/Demote already enforce elsewhere). This test proves
        // that expected replacement actually renders correctly end-to-end (real
        // TransmitImagePreparer, real Skia-backed WriteableBitmap read-back) rather than silently
        // rendering something else (stale, blank, or wrong-colored) -- which is what "no longer
        // displays" would actually look like if this were a genuine bug.
        var red = new Rgb24(200, 20, 20);
        var blue = new Rgb24(20, 20, 200);
        var background = CreateSolidSource(DefaultSourceWidth, DefaultSourceHeight, red);
        var insertedImage = CreateSolidSource(DefaultSourceWidth, DefaultSourceHeight, blue);
        var vm = CreateEditor(background, TestMode, new FakeImageFileLoader { ResultToReturn = insertedImage });
        var (window, _, _) = BuildRealWindowForVm(vm);
        try
        {
            Assert.Equal(red, SamplePreviewPixel(vm.PreviewImage!, 0, 0));

            await vm.AddImageFromFileCommand.ExecuteAsync(null);
            PumpDispatcher();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);
            // Sanity: a freshly-inserted image defaults to less than full-frame, so the corner still
            // shows the background here -- proves the later color flip is caused specifically by Set
            // as backdrop, not by AddImageFromFile alone.
            Assert.Equal(red, SamplePreviewPixel(vm.PreviewImage!, 0, 0));

            vm.SetAsBackdropCommand.Execute(image);
            PumpDispatcher();

            Assert.Equal(blue, SamplePreviewPixel(vm.PreviewImage!, 0, 0));
            Assert.Equal(blue, SamplePreviewPixel(vm.PreviewImage!, DefaultSourceWidth / 2, DefaultSourceHeight / 2));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CancelButton_RendersExactlyOneLabel_NotTwoStackedOnes()
    {
        // Auditor-found blocker (2026-09-15): migrating Cancel from an in-row two-click arm/confirm
        // to a real dialog removed the VM's own IsCancelArmed property, but an earlier draft of that
        // migration left the BUTTON's own AXAML still binding to it (Classes.IndustryBtnDanger and
        // two IsVisible-gated TextBlocks in a Panel). This project has no compiled bindings enabled
        // (confirmed via check-help.mjs's own sibling AXAML-integrity checks not covering reflection
        // bindings either), so an unresolvable binding like that doesn't fail the build -- it resolves
        // to Avalonia's own UnsetValue, and IsVisible's default is true, so BOTH TextBlocks rendered
        // "CANCEL" and "DISCARD?" stacked on top of each other, silently, at runtime only. Fixed by
        // replacing the whole Panel/two-TextBlock structure with a single plain Content binding --
        // this test pins that structurally: Content is a single string (the raw loc key, via
        // FakeLocalizationService's own pass-through), not a Panel, so this exact failure mode is no
        // longer even representable, not just observed-passing today.
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            var view = (TxImageEditorPaneView)window.Content!;
            var cancelButton = view.FindControl<Button>("CancelButton")
                ?? throw new InvalidOperationException("CancelButton not found in the real View's visual tree.");

            Assert.Equal("Panes.TxImageEditor.Cancel", Assert.IsType<string>(cancelButton.Content));
            Assert.DoesNotContain("IndustryBtnDanger", cancelButton.Classes);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ImageElementLayersRow_BackdropToggleButtons_ResolveAgainstTheRightElementAndFlipVisibility()
    {
        // User-reported gap (2026-09-15): a LOCKED backdrop element's own canvas Border has
        // IsHitTestVisible="{Binding !BlocksHitTesting}" = False (pre-existing, documented limitation
        // at that Border's own comment) -- a real right-click on a backdrop falls through to whatever
        // is underneath (the crop rect / canvas empty-area menu) instead of reaching the backdrop's
        // own SetAsBackdrop/DemoteToBackground/Remove menu. The ELEMENTS layers-list row is NOT gated
        // by canvas hit-testing at all, so its own backdrop-toggle buttons are the fix -- this proves
        // they resolve against the RIGHT element instance through a REAL render and flip IsVisible
        // correctly as IsBackground changes, exactly the class of bug this whole file exists to catch
        // (a bare {Binding XCommand} resolving against the wrong DataContext stays silently null/wrong
        // at runtime, invisible to a VM-level unit test).
        //
        // Harness gotcha found while writing this test (not a production bug, auditor-reviewed): an
        // earlier draft blamed this on two Buttons sharing one Grid.Column -- wrong, Grid attached
        // properties don't affect binding reactivity, and both buttons now have their own dedicated
        // column (see the AXAML's own comment on the demote button below) with no change in behavior.
        // The actual cause: SetAsBackdrop calls OverlayElements.Move(index, 0) -- for this test's
        // single-element collection that's a same-index move, but ObservableCollection<T> still
        // raises a real Move notification. A Button captured BEFORE that call (bound to
        // SetAsBackdropCommand) stopped tracking further changes afterward; a SIBLING Button captured
        // the exact same way (bound to DemoteToBackgroundCommand) did not -- which specific captured
        // reference goes stale isn't fully understood, so don't assume it's predictable. What IS
        // confirmed: a FRESH FindBackdropRowButtons call right after the move always sees the correct,
        // already-flipped state. Re-query after any mutation that could reorder the backing collection
        // -- don't hold onto row-button references across one.
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            await vm.AddImageFromFileCommand.ExecuteAsync(null);
            PumpDispatcher();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);

            var (promoteButton, demoteButton) = FindBackdropRowButtons(window, image);
            Assert.True(promoteButton.IsEffectivelyVisible);
            Assert.False(demoteButton.IsEffectivelyVisible);
            Assert.Same(image.SetAsBackdropCommand, promoteButton.Command);
            Assert.Same(image.DemoteToBackgroundCommand, demoteButton.Command);

            vm.SetAsBackdropCommand.Execute(image);
            PumpDispatcher();

            Assert.True(image.IsBackground);

            var (freshPromote, freshDemote) = FindBackdropRowButtons(window, image);
            Assert.False(freshPromote.IsVisible);
            Assert.True(freshDemote.IsVisible);
            Assert.False(freshPromote.IsEffectivelyVisible);
            Assert.True(freshDemote.IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RightClickingTheEmptyCanvas_OpensItsContextMenu_AndPasteCommandResolves()
    {
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            // Empty area away from the crop-body/any element in the layout THIS TEST'S OWN mode uses
            // (a 320x240 source in a 320x240 mode, so the default crop covers the whole canvas) --
            // (2,2) still lands on the crop body Border first and reaches the canvas menu by bubbling
            // once that Border's own context menu comes up empty for that press, not because nothing
            // else claims the press.
            var pointInCanvas = new Point(2, 2);
            var pointInWindow = editorCanvas.TranslatePoint(pointInCanvas, window)!.Value;

            window.MouseDown(pointInWindow, MouseButton.Right, RawInputModifiers.None);
            window.MouseUp(pointInWindow, MouseButton.Right, RawInputModifiers.None);
            PumpDispatcher();

            var contextMenu = editorCanvas.ContextMenu ?? throw new InvalidOperationException("EditorCanvas has no ContextMenu.");
            Assert.True(contextMenu.IsOpen);

            var pasteItem = WalkMenuItems(contextMenu.Items).Single(mi => mi.Header as string == "Panes.TxImageEditor.Paste");
            Assert.NotNull(pasteItem.Command);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RealRightClickThenDuplicateMenuItemClick_OnATextElement_ActuallyDuplicatesIt()
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddOverlayElementCommand.Execute(null);
            PumpDispatcher();
            var text = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
            Assert.Single(vm.OverlayElements);

            var contextMenu = RightClickElementBody(window, text);
            var duplicateItem = WalkMenuItems(contextMenu.Items).Single(mi => mi.Header as string == "Panes.TxImageEditor.Duplicate");

            duplicateItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            PumpDispatcher();

            Assert.Equal(2, vm.OverlayElements.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DrawToPlaceGesture_ClickingAddTextThenDraggingOnTheCanvas_CreatesANewTextElementAtTheDraggedRect()
    {
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            Assert.Empty(vm.OverlayElements);

            var view = (TxImageEditorPaneView)window.Content!;
            var addTextButton = view.FindControl<Button>("AddTextButton")
                ?? throw new InvalidOperationException("AddTextButton not found in the real View's visual tree.");
            var buttonCenter = addTextButton.TranslatePoint(
                new Point(addTextButton.Bounds.Width / 2, addTextButton.Bounds.Height / 2), window)!.Value;

            // Real click arms the tool -- TxImageEditorPaneView.axaml.cs's constructor wires this via
            // AddHandler(..., RoutingStrategies.Tunnel), not a plain XAML PointerPressed attribute (see
            // this file's own class-level doc comment for why the plain attribute never fired).
            window.MouseDown(buttonCenter, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(buttonCenter, MouseButton.Left, RawInputModifiers.None);
            PumpDispatcher();

            // A genuine drag (well past PlacementClickThresholdPixels), not a click -- exercises
            // TxImageEditorPaneView.ComputeRectFromDrag (public, unit-testable by design), not the
            // click-default-size branch.
            var dragStartLocal = new Point(40, 30);
            var dragEndLocal = new Point(140, 110);
            var dragStart = editorCanvas.TranslatePoint(dragStartLocal, window)!.Value;
            var dragEnd = editorCanvas.TranslatePoint(dragEndLocal, window)!.Value;

            window.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(dragEnd);
            window.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);
            PumpDispatcher();

            var created = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
            Assert.Same(created, vm.SelectedOverlayElement);

            // Expected value comes from the production code's OWN formula, not re-derived -- same
            // "read the real formula, predict the real value" strategy the perspective-corner-drag
            // tests use, for the same reason (if the math were wrong, independently recomputing it
            // here would just reproduce the same bug and pass anyway).
            var (expectedCenterX, expectedCenterY, expectedWidth, expectedHeight) = TxImageEditorPaneView.ComputeRectFromDrag(
                dragStartLocal, dragEndLocal, vm.CanvasDisplayWidth, vm.CanvasDisplayHeight);
            // X/Y store the CENTER directly (AddOverlayElementAt's own centerX/centerY parameters flow
            // straight through to CreateOverlayElement's x/y, confirmed by reading it), not a top-left.
            AssertClose(expectedCenterX, created.X);
            AssertClose(expectedCenterY, created.Y);
            AssertClose(expectedWidth, created.Width);
            AssertClose(expectedHeight, created.Height);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingTheSelectionReadoutBadge_MovesTheSelectedElement()
    {
        // User-requested (2026-09-16): the floating selection-readout badge above a selected element
        // previously had IsHitTestVisible="False" -- clicking/dragging it did nothing instead of
        // moving the element the same way dragging the element's own body does. Real drag through
        // Avalonia's actual input pipeline, not a VM-level property check -- a plain XAML wiring
        // mistake (e.g. leaving IsHitTestVisible="False" in place) wouldn't be caught by one.
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddOverlayElementCommand.Execute(null);
            vm.SnapToGrid = false; // exact-delta assertion below, not the post-drag 5% grid snap
            PumpDispatcher();
            var element = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
            var xBefore = element.X;
            var yBefore = element.Y;

            var view = (TxImageEditorPaneView)window.Content!;
            var badge = view.FindControl<Border>("SelectionReadoutBadge")
                ?? throw new InvalidOperationException("SelectionReadoutBadge not found in the real View's visual tree.");
            var pressPoint = badge.TranslatePoint(new Point(5, 5), window)!.Value;
            var pressPointInCanvas = window.TranslatePoint(pressPoint, editorCanvas)!.Value;
            var dragVector = new Vector(50, 35);
            var releasePoint = editorCanvas.TranslatePoint(pressPointInCanvas + dragVector, window)!.Value;

            window.MouseDown(pressPoint, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(releasePoint);
            window.MouseUp(releasePoint, MouseButton.Left, RawInputModifiers.None);
            PumpDispatcher();

            // Same "read the real formula, predict the real value" strategy as this file's other drag
            // tests (e.g. ComputeRectFromDrag above) -- a real drop also runs alignment-guide snap
            // (TxImageEditorPaneView.ComputeAlignmentSnap, UNCONDITIONAL for DragMode.Overlay, no VM
            // flag to disable it, unlike SnapToGrid), so the raw linear delta alone isn't always the
            // final value; predicting it via the production formula (rather than picking a drag
            // distance that happens to dodge the 1% threshold) keeps this robust if defaults change.
            var rawExpectedX = xBefore + (dragVector.X / vm.CanvasDisplayWidth);
            var rawExpectedY = yBefore + (dragVector.Y / vm.CanvasDisplayHeight);
            var cropCenter = (vm.CropRect.X + (vm.CropRect.Width / 2), vm.CropRect.Y + (vm.CropRect.Height / 2));
            var (snapX, snapY) = TxImageEditorPaneView.ComputeAlignmentSnap(
                (rawExpectedX, rawExpectedY, element.Width, element.Height), [], cropCenter);

            AssertClose(snapX ?? rawExpectedX, element.X);
            AssertClose(snapY ?? rawExpectedY, element.Y);
            // The regression this test actually guards against: the badge press/drag wiring itself.
            // If IsHitTestVisible were still False (or the handler weren't wired), the element simply
            // wouldn't have moved at all -- both snap and raw-delta predictions above would then fail
            // this pair of checks too, but this makes the real intent explicit.
            Assert.NotEqual(xBefore, element.X);
            Assert.NotEqual(yBefore, element.Y);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CtrlDraggingAnExistingElement_DuplicatesItAndDragsOnlyTheClone_UndoRemovesJustTheClone()
    {
        var (window, vm, editorCanvas) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            vm.AddOverlayElementCommand.Execute(null);
            PumpDispatcher();
            var original = Assert.IsType<OverlayElementViewModel>(vm.SelectedOverlayElement);
            var originalXBefore = original.X;
            var originalYBefore = original.Y;

            var body = FindElementBody(window, original);
            var pressPoint = body.TranslatePoint(new Point(body.Bounds.Width / 2, body.Bounds.Height / 2), window)!.Value;
            var pressPointInCanvas = window.TranslatePoint(pressPoint, editorCanvas)!.Value;
            var dragVector = new Vector(60, 45);
            var releasePoint = editorCanvas.TranslatePoint(pressPointInCanvas + dragVector, window)!.Value;

            window.MouseDown(pressPoint, MouseButton.Left, RawInputModifiers.Control);
            window.MouseMove(releasePoint);
            window.MouseUp(releasePoint, MouseButton.Left, RawInputModifiers.Control);
            PumpDispatcher();

            Assert.Equal(2, vm.OverlayElements.Count);
            // The ORIGINAL never moved -- only a clone was dragged (OnOverlayElementPointerPressed's
            // own Ctrl-drag-to-duplicate doc comment: "leaving the original in place").
            Assert.Equal(originalXBefore, original.X);
            Assert.Equal(originalYBefore, original.Y);

            // The CLONE actually moved, by the real drag delta -- OnCanvasPointerMoved's own formula
            // (dxNormalized = pixelDelta / vm.CanvasDisplayWidth, same for Y), not re-derived, same
            // "read the real formula" strategy as ComputeRectFromDrag above. Without this, a
            // duplicate-but-don't-actually-drag regression would still pass (Count==2 plus the
            // original's own unchanged position are the only other observable signals here).
            // DuplicateElementForDrag's own +0.02 seed offset (InsertClonedSnapshot's
            // OffsetSnapshotForClone, applied to BOTH X and Y before the drag starts) has to be
            // included here too -- it's real production behavior, not test noise, per that method's
            // own doc comment ("harmless +0.02 seed offset is immediately overridden by the drag that
            // follows" -- overridden by an ADDITIONAL delta on top, not replaced).
            const double CloneSeedOffset = 0.02;
            var clone = Assert.Single(vm.OverlayElements.Where(e => !ReferenceEquals(e, original)));
            AssertClose(originalXBefore + CloneSeedOffset + dragVector.X / vm.CanvasDisplayWidth, clone.X);
            AssertClose(originalYBefore + CloneSeedOffset + dragVector.Y / vm.CanvasDisplayHeight, clone.Y);

            Assert.True(vm.UndoCommand.CanExecute(null));
            vm.UndoCommand.Execute(null);

            // One Undo removes the clone ENTIRELY (not just its dragged position) -- matches the
            // VM-level DuplicateElementForDrag test's own established assertion shape. Compared by
            // VALUE, not Assert.Same: ApplyState (Undo's own restore path) replaces OverlayElements
            // wholesale with fresh instances built from the pre-drag snapshot, not the literal same
            // object references (same "re-read from OverlayElements, not the captured element
            // reference" convention this codebase's own VM-level undo tests already establish).
            var remaining = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
            Assert.Equal(originalXBefore, remaining.X);
            Assert.Equal(originalYBefore, remaining.Y);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>"One real flatten" (PROJECT_BRIEF.md's own item wording) via direct command
    /// invocation, not a real right-click -- the AXAML <c>Command="{Binding FlattenCommand}"</c>
    /// binding itself is already covered by <see cref="RightClickingAnImageElement_OpensItsContextMenu_AndEveryBoundCommandResolves"/>
    /// above; this test's own job is the REAL <c>FlattenElementAsync</c> pipeline end-to-end (real
    /// <see cref="TransmitImagePreparer"/>, its own <c>Task.Run</c> bake offload, source
    /// replacement).</summary>
    [AvaloniaFact]
    public async Task FlattenElementCommand_OnAnImageElement_RemovesItAndReplacesTheSource()
    {
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            await vm.AddImageFromFileCommand.ExecuteAsync(null);
            PumpDispatcher();
            var image = Assert.IsType<ImageElementViewModel>(vm.SelectedOverlayElement);
            Assert.Single(vm.OverlayElements);
            Assert.True(image.FlattenCommand!.CanExecute(image));
            var previewBeforeFlatten = vm.PreviewImage;

            await ((IAsyncRelayCommand)image.FlattenCommand!).ExecuteAsync(image);
            PumpDispatcher();

            Assert.Empty(vm.OverlayElements);
            // Proves a preview recompute actually happened after the flatten (FlattenElementAsync's
            // own single, unconditional RecomputePreview() call at its end flips the pooled
            // PreviewImage instance every time it runs) -- a WEAKER claim than "the source was
            // replaced" (auditor code-review finding on an earlier draft of this comment): a
            // hypothetical regression that skipped ReplaceSourceAndWorkingCopy but still reached that
            // same trailing RecomputePreview would still flip the instance and still pass this
            // assertion. Deterministic, not flaky, since the bake path always runs RecomputePreview
            // exactly once -- but it isn't proof of source replacement specifically.
            Assert.NotSame(previewBeforeFlatten, vm.PreviewImage);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Locates the real element-body <see cref="Border"/> -- the one that owns
    /// <c>Border.ContextMenu</c> and <c>PointerPressed="OnOverlayElementPointerPressed"</c> -- as
    /// opposed to a resize/corner/endpoint handle Border, which never has a <c>ContextMenu</c> of its
    /// own.</summary>
    private static Border FindElementBody(Visual root, ITemplateElementViewModel element)
        => root.GetVisualDescendants()
            .OfType<Border>()
            .Single(b => ReferenceEquals(b.DataContext, element) && b.ContextMenu is not null);

    /// <summary>The ELEMENTS layers-list row's own promote/demote buttons (background/backdrop
    /// naming work, 2026-09-15) -- both bound to the SAME <c>DataContext</c> and the SAME
    /// <c>Content</c> glyph, mutually-exclusive via <c>IsVisible</c>, so <c>Command</c> reference
    /// identity (not DataContext or Content) is the only thing that discriminates one from the
    /// other.</summary>
    private static (Button Promote, Button Demote) FindBackdropRowButtons(Visual root, ImageElementViewModel image)
    {
        var buttons = root.GetVisualDescendants().OfType<Button>().Where(b => ReferenceEquals(b.DataContext, image)).ToList();
        var promote = buttons.Single(b => ReferenceEquals(b.Command, image.SetAsBackdropCommand));
        var demote = buttons.Single(b => ReferenceEquals(b.Command, image.DemoteToBackgroundCommand));
        return (promote, demote);
    }

    /// <summary>Real right-click (MouseDown+MouseUp, <see cref="MouseButton.Right"/>, through
    /// Avalonia's actual raw-input pipeline -- same technique
    /// <c>TxImageEditorQuickStyleFlyoutRealClickTests</c> established) on <paramref name="element"/>'s
    /// own body Border, asserting the real <see cref="ContextMenu"/> actually opened before
    /// returning it.</summary>
    private static ContextMenu RightClickElementBody(Window window, ITemplateElementViewModel element)
    {
        var body = FindElementBody(window, element);
        var center = body.TranslatePoint(new Point(body.Bounds.Width / 2, body.Bounds.Height / 2), window)!.Value;

        window.MouseDown(center, MouseButton.Right, RawInputModifiers.None);
        window.MouseUp(center, MouseButton.Right, RawInputModifiers.None);
        PumpDispatcher();

        var contextMenu = body.ContextMenu!;
        Assert.True(contextMenu.IsOpen);
        return contextMenu;
    }

    private static IEnumerable<MenuItem> WalkMenuItems(IEnumerable items)
    {
        foreach (var item in items)
        {
            if (item is not MenuItem menuItem)
            {
                continue;
            }

            yield return menuItem;

            foreach (var descendant in WalkMenuItems(menuItem.Items))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>The core "every menu item" assertion: every LEAF menu item (no sub-items, so not a
    /// submenu header like "Arrange"/"Align to Crop") that isn't a checkbox toggle (Lock/Snap to Grid,
    /// which have no <c>Command</c> by design) and isn't one of the small known <c>Click</c>-handler-
    /// based items (<see cref="ClickBasedMenuItemHeaders"/>) must have a non-null <c>Command</c> --
    /// exactly the property a bare <c>{Binding XCommand}</c> resolving against the wrong DataContext
    /// leaves silently null.
    ///
    /// <paramref name="element"/>, when given, adds a SECOND, stronger check: wherever the AXAML also
    /// sets <c>CommandParameter="{Binding}"</c>, assert that parameter IS <paramref name="element"/>
    /// by reference. This closes a real gap a bare non-null Command check can't: a handful of parent-
    /// VM commands (Duplicate/AlignSelectedElementToCrop/SetAsBackdrop/BringToFront/SendToBack) share
    /// the exact SAME object reference as the element's own pushed copy (<c>CreateOverlayElement</c>
    /// assigns <c>element.XCommand = this.XCommand</c> directly), so if the binding accidentally
    /// resolved against the PARENT VM instead of the element, <c>Command</c> would STILL be non-null
    /// AND identical -- a code-review finding on an earlier draft of this file. CommandParameter isn't
    /// aliased that way (the element and the parent VM are never the same object), so it's the one
    /// signal that actually discriminates. Not every item has a CommandParameter to check (Duplicate
    /// has none; AlignSelectedElementToCrop's is a literal direction string like <c>"Left"</c>, which
    /// is <c>null or string</c>-excluded below rather than <c>{Binding}</c>-bound) -- those stay
    /// covered by the non-null Command check only, a known residual gap, not silently
    /// unacknowledged.</summary>
    private static void AssertEveryLeafCommandResolves(ContextMenu contextMenu, ITemplateElementViewModel? element = null)
    {
        var checkedCount = 0;
        foreach (var item in WalkMenuItems(contextMenu.Items))
        {
            if (item.Items.Count > 0 || item.ToggleType != MenuItemToggleType.None)
            {
                continue;
            }

            var header = item.Header as string ?? "";
            if (ClickBasedMenuItemHeaders.Contains(header))
            {
                continue;
            }

            Assert.True(item.Command is not null, $"MenuItem '{header}' has a null Command.");

            // Only meaningful when CommandParameter is itself DataContext-shaped
            // (CommandParameter="{Binding}" in the AXAML) -- a literal string ("Left"/"Stretch"/...)
            // is a deliberately different kind of parameter, not evidence of anything about
            // DataContext. Deliberately NOT narrowed to `is ITemplateElementViewModel`: the parent VM
            // (TxImageEditorPaneViewModel) does NOT implement that interface, so that narrower check
            // would SILENTLY SKIP the exact wrong-DataContext-resolved-to-the-parent-VM case this
            // whole check exists to catch (auditor code-review finding) -- a `null or string` exclusion
            // still lets a wrongly-resolved parent VM instance through to the ReferenceEquals below,
            // where it correctly fails instead of being skipped.
            if (element is not null && item.CommandParameter is not (null or string))
            {
                Assert.True(ReferenceEquals(item.CommandParameter, element),
                    $"MenuItem '{header}' CommandParameter is not the element itself -- Command likely bound against the wrong DataContext.");
            }

            checkedCount++;
        }

        // Guards against this method silently checking zero items if the menu structure ever changes
        // shape (e.g. every item becomes a submenu header) -- a vacuously-passing "every item is fine"
        // result would be worse than no test at all.
        Assert.True(checkedCount > 0, "Expected at least one Command-bound leaf menu item to check.");
    }

    [AvaloniaFact]
    public void RackAndLibraryActionStrips_NoSelectionMade_AreNotVisible()
    {
        // User-reported gap (2026-09-15): both action strips (rename box + Load/Unpin/Export/Delete)
        // rendered VISIBLE on every app launch with nothing selected -- verified permanent, not a
        // startup flash (a fresh window sat 12+ real seconds untouched and never self-corrected).
        // Real cause: ReadyRack.SelectedSlot.Template is a TWO-STEP path, and SelectedSlot is null the
        // entire time nothing's selected -- a null intermediate makes Avalonia yield UnsetValue for
        // the whole path (the converter never runs), and IsVisible falls back to ITS OWN property
        // default, true. Fixed with FallbackValue=False on that binding (TxImageEditorPaneView.axaml).
        // SelectedLibraryItem is single-level, so it was never actually broken -- null there is a real
        // value the converter correctly turns into false; its own FallbackValue is harmless
        // defense-in-depth, not a fix (confirmed: removing it changes nothing, still checked below for
        // regression coverage). A VM-level test can't catch either shape -- SelectedSlot/
        // SelectedLibraryItem are null in the buggy and fixed versions alike; only a real rendered
        // window proves the CONTROL itself starts hidden, not just the bound VALUE.
        var (window, vm, _) = BuildRealWindow(CreateSource(DefaultSourceWidth, DefaultSourceHeight));
        try
        {
            Assert.Null(vm.ReadyRack.SelectedSlot);
            Assert.Null(vm.ReadyRack.SelectedLibraryItem);

            var rackStrip = window.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "RackActionStrip")
                ?? throw new InvalidOperationException("RackActionStrip not found in the real View's visual tree.");
            var libraryStrip = window.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "LibraryActionStrip")
                ?? throw new InvalidOperationException("LibraryActionStrip not found in the real View's visual tree.");

            Assert.False(rackStrip.IsVisible);
            Assert.False(libraryStrip.IsVisible);

            // yoniq-auditor finding: the negative case alone lets FallbackValue=False regress into
            // "the strip silently never appears" (e.g. a future rename of SelectedSlot/Template) with
            // no binding error and nothing else failing -- exactly the silent-failure class this whole
            // test file exists to catch (see its own class doc comment). Prove both strips still show
            // for a REAL selection too.
            var metadata = new TemplateMetadata("t1", "Test Template", DateTimeOffset.UnixEpoch, ThumbnailPath: "");
            vm.ReadyRack.Slots[0].Template = new TemplateListRowViewModel(metadata, isPinned: true);
            vm.ReadyRack.SelectedSlot = vm.ReadyRack.Slots[0];
            vm.ReadyRack.SelectedLibraryItem = new TemplateListRowViewModel(metadata, isPinned: true);
            PumpDispatcher();

            Assert.True(rackStrip.IsVisible);
            Assert.True(libraryStrip.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) < 1e-6, $"Expected {expected}, got {actual}");
}
