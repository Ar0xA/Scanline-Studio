using ScanlineStudio.Core.Drm.Tables;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>The object one transmission sends: MOT body bytes and the on-air Latin-1 MOT name.</summary>
internal sealed class DrmTxObject
{
    private readonly byte[] _body;
    private readonly byte[] _motName;

    private DrmTxObject(byte[] body, byte[] motName)
    {
        _body = body;
        _motName = motName;
    }

    /// <summary>MOT body: the payload, or its RS coding.</summary>
    public ReadOnlyMemory<byte> Body => _body;

    /// <summary>On-air MOT object name (may be empty, which QSSTV cannot send).</summary>
    public ReadOnlyMemory<byte> MotName => _motName;

    /// <summary>drmTx::initDRMImage after naming (drmtx.cpp:117-126): RS-codes the payload when asked,
    /// with the original suffix in the RS header and "rsN" as the MOT extension.</summary>
    public static DrmTxObject ForFile(ReadOnlySpan<byte> payload, DrmTxFileName fileName, DrmReedSolomon reedSolomon)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (reedSolomon == DrmReedSolomon.None)
            return new DrmTxObject(payload.ToArray(), DrmMotName.Build(fileName.BaseName, fileName.Suffix));

        byte[] body = RsFileEncoder.Encode(payload, fileName.Suffix, reedSolomon);
        return new DrmTxObject(body, DrmMotName.Build(fileName.BaseName, RsFileEncoder.TypeExtension(reedSolomon)));
    }

    /// <summary>drmTx::initDRMBSR (drmtx.cpp:291-297): "bsr.bin", never RS-coded.</summary>
    public static DrmTxObject ForBsr(ReadOnlySpan<byte> payload) => new(payload.ToArray(), DrmMotName.Bsr.ToArray());
}

/// <summary>One 400 ms frame of the TX source side.</summary>
/// <param name="Index">Frame number from 0.</param>
/// <param name="MscBits">The MLC input before energy dispersal: <c>iNumDecodedBitsMSC</c> bits, one per
/// byte, the packets followed by zero bits.</param>
/// <param name="Packets">The data packets in this frame (header, data field, CRC16).</param>
/// <param name="FacBits">The 48 FAC bits, one per byte. Null for the final frame: QSSTV builds one
/// MSC frame more than it transmits, and its run loop stops before that frame's FAC.</param>
internal sealed record DrmSourceFrame(int Index, byte[] MscBits, IReadOnlyList<byte[]> Packets, byte[]? FacBits);

/// <summary>Frame-level TX source driver, the source half of CDRMTransmitter::Run (CDrmTransmitter.cpp:51-114):
/// per frame the AudioSourceEncoder packets, then the FAC block. Stops after the frame in which the
/// end-of-transmission stop is raised. Channel coding (energy dispersal onwards) is the next stage.
/// Single use, not thread-safe.</summary>
internal sealed class DrmSourceFrameGenerator
{
    private const int PacketId = 0;

    private readonly MotEncoder _mot;
    private readonly MotSlideShowEncoder _slideShow;
    private readonly DataPacketEncoder _packets;
    private readonly FacEncoder _fac;
    private bool _started;

    /// <param name="bsrCounter">The transmitter's BSR transport-ID counter, shared across transmissions.</param>
    /// <param name="fixBlockList">FIX resend segments; empty for a normal send.</param>
    public DrmSourceFrameGenerator(DrmTxObject txObject, DrmTxSettings settings, BsrTransportIdCounter bsrCounter, IReadOnlyList<int>? fixBlockList = null)
    {
        ArgumentNullException.ThrowIfNull(txObject);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(bsrCounter);

        // QSSTV sets no MOT object for an empty name (DABMOT.cpp:110-111), then indexes an empty header list.
        if (txObject.MotName.IsEmpty)
            throw new ArgumentException("An empty MOT name gives no MOT object to send.", nameof(txObject));

        var table = new CellMappingTable(settings.Mode, settings.Occupancy);
        NumDecodedBitsMsc = MscCapacity.NumDecodedBits(table, settings.Qam, settings.Protection);
        PacketLength = (NumDecodedBitsMsc / 8) - 3;

        _mot = new MotEncoder(bsrCounter);
        _slideShow = new MotSlideShowEncoder(_mot, txObject.MotName.Span, txObject.Body.Span, fixBlockList ?? []);
        _packets = new DataPacketEncoder(PacketLength, PacketId, _slideShow.GetDataUnit);
        _fac = new FacEncoder(settings, DrmLatin1.EncodeNulTerminated(settings.Callsign), PacketId);

        // CDRMTransmitter::Init order: MLC parameters, then the source encoder (DataEncoder → slide show).
        _slideShow.Init(NumDecodedBitsMsc);
        InitialTransportId = _mot.TransportId;
        _packets.Init();
    }

    /// <summary>iNumDecodedBitsMSC.</summary>
    public int NumDecodedBitsMsc { get; }

    /// <summary>PacLen, data-field bytes per packet (CDrmTransmitter.cpp:95-98).</summary>
    public int PacketLength { get; }

    /// <summary>Transport ID of the first pass (txTransportID after Init).</summary>
    public int InitialTransportId { get; }

    /// <summary>Transport ID now; for a BSR it advances at end of transmission.</summary>
    public int CurrentTransportId => _mot.TransportId;

    public IReadOnlyList<byte[]> HeaderSegments => _mot.HeaderSegments;

    public IReadOnlyList<byte[]> BodySegments => _mot.BodySegments;

    /// <summary>The segment schedule; its length is QSSTV's <c>numTxFrames</c> (DABMOT.cpp:631).</summary>
    public IReadOnlyList<int> SegmentSchedule => _mot.SegmentList;

    /// <summary>Runs the transmission's source side to the end of transmission.</summary>
    public IEnumerable<DrmSourceFrame> Frames()
    {
        if (_started)
            throw new InvalidOperationException("A generator runs one transmission only.");
        _started = true;
        return Run();
    }

    private IEnumerable<DrmSourceFrame> Run()
    {
        int totalPacketBits = _packets.TotalPacketSize * 8;
        int packetsPerFrame = NumDecodedBitsMsc / totalPacketBits;
        for (int index = 0; ; index++)
        {
            // CAudioSourceEncoderImplementation::ProcessDataInternal (AudioSourceDecoder.cpp:40-71).
            var bits = new byte[NumDecodedBitsMsc];
            var packets = new byte[packetsPerFrame][];
            for (int j = 0; j < packetsPerFrame; j++)
            {
                byte[] packet = _packets.GeneratePacket();
                packets[j] = packet;
                int offset = j * totalPacketBits;
                for (int i = 0; i < packet.Length * 8; i++)
                    bits[offset + i] = (byte)((packet[i >> 3] >> (7 - (i & 7))) & 1);
            }

            if (_slideShow.StopRequested)
            {
                yield return new DrmSourceFrame(index, bits, packets, null);
                yield break;
            }

            yield return new DrmSourceFrame(index, bits, packets, _fac.Next(index % DrmGlobal.NumFramesInSuperframe));
        }
    }
}
