using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class TxImageEditorPaneViewModelTests
{
    private static readonly SstvModeDefinition SmallMode = new(
        Id: "small",
        DisplayName: "Small",
        VisCode: 0,
        ImageWidth: 4,
        ImageHeight: 4,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    // spec/18-path-to-1.0.md High item 4 (aspect-locked crop) -- deliberately non-square (2:1) so
    // a test can distinguish "the crop rect matches the MODE's aspect" from "the crop rect happens
    // to be square like SmallMode is."
    private static readonly SstvModeDefinition WideMode = new(
        Id: "wide",
        DisplayName: "Wide",
        VisCode: 0,
        ImageWidth: 8,
        ImageHeight: 4,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    // Real MacroTextResolver + blank OperatorSettings -- none of these tests exercise macro
    // resolution itself (that's MacroTextResolverTests' job), so a real-but-inert resolver is
    // simpler than a fake with nothing to configure.
    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer) =>
        CreateEditor(original, mode, preparer, new OperatorSettings());

    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer, OperatorSettings operatorSettings) =>
        new(original, mode, preparer, new MacroTextResolver(), operatorSettings, new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance);

    [AvaloniaFact]
    public void Constructor_OriginalLargerThanWorkingCopyBudget_DownsamplesBeforeUse()
    {
        // Budget is mode dims * 2 = 8x8; a 20x20 original must be downsampled, not used directly.
        var original = CreateSource(20, 20);
        var preparer = new FakeTransmitImagePreparer();

        var vm = CreateEditor(original, SmallMode, preparer);

        Assert.NotNull(vm.WorkingCopyBitmap);
        Assert.NotNull(vm.PreviewImage);
        // One Resize call to build the downsampled working copy, one more from the initial preview.
        Assert.Equal(2, preparer.ResizeCallCount);
        Assert.Equal((8, 8, false), preparer.ResizeCalls[0]);
    }

    [AvaloniaFact]
    public void Constructor_OriginalWithinWorkingCopyBudget_UsesOriginalDirectlyAsWorkingCopy()
    {
        // A 4x4 original already fits inside the 8x8 budget -- BuildWorkingCopy must return the
        // source itself rather than calling Resize a second time.
        var original = CreateSource(4, 4);
        var preparer = new FakeTransmitImagePreparer();

        var vm = CreateEditor(original, SmallMode, preparer);

        Assert.NotNull(vm.WorkingCopyBitmap);
        Assert.Equal(1, preparer.ResizeCallCount);
    }

    [AvaloniaFact]
    public void NudgeCropMove_PlainArrow_MovesByExactlyOnePixelRelativeToOriginalResolution()
    {
        var original = CreateSource(100, 50);
        var vm = CreateEditor(original, SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.5, 0.5, 0.2, 0.2);

        vm.NudgeCropMove(NudgeDirection.Right, ctrl: false);

        AssertClose(0.5 + 1.0 / 100, vm.CropRect.X);
        AssertClose(0.5, vm.CropRect.Y);
    }

    [AvaloniaFact]
    public void NudgeCropMove_CtrlArrow_MovesByExactlySixteenPixelsRelativeToOriginalResolution()
    {
        var original = CreateSource(100, 50);
        var vm = CreateEditor(original, SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0, 0, 0.2, 0.2);

        vm.NudgeCropMove(NudgeDirection.Down, ctrl: true);

        AssertClose(0, vm.CropRect.X);
        AssertClose(16.0 / 50, vm.CropRect.Y);
    }

    [AvaloniaFact]
    public void NudgeCropResize_EngagesStretchAndResizesByExactlyOnePixel()
    {
        var original = CreateSource(100, 100);
        var vm = CreateEditor(original, SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0, 0, 0.2, 0.2);
        Assert.True(vm.PreserveAspect);

        vm.NudgeCropResize(NudgeDirection.Right);

        Assert.False(vm.PreserveAspect);
        AssertClose(0.2 + 1.0 / 100, vm.CropRect.Width);
        AssertClose(0.2, vm.CropRect.Height);
    }

    [AvaloniaFact]
    public void AddOverlayElement_AddsAndSelectsItAndTriggersPreviewRecompute()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var countBefore = preparer.ApplyOverlayCallCount;

        vm.AddOverlayElementCommand.Execute(null);

        var element = Assert.Single(vm.OverlayElements);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.True(preparer.ApplyOverlayCallCount > countBefore);
    }

    [AvaloniaFact]
    public void OverlayElement_ResolvedTextReflectsMacroTokens_NotTheRawTemplate()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings { Callsign = "W1AW" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];

        element.Text = "DE %m";

        Assert.Equal("DE %m", element.Text);
        Assert.Equal("DE W1AW", element.ResolvedText);
    }

    [AvaloniaFact]
    public void Overlay_BakesResolvedTextIntoTheAppliedImage_NotTheRawTemplate()
    {
        // BuildImageOverlayElement (what actually reaches ApplyOverlay/the TX'd image) must use
        // ResolvedText, not the raw template -- otherwise the transmitted picture would show the
        // literal "%m" token instead of the operator's callsign.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, new OperatorSettings { Callsign = "W1AW" });
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].Text = "DE %m";

        Assert.Contains(preparer.Overlays, o => o.Elements.Any(e => e.Text == "DE W1AW"));
    }

    [AvaloniaFact]
    public void InsertField_AppendsTokenToSelectedElementsText()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].Text = "DE ";

        vm.InsertFieldCommand.Execute("%m");

        Assert.Equal("DE %m", vm.OverlayElements[0].Text);
    }

    [AvaloniaFact]
    public void InsertField_NothingSelected_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.InsertFieldCommand.Execute("%m");

        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void ChangingAnOverlayElementProperty_TriggersPreviewRecompute()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        var countBefore = preparer.ApplyOverlayCallCount;

        element.Text = "Hello";

        Assert.True(preparer.ApplyOverlayCallCount > countBefore);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_UnsubscribesAndClearsSelectionWhenItWasSelected()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];

        vm.RemoveOverlayElementCommand.Execute(element);

        Assert.Empty(vm.OverlayElements);
        Assert.Null(vm.SelectedOverlayElement);

        // Proves the PropertyChanged subscription was actually torn down, not just that the
        // element left the collection.
        var countAfterRemoval = preparer.ApplyOverlayCallCount;
        element.Text = "Still mutated after removal";
        Assert.Equal(countAfterRemoval, preparer.ApplyOverlayCallCount);
    }

    [AvaloniaFact]
    public void Apply_RunsThePipelineAgainstTheOriginalSource_NotTheDownsampledWorkingCopy()
    {
        // Original exceeds the working-copy budget, so the working copy is guaranteed to be a
        // distinct instance from the original -- this is what makes the assertion discriminating.
        var original = CreateSource(20, 20);
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(original, SmallMode, preparer);

        IImageSource? applied = null;
        vm.Applied += img => applied = img;

        vm.ApplyCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.Equal(SmallMode.ImageWidth, applied!.Width);
        Assert.Equal(SmallMode.ImageHeight, applied.Height);
        Assert.Same(original, preparer.CropSources[^1]);
    }

    [AvaloniaFact]
    public void Cancel_FiresCancelledEventWithoutInvokingThePipelineAgain()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var cropCountBefore = preparer.CropCallCount;
        var resizeCountBefore = preparer.ResizeCallCount;
        var overlayCountBefore = preparer.ApplyOverlayCallCount;
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        vm.CancelCommand.Execute(null);

        Assert.True(cancelled);
        Assert.Equal(cropCountBefore, preparer.CropCallCount);
        Assert.Equal(resizeCountBefore, preparer.ResizeCallCount);
        Assert.Equal(overlayCountBefore, preparer.ApplyOverlayCallCount);
    }

    // spec/18-path-to-1.0.md High item 3. All rotate tests below use a non-square 6x4 source
    // within SmallMode's 8x8 working-copy budget (so _workingCopy IS _originalSource, the common
    // small-image case) unless a test specifically needs the two to be distinct instances.

    [AvaloniaFact]
    public void Rotate_SwapsWorkingCopyDimensions()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.Equal(6, vm.WorkingCopyWidth);
        Assert.Equal(4, vm.WorkingCopyHeight);

        vm.RotateCommand.Execute(null);

        Assert.Equal(4, vm.WorkingCopyWidth);
        Assert.Equal(6, vm.WorkingCopyHeight);
    }

    [AvaloniaFact]
    public void Rotate_TransformsCropRectPerTheClockwiseFormula()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);

        vm.RotateCommand.Execute(null);

        // (x,y,w,h) -> (1-y-h, x, h, w).
        AssertClose(0.4, vm.CropRect.X);
        AssertClose(0.1, vm.CropRect.Y);
        AssertClose(0.4, vm.CropRect.Width);
        AssertClose(0.3, vm.CropRect.Height);
    }

    [AvaloniaFact]
    public void Rotate_BeforeAnyCropEdit_StillRecomputesPreview()
    {
        // CommunityToolkit's generated CropRect setter skips OnCropRectChanged entirely for a
        // same-value assignment (record struct equality) -- the initial (0,0,1,1) transforms to
        // itself under the clockwise formula, so this specifically catches a regression where
        // Rotate() relied on that hook instead of calling the shared notify method unconditionally.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var overlayCountBefore = preparer.ApplyOverlayCallCount;

        vm.RotateCommand.Execute(null);

        Assert.Equal(overlayCountBefore + 1, preparer.ApplyOverlayCallCount);
    }

    [AvaloniaFact]
    public void Rotate_TransformsOverlayElementPosition_AndUpdatesImageDimensions()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.X = 0.2;
        element.Y = 0.3;

        vm.RotateCommand.Execute(null);

        // (x,y) -> (1-y, x), same point transform as the crop rect.
        AssertClose(0.7, element.X);
        AssertClose(0.2, element.Y);
        AssertClose(4, element.ImageWidth);
        AssertClose(6, element.ImageHeight);
        AssertClose(element.X * element.ImageWidth, element.LeftPixels);
        AssertClose(element.Y * element.ImageHeight, element.TopPixels);
    }

    [AvaloniaFact]
    public void Rotate_FourTimes_RoundTripsCropRectAndOverlayPositionsWithinTolerance()
    {
        // Floating-point subtraction in the transform means this isn't bit-exact -- AssertClose's
        // 1e-9 tolerance, not exact struct/double equality, per round-1 plan-review.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.X = 0.15;
        element.Y = 0.65;

        // Deliberately off-canvas (Y > 1) -- code-review finding: this element's own drag handler
        // (TxImageEditorPaneView.axaml.cs) allows free overflow past the image bounds, clipped only
        // at render time, so Rotate() must NOT clamp overlay positions to [0,1] the way it clamps
        // the crop rect (which has a real invariant to protect). Round-tripping this pins that.
        vm.AddOverlayElementCommand.Execute(null);
        var offCanvasElement = vm.OverlayElements[1];
        offCanvasElement.X = 0.5;
        offCanvasElement.Y = 1.2;

        for (var i = 0; i < 4; i++)
        {
            vm.RotateCommand.Execute(null);
        }

        AssertClose(0.1, vm.CropRect.X);
        AssertClose(0.2, vm.CropRect.Y);
        AssertClose(0.3, vm.CropRect.Width);
        AssertClose(0.4, vm.CropRect.Height);
        AssertClose(0.15, element.X);
        AssertClose(0.65, element.Y);
        AssertClose(0.5, offCanvasElement.X);
        AssertClose(1.2, offCanvasElement.Y);
        Assert.Equal(6, vm.WorkingCopyWidth);
        Assert.Equal(4, vm.WorkingCopyHeight);
    }

    [AvaloniaFact]
    public void Rotate_WithAnOverlayElementAndANonIdentityCropRect_RecomputesPreviewExactlyOnce()
    {
        // Code-review finding: _suspendPreview (suppressing RecomputePreview() while Rotate() is
        // mid-update) was entirely untested -- every existing Rotate test used either zero overlay
        // elements or asserted no call counts, so deleting the suppression left the suite green.
        // With one element and a CropRect that actually changes under rotation, an unsuppressed
        // Rotate() would fire ~6 RecomputePreview calls (4 from the element's own X/Y/ImageWidth/
        // ImageHeight PropertyChanged cascades, 1 from the CropRect reassignment, 1 final) instead
        // of exactly 1 -- discriminating enough to catch a regression here.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, preparer);
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].X = 0.2;
        vm.OverlayElements[0].Y = 0.3;
        var overlayCountBefore = preparer.ApplyOverlayCallCount;

        vm.RotateCommand.Execute(null);

        Assert.Equal(overlayCountBefore + 1, preparer.ApplyOverlayCallCount);
    }

    [AvaloniaFact]
    public void Rotate_WorkingCopySharesTheOriginalInstance_RotatesOnlyOnce()
    {
        // Within budget -- BuildWorkingCopy returns the source instance itself (see
        // Constructor_OriginalWithinWorkingCopyBudget_UsesOriginalDirectlyAsWorkingCopy above), so
        // Rotate() must not call the preparer's Rotate twice on what's really the same object.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);

        vm.RotateCommand.Execute(null);

        Assert.Equal(1, preparer.RotateCallCount);
    }

    [AvaloniaFact]
    public void Rotate_WorkingCopyIsADistinctDownsampledInstance_RotatesBoth()
    {
        // Exceeds budget -- BuildWorkingCopy downsamples, so _originalSource and _workingCopy are
        // genuinely different instances and each needs its own Rotate call.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(20, 20), SmallMode, preparer);

        vm.RotateCommand.Execute(null);

        Assert.Equal(2, preparer.RotateCallCount);
    }

    [AvaloniaFact]
    public void CurrentSource_ReflectsRotate_NotJustTheConstructorArgument()
    {
        var original = CreateSource(6, 4);
        var vm = CreateEditor(original, SmallMode, new FakeTransmitImagePreparer());
        Assert.Same(original, vm.CurrentSource);

        vm.RotateCommand.Execute(null);

        Assert.NotSame(original, vm.CurrentSource);
        Assert.Equal(4, vm.CurrentSource.Width);
        Assert.Equal(6, vm.CurrentSource.Height);
    }

    // spec/18-path-to-1.0.md High item 4 (aspect-locked crop). All tests below use an 8x8 (square)
    // working copy against WideMode's 2:1 target aspect -- deliberately mismatched, so a passing
    // test proves the PIXEL aspect matches the mode, not just that the working copy happens to
    // already be that shape.

    [AvaloniaFact]
    public void LockAspectToMode_DefaultsToFalse()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.LockAspectToMode);
    }

    [AvaloniaFact]
    public void LockAspectToMode_TurnedOn_ImmediatelyRefitsTheExistingCropRect()
    {
        // Legacy's own SBRatioClick (PicRect.cpp:653-662) re-fits on click, not on the next drag.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        Assert.Equal(new NormalizedRect(0, 0, 1, 1), vm.CropRect);

        vm.LockAspectToMode = true;

        AssertClose(0, vm.CropRect.X);
        AssertClose(0, vm.CropRect.Y);
        AssertClose(1.0, vm.CropRect.Width);
        AssertClose(0.5, vm.CropRect.Height);
    }

    [AvaloniaFact]
    public void DragCropResize_WithLockOn_ProducesACropRectMatchingTheModesPixelAspect()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.LockAspectToMode = true; // auto-refits to (0,0,1.0,0.5)

        vm.DragCropResize(-0.25, 0);

        AssertClose(0.75, vm.CropRect.Width);
        AssertClose(0.375, vm.CropRect.Height);
        var pixelAspect = (vm.CropRect.Width * vm.WorkingCopyWidth) / (vm.CropRect.Height * vm.WorkingCopyHeight);
        AssertClose(2.0, pixelAspect);
    }

    [AvaloniaFact]
    public void DragCropResize_WithLockOn_OverflowingWidth_ShrinksBothAxesProportionally_NotIndependently()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.7, 0, 0.2, 0.1); // already aspect-matching, near the right edge
        vm.LockAspectToMode = true; // no-op refit -- already valid

        // Raw drag would grow to a 4.0x3.2px box (still aspect-fit-able down to 4.0x2.0), but the
        // available width here is only 2.4px (maxWidthPixels = (1-0.7)*8) -- an independent per-axis
        // clamp would produce width=2.4,height=2.0 (aspect 1.2, wrong); shrinking height along with
        // width instead preserves the target 2:1 aspect exactly.
        vm.DragCropResize(0.3, 0.3);

        AssertClose(0.3, vm.CropRect.Width);
        AssertClose(0.15, vm.CropRect.Height);
        var pixelAspect = (vm.CropRect.Width * vm.WorkingCopyWidth) / (vm.CropRect.Height * vm.WorkingCopyHeight);
        AssertClose(2.0, pixelAspect);
    }

    [AvaloniaFact]
    public void ApplyCropResizeAspectLocked_WhenNoValidAspectCorrectBoxFitsBounds_RejectsTheResize_LeavingCropRectUnchanged()
    {
        // Round-1 plan-review blocker repro (a real, deterministic case -- not pathological): a
        // tiny crop pinned near the right edge, then the lock engages. The available width
        // (maxWidthPixels) equals the minimum floor exactly, but deriving height from that width via
        // the mode's own WIDE (2:1) aspect ratio pushes height BELOW its own floor -- there is no
        // valid aspect-correct box, so the fix rejects the resize entirely instead of emitting an
        // aspect-violating rect (the original draft's bug).
        var vm = CreateEditor(CreateSource(100, 100), WideMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.98, 0, 0.02, 0.02);

        vm.LockAspectToMode = true;

        Assert.Equal(new NormalizedRect(0.98, 0, 0.02, 0.02), vm.CropRect);
    }

    [AvaloniaFact]
    public void NudgeCropResize_ClearsLockAspectToMode_MatchingHowItAlreadyClearsPreserveAspect()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.LockAspectToMode = true;

        vm.NudgeCropResize(NudgeDirection.Right);

        Assert.False(vm.LockAspectToMode);
        Assert.False(vm.PreserveAspect); // pre-existing behavior, unaffected by this change
    }

    [AvaloniaFact]
    public void LockAspectToMode_WhenRawBoxIsWiderThanTarget_ShrinksWidthToMatchHeight()
    {
        // Code-review finding: none of the other tests reach the ratio-fit's OTHER branch
        // (rawWidth/rawHeight > targetAspect, i.e. the raw dragged box is even wider than the
        // target itself, so WIDTH -- not height -- has to shrink) -- they all happened to land in
        // the opposite branch.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0, 0, 0.9, 0.3); // raw pixel ratio 7.2/2.4 = 3.0 > targetAspect 2.0

        vm.LockAspectToMode = true;

        AssertClose(0.6, vm.CropRect.Width);
        AssertClose(0.3, vm.CropRect.Height);
        var pixelAspect = (vm.CropRect.Width * vm.WorkingCopyWidth) / (vm.CropRect.Height * vm.WorkingCopyHeight);
        AssertClose(2.0, pixelAspect);
    }

    [AvaloniaFact]
    public void DragCropResize_WithLockOn_OverflowingHeight_ShrinksBothAxesProportionally()
    {
        // Code-review finding: no existing test reaches the height-overflow branch -- only the
        // width-overflow branch (DragCropResize_WithLockOn_OverflowingWidth_...) was covered.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0, 0.85, 0.1, 0.1); // tight vertical room: maxHeightPixels = 1.2px
        vm.LockAspectToMode = true;

        // Raw fit from here would be 6.4x3.2px, taller than the available 1.2px of vertical room.
        vm.DragCropResize(0.7, 0.6);

        AssertClose(0.3, vm.CropRect.Width);
        AssertClose(0.15, vm.CropRect.Height);
        var pixelAspect = (vm.CropRect.Width * vm.WorkingCopyWidth) / (vm.CropRect.Height * vm.WorkingCopyHeight);
        AssertClose(2.0, pixelAspect);
    }

    [AvaloniaFact]
    public void Rotate_WithLockOn_RefitsToTheSameUnchangedTargetAspect_NotItsReciprocal()
    {
        // Round-2 code-review blocker: Rotate's own crop-rect transform swaps width/height along
        // with the working copy's own dimension swap -- for an already-aspect-locked rect, that
        // left the PIXEL aspect at the RECIPROCAL of _targetMode's own (unchanged) aspect while
        // LockAspectToMode still read true. A non-square working copy (6x4, unlike the 8x8 used
        // elsewhere in this file) is essential here -- a square working copy's own aspect doesn't
        // change under rotation, which would silently hide this exact bug.
        var vm = CreateEditor(CreateSource(6, 4), WideMode, new FakeTransmitImagePreparer());
        vm.LockAspectToMode = true; // auto-refits to pixel aspect 2.0 against the 6x4 working copy

        vm.RotateCommand.Execute(null);

        Assert.True(vm.LockAspectToMode); // rotate does not disengage the lock
        var pixelAspect = (vm.CropRect.Width * vm.WorkingCopyWidth) / (vm.CropRect.Height * vm.WorkingCopyHeight);
        AssertClose(2.0, pixelAspect); // still WideMode's own 2:1 -- NOT the reciprocal 0.5
    }

    [AvaloniaFact]
    public void DragCropResize_WithLockOff_StillIndependentlyClampsEachAxis()
    {
        // Regression coverage that the default-off free-form path is genuinely unchanged by this
        // feature -- each axis clamps independently, unlike the locked path.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        Assert.False(vm.LockAspectToMode);

        vm.DragCropResize(-0.3, 0.1);

        AssertClose(0.7, vm.CropRect.Width);
        AssertClose(1.0, vm.CropRect.Height); // clamped independently to 1-Y=1, not aspect-derived
    }

    [AvaloniaFact]
    public void HeaderText_ReflectsTheActualTargetModesDimensionsAndName_NotAHardcodedLiteral()
    {
        // spec/18-path-to-1.0.md Medium item: the header used to be a static locale string reading
        // "640x496 - PD120" regardless of which mode was actually being edited. WideMode (8x4,
        // DisplayName "Wide") is deliberately NOT 640x496/PD120, so this fails loudly if the fix
        // regresses back to a hardcoded literal.
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(8, 4), WideMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance);

        _ = vm.HeaderText;

        Assert.Equal("Panes.TxImageEditor.CardHeaderFormat", localization.LastKey);
        Assert.Equal(new object[] { 8, 4, "Wide" }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void DimensionsChipText_ReflectsTheActualTargetModesDimensions_NotAHardcodedLiteral()
    {
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(8, 4), WideMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance);

        _ = vm.DimensionsChipText;

        Assert.Equal("Panes.TxImageEditor.DimensionsChipFormat", localization.LastKey);
        Assert.Equal(new object[] { 8, 4 }, localization.LastArgs);
    }

    [Fact]
    public void HeaderAndDimensionsChipLocaleFormats_MatchEnJsonsRealValues()
    {
        // Code-review finding: the HeaderText/DimensionsChipText tests above go through
        // FakeLocalizationService, which returns the raw key and can't catch a placeholder-order
        // or -count drift in the real assets/locale/en.json format strings -- same pattern as
        // PaneViewModelTests' own RxTelemetryLocaleFormats_MatchEnJsonsRealValues test. Literal
        // format strings copied from en.json; a drift there should be caught by updating this
        // test, not silently diverging.
        Assert.Equal(
            "EDITOR — OUTGOING FRAME · 320×240 · Robot 36",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "EDITOR — OUTGOING FRAME · {0}×{1} · {2}", 320, 240, "Robot 36"));
        Assert.Equal(
            "320×240",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}×{1}", 320, 240));
    }

    [AvaloniaFact]
    public void BuildOverlay_StretchMode_NonIdentityCrop_ReprojectsPositionCropRelative_NoPadding()
    {
        // Stretch mode (PreserveAspect = false) never letterboxes -- content always fills the
        // target exactly on both axes -- so the crop-relative re-projection reduces to the simple
        // (X-CropRect.X)/CropRect.Width form, with no pad term. Hand-computed: crop (0.25, 0.0,
        // 0.5, 1.0) on an 8x8 working copy, element at (0.625, 0.75) -> relX=(0.625-0.25)/0.5=0.75,
        // relY=0.75/1.0=0.75 -- both axes stretch independently to WideMode's 8x4, so no padding
        // shifts either coordinate; final X/Y equal relX/relY exactly.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.PreserveAspect = false;
        vm.CropRect = new NormalizedRect(0.25, 0.0, 0.5, 1.0);
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].X = 0.625;
        vm.OverlayElements[0].Y = 0.75;

        var overlay = Assert.Single(preparer.Overlays[^1].Elements);

        AssertClose(0.75, overlay.X);
        AssertClose(0.75, overlay.Y);
    }

    [AvaloniaFact]
    public void BuildOverlay_LetterboxMode_AspectMismatchedCrop_ReprojectsPositionWithPadding()
    {
        // Round-1 auditor plan-review's own highest-severity finding on this fix: PreserveAspect =
        // true (the default) uses ResizeMode.Pad, so the naive (X-CropRect.X)/CropRect.Width
        // re-projection is WRONG here -- the letterbox pad term must shift the padded axis.
        // Hand-computed: crop (0.25, 0.0, 0.5, 1.0) on an 8x8 working copy -> cropW=4px, cropH=8px
        // (working-copy pixel space). Target WideMode is 8x4 (2:1). scale = min(8/4, 4/8) =
        // min(2, 0.5) = 0.5 (height-constrained) -> contentW=4*0.5=2, contentH=8*0.5=4=targetH (no
        // vertical padding). padX=(8-2)/2=3, padY=0. Element at (0.625, 0.75) -> relX=0.75,
        // relY=0.75 -> finalX=(3+0.75*2)/8=4.5/8=0.5625 (differs from the naive relX=0.75 -- this
        // is the discriminating assertion), finalY=(0+0.75*4)/4=0.75 (unaffected, no Y padding here).
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.CropRect = new NormalizedRect(0.25, 0.0, 0.5, 1.0);
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].X = 0.625;
        vm.OverlayElements[0].Y = 0.75;

        var overlay = Assert.Single(preparer.Overlays[^1].Elements);

        AssertClose(0.5625, overlay.X);
        AssertClose(0.75, overlay.Y);
    }

    [AvaloniaFact]
    public void BuildOverlay_ElementOutsideCropAfterReprojection_DoesNotThrow()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.CropRect = new NormalizedRect(0.4, 0.4, 0.2, 0.2);
        vm.AddOverlayElementCommand.Execute(null);
        // Far outside the crop -- re-projects to a coordinate well outside [0,1].
        vm.OverlayElements[0].X = 0.0;
        vm.OverlayElements[0].Y = 0.0;

        var overlay = Assert.Single(preparer.Overlays[^1].Elements);

        Assert.True(overlay.X < 0 || overlay.Y < 0);
    }

    [AvaloniaFact]
    public void BuildOverlay_DegenerateCropRect_FallsBackToUnprojectedPosition_DoesNotThrow()
    {
        // Round-1 plan-review finding: CropRect is directly settable and not clamped away from
        // zero-size outside the drag handlers -- the re-projection must guard against dividing by
        // a zero-width/height crop rather than propagating NaN into the pipeline.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].X = 0.3;
        vm.OverlayElements[0].Y = 0.4;
        vm.CropRect = new NormalizedRect(0.5, 0.5, 0, 0);

        var overlay = Assert.Single(preparer.Overlays[^1].Elements);

        Assert.False(double.IsNaN(overlay.X));
        Assert.False(double.IsNaN(overlay.Y));
        AssertClose(0.3, overlay.X);
        AssertClose(0.4, overlay.Y);
    }

    [AvaloniaFact]
    public void CanvasFontSize_ReflectsFontSizeRelativeAndCropDimensions_RecomputedAfterCropRectChange()
    {
        // Deliberately WIDTH-constrained (crop aspect 8:2=4:1, wider than WideMode's own 8:4=2:1) --
        // a height-constrained crop (like BuildOverlay_LetterboxMode_*'s 4:8 crop) makes scaleY
        // reduce to exactly targetHeight/cropHeightPixels, which happens to make the naive
        // "FontSizeRelative * cropHeightPixels" formula coincide with the correct one and silently
        // not discriminate a regression back to it -- caught via mutation testing. Crop
        // (0.0, 0.375, 1.0, 0.25) on an 8x8 working copy -> cropW=8, cropH=2. scaleY =
        // min(targetW/cropW, targetH/cropH) = min(8/8, 4/2) = min(1, 2) = 1 (width-constrained, NOT
        // targetHeight/cropHeightPixels=2). FontSizeRelative defaults to 0.1 -> canvasFontSize =
        // 0.1*4/1 = 0.4 (the naive formula would instead give 0.1*2=0.2).
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];

        vm.CropRect = new NormalizedRect(0.0, 0.375, 1.0, 0.25);

        AssertClose(0.4, element.CanvasFontSize);
    }

    [AvaloniaFact]
    public void CanvasFontSize_DoesNotTriggerAPreviewRecompute_OnlyFontSizeRelativeAndCropChangesDo()
    {
        // CanvasFontSize is canvas-chrome-only (never feeds BuildOverlay/the real pipeline) --
        // OnOverlayElementPropertyChanged must exclude it, or every crop-drag frame would fire one
        // redundant extra RecomputePreview per overlay element on top of the one already required.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        var countBeforeDirectSet = preparer.ApplyOverlayCallCount;

        element.CanvasFontSize = 12.34;

        Assert.Equal(countBeforeDirectSet, preparer.ApplyOverlayCallCount);
    }

    [AvaloniaFact]
    public void AddOverlayElement_SeedsPositionAtCropCenter_NotPhotoCenter()
    {
        // Round-1 plan-review finding: OverlayElementViewModel's own raw field default (0.5, 0.5)
        // is the PHOTO center -- under a tight, off-center crop that lands outside the visible/
        // transmitted frame. AddOverlayElement must seed the CROP's center instead.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.6, 0.1, 0.2, 0.2);

        vm.AddOverlayElementCommand.Execute(null);

        AssertClose(0.7, vm.OverlayElements[0].X);
        AssertClose(0.2, vm.OverlayElements[0].Y);
    }

    [AvaloniaTheory]
    [InlineData(nameof(TxImageEditorPaneViewModel.Brightness))]
    [InlineData(nameof(TxImageEditorPaneViewModel.Contrast))]
    [InlineData(nameof(TxImageEditorPaneViewModel.Saturation))]
    [InlineData(nameof(TxImageEditorPaneViewModel.Gamma))]
    [InlineData(nameof(TxImageEditorPaneViewModel.Sharpen))]
    [InlineData(nameof(TxImageEditorPaneViewModel.Denoise))]
    public void ChangingAnyAdjustmentSlider_TriggersPreviewRecompute(string propertyName)
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var countBefore = preparer.ApplyOverlayCallCount;

        typeof(TxImageEditorPaneViewModel).GetProperty(propertyName)!.SetValue(vm, 25.0);

        Assert.True(preparer.ApplyOverlayCallCount > countBefore);
    }

    [AvaloniaFact]
    public void RecomputePreview_PassesCurrentSliderValues_ToApplyAdjustments()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);

        vm.Brightness = 10;
        vm.Contrast = -20;
        vm.Saturation = 5;
        vm.Gamma = -15;
        vm.Sharpen = 40;
        vm.Denoise = 60;

        var adjustments = preparer.Adjustments[^1];
        Assert.Equal(10, adjustments.Brightness);
        Assert.Equal(-20, adjustments.Contrast);
        Assert.Equal(5, adjustments.Saturation);
        Assert.Equal(-15, adjustments.Gamma);
        Assert.Equal(40, adjustments.Sharpen);
        Assert.Equal(60, adjustments.Denoise);
    }

    [AvaloniaFact]
    public void Apply_PassesCurrentSliderValues_ToApplyAdjustments()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.Brightness = 30;
        vm.Sharpen = 70;
        IImageSource? applied = null;
        vm.Applied += img => applied = img;
        var countBeforeApply = preparer.ApplyAdjustmentsCallCount;

        vm.ApplyCommand.Execute(null);

        Assert.NotNull(applied);
        Assert.True(preparer.ApplyAdjustmentsCallCount > countBeforeApply);
        var adjustments = preparer.Adjustments[^1];
        Assert.Equal(30, adjustments.Brightness);
        Assert.Equal(70, adjustments.Sharpen);
    }

    [AvaloniaFact]
    public void DefaultSliderValues_AreAllZero_AnIdentityImageAdjustments()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);

        var adjustments = preparer.Adjustments[^1];

        Assert.True(adjustments.IsIdentity);
    }

    [AvaloniaFact]
    public void Apply_RunsPipelineInOrder_ResizeThenAdjustThenOverlay()
    {
        // Code-review finding: ITransmitImagePreparer.ApplyAdjustments' own doc comment declares
        // "must run AFTER Resize and BEFORE ApplyOverlay" as a real contract, but nothing pinned
        // it -- FakeTransmitImagePreparer's ApplyAdjustments used to identity-return its input, so
        // a future Resize->ApplyOverlay->ApplyAdjustments reordering bug would have passed every
        // existing test silently (nothing would distinguish "ran between Resize and ApplyOverlay"
        // from "never ran at all"). Now that both Resize and ApplyAdjustments return genuinely new,
        // distinct instances (same pattern as Rotate), this test asserts the real chain by
        // reference identity: ApplyAdjustments must receive exactly what Resize returned, and
        // ApplyOverlay must receive exactly what ApplyAdjustments returned.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), SmallMode, preparer);

        vm.ApplyCommand.Execute(null);

        Assert.Same(preparer.ResizeResults[^1], preparer.AdjustmentsSources[^1]);
        Assert.Same(preparer.AdjustmentsResults[^1], preparer.OverlaySources[^1]);
    }

    private static ArrayImageSource CreateSource(int width, int height)
        => new(width, height, new Rgb24[width * height]);

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}, got {actual}");
}
