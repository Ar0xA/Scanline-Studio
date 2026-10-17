using System.Buffers.Binary;

namespace ScanlineStudio.Imaging.OpenJpeg;

public sealed partial class OpenJpegJpeg2000Codec
{
    private const int SizFixedLength = 38;
    private const int SizCsizOffset = 40;
    private const int SizComponentsOffset = 42;

    /// <summary>Applies the decode limits to the SIZ marker (ISO/IEC 15444-1 A.5.1) before OpenJPEG sees the
    /// data, so a hostile header cannot make <c>opj_read_header</c> allocate for a huge tile grid or image.</summary>
    private static void CheckSizLimits(ReadOnlySpan<byte> data, int format)
    {
        if (format == OpenJpegNative.CodecJp2)
            CheckPaletteLimits(data);
        ReadOnlySpan<byte> cs = format == OpenJpegNative.CodecJp2 ? FindCodestream(data) : data;
        if (cs.Length < SizComponentsOffset || !cs.StartsWith(J2kMagic))
            throw new InvalidDataException("The JPEG 2000 codestream does not start with SOC and SIZ.");

        int lsiz = BinaryPrimitives.ReadUInt16BigEndian(cs[4..]);
        uint xsiz = BinaryPrimitives.ReadUInt32BigEndian(cs[8..]);
        uint ysiz = BinaryPrimitives.ReadUInt32BigEndian(cs[12..]);
        uint xosiz = BinaryPrimitives.ReadUInt32BigEndian(cs[16..]);
        uint yosiz = BinaryPrimitives.ReadUInt32BigEndian(cs[20..]);
        uint xtsiz = BinaryPrimitives.ReadUInt32BigEndian(cs[24..]);
        uint ytsiz = BinaryPrimitives.ReadUInt32BigEndian(cs[28..]);
        uint xtosiz = BinaryPrimitives.ReadUInt32BigEndian(cs[32..]);
        uint ytosiz = BinaryPrimitives.ReadUInt32BigEndian(cs[36..]);
        int csiz = BinaryPrimitives.ReadUInt16BigEndian(cs[SizCsizOffset..]);

        if (csiz is 0 or > MaxDecodeComponents)
            throw new InvalidDataException($"JPEG 2000 image has {csiz} components; at most {MaxDecodeComponents} are accepted.");
        if (lsiz != SizFixedLength + (3 * csiz) || cs.Length < 4 + lsiz)
            throw new InvalidDataException("The JPEG 2000 SIZ marker is malformed.");

        long width = (long)xsiz - xosiz;
        long height = (long)ysiz - yosiz;
        if (width <= 0 || height <= 0 || width * height > MaxDecodePixels)
            throw new InvalidDataException($"JPEG 2000 image of {width}x{height} exceeds the {MaxDecodePixels} pixel limit.");

        if (xtsiz == 0 || ytsiz == 0 || xtosiz > xosiz || ytosiz > yosiz)
            throw new InvalidDataException("The JPEG 2000 tile grid is malformed.");
        // 128-bit, so a hostile grid cannot overflow the product past the check.
        UInt128 tiles = (UInt128)(ulong)CeilDiv((long)xsiz - xtosiz, xtsiz) * (ulong)CeilDiv((long)ysiz - ytosiz, ytsiz);
        if (tiles > (UInt128)MaxDecodeTiles)
            throw new InvalidDataException($"JPEG 2000 image has {tiles} tiles; at most {MaxDecodeTiles} are accepted.");

        for (int i = 0; i < csiz; i++)
        {
            ReadOnlySpan<byte> entry = cs.Slice(SizComponentsOffset + (3 * i), 3);
            if (entry[1] == 0 || entry[2] == 0)
                throw new InvalidDataException("The JPEG 2000 SIZ marker has a zero subsampling factor.");
        }
    }

    /// <summary>The contents of the first top-level "jp2c" box (ISO/IEC 15444-1 I.4). A jp2c length past the end of the
    /// file is clamped to the file, as OpenJPEG never checks it (jp2.c:2290-2294) and decodes to EOF.</summary>
    private static ReadOnlySpan<byte> FindCodestream(ReadOnlySpan<byte> data)
    {
        int at = 0;
        while (TryReadBox(data, at, clampLength: true, out ReadOnlySpan<byte> type, out ReadOnlySpan<byte> content, out int next))
        {
            if (type.SequenceEqual("jp2c"u8))
                return content;
            at = next;
        }

        throw new InvalidDataException("The JP2 file has no readable codestream box.");
    }

    /// <summary>Refuses a palette (pclr at the top level or in jp2h, before jp2c) that expands to more channels than <see cref="MaxDecodeComponents"/>:
    /// opj_jp2_apply_pclr allocates NPC × width × height samples inside opj_decode (jp2.c:1073-1101), past every
    /// other limit here.</summary>
    private static void CheckPaletteLimits(ReadOnlySpan<byte> data)
    {
        int at = 0;
        while (TryReadBox(data, at, clampLength: false, out ReadOnlySpan<byte> type, out ReadOnlySpan<byte> jp2h, out int next))
        {
            // OpenJPEG's header pass stops at the codestream (jp2.c:2290-2294); a pclr after it is never applied.
            if (type.SequenceEqual("jp2c"u8))
                return;

            // A top-level pclr is read too, with only a warning (jp2.c:2314-2325).
            CheckPalette(type, jp2h);
            if (type.SequenceEqual("jp2h"u8))
            {
                int child = 0;
                while (TryReadBox(jp2h, child, clampLength: false, out ReadOnlySpan<byte> childType, out ReadOnlySpan<byte> content, out int childNext))
                {
                    CheckPalette(childType, content);
                    child = childNext;
                }
            }

            at = next;
        }
    }

    // pclr: NE (2 bytes), NPC (1 byte), ... (ISO/IEC 15444-1 I.5.3.4).
    private static void CheckPalette(ReadOnlySpan<byte> type, ReadOnlySpan<byte> content)
    {
        if (type.SequenceEqual("pclr"u8) && content.Length >= 3 && content[2] > MaxDecodeComponents)
            throw new InvalidDataException($"JPEG 2000 palette has {content[2]} channels; at most {MaxDecodeComponents} are accepted.");
    }

    /// <summary>One box header at <paramref name="at"/> with OpenJPEG's length rules (jp2.c:466-525): 0 = to the end,
    /// 1 = a 64-bit XLBox follows. False when no complete box header is left or the length is invalid.</summary>
    private static bool TryReadBox(ReadOnlySpan<byte> data, int at, bool clampLength, out ReadOnlySpan<byte> type,
        out ReadOnlySpan<byte> content, out int next)
    {
        type = default;
        content = default;
        next = 0;
        if (at < 0 || data.Length - at < 8)
            return false;
        ulong length = BinaryPrimitives.ReadUInt32BigEndian(data[at..]);
        type = data.Slice(at + 4, 4);
        int header = 8;
        if (length == 1)
        {
            if (data.Length - at < 16)
                return false;
            length = BinaryPrimitives.ReadUInt64BigEndian(data[(at + 8)..]);
            header = 16;
        }
        else if (length == 0)
        {
            length = (ulong)(data.Length - at);
        }

        if (length < (ulong)header)
            return false;
        if (length > (ulong)(data.Length - at))
        {
            if (!clampLength)
                return false;
            length = (ulong)(data.Length - at);
        }

        content = data.Slice(at + header, (int)length - header);
        next = at + (int)length;
        return true;
    }

    private static long CeilDiv(long value, uint divisor) => (value + divisor - 1) / divisor;
}
