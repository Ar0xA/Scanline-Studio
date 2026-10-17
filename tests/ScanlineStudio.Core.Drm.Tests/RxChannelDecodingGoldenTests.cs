using System.Globalization;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Util;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P6: QSSTV's channel_decoding (channeldecode.cpp:79-904, msdhardfac/msdhardmsc, viterbi_decode, mkmscmap,
/// deinterleaver) against the rx_frames and rx_msc_bits goldens, all 79 cases, on runs shared with the P5 tests
/// (<see cref="RxGoldenRun"/>).</summary>
/// <remarks>
/// <para><b>Exact, per data-valid frame</b> (one rx_frames row per stripe that ends with
/// transmission_frame_buffer_data_valid = 1): stripe, fac_valid, robustness_mode, spectrum_occupancy_new, msc_mode_new,
/// interleaver_depth_new, PL_PartA/B, HM_length, callsignValid, drmCallsign, channel_decoded_data_buffer_data_valid,
/// length_decoded_data, the packet CRC/first/last flags (bits2bytes + crc16_bytewise, as the harness computes them), and
/// every decoded MSC bit of each frame with data_valid 1. The source-decoder columns (msc_valid, data group, segment
/// counters) are P7a.</para>
/// <para><b>Range</b> as the P5 tests: every frame, B0_q4_high_long up to stripe 104 (knife edge), after which it must
/// decode at least QSSTV's number of CRC-valid MSC frames.</para>
/// </remarks>
public sealed class RxChannelDecodingGoldenTests(ITestOutputHelper output)
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string id in GoldenFixtures.CaseIds())
            data.Add(id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Frames_MatchQsstv(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        string[][] golden = RxGoldenRun.Csv(caseId, "rx/rx_frames.csv.gz");
        byte[] gBits = GoldenFixtures.ReadStored(caseId, "rx/rx_msc_bits.bin.gz");
        int last = run.LastStripe;
        var failures = new List<string>();
        List<RxGoldenRun.DecodedFrame> ours = run.Decoded.Where(f => f.Stripe <= last).ToList();
        string[][] gRows = golden.Where(r => I(r[1]) <= last).ToArray();
        if (ours.Count != gRows.Length)
            failures.Add($"frames: QSSTV {gRows.Length}, ours {ours.Count}");
        int bitPos = 0, mscFrames = 0, crcOk = 0;
        for (int f = 0; f < Math.Min(ours.Count, gRows.Length); f++)
        {
            RxGoldenRun.DecodedFrame o = ours[f];
            string[] g = gRows[f];
            void Ex(string field, string q, string v)
            {
                if (q != v)
                    failures.Add($"frame {f} (stripe {g[1]}) {field}: QSSTV {q}, ours {v}");
            }

            Ex("stripe", g[1], S(o.Stripe));
            Ex("fac_valid", g[2], S(o.FacValid));
            Ex("robustness_mode", g[3], S(o.Mode));
            Ex("spectrum_occupancy_new", g[4], S(o.SoNew));
            Ex("msc_mode_new", g[5], S(o.MscModeNew));
            Ex("interleaver_depth_new", g[6], S(o.InterleaverDepthNew));
            Ex("PL_PartA", g[7], S(o.PlPartA));
            Ex("PL_PartB", g[8], S(o.PlPartB));
            Ex("HM_length", g[9], S(o.HmLength));
            Ex("callsignValid", g[10], o.CallsignValid ? "1" : "0");
            Ex("drmCallsign", g[11], o.Callsign);
            Ex("msc_data_valid", g[12], S(o.MscDataValid));
            Ex("length_decoded_data", g[13], S(o.Length));
            Ex("packet_crc_ok", g[14], S(o.CrcOk));
            Ex("packet_first", g[15], S(o.First));
            Ex("packet_last", g[16], S(o.Last));
            if (g[12] == "1")
            {
                int n = I(g[13]);
                if (o.Bits is { } bits && bits.Length == n)
                {
                    int bad = 0, firstBad = -1;
                    for (int i = 0; i < n; i++)
                    {
                        if (bits[i] != gBits[bitPos + i])
                        {
                            bad++;
                            if (firstBad < 0)
                                firstBad = i;
                        }
                    }

                    if (bad > 0)
                        failures.Add($"frame {f} (stripe {g[1]}): {bad} of {n} MSC bits differ, first at {firstBad}");
                }

                bitPos += n;
                mscFrames++;
                if (g[14] == "1")
                    crcOk++;
            }
        }

        output.WriteLine($"{caseId}: {Math.Min(ours.Count, gRows.Length)} data-valid frames to stripe {last}, {mscFrames} with MSC bits ({bitPos} bits), {crcOk} packet CRCs ok");
        Assert.True(failures.Count == 0, $"{caseId}: {string.Join("; ", failures.Take(8))}");
        Assert.True(mscFrames > 10, $"{caseId}: only {mscFrames} MSC frames compared");
        if (last >= run.Stripes.Count - 1)
            Assert.Equal(gBits.Length, bitPos);
        else
        {
            int qsstv = golden.Count(r => I(r[1]) > last && r[12] == "1" && r[14] == "1");
            int mine = run.Decoded.Count(d => d.Stripe > last && d.MscDataValid == 1 && d.CrcOk == 1);
            output.WriteLine($"after stripe {last}: CRC-valid MSC frames QSSTV {qsstv}, ours {mine}");
            Assert.True(mine >= qsstv, $"{caseId}: after stripe {last} only {mine} CRC-valid MSC frames, QSSTV {qsstv}");
        }
    }

    public static TheoryData<string> NoisyConfigs()
    {
        var data = new TheoryData<string>();
        foreach (string d in Directory.GetDirectories(Path.Combine(GoldenFixtures.GoldenDir, "rx_chdec")).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal))
            data.Add(d);
        return data;
    }

    /// <summary>channel_decoding on synthetic noisy frames (rx_chdec golden, harness <c>chdec</c>): the same frames as
    /// QSSTV, written where channelEstimation writes them, must give exactly QSSTV's state and decoded bits, including the
    /// erroneous bits of the deinterleaver-filling frames (data_valid 2), the noisy MSC frames, a FAC CRC failure, a
    /// data-invalid frame and superframe realignment. This pins the bit metrics, Viterbi decisions and refinement passes,
    /// which clean input cannot distinguish.</summary>
    [Theory]
    [MemberData(nameof(NoisyConfigs))]
    public void NoisyFrames_MatchQsstvChannelDecoding(string config)
    {
        string dir = Path.Combine("rx_chdec", config);
        using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFixtures.GoldenDir, dir, "config.json")));
        System.Text.Json.JsonElement c = cfg.RootElement;
        int rm = c.GetProperty("robustness_mode").GetInt32();
        int kMin = c.GetProperty("K_min").GetInt32(), kMax = c.GetProperty("K_max").GetInt32();
        float[] frames = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(GoldenFixtures.ReadStored(dir, "frames.f32.gz")).ToArray();
        string[][] golden = RxGoldenRun.Csv(dir, "chdec.csv.gz");
        byte[] gBits = GoldenFixtures.ReadStored(dir, "chdec_bits.bin.gz");

        var st = new DrmDemodState
        {
            RobustnessMode = rm,
            SymbolsPerFrame = DrmRxTables.SymbolsPerFrame[rm],
            KModulo = DrmRxTables.Tu[rm],
            KDc = DrmRxTables.Tu[rm] / 2,
            SpectrumOccupancyEstimation = c.GetProperty("so_est").GetInt32(),
            MeanEnergyOfUsedCells = 1.0f,
        };
        var decoder = new DrmChannelDecoder();
        decoder.Reset(st);
        int perFrame = st.SymbolsPerFrame * (kMax - kMin + 1) * 2;
        var failures = new List<string>();
        int bitPos = 0;
        System.Text.Json.JsonElement[] fr = c.GetProperty("frames").EnumerateArray().ToArray();
        for (int f = 0; f < fr.Length; f++)
        {
            // demodulator.cpp:573-579, then the frame as channelEstimation writes it, then channel_decoding().
            if (st.SpectrumOccupancy <= 0)
                st.SpectrumOccupancy = st.SpectrumOccupancyEstimation;
            if (st.SpectrumOccupancy > 3)
                st.SpectrumOccupancy = 3;
            int wptrBefore = st.TransmissionFrameBufferWptr;
            int n = 2 * f * perFrame;
            for (int s = 0; s < st.SymbolsPerFrame; s++)
            {
                for (int k = kMin; k <= kMax; k++, n += 2)
                {
                    int t = wptrBefore + s * st.KModulo + st.KDc + k;
                    st.TransmissionFrameBuffer[2 * t] = frames[n];
                    st.TransmissionFrameBuffer[2 * t + 1] = frames[n + 1];
                    st.ChannelTransferFunctionBuffer[2 * t] = frames[n + perFrame];
                    st.ChannelTransferFunctionBuffer[2 * t + 1] = frames[n + perFrame + 1];
                }
            }

            st.TransmissionFrameBufferDataValid = fr[f].GetProperty("data_valid").GetInt32();
            if (st.TransmissionFrameBufferDataValid != 0)
                st.FacValid = 1;
            decoder.Decode(st);

            string[] g = golden[f];
            var dm = new int[6 * decoder.LMsc];
            for (int j = 0; j < 6; j++)
                decoder.MscDemapper.Slice(j * DrmMscCellMap.MaxCells, decoder.LMsc).CopyTo(dm.AsSpan(j * decoder.LMsc));
            string[] ours =
            [
                S(f), S(wptrBefore), S(st.TransmissionFrameBufferDataValid), S(st.FacValid), S(st.SpectrumOccupancy),
                S(st.TransmissionFrameBufferWptr), S(decoder.SpectrumOccupancyNew), S(decoder.MscModeNew), S(decoder.InterleaverDepthNew),
                S(decoder.PlPartA), S(decoder.PlPartB), S(decoder.HmLength), S(decoder.AudioDataFlag), decoder.CallsignValid ? "1" : "0",
                decoder.Callsign, S(st.LFac), S(decoder.LMsc), S(decoder.MscDataValid), S(decoder.LengthDecodedData),
                Sha(System.Runtime.InteropServices.MemoryMarshal.AsBytes(dm.AsSpan())),
                Sha(System.Runtime.InteropServices.MemoryMarshal.AsBytes(st.TransmissionFrameBuffer.AsSpan())),
            ];
            string[] names = ["frame", "wptr_before", "data_valid", "fac_valid", "spectrum_occupancy", "wptr_after", "spectrum_occupancy_new",
                "msc_mode_new", "interleaver_depth_new", "PL_PartA", "PL_PartB", "HM_length", "audio_data_flag", "callsignValid", "drmCallsign",
                "lFAC", "lMSC", "msc_data_valid", "length_decoded_data", "msc_demapper_sha256", "tfb_sha256"];
            for (int i = 0; i < names.Length; i++)
            {
                if (g[i] != ours[i])
                    failures.Add($"frame {f} {names[i]}: QSSTV {g[i]}, ours {ours[i]}");
            }

            if (decoder.MscDataValid != 0)
            {
                ReadOnlySpan<double> bits = decoder.ChannelDecodedData;
                int bad = 0;
                for (int i = 0; i < bits.Length; i++)
                {
                    if ((bits[i] != 0 ? 1 : 0) != gBits[bitPos + i])
                        bad++;
                }

                if (bad > 0)
                    failures.Add($"frame {f}: {bad} of {bits.Length} decoded bits differ");
                bitPos += bits.Length;
            }
        }

        output.WriteLine($"{config}: {fr.Length} frames, {bitPos} decoded bits compared");
        Assert.True(failures.Count == 0, $"{config}: {string.Join("; ", failures.Take(8))}");
        Assert.Equal(gBits.Length, bitPos);
    }

    /// <summary>msc_mode 2 on the first-ever MSC set-up (no set-up branch, channeldecode.cpp:584-717): QSSTV is undefined
    /// here; the port keeps data_valid 2 and decodes no bits instead of throwing.</summary>
    [Fact]
    public void GarbageFacMscMode2_FirstSetup_DecodesNothing()
    {
        DrmDemodState st = ModeAState();
        var decoder = new DrmChannelDecoder();
        decoder.Reset(st);
        DecodeFacFrame(st, decoder, identityCode: 3, so: 0, cp4: 0, cp9: 1);
        Assert.Equal(1, st.FacValid);
        Assert.Equal(2, decoder.MscModeNew);
        Assert.Equal(2, decoder.MscDataValid);
        Assert.Equal(0, decoder.LengthDecodedData);
    }

    /// <summary>msc_mode 2 after an occupancy change that raised N: QSSTV would decode with the old, shorter set-up
    /// (undefined); the port keeps data_valid 1 and decodes no bits instead of throwing.</summary>
    [Fact]
    public void GarbageFacMscMode2_AfterNChange_DecodesNothing()
    {
        DrmDemodState st = ModeAState();
        var decoder = new DrmChannelDecoder();
        decoder.Reset(st);
        DecodeFacFrame(st, decoder, identityCode: 3, so: 0, cp4: 1, cp9: 1);
        Assert.Equal(3, decoder.MscModeNew);
        Assert.Equal(2, decoder.MscDataValid);
        Assert.Equal(768, decoder.LengthDecodedData);
        int nBefore = decoder.LMsc;
        DecodeFacFrame(st, decoder, identityCode: 1, so: 1, cp4: 0, cp9: 1);
        Assert.Equal(1, st.FacValid);
        Assert.Equal(2, decoder.MscModeNew);
        Assert.True(decoder.LMsc > nBefore);
        Assert.Equal(1, decoder.MscDataValid);
        Assert.Equal(0, decoder.LengthDecodedData);
    }

    /// <summary>ITER_BREAK (msdhardmsc.cpp:552) compares whole ints: with N odd a change in the last 2 bytes counts as unchanged.</summary>
    [Fact]
    public void IterBreak_OddN_IgnoresTheLastTwoBytes()
    {
        const int twoN = 2 * 45;
        var a = new byte[twoN];
        var b = new byte[twoN];
        b[twoN - 1] = 3;
        b[twoN - 2] = 1;
        Assert.True(DrmMultiStageDecoder.HardpointsUnchanged(a, b, twoN));
        b[twoN - 3] = 1;
        Assert.False(DrmMultiStageDecoder.HardpointsUnchanged(a, b, twoN));
        var even = new byte[92];
        even[91] = 1;
        Assert.False(DrmMultiStageDecoder.HardpointsUnchanged(new byte[92], even, 92));
    }

    /// <summary>The deinterleaver size, s = next power of two, equals QSSTV's <c>(int) pow(2, ceil(log(x) / log(2)))</c>
    /// (deinterleaver.cpp:79-98) on every size QSSTV passes: the FAC (90), N_MUX of each mode/occupancy (cell
    /// deinterleaver) and 2·N_MUX (bit deinterleavers); xinA is always 0.</summary>
    [Fact]
    public void Deinterleaver_SizeMatchesQsstvExpression()
    {
        var nMux = new SortedSet<int>();
        var map = new DrmMscCellMap();
        for (int rm = 0; rm < 3; rm++)
        {
            for (int so = 0; so < 2; so++)
            {
                for (int depth = 0; depth < 2; depth++)
                    nMux.Add(map.Build(rm, so, depth, DrmRxTables.Tu[rm] / 2, DrmRxTables.Tu[rm], out _));
            }
        }

        Assert.Equal([339, 369, 455, 530, 647, 704], nMux);
        int[] sizes = [90, .. nMux, .. nMux.Select(n => 2 * n)];
        foreach (int x in sizes)
            Assert.Equal((int)Math.Pow(2, Math.Ceiling(Math.Log(x) / Math.Log(2.0))), DrmDeinterleaver.PowerOfTwoAbove(x));
        Assert.Equal(0, DrmDeinterleaver.PowerOfTwoAbove(0));
    }

    private static DrmDemodState ModeAState()
    {
        var st = new DrmDemodState
        {
            RobustnessMode = 0,
            SymbolsPerFrame = DrmRxTables.SymbolsPerFrame[0],
            KModulo = DrmRxTables.Tu[0],
            KDc = DrmRxTables.Tu[0] / 2,
            SpectrumOccupancyEstimation = 0,
            MeanEnergyOfUsedCells = 1.0f,
        };
        Array.Fill(st.ChannelTransferFunctionBuffer, 0.0f);
        for (int i = 0; i < st.ChannelTransferFunctionBuffer.Length; i += 2)
            st.ChannelTransferFunctionBuffer[i] = 1.0f;
        return st;
    }

    // One data-valid frame whose FAC carries the given fields (TX FAC layout, FacEncoder.cs), clean 4-QAM FAC cells, zero MSC cells.
    private static void DecodeFacFrame(DrmDemodState st, DrmChannelDecoder decoder, int identityCode, int so, int cp4, int cp9)
    {
        var bits = new byte[48];
        bits[0] = (byte)(identityCode >> 1);
        bits[1] = (byte)(identityCode & 1);
        bits[2] = (byte)so;
        bits[3] = 1;
        bits[4] = (byte)cp4;
        bits[6] = 1;
        bits[9] = (byte)cp9;
        var crc = new DrmCrc();
        crc.Reset(8);
        for (int i = 0; i < 40; i += 8)
        {
            int v = 0;
            for (int k = 0; k < 8; k++)
                v = (v << 1) | bits[i + k];
            crc.AddByte((byte)v);
        }

        uint c = crc.GetCrc();
        for (int i = 0; i < 8; i++)
            bits[40 + i] = (byte)((c >> (7 - i)) & 1);
        System.Numerics.Complex[] cells = ScanlineStudio.Core.Drm.Tx.MlcEncoder.ForFac().Encode(bits).Cells;
        Array.Clear(st.TransmissionFrameBuffer);
        var facK = new int[65];
        int lFac = DrmFacCellMap.Build(0, st.KDc, st.KModulo, facK);
        for (int i = 0; i < lFac; i++)
        {
            int t = st.TransmissionFrameBufferWptr + facK[i];
            st.TransmissionFrameBuffer[2 * t] = (float)cells[i].Real;
            st.TransmissionFrameBuffer[2 * t + 1] = (float)cells[i].Imaginary;
        }

        if (st.SpectrumOccupancy <= 0)
            st.SpectrumOccupancy = st.SpectrumOccupancyEstimation;
        st.TransmissionFrameBufferDataValid = 1;
        st.FacValid = 1;
        decoder.Decode(st);
    }

    /// <summary>The RX CRC16 (crc16_bytewise.cpp) accepts exactly what the TX CRC16 (CRC.cpp, CRC-16/GENIBUS) appends.</summary>
    [Fact]
    public void RxCrc16_AcceptsTxGenibusCrc()
    {
        var rng = new Random(16);
        var crc = new DrmCrc();
        for (int t = 0; t < 200; t++)
        {
            var data = new byte[2 + rng.Next(1, 300)];
            rng.NextBytes(data.AsSpan(0, data.Length - 2));
            crc.Reset(16);
            for (int i = 0; i < data.Length - 2; i++)
                crc.AddByte(data[i]);
            uint c = crc.GetCrc();
            data[^2] = (byte)(c >> 8);
            data[^1] = (byte)c;
            Assert.Equal(0, RxCrc.Crc16Bytewise(data));
            data[rng.Next(data.Length)] ^= (byte)(1 << rng.Next(8));
            Assert.NotEqual(0, RxCrc.Crc16Bytewise(data));
        }
    }

    /// <summary>QSSTV's RX deinterleaver is the inverse of the TX block-interleaver permutation at the sizes it uses.</summary>
    [Theory]
    [InlineData(90, 21)]
    [InlineData(2 * 768, 21)]
    [InlineData(2 * 1100, 13)]
    [InlineData(373, 5)]
    public void Deinterleaver_IsInverseOfTxInterleaver(int size, int t)
    {
        var rx = new int[size];
        DrmDeinterleaver.Build(0, t, size, t, rx);
        int[] tx = BlockInterleaverTable.Make(size, t);
        for (int i = 0; i < size; i++)
            Assert.Equal(i, rx[tx[i]]);
    }

    [Fact]
    public void BitsToBytes_MsbFirst_AndIgnoresPartialBytes()
    {
        double[] bits = [1, 0, 0, 0, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 0, 0];
        var bytes = new byte[2];
        DrmRxBits.BitsToBytes(bits, bytes);
        Assert.Equal([0x81, 0x60], bytes);
        var none = new byte[2];
        DrmRxBits.BitsToBytes(bits.AsSpan(0, 9), none);
        Assert.Equal([0, 0], none);
    }

    private static int I(string v) => int.Parse(v, CultureInfo.InvariantCulture);

    private static string Sha(ReadOnlySpan<byte> data) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private static string S(int v) => v.ToString(CultureInfo.InvariantCulture);
}
