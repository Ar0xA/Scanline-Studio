using ScanlineStudio.Abstractions.Drm;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using ScanlineStudio.Core.Drm.Rx;
using Xunit.Abstractions;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P7a part 1: QSSTV's source decoder (sourcedecoder.cpp, reedsolomoncoder.cpp decode) against the rx_frames
/// source-decoder columns and rx_manifest (transport blocks, saved files, events), all 79 cases, on the runs shared with
/// the P5/P6 tests (<see cref="RxGoldenRun"/>).</summary>
/// <remarks>
/// <para><b>Exact.</b> Per data-valid frame: msc_valid, the completed data group (type, segment, last, transport ID,
/// size, length), currentSegmentNumber, rxTransportID, bodyTotalSegments, rxSegments. Per case, with the decoder that
/// reads the RS size byte signed as x86 QSSTV: every transport block field, every saved file (name, length, sha256),
/// the bsr.bin payload and the event sequence. Golden <c>saveDRMImage</c>/<c>displayText</c> are one received file
/// (image vs text mode); <c>loadRXImage</c> + <c>displayMBox</c> are one "already received".</para>
/// <para><b>Production decoder</b> (RS size byte unsigned): the same transport blocks and events, and every file equals
/// the transmitted payload, including B1_q4_high_long_rs1, where QSSTV x86 appends 141 bytes.</para>
/// <para><b>B0_q4_high_long</b> (knife edge): frames are compared up to stripe 104; after that the file must still
/// decode, byte-identical.</para>
/// </remarks>
public sealed class RxSourceDecodingGoldenTests(ITestOutputHelper output)
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
    public void SourceColumns_MatchQsstv(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        int last = run.LastStripe;
        string[][] g = RxGoldenRun.Csv(caseId, "rx/rx_frames.csv.gz").Where(r => I(r[1]) <= last).ToArray();
        List<RxGoldenRun.DecodedFrame> ours = run.Decoded.Where(f => f.Stripe <= last).ToList();
        Assert.Equal(g.Length, ours.Count);
        var failures = new List<string>();
        int groups = 0;
        for (int f = 0; f < g.Length; f++)
        {
            RxGoldenRun.DecodedFrame o = ours[f];
            void Ex(string field, string q, string v)
            {
                if (q != v)
                    failures.Add($"frame {f} (stripe {g[f][1]}) {field}: QSSTV {q}, ours {v}");
            }

            Ex("msc_valid", g[f][17], S(o.MscValid));
            Ex("data_group", string.Join(',', g[f][18..24]), o.DataGroup);
            Ex("currentSegmentNumber", g[f][24], S(o.CurrentSegmentNumber));
            Ex("rxTransportID", g[f][25], o.RxTransportId.ToString(CultureInfo.InvariantCulture));
            Ex("bodyTotalSegments", g[f][26], S(o.BodyTotalSegments));
            Ex("rxSegments", g[f][27], S(o.RxSegments));
            if (g[f][18].Length > 0)
                groups++;
        }

        output.WriteLine($"{caseId}: {g.Length} frames to stripe {last}, {groups} completed data groups");
        Assert.True(failures.Count == 0, $"{caseId}: {string.Join("; ", failures.Take(8))}");
        Assert.True(groups > 5, $"{caseId}: only {groups} data groups compared");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FilesBlocksAndEvents_MatchQsstv(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        using JsonDocument m = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFixtures.GoldenDir, caseId, "rx", "rx_manifest.json")));
        JsonElement root = m.RootElement;
        RxGoldenRun.SourceRecorder legacy = run.Legacy;

        // Saved files: QSSTV's RX folder, listed by name.
        string[] want = root.GetProperty("saved_files").EnumerateArray()
            .Select(f => $"{f.GetProperty("name").GetString()}|{f.GetProperty("bytes").GetInt32()}|{f.GetProperty("sha256").GetString()}").ToArray();
        string[] got = legacy.Files.OrderBy(f => f.QsstvFileName, StringComparer.Ordinal)
            .Select(f => $"{f.QsstvFileName}|{f.Data.Length}|{Sha(f.Data.Span)}").ToArray();
        Assert.Equal(want, got);
        Assert.All(legacy.Files, f => Assert.Equal(f.QsstvFileName, f.FileName));

        string[] wantBsr = root.GetProperty("bsr_received").EnumerateArray().Select(b => b.GetProperty("sha256").GetString()!).ToArray();
        Assert.Equal(wantBsr, legacy.Bsr.Select(b => Sha(b)).ToArray());
        foreach (JsonElement b in root.GetProperty("bsr_received").EnumerateArray())
            Assert.Equal(GoldenFixtures.ReadStored(caseId, Path.Combine("rx", b.GetProperty("file").GetString()!)), legacy.Bsr[0]);

        Assert.Equal(GoldenEvents(root), legacy.Events);
        Assert.Equal(TransportBlocks(root), legacy.Decoder.TransportBlocks.Select(Describe).ToArray());

        // Text vs image mode: displayText vs saveDRMImage.
        bool[] textModes = root.GetProperty("events").EnumerateArray().Select(e => e.GetString()!)
            .Where(e => e.StartsWith("displayText:", StringComparison.Ordinal) || e.StartsWith("saveDRMImage:", StringComparison.Ordinal))
            .Select(e => e.StartsWith("displayText:", StringComparison.Ordinal)).ToArray();
        Assert.Equal(textModes, legacy.Files.Select(f => f.IsQsstvTextMode).ToArray());
        foreach (DrmReceivedFile f in legacy.Files.Where(f => f.IsQsstvTextMode))
        {
            string text = root.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).First(e => e.StartsWith("displayText:", StringComparison.Ordinal));
            // displayText is QString(readAll()), which in Qt 5 is UTF-8 up to the first NUL (sourcedecoder.cpp:615).
            Assert.Equal(text["displayText:".Length..], ScanlineStudio.Core.Drm.Util.QtUtf8.DecodeNulTerminated(f.Data.Span));
        }

        output.WriteLine($"{caseId}: {legacy.Files.Count} file(s), {legacy.Bsr.Count} bsr, {legacy.Events.Count} events, " +
            $"{legacy.Decoder.TransportBlocks.Count} transport block(s), last compared stripe {run.LastStripe} of {run.Stripes.Count - 1}");
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProductionDecoder_GivesTransmittedPayload(string caseId)
    {
        RxGoldenRun.Result run = RxGoldenRun.Get(caseId);
        RxGoldenRun.SourceRecorder legacy = run.Legacy, fixedSrc = run.Fixed;
        Assert.Equal(legacy.Events, fixedSrc.Events);
        Assert.Equal(legacy.Decoder.TransportBlocks.Select(Describe), fixedSrc.Decoder.TransportBlocks.Select(Describe));

        using JsonDocument cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenFixtures.GoldenDir, "cases.json")));
        JsonElement c = cases.RootElement.EnumerateArray().Single(x => x.GetProperty("id").GetString() == caseId);
        byte[] payload = File.ReadAllBytes(Path.Combine(GoldenFixtures.GoldenDir, "inputs", c.GetProperty("payload").GetString()!));
        if (c.GetProperty("config").GetProperty("bsr").GetBoolean())
        {
            Assert.Empty(fixedSrc.Files);
            Assert.Equal(payload, Assert.Single(fixedSrc.Bsr));
            return;
        }

        DrmReceivedFile f = Assert.Single(fixedSrc.Files);
        DrmReceivedFile l = Assert.Single(legacy.Files);
        Assert.Equal(payload, f.Data.ToArray());
        Assert.Equal(l.QsstvFileName, f.QsstvFileName);
        Assert.Equal(l with { Data = f.Data }, f);
        if (caseId == "B1_q4_high_long_rs1")
        {
            Assert.Equal(441, l.Data.Length);
            Assert.Equal(payload, l.Data[..payload.Length].ToArray());
            output.WriteLine("rs1 surplus ≥ 128: QSSTV x86 441 bytes, production 300 bytes = payload");
        }
        else
        {
            Assert.Equal(l.Data.ToArray(), f.Data.ToArray());
        }

        Assert.Equal("PA0TST", f.Callsign);
        Assert.Equal(int.Parse(caseId[1..2], CultureInfo.InvariantCulture), f.SpectrumOccupancy);
        Assert.Equal("ABE".IndexOf(caseId[0], StringComparison.Ordinal), f.RobustnessMode);
    }

    /// <summary>The golden events in <see cref="RxGoldenRun.SourceRecorder"/> form.</summary>
    private static List<string> GoldenEvents(JsonElement root)
    {
        string[] savedNames = root.GetProperty("saved_files").EnumerateArray().Select(f => f.GetProperty("name").GetString()!).ToArray();
        var list = new List<string>();
        string[] ev = root.GetProperty("events").EnumerateArray().Select(e => e.GetString()!).ToArray();
        int textFiles = 0;
        for (int i = 0; i < ev.Length; i++)
        {
            string e = ev[i];
            if (e.StartsWith("rxDRMStatus:", StringComparison.Ordinal) || e.StartsWith("prepareFix:", StringComparison.Ordinal))
            {
                list.Add(e);
            }
            else if (e.StartsWith("saveDRMImage:", StringComparison.Ordinal))
            {
                list.Add("file:" + e["saveDRMImage:".Length..]);
            }
            else if (e.StartsWith("displayText:", StringComparison.Ordinal))
            {
                // displayText carries the text, not the name; the case's text file is its saved .txt file.
                list.Add("file:" + savedNames.Where(n => n.EndsWith(".txt", StringComparison.Ordinal)).ElementAt(textFiles++));
            }
            else if (e.StartsWith("loadRXImage:", StringComparison.Ordinal))
            {
                string name = e["loadRXImage:".Length..];
                Assert.True(i + 1 < ev.Length && ev[i + 1].StartsWith("displayMBox:File ", StringComparison.Ordinal)
                    && ev[i + 1].EndsWith("/" + name + " already received", StringComparison.Ordinal), $"event {i + 1}: {ev.ElementAtOrDefault(i + 1)}");
                list.Add("alreadyReceived:" + name);
                i++;
            }
            else
            {
                Assert.Fail($"unmapped golden event {e}");
            }
        }

        return list;
    }

    private static string[] TransportBlocks(JsonElement root) =>
        root.GetProperty("transport_blocks").EnumerateArray().Select(t => string.Join('|',
            t.GetProperty("transportID").GetInt32(), t.GetProperty("fileName").GetString(), B(t.GetProperty("headerReceived").GetBoolean()),
            t.GetProperty("totalSegments").GetInt32(), t.GetProperty("segmentsReceived").GetInt32(), t.GetProperty("bodySize").GetInt64(),
            t.GetProperty("contentType").GetInt32(), t.GetProperty("contentSubtype").GetInt32(), B(t.GetProperty("alreadyReceived").GetBoolean()),
            B(t.GetProperty("lastSegmentReceived").GetBoolean()), t.GetProperty("defaultSegmentSize").GetInt64(), t.GetProperty("modeCode").GetInt64(),
            t.GetProperty("callsign").GetString(), string.Join(',', t.GetProperty("segments_with_data").EnumerateArray().Select(x => x.GetInt32()))))
            .ToArray();

    private static string Describe(DrmTransportBlock t) => string.Join('|',
        t.TransportId, t.FileName, B(t.HeaderReceived), t.TotalSegments, t.SegmentsReceived, t.BodySize, t.ContentType, t.ContentSubtype,
        B(t.AlreadyReceived), B(t.LastSegmentReceived), t.DefaultSegmentSize, t.ModeCode, t.Callsign,
        string.Join(',', Enumerable.Range(0, t.Segments.Count).Where(i => t.Segments[i].HasData)));

    private static string B(bool b) => b ? "1" : "0";

    private static string Sha(ReadOnlySpan<byte> d) => Convert.ToHexString(SHA256.HashData(d)).ToLowerInvariant();

    private static int I(string v) => int.Parse(v, CultureInfo.InvariantCulture);

    private static string S(int v) => v.ToString(CultureInfo.InvariantCulture);
}
