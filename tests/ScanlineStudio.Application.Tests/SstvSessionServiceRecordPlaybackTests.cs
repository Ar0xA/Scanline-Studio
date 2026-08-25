using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>Piece C1/C2 (RX tab Re-decode port, `spec/16-gui-wiring-survey.md`) -- see
/// `/home/artien/.claude/plans/reflective-squishing-clock.md`'s "Piece C" section for the full
/// legacy citations and round-2 plan-review corrections these tests pin.</summary>
public sealed class SstvSessionServiceRecordPlaybackTests
{
    private static (SstvSessionService Service, FakeAudioEngine AudioEngine, FakeSstvDecoder Decoder,
        FakeWaterfallSource Waterfall, FakeRadioSessionService RadioSession)
        CreateService()
    {
        var audioEngine = new FakeAudioEngine();
        var deviceEnumerator = new FakeAudioDeviceEnumerator
        {
            InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [11025])],
            OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [11025])],
        };
        var deviceMuteQuery = new FakeAudioDeviceMuteQuery();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                AudioDeviceSettings.SectionKey,
                new AudioDeviceSettings
                {
                    CaptureDeviceId = "capture-1",
                    PlaybackDeviceId = "playback-1",
                    SampleRate = 11025,
                },
                AudioSettingsJsonContext.Default.AudioDeviceSettings),
        };
        var decoder = new FakeSstvDecoder();
        var encoder = new FakeSstvEncoder();
        var waterfall = new FakeWaterfallSource();
        var receivedImage = new FakeReceivedImageBuffer();
        var radioSession = new FakeRadioSessionService();

        var service = new SstvSessionService(
            audioEngine, deviceEnumerator, deviceMuteQuery, settingsStore, decoder, encoder,
            new MacroTextResolver(), waterfall, receivedImage, radioSession, NullLogger<SstvSessionService>.Instance);
        return (service, audioEngine, decoder, waterfall, radioSession);
    }

    // ---- C1: Recording ----

    [Fact]
    public async Task StartRecordingAsync_WhileNotReceiving_Throws()
    {
        var (service, _, _, _, _) = CreateService();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartRecordingAsync(Path.GetTempFileName()));
        Assert.Equal("Cannot start recording while not receiving.", ex.Message);
    }

    [Fact]
    public async Task StartRecordingAsync_AlreadyRecording_Throws()
    {
        var (service, _, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        var path = Path.GetTempFileName();
        try
        {
            await service.StartRecordingAsync(path);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartRecordingAsync(path));
            Assert.Equal("A recording is already in progress.", ex.Message);
        }
        finally
        {
            await service.StopRecordingAsync();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task StopRecordingAsync_NoActiveRecording_IsNoOpAndWritesNoFile()
    {
        var (service, _, _, _, _) = CreateService();
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".wav");

        await service.StopRecordingAsync();

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task StartRecordingAsync_ThenCapture_StopWritesReadableWavFile()
    {
        var (service, audioEngine, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        var path = Path.GetTempFileName();
        try
        {
            await service.StartRecordingAsync(path);
            float[] firstChunk = [0f, 0.5f, -0.5f];
            float[] secondChunk = [0.5f, 0f];
            audioEngine.PushCapturedSamples(firstChunk);
            audioEngine.PushCapturedSamples(secondChunk);
            await service.StopRecordingAsync();

            var (samples, sampleRate) = WavFile.Read(path);
            Assert.Equal(11025, sampleRate);
            Assert.Equal(new float[] { 0f, 0.5f, -0.5f, 0.5f, 0f }, samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Recording_SurvivesStopStartReceivingCycle()
    {
        var (service, audioEngine, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        var path = Path.GetTempFileName();
        try
        {
            await service.StartRecordingAsync(path);
            float[] beforeStop = [0.5f];
            audioEngine.PushCapturedSamples(beforeStop);

            await service.StopReceivingAsync();
            await service.StartReceivingAsync();
            float[] afterRestart = [-0.5f];
            audioEngine.PushCapturedSamples(afterRestart);

            await service.StopRecordingAsync();

            var (samples, _) = WavFile.Read(path);
            Assert.Equal(new float[] { 0.5f, -0.5f }, samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DisposeAsync_FinalizesInProgressRecording_InsteadOfDiscardingIt()
    {
        var (service, audioEngine, _, _, _) = CreateService();
        await service.StartReceivingAsync();
        var path = Path.GetTempFileName();
        try
        {
            await service.StartRecordingAsync(path);
            float[] chunk = [0.5f, -0.5f];
            audioEngine.PushCapturedSamples(chunk);

            await service.DisposeAsync();

            var (samples, _) = WavFile.Read(path);
            Assert.Equal(new float[] { 0.5f, -0.5f }, samples);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---- C2: Playback ----

    [Fact]
    public async Task DecodeFromFileAsync_RateMismatch_Throws()
    {
        var (service, _, decoder, _, _) = CreateService();
        decoder.SampleRate = 11025;
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, [0f, 0f], sampleRate: 8000);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecodeFromFileAsync(path));
            Assert.Equal("File sample rate (8000 Hz) does not match the configured decode rate (11025 Hz).", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_EmptyFile_Throws()
    {
        var (service, _, decoder, _, _) = CreateService();
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, [], sampleRate: decoder.SampleRate);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecodeFromFileAsync(path));
            Assert.Equal("File contains no audio samples.", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_WhileAutoDetectPaused_Throws()
    {
        var (service, _, decoder, _, _) = CreateService();
        service.SetAutoDetectPaused(true);
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, [0f], sampleRate: decoder.SampleRate);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecodeFromFileAsync(path));
            Assert.Equal("Cannot decode a file while auto-detect is paused.", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_WhileTransmitInFlight_Throws()
    {
        // Round-2 plan-review decision: the file-decode/transmit cross-guard is bidirectional -- this
        // pins the DecodeFromFileAsync-rejects-during-a-transmit direction; the mirrored
        // transmit-rejects-during-a-file-decode direction is pinned by
        // DecodeFromFileAsync_InProgress_BlocksTransmit below.
        var (service, _, decoder, _, radio) = CreateService();
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, [0f], sampleRate: decoder.SampleRate);
            radio.BeforeSetPtt = tx =>
            {
                if (tx)
                {
#pragma warning disable xUnit1031
                    var ex = Assert.ThrowsAsync<InvalidOperationException>(() => service.DecodeFromFileAsync(path)).GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                    Assert.Equal("Cannot decode a file while a transmit or tune is in progress.", ex.Message);
                }
            };

            await service.TuneAsync(1750, TimeSpan.FromMilliseconds(1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_AlreadyInProgress_Throws()
    {
        var (service, _, decoder, _, _) = CreateService();
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, new float[10_000], sampleRate: decoder.SampleRate);

            var reachedFirstChunk = new TaskCompletionSource();
            var releaseFirstChunk = new TaskCompletionSource();
            decoder.OnPushSamples = _ =>
            {
                reachedFirstChunk.TrySetResult();
                // Runs on DecodeFromFileAsync's own background Task.Run thread, not the test's async
                // continuation -- blocking here is intentional (holds the chunk loop open) and safe.
#pragma warning disable xUnit1031
                releaseFirstChunk.Task.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            };

            var firstCall = service.DecodeFromFileAsync(path);
            await reachedFirstChunk.Task;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DecodeFromFileAsync(path));
            Assert.Equal("A file decode is already in progress.", ex.Message);

            releaseFirstChunk.SetResult();
            await firstCall;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_InProgress_BlocksTransmit()
    {
        var (service, _, decoder, _, radio) = CreateService();
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, new float[10_000], sampleRate: decoder.SampleRate);

            var reachedFirstChunk = new TaskCompletionSource();
            var releaseFirstChunk = new TaskCompletionSource();
            decoder.OnPushSamples = _ =>
            {
                reachedFirstChunk.TrySetResult();
                // Runs on DecodeFromFileAsync's own background Task.Run thread, not the test's async
                // continuation -- blocking here is intentional (holds the chunk loop open) and safe.
#pragma warning disable xUnit1031
                releaseFirstChunk.Task.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
            };

            var decodeCall = service.DecodeFromFileAsync(path);
            await reachedFirstChunk.Task;

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.TuneAsync(1750, TimeSpan.FromMilliseconds(1)));
            Assert.Equal("Cannot transmit or tune while a file decode is in progress.", ex.Message);
            Assert.Empty(radio.PttCalls);

            releaseFirstChunk.SetResult();
            await decodeCall;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_ValidFile_PushesAllChunksToDecoderAndWaterfall_AndResumesReceiving()
    {
        var (service, _, decoder, waterfall, _) = CreateService();
        await service.StartReceivingAsync();
        var path = Path.GetTempFileName();
        try
        {
            var fileSamples = new float[10_000];
            WavFile.Write(path, fileSamples, sampleRate: decoder.SampleRate);
            decoder.PushedSamples.Clear();
            waterfall.PushedSamples.Clear();

            await service.DecodeFromFileAsync(path);

            Assert.True(decoder.PushedSamples.Count > 1, "Expected the 10,000-sample file to be pushed as multiple chunks.");
            Assert.Equal(fileSamples.Length, decoder.PushedSamples.Sum(c => c.Length));
            Assert.Equal(fileSamples.Length, waterfall.PushedSamples.Sum(c => c.Length));
            Assert.Equal(1, decoder.RequestAbandonReceptionCallCount);
            Assert.True(service.IsReceiving);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task DecodeFromFileAsync_NotReceivingBeforehand_DoesNotStartReceiving()
    {
        var (service, _, decoder, _, _) = CreateService();
        var path = Path.GetTempFileName();
        try
        {
            WavFile.Write(path, new float[10], sampleRate: decoder.SampleRate);

            await service.DecodeFromFileAsync(path);

            Assert.False(service.IsReceiving);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
