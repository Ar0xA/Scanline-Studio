using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Port of QSSTV's MOT encoder <c>CMOTDABEnc</c> (drmtx/common/datadecoding/DABMOT.cpp:82-664,807-826):
/// MOT header and body segmentation, the segment schedule and the MSC data groups. One instance per
/// transmission, as QSSTV builds a new CDRMTransmitter per send. Not thread-safe.</summary>
internal sealed class MotEncoder
{
    /// <summary>RUNINLEN (DABMOT.cpp:53).</summary>
    public const int RunInLength = 24;

    /// <summary>RUNOUTLEN (DABMOT.cpp:54).</summary>
    public const int RunOutLength = 10;

    /// <summary>Longest name QSSTV puts in the MOT header and transport-ID hash (DABMOT.cpp:106-107).</summary>
    public const int MaxNameLength = 80;

    /// <summary>iPartiSizeHeader (DABMOT.cpp:289).</summary>
    public const int HeaderPartitionSize = 98;

    /// <summary>Body segments are this much smaller than the MSC frame in bytes (DABMOT.cpp:294).</summary>
    public const int BodyPartitionOverhead = 14;

    /// <summary>MOT ContentType/SubType, pinned to (2,1). QSSTV's lookup is undefined behaviour: it
    /// <c>transform</c>s into an empty <c>std::string</c> (DABMOT.cpp:143-145). (2,1) is the value QSSTV
    /// sends in every golden case and is correct for jp2, rsN and txt.</summary>
    public const int ContentType = 2;

    /// <inheritdoc cref="ContentType"/>
    public const int ContentSubType = 1;

    private readonly BsrTransportIdCounter _bsrCounter;
    private byte[][] _headerSegments = [];
    private byte[][] _bodySegments = [];
    private int[] _segmentList = [];
    private int _segmentListIndex;
    private int _contIndexHeader;
    private int _contIndexBody;

    public MotEncoder(BsrTransportIdCounter bsrCounter)
    {
        ArgumentNullException.ThrowIfNull(bsrCounter);
        _bsrCounter = bsrCounter;
    }

    /// <summary>QSSTV's global <c>txTransportID</c> as last set by <see cref="SetMotObject"/>.</summary>
    public int TransportId { get; private set; }

    /// <summary>Header segments (<c>vvbiHeader</c>): 16-bit segment header plus data.</summary>
    public IReadOnlyList<byte[]> HeaderSegments => _headerSegments;

    /// <summary>Body segments (<c>vvbiBody</c>): 16-bit segment header plus data.</summary>
    public IReadOnlyList<byte[]> BodySegments => _bodySegments;

    /// <summary>The schedule (<c>segmentList</c>): negative k = header segment -1-k, else body segment k.</summary>
    public IReadOnlyList<int> SegmentList => _segmentList;

    /// <summary>CMOTDABEnc::Reset (DABMOT.cpp:807-826). Its "null object" has an empty name, so
    /// SetMOTObject returns before touching anything.</summary>
    public void Reset()
    {
        _contIndexHeader = 0;
        _contIndexBody = 0;
        TransportId = 0;
    }

