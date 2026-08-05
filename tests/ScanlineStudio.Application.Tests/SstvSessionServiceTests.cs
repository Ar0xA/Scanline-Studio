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
}
