using System.Text.Json;
using ScanlineStudio.Core.Drm.Tx;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tests;

/// <summary>P2 TX source side against every QSSTV golden case: RS file, MOT segments, schedule,
/// transport IDs, MSC packets per frame, FAC bits per frame and the frame count with the
/// end-of-transmission tail. All exact.</summary>
public class TxSourceGoldenTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string id in GoldenFixtures.CaseIds())
            data.Add(id);
        return data;
    }

    [Fact]
    public void AllSeventyNineCasesAreEnumerated() => Assert.Equal(79, GoldenFixtures.CaseIds().Count());

    [Theory]
    [MemberData(nameof(Cases))]
    public void SourceSide_MatchesQsstvExactly(string caseId)
    {
        using JsonDocument doc = GoldenFixtures.LoadManifest(caseId);
        JsonElement m = doc.RootElement;
        JsonElement cfg = m.GetProperty("config");
        JsonElement p = m.GetProperty("drmTxParams");

        var qp = new QsstvDrmTxParams(
            p.GetProperty("robMode").GetInt32(),
            p.GetProperty("bandwith").GetInt32(),
            p.GetProperty("qam").GetInt32(),
            p.GetProperty("protection").GetInt32(),
            p.GetProperty("interleaver").GetInt32(),
            p.GetProperty("reedSolomon").GetInt32());
        DrmTxSettings settings = qp.ToSettings(cfg.GetProperty("fac_callsign_sent").GetString()!);
        byte[] payload = GoldenFixtures.ReadStored(caseId, m.GetProperty("stages").GetProperty("payload").GetProperty("stored").GetString()!);

        DrmTxObject obj;
        if (cfg.GetProperty("bsr").GetBoolean())
        {
            obj = DrmTxObject.ForBsr(payload);
        }
        else
        {
            var naming = new DrmTxNaming(new FixedTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)));
            DrmTxFileName fileName = naming.GetTxFileName(cfg.GetProperty("name").GetString()!, binary: true);
            Assert.Equal(m.GetProperty("drmTxFileName").GetString(), fileName.FileName);
            obj = DrmTxObject.ForFile(payload, fileName, settings.ReedSolomon);
        }

        Assert.Equal(m.GetProperty("mot_object_name").GetString(), DrmLatin1.Decode(obj.MotName.Span));
        if (settings.ReedSolomon != DrmReedSolomon.None)
            Assert.Equal(GoldenFixtures.LoadStageBlocks(caseId, m, "rs_coded")[0], obj.Body.ToArray());

        var gen = new DrmSourceFrameGenerator(obj, settings, new BsrTransportIdCounter());
        JsonElement par = m.GetProperty("parameters");
        Assert.Equal(par.GetProperty("iNumDecodedBitsMSC").GetInt32(), gen.NumDecodedBitsMsc);
        Assert.Equal(par.GetProperty("iPacketLen").GetInt32(), gen.PacketLength);
        Assert.Equal(m.GetProperty("txTransportID").GetInt32(), gen.InitialTransportId);
        AssertBlocks(GoldenFixtures.LoadStageBlocks(caseId, m, "mot_header_segments"), gen.HeaderSegments, "header segment");
        AssertBlocks(GoldenFixtures.LoadStageBlocks(caseId, m, "mot_body_segments"), gen.BodySegments, "body segment");
        Assert.Equal(m.GetProperty("segment_schedule").Ints(), gen.SegmentSchedule.ToArray());
        Assert.Equal(m.GetProperty("numTxFrames_segmentList_size").GetInt32(), gen.SegmentSchedule.Count);

        List<DrmSourceFrame> frames = gen.Frames().ToList();
        byte[][] msc = GoldenFixtures.LoadStageBlocks(caseId, m, "msc_packets");
        byte[][] fac = GoldenFixtures.LoadStageBlocks(caseId, m, "fac_bits");
        Assert.Equal(m.GetProperty("frames_mlc").GetInt32(), frames.Count);
        Assert.Equal(msc.Length, frames.Count);
        Assert.Equal(fac.Length, frames.Count(f => f.FacBits is not null));
        Assert.Null(frames[^1].FacBits);
        for (int k = 0; k < frames.Count; k++)
        {
            Assert.True(msc[k].AsSpan().SequenceEqual(frames[k].MscBits), $"MSC frame {k} differs");
            if (k < fac.Length)
                Assert.True(fac[k].AsSpan().SequenceEqual(frames[k].FacBits), $"FAC frame {k} differs");
        }

        Assert.Equal(m.GetProperty("txTransportID_at_end").GetInt32(), gen.CurrentTransportId);
    }

    private static void AssertBlocks(byte[][] expected, IReadOnlyList<byte[]> actual, string what)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i].AsSpan().SequenceEqual(actual[i]), $"{what} {i} differs");
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
