using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Options › Decode › HamDRM receive on/off.</summary>
public sealed class OptionsWindowDrmReceiveTests
{
    private static OptionsWindowViewModel CreateVm(FakeSstvSessionService session, FakeSettingsStore? store = null)
    {
        store ??= new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(TestOptionsSettings.Create(store), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), store, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), session, new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_ShowsTheSavedValue(bool saved)
    {
        var vm = CreateVm(new FakeSstvSessionService { SavedDrmReceiveEnabled = saved });

        Assert.Equal(saved, vm.DrmReceiveEnabled);
    }

    [AvaloniaFact]
    public async Task Save_Unchanged_AppliesNothing()
    {
        var session = new FakeSstvSessionService();
        var vm = CreateVm(session);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(session.DrmReceiveEnabledCalls);
        Assert.Null(vm.SaveErrorMessage);
    }

    [AvaloniaFact]
    public async Task ApplyOff_ThenApplyOn_AppliesEachChangeOnce()
    {
        var session = new FakeSstvSessionService { SavedDrmReceiveEnabled = true };
        var vm = CreateVm(session);

        vm.DrmReceiveEnabled = false;
        await vm.ApplyCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);
        vm.DrmReceiveEnabled = true;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal([false, true], session.DrmReceiveEnabledCalls);
        Assert.True(session.SavedDrmReceiveEnabled);
    }

    [AvaloniaFact]
    public async Task AFailedApply_ShowsTheError_KeepsTheDialogOpen_FinishesLaterWrites_AndRetries()
    {
        var store = new FakeSettingsStore();
        var session = new FakeSstvSessionService { SavedDrmReceiveEnabled = true, SetDrmReceiveEnabledFailure = new IOException("disk") };
        var vm = CreateVm(session, store);
        bool closed = false;
        vm.RequestClose += () => closed = true;

        vm.DrmReceiveEnabled = false;
        vm.JpegQuality = 55;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Options.Decode.DrmReceive.ApplyFailed", vm.SaveErrorMessage);
        Assert.False(closed);
        Assert.Equal(55, store.Settings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings)!.JpegQuality);

        session.SetDrmReceiveEnabledFailure = null;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([false, false], session.DrmReceiveEnabledCalls);
        Assert.False(session.SavedDrmReceiveEnabled);
        Assert.True(closed);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedApply_StillShowsTheOtherPostSaveWarnings(bool viaApply)
    {
        var store = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RadioConnectionSettings.SectionKey,
                new RadioConnectionSettings { HamlibLibraryPath = "/usr/lib/libhamlib.so.4" },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var radio = new FakeRadioSessionService
        {
            HamlibLibraryReloadResultToReturn = new HamlibLibraryReloadResult(false, "/bad/path.so", null, ["/bad/path.so: file not found"]),
        };
        var session = new FakeSstvSessionService { SavedDrmReceiveEnabled = true, SetDrmReceiveEnabledFailure = new IOException("disk") };
        var vm = new OptionsWindowViewModel(TestOptionsSettings.Create(store), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), store, radio, new FakeHamlibDiscoveryService(), new FakeFilePickerService(), session, new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        int warnings = 0;
        vm.HamlibLibraryReloadFailedWarningRequested += () => { warnings++; return Task.CompletedTask; };

        vm.DrmReceiveEnabled = false;
        vm.HamlibLibraryPath = "/bad/path.so";
        await (viaApply ? vm.ApplyCommand : vm.SaveCommand).ExecuteAsync(null);

        Assert.Equal(1, warnings);
        Assert.Equal("Options.Decode.DrmReceive.ApplyFailed", vm.SaveErrorMessage);
    }

    [AvaloniaFact]
    public void Cancel_AppliesNothing()
    {
        var session = new FakeSstvSessionService { SavedDrmReceiveEnabled = true };
        var vm = CreateVm(session);

        vm.DrmReceiveEnabled = false;
        vm.CancelCommand.Execute(null);

        Assert.Empty(session.DrmReceiveEnabledCalls);
        Assert.True(session.SavedDrmReceiveEnabled);
    }

    [AvaloniaFact]
    public void ResetDecode_RestoresTheDefault()
    {
        var vm = CreateVm(new FakeSstvSessionService { SavedDrmReceiveEnabled = !DrmReceiveDefaults.Enabled });

        vm.ResetDecodeToDefaultCommand.Execute(null);

        Assert.Equal(DrmReceiveDefaults.Enabled, vm.DrmReceiveEnabled);
    }
}
