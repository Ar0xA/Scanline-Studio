using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>
/// T0-13 (production_audit.md): <see cref="MainViewModel"/> had zero test coverage --
/// specifically <see cref="MainViewModel.SaveOrApplyCommand"/>'s Ctrl+S dispatch (whose own doc
/// comment records a previously shipped bug: Ctrl+S used to always save the RX frame, even with a
/// TX editor open), <see cref="MainViewModel.OnSelectedTabIndexChanged"/>'s Gallery-tab reconcile
/// trigger, and the <c>EditorOpened</c>/<c>EditorClosed</c> -&gt; <see cref="MainViewModel.ActiveEditor"/>
/// wiring.
///
/// No production code change was needed -- <see cref="MainViewModel"/>'s own constructor comment
/// documents that with fakes whose async calls resolve already-completed <see cref="Task"/>s (this
/// project's standard test convention), the constructor's own fire-and-forget auto-open-blank-
/// editor chain runs to completion INLINE before the constructor call returns; the first test below
/// proves this empirically rather than assuming it. Confirmed via a full trace (plan-review):
/// <c>OpenBlankEditorSafelyAsync</c> -&gt; <c>TxControlsPaneViewModel.OpenBlankEditorAsync</c> -&gt;
/// <c>OpenEditorWithLoadedSourceAsync</c> -&gt; <c>EditorOpened?.Invoke(editor)</c> has no genuine
/// yield point given synchronous fakes -- PROVIDED <see cref="FakeSstvSessionService.AvailableModes"/>
/// is seeded (an empty default makes <c>TxControlsPaneViewModel.SelectedMode</c> null, which
/// silently early-returns the whole chain before <c>EditorOpened</c> ever fires).
/// </summary>
public sealed class MainViewModelTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static (MainViewModel ViewModel, FakeSstvSessionService SstvSession, FakeReceivedImageBuffer ReceivedImage, FakeReceiveHistoryStore ReceiveHistoryStore, FakeFilePickerService FilePicker, FakeUrlLauncher UrlLauncher)
        CreateMainViewModel()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receiveHistoryStore = new FakeReceiveHistoryStore();
        var filePicker = new FakeFilePickerService();
        var settingsStore = new FakeSettingsStore();

        var rxImage = new RxImagePaneViewModel(
            sstvSession,
            new FakeLocalizationService(),
            new FakeLogbookSessionService(),
            filePicker,
            receiveHistoryStore,
            settingsStore,
            NullLogger<RxImagePaneViewModel>.Instance);

        var rxHistory = new RxHistoryPaneViewModel(
            receiveHistoryStore,
            new FakeLocalizationService(),
            NullLogger<RxHistoryPaneViewModel>.Instance,
            new FakeLogbookSessionService(),
            NullLogger<QsoLinkWindowViewModel>.Instance,
            new FakeReceivedFrameExporter(),
            new FakeFilePickerService(),
            new FakeSettingsStore(),
            new FakeUrlLauncher(),
            new FakeClipboardImageService(),
            NullLogger<ImageViewerWindowViewModel>.Instance,
            new FakeRxAudioAutoSaver(),
            new FakeRxStationIdAttacher());

        // AvailableModes MUST be seeded -- an empty default leaves TxControlsPaneViewModel's own
        // SelectedMode null, which silently early-returns OpenBlankEditorAsync before it ever
        // fires EditorOpened (plan-review finding).
        // Code-review nit: TxControls gets its OWN FakeReceivedImageBuffer/FakeReceiveHistoryStore
        // below, deliberately NOT shared with the ones returned to callers (RxImage's/the tuple's
        // own receivedImage/receiveHistoryStore) -- none of this file's tests assert on an
        // editor-driven save or a TX-side history write, so this is harmless today, but a future
        // test adding one must share these two args instead of assuming they're already wired.
        var txControls = new TxControlsPaneViewModel(
            sstvSession,
            new FakeImageFileLoader(),
            new FakeStockImageLibrary(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService(),
            new FakeLocalizationService(),
            settingsStore,
            new FakeRadioSessionService(),
            new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance,
            NullLogger<TxImageEditorPaneViewModel>.Instance,
            new FakeReceivedImageBuffer(),
            new FakeReceiveHistoryStore(),
            new FakeTemplateStore(),
            new FakeImageSourceWriter(),
            NullLogger<ReadyRackViewModel>.Instance);

        var logbook = new LogbookPaneViewModel(
            new FakeLogbookSessionService(),
            new FakeFilePickerService(),
            new FakeSstvSessionService { AvailableModes = [TestMode] },
            new FakeLocalizationService(),
            new FakeReceiveHistoryStore(),
            NullLogger<LogbookPaneViewModel>.Instance);

        var waterfall = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);
        var decoderTrace = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());
        var urlLauncher = new FakeUrlLauncher();

        var viewModel = new MainViewModel(
            waterfall,
            rxImage,
            rxHistory,
            txControls,
            logbook,
            decoderTrace,
            new FakeRadioSessionService(),
            sstvSession,
            new FakeLocalizationService(),
            new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance),
            settingsStore,
            urlLauncher,
            new FakeAppearanceSettingsService(),
            new FakeServiceProvider(),
            NullLogger<MainViewModel>.Instance,
            NullLogger<RadioStatusViewModel>.Instance);

        return (viewModel, sstvSession, (FakeReceivedImageBuffer)sstvSession.ReceivedImage, receiveHistoryStore, filePicker, urlLauncher);
    }

    [AvaloniaFact]
    public void Construction_AutoOpensABlankEditor_ActiveEditorIsSet()
    {
        // The empirical check this whole file's design depends on -- see the class doc comment.
        // No await, no Dispatcher pump: if the auto-open chain genuinely completed synchronously,
        // ActiveEditor is already populated the instant the constructor call returns.
        var (viewModel, _, _, _, _, _) = CreateMainViewModel();

        Assert.NotNull(viewModel.ActiveEditor);
    }

    [AvaloniaFact]
    public void Construction_WiresTheCrossVmTxControlsReference_OnRadioStatus()
    {
        // yoniq-auditor finding (2026-09-15): RadioHeaderView.axaml's "Stop TX" button binds
        // Command="{Binding TxControls.StopTransmitCommand}" -- if the constructor's own
        // `RadioStatus.TxControls = txControls;` line is ever reordered/dropped, that binding
        // silently resolves to null and the button becomes a no-op with no build error, no
        // exception, no other failing test. This is the one-line regression guard for that.
        var (viewModel, _, _, _, _, _) = CreateMainViewModel();

        Assert.NotNull(viewModel.RadioStatus.TxControls);
        Assert.Same(viewModel.TxControls, viewModel.RadioStatus.TxControls);
    }

    [AvaloniaFact]
    public void SaveOrApply_TransmitTabWithActiveEditor_RunsEditorApply_NotSaveFrame()
    {
        var (viewModel, _, _, _, _, _) = CreateMainViewModel();
        var editor = viewModel.ActiveEditor!;
        var applyInvoked = false;
        editor.Applied += _ => applyInvoked = true;
        viewModel.SelectedTabIndex = MainViewModel.TransmitTabIndex;

        viewModel.SaveOrApplyCommand.Execute(null);

        Assert.True(applyInvoked);
        // 2026-09-19 user request: Apply no longer closes the editor -- OnEditorApplied doesn't set
        // IsEditorOpen = false or null _currentEditor anymore, so ActiveEditor stays pointed at the
        // SAME still-open editor instance.
        Assert.Same(editor, viewModel.ActiveEditor);
    }

    [AvaloniaFact]
    public void SaveOrApply_ReceiveTabWithActiveEditor_RunsSaveFrame_NotEditorApply()
    {
        // Code-review finding: closes a surviving mutant -- without this test, dropping
        // SaveOrApply's own `SelectedTabIndex == TransmitTabIndex` condition (leaving only the
        // ActiveEditor check) still passed every other test here. This is the actual T1-3
        // regression the audit is about: Ctrl+S on the Receive tab with an editor still open (e.g.
        // left over from a prior Transmit-tab visit) must save the RX frame, not silently Apply
        // the editor instead.
        var (viewModel, sstvSession, receivedImage, _, filePicker, _) = CreateMainViewModel();
        var editor = viewModel.ActiveEditor!;
        var applyInvoked = false;
        editor.Applied += _ => applyInvoked = true;
        filePicker.SaveImagePathToReturn = ("/tmp/chosen.png", ImageExportFormat.Png);
        sstvSession.RaiseModeDetected(TestMode);
        Dispatcher.UIThread.RunJobs();
        viewModel.SelectedTabIndex = 0; // Receive tab

        viewModel.SaveOrApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(applyInvoked);
        Assert.Equal(["/tmp/chosen.png"], receivedImage.SavedPaths);
    }

    [AvaloniaFact]
    public void SaveOrApply_TransmitTabNoActiveEditor_FallsBackToSaveFrame()
    {
        // The original bug's exact fallthrough: Ctrl+S with no editor open must still save the RX
        // frame.
        var (viewModel, sstvSession, receivedImage, _, filePicker, _) = CreateMainViewModel();
        filePicker.SaveImagePathToReturn = ("/tmp/chosen.png", ImageExportFormat.Png);
        // Setting ActiveEditor directly desyncs from TxControls' own internal IsEditorOpen/
        // _currentEditor state -- fine for this pure dispatch-logic test (SaveOrApply's own
        // branching), not a scenario production code itself would reach.
        viewModel.ActiveEditor = null;
        // ModeDetected is Dispatcher.UIThread.Post-ed, not synchronous -- RunJobs() required
        // before CanSaveFrame() sees a non-null DetectedMode.
        sstvSession.RaiseModeDetected(TestMode);
        Dispatcher.UIThread.RunJobs();
        viewModel.SelectedTabIndex = MainViewModel.TransmitTabIndex;

        viewModel.SaveOrApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["/tmp/chosen.png"], receivedImage.SavedPaths);
    }

    [AvaloniaFact]
    public void SaveOrApply_NoActiveEditor_SaveFrameCanExecuteFalse_DoesNothing()
    {
        // Only covers the SaveFrameCommand-false half of "either branch with CanExecute == false"
        // -- ApplyCommand has no CanExecute guard today (TxImageEditorPaneViewModel's own Apply is
        // a bare [RelayCommand]), so that half is not reachable, not omitted by oversight.
        var (viewModel, _, receivedImage, _, filePicker, _) = CreateMainViewModel();
        filePicker.SaveImagePathToReturn = ("/tmp/chosen.png", ImageExportFormat.Png);
        viewModel.ActiveEditor = null;
        viewModel.SelectedTabIndex = MainViewModel.TransmitTabIndex;
        // DetectedMode deliberately left null (default state, no ModeDetected raised) -- SaveFrameCommand.CanExecute stays false.

        var exception = Record.Exception(() => viewModel.SaveOrApplyCommand.Execute(null));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(exception);
        Assert.Empty(receivedImage.SavedPaths);
    }

    [AvaloniaFact]
    public void OnSelectedTabIndexChanged_GallerySelectedTwice_ReconcilesExactlyOnce()
    {
        // Scoped to the tab-index wiring specifically -- ReconcileDiskThenRefreshAsync's own
        // once-per-session guard is already covered directly by PaneViewModelTests.cs.
        var (viewModel, _, _, receiveHistoryStore, _, _) = CreateMainViewModel();

        // Code-review finding: selecting a NON-Gallery tab first and asserting the count stays 0
        // closes a surviving mutant -- without this, dropping OnSelectedTabIndexChanged's own
        // `if (value == GalleryTabIndex)` guard entirely (reconcile on every tab change) still
        // passed the rest of this test, since the first real assertion was only ever reached via
        // an actual Gallery selection.
        viewModel.SelectedTabIndex = MainViewModel.TransmitTabIndex;
        Assert.Equal(0, receiveHistoryStore.ReconcileCallCount);

        viewModel.SelectedTabIndex = MainViewModel.GalleryTabIndex;
        Assert.Equal(1, receiveHistoryStore.ReconcileCallCount);

        // Navigate away and back -- a genuine repeat SELECTION, not just re-assigning the same
        // value (which the [ObservableProperty]-generated setter would no-op on before the
        // partial change handler ever ran again, testing the wrong thing).
        viewModel.SelectedTabIndex = MainViewModel.LogbookTabIndex;
        viewModel.SelectedTabIndex = MainViewModel.GalleryTabIndex;
        Assert.Equal(1, receiveHistoryStore.ReconcileCallCount);
    }

    [AvaloniaFact]
    public void OpenWebsiteCommand_OpensTheAppWebsite()
    {
        var (viewModel, _, _, _, _, urlLauncher) = CreateMainViewModel();

        viewModel.OpenWebsiteCommand.Execute(null);

        Assert.Equal(["https://scanlinestudio.app"], urlLauncher.OpenedUrls);
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
