namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Golden-vector tests for <see cref="CwMorseGenerator"/>, hand-derived from legacy
/// <c>CSSTVMOD::WriteCWID</c> (`sstv.cpp:2951-3003`) directly -- not round-tripped against any
/// decoder (there is no legacy CW decoder in this project; CW-ID is judged by a human ear or a CW
/// skimmer, so timing fidelity to the real formula is the whole point).
/// </summary>
public class CwMorseGeneratorTests
{
    private const double ToneHz = 700.0;
    private const double DotMs = 60.0; // sys.m_CWIDSpeed=30 -> dot=60, a representative mid speed.

    [Fact]
    public void Generate_EmptyText_YieldsOnlyTheLeadingAtSilence()
    {
        var segments = CwMorseGenerator.Generate(string.Empty, ToneHz, DotMs).ToList();

        Assert.Equal([(0.0, 250.0)], segments);
    }

    [Fact]
    public void Generate_LetterA_MatchesTableEntry0x8002_DitDah()
    {
        // sstv.cpp:2959: table['A'-'0'] = 0x8002 -> elementCount=2, bits MSB-first: 1,0 -> dit,dah.
        // Standard Morse for 'A' is dit-dah, confirms the table + bit-scan direction are both right.
        var segments = CwMorseGenerator.Generate("A", ToneHz, DotMs).ToList();

        Assert.Equal(
        [
            (0.0, 250.0), // leading '@'
            (ToneHz, DotMs), (0.0, DotMs), // dit + inter-element gap
            (ToneHz, DotMs * 3), (0.0, DotMs), // dah + inter-element gap
            (0.0, DotMs * 2), // inter-character tail
        ],
            segments);
    }

    [Fact]
    public void Generate_Slash_UsesLiteral0x6805_NotTableLookup_DahDitDitDahDit()
    {
        // sstv.cpp:2973-2975: '/' bypasses the table entirely, d=0x6805 (not addressable via any
        // 'c'-'0' index). Bit-scan (MSB first, shifting left, elementCount = 0x6805 & 0xff = 5):
        // 0110100000000101 -> 0,1,1,0,1 -> dah,dit,dit,dah,dit -- matches ITU Morse for '/'.
        var segments = CwMorseGenerator.Generate("/", ToneHz, DotMs).Skip(1).ToList(); // skip leading '@'

        Assert.Equal(
        [
            (ToneHz, DotMs * 3), (0.0, DotMs), // dah
            (ToneHz, DotMs), (0.0, DotMs), // dit
            (ToneHz, DotMs), (0.0, DotMs), // dit
            (ToneHz, DotMs * 3), (0.0, DotMs), // dah
            (ToneHz, DotMs), (0.0, DotMs), // dit
            (0.0, DotMs * 2), // tail
        ],
            segments);
    }

    [Fact]
    public void Generate_Period_RemapsToSamePatternAsLetterR()
    {
        // sstv.cpp:2972: `if (c == '.') c = 'R';` -- verified by direct comparison, not by
        // independently re-deriving R's own table pattern (that would just restate the source).
        var periodSegments = CwMorseGenerator.Generate(".", ToneHz, DotMs).Skip(1).ToList();
        var rSegments = CwMorseGenerator.Generate("R", ToneHz, DotMs).Skip(1).ToList();

        Assert.Equal(rSegments, periodSegments);
    }

    [Theory]
    [InlineData(':')] // sstv.cpp:2957: table[':'-'0'] = 0x0000, an explicitly empty slot.
    [InlineData('#')] // outside the '0'-'Z' addressable range entirely.
    public void Generate_UnrecognizedOrEmptyTableSlot_YieldsSevenDotSilence_NoTail(char unrecognized)
    {
        var segments = CwMorseGenerator.Generate(unrecognized.ToString(), ToneHz, DotMs).Skip(1).ToList();

        // sstv.cpp:2988-2990: `if (!d) { Write(0, dot*7); return; }` -- returns immediately, no
        // dot*2 tail (unlike the normal per-character path).
        Assert.Equal([(0.0, DotMs * 7)], segments);
    }

    [Fact]
    public void Generate_Lowercase_IsUppercasedBeforeLookup()
    {
        // sstv.cpp:2969: `c = toupper(c);` -- lowercase 'a' must produce 'A''s exact pattern.
        var lower = CwMorseGenerator.Generate("a", ToneHz, DotMs).Skip(1).ToList();
        var upper = CwMorseGenerator.Generate("A", ToneHz, DotMs).Skip(1).ToList();

        Assert.Equal(upper, lower);
    }

    [Theory]
    [InlineData('あ')] // HIRAGANA LETTER A ('あ') -- masks to 0x42 = 'B' under a naive `&0x7f`.
    [InlineData('À')] // Latin-1 'À' -- masks to 0x40 = '@' under a naive `&0x7f` (comment corrected,
                       // Tier A Batch 8 chunk 8d: '@' is a SPECIAL-CASED 250ms-silence branch checked
                       // BEFORE the table lookup, sstv.cpp:2976-2979 -- not an aliased table letter --
                       // but a naive mask would still route it there instead of to real silence for
                       // the right reason, which is exactly the hazard this test guards against.
    public void Generate_NonAsciiCharacter_ProducesSilence_NotAnAliasedLetter(char nonAscii)
    {
        // Code-review finding: legacy iterates CP932 BYTES (Main.cpp:6976), each independently
        // masked to 7 bits (sstv.cpp:2970) -- a genuinely non-ASCII UTF-16 char has no legacy
        // byte-level equivalent, so masking it here would alias arbitrary Unicode code points into
        // real Morse letters instead of producing silence. Must NOT equal any real letter's pattern
        // (in particular must not equal 'B' for U+3042, which is exactly what a naive `&0x7f` mask
        // would produce).
        var segments = CwMorseGenerator.Generate(nonAscii.ToString(), ToneHz, DotMs).Skip(1).ToList();
        var bPattern = CwMorseGenerator.Generate("B", ToneHz, DotMs).Skip(1).ToList();

        Assert.Equal([(0.0, DotMs * 7)], segments);
        Assert.NotEqual(bPattern, segments);
    }

