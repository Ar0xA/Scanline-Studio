using System.Text;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Application;

/// <summary>Maps a parsed <see cref="MtmGroupElement"/> tree (produced by <see cref="LegacyMtmReader"/>,
/// a pure byte-accurate transcription) onto the SHIPPED <see cref="PersistedTemplateElement"/> model --
/// every "how does legacy content become a modern template element" DECISION lives here, settled
/// across three `yoniq-auditor` plan-review rounds plus one `yoniq-principal` verification (see
/// `BACKLOG.md`'s legacy-`.mtm`-import entry for the full record). <see cref="LegacyMtmReader"/>
/// deliberately does none of this -- it only transcribes wire facts.
///
/// <para><b>Never blocks the whole import on one unsupported element.</b> `CM_LIB`, an empty `CM_PIC`,
/// an invisible legacy box, an unreproducible gradient shape -- each is dropped or approximated
/// individually, with a plain-English note recorded in <see cref="ConversionResult.Notes"/>, while
/// every other element keeps importing. The one exception is `CM_OLE`, which
/// <see cref="LegacyMtmReader"/> itself already turns into a thrown exception that aborts the whole
/// file -- its payload has no length prefix, so there is no way to skip past it and keep going (see
/// <see cref="LegacyMtmOleNotImportableException"/>'s own doc comment).</para>
///
/// <para><b>Z-order</b> is assigned by FLATTENED list position (legacy has no explicit per-element Z;
/// draw order IS z-order, `CDrawGroup::SaveToStream` walks its list in draw order) -- a nested
/// `CM_GROUP`'s own children are spliced into the same flat sequence at the position the nested group
/// itself would have occupied, matching the modern model's own flat `(Z, index)` compositing
/// (`ITransmitImagePreparer.ApplyTemplate` sorts by exactly that).</para></summary>
internal static class LegacyMtmImportAdapter
{
    // Draw.cpp:1217-1230's _ft[] grey-level table for CM_TITLE m_Type==4 -- index by (char - '1'),
    // valid for '1'..'8' only.
    private static readonly byte[] SoundGreyLevels = [0, 29, 60, 92, 126, 162, 201, 242];

    // Draw.cpp:1051's ctor default -- the string an OLDER (m_Ver 1-2) m_Type==4 title implicitly
    // has, since m_Sound itself is only on the wire when m_Ver >= 3.
    private const string DefaultSoundPattern = "1356865313568888";

    // ComLib.h:66-69.
    private const int FontStyleBold = 1;
    private const int FontStyleItalic = 2;
    private const int FontStyleUnderline = 4;
    private const int FontStyleStrikeout = 8;

    public sealed record ConversionResult(IReadOnlyList<PersistedTemplateElement> Elements, IReadOnlyList<string> Notes);

    /// <summary><paramref name="writeImageAssetAsync"/> decodes a raw embedded bitmap and returns the
    /// GUID-based asset file name it was written under (via the temp-file bridge --
    /// <see cref="IImageFileLoader"/> is path-only, so a raw byte blob has to touch disk once before
    /// this class can hand it to <see cref="IImageSourceWriter"/>). Owned by the caller
    /// (<c>TemplateStore</c>), which alone knows the target <c>templateId</c>/<c>GetAssetPath</c>.</summary>
    public static async Task<ConversionResult> ConvertAsync(
        MtmGroupElement root, Func<MtmBitmap, CancellationToken, Task<string>> writeImageAssetAsync, CancellationToken ct)
    {
        var flattened = new List<MtmElement>();
        Flatten(root, flattened);

        var elements = new List<PersistedTemplateElement>();
        var notes = new List<string>();

        for (var z = 0; z < flattened.Count; z++)
        {
            ct.ThrowIfCancellationRequested();
            var converted = await ConvertElementAsync(flattened[z], z, root.Sx, root.Sy, writeImageAssetAsync, notes, ct)
                .ConfigureAwait(false);
            elements.AddRange(converted);
        }

        return new ConversionResult(elements, notes);
    }

    // Nested CM_GROUP children are spliced in at the position their parent group occupied -- there is
    // no modern group/container element, and Draw.cpp:4963-5012 applies no coordinate transform to a
    // nested group's own children, so their bounds are already in the SAME absolute space as
    // everything else and need no adjustment here, only flattening.
    private static void Flatten(MtmGroupElement group, List<MtmElement> into)
    {
        foreach (var child in group.Children)
        {
            if (child is MtmGroupElement nested)
            {
                Flatten(nested, into);
            }
            else
            {
                into.Add(child);
            }
        }
    }

