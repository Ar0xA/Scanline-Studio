namespace ScanlineStudio.Application.Tests;

/// <summary>Unit tests for <c>LegacyMtmReader</c>/<c>MtmCursor</c> against hand-authored, synthetic
/// `.mtm`-format byte arrays -- built via <see cref="MtmFixtureBuilder"/>, never copied from a real
/// legacy sample (this project's own standing rule: no legacy-sourced literal content in a committed
/// test fixture). The real local samples in `yoniq-old/YONIQ-main/` are exercised separately, opt-in,
/// by <c>LegacyMtmRealSampleTests</c> -- these tests exist to pin exact field-order/version-gate
/// behavior per <c>docs/mtm-binary-format.md</c>, including branches no local real sample reaches at
/// all (a multi-byte CP932 string, a `CM_LIB` element, an old-format `m_Ver &lt; 1` title bar).</summary>
public sealed class LegacyMtmReaderTests
{
    [Fact]
    public void ReadTemplate_EmptyGroup_ParsesSxSyAndZeroElements()
    {
        var bytes = MtmFixtureBuilder.Group(version: 2, sx: 320, sy: 256, children: []);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        Assert.Equal(320, group.Sx);
        Assert.Equal(256, group.Sy);
        Assert.Empty(group.Children);
    }

    [Fact]
    public void ReadTemplate_GroupVersionBelow2_DefaultsSxSyTo320x256WithoutReadingThem()
    {
        // m_Ver < 2 groups carry no Sx/Sy on the wire at all (Draw.cpp:4722-4735) -- the group
        // written here has version 1, so MtmFixtureBuilder.Group never emits those two ints, and a
        // reader that (wrongly) tried to read them anyway would desync and fail this test some other
        // way (a truncation error, not merely a wrong default).
        var bytes = MtmFixtureBuilder.Group(version: 1, sx: 999, sy: 999, children: []);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        Assert.Equal(320, group.Sx);
        Assert.Equal(256, group.Sy);
    }

    [Fact]
    public void ReadTemplate_NotAGroupTag_Throws()
    {
        var bytes = new MtmFixtureBuilder().Int32(3 /* CM_BOX, not CM_GROUP */).ToArray();

        Assert.Throws<LegacyMtmFormatException>(() => LegacyMtmReader.ReadTemplate(bytes));
    }