    /// <summary>CMOTDABEnc::SetMOTObject (DABMOT.cpp:82-295). <paramref name="name"/> is the
    /// on-air Latin-1 name. Returns false, changing nothing, for an empty name: QSSTV then has no MOT object.</summary>
    public bool SetMotObject(ReadOnlySpan<byte> name, ReadOnlySpan<byte> body, int bytesAvailable)
    {
        int nameSize = name.Length;
        if (name.SequenceEqual(DrmMotName.Bsr))
        {
            TransportId = _bsrCounter.Next();
        }
        else
        {
            if (nameSize > MaxNameLength)
                nameSize = MaxNameLength;
            if (nameSize == 0)
                return false;

            TransportId = TransportIdHash(name[..nameSize]);
        }

        if (bytesAvailable - BodyPartitionOverhead <= 0)
            throw new ArgumentOutOfRangeException(nameof(bytesAvailable), bytesAvailable, "MSC frame too small for a MOT body segment.");

        int headerSize = 7 + 5 + 3 + nameSize + 2;
        var header = new byte[headerSize];
        var w = new BitWriter(header);
        w.Write((uint)body.Length, 28);
        w.Write((uint)headerSize, 13);
        w.Write(ContentType, 6);
        w.Write(ContentSubType, 9);

        // TriggerTime "now": PLI 2, ParamId 5, 32 zero bits.
        w.Write(2, 2);
        w.Write(5, 6);
        w.Write(0, 32);

        // VersionNumber 0: PLI 1, ParamId 6.
        w.Write(1, 2);
        w.Write(6, 6);
        w.Write(0, 8);

        // ContentName: PLI 3, ParamId 12, Ext 0, length 1+n, charset 0, Rfa 0, name.
        w.Write(3, 2);
        w.Write(12, 6);
        w.Write(0, 1);
        w.Write((uint)(1 + nameSize), 7);
        w.Write(0, 4);
        w.Write(0, 4);
        w.WriteBytes(name[..nameSize]);

        _headerSegments = PartitionUnits(header, HeaderPartitionSize);
        _bodySegments = PartitionUnits(body, bytesAvailable - BodyPartitionOverhead);
        return true;
    }

    /// <summary>The "special hamcode" transport ID (DABMOT.cpp:113-123), over the capped Latin-1 name.</summary>
    public static int TransportIdHash(ReadOnlySpan<byte> name)
    {
        byte xor = 0;
        byte add = 0;
        for (int k = 0; k < name.Length; k++)
        {
            xor ^= name[k];
            add = unchecked((byte)(add + name[k]));
            add ^= unchecked((byte)k);
        }

        int id = (256 * add) + xor;
        if (id <= 2)
            id += name.Length;
        return id;
    }

    /// <summary>CMOTDABEnc::PartitionUnits (DABMOT.cpp:298-349): each segment is RepetitionCount 0 (3 bits),
    /// SegmentSize (13 bits), then the data.</summary>
    public static byte[][] PartitionUnits(ReadOnlySpan<byte> source, int partitionSize)
    {
        int size = source.Length;
        int numSeg = (size + partitionSize - 1) / partitionSize;
        int lastSize = size - (size / partitionSize * partitionSize);
        var segments = new byte[numSeg][];
        int pos = 0;
        for (int i = 0; i < numSeg; i++)
        {
            int segSize = (i < numSeg - 1) || lastSize == 0 ? partitionSize : lastSize;
            var seg = new byte[segSize + 2];
            var w = new BitWriter(seg);
            w.Write(0, 3);
            w.Write((uint)segSize, 13);
            source.Slice(pos, segSize).CopyTo(seg.AsSpan(2));
            pos += segSize;
            segments[i] = seg;
        }

        return segments;
    }

    /// <summary>CMOTDABEnc::prepareSegmentList (DABMOT.cpp:560-632). A non-empty
    /// <paramref name="fixBlockList"/> is QSSTV's FIX resend; its <c>counter % 30</c> test reads the
    /// run-in counter, which always ends at 24, so FIX bodies never re-insert headers.</summary>
    public static int[] PrepareSegmentList(int numHeaderSegments, int numBodySegments, IReadOnlyList<int> fixBlockList, int repetition = 1)
    {
        ArgumentNullException.ThrowIfNull(fixBlockList);
        var list = new List<int>();
        int counter;
        for (int j = 0; j < repetition; j++)
        {
            for (counter = 0; counter < RunInLength;)
            {
                AppendHeaders(list, numHeaderSegments, numBodySegments);
                counter++;
                for (int i = 0; i < numBodySegments && counter < RunInLength; i++)
                {
                    list.Add(i);
                    counter++;
                }
            }

            if (fixBlockList.Count == 0)
            {
                for (counter = 0; counter < numBodySegments; counter++)
                {
                    if (counter % 50 == 0)
                        AppendHeaders(list, numHeaderSegments, numBodySegments);
                    list.Add(counter);
                }
            }
            else
            {
                for (int m = 0; m < fixBlockList.Count; m++)
                {
                    if (counter % 30 == 0)
                        AppendHeaders(list, numHeaderSegments, numBodySegments);
                    list.Add(fixBlockList[m]);
                }
            }

            for (counter = 0; counter < RunOutLength;)
            {
                AppendHeaders(list, numHeaderSegments, numBodySegments);
                counter++;
                for (int i = 0; i < numBodySegments && counter < RunOutLength; i++)
                {
                    list.Add(i);
                    counter++;
                }
            }
        }

        return [.. list];
    }

