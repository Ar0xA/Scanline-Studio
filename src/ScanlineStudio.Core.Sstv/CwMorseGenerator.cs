namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>CSSTVMOD::WriteCWID</c> (`sstv.cpp:2951-3003`) -- bit-packed dot/dash
/// Morse table, one element (tone) + inter-element gap per bit, MSB-first: bit=1 -&gt; dit (<c>dot</c>
/// length), bit=0 -&gt; dah (<c>dot*3</c> length); the low byte of a table entry is the element
/// count. Two characters are special-cased ahead of the table lookup (`sstv.cpp:2972-2975`): '.' is
/// remapped to 'R'; '/' uses a literal bit pattern <c>0x6805</c> (dah-dit-dit-dah-dit) not present
/// anywhere in the table -- common in real station IDs (portable-station suffixes, e.g. "W1AW/M"),
/// omitting it would silently turn every '/' into a 7-dot silence instead. '@' means 250ms of
/// leading silence, not a Morse character -- legacy's <c>OutputCWID</c> (`Main.cpp:6973`) prepends
/// it to every CW-ID transmission, which <see cref="Generate"/> does too. Any character outside the
/// table's '0'-'Z' range, or a table slot that's itself empty (':', ';', '&lt;'), produces a 7-dot
/// silence instead of a tone -- matches legacy's own "unrecognized character" behavior exactly, not
/// a deviation.
///
/// Dot length is an opaque parameter here (<c>dotDurationMs</c>) -- legacy's own
/// <c>dot = sys.m_CWIDSpeed + 30</c> (`sstv.cpp:2967`) has no WPM concept inside this function at
/// all; WPM-to-dot-length is the caller's responsibility (<see cref="MillisecondsPerDotFromWpm"/>).
/// Per the CW-ID/FSK station-ID subsystem implementation plan (round-2 finding, user-approved): the
/// configured WPM is wired to dot length correctly here, a deliberate, documented deviation from an
/// apparent legacy bug -- doc correction (Tier A Batch 8 chunk 8d): the bug is narrower than
/// "never actually applied" claimed. <c>sys.m_CWIDSpeed</c> is a GLOBAL legacy field
/// (`ComLib.h:263`), and legacy's OTHER CW-send call site, <c>SendCWID</c> (`Main.cpp:13742`), DOES
/// write it from the configured WPM -- so once a session has sent CW manually even once (Tune-button
/// CW, a repeater-answer send, or the CW menu), a SUBSEQUENT post-image <c>OutputCWID</c> genuinely
/// does pick up the WPM-derived dot length and keeps using it. The real, narrower bug: `OutputCWID`
/// itself (`sstv.cpp:2967`) never performs that conversion, so a session that never sent CW manually
/// runs post-image CW-ID at legacy's hardcoded default (`m_CWIDSpeed=10` -&gt; 40ms dot,
/// `Main.cpp:904`) regardless of the configured WPM, since `SaveIni` (`Main.cpp:2396-2399`) persists
/// `CWIDWPM` but never the derived `CWIDSpeed`. This port's fix (always deriving dot length from the
/// configured WPM) is still the right call either way -- only the "never applied" framing was
/// overstated.
/// </summary>
internal static class CwMorseGenerator
{
    /// <summary>Doc correction (Tier A Batch 8 chunk 8d): NOT literally the exact inverse of
    /// legacy's real WPM-to-dot conversion. `Main.cpp:1888` (`m_CWIDWPM = (1110.0/(m_CWIDSpeed+30))
    /// + 0.5`) is the DOT-to-WPM display direction, not WPM-to-dot; legacy's actual WPM-to-dot
    /// conversion lives at `Main.cpp:13742` (`SendCWID`), does its own `+0.5` int-assignment rounding
    /// AND quantizes to a whole millisecond -- at WPM 28 legacy's real dot is exactly 40ms, this
    /// method's `1110.0/28` is ~39.64ms (~0.9% faster), inaudible but not bit-identical. The `1110`
    /// constant itself IS correctly carried over (not the standard PARIS-timing `1200`) -- using
    /// `1200` here would run CW-ID slower than legacy at any given configured WPM number (legacy's
    /// own `1110` constant already runs ~8% fast relative to standard PARIS timing for the same WPM
    /// label -- an earlier version of this comment had this direction backwards).</summary>
    public static double MillisecondsPerDotFromWpm(double wpm) => 1110.0 / wpm;

