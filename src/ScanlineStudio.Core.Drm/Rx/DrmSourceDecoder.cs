using System.Diagnostics;
using ScanlineStudio.Abstractions.Drm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Rx;

/// <summary>QSSTV's <c>emscStatus msc_valid</c> (drmrx/drm.h:114): the status display's MSC state.</summary>
internal enum DrmMscStatus
{
    Invalid = 0,
    Valid = 1,
    AlreadyReceived = 2,
}

/// <summary>What source decoding reads from channel decoding (the drm.cpp/channeldecode.cpp globals, sourcedecoder.cpp:78-91,411,810-841);
/// <see cref="DrmChannelDecoder"/> implements it.</summary>
internal interface IDrmMscFrame
{
    /// <summary><c>channel_decoded_data_buffer_data_valid</c>.</summary>
    int MscDataValid { get; }

    /// <summary><c>audio_data_flag</c>.</summary>
    int AudioDataFlag { get; }

    /// <summary><c>length_decoded_data</c>.</summary>
    int LengthDecodedData { get; }

    /// <summary><c>channel_decoded_data_buffer</c>, <see cref="LengthDecodedData"/> bits.</summary>
    ReadOnlySpan<double> ChannelDecodedData { get; }

    /// <summary><c>callsignValid</c>.</summary>
    bool CallsignValid { get; }

    /// <summary><c>drmCallsign</c>.</summary>
    string Callsign { get; }

    /// <summary><c>msc_mode_new</c>.</summary>
    int MscModeNew { get; }

    /// <summary><c>interleaver_depth_new</c>.</summary>
    int InterleaverDepthNew { get; }

    /// <summary><c>multiplex_description.PL_PartB</c>.</summary>
    int PlPartB { get; }

    /// <summary><c>spectrum_occupancy_new</c>.</summary>
    int SpectrumOccupancyNew { get; }

    /// <summary><c>callsignValid = false</c> (sourcedecoder.cpp:810).</summary>
    void InvalidateCallsign();
}

/// <summary>QSSTV's <c>currentDataPacket</c> (drmrx/sourcedecoder.h:51-81): the last reassembled MSC data group,
/// parsed in place. Fields not rewritten by a data group keep their previous value, as in QSSTV.</summary>
internal sealed class DrmDataGroup
{
    private byte[] _ba = [];
    private int _offset;

    /// <summary><c>ba.count()</c>: bytes not yet consumed.</summary>
    public int Count { get; private set; }

    public bool ExtFlag { get; set; }

    public bool CrcFlag { get; set; }

    public bool SessionFlag { get; set; }

    public bool UserFlag { get; set; }

    /// <summary><c>dataGroupType</c> = header &amp; 0x07 (sourcedecoder.cpp:234), ported as-is.</summary>
    public int DataGroupType { get; set; }

    public ushort SegmentNumber { get; set; }

    public bool LastSegment { get; set; }

    public ushort TransportId { get; set; }

    public byte UserAccessField { get; set; }

    public ushort SegmentSize { get; set; }

    /// <summary><c>lenght</c>: the segment bytes after the headers.</summary>
    public int Lenght { get; set; }

    /// <summary>The unconsumed bytes.</summary>
    public ReadOnlySpan<byte> Span => _ba.AsSpan(_offset, Count);

    public void Load(byte[] ba)
    {
        _ba = ba;
        _offset = 0;
        Count = ba.Length;
    }

    /// <summary><c>ba.at(i)</c>. Qt reads the terminating NUL at i == size; past it QSSTV reads undefined memory, defined here as 0.</summary>
    public byte At(int i) => i < Count ? _ba[_offset + i] : (byte)0;

    /// <summary><c>advance(n)</c> = <c>ba.remove(0, n)</c>, clamped as QByteArray does.</summary>
    public void Advance(int n)
    {
        if (n <= 0)
            return;
        int k = Math.Min(n, Count);
        _offset += k;
        Count -= k;
    }

    /// <summary><c>chop(n)</c>.</summary>
    public void Chop(int n)
    {
        if (n > 0)
            Count = Math.Max(Count - n, 0);
    }

    /// <summary><c>QString::fromLatin1(ba.data() + 1)</c>: Latin-1 up to the first NUL (sourcedecoder.cpp:395). With an
    /// empty array QSSTV reads past the terminator (undefined), defined here as "".</summary>
    public string Latin1CStringFrom1()
    {
        if (Count <= 1)
            return string.Empty;
        ReadOnlySpan<byte> s = Span[1..];
        int nul = s.IndexOf((byte)0);
        return DrmLatin1.Decode(nul < 0 ? s : s[..nul]);
    }
}

