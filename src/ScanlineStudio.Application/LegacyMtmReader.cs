using System.Buffers.Binary;
using System.Text;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application;

/// <summary>Thrown when a `.mtm`/`.mti` byte stream does not match the format `docs/mtm-binary-format.md`
/// documents -- a genuinely corrupt/truncated file, an attacker-controlled length field that would
/// otherwise drive an oversized allocation, or (via <see cref="LegacyMtmOleNotImportableException"/>)
/// a `CM_OLE` element, whose payload has no length prefix and cannot be skipped without decoding
/// VCL's own `TOleContainer` stream format (not available to this project). Every throw site in
/// <see cref="LegacyMtmReader"/> uses one of these two types, never a raw <see cref="Exception"/> or a
/// BCL type like <see cref="IndexOutOfRangeException"/> that would read as a bug in the parser rather
/// than a fact about the input file.</summary>
public class LegacyMtmFormatException(string message) : Exception(message);

/// <summary>`CM_OLE` (`CDrawOle`) has no length prefix on its payload -- `Draw.cpp:3922`'s
/// `pContainer->SaveToStream(sp)` is VCL's own `TOleContainer` compound-stream format, implemented
/// inside the VCL library itself, not in any source this project has access to. Once this tag is
/// seen, the read position inside the element list is unrecoverable without decoding that format, so
/// the only source-verifiable choice is to reject the WHOLE file rather than attempt to skip past it
/// -- see `docs/mtm-binary-format.md`'s `CDrawOle` section for the full reasoning.</summary>
public sealed class LegacyMtmOleNotImportableException() : LegacyMtmFormatException(
    "This template contains an OLE-embedded object (legacy's CM_OLE), which cannot be imported -- "
    + "its stream format is internal to Borland VCL and not available to this project.");

/// <summary>One `CDraw`'s common leading fields, read by <see cref="LegacyMtmReader"/> for every
/// element kind before that kind's own additional fields (if any). <see cref="LineColor"/> is already
/// R/G/B-swapped from the wire's `0x00BBGGRR` `TColor` convention. <see cref="LineStyle"/> is the
/// signed on-disk byte widened to <see langword="int"/> -- NEGATIVE means "no line at all" (VCL's
/// `RoundRect`/`Draw` early-return on `m_LineStyle &lt; 0`, confirmed against `Draw.cpp` and the
/// signedness settled by a `yoniq-principal` review: the byte and its intended meaning are the same
/// regardless of how Borland's compiler happened to type the enum, so this port reads the SOURCE'S
/// intent, not a guess at legacy's own binary behavior).</summary>
internal sealed record MtmBaseFields(
    int Version, int X1, int Y1, int X2, int Y2, Rgb24 LineColor, int LineStyle, int BoxStyle, int LineWidth);

/// <summary>A standalone BMP blob exactly as `TBitmap::SaveToStream` wrote it (14-byte
/// `BITMAPFILEHEADER` starting `"BM"`, its own `bfSize` total-length field, then a DIB header,
/// optional palette, and pixel data) -- self-describing and decodable by any standard BMP reader.
/// Decoding is deliberately NOT this reader's job (see this class's own top-of-file doc comment: pure
/// parsing only) -- <c>LegacyMtmImportAdapter</c> owns turning this into a template asset file.</summary>
internal sealed record MtmBitmap(byte[] Bytes);

internal enum MtmShapeKind { Line, Box, BoxS }

internal abstract record MtmElement;

/// <summary>`CM_LINE`/`CM_BOX`/`CM_BOXS` share byte-identical wire records with the base `CDraw`
/// record -- confirmed by reading all three classes' full bodies in `Draw.h`: none declares an extra
/// member or its own `SaveToStream`/`LoadFromStream` override. <see cref="Kind"/> is the only thing
/// that distinguishes them, carried from the `CM_*` tag the PARENT read to select this record's
/// concrete mapping in <c>LegacyMtmImportAdapter</c> (an outline-only box, a filled box, or -- with no
/// modern equivalent besides a box -- a line).</summary>
internal sealed record MtmShapeElement(MtmBaseFields Base, MtmShapeKind Kind) : MtmElement;

