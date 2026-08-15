using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

public sealed class SstvSessionServiceTests
{
    private static readonly bool[] PttOnThenOff = [true, false];
    private static readonly float[] ExpectedPlaybackSamples = [0.1f, 0.2f, 0.3f];
    private static readonly float[] TwoSamplePush = [1f, 2f];

    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 1,
        ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder,
        FakeWaterfallSource Waterfall, FakeRadioSessionService RadioSession, FakeSettingsStore SettingsStore)
        CreateService(string? captureDeviceId = "capture-1", string? playbackDeviceId = "playback-1")
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = captureDeviceId, PlaybackDeviceId = playbackDeviceId, SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder();
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, decoder, waterfall, radioSession, settingsStore);
    }

    // Renamed + re-commented (spec/18-path-to-1.0.md Critical item 1 / item 8, round-1 plan-review
    // finding 4): this now specifically tests "nothing configured AND no default device available
    // either" -- it stays passing only because CreateService's fixture capture device has
    // IsDefault left at its default (false), not because "nothing configured" alone still throws.
    // See DeviceFallback_NoDeviceConfigured_FallsBackToBackendReportedDefault below for the new
    // fallback-success path.
    [Fact]
    public async Task StartReceivingAsync_NoCaptureDeviceConfigured_AndNoDefaultDeviceAvailable_Throws()
    {
        var (service, _, _, _, _, _) = CreateService(captureDeviceId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartReceivingAsync());
    }

    [Fact]
    public async Task StartReceivingAsync_ValidDevice_StartsCaptureAndFansSamplesOutToDecoderAndWaterfall()
    {
        var (service, audioEngine, decoder, waterfall, _, _) = CreateService();

        await service.StartReceivingAsync();
        var samples = new float[] { 1f, 2f, 3f };
        audioEngine.PushCapturedSamples(samples);

        Assert.True(audioEngine.IsCapturing);
        Assert.Single(decoder.PushedSamples);
        Assert.Single(waterfall.PushedSamples);
        Assert.Equal(samples, decoder.PushedSamples[0].ToArray());
        Assert.Equal(samples, waterfall.PushedSamples[0].ToArray());
    }

    [Fact]
    public async Task StartReceivingAsync_ConfiguredCaptureThreadPriority_IsPassedToTheAudioEngine()
    {
        var (service, audioEngine, _, _, _, settingsStore) = CreateService();
        var current = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)!;
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with { CaptureThreadPriority = ThreadPriority.AboveNormal },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);

        await service.StartReceivingAsync();

        Assert.Equal(ThreadPriority.AboveNormal, audioEngine.LastRequestedDrainThreadPriority);
    }

    [Fact]
    public async Task StartReceivingAsync_NoConfiguredCaptureThreadPriority_PassesNullThroughUnchanged()
    {
        var (service, audioEngine, _, _, _, _) = CreateService();

        await service.StartReceivingAsync();

        Assert.Null(audioEngine.LastRequestedDrainThreadPriority);
    }

    [Fact]
    public async Task IsReceiving_ReflectsStartAndStop_ForTheHeaderReceivingToggle()
    {
        var (service, _, _, _, _, _) = CreateService();

        Assert.False(service.IsReceiving);

        await service.StartReceivingAsync();
        Assert.True(service.IsReceiving);

        await service.StopReceivingAsync();
        Assert.False(service.IsReceiving);
    }

    [Fact]
    public async Task StartAndStopReceivingAsync_BothResetDecoderAgc()
    {
        // ultracode audit finding #6: legacy resets AGC (CLVL::Init) at BOTH TX<->RX transition
        // directions (Sound.cpp:398,443) -- RX start (entering RX) and RX stop (entering TX) are
        // this port's equivalent transition points.
        var (service, _, decoder, _, _, _) = CreateService();

        await service.StartReceivingAsync();
        Assert.Equal(1, decoder.ResetAgcCallCount);

        await service.StopReceivingAsync();
        Assert.Equal(2, decoder.ResetAgcCallCount);
    }

    [Fact]
    public void DecoderRestartOverdue_RaisesMaintenanceWarningRaised()
    {
        // Ultracode audit finding #34's warning layer: FakeSstvDecoder implements
        // ISstvDecoderMaintenance, so SstvSessionService's constructor wires this up automatically.
        var (service, _, decoder, _, _, _) = CreateService();
        var warningRaised = 0;
        service.MaintenanceWarningRaised += () => warningRaised++;

        decoder.RaiseRestartOverdue();

        Assert.Equal(1, warningRaised);
    }

    [Fact]
    public void DecoderRestarted_AfterAWarningWasRaised_RaisesMaintenanceWarningCleared()
    {
        var (service, _, decoder, _, _, _) = CreateService();
        var cleared = 0;
        service.MaintenanceWarningCleared += () => cleared++;

        decoder.RaiseRestartOverdue();
        decoder.RaiseRestarted();

        Assert.Equal(1, cleared);
    }

    [Fact]
    public void DecoderRestarted_WithNoActiveWarning_DoesNotRaiseMaintenanceWarningCleared()
    {
        var (service, _, decoder, _, _, _) = CreateService();
        var cleared = 0;
        service.MaintenanceWarningCleared += () => cleared++;

        // A normal, unremarkable swap while idle -- no warning was ever active, so nothing to clear.
        decoder.RaiseRestarted();

        Assert.Equal(0, cleared);
    }

    [Fact]
    public async Task DecoderRestartCriticallyOverdue_StopsReceiving_AndRaisesMaintenanceCriticalStopRaised()
    {
        var (service, audioEngine, decoder, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        Assert.True(service.IsReceiving);

        var criticalStopRaised = 0;
        service.MaintenanceCriticalStopRaised += () => criticalStopRaised++;

        // Raised synchronously, same as the real RestartableSstvDecoder would from inside a live
        // SamplesCaptured invocation on the drain thread -- must not throw or deadlock.
        decoder.RaiseRestartCriticallyOverdue();

        Assert.False(service.IsReceiving);
        Assert.False(audioEngine.IsCapturing);
        Assert.Equal(1, criticalStopRaised);

        // Confirms the capture handler was actually unsubscribed (StopReceivingAsync's real effect),
        // not just that IsReceiving flipped -- pushing further samples must not reach the decoder.
        var pushedBefore = decoder.PushedSamples.Count;
        audioEngine.PushCapturedSamples(TwoSamplePush);
        Assert.Equal(pushedBefore, decoder.PushedSamples.Count);
    }

    [Fact]
    public void DecoderRestartCriticallyOverdue_WhileNotReceiving_DoesNotThrow()
    {
        // The real decoder can only cross the critical threshold while PushSamples is being called,
        // which only happens while receiving -- but a fake can raise the event at any time, and this
        // must still degrade gracefully (StopReceivingAsync already no-ops when not receiving).
        var (service, _, decoder, _, _, _) = CreateService();
        Assert.False(service.IsReceiving);

        decoder.RaiseRestartCriticallyOverdue();

        Assert.False(service.IsReceiving);
    }

    [Fact]
    public async Task PushSamples_DecoderThrows_WaterfallStillReceivesTheSamples()
    {
        var (service, audioEngine, decoder, waterfall, _, _) = CreateService();
        decoder.ThrowOnPush = new InvalidOperationException("simulated decoder failure");
        await service.StartReceivingAsync();

        audioEngine.PushCapturedSamples(TwoSamplePush);

        Assert.Single(decoder.PushedSamples);
        Assert.Single(waterfall.PushedSamples);
    }

    [Fact]
    public async Task PushSamples_WaterfallThrows_DecoderStillReceivesTheSamples()
    {
        var (service, audioEngine, decoder, waterfall, _, _) = CreateService();
        waterfall.ThrowOnPush = new InvalidOperationException("simulated waterfall failure");
        await service.StartReceivingAsync();

        audioEngine.PushCapturedSamples(TwoSamplePush);

        Assert.Single(decoder.PushedSamples);
        Assert.Single(waterfall.PushedSamples);
    }

    // Renamed + re-commented (spec/18-path-to-1.0.md Critical item 1 / item 8, round-1 plan-review
    // finding 4): now specifically "no default device available either" -- see the fallback-success
    // and no-radio-configured tests near the bottom of this file for the new behavior.
    [Fact]
    public async Task TransmitAsync_NoPlaybackDeviceConfigured_AndNoDefaultDeviceAvailable_Throws()
    {
        var (service, _, _, _, _, _) = CreateService(playbackDeviceId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));
    }

    [Fact]
    public async Task GetConfiguredPlaybackDeviceNameAsync_ValidDevice_ReturnsItsName()
    {
        var (service, _, _, _, _, _) = CreateService();

        var name = await service.GetConfiguredPlaybackDeviceNameAsync();

        Assert.Equal("Playback", name);
    }

    // Renamed (round-1 plan-review finding 4): "no default device available either."
    [Fact]
    public async Task GetConfiguredPlaybackDeviceNameAsync_NoPlaybackDeviceConfigured_AndNoDefaultDeviceAvailable_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _) = CreateService(playbackDeviceId: null);

        var name = await service.GetConfiguredPlaybackDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredPlaybackDeviceNameAsync_ConfiguredDeviceNoLongerPresent_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _) = CreateService(playbackDeviceId: "playback-vanished");

        var name = await service.GetConfiguredPlaybackDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_ValidDevice_ReturnsItsName()
    {
        var (service, _, _, _, _, _) = CreateService();

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Equal("Capture", name);
    }

    // Renamed (round-1 plan-review finding 4): "no default device available either."
    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_NoCaptureDeviceConfigured_AndNoDefaultDeviceAvailable_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _) = CreateService(captureDeviceId: null);

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_ConfiguredDeviceNoLongerPresent_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _) = CreateService(captureDeviceId: "capture-vanished");

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Null(name);
    }

    // spec/18-path-to-1.0.md Critical item 1 / item 8: the fresh-install fallback -- nothing
    // configured, but the backend reports a default device, so RX/TX both succeed instead of
    // throwing, and the display readouts show the real device that will actually be used.
    // CreateService's own device enumerator has no IsDefault device, so these three tests build a
    // fresh SstvSessionService directly (FakeAudioDeviceEnumerator is a constructor-only
    // dependency, no settable property to swap it in after the fact) sharing the same fake pieces
    // CreateService would otherwise have built.
    [Fact]
    public async Task StartReceivingAsync_NoCaptureDeviceConfigured_FallsBackToBackendReportedDefault()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000], IsDefault: true)],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = null, PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        await service.StartReceivingAsync();

        Assert.True(audioEngine.IsCapturing);
    }

    [Fact]
    public async Task TransmitAsync_NoPlaybackDeviceConfigured_FallsBackToBackendReportedDefault()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025], IsDefault: true)],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = null, SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples, audioEngine.PlaybackSamples);
    }

    [Fact]
    public async Task TransmitAsync_ConfiguredPlaybackDeviceMissing_StillThrows_EvenWhenADefaultDeviceExists()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025], IsDefault: true)],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-vanished", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        // A device the user explicitly configured going missing must still fail loudly -- never
        // silently substituted with the default, even though one exists (round-1 plan-review's own
        // explicit design confirmation).
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));
    }

    // Capture-side counterpart of the playback test immediately above -- same shared
    // TryResolveDeviceAsync/ResolveDeviceAsync code path (code-review nit on spec/18-path-to-1.0.md
    // Critical item 1: only the playback direction had a "configured device missing" regression
    // test).
    [Fact]
    public async Task StartReceivingAsync_ConfiguredCaptureDeviceMissing_StillThrows_EvenWhenADefaultDeviceExists()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000], IsDefault: true)],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-vanished", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartReceivingAsync());
    }

    [Fact]
    public async Task TransmitAsync_KeysPttOnThenOffAroundPlayback()
    {
        var (service, _, _, _, radioSession, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
    }

    // spec/18-path-to-1.0.md Critical item 1: the actual bug this whole fix targets -- previously
    // NoneRadioProtocol.SetPttAsync always throwing meant this exact scenario (RigId="none", the
    // real default for a fresh install) made every transmit fail before any audio was ever
    // produced. RigId="none" is a real value FakeRadioSessionService now supports specifically for
    // this test (see its own doc comment) -- every OTHER existing test in this file keeps using the
    // fake's non-"none" default and is unaffected.
    [Fact]
    public async Task TransmitAsync_NoRadioConfigured_StillProducesAudio_AndNeverCallsSetPtt()
    {
        var (service, audioEngine, _, _, radioSession, _) = CreateService();
        radioSession.RigId = "none";

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples, audioEngine.PlaybackSamples);
        Assert.Empty(radioSession.PttCalls);
    }

    [Fact]
    public async Task TransmitAsync_EncodesAndEnqueuesAllSamplesForPlayback()
    {
        var (service, audioEngine, _, _, _, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples, audioEngine.PlaybackSamples);
        Assert.False(audioEngine.IsPlaying);
    }

    [Fact]
    public async Task TransmitAsync_WhileReceiving_StopsCaptureDuringTxAndRestartsAfterward()
    {
        var (service, audioEngine, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        Assert.True(audioEngine.IsCapturing);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.True(audioEngine.IsCapturing, "capture should have been restarted after TX completed");
    }

    [Fact]
    public async Task TransmitAsync_WhileNotReceiving_DoesNotStartCaptureAfterward()
    {
        var (service, audioEngine, _, _, _, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(audioEngine.IsCapturing);
    }

    [Fact]
    public async Task SetPttLockAsync_Locked_KeysPttImmediatelyAndReportsLocked()
    {
        var (service, _, _, _, radioSession, _) = CreateService();

        await service.SetPttLockAsync(true);

        Assert.True(service.IsPttLocked);
        Assert.Equal([true], radioSession.PttCalls);
    }

    [Fact]
    public async Task SetPttLockAsync_LockedThenTransmit_DoesNotDoubleKeyAndLeavesPttKeyedAfterward()
    {
        var (service, audioEngine, _, _, radioSession, _) = CreateService();
        await service.StartReceivingAsync();

        await service.SetPttLockAsync(true);
        await service.TransmitAsync(TestMode, TestImage);

        // Exactly one PTT-on (from the lock engage) and no PTT-off at all -- the Transmit call must
        // not have keyed again on entry, nor un-keyed in its own cleanup while still locked.
        Assert.Equal([true], radioSession.PttCalls);
        Assert.True(service.IsPttLocked);
        // RX must not have been silently resumed either -- the operator is still "on the lock."
        Assert.False(audioEngine.IsCapturing);
    }

    [Fact]
    public async Task SetPttLockAsync_LockedThenUnlocked_UnkeysPttExactlyOnce()
    {
        var (service, _, _, _, radioSession, _) = CreateService();

        await service.SetPttLockAsync(true);
        await service.SetPttLockAsync(false);

        Assert.False(service.IsPttLocked);
        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
    }

    [Fact]
    public async Task SetPttLockAsync_DoubleLockOrDoubleUnlock_IsIdempotentInOutcome_NotInSuppressingCalls()
    {
        // Audit-fix regression test: an earlier version short-circuited when the requested state
        // already matched IsPttLocked, which is exactly the shape of bug fixed below (a no-op unlock
        // on a still-keyed rig). Every call now always issues the command -- redundant but harmless
        // (confirmed idempotent on every real protocol backend) -- so the OUTCOME (locked state, and
        // that the rig ends up correctly keyed/unkeyed) is what's asserted, not call suppression.
        var (service, _, _, _, radioSession, _) = CreateService();

        await service.SetPttLockAsync(true);
        await service.SetPttLockAsync(true); // already locked -- redundant re-key is fine
        Assert.True(service.IsPttLocked);
        Assert.Equal([true, true], radioSession.PttCalls);

        await service.SetPttLockAsync(false);
        await service.SetPttLockAsync(false); // already unlocked -- redundant re-unkey is fine
        Assert.False(service.IsPttLocked);
        Assert.Equal([true, true, false, false], radioSession.PttCalls);
    }

    [Fact]
    public async Task SetPttLockAsync_False_AlwaysAttemptsUnkey_EvenWhenAlreadyReportedUnlocked()
    {
        // The actual bug the fix above closes: TuneAsync(leaveKeyedAfterTune: true) leaves PTT
        // physically keyed WITHOUT ever setting _pttLocked -- so a caller unlocking afterward, under
        // the old short-circuit, would have silently no-op'd on a still-keyed rig with zero recovery
        // path. Now it always sends the command regardless of the tracked flag's current value.
        var (service, _, _, _, radioSession, _) = CreateService();

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);
        Assert.False(service.IsPttLocked); // tracked state says "not locked"...
        Assert.Equal([true], radioSession.PttCalls); // ...but the rig is still physically keyed

        await service.SetPttLockAsync(false);

        Assert.Equal([true, false], radioSession.PttCalls); // unlock still sent the real un-key command
    }

    [Fact]
    public async Task SwrCutoffStyleCancellation_ForceUnkeysAndClearsLock_EvenWhileLocked()
    {
        // Audit-fix regression test for the most severe finding: a lock must NEVER be able to defeat
        // an abnormal-termination path (SWR auto-cutoff / manual Stop TX both work by cancelling the
        // token passed into TransmitAsync/TuneAsync). Simulated here the same way the existing
        // TuneAsync_TokenCancelledMidTone_... regression test does -- a token cancelled mid-flight.
        var (service, audioEngine, _, _, radioSession, _) = CreateService();
        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);
        radioSession.PttCalls.Clear(); // isolate this test's own assertions from the lock-engage call above

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.TuneAsync(1750, TimeSpan.FromSeconds(5), ct: cts.Token));

        Assert.Equal([false], radioSession.PttCalls); // force-unkeyed despite the lock
        Assert.False(service.IsPttLocked, "a cutoff/cancel must force-clear the lock, not leave IsPttLocked lying about a rig that's now confirmed unkeyed");
        Assert.True(audioEngine.IsCapturing, "RX must resume after a force-unkey too, same as the normal cancellation path");
    }

    [Fact]
    public async Task UnlockAfterALockedTransmitPausedRx_ResumesRx()
    {
        // Audit-fix regression test: PlayWithPttAsync's own `wasReceiving` local is scoped to one
        // call and gets discarded once that call returns (still locked) -- without the
        // _rxPendingResumeAfterUnlock handoff, RX would stay stopped forever after this sequence.
        var (service, audioEngine, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();

        await service.SetPttLockAsync(true);
        await service.TransmitAsync(TestMode, TestImage); // pauses RX, then skips resume because locked
        Assert.False(audioEngine.IsCapturing);

        await service.SetPttLockAsync(false);

        Assert.True(audioEngine.IsCapturing, "RX should have been resumed once the lock covering it was released");
    }

    [Fact]
    public async Task DisposeAsync_WhilePttLocked_ForceUnkeysBeforeTearingDown()
    {
        // Audit-fix regression test: app shutdown must never leave a locked rig keyed indefinitely
        // just because nothing called SetPttLockAsync(false) first.
        var (service, _, _, _, radioSession, _) = CreateService();
        await service.SetPttLockAsync(true);
        radioSession.PttCalls.Clear();

        await service.DisposeAsync();

        Assert.Equal([false], radioSession.PttCalls);
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheDecoder_IfItIsDisposable()
    {
        // RX buffer subsystem Phase 7 (disposal-chain sub-piece): _decoder is ISstvDecoder-typed, not
        // IDisposable itself -- SstvSessionService.DisposeAsync duck-types the check (same pattern
        // already proven for Waterfall), matching the real production RestartableSstvDecoder, which
        // now implements IDisposable to tear down RxBufferMode.Extended's scratch files/background
        // writer task. FakeSstvDecoder implements IDisposable for exactly this test.
        var (service, _, decoder, _, _, _) = CreateService();

        await service.DisposeAsync();

        Assert.True(decoder.DisposedForTests);
    }

    [Fact]
    public async Task TransmitAsync_WithNoLockEngaged_BehavesExactlyAsBeforeThisFeature()
    {
        // Regression guard: introducing the lock must not change the un-locked default path.
        var (service, _, _, _, radioSession, _) = CreateService();

        Assert.False(service.IsPttLocked);
        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
        Assert.False(service.IsPttLocked);
    }

    [Fact]
    public async Task TuneAsync_TokenCancelledMidTone_StillUnkeysPttAndRestartsCapture()
    {
        // Regression test for a real bug found while designing Piece 6 (SWR auto-cutoff / Stop TX):
        // PlayWithPttAsync's cleanup previously reused the same (now-cancelled) token that triggered
        // the cancellation for its own PTT-off/resume-capture calls -- both would immediately throw
        // OperationCanceledException from their own WaitAsync(ct) on a real protocol, before ever
        // sending the PTT-off command, leaving the rig keyed and RX capture stopped indefinitely (the
        // exact opposite of what a safety cutoff exists to guarantee). FakeRadioSessionService.SetPttAsync
        // was made to actually honor cancellation (see its own doc comment) specifically so this test
        // can tell the fixed behavior (a fresh, non-cancelled cleanup token) apart from the bug.
        var (service, audioEngine, _, _, radioSession, _) = CreateService();
        await service.StartReceivingAsync();

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        // 5 seconds of generated tone at 48kHz (TuneAsync's fixed sample rate) gives the CPU-bound
        // sample loop -- with its periodic `await Task.Yield()` every 4096 samples -- ample real
        // wall-clock time to still be mid-generation when the 10ms cancellation fires; TransmitAsync's
        // own tiny 3-sample fixture completes too fast for this to land reliably, which is why this
        // regression uses TuneAsync instead.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.TuneAsync(1750, TimeSpan.FromSeconds(5), ct: cts.Token));

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
        Assert.True(audioEngine.IsCapturing, "capture should have been restarted after a cancelled Tune");
    }

    [Fact]
    public async Task TuneAsync_LeaveKeyedAfterTuneFalse_KeysThenUnkeysPtt_UnchangedDefaultBehavior()
    {
        var (service, audioEngine, _, _, radioSession, _) = CreateService();
        await service.StartReceivingAsync();

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
        Assert.True(audioEngine.IsCapturing, "RX should have resumed once the tune tone finished");
    }

    [Fact]
    public async Task TuneAsync_LeaveKeyedAfterTuneTrue_KeysPttAndDoesNotUnkeyOrResumeCapture()
    {
        var (service, audioEngine, _, _, radioSession, _) = CreateService();
        await service.StartReceivingAsync();

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);

        Assert.Equal([true], radioSession.PttCalls); // keyed, and never un-keyed by this call
        Assert.False(audioEngine.IsCapturing, "RX must not resume while PTT is deliberately left keyed");
    }

    [Fact]
    public async Task GetTxVolumePercentAsync_SectionPredatesTheField_DefaultsTo100NotZero()
    {
        // Regression test for a real, confirmed bug: System.Text.Json does not honor an init-only
        // property's C# initializer default when that property is absent from the JSON payload --
        // it silently deserializes to the CLR default (0 for int), not the field's declared default.
        // A settings.json saved before TxVolumePercent existed (exactly this raw JSON shape) must
        // still resolve to the intended 100% default, not a silently muted 0%.
        var legacyAudioSection = JsonDocument.Parse(
            """{"CaptureDeviceId":"capture-1","PlaybackDeviceId":"playback-1","SampleRate":8000}""").RootElement;
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings { Sections = new() { [AudioDeviceSettings.SectionKey] = legacyAudioSection } },
        };
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [8000])],
        };
        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        var percent = await service.GetTxVolumePercentAsync();

        Assert.Equal(100, percent);
    }

    [Fact]
    public async Task TransmitAsync_AppliesTxVolumeAsALinearGainOnEncodedSamples()
    {
        var (service, audioEngine, _, _, _, settingsStore) = CreateService();
        var current = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)!;
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey, current with { TxVolumePercent = 50 }, AudioSettingsJsonContext.Default.AudioDeviceSettings);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples.Select(s => s * 0.5f), audioEngine.PlaybackSamples);
    }

    [Fact]
    public void CaptureOverrunCount_UnderlyingEngineThrowsObjectDisposed_ReportsZeroInstead()
    {
        // Regression test for a real bug an auditor caught (batch-5 wiring, round 1): a concurrent
        // StopReceivingAsync can dispose the underlying capture session between IAudioEngine.CaptureOverrunCount's
        // own field read and its native call, surfacing ObjectDisposedException -- MiniAudioEngine's
        // own doc comment documents this exact narrow race. RxImagePaneViewModel polls THIS property
        // every 250ms on a DispatcherTimer tick with nothing to catch an uncaught exception there, so
        // this pass-through must absorb the race rather than propagate it -- 0 is the correct,
        // documented "capture isn't running" value in that state, not a fallback masking a real
        // failure.
        var audioEngine = new ThrowingCaptureOverrunCountAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator();
        var settingsStore = new FakeSettingsStore { Settings = new AppSettings() };
        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        var result = service.CaptureOverrunCount;

        Assert.Equal(0, result);
    }

    /// <summary>Throws <see cref="ObjectDisposedException"/> from <see cref="CaptureOverrunCount"/>
    /// only -- every other member is unreachable by <see cref="CaptureOverrunCount_UnderlyingEngineThrowsObjectDisposed_ReportsZeroInstead"/>
    /// (a plain property read, no construction-time engine calls), so they throw
    /// <see cref="NotImplementedException"/> rather than pretending to a fuller contract this test
    /// doesn't need.</summary>
    private sealed class ThrowingCaptureOverrunCountAudioEngine : IAudioEngine
    {
        public int CaptureOverrunCount => throw new ObjectDisposedException(nameof(ThrowingCaptureOverrunCountAudioEngine));

        public event Action<ReadOnlyMemory<float>>? SamplesCaptured
        {
            add { }
            remove { }
        }

        public Task StartCaptureAsync(
            AudioDeviceInfo device, int sampleRate, ThreadPriority? drainThreadPriority = null,
            int periodSizeInFrames = 0, int periods = 0, AudioChannelSource channelSource = AudioChannelSource.Mono,
            CancellationToken ct = default) => throw new NotImplementedException();

        public Task StopCaptureAsync() => throw new NotImplementedException();

        public Task StartPlaybackAsync(
            AudioDeviceInfo device, int sampleRate, int periodSizeInFrames = 0, int periods = 0,
            bool stereoTx = false, CancellationToken ct = default) => throw new NotImplementedException();

        public Task StopPlaybackAsync() => throw new NotImplementedException();

        public int EnqueuePlaybackSamples(ReadOnlyMemory<float> samples) => throw new NotImplementedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