    private static async Task<IReadOnlyList<PersistedTemplateElement>> ConvertElementAsync(
        MtmElement element, int z, int sx, int sy,
        Func<MtmBitmap, CancellationToken, Task<string>> writeImageAssetAsync, List<string> notes, CancellationToken ct) => element switch
    {
        MtmShapeElement shape => AsList(ConvertShape(shape, z, sx, sy, notes)),
        MtmTextElement text => AsList(ConvertText(text, z, sx, sy, notes)),
        MtmPictureElement pic => AsList(await ConvertPictureAsync(pic, z, sx, sy, writeImageAssetAsync, notes, ct).ConfigureAwait(false)),
        // CM_TITLE m_Type==4 is the one element kind that can expand into MANY modern elements (a
        // stacked row per sound-preset character) -- routed through its own list-returning method
        // rather than forced through the single-element shape every other case uses.
        MtmTitleElement { Type: 4 } soundTitle => ConvertTitleSoundBar(soundTitle, z, sx, sy, notes),
        MtmTitleElement title => AsList(await ConvertTitleAsync(title, z, sx, sy, writeImageAssetAsync, notes, ct).ConfigureAwait(false)),
        MtmLibElement lib => Dropped(notes, $"Unsupported plugin element '{lib.Name}' (legacy CM_LIB) -- dropped, no modern equivalent."),
        _ => throw new NotSupportedException($"Unrecognized parsed element type {element.GetType()}."),
    };

    private static IReadOnlyList<PersistedTemplateElement> AsList(PersistedTemplateElement? element) =>
        element is null ? [] : [element];

    private static IReadOnlyList<PersistedTemplateElement> Dropped(List<string> notes, string note)
    {
        notes.Add(note);
        return [];
    }

    // CM_LINE/CM_BOX/CM_BOXS -- plan-review round 3's settled mapping.
    private static PersistedTemplateElement? ConvertShape(MtmShapeElement shape, int z, int sx, int sy, List<string> notes)
    {
        var b = shape.Base;
        var (x, y, width, height) = CornerToCenter(b.X1, b.Y1, b.X2, b.Y2, sx, sy);

        if (shape.Kind == MtmShapeKind.Line)
        {
            // Code-review finding: CDrawLine::Draw (Draw.cpp:627-641) is
            // `if (m_LineStyle >= 5) {...} else if (m_LineStyle >= 0) {...}` -- NO else arm, so a
            // negative style draws NOTHING, exactly the same invisibility rule CDrawBox's own
            // RoundRect uses. An earlier version of this comment claimed no such guard existed;
            // checked directly against these lines and that claim was wrong.
            if (b.LineStyle < 0)
            {
                notes.Add("A line had no visible line style in legacy (invisible) -- dropped.");
                return null;
            }

            NoteNonSolidLineStyle(b.LineStyle, notes, "line");
            return new PersistedLineElement(
                x, y, width, height, z, Locked: false,
                b.X1 / (double)sx, b.Y1 / (double)sy, b.X2 / (double)sx, b.Y2 / (double)sy,
                b.LineColor, NormalizeThickness(b.LineWidth, sy));
        }

        if (shape.Kind == MtmShapeKind.Box)
        {
            // CDrawBox::Draw selects NULL_BRUSH unconditionally (Draw.cpp:803) -- legacy's plain
            // CM_BOX never fills, only outlines. RoundRect's own m_LineStyle < 0 early-return
            // (Draw.cpp:752) means a negative style draws NOTHING AT ALL here.
            //
            // Checked BEFORE computing a corner radius (code-review finding, round 1: computing it
            // first meant a box dropped for invisibility could still emit a "corner style
            // approximated" note about a box that was never going to render at all).
            if (b.LineStyle < 0)
            {
                notes.Add("An outline-only box had no visible line style in legacy (invisible) -- dropped.");
                return null;
            }

            var boxCornerRadius = ConvertCornerRadius(b, sx, sy, notes);
            NoteNonSolidLineStyle(b.LineStyle, notes, "box outline");
            return new PersistedBoxElement(
                x, y, width, height, z, Locked: false,
                FillColor: b.LineColor, BorderColor: b.LineColor, BorderThickness: NormalizeThickness(b.LineWidth, sy),
                Opacity: 1.0, CornerRadius: boxCornerRadius, FillEnabled: false);
        }

        var cornerRadius = ConvertCornerRadius(b, sx, sy, notes);

        // CM_BOXS: fill is unconditional (Draw.cpp:1023/:1029); the ctor default m_LineStyle == -1
        // (Draw.cpp:1010) means MOST filled boxes carry no border at all -- CDrawBoxS::Draw only
        // draws the white outline when the author explicitly set a non-default (>= 0) style.
        Rgb24? borderColor = null;
        var borderThickness = 0.0;
        if (b.LineStyle >= 0)
        {
            NoteNonSolidLineStyle(b.LineStyle, notes, "box border");
            borderColor = new Rgb24(255, 255, 255); // CDrawBoxS::Draw:1033 forces clWhite
            borderThickness = NormalizeThickness(b.LineWidth, sy);
        }

        return new PersistedBoxElement(
            x, y, width, height, z, Locked: false,
            FillColor: b.LineColor, BorderColor: borderColor, BorderThickness: borderThickness,
            Opacity: 1.0, CornerRadius: cornerRadius, FillEnabled: true);
    }

