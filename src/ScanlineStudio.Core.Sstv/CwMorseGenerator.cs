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
/// apparent legacy bug where the WPM UI value is never actually applied to CW-ID's own post-image
/// dot length (`Option.cpp:594`, `Main.cpp:1886-1888`, `Main.cpp:13742` -- the conversion exists but
/// <c>OutputCWID</c> never calls the code path that would apply it).
/// </summary>
internal static class CwMorseGenerator
{
    /// <summary>Inverse of legacy's own WPM formula (`Main.cpp:1888`:
    /// <c>m_CWIDWPM = (1110.0 / (m_CWIDSpeed + 30)) + 0.5</c>, and <c>dot = m_CWIDSpeed + 30</c>
    /// directly per `sstv.cpp:2967` -- so, dropping the +0.5 display-rounding term, <c>wpm =
    /// 1110.0 / dot</c>, inverted here). Constant is 1110, NOT the standard PARIS-timing 1200;
    /// implementing "standard" Morse WPM here would run CW-ID ~8% fast relative to what the
    /// configured WPM number implies.</summary>
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
