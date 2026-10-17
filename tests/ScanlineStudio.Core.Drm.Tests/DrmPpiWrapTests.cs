using ScanlineStudio.Abstractions.Drm;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tx;
using Xunit.Abstractions;
using PublicSettings = ScanlineStudio.Abstractions.Drm.DrmTxSettings;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P6b-1: QSSTV TX writes the last packet's PPI useful-byte count in 8 bits, so in A/64-QAM/low and
/// A/SO1/64-QAM/high a last body segment of L ≥ 245 bytes (group L + 11 ≥ 256) arrives wrapped and QSSTV RX never
/// completes the file.</summary>
public sealed class DrmPpiWrapTests(ITestOutputHelper output)
{
    // A body segment array is the 2-byte segment header plus L data bytes; its data group adds 9 more (group header 2,
    // session 2, user access 3, CRC 2), so the last packet's PPI count is L + 11.
    private const int SegmentArrayOverhead = 2;
    private static readonly DateTimeOffset Stamp = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Every A/64-QAM config with room for a wrapped count: L = 244 (no wrap), 245 (count byte 0), 246 and
    /// the maximum, where L + 11 is the packet's PPI maximum of packet bytes − 4.</summary>
    public static TheoryData<DrmBandwidth, DrmProtectionLevel, DrmInterleaving, int> Cases()
    {
        var data = new TheoryData<DrmBandwidth, DrmProtectionLevel, DrmInterleaving, int>();
        foreach (DrmBandwidth bw in new[] { DrmBandwidth.Hz2200, DrmBandwidth.Hz2500 })
        {
            foreach (DrmProtectionLevel p in new[] { DrmProtectionLevel.Low, DrmProtectionLevel.High })
            {
                int maxL = MaxL(bw, p);
                if (maxL < 245)
                    continue;
                foreach (DrmInterleaving il in new[] { DrmInterleaving.ShortInterleaver, DrmInterleaving.LongInterleaver })
                {
                    foreach (int l in new[] { 244, 245, 246, maxL }.Distinct())
                        data.Add(bw, p, il, l);
                }
            }
        }

        return data;
    }

    [Fact]
    public void TheWrapConfigs_AreExactlyALowSo0_ALowSo1_AndAHighSo1()
    {
        Assert.Equal(273, MaxL(DrmBandwidth.Hz2200, DrmProtectionLevel.Low));
        Assert.Equal(298, MaxL(DrmBandwidth.Hz2500, DrmProtectionLevel.Low));
        Assert.Equal(246, MaxL(DrmBandwidth.Hz2500, DrmProtectionLevel.High));
        Assert.True(MaxL(DrmBandwidth.Hz2200, DrmProtectionLevel.High) < 245);
    }

    private static PublicSettings Settings(DrmBandwidth bw, DrmInterleaving il, DrmReedSolomonLevel rs = DrmReedSolomonLevel.None,
        DrmProtectionLevel p = DrmProtectionLevel.Low) =>
        new(DrmRobustnessMode.A, bw, DrmQamOrder.Qam64, p, il, rs, "PA0TST", true, 400);

    private static DrmSourceFrameGenerator Generator(PublicSettings settings, int payloadLength)
    {
        var core = DrmTransmitter.ToCoreSettings(settings);
        DrmTxFileName name = new DrmTxNaming(new FixedTimeProvider(Stamp)).GetTxFileName("ppi.bin", binary: true);
        return new DrmSourceFrameGenerator(DrmTxObject.ForFile(new byte[payloadLength], name, core.ReedSolomon), core, new BsrTransportIdCounter());
    }

    // PacketLength is the data field (QSSTV iPacketLen); the packet adds 3 bytes and a body segment holds 14 fewer.
    private static int PacketLength(DrmBandwidth bw, DrmProtectionLevel p = DrmProtectionLevel.Low) =>
        Generator(Settings(bw, DrmInterleaving.LongInterleaver, p: p), 100).PacketLength;

    private static int MaxL(DrmBandwidth bw, DrmProtectionLevel p) => PacketLength(bw, p) - 12;