    private static void NoteNonSolidLineStyle(int lineStyle, List<string> notes, string what)
    {
        if (lineStyle is >= 1 and <= 4)
        {
            notes.Add($"A {what}'s dash style was approximated as solid.");
        }
        else if (lineStyle >= 5)
        {
            notes.Add($"A {what}'s decorative frame style was approximated as solid.");
        }
    }

    // Draw.cpp:804-821's RoundRect ratios (1/3, 1/2, 3/4, then a full ellipse) applied to the box's
    // own pixel dimensions, then normalized by frame HEIGHT -- CornerRadius's own confirmed
    // convention (TransmitImagePreparer.cs:1024). Legacy corners are ELLIPTICAL (independent X/Y
    // axes); collapsing to one scalar radius loses horizontal roundness on any non-square box, so
    // this is reported for every non-zero style, not only the elliptical (4/5) case.
    private static double ConvertCornerRadius(MtmBaseFields b, int sx, int sy, List<string> notes)
    {
        if (b.BoxStyle == 0)
        {
            return 0;
        }

        var boxWidthPx = Math.Abs(b.X2 - b.X1);
        var boxHeightPx = Math.Abs(b.Y2 - b.Y1);
        var shortSide = Math.Min(boxWidthPx, boxHeightPx);

        double ratio;
        string report;
        switch (b.BoxStyle)
        {
            case 1: ratio = 1.0 / 3; report = "A box's rounded-corner style was approximated (legacy's independent X/Y radii collapsed to one)."; break;
            case 2: ratio = 1.0 / 2; report = "A box's rounded-corner style was approximated (legacy's independent X/Y radii collapsed to one)."; break;
            case 3: ratio = 3.0 / 4; report = "A box's rounded-corner style was approximated (legacy's independent X/Y radii collapsed to one)."; break;
            default: ratio = 1.0; report = "A box's elliptical style was approximated as fully-rounded corners (a true ellipse on a non-square box isn't representable)."; break;
        }

        notes.Add(report);
        var radiusPx = ratio * shortSide / 2.0;
        return radiusPx / sy;
    }

    // TemplateBoxElement.BorderThickness/CornerRadius/FontSizeRelative all share this "fraction of
    // the target frame's own HEIGHT" convention -- see TransmitImagePreparer.cs's own confirmed
    // usage of each.
    private static double NormalizeThickness(int pixelWidth, int sy) => pixelWidth / (double)sy;

    // Min/max-corner-to-center-and-size -- plan-review round 2 blocker 1, adopted exactly as settled.
    private static (double X, double Y, double Width, double Height) CornerToCenter(int x1, int y1, int x2, int y2, int sx, int sy)
    {
        var width = Math.Abs(x2 - x1) / (double)sx;
        var height = Math.Abs(y2 - y1) / (double)sy;
        var x = (x1 + x2) / 2.0 / sx;
        var y = (y1 + y2) / 2.0 / sy;
        return (x, y, width, height);
    }

