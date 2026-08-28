using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// Configurations-preset backlog, Phase 1 (2026-08-28): <see cref="SstvSessionService.RequestCaptureDeviceAsync"/>
/// applies a new RX capture device live, without an app restart -- previously the one setting with no
/// live-apply path at all. Same shape/precedent as <see cref="SstvSessionServiceSampleRateLiveApplyTests"/>
/// (that class's own doc comment covers the shared stop-then-commit-then-restart reasoning); this
/// class additionally covers the device-resolution failure/rollback path, which the sample-rate
/// feature has no analogue to (a rate can't "not exist").
/// </summary>
public sealed class SstvSessionServiceCaptureDeviceLiveApplyTests
{
    private static readonly AudioDeviceInfo Device1 = new("capture-1", "Capture One", 1, 0, [8000]);
    private static readonly AudioDeviceInfo Device2 = new("capture-2", "Capture Two", 1, 0, [8000]);

    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeAudioDeviceEnumerator Enumerator, FakeSettingsStore SettingsStore) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var enumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [Device1, Device2],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [8000])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = Device1.Id, CaptureDeviceName = Device1.Name, PlaybackDeviceId = "playback-1" },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder { SampleRate = 8000 };
        var encoder = new FakeSstvEncoder { SampleRate = 8000 };
        var waterfall = new FakeWaterfallSource { SampleRate = 8000 };
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(audioEngine, enumerator, deviceMuteQuery, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, enumerator, settingsStore);
    }

    private static AudioDeviceSettings ReadAudioSettings(FakeSettingsStore settingsStore) =>
        settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)!;

    [Fact]
    public async Task RequestCaptureDeviceAsync_SameDeviceAlreadyActive_ReturnsNoChange_TouchesNothing()
    {
        var (service, audioEngine, _, settingsStore) = CreateService();
        await service.StartReceivingAsync(); // latches _activeCaptureDeviceId to Device1.Id

        var result = await service.RequestCaptureDeviceAsync(Device1.Id, Device1.Name);

        Assert.Equal(CaptureDeviceApplyResult.NoChange, result);
        Assert.True(audioEngine.IsCapturing); // never stopped
        Assert.Equal(Device1.Id, ReadAudioSettings(settingsStore).CaptureDeviceId); // never rewritten
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_NotReceiving_CommitsImmediately_NoCaptureCalls()
    {
        var (service, audioEngine, _, settingsStore) = CreateService();

        var result = await service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name);

        Assert.Equal(CaptureDeviceApplyResult.Applied, result);
        Assert.False(audioEngine.IsCapturing); // never opened -- nothing to coordinate
        Assert.Equal(Device2.Id, ReadAudioSettings(settingsStore).CaptureDeviceId);
        Assert.Equal(Device2.Name, ReadAudioSettings(settingsStore).CaptureDeviceName);
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_WhileReceiving_StopsThenReopensWithTheNewDevice()
    {
        var (service, audioEngine, _, settingsStore) = CreateService();
        await service.StartReceivingAsync();
        Assert.Equal(Device1.Id, audioEngine.LastRequestedCaptureDevice!.Id);

        var result = await service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name);

        Assert.Equal(CaptureDeviceApplyResult.Applied, result);
        Assert.True(audioEngine.IsCapturing); // reopened afterward
        Assert.Equal(Device2.Id, audioEngine.LastRequestedCaptureDevice!.Id);
        Assert.Equal(Device2.Id, ReadAudioSettings(settingsStore).CaptureDeviceId);
        Assert.Equal(Device2.Name, ReadAudioSettings(settingsStore).CaptureDeviceName);
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_RecordingInProgress_ReturnsDeferred_ChangesNothing()
    {
        var (service, audioEngine, _, settingsStore) = CreateService();
        await service.StartReceivingAsync();
        await service.StartRecordingAsync("/tmp/does-not-matter.wav");

        var result = await service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name);

        Assert.Equal(CaptureDeviceApplyResult.DeferredRecordingInProgress, result);
        Assert.Equal(Device1.Id, audioEngine.LastRequestedCaptureDevice!.Id); // untouched
        Assert.True(audioEngine.IsCapturing); // untouched -- the recording (and reception) is still live
        Assert.Equal(Device1.Id, ReadAudioSettings(settingsStore).CaptureDeviceId); // never rewritten
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_WhileRecordingInProgress_StartRecordingRefusesDuringTheChange()
    {
        // Round-1 plan-review finding 4's TOCTOU close, from the OTHER direction -- mirrors
        // SstvSessionServiceSampleRateLiveApplyTests' own equivalent test for _sampleRateChangeInProgress.
        // FakeAudioEngine.OnStopCaptureAsync fires at the exact point StopReceivingLockedAsync (the
        // FIRST step of the commit sequence) actually stops capture -- _captureDeviceChangeInProgress
        // is already true by then (set before ApplyCaptureDeviceLockedAsync is ever called).
        var (service, audioEngine, _, _) = CreateService();
        await service.StartReceivingAsync();

        // Code-review round-1 finding: asserting only the exception TYPE is weaker than the test's
        // own name claims -- StartRecordingAsync has 4 distinct InvalidOperationException throw
        // sites (not receiving, already recording, a rate change in progress, a device change in
        // progress). Assert the MESSAGE too, so this test fails for the right reason if that
        // ordering ever changes and a DIFFERENT guard fires instead.
        string? caughtMessage = null;
        audioEngine.OnStopCaptureAsync = () =>
        {
            try
            {
                service.StartRecordingAsync("/tmp/racing.wav");
            }
            catch (InvalidOperationException ex)
            {
                caughtMessage = ex.Message;
            }

            return Task.CompletedTask;
        };

        var result = await service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name);

        Assert.Contains("capture device change", caughtMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CaptureDeviceApplyResult.Applied, result);
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_RequestedDeviceFailsToOpen_RollsBackToPreviousDevice_ReturnsRejected()
    {
        // Code-review round-1 finding: the ORIGINAL catch filter (`InvalidOperationException` alone)
        // missed this -- the REALISTIC swap failure. A device can ENUMERATE (so ResolveDeviceAsync
        // succeeds, no throw there) but still fail to actually OPEN (exclusive use, an unsupported
        // rate, unplugged in the gap between resolve and open) -- MiniAudioEngine wraps every native
        // capture-open failure into AudioDeviceUnavailableException, not InvalidOperationException.
        var (service, audioEngine, _, settingsStore) = CreateService();
        await service.StartReceivingAsync();

        audioEngine.StartCaptureExceptionToThrowOnce = new AudioDeviceUnavailableException("Device busy (simulated).");

        var result = await service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name);

        Assert.Equal(CaptureDeviceApplyResult.Rejected, result);
        Assert.True(audioEngine.IsCapturing); // rolled-back restart succeeded (the one-shot exception was already consumed)
        Assert.Equal(Device1.Id, audioEngine.LastRequestedCaptureDevice!.Id); // restored to the PREVIOUS device
        Assert.Equal(Device1.Id, ReadAudioSettings(settingsStore).CaptureDeviceId); // settings rolled back too
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_PersistingTheNewDeviceFails_RestoresCaptureAtOldDevice_ThenRethrows()
    {
        // Code-review round-1 finding: persisting the new device (disk full/permission denied) used
        // to be outside any try/catch -- capture was already stopped by that point, so an uncaught
        // persist failure left RX silently dead with no restart attempt. Now: best-effort restore RX
        // at the OLD device, then let the ORIGINAL persist exception propagate (not silently
        // converted into a Rejected result -- a persist failure is a different problem than "device
        // unresolvable").
        var (service, audioEngine, _, settingsStore) = CreateService();
        await service.StartReceivingAsync();

        settingsStore.SaveAsyncExceptionOnce = new IOException("Disk full (simulated).");

        var ex = await Assert.ThrowsAsync<IOException>(() => service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name));

        Assert.Contains("Disk full", ex.Message);
        Assert.True(audioEngine.IsCapturing); // best-effort restart still happened despite the persist failure
        Assert.Equal(Device1.Id, audioEngine.LastRequestedCaptureDevice!.Id); // restored to the OLD device
        Assert.Equal(Device1.Id, ReadAudioSettings(settingsStore).CaptureDeviceId); // never actually rewritten -- SaveAsync threw before updating
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_RequestedDeviceUnresolvable_RollsBackToPreviousDevice_ReturnsRejected()
    {
        // The one failure mode with no sample-rate analogue: the requested device (and every
        // fallback the resolver tries) can't be resolved to anything at all. OnRefreshAsync fires
        // once per resolve attempt inside RequestCaptureDeviceAsync's own commit sequence -- the 2nd
        // overall call (1st was StartReceivingAsync above) is the FAILING resolve for Device2 (devices
        // list left empty); the 3rd is the ROLLBACK resolve for Device1 (devices list restored there).
        var (service, audioEngine, enumerator, settingsStore) = CreateService();
        await service.StartReceivingAsync(); // RefreshAsync call #1

        var refreshCallCount = 0;
        enumerator.OnRefreshAsync = () =>
        {
            refreshCallCount++;
            if (refreshCallCount == 1)
            {
                enumerator.InputDevices = []; // nothing resolvable for the requested device
            }
            else if (refreshCallCount == 2)
            {
                enumerator.InputDevices = [Device1]; // restored for the rollback attempt
            }
        };

        var result = await service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name);

        Assert.Equal(CaptureDeviceApplyResult.Rejected, result);
        Assert.True(audioEngine.IsCapturing); // rolled-back restart succeeded
        Assert.Equal(Device1.Id, audioEngine.LastRequestedCaptureDevice!.Id); // restored to the PREVIOUS device
        Assert.Equal(Device1.Id, ReadAudioSettings(settingsStore).CaptureDeviceId); // settings rolled back too, not left on the failed request
        Assert.Equal(Device1.Name, ReadAudioSettings(settingsStore).CaptureDeviceName);
    }

    [Fact]
    public async Task RequestCaptureDeviceAsync_NeitherRequestedNorPreviousDeviceResolvable_Throws()
    {
        // The genuinely unrecoverable case (round-1 plan-review finding 5's "throw, don't return an
        // enum pretending it's a normal outcome" precedent, matching HandleSampleRateBusyAsync's own
        // final-failure shape) -- nothing resolves for EITHER the requested OR the rollback attempt.
        var (service, _, enumerator, _) = CreateService();
        await service.StartReceivingAsync();
        enumerator.InputDevices = []; // stays empty for every subsequent resolve attempt

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RequestCaptureDeviceAsync(Device2.Id, Device2.Name));
    }
}