/// <summary><see cref="Type"/>: 0 = flat `Col1` fill, 1 = 2-stop gradient (`Col1`-&gt;`Col2`,
/// `ColVert` selects axis), 2 = 3-band 4-stop gradient (`Col1`-&gt;`Col2`-&gt;`Col3`-&gt;`Col4`), 3 =
/// image-backed (<see cref="Bitmap"/>), 4 = a per-character greyscale scanline pattern keyed off
/// <see cref="Sound"/> (`Draw.cpp:1217-1230`, the app's own <c>_ft[]</c> table). <see cref="ColVert"/>
/// defaults to 0 (Horizontal) when absent (`m_Ver &lt; 2`); <see cref="Sound"/> is only present on the
/// wire when `m_Ver &gt;= 3 &amp;&amp; Type == 4` -- its legacy default when absent for an OLDER
/// `Type == 4` record is the literal string `"1356865313568888"` (`Draw.cpp:1051`'s ctor default),
/// which <c>LegacyMtmImportAdapter</c> substitutes when this is <see langword="null"/> in that
/// specific case, not this reader (a mapping default, not a wire fact).</summary>
internal sealed record MtmTitleElement(
    MtmBaseFields Base, int Type, int ColVert, Rgb24 Col1, Rgb24 Col2, Rgb24 Col3, Rgb24 Col4,
    string? Sound, MtmBitmap? Bitmap) : MtmElement;

/// <summary><see cref="Text"/> may contain unresolved legacy macro tokens (`%m`/`%c`/etc., confirmed
/// present in every real local sample) -- left exactly as read; token translation is
/// <c>LegacyMtmImportAdapter</c>'s job, not this reader's. <see cref="FontHeightOrSize"/> is the raw
/// signed `int` legacy wrote: legacy always saves it negative (`Draw.cpp:3130-3132`'s
/// `if (d &gt;= 0) d = -d;` before writing) so `|value|` is the font's em size in pixels; a real save
/// is never positive, but this reader does not enforce that, it only transcribes the byte. Font style
/// bits (`FSBOLD`=1/`FSITALIC`=2/`FSUNDERLINE`=4/`FSSTRIKEOUT`=8, `ComLib.h:66-69`) are left packed in
/// <see cref="FontStyleCode"/> for the adapter to unpack -- interpreting which bits the modern model
/// can represent is a mapping decision.</summary>
internal sealed record MtmTextElement(
    MtmBaseFields Base, int Grade, int Shadow, bool HasStack, int Vert, int PerSpect, int RightAdj,
    string Text, Rgb24 Col1, Rgb24 Col2, Rgb24 Col3, Rgb24 Col4, Rgb24 ColS, Rgb24? ColB,
    string FontFamily, int FontCharset, int FontHeightOrSize, int FontStyleCode,
    MtmBitmap? BrushBitmap) : MtmElement;

/// <summary><see cref="Type"/> `== 0` means "no image loaded" (confirmed: every real local
/// `CM_PIC` sample is exactly this) -- <see cref="Bitmap"/> is <see langword="null"/> in that case,
/// never an empty placeholder. <see cref="Polygon"/> is read whenever <see cref="Shape"/> `== 5`,
/// REGARDLESS of <see cref="Type"/> (`Draw.cpp:3779-3785` reads it unconditionally on that gate alone
/// -- a `Type == 0` picture with `Shape == 5` still carries polygon bytes on the wire that must be
/// consumed to stay byte-synced with whatever record follows).</summary>
internal sealed record MtmPictureElement(
    MtmBaseFields Base, int Type, int Shape, MtmBitmap? Bitmap, MtmPolygon? Polygon) : MtmElement;

/// <summary>The polygon shape record embedded in a <see cref="MtmPictureElement"/> when
/// <c>Shape == 5</c>. <see cref="Points"/> are ALREADY rescaled into a 320x256 space by this reader,
/// using `y * 256 / YW` -- the CORRECT rescale, not legacy's own `y * 256 / XW` bug
/// (`Draw.cpp:5417-5459`, a source-confirmed defect: the Y-scale line divides by the wrong variable).
/// §0a permits fixing this deliberately (off-air, never wire-observable), and it costs nothing here
/// since nothing in the modern element model consumes polygon-shaped picture clipping at all yet --
/// <c>LegacyMtmImportAdapter</c> reports this shape as unsupported rather than approximating it, per
/// plan-review round 3.</summary>
internal sealed record MtmPolygon(IReadOnlyList<(int X, int Y)> Points);

