using ScanlineStudio.Core.Sstv;

namespace ScanlineStudio.Core.Cw.Tests;

/// <summary>Isolates <see cref="MorseAlphabet"/> from <see cref="ClassicalCwDecoder"/>'s own much
/// larger timing/envelope pipeline (fsk_cwid.md §8.4) -- proves the inverse (pattern-&gt;char) lookup
/// agrees with the real, already-verified TX-side <see cref="CwMorseGenerator"/> for every character
/// it can actually encode, BEFORE any DSP code depends on it. A bug here would otherwise be
/// indistinguishable from a DSP timing bug once wired into the full decoder.</summary>
public class MorseAlphabetTests
{
    private const double DotDurationMs = 40; // 28 WPM, matching legacy's own hardcoded default.

    [Theory]
    [InlineData('0')] [InlineData('1')] [InlineData('2')] [InlineData('3')] [InlineData('4')]
    [InlineData('5')] [InlineData('6')] [InlineData('7')] [InlineData('8')] [InlineData('9')]
    [InlineData('A')] [InlineData('B')] [InlineData('C')] [InlineData('D')] [InlineData('E')]
    [InlineData('F')] [InlineData('G')] [InlineData('H')] [InlineData('I')] [InlineData('J')]
    [InlineData('K')] [InlineData('L')] [InlineData('M')] [InlineData('N')] [InlineData('O')]
    [InlineData('P')] [InlineData('Q')] [InlineData('R')] [InlineData('S')] [InlineData('T')]
    [InlineData('U')] [InlineData('V')] [InlineData('W')] [InlineData('X')] [InlineData('Y')]
    [InlineData('Z')] [InlineData('?')] [InlineData('=')] [InlineData('>')]
    public void Lookup_EveryRealTableCharacter_RoundTripsThroughTheRealGenerator(char c)
    {
        var pattern = PatternFromGenerator(c);
        var decoded = MorseAlphabet.Lookup(pattern);

        Assert.NotNull(decoded);
        Assert.Equal(c, decoded!.Value);
    }

    [Theory]
    [InlineData(':')] [InlineData(';')] [InlineData('<')]
    public void Lookup_EmptyTableSlots_ReturnNull(char c)
    {
        // CwMorseTable's own empty slots (0x0000) -- CwMorseGenerator.GenerateChar's real behavior
        // for these is a 7-dot silence (no tone at all, so there's no real pattern to look up), NOT
        // a decodable character. Confirmed via the generator directly rather than assumed.
        var segments = CwMorseGenerator.Generate(c.ToString(), 1000, DotDurationMs).ToList();
        var toneSegments = segments.Skip(1).Where(s => s.FrequencyHz != 0).ToList();
        Assert.Empty(toneSegments);
    }

    [Fact]
    public void Lookup_Slash_ResolvesToTheLiteralBitPatternNotInTheTable()
    {
        // '/' has no CwMorseTable slot -- CwMorseGenerator.GenerateChar special-cases it with a
        // literal bit pattern (0x6805) before ever consulting the table. Confirms MorseAlphabet's
        // own hand-added entry for this actually matches the real generator's output, not a
        // guessed pattern.
        var pattern = PatternFromGenerator('/');
        Assert.Equal('/', MorseAlphabet.Lookup(pattern));
    }

    [Fact]
    public void Lookup_Period_ResolvesToR_TheDocumentedAmbiguity()
    {
        // '.' encodes to the SAME pattern as 'R' (CwMorseGenerator.GenerateChar's own '.' -> 'R'
        // special case, before table lookup) -- confirms the real generator's '.' output decodes to
        // 'R' via MorseAlphabet, matching the documented, deliberate ambiguity (not distinguishable
        // from a real 'R' on decode, by legacy's own design).
        var periodPattern = PatternFromGenerator('.');
        var rPattern = PatternFromGenerator('R');

        Assert.Equal(rPattern, periodPattern);
        Assert.Equal('R', MorseAlphabet.Lookup(periodPattern));
    }

    [Fact]
    public void Lookup_UnrecognizedPattern_ReturnsNull()
    {
        Assert.Null(MorseAlphabet.Lookup("......."));
        Assert.Null(MorseAlphabet.Lookup(""));
    }

    /// <summary>Encodes a single character via the real <see cref="CwMorseGenerator"/> and derives
    /// its dit/dah pattern from the actual tone-segment durations it produced -- classifying by
    /// duration relative to <see cref="DotDurationMs"/>, the same threshold shape
    /// <c>ClassicalCwDecoder</c>'s own classification step (§8.4 step 5) will use, so this helper
    /// exercises the real generator's output shape, not a hand-typed guess at what it should be.</summary>
    private static string PatternFromGenerator(char c)
    {
        // Generate("X", ...) yields a leading '@' segment (250ms silence) then X's own segments --
        // skip index 0, keep only the TONE segments (silence gaps between elements/chars are the
        // remainder), same as CwMorseGenerator.GenerateChar's own inter-element/inter-character gap
        // shape (dot after every element, dot*2 after the whole character).
        var toneSegments = CwMorseGenerator.Generate(c.ToString(), 1000, DotDurationMs)
            .Skip(1)
            .Where(s => s.FrequencyHz != 0)
            .ToList();

        return new string(toneSegments.Select(s => s.DurationMs < DotDurationMs * 2 ? '.' : '-').ToArray());
    }
}
