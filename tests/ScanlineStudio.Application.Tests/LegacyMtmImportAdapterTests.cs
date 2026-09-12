using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application.Tests;

/// <summary>Unit tests for <c>LegacyMtmImportAdapter</c>'s mapping rules -- constructs already-parsed
/// <c>Mtm*Element</c> trees directly (bypassing <c>LegacyMtmReader</c>, which has its own dedicated
/// test file) to isolate the DECISION layer from the byte-parsing layer. Every rule pinned here traces
/// to a specific plan-review round in <c>BACKLOG.md</c>'s legacy-`.mtm`-import entry.</summary>
public sealed class LegacyMtmImportAdapterTests
{
    private static readonly Rgb24 Red = new(255, 0, 0);
    private static readonly Rgb24 Green = new(0, 255, 0);
    private static readonly Rgb24 White = new(255, 255, 255);

    private static MtmBaseFields Base(
        int x1 = 0, int y1 = 0, int x2 = 100, int y2 = 100, Rgb24? lineColor = null,
        int lineStyle = 0, int boxStyle = 0, int lineWidth = 1, int version = 0) =>
        new(version, x1, y1, x2, y2, lineColor ?? Red, lineStyle, boxStyle, lineWidth);

    private static Task<LegacyMtmImportAdapter.ConversionResult> ConvertAsync(
        MtmGroupElement root, Func<MtmBitmap, CancellationToken, Task<string>>? writeAsset = null) =>
        LegacyMtmImportAdapter.ConvertAsync(
            root, writeAsset ?? ((_, _) => Task.FromResult("unused.png")), CancellationToken.None);

    private static MtmGroupElement Group(params MtmElement[] children) =>
        new(Base(x1: 0, y1: 0, x2: 0, y2: 0), TransX: 0, TransY: 0, TransCol: default, Sx: 320, Sy: 256, children);

