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
    [InlineData('À')] // Latin-1 'À' -- masks to 0x40, one below 'A'.
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
    public void MillisecondsPerDotFromWpm_IsExactInverseOfLegacysOwnFormula()
    {
        // Main.cpp:1888: m_CWIDWPM = (1110.0 / (m_CWIDSpeed + 30)) + 0.5 -- verify round-trip
        // (minus legacy's own +0.5 rounding term, which only applies going dot->WPM for display).
        const double dotMs = 60.0;
        var wpm = 1110.0 / dotMs; // legacy's forward formula without the +0.5 display-rounding term
        var roundTrippedDotMs = CwMorseGenerator.MillisecondsPerDotFromWpm(wpm);

        Assert.Equal(dotMs, roundTrippedDotMs, precision: 9);
    }
}