/// <summary>`CM_LIB` (`CDrawLib`) -- an opaque `CItems`-plugin blob, format unknown and out of scope,
/// but cleanly skippable because of its own explicit length prefix (unlike `CM_OLE`). This reader
/// already consumed and discarded the payload bytes to stay byte-synced; only <see cref="Name"/>
/// survives for <c>LegacyMtmImportAdapter</c>'s "unsupported element dropped" report.</summary>
internal sealed record MtmLibElement(string Name) : MtmElement;

/// <summary>Top-level file = one of these (`CM_GROUP`), and a group can nest others recursively
/// (structurally supported by this reader; no local sample exercises it). <see cref="Sx"/>/
/// <see cref="Sy"/> default to 320/256 when `m_Ver &lt; 2` leaves them unwritten -- never 0, so a
/// caller normalizing coordinates by these never divides by zero. <see cref="TransX"/>/
/// <see cref="TransY"/>/<see cref="TransCol"/> are cosmetic editor-UI state (where the group's own
/// resize handle last sat), not needed for a faithful import of element content, but transcribed
/// anyway since they cost nothing to carry.</summary>
internal sealed record MtmGroupElement(
    MtmBaseFields Base, int TransX, int TransY, Rgb24 TransCol, int Sx, int Sy,
    IReadOnlyList<MtmElement> Children) : MtmElement;

/// <summary>Pure, byte-accurate parser for legacy YONIQ/MMSSTV's `.mtm`/`.mti` template format --
/// `docs/mtm-binary-format.md` is the authoritative spec this class implements field-for-field, and
/// every design choice below (what's read, in what order, under what version gate) traces to a line
/// cited there. Deliberately does NO interpretation beyond objective wire-format facts (CP932 string
/// decode, the `0x00BBGGRR`-&gt;RGB color swap, the signed line-style byte, the polygon Y-scale
/// correction) -- anything that is a DECISION about how legacy content becomes a modern template
/// element (font substitution, macro-token rewriting, box fill/border rules, gradient approximation)
/// belongs to <c>LegacyMtmImportAdapter</c>, not here (plan-review's settled split, round 1).
/// <para><b>Hardened against a hostile or corrupt file</b> (plan-review blocker 5): every
/// stream-read count that drives an allocation or a loop -- element count, polygon point count,
/// string length, embedded-bitmap size -- is validated against the bytes actually remaining before
/// use. A violation throws <see cref="LegacyMtmFormatException"/>, never an
/// <see cref="OutOfMemoryException"/>, a hang, or a BCL exception that reads as this parser's own bug
/// rather than a fact about the input.</para></summary>
internal static class LegacyMtmReader
{
    // Draw.h:79-90, sequential from 0.
    private const int CmSelect = 0;
    private const int CmGroup = 1;
    private const int CmLine = 2;
    private const int CmBox = 3;
    private const int CmText = 4;
    private const int CmPic = 5;
    private const int CmBoxS = 6;
    private const int CmTitle = 7;
    private const int CmOle = 8;
    private const int CmLib = 9;

    // Draw.cpp:437-447's box-style/line-width lookahead: the DWORD just read is the magic marker
    // only when its TOP 16 bits equal this value -- the low 16 bits carry m_BoxStyle's own low bits
    // per the source, but only the high word is ever tested back on load.
    private const int BoxStyleMagicHighWord = 0x55aa;

    private const int DefaultGroupWidth = 320;
    private const int DefaultGroupHeight = 256;

    /// <summary>Entry point. <paramref name="data"/> is the WHOLE file's bytes -- `.mtm`/`.mti` files
    /// are KB-scale (every real local sample is under 1KB), so reading the whole thing into memory
    /// before parsing is simpler and exactly as safe as streaming it, and it's what makes the
    /// remaining-length hardening checks below trivial (a plain index comparison, not a `Stream`
    /// abstraction's own `CanSeek`/`Length` caveats).</summary>
    public static MtmGroupElement ReadTemplate(byte[] data) => ReadTemplate(data, out _);

