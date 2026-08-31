using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>
/// Stub survey Tier 3, "Loopback self-test" -- <see cref="SstvSessionService.RunLoopbackSelfTestAsync"/>.
/// See <see cref="ISstvSessionService.RunLoopbackSelfTestAsync"/>'s own doc comment for the locked
/// design (round-5 plan-review): a fresh, throwaway decoder instance, never the shared
/// <c>_decoder</c>, encoded with <c>sampleRateOffsetHz: 0.0</c> and no station-ID footer.
/// </summary>
public sealed class SstvSessionServiceLoopbackSelfTestTests
{
    private static readonly IImageSource TestImage1x1 = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static (SstvSessionService Service, FakeSstvEncoder Encoder, FakeSstvDecoder Decoder) CreateServiceWithFakeEncoder()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [11025])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 11025 },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var encoder = new FakeSstvEncoder { SampleRate = 11025 };
        var decoder = new FakeSstvDecoder { SampleRate = 11025 };

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, decoder, encoder, new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);
        return (service, encoder, decoder);
    }

    private static (SstvSessionService Service, FakeSstvDecoder Decoder, FakeSettingsStore SettingsStore) CreateServiceWithRealEncoder(int sampleRate = 11025)
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [sampleRate])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [sampleRate])],
        };
        var settingsStore = new FakeSettingsStore { Settings = new AppSettings() };
        var encoder = new AnalogFmSstvEncoder(sampleRate);
        var decoder = new FakeSstvDecoder { SampleRate = sampleRate };

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, decoder, encoder, new MacroTextResolver(),
            new FakeWaterfallSource(), new FakeReceivedImageBuffer(), new FakeRadioSessionService(), NullLogger<SstvSessionService>.Instance);
        return (service, decoder, settingsStore);
    }

    private static ArrayImageSource CreateGradientTestImage(int width, int height)
    {
        var pixels = new Rgb24[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = new Rgb24(
                    R: (byte)(x * 255 / Math.Max(1, width - 1)),
                    G: (byte)(y * 255 / Math.Max(1, height - 1)),
                    B: 128);
            }
        }

        return new ArrayImageSource(width, height, pixels);
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_EncodesWithZeroOffsetAndNoStationId()
    {
        // Round-3/round-4/round-5 plan-review: the self-test's own encode must ALWAYS use
        // sampleRateOffsetHz 0.0 (the round trip has no physical hardware clock to correct for --
        // applying the real persisted offset would fabricate a slant) and no station-ID footer
        // (EndOfImage's documented low-risk false-lock-into-trailing-audio window). Uses the fake
        // encoder here -- this test only cares about what was PASSED to EncodeBatchedAsync (T1-3,
        // production_audit.md: the loopback self-test's real production path now uses the batched
        // method), not about a real decode.
        var (service, encoder, _) = CreateServiceWithFakeEncoder();

        await service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage1x1);

        Assert.Equal(0.0, encoder.LastSampleRateOffsetHz);
        Assert.Null(encoder.LastStationIdOptions);
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_NeverTouchesTheSharedDecoder()
    {
        // Round-5 plan-review's load-bearing design decision: a fresh, throwaway decoder instance,
        // never the shared _decoder the live RX pipeline/ReceivedImageBuffer/ReceiveHistoryRecorder
        // observe. FakeSstvDecoder.PushedSamples staying empty is a direct, structural proof of that
        // isolation -- not an inference from the design doc alone.
        var (service, _, decoder) = CreateServiceWithFakeEncoder();

        await service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage1x1);

        Assert.Empty(decoder.PushedSamples);
        Assert.Equal(0, decoder.RequestAbandonReceptionCallCount);
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_WhileTransmitInFlight_Throws()
    {
        var (service, encoder, _) = CreateServiceWithFakeEncoder();
        var reachedGate = new TaskCompletionSource();
        var releaseGate = new TaskCompletionSource();
        // _transmitInFlight is set at PlayWithPttAsync's own entry, before the encoder is ever
        // enumerated (see that method's own doc comment) -- signaling reachedGate from inside the
        // first yield proves the guard is set by the time this test awaits it, rather than assuming
        // a race resolves in a particular order.
        encoder.BeforeFirstYield = async () =>
        {
            reachedGate.SetResult();
            await releaseGate.Task;
        };

        var transmitTask = service.TransmitAsync(SstvModeRegistry.MartinM1, TestImage1x1);
        await reachedGate.Task;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage1x1));

        releaseGate.SetResult();
        await transmitTask;
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_ConcurrentCalls_SecondThrows()
    {
        // The Interlocked.CompareExchange guard runs synchronously before this method's first
        // `await` -- so by the time the first call below returns a Task (at that first await), the
        // guard is already set, and a second call made without awaiting the first observes it
        // without needing an artificial gate.
        var (service, _, _) = CreateServiceWithFakeEncoder();

        var firstCall = service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage1x1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage1x1));

        await firstCall;
    }

    public static readonly TheoryData<SstvModeDefinition> RepresentativeModes = new()
    {
        SstvModeRegistry.MartinM1,
        // Scottie DX has the longest line duration in the registry (1050.3ms) -- the worst case for
        // the trailing-silence-padding fix (round-5 plan-review finding: TryProcessBuffer can leave
        // the final line undecoded without it).
        SstvModeRegistry.ScottieDx,
        // Paired-line family (RowsPerTransmissionLine == 2) -- proves the step-learned completion
        // detection (round-5 plan-review finding) works for a family that advances 2 rows per
        // LineDecoded event, not just the common 1-row case.
        SstvModeRegistry.Pd50,
    };

    [Theory]
    [MemberData(nameof(RepresentativeModes))]
    public async Task RunLoopbackSelfTestAsync_FullRoundTrip_ReportsCompleted(SstvModeDefinition mode)
    {
        var (service, _, _) = CreateServiceWithRealEncoder();
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var result = await service.RunLoopbackSelfTestAsync(mode, sourceImage);

        Assert.Equal(LoopbackSelfTestOutcome.Completed, result.Outcome);
        Assert.Equal(mode.Id, result.DetectedModeId);
        Assert.Equal(mode.ImageWidth, result.Image.Width);
        Assert.Equal(mode.ImageHeight, result.Image.Height);
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_IgnoresPersistedTxSampleRateOffset()
    {
        // Round-3 plan-review finding: a nonzero persisted TxSampleRateOffsetHz must NOT reach the
        // self-test's own encode call -- confirmed here by setting a large offset and asserting the
        // round trip still completes cleanly (a real encoder that respected the persisted offset
        // would decode at the WRONG rate against this test's fixed-rate decoder construction,
        // corrupting the round trip).
        var (service, _, settingsStore) = CreateServiceWithRealEncoder();
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { TxSampleRateOffsetHz = 1400.0 },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var result = await service.RunLoopbackSelfTestAsync(mode, sourceImage);

        Assert.Equal(LoopbackSelfTestOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_UsesEncoderSampleRate_NotAFreshPersistedSampleRateRead()
    {
        // Code-review round-1 coverage gap: nothing pinned the _encoder.SampleRate-not-a-fresh-read
        // fix -- the other real-encoder tests all use a persisted SampleRate that happens to already
        // match the encoder (11025), so reintroducing the bug (re-deriving sampleRate from a fresh
        // AudioDeviceSettings read) would leave them green. Here the persisted value (48000) actively
        // DISAGREES with the real encoder's own rate (11025, restart-only per
        // ISstvDecoder.SampleRate's own doc comment) -- if the bug were reintroduced, the self-test's
        // internal decoder would be constructed at 48000 while consuming audio actually encoded at
        // 11025, and the round trip would NOT complete.
        var (service, _, settingsStore) = CreateServiceWithRealEncoder(sampleRate: 11025);
        settingsStore.Settings = settingsStore.Settings.WithSection(
            AudioDeviceSettings.SectionKey,
            new AudioDeviceSettings { SampleRate = 48000 },
            AudioSettingsJsonContext.Default.AudioDeviceSettings);
        var mode = SstvModeRegistry.MartinM1;
        var sourceImage = CreateGradientTestImage(mode.ImageWidth, mode.ImageHeight);

        var result = await service.RunLoopbackSelfTestAsync(mode, sourceImage);

        Assert.Equal(LoopbackSelfTestOutcome.Completed, result.Outcome);
    }

    [Fact]
    public async Task RunLoopbackSelfTestAsync_GarbageAudio_ReportsIncomplete()
    {
        var (service, _, _) = CreateServiceWithFakeEncoder();

        var result = await service.RunLoopbackSelfTestAsync(SstvModeRegistry.MartinM1, TestImage1x1);

        Assert.Equal(LoopbackSelfTestOutcome.Incomplete, result.Outcome);
        Assert.Null(result.DetectedModeId);
    }
}
