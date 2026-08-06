using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class OptionsWindowViewModelTests
{
    private static readonly AudioDeviceInfo CaptureDevice = new("cap1", "Capture One", 2, 0, [11025]);
    private static readonly AudioDeviceInfo PlaybackDevice = new("play1", "Playback One", 0, 2, [11025]);

    [AvaloniaFact]
    public void Constructor_LoadsEveryFieldFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "en" }, LocalizationSettingsJsonContext.Default.LocalizationSettings)
                .WithSection(AudioDeviceSettings.SectionKey, new AudioDeviceSettings { CaptureDeviceId = "cap1", PlaybackDeviceId = "play1", SampleRate = 22050 }, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                .WithSection(RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "rigctld", Host = "localhost", Port = 4532 }, RadioSettingsJsonContext.Default.RadioConnectionSettings)
                .WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "KD9TAW" }, OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [CaptureDevice], OutputDevices = [PlaybackDevice] };

        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("en", vm.SelectedCulture?.Name);
        Assert.Same(CaptureDevice, vm.SelectedCaptureDevice);
        Assert.Same(PlaybackDevice, vm.SelectedPlaybackDevice);
        Assert.Equal(22050, vm.SampleRate);
        Assert.Equal("rigctld", vm.RadioBackendId);
        Assert.True(vm.IsRigctldSelected);
        Assert.Equal("localhost", vm.RigctldHost);
        Assert.Equal(4532, vm.RigctldPort);
        Assert.Equal("KD9TAW", vm.Callsign);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsEveryFieldAndFiresRequestClose()
    {
        var settingsStore = new FakeSettingsStore();
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [CaptureDevice], OutputDevices = [PlaybackDevice] };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedCaptureDevice = CaptureDevice;
        vm.SelectedPlaybackDevice = PlaybackDevice;
        vm.SampleRate = 48000;
        vm.RadioBackendId = "rigctld";
        vm.RigctldHost = "192.168.1.5";
        vm.RigctldPort = 4532;
        vm.Callsign = "N0CALL";

        var closed = false;
        vm.RequestClose += () => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        var audio = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal("cap1", audio?.CaptureDeviceId);
        Assert.Equal("play1", audio?.PlaybackDeviceId);
        Assert.Equal(48000, audio?.SampleRate);

        var radio = settingsStore.Settings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings);
        Assert.Equal("rigctld", radio?.BackendId);
        Assert.Equal("192.168.1.5", radio?.Host);
        Assert.Equal(4532, radio?.Port);

        var operatorSettings = settingsStore.Settings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings);
        Assert.Equal("N0CALL", operatorSettings?.Callsign);
    }

    [AvaloniaFact]
    public void CancelCommand_FiresRequestCloseWithoutSaving()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.Callsign = "SHOULD-NOT-PERSIST";

        var closed = false;
        vm.RequestClose += () => closed = true;

        vm.CancelCommand.Execute(null);

        Assert.True(closed);
        Assert.Empty(settingsStore.Settings.Sections);
    }

    [AvaloniaFact]
    public void ResetAudioToDefaultCommand_ClearsDevicesAndRestoresDefaultSampleRate()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(AudioDeviceSettings.SectionKey, new AudioDeviceSettings { CaptureDeviceId = "cap1", PlaybackDeviceId = "play1", SampleRate = 48000 }, AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [CaptureDevice], OutputDevices = [PlaybackDevice] };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(48000, vm.SampleRate);

        vm.ResetAudioToDefaultCommand.Execute(null);

        Assert.Null(vm.SelectedCaptureDevice);
        Assert.Null(vm.SelectedPlaybackDevice);
        Assert.Equal(new AudioDeviceSettings().SampleRate, vm.SampleRate);
    }

    [AvaloniaFact]
    public void ResetRadioToDefaultCommand_RestoresNoneBackendAndClearsHostPort()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "rigctld", Host = "x", Port = 1 }, RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsRigctldSelected);

        vm.ResetRadioToDefaultCommand.Execute(null);

        Assert.Equal("none", vm.RadioBackendId);
        Assert.False(vm.IsRigctldSelected);
        Assert.Null(vm.RigctldHost);
        Assert.Null(vm.RigctldPort);
    }

    [AvaloniaFact]
    public void RadioBackendRadioButtons_TogglingOneUpdatesRadioBackendIdAndTheOthers()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.IsRigctldBackendSelected = true;

        Assert.Equal("rigctld", vm.RadioBackendId);
        Assert.False(vm.IsNoneBackendSelected);
        Assert.False(vm.IsHamlibBackendSelected);

        vm.IsHamlibBackendSelected = true;

        Assert.Equal("hamlib", vm.RadioBackendId);
        Assert.True(vm.IsHamlibSelected);
        Assert.False(vm.IsRigctldBackendSelected);

        vm.IsNoneBackendSelected = true;

        Assert.Equal("none", vm.RadioBackendId);
        Assert.False(vm.IsRigctldBackendSelected);
        Assert.False(vm.IsHamlibBackendSelected);
    }

    [AvaloniaFact]
    public void Constructor_LoadsHamlibFieldsWhenBackendIsHamlib()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RadioConnectionSettings.SectionKey,
                new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1035, SerialPort = "/dev/ttyUSB0", BaudRate = 4800, PttType = "RIG_PTT_RIG" },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsHamlibSelected);
        Assert.Equal(1035u, vm.HamlibModel);
        Assert.Equal("/dev/ttyUSB0", vm.HamlibSerialPort);
        Assert.Equal(4800, vm.HamlibBaudRate);
        Assert.Equal("RIG_PTT_RIG", vm.HamlibPttType);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsHamlibFieldsWhenBackendIsHamlib()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.IsHamlibBackendSelected = true;
        vm.HamlibModel = 2037;
        vm.HamlibSerialPort = "/dev/ttyS0";
        vm.HamlibBaudRate = 9600;
        vm.HamlibPttType = "RIG_PTT_SERIAL_RTS";

        await vm.SaveCommand.ExecuteAsync(null);

        var radio = settingsStore.Settings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings);
        Assert.Equal("hamlib", radio?.BackendId);
        Assert.Equal(2037u, radio?.HamlibModel);
        Assert.Equal("/dev/ttyS0", radio?.SerialPort);
        Assert.Equal(9600, radio?.BaudRate);
        Assert.Equal("RIG_PTT_SERIAL_RTS", radio?.PttType);
    }

    [AvaloniaFact]
    public void ResetRadioToDefaultCommand_AlsoClearsHamlibFields()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RadioConnectionSettings.SectionKey,
                new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1035, SerialPort = "/dev/ttyUSB0", BaudRate = 4800, PttType = "RIG_PTT_RIG" },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsHamlibSelected);

        vm.ResetRadioToDefaultCommand.Execute(null);

        Assert.Equal("none", vm.RadioBackendId);
        Assert.Null(vm.HamlibModel);
        Assert.Null(vm.HamlibSerialPort);
        Assert.Null(vm.HamlibBaudRate);
        Assert.Null(vm.HamlibPttType);
    }

    [AvaloniaFact]
    public void RequestResetAll_ThenConfirm_ResetsEveryFieldAndClearsConfirmFlag()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "rigctld", Host = "x", Port = 1 }, RadioSettingsJsonContext.Default.RadioConnectionSettings)
                .WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "SOMECALL" }, OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.RequestResetAllCommand.Execute(null);
        Assert.True(vm.IsConfirmingResetAll);
        // Nothing reset yet -- still requires the explicit confirm step.
        Assert.Equal("SOMECALL", vm.Callsign);

        vm.ConfirmResetAllCommand.Execute(null);

        Assert.False(vm.IsConfirmingResetAll);
        Assert.Equal("none", vm.RadioBackendId);
        Assert.Null(vm.Callsign);
    }

    [AvaloniaFact]
    public void CancelResetAll_ClearsConfirmFlagWithoutResettingAnything()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "SOMECALL" }, OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.RequestResetAllCommand.Execute(null);

        vm.CancelResetAllCommand.Execute(null);

        Assert.False(vm.IsConfirmingResetAll);
        Assert.Equal("SOMECALL", vm.Callsign);
    }
}