    // CM_TEXT -- plan-review round 2/3's settled geometry rule (candidate (a): trust the stored rect,
    // legacy recomputes m_X2/m_Y2 from live font metrics on every render, so the persisted rect IS
    // the authored ink box, not stale) plus the round-3 off-by-one and degenerate-rect caveats.
    private static PersistedTextElement ConvertText(MtmTextElement text, int z, int sx, int sy, List<string> notes)
    {
        var b = text.Base;
        double x, y, width, height;
        if (b.X2 > b.X1 && b.Y2 > b.Y1)
        {
            // Draw.cpp:1779's rendered extent is x2-x1+1 by y2-y1+1 (an inclusive pixel range), not
            // a bare difference.
            width = (b.X2 - b.X1 + 1) / (double)sx;
            height = (b.Y2 - b.Y1 + 1) / (double)sy;
            x = (b.X1 + b.X2) / 2.0 / sx;
            y = (b.Y1 + b.Y2) / 2.0 / sy;
        }
        else
        {
            // Degenerate rect -- an m_Ver < 4 record whose m_X2 was forced to 0 on load
            // (Draw.cpp:3164-3166) and never re-rendered since, or a never-drawn old-format save.
            // No live font-metrics engine is available at import time, so this estimates a box from
            // the font size and character count rather than measuring exactly -- reported, since it
            // is an approximation, not a transcription.
            notes.Add("A text element's stored size was invalid (unrendered legacy record) -- its box was estimated from font size and character count.");
            var fontSizeEstimate = Math.Abs(text.FontHeightOrSize) is > 0 and var h ? h / (double)sy : 0.1;
            var estimatedWidthPx = Math.Max(1, text.Text.Length) * fontSizeEstimate * sy * 0.6;
            var estimatedHeightPx = fontSizeEstimate * sy * 1.2;
            width = estimatedWidthPx / sx;
            height = estimatedHeightPx / sy;
            x = (b.X1 + estimatedWidthPx / 2.0) / sx;
            y = (b.Y1 + estimatedHeightPx / 2.0) / sy;
        }

        var fontSizeRelative = Math.Abs(text.FontHeightOrSize) > 0 ? Math.Abs(text.FontHeightOrSize) / (double)sy : 0.1;

        var (rewrittenText, unhandledTokens) = RewriteLegacyMacroTokens(text.Text);
        if (unhandledTokens.Count > 0)
        {
            notes.Add($"Text contains legacy macro token(s) {string.Join(", ", unhandledTokens.Distinct())} with no modern equivalent -- converted to a named fill-in field, visible in the QSO FILL bar.");
        }

        var bold = (text.FontStyleCode & FontStyleBold) != 0;
        var italic = (text.FontStyleCode & FontStyleItalic) != 0;
        if ((text.FontStyleCode & (FontStyleUnderline | FontStyleStrikeout)) != 0)
        {
            notes.Add("Text underline/strikethrough styling has no modern equivalent -- dropped.");
        }

        // Shadow/stack/gradient/brush-fill text effects: m_Grade's exact semantics for which of
        // these are active were not settled by plan-review (flagged explicitly, round 3's "not
        // stated in this revision" note) -- rather than guess at a mapping nobody verified against
        // source, this imports the well-specified core (text/font/position/color/bold/italic)
        // faithfully and reports the rest as not reproduced, instead of risking a wrong effect.
        if (text.Shadow != 0)
        {
            notes.Add("Text shadow effect has no confirmed modern mapping -- not reproduced.");
        }

        if (text.HasStack)
        {
            notes.Add("Text stacked-copy (\"3D\") effect has no confirmed modern mapping -- not reproduced.");
        }

        if (text.Grade != 0)
        {
            notes.Add("Text gradient/brush-fill has no confirmed modern mapping -- imported as a flat color.");
        }

        return new PersistedTextElement(
            x, y, width, height, z, Locked: false,
            rewrittenText, fontSizeRelative, text.Col1,
            text.FontFamily, StrokeColor: null, StrokeThickness: 0,
            Bold: bold, Italic: italic);
    }

