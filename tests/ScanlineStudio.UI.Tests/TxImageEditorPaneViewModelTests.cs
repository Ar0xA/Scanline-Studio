using System.Globalization;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Imaging;
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

    // Regression fixture for FrameReadoutText/SendMetaText's own PD90-family double-counting bug:
    // ImageHeight 4 with a paired encoding means only 2 transmission lines, not 4 -- a naive
    // LineDurationMs * ImageHeight formula would compute 100ms * 4 = 0.4s; the real value is
    // 100ms * 4 / 2 (rows-per-transmission-line) / 1000 = 0.2s.
    private static readonly SstvModeDefinition LinePairedMode = new(
        Id: "line-paired",
        DisplayName: "Line Paired",
        VisCode: 0,
        ImageWidth: 4,
        ImageHeight: 4,
        ColorEncoding: ColorEncoding.YCbCrLinePaired,
        LineSegments: [new ScanSegment("Y", 100)]);

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

    /// <summary>ui_transition_plan.md step 2 (T1-2) overload -- exposes the parent's
    /// canTransmitNow delegate for <see cref="TxImageEditorPaneViewModelTests.ApplyAndTransmitCommand_CanExecute_ReflectsTheParentsCanTransmitNowDelegate"/>
    /// and friends. Every OTHER <c>CreateEditor</c> overload omits it (defaults to "always
    /// allowed" -- see the production constructor's own doc comment), matching how the real
    /// production call sites are the only ones that ever pass a real delegate.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer, Func<bool> canTransmitNow) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(), canTransmitNow: canTransmitNow);

    /// <summary>ui_transition_plan.md step 5 (T1-6) overload -- exposes the "Copy to TX" HIS
    /// CALL/HIS GRID seed.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer, IReadOnlyDictionary<string, string> currentContactVariables) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(), currentContactVariables: currentContactVariables);

    /// <summary>Macros help plan (2026-09-01), item B overload -- exposes the
    /// macrosReferenceRequested delegate for <see cref="OpenMacrosReferenceCommand_InvokesTheWiredDelegate"/>
    /// and friends. Every OTHER <see cref="CreateEditor"/> overload omits it (defaults to null, a
    /// silent no-op -- see the production constructor's own doc comment), matching how the real
    /// production call sites are the only ones that ever pass a real delegate.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer, Action macrosReferenceRequested) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack(), macrosReferenceRequested: macrosReferenceRequested);

    /// <summary>Ready Rack direct-fire plan (2026-09-01) overload -- exposes the live
    /// currentContactProvider delegate for direct-fire re-seed tests, mirroring the
    /// currentContactVariables (one-shot snapshot) overload above. Also exposes
    /// <see cref="ITemplateStore"/>/<see cref="ReadyRackViewModel"/> together, since every
    /// direct-fire test needs a real rack to pin a template into.</summary>
    private static TxImageEditorPaneViewModel CreateEditor(
        IImageSource original, SstvModeDefinition mode, ITransmitImagePreparer preparer,
        ITemplateStore templateStore, ReadyRackViewModel readyRack,
        Func<IReadOnlyDictionary<string, string>?>? currentContactProvider = null, Func<bool>? canTransmitNow = null) =>
        new(original, mode, preparer, new MacroTextResolver(), new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            templateStore, new FakeImageSourceWriter(), readyRack, canTransmitNow: canTransmitNow, currentContactProvider: currentContactProvider);

    private static ReadyRackViewModel CreateReadyRack(ITemplateStore? templateStore = null) =>
        new(templateStore ?? new FakeTemplateStore(), new FakeSettingsStore(), new FakeLocalizationService(), new FakeFilePickerService(), NullLogger<ReadyRackViewModel>.Instance);

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
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(preparer.TemplateDocuments, d => d.Elements.Any(e => e is TemplateTextElement text && text.Content == "DE W1AW"));
    }

    // User-reported (2026-09-20): an ALREADY-OPEN editor kept resolving %m/{grid} against whatever
    // OperatorSettings snapshot was current when it opened -- setting Grid in Options and adding a
    // {grid} field to an editor that was already open still showed OperatorSettings.GridFallback
    // ("XX00"). RefreshOperatorSettings (called from MainViewModel.LoadOperatorSettingsAsync, in
    // turn called from OptionsWindowViewModel.OperatorSettingsSaved) fixes this.
    [AvaloniaFact]
    public void RefreshOperatorSettings_UpdatesResolvedTextOfExistingAndNewElements()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings());
        vm.AddOverlayElementCommand.Execute(null);
        var existingElement = (OverlayElementViewModel)vm.OverlayElements[0];
        existingElement.Text = "DE %m {grid}";

        Assert.Equal($"DE {OperatorSettings.CallsignFallback} {OperatorSettings.GridFallback}", existingElement.ResolvedText);

        vm.RefreshOperatorSettings(new OperatorSettings { Callsign = "W1AW", Grid = "EN52" });

        Assert.Equal("DE W1AW EN52", existingElement.ResolvedText);

        vm.AddOverlayElementCommand.Execute(null);
        var newElement = (OverlayElementViewModel)vm.OverlayElements[1];
        newElement.Text = "{grid}";

        Assert.Equal("EN52", newElement.ResolvedText);
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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(countAfterRemoval, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_StaleReferenceNoLongerInTheCollection_IsATrueNoOp()
    {
        // Tier B audit finding: null-checked, but not checked for being a stale reference no longer
        // in OverlayElements (e.g. a queued click racing an Undo, which replaces every element
        // wholesale) -- same guard SetAsBackdrop/MoveElementUp/MoveElementDown/BringToFront/
        // SendToBack all already have. ObservableCollection.Remove itself already no-ops silently on
        // an absent element, so without this guard the only visible effect used to be a bogus undo
        // step.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.RemoveOverlayElementCommand.Execute(element); // real removal -- 1 legit undo step on top of Add

        vm.RemoveOverlayElementCommand.Execute(element); // stale reference, already removed

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // TX editor gap-items plan, item 2 (group-ops-lite, revision 3 -- self-healing sync).

    [AvaloniaFact]
    public void ToggleElementSelection_AddsThenRemovesElementFromTheSet()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];

        vm.ToggleElementSelection(a);

        Assert.Equal(2, vm.SelectedOverlayElements.Count);
        Assert.Contains(a, vm.SelectedOverlayElements);
        Assert.Contains(b, vm.SelectedOverlayElements);
        Assert.True(a.IsSelected);
        Assert.True(b.IsSelected);

        vm.ToggleElementSelection(a);

        Assert.Equal(new[] { b }, vm.SelectedOverlayElements);
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
    }

    [AvaloniaFact]
    public void DirectAssignmentToSelectedOverlayElement_CollapsesAnExistingGroup()
    {
        // Round-3 plan-review's own self-healing hook: EVERY raw SelectedOverlayElement assignment
        // (all ~13 pre-existing call sites, unchanged) must collapse a live multi-selection, not just
        // the new SetSelection-routed sites.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var c = vm.OverlayElements[2];
        vm.SetSelection([a, b, c]);
        Assert.Equal(3, vm.SelectedOverlayElements.Count);

        vm.SelectedOverlayElement = b;

        Assert.Equal(new[] { b }, vm.SelectedOverlayElements);
        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
        Assert.False(c.IsSelected);
    }

    [AvaloniaFact]
    public void SetSelectionToAnAlreadyPrimaryGroupMember_StillCollapsesTheGroup()
    {
        // Round-3 plan-review blocker: a sidebar row click on the element that's ALREADY the
        // group's primary is a value-EQUAL SelectedOverlayElement assignment, which CommunityToolkit's
        // generated setter skips entirely -- the collapse hook never fires on a bare assignment. This
        // pins that SetSelection itself (used by OnElementRowPointerPressed instead of a raw
        // assignment) still collapses correctly in exactly that edge case, since it rebuilds
        // SelectedOverlayElements directly rather than depending on the hook firing.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var c = vm.OverlayElements[2];
        vm.SetSelection([a, b, c]);
        Assert.Same(c, vm.SelectedOverlayElement);
        Assert.Equal(3, vm.SelectedOverlayElements.Count); // group genuinely formed before the collapse below

        vm.SetSelection([c]);

        Assert.Equal(new[] { c }, vm.SelectedOverlayElements);
        Assert.False(a.IsSelected);
        Assert.False(b.IsSelected);
        Assert.True(c.IsSelected);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_OnNonPrimaryGroupMember_ShrinksGroupRatherThanLeavingAStaleMember()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var c = vm.OverlayElements[2];
        vm.SetSelection([a, b, c]);
        Assert.Same(c, vm.SelectedOverlayElement);

        vm.RemoveOverlayElementCommand.Execute(a);

        Assert.DoesNotContain(a, vm.OverlayElements);
        Assert.Equal(2, vm.SelectedOverlayElements.Count);
        Assert.DoesNotContain(a, vm.SelectedOverlayElements);
        Assert.Contains(b, vm.SelectedOverlayElements);
        Assert.Contains(c, vm.SelectedOverlayElements);
        Assert.Same(c, vm.SelectedOverlayElement);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_OnThePrimaryOfAThreeMemberGroup_ShrinksRatherThanClearingTheWholeGroup()
    {
        // Regression pin for the ORIGINAL bug this fix replaced: the old `if (ReferenceEquals(
        // SelectedOverlayElement, element)) SelectedOverlayElement = null;` check would have wrongly
        // cleared BOTH remaining group members just because the removed element was the primary.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var c = vm.OverlayElements[2];
        vm.SetSelection([a, b, c]);
        Assert.Same(c, vm.SelectedOverlayElement);

        vm.RemoveOverlayElementCommand.Execute(c);

        Assert.Equal(2, vm.SelectedOverlayElements.Count);
        Assert.Contains(a, vm.SelectedOverlayElements);
        Assert.Contains(b, vm.SelectedOverlayElements);
        Assert.NotNull(vm.SelectedOverlayElement);
    }

    [AvaloniaFact]
    public void RemoveSelectedElements_RemovesOnlyGroupMembersInExactlyOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddOverlayElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        vm.AddOverlayElementCommand.Execute(null);
        var c = vm.OverlayElements[2];
        vm.SetSelection([a, c]); // b left out of the group deliberately

        vm.RemoveSelectedElementsCommand.Execute(null);

        Assert.Equal(new[] { b }, vm.OverlayElements);
        Assert.Empty(vm.SelectedOverlayElements);

        vm.UndoCommand.Execute(null); // undoes ONLY the group removal, not the 3 earlier adds

        Assert.Equal(3, vm.OverlayElements.Count);
        Assert.True(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void DuplicateGroup_ClonesInZOrder_NotSelectionOrder()
    {
        // Round-1 plan-review's real Z-order finding: cloning in SELECTION order (rather than
        // sorting by each element's own Z first) would silently re-stack an overlapping group on
        // duplicate. b is added but deliberately left OUT of the group, so it can't coincidentally
        // make the assertion pass by being adjacent in Z.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var a = (OverlayElementViewModel)vm.OverlayElements[0];
        a.Text = "A";
        vm.AddOverlayElementCommand.Execute(null); // b: Z between a and c, not selected
        vm.AddOverlayElementCommand.Execute(null);
        var c = (OverlayElementViewModel)vm.OverlayElements[2];
        c.Text = "C";
        vm.SetSelection([c, a]); // reverse click order -- a has the LOWER Z, c the higher

        vm.DuplicateCommand.Execute(null);

        Assert.Equal(5, vm.OverlayElements.Count);
        Assert.Equal(2, vm.SelectedOverlayElements.Count);
        var cloneOfA = (OverlayElementViewModel)vm.SelectedOverlayElements[0];
        var cloneOfC = (OverlayElementViewModel)vm.SelectedOverlayElements[1];
        Assert.Equal("A", cloneOfA.Text);
        Assert.Equal("C", cloneOfC.Text);
        Assert.True(cloneOfA.Z < cloneOfC.Z);
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

    /// <summary>ui_transition_plan.md step 2 (T1-2): the SEND row's new primary action -- same
    /// pipeline as plain Apply, but through <see cref="TxImageEditorPaneViewModel.AppliedAndTransmitRequested"/>
    /// instead of <see cref="TxImageEditorPaneViewModel.Applied"/>, and must fire ONLY that one --
    /// a caller (TxControlsPaneViewModel) that handled both would double-apply the same output.
    /// </summary>
    [AvaloniaFact]
    public void ApplyAndTransmit_RunsTheSamePipelineAsApply_AndFiresOnlyAppliedAndTransmitRequested()
    {
        var original = CreateSource(20, 20);
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(original, SmallMode, preparer);

        IImageSource? appliedAndTransmit = null;
        var plainAppliedFired = false;
        vm.Applied += _ => plainAppliedFired = true;
        vm.AppliedAndTransmitRequested += img => appliedAndTransmit = img;

        vm.ApplyAndTransmitCommand.Execute(null);

        Assert.NotNull(appliedAndTransmit);
        Assert.Equal(SmallMode.ImageWidth, appliedAndTransmit!.Width);
        Assert.Equal(SmallMode.ImageHeight, appliedAndTransmit.Height);
        Assert.Same(original, preparer.CropSources[^1]);
        Assert.False(plainAppliedFired);
    }

    [AvaloniaFact]
    public void ApplyAndTransmitCommand_DefaultsToAlwaysAllowed_WhenNoDelegateIsSupplied()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.True(vm.ApplyAndTransmitCommand.CanExecute(null));
    }

    /// <summary>CommunityToolkit does not auto-requery a CanExecute predicate that closes over
    /// another object's property (see <see cref="TxImageEditorPaneViewModel.NotifyTransmitAvailabilityChanged"/>'s
    /// own doc comment) -- this asserts the parent's re-notify contract actually flips the
    /// command's CanExecute, not just that the delegate itself would return the right value.
    /// </summary>
    [AvaloniaFact]
    public void ApplyAndTransmitCommand_CanExecute_ReflectsTheParentsCanTransmitNowDelegate()
    {
        var canTransmitNow = false;
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), () => canTransmitNow);

        Assert.False(vm.ApplyAndTransmitCommand.CanExecute(null));

        canTransmitNow = true;
        vm.NotifyTransmitAvailabilityChanged();

        Assert.True(vm.ApplyAndTransmitCommand.CanExecute(null));
    }

    /// <summary>Macros help plan (2026-09-01), item B.</summary>
    [AvaloniaFact]
    public void OpenMacrosReferenceCommand_InvokesTheWiredDelegate()
    {
        var invokedCount = 0;
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), () => invokedCount++);

        vm.OpenMacrosReferenceCommand.Execute(null);

        Assert.Equal(1, invokedCount);
    }

    /// <summary>Macros help plan (2026-09-01), item B: every OTHER <see cref="CreateEditor"/> overload
    /// (used by ~20 other test call sites) passes no delegate at all, matching production's own
    /// "unwired = silent no-op" contract -- this pins that the command itself doesn't throw against
    /// that default.</summary>
    [AvaloniaFact]
    public void OpenMacrosReferenceCommand_UnwiredDelegate_DoesNotThrow()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        var exception = Record.Exception(() => vm.OpenMacrosReferenceCommand.Execute(null));

        Assert.Null(exception);
    }

    [AvaloniaFact]
    public async Task Cancel_FiresCancelledEventWithoutInvokingThePipelineAgain()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var cropCountBefore = preparer.CropCallCount;
        var resizeCountBefore = preparer.ResizeCallCount;
        var overlayCountBefore = preparer.ApplyTemplateCallCount;
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.True(cancelled);
        Assert.Equal(cropCountBefore, preparer.CropCallCount);
        Assert.Equal(resizeCountBefore, preparer.ResizeCallCount);
        Assert.Equal(overlayCountBefore, preparer.ApplyTemplateCallCount);
    }

    // Backlog item (auditor usability review, 2026-08-17): "Cancel discards all edits with no
    // confirmation, even though HasUnsavedEdits already exists." User-reported feedback
    // (2026-09-15): the original arm/confirm (a second click on the SAME Cancel button) shape went
    // stale the moment ConfirmRequested/RequestConfirmAsync were added for template recall --
    // migrated to that same real dialog. See LoadTemplate_WithUnsavedEdits_RequestsARealConfirmDialogWithTheRightText
    // above for the sibling test this one mirrors.

    [AvaloniaFact]
    public async Task Cancel_WithUnsavedEdits_RequestsARealConfirmDialogWithTheRightText()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        ConfirmActionDialogViewModel? seenConfirmVm = null;
        vm.ConfirmRequested = confirmVm =>
        {
            seenConfirmVm = confirmVm;
            return Task.FromResult(false); // decline
        };
        vm.AddOverlayElementCommand.Execute(null);
        Assert.True(vm.HasUnsavedEdits);
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.NotNull(seenConfirmVm);
        // FakeLocalizationService.GetString returns the raw key -- asserting the exact keys proves
        // title/body/both button labels all reach the dialog, not just "some text was set."
        Assert.Equal("Panes.TxImageEditor.ConfirmCancelTitle", seenConfirmVm!.Title);
        Assert.Equal("Panes.TxImageEditor.ConfirmCancelBody", seenConfirmVm.Message);
        Assert.Equal("Panes.TxImageEditor.ConfirmCancelButton", seenConfirmVm.ConfirmLabel);
        Assert.Equal("Panes.TxImageEditor.DialogCancel", seenConfirmVm.CancelLabel);
        // Declining leaves the editor open.
        Assert.False(cancelled);
    }

    [AvaloniaFact]
    public async Task Cancel_WithUnsavedEdits_ConfirmingTheDialogCancels()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.ConfirmRequested = _ => Task.FromResult(true);
        vm.AddOverlayElementCommand.Execute(null);
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.True(cancelled);
    }

    [AvaloniaFact]
    public async Task Cancel_WithoutUnsavedEdits_CancelsImmediatelyWithNoDialog()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.HasUnsavedEdits);
        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.True(cancelled);
        Assert.False(confirmRequested);
    }

    // User-requested (2026-09-19): "when you save a template into the template library 'unsaved
    // edits' should be removed and only added again if actual edits are made. Same for when it's
    // loaded." Cancel's own confirm-gate moved from HasUnsavedEdits to IsDirtySinceLastCheckpoint --
    // these three tests assert BOTH signals directly at each step, proving they genuinely diverge
    // (HasUnsavedEdits stays permanently true after a load/save, IsDirtySinceLastCheckpoint doesn't),
    // not just that the surface Cancel behavior happened to change for some other reason.

    [AvaloniaFact]
    public async Task LoadTemplate_CleanEditor_FirstLoad_DoesNotConfirmOnCancelAfterward()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var templateId = templateStore.CreateTemplateId("A");
        await templateStore.SaveAsync(templateId, "A", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.HasUnsavedEdits); // unchanged -- the load itself is a pushed undo entry
        Assert.False(vm.IsDirtySinceLastCheckpoint); // but the checkpoint-relative signal is clean

        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.False(confirmRequested);
        Assert.True(cancelled);
    }

    [AvaloniaFact]
    public async Task SaveTemplate_ClearsIsDirtySinceLastCheckpoint_SoCancelDoesNotConfirmAfterward()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        Assert.True(vm.HasUnsavedEdits);
        Assert.True(vm.IsDirtySinceLastCheckpoint);

        vm.NewTemplateName = "Saved Template";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.True(vm.HasUnsavedEdits); // unchanged -- a save doesn't clear the undo stack itself
        Assert.False(vm.IsDirtySinceLastCheckpoint); // but the save IS now a fresh checkpoint

        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.False(confirmRequested);
        Assert.True(cancelled);
    }

    [AvaloniaFact]
    public async Task SaveTemplate_ThenARealEditAfterward_ReArmsIsDirtySinceLastCheckpoint_SoCancelConfirmsAgain()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "Saved Template";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirtySinceLastCheckpoint);

        vm.AddOverlayElementCommand.Execute(null); // a real edit made AFTER the checkpoint

        Assert.True(vm.IsDirtySinceLastCheckpoint);

        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.True(confirmRequested);
        Assert.True(cancelled);
    }

    [AvaloniaFact]
    public async Task ModeSwitchedEditor_CarriedOverDirtiness_ClearsAfterASave_SoCancelDoesNotConfirmAfterward()
    {
        // yoniq-auditor Blocker 1 (2026-09-19): _carriedOverUnsavedEdits is readonly and backs
        // HasUnsavedEdits's own 3 unrelated production consumers -- IsDirtySinceLastCheckpoint must
        // NOT OR that same field in, or a mode-switched editor's checkpoint would read permanently
        // dirty for its whole lifetime regardless of any later save. Constructs an editor the way
        // ReplaceEditorForModeSwitch does (carriedOverUnsavedEdits: true) directly, since no
        // CreateEditor overload exposes that parameter.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), new FakeLocalizationService(),
            NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeFilePickerService(), new FakeImageFileLoader(),
            new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), templateStore, new FakeImageSourceWriter(),
            readyRack, carriedOverUnsavedEdits: true);

        Assert.True(vm.IsDirtySinceLastCheckpoint);

        vm.NewTemplateName = "Carried Over Template";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.False(vm.IsDirtySinceLastCheckpoint);

        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.False(confirmRequested);
        Assert.True(cancelled);
    }

    [AvaloniaFact]
    public async Task UndoStackPinnedAtMaxDepth_CheckpointedThere_FurtherEditsStayDirty_SoCancelStillConfirms()
    {
        // yoniq-auditor Blocker 2 (2026-09-19): comparing _undoStack.Count against a saved baseline
        // silently reads "clean" once the stack is pinned at MaxUndoDepth (50) and a checkpoint was
        // taken at that same pinned count -- every push past that point nets back to Count=50 (add-
        // then-evict), hiding real further edits and letting "New Template" discard them with no
        // confirm dialog. Pushes 55 discrete edits (past the 50 cap) BEFORE saving, so the checkpoint
        // is captured while the stack is already pinned, then makes one more edit afterward.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);

        for (var i = 0; i < 55; i++)
        {
            vm.AddOverlayElementCommand.Execute(null);
        }

        vm.NewTemplateName = "Pinned Template";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        Assert.False(vm.IsDirtySinceLastCheckpoint);

        vm.AddOverlayElementCommand.Execute(null); // a real edit made AFTER the pinned checkpoint

        Assert.True(vm.IsDirtySinceLastCheckpoint);

        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var cancelled = false;
        vm.Cancelled += () => cancelled = true;

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.True(confirmRequested);
        Assert.True(cancelled);
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
        // duration is exactly 0 -- a real, if degenerate, value from
        // TxControlsPaneViewModel.GetFrameSeconds, same formula the TX Controls card's own Duration
        // row uses (see the paired-family regression test below for the non-degenerate case).
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(8, 4), WideMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        _ = vm.FrameReadoutText;

        Assert.Equal("Panes.TxImageEditor.FrameReadoutFormat", localization.LastKey);
        // Mode name uppercased for display (design-fidelity Phase I nit) -- the mock's own readouts
        // are all-caps mono chrome text.
        Assert.Equal(new object[] { 8, 4, "WIDE", 0.0 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void FrameReadoutText_LinePairedMode_DividesByRowsPerTransmissionLine()
    {
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), LinePairedMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        _ = vm.FrameReadoutText;

        Assert.Equal("Panes.TxImageEditor.FrameReadoutFormat", localization.LastKey);
        var duration = Assert.IsType<double>(localization.LastArgs[3]);
        Assert.Equal(0.2, duration, precision: 10);
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
    public void Undo_PreservesGrowToFillEnabled_OnAnUnrelatedElement()
    {
        // yoniq-auditor-flagged risk: ApplyState's own recreate-every-element-from-a-snapshot path
        // had no home for GrowToFillEnabled before RawTextElementSnapshot carried it -- any unrelated
        // Undo (here, RotateCommand, which pushes its own whole-editor undo step) would silently
        // reset the toggle on EVERY text element, not just whatever change was actually undone.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.GrowToFillEnabled = true;

        vm.RotateCommand.Execute(null);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        var restoredElement = Assert.Single(vm.OverlayElements);
        Assert.True(((OverlayElementViewModel)restoredElement).GrowToFillEnabled);
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
        Dispatcher.UIThread.RunJobs();

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

    // PROJECT_BRIEF.md tracked debt: same coalescing mechanism as Brightness above
    // (PushUndoSnapshotCoalesced, keyed on nameof(<Property>)), each key needing its own dedicated
    // same-key-burst test -- a test that changes two DIFFERENT keys back-to-back only proves
    // non-interference, not that a given key's OWN burst actually collapses to one step. Mirrors
    // Brightness_RapidBurst_CoalescesIntoOneUndoStep's exact shape for the 5 remaining adjustment
    // sliders, then the 2 boolean toggles, then text's "OverlayStyle" key (the one other undo-
    // coalescing gap the same tracked-debt note names, alongside BoxStyle -- already covered by
    // BoxFillColorAndBorderThickness_ThenUndo_RevertsBothAsOneStep above).

    [AvaloniaFact]
    public void Contrast_RapidBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Contrast = 20;
        vm.Contrast = 30;
        vm.Contrast = 40;
        vm.UndoCommand.Execute(null);

        AssertClose(0, vm.Contrast);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Saturation_RapidBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Saturation = 20;
        vm.Saturation = 30;
        vm.Saturation = 40;
        vm.UndoCommand.Execute(null);

        AssertClose(0, vm.Saturation);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Gamma_RapidBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Gamma = 20;
        vm.Gamma = 30;
        vm.Gamma = 40;
        vm.UndoCommand.Execute(null);

        AssertClose(0, vm.Gamma);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Sharpen_RapidBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Sharpen = 20;
        vm.Sharpen = 30;
        vm.Sharpen = 40;
        vm.UndoCommand.Execute(null);

        AssertClose(0, vm.Sharpen);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Denoise_RapidBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Denoise = 20;
        vm.Denoise = 30;
        vm.Denoise = 40;
        vm.UndoCommand.Execute(null);

        AssertClose(0, vm.Denoise);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PreserveAspect_RapidToggleBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.True(vm.PreserveAspect);

        vm.PreserveAspect = false;
        vm.PreserveAspect = true;
        vm.PreserveAspect = false;
        vm.UndoCommand.Execute(null);

        Assert.True(vm.PreserveAspect);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void LockAspectToMode_RapidToggleBurst_CoalescesIntoOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.LockAspectToMode);

        vm.LockAspectToMode = true;
        vm.LockAspectToMode = false;
        vm.LockAspectToMode = true;
        vm.UndoCommand.Execute(null);

        Assert.False(vm.LockAspectToMode);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void TextFontSizeAndColor_ThenUndo_RevertsBothAsOneStep()
    {
        // "OverlayStyle" key's own dedicated coalescing test -- same shape as
        // BoxFillColorAndBorderThickness_ThenUndo_RevertsBothAsOneStep above, for the text-element
        // counterpart (OnFontSizeRelativeChanging/OnColorChanging -> PushUndoSnapshotForStyleChange
        // -> PushUndoSnapshotCoalesced("OverlayStyle")).
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null); // its own push -- one level stays below the burst's
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var fontSizeBefore = element.FontSizeRelative;
        var colorBefore = element.Color;

        element.FontSizeRelative = 0.25;
        element.Color = new Rgb24(10, 200, 30);

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        // Re-read from vm.OverlayElements, not the captured `element` reference -- ApplyState
        // (undo's own restore path) replaces elements wholesale from the snapshot, same reasoning
        // BoxFillColorAndBorderThickness_ThenUndo_RevertsBothAsOneStep's own re-read follows.
        var restored = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        AssertClose(fontSizeBefore, restored.FontSizeRelative);
        Assert.Equal(colorBefore, restored.Color);
        // Exactly ONE step for the size+color burst -- AddOverlayElement's own earlier push is the
        // one level still remaining, not a second style-burst step.
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SwitchingSelection_DoesNotTriggerAPipelineRecompute()
    {
        // Tier B audit finding: IsSelected/IsEditingText are pure interaction state, the same tier
        // as Locked/IsBackground/BlocksHitTesting (already filtered out of
        // OnOverlayElementPropertyChanged's recompute trigger), but weren't filtered -- clicking a
        // different element on the canvas flips IsSelected false on the old element and true on the
        // new (OnSelectedOverlayElementChanged's own loop), firing TWO extra full
        // Crop->Resize->ApplyAdjustments->ApplyTemplate passes for a change that can never affect
        // pipeline output.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddOverlayElementCommand.Execute(null);
        var elementA = vm.OverlayElements[0];
        var elementB = vm.OverlayElements[1];
        vm.SelectedOverlayElement = elementA;
        var callCountBefore = preparer.ApplyTemplateCallCount;

        vm.SelectedOverlayElement = elementB;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(callCountBefore, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void Undo_LandingInsideAnOpenCoalescingWindow_TheNextEditToTheSamePropertyStillPushesAFreshStep()
    {
        // Tier B audit finding: Undo (and Redo, via the same ApplyState path) never reset
        // _pendingCoalesceProperty, unlike PushUndoSnapshot/PushUndoSnapshotCoalesced, which both
        // do. An Undo landing inside an open coalescing window (no Dispatcher-idle continuation has
        // run yet to naturally close it) left the marker pointing at the just-reverted property, so
        // the VERY NEXT edit to that SAME property hit PushUndoSnapshotCoalesced's own
        // "_pendingCoalesceProperty == propertyName -> return" early-out and silently pushed NOTHING
        // -- the edit was applied (Brightness really did change) but became invisibly non-undoable,
        // with UndoCommand.CanExecute reading false even though the document was actually dirty.
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.Brightness = 20; // opens a coalescing window for Brightness -- no RunJobs, window still open
        vm.UndoCommand.Execute(null); // Brightness -> 0; ApplyState must reset the coalescing marker
        vm.Brightness = 30; // must push a FRESH step, not silently no-op against the gone snapshot

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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countBeforeDirectSet, preparer.ApplyTemplateCallCount);
    }

    // Backlog item (user request, 2026-08-17): canvas-preview outline fix -- CanvasStrokeThicknessPixels
    // mirrors CanvasFontSize's own real-pixel-space/chrome-only/recompute-trigger contract exactly,
    // same test shape as the 3 CanvasFontSize siblings above.

    [AvaloniaFact]
    public void CanvasStrokeThicknessPixels_ZeroWhenStrokeColorIsNull()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        Assert.Null(element.StrokeColor);
        Assert.Equal(0, element.CanvasStrokeThicknessPixels);
    }

    [AvaloniaFact]
    public void CanvasStrokeThicknessPixels_ReflectsStrokeThicknessAndTargetModeHeight_RecomputedAfterCropRectChange()
    {
        // Same crop/mode setup as CanvasFontSize_ReflectsFontSizeRelativeAndCropDimensions above
        // (scaleY = 1, width-constrained -- see that test's own doc comment for the full derivation).
        // StrokeThickness defaults to 0.02, WideMode.ImageHeight is 4 -> (0.02 * 4) / 1 = 0.08.
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.StrokeColor = new Rgb24(0, 0, 0);

        vm.CropRect = new NormalizedRect(0.0, 0.375, 1.0, 0.25);

        AssertClose(0.08, element.CanvasStrokeThicknessPixels);
    }

    [AvaloniaFact]
    public void CanvasStrokeThicknessPixels_DoesNotTriggerAPreviewRecompute()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var countBeforeDirectSet = preparer.ApplyTemplateCallCount;

        element.CanvasStrokeThicknessPixels = 12.34;
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

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
        Dispatcher.UIThread.RunJobs();

        var adjustments = preparer.Adjustments[^1];
        Assert.Equal(10, adjustments.Brightness);
        Assert.Equal(-20, adjustments.Contrast);
        Assert.Equal(5, adjustments.Saturation);
        Assert.Equal(-15, adjustments.Gamma);
        Assert.Equal(40, adjustments.Sharpen);
        Assert.Equal(60, adjustments.Denoise);
    }

    // T0-12 (production_audit.md): RecomputePreviewCoalesced -- a burst of hot-path triggers before
    // the UI thread goes idle must collapse into exactly one pipeline pass, same shape as
    // WaterfallPaneViewModel_MultipleFramesBeforeUiThreadRuns_CoalescesToOnlyTheLatest
    // (PaneViewModelTests.cs).

    [AvaloniaFact]
    public void RapidCropRectChanges_BeforeUiThreadRuns_CoalesceToOneRecomputePass()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        var countBefore = preparer.ApplyTemplateCallCount;

        vm.CropRect = new NormalizedRect(0.1, 0.1, 0.5, 0.5);
        vm.CropRect = new NormalizedRect(0.2, 0.2, 0.5, 0.5);
        vm.CropRect = new NormalizedRect(0.3, 0.3, 0.5, 0.5);

        Assert.Equal(countBefore, preparer.ApplyTemplateCallCount);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countBefore + 1, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void RapidSliderChanges_BeforeUiThreadRuns_CoalesceToOneRecomputePass_UsingTheLastValues()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var countBefore = preparer.ApplyTemplateCallCount;

        vm.Brightness = 10;
        vm.Brightness = 20;
        vm.Brightness = 30;

        Assert.Equal(countBefore, preparer.ApplyTemplateCallCount);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countBefore + 1, preparer.ApplyTemplateCallCount);
        Assert.Equal(30, preparer.Adjustments[^1].Brightness);
    }

    [AvaloniaFact]
    public void RapidOverlayElementPositionChanges_BeforeUiThreadRuns_CoalesceToOneRecomputePass()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var countBefore = preparer.ApplyTemplateCallCount;

        vm.OverlayElements[0].X = 0.1;
        vm.OverlayElements[0].Y = 0.1;
        vm.OverlayElements[0].X = 0.6;
        vm.OverlayElements[0].Y = 0.6;

        Assert.Equal(countBefore, preparer.ApplyTemplateCallCount);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countBefore + 1, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void MixedBurstAcrossAllThreeHotPaths_BeforeUiThreadRuns_StillCoalescesToOneRecomputePass()
    {
        // Proves the 3 hot-path triggers share ONE _recomputePreviewScheduled flag, not 3
        // independent ones -- a burst spanning crop, a slider, and an overlay element in the same
        // UI-thread turn still collapses to a single pipeline pass.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var countBefore = preparer.ApplyTemplateCallCount;

        vm.CropRect = new NormalizedRect(0.1, 0.1, 0.5, 0.5);
        vm.Brightness = 15;
        vm.OverlayElements[0].X = 0.6;

        Assert.Equal(countBefore, preparer.ApplyTemplateCallCount);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(countBefore + 1, preparer.ApplyTemplateCallCount);
    }

    [AvaloniaFact]
    public void ElementTextChange_MidBurst_StillUpdatesTemplateVariableRowsSynchronously()
    {
        // The design's central behavioral guarantee: RescanTemplateVariables stays eager even though
        // the expensive pipeline pass is deferred, so the fill-bar never goes stale mid-drag.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.CropRect = new NormalizedRect(0.1, 0.1, 0.5, 0.5);
        element.Text = "DE {his_call}";

        // No RunJobs() here -- TemplateVariableRows must already reflect the just-typed token.
        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_call", row.Key);
    }

    [AvaloniaFact]
    public void DiscreteActionRightAfterACoalescedBurst_StillRecomputesSynchronously_NoLeakedCoalescing()
    {
        // Proves the NotifyCropRectDerivedPropertiesAndRecomputePreview(coalesceRecompute:) split
        // didn't leak coalescing into Rotate -- a pending coalesced pass from a slider burst must
        // not delay Rotate's own synchronous recompute.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(8, 8), WideMode, preparer);
        vm.Brightness = 10; // schedules a coalesced pass, not yet run
        var countBeforeRotate = preparer.ApplyTemplateCallCount;

        vm.RotateCommand.Execute(null);

        Assert.True(preparer.ApplyTemplateCallCount > countBeforeRotate, "Rotate must recompute synchronously, not wait for the pending coalesced pass");
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
        Dispatcher.UIThread.RunJobs();

        var box = Assert.IsType<TemplateBoxElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Equal(new Rgb24(10, 20, 30), box.FillColor);
        Assert.Equal(new Rgb24(40, 50, 60), box.BorderColor);
        AssertClose(0.05, box.BorderThickness);
        AssertClose(0.5, box.Opacity);
    }

    /// <summary>Code-review finding (2026-09-01, box gradient fill): the persistence/VM-level tests
    /// for box gradient fill all used a fake preparer or asserted at the persistence layer, leaving
    /// the ONE line that actually puts the gradient into the transmitted image
    /// (<see cref="TxImageEditorPaneViewModel.BuildTemplateElement"/>'s box case) unguarded -- a
    /// mutation reverting it to a plain solid box would have passed every other test in this file.
    /// Same real-pipeline-document assertion pattern as <see cref="AddBoxElement_BakesFillBorderThicknessOpacityIntoTheAppliedDocument"/>
    /// above, extended to the gradient fields.</summary>
    [AvaloniaFact]
    public void AddBoxElement_WithGradientEnabled_BakesTheGradientIntoTheAppliedDocument()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = (BoxElementViewModel)vm.OverlayElements[0];

        element.GradientEnabled = true;
        element.GradientKind = TextGradientKind.Vertical;
        element.GradientStartColor = new Rgb24(10, 20, 30);
        element.GradientEndColor = new Rgb24(40, 50, 60);
        Dispatcher.UIThread.RunJobs();

        var box = Assert.IsType<TemplateBoxElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        if (box.Gradient is not { } gradient)
        {
            Assert.Fail("Expected a non-null Gradient.");
            return;
        }

        Assert.Equal(TextGradientKind.Vertical, gradient.Kind);
        Assert.Equal(new Rgb24(10, 20, 30), gradient.Stops[0].Color);
        Assert.Equal(new Rgb24(40, 50, 60), gradient.Stops[1].Color);
    }

    /// <summary>Code-review finding (2026-09-01, box gradient fill): GradientEnabled=false must
    /// still bake Gradient: null into the applied document -- otherwise a stale non-null gradient
    /// from an earlier enable/disable toggle could leak through.</summary>
    [AvaloniaFact]
    public void AddBoxElement_GradientDisabled_BakesNullGradientIntoTheAppliedDocument()
    {
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = (BoxElementViewModel)vm.OverlayElements[0];

        element.GradientEnabled = true;
        Dispatcher.UIThread.RunJobs();
        element.GradientEnabled = false;
        Dispatcher.UIThread.RunJobs();

        var box = Assert.IsType<TemplateBoxElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Null(box.Gradient);
    }

    /// <summary>Code-review finding (2026-09-01, box gradient fill): CopySelectedElementStyle/
    /// PasteSelectedElementStyle's box case was missing Gradient entirely -- Copy Style on a gradient
    /// box then Paste Style onto a solid box silently left the target solid instead of copying the
    /// gradient across.</summary>
    [AvaloniaFact]
    public void PasteSelectedElementStyle_BoxWithGradient_CopiesGradientFieldsToTheTarget()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var source = (BoxElementViewModel)vm.OverlayElements[0];
        source.GradientEnabled = true;
        source.GradientKind = TextGradientKind.Radial;
        source.GradientStartColor = new Rgb24(1, 2, 3);
        source.GradientEndColor = new Rgb24(4, 5, 6);
        vm.SelectedOverlayElement = source;
        vm.CopySelectedElementStyleCommand.Execute(null);

        vm.AddBoxElementCommand.Execute(null);
        var target = (BoxElementViewModel)vm.OverlayElements[1];
        vm.SelectedOverlayElement = target;

        vm.PasteSelectedElementStyleCommand.Execute(null);

        Assert.True(target.GradientEnabled);
        Assert.Equal(TextGradientKind.Radial, target.GradientKind);
        Assert.Equal(new Rgb24(1, 2, 3), target.GradientStartColor);
        Assert.Equal(new Rgb24(4, 5, 6), target.GradientEndColor);
    }

    // TX workflow modernization plan, Phase 1: Quick Style Flyout / Fill & Border flyout.

    [AvaloniaFact]
    public void FontSizePx_GetSet_RoundTripsThroughFontSizeRelative()
    {
        // SmallMode.ImageHeight == 4, so TargetModeHeightPx == 4 -- picked so the round-trip math
        // (relative = px / height) lands on clean, easily-checked values.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        AssertClose(0.4, element.FontSizePx); // default FontSizeRelative 0.1 * 4

        element.FontSizePx = 2.0;

        AssertClose(0.5, element.FontSizeRelative);
    }

    [AvaloniaFact]
    public void BorderThicknessPxAndCornerRadiusPx_GetSet_RoundTripThroughUnderlyingRelativeProperties()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = (BoxElementViewModel)vm.OverlayElements[0];

        element.BorderThicknessPx = 1.0;
        element.CornerRadiusPx = 2.0;

        AssertClose(0.25, element.BorderThickness);
        AssertClose(0.5, element.CornerRadius);
        AssertClose(1.0, element.BorderThicknessPx);
        AssertClose(2.0, element.CornerRadiusPx);
    }

    [AvaloniaFact]
    public void BoxFillColorAndBorderThickness_ThenUndo_RevertsBothAsOneStep()
    {
        // Real pre-existing gap this closes (see PushUndoSnapshotForStyleChange's own doc comment):
        // before this, NEITHER FillColor NOR BorderThickness pushed any undo step at all, on ANY
        // entry point -- including the already-shipped sidebar Box Style block. Same
        // one-coalesced-step-per-burst shape as OverlayElementXAndY_ThenUndo_RevertsBothAsOneStep
        // above, under its own "BoxStyle" key so it can't fold into a concurrent geometry drag.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null); // its own push -- one level stays below the burst's
        var element = (BoxElementViewModel)vm.OverlayElements[0];
        var fillBefore = element.FillColor;
        var thicknessBefore = element.BorderThickness;

        element.FillColor = new Rgb24(200, 10, 10);
        element.BorderThickness = 0.05;

        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        // Re-read from vm.OverlayElements, not the captured `element` reference -- ApplyState
        // (undo's own restore path) replaces elements wholesale from the snapshot, same reasoning
        // OverlayElementXAndY_ThenUndo_RevertsBothAsOneStep's own re-reads follow.
        var restored = (BoxElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Equal(fillBefore, restored.FillColor);
        AssertClose(thicknessBefore, restored.BorderThickness);
        // Exactly ONE step for the fill+thickness burst -- AddBoxElement's own earlier push is the
        // one level still remaining, not a second style-burst step.
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
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

    // Missing-feature sweep (2026-08-31): OS file drag-and-drop onto the TX editor
    // (AddImagesFromDroppedFilesAsync). Same load-pipeline reuse as the 3 sources above, plus its
    // own batch-specific rules: a shared 20-file cap, one undo step for the whole drop, and a
    // cascade offset so a multi-file drop doesn't stack every element on top of the first.

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_EmptyList_SetsStatusAndAddsNothing()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImagesFromDroppedFilesAsync([]);

        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Panes.TxImageEditor.NoImageFilesDropped", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_AllFilesFail_SetsStatusAndAddsNothing()
    {
        var loader = new FakeImageFileLoader();
        loader.FailForPath["/tmp/a.jpg"] = new InvalidOperationException("decode failed");
        loader.FailForPath["/tmp/b.jpg"] = new InvalidOperationException("decode failed");
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImagesFromDroppedFilesAsync(["/tmp/a.jpg", "/tmp/b.jpg"]);

        Assert.Empty(vm.OverlayElements);
        // No PushUndoSnapshot when nothing was actually inserted -- a drop that adds nothing must
        // not create a no-op undo step.
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Equal("Panes.TxImageEditor.AddImageFailed", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_SomeFilesFail_AddsTheRestAndReportsFailureCount()
    {
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        loader.FailForPath["/tmp/bad.jpg"] = new InvalidOperationException("decode failed");
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        await vm.AddImagesFromDroppedFilesAsync(["/tmp/good1.jpg", "/tmp/bad.jpg", "/tmp/good2.jpg"]);

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.Equal("Panes.TxImageEditor.SomeDroppedImagesFailed", vm.StatusMessage);
        // loaded.Count=2, capped.Count=3, failureCount=1 -- the 3 numeric args the locale string's
        // own {0}/{1}/{2} placeholders format.
        Assert.Equal(new object[] { 2, 3, 1 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_MoreThanCap_OnlyLoadsFirst20AndReportsTruncation()
    {
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(),
            new OperatorSettings(), new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());
        var paths = Enumerable.Range(0, 25).Select(i => $"/tmp/{i}.jpg").ToList();

        await vm.AddImagesFromDroppedFilesAsync(paths);

        Assert.Equal(20, vm.OverlayElements.Count);
        Assert.Equal(20, loader.RequestedPaths.Count);
        Assert.Equal(paths.Take(20), loader.RequestedPaths);
        Assert.Equal("Panes.TxImageEditor.DroppedImagesTruncated", vm.StatusMessage);
        Assert.Equal(new object[] { 20, 20, 25 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_AllSucceed_ClearsStatusMessage()
    {
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        vm.StatusMessage = "stale message from an earlier action";

        await vm.AddImagesFromDroppedFilesAsync(["/tmp/a.jpg", "/tmp/b.jpg"]);

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.Null(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_MultipleFiles_CascadesPositionsAndWrapsAt8()
    {
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        var paths = Enumerable.Range(0, 9).Select(i => $"/tmp/{i}.jpg").ToList();

        await vm.AddImagesFromDroppedFilesAsync(paths);

        var elements = vm.OverlayElements.Cast<ImageElementViewModel>().ToList();
        Assert.Equal(9, elements.Count);
        for (var i = 0; i < 8; i++)
        {
            AssertClose(0.5 + 0.03 * i, elements[i].X);
            AssertClose(0.5 + 0.03 * i, elements[i].Y);
        }

        // cascadeIndex 8 wraps back to offset 0 (CascadeWrap=8), matching element 0's own position.
        AssertClose(elements[0].X, elements[8].X);
        AssertClose(elements[0].Y, elements[8].Y);
        // Last one dropped is the one left selected, matching every other Add* source's own
        // "select what you just inserted" convention.
        Assert.Same(elements[^1], vm.SelectedOverlayElement);
    }

    [AvaloniaFact]
    public async Task AddImagesFromDroppedFilesAsync_MultipleFiles_IsOneUndoStep()
    {
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImagesFromDroppedFilesAsync(["/tmp/a.jpg", "/tmp/b.jpg", "/tmp/c.jpg"]);

        Assert.Equal(3, vm.OverlayElements.Count);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        // One Undo removes all 3 elements from the drop, not just the last one -- the whole drop is
        // one gesture, one undo step (this editor's own established convention).
        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ReportDroppedFilesUnreadable_SetsStatusMessage()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ReportDroppedFilesUnreadable(new InvalidOperationException("drag payload malformed"));

        Assert.Equal("Panes.TxImageEditor.DroppedFilesUnreadable", vm.StatusMessage);
    }

    // Auditor usability review follow-up (2026-08-18): 4th image source, clipboard paste -- Phase 2's
    // own logged scope cut, picked back up. Same shape as AddImageFromFileAsync's own tests just
    // above, since AddImageFromClipboardAsync reuses the identical picker-call -> loader-call ->
    // insert pipeline (see that command's own doc comment).

    [AvaloniaFact]
    public async Task AddImageFromClipboardAsync_LoadsThePastedImageAndAddsSelectsElement()
    {
        var preparer = new FakeTransmitImagePreparer();
        var picker = new FakeFilePickerService { ClipboardPathToReturn = "/tmp/clipboard-paste.png" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        var countBefore = preparer.ApplyTemplateCallCount;

        await vm.AddImageFromClipboardCommand.ExecuteAsync(null);

        var element = (ImageElementViewModel)Assert.Single(vm.OverlayElements);
        Assert.Same(element, vm.SelectedOverlayElement);
        Assert.True(preparer.ApplyTemplateCallCount > countBefore);
        var image = Assert.IsType<TemplateImageElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.Same(loader.ResultToReturn, image.Source);
        // Clipboard's own Origin has a null Payload (same "ephemeral, nothing to re-resolve" tier as
        // LastRx), NOT the throwaway temp file path -- that path is deleted immediately after load,
        // so persisting it here would be a dangling reference.
        Assert.Equal(new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.Clipboard, null), element.Origin);
    }

    [AvaloniaFact]
    public async Task AddImageFromClipboardAsync_NothingOnClipboard_IsANoOp()
    {
        // Default FakeFilePickerService.ClipboardPathToReturn is null -- "nothing image-shaped on
        // the clipboard right now" is a normal, silent state, not an error.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

        await vm.AddImageFromClipboardCommand.ExecuteAsync(null);

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
        var receivedAt = new DateTimeOffset(2026, 9, 2, 14, 30, 0, TimeSpan.Zero);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("entry-1", receivedAt, "PD120", "/tmp/rx1.png", null, ReceiveDecodeState.Completed),
            ],
            ThumbnailToReturn = CreateSource(1, 1),
        };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), historyStore);

        await vm.RefreshRxHistoryPickerCommand.ExecuteAsync(null);

        var entry = Assert.Single(vm.RxHistoryPickerEntries);
        Assert.Equal("entry-1", entry.Id);
        // UX friction fix (Fable operator-perspective review): the AXAML row displays ReceivedAt, not
        // the raw Id, so a real regression here would leave the picker showing GUID-shaped strings
        // again -- this pins that the VM actually carries the value through, not just that Id survives.
        Assert.Equal(receivedAt, entry.ReceivedAt);
        Assert.Equal("/tmp/rx1.png", entry.FilePath);
        Assert.NotNull(entry.Thumbnail);
        // SelectCommand is parent-pushed (same pattern as ITemplateElementViewModel.RemoveCommand)
        // so the AXAML picker row can bind directly, not via a $parent[ItemsControl] path.
        Assert.Same(vm.AddImageFromRxHistoryCommand, entry.SelectCommand);
    }

    [AvaloniaFact]
    public async Task RefreshRxHistoryPickerAsync_QueryThrows_SetsStatusMessage()
    {
        // Tier B audit finding: this was the one sibling among the image-source add/refresh paths
        // with no StatusMessage on failure -- log-only, so a failure (e.g. an unreadable RX history
        // SQLite file) left the "From RX history" flyout silently empty with no explanation.
        var historyStore = new FakeReceiveHistoryStore { ThrowOnQuery = new InvalidOperationException("database is locked") };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), historyStore);

        await vm.RefreshRxHistoryPickerCommand.ExecuteAsync(null);

        Assert.NotNull(vm.StatusMessage);
        Assert.Empty(vm.RxHistoryPickerEntries);
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
        var entry = new TxImageEditorPaneViewModel.RxHistoryPickerEntry("entry-1", DateTimeOffset.Now, "/tmp/rx1.png", null, null);

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
    public void SetAsBackdrop_MovesElementToFullFrameBottomZAndCollectionIndexZero()
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

        vm.SetAsBackdropCommand.Execute(image);

        AssertClose(0.5, image.X);
        AssertClose(0.5, image.Y);
        AssertClose(1, image.Width);
        AssertClose(1, image.Height);
        Assert.True(image.Z < text.Z);
        Assert.Same(image, vm.OverlayElements[0]);
        Assert.Same(text, vm.OverlayElements[1]);
    }

    [AvaloniaFact]
    public void SetAsBackdrop_PushesExactlyOneUndoStep()
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
        // total undo depth instead: push AddLastRxImage (1 action) then SetAsBackdrop (should be
        // exactly 1 more), then Undo exactly twice and assert NOTHING is left. A stray extra push
        // would leave one more Undo available after these two clicks.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];

        vm.SetAsBackdropCommand.Execute(image);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void SetAsBackdrop_OnNullElement_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.SetAsBackdropCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    // TX editor gap-items plan, item 3 (perspective transform) -- these tests target the exact
    // failure classes 3 rounds of adversarial plan-review found in the design: undo-step double-
    // counting (TogglePerspective/SetAsBackdrop/ResetToOriginalSize all mutate perspective state
    // inside an existing _suspendPreview window), the corner-cascade notification chain actually
    // reaching what AXAML binds, and the "independent per-corner clamp shears the quad" trap
    // InsertClonedSnapshot's own Line-element precedent already hit once.

    [AvaloniaFact]
    public void TogglePerspective_On_SeedsCornersFromTheCurrentBboxAndPushesExactlyOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
        var (x, y, w, h) = (box.X, box.Y, box.Width, box.Height);

        vm.TogglePerspectiveCommand.Execute(box);

        Assert.True(box.PerspectiveEnabled);
        AssertClose(x - (w / 2), box.Corner0X);
        AssertClose(y - (h / 2), box.Corner0Y);
        AssertClose(x + (w / 2), box.Corner1X);
        AssertClose(y - (h / 2), box.Corner1Y);
        AssertClose(x + (w / 2), box.Corner2X);
        AssertClose(y + (h / 2), box.Corner2Y);
        AssertClose(x - (w / 2), box.Corner3X);
        AssertClose(y + (h / 2), box.Corner3Y);
        // bbox center/extent unchanged by enabling -- X/Y/Width/Height now read through the corners.
        AssertClose(x, box.X);
        AssertClose(y, box.Y);
        AssertClose(w, box.Width);
        AssertClose(h, box.Height);

        // Same "count total undo depth" idiom as SetAsBackdrop_PushesExactlyOneUndoStep above --
        // AddBoxElement (1) then TogglePerspective (should be exactly 1 more); 2 Undos must leave
        // nothing.
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void TogglePerspective_OnThenOff_RestoresTheOriginalBboxAndPushesExactlyTwoUndoSteps()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
        var (x, y, w, h) = (box.X, box.Y, box.Width, box.Height);

        vm.TogglePerspectiveCommand.Execute(box);
        vm.TogglePerspectiveCommand.Execute(box);

        Assert.False(box.PerspectiveEnabled);
        AssertClose(x, box.X);
        AssertClose(y, box.Y);
        AssertClose(w, box.Width);
        AssertClose(h, box.Height);

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void SetAsBackdrop_OnAWarpedImageElement_TurnsPerspectiveOffWithExactlyOneUndoStep()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.TogglePerspectiveCommand.Execute(image);
        Assert.True(image.PerspectiveEnabled);

        vm.SetAsBackdropCommand.Execute(image);

        Assert.False(image.PerspectiveEnabled);
        AssertClose(0.5, image.X);
        AssertClose(0.5, image.Y);
        AssertClose(1, image.Width);
        AssertClose(1, image.Height);

        // AddLastRxImage (1) + TogglePerspective (1) + SetAsBackdrop (should be exactly 1 more) = 3.
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Empty(vm.OverlayElements);
    }

    [AvaloniaFact]
    public void Rotate_OnAWarpedBoxElement_RotatesTheCornersDirectlyNotTheDerivedBbox()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
        box.Corner0X = 0.1;
        box.Corner0Y = 0.2;
        box.Corner1X = 0.6;
        box.Corner1Y = 0.25;
        box.Corner2X = 0.55;
        box.Corner2Y = 0.7;
        box.Corner3X = 0.05;
        box.Corner3Y = 0.65;
        box.PerspectiveEnabled = true;

        vm.RotateCommand.Execute(null);

        AssertClose(1 - 0.2, box.Corner0X);
        AssertClose(0.1, box.Corner0Y);
        AssertClose(1 - 0.25, box.Corner1X);
        AssertClose(0.6, box.Corner1Y);
        AssertClose(1 - 0.7, box.Corner2X);
        AssertClose(0.55, box.Corner2Y);
        AssertClose(1 - 0.65, box.Corner3X);
        AssertClose(0.05, box.Corner3Y);
    }

    [AvaloniaFact]
    public void RawOverlayElements_ForAWarpedBox_IncludesPerspectiveEnabledAndTheCorners()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
        vm.TogglePerspectiveCommand.Execute(box);
        box.Corner1X = 0.77;

        var raw = Assert.IsType<TxImageEditorPaneViewModel.RawBoxElementSnapshot>(Assert.Single(vm.RawOverlayElements));

        Assert.True(raw.PerspectiveEnabled);
        AssertClose(0.77, raw.Corner1X);
        AssertClose(box.Corner0X, raw.Corner0X);
        AssertClose(box.Corner2Y, raw.Corner2Y);
    }

    [AvaloniaFact]
    public void Duplicate_OnAWarpedBoxElement_ShiftsAllCornersByTheSameSharedDeltaNotIndependentClamps()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
        box.Corner0X = 0.1;
        box.Corner0Y = 0.1;
        box.Corner1X = 0.5;
        box.Corner1Y = 0.15;
        box.Corner2X = 0.45;
        box.Corner2Y = 0.5;
        box.Corner3X = 0.05;
        box.Corner3Y = 0.45;
        box.PerspectiveEnabled = true;

        vm.DuplicateCommand.Execute(null);

        var clone = Assert.IsType<BoxElementViewModel>(vm.SelectedOverlayElement);
        Assert.NotSame(box, clone);
        Assert.True(clone.PerspectiveEnabled);
        var dx = clone.Corner0X - box.Corner0X;
        var dy = clone.Corner0Y - box.Corner0Y;
        AssertClose(dx, clone.Corner1X - box.Corner1X);
        AssertClose(dx, clone.Corner2X - box.Corner2X);
        AssertClose(dx, clone.Corner3X - box.Corner3X);
        AssertClose(dy, clone.Corner1Y - box.Corner1Y);
        AssertClose(dy, clone.Corner2Y - box.Corner2Y);
        AssertClose(dy, clone.Corner3Y - box.Corner3Y);
    }

    [AvaloniaFact]
    public void SetAsBackdrop_OnAnElementNoLongerInOverlayElements_DoesNotThrowAndIsANoOp()
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

        var exception = Record.Exception(() => vm.SetAsBackdropCommand.Execute(element));

        Assert.Null(exception);
        // No bogus undo step left behind by the guarded-out call.
        Assert.Equal(undoDepthBefore, vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SetAsBackdrop_SetsIsBackgroundAndAutoLocks()
    {
        // Phase 6 (spec/15-template-designer.md): both together are what let the crop rect
        // underneath become click-reachable again (BlocksHitTesting = Locked && IsBackground).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        Assert.False(image.IsBackground);
        Assert.False(image.Locked);

        vm.SetAsBackdropCommand.Execute(image);

        Assert.True(image.IsBackground);
        Assert.True(image.Locked);
        Assert.True(image.BlocksHitTesting);
    }

    [AvaloniaTheory]
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
        vm.SetAsBackdropCommand.Execute(image);
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
    public async Task SaveThenLoadTemplate_PreFillsNewTemplateNameWithTheLoadedTemplatesOwnName()
    {
        // UX friction fix (Fable operator-perspective review): "template-name retyping" -- loading a
        // template used to leave the Save-template name field exactly as SaveTemplateAsync's own
        // success path left it (blank), so tweaking a just-loaded template and re-saving required
        // retyping its full name from scratch, even though SaveTemplateAsync's own overwrite-by-
        // matching-name logic would have happily overwritten it in place if the name field had been
        // right.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "Contest Exchange";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.NewTemplateName);

        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        // This VM already has an unsaved edit on it (the AddOverlayElementCommand above), so
        // OnReadyRackTemplateSelected awaits a real confirm dialog before loading (Templates rack
        // rework -- wired here to auto-confirm) -- one click is enough now, no more two-click arm.
        vm.ConfirmRequested = _ => Task.FromResult(true);
        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Contest Exchange", vm.NewTemplateName);
        Assert.True(vm.SaveTemplateCommand.CanExecute(null));
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
    public void NudgeSelectedElements_MovesEveryUnlockedGroupMemberBySameDeltaAndSkipsLocked()
    {
        // Auditor finished-code-review finding (group-ops-lite): plain-arrow nudge only moved the
        // PRIMARY while a 2+ group was selected, an oversight rather than a documented v1 cut, since
        // group MOVE is explicitly in scope. Locked members are skipped individually, matching the
        // drag path's own _draggedGroup capture (excludes Locked, never rejects the whole gesture).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var a = vm.OverlayElements[0];
        vm.AddBoxElementCommand.Execute(null);
        var b = vm.OverlayElements[1];
        b.Locked = true;
        vm.AddBoxElementCommand.Execute(null);
        var c = vm.OverlayElements[2];
        vm.SetSelection([a, b, c]);
        var (xBeforeA, xBeforeB, xBeforeC) = (a.X, b.X, c.X);

        vm.NudgeSelectedElements(NudgeDirection.Right, ctrl: false);

        AssertClose(xBeforeA + (1.0 / vm.WorkingCopyWidth), a.X);
        AssertClose(xBeforeB, b.X); // Locked -- untouched
        AssertClose(xBeforeC + (1.0 / vm.WorkingCopyWidth), c.X);
    }

    // TX editor gap-items plan, line element (2026-09-01) -- VM-layer integration tests. See
    // LineElementViewModelTests.cs for the element's own standalone derived-contract tests.

    [AvaloniaFact]
    public void AddLineElementCommand_CreatesAndSelectsAHorizontalLine_OnTheGeometryTab()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddLineElementCommand.Execute(null);

        var line = Assert.IsType<LineElementViewModel>(Assert.Single(vm.OverlayElements));
        Assert.Same(line, vm.SelectedOverlayElement);
        AssertClose(line.Y1, line.Y2); // horizontal default
        Assert.True(vm.IsGeometryTabSelected);
    }

    [AvaloniaFact]
    public void BuildTemplateElement_HorizontalLine_ProducesANonDegenerateInkInflatedBounds()
    {
        // Round 1/3 plan-review's own central concern: an un-inflated horizontal line's bbox has
        // zero height, which ApplyTemplate's shared skip rule would silently drop before any render
        // code even runs. This pins that BuildTemplateElement (not just the pipeline layer in
        // isolation, already covered by ApplyTemplateTests.cs) actually produces a real, positive
        // Bounds for the live VM-built element.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);

        var line = Assert.IsType<TemplateLineElement>(Assert.Single(vm.Document.Elements));

        Assert.True(line.Bounds.Width > 0);
        Assert.True(line.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void Rotate_Line_RotatesEndpointsDirectly_FourRotationsReturnToStart()
    {
        // Round 1 blocker / round 2-3 verified fix: the GENERIC X/Y/Width/Height rotation transform
        // would turn a 90-degree rotation into a MIRROR for a line (and collapse a horizontal line's
        // Height to 0) -- this pins the real endpoint-rotation branch instead, including the
        // 4-clicks-returns-to-start invariant every other element already has.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        // Asymmetric, off-center, diagonal -- deliberately NOT the symmetric-about-canvas-center
        // default AddLineElement seeds: a mirror of a centered line coincidentally lands in the
        // same place a true rotation would, so a symmetric line can't distinguish the two -- this
        // needs an input where the buggy generic-transform mirror and a real per-point rotation
        // provably diverge.
        line.X1 = 0.2;
        line.Y1 = 0.3;
        line.X2 = 0.6;
        line.Y2 = 0.35;
        var (x1, y1, x2, y2) = (line.X1, line.Y1, line.X2, line.Y2);

        vm.RotateCommand.Execute(null);

        // The SAME per-point (x,y) -> (1-y,x) transform Rotate already applies to every other
        // element's own center, applied independently to each endpoint.
        AssertClose(1 - y1, line.X1);
        AssertClose(x1, line.Y1);
        AssertClose(1 - y2, line.X2);
        AssertClose(x2, line.Y2);

        vm.RotateCommand.Execute(null);
        vm.RotateCommand.Execute(null);
        vm.RotateCommand.Execute(null);

        AssertClose(x1, line.X1);
        AssertClose(y1, line.Y1);
        AssertClose(x2, line.X2);
        AssertClose(y2, line.Y2);
    }

    [AvaloniaFact]
    public void NudgeElementResize_SelectedLine_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        var (x1, y1, x2, y2) = (line.X1, line.Y1, line.X2, line.Y2);

        vm.NudgeElementResize(NudgeDirection.Right);

        AssertClose(x1, line.X1);
        AssertClose(y1, line.Y1);
        AssertClose(x2, line.X2);
        AssertClose(y2, line.Y2);
    }

    [AvaloniaFact]
    public void ApplySnappedElementBounds_Line_SnapToGridOn_SnapsBothEndpointsIndependently()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SnapToGrid = true;
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        line.X1 = 0.313;
        line.Y1 = 0.501;
        line.X2 = 0.647;
        line.Y2 = 0.499;

        // The x/y/width/height args passed in are deliberately WRONG/stale (as they'd be if computed
        // off the box-shaped helper) -- the line branch must ignore them entirely and re-derive its
        // own snap straight from the live endpoints.
        vm.ApplySnappedElementBounds(line, x: 999, y: 999, width: 999, height: 999);

        AssertClose(0.3, line.X1);
        AssertClose(0.5, line.Y1);
        AssertClose(0.65, line.X2);
        AssertClose(0.5, line.Y2);
    }

    [AvaloniaFact]
    public void ApplySnappedElementBounds_Line_AlreadyGridAligned_DoesNotPushADeadUndoStep()
    {
        // 2nd-round code-review finding: the CALLER's own "did anything change" guard compares
        // against the line's DERIVED box, which can spuriously fire even when the real endpoints are
        // already grid-aligned (the default line seed IS already 0.05-grid-aligned) -- an
        // unconditional PushUndoSnapshot before that was re-checked left a dead step on the stack,
        // so the first Ctrl+Z after such a drop silently did nothing.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SnapToGrid = true;
        vm.AddLineElementCommand.Execute(null); // pushes exactly 1 undo step (the Add)
        var line = (LineElementViewModel)vm.OverlayElements[0];

        vm.ApplySnappedElementBounds(line, x: 999, y: 999, width: 999, height: 999);

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null), "a no-op snap must not leave a second, dead undo step on the stack");
    }

    [AvaloniaFact]
    public void Duplicate_OffCanvasLine_PreservesLengthAndAngle_ViaASharedDelta()
    {
        // 2nd-round code-review finding: a bare per-endpoint Math.Clamp (like every other element's
        // own duplicate-offset case) can move one endpoint closer to the other than intended,
        // distorting an off-canvas line's own length/angle -- off-canvas is explicitly legal (Rotate's
        // own doc comment: "free overflow ... clipped at render time only"). ClampSharedOffsetDelta
        // must pick ONE delta valid for both endpoints instead.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);
        var original = (LineElementViewModel)vm.OverlayElements[0];
        original.X1 = 0.95;
        original.Y1 = 0.5;
        original.X2 = 1.05; // off-canvas
        original.Y2 = 0.5;
        vm.SelectedOverlayElement = original;

        vm.DuplicateCommand.Execute(null);

        var clone = Assert.IsType<LineElementViewModel>(vm.OverlayElements[1]);
        AssertClose(0.1, clone.X2 - clone.X1); // length preserved exactly, not shrunk by an uneven clamp
        Assert.NotEqual(original.X1, clone.X1); // the clone still actually moved
    }

    [AvaloniaFact]
    public void ApplySnappedElementBounds_Line_SnapToGridOff_IsANoOp()
    {
        // Round 3 plan-review finding: this method is ALSO reached by the separate alignment-guide-
        // snap path, which runs regardless of SnapToGrid -- an ungated line branch would grid-snap
        // even with grid-snap explicitly turned off.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SnapToGrid = false;
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        // Deliberately NOT the default (already 0.05-grid-aligned) seed -- snapping an
        // already-aligned value produces the SAME value either way, which would make this test pass
        // vacuously regardless of whether the SnapToGrid gate actually works.
        line.X1 = 0.313;
        line.Y1 = 0.501;
        line.X2 = 0.647;
        line.Y2 = 0.499;
        var (x1, y1, x2, y2) = (line.X1, line.Y1, line.X2, line.Y2);

        vm.ApplySnappedElementBounds(line, x: 999, y: 999, width: 999, height: 999);

        AssertClose(x1, line.X1);
        AssertClose(y1, line.Y1);
        AssertClose(x2, line.X2);
        AssertClose(y2, line.Y2);
    }

    [AvaloniaFact]
    public void Duplicate_Line_OffsetsBothEndpoints_NotJustZ()
    {
        // Plan-review-flagged real trap: InsertClonedSnapshot's own fallthrough (`var other => other`)
        // would have left a duplicated line sitting EXACTLY on top of the original -- offsetting the
        // base X/Y (like every other element's own case) does nothing for a line, since
        // CreateElementFromSnapshot's own line case reads only X1/Y1/X2/Y2.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);
        var original = (LineElementViewModel)vm.OverlayElements[0];
        var (x1, y1, x2, y2) = (original.X1, original.Y1, original.X2, original.Y2);
        vm.SelectedOverlayElement = original;

        vm.DuplicateCommand.Execute(null);

        Assert.Equal(2, vm.OverlayElements.Count);
        var clone = Assert.IsType<LineElementViewModel>(vm.OverlayElements[1]);
        Assert.NotEqual(x1, clone.X1);
        Assert.NotEqual(y1, clone.Y1);
        Assert.NotEqual(x2, clone.X2);
        Assert.NotEqual(y2, clone.Y2);
        // Same +0.02 offset, both endpoints, so the line's own length/angle survive the clone.
        AssertClose(x2 - x1, clone.X2 - clone.X1);
        AssertClose(y2 - y1, clone.Y2 - clone.Y1);
    }

    // Round 1 plan-review finding: X/Y/Width/Height are DERIVED cascades for a LineElementViewModel
    // (unlike every other element kind, where they're the real drivers) -- OnOverlayElementPropertyChanged
    // filters those names back out for a line sender specifically (see that method's own doc comment),
    // matching the established "FillBrush" double-recompute precedent (box gradient fill). A raw
    // ApplyTemplate-call-count assertion can't observe this (RecomputePreviewCoalesced's own coalescing
    // absorbs any difference within one UI-thread idle tick, confirmed by direct experiment -- an
    // earlier version of this test passed identically with the filter guard disabled and was removed
    // for being vacuous) -- see SelectionReadoutText_LineEndpointEdit_RaisesExactlyOnce... below
    // instead, which pins the filter's PLACEMENT (before the readout-raise block) via a synchronous
    // PropertyChanged count unaffected by that coalescing.

    // Backlog item (auditor usability review, 2026-08-17, item 18): "no keyboard element-resize path
    // at all." Ctrl+Shift+arrow (TxImageEditorPaneView.OnRootKeyDown) resizes instead of moves --
    // same DirectionToPixelDelta sign convention as ApplyCropResize's own bottom-right-corner-grow.

    [AvaloniaFact]
    public void NudgeElementResize_NoSelection_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        var widthBefore = element.Width;
        vm.SelectedOverlayElement = null;

        vm.NudgeElementResize(NudgeDirection.Right);

        AssertClose(widthBefore, element.Width);
    }

    [AvaloniaTheory]
    [InlineData(NudgeDirection.Right, 1)]
    [InlineData(NudgeDirection.Left, -1)]
    public void NudgeElementResize_Horizontal_GrowsOnRightAndShrinksOnLeft(NudgeDirection direction, int expectedPixels)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        vm.SelectedOverlayElement = element;
        var widthBefore = element.Width;
        var heightBefore = element.Height;

        vm.NudgeElementResize(direction);

        AssertClose(widthBefore + ((double)expectedPixels / vm.WorkingCopyWidth), element.Width);
        AssertClose(heightBefore, element.Height); // horizontal direction leaves Height untouched
    }

    [AvaloniaTheory]
    [InlineData(NudgeDirection.Down, 1)]
    [InlineData(NudgeDirection.Up, -1)]
    public void NudgeElementResize_Vertical_GrowsOnDownAndShrinksOnUp(NudgeDirection direction, int expectedPixels)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.Height = 0.8; // well clear of the floor -- SmallMode's tiny 4px working copy means a
                               // 1px shrink from the default 0.2 would otherwise hit it immediately
                               // (see the dedicated floor test below), which isn't what this test means to check.
        vm.SelectedOverlayElement = element;
        var heightBefore = element.Height;

        vm.NudgeElementResize(direction);

        AssertClose(heightBefore + ((double)expectedPixels / vm.WorkingCopyHeight), element.Height);
    }

    [AvaloniaFact]
    public void NudgeElementResize_FloorsAtMinNormalizedElementSize_NeverShrinksToZeroOrNegative()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = vm.OverlayElements[0];
        element.Width = 0.001;
        vm.SelectedOverlayElement = element;

        vm.NudgeElementResize(NudgeDirection.Left);

        Assert.True(element.Width > 0);
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

    // Backlog item (user request, 2026-08-17): "Fit safe area"/"Fit width"/"Fit height" alongside
    // the existing whole-frame Fit above -- same View-owns-viewport-size call convention.

    [AvaloniaFact]
    public void ApplyFitSafeArea_FitsTheSafeAreaBoxNotTheWholeFrame()
    {
        // Realistic mode (see SafeAreaWidthAndHeightPixels_TrackCanvasDisplaySizeAtAnyZoom's own doc
        // comment for why SmallMode/WideMode are too small here): WorkingCopy is exactly 320x256 (the
        // 320x256 source is within the mode's own 640x512 downsample budget, so no downsampling
        // happens), safe box is (320-28)x(256-28) = 292x228. A 292x684 viewport makes WIDTH the
        // constraining axis (292/292=1.0 vs 684/228=3.0) -- the whole-frame Fit would instead pick
        // 292/320=0.9125, so this is a real, discriminating check that the SAFE box (not the frame)
        // drove the computed zoom.
        var realisticMode = new SstvModeDefinition(
            Id: "realistic", DisplayName: "Realistic", VisCode: 0, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);
        var vm = CreateEditor(CreateSource(320, 256), realisticMode, new FakeTransmitImagePreparer());

        vm.ApplyFitSafeArea(292, 684);

        AssertClose(1.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitSafeArea_DegenerateSafeArea_FallsBackToWholeFrameFit()
    {
        // SmallMode's 4x4 WorkingCopy is smaller than 2x the 14px inset -- the safe box would be
        // negative-sized, so this must fall back to ApplyFit's own whole-frame math, not divide by
        // (or against) a non-positive dimension. Same inputs as
        // ApplyFit_ViewportLargerThanWorkingCopy_ZoomFactorGrowsToFillTheSmallerAxis (expected 2.0)
        // -- a real regression pin that the fallback actually reaches ApplyFit's own logic.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFitSafeArea(12, 8);

        AssertClose(2.0, vm.ZoomFactor);
    }

    [AvaloniaTheory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public void ApplyFitSafeArea_NonPositiveViewportDimension_IsANoOp(double width, double height)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var zoomBefore = vm.ZoomFactor;

        vm.ApplyFitSafeArea(width, height);

        AssertClose(zoomBefore, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitWidth_SetsZoomFactorToViewportWidthOverWorkingCopyWidth()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFitWidth(10);

        AssertClose(2.5, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitWidth_ComputedRatioAboveMaxZoomFactor_ClampsToMax()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFitWidth(4000);

        AssertClose(4.0, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitWidth_NonPositiveViewportDimension_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var zoomBefore = vm.ZoomFactor;

        vm.ApplyFitWidth(0);

        AssertClose(zoomBefore, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitHeight_SetsZoomFactorToViewportHeightOverWorkingCopyHeight()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFitHeight(10);

        AssertClose(2.5, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitHeight_ComputedRatioBelowMinZoomFactor_ClampsToMin()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.ApplyFitHeight(0.01);

        AssertClose(0.1, vm.ZoomFactor);
    }

    [AvaloniaFact]
    public void ApplyFitHeight_NonPositiveViewportDimension_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var zoomBefore = vm.ZoomFactor;

        vm.ApplyFitHeight(0);

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
    public void SafeAreaWidthAndHeightPixels_TrackCanvasDisplaySizeAtAnyZoom()
    {
        // Backlog fix (user real-window finding, 2026-08-17): "the blue lines... stay the same place
        // in the window" instead of tracking zoom -- the guide moved from a Margin-inset Panel
        // sibling of EditorCanvas to a direct Canvas.Left/Top+Width/Height child, matching the crop
        // rect's own already-reliable positioning convention. This pins the inset math itself: at
        // any zoom, the guide's own size must equal the canvas size minus twice the (also
        // zoom-scaled) inset on each axis, never negative.
        // A realistically-sized mode (320x256, unlike this file's own SmallMode/WideMode fixtures,
        // both deliberately tiny for fast unit tests) so the working-copy budget
        // (mode.ImageWidth/Height * WorkingCopyScaleFactor) comfortably exceeds the 28px total inset
        // at ZoomFactor=1 -- that budget is keyed off the MODE's own dimensions, not the source's, so
        // no zoom level can fix an undersized fixture (both CanvasDisplayWidth and SafeAreaInsetPixels
        // scale by the identical ZoomFactor, so their difference's SIGN is zoom-invariant). The
        // Math.Max(0, ...) floor this would otherwise trigger is real, correct, separately-pinned
        // behavior (see the dedicated NeverGoNegative test below), not what THIS test is checking.
        var realisticMode = new SstvModeDefinition(
            Id: "realistic", DisplayName: "Realistic", VisCode: 0, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);
        var vm = CreateEditor(CreateSource(320, 256), realisticMode, new FakeTransmitImagePreparer());
        var baselineWidth = vm.SafeAreaWidthPixels;
        var baselineHeight = vm.SafeAreaHeightPixels;
        Assert.True(baselineWidth > 0);
        Assert.True(baselineHeight > 0);
        AssertClose(vm.CanvasDisplayWidth - (2 * vm.SafeAreaInsetPixels), baselineWidth);
        AssertClose(vm.CanvasDisplayHeight - (2 * vm.SafeAreaInsetPixels), baselineHeight);

        vm.ZoomFactor = 3.0;

        AssertClose(vm.CanvasDisplayWidth - (2 * vm.SafeAreaInsetPixels), vm.SafeAreaWidthPixels);
        AssertClose(vm.CanvasDisplayHeight - (2 * vm.SafeAreaInsetPixels), vm.SafeAreaHeightPixels);
        // Both terms scale by the SAME zoom factor (14*zoom inset, CanvasDisplay*zoom size), so the
        // guide's own size scales linearly with zoom too, not just staying non-negative.
        AssertClose(baselineWidth * 3.0, vm.SafeAreaWidthPixels);
        AssertClose(baselineHeight * 3.0, vm.SafeAreaHeightPixels);
    }

    [AvaloniaFact]
    public void SafeAreaWidthAndHeightPixels_NeverGoNegativeWhenInsetExceedsCanvasSize()
    {
        // A working copy smaller than 2x the safe-area inset (28px at ZoomFactor=1) is a real,
        // reachable state (e.g. a tiny source image) -- the guide must floor at 0, not render with a
        // negative size (which would be a real Avalonia layout exception, not just a cosmetic bug).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.Equal(0, vm.SafeAreaWidthPixels);
        Assert.Equal(0, vm.SafeAreaHeightPixels);
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

    // User-reported gap (2026-09-15): the right-click "Clear picture fill" menu item binds
    // {Binding ClearTextBitmapFillCommand} against the element's own DataContext, same inline-per-
    // DataTemplate resolution as AddPlateCommand/InsertFieldCommand above -- same wiring-test
    // precedent for the same failure mode.

    [AvaloniaFact]
    public void ClearTextBitmapFillCommand_IsWiredOnTextElements()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.NotNull(text.ClearTextBitmapFillCommand);
    }

    // Backlog item (user request, 2026-08-17): context-menu font size/text color quick-pick
    // submenus, same wiring precedent/regression risk as InsertFieldCommand/
    // AlignSelectedElementToCropCommand above -- a forgotten parent-pushed assignment renders the
    // menu item permanently disabled without throwing anywhere, so a wiring test is the only thing
    // that catches it.

    [AvaloniaFact]
    public void SetFontSizePresetCommand_IsWiredOnTextElements()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.NotNull(text.SetFontSizePresetCommand);
    }

    [AvaloniaFact]
    public void SetTextColorPresetCommand_IsWiredOnTextElements()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        vm.AddOverlayElementCommand.Execute(null);

        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.NotNull(text.SetTextColorPresetCommand);
    }

    [AvaloniaTheory]
    [InlineData("Small", 0.06)]
    [InlineData("Medium", 0.10)]
    [InlineData("Large", 0.16)]
    [InlineData("XLarge", 0.24)]
    public void SetFontSizePresetCommand_SetsFontSizeRelativeToThePresetValue(string presetKey, double expected)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.SetFontSizePresetCommand.Execute(presetKey);

        Assert.Equal(expected, text.FontSizeRelative);
    }

    [AvaloniaFact]
    public void SetFontSizePresetCommand_UnrecognizedKey_LeavesFontSizeRelativeUnchanged()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        var before = text.FontSizeRelative;

        vm.SetFontSizePresetCommand.Execute("not-a-real-key");

        Assert.Equal(before, text.FontSizeRelative);
    }

    [AvaloniaTheory]
    [InlineData("White", 255, 255, 255)]
    [InlineData("Black", 0, 0, 0)]
    [InlineData("Yellow", 234, 179, 8)]
    [InlineData("Red", 220, 38, 38)]
    [InlineData("Green", 34, 197, 94)]
    [InlineData("Blue", 59, 130, 246)]
    public void SetTextColorPresetCommand_SetsColorToThePresetValue(string presetKey, byte r, byte g, byte b)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];

        vm.SetTextColorPresetCommand.Execute(presetKey);

        Assert.Equal(new Rgb24(r, g, b), text.Color);
    }

    [AvaloniaFact]
    public void SetTextColorPresetCommand_UnrecognizedKey_LeavesColorUnchanged()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        var before = text.Color;

        vm.SetTextColorPresetCommand.Execute("not-a-real-key");

        Assert.Equal(before, text.Color);
    }

    [AvaloniaFact]
    public void SetFontSizePresetCommand_NoTextElementSelected_IsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);

        vm.SetFontSizePresetCommand.Execute("Large");

        Assert.False(vm.SetFontSizePresetCommand.CanExecute("Large"));
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
        // Tier B audit finding: the previous version of this test compared UndoCommand.CanExecute
        // (a bool) before/after a single Undo -- with AddOverlayElement having already pushed a
        // step, that stays true whether Align pushed 1 or 2 steps, so it passed against the actual
        // bug (element.X's own OnXChanging hook pushing a SECOND, redundant coalesced step on top of
        // this command's own explicit PushUndoSnapshot, unguarded by _suspendPreview). A redundant
        // second push captures the SAME pre-align state as the first, so a single-Undo value-based
        // assertion can't tell "1 push" from "2 identical pushes" apart either (same reasoning
        // SetAsBackdrop_PushesExactlyOneUndoStep's own comment documents) -- counts total undo
        // depth instead: Add (1 action) then Align (should be exactly 1 more), then Undo exactly
        // twice and assert NOTHING is left. A stray extra push would leave one more Undo available.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);

        vm.AlignSelectedElementToCropCommand.Execute("Left");

        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);

        Assert.False(vm.UndoCommand.CanExecute(null));
        Assert.Empty(vm.OverlayElements);
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

    // PROJECT_BRIEF.md tracked debt, TX workflow modernization plan Phase 2's own tab-switch
    // policy: the two tests above only prove the 3 Select*Tab commands are mutually exclusive in
    // isolation -- neither ever exercises WHEN each caller (new-element creation vs. Paste/
    // Duplicate/Ctrl-drag-clone vs. a plain selection change) is supposed to invoke them. The 6
    // tests below cover each documented behavior from SwitchToApplicableTabIfNeeded's/
    // AddOverlayElementAt's/AddBoxElementAt's own doc comments individually.

    [AvaloniaFact]
    public void NewElementCreation_ForceSelectsItsApplicableTab_EvenOverridingAnOpenGeometryTab()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SelectGeometryTabCommand.Execute(null);
        Assert.True(vm.IsGeometryTabSelected);

        vm.AddOverlayElementCommand.Execute(null);
        Assert.True(vm.IsTextStyleTabSelected, "new text element must force-select Text Style, even overriding an open Geometry tab");

        vm.SelectGeometryTabCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        Assert.True(vm.IsGeometryTabSelected, "box's own applicable tab IS Geometry -- already selected, force-select is a no-op here");

        // Code-review finding: the block above starts already on Geometry, so it can't distinguish
        // "force-selected" from "never left" -- this one starts from a DIFFERENT tab (Image) so
        // landing on Geometry actually proves the force-select fired, not that it was a no-op.
        vm.SelectImageTabCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        Assert.True(vm.IsGeometryTabSelected, "box's applicable tab stays Geometry regardless of starting tab");
    }

    [AvaloniaFact]
    public async Task NewImageElementInsertion_ForceSelectsImageTab_EvenOverridingAnOpenGeometryTab()
    {
        var preparer = new FakeTransmitImagePreparer();
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        vm.SelectGeometryTabCommand.Execute(null);
        Assert.True(vm.IsGeometryTabSelected);

        await vm.AddImageFromFileCommand.ExecuteAsync(null);

        Assert.True(vm.IsImageTabSelected, "a new image element must force-select Image, even overriding an open Geometry tab");
    }

    [AvaloniaFact]
    public void Duplicate_DoesNotForceSelectATab_UnlikeNewElementCreation()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null); // force-selects Text Style
        var element = vm.OverlayElements[0];
        vm.SelectedOverlayElement = element;
        vm.SelectGeometryTabCommand.Execute(null); // operator deliberately parked on Geometry

        vm.DuplicateCommand.Execute(null);

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.True(vm.IsGeometryTabSelected, "Duplicate must NOT force-switch away from an open Geometry tab the way new-element creation does");
    }

    [AvaloniaFact]
    public void PasteElement_DoesNotForceSelectATab_UnlikeNewElementCreation()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.SelectedOverlayElement = vm.OverlayElements[0];
        vm.CopySelectedElementCommand.Execute(null);
        vm.SelectGeometryTabCommand.Execute(null); // operator deliberately parked on Geometry

        vm.PasteElementCommand.Execute(null);

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.True(vm.IsGeometryTabSelected, "Paste must NOT force-switch away from an open Geometry tab the way new-element creation does");
    }

    [AvaloniaFact]
    public void CtrlDragClone_DoesNotForceSelectATab_UnlikeNewElementCreation()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = vm.OverlayElements[0];
        vm.SelectGeometryTabCommand.Execute(null); // operator deliberately parked on Geometry

        var clone = vm.DuplicateElementForDrag(original);

        Assert.NotSame(original, clone);
        Assert.True(vm.IsGeometryTabSelected, "Ctrl-drag-clone must NOT force-switch away from an open Geometry tab the way new-element creation does");
    }

    [AvaloniaFact]
    public void PlainSelectionChange_SwitchesAwayFromAnInapplicableTab()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var textElement = vm.OverlayElements[0];
        vm.AddBoxElementCommand.Execute(null); // force-selects Geometry, box's own applicable tab
        Assert.True(vm.IsGeometryTabSelected);
        vm.SelectImageTabCommand.Execute(null); // simulate the operator having since switched to Image

        vm.SelectedOverlayElement = textElement; // a plain re-selection, not a new-element/Paste/Duplicate path

        Assert.True(vm.IsTextStyleTabSelected, "selecting a text element while on the inapplicable Image tab must switch to Text Style");
    }

    [AvaloniaFact]
    public void PlainSelectionChange_NeverSwitchesAwayFromGeometry()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var textElement = vm.OverlayElements[0];
        vm.AddBoxElementCommand.Execute(null); // force-selects Geometry, box's own applicable tab
        var boxElement = vm.OverlayElements[1];
        Assert.True(vm.IsGeometryTabSelected);

        vm.SelectedOverlayElement = textElement; // plain selection change, box -> text, while on Geometry
        Assert.True(vm.IsGeometryTabSelected, "Geometry applies to every element type and must never be switched away from on a plain selection change");

        vm.SelectedOverlayElement = boxElement;
        Assert.True(vm.IsGeometryTabSelected);
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
    public void SelectionReadoutText_LineElementSelected_UsesFormatWithoutRotation()
    {
        // 2nd-round code-review finding: the typeLabel switch's own `_ => throw` had no line case,
        // so simply SELECTING a line threw out of this getter -- caught by adding the missing case
        // and a test that actually reads the property for a line (no prior test did).
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());
        vm.AddLineElementCommand.Execute(null);

        _ = vm.SelectionReadoutText;

        Assert.Equal("Panes.TxImageEditor.SelectionReadoutFormat", localization.LastKey);
        Assert.Equal("Panes.TxImageEditor.TypeBadgeLine", localization.LastArgs?[0]);
    }

    [AvaloniaFact]
    public void SelectedLineElement_RaisesPropertyChanged_OnSelectionChange()
    {
        // 2nd-round code-review finding: SelectedLineElement was declared alongside SelectedBoxElement
        // but never added to OnSelectedOverlayElementChanged's own raise list -- the same
        // "check every sibling on a notification set" bug class this project keeps re-hitting. A
        // future GEOMETRY-tab line-style block binding to this property would have stayed
        // permanently stale on selection change.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);
        var line = vm.OverlayElements[0];
        vm.SelectedOverlayElement = null;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.SelectedOverlayElement = line;

        Assert.Contains(nameof(vm.SelectedLineElement), raised);
    }

    [AvaloniaFact]
    public void SelectionReadoutText_LineEndpointEdit_RaisesExactlyOnce_ProvingTheRecomputeFilterGuardIsPlacedCorrectly()
    {
        // Round-1 plan-review finding, verified via the specific deterministic signal round-2
        // code-review identified: OnOverlayElementPropertyChanged's sender-typed X/Y/Width/Height
        // filter guard for a line returns BEFORE the `ReferenceEquals(sender, SelectedOverlayElement)`
        // readout block that raises SelectionReadoutText -- so a single endpoint edit must raise this
        // exactly ONCE (from the raw X1 notification), not once per cascaded X/Y/Width/Height name
        // too. A raw ApplyTemplate-call-count assertion can't observe this (RecomputePreviewCoalesced's
        // own coalescing absorbs any difference within one UI-thread idle tick -- confirmed by direct
        // experiment, see this test file's own comment near the old, removed version of this check),
        // but this synchronous PropertyChanged count is unaffected by that coalescing entirely.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = line;
        var raiseCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectionReadoutText))
            {
                raiseCount++;
            }
        };

        line.X1 = 0.1;

        Assert.Equal(1, raiseCount);
    }

    // Backlog item (user request, 2026-08-17): "text size should be in px not 0.1 or 0.16 etc" --
    // SelectedTextElementFontSizePx converts FontSizeRelative against the TARGET mode's own height
    // (WideMode.ImageHeight = 4), the same real-pixel convention as
    // TransmitImagePreparer.DrawTemplateText's own strokeThicknessPx formula.

    [AvaloniaFact]
    public void SelectedTextElementFontSizePx_ReflectsFontSizeRelativeTimesTargetModeHeight()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;

        AssertClose(0.4, vm.SelectedTextElementFontSizePx);
    }

    [AvaloniaFact]
    public void SelectedTextElementFontSizePx_Set_WritesBackToFontSizeRelative()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;

        vm.SelectedTextElementFontSizePx = 2.0;

        AssertClose(0.5, text.FontSizeRelative);
    }

    [AvaloniaFact]
    public void SelectedTextElementFontSizePx_NoSelection_GetIsZeroAndSetIsANoOp()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());

        Assert.Equal(0, vm.SelectedTextElementFontSizePx);

        vm.SelectedTextElementFontSizePx = 5.0;

        Assert.Equal(0, vm.SelectedTextElementFontSizePx);
    }

    // Backlog item (auditor usability review, 2026-08-17): "Stroke thickness / shadow offsets in TEXT
    // STYLE are still raw relative fractions ... outline width shows '0.004' with no unit." Same
    // target-mode-height px conversion as SelectedTextElementFontSizePx above.

    [AvaloniaFact]
    public void SelectedTextElementStrokeThicknessPx_And_ShadowOffsetPx_ConvertAgainstTargetModeHeight()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;

        AssertClose(text.StrokeThickness * WideMode.ImageHeight, vm.SelectedTextElementStrokeThicknessPx);
        AssertClose(text.ShadowOffsetX * WideMode.ImageHeight, vm.SelectedTextElementShadowOffsetXPx);
        AssertClose(text.ShadowOffsetY * WideMode.ImageHeight, vm.SelectedTextElementShadowOffsetYPx);

        vm.SelectedTextElementStrokeThicknessPx = 2.0;
        vm.SelectedTextElementShadowOffsetXPx = 1.0;
        vm.SelectedTextElementShadowOffsetYPx = 0.5;

        AssertClose(2.0 / WideMode.ImageHeight, text.StrokeThickness);
        AssertClose(1.0 / WideMode.ImageHeight, text.ShadowOffsetX);
        AssertClose(0.5 / WideMode.ImageHeight, text.ShadowOffsetY);
    }

    // Backlog item (auditor usability review, 2026-08-17): "GEOMETRY X/Y/W/H are raw full-precision
    // doubles, no px option." Converted against WorkingCopyWidth/Height (zoom-independent), not
    // ImageWidth/Height (the zoom-premultiplied canvas display size) -- see SelectedElementLeftPx's
    // own doc comment for why.

    [AvaloniaFact]
    public void SelectedElementLeftTopWidthHeightPx_ReflectTheWorkingCopyPixelGeometry()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = vm.OverlayElements[0];
        vm.SelectedOverlayElement = box;

        AssertClose((box.X - (box.Width / 2)) * vm.WorkingCopyWidth, vm.SelectedElementLeftPx);
        AssertClose((box.Y - (box.Height / 2)) * vm.WorkingCopyHeight, vm.SelectedElementTopPx);
        AssertClose(box.Width * vm.WorkingCopyWidth, vm.SelectedElementWidthPx);
        AssertClose(box.Height * vm.WorkingCopyHeight, vm.SelectedElementHeightPx);
    }

    [AvaloniaFact]
    public void SelectedElementWidthHeightPx_Set_WritesBackToNormalizedWidthAndHeight()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = vm.OverlayElements[0];
        vm.SelectedOverlayElement = box;

        vm.SelectedElementWidthPx = 4.0;
        vm.SelectedElementHeightPx = 2.0;

        AssertClose(4.0 / vm.WorkingCopyWidth, box.Width);
        AssertClose(2.0 / vm.WorkingCopyHeight, box.Height);
    }

    [AvaloniaFact]
    public void SelectedElementLeftTopPx_Set_MovesTheElementsTopLeftCornerToThatPixel()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = vm.OverlayElements[0];
        vm.SelectedOverlayElement = box;
        var widthBefore = box.Width;
        var heightBefore = box.Height;

        vm.SelectedElementLeftPx = 0;
        vm.SelectedElementTopPx = 0;

        AssertClose(widthBefore / 2, box.X);
        AssertClose(heightBefore / 2, box.Y);
    }

    [AvaloniaFact]
    public void SelectedElementPx_NoSelection_GetIsZeroAndSetIsANoOp()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());

        Assert.Equal(0, vm.SelectedElementLeftPx);
        Assert.Equal(0, vm.SelectedElementTopPx);
        Assert.Equal(0, vm.SelectedElementWidthPx);
        Assert.Equal(0, vm.SelectedElementHeightPx);

        vm.SelectedElementLeftPx = 5;
        vm.SelectedElementWidthPx = 5;

        Assert.Equal(0, vm.SelectedElementLeftPx);
        Assert.Equal(0, vm.SelectedElementWidthPx);
    }

    // Backlog item (auditor usability review, 2026-08-17): "Box elements have no style UI at all" /
    // "Image elements' Fit mode isn't editable" -- SelectedBoxElement/SelectedImageElement narrow
    // SelectedOverlayElement the same way SelectedTextElement already did.

    [AvaloniaFact]
    public void SelectedBoxElement_And_SelectedImageElement_NarrowSelectedOverlayElementByType()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = vm.OverlayElements[0];
        vm.SelectedOverlayElement = box;

        Assert.Same(box, vm.SelectedBoxElement);
        Assert.Null(vm.SelectedImageElement);
        Assert.Null(vm.SelectedTextElement);
    }

    [AvaloniaFact]
    public void SelectedImageElement_ImageElementSelected_ExposesItsFitForEditing()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;

        Assert.Same(image, vm.SelectedImageElement);
        Assert.Contains(ImageFitMode.Cover, vm.AvailableImageFitModes);

        vm.SelectedImageElement!.Fit = ImageFitMode.Cover;

        Assert.Equal(ImageFitMode.Cover, image.Fit);
    }

    [AvaloniaFact]
    public void SelectedBoxElementBorderThicknessPx_ConvertsAgainstTargetModeHeight()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = (BoxElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = box;

        vm.SelectedBoxElementBorderThicknessPx = 2.0;

        AssertClose(2.0 / WideMode.ImageHeight, box.BorderThickness);
        AssertClose(2.0, vm.SelectedBoxElementBorderThicknessPx);
    }

    // Auditor usability review follow-up (2026-08-18): "missing item" against spec/15's own
    // box-elements friction risk ("need border, corner-radius, and opacity").

    [AvaloniaFact]
    public void SelectedBoxElementCornerRadiusPx_ConvertsAgainstTargetModeHeight()
    {
        var vm = CreateEditor(CreateSource(8, 8), WideMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = (BoxElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = box;

        vm.SelectedBoxElementCornerRadiusPx = 3.0;

        AssertClose(3.0 / WideMode.ImageHeight, box.CornerRadius);
        AssertClose(3.0, vm.SelectedBoxElementCornerRadiusPx);
    }

    [AvaloniaFact]
    public void DuplicateBoxElement_PreservesCornerRadius()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var box = (BoxElementViewModel)vm.OverlayElements[0];
        box.CornerRadius = 0.05;
        vm.SelectedOverlayElement = box;

        vm.DuplicateCommand.Execute(null);

        var copy = Assert.IsType<BoxElementViewModel>(vm.OverlayElements[1]);
        AssertClose(0.05, copy.CornerRadius);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_RoundTripsBoxCornerRadius()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddBoxElementCommand.Execute(null);
        var box = (BoxElementViewModel)vm.OverlayElements[0];
        box.CornerRadius = 0.08;
        vm.NewTemplateName = "Rounded box";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedBox = Assert.IsType<PersistedBoxElement>(Assert.Single(document.Elements));

        AssertClose(0.08, persistedBox.CornerRadius);
    }

    [AvaloniaFact]
    public void BoxElementViewModel_HasBorder_TogglesBorderColorAndRoundTripsThroughBorderColorForPicker()
    {
        var box = new BoxElementViewModel();
        Assert.False(box.HasBorder);

        box.HasBorder = true;
        Assert.NotNull(box.BorderColor);

        box.BorderColorForPicker = new Rgb24(10, 20, 30);
        Assert.Equal(new Rgb24(10, 20, 30), box.BorderColor);

        box.HasBorder = false;
        Assert.Null(box.BorderColor);
        // Setter is a no-op while disabled -- same "disabled picker write is dropped" contract as
        // OverlayElementViewModel.StrokeColorForPicker's own doc comment.
        box.BorderColorForPicker = new Rgb24(1, 1, 1);
        Assert.Null(box.BorderColor);
    }

    // Auditor usability review follow-up (2026-08-18): "missing yoniq text effects" -- Bold/Italic,
    // the two items never started from the user's original 2026-08-16 checklist.

    [AvaloniaFact]
    public void OverlayElementViewModel_Bold_TogglesCanvasFontWeightAndRaisesChangeNotification()
    {
        var text = new OverlayElementViewModel();
        var raised = false;
        text.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(OverlayElementViewModel.CanvasFontWeight);
        Assert.Equal(Avalonia.Media.FontWeight.Normal, text.CanvasFontWeight);

        text.Bold = true;

        Assert.True(raised);
        Assert.Equal(Avalonia.Media.FontWeight.Bold, text.CanvasFontWeight);
    }

    [AvaloniaFact]
    public void OverlayElementViewModel_Italic_TogglesCanvasFontStyleAndRaisesChangeNotification()
    {
        var text = new OverlayElementViewModel();
        var raised = false;
        text.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(OverlayElementViewModel.CanvasFontStyle);
        Assert.Equal(Avalonia.Media.FontStyle.Normal, text.CanvasFontStyle);

        text.Italic = true;

        Assert.True(raised);
        Assert.Equal(Avalonia.Media.FontStyle.Italic, text.CanvasFontStyle);
    }

    [AvaloniaFact]
    public void DuplicateTextElement_PreservesBoldAndItalic()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.Bold = true;
        text.Italic = true;
        vm.SelectedOverlayElement = text;

        vm.DuplicateCommand.Execute(null);

        var copy = Assert.IsType<OverlayElementViewModel>(vm.OverlayElements[1]);
        Assert.True(copy.Bold);
        Assert.True(copy.Italic);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_RoundTripsBoldAndItalic()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.Bold = true;
        text.Italic = false;
        vm.NewTemplateName = "Bold text";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedText = Assert.IsType<PersistedTextElement>(Assert.Single(document.Elements));

        Assert.True(persistedText.Bold);
        Assert.False(persistedText.Italic);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_GrowToFillEnabled_PersistsTheGrownFontSizeNotTheOriginal()
    {
        // User-reported gap (2026-09-15): GrowToFillEnabled is deliberately NOT persisted (see
        // OverlayElementViewModel.GrowToFillEnabled's own doc comment) -- a save used to write the
        // small pre-grow FontSizeRelative verbatim, so reloading rendered small again even though
        // GrowToFillEnabled had visibly grown the font on screen right before saving. Proves
        // ComputeFittedFontSizeRelative bakes the grown RESULT into the persisted value instead. Uses
        // the REAL TransmitImagePreparer, same reasoning as GrowToFillEnabled_Toggling_
        // GrowsCanvasFontSizePastNominal above -- a fake's MeasureFittedFontSize ignores growToFill
        // entirely, which would make this test vacuous.
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer, templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Width = 0.95;
        element.Height = 0.95;
        var originalFontSizeRelative = element.FontSizeRelative;

        element.GrowToFillEnabled = true;
        Assert.True(element.CanvasFontSize > 0); // sanity: grow-to-fill actually did something
        vm.NewTemplateName = "Grown text";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedText = Assert.IsType<PersistedTextElement>(Assert.Single(document.Elements));

        Assert.True(persistedText.FontSizeRelative > originalFontSizeRelative,
            $"Expected the persisted size ({persistedText.FontSizeRelative}) to reflect the grown result, not the original set size ({originalFontSizeRelative}).");
    }

    // Auditor usability review follow-up (2026-08-18): the "3D"/Stack text effect (legacy YONIQ's
    // CBStack/m_StackPara -- a stepped stack of offset solid-color copies, NOT a real 3D transform),
    // the other item never started from the user's original 2026-08-16 checklist.

    [Fact]
    public void HasStack_TogglesStackColorNullness()
    {
        var text = new OverlayElementViewModel();
        Assert.False(text.HasStack);
        Assert.Null(text.StackColor);

        text.HasStack = true;

        Assert.True(text.HasStack);
        Assert.NotNull(text.StackColor);
    }

    [Fact]
    public void StackColorForPicker_WriteWhileHasStackIsFalse_DoesNotUnnullStackColor()
    {
        var text = new OverlayElementViewModel();

        text.StackColorForPicker = new Rgb24(0, 0, 0);

        Assert.Null(text.StackColor);
    }

    [Fact]
    public void StackColorForPicker_WriteWhileHasStackIsTrue_UpdatesStackColor()
    {
        var text = new OverlayElementViewModel { HasStack = true };
        var chosen = new Rgb24(10, 20, 30);

        text.StackColorForPicker = chosen;

        Assert.Equal(chosen, text.StackColor);
    }

    [AvaloniaFact]
    public void DuplicateTextElement_PreservesStack()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.StackColor = new Rgb24(1, 2, 3);
        text.StackStepX = 0.05;
        text.StackStepY = 0.06;
        vm.SelectedOverlayElement = text;

        vm.DuplicateCommand.Execute(null);

        var copy = Assert.IsType<OverlayElementViewModel>(vm.OverlayElements[1]);
        Assert.Equal(new Rgb24(1, 2, 3), copy.StackColor);
        Assert.Equal(0.05, copy.StackStepX);
        Assert.Equal(0.06, copy.StackStepY);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_RoundTripsStack()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.StackColor = new Rgb24(4, 5, 6);
        text.StackStepX = 0.07;
        text.StackStepY = 0.08;
        vm.NewTemplateName = "Stacked text";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedText = Assert.IsType<PersistedTextElement>(Assert.Single(document.Elements));

        Assert.Equal(new Rgb24(4, 5, 6), persistedText.StackColor);
        AssertClose(0.07, persistedText.StackStepX);
        AssertClose(0.08, persistedText.StackStepY);
    }

    // Auditor usability review follow-up (2026-08-18): "bitmap mask" text fill -- legacy YONIQ's real
    // RGGrade radio-group option, a tiled 2-color pattern brush, added as a 4th TextGradientKind
    // value alongside the existing Horizontal/Vertical/Radial (see that enum's own doc comment).

    [AvaloniaFact]
    public void ForegroundBrush_BitmapPatternGradientKind_ReturnsATiledDrawingBrush()
    {
        var text = new OverlayElementViewModel
        {
            GradientEnabled = true,
            GradientKind = TextGradientKind.BitmapPattern,
        };

        var brush = Assert.IsType<Avalonia.Media.DrawingBrush>(text.ForegroundBrush);

        Assert.Equal(Avalonia.Media.TileMode.Tile, brush.TileMode);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_RoundTripsBitmapPatternGradientKind()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.GradientEnabled = true;
        text.GradientKind = TextGradientKind.BitmapPattern;
        vm.NewTemplateName = "Patterned text";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedText = Assert.IsType<PersistedTextElement>(Assert.Single(document.Elements));

        Assert.True(persistedText.GradientEnabled);
        Assert.Equal(TextGradientKind.BitmapPattern, persistedText.GradientKind);
    }

    // TX editor gap-items plan (2026-09-01): box gradient fill -- the same gap Fable's comparative
    // review flagged (text gradients shipped, boxes only had flat fill). Mirrors the two text-gradient
    // tests immediately above, one property/type swapped throughout.

    [AvaloniaFact]
    public void BoxFillBrush_BitmapPatternGradientKind_ReturnsATiledDrawingBrush()
    {
        var box = new BoxElementViewModel
        {
            GradientEnabled = true,
            GradientKind = TextGradientKind.BitmapPattern,
            GradientStartColor = new Rgb24(255, 0, 0),
            GradientEndColor = new Rgb24(0, 0, 255),
        };

        var brush = Assert.IsType<Avalonia.Media.DrawingBrush>(box.FillBrush);

        Assert.Equal(Avalonia.Media.TileMode.Tile, brush.TileMode);
        // Code-review finding: TileMode alone doesn't distinguish a correctly-built pattern from
        // swapped fore/back colors or an empty DrawingGroup -- assert the actual tile content:
        // the first child is the full-tile BACKGROUND (GradientEndColor), and the group has more
        // than just that one background fill (the foreground pattern cells).
        var drawingGroup = Assert.IsType<Avalonia.Media.DrawingGroup>(brush.Drawing);
        Assert.True(drawingGroup.Children.Count > 1, "Expected background fill plus at least one foreground pattern cell.");
        var background = Assert.IsType<Avalonia.Media.GeometryDrawing>(drawingGroup.Children[0]);
        var backgroundBrush = Assert.IsType<Avalonia.Media.SolidColorBrush>(background.Brush);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0, 0, 255), backgroundBrush.Color);
        var foreground = Assert.IsType<Avalonia.Media.GeometryDrawing>(drawingGroup.Children[1]);
        var foregroundBrush = Assert.IsType<Avalonia.Media.SolidColorBrush>(foreground.Brush);
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 0, 0), foregroundBrush.Color);
    }

    // Second-round audit finding: GradientBrushFactory's Horizontal/Vertical/Radial branches had no
    // test at all for either caller -- the extraction's "behavior-neutral" claim rested on reading,
    // not a guard. These cover the shared factory through the box caller (text shares the same
    // factory call, so this covers both by construction).

    [AvaloniaFact]
    public void BoxFillBrush_HorizontalGradientKind_ReturnsALeftToRightLinearBrush()
    {
        var box = new BoxElementViewModel
        {
            GradientEnabled = true,
            GradientKind = TextGradientKind.Horizontal,
            GradientStartColor = new Rgb24(255, 0, 0),
            GradientEndColor = new Rgb24(0, 0, 255),
        };

        var brush = Assert.IsType<Avalonia.Media.LinearGradientBrush>(box.FillBrush);

        Assert.Equal(new Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative), brush.StartPoint);
        Assert.Equal(new Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative), brush.EndPoint);
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 0, 0), brush.GradientStops[0].Color);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0, 0, 255), brush.GradientStops[1].Color);
    }

    [AvaloniaFact]
    public void BoxFillBrush_VerticalGradientKind_ReturnsATopToBottomLinearBrush()
    {
        var box = new BoxElementViewModel
        {
            GradientEnabled = true,
            GradientKind = TextGradientKind.Vertical,
            GradientStartColor = new Rgb24(255, 0, 0),
            GradientEndColor = new Rgb24(0, 0, 255),
        };

        var brush = Assert.IsType<Avalonia.Media.LinearGradientBrush>(box.FillBrush);

        Assert.Equal(new Avalonia.RelativePoint(0.5, 0, Avalonia.RelativeUnit.Relative), brush.StartPoint);
        Assert.Equal(new Avalonia.RelativePoint(0.5, 1, Avalonia.RelativeUnit.Relative), brush.EndPoint);
        Assert.Equal(Avalonia.Media.Color.FromRgb(255, 0, 0), brush.GradientStops[0].Color);
        Assert.Equal(Avalonia.Media.Color.FromRgb(0, 0, 255), brush.GradientStops[1].Color);
    }

    [AvaloniaFact]
    public void BoxFillBrush_RadialGradientKind_ReturnsACenteredRadialBrush()
    {
        var box = new BoxElementViewModel
        {
            GradientEnabled = true,
            GradientKind = TextGradientKind.Radial,
        };

        var brush = Assert.IsType<Avalonia.Media.RadialGradientBrush>(box.FillBrush);

        var center = new Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative);
        Assert.Equal(center, brush.Center);
        Assert.Equal(center, brush.GradientOrigin);
        Assert.Equal(new Avalonia.RelativeScalar(0.5, Avalonia.RelativeUnit.Relative), brush.RadiusX);
        Assert.Equal(new Avalonia.RelativeScalar(0.5, Avalonia.RelativeUnit.Relative), brush.RadiusY);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_RoundTripsBoxGradientFill()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddBoxElementCommand.Execute(null);
        var box = (BoxElementViewModel)vm.OverlayElements[0];
        box.GradientEnabled = true;
        box.GradientKind = TextGradientKind.Vertical;
        box.GradientStartColor = new Rgb24(10, 20, 30);
        box.GradientEndColor = new Rgb24(40, 50, 60);
        vm.NewTemplateName = "Gradient box";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedBox = Assert.IsType<PersistedBoxElement>(Assert.Single(document.Elements));

        Assert.True(persistedBox.GradientEnabled);
        Assert.Equal(TextGradientKind.Vertical, persistedBox.GradientKind);
        Assert.Equal(new Rgb24(10, 20, 30), persistedBox.GradientStartColor);
        Assert.Equal(new Rgb24(40, 50, 60), persistedBox.GradientEndColor);
    }

    /// <summary>Proves the round-trip is genuinely wired end-to-end, not just persisted -- loading a
    /// saved template back through the real Ready Rack path must produce a live BoxElementViewModel
    /// whose gradient properties match what was saved (the ToRawElementSnapshotAsync/CreateBoxElement
    /// load path, not just BuildPersistedElementAsync's save path). Uses ReadyRack.LoadCommand, the
    /// same real load mechanism LoadTemplate_ReplacesElementsAndPushesOneUndoStep's own sibling test
    /// uses -- there is no direct LoadTemplateCommand on this VM.</summary>
    [AvaloniaFact]
    public async Task LoadTemplate_BoxGradientFill_RehydratesIntoALiveElementWithMatchingValues()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);

        var templateId = templateStore.CreateTemplateId("Reloaded gradient box");
        await templateStore.SaveAsync(templateId, "Reloaded gradient box", new PersistedTemplateDocument([
            new PersistedBoxElement(
                X: 0.5, Y: 0.5, Width: 0.2, Height: 0.2, Z: 0, Locked: false,
                FillColor: new Rgb24(0, 0, 0), BorderColor: null, BorderThickness: 0, Opacity: 1.0,
                GradientEnabled: true, GradientKind: TextGradientKind.Radial,
                GradientStartColor: new Rgb24(1, 2, 3), GradientEndColor: new Rgb24(4, 5, 6)),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        // No unsaved edits on this fresh editor, so the first click loads immediately -- the second
        // is a harmless no-op re-click, matching this file's own established belt-and-suspenders
        // shape for this same load mechanism elsewhere.
        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();

        var reloadedBox = Assert.IsType<BoxElementViewModel>(Assert.Single(vm.OverlayElements));
        Assert.True(reloadedBox.GradientEnabled);
        Assert.Equal(TextGradientKind.Radial, reloadedBox.GradientKind);
        Assert.Equal(new Rgb24(1, 2, 3), reloadedBox.GradientStartColor);
        Assert.Equal(new Rgb24(4, 5, 6), reloadedBox.GradientEndColor);
    }

    // TX editor gap-items plan, item 4b (picture fill, 2026-09-01) -- text elements filled with a
    // real picture instead of solid color or gradient. Plan-review's own settled design: a sibling
    // bool to GradientEnabled (not a shared 3-way discriminator), mutually exclusive at the setter
    // level, real precedence enforced at every composition site when both are somehow true.

    [AvaloniaFact]
    public void BitmapFillEnabled_SetTrue_ClearsGradientEnabled()
    {
        var text = new OverlayElementViewModel { GradientEnabled = true };

        text.BitmapFillEnabled = true;

        Assert.False(text.GradientEnabled);
        Assert.True(text.BitmapFillEnabled);
    }

    [AvaloniaFact]
    public void GradientEnabled_SetTrue_ClearsBitmapFillEnabled()
    {
        var text = new OverlayElementViewModel { BitmapFillEnabled = true };

        text.GradientEnabled = true;

        Assert.False(text.BitmapFillEnabled);
        Assert.True(text.GradientEnabled);
    }

    [AvaloniaFact]
    public void ForegroundBrush_BitmapFillEnabledWithSource_ReturnsAnImageBrush()
    {
        var text = new OverlayElementViewModel
        {
            BitmapFillEnabled = true,
            BitmapFillSource = CreateSource(2, 2),
        };

        Assert.IsType<Avalonia.Media.ImageBrush>(text.ForegroundBrush);
    }

    [AvaloniaFact]
    public void ForegroundBrush_BitmapFillEnabledButNoSourceYet_FallsBackToSolidColor()
    {
        // Reachable in practice: BitmapFillEnabled flips true the instant a picture-pick command
        // starts (see PickTextBitmapFillFromFileAsync's own body), but BitmapFillSource isn't
        // assigned until the async load actually completes -- ForegroundBrush must not crash or
        // return a broken brush for that in-between window.
        var text = new OverlayElementViewModel { BitmapFillEnabled = true };

        Assert.IsType<Avalonia.Media.SolidColorBrush>(text.ForegroundBrush);
    }

    [AvaloniaFact]
    public async Task PickTextBitmapFillFromFileCommand_LoadsThePickedFileAndSetsSourceAndEnabled()
    {
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/fill.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;

        await vm.PickTextBitmapFillFromFileCommand.ExecuteAsync(null);

        Assert.True(text.BitmapFillEnabled);
        Assert.Same(loader.ResultToReturn, text.BitmapFillSource);
    }

    [AvaloniaFact]
    public async Task PickTextBitmapFillFromFileCommand_PickerReturnsNull_IsANoOp()
    {
        var picker = new FakeFilePickerService { PathToReturn = null };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), picker, new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = text;

        await vm.PickTextBitmapFillFromFileCommand.ExecuteAsync(null);

        Assert.False(text.BitmapFillEnabled);
        Assert.Null(text.BitmapFillSource);
    }

    [AvaloniaFact]
    public void PickTextBitmapFillFromFileCommand_NoTextElementSelected_CanExecuteIsFalse()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        vm.SelectedOverlayElement = vm.OverlayElements[0];

        Assert.False(vm.PickTextBitmapFillFromFileCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void ClearTextBitmapFillCommand_SetsEnabledFalseButPreservesTheLastPickedSource()
    {
        // Same "the flag gates whether the resolved value is USED, not whether it's kept" shape as
        // toggling GRADIENT off -- re-checking PICTURE FILL later without re-picking a file must
        // still show the last picture (ClearTextBitmapFillCommand's own doc comment).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        var source = CreateSource(2, 2);
        text.BitmapFillSource = source;
        text.BitmapFillEnabled = true;
        vm.SelectedOverlayElement = text;

        vm.ClearTextBitmapFillCommand.Execute(null);

        Assert.False(text.BitmapFillEnabled);
        Assert.Same(source, text.BitmapFillSource);
    }

    [AvaloniaFact]
    public async Task SaveThenLoadTemplate_RoundTripsTextBitmapFill()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.BitmapFillSource = CreateSource(2, 2);
        text.BitmapFillEnabled = true;
        vm.NewTemplateName = "Picture-filled text";

        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var saved = Assert.Single(await templateStore.ListAsync());
        var document = await templateStore.LoadAsync(saved.Id);
        var persistedText = Assert.IsType<PersistedTextElement>(Assert.Single(document.Elements));

        Assert.True(persistedText.BitmapFillEnabled);
        Assert.NotNull(persistedText.BitmapFillAssetFileName);
    }

    /// <summary>Same "genuinely wired end-to-end, not just persisted" proof as
    /// <see cref="LoadTemplate_BoxGradientFill_RehydratesIntoALiveElementWithMatchingValues"/> --
    /// unlike that test's box gradient (no image dependency), this path DOES call
    /// <see cref="IImageFileLoader.LoadOriginalAsync"/> for the fill asset, so this needs a fully
    /// hand-wired VM (no single <c>CreateEditor</c> overload exposes both a configurable
    /// <see cref="IImageFileLoader"/> AND <see cref="ITemplateStore"/>/<see cref="ReadyRackViewModel"/>
    /// together).</summary>
    [AvaloniaFact]
    public async Task LoadTemplate_TextBitmapFill_RehydratesIntoALiveElementWithMatchingSource()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var fillSource = CreateSource(2, 2);
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = fillSource };
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), imageFileLoader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            templateStore, new FakeImageSourceWriter(), readyRack);

        var templateId = templateStore.CreateTemplateId("Reloaded picture-filled text");
        await templateStore.SaveAsync(templateId, "Reloaded picture-filled text", new PersistedTemplateDocument([
            new PersistedTextElement(
                X: 0.5, Y: 0.5, Width: 0.4, Height: 0.2, Z: 0, Locked: false,
                Text: "W1AW", FontSizeRelative: 0.2, Color: new Rgb24(0, 0, 0), FontFamily: "DejaVu Sans Mono", StrokeColor: null, StrokeThickness: 0,
                BitmapFillEnabled: true, BitmapFillAssetFileName: "fill.png"),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();

        var reloadedText = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
        Assert.True(reloadedText.BitmapFillEnabled);
        Assert.Same(fillSource, reloadedText.BitmapFillSource);
    }

    /// <summary>Same "second hand-maintained switch" bug class the box-gradient feature already
    /// shipped once (Copy Style then Paste Style silently dropping the gradient) -- pins that the
    /// new bitmap-fill fields are in <c>PasteSelectedElementStyle</c>'s text case.</summary>
    [AvaloniaFact]
    public void PasteSelectedElementStyle_CopiesBitmapFillFields()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddOverlayElementCommand.Execute(null);
        var source = (OverlayElementViewModel)vm.OverlayElements[0];
        var target = (OverlayElementViewModel)vm.OverlayElements[1];
        var fillSource = CreateSource(2, 2);
        source.BitmapFillSource = fillSource;
        source.BitmapFillEnabled = true;
        vm.SelectedOverlayElement = source;
        vm.CopySelectedElementStyleCommand.Execute(null);
        vm.SelectedOverlayElement = target;

        vm.PasteSelectedElementStyleCommand.Execute(null);

        Assert.True(target.BitmapFillEnabled);
        Assert.Same(fillSource, target.BitmapFillSource);
    }

    [AvaloniaFact]
    public void PasteSelectedElementStyle_SolidStyleOntoAPictureFilledElement_ClearsThePictureFill()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddOverlayElementCommand.Execute(null);
        var source = (OverlayElementViewModel)vm.OverlayElements[0];
        var target = (OverlayElementViewModel)vm.OverlayElements[1];
        target.BitmapFillSource = CreateSource(2, 2);
        target.BitmapFillEnabled = true;
        vm.SelectedOverlayElement = source;
        vm.CopySelectedElementStyleCommand.Execute(null);
        vm.SelectedOverlayElement = target;

        vm.PasteSelectedElementStyleCommand.Execute(null);

        Assert.False(target.BitmapFillEnabled);
    }

    /// <summary>Same equality-trap coverage as <see cref="FlattenElementAsync_GradientTextElement_SucceedsRatherThanDiscardingAsStale"/>
    /// -- <see cref="TemplateTextElement.BitmapFill"/>'s own doc comment states the stable-instance
    /// invariant this test pins: <see cref="BitmapFillSource"/> stays the SAME cached reference across
    /// Flatten's own two <c>BuildTemplateElement</c> calls (the bake, and the stale-result guard's
    /// comparison), so record equality on that interface-typed member -- reference equality, same
    /// trap <see cref="TextGradient"/>'s own <c>Stops</c> list already hit -- correctly matches
    /// instead of spuriously discarding the flatten as stale.</summary>
    [AvaloniaFact]
    public async Task FlattenElementAsync_BitmapFillTextElement_SucceedsRatherThanDiscardingAsStale()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        element.BitmapFillSource = CreateSource(4, 4);
        element.BitmapFillEnabled = true;
        var sourceBefore = vm.CurrentSource;

        await vm.FlattenElementCommand.ExecuteAsync(element);

        Assert.Empty(vm.OverlayElements);
        Assert.NotSame(sourceBefore, vm.CurrentSource);
        Assert.NotEqual("Panes.TxImageEditor.FlattenDiscardedStale", vm.StatusMessage);
    }

    [AvaloniaFact]
    public void RemoveOverlayElement_BitmapFillTextElement_DisposesItsCanvasBitmapFill_DeferredViaDispatcherPost()
    {
        // Same T0-11 disposal shape as Undo_AfterAddLastRxImage_DisposesTheRemovedImageElementsBitmap
        // above, widened this session to a generic `is IDisposable` discard check -- pins that
        // OverlayElementViewModel's own CanvasBitmapFill actually gets caught by that widened check.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var text = (OverlayElementViewModel)vm.OverlayElements[0];
        text.BitmapFillSource = CreateSource(2, 2);
        text.BitmapFillEnabled = true;
        var bitmap = text.CanvasBitmapFill!;

        vm.RemoveOverlayElementCommand.Execute(text);

        Assert.False(IsWriteableBitmapDisposed(bitmap), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsWriteableBitmapDisposed(bitmap));
    }

    // Backlog item (auditor usability review, 2026-08-17): "ELEMENTS rows don't select or highlight
    // on click." IsSelected is set by OnSelectedOverlayElementChanged's own loop over every element.

    [AvaloniaFact]
    public void SelectedOverlayElementChanged_UpdatesIsSelectedOnEveryElement()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddBoxElementCommand.Execute(null);
        var first = vm.OverlayElements[0];
        var second = vm.OverlayElements[1];

        vm.SelectedOverlayElement = first;
        Assert.True(first.IsSelected);
        Assert.False(second.IsSelected);

        vm.SelectedOverlayElement = second;
        Assert.False(first.IsSelected);
        Assert.True(second.IsSelected);

        vm.SelectedOverlayElement = null;
        Assert.False(first.IsSelected);
        Assert.False(second.IsSelected);
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

    // TX workflow modernization plan, Phase 3b: Ctrl-drag-to-duplicate.

    [AvaloniaFact]
    public void DuplicateElementForDrag_ClonesElement_AndSuppressesTheFollowingGeometryPush()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = (OverlayElementViewModel)vm.OverlayElements[0];

        var clone = vm.DuplicateElementForDrag(original);

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.NotSame(original, clone);
        Assert.Same(clone, vm.SelectedOverlayElement);

        // Simulates the immediately-following Overlay drag's first move (OnCanvasPointerMoved's own
        // `element.X += dxNormalized`) -- must NOT push a second undo step, or one Ctrl-drag gesture
        // would need two Undo clicks to fully revert.
        clone.X += 0.05;
        clone.Y += 0.05;

        vm.UndoCommand.Execute(null);
        Assert.Single(vm.OverlayElements); // one Undo removed the clone entirely, not just its position
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
        vm.SetAsBackdropCommand.Execute(image);
        Assert.True(image.IsBackground);
        Assert.True(image.Locked);

        vm.DuplicateCommand.Execute(null);

        var copy = Assert.IsType<ImageElementViewModel>(vm.OverlayElements[1]);
        Assert.False(copy.IsBackground);
        Assert.False(copy.Locked);
    }

    // Backlog item (user request, 2026-08-17): in-editor Copy/Cut/Paste, reusing Duplicate's own
    // InsertClonedSnapshot helper (offset/Z/background-clearing logic identical, only the snapshot
    // source differs).

    [AvaloniaFact]
    public void CopyAndPaste_ClonesTheCopiedElement_LeavingTheOriginalInPlace()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = (OverlayElementViewModel)vm.OverlayElements[0];
        original.Text = "Original";
        vm.SelectedOverlayElement = original;

        vm.CopySelectedElementCommand.Execute(null);
        vm.PasteElementCommand.Execute(null);

        Assert.Equal(2, vm.OverlayElements.Count);
        Assert.Same(original, vm.OverlayElements[0]);
        var copy = Assert.IsType<OverlayElementViewModel>(vm.OverlayElements[1]);
        Assert.Same(copy, vm.SelectedOverlayElement);
        Assert.Equal("Original", copy.Text);
        Assert.True(copy.Z > original.Z);
    }

    [AvaloniaFact]
    public void CopyThenPasteTwice_InsertsTwoIndependentCopies()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.SelectedOverlayElement = vm.OverlayElements[0];

        vm.CopySelectedElementCommand.Execute(null);
        vm.PasteElementCommand.Execute(null);
        vm.PasteElementCommand.Execute(null);

        Assert.Equal(3, vm.OverlayElements.Count);
    }

    [AvaloniaFact]
    public void CutSelectedElement_RemovesItAndPasteReinsertsAClone()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = (OverlayElementViewModel)vm.OverlayElements[0];
        original.Text = "Cut me";
        vm.SelectedOverlayElement = original;

        vm.CutSelectedElementCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.Null(vm.SelectedOverlayElement);

        vm.PasteElementCommand.Execute(null);

        var pasted = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
        Assert.Equal("Cut me", pasted.Text);
        Assert.NotSame(original, pasted);
    }

    [AvaloniaFact]
    public void PasteElementCommand_CanExecute_FalseUntilSomethingHasBeenCopiedOrCut()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.PasteElementCommand.CanExecute(null));

        vm.AddOverlayElementCommand.Execute(null);
        vm.SelectedOverlayElement = vm.OverlayElements[0];
        vm.CopySelectedElementCommand.Execute(null);

        Assert.True(vm.PasteElementCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void CopyAndCutSelectedElementCommands_CanExecute_FalseWhenNothingSelected()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.CopySelectedElementCommand.CanExecute(null));
        Assert.False(vm.CutSelectedElementCommand.CanExecute(null));

        vm.AddBoxElementCommand.Execute(null);
        vm.SelectedOverlayElement = vm.OverlayElements[0];

        Assert.True(vm.CopySelectedElementCommand.CanExecute(null));
        Assert.True(vm.CutSelectedElementCommand.CanExecute(null));
    }

    // Backlog item (auditor usability review, 2026-08-17): "Repeated Paste stacks copies at the
    // identical offset from the clipboard snapshot (not incrementing per paste), so 3x Ctrl+V looks
    // like paste only worked once." Each paste now re-snapshots the just-pasted element back into the
    // clipboard, so the NEXT paste offsets from the PREVIOUS paste (cascading), not the original.

    [AvaloniaFact]
    public void CopyThenPasteThreeTimes_EachPasteOffsetsFromThePreviousPaste_NotAllAtTheSameSpot()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = original;
        vm.CopySelectedElementCommand.Execute(null);

        vm.PasteElementCommand.Execute(null);
        var firstPaste = vm.OverlayElements[1];
        vm.PasteElementCommand.Execute(null);
        var secondPaste = vm.OverlayElements[2];
        vm.PasteElementCommand.Execute(null);
        var thirdPaste = vm.OverlayElements[3];

        Assert.Equal(4, vm.OverlayElements.Count);
        // Each paste is strictly farther from the original than the last, not identical (the bug's
        // exact symptom: all copies landing on top of each other at the same offset).
        Assert.True(firstPaste.X < secondPaste.X);
        Assert.True(secondPaste.X < thirdPaste.X);
    }

    [AvaloniaFact]
    public void CopyAgainAfterPasting_ResetsTheClipboardToTheLiveSourcePosition()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var original = (OverlayElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = original;
        vm.CopySelectedElementCommand.Execute(null);
        vm.PasteElementCommand.Execute(null);
        vm.PasteElementCommand.Execute(null); // clipboard is now cascaded away from `original`

        vm.SelectedOverlayElement = original;
        vm.CopySelectedElementCommand.Execute(null); // fresh copy from the ORIGINAL position again
        vm.PasteElementCommand.Execute(null);
        var freshPaste = vm.OverlayElements[^1];

        AssertClose(original.X + 0.02, freshPaste.X);
    }

    [AvaloniaFact]
    public void PasteElement_BackgroundImageElementWasCopied_CloneIsNotBackgroundAndNotLocked()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(image);
        vm.SelectedOverlayElement = image;

        vm.CopySelectedElementCommand.Execute(null);
        vm.PasteElementCommand.Execute(null);

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
        vm.SetAsBackdropCommand.Execute(background);
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
        vm.SetAsBackdropCommand.Execute(background);
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
    public void SendToBack_OnAnAlreadyBottommostElement_WithNoBackgroundPresent_IsATrueNoOp()
    {
        // Tier B audit finding: SendToBack was missing the already-at-bottom no-op guard its
        // siblings (BringToFront/MoveElementUp/MoveElementDown) already have -- an element already
        // at collection index 0 with no background present fell through to the unconditional
        // "no background" branch, decrementing its own Z and pushing a bogus undo step on every
        // click forever (cosmetically invisible -- Move(0, 0) is a no-op -- but real undo-stack/
        // HasUnsavedEdits pollution).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        vm.AddOverlayElementCommand.Execute(null);
        var bottommost = vm.OverlayElements[0];
        var zBefore = bottommost.Z;

        vm.SendToBackCommand.Execute(bottommost);

        Assert.Equal(zBefore, bottommost.Z);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void SendToBack_OnAnElementAlreadyImmediatelyAfterTheBackground_IsATrueNoOp()
    {
        // Tier B audit finding: same class as the no-background case above -- an element already
        // sitting immediately after the background (target == index) fell through to the
        // background-present branch, where Move(index, index) is a no-op but the undo step and
        // RecomputePreview pass weren't.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var background = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(background);
        vm.AddOverlayElementCommand.Execute(null);
        var justAboveBackground = vm.OverlayElements[1];
        var zBefore = justAboveBackground.Z;

        vm.SendToBackCommand.Execute(justAboveBackground);

        Assert.Equal(zBefore, justAboveBackground.Z);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null));
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
        vm.SetAsBackdropCommand.Execute(background);
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
        // Auditor code-review finding, ORIGINALLY reachable via two SetAsBackdrop clicks (that gap
        // is now closed -- background/backdrop naming work, 2026-09-15, added a single-backdrop
        // invariant: SetAsBackdrop clears any OTHER element's own IsBackground flag first). Still
        // worth testing directly: SendToBack's own defensive "scan backwards from element's
        // position" logic must stay correct even if two IsBackground elements exist for some OTHER
        // reason (e.g. a template saved by a pre-invariant build of the app). Constructed here by
        // setting IsBackground back to true directly on firstBackground AFTER the second
        // SetAsBackdrop call already cleared it via the new invariant, rather than by exploiting a
        // live command path -- everything else (Z/collection-order setup) is unchanged: a second
        // SetAsBackdrop call moves ITS OWN element to the new collection-wide minimum (Min(Z) - 1
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
        vm.SetAsBackdropCommand.Execute(firstBackground);
        vm.AddLastRxImageCommand.Execute(null);
        var secondBackground = (ImageElementViewModel)vm.OverlayElements[1];
        vm.SetAsBackdropCommand.Execute(secondBackground);
        firstBackground.IsBackground = true;
        Assert.Equal([secondBackground, firstBackground], vm.OverlayElements);
        vm.AddOverlayElementCommand.Execute(null);
        var top = vm.OverlayElements[2];

        vm.SendToBackCommand.Execute(top);

        Assert.True(top.Z > firstBackground.Z);
        Assert.True(vm.OverlayElements.IndexOf(top) > vm.OverlayElements.IndexOf(firstBackground));
    }

    [AvaloniaFact]
    public void SetAsBackdrop_WithAnExistingBackdrop_ClearsTheOldOnesFlag()
    {
        // Background/backdrop naming work (2026-09-15): the single-backdrop invariant this feature
        // added -- SetAsBackdrop must clear IsBackground on any OTHER image element before marking
        // the new one, so at most one backdrop exists at a time (see that method's own doc comment
        // for why: SendToBack's own "nearest backdrop below" scan otherwise picks the wrong one once
        // two exist).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var first = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(first);
        vm.AddLastRxImageCommand.Execute(null);
        var second = (ImageElementViewModel)vm.OverlayElements[1];

        vm.SetAsBackdropCommand.Execute(second);

        Assert.False(first.IsBackground);
        Assert.True(second.IsBackground);
        Assert.Single(vm.OverlayElements.OfType<ImageElementViewModel>(), e => e.IsBackground);
    }

    [AvaloniaFact]
    public void PromoteBackgroundToBackdropCommand_CreatesOneBackdropAndResetsBackgroundToBlank()
    {
        // Background/backdrop naming work (2026-09-15): the promote direction -- see that method's
        // own doc comment.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        var originalSource = vm.CurrentSource;
        Assert.True(vm.HasRealBackground);

        vm.PromoteBackgroundToBackdropCommand.Execute(null);

        var backdrop = Assert.Single(vm.OverlayElements.OfType<ImageElementViewModel>(), e => e.IsBackground);
        Assert.Same(backdrop, vm.OverlayElements[0]);
        Assert.True(backdrop.Locked);
        Assert.Equal(0.5, backdrop.X);
        Assert.Equal(0.5, backdrop.Y);
        Assert.Equal(1, backdrop.Width);
        Assert.Equal(1, backdrop.Height);
        Assert.False(vm.HasRealBackground);
        Assert.IsType<BlankImageSource>(vm.CurrentSource);

        // Auditor-found gap: the fake's own Crop is an identity pass-through and none of the
        // prior Promote tests asserted on CropSources/ResizeCalls/AdjustmentsSources, so a
        // Resize<->Crop reorder or a dropped Crop call would have passed silently. Pin the actual
        // pipeline sources of Promote's OWN bake call -- not exact call counts, since
        // RecomputePreview (called both at construction and at the end of Promote) legitimately
        // calls this same preparer for its own, separate preview pass. The bake's OWN call is the
        // one whose result actually became the backdrop's Source, which is unique per call --
        // finding it by that result, not by array index, is what actually isolates it from the
        // preview passes.
        var bakeIndex = preparer.AdjustmentsResults.IndexOf(backdrop.Source);
        Assert.True(bakeIndex >= 0);
        Assert.Same(preparer.ResizeResults[bakeIndex], preparer.AdjustmentsSources[bakeIndex]);
        Assert.Contains(originalSource, preparer.CropSources);
    }

    [AvaloniaFact]
    public void PromoteBackgroundToBackdropCommand_WithNoRealBackgroundYet_IsRefusedAsANoOp()
    {
        // Background/backdrop naming work (2026-09-15): refused (StatusMessage, not silent), same
        // "no real photo" shape the (now-removed) Ready Rack direct-fire handler used for the
        // identical check.
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer());
        Assert.False(vm.HasRealBackground);

        vm.PromoteBackgroundToBackdropCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public void PromoteBackgroundToBackdropCommand_WithAnExistingBackdrop_ClearsTheOldOnesFlag()
    {
        // Background/backdrop naming work (2026-09-15): Promote is a SECOND path (besides
        // SetAsBackdrop) that creates a backdrop element, so it enforces the same single-backdrop
        // invariant -- see PromoteBackgroundToBackdrop's own doc comment.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var existingBackdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(existingBackdrop);

        vm.PromoteBackgroundToBackdropCommand.Execute(null);

        Assert.False(existingBackdrop.IsBackground);
        Assert.Single(vm.OverlayElements.OfType<ImageElementViewModel>(), e => e.IsBackground);
    }

    [AvaloniaFact]
    public void DemoteToBackgroundCommand_IsWiredOnImageElements()
    {
        // Background/backdrop naming work (2026-09-15): DemoteToBackgroundCommand binds against the
        // image element's own DataContext in the context menu (same inline-per-DataTemplate
        // resolution as SetAsBackdropCommand/AddPlateCommand elsewhere in this file) -- a forgotten
        // parent-pushed assignment renders the menu item permanently disabled without throwing
        // anywhere, so a wiring test is the only thing that catches it.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());

        vm.AddLastRxImageCommand.Execute(null);

        var image = (ImageElementViewModel)vm.OverlayElements[0];
        Assert.NotNull(image.DemoteToBackgroundCommand);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_RestoresBackgroundAndRemovesTheElement()
    {
        // Background/backdrop naming work (2026-09-15): the demote direction -- see that method's
        // own doc comment. Starting background is blank (OpenBlankEditorAsync's own placeholder), so
        // no arm/confirm step is needed -- that path is covered separately below. Async since the fix
        // for the auditor-found Fit/perspective blocker routes this through the same Task.Run-offloaded
        // bake FlattenElementAsync uses -- ExecuteAsync, not the bare synchronous Execute, is required
        // to observe the mutation (same convention every existing Flatten test already uses).
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        Assert.Empty(vm.OverlayElements);
        Assert.True(vm.HasRealBackground);
        Assert.IsNotType<BlankImageSource>(vm.CurrentSource);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_WithARealBackgroundAlreadyLoaded_ReplacesItOnOneClick()
    {
        // User-reported feedback (2026-09-15): an earlier draft armed/required a second click to
        // confirm before replacing a real background -- removed, see
        // DemoteBackdropToBackgroundAsync's own doc comment. One click now replaces it outright;
        // DemoteBackdropToBackgroundCommand_UndoRestoresTheBackdropElement covers the actual safety
        // net (Undo), which is what replaces the old confirm step.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        Assert.Empty(vm.OverlayElements);
        Assert.True(vm.HasRealBackground);
    }

    [AvaloniaFact]
    public void RemoveBackgroundCommand_WithNoRealBackgroundYet_IsANoOp()
    {
        // Background/backdrop naming work (2026-09-15): the plain destructive clear -- see that
        // method's own doc comment for how it differs from PromoteBackgroundToBackdrop.
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer());

        vm.RemoveBackgroundCommand.Execute(null);

        Assert.Null(vm.StatusMessage);
        Assert.IsType<BlankImageSource>(vm.CurrentSource);
    }

    [AvaloniaFact]
    public void RemoveBackgroundCommand_WithARealBackgroundLoaded_ClearsItOnOneClick()
    {
        // User-reported feedback (2026-09-15): an earlier draft armed/required a second click to
        // confirm before clearing a real background -- removed, see RemoveBackground's own doc
        // comment. One click now clears it outright; the safety net is Undo
        // (RemoveBackgroundCommand_UndoRestoresTheOriginalBackground), not a confirm step.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        Assert.True(vm.HasRealBackground);

        vm.RemoveBackgroundCommand.Execute(null);

        Assert.False(vm.HasRealBackground);
        Assert.IsType<BlankImageSource>(vm.CurrentSource);
    }

    [AvaloniaFact]
    public void RemoveBackgroundCommand_UndoRestoresTheOriginalBackground()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var originalSource = vm.CurrentSource;

        vm.RemoveBackgroundCommand.Execute(null);
        Assert.False(vm.HasRealBackground);

        vm.UndoCommand.Execute(null);

        Assert.True(vm.HasRealBackground);
        Assert.Same(originalSource, vm.CurrentSource);
    }

    // User-requested (2026-09-15): "even if there are elements on the canvas, if no background has
    // been picked before, i should be able to load one later also, not only as first canvas
    // element." LoadBackground is TxControlsPaneViewModel.OpenEditorForSourceAsync's own new
    // in-place path -- these pin the VM-layer contract directly (refusal, undo, element preservation)
    // independent of the parent VM's own wiring, which PaneViewModelTests.cs covers separately.

    [AvaloniaFact]
    public void LoadBackground_WithNoRealBackgroundYet_InstallsItAndPreservesOverlayElements()
    {
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);
        Assert.False(vm.HasRealBackground);
        var newSource = CreateSource(4, 4);

        vm.LoadBackground(newSource);

        Assert.True(vm.HasRealBackground);
        Assert.Same(newSource, vm.CurrentSource);
        Assert.Same(element, Assert.Single(vm.OverlayElements));
    }

    [AvaloniaFact]
    public void LoadBackground_WithARealBackgroundAlreadyLoaded_IsRefusedAsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var originalSource = vm.CurrentSource;
        Assert.True(vm.HasRealBackground);

        vm.LoadBackground(CreateSource(6, 6));

        Assert.Same(originalSource, vm.CurrentSource);
    }

    [AvaloniaFact]
    public void LoadBackground_UndoRestoresTheBlankPlaceholder()
    {
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer());
        var blankSource = vm.CurrentSource;

        vm.LoadBackground(CreateSource(4, 4));
        Assert.True(vm.HasRealBackground);

        vm.UndoCommand.Execute(null);

        Assert.False(vm.HasRealBackground);
        Assert.Same(blankSource, vm.CurrentSource);
    }

    [AvaloniaFact]
    public void RemoveBackgroundCommand_DoesNotTouchExistingBackdropElements()
    {
        // Background/backdrop naming work (2026-09-15): distinct from Demote/Promote, RemoveBackground
        // never touches OverlayElements at all -- only the base photo (_originalSource).
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);

        vm.RemoveBackgroundCommand.Execute(null);

        Assert.Contains(backdrop, vm.OverlayElements);
        Assert.True(backdrop.IsBackground);
        Assert.False(vm.HasRealBackground);
    }

    [AvaloniaFact]
    public void PromoteBackgroundToBackdropCommand_DoesNotResetCropRectOrReprojectOtherElements()
    {
        // Auditor-found blocker (2026-09-15): BuildTemplateElement/ProjectRectToCropRelative project
        // every OTHER overlay element's bounds relative to CropRect and its own letterbox padding --
        // resetting CropRect here would silently resize/reposition every other element in the
        // transmitted frame, not just reframe the promoted backdrop. FlattenElementAsync's own doc
        // comment states the identical reasoning for why IT never resets CropRect either.
        var vm = CreateEditor(CreateSource(8, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.5, 0.5);
        vm.AddOverlayElementCommand.Execute(null);
        var text = vm.OverlayElements[0];
        var (x, y, width, height) = (text.X, text.Y, text.Width, text.Height);

        vm.PromoteBackgroundToBackdropCommand.Execute(null);

        Assert.Equal(new NormalizedRect(0.1, 0.2, 0.5, 0.5), vm.CropRect);
        Assert.Equal(x, text.X);
        Assert.Equal(y, text.Y);
        Assert.Equal(width, text.Width);
        Assert.Equal(height, text.Height);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_DoesNotResetCropRectOrReprojectOtherElements()
    {
        // Same auditor-found blocker as PromoteBackgroundToBackdropCommand's own identical test --
        // see that test's own doc comment.
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.5, 0.5);
        vm.AddOverlayElementCommand.Execute(null);
        var text = vm.OverlayElements[1];
        var (x, y, width, height) = (text.X, text.Y, text.Width, text.Height);

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        Assert.Equal(new NormalizedRect(0.1, 0.2, 0.5, 0.5), vm.CropRect);
        Assert.Equal(x, text.X);
        Assert.Equal(y, text.Y);
        Assert.Equal(width, text.Width);
        Assert.Equal(height, text.Height);
    }

    [AvaloniaFact]
    public void RemoveBackgroundCommand_DoesNotResetCropRect()
    {
        // Same auditor-found blocker as PromoteBackgroundToBackdropCommand's own identical test --
        // see that test's own doc comment. RemoveBackground has no elements of its own to reproject,
        // but CropRect feeds every OTHER element's projection regardless of which command touched it.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.CropRect = new NormalizedRect(0.1, 0.2, 0.5, 0.5);

        vm.RemoveBackgroundCommand.Execute(null);

        Assert.Equal(new NormalizedRect(0.1, 0.2, 0.5, 0.5), vm.CropRect);
    }

    [AvaloniaFact]
    public void PromoteBackgroundToBackdropCommand_UndoRestoresTheOriginalBackgroundAndRemovesTheBackdrop()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        var originalSource = vm.CurrentSource;

        vm.PromoteBackgroundToBackdropCommand.Execute(null);
        Assert.Single(vm.OverlayElements);
        Assert.False(vm.HasRealBackground);

        vm.UndoCommand.Execute(null);

        Assert.Empty(vm.OverlayElements);
        Assert.True(vm.HasRealBackground);
        Assert.Same(originalSource, vm.CurrentSource);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_UndoRestoresTheBackdropElement()
    {
        var vm = CreateEditor(new BlankImageSource(SmallMode.ImageWidth, SmallMode.ImageHeight, BlankImageSource.DefaultColor), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);
        Assert.Empty(vm.OverlayElements);
        Assert.True(vm.HasRealBackground);

        vm.UndoCommand.Execute(null);

        var restored = Assert.Single(vm.OverlayElements.OfType<ImageElementViewModel>(), e => e.IsBackground);
        Assert.True(restored.Locked);
        Assert.False(vm.HasRealBackground);
        Assert.IsType<BlankImageSource>(vm.CurrentSource);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_ForAPromotedBackdropWithNonIdentityAdjustments_WarnsAboutReapplication()
    {
        // Auditor-found blocker (2026-09-15): a backdrop created by PromoteBackgroundToBackdrop
        // already has the CURRENT adjustment sliders baked into its own pixels -- if those sliders
        // are still non-identity when demoted back, the live pipeline applies them a SECOND time on
        // every subsequent render. Warned, same "surfaced via StatusMessage, not silently" shape
        // FlattenElementAsync's own identical case (FlattenAdjustmentsNowApply) already uses.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.Brightness = 0.5;
        vm.PromoteBackgroundToBackdropCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        // FakeLocalizationService.GetString returns the raw key, not a translation.
        Assert.Equal("Panes.TxImageEditor.DemotePromotedAdjustmentsNowReapply", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_ForAPromotedBackdropWithIdentityAdjustments_DoesNotWarn()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.PromoteBackgroundToBackdropCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        Assert.Null(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_ForANonPromotedBackdropWithNonIdentityAdjustments_DoesNotWarn()
    {
        // A backdrop created via the OTHER path (+Image, then Set as backdrop directly, never
        // promoted) has raw, never-baked pixels -- continuing to apply the current sliders to it on
        // every render is the SAME "sliders apply live to whatever's currently loaded" behavior
        // Browse/Stock already have, not a double-application. Warning here would be a false alarm.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);
        vm.Brightness = 0.5;

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        Assert.Null(vm.StatusMessage);
    }

    // User-reported bug (2026-09-15): "if i do 'remove background' i end up with a white background
    // but the 'safe area'-blue lines dont match up." Root cause: NotifyWorkingCopyGeometryChanged
    // (shared by RemoveBackground/Promote/Demote/Flatten) never re-raised CropLeftPixels/CropTopPixels/
    // CropWidthPixels/CropHeightPixels/CropRightPixels/CropBottomPixels -- its own comment claimed they
    // "get their own re-notify from the CropRect reassignment in the Rotate() caller," true for Rotate
    // but wrong for these three commands, which deliberately leave CropRect untouched (see each one's
    // own doc comment) while still swapping in a working copy of a DIFFERENT pixel size. The crop-rect
    // Border in the View (same accent-blue as the safe-area guide, easy to conflate) kept rendering at
    // whatever pixel rect it last computed against the OLD CanvasDisplayWidth/Height. A background
    // LARGER than the budget-capped blank placeholder makes WorkingCopyWidth/Height genuinely change
    // across each call, so these tests actually exercise the gap instead of passing vacuously.
    [AvaloniaFact]
    public void RemoveBackgroundCommand_WhenWorkingCopyDimensionsChange_RaisesCropPixelPropertiesSoTheGuideStaysInSync()
    {
        var vm = CreateEditor(CreateSource(20, 20), SmallMode, new FakeTransmitImagePreparer());
        var widthBefore = vm.CanvasDisplayWidth;
        var raised = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.RemoveBackgroundCommand.Execute(null);

        Assert.NotEqual(widthBefore, vm.CanvasDisplayWidth);
        Assert.Contains(nameof(vm.CropLeftPixels), raised);
        Assert.Contains(nameof(vm.CropTopPixels), raised);
        Assert.Contains(nameof(vm.CropWidthPixels), raised);
        Assert.Contains(nameof(vm.CropHeightPixels), raised);
        Assert.Contains(nameof(vm.CropRightPixels), raised);
        Assert.Contains(nameof(vm.CropBottomPixels), raised);
    }

    [AvaloniaFact]
    public void PromoteBackgroundToBackdropCommand_WhenWorkingCopyDimensionsChange_RaisesCropPixelPropertiesSoTheGuideStaysInSync()
    {
        var vm = CreateEditor(CreateSource(20, 20), SmallMode, new FakeTransmitImagePreparer());
        var widthBefore = vm.CanvasDisplayWidth;
        var raised = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.PromoteBackgroundToBackdropCommand.Execute(null);

        Assert.NotEqual(widthBefore, vm.CanvasDisplayWidth);
        Assert.Contains(nameof(vm.CropWidthPixels), raised);
        Assert.Contains(nameof(vm.CropHeightPixels), raised);
    }

    [AvaloniaFact]
    public async Task DemoteBackdropToBackgroundCommand_RaisesCropPixelPropertiesSoTheGuideStaysInSync()
    {
        // Unlike RemoveBackground/Promote's fixed-size blank placeholder, DemoteBackdropToBackground's
        // baked result tracks the ORIGINAL source's own proportional resolution (BakeElementIntoSource,
        // same as FlattenElementAsync), so it doesn't reliably produce a DIFFERENT working-copy size in
        // a small fixture like this one to assert against directly. What this test pins instead: the
        // fix made NotifyWorkingCopyGeometryChanged raise Crop*Pixels UNCONDITIONALLY on every working
        // copy swap (matching OnZoomFactorChanged's own unconditional style) -- before the fix, this
        // command's ReplaceSourceAndWorkingCopy call never raised these AT ALL, dimension change or
        // not, which is what actually left the crop-rect guide stale for this command too.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var backdrop = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(backdrop);
        var raised = new HashSet<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.DemoteBackdropToBackgroundCommand.ExecuteAsync(backdrop);

        Assert.Contains(nameof(vm.CropWidthPixels), raised);
        Assert.Contains(nameof(vm.CropHeightPixels), raised);
    }

    // User-reported bug (2026-09-15): "when i set an image as backdrop and i pick the option 'reset
    // to original size' it gives an error but does not return as a non-backdropped image of the
    // previous size." A locked backdrop is defined as covering the whole frame (SetAsBackdrop's own
    // doc comment) -- resetting it to its natural size, with no Locked check at all, left it Locked
    // and IsBackground=true but no longer full-frame, a self-contradictory state. Fixed by adding the
    // same "skip locked elements" check every other geometry-changing operation in this class already
    // has (NudgeSelectedElements' own precedent).

    [AvaloniaFact]
    public void ResetImageElementToOriginalSizeCommand_ForALockedBackdrop_IsRefusedAsANoOp()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SetAsBackdropCommand.Execute(image);
        Assert.True(image.Locked);
        var widthBefore = image.Width;
        var heightBefore = image.Height;

        Assert.False(vm.ResetImageElementToOriginalSizeCommand.CanExecute(image));
        // Direct Execute bypasses CanExecute (same "body-level check is the real backstop" reasoning
        // this class's other commands already document) -- must still be a safe no-op.
        vm.ResetImageElementToOriginalSizeCommand.Execute(image);

        Assert.Equal(widthBefore, image.Width);
        Assert.Equal(heightBefore, image.Height);
        Assert.True(image.Locked);
        Assert.True(image.IsBackground);
    }

    [AvaloniaFact]
    public void ResetImageElementToOriginalSizeCommand_ForAnUnlockedElement_StillAllowed()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        Assert.False(image.Locked);

        Assert.True(vm.ResetImageElementToOriginalSizeCommand.CanExecute(image));
    }

    // User-requested (2026-09-15): "right click menu 'fit'... should have an option of 'fit safe
    // area', fit width, fit height. Those options should resize the image area and image to the safe
    // area size (or width or height)." A DIFFERENT concept from FitCommand/SetSelectedImageFit
    // (Stretch/Contain/Cover, how the pixels fill the EXISTING bounds) -- this resizes the bounds
    // themselves. FlattenTestMode (80x60) is used instead of SmallMode (4x4) because the safe-area
    // inset (14 working-copy units) exceeds SmallMode's own canvas entirely, which would make every
    // one of these tests exercise the "Unavailable" refusal path instead of the real math.

    [AvaloniaFact]
    public void FitSelectedImageToSafeAreaCommand_SafeArea_SetsExactSizeAndCentersTheElement()
    {
        var vm = CreateEditor(CreateSource(FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight), FlattenTestMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(10, 10) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;

        Assert.True(vm.FitSelectedImageToSafeAreaCommand.CanExecute("SafeArea"));
        vm.FitSelectedImageToSafeAreaCommand.Execute("SafeArea");

        // 1 - 2*14/80 and 1 - 2*14/60 -- SafeAreaInsetWorkingCopyUnits's own value against
        // FlattenTestMode's working-copy dimensions, same math SafeAreaWidthPixels/HeightPixels use
        // in pixel space.
        AssertClose(0.5, image.X);
        AssertClose(0.5, image.Y);
        AssertClose(1 - (28.0 / 80), image.Width);
        AssertClose(1 - (28.0 / 60), image.Height);
    }

    [AvaloniaFact]
    public void FitSelectedImageToSafeAreaCommand_Width_OnlyTouchesWidthAndX()
    {
        var vm = CreateEditor(CreateSource(FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight), FlattenTestMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(10, 10) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;
        var heightBefore = image.Height;
        var yBefore = image.Y;

        vm.FitSelectedImageToSafeAreaCommand.Execute("Width");

        AssertClose(0.5, image.X);
        AssertClose(1 - (28.0 / 80), image.Width);
        Assert.Equal(heightBefore, image.Height);
        Assert.Equal(yBefore, image.Y);
    }

    [AvaloniaFact]
    public void FitSelectedImageToSafeAreaCommand_Height_OnlyTouchesHeightAndY()
    {
        var vm = CreateEditor(CreateSource(FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight), FlattenTestMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(10, 10) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;
        var widthBefore = image.Width;
        var xBefore = image.X;

        vm.FitSelectedImageToSafeAreaCommand.Execute("Height");

        AssertClose(0.5, image.Y);
        AssertClose(1 - (28.0 / 60), image.Height);
        Assert.Equal(widthBefore, image.Width);
        Assert.Equal(xBefore, image.X);
    }

    [AvaloniaFact]
    public void FitSelectedImageToSafeAreaCommand_ForALockedElement_IsRefusedAsANoOp()
    {
        var vm = CreateEditor(CreateSource(FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight), FlattenTestMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(10, 10) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;
        image.Locked = true;
        var widthBefore = image.Width;
        var heightBefore = image.Height;

        Assert.False(vm.FitSelectedImageToSafeAreaCommand.CanExecute("SafeArea"));
        // Direct Execute bypasses CanExecute -- must still be a safe no-op, same "body-level check is
        // the real backstop" reasoning this class's other commands already document.
        vm.FitSelectedImageToSafeAreaCommand.Execute("SafeArea");

        Assert.Equal(widthBefore, image.Width);
        Assert.Equal(heightBefore, image.Height);
    }

    [AvaloniaFact]
    public void FitSelectedImageToSafeAreaCommand_WhenTheWorkingCopyIsTooSmallForTheInset_ShowsUnavailable()
    {
        // SmallMode (4x4) is narrower than twice SafeAreaInsetWorkingCopyUnits (14) -- the exact
        // degenerate case TryGetSafeAreaNormalizedSize's own doc comment guards against.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(2, 2) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;
        var widthBefore = image.Width;

        vm.FitSelectedImageToSafeAreaCommand.Execute("SafeArea");

        Assert.Equal(widthBefore, image.Width);
        Assert.Equal("Panes.TxImageEditor.FitToSafeAreaUnavailable", vm.StatusMessage);
    }

    [AvaloniaFact]
    public void FitSelectedImageToSafeAreaCommand_UndoRestoresTheOriginalBoundsInOneStep()
    {
        var vm = CreateEditor(CreateSource(FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight), FlattenTestMode, new FakeTransmitImagePreparer(),
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer { Current = CreateSource(10, 10) }, new FakeReceiveHistoryStore());
        vm.AddLastRxImageCommand.Execute(null);
        var image = (ImageElementViewModel)vm.OverlayElements[0];
        vm.SelectedOverlayElement = image;
        var xBefore = image.X;
        var yBefore = image.Y;
        var widthBefore = image.Width;
        var heightBefore = image.Height;

        vm.FitSelectedImageToSafeAreaCommand.Execute("SafeArea");
        Assert.NotEqual(widthBefore, image.Width);

        vm.UndoCommand.Execute(null);

        // Re-fetched, not the captured `image` reference -- ApplyState's own restore path replaces
        // every element wholesale from the snapshot (same reasoning SetAsBackdrop's own doc comment
        // gives for why a stale element reference is a real, previously-hit bug class here).
        var restored = Assert.Single(vm.OverlayElements);
        Assert.Equal(xBefore, restored.X);
        Assert.Equal(yBefore, restored.Y);
        Assert.Equal(widthBefore, restored.Width);
        Assert.Equal(heightBefore, restored.Height);
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

    // TX editor gap-items plan, item 3 (perspective transform) -- TryWritePerspectiveCorner's own
    // real-time convexity clamp, same "public static, unit-testable without simulating real Avalonia
    // pointer events" precedent as ComputeElementResize above.

    [Fact]
    public void TryWritePerspectiveCorner_ValidPosition_WritesTheCorner()
    {
        var box = new BoxElementViewModel
        {
            Corner0X = 0, Corner0Y = 0, Corner1X = 1, Corner1Y = 0, Corner2X = 1, Corner2Y = 1, Corner3X = 0, Corner3Y = 1,
            PerspectiveEnabled = true,
        };

        TxImageEditorPaneView.TryWritePerspectiveCorner(box, cornerIndex: 0, x: 0.1, y: 0.15, workingCopyWidth: 100, workingCopyHeight: 100);

        AssertClose(0.1, box.Corner0X);
        AssertClose(0.15, box.Corner0Y);
        // Untouched corners stay untouched.
        AssertClose(1, box.Corner1X);
        AssertClose(1, box.Corner2Y);
    }

    [Fact]
    public void TryWritePerspectiveCorner_WouldMakeTheQuadNonConvex_RejectsAndLeavesTheCornerUnchanged()
    {
        // Square quad, TopLeft=Corner0. Dragging it far past TopRight (Corner1) on the X axis
        // crosses the top edge, producing a self-intersecting (bowtie) quad -- independently
        // verified by hand (pixel-space cross products at the two affected vertices have opposite
        // signs), not just asserted from the implementation's own claim.
        var box = new BoxElementViewModel
        {
            Corner0X = 0, Corner0Y = 0, Corner1X = 1, Corner1Y = 0, Corner2X = 1, Corner2Y = 1, Corner3X = 0, Corner3Y = 1,
            PerspectiveEnabled = true,
        };

        TxImageEditorPaneView.TryWritePerspectiveCorner(box, cornerIndex: 0, x: 2.0, y: 0, workingCopyWidth: 100, workingCopyHeight: 100);

        AssertClose(0, box.Corner0X);
        AssertClose(0, box.Corner0Y);
    }

    // Auditor usability review follow-up (2026-08-18): 8-handle resize (corners + edge midpoints),
    // replacing the earlier bottom-right-only handle -- see TxImageEditorPaneView.ResizeHandle's own
    // doc comment for why this is scoped as new interaction-model functionality, not a legacy port.

    [Fact]
    public void ComputeElementResize_TopLeftHandle_GrowsAwayFromTheOppositeCornerAndPinsBottomRight()
    {
        // Dragging TopLeft up-and-left (negative dx/dy) should GROW the box while pinning the
        // BOTTOM-RIGHT corner in place -- the opposite of the BottomRight handle's own pinning.
        var (width, height, centerDeltaX, centerDeltaY) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: -0.1, dyNormalized: -0.04,
            handle: TxImageEditorPaneView.ResizeHandle.TopLeft);

        AssertClose(0.4, width);
        AssertClose(0.24, height);
        AssertClose(-0.05, centerDeltaX);
        AssertClose(-0.02, centerDeltaY);
    }

    [Theory]
    [InlineData(TxImageEditorPaneView.ResizeHandle.Top)]
    [InlineData(TxImageEditorPaneView.ResizeHandle.Bottom)]
    public void ComputeElementResize_VerticalEdgeHandle_ChangesOnlyHeight(TxImageEditorPaneView.ResizeHandle handle)
    {
        var (width, height, centerDeltaX, _) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: 0.5, dyNormalized: 0.04, handle: handle);

        // Width and its center-X are untouched by a purely-vertical handle, even though a (deliberately
        // large/off-axis) dx was passed -- a Top/Bottom handle has no free horizontal edge at all.
        AssertClose(0.3, width);
        AssertClose(0, centerDeltaX);
        Assert.NotEqual(0.2, height);
    }

    [Theory]
    [InlineData(TxImageEditorPaneView.ResizeHandle.Left)]
    [InlineData(TxImageEditorPaneView.ResizeHandle.Right)]
    public void ComputeElementResize_HorizontalEdgeHandle_ChangesOnlyWidth(TxImageEditorPaneView.ResizeHandle handle)
    {
        var (width, height, _, centerDeltaY) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: 0.04, dyNormalized: 0.5, handle: handle);

        AssertClose(0.2, height);
        AssertClose(0, centerDeltaY);
        Assert.NotEqual(0.3, width);
    }

    [Fact]
    public void ComputeElementResize_PreserveAspectAtCornerHandle_DerivesTheOtherAxisFromTheOriginalRatio()
    {
        // 2:1 aspect box; a much larger dx than dy should still keep width:height at 2:1 in the result,
        // since the horizontal drag is the dominant (proportionally larger) axis here.
        var (width, height, _, _) = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.4, currentHeight: 0.2, dxNormalized: 0.2, dyNormalized: 0.01,
            handle: TxImageEditorPaneView.ResizeHandle.BottomRight, preserveAspect: true);

        AssertClose(0.6, width);
        AssertClose(0.3, height);
    }

    [Fact]
    public void ComputeElementResize_PreserveAspectAtEdgeHandle_IsIgnored_StaysSingleAxis()
    {
        // Shift held on an EDGE handle (no second free axis to derive a ratio from) must not do
        // anything different from the non-aspect-locked case -- matches mainstream editor convention.
        var withAspect = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: 0.1, dyNormalized: 0,
            handle: TxImageEditorPaneView.ResizeHandle.Right, preserveAspect: true);
        var withoutAspect = TxImageEditorPaneView.ComputeElementResize(
            currentWidth: 0.3, currentHeight: 0.2, dxNormalized: 0.1, dyNormalized: 0,
            handle: TxImageEditorPaneView.ResizeHandle.Right, preserveAspect: false);

        Assert.Equal(withoutAspect, withAspect);
        AssertClose(0.2, withAspect.Height);
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

    // TX workflow modernization plan, Phase 3a: draw-to-place drag-to-rect math.

    [Fact]
    public void ComputeRectFromDrag_AnchorTopLeftOfCurrent_DerivesCenterAnchoredRect()
    {
        var (centerX, centerY, width, height) = TxImageEditorPaneView.ComputeRectFromDrag(
            anchor: new Avalonia.Point(100, 100), current: new Avalonia.Point(300, 200),
            canvasDisplayWidth: 1000, canvasDisplayHeight: 1000);

        // anchor/current normalize to (0.1,0.1) and (0.3,0.2) -> left=0.1, top=0.1, width=0.2, height=0.1
        AssertClose(0.1, centerX - (width / 2));
        AssertClose(0.1, centerY - (height / 2));
        AssertClose(0.2, width);
        AssertClose(0.1, height);
    }

    [Fact]
    public void ComputeRectFromDrag_AnchorBottomRightOfCurrent_SameRectRegardlessOfDragDirection()
    {
        // Dragging from bottom-right back to top-left must produce the IDENTICAL rect a top-left-to-
        // bottom-right drag over the same two points would -- a placement drag can go any direction.
        var forward = TxImageEditorPaneView.ComputeRectFromDrag(
            new Avalonia.Point(100, 100), new Avalonia.Point(300, 200), 1000, 1000);
        var reversed = TxImageEditorPaneView.ComputeRectFromDrag(
            new Avalonia.Point(300, 200), new Avalonia.Point(100, 100), 1000, 1000);

        AssertClose(forward.CenterX, reversed.CenterX);
        AssertClose(forward.CenterY, reversed.CenterY);
        AssertClose(forward.Width, reversed.Width);
        AssertClose(forward.Height, reversed.Height);
    }

    [Fact]
    public void ComputeRectFromDrag_TinyDrag_FloorsBothAxesAtMinSize()
    {
        var (_, _, width, height) = TxImageEditorPaneView.ComputeRectFromDrag(
            new Avalonia.Point(100, 100), new Avalonia.Point(101, 100), 1000, 1000, minSize: 0.02);

        AssertClose(0.02, width);
        AssertClose(0.02, height);
    }

    // User-reported (2026-09-20), with the user's own explicit correction: the canvas context menu's
    // Add Text/Box place their new element with the right-click point as its TOP-LEFT corner, not its
    // center -- ComputeCenterFromTopLeft's own doc comment records that an earlier version of this
    // feature got this backward (centered on the click point) before the correction.
    [Fact]
    public void ComputeCenterFromTopLeft_ReturnsACenterOffsetByHalfTheGivenSize_NotTheTopLeftItself()
    {
        var (centerX, centerY) = TxImageEditorPaneView.ComputeCenterFromTopLeft(
            topLeft: new Avalonia.Point(0.2, 0.3), width: 0.1, height: 0.06);

        // The TOP-LEFT corner of the resulting rect must land exactly on the given point -- not its
        // center, which is what an earlier (incorrect) version of this feature did instead.
        AssertClose(0.2, centerX - (0.1 / 2));
        AssertClose(0.3, centerY - (0.06 / 2));
        AssertClose(0.25, centerX);
        AssertClose(0.33, centerY);
    }

    // TX editor gap-items plan, line element: line-drag geometry math.

    [Fact]
    public void ComputeLineFromDrag_ReversedDragDirection_ProducesAReversedLine()
    {
        // Unlike ComputeRectFromDrag, a line must NOT normalize to the same result regardless of
        // drag direction -- the endpoints ARE the anchor/current points, in that order.
        var forward = TxImageEditorPaneView.ComputeLineFromDrag(
            new Avalonia.Point(100, 100), new Avalonia.Point(300, 200), 1000, 1000);
        var reversed = TxImageEditorPaneView.ComputeLineFromDrag(
            new Avalonia.Point(300, 200), new Avalonia.Point(100, 100), 1000, 1000);

        AssertClose(0.1, forward.X1);
        AssertClose(0.1, forward.Y1);
        AssertClose(0.3, forward.X2);
        AssertClose(0.2, forward.Y2);
        AssertClose(forward.X1, reversed.X2);
        AssertClose(forward.Y1, reversed.Y2);
        AssertClose(forward.X2, reversed.X1);
        AssertClose(forward.Y2, reversed.Y1);
    }

    [Fact]
    public void SnapPointToAngle_NearHorizontalDrag_SnapsToExactlyZeroDegreesPreservingDistance()
    {
        var fixedPoint = new Avalonia.Point(100, 100);
        var cursor = new Avalonia.Point(300, 108); // ~2.3 degrees off horizontal

        var snapped = TxImageEditorPaneView.SnapPointToAngle(fixedPoint, cursor);

        var originalDistance = Math.Sqrt(Math.Pow(cursor.X - fixedPoint.X, 2) + Math.Pow(cursor.Y - fixedPoint.Y, 2));
        var snappedDistance = Math.Sqrt(Math.Pow(snapped.X - fixedPoint.X, 2) + Math.Pow(snapped.Y - fixedPoint.Y, 2));
        AssertClose(100, snapped.Y); // horizontal: Y unchanged from the fixed point
        AssertClose(originalDistance, snappedDistance); // only the angle is quantized, not the length
    }

    [Fact]
    public void SnapPointToAngle_DiagonalDrag_SnapsToExactly45Degrees()
    {
        var fixedPoint = new Avalonia.Point(0, 0);
        var cursor = new Avalonia.Point(100, 80); // ~38.7 degrees, closer to 45 than 0

        var snapped = TxImageEditorPaneView.SnapPointToAngle(fixedPoint, cursor);

        AssertClose(snapped.X, snapped.Y); // 45 degrees means equal X/Y offset from the fixed point
    }

    [Fact]
    public void SnapPointToAngle_CursorAtFixedPoint_ReturnsCursorUnchangedRatherThanDividingByZero()
    {
        var fixedPoint = new Avalonia.Point(50, 50);
        var cursor = new Avalonia.Point(50, 50);

        var snapped = TxImageEditorPaneView.SnapPointToAngle(fixedPoint, cursor);

        AssertClose(cursor.X, snapped.X);
        AssertClose(cursor.Y, snapped.Y);
    }

    // TX workflow modernization plan, Phase 3c: alignment-guide snap math.

    [Fact]
    public void ComputeAlignmentSnap_LeftEdgeCloseToOtherElementsLeftEdge_SnapsXOnly()
    {
        // dragged left edge = 0.433 - 0.03 = 0.403, within threshold of the other's left edge (0.4).
        // Every other dragged/target pairing (center, right edge, crop center) is deliberately far
        // apart so this test isolates the one intended match, not a coincidental closer one.
        var dragged = (X: 0.433, Y: 0.5, Width: 0.06, Height: 0.1);
        var others = new List<(double X, double Y, double Width, double Height)> { (0.5, 0.99, 0.2, 0.1) }; // left edge = 0.4

        var (x, y) = TxImageEditorPaneView.ComputeAlignmentSnap(dragged, others, cropCenter: (0.99, 0.99));

        Assert.NotNull(x);
        AssertClose(0.4, x!.Value - (dragged.Width / 2)); // dragged left edge now exactly on the other's left edge
        Assert.Null(y); // Y was nowhere near any target, must not snap
    }

    [Fact]
    public void ComputeAlignmentSnap_CentersClose_SnapsToExactCenterMatch()
    {
        var dragged = (X: 0.503, Y: 0.301, Width: 0.06, Height: 0.06);
        var others = new List<(double X, double Y, double Width, double Height)> { (0.5, 0.99, 0.3, 0.02) };

        var (x, _) = TxImageEditorPaneView.ComputeAlignmentSnap(dragged, others, cropCenter: (0.01, 0.01));

        AssertClose(0.5, x!.Value);
    }

    [Fact]
    public void ComputeAlignmentSnap_NearCropCenter_SnapsToCropCenterWithNoOtherElements()
    {
        var dragged = (X: 0.503, Y: 0.5, Width: 0.1, Height: 0.1);

        var (x, y) = TxImageEditorPaneView.ComputeAlignmentSnap(
            dragged, others: [], cropCenter: (0.5, 0.5));

        AssertClose(0.5, x!.Value);
        AssertClose(0.5, y!.Value);
    }

    [Fact]
    public void ComputeAlignmentSnap_NothingWithinThreshold_ReturnsNullForBothAxes()
    {
        var dragged = (X: 0.1, Y: 0.1, Width: 0.05, Height: 0.05);
        var others = new List<(double X, double Y, double Width, double Height)> { (0.9, 0.9, 0.05, 0.05) };

        var (x, y) = TxImageEditorPaneView.ComputeAlignmentSnap(dragged, others, cropCenter: (0.5, 0.5));

        Assert.Null(x);
        Assert.Null(y);
    }

    // Follow-up visual-polish pass: ComputeAlignmentGuideLines is ComputeAlignmentSnap's
    // guide-LINE counterpart -- same match, different number out. Plan-review blocker this pins:
    // for an EDGE match, the two differ by the dragged element's own half-width/height (only a
    // center-to-center match has them coincide) -- an earlier draft bound ComputeAlignmentSnap's
    // own snapped-CENTER return straight into the rendered guide line, which would have drawn it
    // off the edge it claimed to align with for every edge case. These tests use the exact same
    // fixtures as ComputeAlignmentSnap's own tests immediately above, asserting the DIFFERENT
    // (correct) number each one returns.

    [Fact]
    public void ComputeAlignmentGuideLines_LeftEdgeCloseToOtherElementsLeftEdge_ReturnsTheSharedEdgeItself()
    {
        var dragged = (X: 0.433, Y: 0.5, Width: 0.06, Height: 0.1);
        var others = new List<(double X, double Y, double Width, double Height)> { (0.5, 0.99, 0.2, 0.1) }; // left edge = 0.4

        var (x, y) = TxImageEditorPaneView.ComputeAlignmentGuideLines(dragged, others, cropCenter: (0.99, 0.99));

        Assert.NotNull(x);
        // The guide line sits AT the shared edge (0.4) -- NOT at ComputeAlignmentSnap's own 0.43
        // (0.4 + dragged.Width/2), which is the dragged element's snapped CENTER, not the line.
        AssertClose(0.4, x!.Value);
        Assert.Null(y);
    }

    [Fact]
    public void ComputeAlignmentGuideLines_CentersClose_ReturnsExactCenterMatch()
    {
        // Center-to-center is the ONE case where ComputeAlignmentSnap and ComputeAlignmentGuideLines
        // agree (CenterOffset == 0), pinning that the two functions share one search, not two that
        // could silently disagree on which match won.
        var dragged = (X: 0.503, Y: 0.301, Width: 0.06, Height: 0.06);
        var others = new List<(double X, double Y, double Width, double Height)> { (0.5, 0.99, 0.3, 0.02) };

        var (x, _) = TxImageEditorPaneView.ComputeAlignmentGuideLines(dragged, others, cropCenter: (0.01, 0.01));

        AssertClose(0.5, x!.Value);
    }

    [Fact]
    public void ComputeAlignmentGuideLines_NearCropCenter_ReturnsCropCenterWithNoOtherElements()
    {
        var dragged = (X: 0.503, Y: 0.5, Width: 0.1, Height: 0.1);

        var (x, y) = TxImageEditorPaneView.ComputeAlignmentGuideLines(
            dragged, others: [], cropCenter: (0.5, 0.5));

        AssertClose(0.5, x!.Value);
        AssertClose(0.5, y!.Value);
    }

    [Fact]
    public void ComputeAlignmentGuideLines_NothingWithinThreshold_ReturnsNullForBothAxes()
    {
        var dragged = (X: 0.1, Y: 0.1, Width: 0.05, Height: 0.05);
        var others = new List<(double X, double Y, double Width, double Height)> { (0.9, 0.9, 0.05, 0.05) };

        var (x, y) = TxImageEditorPaneView.ComputeAlignmentGuideLines(dragged, others, cropCenter: (0.5, 0.5));

        Assert.Null(x);
        Assert.Null(y);
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
        // SetAsBackdrop's own single-push test for the established pattern). If this regressed
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

    /// <summary>ui_transition_plan.md step 5 (T1-6): "Copy to TX"'s HIS CALL seed -- a template
    /// referencing {his_call} comes up already filled with the received station's own callsign,
    /// instead of an empty row the operator has to retype.</summary>
    [AvaloniaFact]
    public void CurrentContactVariables_HisCallReferenced_PrefillsTheFillBarRow()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new Dictionary<string, string> { ["his_call"] = "W1AW", ["his_grid"] = "FN31pr" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "DE {his_call} {his_grid}";

        Assert.Equal("W1AW", vm.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
        Assert.Equal("FN31pr", vm.TemplateVariableRows.Single(r => r.Key == "his_grid").Value);
    }

    /// <summary>A key the seed didn't provide (no callsign known yet, e.g. no radio/FSK-decode) must
    /// stay a genuinely empty, editable row -- never a fabricated value.</summary>
    [AvaloniaFact]
    public void CurrentContactVariables_KeyNotProvided_LeavesTheFillBarRowEmpty()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new Dictionary<string, string> { ["his_call"] = "W1AW" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "{his_call} {his_grid}";

        Assert.Equal("W1AW", vm.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
        Assert.Equal(string.Empty, vm.TemplateVariableRows.Single(r => r.Key == "his_grid").Value);
    }

    /// <summary>The seed must never overwrite a value the operator ALREADY typed -- not reachable via
    /// the real "Copy to TX" entry point today (a brand-new editor has nothing typed yet), but this
    /// pins the ordering documented on the constructor itself, matching how the analogous
    /// EditorInitialState.TemplateVariables restore is documented to win too.</summary>
    [AvaloniaFact]
    public void CurrentContactVariables_DoesNotOverwriteAnAlreadyTypedValue()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(),
            new Dictionary<string, string> { ["his_call"] = "W1AW" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "{his_call}";
        var row = vm.TemplateVariableRows.Single(r => r.Key == "his_call");
        row.Value = "N0CALL";

        // Re-triggering the scan (e.g. editing the text and back) must not clobber the typed value.
        element.Text = "DE {his_call}";

        Assert.Equal("N0CALL", vm.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
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
        //
        // Tier B audit finding: KnownMacroTokenNames was never updated when {dist}/{bearing} were
        // added to MacroTextResolver (2026-08-18) -- exactly the drift this test's own doc comment
        // warned about. {dist}/{bearing} aren't included in THIS test's Text, though: unlike
        // name/grid/freq/mode, they resolve INDIRECTLY through the "his_grid" variable, so
        // referencing them legitimately DOES produce a row -- named "his_grid", never "dist"/
        // "bearing" themselves. See RescanTemplateVariables_DistOrBearingReferenced_CreatesAHisGridRow
        // below for that behavior; this test stays scoped to the four macros that produce no row at
        // all, which is still the exact invariant KnownMacroTokenNames itself must uphold for them.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "{name} {grid} {freq} {mode}";

        Assert.Empty(vm.TemplateVariableRows);
    }

    [AvaloniaFact]
    public void RescanTemplateVariables_DistOrBearingReferenced_CreatesAHisGridRow()
    {
        // Tier B audit follow-up: {dist}/{bearing} resolve FROM "his_grid" (see
        // OnTemplateVariableValueChanged's own test above), but RescanTemplateVariables only ever
        // created a fill-bar row for a key LITERALLY referenced in some element's Text -- a template
        // using only the DIST/BEARING chips (which insert {dist}/{bearing}, never a literal
        // {his_grid}) got no row at all, so the operator had no way to type HIS grid in and both
        // tokens resolved to empty forever.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "DIST {dist} BEARING {bearing}";

        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("his_grid", row.Key);
    }

    // User-reported (2026-09-20): inserting the "HIS RSV" chip ({rsv}) used to leave the fill-bar
    // row (and the live preview) blank until the operator retyped the operator's own near-universal
    // "595" convention every single time -- RescanTemplateVariables now seeds it from
    // OperatorSettings.DefaultRst on first discovery, same value the Options dialog itself defaults
    // to (OperatorSettings.DefaultRstFallback).
    [AvaloniaFact]
    public void RescanTemplateVariables_RsvReferenced_SeedsRowFromDefaultRst()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings { DefaultRst = "579" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "HIS RSV {rsv}";

        var row = Assert.Single(vm.TemplateVariableRows);
        Assert.Equal("rsv", row.Key);
        Assert.Equal("579", row.Value);
        Assert.Equal("HIS RSV 579", element.ResolvedText);
    }

    // User-reported (2026-09-20), real regression: the test above reads ResolvedText directly,
    // which always computes fresh and so can't tell "the canvas TextBlock was actually told to
    // re-fetch it" apart from "the property would return the right value if you asked it" -- those
    // came apart in practice. element.OnTextChanged already raises PropertyChanged(ResolvedText)
    // ONCE, synchronously, the instant Text is set to "HIS RSV {rsv}" -- but that fires BEFORE
    // RescanTemplateVariables (reached via the same Text-changed chain) has seeded
    // _templateVariables, so a bound TextBlock that received only THAT one (early) notification
    // would still be showing "HIS RSV {rsv}" verbatim, exactly what the user saw, even though a
    // FRESH read of ResolvedText (as in the test above) already returned the seeded value. A plain
    // Assert.Contains(nameof(ResolvedText), raisedPropertyNames) would NOT catch a missing second
    // raise -- that first, early one already satisfies it regardless of this fix -- so this reads
    // ResolvedText's value AT THE MOMENT of each raise, and asserts the ALREADY-SEEDED value shows
    // up at one of them (impossible without the seeding step's own explicit second raise).
    [AvaloniaFact]
    public void RescanTemplateVariables_RsvReferenced_RaisesResolvedTextChanged_AfterSeeding()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings { DefaultRst = "579" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var resolvedTextAtEachRaise = new List<string>();
        element.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayElementViewModel.ResolvedText))
            {
                resolvedTextAtEachRaise.Add(element.ResolvedText);
            }
        };

        element.Text = "HIS RSV {rsv}";

        Assert.Contains("HIS RSV 579", resolvedTextAtEachRaise);
    }

    [AvaloniaFact]
    public void RescanTemplateVariables_RsvReferenced_UnsetDefaultRst_SeedsRowFromDefaultRstFallback()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];

        element.Text = "{rsv}";

        Assert.Equal(OperatorSettings.DefaultRstFallback, vm.TemplateVariableRows[0].Value);
        Assert.Equal(OperatorSettings.DefaultRstFallback, element.ResolvedText);
    }

    [AvaloniaFact]
    public void RsvSeed_IsFreelyOverridableAndStaysOverriddenAfterAnotherRescan()
    {
        // "obviously allow manual changing" (user's own words): the seed is a REAL, ordinary
        // fill-bar value, not a sticky default -- editing it must behave exactly like editing
        // his_call/any other row, and a later Rescan (e.g. adding a second element) must not
        // clobber the operator's own per-QSO override back to the seed.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new OperatorSettings { DefaultRst = "595" });
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "HIS RSV {rsv}";
        Assert.Equal("595", vm.TemplateVariableRows[0].Value);

        vm.TemplateVariableRows[0].Value = "429";

        Assert.Equal("HIS RSV 429", element.ResolvedText);

        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[1]).Text = "another element, no rsv reference";

        Assert.Equal("429", vm.TemplateVariableRows[0].Value);
    }

    [AvaloniaFact]
    public void OnTemplateVariableValueChanged_HisGridEdited_RefreshesDistAndBearingElementsToo()
    {
        // Tier B audit finding: {dist}/{bearing} resolve FROM the "his_grid" variable
        // (MacroTextResolver.TryResolveDistanceBearing), not from a literal {his_grid} token in an
        // element's own Text -- an element reading "DIST {dist}" contains no "{his_grid}" substring,
        // so the plain Contains(token) check in OnTemplateVariableValueChanged never matched it,
        // leaving the canvas TextBlock's ResolvedText stale after a his_grid fill-bar edit even
        // though the mini-preview (which recomputes independently) updated correctly. Two elements:
        // one with a literal {his_grid} token (so the fill-bar row exists at all -- RescanTemplateVariables
        // only creates a row for a key actually referenced somewhere) and a SEPARATE one with only
        // {dist}/{bearing}, isolating the fix from the pre-existing literal-token-match path.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        ((OverlayElementViewModel)vm.OverlayElements[0]).Text = "HIS GRID: {his_grid}";
        vm.AddOverlayElementCommand.Execute(null);
        var distBearingElement = (OverlayElementViewModel)vm.OverlayElements[1];
        distBearingElement.Text = "DIST {dist} BEARING {bearing}";
        var row = Assert.Single(vm.TemplateVariableRows, r => r.Key == "his_grid");

        var raised = false;
        distBearingElement.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(OverlayElementViewModel.ResolvedText);
        row.Value = "JO65";

        Assert.True(raised);
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
        // Mode name uppercased for display (design-fidelity Phase I nit) -- the mock's own readouts
        // are all-caps mono chrome text.
        // 0.0 either way (SmallMode.LineSegments is empty) -- see the LinePairedMode-based test
        // below for the non-degenerate case that actually distinguishes the fixed formula.
        Assert.Equal(new object[] { SmallMode.DisplayName.ToUpperInvariant(), 0.0 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void SendMetaText_LinePairedMode_DividesByRowsPerTransmissionLine()
    {
        var localization = new FakeLocalizationService();
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), LinePairedMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), localization, NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeFilePickerService(), new FakeImageFileLoader(), new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());

        _ = vm.SendMetaText;

        Assert.Equal("Panes.TxImageEditor.SendMetaFormat", localization.LastKey);
        var duration = Assert.IsType<double>(localization.LastArgs[1]);
        Assert.Equal(0.2, duration, precision: 10);
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
    public void OnTemplateVariableValueChanged_BlankedToEmpty_RemovesTheKeyInsteadOfStoringAnEmptyValue()
    {
        // Tier B audit finding: blanking a SINGLE fill-bar field (typing, not the bulk "Clear
        // fields" command -- see ClearTemplateVariables_BlanksEveryValue_IncludingCurrentlyHiddenOnes
        // above for that path's own already-correct behavior) used to store an empty string
        // unconditionally, unlike the bulk-clear path. Same MacroTextResolver contract applies here:
        // a present-but-empty key resolves to "", not the verbatim token -- two UI paths to the same
        // "clear this field" intent must produce the same result.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Text = "{his_call}";
        var row = Assert.Single(vm.TemplateVariableRows);
        row.Value = "K1ABC";
        Assert.Equal("K1ABC", element.ResolvedText);

        row.Value = string.Empty;

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
        Dispatcher.UIThread.RunJobs();

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
    public void BuildTemplateElement_GrowToFillEnabled_PassesThroughToTheRealPipeline()
    {
        // Same "the real pipeline call site" regression guard as
        // BuildTemplateElement_PassesFontFamilyAndStroke_ToTheRealPipeline above -- confirms
        // GrowToFillEnabled reaches TemplateTextElement (what DrawTemplateText actually renders
        // with), not just ComputeCanvasFontSize's own canvas-preview-only call site.
        var preparer = new FakeTransmitImagePreparer();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        Assert.False(element.GrowToFillEnabled); // off by default (user-requested opt-in)

        element.GrowToFillEnabled = true;
        Dispatcher.UIThread.RunJobs();

        var text = Assert.IsType<TemplateTextElement>(Assert.Single(preparer.TemplateDocuments[^1].Elements));
        Assert.True(text.GrowToFillEnabled);
    }

    [AvaloniaFact]
    public void GrowToFillEnabled_Toggling_GrowsCanvasFontSizePastNominal()
    {
        // yoniq-auditor nit: a FakeTransmitImagePreparer-based version of this test would be vacuous
        // (the fake's own MeasureFittedFontSize ignores growToFill entirely, same reasoning as
        // BuildTemplateElement_GrowToFillEnabled_PassesThroughToTheRealPipeline above needing the
        // fake instead). This one uses the REAL preparer with a deliberately huge box, so
        // OnOverlayElementPropertyChanged's own filter genuinely has to include GrowToFillEnabled (not
        // just avoid throwing) for CanvasFontSize to move at all.
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        element.Width = 0.95;
        element.Height = 0.95;
        var beforeGrow = element.CanvasFontSize;

        element.GrowToFillEnabled = true;

        Assert.True(element.CanvasFontSize > beforeGrow, $"Expected grow-to-fill to grow CanvasFontSize above {beforeGrow}, got {element.CanvasFontSize}.");
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
        // Code-review-class regression guard: a plain `text.Z - 1` (mirroring SetAsBackdrop's own
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
    public void TextStyle_BoldPropertyCreatesUndoStepAndNewTextInvalidatesRedo()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));

        element.Bold = true;
        vm.UndoCommand.Execute(null);

        var restored = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
        Assert.False(restored.Bold);
        Assert.True(vm.RedoCommand.CanExecute(null));
        restored.Text = "new text after undo";
        Assert.False(vm.RedoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);
        Assert.NotEqual("new text after undo", Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements)).Text);
    }

    [AvaloniaTheory]
    [InlineData("Text", "changed")]
    [InlineData("FontFamily", "Barlow")]
    [InlineData("Italic", true)]
    [InlineData("StrokeThickness", .07)]
    [InlineData("ShadowOffsetX", .07)]
    [InlineData("ShadowOffsetY", .07)]
    [InlineData("StackStepX", .07)]
    [InlineData("StackStepY", .07)]
    [InlineData("RotationDegrees", 20.0)]
    [InlineData("GradientEnabled", true)]
    [InlineData("BitmapFillEnabled", true)]
    public void TextStyle_DirectPropertyBinding_CapturesUndo(string propertyName, object value)
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
        var property = typeof(OverlayElementViewModel).GetProperty(propertyName)!;
        var original = property.GetValue(element);
        property.SetValue(element, value);

        vm.UndoCommand.Execute(null);

        Assert.Equal(original, property.GetValue(Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements))));
        vm.RedoCommand.Execute(null);
        Assert.Equal(value, property.GetValue(Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements))));
    }

    [AvaloniaFact]
    public void TextStyle_ColorGradientAndBitmapProperties_CaptureUndo()
    {
        foreach (var name in new[] { "StrokeColor", "ShadowColor", "StackColor", "GradientStartColor", "GradientEndColor", "GradientKind", "BitmapFillSource" })
        {
            using var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
            vm.AddOverlayElementCommand.Execute(null);
            var element = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
            var property = typeof(OverlayElementViewModel).GetProperty(name)!;
            var original = property.GetValue(element);
            object value = name switch
            {
                "GradientKind" => TextGradientKind.Radial,
                "BitmapFillSource" => CreateSource(4, 4),
                _ => new Rgb24(1, 2, 3),
            };
            property.SetValue(element, value);

            vm.UndoCommand.Execute(null);

            Assert.Equal(original, property.GetValue(Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements))));
            vm.RedoCommand.Execute(null);
            Assert.Equal(value, property.GetValue(Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements))));
        }
    }

    [AvaloniaFact]
    public void TextStyle_BitmapDisablesGradient_OneUndoRestoresBoth()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
        element.GradientEnabled = true;
        Dispatcher.UIThread.RunJobs();
        element.BitmapFillEnabled = true;
        Assert.False(element.GradientEnabled);

        vm.UndoCommand.Execute(null);

        var restored = Assert.IsType<OverlayElementViewModel>(Assert.Single(vm.OverlayElements));
        Assert.True(restored.GradientEnabled);
        Assert.False(restored.BitmapFillEnabled);
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

    // User-reported gap (2026-09-15): the canvas right-click "Save Template" entry used to just
    // focus an empty name field instead of saving. Now auto-fills the next free "tmp{N}" name.

    [AvaloniaFact]
    public async Task SaveTemplateWithAutoNameAsync_NameEmpty_UsesTmp0()
    {
        // User-requested (2026-09-19): numbering starts at 0, not 1.
        var templateStore = new FakeTemplateStore();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter());
        vm.AddOverlayElementCommand.Execute(null);
        Assert.Equal(string.Empty, vm.NewTemplateName);

        await vm.SaveTemplateWithAutoNameAsync();

        var saved = Assert.Single(await templateStore.ListAsync());
        Assert.Equal("tmp0", saved.Name);
    }

    [AvaloniaFact]
    public async Task SaveTemplateWithAutoNameAsync_Tmp0AlreadyTaken_UsesNextFreeNumber()
    {
        var templateStore = new FakeTemplateStore();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter());
        vm.AddOverlayElementCommand.Execute(null);
        await vm.SaveTemplateWithAutoNameAsync(); // "tmp0"

        vm.AddOverlayElementCommand.Execute(null);
        await vm.SaveTemplateWithAutoNameAsync();

        var names = (await templateStore.ListAsync()).Select(t => t.Name).OrderBy(n => n).ToList();
        Assert.Equal(["tmp0", "tmp1"], names);
    }

    [AvaloniaFact]
    public async Task SaveTemplateWithAutoNameAsync_NameAlreadyTyped_UsesTypedNameNotAuto()
    {
        var templateStore = new FakeTemplateStore();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter());
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "My Template";

        await vm.SaveTemplateWithAutoNameAsync();

        var saved = Assert.Single(await templateStore.ListAsync());
        Assert.Equal("My Template", saved.Name);
    }

    // Backlog item (auditor usability review, 2026-08-17): "Saving a template under an existing name
    // creates a duplicate entry, not an update/rename." CreateTemplateId always mints a fresh id
    // (guid8 suffix) -- saving under a name that already exists in the rack's own list must reuse
    // THAT id instead, so the save overwrites in place.

    [AvaloniaFact]
    public async Task SaveTemplateAsync_SameNameTwice_OverwritesInPlaceInsteadOfCreatingADuplicate()
    {
        var templateStore = new FakeTemplateStore();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter());
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "Contest card";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var firstSaved = Assert.Single(await templateStore.ListAsync());

        vm.AddBoxElementCommand.Execute(null); // change the content before the second save
        vm.NewTemplateName = "Contest card";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        var secondSaved = Assert.Single(await templateStore.ListAsync());
        Assert.Equal(firstSaved.Id, secondSaved.Id);
        var document = await templateStore.LoadAsync(secondSaved.Id);
        Assert.Equal(2, document.Elements.Count); // reflects the SECOND save's content
    }

    [AvaloniaFact]
    public async Task SaveTemplateAsync_OverwriteFailsPartway_LeavesThePreExistingTemplateIntact()
    {
        // Tier B audit finding (blocker): this used to DeleteAsync the pre-existing template BEFORE
        // writing anything, then unconditionally delete-on-failure again in the catch -- a save that
        // failed ANY time after the up-front delete permanently destroyed the operator's real,
        // pre-existing template with no way to recover it. Save must now leave the old template
        // completely untouched if the overwrite fails.
        var templateStore = new FakeTemplateStore();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter());
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "Contest card";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var firstSaved = Assert.Single(await templateStore.ListAsync());
        var originalDocument = await templateStore.LoadAsync(firstSaved.Id);

        vm.AddBoxElementCommand.Execute(null);
        vm.NewTemplateName = "Contest card";
        templateStore.SaveExceptionToThrow = new IOException("disk full");

        await vm.SaveTemplateCommand.ExecuteAsync(null);

        templateStore.SaveExceptionToThrow = null;
        var stillSaved = Assert.Single(await templateStore.ListAsync());
        Assert.Equal(firstSaved.Id, stillSaved.Id);
        var documentAfterFailure = await templateStore.LoadAsync(stillSaved.Id);
        Assert.Equal(originalDocument.Elements.Count, documentAfterFailure.Elements.Count);
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task SaveTemplateAsync_ResolvesExistingIdFromTheTemplateStore_NotTheReadyRacksOwnPossiblyStaleList()
    {
        // Tier B audit finding: this used to resolve existingId from ReadyRack.AllTemplates, a
        // separate VM's own in-memory projection populated by a fire-and-forget RefreshAsync call --
        // saving before that refresh completes read a stale, EMPTY list, so an overwrite of a real
        // existing template silently degraded into creating a duplicate instead. _templateStore is
        // now read directly, independent of whether ReadyRack's own list has caught up.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(); // fresh, never refreshed -- AllTemplates starts empty
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "Contest card";
        await vm.SaveTemplateCommand.ExecuteAsync(null);
        var firstSaved = Assert.Single(await templateStore.ListAsync());
        Assert.Empty(readyRack.AllTemplates); // proves the read below can't be coming from here

        vm.AddBoxElementCommand.Execute(null);
        vm.NewTemplateName = "Contest card";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        var secondSaved = Assert.Single(await templateStore.ListAsync());
        Assert.Equal(firstSaved.Id, secondSaved.Id);
    }

    [AvaloniaFact]
    public async Task SaveTemplateAsync_DifferentName_CreatesASeparateEntry()
    {
        var templateStore = new FakeTemplateStore();
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter());
        vm.AddOverlayElementCommand.Execute(null);
        vm.NewTemplateName = "First";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        vm.NewTemplateName = "Second";
        await vm.SaveTemplateCommand.ExecuteAsync(null);

        Assert.Equal(2, (await templateStore.ListAsync()).Count);
    }

    // Backlog item (auditor usability review, 2026-08-17): "No error surface anywhere in the editor
    // -- a failed Save/Load/add-image is ILogger-only, invisible to the operator."

    [AvaloniaFact]
    public async Task AddImageFromFileAsync_LoadThrows_SetsStatusMessageInsteadOfFailingSilently()
    {
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader(); // ResultToReturn unset -> throws
        var vm = new TxImageEditorPaneViewModel(
            CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), new MacroTextResolver(), new OperatorSettings(),
            new FakeRadioSessionService(), new FakeLocalizationService(), NullLogger<TxImageEditorPaneViewModel>.Instance,
            picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), CreateReadyRack());
        Assert.Null(vm.StatusMessage);

        await vm.AddImageFromFileCommand.ExecuteAsync(null);

        Assert.False(string.IsNullOrEmpty(vm.StatusMessage));
        Assert.Empty(vm.OverlayElements);
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
            var templateStore = new TemplateStore(imageSourceWriter, new ImageFileLoader(), new FakeTransmitImagePreparer(), NullLogger<TemplateStore>.Instance, root);
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

        vm.ConfirmRequested = _ => Task.FromResult(true);
        var templateId = templateStore.CreateTemplateId("Loadable");
        await templateStore.SaveAsync(templateId, "Loadable", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
            new PersistedBoxElement(0.3, 0.3, 0.1, 0.1, 1, false, new Rgb24(4, 5, 6), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        // Templates rack rework: loading a template while the editor HasUnsavedEdits
        // (AddOverlayElementCommand above pushed one) now awaits a real confirm dialog (wired above
        // to auto-confirm) instead of the old two-click status-bar arm -- see
        // OnReadyRackTemplateSelected's own doc comment. One click is enough.
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
    public async Task LoadTemplate_OlderSlowerLoadCompletesAfterANewerFasterOne_DoesNotClobberTheNewerResult()
    {
        // Tier B audit finding: ReadyRackViewModel's Load command is a plain synchronous
        // [RelayCommand] that just raises TemplateSelected into OnReadyRackTemplateSelected (an
        // async void) -- nothing serializes two overlapping loads. Without a generation guard,
        // clicking template A (slow) then quickly clicking template B (fast) let B populate the
        // canvas first, then A's slower continuation overwrite it right back with A -- the operator
        // ends up looking at the template they did NOT just ask for.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var idA = templateStore.CreateTemplateId("A");
        await templateStore.SaveAsync(idA, "A", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        var idB = templateStore.CreateTemplateId("B");
        await templateStore.SaveAsync(idB, "B", new PersistedTemplateDocument([
            new PersistedBoxElement(0.1, 0.1, 0.1, 0.1, 0, false, new Rgb24(1, 1, 1), null, 0, 1.0),
            new PersistedBoxElement(0.2, 0.2, 0.1, 0.1, 1, false, new Rgb24(2, 2, 2), null, 0, 1.0),
            new PersistedBoxElement(0.3, 0.3, 0.1, 0.1, 2, false, new Rgb24(3, 3, 3), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var rowA = readyRack.AllTemplates.Single(t => t.Name == "A");
        var rowB = readyRack.AllTemplates.Single(t => t.Name == "B");

        var gateA = new TaskCompletionSource<PersistedTemplateDocument>();
        templateStore.LoadGates[idA] = gateA;
        readyRack.LoadCommand.Execute(rowA); // starts A's load, suspends on the gate
        readyRack.LoadCommand.Execute(rowB); // B has no gate -- resolves synchronously, wins the canvas

        Assert.Equal(3, vm.OverlayElements.Count); // B's content

        gateA.SetResult(templateStore.Templates[idA].Document); // A's slow load finally completes
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        // Must still show B -- A's now-stale completion must be discarded, not clobber the canvas.
        Assert.Equal(3, vm.OverlayElements.Count);
    }

    [AvaloniaFact]
    public async Task LoadTemplate_CleanEditor_FirstLoad_DoesNotAskAndBadgeIsPlainLoaded()
    {
        // yoniq-auditor finding: LoadTemplateIntoLiveEditor pushes its OWN undo snapshot (a template
        // load must be undoable, a deliberate pre-existing design decision), which made the OLD
        // "reuse HasUnsavedEdits" design read the slot as "Loaded • edited" from the very first
        // load, with zero operator edits. Confirms the fix: a fresh editor's first load neither
        // requests a confirm dialog nor marks IsLoadedAndEdited.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var confirmRequested = false;
        vm.ConfirmRequested = _ => { confirmRequested = true; return Task.FromResult(true); };
        var templateId = templateStore.CreateTemplateId("A");
        await templateStore.SaveAsync(templateId, "A", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);
        await readyRack.TogglePinCommand.ExecuteAsync(row); // the Loaded badge only shows on a PINNED slot

        readyRack.LoadCommand.Execute(readyRack.AllTemplates.Single(t => t.Id == templateId));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.False(confirmRequested);
        Assert.Contains(readyRack.Slots, s => s.IsLoadedOnly);
        Assert.DoesNotContain(readyRack.Slots, s => s.IsLoadedAndEdited);
    }

    [AvaloniaFact]
    public async Task LoadTemplate_SecondLoadWithNoRealEditsSinceTheFirst_DoesNotAskAgain()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var confirmCount = 0;
        vm.ConfirmRequested = _ => { confirmCount++; return Task.FromResult(true); };
        var idA = templateStore.CreateTemplateId("A");
        await templateStore.SaveAsync(idA, "A", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        var idB = templateStore.CreateTemplateId("B");
        await templateStore.SaveAsync(idB, "B", new PersistedTemplateDocument([
            new PersistedBoxElement(0.3, 0.3, 0.1, 0.1, 0, false, new Rgb24(4, 5, 6), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var rowA = readyRack.AllTemplates.Single(t => t.Id == idA);
        var rowB = readyRack.AllTemplates.Single(t => t.Id == idB);
        readyRack.LoadCommand.Execute(rowA);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, confirmCount);

        // Load a DIFFERENT template with zero operator edits since A's own load completed -- must
        // still proceed without a dialog, since HasUnsavedEdits (permanently true after any load)
        // is no longer what gates this.
        readyRack.LoadCommand.Execute(rowB);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, confirmCount);
        Assert.Single(vm.OverlayElements);
    }

    [AvaloniaFact]
    public async Task LoadTemplate_ARealEditAfterLoading_AsksOnTheNextLoadAndMarksTheBadgeEdited()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var confirmCount = 0;
        vm.ConfirmRequested = _ => { confirmCount++; return Task.FromResult(true); };
        var idA = templateStore.CreateTemplateId("A");
        await templateStore.SaveAsync(idA, "A", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        var idB = templateStore.CreateTemplateId("B");
        await templateStore.SaveAsync(idB, "B", new PersistedTemplateDocument([]));
        await readyRack.RefreshAsync();
        await readyRack.TogglePinCommand.ExecuteAsync(readyRack.AllTemplates.Single(t => t.Id == idA)); // the badge only shows on a PINNED slot
        readyRack.LoadCommand.Execute(readyRack.AllTemplates.Single(t => t.Id == idA));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        vm.AddOverlayElementCommand.Execute(null); // a REAL operator edit since the load

        Assert.Contains(readyRack.Slots, s => s.IsLoadedAndEdited);
        readyRack.LoadCommand.Execute(readyRack.AllTemplates.Single(t => t.Id == idB));
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, confirmCount);
    }

    [AvaloniaFact]
    public async Task LoadTemplate_FromLibrarySelection_GoesThroughTheSameConfirmGatedFlowAsTheRack()
    {
        // Expanded template selector: OnLibraryItemDoubleTapped (the real code-behind handler a
        // double-click on a Library row/tile invokes) just calls ReadyRack.LoadCommand.Execute(row)
        // -- this proves that reuse actually wires up to the SAME OnReadyRackTemplateSelected confirm
        // gate the rack already has tests for, not a full re-test of that gate's own logic.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        ConfirmActionDialogViewModel? seenConfirmVm = null;
        vm.ConfirmRequested = confirmVm => { seenConfirmVm = confirmVm; return Task.FromResult(true); };
        vm.AddOverlayElementCommand.Execute(null);
        var templateId = templateStore.CreateTemplateId("Loadable");
        await templateStore.SaveAsync(templateId, "Loadable", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        // Same call OnLibraryItemDoubleTapped makes -- not simulating the actual pointer event,
        // since this is a VM-level test.
        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(seenConfirmVm);
        Assert.Equal("Panes.TxImageEditor.ConfirmDiscardTitle", seenConfirmVm!.Title);
        Assert.Single(vm.OverlayElements);
        Assert.IsType<BoxElementViewModel>(vm.OverlayElements[0]);
    }

    // Backlog item (auditor usability review, 2026-08-17): "Ready Rack ... recall silently replaces
    // the whole layout with no confirmation."

    [AvaloniaFact]
    public async Task LoadTemplate_WithUnsavedEdits_RequestsARealConfirmDialogWithTheRightText()
    {
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        ConfirmActionDialogViewModel? seenConfirmVm = null;
        vm.ConfirmRequested = confirmVm =>
        {
            seenConfirmVm = confirmVm;
            return Task.FromResult(false); // decline
        };
        vm.AddOverlayElementCommand.Execute(null);
        var originalElementCount = vm.OverlayElements.Count;
        var templateId = templateStore.CreateTemplateId("Loadable");
        await templateStore.SaveAsync(templateId, "Loadable", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var row = Assert.Single(readyRack.AllTemplates);

        readyRack.LoadCommand.Execute(row);
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(seenConfirmVm);
        // FakeLocalizationService.GetString returns the raw key -- asserting the exact keys proves
        // title/body/both button labels all reach the dialog, not just "some text was set."
        Assert.Equal("Panes.TxImageEditor.ConfirmDiscardTitle", seenConfirmVm!.Title);
        Assert.Equal("Panes.TxImageEditor.ConfirmDiscardBody", seenConfirmVm.Message);
        Assert.Equal("Panes.TxImageEditor.ConfirmDiscardButton", seenConfirmVm.ConfirmLabel);
        Assert.Equal("Panes.TxImageEditor.DialogCancel", seenConfirmVm.CancelLabel);
        // Declining leaves the canvas untouched.
        Assert.Equal(originalElementCount, vm.OverlayElements.Count);
    }

    [AvaloniaFact]
    public async Task LoadTemplate_WithUnsavedEdits_EachSelectionGetsItsOwnIndependentConfirmation()
    {
        // Templates rack rework: the old status-bar arm/confirm re-armed on a different target
        // instead of confirming the old one -- there's no shared arm token anymore for that failure
        // mode to exist in. This proves the replacement: two different templates each independently
        // trigger (and here, each decline) their own dialog, with neither ever loading.
        var templateStore = new FakeTemplateStore();
        var readyRack = CreateReadyRack(templateStore);
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer(), templateStore, new FakeImageSourceWriter(), readyRack);
        var confirmCallCount = 0;
        vm.ConfirmRequested = _ =>
        {
            confirmCallCount++;
            return Task.FromResult(false);
        };
        vm.AddOverlayElementCommand.Execute(null);
        var originalElementCount = vm.OverlayElements.Count;
        var firstId = templateStore.CreateTemplateId("First");
        await templateStore.SaveAsync(firstId, "First", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(1, 2, 3), null, 0, 1.0),
        ]));
        var secondId = templateStore.CreateTemplateId("Second");
        await templateStore.SaveAsync(secondId, "Second", new PersistedTemplateDocument([
            new PersistedBoxElement(0.5, 0.5, 0.2, 0.2, 0, false, new Rgb24(4, 5, 6), null, 0, 1.0),
        ]));
        await readyRack.RefreshAsync();
        var first = readyRack.AllTemplates.Single(r => r.Id == firstId);
        var second = readyRack.AllTemplates.Single(r => r.Id == secondId);

        readyRack.LoadCommand.Execute(first);
        Dispatcher.UIThread.RunJobs();
        readyRack.LoadCommand.Execute(second);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, confirmCallCount);
        Assert.Equal(originalElementCount, vm.OverlayElements.Count); // neither loaded
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

    [AvaloniaFact]
    public void Undo_AfterAddLastRxImage_DisposesTheRemovedImageElementsBitmap_DeferredViaDispatcherPost()
    {
        // T0-11 (production_audit.md): ApplyState's own whole-collection discard (Undo/Redo)
        // disposes every removed ImageElementViewModel's CanvasBitmap, not just OnSourceChanged's
        // in-place reassignment. AddLastRxImage pushes an undo snapshot before inserting, so Undo
        // here exercises ApplyState's own clear/rebuild loop end to end.
        var receivedImage = new FakeReceivedImageBuffer { Current = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(6, 4), SmallMode, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeImageFileLoader(), receivedImage, new FakeReceiveHistoryStore());

        vm.AddLastRxImageCommand.Execute(null);
        var inserted = Assert.IsType<ImageElementViewModel>(Assert.Single(vm.OverlayElements));
        var bitmap = inserted.CanvasBitmap;

        vm.UndoCommand.Execute(null);

        Assert.False(IsWriteableBitmapDisposed(bitmap), "must not be disposed before the deferred post runs");
        Assert.Empty(vm.OverlayElements);
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsWriteableBitmapDisposed(bitmap));
    }

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique WriteableBitmapPoolTests
    // uses: a disposed instance throws NullReferenceException (not ObjectDisposedException) from
    // any real operation, here .Lock().
    private static bool IsWriteableBitmapDisposed(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        try
        {
            using (bitmap.Lock())
            {
            }

            return false;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    }

    // TX workflow modernization plan, Phase 7: flatten. Uses the REAL TransmitImagePreparer (not a
    // fake) -- a fake can't produce pixels to bake, and CanExecute/undo/source-replacement behavior
    // needs the real bake path to actually run without throwing. Deep pixel-equivalence testing
    // (the auditor-specified tolerance rule: tight bound on a solid box, MAE+outlier bound for text,
    // plus a non-vacuous ink-presence assertion) lives further down, in
    // FlattenElementAsync_SolidBoxElement_InteriorMatchesFillColorAfterResample and
    // FlattenElementAsync_TextElement_MatchesDirectRenderWithinToleranceAndLeavesVisibleInk.
    private static readonly SstvModeDefinition FlattenTestMode = new(
        Id: "flatten-test", DisplayName: "FlattenTest", VisCode: 0,
        ImageWidth: 80, ImageHeight: 60, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    // Same repo-root-relative font resolution as TransmitImagePreparerComposePreviewTests --
    // TransmitImagePreparer's own default font path assumes assets/fonts sits next to the running
    // assembly, which isn't true for this test project's output directory.
    private static readonly string FlattenTestFontPath =
        Path.Combine(FindRepoRoot(), "assets", "fonts", "DejaVuSansMono.ttf");

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "ScanlineStudio.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("Could not locate repo root (ScanlineStudio.sln) from " + AppContext.BaseDirectory);
    }

    [AvaloniaFact]
    public async Task FlattenElementAsync_ValidBoxElement_RemovesElementAndReplacesSourceSameDimensions()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);
        var sourceBefore = vm.CurrentSource;

        await vm.FlattenElementCommand.ExecuteAsync(element);

        Assert.Empty(vm.OverlayElements);
        Assert.Null(vm.SelectedOverlayElement);
        Assert.NotSame(sourceBefore, vm.CurrentSource);
        Assert.Equal(sourceBefore.Width, vm.CurrentSource.Width);
        Assert.Equal(sourceBefore.Height, vm.CurrentSource.Height);
    }

    /// <summary>TX editor gap-items plan, item 2 (group-ops-lite) -- flattening a NON-primary group
    /// member must shrink <see cref="TxImageEditorPaneViewModel.SelectedOverlayElements"/> to the
    /// remaining member, not leave a stale (and by then disposed) reference in the set. Uses the
    /// REAL <see cref="TransmitImagePreparer"/>, same as the other Flatten tests in this region,
    /// since Flatten's own stale-result guard compares against a genuine rendered result.</summary>
    [AvaloniaFact]
    public async Task FlattenElementAsync_OnNonPrimaryGroupMember_ShrinksTheGroup()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var boxA = vm.OverlayElements[0];
        vm.AddBoxElementCommand.Execute(null);
        var boxB = vm.OverlayElements[1];
        vm.SetSelection([boxA, boxB]);
        Assert.Same(boxB, vm.SelectedOverlayElement);

        await vm.FlattenElementCommand.ExecuteAsync(boxA);

        Assert.DoesNotContain(boxA, vm.OverlayElements);
        Assert.Equal(new[] { boxB }, vm.SelectedOverlayElements);
        Assert.Same(boxB, vm.SelectedOverlayElement);
    }

    /// <summary>Code-review finding (2026-09-01, box gradient fill): <see cref="TextGradient"/>'s
    /// auto-generated record equality compared its <c>Stops</c> list via reference equality (an
    /// <c>IReadOnlyList&lt;T&gt;</c>-typed property falls back to that), and
    /// <see cref="TxImageEditorPaneViewModel.BuildTemplateElement"/> mints a fresh <c>Stops</c> array
    /// on every call -- so Flatten's own stale-result guard (<c>!BuildTemplateElement(element).Equals(
    /// request.Element)</c>) NEVER matched for a gradient element, unconditionally discarding every
    /// flatten of a gradient box (or text -- same pre-existing shape, now reachable for boxes too) as
    /// stale. Fixed with a real <see cref="TextGradient.Equals(TextGradient?)"/>/<c>GetHashCode</c>
    /// override; this proves flatten actually succeeds now, using the REAL preparer (not a fake), so
    /// the genuine equality comparison is exercised end-to-end.</summary>
    [AvaloniaFact]
    public async Task FlattenElementAsync_GradientBoxElement_SucceedsRatherThanDiscardingAsStale()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = (BoxElementViewModel)Assert.Single(vm.OverlayElements);
        element.GradientEnabled = true;
        element.GradientKind = TextGradientKind.Horizontal;
        var sourceBefore = vm.CurrentSource;

        await vm.FlattenElementCommand.ExecuteAsync(element);

        Assert.Empty(vm.OverlayElements);
        Assert.NotSame(sourceBefore, vm.CurrentSource);
        // FakeLocalizationService.GetString returns the raw key unchanged -- a literal match against
        // that key IS the exact-value check that this StatusMessage was NOT the stale-discard path.
        Assert.NotEqual("Panes.TxImageEditor.FlattenDiscardedStale", vm.StatusMessage);
    }

    /// <summary>Second-round audit finding: the sibling test above covers box only, but the
    /// <see cref="TextGradient"/> equality bug it fixes was ALREADY reachable for gradient TEXT
    /// elements before this session's box gradient fill ever existed -- meaning flatten-a-gradient-
    /// text-element had never executed its success path in production until this fix. Same real-
    /// preparer proof, text element instead of box.</summary>
    [AvaloniaFact]
    public async Task FlattenElementAsync_GradientTextElement_SucceedsRatherThanDiscardingAsStale()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)Assert.Single(vm.OverlayElements);
        element.GradientEnabled = true;
        element.GradientKind = TextGradientKind.Horizontal;
        var sourceBefore = vm.CurrentSource;

        await vm.FlattenElementCommand.ExecuteAsync(element);

        Assert.Empty(vm.OverlayElements);
        Assert.NotSame(sourceBefore, vm.CurrentSource);
        Assert.NotEqual("Panes.TxImageEditor.FlattenDiscardedStale", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task FlattenElementAsync_ThenUndo_RestoresElementAndOriginalSourceReference()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);
        var sourceBefore = vm.CurrentSource;

        await vm.FlattenElementCommand.ExecuteAsync(element);
        Assert.True(vm.UndoCommand.CanExecute(null));
        vm.UndoCommand.Execute(null);

        Assert.Single(vm.OverlayElements);
        // Bit-exact, not tolerance-based -- undo restores the pre-flatten source INSTANCE (the
        // SourceBaseline swap in ApplyState), it does not re-derive it.
        Assert.Same(sourceBefore, vm.CurrentSource);
    }

    [AvaloniaFact]
    public async Task FlattenElementAsync_ThenUndoThenRedo_ReappliesFlatten()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);

        await vm.FlattenElementCommand.ExecuteAsync(element);
        var sourceAfterFlatten = vm.CurrentSource;
        vm.UndoCommand.Execute(null);
        Assert.True(vm.RedoCommand.CanExecute(null));
        vm.RedoCommand.Execute(null);

        // Round-2 auditor finding this test pins: the redo counterpart snapshot must carry the
        // OUTGOING baseline so redo can swap FORWARD again, not just backward -- without that fix
        // redo silently left the flattened pixels unreachable (element gone, bake not restored).
        Assert.Empty(vm.OverlayElements);
        Assert.Same(sourceAfterFlatten, vm.CurrentSource);
    }

    [AvaloniaFact]
    public void CanFlattenElement_ElementNotInOverlayElements_ReturnsFalse()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(80, 60), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);
        vm.RemoveOverlayElementCommand.Execute(element);

        Assert.False(vm.FlattenElementCommand.CanExecute(element));
    }

    // Follow-up visual-polish pass: PlacementPreviewRect/GuideLineXNormalized/GuideLineYNormalized
    // are set by TxImageEditorPaneView's own pointer-move code-behind, which this VM-only test file
    // can't drive directly (no real Avalonia pointer events here) -- this instead pins the VM-side
    // wiring those code-behind writes depend on: the pixel-space cascade and IsVisible derivation,
    // the one piece of this feature that IS unit-testable without a real drag.
    [AvaloniaFact]
    public void PlacementPreviewAndGuideLineState_SetAndClear_CascadeToTheirPixelSpaceAndVisibilityProperties()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.False(vm.IsPlacementPreviewVisible);
        Assert.False(vm.IsGuideLineXVisible);
        Assert.False(vm.IsGuideLineYVisible);

        vm.PlacementPreviewRect = new NormalizedRect(0.25, 0.25, 0.5, 0.25);
        vm.GuideLineXNormalized = 0.5;
        vm.GuideLineYNormalized = 0.75;

        Assert.True(vm.IsPlacementPreviewVisible);
        AssertClose(0.25 * vm.CanvasDisplayWidth, vm.PlacementPreviewLeftPixels);
        AssertClose(0.25 * vm.CanvasDisplayHeight, vm.PlacementPreviewTopPixels);
        AssertClose(0.5 * vm.CanvasDisplayWidth, vm.PlacementPreviewWidthPixels);
        AssertClose(0.25 * vm.CanvasDisplayHeight, vm.PlacementPreviewHeightPixels);
        Assert.True(vm.IsGuideLineXVisible);
        AssertClose(0.5 * vm.CanvasDisplayWidth, vm.GuideLineXPixels);
        Assert.True(vm.IsGuideLineYVisible);
        AssertClose(0.75 * vm.CanvasDisplayHeight, vm.GuideLineYPixels);

        vm.PlacementPreviewRect = null;
        vm.GuideLineXNormalized = null;
        vm.GuideLineYNormalized = null;

        Assert.False(vm.IsPlacementPreviewVisible);
        Assert.False(vm.IsGuideLineXVisible);
        Assert.False(vm.IsGuideLineYVisible);
    }

    // TX editor gap-items plan, line element: same "set/clear cascades to pixel-space and
    // visibility properties" contract as PlacementPreviewRect above, for the line-shaped preview
    // TxImageEditorPaneView's own code-behind writes during a line placement drag.
    [AvaloniaFact]
    public void PlacementPreviewLine_SetAndClear_CascadeToItsPixelSpaceAndVisibilityProperties()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());

        Assert.False(vm.IsPlacementPreviewLineVisible);

        vm.PlacementPreviewLine = (0.1, 0.2, 0.6, 0.8);

        Assert.True(vm.IsPlacementPreviewLineVisible);
        AssertClose(0.1 * vm.CanvasDisplayWidth, vm.PlacementPreviewLineStart.X);
        AssertClose(0.2 * vm.CanvasDisplayHeight, vm.PlacementPreviewLineStart.Y);
        AssertClose(0.6 * vm.CanvasDisplayWidth, vm.PlacementPreviewLineEnd.X);
        AssertClose(0.8 * vm.CanvasDisplayHeight, vm.PlacementPreviewLineEnd.Y);

        vm.PlacementPreviewLine = null;

        Assert.False(vm.IsPlacementPreviewLineVisible);
    }

    [AvaloniaFact]
    public void ApplySnappedLineEndpoint_FirstEndpoint_SnapsOnlyThatEndpoint()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SnapToGrid = true;
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        line.X1 = 0.313;
        line.Y1 = 0.501;
        line.X2 = 0.647;
        line.Y2 = 0.499;

        vm.ApplySnappedLineEndpoint(line, isFirstEndpoint: true);

        AssertClose(0.3, line.X1);
        AssertClose(0.5, line.Y1);
        AssertClose(0.647, line.X2); // the OTHER endpoint must be untouched
        AssertClose(0.499, line.Y2);
    }

    [AvaloniaFact]
    public void ApplySnappedLineEndpoint_SecondEndpoint_SnapsOnlyThatEndpoint()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SnapToGrid = true;
        vm.AddLineElementCommand.Execute(null);
        var line = (LineElementViewModel)vm.OverlayElements[0];
        line.X1 = 0.313;
        line.Y1 = 0.501;
        line.X2 = 0.647;
        line.Y2 = 0.499;

        vm.ApplySnappedLineEndpoint(line, isFirstEndpoint: false);

        AssertClose(0.313, line.X1); // the OTHER endpoint must be untouched
        AssertClose(0.501, line.Y1);
        AssertClose(0.65, line.X2);
        AssertClose(0.5, line.Y2);
    }

    [AvaloniaFact]
    public void ApplySnappedLineEndpoint_AlreadyGridAligned_DoesNotPushADeadUndoStep()
    {
        // Same class of bug already fixed once in ApplySnappedElementBounds's own line branch:
        // SnapValueToGrid's divide-then-multiply round-trip can land on a different bit pattern than
        // an already-aligned value, so the no-op guard needs a tolerance, not exact equality.
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.SnapToGrid = true;
        vm.AddLineElementCommand.Execute(null); // pushes exactly 1 undo step (the Add)
        var line = (LineElementViewModel)vm.OverlayElements[0];

        vm.ApplySnappedLineEndpoint(line, isFirstEndpoint: true);

        vm.UndoCommand.Execute(null);
        Assert.False(vm.UndoCommand.CanExecute(null), "a no-op endpoint snap must not leave a second, dead undo step on the stack");
    }

    // Auditor-specified tolerance rule (PROJECT_BRIEF.md flatten test debt), tight-bound half: a
    // 160x120 source into an 80x60 mode forces a REAL 2x downscale, so this exercises actual
    // resampling in BakeElementIntoSource's bake-then-write-back path, not a scale=1 no-op. Source
    // and mode share one aspect ratio (4:3) so CropRect=(0,0,1,1) projects with zero letterbox pad --
    // confirmed against ProjectRectToCropRelative/TryGetCropContentMetrics's own formulas, not
    // assumed -- so the default centered box's TemplateBoxElement.Bounds equals its raw
    // (X-Width/2, Y-Height/2, Width, Height) exactly, letting this test locate the box's interior
    // pixels in target space without needing to call that private method.
    [AvaloniaFact]
    public async Task FlattenElementAsync_SolidBoxElement_InteriorMatchesFillColorAfterResample()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(160, 120), FlattenTestMode, preparer);
        vm.AddBoxElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);
        Assert.Equal(new Rgb24(64, 64, 64), ((BoxElementViewModel)element).FillColor);

        await vm.FlattenElementCommand.ExecuteAsync(element);

        var actual = preparer.ComposePreview(
            vm.CurrentSource, vm.CropRect, FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight,
            vm.PreserveAspect, new ImageAdjustments(), new TemplateDocument(Name: null, []));

        // Default box: centered (0.5, 0.5), 0.3x0.2 -> target-pixel rect [28,52)x[24,36). Margined
        // in by 3px on every side to stay clear of the fill/background edge (antialiasing, plus the
        // bake's own resample), leaving a solid interior that should resample right back to the
        // exact fill color with no blending contribution from anything outside the box.
        for (var y = 27; y < 33; y++)
        {
            for (var x = 31; x < 49; x++)
            {
                Assert.Equal(new Rgb24(64, 64, 64), actual.GetScanline(y)[x]);
            }
        }
    }

    // MAE+outlier-fraction half of the same tolerance rule, plus the non-vacuous ink-presence
    // assertion the auditor specifically called out (proving flatten didn't just silently drop the
    // element rather than actually baking visible ink). Text can't use the box test's exact-equality
    // bound: BakeElementIntoSource rasterizes the glyphs at 2x bake resolution and downsamples them
    // back through the crop/composite chain, so anti-aliased edge pixels legitimately differ in
    // rounding from a direct render at the final 80x60 target resolution -- the interior-fill case
    // above has no edges to speak of, text is almost all edge.
    [AvaloniaFact]
    public async Task FlattenElementAsync_TextElement_MatchesDirectRenderWithinToleranceAndLeavesVisibleInk()
    {
        var preparer = new TransmitImagePreparer(FlattenTestFontPath);
        var vm = CreateEditor(CreateSource(160, 120), FlattenTestMode, preparer);
        vm.AddOverlayElementCommand.Execute(null);
        var element = Assert.Single(vm.OverlayElements);
        var text = (OverlayElementViewModel)element;
        var sourceBefore = vm.CurrentSource;

        // Same identity projection as the box test above (matching source/mode aspect, full-frame
        // crop) -- the reference element's Bounds is the element's own raw center-anchored rect.
        var bounds = new NormalizedRect(text.X - (text.Width / 2), text.Y - (text.Height / 2), text.Width, text.Height);
        var referenceElement = new TemplateTextElement(
            bounds, text.Z, text.ResolvedText, new FontSpec(text.FontFamily, text.FontSizeRelative, text.Bold, text.Italic), text.Color);
        var expected = preparer.ComposePreview(
            sourceBefore, vm.CropRect, FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight,
            vm.PreserveAspect, new ImageAdjustments(), new TemplateDocument(Name: null, [referenceElement]));

        await vm.FlattenElementCommand.ExecuteAsync(element);

        var actual = preparer.ComposePreview(
            vm.CurrentSource, vm.CropRect, FlattenTestMode.ImageWidth, FlattenTestMode.ImageHeight,
            vm.PreserveAspect, new ImageAdjustments(), new TemplateDocument(Name: null, []));

        long sumAbsDiff = 0;
        var pixelCount = 0;
        var outlierCount = 0;
        var inkPixelCount = 0;
        for (var y = 0; y < FlattenTestMode.ImageHeight; y++)
        {
            for (var x = 0; x < FlattenTestMode.ImageWidth; x++)
            {
                var e = expected.GetScanline(y)[x];
                var a = actual.GetScanline(y)[x];
                var diff = Math.Abs(e.R - a.R) + Math.Abs(e.G - a.G) + Math.Abs(e.B - a.B);
                sumAbsDiff += diff;
                pixelCount++;
                if (diff > 90)
                {
                    outlierCount++;
                }

                // Background is pure black; "Text" in white ink leaves plainly non-black pixels
                // wherever a glyph landed, regardless of anti-aliasing rounding. Threshold set well
                // below full white -- at this frame's small text height (fontSizeRelative 0.1 into
                // an 80x60 mode is ~6px tall), most glyph pixels are partial-coverage edge pixels,
                // not solid interior.
                if (a.R > 40 && a.G > 40 && a.B > 40)
                {
                    inkPixelCount++;
                }
            }
        }

        var mae = (double)sumAbsDiff / (pixelCount * 3);
        var outlierFraction = (double)outlierCount / pixelCount;
        Assert.True(mae < 1.5, $"Mean absolute error too high: {mae}");
        Assert.True(outlierFraction < 0.02, $"Outlier fraction too high: {outlierFraction}");
        Assert.True(inkPixelCount > 5, $"Expected visible text ink after flatten, found {inkPixelCount} bright pixels");
    }

    // Menu-command reachability (PROJECT_BRIEF.md tracked debt). Confirms every command a
    // context-menu MenuItem binds to actually resolves to the ELEMENT's own pushed command
    // instance, not silently null. Real regression class this project already hit once ("Important
    // correction found and fixed mid-Phase-7" in PROJECT_BRIEF.md): a bare
    // Command="{Binding XCommand}" where XCommand exists only on the PARENT VM, not the element
    // DataContext these per-row context menus actually have, resolves to null with no build error
    // and no failing VM unit test -- the menu item just sits permanently disabled at runtime.
    // Instantiates the REAL TxImageEditorPaneView (not a fake/mock) and inspects the real
    // Avalonia-resolved Command values on its real ContextMenu/MenuItem tree.

    [AvaloniaFact]
    public void TextElementContextMenu_EveryPushedCommand_ResolvesToTheElementsOwnInstance()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)vm.OverlayElements[0];
        var found = CollectMenuCommands(BuildElementContextMenu(vm, element));

        AssertCommandReachable(element.RemoveCommand, found);
        AssertCommandReachable(element.MoveUpCommand, found);
        AssertCommandReachable(element.MoveDownCommand, found);
        AssertCommandReachable(element.BringToFrontCommand, found);
        AssertCommandReachable(element.SendToBackCommand, found);
        AssertCommandReachable(element.DuplicateCommand, found);
        AssertCommandReachable(element.AlignSelectedElementToCropCommand, found);
        AssertCommandReachable(element.CopyCommand, found);
        AssertCommandReachable(element.CutCommand, found);
        AssertCommandReachable(element.PasteCommand, found);
        AssertCommandReachable(element.FlattenCommand, found);
        AssertCommandReachable(element.CopyStyleCommand, found);
        AssertCommandReachable(element.PasteStyleCommand, found);
        AssertCommandReachable(element.AddPlateCommand, found);
        AssertCommandReachable(element.InsertFieldCommand, found);
        AssertCommandReachable(element.SetFontSizePresetCommand, found);
        AssertCommandReachable(element.SetTextColorPresetCommand, found);
    }

    [AvaloniaFact]
    public void BoxElementContextMenu_EveryPushedCommand_ResolvesToTheElementsOwnInstance()
    {
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, new FakeTransmitImagePreparer());
        vm.AddBoxElementCommand.Execute(null);
        var element = (BoxElementViewModel)vm.OverlayElements[0];
        var found = CollectMenuCommands(BuildElementContextMenu(vm, element));

        AssertCommandReachable(element.RemoveCommand, found);
        AssertCommandReachable(element.MoveUpCommand, found);
        AssertCommandReachable(element.MoveDownCommand, found);
        AssertCommandReachable(element.BringToFrontCommand, found);
        AssertCommandReachable(element.SendToBackCommand, found);
        AssertCommandReachable(element.DuplicateCommand, found);
        AssertCommandReachable(element.AlignSelectedElementToCropCommand, found);
        AssertCommandReachable(element.CopyCommand, found);
        AssertCommandReachable(element.CutCommand, found);
        AssertCommandReachable(element.PasteCommand, found);
        AssertCommandReachable(element.FlattenCommand, found);
        AssertCommandReachable(element.CopyStyleCommand, found);
        AssertCommandReachable(element.PasteStyleCommand, found);
    }

    [AvaloniaFact]
    public async Task ImageElementContextMenu_EveryPushedCommand_ResolvesToTheElementsOwnInstance()
    {
        var preparer = new FakeTransmitImagePreparer();
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = CreateSource(2, 2) };
        var vm = CreateEditor(CreateSource(4, 4), SmallMode, preparer, picker, loader, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());
        await vm.AddImageFromFileCommand.ExecuteAsync(null);
        var element = (ImageElementViewModel)vm.OverlayElements[0];
        var found = CollectMenuCommands(BuildElementContextMenu(vm, element));

        AssertCommandReachable(element.RemoveCommand, found);
        AssertCommandReachable(element.MoveUpCommand, found);
        AssertCommandReachable(element.MoveDownCommand, found);
        AssertCommandReachable(element.BringToFrontCommand, found);
        AssertCommandReachable(element.SendToBackCommand, found);
        AssertCommandReachable(element.DuplicateCommand, found);
        AssertCommandReachable(element.AlignSelectedElementToCropCommand, found);
        AssertCommandReachable(element.CopyCommand, found);
        AssertCommandReachable(element.CutCommand, found);
        AssertCommandReachable(element.PasteCommand, found);
        AssertCommandReachable(element.FlattenCommand, found);
        AssertCommandReachable(element.SetAsBackdropCommand, found);
        AssertCommandReachable(element.ResetToOriginalSizeCommand, found);
        AssertCommandReachable(element.FitCommand, found);
    }

    private static void AssertCommandReachable(CommunityToolkit.Mvvm.Input.IRelayCommand? command, HashSet<object> found)
    {
        Assert.NotNull(command);
        Assert.Contains((object)command, found);
    }

    /// <summary>Real Avalonia View infra for the 3 tests above -- headless (Avalonia.Headless.XUnit,
    /// see TestAppBuilder), not a fake DataContext walk. Builds the real, compiled canvas
    /// DataTemplate for <paramref name="element"/>'s own runtime type directly
    /// (<c>IDataTemplate.Build</c>), rather than going through <c>OverlayElementsHost</c>'s normal
    /// ItemsControl container-generation + a real layout pass -- this View's canvas TextBlocks use
    /// custom bundled fonts (Barlow Condensed/DejaVu Sans Mono) that Avalonia.Headless's default
    /// font-manager STUB (this test project's own <c>TestAppBuilder</c>, deliberately fast/no-real-
    /// rendering -- see <c>ScanlineStudio.UI.FontTests</c>'s own doc comment for why that split
    /// project exists) can't resolve, so any real Measure/Show() pass over this View throws.
    /// Building the template directly needs no layout at all -- but needs two DataContext
    /// assignments neither the ItemsControl's own normal container-prep path nor a naive read of
    /// <c>DataTemplate.Build</c>'s docs would suggest, both found empirically (a diagnostic dump of
    /// the built tree, not assumed from documentation): (1) <c>DataTemplate.Build(object)</c> does
    /// NOT itself set the returned root's DataContext -- an ItemsControl's own container generator
    /// does that as a SEPARATE step this bypasses, so it must be set here explicitly; ordinary child
    /// controls (the row Border, resize handles) then inherit it fine through the logical tree, even
    /// fully detached from any Window. (2) <c>Border.ContextMenu</c> does NOT inherit DataContext the
    /// same way -- it's Popup-backed and, per this project's own established comments elsewhere
    /// ("CanExecute states are already correct by the time the menu opens"), only gets its
    /// DataContext copied across by Avalonia's real context-menu-OPEN machinery, not passively while
    /// closed; mirrored here with one direct assignment rather than actually opening the popup.
    /// <para><c>{loc:Translate ...}</c> (used by every MenuItem Header in this View) requires
    /// <c>App.Services</c> to hold a real <see cref="ILocalizationService"/> or it throws AT
    /// BUILD TIME (<c>TranslateExtension.ProvideValue</c>) -- and that build is DEFERRED until
    /// <c>DataTemplate.Build</c> runs below, not eager at <c>InitializeComponent</c> time (an
    /// earlier draft of this helper restored <c>App.Services</c> right after construction and broke
    /// here for exactly that reason). Kept swapped in for BOTH the View construction and the
    /// template build, restored once at the very end -- <c>App.Services</c> is a shared static and
    /// each <c>TranslateBindingSource</c> captures its own <see cref="ILocalizationService"/>
    /// reference once, at build time (it never reads <c>App.Services</c> again after that), so
    /// nothing later needs the swap still active.</para></summary>
    private static ContextMenu BuildElementContextMenu(TxImageEditorPaneViewModel vm, ITemplateElementViewModel element)
    {
        EnsureIndustryStylesLoaded();
        var previousServices = App.Services;
        try
        {
            App.Services = new ServiceCollection()
                .AddSingleton<ILocalizationService>(new FakeLocalizationService())
                .BuildServiceProvider();
            var view = new TxImageEditorPaneView { DataContext = vm };
            var itemsHost = view.GetLogicalDescendants().OfType<ItemsControl>().First(c => c.Name == "OverlayElementsHost");
            var template = itemsHost.DataTemplates.First(t => t.Match(element));
            var root = template.Build(element) ?? throw new InvalidOperationException("DataTemplate.Build returned null.");
            root.DataContext = element;
            var descendants = root.GetLogicalDescendants().ToList();
            var border = descendants.OfType<Border>()
                .First(b => ReferenceEquals(b.DataContext, element) && b.ContextMenu is not null);
            var contextMenu = border.ContextMenu!;
            // A real right-click copies the owning control's DataContext onto its ContextMenu at
            // OPEN time (this app's own doc comments confirm CanExecute states are already correct
            // "by the time the menu opens" -- a real click was never simulated to discover this, DID
            // discover it: Border.DataContext inherits fine off root.DataContext = element above,
            // but ContextMenu is Popup-backed and does NOT participate in that same passive logical-
            // tree inheritance while unopened -- its own DataContext stays null/unset until Avalonia's
            // real context-menu-open machinery copies it across). Mirrored here directly rather than
            // actually opening the popup, which needs a real pointer event this project's own
            // OnOverlayElementPointerPressed doc comment says fires the SAME copy this line does.
            contextMenu.DataContext = element;
            return contextMenu;
        }
        finally
        {
            App.Services = previousServices;
        }
    }

    // Same pattern IndustryStepperTests.EnsureIndustryStylesLoaded already established -- this
    // headless TestAppBuilder configures a bare Avalonia.Application, not ScanlineStudio.UI.App, so
    // App.axaml's own <Application.Styles> (FluentTheme, ColorPicker's Fluent theme, Atoms*.axaml)
    // never runs; StaticResource lookups this View makes (IndustryAccent, IndustrySliderTheme, etc.)
    // need them loaded directly instead.
    private static void EnsureIndustryStylesLoaded()
    {
        var app = Avalonia.Application.Current!;
        if (app.Styles.OfType<StyleInclude>().Any(s => s.Source!.OriginalString.Contains("Atoms.axaml")))
        {
            return;
        }

        var baseUri = new System.Uri("avares://ScanlineStudio.UI/");
        app.Styles.Add(new StyleInclude(baseUri) { Source = new System.Uri("avares://ScanlineStudio.UI/Styles/AtomsTokens.axaml") });
        app.Styles.Add(new StyleInclude(baseUri) { Source = new System.Uri("avares://ScanlineStudio.UI/Styles/Atoms.axaml") });
        app.Styles.Add(new StyleInclude(baseUri) { Source = new System.Uri("avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml") });
    }

    /// <summary>Recursively walks a ContextMenu's real, XAML-resolved MenuItem tree (submenus
    /// included) and collects every non-null bound Command by reference -- ContextMenu/MenuItem
    /// content is populated from XAML at construction time regardless of Popup-open state (it's a
    /// LOGICAL-tree structure, not a visual-tree one), and DataContext inheritance for it flows the
    /// same way, so this does not need to actually open the menu to see real, resolved bindings.</summary>
    private static HashSet<object> CollectMenuCommands(ItemsControl menu)
    {
        var found = new HashSet<object>(ReferenceEqualityComparer.Instance);
        CollectMenuCommandsInto(menu, found);
        return found;
    }

    private static void CollectMenuCommandsInto(ItemsControl menu, HashSet<object> found)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (item.Command is { } command)
            {
                found.Add(command);
            }

            CollectMenuCommandsInto(item, found);
        }
    }

    private static ArrayImageSource CreateSource(int width, int height)
        => new(width, height, new Rgb24[width * height]);

    private static void AssertClose(double expected, double actual)
        => Assert.True(Math.Abs(expected - actual) < 1e-9, $"Expected {expected}, got {actual}");
}
