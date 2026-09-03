namespace ScanlineStudio.Core.Cw.Tests;

/// <summary>Isolates <see cref="CwIdCallsignExtractor"/> from the DSP decode pipeline -- fed
/// hand-constructed decoded-text strings directly, not real decoded audio, so a bug here can't be
/// confused with a bug in the timing/envelope code that produces the text in the first place.</summary>
public class CwIdCallsignExtractorTests
{
    [Theory]
    [InlineData("DE W1AW", "W1AW")]
    [InlineData("DE W1AW/M", "W1AW/M")]
    [InlineData("de w1aw", "W1AW")] // case-insensitive "DE", normalized to uppercase output.
    [InlineData("DE JA1XYZ", "JA1XYZ")]
    [InlineData("  DE   W1AW  ", "W1AW")] // extra whitespace tolerated (RemoveEmptyEntries).
    public void Extract_RealShapedText_ReturnsTheNormalizedCallsign(string decodedText, string expected)
    {
        Assert.Equal(expected, CwIdCallsignExtractor.Extract(decodedText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("W1AW")] // no "DE" at all.
    [InlineData("DE")] // "DE" with nothing after it.
    [InlineData("DE 599")] // token after DE doesn't look callsign-shaped (no digit-then-letters).
    [InlineData("DE ABCDEFG")] // no digit anywhere -- not callsign-shaped.
    public void Extract_NoRealCallsignPresent_ReturnsNull(string decodedText)
    {
        Assert.Null(CwIdCallsignExtractor.Extract(decodedText));
    }

    [Fact]
    public void Extract_TokenContainingAnUnknownCharacterMarker_ReturnsNull()
    {
        // U+FFFD is ClassicalCwDecoder's own "unknown character" marker (fsk_cwid.md §8.4 step 6) --
        // a decode with an unresolved character in the callsign token must not be treated as a real,
        // trustworthy callsign.
        Assert.Null(CwIdCallsignExtractor.Extract("DE W1�W"));
    }

    [Fact]
    public void Extract_MultipleDeTokens_ReturnsTheFirstCallsignShapedMatch()
    {
        Assert.Equal("W1AW", CwIdCallsignExtractor.Extract("DE NOTACALL DE W1AW"));
    }
}
