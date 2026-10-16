using ScanlineStudio.Core.Drm.Util;

namespace ScanlineStudio.Core.Drm.Tx;

/// <summary>Port of QSSTV's <c>reedSolomonCoder::encode</c> and <c>distribute</c>
/// (utils/reedsolomoncoder.cpp:181-202,247-305): a 7-byte header, RS(255,k) blocks, then a
/// row/column interleave so a lost MOT segment spreads over many blocks.</summary>
internal static class RsFileEncoder
{
    /// <summary>Header size: surplus, chunk count (LE, 2 bytes), 3 extension bytes, a zero byte.</summary>
    public const int HeaderSize = 7;

    /// <summary>KK per RS type (rs.h:16-19).</summary>
    public static int DataSize(DrmReedSolomon rs) => rs switch
    {
        DrmReedSolomon.Rs1 => 224,
        DrmReedSolomon.Rs2 => 192,
        DrmReedSolomon.Rs3 => 160,
        DrmReedSolomon.Rs4 => 128,
        _ => throw new ArgumentOutOfRangeException(nameof(rs), rs, "No RS data size for this type."),
    };

    /// <summary>The MOT name extension for an RS-coded body: "rs1".."rs4" (reedsolomoncoder.cpp:13-20).</summary>
    public static string TypeExtension(DrmReedSolomon rs) => rs switch
    {
        DrmReedSolomon.Rs1 => "rs1",
        DrmReedSolomon.Rs2 => "rs2",
        DrmReedSolomon.Rs3 => "rs3",
        DrmReedSolomon.Rs4 => "rs4",
        _ => throw new ArgumentOutOfRangeException(nameof(rs), rs, "No extension for this type."),
    };

    /// <summary>Encodes a file. <paramref name="extension"/> is the original suffix as a Qt string;
    /// like <c>leftJustified(3, 0).toLatin1()</c> it is NUL-padded to 3 and only its first 3 bytes are kept.</summary>
    public static byte[] Encode(ReadOnlySpan<byte> payload, string extension, DrmReedSolomon rs)
    {
        ArgumentNullException.ThrowIfNull(extension);
        int dsize = DataSize(rs);
        var coder = new KarnReedSolomon(dsize);
        const int bsize = KarnReedSolomon.BlockSize;

        byte[] ext = DrmLatin1.Encode(extension.Length < 3 ? extension.PadRight(3, '\0') : extension);
        long got = payload.Length;
        long chunks = (got + HeaderSize) / dsize;
        if ((got + HeaderSize) % dsize > 0)
            chunks++;

        var ec = new byte[checked((int)(chunks * bsize))];
        ec[0] = unchecked((byte)(dsize - (got % dsize)));
        ec[1] = unchecked((byte)(chunks % 256));
        ec[2] = unchecked((byte)(chunks / 256));
        ec[3] = ext[0];
        ec[4] = ext[1];
        ec[5] = ext[2];
        ec[6] = 0;

        // QSSTV does not pad a first chunk shorter than k-7 bytes (reedsolomoncoder.cpp:284-286, UB); we zero-pad.
        int first = (int)Math.Min(got, dsize - HeaderSize);
        payload[..first].CopyTo(ec.AsSpan(HeaderSize));
        coder.Encode(ec.AsSpan(0, dsize), ec.AsSpan(dsize, bsize - dsize));

        for (int i = 1; i < chunks; i++)
        {
            int start = (i * dsize) - HeaderSize;
            int len = (int)Math.Min(dsize, got - start);
            Span<byte> block = ec.AsSpan(i * bsize, bsize);
            payload.Slice(start, len).CopyTo(block);
            coder.Encode(block[..dsize], block[dsize..]);
        }

        var output = new byte[ec.Length];
        Distribute(ec, output, (int)chunks, bsize, reverse: false);
        return output;
    }

    /// <summary><c>distribute</c> (reedsolomoncoder.cpp:181-202). Encode (reverse = false) writes
    /// source byte n of block r to column-major position; reverse undoes it.</summary>
    public static void Distribute(ReadOnlySpan<byte> src, Span<byte> dst, int rows, int cols, bool reverse)
    {
        int rc = rows * cols;
        int rl = reverse ? cols : rows;
        int ri = 0;
        for (int i = 0; i < rc; i += 64)
        {
            for (int j = 0; j < 64 && i + j < rc; j++)
            {
                dst[ri] = src[i + j];
                ri += rl;
                if (ri >= rc)
                    ri -= rc - 1;
            }
        }
    }
}