    // Code-review finding, round 1: leaving an unhandled token as literal "%x" text is NOT safe --
    // MacroTextResolver treats every '%' in ANY text as a live macro trigger (matching legacy's own
    // always-parse-percent behavior, Main.cpp:10689's unconditional p++), and its own percent pass
    // only implements 'm'/'D'/'T' (MacroTextResolver.cs:109-115); everything else falls through to
    // its OWN default arm, which renders the literal string "%%" -- confirmed on a REAL sample
    // (t1.mtm contains "MMSSTV %v"; %v is legacy's app-version macro, Main.cpp:10792-10794 -- left as
    // raw text, that template would transmit "MMSSTV %%", not a version string and not even the
    // original literal characters).
    //
    // So only 'm'/'D'/'T' are safe to leave as raw percent-text (MacroTextResolver already resolves
    // them correctly, unchanged). EVERY other token is rewritten to a `{brace}` fill-variable instead
    // -- the same named-fill-variable mechanism spec/15's own shipped design already uses for
    // per-QSO fields, and MacroTextResolver's own brace-token pass falls through to the literal
    // `{name}` text when a variable was never filled (MacroTextResolver.cs:165), which is a visible,
    // recognizable placeholder rather than a silently wrong "%%". Named where the legacy meaning is
    // confirmed (Main.cpp:10679-10833); a generic `{legacy_X}` name otherwise, since guessing a wrong
    // specific name would be worse than an honest generic one -- reported either way.
    private static readonly Dictionary<char, string> KnownTokenVariableNames = new()
    {
        ['c'] = "his_call", ['C'] = "his_call", // correspondent callsign
        ['r'] = "his_rst",                      // his RST
        ['v'] = "app_version", ['V'] = "app_version", // sending software's name/version
    };

    private static (string Text, IReadOnlyList<string> Unhandled) RewriteLegacyMacroTokens(string text)
    {
        var builder = new StringBuilder(text.Length);
        var unhandled = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '%')
            {
                builder.Append(text[i]);
                continue;
            }

            // Legacy always consumes the character after '%', even at end-of-string
            // (Main.cpp:10689's p++ runs unconditionally) -- mirrored here for the same reason
            // MacroTextResolver's own percent-token pass already documents.
            var token = i + 1 < text.Length ? text[i + 1] : '\0';
            i++;

            if (token is 'm' or 'D' or 'T')
            {
                builder.Append('%').Append(token);
                continue;
            }

