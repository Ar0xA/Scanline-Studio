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
            await h.Service.TuneAsync(TxLevelFamily.Sstv, 1000, TimeSpan.FromSeconds(1));
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

    private static readonly float[] SstvSamples = Enumerable.Range(0, 9000).Select(i => MathF.Sin(i * 0.05f) * 0.5f).ToArray();

    // Addendum A: each entry point plays at its own family's level (SSTV 100, DRM 40).
    [Fact]
    public async Task TransmitDrm_PlaysAtTheDrmLevel_AndSstvAndTuneAtTheirOwn()
    {
        var h = new Harness(sstvSamples: SstvSamples);
        await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Sstv, 100);
        await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Drm, 40);

        DrmTxResult result = await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);

        float[] expected = (await ExpectedSamplesAsync()).Select(x => x * 0.4f).ToArray();
        float[] drm = [.. h.Audio.PlaybackSamples];
        Assert.True(expected.AsSpan().SequenceEqual(drm), "DRM samples are not the Core output at the DRM level");
        Assert.Equal(FakeJp2.Length, result.Jp2Bytes);
        Assert.Equal("20260101120000-photo.rs1", result.OnAirName);

        await h.Service.TransmitAsync(SstvMode, Image);
        Assert.True(SstvSamples.AsSpan().SequenceEqual(h.Audio.PlaybackSamples.Skip(drm.Length).ToArray()), "SSTV did not play at the SSTV level");

        int beforeTune = h.Audio.PlaybackSamples.Count;
        await h.Service.TuneAsync(TxLevelFamily.Sstv, 1750, TimeSpan.FromMilliseconds(200));
        Assert.InRange(h.Audio.PlaybackSamples.Skip(beforeTune).Max(Math.Abs), 0.999f, 1.001f);
        beforeTune = h.Audio.PlaybackSamples.Count;
        await h.Service.TuneAsync(TxLevelFamily.Drm, 1750, TimeSpan.FromMilliseconds(200));
        Assert.InRange(h.Audio.PlaybackSamples.Skip(beforeTune).Max(Math.Abs), 0.399f, 0.401f);
    }

    [Theory]
    [InlineData(TxLevelFamily.Drm, 0.5f)]
    [InlineData(TxLevelFamily.Sstv, 1f)]
    public async Task TransmitDrm_PwrChangedMidTransmission_OnlyTheDrmLevelReachesTheLiveGain(TxLevelFamily changed, float laterGain)
    {
        var h = new Harness();
        bool set = false;
        h.Audio.OnPlaybackChunkEnqueued = async () =>
        {
            if (!set)
            {
                set = true;
                await h.Service.SetTxVolumePercentAsync(changed, 50);
            }
        };

        await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);

        float[] core = await ExpectedSamplesAsync();
        float[] played = [.. h.Audio.PlaybackSamples];
        for (int i = 0; i < 4096; i++)
            Assert.Equal(core[i], played[i]);
        for (int i = 4096; i < 3 * 4096; i++)
            Assert.Equal(core[i] * laterGain, played[i]);
    }

    // Addendum A: an SSTV write committed before the DRM seed but applied after it must not reach the live gain,
    // while a later DRM write still does. Gates: the write is held between its commit and its apply.
    [Fact]
    public async Task TransmitDrm_OtherFamilyWriteAppliedAfterTheSeed_DoesNotOverrideIt_ALaterDrmWriteStillWins()
    {
        var h = new Harness();
        await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Drm, 40);
        var applyGate = new TaskCompletionSource();
        h.Store.AfterUpdate = () => applyGate.Task;
        Task heldSstvWrite = h.Service.SetTxVolumePercentAsync(TxLevelFamily.Sstv, 20);
        int chunk = 0;
        h.Audio.OnPlaybackChunkEnqueued = async () =>
        {
            chunk++;
            if (chunk == 1)
            {
                applyGate.SetResult();
                await heldSstvWrite;
            }
            else if (chunk == 2)
            {
                await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Drm, 50);
            }
        };

        await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);

        float[] core = await ExpectedSamplesAsync();
        float[] played = [.. h.Audio.PlaybackSamples];
        for (int i = 0; i < 2 * 4096; i++)
            Assert.Equal(core[i] * 0.4f, played[i]);
        for (int i = 2 * 4096; i < 3 * 4096; i++)
            Assert.Equal(core[i] * 0.5f, played[i]);
    }

    // The seed makes DRM the live family before it reads settings, so an SSTV write landing between the read and
    // the seed's own apply cannot take the live gain.
    [Fact]
    public async Task TransmitDrm_SstvWriteLandingInsideTheSeed_DoesNotTakeTheLiveGain()
    {
        var h = new Harness();
        await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Drm, 40);
        h.Store.AfterUpdate = () => h.Service.SetTxVolumePercentAsync(TxLevelFamily.Sstv, 20);

        await h.Service.TransmitDrmAsync(Image, "photo.png", Settings);

        float[] core = await ExpectedSamplesAsync();
        float[] played = [.. h.Audio.PlaybackSamples];
        for (int i = 0; i < 4096; i++)
            Assert.Equal(core[i] * 0.4f, played[i]);
    }

    [Fact]
    public async Task TuneForDrm_DragDuringTheTone_ChangesTheDrmLevelAndTheLiveGain()
    {
        var h = new Harness();
        await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Sstv, 100);
        bool set = false;
        h.Audio.OnPlaybackChunkEnqueued = async () =>
        {
            if (!set)
            {
                set = true;
                await h.Service.SetTxVolumePercentAsync(TxLevelFamily.Drm, 50);
            }
        };

        await h.Service.TuneAsync(TxLevelFamily.Drm, 1750, TimeSpan.FromMilliseconds(200));

        float[] played = [.. h.Audio.PlaybackSamples];
        Assert.InRange(played.Take(4096).Max(Math.Abs), 0.999f, 1.001f);
        Assert.InRange(played.Skip(4096).Max(Math.Abs), 0.499f, 0.501f);
        Assert.Equal(50, await h.Service.GetTxVolumePercentAsync(TxLevelFamily.Drm));
        Assert.Equal(100, await h.Service.GetTxVolumePercentAsync(TxLevelFamily.Sstv));
    }

    [Fact]
    public async Task EstimateDrm_EncodesLikeTheSend_CountsItsLength_WithoutKeyingOrStamping()
    {
        var h = new Harness();

        DrmEstimate estimate = await h.Service.EstimateDrmAsync(Image, "photo.png", Settings);
        TimeSpan recount = await h.Service.EstimateDrmDurationAsync(estimate.Jp2, "photo.png", Settings);

        float[] sent = await ExpectedSamplesAsync();
        Assert.Equal(FakeJp2, estimate.Jp2);
        Assert.Equal(23, h.Codec.LastRatio);
        Assert.Equal(sent.Length / 48_000.0, estimate.Duration.TotalSeconds, 9);
        Assert.Equal(estimate.Duration, recount);
        Assert.Empty(h.Radio.PttCalls);
        Assert.Empty(h.Transmitter.OnAirNames);
        Assert.Null(h.Audio.LastRequestedPlaybackSampleRate);
    }

    [Fact]
    public async Task EstimateDrmDuration_ProfileChange_RecountsWithoutAnotherEncode()
    {
        var h = new Harness();
        DrmEstimate estimate = await h.Service.EstimateDrmAsync(Image, "photo.png", Settings);

        TimeSpan fast = await h.Service.EstimateDrmDurationAsync(estimate.Jp2, "photo.png", Settings with { Mode = DrmRobustnessMode.E, Qam = DrmQamOrder.Qam64, VoxTone = false });

        Assert.Equal(1, h.Codec.EncodeCalls);
        Assert.True(fast < estimate.Duration);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public async Task GetDrmTransmitAvailability_NeedsATransmitterAndALoadableCodec(bool codecAvailable, bool withDrm, bool expected)
    {
        var h = new Harness(codecAvailable, withDrm);

        Assert.Equal(expected, await h.Service.GetDrmTransmitAvailabilityAsync());
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
            Store = settingsStore;
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

        public FakeSettingsStore Store { get; }

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

        public DrmFrameCount CountFrames(ReadOnlySpan<byte> jp2, string fileName, DrmTxSettings settings) => inner.CountFrames(jp2, fileName, settings);

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