    // (tag, expected MtmShapeKind ordinal: Line=0, Box=1, BoxS=2) -- InlineData can't carry an
    // internal enum type into a public test method's signature, so the ordinal travels as an int.
    [Theory]
    [InlineData(2, 0)]
    [InlineData(3, 1)]
    [InlineData(6, 2)]
    public void ReadTemplate_ShapeElements_ParseWithTheCorrectKindAndBaseFields(int tag, int expectedKindOrdinal)
    {
        var element = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 10, y1: 20, x2: 110, y2: 220, lineColor: (255, 0, 0), lineStyle: 0, lineWidth: 2)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(tag, element)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var shape = Assert.IsType<MtmShapeElement>(Assert.Single(group.Children));
        Assert.Equal(expectedKindOrdinal, (int)shape.Kind);
        Assert.Equal(10, shape.Base.X1);
        Assert.Equal(220, shape.Base.Y2);
        // ReadColor swaps the wire's 0x00BBGGRR into RGB -- (255,0,0) written as R,G,B above must
        // come back as R=255 in the decoded Rgb24, proving the swap runs in the right direction.
        Assert.Equal(255, shape.Base.LineColor.R);
        Assert.Equal(0, shape.Base.LineColor.B);
    }

    [Fact]
    public void ReadTemplate_NegativeLineStyle_ParsesAsANegativeInt()
    {
        // yoniq-principal: the byte must be read SIGNED, so 0xFF on the wire must come back as -1,
        // not 255 -- this is the one fact the whole "invisible box" import rule rests on.
        var element = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 1, y2: 1, lineColor: (0, 0, 0), lineStyle: -1, lineWidth: 1)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(3, element)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var shape = Assert.IsType<MtmShapeElement>(Assert.Single(group.Children));
        Assert.Equal(-1, shape.Base.LineStyle);
    }

    [Fact]
    public void ReadTemplate_BoxStyleMagicPresent_ReadsBoxStyleAndLineWidthAsTwoSeparateInts()
    {
        var element = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 1, y2: 1, lineColor: (0, 0, 0), lineStyle: 0,
                lineWidth: 7, withBoxStyleMagic: true, boxStyle: 2)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(3, element)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var shape = Assert.IsType<MtmShapeElement>(Assert.Single(group.Children));
        Assert.Equal(2, shape.Base.BoxStyle);
        Assert.Equal(7, shape.Base.LineWidth);
    }

    [Fact]
    public void ReadTemplate_BoxStyleMagicAbsent_TreatsTheSingleDwordAsLineWidthWithZeroBoxStyle()
    {
        var element = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 1, y2: 1, lineColor: (0, 0, 0), lineStyle: 0,
                lineWidth: 5, withBoxStyleMagic: false)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(3, element)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var shape = Assert.IsType<MtmShapeElement>(Assert.Single(group.Children));
        Assert.Equal(0, shape.Base.BoxStyle);
        Assert.Equal(5, shape.Base.LineWidth);
    }

    [Fact]
    public void ReadTemplate_TextElement_FullVersion7Record_ParsesEveryField()
    {
        var textBuilder = new MtmFixtureBuilder()
            .BaseFields(version: 7, x1: 5, y1: 6, x2: 105, y2: 30, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0)  // grade
            .Int32(0)  // shadow
            .Int32(0)  // m_Zero (v>=1)
            .Int32(0).Int32(5).Int32(6)  // m_Rot, m_X, m_Y (v>=2)
            .Int32(0)  // m_RightAdj (v>=4)
            .Int32(0).Int32(0)  // m_Stack, m_StackPara (v>=5)
            .Int32(0)  // m_PerSpect == 0, so no SPERSPECT block follows (v>=5)
            .Int32(0).Int32(-6)  // m_Vert, m_VertH (v>=6)
            .Str("%c de %m")  // text -- a real legacy macro-token pair, per docs/mtm-binary-format.md
            .Color(255, 255, 255).Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0)  // Col1..Col4
            .Color(10, 20, 30)  // ColS
            .Color(40, 50, 60)  // ColB (v>=3)
            .Str("Arial")  // font family
            .Int32(0)  // font charset
            .Int32(-18)  // font height (negative convention -- always negative on a real save)
            .Int32(1)  // font style code (FSBOLD)
            .Int32(0); // dummy
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(4, textBuilder.ToArray())]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var text = Assert.IsType<MtmTextElement>(Assert.Single(group.Children));
        Assert.Equal("%c de %m", text.Text);
        Assert.Equal("Arial", text.FontFamily);
        Assert.Equal(-18, text.FontHeightOrSize);
        Assert.Equal(1, text.FontStyleCode);
        Assert.NotNull(text.ColB);
        Assert.Equal(40, text.ColB.Value.R);
    }

    [Fact]
    public void ReadTemplate_TextElement_VersionBelow3_HasNoColB()
    {
        var textBuilder = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 0, y2: 0, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(0)  // grade, shadow (always, even at m_Ver 0)
            .Str("plain")
            .Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0)
            .Color(0, 0, 0)  // ColS -- NOT followed by ColB since version < 3
            .Str("Arial")
            .Int32(0).Int32(-12).Int32(0).Int32(0);
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(4, textBuilder.ToArray())]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var text = Assert.IsType<MtmTextElement>(Assert.Single(group.Children));
        Assert.Null(text.ColB);
        Assert.Equal("plain", text.Text);
    }

    [Fact]
    public void ReadTemplate_TextElement_VersionBelow4_ForcesX2ToZeroEvenThoughTheWireValueIsNonZero()
    {
        // Code-review finding: CDrawText::LoadFromStream forces m_X2 = 0 for m_Ver < 4
        // (Draw.cpp:3161-3166) -- a real, written X2 value on the wire (here 250) must still be
        // discarded in memory for an old-format record, matching legacy exactly.
        var textBuilder = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 10, y1: 20, x2: 250, y2: 40, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(0)
            .Str("old format")
            .Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0)
            .Color(0, 0, 0)
            .Str("Arial")
            .Int32(0).Int32(-12).Int32(0).Int32(0);
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(4, textBuilder.ToArray())]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var text = Assert.IsType<MtmTextElement>(Assert.Single(group.Children));
        Assert.Equal(0, text.Base.X2);
        Assert.Equal(10, text.Base.X1); // only X2 is forced; the rest of the base record is untouched
    }

    [Fact]
    public void ReadTemplate_TextElement_MultiByteCp932String_DecodesCorrectly()
    {
        // No local real sample exercises a multi-byte CP932 sequence (docs/mtm-binary-format.md's own
        // stated gap) -- this is the one test that actually proves the decode direction is right,
        // not merely that ASCII happens to survive either encoding.
        const string japaneseText = "こんにちは";
        var textBuilder = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 0, y2: 0, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(0)
            .Str(japaneseText)
            .Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0)
            .Color(0, 0, 0)
            .Str("MS Gothic")
            .Int32(0).Int32(-12).Int32(0).Int32(0);
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(4, textBuilder.ToArray())]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var text = Assert.IsType<MtmTextElement>(Assert.Single(group.Children));
        Assert.Equal(japaneseText, text.Text);
    }

    [Fact]
    public void ReadTemplate_TitleElement_VersionBelowOne_ReadsNothingBeyondTheBaseRecord()
    {
        // m_Ver < 1 titles have NO Type/ColVert/colors on the wire at all (Draw.cpp:1359) -- the
        // group below has exactly one more element after this title, and if the reader wrongly tried
        // to consume bytes this title's own record doesn't have, it would desync and misread (or
        // fail to find) that next sibling instead.
        var titleBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 319, y2: 15, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .ToArray();
        var nextShapeBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 42, y1: 42, x2: 99, y2: 99, lineColor: (1, 2, 3), lineStyle: 0, lineWidth: 1)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [
            MtmFixtureBuilder.Element(7, titleBody),
            MtmFixtureBuilder.Element(3, nextShapeBody),
        ]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        Assert.Equal(2, group.Children.Count);
        var title = Assert.IsType<MtmTitleElement>(group.Children[0]);
        // Legacy's own CDrawTitle constructor default, pinned by yoniq-principal -- not 0.
        Assert.Equal(1, title.Type);
        Assert.Equal(0, title.Col1.R);
        Assert.Equal(0, title.Col4.R);
        Assert.Equal(128, title.Col4.G);
        var nextShape = Assert.IsType<MtmShapeElement>(group.Children[1]);
        Assert.Equal(42, nextShape.Base.X1);
    }

    [Fact]
    public void ReadTemplate_TitleElement_VersionOne_HasNoColVertButStillReadsAllFourColors()
    {
        var titleBody = new MtmFixtureBuilder()
            .BaseFields(version: 1, x1: 0, y1: 0, x2: 319, y2: 15, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(1)  // Type
            // NO ColVert -- version < 2
            .Color(10, 10, 10).Color(20, 20, 20).Color(30, 30, 30).Color(40, 40, 40)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(7, titleBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var title = Assert.IsType<MtmTitleElement>(Assert.Single(group.Children));
        Assert.Equal(0, title.ColVert); // defaulted, not read
        Assert.Equal(40, title.Col4.R);
    }

    [Fact]
    public void ReadTemplate_TitleElement_TypeFourWithSound_ReadsSoundOnlyWhenVersionAtLeastThree()
    {
        var titleBody = new MtmFixtureBuilder()
            .BaseFields(version: 3, x1: 0, y1: 0, x2: 319, y2: 15, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(4)  // Type == 4
            .Int32(0)  // ColVert
            .Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0)
            .Str("1234")
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(7, titleBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var title = Assert.IsType<MtmTitleElement>(Assert.Single(group.Children));
        Assert.Equal("1234", title.Sound);
    }

    [Fact]
    public void ReadTemplate_TitleElement_TypeThree_ReadsAnEmbeddedBitmap()
    {
        var titleBody = new MtmFixtureBuilder()
            .BaseFields(version: 3, x1: 0, y1: 0, x2: 319, y2: 15, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(3)  // Type == 3, image-backed
            .Int32(0)  // ColVert
            .Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0).Color(0, 0, 0)
            .Bitmap()
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(7, titleBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var title = Assert.IsType<MtmTitleElement>(Assert.Single(group.Children));
        Assert.NotNull(title.Bitmap);
        Assert.True(title.Bitmap!.Bytes.Length > 14);
    }

    [Fact]
    public void ReadTemplate_PicElement_TypeZero_HasNoBitmapAtAll()
    {
        var picBody = new MtmFixtureBuilder()
            .BaseFields(version: 5, x1: 0, y1: 0, x2: 50, y2: 50, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0)  // Type == 0, no image
            .Int32(0)  // Shape
            .Int32(0)  // m_Adjust
            .Int32(0)  // m_TransPoint
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(5, picBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var pic = Assert.IsType<MtmPictureElement>(Assert.Single(group.Children));
        Assert.Null(pic.Bitmap);
    }

    [Fact]
    public void ReadTemplate_PicElement_TypeNonZero_ReadsTheBitmap()
    {
        var picBody = new MtmFixtureBuilder()
            .BaseFields(version: 5, x1: 0, y1: 0, x2: 50, y2: 50, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(1).Int32(0).Int32(0).Int32(0)
            .Bitmap()
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(5, picBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var pic = Assert.IsType<MtmPictureElement>(Assert.Single(group.Children));
        Assert.NotNull(pic.Bitmap);
    }

    [Fact]
    public void ReadTemplate_PicElement_ShapeFiveWithTypeZero_StillReadsThePolygon()
    {
        // The polygon read is gated on Shape == 5 alone (Draw.cpp:3779-3785), independent of Type --
        // a Type == 0 (no image) picture with Shape == 5 must still consume the polygon bytes to stay
        // byte-synced with whatever follows. This is the one test that would catch a reader that
        // wrongly gated the polygon read on Type != 0 too.
        var picBody = new MtmFixtureBuilder()
            .BaseFields(version: 5, x1: 0, y1: 0, x2: 50, y2: 50, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0)  // Type == 0, no bitmap
            .Int32(5)  // Shape == 5
            .Int32(0).Int32(0)
            // CPolygon, no-magic branch: the int IS the count.
            .Int32(1)   // Cnt = 1 (defaults XW=256, YW=200)
            .Int32(64).Int32(50)  // one point
            .ToArray();
        var nextShapeBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 7, y1: 7, x2: 8, y2: 8, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [
            MtmFixtureBuilder.Element(5, picBody),
            MtmFixtureBuilder.Element(3, nextShapeBody),
        ]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        Assert.Equal(2, group.Children.Count);
        var pic = Assert.IsType<MtmPictureElement>(group.Children[0]);
        Assert.NotNull(pic.Polygon);
        Assert.Single(pic.Polygon!.Points);
        var nextShape = Assert.IsType<MtmShapeElement>(group.Children[1]);
        Assert.Equal(7, nextShape.Base.X1); // proves byte-sync survived the polygon read
    }

    [Fact]
    public void ReadTemplate_Polygon_NoMagicDefaultCoordinateSpace_YScaleIsANoOp()
    {
        // In the no-magic default path (XW=256, YW=200), this reader's CORRECT y*256/YW rescale
        // makes Y a no-op (256/256 == 1) -- legacy's own confirmed bug (dividing by XW instead)
        // would make this 256/256 too by coincidence here, so this test alone can't distinguish the
        // two; the point is pinning the DEFAULT values themselves (256/200), which the next test's
        // explicit-XW/YW case exercises for real.
        var picBody = new MtmFixtureBuilder()
            .BaseFields(version: 5, x1: 0, y1: 0, x2: 50, y2: 50, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(5).Int32(0).Int32(0)
            .Int32(1)
            .Int32(128).Int32(100)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(5, picBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var pic = Assert.IsType<MtmPictureElement>(Assert.Single(group.Children));
        var (x, y) = pic.Polygon!.Points[0];
        Assert.Equal(160, x); // 128 * 320 / 256
        Assert.Equal(128, y); // 100 * 256 / 200
    }

    [Fact]
    public void ReadTemplate_Polygon_MagicPresent_UsesTheExplicitXwYwNotTheDefaults()
    {
        var picBody = new MtmFixtureBuilder()
            .BaseFields(version: 5, x1: 0, y1: 0, x2: 50, y2: 50, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(5).Int32(0).Int32(0)
            .Int32(unchecked((int)0x55aa2233))  // magic
            .Int32(1)    // Cnt
            .Int32(640)  // XW
            .Int32(512)  // YW
            .Int32(320).Int32(256)  // one point, dead center of the authored space
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(5, picBody)]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var pic = Assert.IsType<MtmPictureElement>(Assert.Single(group.Children));
        var (x, y) = pic.Polygon!.Points[0];
        Assert.Equal(160, x); // 320 * 320 / 640
        Assert.Equal(128, y); // 256 * 256 / 512 -- the CORRECT rescale (divides by YW, not XW)
    }

    [Fact]
    public void ReadTemplate_LibElement_IsSkippedButByteSyncSurvivesIntoTheNextElement()
    {
        var libBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 0, y2: 0, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Str("SomePlugin")
            .Int32(5)  // size
            .Bytes([9, 9, 9, 9, 9])  // opaque payload, discarded
            .ToArray();
        var nextShapeBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 3, y1: 4, x2: 5, y2: 6, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [
            MtmFixtureBuilder.Element(9, libBody),
            MtmFixtureBuilder.Element(3, nextShapeBody),
        ]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        Assert.Equal(2, group.Children.Count);
        var lib = Assert.IsType<MtmLibElement>(group.Children[0]);
        Assert.Equal("SomePlugin", lib.Name);
        var nextShape = Assert.IsType<MtmShapeElement>(group.Children[1]);
        Assert.Equal(3, nextShape.Base.X1);
    }

    [Fact]
    public void ReadTemplate_OleElement_ThrowsTheDedicatedNotImportableException()
    {
        var oleBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 0, y2: 0, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(0)  // m_Trans, m_Stretch
            .Bytes([1, 2, 3])   // opaque OLE payload -- never reached
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(8, oleBody)]);

        Assert.Throws<LegacyMtmOleNotImportableException>(() => LegacyMtmReader.ReadTemplate(bytes));
    }

    [Fact]
    public void ReadTemplate_NestedGroup_ParsesRecursivelyWithAbsoluteChildCoordinates()
    {
        var innerShapeBody = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 50, y1: 60, x2: 70, y2: 80, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .ToArray();
        var innerGroupBytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(3, innerShapeBody)]);
        // MtmFixtureBuilder.Group already writes its own leading CM_GROUP tag, so this is a
        // ready-to-embed nested-group element body as-is.
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [innerGroupBytes]);

        var group = LegacyMtmReader.ReadTemplate(bytes);

        var nested = Assert.IsType<MtmGroupElement>(Assert.Single(group.Children));
        var innerShape = Assert.IsType<MtmShapeElement>(Assert.Single(nested.Children));
        // No offset/scale is applied to a nested group's children (Draw.cpp:4963-5012 confirmed) --
        // the coordinates must survive completely unchanged.
        Assert.Equal(50, innerShape.Base.X1);
        Assert.Equal(80, innerShape.Base.Y2);
    }

    [Fact]
    public void ReadTemplate_StringLengthExceedsRemainingBytes_ThrowsRatherThanAllocatingOrCrashing()
    {
        var element = new MtmFixtureBuilder()
            .BaseFields(version: 0, x1: 0, y1: 0, x2: 0, y2: 0, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1)
            .Int32(0).Int32(0)
            .Int32(int.MaxValue) // claimed string length, wildly larger than anything remaining
            .ToArray();
        var bytes = MtmFixtureBuilder.Group(2, 320, 256, [MtmFixtureBuilder.Element(4, element)]);

        Assert.Throws<LegacyMtmFormatException>(() => LegacyMtmReader.ReadTemplate(bytes));
    }

    [Fact]
    public void ReadTemplate_GroupElementCountExceedsRemainingBytes_ThrowsRatherThanAllocatingOrHanging()
    {
        var builder = new MtmFixtureBuilder();
        builder.Int32(1); // CM_GROUP
        builder.BaseFields(version: 2, x1: 0, y1: 0, x2: 0, y2: 0, lineColor: (0, 0, 0), lineStyle: 0, lineWidth: 1);
        builder.Int32(0).Int32(0).Color(0, 0, 0); // TransX, TransY, TransCol (v>=1)
        builder.Int32(320).Int32(256); // Sx, Sy (v>=2)
        builder.Int32(int.MaxValue); // claimed element count, no bytes actually follow

        Assert.Throws<LegacyMtmFormatException>(() => LegacyMtmReader.ReadTemplate(builder.ToArray()));
    }

    [Fact]
    public void ReadTemplate_TruncatedFile_ThrowsRatherThanAnIndexOutOfRangeException()
    {
        var bytes = new MtmFixtureBuilder().Int32(1).Int32(2).ToArray(); // CM_GROUP tag, then nothing else

        Assert.Throws<LegacyMtmFormatException>(() => LegacyMtmReader.ReadTemplate(bytes));
    }
}
