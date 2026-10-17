using ScanlineStudio.Abstractions.Drm;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Rx;
using ScanlineStudio.Core.Drm.Util;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P7a part 1: QSSTV's sourceDecoder::decode on packet sequences no clean OTA run produces (rx_srcdec golden,
/// harness <c>srcdec</c>): missing segments with and without RS, the 63 % threshold, a missing last segment, packet and
/// data-group CRC failures, continuity breaks, duplicates, audio/invalid frames, init mid-stream, header after body,
/// interleaved objects, transport-list eviction and bsr.bin removal, hostile names, RS extensions, and the PPI wrap.</summary>
/// <remarks>
/// <para><b>Exact</b> per frame: msc_valid, the data group as rx_frames prints it, currentSegmentNumber, rxTransportID,
/// bodyTotalSegments, rxSegments, callsignValid, drmBlockList count; per scenario: every transport block, every file
/// QSSTV wrote (name, bytes, sha256; last write wins, as on disk), the bsr payloads and the event sequence, with the
/// x86 signed RS size byte reproduced.</para>
/// <para><b>Documented deviation</b> (hardening, <see cref="DrmSourceDecoder"/>): "sub/x.txt" (name_subdir) is reported as
/// a file; QSSTV cannot open a missing subdirectory and saves nothing. Its safe name is "x.txt". The scenario sends the
/// object once, so the block being already received here (not in QSSTV) changes nothing after it.</para>
/// </remarks>
public sealed class RxSourceDecoderScenarioGoldenTests(ITestOutputHelper output)
{
    private static readonly Dictionary<string, string[]> DeviatingFiles = new() { ["name_subdir"] = ["sub/x.txt"] };

    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (string d in Directory.GetDirectories(Path.Combine(GoldenFixtures.GoldenDir, "rx_srcdec")).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal))
            data.Add(d);
        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Scenario_MatchesQsstv(string scenario)
    {
        string dir = Path.Combine("rx_srcdec", scenario);
        Run run = Execute(dir, legacy: true);
        string[][] golden = RxGoldenRun.Csv(dir, "srcdec.csv.gz");
        Assert.Equal(golden.Length, run.Rows.Count);
        var failures = new List<string>();
        for (int f = 0; f < golden.Length; f++)
        {
            string want = string.Join(',', golden[f][1..]);
            if (want != run.Rows[f])
                failures.Add($"frame {f}: QSSTV {want}, ours {run.Rows[f]}");
        }

        Assert.True(failures.Count == 0, $"{scenario}: {string.Join("; ", failures.Take(6))}");

        using JsonDocument m = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFixtures.GoldenDir, dir, "srcdec_manifest.json")));
        JsonElement root = m.RootElement;
        // A deviating file is already received here, not in QSSTV (it could not open it).
        string[] deviating = DeviatingFiles.GetValueOrDefault(scenario, []);
        Assert.Equal(
            root.GetProperty("transport_blocks").EnumerateArray().Select(DescribeGolden).ToArray(),
            run.Decoder.TransportBlocks.Select(t => Describe(t, deviating.Contains(t.FileName) ? false : t.AlreadyReceived)).ToArray());

        // Files on QSSTV's disk: last write per name wins.
        var disk = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (DrmReceivedFile f in run.Files.Where(f => !deviating.Contains(f.QsstvFileName)))
            disk[f.QsstvFileName] = f.Data.ToArray();
        string[] wantFiles = root.GetProperty("saved_files").EnumerateArray()
            .Select(f => $"{f.GetProperty("name").GetString()}|{f.GetProperty("bytes").GetInt32()}|{f.GetProperty("sha256").GetString()}")
            .Order(StringComparer.Ordinal).ToArray();
        string[] gotFiles = disk.Select(kv => $"{kv.Key}|{kv.Value.Length}|{Sha(kv.Value)}").Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(wantFiles, gotFiles);
        Assert.Equal(deviating, run.Files.Where(f => deviating.Contains(f.QsstvFileName)).Select(f => f.QsstvFileName).ToArray());

        string[] wantBsr = root.GetProperty("bsr_received").EnumerateArray().Select(b => b.GetProperty("sha256").GetString()!).ToArray();
        Assert.Equal(wantBsr, run.Bsr.Select(b => Sha(b)).ToArray());

        Assert.Equal(GoldenEvents(root), OurEvents(run, deviating));
        output.WriteLine($"{scenario}: {run.Rows.Count} frames, {run.Files.Count} file(s), {run.Bsr.Count} bsr, {run.Decoder.TransportBlocks.Count} block(s)");
    }

    /// <summary>The production decoder on the same scenarios: only RS files with a surplus of 128 or more change, to the
    /// transmitted size, and ppi_wrap's file that QSSTV loses to the 8-bit PPI count is received (P6b-1).</summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void ProductionDecoder_FixesOnlyTheSignedSizeByteAndThePpiWrap(string scenario)
    {
        string dir = Path.Combine("rx_srcdec", scenario);
        Run legacy = Execute(dir, legacy: true);
        Run prod = Execute(dir, legacy: false);
        if (scenario == "ppi_wrap")
        {
            // make_rx_srcdec.py ppi_scenario: wrap.txt = (test.txt × 3)[:571], last group 279 bytes (count byte 23).
            byte[] base3 = [.. Enumerable.Repeat(File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", "test.txt")), 3).SelectMany(b => b)];
            Assert.Equal(["ok.txt"], legacy.Files.Select(f => f.QsstvFileName));
            Assert.Equal(["wrap.txt", "ok.txt"], prod.Files.Select(f => f.QsstvFileName));
            Assert.Equal(base3[..571], prod.Files[0].Data.ToArray());
            Assert.Equal(legacy.Files[0].Data.ToArray(), prod.Files[1].Data.ToArray());
            return;
        }

        Assert.Equal(legacy.Rows, prod.Rows);
        Assert.Equal(legacy.Events, prod.Events);
        Assert.Equal(legacy.Files.Count, prod.Files.Count);
        byte[] test = File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", "test.txt"));
        for (int i = 0; i < prod.Files.Count; i++)
        {
            DrmReceivedFile l = legacy.Files[i], p = prod.Files[i];
            Assert.Equal(l with { Data = p.Data }, p);
            string key = scenario + ":" + p.QsstvFileName;
            switch (key)
            {
                case "rs1_full_x86:20260101120000-test.txt":
                    Assert.Equal(441, l.Data.Length);
                    Assert.Equal(test, p.Data.ToArray());
                    break;
                case "rs_crafted:p10.txt":
                    Assert.Equal(217, l.Data.Length);
                    Assert.Equal(test[..10], p.Data.ToArray());
                    break;
                case "rs_crafted:p220.txt":
                    // QSSTV quirk kept: 220 mod 224 > 224-7, so the size byte cannot describe the padding on any CPU.
                    Assert.Equal(441, p.Data.Length);
                    Assert.Equal(test[..220], p.Data[..220].ToArray());
                    Assert.Equal(l.Data.ToArray(), p.Data.ToArray());
                    break;
                default:
                    Assert.Equal(l.Data.ToArray(), p.Data.ToArray());
                    break;
            }
        }
    }

    /// <summary>The RS early erasure check never rejects an attempt the full decode would accept, and with the check in
    /// place the scenario still produces exactly the same files and events.</summary>
    [Theory]
    [MemberData(nameof(Scenarios))]
    public void EarlyErasureCheck_NeverRejectsADecodableAttempt(string scenario)
    {
        string dir = Path.Combine("rx_srcdec", scenario);
        int attempts = 0, rejected = 0;
        Run probed = Execute(dir, legacy: true, (early, full) =>
        {
            attempts++;
            if (!early)
                rejected++;
            Assert.False(!early && full, $"{scenario}: early check rejected a decodable attempt");
        });
        Run plain = Execute(dir, legacy: true);
        Assert.Equal(probed.Rows, plain.Rows);
        Assert.Equal(probed.Events, plain.Events);
        Assert.Equal(probed.Files.Select(f => Convert.ToHexString(f.Data.Span)), plain.Files.Select(f => Convert.ToHexString(f.Data.Span)));
        output.WriteLine($"{scenario}: {attempts} RS attempts, {rejected} rejected early");
        string[] withAttempts = ["rs1_full_x86", "rs1_jp2_missing_1", "rs2_missing_1", "rs2_missing_2", "rs3_missing_2", "rs4_missing_4", "rs_crafted"];
        if (withAttempts.Contains(scenario))
            Assert.True(attempts > 0, $"{scenario}: the probe saw no RS attempt");
    }

    [Fact]
    public void Names_AreSanitizedAndFlagged()
    {
        Run run = Execute(Path.Combine("rx_srcdec", "names"), legacy: false);
        string[] safe = run.Files.Select(f => f.FileName).ToArray();
        Assert.Equal(
            ["evil.txt", "ab", "café.txt", "__.txt", "_CON.txt", "x.TXT", "de_hybrid.jpg", "img.txt", "dup.txt", "dup.txt", "after.txt", "tail.txt"],
            safe);
        Assert.Equal(["de_hybrid.jpg"], run.Files.Where(f => f.IsQsstvHybridName).Select(f => f.QsstvFileName));
        Assert.False(run.Files.Single(f => f.QsstvFileName == "img.txt").IsQsstvTextMode);
        Assert.False(run.Files.Single(f => f.QsstvFileName == "x.TXT").IsQsstvTextMode);
        Assert.True(run.Files.Single(f => f.QsstvFileName == "café.txt").IsQsstvTextMode);
        Assert.DoesNotContain(run.Files, f => f.TransportName.StartsWith("LLLL", StringComparison.Ordinal));
        Assert.Equal("café.txt", run.Files.Single(f => f.FileName == "café.txt").TransportName);
    }

    private sealed record Run(DrmSourceDecoder Decoder, List<string> Rows, List<string> Events, List<DrmReceivedFile> Files, List<byte[]> Bsr);

    private static Run Execute(string dir, bool legacy, Action<bool, bool>? rsProbe = null)
    {
        using JsonDocument cfg = JsonDocument.Parse(GoldenFixtures.ReadStored(dir, "srcdec.json.gz"));
        JsonElement c = cfg.RootElement;
        var frame = new FakeMscFrame
        {
            MscModeNew = c.GetProperty("msc_mode_new").GetInt32(),
            InterleaverDepthNew = c.GetProperty("interleaver_depth_new").GetInt32(),
            PlPartB = c.GetProperty("PL_PartB").GetInt32(),
            SpectrumOccupancyNew = c.GetProperty("spectrum_occupancy_new").GetInt32(),
        };
        int rm = c.GetProperty("robustness_mode").GetInt32();
        var decoder = new DrmSourceDecoder { ReproduceQsstvSignedSurplus = legacy, ReproduceQsstvPpiWrap = legacy, RsAttemptProbe = rsProbe };
        decoder.Init();
        var events = new List<string>();
        var files = new List<DrmReceivedFile>();
        var bsr = new List<byte[]>();
        var rows = new List<string>();
        decoder.ReceivingNameChanged += n => events.Add("rxDRMStatus:" + n);
        decoder.FileReceived += f =>
        {
            files.Add(f);
            events.Add("file:" + f.QsstvFileName);
        };
        decoder.BsrReceived += b =>
        {
            bsr.Add(b.ToArray());
            events.Add("prepareFix:" + b.Length.ToString(CultureInfo.InvariantCulture));
        };
        decoder.AlreadyReceived += n => events.Add("alreadyReceived:" + n);

        foreach (JsonElement f in c.GetProperty("frames").EnumerateArray())
        {
            bool init = f.TryGetProperty("init", out JsonElement ie) && ie.GetBoolean();
            if (init)
            {
                decoder.Init();
            }
            else
            {
                if (f.TryGetProperty("callsign", out JsonElement cs))
                    frame.SetCallsign(cs.GetString()!);
                frame.Load(Convert.FromHexString(f.GetProperty("hex").GetString()!), f.GetProperty("bits").GetInt32(),
                    f.GetProperty("valid").GetInt32(), f.GetProperty("audio").GetInt32());
                decoder.Decode(frame, rm);
            }

            rows.Add(Row(decoder, frame, init));
        }

        return new Run(decoder, rows, events, files, bsr);
    }

    /// <summary>The srcdec.csv columns after <c>frame</c> (harness srcdec.cpp).</summary>
    private static string Row(DrmSourceDecoder d, FakeMscFrame frame, bool init)
    {
        string dg = ",,,,,";
        if (!init && frame.MscDataValid == 1)
        {
            int nb = frame.LengthDecodedData / 8;
            var bytes = new byte[nb + 1];
            DrmRxBits.BitsToBytes(frame.ChannelDecodedData[..(nb * 8)], bytes);
            if (nb >= 2 && RxCrc.Crc16Bytewise(bytes.AsSpan(0, nb)) == 0 && (bytes[0] & 0x40) != 0)
            {
                DrmDataGroup p = d.CurrentDataGroup;
                dg = string.Join(',', S(p.DataGroupType), S(p.SegmentNumber), p.LastSegment ? "1" : "0", S(p.TransportId), S(p.SegmentSize), S(p.Lenght));
            }
        }

        return string.Join(',', S((int)d.MscValid), dg, S(d.CurrentSegmentNumber), d.RxTransportId.ToString(CultureInfo.InvariantCulture),
            S(d.BodyTotalSegments), S(d.RxSegments), frame.CallsignValid ? "1" : "0", S(d.BlockList.Count));
    }

    private static List<string> GoldenEvents(JsonElement root)
    {
        var list = new List<string>();
        string[] ev = root.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray();
        for (int i = 0; i < ev.Length; i++)
        {
            string e = ev[i];
            if (e.StartsWith("rxDRMStatus:", StringComparison.Ordinal) || e.StartsWith("prepareFix:", StringComparison.Ordinal))
                list.Add(e);
            else if (e.StartsWith("saveDRMImage:", StringComparison.Ordinal))
                list.Add("image:" + e["saveDRMImage:".Length..]);
            else if (e.StartsWith("displayText:", StringComparison.Ordinal))
                list.Add("text:" + e["displayText:".Length..]);
            else if (e.StartsWith("loadRXImage:", StringComparison.Ordinal))
            {
                Assert.StartsWith("displayMBox:File ", ev[i + 1], StringComparison.Ordinal);
                list.Add("alreadyReceived:" + e["loadRXImage:".Length..]);
                i++;
            }
            else
            {
                Assert.Fail($"unmapped golden event {e}");
            }
        }

        return list;
    }

    /// <summary>Our events as QSSTV's GUI would show them: a hybrid file is saved silently; a file QSSTV cannot open
    /// (deviating) is not shown; an image shows its file name, a text file its text.</summary>
    private static List<string> OurEvents(Run run, string[] deviating)
    {
        var list = new List<string>();
        int fileIndex = 0;
        foreach (string e in run.Events)
        {
            if (!e.StartsWith("file:", StringComparison.Ordinal))
            {
                list.Add(e);
                continue;
            }

            DrmReceivedFile f = run.Files[fileIndex++];
            if (f.IsQsstvHybridName || deviating.Contains(f.QsstvFileName))
                continue;
            list.Add(f.IsQsstvTextMode ? "text:" + QtUtf8.DecodeNulTerminated(f.Data.Span) : "image:" + QsstvPath.FileName(f.QsstvFileName));
        }

        return list;
    }

    private static string DescribeGolden(JsonElement t) => string.Join('|',
        t.GetProperty("transportID").GetInt32(), t.GetProperty("fileName").GetString(), B(t.GetProperty("headerReceived").GetBoolean()),
        t.GetProperty("totalSegments").GetInt32(), t.GetProperty("segmentsReceived").GetInt32(), t.GetProperty("bodySize").GetInt64(),
        t.GetProperty("contentType").GetInt32(), t.GetProperty("contentSubtype").GetInt32(), B(t.GetProperty("alreadyReceived").GetBoolean()),
        B(t.GetProperty("lastSegmentReceived").GetBoolean()), t.GetProperty("defaultSegmentSize").GetInt64(), t.GetProperty("modeCode").GetInt64(),
        t.GetProperty("callsign").GetString(), string.Join(',', t.GetProperty("segments_with_data").EnumerateArray().Select(x => x.GetInt32())));

    private static string Describe(DrmTransportBlock t, bool alreadyReceived) => string.Join('|',
        t.TransportId, t.FileName, B(t.HeaderReceived), t.TotalSegments, t.SegmentsReceived, t.BodySize, t.ContentType, t.ContentSubtype,
        B(alreadyReceived), B(t.LastSegmentReceived), t.DefaultSegmentSize, t.ModeCode, t.Callsign,
        string.Join(',', Enumerable.Range(0, t.Segments.Count).Where(i => t.Segments[i].HasData)));

    private static string B(bool b) => b ? "1" : "0";

    private static string S(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Sha(ReadOnlySpan<byte> d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();
}

/// <summary>A settable <see cref="IDrmMscFrame"/>: what channel decoding leaves for the source decoder.</summary>
internal sealed class FakeMscFrame : IDrmMscFrame
{
    private double[] _bits = [];

    public int MscDataValid { get; set; } = 1;

    public int AudioDataFlag { get; set; } = 1;

    public int LengthDecodedData { get; private set; }

    public ReadOnlySpan<double> ChannelDecodedData => _bits.AsSpan(0, LengthDecodedData);

    public bool CallsignValid { get; private set; }

    public string Callsign { get; private set; } = string.Empty;

    public int MscModeNew { get; set; } = 3;

    public int InterleaverDepthNew { get; set; }

    public int PlPartB { get; set; }

    public int SpectrumOccupancyNew { get; set; } = 1;

    public void InvalidateCallsign() => CallsignValid = false;

    public void SetCallsign(string callsign)
    {
        CallsignValid = true;
        Callsign = callsign;
    }

    /// <summary>The packet bytes MSB first, then zero bits up to <paramref name="bits"/>.</summary>
    public void Load(ReadOnlySpan<byte> bytes, int bits, int valid = 1, int audio = 1)
    {
        if (_bits.Length < bits)
            _bits = new double[bits];
        for (int i = 0; i < bits; i++)
            _bits[i] = i / 8 < bytes.Length ? (bytes[i / 8] >> (7 - (i % 8))) & 1 : 0;
        LengthDecodedData = bits;
        MscDataValid = valid;
        AudioDataFlag = audio;
    }
}
