using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class TxControlsFavoritesAndAutoFollowTests
{
    private static readonly SstvModeDefinition ModeA = new(
        Id: "robot36", DisplayName: "Robot 36", VisCode: 8, ImageWidth: 1, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly SstvModeDefinition ModeB = new(
        Id: "martin-m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 1, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    // Deliberately a DIFFERENT ImageWidth than ModeA/ModeB (both 1) -- the width-mismatch guard
    // tests below need a mode whose own picture width genuinely differs from whatever is loaded.
    private static readonly SstvModeDefinition ModeWiderWidth = new(
        Id: "wider", DisplayName: "Wider", VisCode: 99, ImageWidth: 2, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static TxControlsPaneViewModel CreateViewModel(FakeSettingsStore settingsStore, FakeSstvSessionService? sstvSession = null)
        => new(
            sstvSession ?? new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] },
            new FakeImageFileLoader(),
            new FakeStockImageLibrary(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService(),
            new FakeLocalizationService(),
            settingsStore,
            new FakeRadioSessionService(),
            new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance,
            NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

    [AvaloniaFact]
    public void Constructor_LoadsFavoritesAndAutoFollowFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { FavoriteModeIds = ["martin-m1"], AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };

        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.AutoFollowRxMode);
        Assert.Equal(2, vm.FavoriteModeOptions.Count);
        Assert.False(vm.FavoriteModeOptions.Single(o => o.Mode.Id == "robot36").IsSelected);
        Assert.True(vm.FavoriteModeOptions.Single(o => o.Mode.Id == "martin-m1").IsSelected);
        var favorite = Assert.Single(vm.FavoriteModes);
        Assert.Equal("martin-m1", favorite.Mode.Id);
    }

    [AvaloniaFact]
    public void TogglingAFavoriteModeOption_UpdatesFavoriteModesAndPersists()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.FavoriteModes);

        vm.FavoriteModeOptions.Single(o => o.Mode.Id == "robot36").IsSelected = true;
        Dispatcher.UIThread.RunJobs();

        var favorite = Assert.Single(vm.FavoriteModes);
        Assert.Equal("robot36", favorite.Mode.Id);

        var persisted = settingsStore.Settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
        Assert.Equal(["robot36"], persisted?.FavoriteModeIds);
    }

    [AvaloniaFact]
    public void SelectFavoriteModeCommand_SetsSelectedMode()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { FavoriteModeIds = ["martin-m1"] },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();
        var favorite = Assert.Single(vm.FavoriteModes);

        favorite.SelectCommand.Execute(favorite.Mode);

        Assert.Equal("martin-m1", vm.SelectedMode?.Id);
    }

    [AvaloniaFact]
    public void ModeDetected_WithAutoFollowOn_UpdatesSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        sstvSession.RaiseModeDetected(ModeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("martin-m1", vm.SelectedMode?.Id);
    }

    [AvaloniaFact]
    public void ModeDetected_WithAutoFollowOff_DoesNotUpdateSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var vm = CreateViewModel(new FakeSettingsStore(), sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;
        Assert.False(vm.AutoFollowRxMode);

        sstvSession.RaiseModeDetected(ModeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
    }

    // Legacy TrackTxMode (Main.cpp:4907-4915) guard 1/2: don't switch TX mode while actively
    // transmitting -- see OnModeDetected's own doc comment for the full reasoning.
    [AvaloniaFact]
    public void ModeDetected_WhileTransmitting_DoesNotUpdateSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;
        vm.IsTransmitting = true;

        sstvSession.RaiseModeDetected(ModeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
    }

    // Legacy TrackTxMode guard 2/2: only follow when the detected mode's own picture width matches
    // the currently loaded TX image's width -- OnSelectedModeChanged unconditionally reflows the
    // loaded image to whatever mode is selected, so without this guard auto-follow would silently
    // re-crop an already-framed TX image. See OnModeDetected's own doc comment for the full
    // reasoning and the legacy citation.
    [AvaloniaFact]
    public async Task ModeDetected_WithMismatchedLoadedImageWidth_DoesNotUpdateSelectedModeOrReflowTheLoadedImage()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeWiderWidth] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(opened);
        opened!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, ExtractLoadedImage(vm)?.Width);

        sstvSession.RaiseModeDetected(ModeWiderWidth); // width 2, mismatched against the loaded width-1 image
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("robot36", vm.SelectedMode?.Id);
        Assert.Equal(1, ExtractLoadedImage(vm)?.Width); // unchanged, not silently reflowed to width 2
    }

    // Auditor-flagged gap: with NOTHING loaded yet, the width guard must not block auto-follow at
    // all -- there is nothing to protect from a silent reflow. This is a deliberate, documented
    // deviation from legacy (legacy's pBitmapTX always exists and is always sized to the current TX
    // mode, so legacy's own width check is always live; this port's _loadedImage genuinely can be
    // null, e.g. a fresh session with nothing picked for TX yet).
    [AvaloniaFact]
    public void ModeDetected_WithNothingLoaded_StillUpdatesSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeWiderWidth] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = CreateViewModel(settingsStore, sstvSession);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;
        Assert.Null(ExtractLoadedImage(vm));

        sstvSession.RaiseModeDetected(ModeWiderWidth); // width 2, but nothing loaded to mismatch against
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("wider", vm.SelectedMode?.Id);
    }

    // Positive control for the width guard above: a MATCHING width must still auto-follow normally
    // -- proves the guard discriminates on width, not just "any image loaded blocks auto-follow."
    [AvaloniaFact]
    public async Task ModeDetected_WithMatchingLoadedImageWidth_UpdatesSelectedMode()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [ModeA, ModeB] };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = ModeA;

        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(opened);
        opened!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        sstvSession.RaiseModeDetected(ModeB); // width 1, same as ModeA and the loaded image
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("martin-m1", vm.SelectedMode?.Id);
    }

    private static IImageSource? ExtractLoadedImage(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_loadedImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as IImageSource;

    [AvaloniaFact]
    public void TogglingAutoFollowRxMode_LeavesAnUnrelatedSiblingSectionUntouched()
    {
        // TxPaneUiSettings only owns FavoriteModeIds/AutoFollowRxMode today. This test's real target
        // is the `settings.WithSection(...)` mechanism itself: saving one section must never disturb
        // a completely different section already present in the same AppSettings -- AudioDeviceSettings
        // (an arbitrary unrelated section) with a distinctive SampleRate sentinel stands in for that.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ScanlineStudio.Core.Audio.AudioDeviceSettings.SectionKey,
                new ScanlineStudio.Core.Audio.AudioDeviceSettings { SampleRate = 77000 },
                ScanlineStudio.Core.Audio.AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var vm = CreateViewModel(settingsStore);
        Dispatcher.UIThread.RunJobs();

        vm.AutoFollowRxMode = true;
        Dispatcher.UIThread.RunJobs();

        var audio = settingsStore.Settings.GetSection(ScanlineStudio.Core.Audio.AudioDeviceSettings.SectionKey, ScanlineStudio.Core.Audio.AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(77000, audio?.SampleRate);

        var txPaneUi = settingsStore.Settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
        Assert.True(txPaneUi?.AutoFollowRxMode);
    }
}
