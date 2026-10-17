using DrmReceivedFile = ScanlineStudio.Abstractions.Drm.DrmReceivedFile;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Tables;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P7a part 1 unit and robustness tests: the RS erasure decoder, the RS file decoder against our verified TX
/// encoder, hostile and corrupted MSC input, the PPI wrap with our own (wire-identical) TX, the name sanitizer.</summary>
public sealed class DrmSourceDecoderUnitTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int PacketLength = 75;
    private const int Bits = 627;

    // ---- Reed-Solomon ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(224)]
    [InlineData(192)]
    [InlineData(160)]
    [InlineData(128)]
    public void DecodeErasures_CorrectsErasuresAndErrorsUpToTheBound(int k)
    {
        var rs = new KarnReedSolomon(k);
        int nk = 255 - k;
        var rng = new Random(k);
        for (int trial = 0; trial < 40; trial++)
        {
            var word = new byte[255];
            rng.NextBytes(word.AsSpan(0, k));
            rs.Encode(word.AsSpan(0, k), word.AsSpan(k));
            byte[] clean = (byte[])word.Clone();

            int erasures = rng.Next(0, nk + 1);
            int errors = (nk - erasures) / 2;
            int[] pos = Enumerable.Range(0, 255).OrderBy(_ => rng.Next()).Take(erasures + errors).ToArray();
            var eras = new int[255];
            for (int i = 0; i < erasures; i++)
            {
                eras[i] = pos[i];
                word[pos[i]] = 0;
            }

            for (int i = erasures; i < pos.Length; i++)
                word[pos[i]] ^= (byte)rng.Next(1, 256);

            int n = rs.DecodeErasures(word, eras, erasures);
            Assert.NotEqual(-1, n);
            Assert.Equal(clean, word);
        }

        // One erasure past NN-KK is refused by the caller's bound; one error past t is not silently "corrected" here.
        var w = new byte[255];
        rs.Encode(w.AsSpan(0, k), w.AsSpan(k));
        Assert.Equal(0, rs.DecodeErasures(w, new int[255], 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rs.DecodeErasures(w, new int[255], nk + 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RsFileDecoder_InvertsOurEncoder_WithMissingSegments(int level)
    {
        var rsType = Enum.Parse<DrmReedSolomon>("Rs" + level);
        int k = RsFileEncoder.DataSize(rsType);
        var rng = new Random(level);
        foreach (int size in new[] { 1, 10, 100, k - 8, k - 7, k - 6, k, 300, 1000, 5000 })
        {
            var payload = new byte[size];
            rng.NextBytes(payload);
            byte[] coded = RsFileEncoder.Encode(payload, "jpg", rsType);
            const int segLen = 64;
            int segments = (coded.Length + segLen - 1) / segLen;
            int bep = coded.Length / 255;

            // Drop as many whole segments as the code can take: each spreads over every code word.
            int perSegment = (segLen + bep - 1) / bep;
            int drop = Math.Min((255 - k) / perSegment, segments - 1);
            int[] missing = Enumerable.Range(0, segments - 1).OrderBy(_ => rng.Next()).Take(drop).Order().ToArray();
            byte[] body = (byte[])coded.Clone();
            foreach (int m in missing)
                body.AsSpan(m * segLen, segLen).Clear();
            var erasures = new List<int> { segments, segLen };
            erasures.AddRange(missing);

            Assert.True(RsFileDecoder.TryDecode(body, "a.b/x.rs" + level, "rs" + level, erasures, false, out RsDecodeResult? r));
            Assert.Equal("x.jpg", r!.QsstvFileName);
            int surplus = k - (size % k);
            if (size % k > k - 7)
            {
                // QSSTV quirk kept: the size byte cannot describe the padding; the file keeps trailing zeros.
                Assert.Equal(payload, r.File[..size]);
                Assert.True(r.File.Length > size);
            }
            else
            {
                Assert.Equal(payload, r.File);
            }

            Assert.True(RsFileDecoder.TryDecode(body, "x.rs" + level, "rs" + level, erasures, true, out RsDecodeResult? legacy));
            if (surplus >= 128 && size % k <= k - 7)
                Assert.True(legacy!.File.Length > size, $"x86 signed size byte, size {size}");
            else
                Assert.Equal(r.File, legacy!.File);
        }
    }

    /// <summary>Cost of one RS4 decode attempt with a third of the segments missing (~85 erasures per code word, the
    /// worst case the 63 % rule lets through is ~94), at 100 kB, 1 MB and the 4 MiB body cap. Reported, not gated.</summary>
    [Theory]
    [InlineData(100_000)]
    [InlineData(1_000_000)]
    public void RsFileDecoder_DecodeCost(int bodyBytes)
    {
        int payloadSize = (bodyBytes / 255 * 128) - 7;
        var payload = new byte[payloadSize];
        new Random(1).NextBytes(payload);
        byte[] coded = RsFileEncoder.Encode(payload, "jp2", DrmReedSolomon.Rs4);
        const int segLen = 299;
        int segments = (coded.Length + segLen - 1) / segLen;
        var erasures = new List<int> { segments, segLen };
        byte[] body = (byte[])coded.Clone();
        for (int sgm = 0; sgm < segments - 1; sgm += 3)
        {
            erasures.Add(sgm);
            body.AsSpan(sgm * segLen, segLen).Clear();
        }

        Assert.True(RsFileDecoder.TryDecode(body, "x.rs4", "rs4", erasures, false, out _));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(RsFileDecoder.TryDecode(body, "x.rs4", "rs4", erasures, false, out RsDecodeResult? r));
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Assert.Equal(payload, r!.File);
        output.WriteLine($"RS4 body {coded.Length} bytes, {erasures.Count - 2}/{segments} segments missing: {ms:F1} ms per attempt");
    }

    [Fact]
    public void RsFileDecoder_RefusesTooManyErasuresAndBadHeaders()
    {
        byte[] payload = new byte[300];
        new Random(5).NextBytes(payload);
        byte[] coded = RsFileEncoder.Encode(payload, "txt", DrmReedSolomon.Rs1);
        int segments = (coded.Length + 63) / 64;

        // 64 erased bytes over 2 code words = 32 per word > 31 parity bytes.
        Assert.False(RsFileDecoder.TryDecode(coded, "x.rs1", "rs1", [segments, 64, 3], false, out _));
        Assert.False(RsFileDecoder.TryDecode(coded, "x.rs1", "txt", [segments, 64], false, out _));
        Assert.False(RsFileDecoder.TryDecode([], "x.rs1", "rs1", [0, 64], false, out _));

        // A corrupted chunk count in the RS header is rejected, as QSSTV does (reedsolomoncoder.cpp:151-157).
        byte[] block = new byte[255];
        var rs = new KarnReedSolomon(224);
        block[0] = 10;
        block[1] = 7;
        rs.Encode(block.AsSpan(0, 224), block.AsSpan(224));
        Assert.False(RsFileDecoder.TryDecode(block, "x.rs1", "rs1", [4, 64], false, out _));
    }

    /// <summary>Random segment losses at every level: when the early check says no, the full decode fails too.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void ErasuresDecodable_FalseImpliesTryDecodeFails(int level)
    {
        var rsType = Enum.Parse<DrmReedSolomon>("Rs" + level);
        var rng = new Random(100 + level);
        int agreeFalse = 0, passes = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            byte[] payload = new byte[rng.Next(1, 6000)];
            rng.NextBytes(payload);
            byte[] coded = RsFileEncoder.Encode(payload, "jpg", rsType);
            int segLen = rng.Next(16, 300);
            int segments = (coded.Length + segLen - 1) / segLen;
            double loss = rng.NextDouble() * 0.6;
            var erasures = new List<int> { segments, segLen };
            byte[] body = (byte[])coded.Clone();
            for (int sgm = 0; sgm < segments - 1; sgm++)
            {
                if (rng.NextDouble() < loss)
                {
                    erasures.Add(sgm);
                    body.AsSpan(sgm * segLen, segLen).Clear();
                }
            }

            bool early = RsFileDecoder.ErasuresDecodable(body.Length, level, erasures);
            bool full = RsFileDecoder.TryDecode(body, "x.rs" + level, "rs" + level, erasures, false, out _);
            Assert.False(!early && full, $"trial {trial}: early check rejected a decodable body");
            if (!early)
                agreeFalse++;
            else
                passes++;
        }

        Assert.True(agreeFalse > 0 && passes > 0, $"both outcomes exercised ({agreeFalse} rejected, {passes} passed)");
    }

    /// <summary>The not-yet-sent tail erases the high-index words first, so word 0 passes while a later word fails;
    /// the early check must reject that body, not only check word 0.</summary>
    [Fact]
    public void ErasuresDecodable_RejectsTailLossThatWordZeroSurvives()
    {
        byte[] payload = new byte[20_000];
        new Random(9).NextBytes(payload);
        byte[] coded = RsFileEncoder.Encode(payload, "jp2", DrmReedSolomon.Rs1);
        const int segLen = 200;
        int segments = (coded.Length + segLen - 1) / segLen;
        int bep = coded.Length / 255;
        int parity = 255 - RsFileEncoder.DataSize(DrmReedSolomon.Rs1);

        // Grow the missing tail (the last segment is received) until some word passes parity; word 0 must not.
        int[] Counts(int first)
        {
            var c = new int[bep];
            for (long p = (long)first * segLen; p < Math.Min((long)(segments - 1) * segLen, 255L * bep); p++)
                c[p % bep]++;
            return c;
        }

        int firstMissing = segments - 2;
        while (Counts(firstMissing).Max() <= parity)
            firstMissing--;
        int[] counts = Counts(firstMissing);
        Assert.True(counts[0] <= parity, $"word 0 has {counts[0]} erasures, parity {parity}");
        Assert.True(counts.Max() > parity);

        var erasures = new List<int> { segments, segLen };
        byte[] body = (byte[])coded.Clone();
        for (int sgm = firstMissing; sgm < segments - 1; sgm++)
        {
            erasures.Add(sgm);
            body.AsSpan(sgm * segLen, segLen).Clear();
        }

        Assert.False(RsFileDecoder.TryDecode(body, "x.rs1", "rs1", erasures, false, out _));
        Assert.False(RsFileDecoder.ErasuresDecodable(body.Length, 1, erasures));
    }

    /// <summary>Worst-case attempt cost per RS level at our TX maximum (100 kB): the most erasures each code can still
    /// correct, plus the early check alone on a body one segment past that. Reported, not gated.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RsFileDecoder_WorstCaseCostPerLevel(int level)
    {
        var rsType = Enum.Parse<DrmReedSolomon>("Rs" + level);
        int k = RsFileEncoder.DataSize(rsType);
        int payloadSize = (100_000 / 255 * k) - 7;
        var payload = new byte[payloadSize];
        new Random(level).NextBytes(payload);
        byte[] coded = RsFileEncoder.Encode(payload, "jp2", rsType);
        int bep = coded.Length / 255;
        int parity = 255 - k;
        int segLen = bep;
        int segments = (coded.Length + segLen - 1) / segLen;

        // A segment of exactly bep bytes erases one symbol in every word; take parity of them, spread out.
        var erasures = new List<int> { segments, segLen };
        byte[] body = (byte[])coded.Clone();
        int step = (segments - 1) / parity;
        for (int i = 0; i < parity; i++)
        {
            erasures.Add(i * step);
            body.AsSpan(i * step * segLen, segLen).Clear();
        }

        Assert.True(RsFileDecoder.TryDecode(body, "x.rs" + level, "rs" + level, erasures, false, out _));
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.True(RsFileDecoder.ErasuresDecodable(body.Length, level, erasures));
        Assert.True(RsFileDecoder.TryDecode(body, "x.rs" + level, "rs" + level, erasures, false, out RsDecodeResult? r));
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        Assert.Equal(payload, r!.File);

        erasures.Add(1);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.False(RsFileDecoder.ErasuresDecodable(body.Length, level, erasures));
        double checkMs = System.Diagnostics.Stopwatch.GetElapsedTime(t1).TotalMilliseconds;
        output.WriteLine($"RS{level} body {coded.Length} bytes, {parity} erasures per word: attempt {ms:F1} ms; early rejection {checkMs:F2} ms");
    }

    // ---- robustness -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(23)]
    public void ShortPackets_AreInvalid(int bits)
    {
        var d = new DrmSourceDecoder();
        var f = new FakeMscFrame();
        f.Load([0xC0, 0, 0], bits);
        d.Decode(f, 1);
        Assert.Equal(DrmMscStatus.Invalid, d.MscValid);
        Assert.Empty(d.TransportBlocks);
    }

    [Fact]
    public void OversizePacket_IsDroppedWithoutOverflow()
    {
        var (d, f) = DecoderAfterValidHeader();
        byte[] p = new byte[600];
        p[0] = 0xC0;
        f.Load(WithCrc(p), 602 * 8);
        d.Decode(f, 1);
        Assert.Equal(DrmMscStatus.Invalid, d.MscValid);
    }

    /// <summary>A 2-byte packet whose CRC16 passes (the CRC of no data is 0x0000), continuing a packet with index 7:
    /// QSSTV's <c>len-3</c> wraps (sourcedecoder.cpp:140); here it is dropped as invalid.</summary>
    [Fact]
    public void TwoBytePacketWithValidCrc_IsInvalid()
    {
        var (d, f) = DecoderAfterValidHeader();
        byte[] p = WithCrc([]);
        Assert.Equal(new byte[] { 0, 0 }, p);
        f.Load(p, 16);
        d.Decode(f, 1);
        Assert.Equal(DrmMscStatus.Invalid, d.MscValid);
        Assert.Single(d.TransportBlocks);
    }

    /// <summary>Burst corruption of 2-12 bytes in 30 % of the packets; half of those get a recomputed packet CRC, as an
    /// escaped packet-CRC failure would look, so only the data-group CRC protects them. A file is the exact payload or absent.</summary>
    [Fact]
    public void CorruptedPackets_IncludingPacketCrcEscapes_NeverGiveAWrongFile()
    {
        byte[] payload = Text(600);
        List<byte[]> clean = Packetize(MotObject(0x1234, "t.txt", payload, 60, rounds: 4), PacketLength);
        var rng = new Random(11);
        int received = 0, absent = 0, escapes = 0;
        for (int trial = 0; trial < 200; trial++)
        {
            var (d, files) = NewDecoder();
            var f = new FakeMscFrame();
            foreach (byte[] p in clean)
            {
                byte[] q = (byte[])p.Clone();
                if (rng.NextDouble() < 0.3)
                {
                    int len = rng.Next(2, 13);
                    int at = rng.Next(q.Length - len + 1);
                    rng.NextBytes(q.AsSpan(at, len));
                    if (rng.Next(2) == 0)
                    {
                        q = WithCrc(q[..^2]);
                        escapes++;
                    }
                }

                f.Load(q, Bits);
                d.Decode(f, 1);
            }

            foreach (DrmReceivedFile file in files)
            {
                Assert.Equal(payload, file.Data.ToArray());
                received++;
            }

            if (files.Count == 0)
                absent++;
        }

        Assert.True(escapes > 500, $"{escapes} packet-CRC escapes");
        Assert.True(received > 0 && absent > 0, $"received {received}, absent {absent}");
    }

    [Fact]
    public void CrcValidGarbage_NeverThrows_AndStaysBounded()
    {
        var rng = new Random(3);
        var (d, files) = NewDecoder();
        var f = new FakeMscFrame();
        for (int i = 0; i < 20000; i++)
        {
            int len = rng.Next(3, 80);
            var body = new byte[len - 2];
            rng.NextBytes(body);
            if (rng.Next(4) == 0)
                body[0] = (byte)((body[0] & 0x37) | 0xC0); // first and last: a whole data group per packet
            if (rng.Next(3) == 0 && len > 8)
            {
                // A CRC-valid data group inside, so the MOT parsing runs on random fields.
                byte[] group = WithCrc(RandomBytes(rng, rng.Next(1, len - 4)));
                group[0] = (byte)((group[0] & 0xB8) | 0x40 | (rng.Next(2) == 0 ? 3 : 4));
                group = WithCrc(group[..^2]);
                body = new byte[] { (byte)(0xC8 | rng.Next(8)), (byte)group.Length }.Concat(group).ToArray();
                body = body.Concat(new byte[Math.Max(0, len - 2 - body.Length)]).ToArray();
            }

            f.Load(WithCrc(body), (body.Length + 2) * 8 + rng.Next(8));
            d.Decode(f, rng.Next(3));
        }

        Assert.True(d.TransportBlocks.Count <= DrmSourceDecoder.MaxTransportLists);
        Assert.All(files, x => Assert.True(x.Data.Length <= DrmSourceDecoder.MaxBodyBytes));
    }

    /// <summary>A continuation chain past <see cref="DrmSourceDecoder.MaxDataGroupBytes"/> is dropped and continuity reset,
    /// so the chain's last packet, which would otherwise complete a (header-valid, CRC-less) data group, is rejected.</summary>
    [Fact]
    public void ContinuationChain_IsCappedAtTheDataGroupBound()
    {
        var (d, _) = NewDecoder();
        var f = new FakeMscFrame();
        const int packets = 400;
        for (int i = 0; i <= packets; i++)
        {
            var p = new byte[PacketLength + 1];
            p[0] = (byte)((i == 0 ? 0x80 : 0) | (i == packets ? 0x40 : 0) | (i & 7));
            if (i == 0)
                new byte[] { 0x34, 0x00, 0x00, 0x00, 0x12, 0x00, 0x09, 0x00, 0x10 }.CopyTo(p, 1); // MOT body group, no CRC flag
            f.Load(WithCrc(p), Bits);
            d.Decode(f, 1);
            Assert.Equal(DrmMscStatus.Invalid, d.MscValid);
        }

        Assert.True(packets * PacketLength > DrmSourceDecoder.MaxDataGroupBytes);
        Assert.Empty(d.TransportBlocks);
    }

    [Fact]
    public void HugeSegmentNumberAndPpiPastThePacket_DoNotThrow()
    {
        var (d, files) = NewDecoder();
        var f = new FakeMscFrame();
        var mot = new MotBuilder();
        byte[] g = mot.Group(4, 0x7FFF, false, 9, [0, 4, 1, 2, 3, 4]);
        foreach (byte[] p in Packetize([g], PacketLength))
        {
            f.Load(p, Bits);
            d.Decode(f, 1);
        }

        Assert.Equal(0x8000, d.TransportBlocks.Single().Segments.Count);
        var bad = new byte[PacketLength + 1];
        bad[0] = 0xC8;
        bad[1] = 255;
        f.Load(WithCrc(bad), Bits);
        d.Decode(f, 1);
        Assert.Empty(files);
    }

    [Fact]
    public void Init_KeepsTransportBlocks_ResetsContinuity()
    {
        var (d, files) = NewDecoder();
        var f = new FakeMscFrame();
        List<byte[]> packets = Packetize(MotObject(0x55, "k.txt", Text(200), 60, rounds: 1), PacketLength);
        for (int i = 0; i < packets.Count - 1; i++)
        {
            f.Load(packets[i], Bits);
            d.Decode(f, 1);
        }

        d.Init();
        Assert.Single(d.TransportBlocks);
        f.Load(packets[^1], Bits);
        d.Decode(f, 1);
        Assert.Equal("k.txt", Assert.Single(files).FileName);
    }

    [Fact]
    public void FrameStageReset_InitsTheSink()
    {
        var sink = new CountingSink();
        var stage = new DrmRxFrameStage(new DrmChannelEstimator(), new DrmChannelDecoder(), sink);
        _ = new DrmDemodulator(stage);
        Assert.Equal(1, sink.Inits);
    }

    /// <summary>The PPI answer: QSSTV RX trusts the 8-bit count. With 310-byte packets (mode A, SO 1, 64-QAM, low),
    /// a file whose last data group is 256..309 bytes never completes, also with our wire-identical TX.</summary>
    [Theory]
    [InlineData(299 + 200, true)]
    [InlineData(299 + 260, false)]
    [InlineData(299 + 244, true)]
    [InlineData(299 + 245, false)]
    [InlineData(299 + 298, false)]
    public void PpiCountWrap_LosesTheLastSegment(int size, bool received)
    {
        var settings = new DrmTxSettings(RobustnessMode.A, SpectrumOccupancy.So1, DrmQam.Qam64, DrmProtection.Low, DrmInterleave.Long, DrmReedSolomon.None, "PA0TST");
        byte[] payload = Text(size);
        var gen = new DrmSourceFrameGenerator(DrmTxObject.ForFile(payload, new DrmTxFileName("p.txt"), DrmReedSolomon.None), settings, new BsrTransportIdCounter());
        Assert.Equal(310, gen.PacketLength);
        var (d, files) = NewDecoder();
        var f = new FakeMscFrame { MscModeNew = 0, PlPartB = 1 };
        foreach (DrmSourceFrame frame in gen.Frames())
        {
            foreach (byte[] p in frame.Packets)
            {
                f.Load(p, gen.NumDecodedBitsMsc);
                d.Decode(f, 0);
            }
        }

        if (received)
            Assert.Equal(payload, Assert.Single(files).Data.ToArray());
        else
            Assert.Empty(files);
    }

    [Fact]
    public void TextMode_FollowsQsstvRule()
    {
        Assert.True(DrmSourceDecoder.IsQsstvTextMode("a.txt", "hello"u8));
        Assert.True(DrmSourceDecoder.IsQsstvTextMode("a.chat", "hello"u8));
        Assert.False(DrmSourceDecoder.IsQsstvTextMode("a.TXT", "hello"u8));
        Assert.False(DrmSourceDecoder.IsQsstvTextMode("a.jp2", "hello"u8));
        Assert.False(DrmSourceDecoder.IsQsstvTextMode("a.txt", [0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.False(DrmSourceDecoder.IsQsstvTextMode("a.txt", "GIF89a...."u8));
        byte[] bmp = new byte[30];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        bmp[14] = 40;
        Assert.False(DrmSourceDecoder.IsQsstvTextMode("a.txt", bmp));
        bmp[14] = 41;
        Assert.True(DrmSourceDecoder.IsQsstvTextMode("a.txt", bmp));
        Assert.True(DrmSourceDecoder.IsQsstvTextMode("a.txt", "BMW is a car brand, not a bitmap."u8));
    }

    // ---- names ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("20260101120000-test.txt", "20260101120000-test.txt")]
    [InlineData("../evil.txt", "evil.txt")]
    [InlineData("..\\..\\win.ini", "win.ini")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\x\\y.jpg", "y.jpg")]
    [InlineData("a:b*c?d\"e<f>g|h.png", "a_b_c_d_e_f_g_h.png")]
    [InlineData("..", "drm-file")]
    [InlineData(".", "drm-file")]
    [InlineData("", "drm-file")]
    [InlineData("   ", "drm-file")]
    [InlineData(".hidden", "_hidden")]
    [InlineData("name. . ", "name")]
    [InlineData("CON", "_CON")]
    [InlineData("con.txt", "_con.txt")]
    [InlineData("Lpt9.log", "_Lpt9.log")]
    [InlineData("COM\u00b9.jpg", "_COM\u00b9.jpg")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("ab\0cd.txt", "ab_cd.txt")]
    [InlineData("\u0001\u001f\u007f\u0085.txt", "____.txt")]
    [InlineData("caf\u00e9 \ufffd.jpg", "caf\u00e9 \ufffd.jpg")]
    [InlineData("evil\u202etxt.exe", "evil_txt.exe")]
    [InlineData("a\u200bb\ufeff.png", "a_b_.png")]
    public void Sanitize_GivesASafeFileName(string raw, string safe) => Assert.Equal(safe, DrmReceivedFileNames.Sanitize(raw));

    [Fact]
    public void Sanitize_CapsLengthAndKeepsTheExtension()
    {
        string s = DrmReceivedFileNames.Sanitize(new string('L', 500) + ".jp2");
        Assert.Equal(DrmReceivedFileNames.MaxLength, s.Length);
        Assert.EndsWith("L.jp2", s, StringComparison.Ordinal);

        string wide = DrmReceivedFileNames.Sanitize(string.Concat(Enumerable.Repeat("\U0001F600", 100)) + ".png");
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(wide) <= DrmReceivedFileNames.MaxUtf8Bytes);
        Assert.EndsWith(".png", wide, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(wide[^5]));

        string latin = DrmReceivedFileNames.Sanitize(new string('\u00e9', 200));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(latin) <= DrmReceivedFileNames.MaxUtf8Bytes);
    }

    [Fact]
    public void MakeUnique_NeverOverwrites()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.jpg", "a (1).jpg", "noext" };
        Assert.Equal("b.jpg", DrmReceivedFileNames.MakeUnique("b.jpg", taken.Contains));
        Assert.Equal("a (2).jpg", DrmReceivedFileNames.MakeUnique("a.jpg", taken.Contains));
        Assert.Equal("noext (1)", DrmReceivedFileNames.MakeUnique("noext", taken.Contains));
        string longName = DrmReceivedFileNames.Sanitize(new string('x', 300) + ".txt");
        string unique = DrmReceivedFileNames.MakeUnique(longName, n => n == longName);
        Assert.True(unique.Length <= DrmReceivedFileNames.MaxLength);
        Assert.EndsWith(" (1).txt", unique, StringComparison.Ordinal);
    }

    // ---- helpers --------------------------------------------------------------------------------------------------

    private static (DrmSourceDecoder Decoder, List<DrmReceivedFile> Files) NewDecoder()
    {
        var d = new DrmSourceDecoder();
        var files = new List<DrmReceivedFile>();
        d.FileReceived += files.Add;
        return (d, files);
    }

    private static (DrmSourceDecoder Decoder, FakeMscFrame Frame) DecoderAfterValidHeader()
    {
        var d = new DrmSourceDecoder();
        var f = new FakeMscFrame();
        // Continuity index 7, so a following packet with index 0 (the 2-byte packet's header) passes continuity.
        byte[] header = Packetize(MotObject(0x77, "v.txt", Text(10), 60, rounds: 1), PacketLength)[0];
        header[0] |= 0x07;
        f.Load(WithCrc(header[..^2]), Bits);
        d.Decode(f, 1);
        Assert.Equal(DrmMscStatus.Valid, d.MscValid);
        return (d, f);
    }

    private static byte[] Text(int n) => Enumerable.Range(0, n).Select(i => (byte)('a' + (i * 7 % 26))).ToArray();

    private static byte[] RandomBytes(Random rng, int n)
    {
        var b = new byte[n];
        rng.NextBytes(b);
        return b;
    }

    internal static byte[] WithCrc(byte[] d)
    {
        var crc = new DrmCrc();
        crc.Reset(16);
        foreach (byte b in d)
            crc.AddByte(b);
        uint c = crc.GetCrc();
        return [.. d, (byte)(c >> 8), (byte)c];
    }

    /// <summary>Header, then all body segments, <paramref name="rounds"/> times (QSSTV's schedule shape).</summary>
    private static List<byte[]> MotObject(int tid, string name, byte[] body, int segSize, int rounds)
    {
        var mot = new MotBuilder();
        byte[] nameBytes = DrmLatin1.Encode(name);
        byte[] header = [(byte)(body.Length >> 20), (byte)(body.Length >> 12), (byte)(body.Length >> 4), (byte)((body.Length & 0xF) << 4), 0, 0x04, 0x01,
            0xCC, (byte)(1 + nameBytes.Length), 0, .. nameBytes];
        var groups = new List<byte[]>();
        int count = (body.Length + segSize - 1) / segSize;
        for (int r = 0; r < rounds; r++)
        {
            groups.Add(mot.Group(3, 0, true, tid, [(byte)(header.Length >> 8), (byte)header.Length, .. header]));
            for (int s = 0; s < count; s++)
            {
                byte[] seg = body[(s * segSize)..Math.Min(body.Length, (s + 1) * segSize)];
                groups.Add(mot.Group(4, s, s == count - 1, tid, [(byte)(seg.Length >> 8), (byte)seg.Length, .. seg]));
            }
        }

        return groups;
    }

    /// <summary>CDataEncoder::GeneratePacket (DataDecoder.cpp:36-139), as <see cref="DataPacketEncoder"/>.</summary>
    private static List<byte[]> Packetize(IEnumerable<byte[]> groups, int plen)
    {
        var queue = new Queue<byte[]>(groups);
        var enc = new DataPacketEncoder(plen, 0, () => queue.Count > 0 ? queue.Dequeue() : []);
        int total = queue.Sum(g => (g.Length + plen - 1) / plen);
        enc.Init();
        var packets = new List<byte[]>();
        for (int i = 0; i < total; i++)
            packets.Add(enc.GeneratePacket());
        return packets;
    }

    private sealed class MotBuilder
    {
        private readonly int[] _ci = new int[16];

        /// <summary>A MOT data group with CRC, session header and a 2-byte transport ID (DABMOT.cpp:403-560).</summary>
        public byte[] Group(int type, int seg, bool last, int tid, byte[] segData)
        {
            byte[] h = [(byte)(0x70 | type), (byte)((_ci[type]++ & 15) << 4), (byte)((last ? 0x80 : 0) | (seg >> 8)), (byte)seg, 0x12, (byte)(tid >> 8), (byte)tid];
            return WithCrc([.. h, .. segData]);
        }
    }

    private sealed class CountingSink : IDrmMscSink
    {
        public int Inits { get; private set; }

        public void OnChannelDecoded(DrmChannelDecoder decoder, DrmDemodState state)
        {
        }

        public void Init() => Inits++;
    }
}