    /// <summary>Same as <see cref="ReadTemplate(byte[])"/>, additionally reporting how many bytes
    /// were left unread after the top-level group finished parsing -- exposed only for
    /// <c>LegacyMtmRealSampleTests</c>'s own "does this reach a byte-exact clean end of file"
    /// validation (the strongest evidence available that this reader's field layout is actually
    /// correct, not merely plausible -- the same check the reference Python parser used during the
    /// original reverse-engineering pass). Not part of the format's own contract -- legacy's real
    /// `LoadFromStream` never checks for or rejects trailing bytes, so this reader doesn't either;
    /// it only makes the fact visible to a caller who wants to assert on it.</summary>
    public static MtmGroupElement ReadTemplate(byte[] data, out int bytesRemainingAfterParse)
    {
        var cursor = new MtmCursor(data);
        var command = cursor.ReadInt32();
        if (command != CmGroup)
        {
            throw new LegacyMtmFormatException(
                $"Expected a top-level CM_GROUP tag (1), found {command} -- this is not a .mtm/.mti file.");
        }

        var group = ReadGroup(cursor);
        bytesRemainingAfterParse = cursor.Remaining;
        return group;
    }

    private static MtmGroupElement ReadGroup(MtmCursor cursor)
    {
        var baseFields = ReadBaseFields(cursor);

        var transX = 0;
        var transY = 0;
        var transCol = default(Rgb24);
        if (baseFields.Version >= 1)
        {
            transX = cursor.ReadInt32();
            transY = cursor.ReadInt32();
            transCol = cursor.ReadColor();
        }

        var sx = DefaultGroupWidth;
        var sy = DefaultGroupHeight;
        if (baseFields.Version >= 2)
        {
            sx = cursor.ReadInt32();
            sy = cursor.ReadInt32();
        }

        var count = cursor.ReadCount();
        var children = new List<MtmElement>(count);
        for (var i = 0; i < count; i++)
        {
            var tag = cursor.ReadInt32();
            children.Add(ReadElement(cursor, tag));
        }

        return new MtmGroupElement(baseFields, transX, transY, transCol, sx, sy, children);
    }

    private static MtmElement ReadElement(MtmCursor cursor, int tag) => tag switch
    {
        CmGroup => ReadGroup(cursor),
        CmLine => ReadShape(cursor, MtmShapeKind.Line),
        CmBox => ReadShape(cursor, MtmShapeKind.Box),
        CmBoxS => ReadShape(cursor, MtmShapeKind.BoxS),
        CmText => ReadText(cursor),
        CmPic => ReadPicture(cursor),
        CmTitle => ReadTitle(cursor),
        CmOle => ReadOle(cursor),
        CmLib => ReadLib(cursor),
        CmSelect => throw new LegacyMtmFormatException(
            "CM_SELECT (0) is an in-memory-only selection marker and never appears in a real stream."),
        _ => throw new LegacyMtmFormatException($"Unrecognized element tag {tag}."),
    };

    // Draw.cpp:408 (Save) / :426 (Load) -- every element's leading fields.
    private static MtmBaseFields ReadBaseFields(MtmCursor cursor)
    {
        var version = cursor.ReadInt32();
        var x1 = cursor.ReadInt32();
        var y1 = cursor.ReadInt32();
        var x2 = cursor.ReadInt32();
        var y2 = cursor.ReadInt32();
        var lineColor = cursor.ReadColor();

        // Draw.h:117 TPenStyle m_LineStyle -- 1 byte (Borland's -b- byte-sized-enum switch,
        // Mmsstv.bpr:60), read SIGNED (yoniq-principal: negative means "no line at all", four
        // separate < 0 guards in Draw.cpp plus a real, user-reachable "No line" UI menu entry backed
        // by LineTable[0] == -1 -- the byte and its intended meaning are the same regardless of how
        // legacy's own compiler happened to type the enum, so this port reads the SOURCE's intent).
        var lineStyle = cursor.ReadSignedByteAsInt();

        // Draw.cpp:437-447 -- box-style/line-width magic-number lookahead, NOT a version gate or a
        // length prefix. Read one DWORD unconditionally; if its top 16 bits are the magic value,
        // two more ints follow (boxStyle, then lineWidth); otherwise the DWORD just read IS
        // lineWidth, and boxStyle is 0.
        int boxStyle;
        int lineWidth;
        var lookahead = cursor.ReadInt32();
        if (((uint)lookahead >> 16) == BoxStyleMagicHighWord)
        {
            boxStyle = cursor.ReadInt32();
            lineWidth = cursor.ReadInt32();
        }
        else
        {
            boxStyle = 0;
            lineWidth = lookahead;
        }

        return new MtmBaseFields(version, x1, y1, x2, y2, lineColor, lineStyle, boxStyle, lineWidth);
    }

