using System.Text;

namespace ScanlineStudio.Application.Tests;

/// <summary>Hand-authored `.mtm`-format byte-array WRITER for <c>LegacyMtmReaderTests</c> -- built
/// independently from <c>LegacyMtmReader</c>/<c>MtmCursor</c> (no shared code) so a test using this
/// can't pass merely because both sides share the same bug. Every field written here is authored
/// fresh for this test suite, never copied from a real legacy sample -- this project's own standing
/// rule against committing legacy-sourced literal content as test fixtures (licensing, see
/// `BACKLOG.md`'s TT1-13/PA-Backfill history and `ui_transition_plan.md`'s step 14 record).</summary>
internal sealed class MtmFixtureBuilder
{
    private readonly List<byte> _bytes = [];

    public byte[] ToArray() => [.. _bytes];

    public MtmFixtureBuilder Int32(int value)
    {
        _bytes.AddRange(BitConverter.GetBytes(value));
        return this;
    }

    public MtmFixtureBuilder SignedByte(sbyte value)
    {
        _bytes.Add(unchecked((byte)value));
        return this;
    }

    /// <summary>Writes an RGB triple as the wire's `0x00BBGGRR` `TColor` -- the inverse of
    /// <c>MtmCursor.ReadColor</c>'s R/B swap.</summary>
    public MtmFixtureBuilder Color(byte r, byte g, byte b) => Int32(r | (g << 8) | (b << 16));

    /// <summary>Length-prefixed, CP932-encoded, no null terminator -- <c>CDraw::SaveString</c>'s own
    /// wire shape.</summary>
    public MtmFixtureBuilder Str(string value)
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var cp932 = Encoding.GetEncoding(932);
        var encoded = cp932.GetBytes(value);
        Int32(encoded.Length);
        _bytes.AddRange(encoded);
        return this;
    }

    public MtmFixtureBuilder Bytes(byte[] raw)
    {
        _bytes.AddRange(raw);
        return this;
    }

    public MtmFixtureBuilder Bytes(int rawLength)
    {
        _bytes.AddRange(new byte[rawLength]);
        return this;
    }

    /// <summary>Every element's leading fields (`CDraw`'s own base record) -- version, four bounds
    /// ints, color, the signed 1-byte line style, then the box-style/line-width lookahead.
    /// <paramref name="withBoxStyleMagic"/> exercises the OTHER lookahead branch: when true, writes
    /// the `0x55aaNNNN` magic DWORD, then `boxStyle`, then `lineWidth` as two further ints; when
    /// false, writes a single DWORD that IS `lineWidth` (no magic, `boxStyle` implicitly 0).</summary>
    public MtmFixtureBuilder BaseFields(
        int version, int x1, int y1, int x2, int y2, (byte R, byte G, byte B) lineColor,
        sbyte lineStyle, int lineWidth, bool withBoxStyleMagic = false, int boxStyle = 0)
    {
        Int32(version);
        Int32(x1);
        Int32(y1);
        Int32(x2);
        Int32(y2);
        Color(lineColor.R, lineColor.G, lineColor.B);
        SignedByte(lineStyle);
        if (withBoxStyleMagic)
        {
            Int32(unchecked((int)0x55aa0000) | (boxStyle & 0xFFFF));
            Int32(boxStyle);
            Int32(lineWidth);
        }
        else
        {
            Int32(lineWidth);
        }

        return this;
    }

    /// <summary>Writes a minimal but genuinely valid 1x1 24bpp BMP, wrapped in the width/height
    /// prefix <c>CDraw::SaveBitmap</c> uses. Real enough for a standard BMP decoder to accept, not
    /// merely a plausible-looking byte count.</summary>
    public MtmFixtureBuilder Bitmap()
    {
        const int width = 1;
        const int height = 1;
        const int fileHeaderSize = 14;
        const int infoHeaderSize = 40;
        const int pixelDataSize = 4; // 1x1 24bpp, row padded to a 4-byte boundary
        var totalSize = fileHeaderSize + infoHeaderSize + pixelDataSize;

        Int32(width);
        Int32(height);
        _bytes.Add((byte)'B');
        _bytes.Add((byte)'M');
        _bytes.AddRange(BitConverter.GetBytes((uint)totalSize));
        _bytes.AddRange(new byte[4]); // reserved
        _bytes.AddRange(BitConverter.GetBytes((uint)(fileHeaderSize + infoHeaderSize))); // pixel data offset

        _bytes.AddRange(BitConverter.GetBytes(infoHeaderSize));
        _bytes.AddRange(BitConverter.GetBytes(width));
        _bytes.AddRange(BitConverter.GetBytes(height));
        _bytes.AddRange(BitConverter.GetBytes((ushort)1)); // planes
        _bytes.AddRange(BitConverter.GetBytes((ushort)24)); // bpp
        _bytes.AddRange(new byte[4]); // compression = BI_RGB
        _bytes.AddRange(BitConverter.GetBytes(pixelDataSize));
        _bytes.AddRange(new byte[16]); // resolution + palette counts, all 0

        _bytes.AddRange([255, 0, 0, 0]); // one BGR pixel (red) + 1 padding byte

        return this;
    }

    /// <summary>Wraps <paramref name="writeElement"/>'s output with its own leading `CM_*` tag, then
    /// appends the whole thing -- mirrors how <c>CDrawGroup::LoadFromStream</c> reads the tag itself
    /// before dispatching to the concrete class's own <c>LoadFromStream</c>.</summary>
    public static byte[] Element(int tag, byte[] body)
    {
        var builder = new MtmFixtureBuilder();
        builder.Int32(tag);
        builder.Bytes(body);
        return builder.ToArray();
    }

    /// <summary>Builds a complete top-level file: the `CM_GROUP` tag, the group's own base record and
    /// trans/size fields, an element count, then each child's bytes concatenated in order.</summary>
    public static byte[] Group(
        int version, int sx, int sy, IReadOnlyList<byte[]> children,
        int transX = 0, int transY = 0, (byte R, byte G, byte B)? transCol = null)
    {
        var builder = new MtmFixtureBuilder();
        builder.Int32(1); // CM_GROUP
        builder.BaseFields(version, 0, 0, 0, 0, (0, 0, 0), lineStyle: 0, lineWidth: 1);

        if (version >= 1)
        {
            builder.Int32(transX);
            builder.Int32(transY);
            var col = transCol ?? (255, 0, 255);
            builder.Color(col.R, col.G, col.B);
        }

        if (version >= 2)
        {
            builder.Int32(sx);
            builder.Int32(sy);
        }

        builder.Int32(children.Count);
        foreach (var child in children)
        {
            builder.Bytes(child);
        }

        return builder.ToArray();
    }
}
