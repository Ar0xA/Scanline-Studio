using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Extracted (CW-ID/FSK station-ID subsystem Phase 5 auditor round-1 finding) from
/// <see cref="AnalogFmSstvEncoder"/>'s original private helper so <c>ScanlineStudio.Application.SstvSessionService.GetOperatorCallsignAsync</c>
/// could share the exact same normalization for its RX self-filter comparison. See
/// <see cref="AnalogFmSstvEncoderStationIdWiringTests"/> for the end-to-end TX-path coverage of this
/// same logic (encode-then-decode, not just the unit); these tests cover the unit directly.
/// </summary>
public class StationIdCallsignNormalizerTests
{
    [Fact]
    public void Normalize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, StationIdCallsignNormalizer.Normalize(null));
        Assert.Equal(string.Empty, StationIdCallsignNormalizer.Normalize(""));
    }

    [Fact]
    public void Normalize_UppercasesAndTrims()
    {
        Assert.Equal("W1AW", StationIdCallsignNormalizer.Normalize("  w1aw  "));
    }

    [Fact]
    public void Normalize_AlreadyNormalized_IsUnchanged()
    {
        Assert.Equal("W1AW", StationIdCallsignNormalizer.Normalize("W1AW"));
    }

    [Fact]
    public void Normalize_TruncatesBeforeUppercasingAndTrimming_MatchingLegacyOrderExactly()
    {
        // Option.cpp:445-448: StrCopy caps at MLCALL=16 chars FIRST, THEN jstrupr uppercases, THEN
        // clipsp/SkipSpace trims -- not trim-then-cap. 20 leading spaces + "W1AW" (24 raw chars):
        // truncate-first takes the first 16 chars (all spaces), so "W1AW" never survives, trimming
        // down to an empty result. Trim-first would have produced "W1AW" instead.
        Assert.Equal(string.Empty, StationIdCallsignNormalizer.Normalize(new string(' ', 20) + "W1AW"));
    }

    [Fact]
    public void Normalize_ExactlySixteenChars_NotTruncated()
    {
        Assert.Equal("ABCDEFGHIJKLMNOP", StationIdCallsignNormalizer.Normalize("abcdefghijklmnop"));
    }

    [Fact]
    public void Normalize_SeventeenChars_TruncatedToSixteen()
    {
        Assert.Equal("ABCDEFGHIJKLMNOP", StationIdCallsignNormalizer.Normalize("abcdefghijklmnopq"));
    }
}
