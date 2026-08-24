using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
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