    // CDrawLine/CDrawBox/CDrawBoxS -- Draw.h confirms zero extra member fields and no
    // SaveToStream/LoadFromStream override on any of the three: byte-identical to the base record.
    private static MtmShapeElement ReadShape(MtmCursor cursor, MtmShapeKind kind) =>
        new(ReadBaseFields(cursor), kind);

    // CDrawTitle -- Draw.cpp:1337 (save) / :1354 (load).
    private static MtmTitleElement ReadTitle(MtmCursor cursor)
    {
        var baseFields = ReadBaseFields(cursor);

        // m_Ver < 1: Draw.cpp:1359 -- Type/ColVert/every color are absent from the wire entirely.
        // Not "defaulted" in the sense of a missing-field convention this reader invents: legacy's
        // own CDrawTitle constructor (Draw.cpp:1038-1056) sets these exact values, sourced from the
        // app's [DrawBar] ini settings at the time -- yoniq-principal pinned them as the only
        // source-determinable answer (a m_Ver<1 title's real rendered color is otherwise a runtime
        // user setting with no file-level record). Treated as a reader-level default, the same
        // category as Sx/Sy defaulting to 320x256 below, not a mapping DECISION for the adapter.
        if (baseFields.Version < 1)
        {
            return new MtmTitleElement(
                baseFields, Type: 1, ColVert: 0,
                Col1: new Rgb24(0, 0, 0), Col2: new Rgb24(240, 240, 240),
                Col3: new Rgb24(255, 0, 0), Col4: new Rgb24(0, 128, 0),
                Sound: null, Bitmap: null);
        }

        var type = cursor.ReadInt32();

        // m_Ver >= 2 gate on ColVert specifically (Draw.cpp:1361) -- a m_Ver == 1 title has Type and
        // all four colors on the wire but NOT ColVert; reading it unconditionally would desync the
        // four color reads that follow.
        var colVert = baseFields.Version >= 2 ? cursor.ReadInt32() : 0;

        var col1 = cursor.ReadColor();
        var col2 = cursor.ReadColor();
        var col3 = cursor.ReadColor();
        var col4 = cursor.ReadColor();

        string? sound = null;
        if (baseFields.Version >= 3 && type == 4)
        {
            sound = cursor.ReadString();
        }

        MtmBitmap? bitmap = null;
        if (type == 3)
        {
            bitmap = cursor.ReadBitmap();
        }

        return new MtmTitleElement(baseFields, type, colVert, col1, col2, col3, col4, sound, bitmap);
    }

    // CDrawText -- Draw.cpp:3097 (save) / :3143 (load). The most version-layered record (7 revisions).
    private static MtmTextElement ReadText(MtmCursor cursor)
    {
        var baseFields = ReadBaseFields(cursor);

        // Code-review finding: CDrawText::LoadFromStream forces m_X2 = 0 for m_Ver < 4
        // (Draw.cpp:3161-3166) -- the base record's own X2 read above is still bytes-correct (every
        // element's base record has the same fixed shape), but legacy discards that value in memory
        // for an old-format text record. LegacyMtmImportAdapter's degenerate-rect fallback exists
        // specifically for this case, so the reader must actually produce it, not just document it.
        if (baseFields.Version < 4)
        {
            baseFields = baseFields with { X2 = 0 };
        }

        var grade = cursor.ReadInt32();
        var shadow = cursor.ReadInt32();

        if (baseFields.Version >= 1)
        {
            cursor.ReadInt32(); // m_Zero -- read to stay byte-synced, value unused on load
        }

        if (baseFields.Version >= 2)
        {
            cursor.ReadInt32(); // m_Rot -- not carried into MtmTextElement (rotation isn't
                                 // consumed by the modern text-geometry mapping this reader feeds)
            cursor.ReadInt32(); // m_X
            cursor.ReadInt32(); // m_Y
        }

        var rightAdj = baseFields.Version >= 4 ? cursor.ReadInt32() : 0;

        var hasStack = false;
        if (baseFields.Version >= 5)
        {
            hasStack = cursor.ReadInt32() != 0; // m_Stack
            cursor.ReadInt32(); // m_StackPara -- not consumed by the adapter's approximation
        }

        var perSpect = baseFields.Version >= 5 ? cursor.ReadInt32() : 0;
        if (baseFields.Version >= 5 && perSpect != 0)
        {
            cursor.ReadBytes(10 * sizeof(double)); // SPERSPECT, 80 bytes -- perspective is a stated
                                                     // non-goal for imported text (spec/15), skipped
                                                     // whole rather than field-decoded.
        }

        var vert = 0;
        if (baseFields.Version >= 6)
        {
            vert = cursor.ReadInt32(); // m_Vert
            cursor.ReadInt32(); // m_VertH
        }

        var text = cursor.ReadString();
        var col1 = cursor.ReadColor();
        var col2 = cursor.ReadColor();
        var col3 = cursor.ReadColor();
        var col4 = cursor.ReadColor();
        var colS = cursor.ReadColor();
        Rgb24? colB = baseFields.Version >= 3 ? cursor.ReadColor() : null;

        var fontFamily = cursor.ReadString();
        var fontCharset = cursor.ReadInt32();
        var fontHeightOrSize = cursor.ReadInt32();
        var fontStyleCode = cursor.ReadInt32();
        cursor.ReadInt32(); // dummy -- written/read, value unused on load

        MtmBitmap? brushBitmap = grade == 3 ? cursor.ReadBitmap() : null;

        return new MtmTextElement(
            baseFields, grade, shadow, hasStack, vert, perSpect, rightAdj,
            text, col1, col2, col3, col4, colS, colB,
            fontFamily, fontCharset, fontHeightOrSize, fontStyleCode, brushBitmap);
    }

