using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Port of QSSTV's packet-mode data encoder <c>CDataEncoder</c>
/// (drmtx/common/datadecoding/DataDecoder.cpp:36-173). Splits MSC data groups into fixed-size
/// packets: 1 header byte (First, Last, PacketId, PPI, CI), the data field, CRC16. Not thread-safe.</summary>
internal sealed class DataPacketEncoder
{
    private readonly int _packetLength;
    private readonly int _packetId;
    private readonly Func<byte[]> _nextDataUnit;
    private byte[] _dataUnit = [];
    private int _pointer;
    private int _continuityIndex;

    /// <param name="packetLength">Data field size in bytes: PacLen = MSC bits / 8 − 3 (CDrmTransmitter.cpp:95-98).</param>
    /// <param name="packetId">Packet Id, 2 bits; QSSTV uses 0 (Parameter.cpp:362).</param>
    /// <param name="nextDataUnit">Source of data groups (MOTSlideShowEncoder.GetDataUnit).</param>
    public DataPacketEncoder(int packetLength, int packetId, Func<byte[]> nextDataUnit)
    {
        ArgumentNullException.ThrowIfNull(nextDataUnit);
        if (packetLength < 1)
            throw new ArgumentOutOfRangeException(nameof(packetLength), packetLength, "Packet length must be positive.");
        _packetLength = packetLength;
        _packetId = packetId;
        _nextDataUnit = nextDataUnit;
    }

    /// <summary>Packet size in bytes including header and CRC.</summary>
    public int TotalPacketSize => _packetLength + 3;

    /// <summary>CDataEncoder::Init (DataDecoder.cpp:141-173): fetches the first data group.</summary>
    public void Init()
    {
        _dataUnit = _nextDataUnit();
        _pointer = 0;
        _continuityIndex = 0;
    }

    /// <summary>CDataEncoder::GeneratePacket (DataDecoder.cpp:36-139).</summary>
    public byte[] GeneratePacket()
    {
        int length = _packetLength;
        var packet = new byte[length + 3];
        int remain = _dataUnit.Length - _pointer;
        bool last = remain <= length;

        int header = 0;
        if (_pointer == 0)
            header |= 0x80;
        if (last)
            header |= 0x40;
        header |= (_packetId & 3) << 4;
        if (remain < length)
            header |= 0x08;
        header |= _continuityIndex;
        packet[0] = (byte)header;
        _continuityIndex = (_continuityIndex + 1) & 7;

        if (remain >= length)
        {
            _dataUnit.AsSpan(_pointer, length).CopyTo(packet.AsSpan(1));
            if (remain != length)
                _pointer += length;
        }
        else
        {
            // PPI: first byte is the useful byte count, 8 bits only, so it wraps above 255 (DataDecoder.cpp:101-102).
            packet[1] = unchecked((byte)remain);
            _dataUnit.AsSpan(_pointer, remain).CopyTo(packet.AsSpan(2));
        }

        if (last)
        {
            _dataUnit = _nextDataUnit();
            _pointer = 0;
        }

        // The CRC overwrites the last padding byte of a padded packet (DataDecoder.cpp:128-138).
        var crc = new DrmCrc();
        crc.Reset(16);
        for (int i = 0; i < length + 1; i++)
            crc.AddByte(packet[i]);
        uint value = crc.GetCrc();
        packet[length + 1] = (byte)(value >> 8);
        packet[length + 2] = (byte)value;
        return packet;
    }
}
