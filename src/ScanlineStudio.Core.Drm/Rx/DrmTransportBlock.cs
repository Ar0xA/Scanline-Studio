namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's <c>dataSegment</c> (drmrx/sourcedecoder.h:22-49): one MOT body segment slot.</summary>
/// <remarks>QSSTV fills a new slot with <c>defaultSegmentSize</c> bytes of 0xAA, which nothing reads before
/// <see cref="SetData"/> (missing segments are reassembled as zeros, sourcedecoder.cpp:471-475), so an empty slot holds
/// no bytes here. <c>recovered</c> is never set in QSSTV and is not ported.</remarks>
internal sealed class DrmDataSegment
{
    /// <summary><c>crcOK</c>.</summary>
    public bool CrcOk { get; private set; }

    /// <summary>The segment bytes; empty until received and after <c>clearData</c>.</summary>
    public byte[] Data { get; private set; } = [];

    /// <summary><c>hasData()</c> = crcOK || recovered; stays true after <see cref="ClearData"/>.</summary>
    public bool HasData => CrcOk;

    /// <summary><c>setData(ba, segNumber, true)</c>.</summary>
    public void SetData(byte[] data)
    {
        CrcOk = true;
        Data = data;
    }

    /// <summary><c>clearData()</c>.</summary>
    public void ClearData() => Data = [];
}

/// <summary>QSSTV's <c>transportBlock</c> (drmrx/sourcedecoder.h:99-170): the reassembly state of one MOT object
/// (transport ID).</summary>
internal sealed class DrmTransportBlock(ushort transportId)
{
    public ushort TransportId { get; } = transportId;

    public uint BodySize { get; set; }

    public uint HeaderSize { get; set; }

    public ushort ContentType { get; set; }

    public ushort ContentSubtype { get; set; }

    public uint DefaultSegmentSize { get; set; }

    /// <summary>The MOT ContentName (<c>fileName</c>).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary><c>newFileName</c> without QSSTV's RX folder prefix.</summary>
    public string NewFileName { get; set; } = string.Empty;

    public bool HeaderReceived { get; set; }

    public bool AlreadyReceived { get; private set; }

    public ushort SegmentsReceived { get; set; }

    public bool LastSegmentReceived { get; set; }

    public int TotalSegments { get; set; }

    public string Callsign { get; set; } = string.Empty;

    /// <summary><c>dataSegmentPtrList</c>.</summary>
    public List<DrmDataSegment> Segments { get; } = [];

    public int RobustnessMode { get; set; }

    public int InterleaverDepth { get; set; }

    public int MscMode { get; set; }

    public int Mpx { get; set; }

    public int Spectrum { get; set; }

    public uint ModeCode { get; set; }

    /// <summary><c>isComplete()</c> (sourcedecoder.h:119-124).</summary>
    public bool IsComplete() => HeaderReceived && SegmentsReceived >= TotalSegments;

    /// <summary><c>isAlmostComplete()</c> (sourcedecoder.h:126-131): percent of segments received.</summary>
    public int IsAlmostComplete()
    {
        if (!HeaderReceived || TotalSegments == 0)
            return 0;
        return SegmentsReceived * 100 / TotalSegments;
    }

    /// <summary><c>setAlreadyReceived(true)</c> (sourcedecoder.h:133-144): drops the segment bytes, keeps hasData.</summary>
    public void SetAlreadyReceived()
    {
        AlreadyReceived = true;
        foreach (DrmDataSegment s in Segments)
            s.ClearData();
    }
}
