using System.Collections.Generic;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
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

        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
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

    // Auditor blocker finding (live-TX-gain review): SaveAsync used to rebuild AudioDeviceSettings
    // from the snapshot OptionsSettingsService captured at dialog-OPEN time, so a value written by
    // something OTHER than this dialog's own Save WHILE the dialog was still open (exactly what the
    // Radio/CAT tab's own Pwr slider does via SstvSessionService.SetTxVolumePercentAsync, completely
    // independent of this dialog's load-once/save-on-click flow) got silently reverted the moment
    // the user clicked Save -- the operator dials TX power on the radio's own meter, hits Save, and
    // the next transmission goes out at the OLD power with the UI still showing the new number.
    [AvaloniaFact]
    public async Task SaveAsync_FieldWrittenExternallyWhileDialogWasOpen_IsPreservedNotReverted()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { TxVolumePercent = 20 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        // Simulates SetTxVolumePercentAsync(80) landing on disk from the Radio/CAT tab's own live
        // Pwr slider, entirely outside this dialog's own Save flow -- the dialog is still open.
        var current = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)!;
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey, current with { TxVolumePercent = 80 }, AudioSettingsJsonContext.Default.AudioDeviceSettings);

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)!;
        Assert.Equal(80, saved.TxVolumePercent);
    }

    // User-reported fix (2026-08-23): a real, OS-agnostic device-id-churn scenario -- observed live
    // when a PipeWire USB capture node was re-created under a new id after a mute toggle, same
    // physical device, same friendly name. AudioDeviceSettings.CaptureDeviceName/PlaybackDeviceName
    // exist specifically to recover the Options dialog's own selection in that case.

    [AvaloniaFact]
    public void Constructor_ConfiguredIdChurnedButNameMatches_RecoversTheSameDeviceByName()
    {
        var churnedCaptureDevice = new AudioDeviceInfo("cap1.new", "Capture One", 2, 0, [11025]);
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "cap1.old", CaptureDeviceName = "Capture One", PlaybackDeviceId = "play1", SampleRate = 22050 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [churnedCaptureDevice], OutputDevices = [PlaybackDevice] };

        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(churnedCaptureDevice, vm.SelectedCaptureDevice);
    }

    [AvaloniaFact]
    public void Constructor_ConfiguredIdChurnedAndNoNameEitherMatches_FallsBackToDefault()
    {
        // Round 3, same day (explicit product decision): a configured device matching NEITHER by id
        // nor by name (genuinely unplugged/uninstalled, not just churned) falls back to the current
        // OS-default device instead of leaving the dropdown blank.
        var churnedCaptureDevice = new AudioDeviceInfo("cap1.new", "Capture One", 2, 0, [11025]);
        var defaultCaptureDevice = new AudioDeviceInfo("cap-default", "System Default Mic", 1, 0, [11025], IsDefault: true);
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "cap1.old", CaptureDeviceName = "A Completely Different Microphone", PlaybackDeviceId = "play1", SampleRate = 22050 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [churnedCaptureDevice, defaultCaptureDevice], OutputDevices = [PlaybackDevice] };

        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(defaultCaptureDevice, vm.SelectedCaptureDevice);
    }

    [AvaloniaFact]
    public void Constructor_ConfiguredIdChurnedAndNoNameOrDefaultMatch_LeavesSelectionBlank()
    {
        // The genuinely-nothing-available case (no id match, no name match, no backend default
        // either) must still leave the dropdown blank -- there is nothing to fall back to.
        var churnedCaptureDevice = new AudioDeviceInfo("cap1.new", "Capture One", 2, 0, [11025]);
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "cap1.old", CaptureDeviceName = "A Completely Different Microphone", PlaybackDeviceId = "play1", SampleRate = 22050 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [churnedCaptureDevice], OutputDevices = [PlaybackDevice] };

        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.SelectedCaptureDevice);
    }

    [AvaloniaFact]
    public void SaveCommand_PersistsSelectedCaptureDevicesName_ForFutureIdChurnRecovery()
    {
        var settingsStore = new FakeSettingsStore { Settings = new AppSettings() };
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [CaptureDevice], OutputDevices = [PlaybackDevice] };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedCaptureDevice = CaptureDevice;

        vm.SaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var saved = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal("Capture One", saved?.CaptureDeviceName);
    }

    [AvaloniaFact]
    public void Constructor_InvalidPersistedSampleRate_UsesLegacyStartupDefault()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { SampleRate = 48501 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };

        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance),
            new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(),
            settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(SstvSampleRate.Default, vm.SampleRate);
    }

    [AvaloniaFact]
    public void Constructor_LoadSucceeds_SaveCommandIsEnabled()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Constructor_SettingsStoreLoadThrows_SaveCommandStaysDisabled()
    {
        // Tier B audit finding (blocker): LoadSafeAsync's catch used to leave every field at its
        // constructor default with no gate on Save -- a user who then changed something unrelated
        // (e.g. Callsign, unreachable in this exact repro since the dialog never even opens usably,
        // but demonstrated via RememberWindowPosition/JpegQuality below) and clicked Save would
        // silently persist those defaults over their real settings. SaveCommand is now unusable until
        // a load has actually completed once.
        var settingsStore = new FakeSettingsStore { LoadAsyncException = new IOException("disk error") };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Constructor_AudioEnumeratorRefreshThrows_SaveCommandStaysDisabled()
    {
        // Tier B audit finding (blocker): same as the settings-store-throws repro above, but for the
        // failure LoadSafeAsync's own doc comment specifically calls out -- the audio backend being
        // unavailable when the dialog opens.
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { RefreshAsyncException = new InvalidOperationException("audio backend unavailable") };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.SaveCommand.CanExecute(null));
    }

    // Auditor usability review follow-up (2026-08-18): Radio/CAT tab's "Test Connection" button.
    // Deliberately tests whatever is CURRENTLY TYPED into RigctldHost/Port (not necessarily saved),
    // via a fresh, disposable IRadioSessionService.TestConnectionAsync call -- never the app's own
    // real, persistent radio session.

    [AvaloniaFact]
    public async Task TestRigctldConnectionCommand_Success_SetsStatusMessageToSuccessKey()
    {
        var radioSession = new FakeRadioSessionService
        {
            TestConnectionResultToReturn = new RadioConnectionTestResult(true, "rigctld-client", RadioCapabilities.PttControl, null),
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), radioSession, new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.RigctldHost = "127.0.0.1";
        vm.RigctldPort = 4532;

        await vm.TestRigctldConnectionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var spec = Assert.IsType<RigctldConnectionSpec>(Assert.Single(radioSession.TestConnectionCalls));
        Assert.Equal("127.0.0.1", spec.Host);
        Assert.Equal(4532, spec.Port);
        Assert.Equal("Options.Radio.TestConnection.Success", vm.TestConnectionStatusMessage);
        Assert.False(vm.IsTestingConnection);
    }

    [AvaloniaFact]
    public async Task TestRigctldConnectionCommand_Failure_SetsStatusMessageToFailedKey()
    {
        var radioSession = new FakeRadioSessionService
        {
            TestConnectionResultToReturn = new RadioConnectionTestResult(false, null, RadioCapabilities.None, "connection refused"),
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), radioSession, new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.RigctldHost = "127.0.0.1";
        vm.RigctldPort = 4532;

        await vm.TestRigctldConnectionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Options.Radio.TestConnection.Failed", vm.TestConnectionStatusMessage);
        Assert.False(vm.IsTestingConnection);
    }

    [AvaloniaFact]
    public async Task TestRigctldConnectionCommand_GetStringThrowsFormattingTheResult_StillResetsIsTestingConnectionWithoutCrashingTheDispatcherLoop()
    {
        // Tier B audit finding: the Dispatcher.UIThread.Post lambda runs AFTER the outer try/catch
        // has already exited, so its own GetString calls used to be unguarded -- a locale file with a
        // mismatched format placeholder throwing FormatException here used to escape uncaught onto
        // the dispatcher loop (confirmed: this exact test failed with an unhandled FormatException
        // from Dispatcher.RunJobs before the inner try/catch was added) AND leave IsTestingConnection
        // stuck true (Test Connection permanently disabled for the life of the dialog).
        var radioSession = new FakeRadioSessionService
        {
            TestConnectionResultToReturn = new RadioConnectionTestResult(true, "rigctld-client", RadioCapabilities.PttControl, null),
        };
        var localization = new FakeLocalizationService
        {
            ThrowOnGetString = new FormatException("Input string was not in a correct format."),
            ThrowOnGetStringForKey = "Options.Radio.TestConnection.Success",
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), localization,
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), radioSession, new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.RigctldHost = "127.0.0.1";
        vm.RigctldPort = 4532;

        await vm.TestRigctldConnectionCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsTestingConnection);
        Assert.Null(vm.TestConnectionStatusMessage);
    }

    [AvaloniaFact]
    public void TestRigctldConnectionCommand_CanExecute_FalseWhenHostOrPortMissing()
    {
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.RigctldHost = null;
        vm.RigctldPort = 4532;
        Assert.False(vm.TestRigctldConnectionCommand.CanExecute(null));

        vm.RigctldHost = "127.0.0.1";
        vm.RigctldPort = null;
        Assert.False(vm.TestRigctldConnectionCommand.CanExecute(null));

        vm.RigctldPort = 4532;
        Assert.True(vm.TestRigctldConnectionCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task BrowseHamlibLibraryCommand_PickedPathProbedSuccessfully_SetsPathAndRigModels()
    {
        var filePicker = new FakeFilePickerService { HamlibLibraryPathToReturn = "/usr/lib/libhamlib.so.4" };
        var hamlibDiscovery = new FakeHamlibDiscoveryService
        {
            ResultToReturn = new HamlibProbeResult(true, "/usr/lib/libhamlib.so.4", "Hamlib 4.5.5", [], [new HamlibRigModelInfo(1035, "Yaesu", "FT-991")]),
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), hamlibDiscovery, filePicker, new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        await vm.BrowseHamlibLibraryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/usr/lib/libhamlib.so.4", vm.HamlibLibraryPath);
        Assert.Equal("/usr/lib/libhamlib.so.4", Assert.Single(hamlibDiscovery.ProbedPaths));
        Assert.Single(vm.HamlibRigModels);
        Assert.False(vm.IsProbingHamlib);
    }

    [AvaloniaFact]
    public async Task BrowseHamlibLibraryCommand_UserCancelled_DoesNotProbe()
    {
        var filePicker = new FakeFilePickerService { HamlibLibraryPathToReturn = null };
        var hamlibDiscovery = new FakeHamlibDiscoveryService();
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), hamlibDiscovery, filePicker, new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        await vm.BrowseHamlibLibraryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.HamlibLibraryPath);
        Assert.Empty(hamlibDiscovery.ProbedPaths);
    }

    [AvaloniaFact]
    public async Task AutoDetectHamlibCommand_Success_OverwritesLibraryPathWithResolvedPath()
    {
        var hamlibDiscovery = new FakeHamlibDiscoveryService
        {
            ResultToReturn = new HamlibProbeResult(true, "/usr/lib/libhamlib.so.4", "Hamlib 4.5.5", [], [new HamlibRigModelInfo(1035, "Yaesu", "FT-991")]),
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), hamlibDiscovery, new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        await vm.AutoDetectHamlibCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/usr/lib/libhamlib.so.4", vm.HamlibLibraryPath);
        Assert.Null(Assert.Single(hamlibDiscovery.ProbedPaths)); // auto-detect passes null (no override)
        Assert.Single(vm.HamlibRigModels);
    }

    [AvaloniaFact]
    public async Task ProbeHamlibCommand_Failure_ClearsRigModelsAndSetsStatusMessage_DoesNotThrow()
    {
        var hamlibDiscovery = new FakeHamlibDiscoveryService
        {
            ResultToReturn = new HamlibProbeResult(false, null, null, ["libhamlib.so.4: not found"], []),
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), hamlibDiscovery, new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.HamlibLibraryPath = "/does/not/exist.so";

        await vm.ProbeHamlibCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/does/not/exist.so", vm.HamlibLibraryPath); // a failed Test never overwrites what the user typed
        Assert.Empty(vm.HamlibRigModels);
        Assert.Equal("Options.Radio.Hamlib.Probe.NotFound", vm.HamlibDiscoveryStatusMessage);
        Assert.False(vm.IsProbingHamlib);
    }

    [AvaloniaFact]
    public void SelectedHamlibRigModel_Changed_SetsHamlibModel()
    {
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(),
            new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedHamlibRigModel = new HamlibRigModelInfo(1035, "Yaesu", "FT-991");

        Assert.Equal(1035u, vm.HamlibModel);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsEveryFieldAndFiresRequestClose()
    {
        var settingsStore = new FakeSettingsStore();
        var audioDeviceEnumerator = new FakeAudioDeviceEnumerator { InputDevices = [CaptureDevice], OutputDevices = [PlaybackDevice] };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
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
    public async Task SaveCommand_OutOfRangeSampleRate_PreservesPreviousSupportedValue()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { SampleRate = 22050 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var vm = new OptionsWindowViewModel(
            new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance),
            new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(),
            settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SampleRate = 4999;

        await vm.SaveCommand.ExecuteAsync(null);

        var audio = settingsStore.Settings.GetSection(
            AudioDeviceSettings.SectionKey,
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(22050, audio?.SampleRate);
    }

    [AvaloniaFact]
    public void CancelCommand_FiresRequestCloseWithoutSaving()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
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
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), audioDeviceEnumerator, new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(48000, vm.SampleRate);

        vm.ResetAudioToDefaultCommand.Execute(null);

        Assert.Null(vm.SelectedCaptureDevice);
        Assert.Null(vm.SelectedPlaybackDevice);
        Assert.Equal(new AudioDeviceSettings().SampleRate, vm.SampleRate);
    }

    [AvaloniaFact]
    public void Constructor_LoadsAudioPerformanceFieldsFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(AudioDeviceSettings.SectionKey, new AudioDeviceSettings { CaptureChannelSource = AudioChannelSource.Right, StereoTxEnabled = true }, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                .WithSection(AppPerformanceSettings.SectionKey, new AppPerformanceSettings { ProcessPriority = System.Diagnostics.ProcessPriorityClass.High }, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AudioChannelSource.Right, vm.CaptureChannelSource);
        Assert.True(vm.IsCaptureChannelRightSelected);
        Assert.False(vm.IsCaptureChannelMonoSelected);
        Assert.True(vm.StereoTxEnabled);
        Assert.True(vm.AppPriorityIsHigh);
        Assert.True(vm.IsAppPriorityHighSelected);
        Assert.False(vm.IsAppPriorityNormalSelected);
    }

    [AvaloniaFact]
    public void Constructor_DefaultsAudioPerformanceFieldsWhenSectionsMissing()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(AudioChannelSource.Mono, vm.CaptureChannelSource);
        Assert.True(vm.IsCaptureChannelMonoSelected);
        Assert.False(vm.StereoTxEnabled);
        Assert.False(vm.AppPriorityIsHigh);
        Assert.True(vm.IsAppPriorityNormalSelected);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsAudioPerformanceFields()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.IsCaptureChannelLeftSelected = true;
        vm.StereoTxEnabled = true;
        vm.IsAppPriorityHighSelected = true;

        await vm.SaveCommand.ExecuteAsync(null);

        var audio = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal(AudioChannelSource.Left, audio?.CaptureChannelSource);
        Assert.True(audio?.StereoTxEnabled);

        var appPerformance = settingsStore.Settings.GetSection(AppPerformanceSettings.SectionKey, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings);
        Assert.Equal(System.Diagnostics.ProcessPriorityClass.High, appPerformance?.ProcessPriority);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsAppPriorityAsNull_NotExplicitNormal_WhenNormalSelected()
    {
        // AppPerformanceSettings.ProcessPriority's own contract: null means "don't touch the OS
        // default," which is what "Normal" must save as, not ProcessPriorityClass.Normal explicitly.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(AppPerformanceSettings.SectionKey, new AppPerformanceSettings { ProcessPriority = System.Diagnostics.ProcessPriorityClass.High }, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.IsAppPriorityNormalSelected = true;
        await vm.SaveCommand.ExecuteAsync(null);

        var appPerformance = settingsStore.Settings.GetSection(AppPerformanceSettings.SectionKey, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings);
        Assert.Null(appPerformance?.ProcessPriority);
    }

    [AvaloniaFact]
    public void ResetAudioToDefaultCommand_RestoresAudioPerformanceFields()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(AudioDeviceSettings.SectionKey, new AudioDeviceSettings { CaptureChannelSource = AudioChannelSource.Right, StereoTxEnabled = true }, AudioSettingsJsonContext.Default.AudioDeviceSettings)
                .WithSection(AppPerformanceSettings.SectionKey, new AppPerformanceSettings { ProcessPriority = System.Diagnostics.ProcessPriorityClass.High }, AppPerformanceSettingsJsonContext.Default.AppPerformanceSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(AudioChannelSource.Right, vm.CaptureChannelSource);

        vm.ResetAudioToDefaultCommand.Execute(null);

        Assert.Equal(AudioChannelSource.Mono, vm.CaptureChannelSource);
        Assert.False(vm.StereoTxEnabled);
        Assert.False(vm.AppPriorityIsHigh);
    }

    [AvaloniaFact]
    public void ResetRadioToDefaultCommand_RestoresNoneBackendAndClearsHostPort()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "rigctld", Host = "x", Port = 1 }, RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsRigctldSelected);

        vm.ResetRadioToDefaultCommand.Execute(null);

        Assert.Equal("none", vm.RadioBackendId);
        Assert.False(vm.IsRigctldSelected);
        Assert.Null(vm.RigctldHost);
        Assert.Null(vm.RigctldPort);
    }

    [AvaloniaFact]
    public void ResetRadioToDefaultCommand_ClearsAStaleTestConnectionStatusMessage()
    {
        // Tier B audit finding: sibling ResetQrzToDefault already clears its own test-result status
        // (TestQrzLookupStatus) -- this one didn't, so a prior "Connected to IC-7300" success line
        // stayed visible under the now-blank host field after a reset.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.TestConnectionStatusMessage = "Options.Radio.TestConnection.Success";

        vm.ResetRadioToDefaultCommand.Execute(null);

        Assert.Null(vm.TestConnectionStatusMessage);
    }

    [AvaloniaFact]
    public void RadioBackendRadioButtons_TogglingOneUpdatesRadioBackendIdAndTheOthers()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
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
                new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1035, HamlibLibraryPath = "/usr/lib/libhamlib.so.4", SerialPort = "/dev/ttyUSB0", BaudRate = 4800, PttType = "RIG_PTT_RIG" },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsHamlibSelected);
        Assert.Equal(1035u, vm.HamlibModel);
        Assert.Equal("/usr/lib/libhamlib.so.4", vm.HamlibLibraryPath);
        Assert.Equal("/dev/ttyUSB0", vm.HamlibSerialPort);
        Assert.Equal(4800, vm.HamlibBaudRate);
        Assert.Equal("RIG_PTT_RIG", vm.HamlibPttType);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsHamlibFieldsWhenBackendIsHamlib()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.IsHamlibBackendSelected = true;
        vm.HamlibModel = 2037;
        vm.HamlibLibraryPath = "/opt/homebrew/lib/libhamlib.4.dylib";
        vm.HamlibSerialPort = "/dev/ttyS0";
        vm.HamlibBaudRate = 9600;
        vm.HamlibPttType = "RIG_PTT_SERIAL_RTS";

        await vm.SaveCommand.ExecuteAsync(null);

        var radio = settingsStore.Settings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings);
        Assert.Equal("hamlib", radio?.BackendId);
        Assert.Equal(2037u, radio?.HamlibModel);
        Assert.Equal("/opt/homebrew/lib/libhamlib.4.dylib", radio?.HamlibLibraryPath);
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
                new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1035, HamlibLibraryPath = "/usr/lib/libhamlib.so.4", SerialPort = "/dev/ttyUSB0", BaudRate = 4800, PttType = "RIG_PTT_RIG" },
                RadioSettingsJsonContext.Default.RadioConnectionSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsHamlibSelected);

        vm.ResetRadioToDefaultCommand.Execute(null);

        Assert.Equal("none", vm.RadioBackendId);
        Assert.Null(vm.HamlibModel);
        Assert.Null(vm.HamlibLibraryPath);
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
                .WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "SOMECALL" }, OperatorSettingsJsonContext.Default.OperatorSettings)
                .WithSection(SstvDecoderSettings.SectionKey, new SstvDecoderSettings { AutoSyncEnabled = false, AutoSlantEnabled = false, DemodType = DemodType.Pll, RxBpfPreset = RxBpfPreset.Narrow, RxBufferMode = RxBufferMode.Extended }, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings)
                .WithSection(StationIdSettings.SectionKey, new StationIdSettings { CwIdMode = CwIdMode.Cw, CwWpm = 40 }, StationIdSettingsJsonContext.Default.StationIdSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.RequestResetAllCommand.Execute(null);
        Assert.True(vm.IsConfirmingResetAll);
        // Nothing reset yet -- still requires the explicit confirm step.
        Assert.Equal("SOMECALL", vm.Callsign);

        vm.ConfirmResetAllCommand.Execute(null);

        Assert.False(vm.IsConfirmingResetAll);
        Assert.Equal("none", vm.RadioBackendId);
        Assert.Null(vm.Callsign);
        Assert.True(vm.AutoSyncEnabled);
        Assert.True(vm.AutoSlantEnabled);
        Assert.Equal(DemodType.Hilbert, vm.DemodType);
        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset);
        Assert.Equal(RxBufferMode.On, vm.RxBufferMode);
        // Auditor round-2 finding: ConfirmResetAll originally omitted Identification entirely --
        // this is the blind spot that let that regression through undetected.
        Assert.Equal(CwIdMode.Off, vm.CwIdMode);
        Assert.Equal(28, vm.CwWpm);
    }

    [AvaloniaFact]
    public void Constructor_LoadsDecodeAutoSyncAndAutoSlantFromPersistedSettings()
    {
        // Explicit false for both -- true is also each field's own desired default (see
        // SstvDecoderSettings' doc comment), so a load path that silently ignores the persisted
        // value and falls through to the default would pass a true/true assertion by coincidence.
        // AutoStop is the inverse case (its own default is false) -- explicit true here for the
        // same "wouldn't pass by coincidence" reasoning. SenseLevel: 1 is also the default, so use
        // 2 ("High") for the same reason. DemodType: Hilbert is also the default, so use Pll for
        // the same reason. RxBpfPreset: Wide is also the default, so use VeryNarrow for the same
        // reason. RxBufferMode: On is also the default, so use Extended for the same reason.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { AutoSyncEnabled = false, AutoSlantEnabled = false, AutoStopEnabled = true, SyncRestartEnabled = false, SenseLevel = 2, DemodType = DemodType.Pll, RxBpfPreset = RxBpfPreset.VeryNarrow, RxBufferMode = RxBufferMode.Extended },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.AutoSyncEnabled);
        Assert.False(vm.AutoSlantEnabled);
        Assert.True(vm.AutoStopEnabled);
        Assert.False(vm.SyncRestartEnabled);
        Assert.Equal(2, vm.SenseLevel);
        Assert.True(vm.IsSenseLevelHighSelected);
        Assert.False(vm.IsSenseLevelLowSelected);
        Assert.Equal(DemodType.Pll, vm.DemodType);
        Assert.True(vm.IsDemodTypePllSelected);
        Assert.False(vm.IsDemodTypeHilbertSelected);
        Assert.Equal(RxBpfPreset.VeryNarrow, vm.RxBpfPreset);
        Assert.True(vm.IsRxBpfVeryNarrowSelected);
        Assert.False(vm.IsRxBpfWideSelected);
        Assert.Equal(RxBufferMode.Extended, vm.RxBufferMode);
        Assert.True(vm.IsRxBufferExtendedSelected);
        Assert.False(vm.IsRxBufferOnSelected);
        Assert.True(vm.IsAutoSlantRowEnabled); // Extended != Off, gate stays open
    }

    [AvaloniaFact]
    public void Constructor_DefaultsDecodeTogglesWhenSectionMissing()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.AutoSyncEnabled);
        Assert.True(vm.AutoSlantEnabled);
        // AutoStopEnabled's own desired default is false, unlike the other three -- legacy's real
        // fresh-install default too (sys.m_AutoStop = 0, Main.cpp:900).
        Assert.False(vm.AutoStopEnabled);
        Assert.True(vm.SyncRestartEnabled);
        Assert.Equal(1, vm.SenseLevel);
        Assert.True(vm.IsSenseLevelLowSelected);
        Assert.Equal(DemodType.Hilbert, vm.DemodType);
        Assert.True(vm.IsDemodTypeHilbertSelected);
        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset);
        Assert.True(vm.IsRxBpfWideSelected);
        Assert.Equal(RxBufferMode.On, vm.RxBufferMode);
        Assert.True(vm.IsRxBufferOnSelected);
        Assert.True(vm.IsAutoSlantRowEnabled);
    }

    [AvaloniaFact]
    public void Constructor_ClampsOutOfRangePersistedSenseLevelToVeryLow()
    {
        // A hand-edited settings.json can persist a value outside 0-3 -- ApplyFromSnapshot must
        // clamp to index 0 ("Very low"), matching legacy's own SetSenseLvl switch `default:` branch
        // (deliberately NOT the same fallback as an absent section, which resolves to 1 above).
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { SenseLevel = 7 },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, vm.SenseLevel);
        Assert.True(vm.IsSenseLevelVeryLowSelected);
        Assert.False(vm.IsSenseLevelLowSelected);
        Assert.False(vm.IsSenseLevelHighSelected);
        Assert.False(vm.IsSenseLevelVeryHighSelected);
    }

    [AvaloniaFact]
    public void Constructor_ClampsOutOfRangePersistedDemodTypeToHilbert()
    {
        // A hand-edited settings.json can persist an enum value outside 0-2 -- ApplyFromSnapshot
        // must clamp to Hilbert, matching SstvDecoderSettings.DemodType's own doc comment (unlike
        // SenseLevel, BOTH the absent-key and out-of-range fallbacks resolve to the SAME value here).
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { DemodType = (DemodType)99 },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(DemodType.Hilbert, vm.DemodType);
        Assert.True(vm.IsDemodTypeHilbertSelected);
        Assert.False(vm.IsDemodTypePllSelected);
        Assert.False(vm.IsDemodTypeZeroCrossingSelected);
    }

    [AvaloniaFact]
    public void Constructor_ClampsOutOfRangePersistedRxBpfPresetToWide()
    {
        // A hand-edited settings.json can persist an enum value outside 0-3 -- ApplyFromSnapshot
        // must clamp to Wide, matching SstvDecoderSettings.RxBpfPreset's own doc comment (unlike
        // SenseLevel, BOTH the absent-key and out-of-range fallbacks resolve to the SAME value here).
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { RxBpfPreset = (RxBpfPreset)99 },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset);
        Assert.True(vm.IsRxBpfWideSelected);
        Assert.False(vm.IsRxBpfOffSelected);
        Assert.False(vm.IsRxBpfNarrowSelected);
        Assert.False(vm.IsRxBpfVeryNarrowSelected);
    }

    [AvaloniaFact]
    public void Constructor_ClampsOutOfRangePersistedRxBufferModeToOn()
    {
        // A hand-edited settings.json can persist an enum value outside 0-2 -- ApplyFromSnapshot
        // must clamp to On, matching SstvDecoderSettings.RxBufferMode's own doc comment (unlike
        // SenseLevel, BOTH the absent-key and out-of-range fallbacks resolve to the SAME value here).
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { RxBufferMode = (RxBufferMode)99 },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBufferMode.On, vm.RxBufferMode);
        Assert.True(vm.IsRxBufferOnSelected);
        Assert.False(vm.IsRxBufferOffSelected);
        Assert.False(vm.IsRxBufferExtendedSelected);
    }

    [AvaloniaFact]
    public void IsDemodTypePllSelected_Set_RaisesPropertyChangedForAllThreeSiblings()
    {
        // Round-1 code-review finding: a passing bool-value assertion alone (the tests above) doesn't
        // prove the live radio group actually re-renders on toggle/load/reset -- only a real
        // PropertyChanged notification does. Mirrors IsIdMethodCwSelected_Set_RaisesPropertyChangedForItselfAndTheOffSibling's
        // own reasoning; CwIdMode and SenseLevel both already have this coverage, DemodType didn't.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsDemodTypePllSelected = true;

        Assert.Contains(nameof(vm.IsDemodTypePllSelected), raised);
        Assert.Contains(nameof(vm.IsDemodTypeZeroCrossingSelected), raised);
        Assert.Contains(nameof(vm.IsDemodTypeHilbertSelected), raised);
    }

    [AvaloniaFact]
    public void IsRxBpfNarrowSelected_Set_RaisesPropertyChangedForAllFourSiblings()
    {
        // Same reasoning as IsDemodTypePllSelected_Set_RaisesPropertyChangedForAllThreeSiblings above
        // -- 4-way exclusive here, not 3.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsRxBpfNarrowSelected = true;

        Assert.Contains(nameof(vm.IsRxBpfOffSelected), raised);
        Assert.Contains(nameof(vm.IsRxBpfWideSelected), raised);
        Assert.Contains(nameof(vm.IsRxBpfNarrowSelected), raised);
        Assert.Contains(nameof(vm.IsRxBpfVeryNarrowSelected), raised);
    }

    [AvaloniaFact]
    public void IsRxBufferOffSelected_Set_RaisesPropertyChangedForAllThreeSiblingsAndTheAutoSlantGate()
    {
        // Same reasoning as IsRxBpfNarrowSelected_Set_RaisesPropertyChangedForAllFourSiblings above,
        // plus IsAutoSlantRowEnabled -- that computed property is also derived from RxBufferMode
        // (OnRxBufferModeChanged's own doc comment) and must re-render too, or a live toggle in the
        // dialog would leave the Auto Slant checkbox's enabled state stale until the next reload.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsRxBufferOffSelected = true;

        Assert.Contains(nameof(vm.IsRxBufferOffSelected), raised);
        Assert.Contains(nameof(vm.IsRxBufferOnSelected), raised);
        Assert.Contains(nameof(vm.IsRxBufferExtendedSelected), raised);
        Assert.Contains(nameof(vm.IsAutoSlantRowEnabled), raised);
        Assert.False(vm.IsAutoSlantRowEnabled);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsDecodeTogglesWithoutDisturbingOtherDecoderFields()
    {
        // AfcEnabled pre-set to a non-default value -- this dialog doesn't edit it, Save must
        // preserve it as-is rather than resetting the whole SstvDecoder section.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { AfcEnabled = false },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.AutoSyncEnabled = false;
        vm.AutoSlantEnabled = false;
        vm.AutoStopEnabled = true;
        vm.SyncRestartEnabled = false;
        vm.SenseLevel = 3;
        vm.DemodType = DemodType.ZeroCrossing;
        vm.RxBpfPreset = RxBpfPreset.Narrow;
        vm.RxBufferMode = RxBufferMode.Extended;

        await vm.SaveCommand.ExecuteAsync(null);

        var decoder = settingsStore.Settings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.False(decoder?.AutoSyncEnabled);
        Assert.False(decoder?.AutoSlantEnabled);
        Assert.True(decoder?.AutoStopEnabled);
        Assert.False(decoder?.SyncRestartEnabled);
        Assert.Equal(3, decoder?.SenseLevel);
        Assert.Equal(DemodType.ZeroCrossing, decoder?.DemodType);
        Assert.Equal(RxBpfPreset.Narrow, decoder?.RxBpfPreset);
        Assert.Equal(RxBufferMode.Extended, decoder?.RxBufferMode);
        Assert.False(decoder?.AfcEnabled);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsRxBpfPresetOff_NotSilentlyDefaultedToWide()
    {
        // Phase-4 auditor code-review finding: RxBpfPreset.Off is deliberately the enum's 0 value
        // (RxBpfPreset.cs's own doc comment) -- the SAME underlying int an absent settings.json key
        // would deserialize to before the "?? Wide" fallback applies. This test locks down that Off
        // is genuinely written and read back as Off (Enum.IsDefined true, distinct from "unset"), not
        // silently coerced to Wide by some accidental default-value-loss path resembling the exact
        // trap SstvDecoderSettings' own class doc comment warns bool fields about.
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.RxBpfPreset = RxBpfPreset.Off;
        await vm.SaveCommand.ExecuteAsync(null);

        var decoder = settingsStore.Settings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.NotNull(decoder?.RxBpfPreset);
        Assert.Equal(RxBpfPreset.Off, decoder!.RxBpfPreset!.Value);

        var reloaded = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBpfPreset.Off, reloaded.RxBpfPreset);
        Assert.True(reloaded.IsRxBpfOffSelected);
        Assert.False(reloaded.IsRxBpfWideSelected);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsRxBufferModeOff_NotSilentlyDefaultedToOn()
    {
        // Same reasoning as SaveCommand_PersistsRxBpfPresetOff_NotSilentlyDefaultedToWide above --
        // RxBufferMode.Off is likewise deliberately the enum's 0 value
        // (RxBufferMode.cs's own doc comment), the same underlying int an absent settings.json key
        // would deserialize to before the "?? On" fallback applies.
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.RxBufferMode = RxBufferMode.Off;
        await vm.SaveCommand.ExecuteAsync(null);

        var decoder = settingsStore.Settings.GetSection(SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.NotNull(decoder?.RxBufferMode);
        Assert.Equal(RxBufferMode.Off, decoder!.RxBufferMode!.Value);

        var reloaded = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBufferMode.Off, reloaded.RxBufferMode);
        Assert.True(reloaded.IsRxBufferOffSelected);
        Assert.False(reloaded.IsRxBufferOnSelected);
        Assert.False(reloaded.IsAutoSlantRowEnabled);
    }

    [AvaloniaFact]
    public void ResetDecodeToDefaultCommand_RestoresAllDecodeFields()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                SstvDecoderSettings.SectionKey,
                new SstvDecoderSettings { AutoSyncEnabled = false, AutoSlantEnabled = false, AutoStopEnabled = true, SyncRestartEnabled = false, SenseLevel = 3, DemodType = DemodType.ZeroCrossing, RxBpfPreset = RxBpfPreset.VeryNarrow, RxBufferMode = RxBufferMode.Off },
                SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.AutoSyncEnabled);
        Assert.False(vm.AutoSlantEnabled);
        Assert.True(vm.AutoStopEnabled);
        Assert.False(vm.SyncRestartEnabled);
        Assert.Equal(3, vm.SenseLevel);
        Assert.Equal(DemodType.ZeroCrossing, vm.DemodType);
        Assert.Equal(RxBpfPreset.VeryNarrow, vm.RxBpfPreset);
        Assert.Equal(RxBufferMode.Off, vm.RxBufferMode);

        vm.ResetDecodeToDefaultCommand.Execute(null);

        Assert.True(vm.AutoSyncEnabled);
        Assert.True(vm.AutoSlantEnabled);
        Assert.False(vm.AutoStopEnabled);
        Assert.True(vm.SyncRestartEnabled);
        Assert.Equal(1, vm.SenseLevel);
        Assert.True(vm.IsSenseLevelLowSelected);
        Assert.Equal(DemodType.Hilbert, vm.DemodType);
        Assert.True(vm.IsDemodTypeHilbertSelected);
        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset);
        Assert.True(vm.IsRxBpfWideSelected);
        Assert.Equal(RxBufferMode.On, vm.RxBufferMode);
        Assert.True(vm.IsRxBufferOnSelected);
        Assert.True(vm.IsAutoSlantRowEnabled);
    }

    [AvaloniaFact]
    public void CancelResetAll_ClearsConfirmFlagWithoutResettingAnything()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "SOMECALL" }, OperatorSettingsJsonContext.Default.OperatorSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.RequestResetAllCommand.Execute(null);

        vm.CancelResetAllCommand.Execute(null);

        Assert.False(vm.IsConfirmingResetAll);
        Assert.Equal("SOMECALL", vm.Callsign);
    }

    [AvaloniaFact]
    public void TestQrzLookupCommand_DisabledUntilBothUsernameAndPasswordAreSet()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.TestQrzLookupCommand.CanExecute(null));

        vm.QrzLookupUsername = "user";
        Assert.False(vm.TestQrzLookupCommand.CanExecute(null));

        vm.QrzLookupPassword = "pass";
        Assert.True(vm.TestQrzLookupCommand.CanExecute(null));

        vm.QrzLookupUsername = "   ";
        Assert.False(vm.TestQrzLookupCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TestQrzLookupAsync_Success_SetsStatus_UsesCurrentInMemoryCredentials()
    {
        var logbookSession = new FakeLogbookSessionService { TestQrzLookupResultToReturn = new(true, null) };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), logbookSession, new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.QrzLookupUsername = "user";
        vm.QrzLookupPassword = "pass";

        await vm.TestQrzLookupCommand.ExecuteAsync(null);

        Assert.Equal("Options.Qrz.TestResult.Success", vm.TestQrzLookupStatus);
        Assert.Equal("user", logbookSession.LastTestUsername);
        Assert.Equal("pass", logbookSession.LastTestPassword);
        Assert.Equal(1, logbookSession.TestQrzLookupCallCount);
    }

    [AvaloniaFact]
    public async Task TestQrzLookupAsync_Failure_SetsStatusWithReason()
    {
        var logbookSession = new FakeLogbookSessionService { TestQrzLookupResultToReturn = new(false, "Username/password incorrect") };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), logbookSession, new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.QrzLookupUsername = "user";
        vm.QrzLookupPassword = "wrongpass";

        await vm.TestQrzLookupCommand.ExecuteAsync(null);

        Assert.Equal("Options.Qrz.TestResult.Failure", vm.TestQrzLookupStatus);
    }

    [AvaloniaFact]
    public void ResetQrzToDefault_ClearsFieldsAndStatus()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.QrzLookupEnabled = true;
        vm.QrzLookupUsername = "user";
        vm.QrzLookupPassword = "pass";
        vm.TestQrzLookupStatus = "some status";

        vm.ResetQrzToDefaultCommand.Execute(null);

        Assert.False(vm.QrzLookupEnabled);
        Assert.Null(vm.QrzLookupUsername);
        Assert.Null(vm.QrzLookupPassword);
        Assert.Null(vm.TestQrzLookupStatus);
    }

    [AvaloniaFact]
    public async Task SaveAsync_PersistsQrzLookupFields_ReloadedCorrectlyOnNextConstruction()
    {
        var settingsStore = new FakeSettingsStore();
        var logbookSession = new FakeLogbookSessionService();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), logbookSession, settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.QrzLookupEnabled = true;
        vm.QrzLookupUsername = "user";
        vm.QrzLookupPassword = "pass";

        await vm.SaveCommand.ExecuteAsync(null);

        var reloaded = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), logbookSession, settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(reloaded.QrzLookupEnabled);
        Assert.Equal("user", reloaded.QrzLookupUsername);
        Assert.Equal("pass", reloaded.QrzLookupPassword);
    }

    [AvaloniaFact]
    public void Constructor_LoadsRememberWindowPositionFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                WindowGeometrySettings.SectionKey,
                new WindowGeometrySettings { RememberWindowPosition = true, Left = 10, Top = 20, Width = 800, Height = 600 },
                WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.RememberWindowPosition);
    }

    [AvaloniaFact]
    public void Constructor_DefaultsRememberWindowPositionToFalseWhenSectionMissing()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.RememberWindowPosition);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsRememberWindowPosition_WithoutDisturbingExistingGeometry()
    {
        // MainWindow (not this dialog) owns Left/Top/Width/Height -- Save must preserve them as-is,
        // only ever touching the RememberWindowPosition flag itself.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                WindowGeometrySettings.SectionKey,
                new WindowGeometrySettings { RememberWindowPosition = false, Left = 10, Top = 20, Width = 800, Height = 600 },
                WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.RememberWindowPosition = true;
        await vm.SaveCommand.ExecuteAsync(null);

        var geometry = settingsStore.Settings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
        Assert.True(geometry?.RememberWindowPosition);
        Assert.Equal(10, geometry?.Left);
        Assert.Equal(20, geometry?.Top);
        Assert.Equal(800, geometry?.Width);
        Assert.Equal(600, geometry?.Height);
    }

    [AvaloniaFact]
    public void ResetGeneralToDefaultCommand_ClearsRememberWindowPosition()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                WindowGeometrySettings.SectionKey,
                new WindowGeometrySettings { RememberWindowPosition = true },
                WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.RememberWindowPosition);

        vm.ResetGeneralToDefaultCommand.Execute(null);

        Assert.False(vm.RememberWindowPosition);
    }

    [AvaloniaFact]
    public void ResetGeneralToDefaultCommand_LeavesSelectedCultureSetInsteadOfBlankingIt()
    {
        // Tier B audit finding: OptionsSettingsService.Defaults.CultureCode is always null (the
        // record default), so a bare FirstOrDefault-by-that-code always misses and used to blank the
        // Language ComboBox on every Reset -- ApplyFromSnapshot already falls back to CurrentCulture
        // for the identical reason; Reset now does too.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.ResetGeneralToDefaultCommand.Execute(null);

        Assert.NotNull(vm.SelectedCulture);
    }

    [AvaloniaFact]
    public void Constructor_LoadsJpegQualityFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ImageExportSettings.SectionKey,
                new ImageExportSettings { JpegQuality = 60 },
                ImageExportSettingsJsonContext.Default.ImageExportSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(60, vm.JpegQuality);
    }

    [AvaloniaFact]
    public void Constructor_DefaultsJpegQualityTo85WhenSectionMissing()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(85, vm.JpegQuality);
    }

    [AvaloniaFact]
    public void Constructor_OutOfRangePersistedJpegQuality_IsClamped()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ImageExportSettings.SectionKey,
                new ImageExportSettings { JpegQuality = 0 },
                ImageExportSettingsJsonContext.Default.ImageExportSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, vm.JpegQuality);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsJpegQuality_WithoutDisturbingWindowGeometry()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings()
                .WithSection(WindowGeometrySettings.SectionKey, new WindowGeometrySettings { RememberWindowPosition = true, Left = 10, Top = 20, Width = 800, Height = 600 }, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings)
                .WithSection(ImageExportSettings.SectionKey, new ImageExportSettings { JpegQuality = 60 }, ImageExportSettingsJsonContext.Default.ImageExportSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.JpegQuality = 30;
        await vm.SaveCommand.ExecuteAsync(null);

        var quality = settingsStore.Settings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings)?.JpegQuality;
        Assert.Equal(30, quality);
        var geometry = settingsStore.Settings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
        Assert.True(geometry?.RememberWindowPosition);
        Assert.Equal(10, geometry?.Left);
    }

    [AvaloniaFact]
    public void ResetGeneralToDefaultCommand_ResetsJpegQualityTo85()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                ImageExportSettings.SectionKey,
                new ImageExportSettings { JpegQuality = 12 },
                ImageExportSettingsJsonContext.Default.ImageExportSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(12, vm.JpegQuality);

        vm.ResetGeneralToDefaultCommand.Execute(null);

        Assert.Equal(85, vm.JpegQuality);
    }

    [AvaloniaFact]
    public void Constructor_NoStationIdSection_DefaultsToOffAndLegacyCwDefaults()
    {
        // StationIdSettings.CwIdMode default (Off), DefaultCwWpm (28), DefaultCwToneFrequencyHz
        // (1000) -- matching legacy's own compiled-in defaults (Main.cpp:902-907).
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(CwIdMode.Off, vm.CwIdMode);
        Assert.True(vm.IsIdMethodOffSelected);
        Assert.False(vm.IsIdMethodCwSelected);
        Assert.Equal("DE %m", vm.CwText);
        Assert.Equal(28, vm.CwWpm);
        Assert.Equal(1000.0, vm.CwToneFrequencyHz);
        Assert.False(vm.FskIdTxEnabled);
        Assert.False(vm.FskIdRxEnabled);
        // StationIdSettings.DefaultNrRstEnabled is true (LogFile.cpp:378's real legacy default,
        // the one field on that record whose default is "on") -- unlike every CW-ID/FSK-ID field
        // above, which default to Off/false.
        Assert.True(vm.NrRstEnabled);
        Assert.Null(vm.NrRstText);
    }

    [AvaloniaFact]
    public void IsIdMethodCwSelected_Set_UpdatesCwIdModeAndTheSiblingProperty()
    {
        // Same computed-bool-radio-group idiom as IsSenseLevelXSelected/etc -- setting one to true
        // switches the underlying enum, which flips the OTHER computed property too.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.IsIdMethodCwSelected = true;

        Assert.Equal(CwIdMode.Cw, vm.CwIdMode);
        Assert.True(vm.IsIdMethodCwSelected);
        Assert.False(vm.IsIdMethodOffSelected);
    }

    [AvaloniaFact]
    public void IsIdMethodCwSelected_Set_RaisesPropertyChangedForItselfAndTheOffSibling()
    {
        // OptionsWindowView.axaml's CW text/frequency/speed sub-panel binds
        // IsVisible="{Binding IsIdMethodCwSelected}" -- a passing bool-value assertion alone (the
        // test above) doesn't prove that binding actually re-evaluates on toggle; only a real
        // PropertyChanged notification does.
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.IsIdMethodCwSelected = true;

        Assert.Contains(nameof(vm.IsIdMethodCwSelected), raised);
        Assert.Contains(nameof(vm.IsIdMethodOffSelected), raised);
    }

    [AvaloniaFact]
    public void Constructor_OutOfRangeCwIdModeInSettings_ClampsToOff()
    {
        // ApplyFromSnapshot's Enum.IsDefined guard -- a hand-edited/future-downgrade settings.json
        // could carry an enum value this build doesn't know about.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                StationIdSettings.SectionKey,
                new StationIdSettings { CwIdMode = (CwIdMode)99 },
                StationIdSettingsJsonContext.Default.StationIdSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(CwIdMode.Off, vm.CwIdMode);
        Assert.True(vm.IsIdMethodOffSelected);
    }

    [AvaloniaFact]
    public void ResetIdentificationToDefault_RestoresEveryField()
    {
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(new FakeSettingsStore(), NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), new FakeSettingsStore(), new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.CwIdMode = CwIdMode.Cw;
        vm.CwText = "DE OTHERCALL";
        vm.CwWpm = 40;
        vm.CwToneFrequencyHz = 600;
        vm.FskIdTxEnabled = true;
        vm.FskIdRxEnabled = true;
        vm.NrRstEnabled = false;
        vm.NrRstText = "599123";

        vm.ResetIdentificationToDefaultCommand.Execute(null);

        Assert.Equal(CwIdMode.Off, vm.CwIdMode);
        Assert.Equal("DE %m", vm.CwText);
        Assert.Equal(28, vm.CwWpm);
        Assert.Equal(1000.0, vm.CwToneFrequencyHz);
        Assert.False(vm.FskIdTxEnabled);
        Assert.False(vm.FskIdRxEnabled);
        Assert.True(vm.NrRstEnabled);
        Assert.Null(vm.NrRstText);
    }

    [AvaloniaFact]
    public async Task SaveAsync_PersistsIdentificationFields_ReloadedCorrectlyOnNextConstruction()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.CwIdMode = CwIdMode.Cw;
        vm.CwText = "DE %m";
        vm.CwWpm = 22;
        vm.CwToneFrequencyHz = 700;
        vm.FskIdTxEnabled = true;
        vm.FskIdRxEnabled = true;
        vm.NrRstEnabled = false;
        vm.NrRstText = "599123";

        await vm.SaveCommand.ExecuteAsync(null);

        var reloaded = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(CwIdMode.Cw, reloaded.CwIdMode);
        Assert.Equal("DE %m", reloaded.CwText);
        Assert.Equal(22, reloaded.CwWpm);
        Assert.Equal(700.0, reloaded.CwToneFrequencyHz);
        Assert.True(reloaded.FskIdTxEnabled);
        Assert.True(reloaded.FskIdRxEnabled);
        Assert.False(reloaded.NrRstEnabled);
        Assert.Equal("599123", reloaded.NrRstText);
    }

    /// <summary>Unlike <see cref="SaveAsync_PersistsIdentificationFields_ReloadedCorrectlyOnNextConstruction"/>'s
    /// non-empty round-trip, this specifically pins the "no trap" claim in
    /// `OptionsSettingsService.SaveAsync`'s own comment: <see cref="StationIdSettings.NrRstText"/>
    /// has no "?? DefaultXxx" fallback at the `LoadAsync` read site the way <see cref="CwText"/>
    /// does, so clearing the TextBox to <see langword="null"/> and saving must persist an actually
    /// empty value, not silently resurrect old text on the next load.</summary>
    [AvaloniaFact]
    public async Task SaveAsync_ClearedNrRstText_PersistsAsNullNotResurrectedOnReload()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                StationIdSettings.SectionKey,
                new StationIdSettings { NrRstEnabled = true, NrRstText = "599123" },
                StationIdSettingsJsonContext.Default.StationIdSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("599123", vm.NrRstText); // loaded correctly before the clear

        vm.NrRstText = null;
        await vm.SaveCommand.ExecuteAsync(null);

        var stationId = settingsStore.Settings.GetSection(StationIdSettings.SectionKey, StationIdSettingsJsonContext.Default.StationIdSettings);
        Assert.Null(stationId!.NrRstText);

        var reloaded = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(reloaded.NrRstText);
    }

    [AvaloniaFact]
    public void Constructor_LoadsPersistedAdifUdpDestinations()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        new AdifUdpDestination { Enabled = true, Name = "GridTracker", Host = "127.0.0.1", Port = 2237 },
                        new AdifUdpDestination { Enabled = false, Name = "N1MM", Host = "192.168.1.50", Port = 2333 },
                    ],
                    ClientId = "TestClient",
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.AdifUdpDestinations.Count);
        Assert.True(vm.AdifUdpDestinations[0].Enabled);
        Assert.Equal("GridTracker", vm.AdifUdpDestinations[0].Name);
        Assert.Equal("127.0.0.1", vm.AdifUdpDestinations[0].Host);
        Assert.Equal(2237, vm.AdifUdpDestinations[0].Port);
        Assert.False(vm.AdifUdpDestinations[1].Enabled);
        Assert.Equal("N1MM", vm.AdifUdpDestinations[1].Name);
    }

    [AvaloniaFact]
    public void Constructor_NullElementInPersistedAdifUdpDestinations_SkipsItInsteadOfThrowing()
    {
        // Tier B audit finding: System.Text.Json will happily deserialize "Destinations": [null] into
        // a list containing a null element -- ApplyFromSnapshot used to dereference it unconditionally
        // and NRE, taking the whole load down (caught by LoadSafeAsync's outer try, but then nothing
        // in the dialog loads at all). AdifUdpStreamer.SendAsync's own `d?.Enabled == true` read of
        // the same data already tolerates this; matched here by skipping the null row entirely.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings
                {
                    Destinations =
                    [
                        null!,
                        new AdifUdpDestination { Enabled = true, Name = "GridTracker", Host = "127.0.0.1", Port = 2237 },
                    ],
                },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        var destination = Assert.Single(vm.AdifUdpDestinations);
        Assert.Equal("GridTracker", destination.Name);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    /// <summary>The dialog must show exactly what <c>AdifUdpStreamer</c> will actually send --
    /// see <c>AdifUdpStreamingSettings.MigrateIfNeeded</c>'s own doc comment. An upgrading user's
    /// already-working legacy GridTracker config must appear here as a real, visible destination
    /// row, not silently show an empty list while forwarding keeps happening underneath.</summary>
    [AvaloniaFact]
    public void Constructor_NoNewSectionButLegacyGridTrackerEnabled_MigratesOneDestinationRow()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                LegacyGridTrackerStreamingSettings.SectionKey,
                new LegacyGridTrackerStreamingSettings { Enabled = true, Host = "192.168.1.99", Port = 9999 },
                AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        var destination = Assert.Single(vm.AdifUdpDestinations);
        Assert.True(destination.Enabled);
        Assert.Equal("GridTracker", destination.Name);
        Assert.Equal("192.168.1.99", destination.Host);
        Assert.Equal(9999, destination.Port);
    }

    [AvaloniaFact]
    public void AddAdifUdpDestinationCommand_AppendsABlankRowWithAWorkingRemoveCommand()
    {
        var settingsStore = new FakeSettingsStore();
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.AdifUdpDestinations);

        vm.AddAdifUdpDestinationCommand.Execute(null);

        var row = Assert.Single(vm.AdifUdpDestinations);
        Assert.False(row.Enabled);
        Assert.Null(row.Name);
        Assert.NotNull(row.RemoveCommand);

        row.RemoveCommand!.Execute(row);
        Assert.Empty(vm.AdifUdpDestinations);
    }

    [AvaloniaFact]
    public async Task SaveCommand_PersistsAdifUdpDestinations_AndPreservesClientIdWithNoDialogControl()
    {
        // ClientId has no dialog control (same "preserve previous" contract as
        // SstvDecoderSettings.AfcEnabled above) -- a naive full-section overwrite on Save would
        // silently null a hand-set ClientId.
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings { Destinations = [], ClientId = "MyClientId" },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.AddAdifUdpDestinationCommand.Execute(null);
        vm.AdifUdpDestinations[0].Enabled = true;
        vm.AdifUdpDestinations[0].Name = "GridTracker";
        vm.AdifUdpDestinations[0].Host = "127.0.0.1";
        vm.AdifUdpDestinations[0].Port = 2237;

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = settingsStore.Settings.GetSection(AdifUdpStreamingSettings.SectionKey, AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings);
        Assert.NotNull(saved);
        Assert.Equal("MyClientId", saved!.ClientId); // preserved, not wiped
        var destination = Assert.Single(saved.Destinations!);
        Assert.True(destination.Enabled);
        Assert.Equal("GridTracker", destination.Name);
        Assert.Equal("127.0.0.1", destination.Host);
        Assert.Equal(2237, destination.Port);
    }

    /// <summary>Auditor code-review finding: the test above seeds the NEW section directly, so
    /// `previousAdifUdp` resolves through `MigrateIfNeeded`'s `ContainsKey` branch -- a naive
    /// `GetSection(...) ?? new AdifUdpStreamingSettings()` would pass that test identically. This is
    /// the actual scenario `OptionsSettingsService.SaveAsync`'s own doc comment justifies itself
    /// with: only a LEGACY GridTracker section exists on disk, the user opens the dialog and hits
    /// Save WITHOUT touching the Forwarding tab at all -- the migrated ClientId/destination must
    /// still be what gets persisted, not silently dropped back to an empty section.</summary>
    [AvaloniaFact]
    public async Task SaveCommand_OnlyLegacyGridTrackerSectionOnDisk_SaveWithNoEditsPersistsTheMigratedConfig()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                LegacyGridTrackerStreamingSettings.SectionKey,
                new LegacyGridTrackerStreamingSettings { Enabled = true, Host = "192.168.1.99", Port = 9999, ClientId = "Legacy" },
                AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.AdifUdpDestinations); // migration already visible in the dialog before any edit

        await vm.SaveCommand.ExecuteAsync(null); // no edits to the Forwarding tab at all

        var saved = settingsStore.Settings.GetSection(AdifUdpStreamingSettings.SectionKey, AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings);
        Assert.NotNull(saved);
        Assert.Equal("Legacy", saved!.ClientId);
        var destination = Assert.Single(saved.Destinations!);
        Assert.True(destination.Enabled);
        Assert.Equal("GridTracker", destination.Name);
        Assert.Equal("192.168.1.99", destination.Host);
        Assert.Equal(9999, destination.Port);
    }

    [AvaloniaFact]
    public void ResetForwardingToDefaultCommand_ClearsAllDestinationRows()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AdifUdpStreamingSettings.SectionKey,
                new AdifUdpStreamingSettings { Destinations = [new AdifUdpDestination { Enabled = true, Name = "X", Host = "127.0.0.1", Port = 1234 }] },
                AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings),
        };
        var vm = new OptionsWindowViewModel(new OptionsSettingsService(settingsStore, NullLogger<OptionsSettingsService>.Instance), new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.AdifUdpDestinations);

        vm.ResetForwardingToDefaultCommand.Execute(null);

        Assert.Empty(vm.AdifUdpDestinations);
    }
}
