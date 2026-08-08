namespace ScanlineStudio.Application.Tests;

public sealed class MacroTextResolverTests
{
    private readonly MacroTextResolver _resolver = new();

    [Fact]
    public void ResolvesCallsignToken()
    {
        var settings = new OperatorSettings { Callsign = "W1AW" };

        Assert.Equal("DE W1AW", _resolver.Resolve("DE %m", settings));
    }

    [Fact]
    public void MissingCallsign_ResolvesToEmptyString()
    {
        var settings = new OperatorSettings();

        Assert.Equal("DE ", _resolver.Resolve("DE %m", settings));
    }

    [Fact]
    public void ResolvesNameAndGridBraceTokens()
    {
        var settings = new OperatorSettings { Name = "Jane", Grid = "EN52" };

        Assert.Equal("Jane, EN52", _resolver.Resolve("{name}, {grid}", settings));
    }

    [Fact]
    public void MissingNameOrGrid_ResolvesToEmptyString()
    {
        var settings = new OperatorSettings();

        Assert.Equal(", ", _resolver.Resolve("{name}, {grid}", settings));
    }

    [Fact]
    public void UnrecognizedPercentToken_ResolvesToLiteralDoublePercent()
    {
        // Matches legacy's own default case (Main.cpp:10817-10819) exactly -- an unrecognized
        // token doesn't pass through unchanged, it becomes "%%".
        var settings = new OperatorSettings();

        Assert.Equal("x%%y", _resolver.Resolve("x%zy", settings));
    }

    [Fact]
    public void LiteralDoublePercent_RoundTripsAsLiteralDoublePercent()
    {
        // "%%" has no dedicated case in legacy's switch either -- it falls into the same default
        // case as any other unrecognized token, so it round-trips rather than collapsing to "%".
        var settings = new OperatorSettings();

        Assert.Equal("100%%", _resolver.Resolve("100%%", settings));
    }

    [Fact]
    public void TrailingLonePercent_DoesNotThrow()
    {
        var settings = new OperatorSettings();

        Assert.Equal("abc%%", _resolver.Resolve("abc%", settings));
    }

    [Fact]
    public void UnrecognizedBraceToken_IsLeftAsIs()
    {
        // No legacy equivalent to match for {}-tokens, so an unknown one is left alone rather than
        // guessing at a "%%"-style escape convention that has no precedent here.
        var settings = new OperatorSettings();

        Assert.Equal("{unknown}", _resolver.Resolve("{unknown}", settings));
    }

    [Fact]
    public void DateToken_ResolvesToUtcDateInLegacyFormat()
    {
        // Deliberately checks shape (YYYY-MON-DD, MON from legacy's own MONT1[] table,
        // LogConv.cpp:175) rather than re-deriving "today" independently -- avoids both a
        // culture-dependent month abbreviation and a midnight-rollover race against DateTime.UtcNow.
        var settings = new OperatorSettings();
        var monthAbbreviations = new[] { "JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC" };

        var resolved = _resolver.Resolve("%D", settings);
        var parts = resolved.Split('-');

        Assert.Equal(3, parts.Length);
        Assert.Equal(4, parts[0].Length);
        Assert.True(int.TryParse(parts[0], out _));
        Assert.Contains(parts[1], monthAbbreviations);
        Assert.Equal(2, parts[2].Length);
        Assert.True(int.TryParse(parts[2], out _));
    }

    [Fact]
    public void EmptyInput_ReturnsEmptyString()
    {
        var settings = new OperatorSettings();

        Assert.Equal(string.Empty, _resolver.Resolve(string.Empty, settings));
    }
}
