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

    // Real MacroTextResolver + blank OperatorSettings -- none of these tests exercise macro
    // resolution itself (that's MacroTextResolverTests' job), so a real-but-inert resolver is
    // simpler than a fake with nothing to configure.
    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer) =>
        CreateEditor(original, mode, preparer, new OperatorSettings());

    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer, OperatorSettings operatorSettings) =>
        new(original, mode, preparer, new MacroTextResolver(), operatorSettings, NullLogger<TxImageEditorPaneViewModel>.Instance);

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
        // ToImageOverlayElement (what actually reaches ApplyOverlay/the TX'd image) must use
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

    private static ArrayImageSource CreateSource(int width, int height)
        => new(width, height, new Rgb24[width * height]);

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}, got {actual}");
}
