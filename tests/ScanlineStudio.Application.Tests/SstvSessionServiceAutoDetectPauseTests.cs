using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// RX pause/resume (elegant-wondering-hinton.md, port of legacy's RX-page <c>SBAuto</c> toggle,
/// <c>TMmsstv::RxAutoPush</c>, `Main.cpp:6042-6060`) -- session-owned, not decoder state. Four
/// rounds of plan-readiness review went into this design; see
/// <see cref="SstvSessionService.SetAutoDetectPaused"/>'s own doc comment for the full reasoning.
/// The decoder-side cleanup command itself (<c>RequestAbandonReception</c>) is covered by real
/// encode/decode-audio tests in <c>ScanlineStudio.Core.Sstv.Tests</c>'s own
/// <c>RequestAbandonReceptionTests</c> -- this file only needs to prove the session-layer gate and
/// drain-pending mechanism, via <see cref="FakeSstvDecoder.PushedSamples"/>.
/// </summary>
public sealed class SstvSessionServiceAutoDetectPauseTests
{
    private static readonly float[] OneSamplePush = [1f];

    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder, FakeWaterfallSource Waterfall) CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1" },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder();
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder, new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, decoder, waterfall);
    }

    [Fact]
    public async Task SetAutoDetectPaused_True_RequestsAbandon_ThenStopsForwardingRealAudioToDecoder_ButNotWaterfall()
    {
        var (service, audioEngine, decoder, waterfall) = CreateService();
        await service.StartReceivingAsync();

        service.SetAutoDetectPaused(true);

        Assert.True(service.IsAutoDetectPaused);
        Assert.Equal(1, decoder.RequestAbandonReceptionCallCount);

        // First callback after pausing: the one-shot drain -- an EMPTY buffer reaches the decoder
        // (so its deferred RequestAbandonReception applies promptly), the REAL samples do not.
        var samples = new float[] { 1f, 2f, 3f };
        audioEngine.PushCapturedSamples(samples);

        Assert.Single(decoder.PushedSamples);
        Assert.Empty(decoder.PushedSamples[0].ToArray());
        Assert.Single(waterfall.PushedSamples); // waterfall keeps receiving real audio throughout
        Assert.Equal(samples, waterfall.PushedSamples[0].ToArray());

        // Every callback after the one-shot drain: skipped entirely, not even an empty forward.
        audioEngine.PushCapturedSamples(samples);

        Assert.Single(decoder.PushedSamples); // still just the one empty drain call
        Assert.Equal(2, waterfall.PushedSamples.Count); // waterfall still getting every chunk
    }

    [Fact]
    public async Task SetAutoDetectPaused_False_ResumesForwardingRealAudio()
    {
        var (service, audioEngine, decoder, _) = CreateService();
        await service.StartReceivingAsync();
        service.SetAutoDetectPaused(true);
        audioEngine.PushCapturedSamples(OneSamplePush); // the one-shot drain

        service.SetAutoDetectPaused(false);
        var samples = new float[] { 9f, 8f, 7f };
        audioEngine.PushCapturedSamples(samples);

        Assert.False(service.IsAutoDetectPaused);
        Assert.Equal(2, decoder.PushedSamples.Count); // the earlier empty drain, plus this real push
        Assert.Equal(samples, decoder.PushedSamples[^1].ToArray());
    }

    [Fact]
    public async Task NewInstance_BeforeAnyPauseToggle_StartsUnpaused()
    {
        var (service, _, _, _) = CreateService();
        await service.StartReceivingAsync();

        Assert.False(service.IsAutoDetectPaused);
    }

    [Fact]
    public async Task SetAutoDetectPaused_True_PersistsAcrossStopThenStartReceiving()
    {
        // Pins a DELIBERATE DEVIATION from legacy (code-review finding, round 2,
        // elegant-wondering-hinton.md), not a legacy-fidelity behavior -- legacy's own m_SyncMode
        // does NOT survive a transmit (TMmsstv::ToTX, Main.cpp:7360, calls pDem->Stop() -- guarded
        // only by the TXLoopBack option, off by default -- which self-clears back to unpaused ~0.5s
        // later, sstv.cpp:1786/2243-2252).
        // This port keeps the flag sticky across Stop/Start instead, because pause/resume is
        // session/UX state, not DSP/protocol math -- see IsAutoDetectPaused's own doc comment for
        // the full reasoning. This test used to pin the OPPOSITE (StartReceivingAsync silently
        // resetting the flag while the UI toggle kept reading "Paused"); that was the actual bug.
        var (service, audioEngine, decoder, _) = CreateService();
        await service.StartReceivingAsync();
        service.SetAutoDetectPaused(true);
        audioEngine.PushCapturedSamples(OneSamplePush); // the one-shot drain
        await service.StopReceivingAsync();

        await service.StartReceivingAsync(); // e.g. the internal resume-after-TX path, or a manual Stop/Start toggle

        Assert.True(service.IsAutoDetectPaused);

        var samples = new float[] { 4f, 5f };
        audioEngine.PushCapturedSamples(samples);

        // Still paused: the real samples above must NOT reach the decoder.
        Assert.Single(decoder.PushedSamples); // still just the earlier empty drain call
    }
}
