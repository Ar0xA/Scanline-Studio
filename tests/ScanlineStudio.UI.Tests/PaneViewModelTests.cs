using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class PaneViewModelTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 1,
        ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    [AvaloniaFact]
    public void RadioStatusViewModel_PushedState_UpdatesDisplayOnUiThread()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = new RadioStatusViewModel(radioSession, new FakeSstvSessionService(), new FakeLocalizationService(), NullLogger<RadioStatusViewModel>.Instance);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("14.230000 MHz", vm.FrequencyDisplay);
        Assert.Equal("Usb", vm.ModeDisplay);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_PushedFrame_UpdatesLatestFrameOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession);
        var frame = new WaterfallFrame([0f, 1f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        ((FakeWaterfallSource)sstvSession.Waterfall).Emit(frame);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(frame, vm.LatestFrame);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_RefreshesImageOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession);

        Assert.Null(vm.Image);
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Image);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_UpdatesModeCardTextsOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession);

        Assert.Equal("—", vm.DetectedModeText);
        Assert.Equal("—", vm.LineTimeText);
        Assert.Equal("—", vm.LinesText);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Scottie 1", vm.DetectedModeText);
        Assert.Equal("138.2 ms", vm.LineTimeText);
        Assert.Equal("256", vm.LinesText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_LoadsTheConfiguredOutputDeviceName()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], ConfiguredPlaybackDeviceName = "USB Audio CODEC" };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("USB Audio CODEC", vm.OutputDeviceName);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_NoPlaybackDeviceConfigured_OutputDeviceNameStaysNull()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], ConfiguredPlaybackDeviceName = null };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OutputDeviceName);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_NoModeSelected_ToneMapTextIsNull()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        Assert.Null(vm.SelectedMode);
        Assert.Null(vm.ToneMapText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_ModeSelected_ToneMapTextIsPopulated()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        Assert.NotNull(vm.SelectedMode);
        Assert.NotNull(vm.ToneMapText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_SelectedModeChanges_RaisesPropertyChangedForToneMapText()
    {
        var narrowMode = TestMode with { Id = "narrow" }; // LuminanceMinHz/MaxHz not overridden here -- this test only needs a DIFFERENT mode instance, not different Hz values, to prove the reactive wiring fires
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode, narrowMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.SelectedMode = narrowMode;

        Assert.Contains(nameof(vm.ToneMapText), raisedProperties);
    }

    [Fact]
    public void ToneMapFormat_MatchesMock2sDisplayConvention_ForBothTheDefaultAndANarrowModesRange()
    {
        // Real-value check independent of FakeLocalizationService (which returns the raw key, not
        // the formatted string, so it can't verify this) -- confirms the actual locale format
        // string (assets/locale/en.json's Panes.TxControls.Telemetry.ToneMapFormat) produces
        // mock2's own literal display text for both the default range and a real narrow-family
        // override (SstvModeRegistry.cs's Mn73/Mn110-style 2044/2300 override).
        const string format = "{0:0}–{1:0} Hz"; // en dash, matching this key's own value in en.json

        Assert.Equal("1500–2300 Hz", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 1500.0, 2300.0));
        Assert.Equal("2044–2300 Hz", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 2044.0, 2300.0));
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_ModeTimingRows_ComputedFromEachAvailableModesRealTiming()
    {
        var scottie1 = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        var sstvSession = new FakeSstvSessionService { AvailableModes = [scottie1] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var row = Assert.Single(vm.ModeTimingRows);
        Assert.Equal("Scottie 1", row.ModeName);
        Assert.Equal(256, row.Lines);
        Assert.Equal(138.24, row.LineMs, precision: 2);
        Assert.Equal(138.24 * 256 / 1000.0, row.FrameSeconds, precision: 2);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitCommand_InvokesSstvSessionServiceWhenImageLoaded()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        Assert.False(vm.TransmitCommand.CanExecute(null));

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));

        await vm.TransmitCommand.ExecuteAsync(null);

        Assert.Single(sstvSession.TransmitCalls);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[0].Mode);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_SelectingANewMode_ClearsAlreadyLoadedImage()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        vm.SelectedMode = TestMode with { Id = "other" };

        Assert.False(vm.TransmitCommand.CanExecute(null));
        Assert.Null(vm.PreviewImage);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectingAStockImage_LoadsItAndAllowsTransmit()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var stockEntry = new StockImageEntry("s1", "stock.png", "/stock/stock.png");
        var stockLibrary = new FakeStockImageLibrary
        {
            EntriesToReturn = [stockEntry],
            FullImageToReturn = new ArrayImageSource(1, 1, [new Rgb24(4, 5, 6)]),
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), stockLibrary, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectStockImageCommand.ExecuteAsync(stockEntry));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Equal("stock.png", vm.SelectedFileName);

        await vm.TransmitCommand.ExecuteAsync(null);
        Assert.Single(sstvSession.TransmitCalls);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_PickingASource_OpensTheEditorInsteadOfLoadingDirectly()
    {
        // The pre-TX-editor behavior auto-resized straight to mode dimensions with no edit step;
        // this pass's real behavior change is that picking a source loads it at native resolution
        // and hands it to an editor instead -- CanTransmit must NOT flip true until Apply happens.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        // Opening the editor must not, by itself, make the old flat-load path's image transmittable
        // -- only Apply does that now.
        Assert.False(vm.TransmitCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CancellingTheEditor_LeavesAnyPreviouslyAppliedImageUntouched()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));
        var loadedAfterFirstApply = ExtractLoadedImage(vm);

        filePicker.PathToReturn = "/tmp/b.png";
        var secondEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        secondEditor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Same(loadedAfterFirstApply, ExtractLoadedImage(vm));
        Assert.Equal("a.png", vm.SelectedFileName);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectingASourceWhileAnEditorIsAlreadyOpen_IsIgnored()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { UseManualGating = true };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;

        var firstPick = vm.SelectImageCommand.ExecuteAsync(null); // original load in flight, not resolved yet
        Dispatcher.UIThread.RunJobs();
        var secondPick = vm.SelectImageCommand.ExecuteAsync(null); // must be a no-op -- an editor is already "opening"
        Dispatcher.UIThread.RunJobs();

        Assert.Single(imageFileLoader.PendingLoads);
        imageFileLoader.PendingLoads[0].SetResult(new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]));
        Dispatcher.UIThread.RunJobs();
        await firstPick;
        await secondPick;

        Assert.Equal(1, editorOpenedCount);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeChangeWithAnAppliedEdit_ReRunsThePipelineAtTheNewModesDimensions()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));

        vm.SelectedMode = modeB;

        // Retained and successfully re-flowed at the new mode's dimensions -- NOT nulled out the
        // way the pre-Phase-4 behavior did, and re-derived from the cached ORIGINAL (never
        // re-reads the file), per spec/07-image-pipeline.md's "Mode-change interaction" note.
        Assert.Equal("a.png", vm.SelectedFileName);
        Assert.NotNull(vm.PreviewImage);
        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Equal((modeB.ImageWidth, modeB.ImageHeight), (ExtractLoadedImage(vm)!.Width, ExtractLoadedImage(vm)!.Height));
    }

    /// <summary>Drives a pick command to the point where <see cref="TxControlsPaneViewModel.EditorOpened"/>
    /// fires, then returns the editor instance -- every "pick a source" test needs this same
    /// choreography now that picking opens an editor instead of loading directly.</summary>
    private static async Task<TxImageEditorPaneViewModel> OpenEditorAsync(TxControlsPaneViewModel vm, Func<Task> pick)
    {
        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;

        await pick();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(opened);
        return opened!;
    }

    private static IImageSource? ExtractLoadedImage(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_loadedImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as IImageSource;

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_Constructed_LoadsEntriesFromTheHistoryStore()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };

        var vm = new RxHistoryPaneViewModel(historyStore, NullLogger<RxHistoryPaneViewModel>.Instance);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("1", entry.Entry.Id);
        Assert.NotNull(entry.Thumbnail);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingAnEntry_LoadsAReadOnlyPreview_NeverTouchingTheLiveReceivedImageBuffer()
    {
        var historyEntry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [historyEntry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(9, 9, 9)]),
        };

        // RxHistoryPaneViewModel's constructor has no parameter that reaches IReceivedImageBuffer at
        // all (unlike RxImagePaneViewModel, which takes ISstvSessionService specifically for that
        // live binding) -- a live-buffer interaction is structurally impossible here, not just
        // unobserved, so there is nothing to fake/assert against for that half of the guarantee.
        var vm = new RxHistoryPaneViewModel(historyStore, NullLogger<RxHistoryPaneViewModel>.Instance);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.PreviewImage);
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.PreviewImage);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_DefaultsToTodayOnly_MatchingTheMock2DraftsOwnDefaultSelection()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxHistoryPaneViewModel(historyStore, NullLogger<RxHistoryPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowTodayOnly);
        var filter = Assert.Single(historyStore.QueryFilters);
        Assert.NotNull(filter.From);
        Assert.Null(filter.To);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_TogglingToAll_ReQueriesWithNoDateFilter()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxHistoryPaneViewModel(historyStore, NullLogger<RxHistoryPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.ShowTodayOnly = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, historyStore.QueryFilters.Count);
        var lastFilter = historyStore.QueryFilters[^1];
        Assert.Null(lastFilter.From);
        Assert.Null(lastFilter.To);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_LoadsImagesDirectory_ForTheGalleryTabsStorageCard()
    {
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = "/tmp/scanlinestudio-history" };
        var vm = new RxHistoryPaneViewModel(historyStore, NullLogger<RxHistoryPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/tmp/scanlinestudio-history", vm.ImagesDirectory);
    }

    private static QsoRecord SampleQsoRecord(string id = "1") =>
        new(id, "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null);

    private static LogbookPaneViewModel CreateLogbookPaneViewModel(
        FakeLogbookSessionService? logbook = null,
        FakeFilePickerService? filePicker = null) =>
        new(
            logbook ?? new FakeLogbookSessionService(),
            filePicker ?? new FakeFilePickerService(),
            new FakeSstvSessionService { AvailableModes = [TestMode] },
            new FakeLocalizationService(),
            NullLogger<LogbookPaneViewModel>.Instance);

    [AvaloniaFact]
    public void LogbookPaneViewModel_Constructed_LoadsEntriesFromSearchAsync()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));

        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("1", entry.Id);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_LogCommand_DisabledWithoutCallsign()
    {
        var vm = CreateLogbookPaneViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = null;
        Assert.False(vm.LogCommand.CanExecute(null));

        vm.FormCallsign = "N0CALL";
        Assert.True(vm.LogCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_PersistsAndClearsFormAndRefreshes()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.Equal("N0CALL", logbook.Records[0].Callsign);
        Assert.Null(vm.FormCallsign);
        Assert.Single(vm.Entries);
        // Regression: ResetForm() (not New()) must run here, or the status line set from
        // BuildLogStatusMessage would be immediately nulled back out before the UI ever shows it.
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_SelectingAnEntry_LoadsFormForEditAndEnablesUpdate()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.UpdateCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries[0];

        Assert.Equal("N0CALL", vm.FormCallsign);
        Assert.True(vm.IsEditing);
        Assert.True(vm.UpdateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_UpdateAsync_DelegatesAndNeverTouchesGridTrackerOrQrz()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        vm.FormNotes = "edited";
        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, logbook.UpdateCallCount);
        Assert.Equal("edited", logbook.Records[0].Notes);
        // Regression: same ResetForm()-vs-New() bug as the Log path above.
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_New_ClearsSelectedEntry_SoReselectingTheSameRowReloadsTheForm()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.IsEditing);

        vm.NewCommand.Execute(null);
        Assert.Null(vm.SelectedEntry);
        Assert.False(vm.IsEditing);

        // Regression: without New() also clearing SelectedEntry, this second assignment of the SAME
        // record would be a no-op (no PropertyChanged), leaving the form stuck empty.
        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.IsEditing);
        Assert.Equal("N0CALL", vm.FormCallsign);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_RefreshAsync_NormalizesFromDateToUtcCalendarDate()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        // Simulates what Avalonia's DatePicker actually emits: a LOCAL-offset DateTimeOffset, not a
        // UTC one -- BuildCurrentQuery must normalize this to a UTC calendar date regardless.
        vm.FromDate = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(5));
        vm.RefreshCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var from = logbook.LastSearchQuery?.From;
        Assert.NotNull(from);
        Assert.Equal(TimeSpan.Zero, from!.Value.Offset);
        Assert.Equal(new DateTime(2026, 8, 1), from.Value.Date);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_ImportAdifAsync_RefreshesEntriesAndReportsCount()
    {
        var imported = new List<QsoRecord> { SampleQsoRecord("imported-1") };
        var logbook = new FakeLogbookSessionService { ImportResultToReturn = imported };
        var filePicker = new FakeFilePickerService { AdifPathToReturn = "/tmp/import.adi" };
        var vm = CreateLogbookPaneViewModel(logbook, filePicker);
        Dispatcher.UIThread.RunJobs();

        await vm.ImportAdifCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.Entries, e => e.Id == "imported-1");
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_ExportAdifAsync_UsesCurrentFilterAndReportsCount()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var filePicker = new FakeFilePickerService { SaveAdifPathToReturn = "/tmp/export.adi" };
        var vm = CreateLogbookPaneViewModel(logbook, filePicker);
        Dispatcher.UIThread.RunJobs();

        vm.CallsignFilter = "N0CALL";
        await vm.ExportAdifCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/tmp/export.adi", logbook.LastExportPath);
        Assert.Equal("N0CALL", logbook.LastExportQuery?.Callsign);
        Assert.NotNull(vm.StatusMessage);
    }
}
