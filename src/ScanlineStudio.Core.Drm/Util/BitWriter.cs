namespace ScanlineStudio.Core.Drm.Util;

/// <summary>MSB-first bit packing into bytes, like QSSTV's <c>CVector::Enqueue</c> (utils/vector.h:166-180):
/// only the low <c>bits</c> bits of the value are written.</summary>
internal ref struct BitWriter
{
    private readonly Span<byte> _buffer;

    public BitWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        BitPosition = 0;
    }

    public int BitPosition { get; private set; }

    public void Write(uint value, int bits)
    {
        for (int i = bits - 1; i >= 0; i--)
        {
            int pos = BitPosition++;
            int mask = 0x80 >> (pos & 7);
            if (((value >> i) & 1) != 0)
                _buffer[pos >> 3] |= (byte)mask;
            else
                _buffer[pos >> 3] &= (byte)~mask;
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            Write(b, 8);
    }
}
