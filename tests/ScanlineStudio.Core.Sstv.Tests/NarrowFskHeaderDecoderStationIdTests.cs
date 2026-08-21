namespace ScanlineStudio.Core.Sstv.Tests;

/// <summary>
/// Golden-vector and behavioral tests for <see cref="NarrowFskHeaderDecoder"/>'s station-ID (STX
/// 0x2a, legacy modes 5-10, `sstv.cpp:2465-2551`) continuation. Shares its wire-byte fixtures with
/// <c>FskStationIdEncoderTests</c> (same callsign/NR inputs, same expected checksum bytes) rather
/// than just round-tripping against that encoder -- proving both sides independently match legacy,
/// not just each other (Scottie-incident precedent, `CLAUDE.md` §4).
/// </summary>
public class NarrowFskHeaderDecoderStationIdTests
{
    private const int SampleRate = 11025;
    private const int Mark = 8192;
    private const int Space = 8192;

    private static int MsToSamples(double ms) => (int)Math.Round(ms / 1000.0 * SampleRate);

    private static void FeedGuardAndStartBit(NarrowFskHeaderDecoder decoder)
    {
        FeedConstant(decoder, MsToSamples(VisHeader.NarrowGuardDurationMs), 0, Space);
        FeedConstant(decoder, MsToSamples(VisHeader.NarrowBitDurationMs), Mark, 0);
    }

    private static void FeedConstant(NarrowFskHeaderDecoder decoder, int count, int m, int s)
    {
        for (var i = 0; i < count; i++)
        {
            decoder.ProcessSample(m, s);
        }
    }

    /// <summary>Feeds guard + start-bit + an arbitrary raw 6-bit wire-byte sequence (already
    /// offset/checksummed by the caller), collecting every non-null <see cref="FskDecodeResult"/>
    /// produced along the way (not just the last one -- the two-stage callsign/NR commit means more
    /// than one can fire in a single feed).</summary>
    private static List<FskDecodeResult> FeedRawBytes(NarrowFskHeaderDecoder decoder, IEnumerable<int> wireBytes)
    {
        FeedGuardAndStartBit(decoder);

        var results = new List<FskDecodeResult>();
        var samplesInBit = MsToSamples(VisHeader.NarrowBitDurationMs);
        foreach (var value in wireBytes)
        {
            for (var bitIndex = 0; bitIndex < 6; bitIndex++)
            {
                var bit = (value >> bitIndex) & 1;
                for (var i = 0; i < samplesInBit; i++)
                {
                    var sample = bit == 1 ? decoder.ProcessSample(Mark, 0) : decoder.ProcessSample(0, Space);
                    if (sample is not null)
                    {
                        results.Add(sample.Value);
                    }
                }
            }
        }

        return results;
    }

    /// <summary>Independently re-derives the exact same "A"/"599123" wire bytes as
    /// <c>FskStationIdEncoderTests.Generate_CallsignPlusCompactNr_...</c> (hand-computed there:
    /// checksum bytes 0x21/0x38) -- shared fixture, not shared code.</summary>
    private static readonly int[] CallsignAThenCompactNr123 =
    [
        0x2a, // STX
        0x21, // 'A' - 0x20
        0x01, // EOT
        0x21, // callsign checksum
        0x02, // compact-NR marker/checksum-seed
        0x01, // high 6 bits of 123
        0x3B, // low 6 bits of 123
        0x38, // NR checksum
    ];

    /// <summary>Independently re-derives "A"/"599ab1" (hand-computed in
    /// <c>FskStationIdEncoderTests</c>: checksum bytes 0x21/0x12).</summary>
    private static readonly int[] CallsignAThenStringNrAb1 =
    [
        0x2a, 0x21, 0x01, 0x21, // callsign packet, identical to above
        0x21, 0x22, 0x11, // 'A','B','1' each -0x20 (uppercased "AB1")
        0x01, // EOT
        0x12, // NR checksum
    ];

    [Fact]
    public void CallsignPlusCompactNr_SharedGoldenVector_TwoStageCommit_CallsignThenCompactNr()
    {
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };

        var results = FeedRawBytes(decoder, CallsignAThenCompactNr123);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].StationIdCallsign);
        Assert.Null(results[0].ModeCode);
        Assert.Equal(123u, results[1].StationIdCompactNr);
        Assert.Null(results[1].ModeCode);
    }

    [Fact]
    public void CallsignPlusStringNr_SharedGoldenVector_TwoStageCommit_CallsignThenNrText()
    {
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };

        var results = FeedRawBytes(decoder, CallsignAThenStringNrAb1);

        Assert.Equal(2, results.Count);
        Assert.Equal("A", results[0].StationIdCallsign);
        Assert.Equal("AB1", results[1].StationIdNrText);
        Assert.Null(results[1].StationIdCompactNr);
    }

    [Fact]
    public void StationIdDecodeEnabled_False_CorrectChecksumStillDoesNotCommit()
    {
        // sstv.cpp:2485: `(m_fskc == m_fsks) && m_fskdecode` -- a correct checksum ALONE is not
        // sufficient, the RX-enable flag gates the commit too.
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = false };

        var results = FeedRawBytes(decoder, CallsignAThenCompactNr123);

        Assert.Empty(results);
    }

    [Fact]
    public void EmptyCallsignBeforeEot_DoesNotCommit_ResetsCleanly()
    {
        // sstv.cpp:2467: `if (m_fskcnt >= 1) mode++; else mode = 0;` -- EOT as the very first byte
        // (no callsign chars at all) resets rather than committing an empty callsign.
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };

        var results = FeedRawBytes(decoder, [0x2a, 0x01]); // STX then immediate EOT, no chars

        Assert.Empty(results);
    }

    [Fact]
    public void CallsignOverflow_MoreThan16Chars_AbortsWithoutCommitting()
    {
        // sstv.cpp:2478: `if (m_fskcnt >= 17) mode = 0;` -- an over-long callsign (never reaching a
        // terminating EOT within the cap) never commits.
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var wireBytes = new List<int> { 0x2a };
        wireBytes.AddRange(Enumerable.Repeat('X' - 0x20, 20)); // 20 chars, well past the 16 cap

        var results = FeedRawBytes(decoder, wireBytes);

        Assert.Empty(results);
    }

    [Fact]
    public void BadChecksum_DoesNotCommit_ThenResumesScanning_AndLocksNextValidStationIdPacket()
    {
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };

        var badChecksumPacket = new List<int> { 0x2a, 0x21, 0x01, 0x00 }; // wrong checksum (not 0x21)
        var firstAttempt = FeedRawBytes(decoder, badChecksumPacket);
        Assert.Empty(firstAttempt);

        var secondAttempt = FeedRawBytes(decoder, new List<int> { 0x2a, 0x21, 0x01, 0x21 }); // correct
        Assert.Single(secondAttempt);
        Assert.Equal("A", secondAttempt[0].StationIdCallsign);
    }

    [Fact]
    public void CompactNrMarker_WithNonZeroCarriedSubPacketCount_ConsumesOnlyOneHalf_NotTwo()
    {
        // Closes a coverage gap flagged by Tier A Batch 6 chunk 6e (docs/functional-audit-playbook.md):
        // mode 7's compact-NR marker byte (0x02) deliberately does NOT reset the shared sub-packet
        // counter (sstv.cpp:2509-2513 sets only m_fsks/m_fskNR/m_fskmode, never m_fskcnt) -- an
        // earlier version of this port wrongly reset it. If corrupt/malformed input already
        // accumulated an NR-string char before the marker byte arrives, legacy carries that nonzero
        // count into mode 9, which only needs ONE more 6-bit half (not the normal two) to reach its
        // own >=2 threshold. Byte sequence: STX, callsign "A", EOT, callsign checksum, ONE leftover
        // NR-string char (0x21, which is >=0x10 so mode 7 treats it as raw text, count becomes 1),
        // the 0x02 compact-NR marker, ONE compact-NR half (0x05), then the checksum
        // (0x02^0x05=0x07). If the marker byte wrongly reset the count to 0, mode 9 would need a
        // SECOND half before transitioning, and this sequence (which supplies only one) would
        // misinterpret the checksum byte as a second NR half instead of committing.
        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var wireBytes = new[] { 0x2a, 0x21, 0x01, 0x21, 0x21, 0x02, 0x05, 0x07 };

        var results = FeedRawBytes(decoder, wireBytes);

        Assert.Equal(2, results.Count); // the callsign commit, then the compact-NR commit
        Assert.Equal("A", results[0].StationIdCallsign);
        Assert.Equal(5u, results[1].StationIdCompactNr);
    }

    [Fact]
    public void CompactNrRoundTrip_MatchesFskStationIdWireFormat_IsCompactEligible()
    {
        // Cross-check against Phase 2's own predicate: 123 with l=3 (a 3-char remainder) is
        // compact-eligible per FskStationIdWireFormatTests -- this confirms the DECODER'S bit-level
        // reconstruction of that same value (via the high/low 6-bit halves) is self-consistent with
        // what the predicate that decided to encode it that way expects.
        Assert.True(FskStationIdWireFormat.IsCompactEligible("123", out var expected));

        var decoder = new NarrowFskHeaderDecoder(SampleRate) { StationIdDecodeEnabled = true };
        var results = FeedRawBytes(decoder, CallsignAThenCompactNr123);

        Assert.Equal(expected, results[1].StationIdCompactNr);
    }
}
