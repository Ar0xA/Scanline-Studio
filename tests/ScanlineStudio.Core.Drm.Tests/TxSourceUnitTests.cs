using ScanlineStudio.Core.Drm.Tables;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P2 pieces the golden cases do not reach: naming quirks, the name cap, empty names,
/// the BSR transport-ID sequence, the QSSTV index mapping and the RS edge cases.</summary>
public class TxSourceUnitTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    // Latin-1 name bytes, format, expected on-air MOT name. Produced by Qt 5.15.13 running
    // MOTSlideShow.cpp:100-101,130's exact conversions (probe built against the harness's Qt).
    private static readonly string[][] QtNameVectors =
    [
        ["636166e9", "txt", "6361663f2e747874"],
        ["c3a9", "txt", "e92e747874"],
        ["c383c2a9", "txt", "c3a92e747874"],
        ["efbbbf6162", "txt", "61622e747874"],
        ["61efbbbf62", "txt", "613f622e747874"],
        ["efb790", "txt", "3f2e747874"],
        ["efbfbe", "txt", "3f2e747874"],
        ["eda080", "txt", "3f3f3f2e747874"],
        ["c080", "txt", "3f3f2e747874"],
        ["f4908080", "txt", "3f3f3f3f2e747874"],
        ["f09f9880", "txt", "3f3f2e747874"],
        ["e282", "txt", "3f3f2e747874"],
        ["e28241", "txt", "3f3f412e747874"],
        ["c3", "txt", "3f2e747874"],
        ["ff", "txt", "3f2e747874"],
        ["80", "txt", "3f2e747874"],
        ["c3a9c3", "txt", "e93f2e747874"],
        ["f0", "txt", "3f2e747874"],
        ["e2828241", "txt", "3f412e747874"],
        ["00616263", "txt", "2e747874"],
        ["616200", "txt", "61622e747874"],
        ["f4", "txt", "3f2e747874"],
        ["f58080", "txt", "3f3f3f2e747874"],
        ["e0a080", "txt", "3f2e747874"],
        ["e08080", "txt", "3f3f3f2e747874"],
        ["ad6142d24c998e925052", "jp2", "3f61423f4c3f3f3f50522e6a7032"],
        ["510b568388bb", "jp2", "510b563f3f3f2e6a7032"],
        ["d452bc43b9de72c3", "jp2", "3f523f433f3f723f2e6a7032"],
        ["caa16c7c", "jp2", "3f6c7c2e6a7032"],
        ["8480c30b55f0e284ef", "jp2", "3f3f3f0b553f3f3f3f2e6a7032"],
        ["b8c94ba24e", "jp2", "3f3f4b3f4e2e6a7032"],
        ["57cbcc4846bbb99b9d", "jp2", "573f3f48463f3f3f3f2e6a7032"],
        ["1775af98", "jp2", "17753f3f2e6a7032"],
        ["815092e34fc5bd58", "jp2", "3f503f3f4f3f582e6a7032"],
        ["1421e74442e3ebc477", "jp2", "14213f44423f3f3f772e6a7032"],
        ["cdc74fccce59d7", "jp2", "3f3f4f3f3f593f2e6a7032"],
        ["486307f4b948e3", "jp2", "4863073f3f483f2e6a7032"],
        ["825055864cb446ac", "jp2", "3f50553f4c3f463f2e6a7032"],
        ["9794ea8352", "jp2", "3f3f3f3f522e6a7032"],
        ["ade1", "jp2", "3f3f2e6a7032"],
        ["f07b", "jp2", "3f7b2e6a7032"],
        ["eb420ced598fa1", "jp2", "3f420c3f593f3f2e6a7032"],
        ["41b4", "jp2", "413f2e6a7032"],
        ["4ca2b9414c", "jp2", "4c3f3f414c2e6a7032"],
        ["e4a5cc6ab347", "jp2", "3f3f3f6a3f472e6a7032"],
        ["b547dc594a59c8cc48", "jp2", "3f473f594a593f3f482e6a7032"],
        ["b03d96aaefcfa8d5", "jp2", "3f3d3f3f3f3f3f2e6a7032"],
        ["52b8d0", "jp2", "523f3f2e6a7032"],
        ["a69884ba88e65e", "jp2", "3f3f3f3f3f3f5e2e6a7032"],
        ["bed3abdce59f", "jp2", "3f3f3f3f3f2e6a7032"],
        ["bcb05a57", "jp2", "3f3f5a572e6a7032"],
        ["dd4dfaad", "jp2", "3f4d3f3f2e6a7032"],
        ["cc4ade575413", "jp2", "3f4a3f5754132e6a7032"],
        ["d0", "jp2", "3f2e6a7032"],
        ["f3dc", "jp2", "3f3f2e6a7032"],
        ["8e8951ca", "jp2", "3f3f513f2e6a7032"],
        ["409eee50", "jp2", "403f3f502e6a7032"],
        ["bea051", "jp2", "3f3f512e6a7032"],
        ["38", "jp2", "382e6a7032"],
        ["a041c96a", "jp2", "3f413f6a2e6a7032"],
        ["348a8c5ac0f3846e56de", "jp2", "343f3f5a3f3f3f6e563f2e6a7032"],
        ["c2b65042", "jp2", "b650422e6a7032"],
        ["aa4b4c49b0", "jp2", "3f4b4c493f2e6a7032"],
        ["11ef", "jp2", "113f2e6a7032"],
        ["5744568eafc6b5", "jp2", "5744563f3f3f2e6a7032"],
    ];

    [Fact]
    public void MotName_MatchesQtConversionChain()
    {
        foreach (string[] v in QtNameVectors)
        {
            string name = DrmLatin1.Decode(Convert.FromHexString(v[0]));
            Assert.Equal(v[2], Convert.ToHexString(DrmMotName.Build(name, v[1])).ToLowerInvariant());
        }
    }

    [Fact]
    public void MotName_EmojiAndNonLatin1BecomeQuestionMarks()
    {
        // toLatin1 turns each UTF-16 unit above 0xFF into '?', so an emoji (surrogate pair) is "??".
        Assert.Equal("20260101120000-a??b.txt"u8.ToArray(), DrmMotName.Build("20260101120000-a\U0001F600b", "txt"));
        Assert.Equal("x?.rs1"u8.ToArray(), DrmMotName.Build("x€", "rs1"));
        Assert.Equal("caf?.txt"u8.ToArray(), DrmMotName.Build("café", "txt"));
    }

    [Fact]
    public void MotName_NulTruncatesNameAndFormat()
    {
        Assert.Equal("ab.txt"u8.ToArray(), DrmMotName.Build("ab\0cd", "txt"));
        Assert.Equal("ab.t"u8.ToArray(), DrmMotName.Build("ab", "t\0xt"));
        Assert.Equal("PA0"u8.ToArray(), DrmLatin1.EncodeNulTerminated("PA0\0TST"));
    }

    [Fact]
    public void Naming_StampIsReusedUntilForgotten()
    {
        var clock = new FixedTimeProvider(Stamp);
        var naming = new DrmTxNaming(clock);
        Assert.Equal("20260101120000-a.txt", naming.GetTxFileName("a.txt", binary: true).FileName);
        clock.Now = Stamp.AddMinutes(5);
        Assert.Equal("20260101120000-b.png", naming.GetTxFileName("dir/b.png", binary: true).FileName);
        naming.ForgetTxFileName();
        Assert.Equal("20260101120500-b.png", naming.GetTxFileName("b.png", binary: true).FileName);
    }

    [Fact]
    public void Naming_ImagePathRenamesToJp2_AndBaseNameCutsAtFirstDot()
    {
        var naming = new DrmTxNaming(new FixedTimeProvider(Stamp));
        DrmTxFileName image = naming.GetTxFileName("my.photo.png", binary: false);
        Assert.Equal("20260101120000-my.photo.jp2", image.FileName);
        Assert.Equal("20260101120000-my", image.BaseName);
        Assert.Equal("jp2", image.Suffix);

        DrmTxObject obj = DrmTxObject.ForFile([1, 2, 3], image, DrmReedSolomon.None);
        Assert.Equal("20260101120000-my.jp2"u8.ToArray(), obj.MotName.ToArray());
        Assert.Equal(string.Empty, new DrmTxFileName("20260101120000-noext").Suffix);
    }

    [Fact]
    public void Mot_NameIsCappedAt80BytesInHeaderAndHash()
    {
        byte[] name = Enumerable.Range(0, 100).Select(i => (byte)('a' + (i % 26))).ToArray();
        var mot = new MotEncoder(new BsrTransportIdCounter());
        Assert.True(mot.SetMotObject(name, new byte[10], 96));

        byte[] header = mot.HeaderSegments.Single();
        Assert.Equal(2 + 17 + MotEncoder.MaxNameLength, header.Length);
        Assert.True(name.AsSpan(0, 80).SequenceEqual(header.AsSpan(header.Length - 80)));
        Assert.Equal(1 + 80, header[2 + 15] & 0x7F);
        Assert.Equal(MotEncoder.TransportIdHash(name.AsSpan(0, 80)), mot.TransportId);
        Assert.NotEqual(MotEncoder.TransportIdHash(name), mot.TransportId);
    }

    [Fact]
    public void Mot_EmptyNameGivesNoMotObject()
    {
        var mot = new MotEncoder(new BsrTransportIdCounter());
        Assert.False(mot.SetMotObject([], new byte[50], 96));
        Assert.Empty(mot.HeaderSegments);
        Assert.Empty(mot.BodySegments);
        Assert.Equal(0, mot.TransportId);
    }

    [Fact]
    public void Mot_SmallHashGetsNameLengthAdded()
    {
        // {0x00, 0x01}: add = (0 + 1) ^ 1 = 0, xor = 1 → ID 1, which is <= 2, so the length (2) is added (DABMOT.cpp:123).
        Assert.Equal(3, MotEncoder.TransportIdHash([0x00, 0x01]));
        Assert.Equal((256 * 0x61) + 0x61, MotEncoder.TransportIdHash("a"u8));
    }

    [Fact]
    public void Bsr_TransportIdContinuesAcrossTwoSends()
    {
        var counter = new BsrTransportIdCounter();
        DrmTxSettings settings = new QsstvDrmTxParams(1, 1, 0, 0, 0, 0).ToSettings(string.Empty);
        byte[] payload = "1\nH_OK\n100\n2\n5\n-99\n"u8.ToArray();

        var first = new DrmSourceFrameGenerator(DrmTxObject.ForBsr(payload), settings, counter);
        List<DrmSourceFrame> f1 = first.Frames().ToList();
        Assert.Equal(0, first.InitialTransportId);
        Assert.Equal(1, first.CurrentTransportId);
        Assert.Equal(0, PacketTransportId(f1[0]));
        Assert.Equal(1, PacketTransportId(f1[^2]));

        var second = new DrmSourceFrameGenerator(DrmTxObject.ForBsr(payload), settings, counter);
        List<DrmSourceFrame> f2 = second.Frames().ToList();
        Assert.Equal(2, second.InitialTransportId);
        Assert.Equal(0, second.CurrentTransportId);
        Assert.Equal(2, PacketTransportId(f2[0]));
        Assert.Equal(0, PacketTransportId(f2[^2]));
    }

    [Fact]
    public void Eot_SendsFiveExtraGroupsFromScheduleStart()
    {
        var naming = new DrmTxNaming(new FixedTimeProvider(Stamp));
        DrmTxObject obj = DrmTxObject.ForFile(new byte[300], naming.GetTxFileName("t.bin", binary: true), DrmReedSolomon.None);
        DrmTxSettings settings = new QsstvDrmTxParams(1, 1, 0, 0, 0, 0).ToSettings("PA0TST");
        var gen = new DrmSourceFrameGenerator(obj, settings, new BsrTransportIdCounter());
        List<DrmSourceFrame> frames = gen.Frames().ToList();

        // One group per packet per frame here: schedule + 5 extra blocks.
        Assert.Equal(gen.SegmentSchedule.Count + MotSlideShowEncoder.ExtraBlocks, frames.Count);
        for (int e = 0; e < MotSlideShowEncoder.ExtraBlocks; e++)
            Assert.Equal(GroupSegmentField(frames[e]), GroupSegmentField(frames[gen.SegmentSchedule.Count + e]));
        Assert.Null(frames[^1].FacBits);
        Assert.All(frames.SkipLast(1), f => Assert.NotNull(f.FacBits));
    }

    [Fact]
    public void FixBranch_NeverReinsertsHeaders()
    {
        int[] list = MotEncoder.PrepareSegmentList(1, 3, [0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1, 2, 0, 1]);
        int runIn = 24 + 6;
        int[] fixPart = list[runIn..(runIn + 32)];
        Assert.DoesNotContain(-1, fixPart);
    }

    [Fact]
    public void QsstvIndexMapping_FollowsDrmTransmitterInit()
    {
        DrmTxSettings s = new QsstvDrmTxParams(0, 0, 0, 0, 0, 0).ToSettings("X");
        Assert.Equal((RobustnessMode.A, SpectrumOccupancy.So0, DrmQam.Qam4, DrmProtection.High, DrmInterleave.Long, DrmReedSolomon.None),
            (s.Mode, s.Occupancy, s.Qam, s.Protection, s.Interleave, s.ReedSolomon));

        s = new QsstvDrmTxParams(7, 3, 9, 1, 5, 4).ToSettings("X");
        Assert.Equal((RobustnessMode.E, SpectrumOccupancy.So1, DrmQam.Qam64, DrmProtection.Low, DrmInterleave.Short, DrmReedSolomon.Rs4),
            (s.Mode, s.Occupancy, s.Qam, s.Protection, s.Interleave, s.ReedSolomon));

        s = new QsstvDrmTxParams(1, 1, 1, 2, 1, 2).ToSettings("X");
        Assert.Equal((RobustnessMode.B, DrmQam.Qam16, DrmProtection.High, DrmReedSolomon.Rs2), (s.Mode, s.Qam, s.Protection, s.ReedSolomon));
        Assert.Equal(new QsstvDrmTxParams(1, 1, 1, 0, 1, 2), QsstvDrmTxParams.FromSettings(s));
    }

    [Fact]
    public void Fac_LabelIsSevenBitsThreePerFrame_AndUnwrittenBitsAlternate()
    {
        DrmTxSettings settings = new QsstvDrmTxParams(1, 1, 0, 0, 0, 0).ToSettings("ABCDEFGHIJKL");
        var fac = new FacEncoder(settings, [(byte)'A', (byte)'B', 0xC3, (byte)'D', (byte)'E', (byte)'F', (byte)'G', (byte)'H', (byte)'I', (byte)'J'], 0);
        byte[] f0 = fac.Next(0);
        byte[] f1 = fac.Next(1);
        byte[] f2 = fac.Next(2);
        Assert.Equal(0xC3 & 127, Bits(f0, 24, 7));
        Assert.Equal('I', Bits(f2, 24, 7));
        Assert.Equal('D', Bits(f1, 10, 7));
        // Bits 31-39 are never written; the in-place FAC dispersal leaves the PRBS there every other frame.
        Assert.Equal((0, 0b000010010, 0), (Bits(f0, 31, 9), Bits(f1, 31, 9), Bits(f2, 31, 9)));
    }

    [Fact]
    public void RsFile_HeaderAndShortExtension()
    {
        byte[] payload = Enumerable.Range(0, 500).Select(i => (byte)i).ToArray();
        byte[] coded = RsFileEncoder.Encode(payload, "tx", DrmReedSolomon.Rs1);
        var ec = new byte[coded.Length];
        int rows = coded.Length / KarnReedSolomon.BlockSize;
        RsFileEncoder.Distribute(coded, ec, rows, KarnReedSolomon.BlockSize, reverse: true);

        // 507 bytes over k = 224 → 3 blocks; surplus 224 - 500 % 224 = 172.
        Assert.Equal(3, rows);
        Assert.Equal(new byte[] { 172, 3, 0, (byte)'t', (byte)'x', 0, 0 }, ec[..7]);
        Assert.Equal(payload[..217], ec[7..224]);
        Assert.Equal(payload[217..441], ec[255..479]);
    }

    [Fact]
    public void RsFile_TinyPayloadIsZeroPaddedToOneBlock()
    {
        byte[] coded = RsFileEncoder.Encode([1, 2, 3], "jp2", DrmReedSolomon.Rs4);
        Assert.Equal(KarnReedSolomon.BlockSize, coded.Length);
        Assert.Equal(new byte[] { 125, 1, 0, (byte)'j', (byte)'p', (byte)'2', 0, 1, 2, 3, 0 }, coded[..11]);
    }

    [Fact]
    public void RsFile_HeaderPushesTailIntoExtraChunk()
    {
        // got % k = 220 > k - 7 = 217: the 7 header bytes push the last 3 payload bytes into a second block.
        byte[] payload = Enumerable.Range(0, 220).Select(i => (byte)(i + 1)).ToArray();
        byte[] coded = RsFileEncoder.Encode(payload, "txt", DrmReedSolomon.Rs1);
        Assert.Equal(2 * KarnReedSolomon.BlockSize, coded.Length);

        var ec = new byte[coded.Length];
        RsFileEncoder.Distribute(coded, ec, 2, KarnReedSolomon.BlockSize, reverse: true);
        Assert.Equal(new byte[] { 4, 2, 0, (byte)'t', (byte)'x', (byte)'t', 0 }, ec[..7]);
        Assert.True(payload.AsSpan(0, 217).SequenceEqual(ec.AsSpan(7, 217)));

        var block1 = new byte[224];
        payload.AsSpan(217, 3).CopyTo(block1);
        Assert.True(block1.AsSpan().SequenceEqual(ec.AsSpan(255, 224)));
        var parity = new byte[31];
        new KarnReedSolomon(224).Encode(block1, parity);
        Assert.True(parity.AsSpan().SequenceEqual(ec.AsSpan(255 + 224, 31)));
    }

    [Fact]
    public void Packet_UsefulByteCountWrapsAt256()
    {
        byte[] unit = Enumerable.Range(0, 280).Select(i => (byte)(i * 7)).ToArray();
        var enc = new DataPacketEncoder(300, 0, () => unit);
        enc.Init();
        byte[] packet = enc.GeneratePacket();

        Assert.Equal(303, packet.Length);
        Assert.Equal(0xC8, packet[0]);
        Assert.Equal(280 & 0xFF, packet[1]);
        Assert.True(unit.AsSpan().SequenceEqual(packet.AsSpan(2, 280)));
        var crc = new DrmCrc();
        crc.Reset(16);
        for (int i = 0; i <= 300; i++)
            crc.AddByte(packet[i]);
        uint value = crc.GetCrc();
        Assert.Equal(new byte[] { (byte)(value >> 8), (byte)value }, packet[301..]);
    }

    [Fact]
    public void Schedule_HeadersEvery50BodySegments()
    {
        // DABMOT.cpp:560-632 with 1 header and 120 body segments: run-in 25, body 3 * 2 + 120, run-out 11.
        int[] expected =
        [
            -1, 119, .. Enumerable.Range(0, 23),
            -1, 119, .. Enumerable.Range(0, 50),
            -1, 119, .. Enumerable.Range(50, 50),
            -1, 119, .. Enumerable.Range(100, 20),
            -1, 119, .. Enumerable.Range(0, 9),
        ];
        int[] list = MotEncoder.PrepareSegmentList(1, 120, []);
        Assert.Equal(162, list.Length);
        Assert.Equal(expected, list);
    }

    [Fact]
    public void Naming_StripsDirectoryOfThePlatform()
    {
        var naming = new DrmTxNaming(new FixedTimeProvider(Stamp));
        Assert.Equal("20260101120000-b.png", naming.GetTxFileName(Path.Combine("some", "dir", "b.png"), binary: true).FileName);
        if (OperatingSystem.IsWindows())
            Assert.Equal("b.png", DrmTxFileName.StripPath(@"C:\pics\b.png"));
    }

    [Fact]
    public void KarnRs_CodewordHasZeroSyndromes()
    {
        var rs = new KarnReedSolomon(192);
        byte[] data = Enumerable.Range(0, 192).Select(i => (byte)((i * 37) + 11)).ToArray();
        var parity = new byte[63];
        rs.Encode(data, parity);

        // Karn's code word is data then parity, highest power first in index order (encode_rs loops KK-1..0).
        byte[] cw = [.. data, .. parity];
        for (int root = 1; root <= 63; root++)
            Assert.Equal(0, EvaluateAtAlphaPower(cw, root));
    }

    private static int EvaluateAtAlphaPower(byte[] cw, int power)
    {
        // c(x) = sum cw[i] x^(NN-KK+i) for data and sum parity[j] x^j: index i of cw maps to x^(i - 192 + 255 - 192 ...).
        // Build exponents: parity bb[j] is x^j, data[i] is x^(63 + i).
        int acc = 0;
        int x = GfPow(power);
        for (int i = 0; i < cw.Length; i++)
        {
            int exp = i < 192 ? 63 + i : i - 192;
            acc ^= GfMul(cw[i], GfPowOf(x, exp));
        }

        return acc;
    }

    private static int GfMul(int a, int b)
    {
        int r = 0;
        while (b != 0)
        {
            if ((b & 1) != 0)
                r ^= a;
            a <<= 1;
            if ((a & 0x100) != 0)
                a ^= 0x11D;
            b >>= 1;
        }

        return r;
    }

    private static int GfPow(int power) => GfPowOf(2, power);

    private static int GfPowOf(int x, int exp)
    {
        int r = 1;
        for (int i = 0; i < exp; i++)
            r = GfMul(r, x);
        return r;
    }

    private static int Bits(byte[] bits, int start, int count)
    {
        int v = 0;
        for (int i = 0; i < count; i++)
            v = (v << 1) | bits[start + i];
        return v;
    }

    private static int PacketTransportId(DrmSourceFrame frame)
    {
        byte[] p = frame.Packets[0];
        int groupStart = (p[0] & 0x08) != 0 ? 2 : 1;
        return (p[groupStart + 5] << 8) | p[groupStart + 6];
    }

    private static int GroupSegmentField(DrmSourceFrame frame)
    {
        byte[] p = frame.Packets[0];
        int groupStart = (p[0] & 0x08) != 0 ? 2 : 1;
        return (p[groupStart] << 24) | (p[groupStart + 2] << 8) | p[groupStart + 3];
    }
}