    // sstv.cpp:2953-2966, verbatim byte values.
    private static readonly ushort[] Table =
    [
        // 0       1       2       3       4       5       6       7
        0x0005, 0x8005, 0xc005, 0xe005, 0xf005, 0xf805, 0x7805, 0x3805,
        // 8       9       :       ;       <       =       >       ?
        0x1805, 0x0805, 0x0000, 0x0000, 0x0000, 0x7005, 0xa805, 0xcc06,
        // @       A       B       C       D       E       F       G
        0x0000, 0x8002, 0x7004, 0x5004, 0x6003, 0x8001, 0xd004, 0x2003,
        // H       I       J       K       L       M       N       O
        0xf004, 0xc002, 0x8004, 0x4003, 0xb004, 0x0002, 0x4002, 0x0003,
        // P       Q       R       S       T       U       V       W
        0x9004, 0x2004, 0xa003, 0xe003, 0x0001, 0xc003, 0xe004, 0x8003,
        // X       Y       Z
        0x6004, 0x4004, 0x3004,
    ];

    /// <summary>Encodes <paramref name="text"/> as a full CW-ID transmission: leading '@' (250ms
    /// silence) then one Morse character at a time, in the same segment-tuple shape as
    /// <c>VisHeader.cs</c>'s generators (<c>IEnumerable&lt;(double FrequencyHz, double
    /// DurationMs)&gt;</c>) so it plugs directly into the existing TX segment pipeline. A
    /// <c>FrequencyHz</c> of 0 is real silence (see <see cref="AnalogFmSstvEncoder.RenderSegments"/>
    /// for the sample-loop side of this contract).
    ///
    /// The unconditional leading '@' mirrors <c>OutputCWID</c> specifically (`Main.cpp:6973`,
    /// `bf[0]='@'`) -- the post-image CW-ID trigger this generator is meant for. Legacy's OTHER
    /// CW-send call site, <c>SendCWID</c> (`Main.cpp:13752-13761`, a manual/test-send path), does
    /// NOT prepend '@'. If a future caller ever needs that path's behavior, it needs its own entry
    /// point, not a flag on this one -- don't silently drop the leading silence for OutputCWID's
    /// real use case to accommodate a manual-send caller that doesn't exist in this port yet.</summary>
    public static IEnumerable<(double FrequencyHz, double DurationMs)> Generate(
        string text, double toneFrequencyHz, double dotDurationMs)
    {
        foreach (var segment in GenerateChar('@', toneFrequencyHz, dotDurationMs))
        {
            yield return segment;
        }

        foreach (var rawChar in text)
        {
            foreach (var segment in GenerateChar(rawChar, toneFrequencyHz, dotDurationMs))
            {
                yield return segment;
            }
        }
    }

    private static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateChar(
        char rawChar, double toneFrequencyHz, double dotDurationMs)
    {
        if (rawChar > 0x7f)
        {
            // Code-review finding: legacy iterates CP932 BYTES (Main.cpp:6976), each masked to 7
            // bits (`c &= 0x7f`, sstv.cpp:2970) before the table lookup -- a byte >= 0x80 still
            // lands somewhere in the 7-bit range and can alias into a real letter, which is
            // meaningful because legacy never sees a value above 0xFF in the first place. This
            // port iterates UTF-16 chars, where a genuinely non-ASCII character (CP932-only text)
            // has no legacy byte-level equivalent -- masking an arbitrary Unicode code point here
            // would alias it into a real Morse letter instead of legacy's actual behavior (which,
            // for the same displayed character, would silence on the individual CP932 bytes).
            // Treat any char outside true 7-bit ASCII as unrecognized (7-dot silence) instead.
            // CP932-imported CW-ID text (out of v1 scope) would need its own byte-level encode
            // step upstream of this method, not a char-level mask here. Checked on the RAW char,
            // before uppercasing (round-2 code-review finding): some exotic Unicode letters
            // simple-uppercase to plain ASCII under invariant casing (e.g. U+0131 'ı' -> 'I'),
            // which would slip past a post-uppercase check and get treated as a real letter.
            yield return (0, dotDurationMs * 7);
            yield break;
        }

        var c = char.ToUpperInvariant(rawChar);
        c = (char)(c & 0x7f); // no-op for the true-ASCII input reaching here; kept for symmetry
                               // with legacy's own `c &= 0x7f` line placement (sstv.cpp:2970).

        if (c == '.')
        {
            c = 'R';
        }

        int d;
        if (c == '/')
        {
            d = 0x6805;
        }
        else if (c == '@')
        {
            yield return (0, 250);
            yield break;
        }
        else if (c is >= '0' and <= 'Z')
        {
            d = Table[c - '0'];
        }
        else
        {
            d = 0;
        }

        if (d == 0)
        {
            yield return (0, dotDurationMs * 7);
            yield break;
        }

        var elementCount = d & 0x00ff;
        for (var i = 0; i < elementCount; i++)
        {
            var toneDurationMs = (d & 0x8000) != 0 ? dotDurationMs : dotDurationMs * 3;
            yield return (toneFrequencyHz, toneDurationMs);
            yield return (0, dotDurationMs);
            d <<= 1;
        }

        yield return (0, dotDurationMs * 2);
    }
}