    /// <summary>The smallest payload of at least two full segments whose last body segment carries <paramref name="l"/> bytes.</summary>
    private static int PayloadFor(PublicSettings settings, int l)
    {
        int segment = Generator(settings, 100).PacketLength - 11;
        for (int n = 2 * segment; n < (2 * segment) + 1000; n++)
        {
            if (Generator(settings, n).BodySegments[^1].Length - SegmentArrayOverhead == l)
                return n;
        }

        throw new InvalidOperationException($"no payload gives a last segment of {l} bytes");
    }

    private static async Task<List<DrmReceivedFile>> RoundTripAsync(byte[] payload, PublicSettings settings, DrmRxOptions options)
    {
        var tx = new DrmTransmitter(new DrmTxNaming(new FixedTimeProvider(Stamp)), new BsrTransportIdCounter());
        IDrmTransmission t = tx.Prepare(payload, "ppi.bin", isImage: false, settings, newTxFileName: true);
        var receiver = new DrmReceiver(48000, options);
        var files = new List<DrmReceivedFile>();
        receiver.FileReceived += files.Add;
        await foreach (ReadOnlyMemory<float> chunk in t.ReadSamplesAsync())
            receiver.Push(chunk.Span);
        receiver.ZeroFill(48000 * 2);
        return files;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ALastSegmentPastTheWrap_FailsInQsstv_AndCompletesWithTheFix(DrmBandwidth bw, DrmProtectionLevel p, DrmInterleaving il, int l)
    {
        PublicSettings settings = Settings(bw, il, p: p);
        int n = PayloadFor(settings, l);
        Assert.Equal(l, Generator(settings, n).BodySegments[^1].Length - SegmentArrayOverhead);
        byte[] payload = new byte[n];
        new Random(l).NextBytes(payload);

        List<DrmReceivedFile> fixedFiles = await RoundTripAsync(payload, settings, DrmRxOptions.Default);
        List<DrmReceivedFile> qsstvFiles = await RoundTripAsync(payload, settings, DrmRxOptions.Qsstv);

        Assert.Equal(payload, Assert.Single(fixedFiles).Data.ToArray());
        if (l + 11 >= 256)
            Assert.Empty(qsstvFiles);
        else
            Assert.Equal(payload, Assert.Single(qsstvFiles).Data.ToArray());
    }

    /// <summary>With RS1 the wrapped segment is an erasure the RS code may repair, so QSSTV can still complete; recorded,
    /// not asserted. The fixed receiver must complete either way. Sizes avoid the kept QSSTV RS quirk (length mod 224
    /// above 217 saves trailing zeros, RsFileDecoder).</summary>
    [Theory]
    [InlineData(DrmBandwidth.Hz2200)]
    [InlineData(DrmBandwidth.Hz2500)]
    public async Task WithRs1_TheFixedReceiverCompletes(DrmBandwidth bw)
    {
        PublicSettings settings = Settings(bw, DrmInterleaving.LongInterleaver, DrmReedSolomonLevel.Rs1);
        int maxL = MaxL(bw, DrmProtectionLevel.Low);
        int[] sizes = [.. Enumerable.Range(200, 6000)
            .Where(n => n % 224 <= 217)
            .Select(n => (N: n, L: Generator(settings, n).BodySegments[^1].Length - SegmentArrayOverhead))
            .Where(x => x.L >= 245 && x.L <= maxL)
            .DistinctBy(x => x.L)
            .Take(3)
            .Select(x => x.N)];
        // RS bodies are whole 255-byte blocks, so only a few last-segment lengths exist per bandwidth.
        Assert.NotEmpty(sizes);

        foreach (int n in sizes)
        {
            byte[] payload = new byte[n];
            new Random(n).NextBytes(payload);
            List<DrmReceivedFile> fixedFiles = await RoundTripAsync(payload, settings, DrmRxOptions.Default);
            List<DrmReceivedFile> qsstvFiles = await RoundTripAsync(payload, settings, DrmRxOptions.Qsstv);

            output.WriteLine($"RS1 {bw} n={n} L={Generator(settings, n).BodySegments[^1].Length - SegmentArrayOverhead}: QSSTV baseline files={qsstvFiles.Count}");
            Assert.Equal(payload, Assert.Single(fixedFiles).Data.ToArray());
        }
    }
}
