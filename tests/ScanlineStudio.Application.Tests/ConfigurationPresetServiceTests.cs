using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Localization;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// Configurations-preset backlog, Phase 3 (2026-08-28): <see cref="ConfigurationPresetService.SwitchToPresetAsync"/>
/// -- the orchestrator that pushes a saved preset's settings live across every subsystem. 2 plan-
/// review rounds found and fixed 4 real corrections before this code existed: a MERGE, not a
/// wholesale settings.json overwrite (round-2 Correction 1); culture/UI-refresh returned to the
/// caller, never applied here (Correction 2); an up-front reject for TX-in-flight/recording, not a
/// late one (round-1 Defect + round-2 fix to the up-front-check design); a sequential-with-fallback
/// rate/device push and a per-DECISION Radio-section diff, not per-section (Correction 4). Each has
/// its own dedicated test below.
/// </summary>
public sealed class ConfigurationPresetServiceTests : IDisposable
{
    private const int InitialSampleRate = 8000;
    private const int NewSampleRate = 11025;

    private static readonly AudioDeviceInfo Device1 = new("capture-1", "Capture One", 1, 0, [8000, 11025]);
    private static readonly AudioDeviceInfo Device2 = new("capture-2", "Capture Two", 1, 0, [8000, 11025]);

    private readonly List<string> _presetDirectories = [];

    public void Dispose()
    {
        foreach (var dir in _presetDirectories)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private (ConfigurationPresetService Service, FakeSstvDecoder Decoder, FakeSstvEncoder Encoder, SstvSessionService SstvSession, FakeSettingsStore SettingsStore, FakeRadioSessionService RadioSession, ConfigurationPresetStore PresetStore, FakeAudioEngine AudioEngine)
        CreateService(AppSettings? initialSettings = null)
    {
        var presetsDirectory = Directory.CreateTempSubdirectory("yoniq-preset-switch-tests-").FullName;
        _presetDirectories.Add(presetsDirectory);
        var presetStore = new ConfigurationPresetStore(NullLogger<ConfigurationPresetStore>.Instance, presetsDirectory);

        var audioEngine = new FakeAudioEngine();
        var enumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [Device1, Device2],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [8000, 11025])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore { Settings = initialSettings ?? DefaultSettings() };
        var decoder = new FakeSstvDecoder { SampleRate = InitialSampleRate };
        var encoder = new FakeSstvEncoder { SampleRate = InitialSampleRate };
        var waterfall = new FakeWaterfallSource { SampleRate = InitialSampleRate };
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var sstvSession = new SstvSessionService(audioEngine, enumerator, deviceMuteQuery, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance);
        var service = new ConfigurationPresetService(presetStore, settingsStore, sstvSession, radioSession, NullLogger<ConfigurationPresetService>.Instance);

        return (service, decoder, encoder, sstvSession, settingsStore, radioSession, presetStore, audioEngine);
    }

