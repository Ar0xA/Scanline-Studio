using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
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
        FakeWaterfallSource Waterfall, FakeRadioSessionService RadioSession, FakeSettingsStore SettingsStore,
        FakeAudioDeviceMuteQuery DeviceMuteQuery)
        CreateService(
            string? captureDeviceId = "capture-1",
            string? playbackDeviceId = "playback-1",
            string? captureDeviceName = null,
            string? playbackDeviceName = null,
            IReadOnlyList<AudioDeviceInfo>? inputDevices = null)
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = inputDevices ?? [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings
                {
                    CaptureDeviceId = captureDeviceId,
                    PlaybackDeviceId = playbackDeviceId,
                    CaptureDeviceName = captureDeviceName,
                    PlaybackDeviceName = playbackDeviceName,
                    SampleRate = 8000,
                },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder();
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, decoder, waterfall, radioSession, settingsStore, deviceMuteQuery);
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
        var (service, _, _, _, _, _, _) = CreateService(captureDeviceId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartReceivingAsync());
    }

    [Fact]
    public async Task StartReceivingAsync_ValidDevice_StartsCaptureAndFansSamplesOutToDecoderAndWaterfall()
    {
        var (service, audioEngine, decoder, waterfall, _, _, _) = CreateService();

        await service.StartReceivingAsync();
        var samples = new float[] { 1f, 2f, 3f };
        audioEngine.PushCapturedSamples(samples);

        Assert.True(audioEngine.IsCapturing);
        Assert.Equal(decoder.SampleRate, audioEngine.LastRequestedCaptureSampleRate);
        Assert.Single(decoder.PushedSamples);
        Assert.Single(waterfall.PushedSamples);
        Assert.Equal(samples, decoder.PushedSamples[0].ToArray());
        Assert.Equal(samples, waterfall.PushedSamples[0].ToArray());
    }

    [Fact]
    public async Task StartReceivingAsync_UsesImmutableDecoderRate_WhenPersistedRateChangesUntilRestart()
    {
        var (service, audioEngine, decoder, _, _, settingsStore, _) = CreateService();
        decoder.SampleRate = 22050;
        var current = settingsStore.Settings.GetSection(
            AudioDeviceSettings.SectionKey,
            AudioSettingsJsonContext.Default.AudioDeviceSettings)!;
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey,
            current with { SampleRate = 48500 },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);

        await service.StartReceivingAsync();

        Assert.Equal(22050, audioEngine.LastRequestedCaptureSampleRate);
    }

    [Fact]
    public async Task StartReceivingAsync_ConfiguredCaptureThreadPriority_IsPassedToTheAudioEngine()
    {
        var (service, audioEngine, _, _, _, settingsStore, _) = CreateService();
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
        var (service, audioEngine, _, _, _, _, _) = CreateService();

        await service.StartReceivingAsync();

        Assert.Null(audioEngine.LastRequestedDrainThreadPriority);
    }

    [Fact]
    public async Task IsReceiving_ReflectsStartAndStop_ForTheHeaderReceivingToggle()
    {
        var (service, _, _, _, _, _, _) = CreateService();

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
        var (service, _, decoder, _, _, _, _) = CreateService();

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
        var (service, _, decoder, _, _, _, _) = CreateService();
        var warningRaised = 0;
        service.MaintenanceWarningRaised += () => warningRaised++;

        decoder.RaiseRestartOverdue();

        Assert.Equal(1, warningRaised);
    }

    [Fact]
    public void DecoderRestarted_AfterAWarningWasRaised_RaisesMaintenanceWarningCleared()
    {
        var (service, _, decoder, _, _, _, _) = CreateService();
        var cleared = 0;
        service.MaintenanceWarningCleared += () => cleared++;

        decoder.RaiseRestartOverdue();
        decoder.RaiseRestarted();

        Assert.Equal(1, cleared);
    }

    [Fact]
    public void DecoderRestarted_WithNoActiveWarning_DoesNotRaiseMaintenanceWarningCleared()
    {
        var (service, _, decoder, _, _, _, _) = CreateService();
        var cleared = 0;
        service.MaintenanceWarningCleared += () => cleared++;

        // A normal, unremarkable swap while idle -- no warning was ever active, so nothing to clear.
        decoder.RaiseRestarted();

        Assert.Equal(0, cleared);
    }

    [Fact]
    public async Task DecoderRestartCriticallyOverdue_StopsReceiving_AndRaisesMaintenanceCriticalStopRaised()
    {
        var (service, audioEngine, decoder, _, _, _, _) = CreateService();
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
        var (service, _, decoder, _, _, _, _) = CreateService();
        Assert.False(service.IsReceiving);

        decoder.RaiseRestartCriticallyOverdue();

        Assert.False(service.IsReceiving);
    }

    [Fact]
    public async Task DecoderRestartCriticallyOverdue_WhileRxTransitionGateContended_DefersInsteadOfBlockingTheCallingThread()
    {
        // T1-6 (production_audit.md): the real maintenance event fires SYNCHRONOUSLY on the audio
        // capture drain thread -- if _rxTransitionGate is already held by another caller (e.g. a
        // concurrent PlayWithPttAsync RX-pause, not just DisposeAsync as an earlier version of this
        // gap's own comment claimed), blocking that thread waiting for the gate risks a bounded
        // ~_cleanupTimeout drain-thread freeze (real RX audio silently dropped, not a deadlock --
        // both sides are independently bounded). This proves the fix: the raise itself returns
        // promptly instead of waiting out that bound, and the deferred stop+notify still happen once
        // the gate frees.
        //
        // Two SEPARATE controllable gates, not one shared gate plus assumed scheduling order (this
        // project's own established rule -- a shared-gate test can pass by scheduling luck against
        // unfixed code): reachedStopGate proves the FIRST StopReceivingAsync call has genuinely
        // acquired _rxTransitionGate and is blocked inside StopCaptureAsync before this test fires
        // the maintenance event; captureStopGate is what that first call is actually blocked on.
        var (service, audioEngine, decoder, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();

        var captureStopGate = new TaskCompletionSource();
        var reachedStopGate = new TaskCompletionSource();
        audioEngine.OnStopCaptureAsync = () =>
        {
            reachedStopGate.TrySetResult();
            return captureStopGate.Task;
        };

        // Simulates another _rxTransitionGate holder (e.g. PlayWithPttAsync's own RX-pause step) --
        // acquires the gate, then blocks inside StopCaptureAsync on captureStopGate.
        var firstStopTask = service.StopReceivingAsync();
        await reachedStopGate.Task; // the gate is now genuinely held, not just "about to be"

        var criticalStopRaised = 0;
        var notified = new TaskCompletionSource();
        service.MaintenanceCriticalStopRaised += () =>
        {
            criticalStopRaised++;
            notified.TrySetResult();
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        decoder.RaiseRestartCriticallyOverdue();
        sw.Stop();

        // The regression this test exists to catch: without the fix, this call blocks (via
        // StopReceivingAsync().GetAwaiter().GetResult()) waiting out _rxTransitionGate's own
        // WaitAsync(_cleanupTimeout, ...) bound before returning -- several seconds. A well-under-1s
        // return proves it deferred instead of blocking.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"Expected a prompt return, took {sw.Elapsed}.");

        // Not yet notified -- the deferred stop is itself waiting on the still-held gate.
        Assert.Equal(0, criticalStopRaised);

        captureStopGate.SetResult(); // let the first caller's own StopCaptureAsync finish...
        await firstStopTask; // ...which releases _rxTransitionGate.

        await notified.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the deferred stop can now proceed
        Assert.Equal(1, criticalStopRaised);
        Assert.False(service.IsReceiving);
    }

    [Fact]
    public async Task PushSamples_DecoderThrows_WaterfallStillReceivesTheSamples()
    {
        var (service, audioEngine, decoder, waterfall, _, _, _) = CreateService();
        decoder.ThrowOnPush = new InvalidOperationException("simulated decoder failure");
        await service.StartReceivingAsync();

        audioEngine.PushCapturedSamples(TwoSamplePush);

        Assert.Single(decoder.PushedSamples);
        Assert.Single(waterfall.PushedSamples);
    }

    [Fact]
    public async Task PushSamples_WaterfallThrows_DecoderStillReceivesTheSamples()
    {
        var (service, audioEngine, decoder, waterfall, _, _, _) = CreateService();
        waterfall.ThrowOnPush = new InvalidOperationException("simulated waterfall failure");
        await service.StartReceivingAsync();

        audioEngine.PushCapturedSamples(TwoSamplePush);

        Assert.Single(decoder.PushedSamples);
        Assert.Single(waterfall.PushedSamples);
    }

    // User-reported fix (2026-08-23, round 2): RawInputPeakLevel exists as a THIRD, independent
    // fan-out target specifically so it reads the raw captured buffer, never anything the decoder's
    // own SSTV-band bandpass filter has touched -- see that property's own doc comment. These tests
    // pin that independence directly: FakeSstvDecoder.SignalPeakLevel is a plain settable field, not
    // derived from pushed samples at all, so if RawInputPeakLevel ever accidentally started reading
    // through the decoder, these values would never match.

    [Fact]
    public async Task RawInputPeakLevel_ReflectsPeakAbsoluteValueOfMostRecentCapturedBuffer()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();

        float[] samples = [0.3f, -0.7f, 0.2f];
        audioEngine.PushCapturedSamples(samples);

        Assert.Equal(0.7, service.RawInputPeakLevel, precision: 6);
    }

    [Fact]
    public async Task RawInputPeakLevel_NewerBufferReplaces_DoesNotAccumulateAcrossBuffers()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();

        float[] loud = [0.9f];
        audioEngine.PushCapturedSamples(loud);
        Assert.Equal(0.9, service.RawInputPeakLevel, precision: 6);

        float[] quiet = [0.1f];
        audioEngine.PushCapturedSamples(quiet);
        Assert.Equal(0.1, service.RawInputPeakLevel, precision: 6);
    }

    [Fact]
    public void RawInputPeakLevel_NotReceiving_IsZero()
    {
        var (service, _, _, _, _, _, _) = CreateService();

        Assert.Equal(0.0, service.RawInputPeakLevel);
    }

    [Fact]
    public async Task RawInputPeakLevel_ResetsToZero_AfterStopReceivingAsync()
    {
        // Contract: 0.0 whenever capture isn't running, never a stale reading left over from before
        // the stop -- no more buffers will arrive to overwrite it once unsubscribed.
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        float[] samples = [0.8f];
        audioEngine.PushCapturedSamples(samples);
        Assert.Equal(0.8, service.RawInputPeakLevel, precision: 6);

        await service.StopReceivingAsync();

        Assert.Equal(0.0, service.RawInputPeakLevel);
    }

    // Renamed + re-commented (spec/18-path-to-1.0.md Critical item 1 / item 8, round-1 plan-review
    // finding 4): now specifically "no default device available either" -- see the fallback-success
    // and no-radio-configured tests near the bottom of this file for the new behavior.
    [Fact]
    public async Task TransmitAsync_NoPlaybackDeviceConfigured_AndNoDefaultDeviceAvailable_Throws()
    {
        var (service, _, _, _, _, _, _) = CreateService(playbackDeviceId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));
    }

    [Fact]
    public async Task GetConfiguredPlaybackDeviceNameAsync_ValidDevice_ReturnsItsName()
    {
        var (service, _, _, _, _, _, _) = CreateService();

        var name = await service.GetConfiguredPlaybackDeviceNameAsync();

        Assert.Equal("Playback", name);
    }

    // Renamed (round-1 plan-review finding 4): "no default device available either."
    [Fact]
    public async Task GetConfiguredPlaybackDeviceNameAsync_NoPlaybackDeviceConfigured_AndNoDefaultDeviceAvailable_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _, _) = CreateService(playbackDeviceId: null);

        var name = await service.GetConfiguredPlaybackDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredPlaybackDeviceNameAsync_ConfiguredDeviceNoLongerPresent_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _, _) = CreateService(playbackDeviceId: "playback-vanished");

        var name = await service.GetConfiguredPlaybackDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_ValidDevice_ReturnsItsName()
    {
        var (service, _, _, _, _, _, _) = CreateService();

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Equal("Capture", name);
    }

    // Renamed (round-1 plan-review finding 4): "no default device available either."
    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_NoCaptureDeviceConfigured_AndNoDefaultDeviceAvailable_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _, _) = CreateService(captureDeviceId: null);

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_ConfiguredDeviceNoLongerPresent_ReturnsNull_NotThrow()
    {
        var (service, _, _, _, _, _, _) = CreateService(captureDeviceId: "capture-vanished");

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Null(name);
    }

    // User-reported fix (2026-08-23): a real, OS-agnostic device-id-churn scenario -- observed live
    // when a PipeWire USB capture node was re-created under a new id after a mute toggle, same
    // physical device, same friendly name. AudioDeviceSettings.CaptureDeviceName/PlaybackDeviceName
    // exist specifically to recover this without silently substituting a DIFFERENT device.

    [Fact]
    public async Task StartReceivingAsync_ConfiguredIdChurnedButNameMatches_RecoversTheSameDeviceByName()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService(
            captureDeviceId: "capture-1.old",
            captureDeviceName: "Capture",
            inputDevices: [new AudioDeviceInfo("capture-1.new", "Capture", 1, 0, [8000])]);

        await service.StartReceivingAsync();

        Assert.True(audioEngine.IsCapturing);
        Assert.True(service.IsReceiving);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_IdChurnedButNameMatches_ReturnsTheRecoveredDevicesName()
    {
        var (service, _, _, _, _, _, _) = CreateService(
            captureDeviceId: "capture-1.old",
            captureDeviceName: "Capture",
            inputDevices: [new AudioDeviceInfo("capture-1.new", "Capture", 1, 0, [8000])]);

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Equal("Capture", name);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_IdChurnedAndNoNameEitherMatches_ReturnsNull_NotThrow()
    {
        // The genuinely-missing case must still be surfaced, not silently papered over -- only an
        // actual name match recovers the device.
        var (service, _, _, _, _, _, _) = CreateService(
            captureDeviceId: "capture-1.old",
            captureDeviceName: "A Completely Different Microphone",
            inputDevices: [new AudioDeviceInfo("capture-1.new", "Capture", 1, 0, [8000])]);

        var name = await service.GetConfiguredCaptureDeviceNameAsync();

        Assert.Null(name);
    }

    [Fact]
    public async Task GetConfiguredCaptureDeviceNameAsync_IdChurnedAndNoNamePersisted_ReturnsNull_NotThrow()
    {
        // An old settings.json from before CaptureDeviceName existed has null here -- must not
        // crash, and correctly has nothing to recover by.
        var (service, _, _, _, _, _, _) = CreateService(
            captureDeviceId: "capture-1.old",
            captureDeviceName: null,
            inputDevices: [new AudioDeviceInfo("capture-1.new", "Capture", 1, 0, [8000])]);

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
        var service = new SstvSessionService(audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

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
        var service = new SstvSessionService(audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples, audioEngine.PlaybackSamples);
    }

    [Fact]
    public async Task TransmitAsync_ConfiguredPlaybackDeviceMissing_FallsBackToDefault()
    {
        // User-reported fix, round 3 (2026-08-23), explicit product decision overriding this test's
        // own prior "still fails loudly, never silently substituted" name and assertion: "if in the
        // file there is an RX/TX device that is not currently attached to the computer, just set it
        // to the OS defaults."
        var audioEngine = new FakeAudioEngine();
        var defaultPlaybackDevice = new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025], IsDefault: true);
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [defaultPlaybackDevice],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-vanished", SampleRate = 8000 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var service = new SstvSessionService(audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        await service.TransmitAsync(TestMode, TestImage);

        var saved = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal("playback-1", saved?.PlaybackDeviceId);
    }

    // Capture-side counterpart of the playback test immediately above -- same shared
    // TryResolveDeviceAsync/ResolveDeviceAsync code path (code-review nit on spec/18-path-to-1.0.md
    // Critical item 1: only the playback direction had a "configured device missing" regression
    // test).
    [Fact]
    public async Task StartReceivingAsync_ConfiguredCaptureDeviceMissing_FallsBackToDefault()
    {
        // User-reported fix, round 3 (2026-08-23), explicit product decision overriding this test's
        // own prior "still throws" name and assertion: "if in the file there is an RX/TX device that
        // is not currently attached to the computer, just set it to the OS defaults."
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
        var service = new SstvSessionService(audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        await service.StartReceivingAsync();

        Assert.True(audioEngine.IsCapturing);
        var saved = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings);
        Assert.Equal("capture-1", saved?.CaptureDeviceId);
    }

    [Fact]
    public async Task StartReceivingAsync_ConfiguredCaptureDeviceMissing_AndNoDefaultEither_StillThrows()
    {
        // The genuinely-nothing-available case (spec/18-path-to-1.0.md Critical item 1 / item 8)
        // must still fail loudly -- there is nothing to fall back to.
        var (service, _, _, _, _, _, _) = CreateService(captureDeviceId: "capture-vanished");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartReceivingAsync());
    }

    [Fact]
    public async Task TransmitAsync_KeysPttOnThenOffAroundPlayback()
    {
        var (service, _, _, _, radioSession, _, _) = CreateService();

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
        var (service, audioEngine, _, _, radioSession, _, _) = CreateService();
        radioSession.RigId = "none";

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples, audioEngine.PlaybackSamples);
        Assert.Empty(radioSession.PttCalls);
    }

    [Fact]
    public async Task TransmitAsync_EncodesAndEnqueuesAllSamplesForPlayback()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples, audioEngine.PlaybackSamples);
        Assert.False(audioEngine.IsPlaying);
    }

    [Fact]
    public async Task TransmitAsync_WhileReceiving_StopsCaptureDuringTxAndRestartsAfterward()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        Assert.True(audioEngine.IsCapturing);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.True(audioEngine.IsCapturing, "capture should have been restarted after TX completed");
    }

    [Fact]
    public async Task TransmitAsync_WhileNotReceiving_DoesNotStartCaptureAfterward()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(audioEngine.IsCapturing);
    }

    // User-reported gap (2026-08-18): the header's Receiving indicator stayed visually lit
    // throughout a local transmission -- IsReceiving itself already correctly flips false during
    // the pause, but nothing ever PUSHED that change to a subscriber (a plain, non-eventing
    // property). CapturePausedForTransmitChanged closes that gap.

    [Fact]
    public async Task TransmitAsync_WhileReceiving_RaisesCapturePausedThenResumed()
    {
        var (service, _, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        List<bool> raised = [];
        service.CapturePausedForTransmitChanged += paused => raised.Add(paused);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal([true, false], raised);
    }

    [Fact]
    public async Task TransmitAsync_WhileNotReceiving_NeverRaisesCapturePaused()
    {
        // Nothing was actually running to pause -- see this event's own doc comment: it's
        // specifically "a running capture was paused for THIS transmission," not a generic
        // TX-in-progress signal.
        var (service, _, _, _, _, _, _) = CreateService();
        var raised = false;
        service.CapturePausedForTransmitChanged += _ => raised = true;

        await service.TransmitAsync(TestMode, TestImage);

        Assert.False(raised);
    }

    [Fact]
    public async Task SetPttLockAsync_LockedThenTransmitThenUnlocked_RaisesCapturePausedFalseOnlyAtDeferredResume()
    {
        // The lock case defers the actual resume to SetPttLockAsync(false) -- see
        // TransmitAsync_WhileReceiving_StopsCaptureDuringTxAndRestartsAfterward's own sibling test
        // and PlayWithPttAsync's own doc comment for the full state machine this exercises.
        var (service, _, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);
        List<bool> raised = [];
        service.CapturePausedForTransmitChanged += paused => raised.Add(paused);

        await service.TransmitAsync(TestMode, TestImage);
        Assert.Equal([true], raised);

        await service.SetPttLockAsync(false);
        Assert.Equal([true, false], raised);
    }

    [Fact]
    public async Task TuneAsync_TokenCancelledMidTone_StillRaisesCapturePausedFalse()
    {
        // Auditor round-1 finding: an abnormal termination (SWR auto-cutoff / manual Stop TX both
        // work by cancelling the token, same as TuneAsync_TokenCancelledMidTone_StillUnkeysPttAndRestartsCapture
        // below) must still end with `false` raised, not leave the Receiving indicator stuck dimmed
        // just because the transmission itself failed rather than completing normally.
        var (service, _, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        List<bool> raised = [];
        service.CapturePausedForTransmitChanged += paused => raised.Add(paused);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.TuneAsync(1750, TimeSpan.FromSeconds(5), ct: cts.Token));

        Assert.Equal([true, false], raised);
    }

    [Fact]
    public async Task TuneAsync_LeaveKeyedAfterCallAndNotLocked_StillRaisesCapturePausedFalse()
    {
        // Auditor round-1 finding: the residual leaveKeyedAfterCall=true/not-locked case (no
        // production caller sets this today -- TuneAsync's own "stay keyed" option is unwired) is a
        // latent bug otherwise: RX intentionally stays stopped here (pre-existing, unchanged
        // behavior), but this transmission's own pause window is still over, so the event must not
        // stay stuck `true` forever with no `false` ever coming.
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        List<bool> raised = [];
        service.CapturePausedForTransmitChanged += paused => raised.Add(paused);

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1), leaveKeyedAfterTune: true);

        Assert.Equal([true, false], raised);
        Assert.False(audioEngine.IsCapturing, "RX intentionally stays stopped in this case -- only the event must not get stuck");
    }

    [Fact]
    public async Task LockedTransmitThenLockedCancelledTune_StillResumesRxAndRaisesCapturePausedFalse()
    {
        // Round-2 auditor finding: a locked TX (#1) defers its RX resume via
        // _rxPendingResumeAfterUnlock (see SetPttLockAsync_LockedThenTransmitThenUnlocked_... above).
        // If a SECOND locked call is then cancelled (SWR cutoff / Stop TX) before anyone ever calls
        // SetPttLockAsync(false), that second call's own `wasReceiving` is false (capture was already
        // stopped by #1) -- without PlayWithPttAsync's own stranded-flag consumption (added for this
        // finding), the pending resume would never fire: IsPttLocked already reads false (force-
        // cleared by the abnormal-termination unkey), so the natural SetPttLockAsync(false) trigger
        // that would otherwise consume the flag may never come, leaving RX stopped and the Receiving
        // indicator dimmed forever.
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        await service.SetPttLockAsync(true);
        List<bool> raised = [];
        service.CapturePausedForTransmitChanged += paused => raised.Add(paused);

        await service.TransmitAsync(TestMode, TestImage); // #1: pauses RX, defers resume (still locked)
        Assert.False(audioEngine.IsCapturing);
        Assert.Equal([true], raised);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.TuneAsync(1750, TimeSpan.FromSeconds(5), ct: cts.Token)); // #2: cancelled while still locked

        Assert.False(service.IsPttLocked, "abnormal termination must still force-clear the lock");
        Assert.True(audioEngine.IsCapturing, "the stranded resume from #1 must not be lost just because #2 force-unkeyed instead of an explicit unlock");
        Assert.Equal([true, false], raised);
    }

    [Fact]
    public async Task SetPttLockAsync_Locked_KeysPttImmediatelyAndReportsLocked()
    {
        var (service, _, _, _, radioSession, _, _) = CreateService();

        await service.SetPttLockAsync(true);

        Assert.True(service.IsPttLocked);
        Assert.Equal([true], radioSession.PttCalls);
    }

    [Fact]
    public async Task SetPttLockAsync_LockedThenTransmit_DoesNotDoubleKeyAndLeavesPttKeyedAfterward()
    {
        var (service, audioEngine, _, _, radioSession, _, _) = CreateService();
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
        var (service, _, _, _, radioSession, _, _) = CreateService();

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
        var (service, _, _, _, radioSession, _, _) = CreateService();

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
        var (service, _, _, _, radioSession, _, _) = CreateService();

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
        var (service, audioEngine, _, _, radioSession, _, _) = CreateService();
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
        var (service, audioEngine, _, _, _, _, _) = CreateService();
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
        var (service, _, _, _, radioSession, _, _) = CreateService();
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
        var (service, _, decoder, _, _, _, _) = CreateService();

        await service.DisposeAsync();

        Assert.True(decoder.DisposedForTests);
    }

    [Fact]
    public async Task TransmitAsync_WithNoLockEngaged_BehavesExactlyAsBeforeThisFeature()
    {
        // Regression guard: introducing the lock must not change the un-locked default path.
        var (service, _, _, _, radioSession, _, _) = CreateService();

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
        var (service, audioEngine, _, _, radioSession, _, _) = CreateService();
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
        var (service, audioEngine, _, _, radioSession, _, _) = CreateService();
        await service.StartReceivingAsync();

        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
        Assert.True(audioEngine.IsCapturing, "RX should have resumed once the tune tone finished");
    }

    [Fact]
    public async Task TuneAsync_LeaveKeyedAfterTuneTrue_KeysPttAndDoesNotUnkeyOrResumeCapture()
    {
        var (service, audioEngine, _, _, radioSession, _, _) = CreateService();
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
            audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);

        var percent = await service.GetTxVolumePercentAsync();

        Assert.Equal(100, percent);
    }

    [Fact]
    public async Task TransmitAsync_AppliesTxVolumeAsALinearGainOnEncodedSamples()
    {
        var (service, audioEngine, _, _, _, settingsStore, _) = CreateService();
        var current = settingsStore.Settings.GetSection(AudioDeviceSettings.SectionKey, AudioSettingsJsonContext.Default.AudioDeviceSettings)!;
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey, current with { TxVolumePercent = 50 }, AudioSettingsJsonContext.Default.AudioDeviceSettings);

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(ExpectedPlaybackSamples.Select(s => s * 0.5f), audioEngine.PlaybackSamples);
    }

    // Regression test for the live-gain plumbing added alongside the Options window's Radio/CAT Pwr
    // slider + Tune button: before this change, PlayWithPttAsync captured Pwr ONCE at call entry and
    // PumpToPlaybackAsync used that single frozen value for the whole call, so dragging the Pwr
    // slider while a Tune tone (or a real transmission) was already playing had no audible/measurable
    // effect until the NEXT call -- exactly backwards for the WSJT-X-style "key Tune, watch the
    // radio's own power meter, dial Pwr to the wattage you want" workflow that feature exists for.
    [Fact]
    public async Task TuneAsync_PwrChangedMidTone_AppliesNewGainToLaterChunksNotEarlierOnes()
    {
        var (service, audioEngine, _, _, _, _, _) = CreateService();
        await service.SetTxVolumePercentAsync(20);

        // Deterministic gate (FakeAudioEngine.OnPlaybackChunkEnqueued), not a race: fires
        // synchronously on the pump's own thread right after the FIRST 4096-sample chunk lands, so
        // this reliably simulates "the user moved the Pwr slider mid-tone" landing exactly between
        // chunk 1 and chunk 2 -- not "at some point during the tone, maybe."
        var changedAfterFirstChunk = false;
        audioEngine.OnPlaybackChunkEnqueued = async () =>
        {
            if (!changedAfterFirstChunk)
            {
                changedAfterFirstChunk = true;
                await service.SetTxVolumePercentAsync(80);
            }
        };

        // TuneAsync's tone runs at a fixed 48kHz (see its own `const int sampleRate = 48_000`) --
        // 500ms gives 24000 samples, i.e. 5 full 4096-sample chunks plus a partial one, comfortably
        // exercising the chunk-boundary re-read this test targets.
        await service.TuneAsync(1750, TimeSpan.FromMilliseconds(500));

        var firstChunk = audioEngine.PlaybackSamples.Take(4096).ToArray();
        var laterChunk = audioEngine.PlaybackSamples.Skip(4096).Take(4096).ToArray();

        // GenerateTone's samples are an unscaled Math.Sin (peak amplitude exactly 1.0), so the max
        // absolute sample in each chunk is a direct readout of the gain PumpToPlaybackAsync actually
        // applied to it -- 4096 samples of a 1750Hz tone at 48kHz covers many full cycles, so both
        // chunks are guaranteed to come within floating-point precision of their own true peak.
        Assert.InRange(firstChunk.Max(Math.Abs), 0.199f, 0.201f);
        Assert.InRange(laterChunk.Max(Math.Abs), 0.799f, 0.801f);
    }

    [Fact]
    public async Task GetTxDeviceMutedAsync_ReturnsQueriedOsMuteState()
    {
        var (service, _, _, _, _, _, deviceMuteQuery) = CreateService();
        deviceMuteQuery.SetMuted("playback-1", isCapture: false, muted: true);

        Assert.True(await service.GetTxDeviceMutedAsync());
    }

    [Fact]
    public async Task GetTxDeviceMutedAsync_UnsupportedDevice_DefaultsToFalse()
    {
        var (service, _, _, _, _, _, deviceMuteQuery) = CreateService();
        deviceMuteQuery.MarkUnsupported("playback-1", isCapture: false);

        Assert.False(await service.GetTxDeviceMutedAsync());
    }

    [Fact]
    public void RequestSenseLevel_ForwardsToTheDecoderLiveProperty()
    {
        var (service, _, decoder, _, _, _, _) = CreateService();

        service.RequestSenseLevel(2);

        Assert.Equal(2, decoder.SenseLevel);
    }

    [Fact]
    public async Task PersistSenseLevelAsync_WritesTheNewValueAndPreservesOtherSiblingFields()
    {
        var (service, _, _, _, _, settingsStore, _) = CreateService();
        settingsStore.Settings = settingsStore.Settings.WithSection(
            SstvDecoderSettings.SectionKey,
            new SstvDecoderSettings
            {
                AutoSyncEnabled = false,
                AutoStopEnabled = true,
                DemodType = DemodType.Pll,
                SenseLevel = 1,
            },
            SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        await service.PersistSenseLevelAsync(3);

        var saved = settingsStore.Settings.GetSection(
            SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.NotNull(saved);
        Assert.Equal(3, saved!.SenseLevel);
        Assert.False(saved.AutoSyncEnabled);
        Assert.True(saved.AutoStopEnabled);
        Assert.Equal(DemodType.Pll, saved.DemodType);
    }

    [Fact]
    public async Task PersistSenseLevelAsync_NoExistingSection_CreatesOneWithJustTheNewValue()
    {
        var (service, _, _, _, _, settingsStore, _) = CreateService();

        await service.PersistSenseLevelAsync(0);

        var saved = settingsStore.Settings.GetSection(
            SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.NotNull(saved);
        Assert.Equal(0, saved!.SenseLevel);
    }

    // Restart-required-settings backlog item 6 (RX BPF Receive-tab live dropdown, 2026-08-28) --
    // same shape as RequestSenseLevel/PersistSenseLevelAsync's own tests above, but forwarding to
    // ISstvDecoderReconfiguration.RequestRxBpfPreset (a per-field-merge sibling of
    // RequestReconfiguration, not that combined call) -- see RequestRxBpfPreset's own doc comment
    // for why.

    [Fact]
    public void RequestRxBpfPreset_ForwardsToTheDecoderReconfigurationSideChannel()
    {
        var (service, _, decoder, _, _, _, _) = CreateService();

        service.RequestRxBpfPreset(RxBpfPreset.Narrow);

        Assert.Equal(1, decoder.RequestRxBpfPresetCallCount);
        Assert.Equal(RxBpfPreset.Narrow, decoder.LastRequestedRxBpfPreset);
    }

    [Fact]
    public async Task PersistRxBpfPresetAsync_WritesTheNewValueAndPreservesOtherSiblingFields()
    {
        var (service, _, _, _, _, settingsStore, _) = CreateService();
        settingsStore.Settings = settingsStore.Settings.WithSection(
            SstvDecoderSettings.SectionKey,
            new SstvDecoderSettings
            {
                AutoSyncEnabled = false,
                AutoStopEnabled = true,
                DemodType = DemodType.Pll,
                RxBufferMode = RxBufferMode.Extended,
                SenseLevel = 1,
            },
            SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);

        await service.PersistRxBpfPresetAsync(RxBpfPreset.VeryNarrow);

        var saved = settingsStore.Settings.GetSection(
            SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.NotNull(saved);
        Assert.Equal(RxBpfPreset.VeryNarrow, saved!.RxBpfPreset);
        // DemodType/RxBufferMode are untouched -- this row edits ONLY RxBpfPreset.
        Assert.Equal(DemodType.Pll, saved.DemodType);
        Assert.Equal(RxBufferMode.Extended, saved.RxBufferMode);
        Assert.False(saved.AutoSyncEnabled);
        Assert.True(saved.AutoStopEnabled);
        Assert.Equal(1, saved.SenseLevel);
    }

    [Fact]
    public async Task PersistRxBpfPresetAsync_NoExistingSection_CreatesOneWithJustTheNewValue()
    {
        var (service, _, _, _, _, settingsStore, _) = CreateService();

        await service.PersistRxBpfPresetAsync(RxBpfPreset.Off);

        var saved = settingsStore.Settings.GetSection(
            SstvDecoderSettings.SectionKey, SstvDecoderSettingsJsonContext.Default.SstvDecoderSettings);
        Assert.NotNull(saved);
        Assert.Equal(RxBpfPreset.Off, saved!.RxBpfPreset);
    }

    [Fact]
    public void ReconfigurationRejected_DecoderRaisesIt_ForwardsThroughThePublicEvent()
    {
        // The BPF row is now a live EDITOR (restart-required-settings backlog item 6) -- it needs
        // this signal to re-sync back to whatever's actually applied when a queued reconfiguration
        // (from ANY source) is rejected. Previously this event existed only on the decoder-level
        // ISstvDecoderReconfiguration side-channel, log-only at this layer.
        var (service, _, decoder, _, _, _, _) = CreateService();
        var raisedCount = 0;
        service.ReconfigurationRejected += () => raisedCount++;

        decoder.RaiseReconfigurationRejected();

        Assert.Equal(1, raisedCount);
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
            audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(), new MacroTextResolver(),
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
