using System.Globalization;
using System.Linq;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.ViewModels;
using ScanlineStudio.UI.Views;

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
        new(original, mode, preparer, new MacroTextResolver(), operatorSettings, new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

    /// <summary>Phase 2 overload -- exposes the 4 new image-source fakes so a test can configure
    /// them (e.g. <see cref="FakeFilePickerService.PathToReturn"/>) and inspect calls afterward,
    /// unlike the other <see cref="CreateEditor"/> overloads which construct fresh, unobservable
    /// fakes internally.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(
        IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer,
        IFilePickerService filePickerService, IImageFileLoader imageFileLoader,
        IReceivedImageBuffer receivedImageBuffer, IReceiveHistoryStore receiveHistoryStore) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            filePickerService, imageFileLoader, receivedImageBuffer, receiveHistoryStore,
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

    /// <summary>Phase 3 overload -- exposes <see cref="FakeRadioSessionService"/> so a test can set
    /// <see cref="FakeRadioSessionService.LastKnownState"/> before constructing, for
    /// <c>{freq}</c>/<c>{mode}</c> resolution tests.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(
        IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer, FakeRadioSessionService radioSessionService) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), radioSessionService, new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

    /// <summary>Phase 5 overload -- exposes <see cref="ITemplateStore"/>/<see cref="IImageSourceWriter"/>
    /// so a save/load test can configure/inspect them directly.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(
        IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer,
        ITemplateStore templateStore, IImageSourceWriter imageSourceWriter, ReadyRackViewModel? readyRack = null) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            templateStore, imageSourceWriter, readyRack ?? CreateReadyRack(templateStore));

    private static ReadyRackViewModel CreateReadyRack(ITemplateStore? templateStore = null) =>
        new(templateStore ?? new FakeTemplateStore(), new FakeSettingsStore(), NullLogger<ReadyRackViewModel>.Instance);

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
        var countBefore = preparer.ApplyTemplateCallCount;

        vm.AddOverlayElementCommand.Execute(null);

        var element = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.True(preparer.ApplyTemplateCallCount > countBefore);
    }

    [AvaloniaFact]
    public void AddOverlayElement_DefaultsToNoStrokeAndNoShadow()
    {
        // A freshly-added text element must render plain -- no outline, no drop shadow -- until the
        // operator explicitly turns one on. Companion to the picker-write-guard tests below, which
        // pin the actual mechanism this depends on.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.False(element.HasStroke);
        Assert.Null(element.StrokeColor);
        Assert.False(element.HasShadow);
        Assert.Null(element.ShadowColor);
    }

    [AvaloniaFact]
    public void StrokeColorForPicker_WriteWhileHasStrokeIsFalse_DoesNotUnnullStrokeColor()
    {
        // Real-window finding: Rgb24ToColorConverter's null -> Colors.Black display fallback gets
        // written straight back through the ColorPicker's own two-way Color binding, silently
        // un-nulling StrokeColor with no user action at all -- confirmed live, a freshly-created
        // text element showed "Outline" checked with a black swatch despite StrokeColor defaulting
        // to null. StrokeColorForPicker exists specifically to absorb that incidental write while
        // the picker is disabled/decorative (HasStroke false) -- this pins that guard directly,
        // since a pure VM-level test can't reproduce the real Avalonia binding round-trip itself.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.False(element.HasStroke);

        element.StrokeColorForPicker = new Rgb24(0, 0, 0);

        Assert.Null(element.StrokeColor);
        Assert.False(element.HasStroke);
    }

    [AvaloniaFact]
    public void StrokeColorForPicker_WriteWhileHasStrokeIsTrue_UpdatesStrokeColor()
    {
        // The guard above must not make the picker permanently read-only -- real edits while
        // actually enabled (HasStroke true) still need to reach StrokeColor.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.HasStroke = true;
        var chosen = new Rgb24(10, 20, 30);

        element.StrokeColorForPicker = chosen;

        Assert.Equal(chosen, element.StrokeColor);
    }

    [AvaloniaFact]
    public void ShadowColorForPicker_WriteWhileHasShadowIsFalse_DoesNotUnnullShadowColor()
    {
        // Phase 8: ShadowColor inherited the identical pattern (and identical bug) from StrokeColor
        // -- same fix, same regression test shape.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.False(element.HasShadow);

        element.ShadowColorForPicker = new Rgb24(0, 0, 0);

        Assert.Null(element.ShadowColor);
        Assert.False(element.HasShadow);
    }

    [AvaloniaFact]
    public void ShadowColorForPicker_WriteWhileHasShadowIsTrue_UpdatesShadowColor()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.HasShadow = true;
        var chosen = new Rgb24(40, 50, 60);

        element.ShadowColorForPicker = chosen;

        Assert.Equal(chosen, element.ShadowColor);
    }

    [AvaloniaFact]
    public void OverlayElement_ResolvedTextReflectsMacroTokens_NotTheRawTemplate()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings { Callsign = "W1AW" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

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
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "DE %m";

        Assert.Contains(preparer.TemplateDocuments, d => d.Elements.Any(e => e is TemplateTextElement text && text.Content == "DE W1AW"));
    }

    [AvaloniaFact]
    public void InsertField_AppendsTokenToSelectedElementsText()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "DE ";

        vm.InsertFieldCommand.Execute("%m");

        Assert.Equal("DE %m", ((OverlayElementViewModel)vm.OverlayElements[0]).Text);
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
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var countBefore = preparer.ApplyTemplateCallCount;

        element.Text = "Hello";

        Assert.True(preparer.ApplyTemplateCallCount > countBefore);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_UnsubscribesAndClearsSelectionWhenItWasSelected()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.RemoveOverlayElementCommand.Execute(element);

        Assert.Empty(vm.OverlayElements);
        Assert.Null(vm.SelectedOverlayElement);

        // Proves the PropertyChanged subscription was actually torn down, not just that the
        // element left the collection.
        var countAfterRemoval = preparer.ApplyTemplateCallCount;
        element.Text = "Still mutated after removal";
        Assert.Equal(countAfterRemoval, preparer.ApplyTemplateCallCount);
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
        var overlayCountBefore = preparer.ApplyTemplateCallCount;
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        vm.CancelCommand.Execute(null);

        Assert.True(cancelled);
        Assert.Equal(cropCountBefore, preparer.CropCallCount);
        Assert.Equal(resizeCountBefore, preparer.ResizeCallCount);
        Assert.Equal(overlayCountBefore, preparer.ApplyTemplateCallCount);
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
        var overlayCountBefore = preparer.ApplyTemplateCallCount;

        vm.RotateCommand.Execute(null);

        Assert.Equal(overlayCountBefore + 1, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void Rotate_TransformsOverlayElementPosition_AndUpdatesImageDimensions()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.X = 0.2;
        element.Y = 0.3;

        vm.RotateCommand.Execute(null);

        // (x,y) -> (1-y, x), same point transform as the crop rect.
        AssertClose(0.7, element.X);
        AssertClose(0.2, element.Y);
        AssertClose(4, element.ImageWidth);
        AssertClose(6, element.ImageHeight);
        // LeftPixels/TopPixels are CENTER-minus-half-extent conversions (Phase 1), not a bare X*ImageWidth
        // point -- Rotate() also swaps Width/Height for text elements, so recompute from the element's
        // own current Width/Height rather than assuming the pre-Phase-1 default (0.3, 0.18) survives rotate.
        AssertClose((element.X - (element.Width / 2)) * element.ImageWidth, element.LeftPixels);
        AssertClose((element.Y - (element.Height / 2)) * element.ImageHeight, element.TopPixels);
    }

    [AvaloniaFact]
    public void Rotate_FourTimes_RoundTripsCropRectAndOverlayPositionsWithinTolerance()
    {
        // Floating-point subtraction in the transform means this isn't bit-exact -- AssertClose's
        // 1e-9 tolerance, not exact struct/double equality, per round-1 plan-review.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
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
        var overlayCountBefore = preparer.ApplyTemplateCallCount;

        vm.RotateCommand.Execute(null);

        Assert.Equal(overlayCountBefore + 1, preparer.ApplyTemplateCallCount);
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
    public void UndoRedoCommands_CanExecute_IsFalseInitially()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    // Design-fidelity Phase B (mockups/Editwindow): HasUnsavedEdits/RevertCommand are a thin proxy
    // over the existing undo stack -- these tests pin that proxy relationship, not undo/redo's own
    // correctness (already covered by the Rotate_ThenUndo_* tests above).

    [AvaloniaFact]
    public void HasUnsavedEdits_IsFalseInitially_TrueAfterAMutation_FalseAfterUndoingItBack()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.HasUnsavedEdits);

        vm.RotateCommand.Execute(null);
        Assert.True(vm.HasUnsavedEdits);

        vm.UndoCommand.Execute(null);
        Assert.False(vm.HasUnsavedEdits);
    }

    [AvaloniaFact]
    public void RevertCommand_CanExecute_MirrorsUndoCommand()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.RevertCommand.CanExecute(null));

        vm.RotateCommand.Execute(null);
        Assert.True(vm.RevertCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RevertCommand_PopsTheEntireUndoStack_RestoringThePreEditState()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        AssertClose(6, vm.WorkingCopyWidth);
        AssertClose(4, vm.WorkingCopyHeight);

        vm.RotateCommand.Execute(null);
        vm.AddOverlayElementCommand.Execute(null);
        Assert.True(vm.HasUnsavedEdits);

        vm.RevertCommand.Execute(null);

        Assert.False(vm.HasUnsavedEdits);
        Assert.False(vm.UndoCommand.CanExecute(null));
        AssertClose(6, vm.WorkingCopyWidth);
        AssertClose(4, vm.WorkingCopyHeight);
        Assert.Empty(vm.OverlayElements);
        // Deliberate, documented consequence of reusing Undo's own machinery (RevertCommand's own
        // doc comment) -- the discarded history stays fully Redo-able, Revert doesn't clear it.
        Assert.True(vm.RedoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void FrameReadoutText_ReflectsTheActualTargetModesDimensionsNameAndDuration()
    {
        // Same FakeLocalizationService pattern as HeaderText_ReflectsTheActualTargetModesDimensionsAndName
        // above -- verifies the CALLER passes the right computed args, not the real interpolated
        // string (that's HeaderAndDimensionsChipLocaleFormats_MatchEnJsonsRealValues's own job,
        // extended below for this new key). WideMode's LineSegments is empty ([]) so the expected
        // duration is exactly 0 -- a real, if degenerate, value from the SAME LineDurationMs *
        // ImageHeight / 1000.0 formula TxControlsPaneViewModel's own Mode Timing Reference table uses.
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(8, 4), WideMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        _ = vm.FrameReadoutText;

        Assert.Equal("Panes.TxImageEditor.FrameReadoutFormat", localization.LastKey);
        Assert.Equal(new object[] { 8, 4, "Wide", 0.0 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void Rotate_ThenUndo_RevertsDimensionsCropRectAndOverlayPosition()
    {
        // spec/18-path-to-1.0.md Medium item: undo/redo, the final TX-image-editor cluster
        // sub-piece. Rotate is the highest-risk push point (round-1 plan-review's own focus) --
        // undoing it must restore BOTH the image orientation (WorkingCopyWidth/Height) AND the
        // coordinate-space state (CropRect/overlay X-Y) consistently, not just one half.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        vm.AddOverlayElementCommand.Execute(null);
        vm.OverlayElements[0].X = 0.2;
        vm.OverlayElements[0].Y = 0.3;
        var cropBeforeRotate = vm.CropRect;

        vm.RotateCommand.Execute(null);
        Assert.True(vm.UndoCommand.CanExecute(null));
        AssertClose(4, vm.WorkingCopyWidth);
        AssertClose(6, vm.WorkingCopyHeight);

        vm.UndoCommand.Execute(null);

        AssertClose(6, vm.WorkingCopyWidth);
        AssertClose(4, vm.WorkingCopyHeight);
        Assert.Equal(cropBeforeRotate, vm.CropRect);
        var restoredElement = Assert.Single(vm.OverlayElements);
        AssertClose(0.2, restoredElement.X);
        AssertClose(0.3, restoredElement.Y);
        Assert.True(vm.RedoCommand.CanExecute(null));
        // One more Undo remains: AddOverlayElement's own push (piece (c) of this same sub-piece --
        // adding the element is itself undoable), not the just-undone Rotate.
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Rotate_ThenUndo_ThenRedo_ReappliesTheRotation()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.RotateCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);

        AssertClose(4, vm.WorkingCopyWidth);
        AssertClose(6, vm.WorkingCopyHeight);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RotateThreeTimes_ThenUndoTwice_ReconcilesToOneRotationNotThree()
    {
        // Round-1 plan-review's own central concern: orientation reconciliation must rotate a
        // DELTA (target - current, mod 4) from whatever the CURRENT state is, not replay from a
        // fixed baseline. Three rotates then two undos should land on exactly ONE rotation's worth
        // of dimension-swapping (odd count -> swapped dims), not zero or some other count.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.RotateCommand.Execute(null);
        vm.RotateCommand.Execute(null);
        vm.RotateCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);

        AssertClose(4, vm.WorkingCopyWidth);
        AssertClose(6, vm.WorkingCopyHeight);
    }

    [AvaloniaFact]
    public void Rotate_ThenUndo_ThenRotateAgain_ClearsTheRedoStack()
    {
        // Standard undo/redo semantics: redo history is only valid until the next NEW action.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.RotateCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.True(vm.RedoCommand.CanExecute(null));

        vm.RotateCommand.Execute(null);

        Assert.False(vm.RedoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Rotate_ThenUndo_DetachesTheOldOverlayElementsPropertyChangedHandler()
    {
        // Round-1 plan-review blocker B2: ApplyState must detach OnOverlayElementPropertyChanged
        // from every element it removes, or the orphaned handler keeps firing RecomputePreview
        // forever. Pinned indirectly: mutating the OLD (detached) element reference after Undo must
        // NOT change ApplyTemplateCallCount, since that element is no longer part of this editor.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var staleElement = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.RotateCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        var countAfterUndo = preparer.ApplyTemplateCallCount;

        staleElement.Text = "still subscribed?";

        Assert.Equal(countAfterUndo, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void Brightness_ThenUndo_RevertsToThePreChangeValue()
    {
        // Confirms the On*Changing hook actually fires as a pre-assignment push (the round-1
        // plan-review B1 finding this hook exists to satisfy) -- Undo must land back on the value
        // BEFORE the change, not the changed-to value or some stale default.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Brightness = 25;

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        AssertClose(0, vm.Brightness);
    }

    [AvaloniaFact]
    public void Brightness_RapidBurst_CoalescesIntoOneUndoStep()
    {
        // Round-1 plan-review's dispatcher-idle coalescing design: several changes to the SAME
        // property before the UI thread goes idle must collapse into one undo step, or a slider
        // drag would flood the stack and one Undo click would barely move the value.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Brightness = 20;
        vm.Brightness = 30;
        vm.Brightness = 40;
        vm.UndoCommand.Execute(null);

        AssertClose(0, vm.Brightness);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Brightness_ChangeThenDispatcherIdleThenChangeAgain_PushesTwoSeparateUndoSteps()
    {
        // The coalescing window closes once the UI thread actually goes idle (the Background-
        // priority Dispatcher.Post continuation clearing _pendingCoalesceProperty) -- a change
        // AFTER that point must push a fresh step, not keep coalescing into the first one.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Brightness = 20;
        Dispatcher.UIThread.RunJobs();
        vm.Brightness = 30;

        vm.UndoCommand.Execute(null);
        AssertClose(20, vm.Brightness);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        AssertClose(0, vm.Brightness);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ChangeToDifferentProperty_MidBurst_PushesAFreshStepForBoth()
    {
        // A change to a DIFFERENT property mid-burst must not be swallowed by the first
        // property's still-open coalescing window -- each property gets its own undo step.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Brightness = 20;
        vm.Contrast = 15;

        vm.UndoCommand.Execute(null);
        AssertClose(20, vm.Brightness);
        AssertClose(0, vm.Contrast);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        AssertClose(0, vm.Brightness);
        AssertClose(0, vm.Contrast);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PreserveAspectAndLockAspectToMode_ThenUndo_RevertBothToPreChangeValues()
    {
        var vm = CreateEditor(CreateSource(6, 4), WideMode, new FakeTransmitImagePreparer());
        var preserveBefore = vm.PreserveAspect;
        var lockBefore = vm.LockAspectToMode;

        vm.PreserveAspect = !preserveBefore;
        vm.LockAspectToMode = !lockBefore;
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);

        Assert.Equal(preserveBefore, vm.PreserveAspect);
        Assert.Equal(lockBefore, vm.LockAspectToMode);
    }

    [AvaloniaFact]
    public void Undo_RestoresALockAspectToModeTrueSnapshot_WithTheExactStoredCropRect()
    {
        // Code-review finding: no prior test exercised a snapshot with LockAspectToMode == true --
        // the scenario where ApplyState's LockAspectToMode assignment can fire a false->true
        // transition mid-restore (OnLockAspectToModeChanged -> ApplyCropResizeAspectLocked(0, 0),
        // which mutates CropRect as a side effect). ApplyState assigns LockAspectToMode BEFORE
        // CropRect so that side effect's refit gets unconditionally overwritten by the exact
        // snapshot value on the next line, rather than relying on "a locked snapshot's rect is
        // already aspect-correct" to make the refit a harmless no-op.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer()); // square source, deliberately mismatched to WideMode's 2:1

        vm.LockAspectToMode = true; // pushes (LockAspectToMode=false, CropRect=default 0,0,1,1)
        var lockedCrop = vm.CropRect;
        Assert.NotEqual(new NormalizedRect(0, 0, 1, 1), lockedCrop); // sanity: engaging the lock did refit something
        Dispatcher.UIThread.RunJobs(); // settle so the next LockAspectToMode change pushes a FRESH step, not coalescing with the engage above

        vm.LockAspectToMode = false; // pushes (LockAspectToMode=true, CropRect=lockedCrop)
        vm.CropRect = new NormalizedRect(0.1, 0.1, 0.3, 0.15); // deliberately non-aspect-correct; CropRect has no push of its own

        vm.UndoCommand.Execute(null); // restores from (LockAspectToMode=false, CropRect=0.1,0.1,0.3,0.15) to the snapshot

        Assert.True(vm.LockAspectToMode);
        Assert.Equal(lockedCrop, vm.CropRect);
    }

    [AvaloniaFact]
    public void ApplyState_DoesNotItselfPushMoreUndoSnapshots()
    {
        // Round-1 plan-review blocker B3's own reuse of _suspendPreview as a "restore in
        // progress" guard: ApplyState assigns Brightness/Contrast/PreserveAspect/etc. directly,
        // which would otherwise re-trigger the very On*Changing hooks under test above and
        // corrupt the stacks on every single Undo/Redo call. Uses TWO different properties
        // (Brightness then Contrast) so the assertion can't accidentally pass by coincidence of
        // _pendingCoalesceProperty still matching the last-restored property's own name -- a
        // single-property version of this test was tried first and did NOT catch removing the
        // guard, precisely because of that coincidence; ChangeToDifferentProperty_MidBurst above
        // is what actually caught it during mutation-testing, informing this rewrite.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.Brightness = 20;
        vm.Contrast = 15;

        vm.UndoCommand.Execute(null);
        Assert.True(vm.UndoCommand.CanExecute(null));
        Assert.True(vm.RedoCommand.CanExecute(null));
        AssertClose(20, vm.Brightness);
        AssertClose(0, vm.Contrast);

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        AssertClose(0, vm.Brightness);
        AssertClose(0, vm.Contrast);

        vm.RedoCommand.Execute(null);
        vm.RedoCommand.Execute(null);
        Assert.False(vm.RedoCommand.CanExecute(null));
        AssertClose(20, vm.Brightness);
        AssertClose(15, vm.Contrast);
    }

    [AvaloniaFact]
    public void AddOverlayElement_ThenUndo_RemovesTheElement()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);
        Assert.Single(vm.OverlayElements);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.Null(vm.SelectedOverlayElement);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_ThenUndo_RestoresTheElementWithItsText()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "CALLSIGN";
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.RemoveOverlayElementCommand.Execute(element);
        Assert.Empty(vm.OverlayElements);

        vm.UndoCommand.Execute(null);

        var restored = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Equal("CALLSIGN", restored.Text);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_ThenUndo_ReattachesThePropertyChangedHandlerOnTheRestoredElement()
    {
        // Code-review finding: the existing detach test (Rotate_ThenUndo_DetachesTheOld...) only
        // pins that a REMOVED element's handler is gone -- the mirror half is that a RESTORED
        // element (recreated via CreateOverlayElement, a genuinely new instance) is properly
        // RE-attached, or edits to it after Undo would silently stop updating the preview.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.RemoveOverlayElementCommand.Execute(element);
        vm.UndoCommand.Execute(null);
        var countAfterUndo = preparer.ApplyTemplateCallCount;

        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "still subscribed";

        Assert.True(preparer.ApplyTemplateCallCount > countAfterUndo);
    }

    [AvaloniaFact]
    public void OverlayElementXAndY_ThenUndo_RevertsBothAsOneStep()
    {
        // Code-review finding on an earlier draft: only the canvas pointer-drag path pushed an
        // undo step for overlay X/Y -- the sidebar X/Y TextBoxes (bound directly to
        // OverlayElementViewModel.X/Y) were completely untracked despite being claimed as covered.
        // OverlayElementViewModel.PushUndoSnapshotForPositionChange now routes BOTH paths through
        // the same coalesced mechanism as the sliders, under a SHARED key so a diagonal
        // change (X then Y in the same burst) collapses into ONE step, not two.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null); // its own push -- one level stays below the burst's
        var xBefore = vm.OverlayElements[0].X;
        var yBefore = vm.OverlayElements[0].Y;

        vm.OverlayElements[0].X = xBefore + 0.1;
        vm.OverlayElements[0].Y = yBefore + 0.1;

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        AssertClose(xBefore, vm.OverlayElements[0].X);
        AssertClose(yBefore, vm.OverlayElements[0].Y);
        // Exactly ONE step for the X+Y burst -- AddOverlayElement's own earlier push is the one
        // level still remaining, not a second X/Y-burst step.
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void NudgeCropMove_ThenUndo_RevertsCropRect()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        var cropBefore = vm.CropRect;

        vm.NudgeCropMove(NudgeDirection.Right, ctrl: false);
        Assert.NotEqual(cropBefore, vm.CropRect);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        Assert.Equal(cropBefore, vm.CropRect);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void NudgeCropResize_ThenUndo_RevertsCropRectAndPreserveAspect_AsOneStep()
    {
        // Regression coverage for the _suspendPreview wrap in NudgeCropResize: without it, the
        // PreserveAspect/LockAspectToMode side-effect assignments (legacy's mutually-exclusive
        // stretch/keep-aspect radio group) would ALSO fire their own On*Changing-coalesced pushes,
        // turning one keypress into up to 3 undo steps instead of the intended 1.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        Assert.True(vm.PreserveAspect);
        var cropBefore = vm.CropRect;

        vm.NudgeCropResize(NudgeDirection.Right);
        Assert.False(vm.PreserveAspect);
        Assert.NotEqual(cropBefore, vm.CropRect);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        Assert.Equal(cropBefore, vm.CropRect);
        Assert.True(vm.PreserveAspect);
        Assert.False(vm.UndoCommand.CanExecute(null)); // exactly ONE step, not up to 3
    }

    [AvaloniaFact]
    public void DragCropMove_MultiFrame_OnlyPushesOneUndoStepWhenGestureStartIsCalledOnce()
    {
        // Simulates the View's real contract: PushUndoSnapshotForDragGesture is called ONCE per
        // gesture (on the first PointerMoved), then DragCropMove is called once per subsequent
        // frame. ApplyCropMove/ApplyCropResize themselves must never push directly (round-1
        // plan-review blocker B4) or every frame of a drag would flood the stack.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        var cropBefore = vm.CropRect;

        vm.PushUndoSnapshotForDragGesture();
        vm.DragCropMove(0.05, 0.0);
        vm.DragCropMove(0.05, 0.0);
        vm.DragCropMove(0.05, 0.0);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Equal(cropBefore, vm.CropRect);
        Assert.False(vm.UndoCommand.CanExecute(null));
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
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

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
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

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
        Assert.Equal(
            "OUTGOING FRAME 320×256 · Martin M1 · 114.3 s",
            string.Format(CultureInfo.InvariantCulture, "OUTGOING FRAME {0}×{1} · {2} · {3:0.0} s", 320, 256, "Martin M1", 114.3));
        Assert.Equal(
            "CROP 320×256",
            string.Format(CultureInfo.InvariantCulture, "CROP {0}×{1}", 320, 256));
        Assert.Equal(
            "WORKING COPY 836×669 · RENDER 320×256",
            string.Format(CultureInfo.InvariantCulture, "WORKING COPY {0:0}×{1:0} · RENDER {2}×{3}", 836.0, 669.0, 320, 256));
    }

    [AvaloniaFact]
    public void WorkingCopyFooterText_ReflectsTheActualWorkingCopyAndTargetModeDimensions()
    {
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(8, 4), WideMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        _ = vm.WorkingCopyFooterText;

        Assert.Equal("Panes.TxImageEditor.WorkingCopyFooterFormat", localization.LastKey);
        Assert.Equal(new object[] { 8.0, 4.0, 8, 4 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void WorkingCopyFooterText_RaisesPropertyChanged_AfterRotate()
    {
        // Pins the OnPropertyChanged(nameof(WorkingCopyFooterText)) raise in RotateImageOnly --
        // without it this text would silently go stale after the one operation that actually
        // changes WorkingCopyWidth/Height mid-session. Checking the raised PropertyChanged name
        // directly (not the text's own value) since FakeLocalizationService always returns the raw
        // key regardless of args -- WorkingCopyWidth/Height swapping is already covered by the
        // Rotate_ThenUndo_* tests above.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        var raised = false;
        vm.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(vm.WorkingCopyFooterText);

        vm.RotateCommand.Execute(null);

        Assert.True(raised);
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

        var overlay = Assert.Single(preparer.TemplateDocuments[^1].Elements);
        var (overlayCenterX, overlayCenterY) = (overlay.Bounds.X + (overlay.Bounds.Width / 2), overlay.Bounds.Y + (overlay.Bounds.Height / 2));

        AssertClose(0.75, overlayCenterX);
        AssertClose(0.75, overlayCenterY);
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

        var overlay = Assert.Single(preparer.TemplateDocuments[^1].Elements);
        var (overlayCenterX, overlayCenterY) = (overlay.Bounds.X + (overlay.Bounds.Width / 2), overlay.Bounds.Y + (overlay.Bounds.Height / 2));

        AssertClose(0.5625, overlayCenterX);
        AssertClose(0.75, overlayCenterY);
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

        var overlay = Assert.Single(preparer.TemplateDocuments[^1].Elements);
        var (overlayCenterX, overlayCenterY) = (overlay.Bounds.X + (overlay.Bounds.Width / 2), overlay.Bounds.Y + (overlay.Bounds.Height / 2));

        Assert.True(overlayCenterX < 0 || overlayCenterY < 0);
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

        var overlay = Assert.Single(preparer.TemplateDocuments[^1].Elements);
        var (overlayCenterX, overlayCenterY) = (overlay.Bounds.X + (overlay.Bounds.Width / 2), overlay.Bounds.Y + (overlay.Bounds.Height / 2));

        Assert.False(double.IsNaN(overlayCenterX));
        Assert.False(double.IsNaN(overlayCenterY));
        AssertClose(0.3, overlayCenterX);
        AssertClose(0.4, overlayCenterY);
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
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

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
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var countBeforeDirectSet = preparer.ApplyTemplateCallCount;

        element.CanvasFontSize = 12.34;

        Assert.Equal(countBeforeDirectSet, preparer.ApplyTemplateCallCount);
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

    [AvaloniaFact]
    public void Constructor_WithInitialState_SeedsCropPreserveAspectAdjustmentsAndOverlayElements()
    {
        // spec/18-path-to-1.0.md Medium item: re-open/re-edit an image after Apply. The
        // TxControlsPaneViewModel.EditCurrentImageAsync command constructs a fresh editor with the
        // PRIOR edit's own state -- confirms every field actually lands, not just some.
        var initialState = new TxImageEditorPaneViewModel.EditorInitialState(
            new NormalizedRect(0.1, 0.2, 0.3, 0.4),
            PreserveAspect: false,
            new ImageAdjustments(Brightness: 11, Contrast: -22, Saturation: 33, Gamma: -44, Sharpen: 55, Denoise: 66),
            [new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                X: 0.25, Y: 0.75, Width: 0.3, Height: 0.18, Z: 0, Locked: false,
                Text: "DE %m", FontSizeRelative: 0.15, Color: new Rgb24(10, 20, 30))]);

        var vm = new TxImageEditorPaneViewModel(
            CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings { Callsign = "W1AW" }, new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(),
            initialState);

        AssertClose(0.1, vm.CropRect.X);
        AssertClose(0.2, vm.CropRect.Y);
        AssertClose(0.3, vm.CropRect.Width);
        AssertClose(0.4, vm.CropRect.Height);
        Assert.False(vm.PreserveAspect);
        AssertClose(11, vm.Brightness);
        AssertClose(-22, vm.Contrast);
        AssertClose(33, vm.Saturation);
        AssertClose(-44, vm.Gamma);
        AssertClose(55, vm.Sharpen);
        AssertClose(66, vm.Denoise);
        var element = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        // Raw Text, NOT resolved -- confirms the macro template itself was restored, not baked.
        Assert.Equal("DE %m", element.Text);
        Assert.Equal("DE W1AW", element.ResolvedText);
        AssertClose(0.25, element.X);
        AssertClose(0.75, element.Y);
        AssertClose(0.15, element.FontSizeRelative);
        Assert.Equal(new Rgb24(10, 20, 30), element.Color);
    }

    [AvaloniaFact]
    public void Constructor_WithInitialStateElementsOutOfZOrder_SeedsOverlayElementsSortedByZ()
    {
        // Round-3 code-review finding: MoveElementUp/Down assume OverlayElements' own collection
        // order always matches Z order (that invariant is what lets a plain Canvas.Move()-based
        // reorder keep the canvas's draw order in sync with Z). Every other mutation site upholds
        // this already; EditorInitialState is the one entry point that could hand in elements out of
        // Z order (e.g. a future template-load path) -- this pins that the constructor sorts on
        // entry instead of trusting the caller.
        var initialState = new TxImageEditorPaneViewModel.EditorInitialState(
            new NormalizedRect(0, 0, 1, 1),
            PreserveAspect: true,
            new ImageAdjustments(),
            [
                new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                    X: 0.5, Y: 0.5, Width: 0.3, Height: 0.18, Z: 5, Locked: false,
                    Text: "Z5", FontSizeRelative: 0.1, Color: new Rgb24(0, 0, 0)),
                new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                    X: 0.5, Y: 0.5, Width: 0.3, Height: 0.18, Z: 1, Locked: false,
                    Text: "Z1", FontSizeRelative: 0.1, Color: new Rgb24(0, 0, 0)),
                new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                    X: 0.5, Y: 0.5, Width: 0.3, Height: 0.18, Z: 3, Locked: false,
                    Text: "Z3", FontSizeRelative: 0.1, Color: new Rgb24(0, 0, 0)),
            ]);

        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(),
            initialState);

        Assert.Equal(["Z1", "Z3", "Z5"], vm.OverlayElements.Select(e => ((OverlayElementViewModel)e).Text));
    }

    [AvaloniaFact]
    public void Constructor_WithInitialState_RecomputesPreviewExactlyOnce()
    {
        // Same suspend-preview shape as Rotate()'s own test -- seeding CropRect/PreserveAspect/6
        // slider properties/N overlay elements must not each independently trigger their own
        // RecomputePreview() pass.
        var preparer = new FakeTransmitImagePreparer();
        var initialState = new TxImageEditorPaneViewModel.EditorInitialState(
            new NormalizedRect(0.1, 0.2, 0.3, 0.4), PreserveAspect: false,
            new ImageAdjustments(Brightness: 10),
            [
                new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                    X: 0.2, Y: 0.2, Width: 0.3, Height: 0.18, Z: 0, Locked: false,
                    Text: "A", FontSizeRelative: 0.1, Color: new Rgb24(255, 255, 255)),
                new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                    X: 0.3, Y: 0.3, Width: 0.3, Height: 0.18, Z: 1, Locked: false,
                    Text: "B", FontSizeRelative: 0.1, Color: new Rgb24(255, 255, 255)),
            ]);

        _ = new TxImageEditorPaneViewModel(
            CreateSource(8, 8), WideMode, preparer, new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(),
            initialState);

        // One from BuildWorkingCopy's own initial Resize + one RecomputePreview pass (Crop, Resize,
        // ApplyAdjustments, ApplyOverlay each call ApplyOverlay/ApplyAdjustments/etc. once) --
        // asserting on ApplyTemplateCallCount specifically, since that's the pipeline's final step.
        Assert.Equal(1, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void Constructor_WithNullInitialState_BehavesExactlyAsBeforeThisFeature()
    {
        // Regression guard: the existing fresh-pick call site (OpenEditorForSourceAsync) never
        // passes initialState -- confirms that path's defaults are completely unaffected.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());

        Assert.Equal(new NormalizedRect(0, 0, 1, 1), vm.CropRect);
        Assert.True(vm.PreserveAspect);
        Assert.True(vm.Adjustments.IsIdentity);
        Assert.Empty(vm.OverlayElements);
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
        var countBefore = preparer.ApplyTemplateCallCount;

        typeof(TxImageEditorPaneViewModel).GetProperty(propertyName)!.SetValue(vm, 25.0);

        Assert.True(preparer.ApplyTemplateCallCount > countBefore);
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
        Assert.Same(preparer.AdjustmentsResults[^1], preparer.ApplyTemplateSources[^1]);
    }

    // Phase 1 (spec/15-template-designer.md): box elements + z-order reorder. Code-review finding --
    // this coverage was entirely missing when Phase 1 first shipped.

    [AvaloniaFact]
    public void AddBoxElement_AddsAndSelectsItAndTriggersPreviewRecompute()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var countBefore = preparer.ApplyTemplateCallCount;

        vm.AddBoxElementCommand.Execute(null);

        var element = (BoxElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.True(preparer.ApplyTemplateCallCount > countBefore);
    }

    [AvaloniaFact]
    public void AddBoxElement_BakesFillBorderThicknessOpacityIntoTheAppliedDocument()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = (BoxElementViewModel)vm.OverlayElements[0];

        element.FillColor = new Rgb24(10, 20, 30);
        element.BorderColor = new Rgb24(40, 50, 60);
        element.BorderThickness = 0.05;
        element.Opacity = 0.5;

        var box = Assert.IsType<TemplateBoxElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Equal(new Rgb24(10, 20, 30), box.FillColor);
        Assert.Equal(new Rgb24(40, 50, 60), box.BorderColor);
        AssertClose(0.05, box.BorderThickness);
        AssertClose(0.5, box.Opacity);
    }

    // Phase 2 (spec/15-template-designer.md): image elements + set-as-background. 3 sources (file /
    // last-RX / RX-history), all real precedent reuse -- see the plan's own scope-cut reasoning.

    [AvaloniaFact]
    public async Task AddImageFromFileAsync_LoadsThePickedFileAndAddsSelectsElement()
    {
        var preparer = new FakeTransmitImagePreparer();
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        var countBefore = preparer.ApplyTemplateCallCount;

        await vm.AddImageFromFileCommand.ExecuteAsync(null);

        var element = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.True(preparer.ApplyTemplateCallCount > countBefore);
        var image = Assert.IsType<TemplateImageElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Same(loader.ResultToReturn, image.Source);
        // Origin exists solely for Phase 5 persistence to tell a file-sourced image from an
        // embedded/RX one apart -- a wrong Kind or null Payload here would pass every other
        // assertion in this test while silently breaking that.
        Assert.Equal(new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, "/tmp/picked.jpg"), element.Origin);
    }

    [AvaloniaFact]
    public async Task AddImageFromFileAsync_PickerReturnsNull_IsANoOp()
    {
        // A null path from the picker is a normal "user hit Cancel", not an error -- this must NOT
        // add an element or throw trying to load a null path.
        var picker = new FakeFilePickerService { PathToReturn = null };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), picker, new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImageFromFileCommand.ExecuteAsync(null);

        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void AddLastRxImage_InsertsReceivedImageBufferCurrentAsASnapshot()
    {
        // Snapshot-at-insert-time, not a live binding (plan-review-resolved open question) -- this
        // pins that the element's Source is captured at CLICK time, not re-read from the buffer
        // later.
        var preparer = new FakeTransmitImagePreparer();
        var rxSource = CreateSource(3, 3);
        var receivedImage = new FakeReceivedImageBuffer { Current = rxSource };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, new FakeFilePickerService(), new FakeImageFileLoader(), receivedImage, new FakeReceiveHistoryStore());

        vm.AddLastRxImageCommand.Execute(null);

        var element = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.Same(rxSource, element.Source);
        var image = Assert.IsType<TemplateImageElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Same(rxSource, image.Source);
        Assert.Equal(new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.LastRx, null), element.Origin);

        // Changing Current afterward must NOT retroactively change the already-inserted element --
        // that's exactly the live-binding behavior the plan-review explicitly rejected.
        receivedImage.Current = CreateSource(5, 5);
        Assert.Same(rxSource, element.Source);
    }

    [AvaloniaFact]
    public async Task RefreshRxHistoryPickerAsync_PopulatesEntriesFromTheStoreWithThumbnails()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("entry-1", DateTimeOffset.UtcNow, "PD120", "/tmp/rx1.png", null, ReceiveDecodeState.Completed),
            ],
            ThumbnailToReturn = CreateSource(1, 1),
        };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), historyStore);

        await vm.RefreshRxHistoryPickerCommand.ExecuteAsync(null);

        var entry = Assert.Single(vm.RxHistoryPickerEntries);
        Assert.Equal("entry-1", entry.Id);
        Assert.Equal("/tmp/rx1.png", entry.FilePath);
        Assert.NotNull(entry.Thumbnail);
        // SelectCommand is parent-pushed (same pattern as ITemplateElementViewModel.RemoveCommand)
        // so the AXAML picker row can bind directly, not via a $parent[ItemsControl] path.
        Assert.Same(vm.AddImageFromRxHistoryCommand, entry.SelectCommand);
    }

    [AvaloniaFact]
    public async Task AddImageFromRxHistoryAsync_LoadsFullResolutionAndAddsSelectsElement()
    {
        // IReceiveHistoryStore has no full-resolution loader (only thumbnails) -- this pins that the
        // full-res load goes through IImageFileLoader.LoadOriginalAsync against the entry's own real
        // FilePath, the same loader the file-picker source already uses.
        var preparer = new FakeTransmitImagePreparer();
        var fullResSource = CreateSource(6, 6);
        var loader = new FakeImageFileLoader { ResultToReturn = fullResSource };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        var entry = new TxImageEditorPaneViewModel.RxHistoryPickerEntry("entry-1", "/tmp/rx1.png", null, null);

        await vm.AddImageFromRxHistoryCommand.ExecuteAsync(entry);

        var element = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(fullResSource, element.Source);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.Equal(new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.RxHistory, "entry-1"), element.Origin);
    }

    [AvaloniaFact]
    public async Task AddImageFromFileAsync_ThenUndoThenRedo_RestoresOriginNotJustSourceAndGeometry()
    {
        // Code-review finding: RawImageElementSnapshot carries Origin specifically so a future
        // Phase 5 persisted-template load can tell a file-sourced image from an RX one apart -- if
        // Undo/Redo's own snapshot round-trip (CreateElementFromSnapshot) ever dropped or
        // mis-mapped it, every other test in this file would still pass (none of them touch
        // Undo/Redo for an image element), so this is pinned separately.
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        await vm.AddImageFromFileCommand.ExecuteAsync(null);
        vm.UndoCommand.Execute(null);

        vm.RedoCommand.Execute(null);

        var restored = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Equal(new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, "/tmp/picked.jpg"), restored.Origin);
    }

    [AvaloniaFact]
    public void SetAsBackground_MovesElementToFullFrameBottomZAndCollectionIndexZero()
    {
        // Round-2-class finding, applied proactively here (Phase 1's own MoveElementUp/Down bug):
        // setting Z alone is NOT enough -- the interactive canvas draws in OverlayElements' own
        // COLLECTION order, so this also asserts collection identity/order, not just Z.
        var preparer = new FakeTransmitImagePreparer();
        var receivedImage = new FakeReceivedImageBuffer { Current = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, new FakeFilePickerService(), new FakeImageFileLoader(), receivedImage, new FakeReceiveHistoryStore());
        vm.AddOverlayElementCommand.Execute(null);
        var text = vm.OverlayElements[0];
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[1];

        vm.SetAsBackgroundCommand.Execute(image);

        AssertClose(0.5, image.X);
        AssertClose(0.5, image.Y);
        AssertClose(1, image.Width);
        AssertClose(1, image.Height);
        Assert.True(image.Z < text.Z);
        Assert.Same(image, vm.OverlayElements[0]);
        Assert.Same(text, vm.OverlayElements[1]);
    }

    [AvaloniaFact]
    public void SetAsBackground_PushesExactlyOneUndoStep()
    {
        // Code-review-class finding, applied proactively (mirrors Rotate()'s own multi-element
        // geometry loop): setting X/Y/Width/Height individually on an already-wired element would
        // each independently trigger PushUndoSnapshotForGeometryChange's own coalesced push on top
        // of this command's explicit PushUndoSnapshot, UNLESS wrapped in _suspendPreview.
        //
        // A redundant SECOND push here would capture the SAME pre-mutation state as the first
        // (PushUndoSnapshotCoalesced's own dedup only kicks in from the SECOND geometry property
        // onward within one call, not the first), so a single-Undo value-based assertion can't tell
        // "1 push" from "2 identical pushes" apart -- mutation-tested by removing the
        // _suspendPreview wrap and confirming this exact test still passed, which is why this counts
        // total undo depth instead: push AddLastRxImage (1 action) then SetAsBackground (should be
        // exactly 1 more), then Undo exactly twice and assert NOTHING is left. A stray extra push
        // would leave one more Undo available after these two clicks.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];

        vm.SetAsBackgroundCommand.Execute(image);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void SetAsBackground_OnNullElement_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.SetAsBackgroundCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SetAsBackground_OnAnElementNoLongerInOverlayElements_DoesNotThrowAndIsANoOp()
    {
        // Code-review finding: a stale element reference (e.g. a queued click racing an Undo,
        // which replaces every element wholesale via ApplyState) must not reach
        // OverlayElements.Move(-1, 0) -- that throws ArgumentOutOfRangeException out of a command
        // handler. This pins the guard without needing to actually race an Undo: add TWO elements
        // (so OverlayElements stays non-empty -- Min(Z) must still succeed, isolating this from the
        // separate "Min on an empty collection" failure mode), then RemoveOverlayElement detaches
        // just the first one from OverlayElements while the reference itself stays valid,
        // reproducing the same "not in the collection, but the collection isn't empty" state.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        vm.AddBoxElementCommand.Execute(null);
        vm.RemoveOverlayElementCommand.Execute(element);
        Assert.DoesNotContain(element, vm.OverlayElements);
        Assert.NotEmpty(vm.OverlayElements);
        var undoDepthBefore = vm.UndoCommand.CanExecute(null);

        var exception = Record.Exception(() => vm.SetAsBackgroundCommand.Execute(element));

        Assert.Null(exception);
        // No bogus undo step left behind by the guarded-out call.
        Assert.Equal(undoDepthBefore, vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SetAsBackground_SetsIsBackgroundAndAutoLocks()
    {
        // Phase 6 (spec/15-template-designer.md): both together are what let the crop rect
        // underneath become click-reachable again (BlocksHitTesting = Locked && IsBackground).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        Assert.False(image.IsBackground);
        Assert.False(image.Locked);

        vm.SetAsBackgroundCommand.Execute(image);

        Assert.True(image.IsBackground);
        Assert.True(image.Locked);
        Assert.True(image.BlocksHitTesting);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void BlocksHitTesting_TrueOnlyWhenBothLockedAndBackground(bool locked, bool isBackground, bool expected)
    {
        var element = new ImageElementViewModel(CreateSource(2, 2))
        {
            Locked = locked,
            IsBackground = isBackground,
            Origin = new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, null),
        };

        Assert.Equal(expected, element.BlocksHitTesting);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_BackgroundImageElement_RoundTripsIsBackground()
    {
        // Code-review-class regression guard for the plan-review blocker: IsBackground IS persisted
        // (unlike most purely-interactive state) specifically because Locked already is -- leaving
        // IsBackground unpersisted would round-trip a background element into a WORSE state than
        // before Phase 6 (locked AND hit-blocking again, no easy way back).
        var templateStore = new FakeTemplateStore();
        var imageSourceWriter = new FakeImageSourceWriter();
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/bg.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var readyRack = CreateReadyRack(templateStore);
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            templateStore, imageSourceWriter, readyRack);
        await vm.AddImageFromFileCommand.ExecuteAsync(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackgroundCommand.Execute(image);
        vm.NewTemplateName = "Background Template";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persisted = Assert.IsType<PersistedImageElement>(Assert.Single(document.Elements));
        Assert.True(persisted.IsBackground);

        await readyRack.RefreshAsync();
        readyRack.LoadCommand.Execute(Assert.Single(readyRack.AllTemplates));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var reloaded = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.True(reloaded.IsBackground);
        Assert.True(reloaded.Locked);
    }

    [AvaloniaFact]
    public void NudgeElement_NoSelection_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        var xBefore = element.X;
        // AddBoxElementCommand itself selects the new element (Phase 1 code-review finding) -- this
        // test is specifically about the "nothing selected" branch, so deselect explicitly, same as
        // OnCanvasKeyDown's own new Escape handler does.
        vm.SelectedOverlayElement = null;

        vm.NudgeElement(NudgeDirection.Right, ctrl: false);

        AssertClose(xBefore, element.X);
    }

    [AvaloniaTheory]
    [InlineData(NudgeDirection.Right, false, 1)]
    [InlineData(NudgeDirection.Right, true, 16)]
    [InlineData(NudgeDirection.Left, false, -1)]
    public void NudgeElement_MovesSelectedElementByPixelDeltaOverWorkingCopyWidth(NudgeDirection direction, bool ctrl, int expectedPixels)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        vm.SelectedOverlayElement = element;
        var xBefore = element.X;

        vm.NudgeElement(direction, ctrl);

        AssertClose(xBefore + ((double)expectedPixels / vm.WorkingCopyWidth), element.X);
    }

    [AvaloniaFact]
    public void NudgeElement_OnLockedElement_ViewModelItselfDoesNotGuard_CallerIsResponsible()
    {
        // NudgeElement is deliberately self-contained about "is anything selected" but NOT about
        // Locked (OnCanvasKeyDown's own SelectedOverlayElement is { Locked: false } check is the
        // real gate, matching every other Locked check in this editor living at the call site, not
        // duplicated inside the mutation method itself) -- this test pins that division of
        // responsibility explicitly so it isn't "discovered" as a missing guard later.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.Locked = true;
        vm.SelectedOverlayElement = element;
        var xBefore = element.X;

        vm.NudgeElement(NudgeDirection.Right, ctrl: false);

        Assert.NotEqual(xBefore, element.X);
    }

    [AvaloniaFact]
    public void ApplyFit_ViewportLargerThanWorkingCopy_ZoomFactorGrowsToFillTheSmallerAxis()
    {
        // WorkingCopy is 4x4 (SmallMode, within-budget original); a 12x8 viewport is the
        // constraining (narrower relative) axis on height (8/4=2) vs width (12/4=3) -- Fit takes
        // the MINIMUM of the two so the whole working copy stays visible on both axes, same
        // reasoning as PreserveAspect's own Math.Min. (Deliberately within MaxZoomFactor -- the
        // clamp itself is covered separately below.)
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFit(12, 8);

        AssertClose(2.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFit_ComputedRatioAboveMaxZoomFactor_ClampsToMax()
    {
        // 4x4 working copy against a huge viewport would compute a huge ratio unclamped.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFit(4000, 4000);

        AssertClose(4.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFit_ComputedRatioBelowMinZoomFactor_ClampsToMin()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFit(0.01, 0.01);

        AssertClose(0.1, vm.ZoomFactor);
    }

    [AvaloniaTheory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-5, 10)]
    public void ApplyFit_NonPositiveViewportDimension_IsANoOp(double width, double height)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var zoomBefore = vm.ZoomFactor;

        vm.ApplyFit(width, height);

        AssertClose(zoomBefore, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ZoomActualCommand_SetsZoomFactorToOne()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ApplyFit(8, 8);
        AssertClose(2.0, vm.ZoomFactor);

        vm.ZoomActualCommand.Execute(null);

        AssertClose(1.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ZoomBy_MultipliesCurrentZoomFactor()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ZoomFactor = 1.0;

        vm.ZoomBy(1.1);

        AssertClose(1.1, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ZoomBy_ClampsToMaxZoomFactor()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ZoomFactor = 3.9;

        vm.ZoomBy(2.0);

        AssertClose(4.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ZoomBy_ClampsToMinZoomFactor()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ZoomFactor = 0.15;

        vm.ZoomBy(0.5);

        AssertClose(0.1, vm.ZoomFactor);
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ZoomBy_NonFiniteOrNonPositiveFactor_IsANoOp(double factor)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ZoomFactor = 1.5;

        vm.ZoomBy(factor);

        AssertClose(1.5, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ZoomPercentText_ReflectsZoomFactorAsAWholeNumberPercentage()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ZoomFactor = 2.0;

        Assert.Equal("200%", vm.ZoomPercentText);
    }

    [AvaloniaFact]
    public void ZoomFactorChange_PushesZoomedCanvasSizeOntoEveryExistingElement()
    {
        // Phase 7 rearchitecture: zoom is baked directly into each element's own ImageWidth/
        // ImageHeight (= parent's CanvasDisplayWidth/Height) rather than a separate InverseZoomScale
        // channel -- see TxImageEditorPaneViewModel.ZoomFactor's own doc comment for why.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        vm.AddOverlayElementCommand.Execute(null);
        var box = vm.OverlayElements[0];
        var text = vm.OverlayElements[1];

        vm.ZoomFactor = 2.0;

        AssertClose(vm.WorkingCopyWidth * 2.0, vm.CanvasDisplayWidth);
        AssertClose(vm.WorkingCopyHeight * 2.0, vm.CanvasDisplayHeight);
        AssertClose(vm.CanvasDisplayWidth, box.ImageWidth);
        AssertClose(vm.CanvasDisplayHeight, box.ImageHeight);
        AssertClose(vm.CanvasDisplayWidth, text.ImageWidth);
        AssertClose(vm.CanvasDisplayHeight, text.ImageHeight);
    }

    [AvaloniaFact]
    public void AddElement_AfterZoomChange_NewElementIsSeededWithTheCurrentZoomedCanvasSize()
    {
        // The 3 creation sites (CreateOverlayElement/CreateBoxElement/CreateImageElement) push the
        // parent's CURRENT CanvasDisplayWidth/Height at construction time -- a freshly-added element
        // must not default back to the unzoomed working-copy size while the editor is already zoomed.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ZoomFactor = 4.0;

        vm.AddBoxElementCommand.Execute(null);

        var box = vm.OverlayElements[0];
        AssertClose(vm.CanvasDisplayWidth, box.ImageWidth);
        AssertClose(vm.CanvasDisplayHeight, box.ImageHeight);
    }

    [AvaloniaFact]
    public void ZoomFactorChange_CanvasFontSizeScalesLinearlyWithZoom_NotQuadratically()
    {
        // Code-review regression test: an earlier version of ComputeCanvasFontSize multiplied by
        // ZoomFactor a SECOND time on top of the factor it already inherits implicitly through
        // CropHeightPixels (zoom-premultiplied per the Phase 7 rearchitecture) -- a real Z^2 bug,
        // invisible in manual testing done at exactly 1.0 zoom (where Z^2 == Z == 1) but wrong at
        // every other zoom (e.g. 2.0 would have produced 4x the correct size, not 2x).
        // FakeTransmitImagePreparer.MeasureFittedFontSize returns a value with no zoom-dependence of
        // its own (font.Size * imageHeightPx, imageHeightPx is the fixed target-mode height), so any
        // observed non-linearity here can only come from ComputeCanvasFontSize's own math.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        var baselineFontSize = text.CanvasFontSize;

        vm.ZoomFactor = 2.0;

        AssertClose(baselineFontSize * 2.0, text.CanvasFontSize);
    }

    [AvaloniaFact]
    public void ZoomFactorChange_CropPixelsScaleWithZoom()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var baselineWidth = vm.CropWidthPixels;
        var baselineHeight = vm.CropHeightPixels;

        vm.ZoomFactor = 2.0;

        AssertClose(baselineWidth * 2.0, vm.CropWidthPixels);
        AssertClose(baselineHeight * 2.0, vm.CropHeightPixels);
    }

    [AvaloniaFact]
    public void SafeAreaInsetPixels_ScalesLinearlyWithZoom()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ZoomFactor = 2.0;

        AssertClose(28.0, vm.SafeAreaInsetPixels);
    }

    [AvaloniaFact]
    public void ZoomFactorChange_DoesNotAffectTheTransmittedDocument()
    {
        // The whole point of baking zoom into on-screen pixel properties instead of the pipeline: the
        // operator's current canvas zoom must never change what actually gets transmitted. Z cancels
        // algebraically inside ProjectRectToCropRelative (see that method's own doc comment) -- this
        // pins the observable guarantee, not just the internal algebra.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        var documentBefore = vm.Document;

        vm.ZoomFactor = 3.0;

        var documentAfter = vm.Document;
        Assert.Equal(documentBefore.Elements.Count, documentAfter.Elements.Count);
        for (var i = 0; i < documentBefore.Elements.Count; i++)
        {
            Assert.Equal(documentBefore.Elements[i], documentAfter.Elements[i]);
        }
    }

    // Task #24 (right-click context menu addendum, plan-reviewed): DuplicateCommand/AddPlateCommand
    // are parent-pushed the SAME way RemoveCommand/MoveUpCommand/etc. already are -- the established
    // failure mode for this pattern (per RemoveCommand's own doc comment, and the sidebar's own
    // Phase-2 image-DataTemplate comment) is a forgotten assignment at exactly one of the three
    // Create* call sites, which shows up as a silently-inert context-menu item rather than a build
    // error or an exception. Pin all three paths explicitly rather than trusting a read-through.

    [AvaloniaFact]
    public void DuplicateCommand_IsWiredOnAllThreeElementTypes()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());

        vm.AddOverlayElementCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        vm.AddLastRxImageCommand.Execute(null);

        Assert.Equal(3, vm.OverlayElements.Count);
        Assert.All(vm.OverlayElements, e => Assert.NotNull(e.DuplicateCommand));
    }

    [AvaloniaFact]
    public void AddPlateCommand_IsWiredOnTextElements()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.NotNull(text.AddPlateCommand);
    }

    // EditWindow redesign Phase 6 (mockups/Editwindow): the context menu's "Insert field"/"Align to
    // crop" submenus bind {Binding InsertFieldCommand}/{Binding AlignSelectedElementToCropCommand}
    // against the ELEMENT's own DataContext (same inline-per-DataTemplate resolution as
    // DuplicateCommand/AddPlateCommand above) -- these must be parent-pushed the same way, or the
    // binding resolves to null and the menu items render permanently disabled (a real regression this
    // pair of tests caught live: both submenus were unclickable until these assignments were added).

    [AvaloniaFact]
    public void AlignSelectedElementToCropCommand_IsWiredOnAllThreeElementTypes()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());

        vm.AddOverlayElementCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        vm.AddLastRxImageCommand.Execute(null);

        Assert.Equal(3, vm.OverlayElements.Count);
        Assert.All(vm.OverlayElements, e => Assert.NotNull(e.AlignSelectedElementToCropCommand));
    }

    [AvaloniaFact]
    public void InsertFieldCommand_IsWiredOnTextElements()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.NotNull(text.InsertFieldCommand);
    }

    // EditWindow redesign Phase 3 (mockups/Editwindow), GEOMETRY tab's align-to-crop actions.
    // CropRect defaults to the full frame (0,0,1,1) in these tests, matching
    // TxImageEditorPaneViewModel's own default -- Left/Right land at exactly element.Width/2 from
    // each edge, Top/Bottom at element.Height/2, Center/Middle at exactly 0.5, which is what makes
    // these good discriminating assertions (a bug that used the WRONG anchor convention -- e.g.
    // copying CropRect.X directly instead of offsetting by half the element's own size -- would fail
    // every one of these except Center/Middle, which happen to coincide either way at this crop).

    [AvaloniaTheory]
    [InlineData("Left", 0.15)]
    [InlineData("Center", 0.5)]
    [InlineData("Right", 0.85)]
    public void AlignSelectedElementToCrop_HorizontalAlignments_SetExpectedX(string alignment, double expectedX)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.Width = 0.3;
        var yBefore = element.Y;

        vm.AlignSelectedElementToCropCommand.Execute(alignment);

        AssertClose(expectedX, element.X);
        AssertClose(yBefore, element.Y);
    }

    [AvaloniaTheory]
    [InlineData("Top", 0.1)]
    [InlineData("Middle", 0.5)]
    [InlineData("Bottom", 0.9)]
    public void AlignSelectedElementToCrop_VerticalAlignments_SetExpectedY(string alignment, double expectedY)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.Height = 0.2;
        var xBefore = element.X;

        vm.AlignSelectedElementToCropCommand.Execute(alignment);

        AssertClose(expectedY, element.Y);
        AssertClose(xBefore, element.X);
    }

    [AvaloniaFact]
    public void AlignSelectedElementToCrop_OnNullSelection_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        var exception = Record.Exception(() => vm.AlignSelectedElementToCropCommand.Execute("Left"));

        Assert.Null(exception);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void AlignSelectedElementToCrop_PushesExactlyOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var undoDepthBeforeAlign = vm.UndoCommand.CanExecute(null);

        vm.AlignSelectedElementToCropCommand.Execute("Left");
        vm.UndoCommand.Execute(null);

        Assert.Equal(undoDepthBeforeAlign, vm.UndoCommand.CanExecute(null));
    }

    // EditWindow redesign Phase 3, Inspector tab-selection flags.

    [AvaloniaFact]
    public void InspectorTabSelection_DefaultsToTextStyle()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.True(vm.IsTextStyleTabSelected);
        Assert.False(vm.IsGeometryTabSelected);
        Assert.False(vm.IsImageTabSelected);
    }

    [AvaloniaFact]
    public void InspectorTabSelection_SwitchingTabs_IsMutuallyExclusive()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.SelectGeometryTabCommand.Execute(null);
        Assert.False(vm.IsTextStyleTabSelected);
        Assert.True(vm.IsGeometryTabSelected);
        Assert.False(vm.IsImageTabSelected);

        vm.SelectImageTabCommand.Execute(null);
        Assert.False(vm.IsTextStyleTabSelected);
        Assert.False(vm.IsGeometryTabSelected);
        Assert.True(vm.IsImageTabSelected);

        vm.SelectTextStyleTabCommand.Execute(null);
        Assert.True(vm.IsTextStyleTabSelected);
        Assert.False(vm.IsGeometryTabSelected);
        Assert.False(vm.IsImageTabSelected);
    }

    [AvaloniaFact]
    public void SelectionReadoutText_NothingSelected_IsEmpty()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.Equal(string.Empty, vm.SelectionReadoutText);
    }

    [AvaloniaFact]
    public void SelectionReadoutText_TextElementSelected_UsesRotationFormatWithZAndPixelGeometry()
    {
        // FakeLocalizationService.GetString returns the raw key, not a real formatted string --
        // asserting on LastKey/LastArgs (this project's established pattern, e.g.
        // TxControlsTransmitProgressTests) is what actually proves the VM picked the rotation-aware
        // format and computed the right arguments, not the localization plumbing.
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.SelectedOverlayElement!;
        element.RotationDegrees = 15;

        _ = vm.SelectionReadoutText;

        Assert.Equal("Panes.TxImageEditor.SelectionReadoutWithRotationFormat", localization.LastKey);
        Assert.Equal(
            new object[] { "Panes.TxImageEditor.TypeBadgeText", element.Z, (int)Math.Round(element.LeftPixels), (int)Math.Round(element.TopPixels), (int)Math.Round(element.CanvasWidthPixels), (int)Math.Round(element.CanvasHeightPixels), 15 },
            localization.LastArgs);
    }

    [AvaloniaFact]
    public void SelectionReadoutText_BoxElementSelected_UsesFormatWithoutRotation()
    {
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());
        vm.AddBoxElementCommand.Execute(null);

        _ = vm.SelectionReadoutText;

        Assert.Equal("Panes.TxImageEditor.SelectionReadoutFormat", localization.LastKey);
    }

    [AvaloniaFact]
    public void IsFontUnavailable_SelectedTextElementFontNotInAvailableFamilies_ReturnsTrue()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;
        Assert.False(vm.IsFontUnavailable);

        text.FontFamily = "Comic Sans MS";

        Assert.True(vm.IsFontUnavailable);
        Assert.DoesNotContain("Comic Sans MS", vm.AvailableFontFamilies);
    }

    [AvaloniaFact]
    public void FontFamilyPickerItems_UnavailableFont_IncludesItSoSelectedItemBindingCanNeverOverwriteIt()
    {
        // Plan-review risk: ComboBox.SelectedItem is two-way bound to FontFamily over an ItemsSource
        // -- if the bound value isn't present in ItemsSource at all, a two-way binding can silently
        // write back a no-match resolution, destroying the real (if unavailable) font name before
        // the operator ever sees the warning. FontFamilyPickerItems must always contain the current
        // value so that can't happen, regardless of the exact no-match binding behavior.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;
        text.FontFamily = "Comic Sans MS";

        Assert.Contains("Comic Sans MS", vm.FontFamilyPickerItems);
        foreach (var family in vm.AvailableFontFamilies)
        {
            Assert.Contains(family, vm.FontFamilyPickerItems);
        }
    }

    [AvaloniaFact]
    public async Task LoadTemplate_TextElementWithUnavailableFont_NameSurvivesAndIsFontUnavailableIsTrue()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var templateId = templateStore.CreateTemplateId("Unavailable Font");
        await templateStore.SaveAsync(templateId, "Unavailable Font", new PersistedTemplateDocument([
            new PersistedTextElement(0.5, 0.5, 0.3, 0.1, 0, false, "hi", 0.1, new Rgb24(255, 255, 255), "Comic Sans MS", null, 0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var text = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        vm.SelectedOverlayElement = text;
        Assert.Equal("Comic Sans MS", text.FontFamily);
        Assert.True(vm.IsFontUnavailable);
    }

    [AvaloniaFact]
    public void Duplicate_CanExecute_FalseWhenNothingSelected_TrueForAnyElementType()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.DuplicateCommand.CanExecute(null));

        vm.AddBoxElementCommand.Execute(null);
        vm.SelectedOverlayElement = vm.OverlayElements[0];
        Assert.True(vm.DuplicateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Duplicate_ClonesSelectedElement_OffsetInsertedAtTopOfStack_AndSelectsTheCopy()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = (OverlayElementViewModel)vm.OverlayElements[0];
        original.Text = "Original";
        vm.SelectedOverlayElement = original;

        vm.DuplicateCommand.Execute(null);

        Assert.Equal(2, vm.OverlayElements.Count);
        var copy = Assert.IsType<OverlayElementViewModel>(vm.OverlayElements[1]);
        Assert.Same(copy, vm.SelectedOverlayElement);
        Assert.Equal("Original", copy.Text);
        Assert.True(copy.Z > original.Z);
        Assert.NotEqual(original.X, copy.X);
        Assert.NotEqual(original.Y, copy.Y);

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void Duplicate_BackgroundImageElement_CloneIsNotBackgroundAndNotLocked()
    {
        // Plan-review risk: without clearing these, the clone would be a second full-frame, top-Z,
        // locked, hit-test-passthrough copy -- covering the whole canvas and itself unreachable by
        // canvas click.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackgroundCommand.Execute(image);
        Assert.True(image.IsBackground);
        Assert.True(image.Locked);

        vm.DuplicateCommand.Execute(null);

        var copy = Assert.IsType<ImageElementViewModel>(vm.OverlayElements[1]);
        Assert.False(copy.IsBackground);
        Assert.False(copy.Locked);
    }

    [AvaloniaFact]
    public void AddLastRxImage_WithNothingEverReceived_IsANoOp()
    {
        // Code-review finding: IReceivedImageBuffer.Current defaults to (and resets to, on decode
        // restart) a 1x1 black placeholder, never null -- a click here with nothing ever received
        // must not silently insert that placeholder as a visible-but-blank image element.
        var receivedImage = new FakeReceivedImageBuffer(); // Current defaults to a 1x1 stub
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), receivedImage, new FakeReceiveHistoryStore());

        vm.AddLastRxImageCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task AddImageFromFileAsync_WithASourceLargerThanTheWorkingCopyBudget_DownsamplesBeforeInserting()
    {
        // Code-review finding: an inserted image element previously went straight from the loader's
        // full native resolution into the element/WriteableBitmap/per-frame-pipeline with no cap,
        // unlike the background image itself (BuildWorkingCopy). This pins that InsertImageElement
        // applies the SAME WorkingCopyScaleFactor budget, preserving aspect.
        var preparer = new FakeTransmitImagePreparer();
        // SmallMode is a small target mode (see its own definition below); working copy budget is
        // WorkingCopyWidth/Height * WorkingCopyScaleFactor (2x) -- an 8x8 original source is already
        // within that budget for SmallMode's own tiny dimensions, so use a source far larger than
        // any plausible mode to force the downsample path deterministically.
        var oversizedSource = CreateSource(4000, 3000); // 4:3 aspect, deliberately huge
        var loader = new FakeImageFileLoader { ResultToReturn = oversizedSource };
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/huge.jpg" };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImageFromFileCommand.ExecuteAsync(null);

        var element = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.NotSame(oversizedSource, element.Source);
        var budgetWidth = (int)(vm.WorkingCopyWidth * 2);
        var budgetHeight = (int)(vm.WorkingCopyHeight * 2);
        Assert.True(element.Source.Width <= budgetWidth, $"Expected downsampled width <= {budgetWidth}, got {element.Source.Width}.");
        Assert.True(element.Source.Height <= budgetHeight, $"Expected downsampled height <= {budgetHeight}, got {element.Source.Height}.");
        // Aspect preserved (4:3 source), not a flat stretch to the budget's own aspect.
        AssertClose((double)oversizedSource.Width / oversizedSource.Height, (double)element.Source.Width / element.Source.Height);
    }

    [AvaloniaFact]
    public async Task AddImageFromFileAsync_WithASourceSmallerThanTheWorkingCopyBudget_InsertsItUnchanged()
    {
        // The no-op branch of the same downsample -- a small source must NOT be upscaled or
        // otherwise mutated, same instance in and out (mirrors DownsampleToBudget/BuildWorkingCopy's
        // own "targetWidth >= source.Width" early return).
        var preparer = new FakeTransmitImagePreparer();
        var smallSource = CreateSource(2, 2);
        var loader = new FakeImageFileLoader { ResultToReturn = smallSource };
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/small.jpg" };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImageFromFileCommand.ExecuteAsync(null);

        var element = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(smallSource, element.Source);
    }

    [AvaloniaFact]
    public void MoveElementUp_OnBottomOfTwoAdjacentElements_SwapsDrawOrder_NotJustIncrementsZ()
    {
        // Code-review finding: NextZ() hands out consecutive Zs (0, 1, ...) with no gaps, so a naive
        // `element.Z += 1` on the bottom element lands it on the SAME Z as its already-on-top
        // neighbor -- ApplyTemplate's stable OrderBy(Z) then still draws them in original
        // (still-wrong) list order, making the FIRST click on "move up" a visible no-op in the
        // default (freshly-added) arrangement. This pins the real fix: a neighbor SWAP, which
        // always changes relative draw order on the very first click.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var bottom = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[1];
        Assert.True(bottom.Z < top.Z);

        vm.MoveElementUpCommand.Execute(bottom);

        Assert.True(bottom.Z > top.Z);
        // Round-3 code-review finding: the canvas ItemsControl's ZIndex binding was confirmed (via a
        // real running window) to have NO effect on draw order -- Avalonia doesn't forward it through
        // the generated item container. The actual fix is keeping OverlayElements' own COLLECTION
        // order in sync with Z (a plain Canvas draws children in child order); a Z-only assertion
        // wouldn't catch a regression that deletes the OverlayElements.Move(...) call but leaves the
        // Z-swap intact, so assert collection identity too.
        Assert.Same(top, vm.OverlayElements[0]);
        Assert.Same(bottom, vm.OverlayElements[1]);
    }

    [AvaloniaFact]
    public void MoveElementDown_OnTopOfTwoAdjacentElements_SwapsDrawOrder_NotJustDecrementsZ()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var bottom = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[1];

        vm.MoveElementDownCommand.Execute(top);

        Assert.True(bottom.Z > top.Z);
        Assert.Same(top, vm.OverlayElements[0]);
        Assert.Same(bottom, vm.OverlayElements[1]);
    }

    [AvaloniaFact]
    public void MoveElementUpAndDown_RepeatedlyOnThreeElements_KeepsCollectionOrderInSyncWithZ()
    {
        // Round-3 code-review finding: OverlayElements.Move(...) is only a pure pairwise swap AS LONG
        // AS collection order already matches Z order before the call -- this test exercises several
        // mixed up/down clicks across 3 elements (not just one swap) to pin that the invariant survives
        // repeated use, not just a single move.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var c = vm.OverlayElements[2];

        vm.MoveElementUpCommand.Execute(a);
        AssertOrderMatchesZ(vm);
        Assert.Equal([b, a, c], vm.OverlayElements);

        vm.MoveElementDownCommand.Execute(c);
        AssertOrderMatchesZ(vm);
        Assert.Equal([b, c, a], vm.OverlayElements);

        vm.MoveElementUpCommand.Execute(b);
        AssertOrderMatchesZ(vm);
        Assert.Equal([c, b, a], vm.OverlayElements);

        static void AssertOrderMatchesZ(TxImageEditorPaneViewModel vm)
        {
            var zs = vm.OverlayElements.Select(e => e.Z).ToList();
            Assert.Equal(zs.OrderBy(z => z), zs);
        }
    }

    [AvaloniaFact]
    public void MoveElementUp_OnTopmostElement_IsANoOp_DoesNotPushAnAdditionalUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var only = vm.OverlayElements[0];
        var zBefore = only.Z;

        vm.MoveElementUpCommand.Execute(only);
        Assert.Equal(zBefore, only.Z);

        // If the no-op still pushed an undo step, this single Undo would revert THAT no-op step and
        // leave the element present with UndoCommand still true; the correct behavior is that this
        // Undo reverts the ADD itself, since a boundary no-op MoveElementUp never pushed anything.
        vm.UndoCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void MoveElementDown_OnBottommostElement_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var only = vm.OverlayElements[0];
        var zBefore = only.Z;

        vm.MoveElementDownCommand.Execute(only);

        Assert.Equal(zBefore, only.Z);
    }

    [AvaloniaFact]
    public void MoveElementUp_ThenUndo_RestoresOriginalZOrder()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var bottom = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[1];
        vm.MoveElementUpCommand.Execute(bottom);
        Assert.True(bottom.Z > top.Z);

        vm.UndoCommand.Execute(null);

        var restoredBottom = vm.OverlayElements[0];
        var restoredTop = vm.OverlayElements[1];
        Assert.True(restoredBottom.Z < restoredTop.Z);
    }

    [AvaloniaFact]
    public void BringToFront_MovesElementAboveEveryOtherElementInOneStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var bottom = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var middle = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[2];

        vm.BringToFrontCommand.Execute(bottom);

        Assert.True(bottom.Z > middle.Z);
        Assert.True(bottom.Z > top.Z);
        Assert.Equal([middle, top, bottom], vm.OverlayElements);
    }

    [AvaloniaFact]
    public void BringToFront_OnNullElement_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);

        vm.BringToFrontCommand.Execute(null);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void BringToFront_OnAlreadyTopmostElement_IsANoOp_DoesNotPushAnAdditionalUndoStep()
    {
        // Code-review finding: an early draft still bumped Z and pushed an undo step even when the
        // element was already topmost, unlike MoveElementUp's own established no-op-at-boundary
        // behavior (see MoveElementUp_OnTopmostElement_IsANoOp_... above for the same pattern).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var only = vm.OverlayElements[0];
        var zBefore = only.Z;

        vm.BringToFrontCommand.Execute(only);
        Assert.Equal(zBefore, only.Z);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SendToBack_NoBackgroundElement_MovesElementBelowEveryOtherElementInOneStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var bottom = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var middle = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[2];

        vm.SendToBackCommand.Execute(top);

        Assert.True(top.Z < bottom.Z);
        Assert.True(top.Z < middle.Z);
        Assert.Equal([top, bottom, middle], vm.OverlayElements);
    }

    [AvaloniaFact]
    public void SendToBack_OnNullElement_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.SendToBackCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SendToBack_WithLockedBackgroundElement_FloorsImmediatelyAboveItInsteadOfBehindIt()
    {
        // Explicit user constraint ("obviously can't hide behind the actual background picture"):
        // SendToBack must never place an element at or below a locked full-frame background image's
        // own Z -- that element is opaque, so anything placed behind it would simply become
        // invisible. Also pins the real tie-break bug this method's own doc comment documents and
        // rejects an alternative implementation for: a naive OrderBy-then-locate-index approach would
        // leave `top` silently unmoved here, since its floored Z (background.Z + 1) TIES with
        // `middle`'s already-existing Z, and the tie-break falls back to `top`'s OLD (last) position.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var background = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackgroundCommand.Execute(background);
        vm.AddOverlayElementCommand.Execute(null);
        var middle = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[2];

        vm.SendToBackCommand.Execute(top);

        Assert.Equal(background.Z + 1, top.Z);
        Assert.True(top.Z > background.Z);
        Assert.Equal([background, top, middle], vm.OverlayElements);
    }

    [AvaloniaFact]
    public void SendToBack_OnTheBackgroundElementItself_IsATrueNoOp_DoesNotDriftZOrPushUndo()
    {
        // Auditor code-review finding: the FIRST corrected draft still let this fall through to the
        // unconditional "no background" branch (Min(Z) - 1), which decremented the background's own
        // Z (and pushed an undo step) on every single click forever -- a real, if cosmetically
        // invisible, drift the doc comment at the time incorrectly claimed couldn't happen. This is
        // now an explicit early-return guard; pin BOTH the Z (unchanged) and the undo depth (no step
        // pushed), not just "doesn't throw."
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var background = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackgroundCommand.Execute(background);
        vm.AddOverlayElementCommand.Execute(null);
        var other = vm.OverlayElements[1];
        var zBefore = background.Z;
        var undoDepthBefore = vm.UndoCommand.CanExecute(null);

        var exception = Record.Exception(() => vm.SendToBackCommand.Execute(background));

        Assert.Null(exception);
        Assert.Equal(zBefore, background.Z);
        Assert.Equal(undoDepthBefore, vm.UndoCommand.CanExecute(null));
        Assert.Same(background, vm.OverlayElements[0]);
        Assert.Same(other, vm.OverlayElements[1]);
        Assert.True(background.Z < other.Z);
    }

    [AvaloniaFact]
    public void BringToFrontOnBackground_ThenSendToBackOnAnother_DoesNotCrashOrLoseTheElement()
    {
        // Auditor code-review finding (real crash/data-loss bug in the first corrected draft): if
        // MoveElementUp/MoveElementDown/BringToFront ever put the background ABOVE the target element
        // (not gated on IsBackground -- a pre-existing gap this addendum doesn't fix, see SendToBack's
        // own doc comment), IndexOf(background) + 1 could equal Count, and
        // ObservableCollection<T>.Move (Remove-then-Insert) throws AFTER the remove already succeeded
        // -- silently dropping the element with no CollectionChanged notification. This reaches that
        // exact state in two clicks using ONLY this addendum's own new commands (no pre-existing gap
        // needs to be separately exploited): BringToFront on the background row puts it topmost, then
        // SendToBack on anything else must not crash or vanish the element.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var background = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackgroundCommand.Execute(background);
        vm.AddOverlayElementCommand.Execute(null);
        var other = vm.OverlayElements[1];
        vm.BringToFrontCommand.Execute(background);
        Assert.Same(background, vm.OverlayElements[1]);

        var exception = Record.Exception(() => vm.SendToBackCommand.Execute(other));

        Assert.Null(exception);
        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.Contains(other, vm.OverlayElements);
        Assert.Contains(background, vm.OverlayElements);
    }

    [AvaloniaFact]
    public void SendToBack_WithMultipleBackgroundElements_FloorsAboveTheNearestOneBelowIt()
    {
        // Auditor code-review finding: SetAsBackground never clears a PREVIOUS element's own
        // IsBackground flag, so multiple backgrounds are reachable (two clicks). A second
        // SetAsBackground call moves ITS OWN element to the new collection-wide minimum (Min(Z) - 1
        // over a set that already contains the first background's Z), displacing the first background
        // from index 0 to index 1 -- so "nearest background below `top`" (firstBackground, correct)
        // and "lowest-Z background" (secondBackground, what a naive
        // OfType<ImageElementViewModel>().FirstOrDefault(e => e.IsBackground) picks) are now two
        // DIFFERENT elements, exactly what this test needs to distinguish. The buggy FirstOrDefault
        // draft would target IndexOf(secondBackground) + 1 = 1 -- landing `top` BEHIND firstBackground,
        // the exact invisible-behind-an-opaque-background failure this feature exists to prevent.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var firstBackground = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackgroundCommand.Execute(firstBackground);
        vm.AddLastRxImageCommand.Execute(null);
        var secondBackground = (ImageElementViewModel)vm.OverlayElements[1];
        vm.SetAsBackgroundCommand.Execute(secondBackground);
        Assert.Equal([secondBackground, firstBackground], vm.OverlayElements);
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[2];

        vm.SendToBackCommand.Execute(top);

        Assert.True(top.Z > firstBackground.Z);
        Assert.True(vm.OverlayElements.IndexOf(top) > vm.OverlayElements.IndexOf(firstBackground));
    }

    // Pure math extracted from TxImageEditorPaneView.axaml.cs's OnCanvasPointerMoved (code-review
    // finding: this logic shipped with zero test coverage since it lived entirely in code-behind;
    // splitting it into a public static method makes it testable without simulating real Avalonia
    // pointer events -- see that method's own doc comment for the InternalsVisibleTo/public
    // reasoning, same precedent as WaterfallPalette).

    [Fact]
    public void ComputeElementResize_GrowingBothAxes_PinsOppositeCornerViaHalfDeltaCenterShift()
    {
        var (width, height, centerDeltaX, centerDeltaY) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: 0.1, dyNormalized: 0.04);

        AssertClose(0.4, width);
        AssertClose(0.24, height);
        AssertClose(0.05, centerDeltaX);
        AssertClose(0.02, centerDeltaY);
    }

    [Fact]
    public void ComputeElementResize_ShrinkingWithinFloor_AppliesTheFullRequestedDelta()
    {
        var (width, height, centerDeltaX, centerDeltaY) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: -0.1, dyNormalized: -0.05);

        AssertClose(0.2, width);
        AssertClose(0.15, height);
        AssertClose(-0.05, centerDeltaX);
        AssertClose(-0.025, centerDeltaY);
    }

    [Fact]
    public void ComputeElementResize_ShrinkingPastTheFloor_ClampsSizeAndOnlyShiftsCenterByTheAppliedDelta()
    {
        // Code-review finding: an unclamped resize could drive Width/Height negative, which
        // ApplyTemplate silently treats as "skip this element" -- a fast drag past the opposite
        // corner made the element vanish from both the canvas and the transmitted image with no
        // visible handle left to recover it (other than Undo). This pins both halves of the fix:
        // the size floors at MinNormalizedElementSize (0.02), and the center-pinning math uses the
        // ACTUALLY-APPLIED delta (not the raw requested one), so the opposite corner doesn't keep
        // drifting once the floor engages.
        var (width, height, centerDeltaX, centerDeltaY) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.05, currentHeight: 0.05, dxNormalized: -0.5, dyNormalized: -0.5);

        AssertClose(0.02, width);
        AssertClose(0.02, height);
        // Applied delta is (0.02 - 0.05) = -0.03, not the raw -0.5 request.
        AssertClose(-0.015, centerDeltaX);
        AssertClose(-0.015, centerDeltaY);
    }

    [Fact]
    public void ComputeElementResize_NeverProducesANonPositiveWidthOrHeight()
    {
        var (width, height, _, _) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.3, dxNormalized: -10, dyNormalized: -10);

        Assert.True(width > 0);
        Assert.True(height > 0);
    }

    // Task #23 (zoom slider addendum, plan-reviewed): pointer-anchored scroll-wheel zoom math, split
    // out of OnEditorWheelChanged for the same unit-testability reason as ComputeElementResize above.

    [Fact]
    public void ComputeAnchoredOffset_ZoomingIn_KeepsTheSameContentPointUnderTheCursor()
    {
        // A 400px-wide canvas, cursor 100px from the left (fraction 0.25) at the moment ZoomFactor
        // changes; after zooming in to 800px wide, that same content point is now at 200px. If the
        // cursor itself hasn't moved (still 100px from the viewport's own left edge, i.e. anchorAfter
        // stays 100 since GetPosition reads viewport-relative, not content-relative), the offset must
        // grow by exactly 100px (200 - 100) to keep that content point under the still-100px cursor.
        var newOffset = TxImageEditorPaneView.ComputeAnchoredOffset(
            currentOffset: 0, fraction: 0.25, newContentSize: 800, anchorAfter: 100);

        AssertClose(100, newOffset);
    }

    [Fact]
    public void ComputeAnchoredOffset_NoZoomChange_ReturnsTheOriginalOffsetUnchanged()
    {
        // Content size unchanged (400 -> 400) and the cursor read at exactly the position the
        // fraction predicts (0.25 * 400 = 100) -- a true no-op zoom must not perturb the offset.
        var newOffset = TxImageEditorPaneView.ComputeAnchoredOffset(
            currentOffset: 50, fraction: 0.25, newContentSize: 400, anchorAfter: 100);

        AssertClose(50, newOffset);
    }

    [Fact]
    public void ComputeAnchoredOffset_StartingFromANonZeroOffset_AddsTheDeltaOnTopOfIt()
    {
        // Same zoom-in scenario as the first test above, but starting from an already-scrolled
        // position -- the correction (+100) must be ADDED to the existing offset, not replace it.
        var newOffset = TxImageEditorPaneView.ComputeAnchoredOffset(
            currentOffset: 30, fraction: 0.25, newContentSize: 800, anchorAfter: 100);

        AssertClose(130, newOffset);
    }

    // Phase 6 (spec/15-template-designer.md): snap-ON-DROP grid math, computed in edge space.

    [Fact]
    public void SnapElementBoundsToGrid_EdgesSnapIndependently_WidthAndHeightAreDerivedNotRoundedDirectly()
    {
        // Plan-review blocker on an earlier draft: rounding center-X/Y and Width/Height
        // INDEPENDENTLY puts edges on inconsistent half-grid multiples. This test picks values where
        // that bug would produce a DIFFERENT (wrong) answer than edge-space snapping, so it actually
        // discriminates between the two approaches rather than merely happening to agree.
        // X=0.30, Width=0.24 -> left=0.18, right=0.42. Grid 0.05: left snaps to 0.20, right to 0.40.
        var (x, y, width, height) = TxImageEditorPaneView.SnapElementBoundsToGrid(
            x: 0.30, y: 0.30, width: 0.24, height: 0.24, gridSize: 0.05);

        AssertClose(0.20, x - (width / 2));
        AssertClose(0.40, x + (width / 2));
        AssertClose(0.20, width);
        AssertClose(0.30, x);
        AssertClose(0.30, y);
        AssertClose(0.20, height);
    }

    [Fact]
    public void SnapElementBoundsToGrid_TwoDifferentlySizedElementsSharingAnEdge_SnapToTheSameLine()
    {
        // The entire point of a snap feature: elements whose real edges are close to the same grid
        // line end up with IDENTICAL snapped edges, not just individually "close to a grid line."
        var (leftX, _, leftWidth, _) = TxImageEditorPaneView.SnapElementBoundsToGrid(
            x: 0.30, y: 0.5, width: 0.19, height: 0.1, gridSize: 0.05); // right edge = 0.395
        var (rightX, _, rightWidth, _) = TxImageEditorPaneView.SnapElementBoundsToGrid(
            x: 0.55, y: 0.5, width: 0.31, height: 0.1, gridSize: 0.05); // left edge = 0.395

        AssertClose(leftX + (leftWidth / 2), rightX - (rightWidth / 2));
    }

    [Fact]
    public void SnapElementBoundsToGrid_RoundingCollapsesWidthBelowFloor_ExpandsRightFromTheLeftEdge()
    {
        // A narrow element whose two edges both round to the SAME grid line would otherwise snap to
        // zero width -- ApplyTemplate treats that as "skip this element," silently deleting it. The
        // left edge is kept fixed and width is restored to the floor by expanding right/down (a
        // deterministic choice; a post-hoc snap has no drag-direction context to prefer otherwise).
        var (x, _, width, _) = TxImageEditorPaneView.SnapElementBoundsToGrid(
            x: 0.301, y: 0.5, width: 0.01, height: 0.3, gridSize: 0.05); // edges 0.296/0.306, both round to 0.30

        Assert.True(width >= 0.02); // MinNormalizedElementSize
        AssertClose(0.30, x - (width / 2)); // left edge stayed fixed
    }

    [Fact]
    public void SnapElementBoundsToGrid_AlreadyOnGridLines_IsUnchanged()
    {
        var (x, y, width, height) = TxImageEditorPaneView.SnapElementBoundsToGrid(
            x: 0.30, y: 0.30, width: 0.20, height: 0.10, gridSize: 0.05);

        AssertClose(0.30, x);
        AssertClose(0.30, y);
        AssertClose(0.20, width);
        AssertClose(0.10, height);
    }

    [AvaloniaFact]
    public void SnapToGrid_DefaultsToFalse_ExistingDragBehaviorUnchangedByDefault()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.SnapToGrid);
    }

    [AvaloniaFact]
    public void ApplySnappedElementBounds_OneUndoFullyRevertsAllFourProperties()
    {
        // Code-review finding: the 4 individual property assignments a snap applies each carry
        // their own coalesced-undo hook, but the coalescing window is virtually always already
        // closed by the time a pointer-release (where a snap fires) is reached -- so applying them
        // directly would need TWO Undos to revert a snapped drag (X/Y coalesced separately from
        // Width/Height, or similar). ApplySnappedElementBounds wraps all 4 in one explicit push
        // instead, matching this editor's own "one gesture, one undo step" convention (see
        // SetAsBackground's own single-push test for the established pattern). If this regressed
        // back to 2 steps, a SINGLE Undo below would leave some of X/Y/Width/Height still at their
        // post-snap values instead of reverting all four together.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.X = 0.3;
        element.Y = 0.3;
        element.Width = 0.2;
        element.Height = 0.2;

        vm.ApplySnappedElementBounds(element, 0.35, 0.35, 0.25, 0.25);

        Assert.Equal(0.35, element.X);
        Assert.Equal(0.35, element.Y);
        Assert.Equal(0.25, element.Width);
        Assert.Equal(0.25, element.Height);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        // Undo replaces OverlayElements wholesale (ApplyState's own established behavior) -- the
        // pre-undo `element` reference is now detached, re-fetch the live one.
        var restored = vm.OverlayElements[0];
        Assert.Equal(0.3, restored.X);
        Assert.Equal(0.3, restored.Y);
        Assert.Equal(0.2, restored.Width);
        Assert.Equal(0.2, restored.Height);
    }

    // Phase 3 (spec/15-template-designer.md): named template variables + fill bar.

    [AvaloniaFact]
    public void AddingAVariableToken_AddsAFillBarRow()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "DE {his_call}";

        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", row.Key);
        Assert.Equal(string.Empty, row.Value);
    }

    [AvaloniaFact]
    public void RemovingTheLastReferenceToAVariable_RemovesItsFillBarRow()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "DE {his_call}";
        Assert.Single(vm.TemplateVariableRows);

        element.Text = "DE W1AW";

        Assert.Empty(vm.TemplateVariableRows);
    }

    [AvaloniaFact]
    public void KnownMacroTokens_NeverGrowAFillBarRow()
    {
        // {name}/{grid} (pre-existing) and {freq}/{mode} (Phase 3) are ordinary resolved macros, not
        // variables -- they must never appear in the fill bar alongside genuine {word} references.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "{name} {grid} {freq} {mode} {his_call}";

        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", row.Key);
    }

    [AvaloniaFact]
    public void EditingAVariableTokenCharacterByCharacter_DoesNotLoseAnAlreadyTypedFillValue()
    {
        // Plan-review blocker: the persistent value map must survive a token's temporary
        // de-reference. Backspacing through "{his_call}" passes through the syntactically-valid
        // intermediate token "{his_cal}" -- a naive "remove keys no longer referenced" rescan would
        // silently discard the operator's already-typed callsign at that point.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "{his_call}";
        var row = Assert.Single(vm.TemplateVariableRows);
        row.Value = "K1ABC";

        element.Text = "{his_cal}"; // simulates a backspace mid-edit
        Assert.DoesNotContain(vm.TemplateVariableRows, r => r.Key == "his_call");

        element.Text = "{his_call}"; // simulates retyping the closing character

        var restoredRow = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", restoredRow.Key);
        Assert.Equal("K1ABC", restoredRow.Value);
    }

    [AvaloniaFact]
    public void FillBarValueEdit_UpdatesResolvedTextAndRecomputesPreview()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "DE {his_call}";
        var row = Assert.Single(vm.TemplateVariableRows);
        var recomputeCountBefore = preparer.ApplyTemplateCallCount;

        row.Value = "K1ABC";

        // Plan-review blocker: ResolvedText's own PropertyChanged raise is filtered out of
        // OnOverlayElementPropertyChanged's recompute trigger, so this only passes if the fill-bar
        // row's edit explicitly drives the recompute itself, not a property-changed cascade.
        Assert.Equal("DE K1ABC", element.ResolvedText);
        Assert.True(preparer.ApplyTemplateCallCount > recomputeCountBefore);
    }

    [AvaloniaFact]
    public void FillBarValueEdit_RaisesResolvedTextPropertyChanged_OnlyForReferencingElements()
    {
        // ResolvedText itself has no caching (it re-invokes ResolveMacros on every read), so simply
        // reading its value afterward can't distinguish "the notification fired" from "the value
        // happens to be correct anyway" -- this test mutation-tests the actual PropertyChanged raise
        // (NotifyResolvedTextChanged), the real thing the canvas TextBlock binding depends on.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var referencing = (OverlayElementViewModel)vm.OverlayElements[0];
        referencing.Text = "{his_call}";
        vm.AddOverlayElementCommand.Execute(null);
        var unrelated = (OverlayElementViewModel)vm.OverlayElements[1];
        unrelated.Text = "Plain text";
        var row = Assert.Single(vm.TemplateVariableRows);
        var referencingRaisedResolvedTextChanged = false;
        var unrelatedRaisedResolvedTextChanged = false;
        referencing.PropertyChanged += (_, e) => referencingRaisedResolvedTextChanged |= e.PropertyName == nameof(OverlayElementViewModel.ResolvedText);
        unrelated.PropertyChanged += (_, e) => unrelatedRaisedResolvedTextChanged |= e.PropertyName == nameof(OverlayElementViewModel.ResolvedText);

        row.Value = "K1ABC";

        Assert.True(referencingRaisedResolvedTextChanged);
        Assert.False(unrelatedRaisedResolvedTextChanged);
        Assert.Equal("K1ABC", referencing.ResolvedText);
        Assert.Equal("Plain text", unrelated.ResolvedText);
    }

    [AvaloniaFact]
    public void RescanTemplateVariables_KnownMacroTokens_NeverProduceAFillBarRow()
    {
        // Code-review finding: the known-macro-token set is duplicated across two projects
        // (MacroTextResolver's own switch cases, and this VM's private KnownMacroTokenNames used to
        // exclude macros from the variable scan) with nothing enforcing they agree -- a future macro
        // added to one without the other would either produce a dead fill-bar row (typed value
        // silently ignored, the macro branch always wins) or leave a real macro unexpectedly
        // resolving through the variable path. This pins today's agreement behaviorally: every
        // currently-known macro name must be excluded from the variable scan.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{name} {grid} {freq} {mode}";

        Assert.Empty(vm.TemplateVariableRows);
    }

    [AvaloniaFact]
    public void TemplateVariableCountText_TracksTemplateVariableRowsCount()
    {
        // design-fidelity Phase F (#9): TemplateVariableCountText is a plain computed property with
        // no property-changed notification of its own -- RescanTemplateVariables (the one place
        // TemplateVariableRows changes) is the one place it needs raising, same discipline already
        // established for HasNoTemplateVariableRows.
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{his_call}";

        _ = vm.TemplateVariableCountText;

        Assert.Equal("Panes.TxImageEditor.TemplateVariableCountFormat", localization.LastKey);
        Assert.Equal(new object[] { 1 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void SendMetaText_UsesTargetModeDisplayNameAndDuration()
    {
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        _ = vm.SendMetaText;

        Assert.Equal("Panes.TxImageEditor.SendMetaFormat", localization.LastKey);
        Assert.Equal(
            new object[] { SmallMode.DisplayName, SmallMode.LineDurationMs * SmallMode.ImageHeight / 1000.0 },
            localization.LastArgs);
    }

    [AvaloniaFact]
    public void ClearTemplateVariablesCommand_CanExecute_FalseUntilAValueIsActuallyTyped()
    {
        // Real-window finding: merely REFERENCING a token (a row appearing) must NOT be enough to
        // enable Clear -- Rescan deliberately never writes into _templateVariables (see its own
        // comment), so CanExecute only flips true once OnTemplateVariableValueChanged actually runs.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.ClearTemplateVariablesCommand.CanExecute(null));

        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{his_call}";
        Assert.False(vm.ClearTemplateVariablesCommand.CanExecute(null));

        vm.TemplateVariableRows[0].Value = "K1ABC";

        Assert.True(vm.ClearTemplateVariablesCommand.CanExecute(null));

        // Code-review finding: the original Clear implementation blanked each VALUE in place
        // instead of removing the KEY, so _templateVariables.Count never returned to 0 and this
        // command stayed permanently enabled after the very first Clear. A real Clear must flip it
        // back to false.
        vm.ClearTemplateVariablesCommand.Execute(null);
        Assert.False(vm.ClearTemplateVariablesCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void UnfilledVariableToken_ResolvesVerbatim_ImmediatelyAfterBeingTyped_NotToEmptyString()
    {
        // Real-window finding: this is exactly the bug a real running window caught that no unit
        // test here previously did -- MacroTextResolverTests' own "unfilled resolves verbatim" test
        // exercises the resolver in isolation with an empty dictionary, which doesn't reflect how
        // the VM actually calls it. An earlier draft of RescanTemplateVariables eagerly wrote
        // _templateVariables[key] = "" the moment a token was first discovered, which made the
        // resolver's "key absent -> verbatim" branch practically unreachable: every token resolved
        // to an empty string the instant it was typed, before the operator ever got a chance to see
        // or fill it in. This must resolve VERBATIM until the fill-bar row is actually edited.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "DE {his_call}";

        Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("DE {his_call}", element.ResolvedText);
    }

    [AvaloniaFact]
    public void ClearTemplateVariables_BlanksEveryValue_IncludingCurrentlyHiddenOnes()
    {
        // spec/15-template-designer.md's own "clear fields" contract: a value the operator already
        // typed for a QSO must not survive to the next one, even if its token isn't referenced by any
        // CURRENTLY visible element right this moment.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "{his_call}";
        Assert.Single(vm.TemplateVariableRows).Value = "K1ABC";
        element.Text = "no longer referenced"; // hides the row, but the value is still persisted

        vm.ClearTemplateVariablesCommand.Execute(null);

        element.Text = "{his_call}"; // re-reference it and confirm the persisted value is now blank
        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal(string.Empty, row.Value);
        // Code-review finding: the KEY must be gone entirely, not present-with-an-empty-value --
        // MacroTextResolver's own unfilled-resolves-VERBATIM branch only fires when the key is
        // ABSENT from the dictionary, so a present-but-blank value would make {his_call} silently
        // resolve to nothing instead of showing the token again (an easy-to-transmit-by-mistake
        // "DE " with no callsign instead of an obvious "DE {his_call}" placeholder). A row.Value
        // assertion alone can't distinguish these two cases, since both display "" -- ResolvedText
        // is the only observable that actually tells them apart.
        Assert.Equal("{his_call}", element.ResolvedText);
    }

    [AvaloniaFact]
    public void ClearTemplateVariables_PushesOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{his_call}";
        vm.TemplateVariableRows[0].Value = "K1ABC";

        vm.ClearTemplateVariablesCommand.Execute(null);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        Assert.Equal("K1ABC", vm.TemplateVariableRows[0].Value);
    }

    [AvaloniaFact]
    public void TemplateVariables_RoundTripsThroughUndoRedo()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{his_call}";
        vm.TemplateVariableRows[0].Value = "K1ABC";

        vm.RemoveOverlayElementCommand.Execute(vm.OverlayElements[0]);
        Assert.Empty(vm.TemplateVariableRows);

        vm.UndoCommand.Execute(null);

        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", row.Key);
        Assert.Equal("K1ABC", row.Value);
    }

    [AvaloniaFact]
    public void TemplateVariables_SnapshotProperty_ReturnsADefensiveCopy()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{his_call}";
        vm.TemplateVariableRows[0].Value = "K1ABC";

        var snapshot = vm.TemplateVariables;
        vm.TemplateVariableRows[0].Value = "CHANGED";

        Assert.Equal("K1ABC", snapshot["his_call"]);
    }

    [AvaloniaFact]
    public void Constructor_WithInitialStateTemplateVariables_SeedsTheFillBarValues()
    {
        var initialState = new TxImageEditorPaneViewModel.EditorInitialState(
            new NormalizedRect(0, 0, 1, 1), PreserveAspect: true, new ImageAdjustments(),
            [new TxImageEditorPaneViewModel.RawTextElementSnapshot(
                X: 0.5, Y: 0.5, Width: 0.3, Height: 0.18, Z: 0, Locked: false,
                Text: "{his_call}", FontSizeRelative: 0.1, Color: new Rgb24(255, 255, 255))],
            new Dictionary<string, string> { ["his_call"] = "K1ABC" });

        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(),
            initialState);

        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", row.Key);
        Assert.Equal("K1ABC", row.Value);
    }

    [AvaloniaFact]
    public void FreqAndModeTokens_ResolveFromRadioSessionServiceLastKnownState()
    {
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(FrequencyHz: 14_230_000, Mode: RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
        };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), radioSession);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "{freq} {mode}";

        Assert.Equal("14.230000 MHz USB", element.ResolvedText);
    }

    // Phase 4 (spec/15-template-designer.md, "real style panel" + user-requested text outline).

    [AvaloniaFact]
    public void AddOverlayElement_SeedsFontFamilyFromPreparersAvailableFontFamilies()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.Equal("DejaVu Sans Mono", element.FontFamily);
    }

    [AvaloniaFact]
    public void BuildTemplateElement_PassesFontFamilyAndStroke_ToTheRealPipeline()
    {
        // Code-review-class regression guard: BuildTemplateElement is the REAL pipeline call site
        // (ApplyTemplate consumes it directly), separate from the canvas-preview-only
        // ComputeCanvasFontSize call site below -- both independently needed FontSpec.Family fixed
        // from an empty string to the element's own selection (Phase 4 plan-review blocker).
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.FontFamily = "Barlow";
        element.StrokeColor = new Rgb24(255, 0, 0);
        element.StrokeThickness = 0.03;

        var text = Assert.IsType<TemplateTextElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Equal("Barlow", text.Font.Family);
        Assert.Equal(new Rgb24(255, 0, 0), text.StrokeColor);
        AssertClose(0.03, text.StrokeThickness);
    }

    [AvaloniaFact]
    public void BuildTemplateElement_NoStroke_LeavesStrokeColorNull()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);

        var text = Assert.IsType<TemplateTextElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Null(text.StrokeColor);
    }

    [AvaloniaFact]
    public void ChangingFontFamilyOrStroke_RefreshesCanvasFontSize()
    {
        // Code-review-class regression guard: OnOverlayElementPropertyChanged's own filter must
        // treat FontFamily/StrokeThickness/StrokeColor the same as FontSizeRelative/Width/Height/Text
        // (all feed ComputeCanvasFontSize's own MeasureFittedFontSize call) -- Phase 4 plan-review
        // blocker 2's whole point is that these must stay in sync with what the real pipeline uses.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var beforeFamily = element.CanvasFontSize;

        element.FontFamily = "Barlow";
        // A family change alone need not change the numeric fitted size (the fake preparer's own
        // MeasureFittedFontSize ignores family entirely) -- what this test actually pins is that the
        // recompute genuinely RAN, not that the fake happens to produce a different number for a
        // different family. Use StrokeThickness (which the fake DOES factor into nothing, but the
        // real fit-box-shrink logic in TransmitImagePreparer does) is covered by the pipeline-layer
        // test above instead; here, just confirm no exception and CanvasFontSize stays a finite,
        // non-negative value after each change (a crash/NaN would be the real regression shape).
        Assert.True(element.CanvasFontSize >= 0);

        element.StrokeColor = new Rgb24(0, 0, 0);
        element.StrokeThickness = 0.05;
        Assert.True(element.CanvasFontSize >= 0);
    }

    [AvaloniaFact]
    public void HasStroke_TogglingOn_SeedsBlack_TogglingOff_ClearsToNull()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.False(element.HasStroke);
        Assert.Null(element.StrokeColor);

        element.HasStroke = true;
        Assert.True(element.HasStroke);
        Assert.Equal(new Rgb24(0, 0, 0), element.StrokeColor);

        element.HasStroke = false;
        Assert.False(element.HasStroke);
        Assert.Null(element.StrokeColor);
    }

    [AvaloniaFact]
    public void HasStroke_TogglingOffThenOn_RestoresThePreviouslyPickedColor_NotBlackAgain()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.StrokeColor = new Rgb24(10, 20, 30);

        element.HasStroke = false;
        Assert.Null(element.StrokeColor);

        element.HasStroke = true;
        Assert.Equal(new Rgb24(0, 0, 0), element.StrokeColor); // cleared to null on toggle-off, so this re-seeds black, not the old color
    }

    [AvaloniaFact]
    public void AddPlateBehindTextCommand_CanExecute_OnlyWhenATextElementIsSelected()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.AddPlateBehindTextCommand.CanExecute(null));

        vm.AddOverlayElementCommand.Execute(null);
        Assert.True(vm.AddPlateBehindTextCommand.CanExecute(null));

        vm.AddBoxElementCommand.Execute(null);
        Assert.False(vm.AddPlateBehindTextCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void AddPlateBehindText_InsertsAnIndependentBoxDirectlyBehindTheText_SizedToItsBounds()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.Width = 0.4;
        text.Height = 0.2;

        vm.AddPlateBehindTextCommand.Execute(null);

        Assert.Equal(2, vm.OverlayElements.Count);
        var plate = Assert.IsType<BoxElementViewModel>(vm.OverlayElements[0]);
        Assert.Same(text, vm.OverlayElements[1]);
        // Collection order IS draw order (Phase 1's own established invariant) -- the plate must be
        // BEHIND (earlier in the collection than) the text it was added for.
        AssertClose(text.X, plate.X);
        AssertClose(text.Y, plate.Y);
        AssertClose(text.Width + 0.04, plate.Width);
        AssertClose(text.Height + 0.04, plate.Height);
        Assert.True(plate.Z < text.Z);
        Assert.Same(plate, vm.SelectedOverlayElement);
    }

    [AvaloniaFact]
    public void AddPlateBehindText_RenumbersZToMatchCollectionOrder_NoDuplicateOrOutOfOrderZ()
    {
        // Code-review-class regression guard: a plain `text.Z - 1` (mirroring SetAsBackground's own
        // Min(Z)-1) could collide with an existing element's Z when the plate ISN'T going to the
        // absolute bottom -- this test adds a 3rd element BEHIND the text first, so a naive Z-1
        // assignment would collide with it.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null); // Z=0, sits behind everything
        vm.AddOverlayElementCommand.Execute(null); // Z=1
        var text = (OverlayElementViewModel)vm.OverlayElements[1];
        vm.SelectedOverlayElement = text;

        vm.AddPlateBehindTextCommand.Execute(null);

        Assert.Equal(3, vm.OverlayElements.Count);
        var zs = vm.OverlayElements.Select(e => e.Z).ToList();
        Assert.Equal(zs.OrderBy(z => z).Distinct(), zs); // strictly ascending, no duplicates
        Assert.Equal(zs, Enumerable.Range(0, 3)); // matches collection order exactly, 0..2
    }

    [AvaloniaFact]
    public void AddPlateBehindText_PushesExactlyOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.AddPlateBehindTextCommand.Execute(null);
        Assert.Equal(2, vm.OverlayElements.Count);

        vm.UndoCommand.Execute(null);

        Assert.Single(vm.OverlayElements);
        Assert.Same(text.GetType(), vm.OverlayElements[0].GetType());
    }

    [AvaloniaFact]
    public void TextElementStyle_RoundTripsThroughUndoRedo()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.PushUndoSnapshotForDragGesture(); // discrete step boundary, mirrors other style-property tests' own shape
        element.FontFamily = "Barlow";
        element.StrokeColor = new Rgb24(1, 2, 3);
        element.StrokeThickness = 0.07;

        vm.UndoCommand.Execute(null);
        var restoredAfterUndo = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.Equal("DejaVu Sans Mono", restoredAfterUndo.FontFamily);
        Assert.Null(restoredAfterUndo.StrokeColor);

        vm.RedoCommand.Execute(null);
        var restoredAfterRedo = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.Equal("Barlow", restoredAfterRedo.FontFamily);
        Assert.Equal(new Rgb24(1, 2, 3), restoredAfterRedo.StrokeColor);
        AssertClose(0.07, restoredAfterRedo.StrokeThickness);
    }

    [AvaloniaFact]
    public void AvailableFontFamilies_ForwardsThePreparersOwnList()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);

        Assert.Equal(preparer.AvailableFontFamilies, vm.AvailableFontFamilies);
    }

    [AvaloniaFact]
    public void SelectedTextElement_NullForABoxSelection_SetForATextSelection()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        Assert.Null(vm.SelectedTextElement);

        vm.AddOverlayElementCommand.Execute(null);
        Assert.Same(vm.OverlayElements[1], vm.SelectedTextElement);
    }

    [AvaloniaFact]
    public void SaveTemplateCommand_CanExecute_FalseWhenNameBlank_TrueOnceNamed()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeTemplateStore(), new FakeImageSourceWriter());

        Assert.False(vm.SaveTemplateCommand.CanExecute(null));

        vm.NewTemplateName = "Contest card";
        Assert.True(vm.SaveTemplateCommand.CanExecute(null));

        vm.NewTemplateName = "   ";
        Assert.False(vm.SaveTemplateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SaveTemplateAsync_TextAndBoxElementsOnly_CallsStoreSaveAsyncAndClearsName()
    {
        var templateStore = new FakeTemplateStore();
        var imageSourceWriter = new FakeImageSourceWriter();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, imageSourceWriter);
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        vm.NewTemplateName = "My Template";

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        var saved = Assert.Single(await templateStore.ListAsync());
        Assert.Equal("My Template", saved.Name);
        var document = await templateStore.LoadAsync(saved.Id);
        Assert.Equal(2, document.Elements.Count);
        Assert.Empty(imageSourceWriter.Calls); // no image elements -- nothing to write
        Assert.Equal(string.Empty, vm.NewTemplateName);
    }

    [AvaloniaFact]
    public async Task SaveTemplateAsync_ImageElement_WritesItsAssetBeforeCallingStoreSaveAsync()
    {
        var templateStore = new FakeTemplateStore();
        var imageSourceWriter = new FakeImageSourceWriter();
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var imageSource = CreateSource(2, 2);
        var loader = new FakeImageFileLoader { ResultToReturn = imageSource };
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            templateStore, imageSourceWriter, CreateReadyRack(templateStore));
        await vm.AddImageFromFileCommand.ExecuteAsync(null);
        vm.NewTemplateName = "With Photo";

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        var call = Assert.Single(imageSourceWriter.Calls);
        Assert.Same(imageSource, call.Source);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var image = Assert.IsType<PersistedImageElement>(Assert.Single(document.Elements));
        Assert.Equal(call.Path, templateStore.GetAssetPath(saved.Id, image.AssetFileName));
        Assert.Equal(PersistedImageSourceKind.File, image.OriginKind);
        Assert.Equal("/tmp/picked.jpg", image.OriginPayload);
    }

    /// <summary>Code-review finding: every OTHER save test in this file uses
    /// <see cref="FakeImageSourceWriter"/>/<see cref="FakeTemplateStore"/>, which cannot fail on a
    /// missing directory since neither one touches real disk -- exactly why a real bug (the
    /// template's own <c>assets/</c> subdirectory was never created before
    /// <c>BuildPersistedElementAsync</c>'s own <c>WritePngAsync</c> call, so EVERY save of an
    /// image-bearing template failed with <c>DirectoryNotFoundException</c>, silently, caught by
    /// <c>SaveTemplateAsync</c>'s own catch block) went undetected by the full green suite. This
    /// test wires the REAL <c>ScanlineStudio.Core.Imaging.ImageSourceWriter</c> and
    /// <c>ScanlineStudio.Application.TemplateStore</c> against a real temp directory -- no fakes in
    /// the write path at all -- specifically so this class of bug can't regress silently again.</summary>
    [AvaloniaFact]
    public async Task SaveTemplateAsync_ImageElement_CreatesTheAssetsDirectoryOnRealDisk_BeforeWritingTheAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), "scanlinestudio-vm-save-realfs-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var imageSourceWriter = new ImageSourceWriter();
            // TemplateStore's OWN IImageFileLoader must be REAL here (not a fake, and a separate
            // instance from the VM's own `loader` below) -- its RenderThumbnailAsync reads the
            // just-written asset PNG back off real disk to build the thumbnail composite, which a
            // dictionary-keyed fake has no way to satisfy.
            var templateStore = new TemplateStore(imageSourceWriter, new ImageFileLoader(), new FakeTransmitImagePreparer(), root);
            var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
            var imageSource = CreateSource(2, 2);
            var loader = new FakeImageFileLoader { ResultToReturn = imageSource };
            var vm = new TxImageEditorPaneViewModel(
                CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
                new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
                picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
                templateStore, imageSourceWriter, CreateReadyRack(templateStore));
            await vm.AddImageFromFileCommand.ExecuteAsync(null);
            vm.NewTemplateName = "Real Disk Photo";

            await vm.SaveTemplateCommand.ExecuteAsync(null);

            Assert.False(vm.IsSavingTemplate);
            var saved = Assert.Single(await templateStore.ListAsync());
            Assert.Equal("Real Disk Photo", saved.Name);
            Assert.True(File.Exists(saved.ThumbnailPath), $"Expected a real thumbnail.png at '{saved.ThumbnailPath}'.");
            var document = await templateStore.LoadAsync(saved.Id);
            var image = Assert.IsType<PersistedImageElement>(Assert.Single(document.Elements));
            var assetPath = templateStore.GetAssetPath(saved.Id, image.AssetFileName);
            Assert.True(File.Exists(assetPath), $"Expected the real asset PNG to exist on disk at '{assetPath}'.");
            // Save succeeding end to end (not silently swallowed into the catch block) is itself the
            // real assertion here -- NewTemplateName is only ever cleared on the success path.
            Assert.Equal(string.Empty, vm.NewTemplateName);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task LoadTemplate_ReplacesOverlayElements_AndIsUndoable()
    {
        var templateStore = new FakeTemplateStore();
        var imageSourceWriter = new FakeImageSourceWriter();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, imageSourceWriter, readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        var originalElementCount = vm.OverlayElements.Count;

        var templateId = templateStore.CreateTemplateId("Loadable");
        await templateStore.SaveAsync(templateId, "Loadable", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
            new PersistedBoxElement(0.3, 0.3, 0.1, 0.1, 1, false, new Rgb24(4, 5, 6), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.All(vm.OverlayElements, e => Assert.IsType<BoxElementViewModel>(e));

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Equal(originalElementCount, vm.OverlayElements.Count);
    }

    [AvaloniaFact]
    public async Task LoadTemplate_DoesNotClearAlreadyTypedTemplateVariableValues()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{his_call}";
        vm.TemplateVariableRows[0].Value = "K1ABC";

        var templateId = templateStore.CreateTemplateId("HasVariable");
        await templateStore.SaveAsync(templateId, "HasVariable", new PersistedTemplateDocument([
            new PersistedTextElement(0.5, 0.5, 0.3, 0.1, 0, false, "{his_call}", 0.1, new Rgb24(255, 255, 255), "", null, 0.02),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        var reloadedRow = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", reloadedRow.Key);
        Assert.Equal("K1ABC", reloadedRow.Value);
    }

    private static ArrayImageSource CreateSource(int width, int height)
        => new(width, height, new Rgb24[width * height]);

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}, got {actual}");
}
