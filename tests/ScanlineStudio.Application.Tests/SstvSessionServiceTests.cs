using System.Text.Json;
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

        var service = new SstvSessionService(audioEngine, deviceEnumerator, settingsStore, decoder, encoder, waterfall, receivedImage, radioSession);
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
            () => service.TuneAsync(1750, TimeSpan.FromSeconds(5), cts.Token));

        Assert.Equal(PttOnThenOff, radioSession.PttCalls);
        Assert.True(audioEngine.IsCapturing, "capture should have been restarted after a cancelled Tune");
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
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService());

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
