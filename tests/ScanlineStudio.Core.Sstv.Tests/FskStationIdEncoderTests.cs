namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Golden-vector tests for <see cref="FskStationIdEncoder"/>, hand-derived from legacy
/// <c>OutputFSKID</c> (`Main.cpp:6903-6965`) directly. Every expected byte's bit sequence is built
/// via <see cref="Bits"/>, an independent restatement of legacy <c>WriteFSK</c>'s spec
/// (`sstv.cpp:2942-2949`: 6 bits, LSB first, 1=1900Hz/0=2100Hz) written from the legacy source, not
/// copied from <see cref="VisHeader.GenerateFskBits"/> -- so this doesn't just check the encoder
/// against itself.
/// </summary>
public class FskStationIdEncoderTests
{
    private const double GuardMs = 100.0;
    private const double PulseMs = 22.0;
    private const double MarkHz = 1900.0;
    private const double SpaceHz = 2100.0;

    private static IEnumerable<(double FrequencyHz, double DurationMs)> Bits(int value)
    {
        for (var i = 0; i < 6; i++)
        {
            yield return ((value & 1) == 1 ? MarkHz : SpaceHz, PulseMs);
            value >>= 1;
        }
    }

    [Fact]
    public void Generate_EmptyCallsign_YieldsNothing()
    {
        // Main.cpp:7017: the caller never invokes OutputFSKID at all for an empty callsign -- not
        // "invokes it and it emits an empty packet shell".
        var segments = FskStationIdEncoder.Generate(string.Empty, null).ToList();

        Assert.Empty(segments);
    }

    [Fact]
    public void Generate_CallsignOnly_MatchesHandDerivedByteSequence()
    {
        // Callsign "AB": 'A'-0x20=0x21, 'B'-0x20=0x22. checksum = 0x21 ^ 0x22 = 0x03.
        var expected = new List<(double, double)> { (SpaceHz, GuardMs), (MarkHz, PulseMs) }; // preamble, no leader
        expected.AddRange(Bits(0x2a)); // STX, raw
        expected.AddRange(Bits(0x21)); // 'A' - 0x20
        expected.AddRange(Bits(0x22)); // 'B' - 0x20
        expected.AddRange(Bits(0x01)); // EOT, raw
        expected.AddRange(Bits(0x03)); // checksum = 0x21 ^ 0x22
        expected.Add((SpaceHz, GuardMs)); // closing guard

        var segments = FskStationIdEncoder.Generate("AB", null).ToList();

        Assert.Equal(expected, segments);
    }

    [Fact]
    public void Generate_CallsignPlusCompactNr_AppendsTwoByteFormWithSeededChecksum_NoEot()
    {
        // "His RST" text "599123": filtered (all digits, all kept) = "599123", skip first 3 ("599")
        // -> remainder "123". l=3<4 -> compact eligible (per IsCompactEligible's own tests),
        // value=123 = 0x7B. high = (123>>6)&0x3f = 0x01. low = 123&0x3f = 0x3B.
        // checksum seeded 0x02, then XORed with high then low: 0x02^0x01=0x03, 0x03^0x3B=0x38.
        var expected = new List<(double, double)> { (SpaceHz, GuardMs), (MarkHz, PulseMs) };
        expected.AddRange(Bits(0x2a));
        expected.AddRange(Bits(0x21)); // 'A'-0x20 for callsign "A"
        expected.AddRange(Bits(0x01)); // EOT
        expected.AddRange(Bits(0x21)); // checksum = 0x21 (single-char callsign "A")
        expected.AddRange(Bits(0x02)); // NR seed byte, sent as the first NR byte
        expected.AddRange(Bits(0x01)); // high 6 bits of 123 = 0x01
        expected.AddRange(Bits(0x3B)); // low 6 bits of 123 = 0x3B
        expected.AddRange(Bits(0x38)); // NR checksum = 0x02 ^ 0x01 ^ 0x3B
        expected.Add((SpaceHz, GuardMs));

        var segments = FskStationIdEncoder.Generate("A", "599123").ToList();

        Assert.Equal(expected, segments);
    }

    [Fact]
    public void Generate_CallsignPlusStringNr_UppercasesAndAppendsEot_IndependentChecksum()
    {
        // "His RST" text "599ab1" -- filtered keeps only chars >= '0': '5','9','9','a','b','1' all
        // pass the >= '0' filter (letters are >= '0' in ASCII), giving filtered="599ab1", skip
        // first 3 ("599") -> remainder "ab1". Contains letters -> NOT compact-eligible -> string
        // form: uppercased "AB1". 'A'-0x20=0x21, 'B'-0x20=0x22, '1'-0x20=0x11.
        // checksum = 0 ^ 0x21 ^ 0x22 ^ 0x11 = 0x12.
        var expected = new List<(double, double)> { (SpaceHz, GuardMs), (MarkHz, PulseMs) };
        expected.AddRange(Bits(0x2a));
        expected.AddRange(Bits(0x21)); // 'A'-0x20 for callsign "A"
        expected.AddRange(Bits(0x01)); // EOT
        expected.AddRange(Bits(0x21)); // callsign checksum
        expected.AddRange(Bits(0x21)); // 'A'
        expected.AddRange(Bits(0x22)); // 'B'
        expected.AddRange(Bits(0x11)); // '1'
        expected.AddRange(Bits(0x01)); // EOT (string form DOES send one)
        expected.AddRange(Bits(0x12)); // NR checksum = 0x21^0x22^0x11
        expected.Add((SpaceHz, GuardMs));

        var segments = FskStationIdEncoder.Generate("A", "599ab1").ToList();

        Assert.Equal(expected, segments);
    }

    [Fact]
    public void Generate_NrRstEnabledButRemainderTooShort_NoSubPacketEmitted()
    {
        // Main.cpp:6936: `if (strlen(p) > 3)` -- filtered "5991" has length 4, so this check
        // actually PASSES (4>3); use a genuinely short one, "599" (length 3, not > 3), to hit the
        // no-sub-packet path.
        var withoutNr = FskStationIdEncoder.Generate("A", null).ToList();
        var withShortNr = FskStationIdEncoder.Generate("A", "599").ToList();

        Assert.Equal(withoutNr, withShortNr);
    }

    [Fact]
    public void Generate_NrRstNull_OmitsSubPacketEntirely()
    {
        var segments = FskStationIdEncoder.Generate("A", null).ToList();

        // 2 preamble + 6*4 bytes (STX, one callsign char, EOT, checksum) + 1 closing guard.
        Assert.Equal(2 + 6 * 4 + 1, segments.Count);
    }
}
