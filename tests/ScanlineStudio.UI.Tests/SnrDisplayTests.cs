using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public class SnrDisplayTests
{
    [Theory]
    [InlineData(null, "Snr.None")]
    [InlineData(double.NaN, "Snr.None")]
    [InlineData(-10.5, "Snr.BelowFloor")]
    [InlineData(-60.0, "Snr.BelowFloor")]
    [InlineData(50.5, "Snr.AboveCeiling")]
    [InlineData(-10.0, "Snr.ValueFormat")]
    [InlineData(17.25, "Snr.ValueFormat")]
    [InlineData(50.0, "Snr.ValueFormat")]
    public void Format_ClampsForDisplay_AndShowsDashWhenAbsent(double? snrDb, string expectedKey)
    {
        var localization = new FakeLocalizationService();

        Assert.Equal(expectedKey, SnrDisplay.Format(localization, snrDb));
        if (expectedKey == "Snr.ValueFormat")
        {
            Assert.Equal([snrDb!.Value], localization.LastArgs);
        }
    }

    [AvaloniaFact]
    public void RxImagePane_Idle_ShowsDash_AndHidesTheStatusChip()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.Equal("Snr.None", vm.SnrDisplay);
        Assert.False(vm.IsSnrChipVisible);
    }

    [AvaloniaFact]
    public void RxImagePane_WhileMeasuring_ShowsTheLiveFigure_AndTheChip_ThenHidesItAgain()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { LiveSnrDb = 22.5 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.PollTelemetry();

        Assert.True(vm.IsSnrChipVisible);
        Assert.Equal("Snr.ValueFormat", vm.SnrDisplay);
        Assert.Equal([22.5], localization.LastArgs);
        Assert.Equal("MainWindow.StatusBar.SnrValueFormat", vm.SnrStatusBarDisplay);
        Assert.Contains(nameof(RxImagePaneViewModel.SnrDisplay), changed);
        Assert.Contains(nameof(RxImagePaneViewModel.IsSnrChipVisible), changed);

        sstvSession.LiveSnrDb = double.NaN;
        vm.PollTelemetry();

        Assert.False(vm.IsSnrChipVisible);
        Assert.Equal("Snr.None", vm.SnrDisplay);
    }

    [AvaloniaFact]
    public async Task Gallery_SelectedEntry_ShowsItsStoredSnr_OrDashWhenAbsent()
    {
        var localization = new FakeLocalizationService();
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, SnrDb: 12.5),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = PaneViewModelTests.CreateRxHistoryPaneViewModel(historyStore, localization: localization);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Assert.Equal("Snr.ValueFormat", vm.SelectedEntrySnrDisplay);
        Assert.Equal([12.5], localization.LastArgs);

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        Assert.Equal("Snr.None", vm.SelectedEntrySnrDisplay);

        vm.SelectedEntry = null;
        Assert.Equal("Snr.None", vm.SelectedEntrySnrDisplay);
    }

    [AvaloniaFact]
    public async Task Options_MeasureSnr_LoadsDefault_SavesLive_AndPersists()
    {
        var settingsStore = new FakeSettingsStore();
        var sstvSession = new FakeSstvSessionService();
        var vm = new OptionsWindowViewModel(TestOptionsSettings.Create(settingsStore), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), sstvSession, new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SstvDecoderSettings.DefaultSnrMeasurementEnabled, vm.SnrMeasurementEnabled);

        vm.SnrMeasurementEnabled = !SstvDecoderSettings.DefaultSnrMeasurementEnabled;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(1, sstvSession.RequestSnrMeasurementEnabledCallCount);
        Assert.Equal(!SstvDecoderSettings.DefaultSnrMeasurementEnabled, sstvSession.LastRequestedSnrMeasurementEnabled);
        var decoder = settingsStore.Settings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.Equal(!SstvDecoderSettings.DefaultSnrMeasurementEnabled, decoder?.SnrMeasurementEnabled);

        vm.ResetDecodeToDefaultCommand.Execute(null);
        Assert.Equal(SstvDecoderSettings.DefaultSnrMeasurementEnabled, vm.SnrMeasurementEnabled);
    }

    [Fact]
    public void Settings_AbsentToggle_ResolvesToTheDefault()
    {
        Assert.Equal(SstvDecoderSettings.DefaultSnrMeasurementEnabled, new SstvDecoderSettings().Resolve().SnrMeasurementEnabled);
        Assert.True(new SstvDecoderSettings { SnrMeasurementEnabled = true }.Resolve().SnrMeasurementEnabled);
        Assert.False(new SstvDecoderSettings { SnrMeasurementEnabled = false }.Resolve().SnrMeasurementEnabled);
    }
}