/// <summary>Port of QSSTV's RX source decoder <c>sourceDecoder</c> (drmrx/sourcedecoder.cpp:43-708,794-848, sourcedecoder.h;
/// Johan Maes ON4QZ, GPL-2.0-or-later): per CRC-valid MSC packet, data-group reassembly by continuity index, the MOT
/// data-group header, MOT header core and ContentName, body segments by transport ID, the save rules and the RS
/// erasure decode (<see cref="RsFileDecoder"/>). It is the <see cref="IDrmMscSink"/> after channel decoding.</summary>
/// <remarks>
/// <para><b>Save rules</b> (exact): a non-RS file needs every segment and the MOT header (sourcedecoder.cpp:683,
/// <c>isComplete</c>); an RS file is tried once ≥ 63 % of the segments, the header and the last segment are in
/// (:491-503), on every new segment until it decodes. A file is reported once per transport ID; at most
/// <see cref="MaxTransportLists"/> transport blocks are kept, oldest dropped, and a new block drops every
/// <c>bsr.bin</c> block (:815-827).</para>
/// <para><b>Output.</b> Events are raised synchronously on the caller's thread (the receive worker), inside
/// <see cref="OnChannelDecoded"/>. There is no buffer and no drop: a slow handler blocks decoding of the next frame
/// (and the whole receive chain behind it), so handlers must only hand off, e.g. to a bounded queue. A handler
/// exception propagates out of <see cref="OnChannelDecoded"/>/<see cref="Decode"/>, so the receive worker must catch it.
/// Handlers must not call back into this instance; a consumer on another thread copies or queues what it needs
/// (<see cref="DrmReceivedFile.Data"/> is not reused).
/// <see cref="FileReceived"/> is the point where QSSTV writes the file and calls <c>saveImage</c>;
/// <see cref="ReceivingNameChanged"/> is QSSTV's <c>rxDRMStatusEvent</c> ("" when a transport block opens, the
/// ContentName when it arrives); <see cref="AlreadyReceived"/> is the one-shot "File … already received" of
/// <c>addHeaderSegment</c> (:301-318). A received <c>bsr.bin</c> is not a file: it is logged at Debug and raised as
/// <see cref="BsrReceived"/> for P9, as QSSTV hands it to the FIX dialog instead of saving it (:685-691).</para>
/// <para><b>Interpretation-side deviations</b> (hardening and defined behaviour where QSSTV is undefined):
/// a packet over <see cref="PacketBufferLength"/> bytes (QSSTV only logs and overflows its buffer, :86-90) or under
/// 3 bytes (QSSTV's <c>len-3</c> wraps, :140) is dropped as invalid; a data group growing past
/// <see cref="MaxDataGroupBytes"/> is dropped and continuity reset (QSSTV appends without bound); reads past a short
/// data group give 0 (<see cref="DrmDataGroup.At"/>); a body over <see cref="MaxBodyBytes"/> is not reassembled;
/// the RS size byte is read unsigned (<see cref="RsFileDecoder"/>); a last PPI packet whose 8-bit useful-byte count
/// wrapped on the TX side (:133-137) is re-read with count + 256 when that gives a CRC-verified group of the right size
/// (<see cref="RetryWrappedPpiCount"/>); names are reported unsanitized as
/// <see cref="DrmReceivedFile.QsstvFileName"/> and sanitized as <see cref="DrmReceivedFile.FileName"/>, so a name with
/// a path is saved under its last component where QSSTV writes into (or fails to open) a subdirectory; Core assumes the
/// consumer's store succeeds, where QSSTV leaves a file unsaved when <c>QFile::open</c> fails (:697-701), except for
/// names whose last component is empty, "." or "..", which QSSTV can never open and which are not saved here either.</para>
/// <para><b>Text mode.</b> QSSTV clears its <c>checkIt</c> flag after showing an image but not after a text file
/// (:636-653), and QSSTV decides "text" by <c>QImage::load</c> failing on a "txt"/"chat" file. Core approximates the
/// load with JPEG/PNG/GIF/BMP signatures (BMP with a known DIB header size) (<see cref="DrmReceivedFile.IsQsstvTextMode"/>).</para>
/// <para><b>Not ported:</b> hybrid FTP retrieval and notification (:525-563,572-593; hybrid RX is treated as off),
/// the logbook entry and avgSNR text (:513-523,626-634; the consumer logs), the BSR text builder <c>storeBSR</c> and
/// <c>getBSR</c> (:710-791, P9), and QSSTV's debug logging.</para>
/// <para><b>QSSTV quirks kept</b> (P6b candidates): the data-group type mask is 3 bits (:234), so types 11 and 12 alias
/// to MOT header/body; a PLI-3 MOT parameter takes its length from the parameter byte itself, whose top bit is always set
/// (:361-374), so it swallows the rest of the header.</para>
/// <para>Not thread-safe: one instance per receive worker.</para>
/// </remarks>
internal sealed partial class DrmSourceDecoder : IDrmMscSink
{
    /// <summary>PACKETBUFFERLEN (sourcedecoder.h:6).</summary>
    public const int PacketBufferLength = 512;

