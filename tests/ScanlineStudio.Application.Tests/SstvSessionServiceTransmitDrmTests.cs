using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Audio;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Application.Tests;

/// <summary>P4a: <see cref="SstvSessionService.TransmitDrmAsync"/> drives the real DRM transmitter through the
/// shared PTT/RX-pause playback path at 48 kHz.</summary>
public sealed class SstvSessionServiceTransmitDrmTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] FakeJp2 = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();

    private static readonly DrmTxSettings Settings = new(
        DrmRobustnessMode.B, DrmBandwidth.Hz2500, DrmQamOrder.Qam16, DrmProtectionLevel.High,
        DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.Rs1, "PA0TST", VoxTone: true, TargetJp2Bytes: 400);

    private static readonly IImageSource Image = new ArrayImageSource(64, 48, Enumerable.Range(0, 64 * 48).Select(i => new Rgb24((byte)i, (byte)(i >> 3), 9)).ToArray());

    [Fact]
    public async Task TransmitDrm_KeysPtt_PlaysTheCoreDrmOutputAt48k_Unkeys_AndResumesRx()
    {
        var h = new Harness();
        await h.Service.StartReceivingAsync();
        var reports = new List<TransmitProgressInfo>();
        h.Service.TransmitProgressChanged += reports.Add;

        await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);

        float[] expected = await ExpectedSamplesAsync();
        Assert.Equal([true, false], h.Radio.PttCalls);
        Assert.Equal(48_000, h.Audio.LastRequestedPlaybackSampleRate);
        Assert.Equal(expected.Length, h.Audio.PlaybackSamples.Count);
        Assert.True(expected.AsSpan().SequenceEqual(h.Audio.PlaybackSamples.ToArray()), "played samples differ from the Core.Drm output");
        Assert.False(h.Audio.IsPlaying);
        Assert.True(h.Service.IsReceiving);
        Assert.Equal(1.0, reports[^1].Fraction);
        Assert.Equal(expected.Length / 48_000.0, reports[^1].EstimatedTotal.TotalSeconds, 6);

        // QSSTV's ratio for 64×48 at 400 bytes, on the image's own RGB bytes.
        Assert.Equal(23, h.Codec.LastRatio);
        Assert.Equal((64, 48, 64 * 3), (h.Codec.LastWidth, h.Codec.LastHeight, h.Codec.LastStride));
        Assert.Equal([0, 0, 9, 1, 0, 9], h.Codec.LastRgb![..6]);
    }

    // 4096-sample pump chunks: chunk 5 lies inside the 48000-sample VOX block, chunk 20 inside the third DRM
    // frame. The pump notices cancellation at its next await, so at most the rest of that block is enqueued
    // (this fake never back-pressures; a real device's wait observes the token sooner).
    [Theory]
    [InlineData(5, 48_000)]
    [InlineData(20, 48_000 + (3 * 19_200))]
    public async Task TransmitDrm_Cancelled_UnkeysStopsPlaybackAndResumesRx(int cancelAtChunk, int blockEnd)
    {
        var h = new Harness();
        await h.Service.StartReceivingAsync();
        using var cts = new CancellationTokenSource();
        int chunks = 0;
        h.Audio.OnPlaybackChunkEnqueued = async () =>
        {
            if (++chunks == cancelAtChunk)
                await cts.CancelAsync();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.TransmitDrmAsync(Image, "photo.png", Settings, cts.Token));

        Assert.Equal([true, false], h.Radio.PttCalls);
        Assert.False(h.Audio.IsPlaying);
        Assert.True(h.Service.IsReceiving);
        Assert.InRange(h.Audio.PlaybackSamples.Count, cancelAtChunk * 4096, blockEnd);
    }

    [Fact]
    public async Task TransmitDrm_TwiceWithTheSameName_StampsEachTransmissionAnew()
    {
        var h = new Harness();

        await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);
        float[] first = [.. h.Audio.PlaybackSamples];
        h.Clock.Now = Stamp.AddMinutes(3);
        await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);
        float[] second = h.Audio.PlaybackSamples.Skip(first.Length).ToArray();

        // A new name is a new MOT transport ID (DABMOT.cpp:114-123), so the second send is not a repeat.
        Assert.Equal(["20260101120000-photo.rs1", "20260101120300-photo.rs1"], h.Transmitter.OnAirNames);
        Assert.False(first.AsSpan().SequenceEqual(second));
    }

    [Fact]
    public async Task TransmitDrm_ProducerFault_PropagatesWithPttUnkeyedAndRxResumed()
    {
        var h = new Harness();
        await h.Service.StartReceivingAsync();
        h.Transmitter.Override = new FaultingTransmission();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.TransmitDrmAsync(Image, "photo.png", Settings));

        Assert.Equal("producer fault", ex.Message);
        Assert.Equal([true, false], h.Radio.PttCalls);
        Assert.False(h.Audio.IsPlaying);
        Assert.True(h.Service.IsReceiving);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TransmitDrm_WhileSstvTxOrTuneIsOnAir_IsRejectedBeforeEncoding(bool tune)
    {
        var h = new Harness(sstvSamples: new float[10_000]);
        Exception? rejection = null;
        h.Audio.OnPlaybackChunkEnqueued = async () =>
        {
            if (rejection is null)
                rejection = await Record.ExceptionAsync(() => h.Service.TransmitDrmAsync(Image, "photo.png", Settings));
        };

        if (tune)
            await h.Service.TuneAsync(1000, TimeSpan.FromSeconds(1));
        else
            await h.Service.TransmitAsync(SstvMode, Image);

        Assert.IsType<InvalidOperationException>(rejection);
        Assert.Equal(0, h.Codec.EncodeCalls);
        Assert.Empty(h.Transmitter.OnAirNames);
        Assert.Equal([true, false], h.Radio.PttCalls);
    }

    [Fact]
    public async Task TransmitDrm_CodecUnavailable_FailsClearly_WithoutKeyingPtt()
    {
        var h = new Harness(codecAvailable: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.TransmitDrmAsync(Image, "photo.png", Settings));

        Assert.Contains("JPEG 2000 codec", ex.Message, StringComparison.Ordinal);
        Assert.Contains("library missing", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Radio.PttCalls);
        Assert.Null(h.Audio.LastRequestedPlaybackSampleRate);
        Assert.Equal(0, h.Codec.EncodeCalls);
    }

    [Fact]
    public async Task TransmitDrm_NotConfigured_FailsWithoutKeyingPtt()
    {
        var h = new Harness(withDrm: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.TransmitDrmAsync(Image, "photo.png", Settings));

        Assert.Empty(h.Radio.PttCalls);
    }

    [Fact]
    public async Task TransmitDrm_EncodeFailure_PropagatesWithoutKeyingPtt()
    {
        var h = new Harness();
        h.Codec.EncodeException = new InvalidOperationException("opj_encode failed");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.TransmitDrmAsync(Image, "photo.png", Settings));

        Assert.Equal("opj_encode failed", ex.Message);
        Assert.Empty(h.Radio.PttCalls);
    }

    private static async Task<float[]> ExpectedSamplesAsync()
    {
        var transmitter = new DrmTransmitter(new DrmTxNaming(new FixedClock(Stamp)), new BsrTransportIdCounter());
        IDrmTransmission tx = transmitter.Prepare(FakeJp2, "photo.png", isImage: true, Settings, newTxFileName: true);
        var all = new List<float>();
        await foreach (ReadOnlyMemory<float> block in tx.ReadSamplesAsync())
            all.AddRange(block.ToArray());
        return [.. all];
    }

    private static readonly SstvModeDefinition SstvMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 64, ImageHeight: 48,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private sealed class Harness
    {
        public Harness(bool codecAvailable = true, bool withDrm = true, float[]? sstvSamples = null)
        {
            var deviceEnumerator = new FakeAudioDeviceEnumerator
            {
                InputDevices = [new AudioDeviceInfo("capture-1", "Capture", 1, 0, [8000])],
                OutputDevices = [new AudioDeviceInfo("playback-1", "Playback", 0, 1, [48000])],
            };
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    AudioDeviceSettings.SectionKey,
                    new AudioDeviceSettings { CaptureDeviceId = "capture-1", PlaybackDeviceId = "playback-1", SampleRate = 8000 },
                    AudioSettingsJsonContext.Default.AudioDeviceSettings),
            };
            Codec = new FakeJpeg2000Codec(codecAvailable);
            Transmitter = new RecordingTransmitter(new DrmTransmitter(new DrmTxNaming(Clock), new BsrTransportIdCounter()));
            IDrmTransmitter? transmitter = withDrm ? Transmitter : null;
            var encoder = sstvSamples is null ? new FakeSstvEncoder() : new FakeSstvEncoder { SamplesToYield = sstvSamples };
            Service = new SstvSessionService(
                Audio, deviceEnumerator, new FakeAudioDeviceMuteQuery(), settingsStore, new FakeSstvDecoder(), encoder,
                new MacroTextResolver(), new FakeWaterfallSource(), new FakeReceivedImageBuffer(), Radio, new FakeCwIdDecoder(),
                NullLogger<SstvSessionService>.Instance, transmitter, withDrm ? Codec : null);
        }

        public FakeAudioEngine Audio { get; } = new();

        public FakeRadioSessionService Radio { get; } = new();

        public FakeJpeg2000Codec Codec { get; }

        public FixedClock Clock { get; } = new(Stamp);

        public RecordingTransmitter Transmitter { get; }

        public SstvSessionService Service { get; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The real transmitter, recording each prepared on-air name; <see cref="Override"/> replaces the result.</summary>
    private sealed class RecordingTransmitter(IDrmTransmitter inner) : IDrmTransmitter
    {
        public List<string> OnAirNames { get; } = [];

        public IDrmTransmission? Override { get; set; }

        public int SampleRate => inner.SampleRate;

        public IDrmTransmission Prepare(ReadOnlySpan<byte> payload, string fileName, bool isImage, DrmTxSettings settings, bool newTxFileName)
        {
            IDrmTransmission tx = inner.Prepare(payload, fileName, isImage, settings, newTxFileName);
            OnAirNames.Add(tx.OnAirName);
            return Override ?? tx;
        }

        public void ForgetTxFileName() => inner.ForgetTxFileName();
    }

    private sealed class FaultingTransmission : IDrmTransmission
    {
        public int FrameCount => 3;

        public long SampleCount => 3 * 19_200;

        public TimeSpan DisplayDuration => TimeSpan.FromSeconds(1.2);

        public string OnAirName => "fault.jp2";

        public async IAsyncEnumerable<ReadOnlyMemory<float>> ReadSamplesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new float[19_200];
            await Task.Yield();
            throw new InvalidOperationException("producer fault");
        }
    }

    private sealed class FakeJpeg2000Codec(bool available) : IJpeg2000Codec
    {
        public int EncodeCalls { get; private set; }

        public int LastRatio { get; private set; } = -1;

        public int LastWidth { get; private set; }

        public int LastHeight { get; private set; }

        public int LastStride { get; private set; }

        public byte[]? LastRgb { get; private set; }

        public Exception? EncodeException { get; set; }

        public bool IsAvailable => available;

        public string? UnavailableReason => available ? null : "library missing";

        public byte[] EncodeRgb24(ReadOnlySpan<byte> rgb24, int width, int height, int stride, int compressionRatio)
        {
            EncodeCalls++;
            (LastRgb, LastWidth, LastHeight, LastStride, LastRatio) = (rgb24.ToArray(), width, height, stride, compressionRatio);
            return EncodeException is { } ex ? throw ex : FakeJp2;
        }

        public IImageSource Decode(ReadOnlySpan<byte> data) => throw new NotSupportedException();
    }
}
