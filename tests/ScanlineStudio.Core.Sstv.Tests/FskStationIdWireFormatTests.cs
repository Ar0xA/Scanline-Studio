namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Golden-vector tests for the NR/RST compact-vs-string predicate (`Main.cpp:6940`) -- this is the
/// specific piece round-1 auditor plan-review found wrong (a simplified "d &lt; 4096" threshold
/// that missed the length/round-trip condition), so it gets dedicated, boundary-heavy coverage
/// rather than being folded into the encoder's own tests.
/// </summary>
public class FskStationIdWireFormatTests
{
    [Theory]
    [InlineData("")] // l=0, below the l>=3 floor.
    [InlineData("12")] // l=2, below the l>=3 floor.
    public void IsCompactEligible_RemainderTooShort_ReturnsFalse(string remainder)
    {
        Assert.False(FskStationIdWireFormat.IsCompactEligible(remainder, out _));
    }

    [Fact]
    public void IsCompactEligible_ShortNumericRemainder_EligibleRegardlessOfMagnitude()
    {
        // l=3 (<4), all-digit, value well under 4096 -- the (l<4) branch of the final OR.
        Assert.True(FskStationIdWireFormat.IsCompactEligible("123", out var value));
        Assert.Equal(123u, value);
    }

    [Fact]
    public void IsCompactEligible_FourDigitRemainder_EligibleOnlyIfValueAtLeast1000()
    {
        // l=4, value=1234 >= 1000 -- eligible via the (value>=1000) branch of the final OR.
        Assert.True(FskStationIdWireFormat.IsCompactEligible("1234", out var eligibleValue));
        Assert.Equal(1234u, eligibleValue);

        // l=4 (leading zero), value=999 < 1000 -- round-trip through sprintf("%03u", 999)="999",
        // a DIFFERENT string than "0999", so compact would silently corrupt the RX-rendered NR.
        // Must fall through to the string form instead.
        Assert.False(FskStationIdWireFormat.IsCompactEligible("0999", out _));
    }

    [Theory]
    [InlineData(4095, true)] // just under the threshold
    [InlineData(4096, false)] // exactly at the threshold -- `d < 4096`, not `<=`
    public void IsCompactEligible_UpperBoundIsExclusive(int candidateValue, bool expectedEligible)
    {
        var remainder = candidateValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expectedEligible, FskStationIdWireFormat.IsCompactEligible(remainder, out _));
    }

    [Fact]
    public void IsCompactEligible_LongDigitRunWithSmallValue_IneligibleEvenThoughAllDigits()
    {
        // l=5 (>=4), value=999 (<1000) -- fails the final OR's second branch even though every
        // character is a plain digit and the value itself is small. This is the case a naive
        // "d < 4096" threshold (this plan's own round-1 mistake) would wrongly accept.
        Assert.False(FskStationIdWireFormat.IsCompactEligible("00999", out _));
    }

    [Fact]
    public void IsCompactEligible_ContainsAnyLetter_Ineligible()
    {
        // ComLib.cpp's IsAlphas is "contains any letter anywhere", not "is entirely letters" --
        // a single embedded letter disqualifies the whole remainder.
        Assert.False(FskStationIdWireFormat.IsCompactEligible("12a", out _));
        Assert.False(FskStationIdWireFormat.IsCompactEligible("abc", out _));
    }

    [Fact]
    public void IsCompactEligible_TrailingNonDigitNonLetter_IgnoredBySscanfStyleParse_ButCountedInLength()
    {
        // sscanf(p, "%u", &d) parses only the LEADING digit run and stops at the first non-digit --
        // it does NOT require the whole string to be numeric. "99:" has l=3 (not 2), no letters,
        // and its leading digit run "99" parses to 99 -- eligible via the (l<4) branch, with the
        // PARSED value being 99, not something derived from the full 3-character string.
        Assert.True(FskStationIdWireFormat.IsCompactEligible("99:", out var value));
        Assert.Equal(99u, value);
    }

    [Fact]
    public void IsCompactEligible_NoLeadingDigit_Ineligible()
    {
        // sscanf finds zero digit matches at the start -- fails outright, distinct from "parses to
        // a too-large value".
        Assert.False(FskStationIdWireFormat.IsCompactEligible(":::", out _));
    }

    [Fact]
    public void IsCompactEligible_FourDigitRemainder_ValueExactly1000_Eligible()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8b (docs/functional-audit-playbook.md):
        // the existing l=4 tests cover 1234 (>=1000, true) and 0999/00999 (<1000, false), but never
        // the >=1000 branch's own inclusive boundary. sprintf("%03u", 1000)="1000" round-trips
        // exactly (4 digits, no padding needed), so this must be eligible.
        Assert.True(FskStationIdWireFormat.IsCompactEligible("1000", out var value));
        Assert.Equal(1000u, value);
    }

    [Theory]
    [InlineData("999:", false)] // l=4 (colon counted), parsed value=999 (<1000) -- ineligible.
    [InlineData("1234:", true)] // l=5 (colon counted), parsed value=1234 (>=1000) -- eligible.
    public void IsCompactEligible_TrailingGarbageCountedInLength_ButNotInParsedValue(string remainder, bool expectedEligible)
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8b: the strlen-vs-sscanf asymmetry
        // (l counts trailing non-digit chars sscanf itself ignores) was only tested at l<4
        // ("99:" above). This is the same asymmetry at l>=4, where it actually changes the
        // eligibility outcome depending on the PARSED value, not the full remainder's length.
        Assert.Equal(expectedEligible, FskStationIdWireFormat.IsCompactEligible(remainder, out _));
    }

    [Fact]
    public void FilterNrRstChars_KeepsOnlyAsciiFrom0x30To0x7F()
    {
        // Main.cpp:6931's `if (*sp >= '0')` is a SIGNED-char comparison in the original C++, so
        // bytes >= 0x80 are negative there and get dropped -- an explicit upper bound reproduces
        // that; a bare `>= '0'` on an unsigned/UTF-16 char would incorrectly keep them.
        var input = "5" + (char)0x08 + "9" + (char)0x9 + "9" + (char)0x80 + "1" + (char)0xFF + "2";
        var filtered = FskStationIdWireFormat.FilterNrRstChars(input);

        Assert.Equal("59912", filtered);
    }

    [Fact]
    public void FilterNrRstChars_DropsCharactersBelowAsciiZero()
    {
        var filtered = FskStationIdWireFormat.FilterNrRstChars("5#9!9-1");

        // '#'=0x23, '!'=0x21, '-'=0x2D are all < '0'=0x30 and get dropped.
        Assert.Equal("5991", filtered);
    }

    [Fact]
    public void FilterNrRstChars_InclusiveBoundaries_0x30And0x7F_AreKept()
    {
        // Closes a coverage gap flagged by Tier A Batch 8 chunk 8b: the existing tests exercise only
        // the DROPPED side of both bounds (below 0x30, at/above 0x80) -- the two inclusive edges the
        // filter's own `>= '0' and <= 0x7F` condition claims to KEEP were never directly asserted.
        var filtered = FskStationIdWireFormat.FilterNrRstChars((char)0x30 + "9" + (char)0x7F);

        Assert.Equal("0" + "9" + (char)0x7F, filtered);
    }
}
