using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// Direct port of legacy <c>TMmsstv::OutputFSKID</c> (`Main.cpp:6903-6965`) -- the FSK
/// callsign-ID packet plus its optional chained NR/RST sub-packet. Uses the same physical-layer
/// tone generator as the already-ported mode-announce packet (<see cref="VisHeader.GenerateFskBits"/>)
/// but a DIFFERENT preamble shape: no leading 300ms leader tone (unlike
/// <see cref="VisHeader.GenerateNarrowModeSegments"/>), just guard tone then a single start-bit
/// pulse -- confirmed by reading `Main.cpp:6903-6965` directly, not assumed from the mode-announce
/// packet's shape.
///
/// Wire-protocol byte-exactness matters here: real legacy YONIQ/MMSSTV stations must be able to
/// decode this port's packets and vice versa. Two easy-to-miss details verified against source: the
/// EOT byte (`0x01`) is transmitted but is NOT XORed into the checksum on either the callsign or
/// NR-string path; STX/EOT/checksum bytes are sent RAW through <see cref="VisHeader.GenerateFskBits"/>
/// (no `-0x20` offset -- that offset applies only to actual callsign/NR text characters, applied by
/// this class before each such character is encoded).
///
/// Callers must normalize the callsign (uppercase, trim, length cap -- see
/// <see cref="StationIdCallsignNormalizer"/>) at the settings boundary before calling
/// <see cref="Generate"/> -- this class does not validate or throw, matching this project's
/// "validate at the boundary, the encoder itself never throws" policy (an in-flight,
/// lazily-enumerated transmission must not abort mid-stream). Doc correction (Tier A Batch 8 chunk
/// 8b): <see cref="StationIdCallsignNormalizer"/> does NOT do printable-range validation -- a
/// non-ASCII character reaches this class's own `(byte)(ch - 0x20)` cast as a raw UTF-16 code unit,
/// diverging from legacy's `BYTE(*p - 0x20)` over CP932 BYTES (one legacy byte per char here, vs. one
/// or two CP932 bytes there for a double-byte character) -- the same divergence class
/// <see cref="FskStationIdWireFormat.FilterNrRstChars"/> already documents for the NR/RST field,
/// just not previously acknowledged here. Garbage-either-way for any real callsign (ASCII by
/// convention), not a reachable bug. See <see cref="FskStationIdWireFormat"/> for the NR/RST
/// compact-vs-string predicate and character filter this class delegates to.
/// </summary>
internal static class FskStationIdEncoder
{
    private const int StxByte = 0x2a;
    private const int EotByte = 0x01;
    private const int CompactNrChecksumSeed = 0x02;

    /// <summary>Generates one full station-ID transmission: opening guard, callsign packet,
    /// optional NR/RST sub-packet (only if <paramref name="rawNrRstText"/> is non-null, mirroring
    /// legacy's <c>Log.m_LogSet.m_FSKNR</c> gate -- pass null when that setting is disabled), and
    /// the closing guard tone. An empty <paramref name="callsign"/> yields nothing at all, matching
    /// legacy's post-image trigger gate, `Main.cpp:7018`'s `!sys.m_Call.IsEmpty()` -- the caller
    /// there never invokes <c>OutputFSKID</c> in the first place, not "invokes it and it does
    /// nothing". (Code-review finding: legacy's OTHER `OutputFSKID` call site, `Main.cpp:7302`, the
    /// `#id` macro token, is NOT gated on an empty callsign the same way -- not relevant yet since
    /// that macro token isn't ported in this port; revisit this doc comment if/when it is.)</summary>
    public static IEnumerable<(double FrequencyHz, double DurationMs)> Generate(
        string callsign, string? rawNrRstText)
    {
        if (string.IsNullOrEmpty(callsign))
        {
            yield break;
        }

        yield return (VisHeader.NarrowSpaceFrequencyHz, VisHeader.NarrowGuardDurationMs);
        yield return (VisHeader.LeaderFrequencyHz, VisHeader.NarrowBitDurationMs); // start-bit pulse

        foreach (var segment in VisHeader.GenerateFskBits(StxByte))
        {
            yield return segment;
        }

        byte checksum = 0;
        foreach (var ch in callsign)
        {
            var c = (byte)(ch - 0x20);
            checksum ^= c;
            foreach (var segment in VisHeader.GenerateFskBits(c))
            {
                yield return segment;
            }
        }

        foreach (var segment in VisHeader.GenerateFskBits(EotByte))
        {
            yield return segment;
        }

        foreach (var segment in VisHeader.GenerateFskBits(checksum))
        {
            yield return segment;
        }

        if (rawNrRstText is not null)
        {
            foreach (var segment in GenerateNrRstSubPacket(rawNrRstText))
            {
                yield return segment;
            }
        }

        yield return (VisHeader.NarrowSpaceFrequencyHz, VisHeader.NarrowGuardDurationMs);
    }

    private static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateNrRstSubPacket(
        string rawNrRstText)
    {
        // Main.cpp:6930-6936: filter, then only proceed if the FILTERED length exceeds 3 (the RST
        // report digits about to be skipped).
        var filtered = FskStationIdWireFormat.FilterNrRstChars(rawNrRstText);
        if (filtered.Length <= 3)
        {
            yield break;
        }

        var remainder = filtered[3..];
        byte checksum;

        if (FskStationIdWireFormat.IsCompactEligible(remainder, out var value))
        {
            // Main.cpp:6941-6948: checksum SEEDED at 0x02 (not inherited from the callsign
            // packet), that seed value is itself the first transmitted byte, then the two 6-bit
            // halves of `value` MSB-first. No EOT for this sub-form.
            checksum = CompactNrChecksumSeed;
            foreach (var segment in VisHeader.GenerateFskBits(checksum))
            {
                yield return segment;
            }

            var high = (byte)((value >> 6) & 0x3f);
            checksum ^= high;
            foreach (var segment in VisHeader.GenerateFskBits(high))
            {
                yield return segment;
            }

            var low = (byte)(value & 0x3f);
            checksum ^= low;
            foreach (var segment in VisHeader.GenerateFskBits(low))
            {
                yield return segment;
            }
        }
        else
        {
            // Main.cpp:6950-6960: checksum RESETS to 0 (independent of the compact form's seed),
            // text is uppercased (jstrupr -- asymmetric with the callsign packet, which legacy does
            // NOT uppercase inside OutputFSKID itself, though this port's callsign already arrives
            // pre-uppercased from the settings boundary either way). EOT IS sent here (unlike the
            // compact form) but, same as the callsign packet, is NOT XORed into the checksum.
            checksum = 0;
            var upper = remainder.ToUpperInvariant();
            foreach (var ch in upper)
            {
                var c = (byte)(ch - 0x20);
                checksum ^= c;
                foreach (var segment in VisHeader.GenerateFskBits(c))
                {
                    yield return segment;
                }
            }

            foreach (var segment in VisHeader.GenerateFskBits(EotByte))
            {
                yield return segment;
            }
        }

        // Main.cpp:6961: the checksum write is OUTSIDE and shared by both branches above.
        foreach (var segment in VisHeader.GenerateFskBits(checksum))
        {
            yield return segment;
        }
    }
}