    [Fact]
    public async Task ConvertAsync_BoxWithNegativeLineStyle_IsDroppedAndReported()
    {
        var shape = new MtmShapeElement(Base(lineStyle: -1), MtmShapeKind.Box);

        var result = await ConvertAsync(Group(shape));

        Assert.Empty(result.Elements);
        Assert.Contains(result.Notes, n => n.Contains("invisible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_BoxWithSolidLineStyle_IsOutlineOnlyWithFillDisabled()
    {
        var shape = new MtmShapeElement(Base(lineStyle: 0, lineColor: Green), MtmShapeKind.Box);

        var result = await ConvertAsync(Group(shape));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.False(box.FillEnabled);
        Assert.Equal(Green, box.BorderColor);
        Assert.Empty(result.Notes); // solid (0) style needs no approximation report
    }

    [Fact]
    public async Task ConvertAsync_BoxWithDashLineStyle_ReportsTheApproximation()
    {
        var shape = new MtmShapeElement(Base(lineStyle: 2), MtmShapeKind.Box);

        var result = await ConvertAsync(Group(shape));

        Assert.Contains(result.Notes, n => n.Contains("dash", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_BoxSWithDefaultCtorLineStyle_IsFilledWithNoBorder()
    {
        // CDrawBoxS's own constructor default is m_LineStyle == -1 -- the MOST COMMON real case,
        // not an edge case, per plan-review round 3.
        var shape = new MtmShapeElement(Base(lineStyle: -1, lineColor: Green), MtmShapeKind.BoxS);

        var result = await ConvertAsync(Group(shape));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.True(box.FillEnabled);
        Assert.Equal(Green, box.FillColor);
        Assert.Null(box.BorderColor);
    }

    [Fact]
    public async Task ConvertAsync_BoxSWithExplicitLineStyle_IsFilledWithAWhiteBorder()
    {
        var shape = new MtmShapeElement(Base(lineStyle: 0, lineColor: Green), MtmShapeKind.BoxS);

        var result = await ConvertAsync(Group(shape));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.True(box.FillEnabled);
        Assert.Equal(White, box.BorderColor);
    }

    [Fact]
    public async Task ConvertAsync_Line_PreservesEndpointOrder()
    {
        var shape = new MtmShapeElement(Base(x1: 10, y1: 90, x2: 90, y2: 10, lineStyle: 0), MtmShapeKind.Line);

        var result = await ConvertAsync(Group(shape));

        var line = Assert.IsType<PersistedLineElement>(Assert.Single(result.Elements));
        Assert.Equal(10.0 / 320, line.X1);
        Assert.Equal(90.0 / 256, line.Y1);
        Assert.Equal(90.0 / 320, line.X2);
        Assert.Equal(10.0 / 256, line.Y2);
    }

    [Fact]
    public async Task ConvertAsync_LineWithNegativeLineStyle_IsDroppedAndReported()
    {
        // Code-review finding: CDrawLine::Draw (Draw.cpp:627-641) is
        // `if (m_LineStyle >= 5) {...} else if (m_LineStyle >= 0) {...}` -- no else arm, so a
        // negative style draws NOTHING, the same invisibility rule as CM_BOX. An earlier version of
        // this test asserted the opposite (imported visible) and was wrong.
        var shape = new MtmShapeElement(Base(x1: 10, y1: 90, x2: 90, y2: 10, lineStyle: -1), MtmShapeKind.Line);

        var result = await ConvertAsync(Group(shape));

        Assert.Empty(result.Elements);
        Assert.Contains(result.Notes, n => n.Contains("invisible", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_Box_CenterAndSizeAreComputedFromCornersNotFromTheTopLeftAlone()
    {
        // Blocker 1 (round 2): X/Y must be the CENTER of x1..x2/y1..y2, not x1/y1 directly -- a
        // reader that forgot this conversion would put every element half its own size off.
        var shape = new MtmShapeElement(Base(x1: 0, y1: 0, x2: 32, y2: 64), MtmShapeKind.Box);

        var result = await ConvertAsync(Group(shape));

        var box = Assert.Single(result.Elements);
        Assert.Equal(16.0 / 320, box.X);
        Assert.Equal(32.0 / 256, box.Y);
        Assert.Equal(32.0 / 320, box.Width);
        Assert.Equal(64.0 / 256, box.Height);
    }

    [Theory]
    [InlineData("%m", "%m")] // already resolves via MacroTextResolver's own percent path, untouched
    [InlineData("%c", "{his_call}")]
    [InlineData("%C", "{his_call}")]
    [InlineData("%c de %m", "{his_call} de %m")]
    [InlineData("%r", "{his_rst}")]
    [InlineData("MMSSTV %v", "MMSSTV {app_version}")] // the exact text a real local sample (t1.mtm) contains
    public async Task ConvertAsync_TextMacroTokens_RewriteKnownTokensToBraceVariablesNeverRawPercent(string input, string expected)
    {
        var text = new MtmTextElement(
            Base(x1: 0, y1: 0, x2: 99, y2: 19), Grade: 0, Shadow: 0, HasStack: false, Vert: 0, PerSpect: 0, RightAdj: 0,
            Text: input, Col1: Red, Col2: Red, Col3: Red, Col4: Red, ColS: Red, ColB: null,
            FontFamily: "Arial", FontCharset: 0, FontHeightOrSize: -18, FontStyleCode: 0, BrushBitmap: null);

        var result = await ConvertAsync(Group(text));

        var persisted = Assert.IsType<PersistedTextElement>(Assert.Single(result.Elements));
        Assert.Equal(expected, persisted.Text);
    }

    [Fact]
    public async Task ConvertAsync_TextWithAnUnknownMacroToken_ConvertsToAGenericNamedVariableRatherThanRawPercent()
    {
        // Code-review finding: leaving an unhandled token as literal "%x" text is NOT safe --
        // MacroTextResolver treats every '%' as a live macro trigger and its own default arm renders
        // the literal string "%%" for anything it doesn't implement (confirmed against a real sample:
        // t1.mtm's "MMSSTV %v" would have transmitted as "MMSSTV %%"). Every token this adapter
        // doesn't specifically name must become a `{brace}` variable instead, which MacroTextResolver
        // renders as the literal, recognizable placeholder text when unfilled -- never "%%".
        var text = new MtmTextElement(
            Base(x1: 0, y1: 0, x2: 99, y2: 19), Grade: 0, Shadow: 0, HasStack: false, Vert: 0, PerSpect: 0, RightAdj: 0,
            Text: "%X", Col1: Red, Col2: Red, Col3: Red, Col4: Red, ColS: Red, ColB: null,
            FontFamily: "Arial", FontCharset: 0, FontHeightOrSize: -18, FontStyleCode: 0, BrushBitmap: null);

        var result = await ConvertAsync(Group(text));

        var persisted = Assert.IsType<PersistedTextElement>(Assert.Single(result.Elements));
        Assert.DoesNotContain("%", persisted.Text, StringComparison.Ordinal);
        Assert.Contains("{legacy_x}", persisted.Text, StringComparison.Ordinal);
        Assert.Contains(result.Notes, n => n.Contains("%X", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertAsync_TextWithAPercentFollowedByANonAlphanumericCharacter_UsesAMatchableVariableName()
    {
        // Code-review finding, round 2: both this app's and the fill-bar's own brace-token grammar is
        // [A-Za-z0-9_]+ -- a token char outside that set (here a space, as in real legacy text like
        // "100% QSL") must NOT build an unmatchable variable name like "{legacy_ }", which would never
        // appear in the QSO FILL bar despite the Notes text promising it would.
        var text = new MtmTextElement(
            Base(x1: 0, y1: 0, x2: 99, y2: 19), Grade: 0, Shadow: 0, HasStack: false, Vert: 0, PerSpect: 0, RightAdj: 0,
            Text: "100% QSL", Col1: Red, Col2: Red, Col3: Red, Col4: Red, ColS: Red, ColB: null,
            FontFamily: "Arial", FontCharset: 0, FontHeightOrSize: -18, FontStyleCode: 0, BrushBitmap: null);

        var result = await ConvertAsync(Group(text));

        var persisted = Assert.IsType<PersistedTextElement>(Assert.Single(result.Elements));
        Assert.Equal("100{legacy_percent}QSL", persisted.Text);
    }

    [Theory]
    [InlineData(1, true, false)]  // FSBOLD
    [InlineData(2, false, true)]  // FSITALIC
    [InlineData(3, true, true)]   // both
    public async Task ConvertAsync_TextFontStyleBits_MapBoldAndItalicIndependently(int styleCode, bool expectedBold, bool expectedItalic)
    {
        var text = new MtmTextElement(
            Base(x1: 0, y1: 0, x2: 99, y2: 19), Grade: 0, Shadow: 0, HasStack: false, Vert: 0, PerSpect: 0, RightAdj: 0,
            Text: "hi", Col1: Red, Col2: Red, Col3: Red, Col4: Red, ColS: Red, ColB: null,
            FontFamily: "Arial", FontCharset: 0, FontHeightOrSize: -18, FontStyleCode: styleCode, BrushBitmap: null);

        var result = await ConvertAsync(Group(text));

        var persisted = Assert.IsType<PersistedTextElement>(Assert.Single(result.Elements));
        Assert.Equal(expectedBold, persisted.Bold);
        Assert.Equal(expectedItalic, persisted.Italic);
    }

    [Fact]
    public async Task ConvertAsync_TextWithUnderlineOrStrikeoutBits_ReportsThemAsUnsupported()
    {
        const int fsUnderline = 4;
        var text = new MtmTextElement(
            Base(x1: 0, y1: 0, x2: 99, y2: 19), Grade: 0, Shadow: 0, HasStack: false, Vert: 0, PerSpect: 0, RightAdj: 0,
            Text: "hi", Col1: Red, Col2: Red, Col3: Red, Col4: Red, ColS: Red, ColB: null,
            FontFamily: "Arial", FontCharset: 0, FontHeightOrSize: -18, FontStyleCode: fsUnderline, BrushBitmap: null);

        var result = await ConvertAsync(Group(text));

        Assert.Contains(result.Notes, n => n.Contains("underline", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_PicWithNoBitmap_IsDroppedAsAnEmptyPlaceholder()
    {
        var pic = new MtmPictureElement(Base(), Type: 0, Shape: 0, Bitmap: null, Polygon: null);

        var result = await ConvertAsync(Group(pic));

        Assert.Empty(result.Elements);
        Assert.Contains(result.Notes, n => n.Contains("empty picture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_PicWithABitmap_WritesAnAssetAndProducesAnImageElement()
    {
        var bitmap = new MtmBitmap([1, 2, 3]);
        var pic = new MtmPictureElement(Base(x1: 0, y1: 0, x2: 63, y2: 63), Type: 1, Shape: 0, Bitmap: bitmap, Polygon: null);
        var written = new List<MtmBitmap>();

        var result = await ConvertAsync(Group(pic), (bmp, _) => { written.Add(bmp); return Task.FromResult("abc123.png"); });

        var image = Assert.IsType<PersistedImageElement>(Assert.Single(result.Elements));
        Assert.Equal("abc123.png", image.AssetFileName);
        Assert.Same(bitmap, Assert.Single(written));
    }

    [Fact]
    public async Task ConvertAsync_PicWithAPolygonShape_ReportsItAsUnreproduced()
    {
        var polygon = new MtmPolygon([(0, 0), (10, 10)]);
        var pic = new MtmPictureElement(
            Base(x1: 0, y1: 0, x2: 63, y2: 63), Type: 1, Shape: 5,
            Bitmap: new MtmBitmap([1]), Polygon: polygon);

        var result = await ConvertAsync(Group(pic));

        Assert.Contains(result.Notes, n => n.Contains("polygon", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_TitleTypeZero_IsAFlatFillWithNoGradient()
    {
        var title = new MtmTitleElement(Base(x1: 0, y1: 0, x2: 319, y2: 15), Type: 0, ColVert: 0, Red, Green, Red, Green, null, null);

        var result = await ConvertAsync(Group(title));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.False(box.GradientEnabled);
        Assert.Equal(Red, box.FillColor);
    }

    [Fact]
    public async Task ConvertAsync_TitleTypeOne_GradientEndColorIsCol2NotCol4()
    {
        // Round 3's blocker: Draw.cpp:1154/:1160 sweeps Col1->Col2, NOT Col1->Col4. Getting this
        // wrong imports every gradient title bar with the wrong end color.
        var col2 = new Rgb24(10, 20, 30);
        var col4 = new Rgb24(200, 200, 200);
        var title = new MtmTitleElement(Base(x1: 0, y1: 0, x2: 319, y2: 15), Type: 1, ColVert: 0, Red, col2, Green, col4, null, null);

        var result = await ConvertAsync(Group(title));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.True(box.GradientEnabled);
        Assert.Equal(Red, box.GradientStartColor);
        Assert.Equal(col2, box.GradientEndColor);
        Assert.Equal(TextGradientKind.Horizontal, box.GradientKind);
    }

    [Fact]
    public async Task ConvertAsync_TitleTypeOneWithColVertSet_IsVerticalNotHorizontal()
    {
        var title = new MtmTitleElement(Base(x1: 0, y1: 0, x2: 319, y2: 15), Type: 1, ColVert: 1, Red, Green, Red, Green, null, null);

        var result = await ConvertAsync(Group(title));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.Equal(TextGradientKind.Vertical, box.GradientKind);
    }

    [Fact]
    public async Task ConvertAsync_TitleTypeTwo_ApproximatesAsTwoStopUsingCol1AndCol4()
    {
        var col4 = new Rgb24(9, 9, 9);
        var title = new MtmTitleElement(Base(x1: 0, y1: 0, x2: 319, y2: 15), Type: 2, ColVert: 0, Red, Green, White, col4, null, null);

        var result = await ConvertAsync(Group(title));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.Equal(Red, box.GradientStartColor);
        Assert.Equal(col4, box.GradientEndColor);
        Assert.Contains(result.Notes, n => n.Contains("3-band", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_TitleWithAnUnrecognizedType_IsDroppedRatherThanRenderedAsAGradient()
    {
        // Code-review finding: CDrawTitle::Draw's switch (Draw.cpp:1138-1231) has no branch for any
        // Type outside 0-4 -- it paints NOTHING. An earlier version of this adapter fell through to
        // the Type==1/2 gradient case for anything unrecognized, silently rendering something legacy
        // never would have shown.
        var title = new MtmTitleElement(Base(x1: 0, y1: 0, x2: 319, y2: 15), Type: 99, ColVert: 0, Red, Green, Red, Green, null, null);

        var result = await ConvertAsync(Group(title));

        Assert.Empty(result.Elements);
        Assert.Contains(result.Notes, n => n.Contains("unrecognized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConvertAsync_TitleTypeFour_ExpandsIntoOneRowPerValidCharacter()
    {
        // '1'..'8' are the only valid pattern characters -- '9' is out of range and must be
        // SKIPPED (no row emitted) while still consuming its position in the sequence, matching
        // legacy's own unconditional y++ (Draw.cpp:1217-1230).
        var title = new MtmTitleElement(
            Base(x1: 0, y1: 100, x2: 319, y2: 115), Type: 4, ColVert: 0, Red, Red, Red, Red, Sound: "139", Bitmap: null);

        var result = await ConvertAsync(Group(title));

        // '1' and '3' are valid (2 rows); '9' is skipped.
        Assert.Equal(2, result.Elements.Count);
        var rows = result.Elements.Cast<PersistedBoxElement>().ToList();
        // char '1' -> _ft[0] == 0 (black); char '3' -> _ft[2] == 60.
        Assert.Equal(new Rgb24(0, 0, 0), rows[0].FillColor);
        Assert.Equal(new Rgb24(60, 60, 60), rows[1].FillColor);
    }

    [Fact]
    public async Task ConvertAsync_TitleTypeFourWithNoSound_UsesTheLegacyCtorDefaultPattern()
    {
        var title = new MtmTitleElement(
            Base(x1: 0, y1: 0, x2: 319, y2: 15), Type: 4, ColVert: 0, Red, Red, Red, Red, Sound: null, Bitmap: null);

        var result = await ConvertAsync(Group(title));

        // "1356865313568888" is 16 characters, all valid ('1'-'8') -- 16 rows.
        Assert.Equal(16, result.Elements.Count);
    }

    [Fact]
    public async Task ConvertAsync_LibElement_IsDroppedAndReported()
    {
        var lib = new MtmLibElement("SomePlugin");

        var result = await ConvertAsync(Group(lib));

        Assert.Empty(result.Elements);
        Assert.Contains(result.Notes, n => n.Contains("SomePlugin", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConvertAsync_NestedGroup_FlattensChildrenInPlaceWithIncreasingZ()
    {
        var first = new MtmShapeElement(Base(x1: 0, y1: 0, x2: 1, y2: 1), MtmShapeKind.Box);
        var nestedChild = new MtmShapeElement(Base(x1: 2, y1: 2, x2: 3, y2: 3), MtmShapeKind.Box);
        var nestedGroup = new MtmGroupElement(Base(), 0, 0, default, 320, 256, [nestedChild]);
        var third = new MtmShapeElement(Base(x1: 4, y1: 4, x2: 5, y2: 5), MtmShapeKind.Box);

        var result = await ConvertAsync(Group(first, nestedGroup, third));

        Assert.Equal(3, result.Elements.Count);
        Assert.Equal(0, result.Elements[0].Z);
        Assert.Equal(1, result.Elements[1].Z); // the nested child, spliced in at its parent's position
        Assert.Equal(2, result.Elements[2].Z);
    }

    [Theory]
    [InlineData(1, 100.0 / 3 / 2 / 256)]
    [InlineData(2, 100.0 / 2 / 2 / 256)]
    [InlineData(3, 100.0 * 3 / 4 / 2 / 256)]
    public async Task ConvertAsync_BoxStyleRatios_ConvertToTheExpectedCornerRadiusFraction(int boxStyle, double expectedRadius)
    {
        var shape = new MtmShapeElement(Base(x1: 0, y1: 0, x2: 100, y2: 100, boxStyle: boxStyle), MtmShapeKind.Box);

        var result = await ConvertAsync(Group(shape));

        var box = Assert.IsType<PersistedBoxElement>(Assert.Single(result.Elements));
        Assert.Equal(expectedRadius, box.CornerRadius, precision: 10);
    }
}