            // Code-review finding, round 2: both this app's and the fill-bar's own brace-token
            // grammar is [A-Za-z0-9_]+ (MacroTextResolver.cs:70) -- a token char outside that set
            // (e.g. legacy text like "100% QSL", where the character after '%' is a space) would
            // build an unmatchable variable name, silently never appearing in the QSO FILL bar
            // despite the Notes text promising it would. Falls back to the SAME fixed name the
            // end-of-string case already uses.
            var variableName = KnownTokenVariableNames.TryGetValue(token, out var known)
                ? known
                : token != '\0' && char.IsAsciiLetterOrDigit(token)
                    ? $"legacy_{char.ToLowerInvariant(token)}"
                    : "legacy_percent";
            builder.Append('{').Append(variableName).Append('}');
            unhandled.Add(token == '\0' ? "%" : $"%{token}");
        }

        return (builder.ToString(), unhandled);
    }

    // CM_PIC -- plan-review round 2's settled mapping.
    private static async Task<PersistedTemplateElement?> ConvertPictureAsync(
        MtmPictureElement pic, int z, int sx, int sy,
        Func<MtmBitmap, CancellationToken, Task<string>> writeImageAssetAsync, List<string> notes, CancellationToken ct)
    {
        if (pic.Polygon is not null)
        {
            notes.Add("A picture's polygon-shaped clipping has no modern equivalent -- not reproduced (imported as a plain rectangle).");
        }

        if (pic.Bitmap is null)
        {
            // Type == 0 -- confirmed empty placeholder, not a decode failure. PersistedImageElement's
            // AssetFileName is non-nullable and SaveAsync requires the asset to already exist, so
            // there is no representable "picture element with no picture" -- drop it, reported.
            notes.Add("An empty picture placeholder (no image ever set) was dropped.");
            return null;
        }

        var (x, y, width, height) = CornerToCenter(pic.Base.X1, pic.Base.Y1, pic.Base.X2, pic.Base.Y2, sx, sy);
        var assetFileName = await writeImageAssetAsync(pic.Bitmap, ct).ConfigureAwait(false);
        return new PersistedImageElement(
            x, y, width, height, z, Locked: false,
            assetFileName, ImageFitMode.Stretch, PersistedImageSourceKind.File, OriginPayload: null);
    }

    // CM_TITLE -- plan-review round 3's fully settled mapping.
    private static async Task<PersistedTemplateElement?> ConvertTitleAsync(
        MtmTitleElement title, int z, int sx, int sy,
        Func<MtmBitmap, CancellationToken, Task<string>> writeImageAssetAsync, List<string> notes, CancellationToken ct)
    {
        var b = title.Base;
        // Draw.cpp:1133/:1236 forces the title bar's right edge to the frame's own full width at
        // draw time, regardless of what m_X2 was last saved as -- imported the same way, using the
        // TOP-LEVEL group's own Sx (never a nested group's, per the reader's own Sx/Sy convention).
        var x2 = sx - 1;

        switch (title.Type)
        {
            case 3:
                if (title.Bitmap is null)
                {
                    notes.Add("An empty title-bar image (no image ever set) was dropped.");
                    return null;
                }

                var (imgX, imgY, imgW, imgH) = CornerToCenter(b.X1, b.Y1, x2, b.Y2, sx, sy);
                var assetFileName = await writeImageAssetAsync(title.Bitmap, ct).ConfigureAwait(false);
                return new PersistedImageElement(
                    imgX, imgY, imgW, imgH, z, Locked: false,
                    assetFileName, ImageFitMode.Stretch, PersistedImageSourceKind.File, OriginPayload: null);

            case 0:
                {
                    var (x, y, width, height) = CornerToCenter(b.X1, b.Y1, x2, b.Y2, sx, sy);
                    return new PersistedBoxElement(
                        x, y, width, height, z, Locked: false,
                        FillColor: title.Col1, BorderColor: null, BorderThickness: 0, Opacity: 1.0);
                }

            case 1:
            case 2:
                {
                    var (x, y, width, height) = CornerToCenter(b.X1, b.Y1, x2, b.Y2, sx, sy);
                    var gradientKind = title.ColVert != 0 ? TextGradientKind.Vertical : TextGradientKind.Horizontal;
                    if (title.Type == 2)
                    {
                        notes.Add("A title bar's 3-band gradient was approximated as a 2-stop gradient (middle color lost).");
                    }

                    var endColor = title.Type == 2 ? title.Col4 : title.Col2;
                    return new PersistedBoxElement(
                        x, y, width, height, z, Locked: false,
                        FillColor: title.Col1, BorderColor: null, BorderThickness: 0, Opacity: 1.0,
                        GradientEnabled: true, GradientKind: gradientKind,
                        GradientStartColor: title.Col1, GradientEndColor: endColor);
                }

            default:
                // Code-review finding: CDrawTitle::Draw's switch (Draw.cpp:1138-1231) has no branch
                // for any Type outside 0-4 -- it paints NOTHING. This used to fall into the
                // Type==1/2 gradient arm above, silently rendering something legacy would not have
                // shown at all.
                notes.Add($"A title bar had an unrecognized style (type {title.Type}) -- dropped.");
                return null;
        }
    }

    // CM_TITLE m_Type==4 -- Draw.cpp:1217-1230 paints one full-width, uniform-grey horizontal row
    // per character of m_Sound, in order starting at m_Y1: NOT a 2-D pattern, N solid stacked rows.
    // The row position (y) advances for EVERY character, including one outside '1'..'8' (the source's
    // own p++/y++ increments run unconditionally, only the paint call is guarded) -- reproduced here
    // as N one-pixel-tall PersistedBoxElements, skipping only the DRAW for an out-of-range character
    // while still leaving a gap at that row (matching legacy's own unpainted-row behavior, which
    // shows whatever was already on the canvas through it).
    private static List<PersistedTemplateElement> ConvertTitleSoundBar(
        MtmTitleElement title, int z, int sx, int sy, List<string> notes)
    {
        var b = title.Base;
        var x2 = sx - 1; // same full-canvas-width forcing as every other title type
        var sound = title.Sound ?? DefaultSoundPattern; // ctor default when m_Ver < 3 left it unwritten

        var rows = new List<PersistedTemplateElement>();
        for (var i = 0; i < sound.Length; i++)
        {
            var ch = sound[i];
            if (ch is < '1' or > '8')
            {
                continue; // row position (i) still advances via the loop counter; only the paint is skipped
            }

            var grey = SoundGreyLevels[ch - '1'];
            var rowY1 = b.Y1 + i;
            var (x, y, width, height) = CornerToCenter(b.X1, rowY1, x2, rowY1 + 1, sx, sy);
            rows.Add(new PersistedBoxElement(
                x, y, width, height, z, Locked: false,
                FillColor: new Rgb24(grey, grey, grey), BorderColor: null, BorderThickness: 0, Opacity: 1.0));
        }

        if (rows.Count == 0)
        {
            notes.Add("A sound-visualizer title bar had no valid pattern characters -- dropped.");
        }
        else
        {
            notes.Add($"A sound-visualizer title bar was reproduced as {rows.Count} stacked grey bars (legacy's own per-character scanline pattern).");
        }

        return rows;
    }
}