    // CDrawPic -- Draw.cpp:3729 (save) / :3746 (load).
    private static MtmPictureElement ReadPicture(MtmCursor cursor)
    {
        var baseFields = ReadBaseFields(cursor);

        var type = baseFields.Version >= 1 ? cursor.ReadInt32() : 0;
        var shape = baseFields.Version >= 2 ? cursor.ReadInt32() : 0;

        if (baseFields.Version >= 4)
        {
            cursor.ReadInt32(); // m_Adjust -- not consumed by the current mapping
        }

        if (baseFields.Version >= 5)
        {
            cursor.ReadInt32(); // m_TransPoint -- not consumed by the current mapping
        }

        // type == 0 means "no image loaded" -- confirmed against every real local sample, and the
        // bitmap payload is entirely absent from the wire in that case (not a zero-length blob).
        MtmBitmap? bitmap = type != 0 ? cursor.ReadBitmap() : null;

        // Read UNCONDITIONALLY on Shape == 5, independent of Type (Draw.cpp:3779-3785) -- a Type == 0
        // picture with Shape == 5 still carries polygon bytes that must be consumed to stay synced.
        MtmPolygon? polygon = shape == 5 ? cursor.ReadPolygon() : null;

        return new MtmPictureElement(baseFields, type, shape, bitmap, polygon);
    }

    // CDrawOle -- Draw.cpp:3922 (save) / :3932 (load). Reads only the two fields BEFORE the opaque,
    // unbounded, no-length-prefix OLE payload, then rejects the whole file -- see
    // LegacyMtmOleNotImportableException's own doc comment for why nothing past this point is
    // recoverable.
    private static MtmElement ReadOle(MtmCursor cursor)
    {
        ReadBaseFields(cursor);
        cursor.ReadInt32(); // m_Trans
        cursor.ReadInt32(); // m_Stretch
        throw new LegacyMtmOleNotImportableException();
    }

    // CDrawLib -- Draw.cpp:4569 (save) / :4584 (load). Explicit length prefix makes this the one
    // unsupported element type that's cleanly skippable, unlike CM_OLE.
    private static MtmLibElement ReadLib(MtmCursor cursor)
    {
        // CDrawLib declares no extra base-record fields of its own beyond CDraw's (confirmed against
        // Draw.h) -- Name and the length-prefixed payload are ADDITIONAL fields after that base record,
        // matching every other element kind's own "base record, then my own extra fields" shape.
        ReadBaseFields(cursor);
        var name = cursor.ReadString();
        var size = cursor.ReadCount();
        cursor.ReadBytes(size); // opaque CItems-plugin payload -- format is plugin-specific and out
                                 // of scope; only the length prefix is needed to skip past it safely.
        return new MtmLibElement(name);
    }
}

