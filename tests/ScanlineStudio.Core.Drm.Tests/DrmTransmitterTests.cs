using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Tx;
using PublicSettings = ScanlineStudio.Abstractions.Drm.DrmTxSettings;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P4a: the public transmitter (naming, frame count, VOX, the background producer) end to end
/// against the P1-P3 pipeline and the committed QSSTV goldens.</summary>
public class DrmTransmitterTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static PublicSettings Settings(DrmRobustnessMode mode, DrmQamOrder qam, DrmReedSolomonLevel rs, bool vox = false) =>
        new(mode, DrmBandwidth.Hz2500, qam, DrmProtectionLevel.High, DrmInterleaving.LongInterleaver, rs, "PA0TST", vox, 400);

    private static DrmTransmitter NewTransmitter() => new(new DrmTxNaming(new FixedTimeProvider(Stamp)), new BsrTransportIdCounter());

    [Theory]
    [InlineData("B1_q4_high_long_rs1_jp2", "card.jp2", DrmQamOrder.Qam4, DrmReedSolomonLevel.Rs1)]
    [InlineData("B1_q16_high_long_jp2", "card.jp2", DrmQamOrder.Qam16, DrmReedSolomonLevel.None)]
    public async Task ImageTransmission_MatchesTheQsstvGoldens(string caseId, string input, DrmQamOrder qam, DrmReedSolomonLevel rs)
    {
        using JsonDocument doc = GoldenFixtures.LoadManifest(caseId);
        JsonElement m = doc.RootElement;
        JsonElement o = m.GetProperty("stages").GetProperty("out48k");
        byte[] payload = File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", input));

        IDrmTransmission tx = NewTransmitter().Prepare(payload, input, isImage: true, Settings(DrmRobustnessMode.B, qam, rs), newTxFileName: true);
        short[] ours = ToInt16(await ReadAllAsync(tx));

        int frameSamples = o.GetProperty("block_size").GetInt32();
        Assert.Equal(o.GetProperty("blocks").GetInt32(), tx.FrameCount);
        Assert.Equal((long)tx.FrameCount * frameSamples, tx.SampleCount);
        Assert.Equal(tx.SampleCount, ours.Length);
        Assert.Equal(m.GetProperty("mot_object_name").GetString(), tx.OnAirName);
        Assert.Equal(m.GetProperty("duration_estimate_s").GetDouble(), tx.DisplayDuration.TotalSeconds, 2);

        // Same tolerance as TxChannelGoldenTests: ±1 LSB from FFT rounding.
        short[] head = ReadInt16(caseId, "out48k_head.bin.gz");
        short[] tail = ReadInt16(caseId, "out48k_tail.bin.gz");
        AssertWithinOneLsb(head, ours.AsSpan(0, head.Length));
        AssertWithinOneLsb(tail, ours.AsSpan(ours.Length - tail.Length));
    }

    [Fact]
    public async Task Output_IsExactlyThePipelineOutput_PrecededByTheVoxTone()
    {
        byte[] payload = File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", "test.txt"));
        PublicSettings settings = Settings(DrmRobustnessMode.A, DrmQamOrder.Qam16, DrmReedSolomonLevel.Rs2, vox: true);

        IDrmTransmission tx = NewTransmitter().Prepare(payload, "test.txt", isImage: false, settings, newTxFileName: true);
        short[] ours = ToInt16(await ReadAllAsync(tx));

        ScanlineStudio.Core.Drm.Tx.DrmTxSettings core = DrmTransmitter.ToCoreSettings(settings);
        var naming = new DrmTxNaming(new FixedTimeProvider(Stamp));
        DrmTxObject obj = DrmTxObject.ForFile(payload, naming.GetTxFileName("test.txt", binary: true), core.ReedSolomon);
        short[] frames = new DrmTxChannel(core).Modulate(new DrmSourceFrameGenerator(obj, core, new BsrTransportIdCounter()).Frames()).SelectMany(f => f).ToArray();
        short[] vox = DrmVoxTone.GenerateInt16(48_000);

        Assert.Equal(vox.Length + frames.Length, ours.Length);
        Assert.Equal(tx.SampleCount, ours.Length);
        Assert.True(ours.AsSpan(0, vox.Length).SequenceEqual(vox));
        Assert.True(ours.AsSpan(vox.Length).SequenceEqual(frames));
    }

    [Fact]
    public void VoxTone_IsQsstvsTableSynthesis()
    {
        short[] vox = DrmVoxTone.GenerateInt16(48_000);

        // Reference: the same table lookup computed independently (Python, C fmod/round semantics).
        Assert.Equal(48_000, vox.Length);
        Assert.Equal([1777, 3443, 4960, 6215, 7179, 7778], vox[..6]);
        Assert.Equal([-3443, -1777, 0], vox[^3..]);
        Assert.Equal(8000, vox.Max());
        Assert.Equal(-8000, vox.Min());
        Assert.Equal("972661a99a025a50af1e1c308a93416b4c3ade6190240359bb36dbce3928f9b7",
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(vox.AsSpan()))), ignoreCase: true);
    }

    [Fact]
    public void Naming_NewNameStampsAgain_ReuseKeepsTheStamp()
    {
        var clock = new FixedTimeProvider(Stamp);
        var transmitter = new DrmTransmitter(new DrmTxNaming(clock), new BsrTransportIdCounter());
        PublicSettings settings = Settings(DrmRobustnessMode.B, DrmQamOrder.Qam4, DrmReedSolomonLevel.None);

        string first = transmitter.Prepare("x"u8, "photo.png", isImage: true, settings, newTxFileName: true).OnAirName;
        clock.Now = Stamp.AddMinutes(5);
        string reused = transmitter.Prepare("x"u8, "photo.png", isImage: true, settings, newTxFileName: false).OnAirName;
        string renewed = transmitter.Prepare("x"u8, "photo.png", isImage: true, settings, newTxFileName: true).OnAirName;
        clock.Now = Stamp.AddMinutes(9);
        transmitter.ForgetTxFileName();
        string afterForget = transmitter.Prepare("x"u8, "photo.png", isImage: true, settings, newTxFileName: false).OnAirName;

        Assert.Equal("20260101120000-photo.jp2", first);
        Assert.Equal(first, reused);
        Assert.Equal("20260101120500-photo.jp2", renewed);
        Assert.Equal("20260101120900-photo.jp2", afterForget);
    }

    [Fact]
    public async Task ReadSamples_RunsOnce_AndCancellationStopsTheProducer()
    {
        IDrmTransmission tx = NewTransmitter().Prepare(new byte[3000], "big.bin", isImage: false, Settings(DrmRobustnessMode.E, DrmQamOrder.Qam4, DrmReedSolomonLevel.None), newTxFileName: true);
        using var cts = new CancellationTokenSource();
        int blocks = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (ReadOnlyMemory<float> _ in tx.ReadSamplesAsync(cts.Token))
            {
                if (++blocks == 2)
                    await cts.CancelAsync();
            }
        });

        Assert.Equal(2, blocks);
        Assert.True(tx.FrameCount > 2);
        Assert.Throws<InvalidOperationException>(() => tx.ReadSamplesAsync());
    }

    [Fact]
    public async Task ProducerFault_ReachesTheConsumer_AfterTheFramesBeforeIt()
    {
        var source = new FrameSource(frames: 5, throwAt: 2);
        var tx = new DrmTransmission(source.Frames, 4, 5, 5, voxTone: false, "x"u8);
        var seen = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (ReadOnlyMemory<float> _ in tx.ReadSamplesAsync())
                seen++;
        });

        Assert.Equal("producer fault", ex.Message);
        Assert.Equal(2, seen);
        Assert.True(source.Stopped);
    }

    [Fact]
    public async Task ConsumerThatStopsReading_StopsTheProducer()
    {
        var source = new FrameSource(frames: 1000, throwAt: -1);
        var tx = new DrmTransmission(source.Frames, 4, 1000, 1000, voxTone: false, "x"u8);

        await using (IAsyncEnumerator<ReadOnlyMemory<float>> e = tx.ReadSamplesAsync().GetAsyncEnumerator())
            Assert.True(await e.MoveNextAsync());

        Assert.NotNull(tx.ProducerTask);
        Assert.True(tx.ProducerTask!.IsCompleted);
        Assert.True(source.Stopped);
        Assert.InRange(source.Produced, 1, 1 + DrmTransmission.BufferedFrames + 1);
    }

    [Fact]
    public async Task Cancel_DoesNotYieldFramesAlreadyBuffered()
    {
        var source = new FrameSource(frames: 1000, throwAt: -1);
        var tx = new DrmTransmission(source.Frames, 4, 1000, 1000, voxTone: false, "x"u8);
        using var cts = new CancellationTokenSource();

        await using IAsyncEnumerator<ReadOnlyMemory<float>> e = tx.ReadSamplesAsync(cts.Token).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        // Frame 1 read, the channel full, and one more frame waiting to be written.
        Assert.True(SpinWait.SpinUntil(() => source.Produced >= DrmTransmission.BufferedFrames + 2, TimeSpan.FromSeconds(10)));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await e.MoveNextAsync());
    }

    [Theory]
    [InlineData(64, 48, 400, 23)]
    [InlineData(320, 256, 15_000, 16)]
    [InlineData(64, 48, 9216, 1)]
    [InlineData(64, 48, 9217, 0)]
    public void Jp2Ratio_IsQsstvsIntegerDivision(int w, int h, int target, int expected) =>
        Assert.Equal(expected, DrmJp2Ratio.FromTargetSize(w, h, target));

    [Fact]
    public void Prepare_RejectsAnEmptyPayload() =>
        Assert.Throws<ArgumentException>(() => NewTransmitter().Prepare([], "a.bin", isImage: false, Settings(DrmRobustnessMode.B, DrmQamOrder.Qam4, DrmReedSolomonLevel.None), newTxFileName: true));

    private static async Task<float[]> ReadAllAsync(IDrmTransmission tx)
    {
        var all = new List<float>();
        await foreach (ReadOnlyMemory<float> block in tx.ReadSamplesAsync())
            all.AddRange(block.ToArray());
        return [.. all];
    }

    private static short[] ToInt16(float[] samples) => samples.Select(s => checked((short)(s * 32768f))).ToArray();

    private static short[] ReadInt16(string caseId, string stored) =>
        MemoryMarshal.Cast<byte, short>(GoldenFixtures.ReadStored(caseId, stored)).ToArray();

    private static void AssertWithinOneLsb(short[] golden, ReadOnlySpan<short> ours)
    {
        Assert.Equal(golden.Length, ours.Length);
        int off = 0;
        for (int i = 0; i < golden.Length; i++)
        {
            int d = Math.Abs(golden[i] - ours[i]);
            Assert.True(d <= 1, $"sample {i}: {golden[i]} vs {ours[i]}");
            off += d;
        }

        Assert.True(off * 1000 <= golden.Length, $"{off} samples off by 1");
    }

    /// <summary>A stand-in for the source → channel pipeline that records how far it ran.</summary>
    private sealed class FrameSource(int frames, int throwAt)
    {
        public int Produced { get; private set; }

        public bool Stopped { get; private set; }

        public IEnumerable<short[]> Frames()
        {
            try
            {
                for (int i = 0; i < frames; i++)
                {
                    if (i == throwAt)
                        throw new InvalidOperationException("producer fault");
                    Produced++;
                    yield return [1, 2, 3, 4];
                }
            }
            finally
            {
                Stopped = true;
            }
        }
    }
}