    private static AppSettings DefaultSettings() =>
        new AppSettings().WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { CaptureDeviceId = Device1.Id, CaptureDeviceName = Device1.Name, PlaybackDeviceId = "playback-1", SampleRate = InitialSampleRate },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);

    private static readonly SstvModeDefinition TestMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    [Fact]
    public async Task SwitchToPresetAsync_Transmitting_RejectsOutright_TouchesNothing()
    {
        var (service, _, encoder, sstvSession, settingsStore, _, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings());
        var originalSettings = settingsStore.Settings;

        // Same hook idiom as SstvSessionServiceSampleRateLiveApplyTests' own encoder-bracket tests --
        // fires while TransmitAsync's own _transmitInFlight guard is genuinely still set, the exact
        // window IsTransmitting exists to detect.
        ConfigurationPresetSwitchResult? resultDuringTransmit = null;
        encoder.BeforeFirstYield = async () =>
        {
            resultDuringTransmit = await service.SwitchToPresetAsync("Test");
        };

        await sstvSession.TransmitAsync(TestMode, TestImage);

        Assert.NotNull(resultDuringTransmit);
        Assert.Equal(ConfigurationPresetSwitchOutcome.RejectedTransmitting, resultDuringTransmit!.Outcome);
        Assert.Equal(originalSettings, settingsStore.Settings); // nothing touched
    }

    [Fact]
    public async Task SwitchToPresetAsync_RecordingArmed_RejectsOutright_TouchesNothing()
    {
        var (service, _, _, sstvSession, settingsStore, _, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings());
        var originalSettings = settingsStore.Settings;

        await sstvSession.StartReceivingAsync();
        await sstvSession.StartRecordingAsync("/tmp/does-not-matter.wav");

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.RejectedRecording, result.Outcome);
        Assert.Equal(originalSettings, settingsStore.Settings); // nothing touched
    }

    [Fact]
    public async Task SwitchToPresetAsync_PresetDoesNotExist_ReturnsNotFound_TouchesNothing()
    {
        var (service, _, _, _, settingsStore, _, _, _) = CreateService();
        var originalSettings = settingsStore.Settings;

        var result = await service.SwitchToPresetAsync("Does Not Exist");

        Assert.Equal(ConfigurationPresetSwitchOutcome.PresetNotFound, result.Outcome);
        Assert.Equal(originalSettings, settingsStore.Settings);
    }

    [Fact]
    public async Task SwitchToPresetAsync_Merge_LeavesSectionsNotInThePresetUntouched()
    {
        // Correction 1: a MERGE, not a wholesale overwrite -- a section present in the CURRENT
        // settings but absent from the preset (e.g. never saved into it, or excluded by the store)
        // must survive the switch, not be deleted.
        var initial = DefaultSettings().WithSection(
            OperatorSettings.SectionKey, new OperatorSettings { Callsign = "N0CALL" }, OperatorSettingsJsonContext.Default.OperatorSettings);
        var (service, _, _, _, settingsStore, _, presetStore, _) = CreateService(initial);
        // Preset intentionally does NOT include OperatorSettings.
        await presetStore.SavePresetAsync("Test", new AppSettings());

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        var survived = settingsStore.Settings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings);
        Assert.Equal("N0CALL", survived?.Callsign);
    }

    [Fact]
    public async Task SwitchToPresetAsync_PresetCarriesQrzLookupSection_LivePasswordSurvivesTheSwitch()
    {
        // T0-8 blocker fix: ConfigurationPresetStore.Sanitize strips Password from every saved
        // preset (presets are user-shareable files) -- without carrying the live secret forward,
        // applying ANY preset with a QrzLookup section would silently wipe the stored password.
        // The preset's own QrzLookup section has no Password below, matching what
        // ConfigurationPresetStore actually produces post-T0-8 (already redacted on save).
        var initial = DefaultSettings().WithSection(
            QrzLookupSettings.SectionKey,
            new QrzLookupSettings { Enabled = true, Username = "N0CALL", Password = "hunter2" },
            QrzLookupSettingsJsonContext.Default.QrzLookupSettings);
        var (service, _, _, _, settingsStore, _, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            QrzLookupSettings.SectionKey,
            new QrzLookupSettings { Enabled = true, Username = "N9NEW" },
            QrzLookupSettingsJsonContext.Default.QrzLookupSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        var qrz = settingsStore.Settings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings);
        Assert.Equal("N9NEW", qrz?.Username); // the preset's own field wins
        Assert.Equal("hunter2", qrz?.Password); // the live secret survives the switch
    }

    [Fact]
    public async Task SwitchToPresetAsync_PresetCarriesQrzUploadSection_LiveApiKeySurvivesTheSwitch()
    {
        var initial = DefaultSettings().WithSection(
            QrzUploadSettings.SectionKey,
            new QrzUploadSettings { Enabled = true, ApiKey = "secret-api-key" },
            QrzUploadSettingsJsonContext.Default.QrzUploadSettings);
        var (service, _, _, _, settingsStore, _, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            QrzUploadSettings.SectionKey,
            new QrzUploadSettings { Enabled = false },
            QrzUploadSettingsJsonContext.Default.QrzUploadSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        var qrz = settingsStore.Settings.GetSection(QrzUploadSettings.SectionKey, QrzUploadSettingsJsonContext.Default.QrzUploadSettings);
        Assert.False(qrz?.Enabled); // the preset's own field wins
        Assert.Equal("secret-api-key", qrz?.ApiKey); // the live secret survives the switch
    }

    [Fact]
    public async Task SavePresetAsync_WithARealQrzLookupPassword_NeverWritesItToTheRawPresetFile()
    {
        // Code-review nit: ConfigurationPresetStore.Sanitize's own redaction is keyed by plain
        // string literals ("QrzLookup"/"Password") duplicating QrzLookupSettings' real property
        // names -- a rename of that property would silently stop redaction working, and neither
        // this file's own carry-forward tests nor ConfigurationPresetStoreTests' test-local-record
        // tests would catch that drift (both use presets with the secret already unset).
        // ScanlineStudio.Settings.Tests (where ConfigurationPresetStore itself is tested) has no
        // reference to Core.Logbook, so this test -- using the REAL QrzLookupSettings type end to
        // end -- lives here instead, standalone (not via CreateService's full harness, which this
        // doesn't need).
        var presetsDirectory = Directory.CreateTempSubdirectory("yoniq-preset-qrz-literal-tests-").FullName;
        _presetDirectories.Add(presetsDirectory);
        var presetStore = new ConfigurationPresetStore(Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationPresetStore>.Instance, presetsDirectory);

        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            QrzLookupSettings.SectionKey,
            new QrzLookupSettings { Enabled = true, Username = "N0CALL", Password = "hunter2" },
            QrzLookupSettingsJsonContext.Default.QrzLookupSettings));

        var rawText = await File.ReadAllTextAsync(Path.Combine(presetsDirectory, "Test.json"));
        Assert.DoesNotContain("Password", rawText, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", rawText, StringComparison.Ordinal);
        Assert.Contains("N0CALL", rawText, StringComparison.Ordinal); // Username itself must survive -- proves this isn't a whole-section drop
    }

    [Fact]
    public async Task SwitchToPresetAsync_SetsTheActivePresetMarker()
    {
        var (service, _, _, _, settingsStore, _, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Field Day", new AppSettings());

        await service.SwitchToPresetAsync("Field Day");

        var marker = settingsStore.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("Field Day", marker?.ActivePresetName);
    }

    [Fact]
    public async Task SwitchToPresetAsync_DecoderBundleChanged_PushesTheWholeBundle()
    {
        // All Request* calls asserted, not just some -- round-1 code-review nit: the original version
        // of this test only checked SenseLevel/AutoSyncEnabled/the reconfiguration bundle, so deleting
        // any of the RequestAutoStopEnabled/RequestAutoSlantEnabled/RequestSyncRestartEnabled lines in
        // PushDecoderChanges would still have passed. AutoStopEnabled/AutoSlantEnabled/SyncRestartEnabled
        // set to the OPPOSITE of both FakeSstvDecoder's own construction defaults and
        // SstvDecoderSettings.Resolve()'s own defaults, so a genuine change is actually exercised.
        // PLL tuning fields (Options stub backlog item 1) added the same way, non-default values.
        var (service, decoder, _, _, _, _, presetStore, _) = CreateService();
        var preset = new AppSettings().WithSection(
            SstvDecoderSettings.SectionKey,
            new SstvDecoderSettings
            {
                SenseLevel = 3, AutoSyncEnabled = false, AutoStopEnabled = true, AutoSlantEnabled = false,
                SyncRestartEnabled = false, RxBpfPreset = RxBpfPreset.Narrow, DemodType = DemodType.Pll, RxBufferMode = RxBufferMode.Off,
                PllVcoGain = 2.5, PllLoopOrder = 6, PllLoopCutoffHz = 1300, PllOutputOrder = 8, PllOutputCutoffHz = 850,
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Fir, ZeroCrossingOutputOrder = 9, ZeroCrossingOutputCutoffHz = 700, ZeroCrossingSmoothingFrequencyHz = 3000,
            },
            SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        await presetStore.SavePresetAsync("Test", preset);

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        Assert.Equal(3, decoder.SenseLevel);
        Assert.False(decoder.AutoSyncEnabled);
        Assert.True(decoder.AutoStopEnabled);
        Assert.False(decoder.AutoSlantEnabled);
        Assert.False(decoder.SyncRestartEnabled);
        Assert.Equal(1, decoder.RequestReconfigurationCallCount);
        Assert.Equal((RxBpfPreset.Narrow, DemodType.Pll, RxBufferMode.Off), decoder.LastRequestedReconfiguration);
        Assert.Equal(1, decoder.RequestPllTuningCallCount);
        Assert.Equal(2.5, decoder.LastPllVcoGain);
        Assert.Equal(6, decoder.LastPllLoopOrder);
        Assert.Equal(1300, decoder.LastPllLoopCutoffHz);
        Assert.Equal(8, decoder.LastPllOutputOrder);
        Assert.Equal(850, decoder.LastPllOutputCutoffHz);
        Assert.Equal(1, decoder.RequestZeroCrossingTuningCallCount);
        Assert.Equal(ZeroCrossingSmoothingMode.Fir, decoder.LastZeroCrossingSmoothingMode);
        Assert.Equal(9, decoder.LastZeroCrossingOutputOrder);
        Assert.Equal(700, decoder.LastZeroCrossingOutputCutoffHz);
        Assert.Equal(3000, decoder.LastZeroCrossingSmoothingFrequencyHz);
    }

    [Fact]
    public async Task SwitchToPresetAsync_DecoderBundleUnchanged_DoesNotPush()
    {
        // The decoder's own defaults already match SstvDecoderSettings' own Resolve() defaults --
        // an explicit-but-equal preset section must not trigger a redundant push.
        var (service, decoder, _, _, _, _, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            SstvDecoderSettings.SectionKey, new SstvDecoderSettings(), SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(0, decoder.RequestReconfigurationCallCount);
    }

    // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: auditor-caught round 1 -- without
    // this push, a preset switch left capture following the PREVIOUS preset's enable flag/directory
    // until an app restart, even though settingsStore/Options/the Gallery Storage card all already
    // show the new preset's values (ISstvSessionService.SetAutoSaveAudioEnabled/SetAudioDirectory are
    // volatile fields cached at the decode path, never re-read from settings on their own).

    [Fact]
    public async Task SwitchToPresetAsync_ReceiveHistorySectionPresent_LiveAppliesAutoSaveAudioEnabledAndTheResolvedDirectory()
    {
        // Behavioral, not a call-count assertion -- proves the live-apply actually took effect, not
        // just that some method was invoked. Directory is deliberately the RAW value a preset would
        // carry (never normalized the way SqliteReceiveHistoryStore.SetAudioSettingsAsync's own
        // Path.GetFullPath call normalizes a live Options edit) -- PushReceiveHistoryChanges must
        // resolve it itself via ReceiveHistorySettings.ResolveAudioDirectory, not assume it's already
        // resolved.
        var (service, decoder, _, sstvSession, _, _, presetStore, audioEngine) = CreateService();
        var presetAudioDirectory = Directory.CreateTempSubdirectory("scanlinestudio-preset-audio-test-").FullName;
        _presetDirectories.Add(presetAudioDirectory);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            ReceiveHistorySettings.SectionKey,
            new ReceiveHistorySettings { AutoSaveAudioEnabled = true, AudioDirectory = presetAudioDirectory },
            ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings));

        var result = await service.SwitchToPresetAsync("Test");
        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);

        decoder.SampleRate = 50;
        await sstvSession.StartReceivingAsync();

        // SetAutoSaveAudioEnabled(true) applied live: arming now happens on ModeDetected, which it
        // would not if the session were still following the OLD (disabled) preset's cached flag.
        decoder.RaiseModeDetected(SstvModeRegistry.Robot36);
        Assert.True(sstvSession.IsAudioAutoSaveActive);

        // SetAudioDirectory(<resolved>) applied live: the resulting scratch subtree lands under the
        // PRESET's own directory, not SstvSessionService's own DefaultAudioDirectory fallback. Waits
        // for the actual background encode+write, matching SstvSessionServiceAudioAutoSaveTests' own
        // WaitForAudioSliceReadyAsync pattern -- the scratch directory is only created inside that
        // background write, not synchronously on the capture-push thread.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sstvSession.AudioSliceReady += (_, _) => tcs.TrySetResult();
        audioEngine.PushCapturedSamples(new float[10]);
        audioEngine.PushCapturedSamples(new float[10]);
        decoder.RaiseDecodeRestarted(SstvModeRegistry.Robot36);
        using var cts = new CancellationTokenSource(2000);
        await using (cts.Token.Register(() => tcs.TrySetCanceled()))
        {
            await tcs.Task;
        }

        Assert.True(Directory.Exists(Path.Combine(presetAudioDirectory, "scratch")));
    }

    [Fact]
    public async Task SwitchToPresetAsync_RateChanged_AppliesTheNewRate()
    {
        // Assert against the DECODER, not settingsStore -- settingsStore already shows the preset's
        // own values right after MergeIntoLiveSettingsAsync, whether or not the live push actually
        // ran. decoder.SampleRate only changes if RequestSampleRateAsync genuinely applied it.
        var (service, decoder, _, _, _, _, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { CaptureDeviceId = Device1.Id, CaptureDeviceName = Device1.Name, SampleRate = NewSampleRate },
            AudioSettingsJsonContext.Default.AudioDeviceSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        Assert.Equal(NewSampleRate, decoder.SampleRate);
    }

    [Fact]
    public async Task SwitchToPresetAsync_DeviceChanged_AppliesTheNewDevice()
    {
        // Assert against the audio engine, not settingsStore -- same reasoning as the rate test
        // above. Receiving first so the live device push is actually observable: a NOT-receiving
        // ApplyCaptureDeviceLockedAsync only persists, never touches the audio engine at all.
        var (service, _, _, sstvSession, _, _, presetStore, audioEngine) = CreateService();
        await sstvSession.StartReceivingAsync();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { CaptureDeviceId = Device2.Id, CaptureDeviceName = Device2.Name, SampleRate = InitialSampleRate },
            AudioSettingsJsonContext.Default.AudioDeviceSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        Assert.Equal(Device2.Id, audioEngine.LastRequestedCaptureDevice!.Id);
    }

    [Fact]
    public async Task SwitchToPresetAsync_RateAlreadyLiveOnDecoder_StillPushesDeviceSeparately()
    {
        // Round-1 code-review fix: PushAudioChangesAsync no longer diffs against a "previous settings
        // snapshot" at all -- it calls RequestSampleRateAsync/RequestCaptureDeviceAsync
        // UNCONDITIONALLY whenever the AudioDeviceSettings section is present, relying entirely on
        // each callee's OWN authoritative live-state no-op guard. This test is now a REGRESSION GUARD
        // against reintroducing a diff-based shortcut: it engineers the decoder already sitting AT the
        // preset's own target rate (so RequestSampleRateAsync's own guard makes that call a no-op)
        // while the settings snapshot still shows the OLD rate -- exactly the scenario an EXCLUSIVE-OR
        // "only push device if rate didn't change" branch (rejected in round-2 plan-review, and the
        // MIRROR gap round-1 code-review additionally found: a diff-based branch can silently drop a
        // change in either direction, not just this one) would have silently dropped the device push.
        var presetsDirectory = Directory.CreateTempSubdirectory("yoniq-preset-switch-tests-").FullName;
        _presetDirectories.Add(presetsDirectory);
        var presetStore = new ConfigurationPresetStore(NullLogger<ConfigurationPresetStore>.Instance, presetsDirectory);
        var audioEngine = new FakeAudioEngine();
        var enumerator = new FakeAudioDeviceEnumerator { InputDevices = [Device1, Device2], OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [8000, 11025])] };
        var settingsStore = new FakeSettingsStore { Settings = DefaultSettings() }; // settings show InitialSampleRate + Device1
        var decoder = new FakeSstvDecoder { SampleRate = NewSampleRate }; // decoder ALREADY at the preset's target rate
        var encoder = new FakeSstvEncoder { SampleRate = NewSampleRate };
        var waterfall = new FakeWaterfallSource { SampleRate = NewSampleRate };
        var radioSession = new FakeRadioSessionService();
        var sstvSession = new SstvSessionService(audioEngine, enumerator, new FakeAudioDeviceMuteQuery(), settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, new FakeReceivedImageBuffer(), radioSession, new FakeCwIdDecoder(), NullLogger<SstvSessionService>.Instance);
        var service = new ConfigurationPresetService(presetStore, settingsStore, sstvSession, radioSession, NullLogger<ConfigurationPresetService>.Instance);

        // Receiving first -- otherwise ApplyCaptureDeviceLockedAsync's own not-receiving branch only
        // persists and never touches the audio engine, so a dropped device push would be
        // unobservable regardless of whether this test's own assertion is correct.
        await sstvSession.StartReceivingAsync();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { CaptureDeviceId = Device2.Id, CaptureDeviceName = Device2.Name, SampleRate = NewSampleRate },
            AudioSettingsJsonContext.Default.AudioDeviceSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        // Assert against the audio engine, not settingsStore -- MergeIntoLiveSettingsAsync already
        // writes the preset's Device2 into settingsStore regardless of whether the live push below
        // actually ran, so a settingsStore-only assertion here cannot distinguish "pushed live" from
        // "merged but silently dropped" -- exactly the gap mutation testing surfaced in this test.
        Assert.Equal(Device2.Id, audioEngine.LastRequestedCaptureDevice!.Id); // the device change was NOT silently dropped
    }

    [Fact]
    public async Task SwitchToPresetAsync_RadioSafetyChanged_Pushes()
    {
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            RadioSafetySettings.SectionKey, new RadioSafetySettings { SwrCutoffEnabled = true, SwrCutoffThreshold = 3.0 },
            RadioSafetySettingsJsonContext.Default.RadioSafetySettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(1, radioSession.SaveSafetySettingsCallCount);
        Assert.True(radioSession.SafetySpec.SwrCutoffEnabled);
        Assert.Equal(3.0, radioSession.SafetySpec.SwrCutoffThreshold);
    }

    [Fact]
    public async Task SwitchToPresetAsync_RadioSafetyUnchanged_DoesNotPush()
    {
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            RadioSafetySettings.SectionKey, new RadioSafetySettings(), RadioSafetySettingsJsonContext.Default.RadioSafetySettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(0, radioSession.SaveSafetySettingsCallCount);
    }

    [Fact]
    public async Task SwitchToPresetAsync_ConnectionSpecChanged_ReconnectsButDoesNotTouchHamlibPath()
    {
        // Correction 4's per-DECISION diff: a host-only change must reconnect WITHOUT also
        // triggering a Hamlib reload (each is a real, independent cost -- an unconditional reload
        // per switch would leak a native library load every time).
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            RadioConnectionSettings.SectionKey,
            new RadioConnectionSettings { BackendId = "rigctld", Host = "192.168.1.50", Port = 4532, HamlibLibraryPath = null },
            RadioSettingsJsonContext.Default.RadioConnectionSettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(1, radioSession.DisconnectCallCount);
        Assert.Equal(1, radioSession.ConnectUsingSettingsCallCount);
        Assert.Equal(0, radioSession.RequestHamlibLibraryPathCallCount);
    }

    [Fact]
    public async Task SwitchToPresetAsync_HamlibPathChanged_ReloadsButDoesNotReconnect()
    {
        var initial = DefaultSettings().WithSection(
            RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1, HamlibLibraryPath = null },
            RadioSettingsJsonContext.Default.RadioConnectionSettings);
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            RadioConnectionSettings.SectionKey,
            new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1, HamlibLibraryPath = "/opt/lib/libhamlib.so.4" },
            RadioSettingsJsonContext.Default.RadioConnectionSettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(0, radioSession.DisconnectCallCount); // same connection spec -- no reconnect
        Assert.Equal(0, radioSession.ConnectUsingSettingsCallCount);
        Assert.Equal(1, radioSession.RequestHamlibLibraryPathCallCount);
        Assert.Equal("/opt/lib/libhamlib.so.4", radioSession.LastRequestedHamlibLibraryPath);
    }

    [Fact]
    public async Task SwitchToPresetAsync_RadioConnectionUnchanged_TouchesNeitherReconnectNorHamlib()
    {
        var initial = DefaultSettings().WithSection(
            RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "none" },
            RadioSettingsJsonContext.Default.RadioConnectionSettings);
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "none" },
            RadioSettingsJsonContext.Default.RadioConnectionSettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(0, radioSession.DisconnectCallCount);
        Assert.Equal(0, radioSession.ConnectUsingSettingsCallCount);
        Assert.Equal(0, radioSession.RequestHamlibLibraryPathCallCount);
    }

    [Fact]
    public async Task SwitchToPresetAsync_HamlibPathNullVsEmpty_TreatedAsEquivalent_NoReload()
    {
        // Same null/blank-equivalence rule as OptionsWindowViewModel's own no-op guard -- a
        // TextBox-yields-"" vs. persisted-null difference alone must not trigger a real reload.
        var initial = DefaultSettings().WithSection(
            RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1, HamlibLibraryPath = null },
            RadioSettingsJsonContext.Default.RadioConnectionSettings);
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "hamlib", HamlibModel = 1, HamlibLibraryPath = "" },
            RadioSettingsJsonContext.Default.RadioConnectionSettings));

        await service.SwitchToPresetAsync("Test");

        Assert.Equal(0, radioSession.RequestHamlibLibraryPathCallCount);
    }

    [Fact]
    public async Task SwitchToPresetAsync_CultureChanged_ReturnedInResult_NotAppliedDirectly()
    {
        // Correction 2: the orchestrator must never call SetCultureAsync itself -- only report what
        // the caller (on the UI thread) should apply.
        var initial = DefaultSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "en" }, LocalizationSettingsJsonContext.Default.LocalizationSettings);
        var (service, _, _, _, _, _, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "de" }, LocalizationSettingsJsonContext.Default.LocalizationSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.True(result.CultureChanged);
        Assert.Equal("de", result.CultureToApply);
    }

    [Fact]
    public async Task SwitchToPresetAsync_CultureUnchanged_ResultHasNoCultureToApply()
    {
        var initial = DefaultSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "en" }, LocalizationSettingsJsonContext.Default.LocalizationSettings);
        var (service, _, _, _, _, _, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "en" }, LocalizationSettingsJsonContext.Default.LocalizationSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.False(result.CultureChanged);
        Assert.Null(result.CultureToApply);
    }

    [Fact]
    public async Task SwitchToPresetAsync_CultureExplicitlyResetToDefault_CultureChangedTrue_ButCultureToApplyIsNull()
    {
        // Round-1 code-review fix: the OLD formula (`newCulture != oldCulture ? newCulture : null`)
        // collapsed "changed TO null/default" and "unchanged" into the same CultureToApply == null
        // result -- a preset whose Localization section explicitly carries a null CultureCode (e.g.
        // saved after an Options reset-to-defaults) is a REAL change when the live culture is
        // non-null, but the OLD result gave the caller no way to tell that apart from "nothing to do."
        // CultureChanged is the only reliable signal now; CultureToApply legitimately stays null here
        // too, since the TARGET genuinely is null/default.
        var initial = DefaultSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "de" }, LocalizationSettingsJsonContext.Default.LocalizationSettings);
        var (service, _, _, _, _, _, presetStore, _) = CreateService(initial);
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = null }, LocalizationSettingsJsonContext.Default.LocalizationSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.True(result.CultureChanged);
        Assert.Null(result.CultureToApply);
    }

    [Fact]
    public async Task SwitchToPresetAsync_RadioSafetyPushThrows_CaughtSetsPartiallyApplied_LaterPushesStillRun()
    {
        // Round-1 code-review fix: a per-push try/catch. Before this fix, a thrown exception from ANY
        // push aborted every push after it -- while settings.json and the active-preset marker were
        // ALREADY fully committed to the new preset, and a retry is a no-op (the diff sees the
        // already-merged settings and finds nothing "changed"). Proven here: the RadioSafety push
        // throws, but the RadioConnection push (which runs AFTER it) still completes.
        var (service, _, _, _, _, radioSession, presetStore, _) = CreateService();
        radioSession.SaveSafetySettingsExceptionOnce = new InvalidOperationException("Simulated radio-safety save failure.");
        var preset = new AppSettings()
            .WithSection(RadioSafetySettings.SectionKey, new RadioSafetySettings { SwrCutoffEnabled = true, SwrCutoffThreshold = 3.0 }, RadioSafetySettingsJsonContext.Default.RadioSafetySettings)
            .WithSection(RadioConnectionSettings.SectionKey, new RadioConnectionSettings { BackendId = "rigctld", Host = "192.168.1.50", Port = 4532 }, RadioSettingsJsonContext.Default.RadioConnectionSettings);
        await presetStore.SavePresetAsync("Test", preset);

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome); // settings ARE still fully switched
        Assert.True(result.PartiallyApplied);
        Assert.Equal(1, radioSession.ConnectUsingSettingsCallCount); // the LATER push still ran despite the earlier throw
    }

    [Fact]
    public async Task SwitchToPresetAsync_AudioDeviceRejected_SetsPartiallyApplied()
    {
        // Round-1 code-review fix: a Rejected audio push (the preset's own capture device fails to
        // open) must be surfaced via PartiallyApplied, not silently discarded -- the original version
        // of PushAudioChangesAsync only inspected DeferredRecordingInProgress.
        var (service, _, _, sstvSession, _, _, presetStore, audioEngine) = CreateService();
        await sstvSession.StartReceivingAsync();
        audioEngine.StartCaptureExceptionToThrowOnce = new AudioDeviceUnavailableException("Device busy (simulated).");
        await presetStore.SavePresetAsync("Test", new AppSettings().WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { CaptureDeviceId = Device2.Id, CaptureDeviceName = Device2.Name, SampleRate = InitialSampleRate },
            AudioSettingsJsonContext.Default.AudioDeviceSettings));

        var result = await service.SwitchToPresetAsync("Test");

        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, result.Outcome);
        Assert.True(result.PartiallyApplied);
    }

    [Fact]
    public async Task SwitchToPresetAsync_ConcurrentCall_SecondIsRejected_FirstStillCompletes()
    {
        // Round-1 code-review fix: a single-flight guard. Without it, two concurrent switches could
        // both snapshot the same "previous" settings, both merge, both push, and the live subsystems
        // could end up a mix of both presets. settingsStore.Gate (a genuine, separately-controllable
        // TaskCompletionSource-backed park point) is used only to make the FIRST call's own suspension
        // deterministic -- it is NOT what makes the guard itself deterministic. That's simpler: the
        // Interlocked.CompareExchange guard is the very first statement in SwitchToPresetAsync, before
        // ANY await, so it always runs synchronously to completion on this thread before control ever
        // returns to this test -- regardless of where the first call's own first genuine suspension
        // point actually is (likely inside ConfigurationPresetStore.LoadPresetAsync's own real file
        // I/O, reached BEFORE settingsStore.LoadAsync is ever called, not the Gate below). Round-2
        // code-review finding: WaitAsync-bounded, not an unbounded await -- an unbounded version of
        // this test previously HUNG (rather than failing cleanly) when the guard itself was mutated
        // out during hand mutation-testing, in a suite that already has a known unrelated
        // test-host-crash flake under parallel load; a bound turns a future regression here into a
        // clean assertion failure instead of a wedged test host.
        var (service, _, _, _, settingsStore, _, presetStore, _) = CreateService();
        await presetStore.SavePresetAsync("Test", new AppSettings());

        var gate = new TaskCompletionSource();
        settingsStore.Gate = gate.Task;

        var firstTask = service.SwitchToPresetAsync("Test");

        var secondResult = await service.SwitchToPresetAsync("Test").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConfigurationPresetSwitchOutcome.RejectedConcurrentSwitch, secondResult.Outcome);

        gate.SetResult();
        var firstResult = await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConfigurationPresetSwitchOutcome.Applied, firstResult.Outcome); // the first call still completes cleanly
    }

    [Fact]
    public async Task SwitchToPresetAsync_PresetWithoutLocalizationSection_CultureChangedFalse()
    {
        // Round-2 code-review finding: no test previously covered the ABSENT-section case on its own
        // -- a future edit that simplified `newCulture` to a direct GetSection(...)?.CultureCode
        // (dropping the ternary that seeds it from oldCulture when the section is absent, but keeping
        // the `!=` comparison) would report CultureChanged: true for every preset lacking a
        // Localization section, silently resetting the user's language on every such switch, and the
        // rest of this suite would stay green.
        var initial = DefaultSettings().WithSection(
            LocalizationSettings.SectionKey, new LocalizationSettings { CultureCode = "de" }, LocalizationSettingsJsonContext.Default.LocalizationSettings);
        var (service, _, _, _, _, _, presetStore, _) = CreateService(initial);
        // Preset intentionally does NOT include a Localization section at all.
        await presetStore.SavePresetAsync("Test", new AppSettings());

        var result = await service.SwitchToPresetAsync("Test");

        Assert.False(result.CultureChanged);
        Assert.Null(result.CultureToApply);
    }
}