/// <summary>Mutable cursor over the whole file's bytes, with every count-driven read validated
/// against the bytes actually remaining before it allocates or advances -- see
/// <see cref="LegacyMtmReader"/>'s own top-of-file doc comment for why. Internal, not a general-
/// purpose binary reader: every method here exists because <see cref="LegacyMtmReader"/> needs it,
/// nothing speculative.</summary>
internal sealed class MtmCursor(byte[] data)
{
    // CLAUDE.md's encoding rule: CP932/Windows-31J for this codebase's Japanese-origin legacy
    // sources, confirmed for this exact format by docs/mtm-binary-format.md's own string-encoding
    // section (AnsiString::c_str() emits the process's current locale codepage). Registered once per
    // call, matching this project's own established per-call-site pattern (e.g.
    // LogbookSessionServiceTests) rather than assuming a shared startup registration always ran --
    // RegisterProvider is documented idempotent, so a repeat call from a caller that already
    // registered it (e.g. ScanlineStudio.Host.Program) is a harmless no-op.
    private static readonly Encoding Cp932 = ResolveCp932();

    private int _position;

    public int Remaining => data.Length - _position;

    private static Encoding ResolveCp932()
    {
        Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(932);
    }

    public int ReadInt32()
    {
        EnsureRemaining(sizeof(int));
        // Explicitly little-endian (docs/mtm-binary-format.md's own stated wire format), not host
        // endianness -- BitConverter.ToInt32 would happen to agree on every platform this project
        // ships for today, but that's an accident of x86/ARM both being little-endian, not a
        // statement of intent (code-review nit).
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(_position, sizeof(int)));
        _position += sizeof(int);
        return value;
    }

    /// <summary>Reads one byte as a SIGNED value widened to <see langword="int"/> -- see
    /// <see cref="MtmBaseFields.LineStyle"/>'s own doc comment for why this must be signed, not
    /// unsigned.</summary>
    public int ReadSignedByteAsInt()
    {
        EnsureRemaining(1);
        var value = unchecked((sbyte)data[_position]);
        _position += 1;
        return value;
    }

    /// <summary>`TColor`/Win32 `COLORREF` on the wire is `0x00BBGGRR` -- this swaps R and B into the
    /// RGB order every consumer in this codebase expects, so nothing downstream of this reader ever
    /// has to remember the wire's own byte order.</summary>
    public Rgb24 ReadColor()
    {
        var raw = ReadInt32();
        var r = (byte)(raw & 0xFF);
        var g = (byte)((raw >> 8) & 0xFF);
        var b = (byte)((raw >> 16) & 0xFF);
        return new Rgb24(r, g, b);
    }

    /// <summary>`CDraw::SaveString`/`LoadString` (Draw.cpp:501/510) -- a 4-byte length prefix, then
    /// exactly that many raw bytes, CP932-decoded, with NO null terminator on the wire (length 0 means
    /// an empty string with nothing after it, not even a zero byte).</summary>
    public string ReadString()
    {
        var length = ReadValidatedCount();
        if (length == 0)
        {
            return string.Empty;
        }

        EnsureRemaining(length);
        var text = Cp932.GetString(data, _position, length);
        _position += length;
        return text;
    }

    /// <summary>Element/point counts that drive a loop or a `List&lt;T&gt;` capacity -- see
    /// <see cref="LegacyMtmReader"/>'s own hardening note. Bounded by bytes actually remaining, which
    /// is always a safe (if loose) upper bound: nothing in this file can ever need to allocate more
    /// slots than there are bytes left to read.</summary>
    public int ReadCount() => ReadValidatedCount();

    private int ReadValidatedCount()
    {
        var value = ReadInt32();
        if (value < 0 || value > Remaining)
        {
            throw new LegacyMtmFormatException(
                $"A length/count field read {value}, which is negative or exceeds the {Remaining} bytes "
                + "actually remaining in this file -- not a valid .mtm/.mti file.");
        }

        return value;
    }

    public byte[] ReadBytes(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new LegacyMtmFormatException(
                $"Attempted to read {count} bytes with only {Remaining} remaining -- not a valid .mtm/.mti file.");
        }

        var bytes = new byte[count];
        Array.Copy(data, _position, bytes, 0, count);
        _position += count;
        return bytes;
    }

    /// <summary>`CDraw::SaveBitmap`/`LoadBitmap` (Draw.cpp:456/471) -- width/height (both 0 means no
    /// bitmap present), then, only if both are nonzero, a complete standard Windows BMP stream
    /// (`TBitmap::SaveToStream`'s own well-documented VCL behavior): a 14-byte `BITMAPFILEHEADER`
    /// starting `"BM"` with its own `bfSize` total-length field at byte offset 2, which this method
    /// reads to know exactly how many further bytes belong to this bitmap -- no legacy-specific
    /// knowledge needed beyond that one field, any standard BMP decoder can take it from there.</summary>
    public MtmBitmap? ReadBitmap()
    {
        var width = ReadInt32();
        var height = ReadInt32();
        if (width == 0 || height == 0)
        {
            return null;
        }

        var signature = ReadBytes(2);
        if (signature[0] != (byte)'B' || signature[1] != (byte)'M')
        {
            throw new LegacyMtmFormatException(
                "Embedded bitmap is missing its \"BM\" BITMAPFILEHEADER signature -- not a valid .mtm/.mti file.");
        }

        var bfSizeBytes = ReadBytes(4);
        var bfSize = BinaryPrimitives.ReadUInt32LittleEndian(bfSizeBytes); // BMP's own little-endian format
        // bfSize is the TOTAL file size including the 6 header bytes already consumed above.
        if (bfSize < 14 || bfSize - 6 > (uint)Remaining)
        {
            throw new LegacyMtmFormatException(
                $"Embedded bitmap's BITMAPFILEHEADER.bfSize ({bfSize}) is smaller than a minimum BMP "
                + $"header or exceeds the {Remaining} bytes actually remaining -- not a valid .mtm/.mti file.");
        }

        var rest = ReadBytes((int)(bfSize - 6));
        var blob = new byte[bfSize];
        Array.Copy(signature, 0, blob, 0, 2);
        Array.Copy(bfSizeBytes, 0, blob, 2, 4);
        Array.Copy(rest, 0, blob, 6, rest.Length);
        return new MtmBitmap(blob);
    }

    /// <summary>`CPolygon::SaveToStream`/`LoadFromStream` (Draw.cpp:5417/:5431) -- its own
    /// magic-number lookahead, a DIFFERENT magic value from the base record's box-style one. See
    /// <see cref="MtmPolygon"/>'s own doc comment for the Y-scale correction applied here.</summary>
    public MtmPolygon ReadPolygon()
    {
        var magicOrCount = ReadInt32();
        int count;
        int xw;
        int yw;
        if (magicOrCount == LegacyMtmReaderConstants.PolygonMagic)
        {
            count = ReadValidatedCount();
            xw = ReadInt32();
            yw = ReadInt32();
        }
        else
        {
            count = magicOrCount;
            if (count < 0 || (long)count * (2 * sizeof(int)) > Remaining)
            {
                throw new LegacyMtmFormatException(
                    $"Polygon point count {count} is negative or exceeds the bytes actually remaining "
                    + "-- not a valid .mtm/.mti file.");
            }

            xw = 256;
            yw = 200;
        }

        var points = new List<(int X, int Y)>(count);
        for (var i = 0; i < count; i++)
        {
            var x = ReadInt32();
            var y = ReadInt32();
            // Legacy rescales into a fixed 320x256 space when the authored coordinate space
            // (xw/yw) differs from it. This reader applies the CORRECT rescale (dividing Y by yw),
            // not legacy's own confirmed bug (Draw.cpp's Y-scale line divides by xw instead) -- see
            // MtmPolygon's own doc comment for why that deviation is deliberate and costs nothing.
            if (xw != 320)
            {
                x = x * 320 / xw;
            }

            if (yw != 256)
            {
                y = y * 256 / yw;
            }

            points.Add((x, y));
        }

        return new MtmPolygon(points);
    }

    private void EnsureRemaining(int count)
    {
        if (count > Remaining)
        {
            throw new LegacyMtmFormatException(
                $"Expected {count} more bytes but only {Remaining} remain -- truncated or corrupt .mtm/.mti file.");
        }
    }
}

/// <summary>Split out so <see cref="MtmCursor"/> doesn't need a forward reference into
/// <see cref="LegacyMtmReader"/>'s own private constants for the one it shares.</summary>
internal static class LegacyMtmReaderConstants
{
    public const int PolygonMagic = unchecked((int)0x55aa2233);
}
