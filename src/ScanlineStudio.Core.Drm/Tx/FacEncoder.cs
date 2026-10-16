using ScanlineStudio.Core.Drm.Tables;
using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Port of QSSTV's HamDRM FAC block <c>CFACTransmit::FACParam</c> (drmtx/common/FAC/FAC.cpp:39-187).
/// 48 bits per frame: identity (2), SO (1), interleaver (1), MSC mode (1), protection (1),
/// data flag (1), packet Id (2), extended MSC mode (1), 3 label characters (7 bits each), 9 bits
/// never written, CRC8 over bits 0-39. Not thread-safe.</summary>
internal sealed class FacEncoder
{
    private const int LabelEndBit = 31;
    private const int CrcStartBit = 40;

    // GenFACDataBuf's vector, reused every frame (CDrmTransmitter.h:89). The FAC MLC encoder disperses
    // it in place (MLC.cpp:48), so bits 31-39 alternate between 0 and the PRBS from frame to frame.
    private readonly byte[] _buffer = new byte[FacTables.NumFacBitsPerBlock];
    private readonly DrmTxSettings _settings;
    private readonly byte[] _label;
    private readonly int _packetId;

    /// <param name="label">On-air label bytes: <c>callsign.toLatin1().data()</c> (drmtransmitter.cpp:112).</param>
    public FacEncoder(DrmTxSettings settings, ReadOnlySpan<byte> label, int packetId)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _label = label.ToArray();
        _packetId = packetId;
    }

    /// <summary>The FAC bits (one per byte) for a frame with the given frame index in the super-frame (0-2).</summary>
    public byte[] Next(int frameIdInSuperframe)
    {
        if (frameIdInSuperframe is < 0 or >= DrmGlobal.NumFramesInSuperframe)
            throw new ArgumentOutOfRangeException(nameof(frameIdInSuperframe), frameIdInSuperframe, "Frame ID must be 0-2.");

        int pos = 0;
        Put(ref pos, frameIdInSuperframe switch { 0 => 3u, 1 => 1u, _ => 2u }, 2);
        Put(ref pos, _settings.Occupancy == SpectrumOccupancy.So1 ? 1u : 0u, 1);
        Put(ref pos, _settings.Interleave == DrmInterleave.Long ? 0u : 1u, 1);
        Put(ref pos, _settings.Qam == DrmQam.Qam64 ? 0u : 1u, 1);
        Put(ref pos, _settings.Protection == DrmProtection.Low ? 1u : 0u, 1);
        Put(ref pos, 1, 1);
        Put(ref pos, (uint)_packetId, 2);
        Put(ref pos, _settings.Qam == DrmQam.Qam4 ? 1u : 0u, 1);

        int labelLength = Math.Min(_label.Length, FacTables.MaxLabelLength);
        for (int i = 3 * frameIdInSuperframe; i < (3 * frameIdInSuperframe) + 3; i++)
        {
            uint c = i >= labelLength ? 0u : _label[i];
            Put(ref pos, c & 127, 7);
        }

        System.Diagnostics.Debug.Assert(pos == LabelEndBit, "FAC field layout drifted.");

        var crc = new DrmCrc();
        crc.Reset(FacTables.CrcDegree);
        for (int i = 0; i < CrcStartBit; i += 8)
        {
            int b = 0;
            for (int k = 0; k < 8; k++)
                b = (b << 1) | _buffer[i + k];
            crc.AddByte((byte)b);
        }

        pos = CrcStartBit;
        Put(ref pos, crc.GetCrc(), 8);

        byte[] output = (byte[])_buffer.Clone();
        EnergyDispersal.Apply(_buffer, 0);
        return output;
    }

    private void Put(ref int pos, uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
            _buffer[pos++] = (byte)((value >> i) & 1);
    }
}