    /// <summary>Sets the schedule and rewinds it (DABMOT.cpp:630).</summary>
    public void PrepareSegmentList(IReadOnlyList<int> fixBlockList, int repetition = 1)
    {
        _segmentList = PrepareSegmentList(_headerSegments.Length, _bodySegments.Length, fixBlockList, repetition);
        _segmentListIndex = 0;
    }

    /// <summary>CMOTDABEnc::GetDataGroup (DABMOT.cpp:635-664): the next scheduled data group.
    /// Returns true when this group was the last of the schedule, which then rewinds.</summary>
    public bool GetDataGroup(out byte[] dataGroup)
    {
        int segmentNumber = _segmentList[_segmentListIndex];
        if (segmentNumber < 0)
        {
            int headerNumber = -1 - segmentNumber;
            dataGroup = GenMotObj(_headerSegments[headerNumber], header: true, headerNumber, TransportId, headerNumber + 1 == _headerSegments.Length);
        }
        else
        {
            dataGroup = GenMotObj(_bodySegments[segmentNumber], header: false, segmentNumber, TransportId, segmentNumber + 1 == _bodySegments.Length);
        }

        _segmentListIndex++;
        if (_segmentListIndex == _segmentList.Length)
        {
            _segmentListIndex = 0;
            return true;
        }

        return false;
    }

    /// <summary>CMOTDABEnc::GenMOTObj (DABMOT.cpp:353-557): MSC data group with CRC, segment field and
    /// user access field carrying a 2-byte transport ID. The CRC is CCRC degree 16
    /// (Reset/AddByte/GetCRC, :527-535); the <c>crc16_bytewise</c> call at :549 is debug-only and unused.</summary>
    private byte[] GenMotObj(byte[] segment, bool header, int segmentNumber, int transportId, bool lastSegment)
    {
        int length = 2 + 2 + 1 + 2 + segment.Length + 2;
        var g = new byte[length];
        var w = new BitWriter(g);
        w.Write(0, 1);
        w.Write(1, 1);
        w.Write(1, 1);
        w.Write(1, 1);
        w.Write(header ? 3u : 4u, 4);
        if (header)
        {
            w.Write((uint)_contIndexHeader, 4);
            _contIndexHeader = (_contIndexHeader + 1) % 16;
        }
        else
        {
            w.Write((uint)_contIndexBody, 4);
            _contIndexBody = (_contIndexBody + 1) % 16;
        }

        w.Write(0, 4);
        w.Write(lastSegment ? 1u : 0u, 1);
        w.Write((uint)segmentNumber, 15);
        w.Write(0, 3);
        w.Write(1, 1);
        w.Write(2, 4);
        w.Write((uint)transportId, 16);
        w.WriteBytes(segment);

        var crc = new DrmCrc();
        crc.Reset(16);
        for (int i = 0; i < length - 2; i++)
            crc.AddByte(g[i]);
        w.Write(crc.GetCrc(), 16);
        return g;
    }

    private static void AppendHeaders(List<int> list, int numHeaderSegments, int numBodySegments)
    {
        for (int k = 0; k < numHeaderSegments; k++)
            list.Add(-1 - k);
        list.Add(numBodySegments - 1);
    }
}