    [Theory]
    [InlineData(40.0)] // sys.m_CWIDSpeed=10 (the legacy default) -> dot=40.
    [InlineData(100.0)] // a slower configured speed -> dot=100.
    public void Generate_ScalesAllDurationsWithConfiguredDotLength(double dotMs)
    {
        var segments = CwMorseGenerator.Generate("A", ToneHz, dotMs).ToList();

        Assert.Equal((0.0, 250.0), segments[0]); // '@' silence is fixed at 250ms regardless of speed
        Assert.Equal((ToneHz, dotMs), segments[1]);
        Assert.Equal((0.0, dotMs), segments[2]);
        Assert.Equal((ToneHz, dotMs * 3), segments[3]);
        Assert.Equal((0.0, dotMs), segments[4]);
        Assert.Equal((0.0, dotMs * 2), segments[5]);
    }

    [Fact]
    public void MillisecondsPerDotFromWpm_RoundTripsItsOwn1110Constant()
    {
        // Renamed (Tier A Batch 8 chunk 8d): this only proves internal self-consistency of this
        // method's own `1110.0/wpm` formula -- NOT a round trip against legacy's real WPM-to-dot
        // conversion, which lives at Main.cpp:13742 (SendCWID), not Main.cpp:1888 (that's the
        // dot-to-WPM DISPLAY direction). Legacy's real conversion does its own +0.5 rounding AND
        // quantizes to a whole millisecond (e.g. WPM 28 -> exactly 40ms there, ~39.64ms here) --
        // this port's dot length is NOT bit-identical to legacy's, by design (an unquantized value
        // is arguably a better match to the LABELED wpm than legacy's own quantization is).
        const double dotMs = 60.0;
        var wpm = 1110.0 / dotMs;
        var roundTrippedDotMs = CwMorseGenerator.MillisecondsPerDotFromWpm(wpm);

        Assert.Equal(dotMs, roundTrippedDotMs, precision: 9);
    }

    [Theory]
    [InlineData('0', "-----")]
    [InlineData('1', ".----")]
    [InlineData('2', "..---")]
    [InlineData('3', "...--")]
    [InlineData('4', "....-")]
    [InlineData('5', ".....")]
    [InlineData('6', "-....")]
    [InlineData('7', "--...")]
    [InlineData('8', "---..")]
    [InlineData('9', "----.")]
    [InlineData('=', "-...-")]
    [InlineData('>', ".-.-.")]
    [InlineData('?', "..--..")]
    [InlineData('A', ".-")]
    [InlineData('B', "-...")]
    [InlineData('C', "-.-.")]
    [InlineData('D', "-..")]
    [InlineData('E', ".")]
    [InlineData('F', "..-.")]
    [InlineData('G', "--.")]
    [InlineData('H', "....")]
    [InlineData('I', "..")]
    [InlineData('J', ".---")]
    [InlineData('K', "-.-")]
    [InlineData('L', ".-..")]
    [InlineData('M', "--")]
    [InlineData('N', "-.")]
    [InlineData('O', "---")]
    [InlineData('P', ".--.")]
    [InlineData('Q', "--.-")]
    [InlineData('R', ".-.")]
    [InlineData('S', "...")]
    [InlineData('T', "-")]
    [InlineData('U', "..-")]
    [InlineData('V', "...-")]
    [InlineData('W', ".--")]
    [InlineData('X', "-..-")]
    [InlineData('Y', "-.--")]
    [InlineData('Z', "--..")]
    public void Generate_EveryTableEntry_MatchesItsRealItuMorsePattern(char letter, string ituPattern)
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8d (docs/functional-audit-playbook.md):
        // only 'A' (and '/' and '.', the two special-cased-outside-the-table characters) had any
        // regression protection before this test -- 40 of the table's 43 hand-typed hex constants
        // (CwMorseGenerator.cs:37-51) had zero coverage. A single-digit typo in the table (e.g.
        // swapping F=0xd004 and L=0xb004) would produce a plausible-sounding wrong letter that
        // nothing in the suite would have caught. Independently derived from real ITU Morse code,
        // not from the table itself -- confirms both the table's own values AND the bit-scan
        // direction (MSB-first) are correct for every reachable entry, not just 'A'.
        var segments = CwMorseGenerator.Generate(letter.ToString(), ToneHz, DotMs).Skip(1).ToList(); // skip leading '@'

        var expected = new List<(double FrequencyHz, double DurationMs)>();
        foreach (var element in ituPattern)
        {
            expected.Add((ToneHz, element == '.' ? DotMs : DotMs * 3));
            expected.Add((0.0, DotMs));
        }

        expected.Add((0.0, DotMs * 2)); // inter-character tail

        Assert.Equal(expected, segments);
    }
}