    /// <summary>MAXTRANSPORTLISTS (sourcedecoder.h:8).</summary>
    public const int MaxTransportLists = 5;

    /// <summary>Hardening bound on one data group: a MOT segment carries at most 8191 bytes (13-bit size) plus headers.</summary>
    public const int MaxDataGroupBytes = 16384;

    /// <summary>Hardening bound on a reassembled MOT body (plan: the MOT body is capped at 4 MB).</summary>
    public const int MaxBodyBytes = 4 * 1024 * 1024;

    private const string BsrFileName = "bsr.bin";

    private readonly ILogger _logger;
    private readonly byte[] _packetBuffer = new byte[PacketBufferLength];
    private readonly List<DrmTransportBlock> _transportBlocks = [];
    private readonly List<int> _erasures = [];
    private readonly List<ushort> _blockList = [];
    private byte[] _holding = new byte[1024];
    private int _holdingLength;
    private int _lastContinuityIndex = -1;
    private bool _checkIt;
    private bool _alreadyDisplayed;

    public DrmSourceDecoder(ILogger<DrmSourceDecoder>? logger = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>A file completed (see the remarks for thread and handler rules).</summary>
    public event Action<DrmReceivedFile>? FileReceived;

    /// <summary><c>rxDRMStatusEvent</c>: "" when a transport block opens, then its ContentName.</summary>
    public event Action<string>? ReceivingNameChanged;

    /// <summary>The MOT header of an already-saved object arrived again (once per <see cref="Init"/>, sourcedecoder.cpp:305-313).
    /// The argument is QSSTV's raw saved name (<see cref="DrmReceivedFile.QsstvFileName"/>), unsanitized: display text only,
    /// never a path; use <see cref="DrmReceivedFileNames.Sanitize"/> to find the stored file.</summary>
    public event Action<string>? AlreadyReceived;

    /// <summary>A complete <c>bsr.bin</c> (a BSR request). Until P9 nothing acts on it.</summary>
    public event Action<ReadOnlyMemory<byte>>? BsrReceived;

    /// <summary>Test-only: read the RS surplus byte as x86 QSSTV does (signed), to compare with its saved files.</summary>
    internal bool ReproduceQsstvSignedSurplus { get; init; }

    /// <summary>Baseline only: when set, a wrapped PPI byte count is trusted as QSSTV does (no 256 retry).</summary>
    internal bool ReproduceQsstvPpiWrap { get; init; }

    /// <summary>Test-only: when set, every RS attempt also runs the full decode and reports (early check passed, decode succeeded).</summary>
    internal Action<bool, bool>? RsAttemptProbe { get; init; }

    /// <summary>Global <c>msc_valid</c>.</summary>
    public DrmMscStatus MscValid { get; private set; }

    /// <summary>Global <c>currentSegmentNumber</c>: the segment number of the last data group with a session header.</summary>
    public int CurrentSegmentNumber { get; private set; }

    /// <summary>Global <c>rxTransportID</c>: the transport ID of the last body segment.</summary>
    public uint RxTransportId { get; private set; }

    /// <summary>Global <c>bodyTotalSegments</c>.</summary>
    public int BodyTotalSegments { get; private set; }

    /// <summary>Global <c>rxSegments</c>: segments received before the last body segment was counted (sourcedecoder.cpp:450).</summary>
    public int RxSegments { get; private set; }

    /// <summary>Global <c>drmBlockList</c>: the received segment numbers of the current transport block.</summary>
    public IReadOnlyList<ushort> BlockList => _blockList;

    /// <summary><c>currentDataPacket</c>.</summary>
    public DrmDataGroup CurrentDataGroup { get; } = new();

    /// <summary>Bytes of the data group being reassembled, for tests.</summary>
    internal int HoldingLength => _holdingLength;

    /// <summary><c>transportBlockPtrList</c>, oldest first.</summary>
    public IReadOnlyList<DrmTransportBlock> TransportBlocks => _transportBlocks;

    /// <summary><c>sourceDecoder::init</c> (sourcedecoder.cpp:51-59), from drmRx::init. Transport blocks are kept, as in QSSTV.</summary>
    public void Init()
    {
        BodyTotalSegments = 0;
        _checkIt = false;
        _erasures.Clear();
        _lastContinuityIndex = -1;
        _alreadyDisplayed = false;
    }

    /// <inheritdoc/>
    public void OnChannelDecoded(DrmChannelDecoder decoder, DrmDemodState state)
    {
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(state);
        Decode(decoder, state.RobustnessMode);
    }

    /// <summary><c>sourceDecoder::decode</c> (sourcedecoder.cpp:72-115), after every channel-decoded frame.
    /// <paramref name="robustnessMode"/> is the global <c>robustness_mode</c> (read when a transport block opens).</summary>
    public void Decode(IDrmMscFrame frame, int robustnessMode)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.MscDataValid != 1)
            return;
        if (frame.AudioDataFlag == 0)
            return;

