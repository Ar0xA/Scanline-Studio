namespace ScanlineStudio.Core.Drm.Util;

/// <summary>TX-side DRM CRC, port of QSSTV's CCRC (drmtx/common/util/CRC.cpp:38-121). MSB-first,
/// register initialised to all ones, result is the ones' complement. Degree 8 is the FAC CRC
/// (x^8+x^4+x^3+x^2+1, FAC.cpp:177); degree 16 is the data-packet/MOT CRC (x^16+x^12+x^5+1,
/// DataDecoder.cpp:131). Not thread-safe; one instance per running CRC.</summary>
internal sealed class DrmCrc
{
    // iPolynMask[degree - 1] (CRC.cpp:113-120); 0 marks degrees QSSTV leaves uninitialised.
    private static ReadOnlySpan<uint> PolynomialMasks =>
    [
        0,
        1u << 1,
        1u << 1,
        0,
        (1u << 1) | (1u << 2) | (1u << 4),
        (1u << 1) | (1u << 2) | (1u << 3) | (1u << 5),
        0,
        (1u << 2) | (1u << 3) | (1u << 4),
        0, 0, 0, 0, 0, 0, 0,
        (1u << 5) | (1u << 12),
    ];

    private uint _bitOutPosMask;
    private uint _polynomialMask;
    private uint _state;

    /// <summary>Starts a new CRC of the given degree (CRC.cpp:38-49). Valid degrees: 1, 2, 3, 5, 6, 8, 16.</summary>
    public void Reset(int degree)
    {
        if (degree is not (1 or 2 or 3 or 5 or 6 or 8 or 16))
            throw new ArgumentOutOfRangeException(nameof(degree), degree, "QSSTV defines CRC polynomials for degrees 1, 2, 3, 5, 6, 8 and 16 only.");

        _bitOutPosMask = 1u << degree;
        _polynomialMask = PolynomialMasks[degree - 1];
        _state = ~0u;
    }

    /// <summary>Adds 8 bits, MSB first (CRC.cpp:51-72).</summary>
    public void AddByte(byte value)
    {
        for (int i = 0; i < 8; i++)
            AddBit((value & (1 << (8 - i - 1))) != 0);
    }

    /// <summary>Adds one bit (CRC.cpp:74-92).</summary>
    public void AddBit(bool bit)
    {
        _state <<= 1;
        if ((_state & _bitOutPosMask) != 0)
            _state |= 1;
        if (bit)
            _state ^= 1;
        if ((_state & 1) != 0)
            _state ^= _polynomialMask;
    }

    /// <summary>Returns the CRC. Like QSSTV it complements the register in place, so a second call
    /// without <see cref="Reset"/> returns a different value (CRC.cpp:94-101).</summary>
    public uint GetCrc()
    {
        _state = ~_state;
        return _state & (_bitOutPosMask - 1);
    }

    /// <summary>CRC.cpp:103-109.</summary>
    public bool CheckCrc(uint crc) => crc == GetCrc();
}

/// <summary>RX-side DRM CRCs from QSSTV's Diorama-derived receiver.</summary>
internal static class RxCrc
{
    /// <summary>Port of drmrx/crc16_bytewise.cpp:91-126 (x^16+x^12+x^5+1, init 0xFFFF, MSB first).
    /// The last two bytes are fed inverted, so data followed by its TX CRC16 yields 0.
    /// QSSTV returns this as a double and reads before the buffer when N &lt; 2.
    /// Input too short to carry a CRC returns -1: never 0, so callers reject it as QSSTV does (sourcedecoder.cpp:238-251).</summary>
    public static int Crc16Bytewise(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return -1;

        const uint x = 0x1021;
        uint b = 0xFFFF;
        int n = data.Length;
        for (int i = 0; i < n - 2; i++)
        {
            for (int j = 7; j >= 0; j--)
            {
                uint y = ((b >> 15) + (uint)(data[i] >> j)) & 0x01;
                b = y == 1 ? (b << 1) ^ x : b << 1;
            }
        }

        for (int i = n - 2; i < n; i++)
        {
            for (int j = 7; j >= 0; j--)
            {
                uint y = (((b >> 15) + (uint)((data[i] >> j) & 0x01)) ^ 0x01) & 0x01;
                b = y == 1 ? (b << 1) ^ x : b << 1;
            }
        }

        return (int)(b & 0xFFFF);
    }

    /// <summary>Port of drmrx/crc8_c.cpp:13-31 (x^8+x^4+x^3+x^2+1, init 0xFF) over soft bits rounded
    /// with floor(v + 0.5). channeldecode.cpp:362-377 inverts the last 8 FAC bits first, so a valid
    /// block yields 0.</summary>
    public static int Crc8Bits(ReadOnlySpan<double> bits)
    {
        const uint x = 0x1D;
        uint b = 0xFF;
        for (int i = 0; i < bits.Length; i++)
        {
            uint y = unchecked((b >> 7) + (uint)(int)Math.Floor(bits[i] + 0.5)) & 0x01;
            b = y == 1 ? (b << 1) ^ x : b << 1;
        }

        return (int)(b & 0xFF);
    }
}
