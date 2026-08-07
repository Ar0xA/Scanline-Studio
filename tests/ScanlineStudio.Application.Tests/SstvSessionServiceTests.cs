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

        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, decoder, encoder, waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, decoder, waterfall, radioSession, settingsStore);
    }

    [Fact]
    public async Task StartReceivingAsync_NoCaptureDeviceConfigured_Throws()
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

    [Fact]
    public async Task TransmitAsync_NoPlaybackDeviceConfigured_Throws()
    {
        var (service, _, _, _, _, _) = CreateService(playbackDeviceId: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TransmitAsync(TestMode, TestImage));
    }

    [Fact]
    public async Task TransmitAsync_KeysPttOnThenOffAroundPlayback()
    {
        var (service, _, _, _, radioSession, _) = CreateService();

        await service.TransmitAsync(TestMode, TestImage);

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
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
            audioEngine, deviceEnumerator, settingsStore, new FakeSstvDecoder(), new FakeSstvEncoder(),
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
}