        int nPartB = frame.LengthDecodedData / 8;
        if (nPartB > PacketBufferLength)
        {
            Log.PacketTooLong(_logger, nPartB);
            MscValid = DrmMscStatus.Invalid;
            return;
        }

        DrmRxBits.BitsToBytes(frame.ChannelDecodedData[..(nPartB * 8)], _packetBuffer);
        if (RxCrc.Crc16Bytewise(_packetBuffer.AsSpan(0, nPartB)) != 0 || !SetupDataBlock(nPartB))
        {
            MscValid = DrmMscStatus.Invalid;
            return;
        }

        switch (CurrentDataGroup.DataGroupType)
        {
            case 4:
                AddDataSegment(frame, robustnessMode);
                break;
            case 3:
                AddHeaderSegment(frame, robustnessMode);
                break;
        }
    }

    /// <summary><c>setupDataBlock</c> (sourcedecoder.cpp:119-168): packet header, continuity, data-group reassembly.</summary>
    private bool SetupDataBlock(int len)
    {
        if (len < 3)
        {
            Log.PacketTooShort(_logger, len);
            return false;
        }

        byte header = _packetBuffer[0];
        bool first = (header & 0x80) != 0;
        bool last = (header & 0x40) != 0;
        bool ppi = (header & 0x08) != 0;
        int continuityIndex = header & 0x07;

        // A PPI count past the packet reads stale buffer bytes, as QSSTV does (its buffer is static here).
        int available = ppi ? _packetBuffer[1] : len - 3;
        int start = ppi ? 2 : 1;
        if (first)
        {
            _holdingLength = 0;
            _lastContinuityIndex = continuityIndex;
        }
        else
        {
            if (_lastContinuityIndex < 0)
                return false;
            _lastContinuityIndex = (_lastContinuityIndex + 1) % 8;
            if (continuityIndex != _lastContinuityIndex)
            {
                _lastContinuityIndex = -1;
                return false;
            }
        }

        if (_holdingLength + available > MaxDataGroupBytes)
        {
            Log.DataGroupTooLong(_logger, _holdingLength + available);
            _holdingLength = 0;
            _lastContinuityIndex = -1;
            return false;
        }

        int before = _holdingLength;
        AppendToHolding(start, available);
        if (!last)
            return false;

        bool ok = SetupDataPacket();
        if (ppi && !ReproduceQsstvPpiWrap && !(ok && CurrentDataGroup.CrcFlag) && available + 256 <= len - 4)
            ok = RetryWrappedPpiCount(before, available, ok);
        return ok;
    }

    private void AppendToHolding(int start, int count)
    {
        if (_holdingLength + count > _holding.Length)
            Array.Resize(ref _holding, Math.Max(_holding.Length * 2, _holdingLength + count));
        _packetBuffer.AsSpan(start, count).CopyTo(_holding.AsSpan(_holdingLength));
        _holdingLength += count;
    }

    /// <summary>QSSTV TX writes the PPI useful-byte count in 8 bits (DataDecoder.cpp:101-102), so a last packet with
    /// 256+ useful bytes (only packets of 260+ bytes have room: A/64-QAM/low and A/SO1/64-QAM/high) arrives with
    /// count − 256 and QSSTV drops the group. Re-reads the packet with count + 256 and keeps that only if the group CRC passes and the segment size matches, both checked
    /// before any state changes; otherwise the first reading stands. <paramref name="firstResult"/> is its result.</summary>
    private bool RetryWrappedPpiCount(int before, int count, bool firstResult)
    {
        int wrapped = count + 256;
        if (before + wrapped > MaxDataGroupBytes)
            return firstResult;

        int firstLength = _holdingLength;
        _holdingLength = before;
        AppendToHolding(2, wrapped);
        if (!IsVerifiedGroupWithMatchingSize(_holding.AsSpan(0, _holdingLength)))
        {
            _holdingLength = before;
            AppendToHolding(2, count);
            Debug.Assert(_holdingLength == firstLength);
            return firstResult;
        }

        bool ok = SetupDataPacket();
        Debug.Assert(ok);
        Log.PpiCountUnwrapped(_logger, count, wrapped);
        return ok;
    }

    /// <summary>The checks <see cref="SetupDataPacket"/> would make, without its side effects: CRC flag set, CRC passes,
    /// and the segment size field equals the bytes that follow it.</summary>
    private static bool IsVerifiedGroupWithMatchingSize(ReadOnlySpan<byte> group)
    {
        if (group.Length < 4 || (group[0] & 0x40) == 0 || RxCrc.Crc16Bytewise(group) != 0)
            return false;

        int end = group.Length - 2;
        int at = 2;
        if ((group[0] & 0x80) != 0)
            at += 2;
        if ((group[0] & 0x20) != 0)
            at += 2;
        if ((group[0] & 0x10) != 0)
        {
            if (at >= end)
                return false;
            at += 1 + (group[at] & 0x0F);
        }

        if (at + 2 > end)
            return false;
        int segmentSize = ((group[at] & 0x1F) * 256) + group[at + 1];
        return segmentSize == end - (at + 2);
    }

    /// <summary><c>setupDataPacket</c> (sourcedecoder.cpp:217-282): the MSC data-group header.</summary>
    private bool SetupDataPacket()
    {
        DrmDataGroup g = CurrentDataGroup;
        g.Load(_holding.AsSpan(0, _holdingLength).ToArray());
        byte header = g.At(0);
        g.TransportId = 0xFFFF;
        g.ExtFlag = (header & 0x80) != 0;
        g.CrcFlag = false;
        g.SessionFlag = (header & 0x20) != 0;
        g.UserFlag = (header & 0x10) != 0;
        g.LastSegment = false;
        g.DataGroupType = header & 0x07;

        if ((header & 0x40) != 0)
        {
            g.CrcFlag = true;
            if (RxCrc.Crc16Bytewise(g.Span) != 0)
            {
                MscValid = DrmMscStatus.Invalid;
                return false;
            }

            g.Chop(2);
        }

        g.Advance(2);
        if (g.ExtFlag)
            g.Advance(2);

        if (g.SessionFlag)
        {
            g.SegmentNumber = (ushort)(((g.At(0) & 0x7F) * 256) + g.At(1));
            if ((g.At(0) & 0x80) != 0)
                g.LastSegment = true;
            CurrentSegmentNumber = g.SegmentNumber;
            g.Advance(2);
        }

        if (g.UserFlag)
        {
            g.UserAccessField = g.At(0);
            g.Advance(1);
            int lengthIndicator = g.UserAccessField & 0x0F;
            if ((g.UserAccessField & 0x10) != 0 && lengthIndicator >= 2)
                g.TransportId = (ushort)((g.At(0) * 256) + g.At(1));
            g.Advance(lengthIndicator);
        }

        g.SegmentSize = (ushort)(((g.At(0) & 0x1F) * 256) + g.At(1));
        g.Advance(2);
        g.Lenght = g.Count;
        return true;
    }

    /// <summary><c>addHeaderSegment</c> (sourcedecoder.cpp:294-379): the MOT header core and parameters.</summary>
    private void AddHeaderSegment(IDrmMscFrame decoder, int robustnessMode)
    {
        DrmDataGroup g = CurrentDataGroup;
        DrmTransportBlock tb = GetTransportBlock(g.TransportId, decoder, robustnessMode);
        if (!tb.AlreadyReceived)
        {
            MscValid = DrmMscStatus.Valid;
        }
        else
        {
            MscValid = DrmMscStatus.AlreadyReceived;
            if (!_alreadyDisplayed)
            {
                _alreadyDisplayed = true;
                AlreadyReceived?.Invoke(tb.NewFileName);
            }

            return;
        }

        tb.HeaderReceived = true;
        tb.BodySize = ((uint)g.At(0) << 20) + ((uint)g.At(1) << 12) + ((uint)g.At(2) << 4) + (((uint)g.At(3) & 0xF0) >> 4);
        tb.HeaderSize = (((uint)g.At(3) & 0x0F) << 9) + ((uint)g.At(4) << 1) + (((uint)g.At(5) & 0x80) >> 7);
        tb.ContentType = (ushort)((g.At(5) & 0x7E) >> 1);
        tb.ContentSubtype = (ushort)(((g.At(5) & 0x01) << 8) + g.At(6));
        g.Advance(7);

        while (g.Count > 0)
        {
            int pli = g.At(0) >> 6;
            int paramId = g.At(0) & 0x3F;
            switch (pli)
            {
                case 0:
                    g.Advance(1);
                    break;
                case 1:
                    LoadParams(tb, paramId, 1);
                    g.Advance(2);
                    break;
                case 2:
                    LoadParams(tb, paramId, 4);
                    g.Advance(5);
                    break;
                default:
                    // QSSTV tests the Ext bit on the parameter byte itself (always set for PLI 3), not on the length byte.
                    ushort dataFieldLength;
                    if ((g.At(0) & 0x80) != 0)
                    {
                        dataFieldLength = (ushort)((256 * (g.At(0) & 0x7F)) + g.At(1));
                        g.Advance(2);
                    }
                    else
                    {
                        dataFieldLength = (ushort)(g.At(0) & 0x7F);
                        g.Advance(1);
                    }

                    LoadParams(tb, paramId, dataFieldLength);
                    g.Advance(dataFieldLength);
                    break;
            }
        }
    }

    /// <summary><c>loadParams</c> (sourcedecoder.cpp:381-402): only ContentName (12) is used.</summary>
    private void LoadParams(DrmTransportBlock tb, int paramId, int len)
    {
        if (paramId != 12)
            return;

        // QString::left(len-1): a count outside 0..length-1 keeps the whole string.
        string name = CurrentDataGroup.Latin1CStringFrom1();
        int n = len - 1;
        tb.FileName = (uint)n >= (uint)name.Length ? name : name[..n];
        ReceivingNameChanged?.Invoke(tb.FileName);
    }

    /// <summary><c>addDataSegment</c> (sourcedecoder.cpp:405-457).</summary>
    private void AddDataSegment(IDrmMscFrame decoder, int robustnessMode)
    {
        DrmDataGroup g = CurrentDataGroup;
        DrmTransportBlock tb = GetTransportBlock(g.TransportId, decoder, robustnessMode);
        RxTransportId = g.TransportId;
        if (decoder.CallsignValid)
            tb.Callsign = decoder.Callsign;

        MscValid = tb.AlreadyReceived ? DrmMscStatus.AlreadyReceived : DrmMscStatus.Valid;
        if (g.LastSegment)
        {
            tb.TotalSegments = g.SegmentNumber + 1;
            tb.LastSegmentReceived = true;
        }
        else
        {
            tb.DefaultSegmentSize = g.SegmentSize;
        }

        for (int i = tb.Segments.Count; i <= g.SegmentNumber; i++)
            tb.Segments.Add(new DrmDataSegment());

        DrmDataSegment segment = tb.Segments[g.SegmentNumber];
        if (!segment.HasData)
            _checkIt = true;
        else
            MscValid = DrmMscStatus.AlreadyReceived;
        if (tb.AlreadyReceived)
        {
            MscValid = DrmMscStatus.AlreadyReceived;
            _checkIt = false;
        }

        if (tb.TotalSegments < g.SegmentNumber + 1)
            tb.TotalSegments = g.SegmentNumber + 1;
        BodyTotalSegments = tb.TotalSegments;
        RxSegments = tb.SegmentsReceived;
        segment.SetData(g.Span.ToArray());
        WriteData(tb);
    }

    /// <summary><c>writeData</c> (sourcedecoder.cpp:459-504): erasure list, segment count, the save thresholds.</summary>
    private void WriteData(DrmTransportBlock tb)
    {
        _erasures.Clear();
        _erasures.Add(tb.TotalSegments);
        _erasures.Add((int)tb.DefaultSegmentSize);
        for (int i = 0; i < tb.Segments.Count; i++)
        {
            if (!tb.Segments[i].HasData)
                _erasures.Add(i);
        }

        tb.SegmentsReceived = 0;
        _blockList.Clear();
        for (int i = 0; i < tb.Segments.Count; i++)
        {
            if (tb.Segments[i].HasData)
            {
                _blockList.Add((ushort)i);
                tb.SegmentsReceived++;
            }
        }

        if (tb.IsAlmostComplete() < 63)
            return;
        if (!tb.LastSegmentReceived)
            return;
        CheckSaveImage(tb);
    }

    /// <summary><c>checkSaveImage</c> (sourcedecoder.cpp:657-708). The body is reassembled only when it is used.</summary>
    private void CheckSaveImage(DrmTransportBlock tb)
    {
        string fileName = tb.FileName;
        tb.NewFileName = fileName;
        if (!_checkIt)
            return;

        string extension = QsstvPath.Suffix(fileName).ToLowerInvariant();
        int level = RsFileDecoder.Level(extension);
        byte[] data;
        int corrected = 0;
        if (level > 0)
        {
            if (tb.AlreadyReceived)
                return;

            // Skips the body copy and the decode when some code word is already past its parity; output identical.
            long bodyLength = BodyLength(tb);
            bool decodable = bodyLength > MaxBodyBytes || RsFileDecoder.ErasuresDecodable(bodyLength, level, _erasures);
            if (!decodable && RsAttemptProbe is null)
            {
                Log.RsDecodeFailed(_logger, tb.TransportId, tb.SegmentsReceived, tb.TotalSegments);
                return;
            }

            byte[]? body = AssembleBody(tb);
            if (body is null)
                return;
            bool decoded = RsFileDecoder.TryDecode(body, fileName, extension, _erasures, ReproduceQsstvSignedSurplus, out RsDecodeResult? rs);
            RsAttemptProbe?.Invoke(decodable, decoded);
            if (!decodable || !decoded || rs is null)
            {
                Log.RsDecodeFailed(_logger, tb.TransportId, tb.SegmentsReceived, tb.TotalSegments);
                return;
            }

            tb.NewFileName = rs.QsstvFileName;
            data = rs.File;
            corrected = rs.CorrectedSymbols;
        }
        else
        {
            if (!tb.IsComplete())
                return;
            tb.NewFileName = fileName;
            if (tb.AlreadyReceived)
                return;
            byte[]? body = AssembleBody(tb);
            if (body is null)
                return;
            if (tb.FileName == BsrFileName)
            {
                tb.SetAlreadyReceived();
                Log.BsrIgnored(_logger, tb.TransportId, body.Length);
                BsrReceived?.Invoke(body);
                return;
            }

            data = body;
        }

        if (tb.AlreadyReceived)
            return;
        string last = QsstvPath.FileName(tb.NewFileName);
        if (last.Length == 0 || last == "." || last == "..")
        {
            Log.UnnamedFile(_logger, tb.TransportId);
            return;
        }

        _erasures.Clear();
        SaveImage(tb, data, level, corrected);
    }

    /// <summary><c>saveImage</c> and the decision half of <c>displayReceivedImage</c> (sourcedecoder.cpp:507-569,597-654).</summary>
    private void SaveImage(DrmTransportBlock tb, byte[] data, int level, int corrected)
    {
        _alreadyDisplayed = true;
        if (tb.FileName.Length == 0)
            return;

        string qsstvName = tb.NewFileName;
        bool hybrid = tb.FileName.StartsWith(".de", StringComparison.Ordinal) || tb.FileName.StartsWith("de_", StringComparison.Ordinal);
        bool textMode = false;
        if (hybrid)
        {
            tb.NewFileName = string.Empty;
        }
        else
        {
            textMode = IsQsstvTextMode(qsstvName, data);
            if (!textMode)
                _checkIt = false;
        }

        int segmentsReceived = tb.SegmentsReceived;
        tb.SetAlreadyReceived();
        var file = new DrmReceivedFile(
            DrmReceivedFileNames.Sanitize(qsstvName), qsstvName, tb.FileName, data, tb.TransportId, tb.Callsign, (int)tb.ModeCode,
            tb.RobustnessMode, tb.Spectrum, tb.MscMode, tb.Mpx, tb.InterleaverDepth, tb.ContentType, tb.ContentSubtype, tb.BodySize,
            tb.TotalSegments, segmentsReceived, level, corrected, textMode, hybrid);
        Log.FileCompleted(_logger, file.FileName, data.Length, tb.TransportId, segmentsReceived, tb.TotalSegments, level);
        FileReceived?.Invoke(file);
    }

    /// <summary><c>getTransporPtr(tId, true)</c> (sourcedecoder.cpp:794-848).</summary>
    private DrmTransportBlock GetTransportBlock(ushort transportId, IDrmMscFrame decoder, int robustnessMode)
    {
        foreach (DrmTransportBlock t in _transportBlocks)
        {
            if (t.TransportId == transportId)
                return t;
        }

        decoder.InvalidateCallsign();
        BodyTotalSegments = 0;
        _blockList.Clear();
        ReceivingNameChanged?.Invoke(string.Empty);
        if (_transportBlocks.Count >= MaxTransportLists)
            _transportBlocks.RemoveAt(0);
        _transportBlocks.RemoveAll(t => t.FileName == BsrFileName);

        int mscMode = decoder.MscModeNew;
        int mCode = mscMode == 3 ? 0 : mscMode == 0 ? 2 : 1;
        int protection = decoder.PlPartB == 1 ? 1 : 0;
        var tb = new DrmTransportBlock(transportId)
        {
            RobustnessMode = robustnessMode,
            InterleaverDepth = decoder.InterleaverDepthNew,
            MscMode = mscMode,
            Mpx = decoder.PlPartB,
            Spectrum = decoder.SpectrumOccupancyNew,
        };
        tb.ModeCode = (uint)((robustnessMode * 10000) + (decoder.SpectrumOccupancyNew * 1000) + (protection * 100) + (mCode * 10)
            + decoder.InterleaverDepthNew);
        _transportBlocks.Add(tb);
        return tb;
    }

    /// <summary>The body as <c>writeData</c> builds it: received segments as received, missing ones as
    /// <c>defaultSegmentSize</c> zeros (sourcedecoder.cpp:469-480). Null past <see cref="MaxBodyBytes"/>.</summary>
    private byte[]? AssembleBody(DrmTransportBlock tb)
    {
        long total = BodyLength(tb);
        if (total > MaxBodyBytes)
        {
            Log.BodyTooLarge(_logger, tb.TransportId, total);
            return null;
        }

        var body = new byte[total];
        int pos = 0;
        foreach (DrmDataSegment s in tb.Segments)
        {
            if (s.HasData)
            {
                s.Data.CopyTo(body, pos);
                pos += s.Data.Length;
            }
            else
            {
                pos += (int)tb.DefaultSegmentSize;
            }
        }

        return body;
    }

    private static long BodyLength(DrmTransportBlock tb)
    {
        long total = 0;
        foreach (DrmDataSegment s in tb.Segments)
            total += s.HasData ? s.Data.Length : tb.DefaultSegmentSize;
        return total;
    }

    /// <summary>QSSTV shows a file as text when <c>QImage::load</c> fails and the suffix is "txt" or "chat"
    /// (sourcedecoder.cpp:607-619). The load is approximated by the JPEG/PNG/GIF signatures and a BMP signature with a known DIB header size.</summary>
    internal static bool IsQsstvTextMode(string qsstvName, ReadOnlySpan<byte> data)
    {
        string suffix = QsstvPath.Suffix(qsstvName);
        if (suffix != "txt" && suffix != "chat")
            return false;
        bool image = data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
            || data.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A])
            || data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)
            || (data.Length >= 26 && data.StartsWith("BM"u8) && IsBmpDibHeaderSize(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data[14..18])));
        return !image;
    }

    // BITMAPCOREHEADER, BITMAPINFOHEADER, the V2/V3/OS2 v2 variants, V4 and V5.
    private static bool IsBmpDibHeaderSize(uint size) => size is 12 or 40 or 52 or 56 or 64 or 108 or 124;

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: PPI byte count {Count} read as {Unwrapped} (8-bit wrap in the sender)")]
        public static partial void PpiCountUnwrapped(ILogger logger, int count, int unwrapped);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: packet of {Bytes} bytes exceeds the packet buffer, dropped")]
        public static partial void PacketTooLong(ILogger logger, int bytes);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: packet of {Bytes} bytes too short, dropped")]
        public static partial void PacketTooShort(ILogger logger, int bytes);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: data group of {Bytes} bytes exceeds the bound, dropped")]
        public static partial void DataGroupTooLong(ILogger logger, int bytes);

        [LoggerMessage(Level = LogLevel.Information, Message = "DRM RX: body of transport {TransportId} would be {Bytes} bytes, not reassembled")]
        public static partial void BodyTooLarge(ILogger logger, int transportId, long bytes);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: RS decode of transport {TransportId} failed with {Received}/{Total} segments")]
        public static partial void RsDecodeFailed(ILogger logger, int transportId, int received, int total);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: bsr.bin of {Bytes} bytes received on transport {TransportId}, ignored until BSR support")]
        public static partial void BsrIgnored(ILogger logger, int transportId, int bytes);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: transport {TransportId} completed without a usable name, not saved")]
        public static partial void UnnamedFile(ILogger logger, int transportId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "DRM RX: file {FileName} completed, {Bytes} bytes, transport {TransportId}, {Received}/{Total} segments, RS level {Level}")]
        public static partial void FileCompleted(ILogger logger, string fileName, int bytes, int transportId, int received, int total, int level);
    }
}
