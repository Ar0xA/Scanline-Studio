namespace Yoniq.Core.Sstv;

/// <summary>
/// VIS (Vertical Interval Signaling) header: a fixed leader/break/leader tone sequence followed by
/// a start bit, 7 data bits (LSB first) encoding the mode's VIS code, an even parity bit, and a
/// stop bit. Shared by the encoder and decoder so both sides agree on bit order — see the parity
/// caveat on <see cref="Yoniq.Abstractions.Sstv.SstvModeDefinition"/>: this bit order is internally
/// consistent but not yet cross-checked against the legacy binary or a real VIS decoder.
/// </summary>
internal static class VisHeader
{
    public const double LeaderDurationMs = 300;
    public const double LeaderFrequencyHz = 1900;
    public const double BreakDurationMs = 10;
    public const double BreakFrequencyHz = 1200;
    public const double BitDurationMs = 30;
    public const double StartStopFrequencyHz = 1200;
    public const double Bit1FrequencyHz = 1100;
    public const double Bit0FrequencyHz = 1300;

    public const int DataBitCount = 7;

    /// <summary>Escape marker for the two-byte "extended VIS" mechanism (MR/MP/ML families) — see
    /// <see cref="GenerateExtendedSegments"/>. Legacy's raw literal for this byte is 0x23, which
    /// happens to already have its top bit 0 without matching the even-parity formula normal codes
    /// use (verified: 0x23's low 7 bits have odd parity, but bit7 is 0, not the 1 an even-parity
    /// scheme would compute) — legacy's RX doesn't check parity validity at all, it just matches
    /// raw bit patterns (`Main.cpp`'s `case 2: case 9:` VIS decode), so this port doesn't either.</summary>
    public const int ExtendedVisEscapeCode = 0x23;

    public const double PrefixDurationMs =
        LeaderDurationMs + BreakDurationMs + LeaderDurationMs // leader-break-leader
        + BitDurationMs // start bit
        + BitDurationMs * DataBitCount; // first 7 data bits (enough to detect the escape code)

    public const double NormalTailDurationMs =
        BitDurationMs // parity bit
        + BitDurationMs; // stop bit

    public const double ExtendedTailDurationMs =
        BitDurationMs // 8th bit of the escape byte
        + BitDurationMs * 8 // second byte (the actual extended mode code), raw, 8 bits
        + BitDurationMs; // stop bit

    public const double TotalDurationMs = PrefixDurationMs + NormalTailDurationMs;

    /// <summary>Legacy's real RM12 VIS byte is <c>0x86</c> (`sstv.cpp:1997`) — its parity bit (1)
    /// does not match even parity computed from its 7 data bits (6 = 0b0000110, two set bits,
    /// already even → computed parity would be 0, producing 0x06 instead). Every other normal VIS
    /// byte in the legacy table was independently re-checked bit-by-bit and does follow even
    /// parity, so this is a one-off quirk in legacy's own assigned byte, not a flaw in this port's
    /// parity computation — and since legacy's VIS-decode switch (`sstv.cpp:1993-2074`) matches the
    /// literal received byte, transmitting a correctly-computed-but-different parity bit would make
    /// this port's RM12 unrecognizable to a real legacy receiver. Passed to
    /// <see cref="GenerateSegments"/>'s <c>forcedParityBit</c> parameter for this one mode only.
    /// Decode is unaffected: <see cref="DecodeVisCode"/> only ever reads the 7 data bits, never the
    /// parity bit, for every mode.</summary>
    public const int Rm12ForcedParityBit = 1;

    public static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateSegments(int visCode, int? forcedParityBit = null)
    {
        yield return (LeaderFrequencyHz, LeaderDurationMs);
        yield return (BreakFrequencyHz, BreakDurationMs);
        yield return (LeaderFrequencyHz, LeaderDurationMs);
        yield return (StartStopFrequencyHz, BitDurationMs);

        var parity = 0;
        for (var bitIndex = 0; bitIndex < DataBitCount; bitIndex++)
        {
            var bit = (visCode >> bitIndex) & 1;
            parity ^= bit;
            yield return (bit == 1 ? Bit1FrequencyHz : Bit0FrequencyHz, BitDurationMs);
        }

        var parityBit = forcedParityBit ?? parity;
        yield return (parityBit == 1 ? Bit1FrequencyHz : Bit0FrequencyHz, BitDurationMs);
        yield return (StartStopFrequencyHz, BitDurationMs);
    }

    /// <summary>Reconstructs the VIS code from 7 measured bits (LSB first, matching <see cref="GenerateSegments"/>).</summary>
    public static int DecodeVisCode(ReadOnlySpan<int> dataBits)
    {
        var visCode = 0;
        for (var bitIndex = 0; bitIndex < dataBits.Length; bitIndex++)
        {
            visCode |= dataBits[bitIndex] << bitIndex;
        }

        return visCode;
    }

    /// <summary>
    /// "Extended VIS" for the MR/MP/ML families: after the normal leader/break/leader/start-bit
    /// prefix, transmits 16 raw bits back-to-back (LSB first) — low byte is
    /// <see cref="ExtendedVisEscapeCode"/> (0x23), high byte is <paramref name="rawExtendedCode"/>
    /// — then a single stop bit. No separate start/stop bit between the two bytes, and no computed
    /// parity: both bytes are transmitted as literal raw values, matching legacy's `Main.cpp` TX
    /// code (`if (d >= 0x100) { for 16 bits... }`) exactly rather than reusing this port's own
    /// computed-parity convention for normal single-byte VIS codes.
    /// </summary>
    public static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateExtendedSegments(int rawExtendedCode)
    {
        yield return (LeaderFrequencyHz, LeaderDurationMs);
        yield return (BreakFrequencyHz, BreakDurationMs);
        yield return (LeaderFrequencyHz, LeaderDurationMs);
        yield return (StartStopFrequencyHz, BitDurationMs);

        var combined = ExtendedVisEscapeCode | (rawExtendedCode << 8);
        for (var bitIndex = 0; bitIndex < 16; bitIndex++)
        {
            var bit = (combined >> bitIndex) & 1;
            yield return (bit == 1 ? Bit1FrequencyHz : Bit0FrequencyHz, BitDurationMs);
        }

        yield return (StartStopFrequencyHz, BitDurationMs);
    }

    /// <summary>Reconstructs a raw byte from 8 measured bits (LSB first), no parity involved —
    /// used for the second byte of <see cref="GenerateExtendedSegments"/>.</summary>
    public static int DecodeRawByte(ReadOnlySpan<int> bits)
    {
        var value = 0;
        for (var bitIndex = 0; bitIndex < bits.Length; bitIndex++)
        {
            value |= bits[bitIndex] << bitIndex;
        }

        return value;
    }

    // --- MN/MC ("narrow") mode-announce packet --- see SstvModeDefinition.NarrowModeCode's doc
    // comment for why this exists instead of a VIS/extended-VIS code, and Main.cpp:7395-7424 /
    // sstv.cpp:2942 (WriteFSK) / sstv.h:705-707 (FSKGARD/FSKINTVAL/FSKSPACE) for the source this is
    // ported from.

    /// <summary>FSKSPACE, sstv.h:707 — the "space"/0-bit tone for <c>WriteFSK</c>, and also the
    /// guard-tone frequency preceding the bit stream.</summary>
    public const double NarrowSpaceFrequencyHz = 2100;

    /// <summary>FSKGARD, sstv.h:705 — guard-tone duration (ms) at <see cref="NarrowSpaceFrequencyHz"/>
    /// before the start-bit training pulse.</summary>
    public const double NarrowGuardDurationMs = 100;

    /// <summary>FSKINTVAL, sstv.h:706 — one FSK bit's duration (ms). Also used for the single
    /// start-bit training pulse before the byte stream.</summary>
    public const double NarrowBitDurationMs = 22;

    /// <summary>Leader tone before the narrow-mode packet, <c>Main.cpp:7396</c> —
    /// <c>mp-&gt;Write(1900, 300)</c>. Same frequency as <see cref="LeaderFrequencyHz"/>
    /// (coincidentally; this is a distinct, unrelated leader from the normal-VIS one) and also
    /// <c>WriteFSK</c>'s mark/1-bit tone.</summary>
    public const double NarrowLeaderDurationMs = 300;

    /// <summary>0x2d — the packet's fixed first byte (<c>Main.cpp:7399</c>), distinguishing this
    /// mode-announce packet from the general station-ID packet, which starts with 0x2a instead
    /// (<c>sstv.cpp</c>'s <c>DecodeFSK</c>, case 4: <c>m_fskc == 0x2a</c> vs <c>0x2d</c>).</summary>
    public const int NarrowStxByte = 0x2d;

    /// <summary>0x15 — fixed second byte (<c>Main.cpp:7400</c>); also, by construction, the XOR
    /// mask applied to the mode code to form the checksum byte (<c>Main.cpp:7423</c>:
    /// <c>WriteFSK(BYTE(d^0x15))</c>).</summary>
    public const int NarrowMarkerByte = 0x15;

    /// <summary>Number of raw bytes in the packet: STX, marker, mode code, checksum.</summary>
    private const int NarrowByteCount = 4;

    public const double NarrowHeaderTotalDurationMs =
        NarrowLeaderDurationMs + NarrowGuardDurationMs + NarrowBitDurationMs // leader + guard + start-bit train
        + NarrowBitDurationMs * 6 * NarrowByteCount; // 4 bytes, 6 bits each (WriteFSK only ever sends 6 bits/byte)

    /// <summary>Ported directly from <c>CSSTVMOD::WriteFSK</c> (<c>sstv.cpp:2942</c>): 6 bits,
    /// LSB first, bit=1 sent as <see cref="LeaderFrequencyHz"/> (1900Hz), bit=0 sent as
    /// <see cref="NarrowSpaceFrequencyHz"/> (2100Hz) — legacy only ever transmits/reads the low 6
    /// bits of a byte this way, so higher bits are silently dropped, matching the source's own
    /// 6-iteration loop rather than a general 8-bit byte encoding.</summary>
    private static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateFskBits(byte value)
    {
        for (var bitIndex = 0; bitIndex < 6; bitIndex++)
        {
            yield return ((value & 1) == 1 ? LeaderFrequencyHz : NarrowSpaceFrequencyHz, NarrowBitDurationMs);
            value = (byte)(value >> 1);
        }
    }

    /// <summary>Generates the MN/MC mode-announce packet in place of a VIS header — see
    /// <c>Main.cpp:7395-7424</c>: leader, guard, one start-bit training pulse, then
    /// <c>[0x2d][0x15][modeCode][modeCode^0x15]</c>, 6 bits each via <see cref="GenerateFskBits"/>.
    /// No stop bit and no leader-break-leader — this packet's framing is entirely different from
    /// <see cref="GenerateSegments"/>/<see cref="GenerateExtendedSegments"/>, not a variant of
    /// either.</summary>
    public static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateNarrowModeSegments(int modeCode)
    {
        yield return (LeaderFrequencyHz, NarrowLeaderDurationMs);
        yield return (NarrowSpaceFrequencyHz, NarrowGuardDurationMs);
        yield return (LeaderFrequencyHz, NarrowBitDurationMs); // start-bit training pulse

        var checksum = (byte)((modeCode ^ NarrowMarkerByte) & 0xFF);
        foreach (var b in new[] { (byte)NarrowStxByte, (byte)NarrowMarkerByte, (byte)modeCode, checksum })
        {
            foreach (var segment in GenerateFskBits(b))
            {
                yield return segment;
            }
        }
    }

    // --- Scottie-specific post-VIS pulse --- Main.cpp:7576-7578: after the shared VIS-block loop,
    // legacy does `else if ((TxMode==smSCT1)||(TxMode==smSCT2)||(TxMode==smSCTDX)) mp->Write(1200,9.0);`.

    /// <summary>Extra pulse legacy emits right after the VIS stop bit for Scottie modes only
    /// (`Main.cpp:7576-7578`: <c>mp-&gt;Write(1200, 9.0)</c>), needed because legacy's RX VIS-decode
    /// state machine (`sstv.cpp:2127-2153`) expects 1200Hz still present ~30ms after the last VIS
    /// bit, and Scottie's own line generator (<c>LineSCT</c>) starts directly on a 1500Hz separator
    /// with no sync of its own at the start of the line.</summary>
    public const double ScottiePostVisPulseFrequencyHz = StartStopFrequencyHz; // 1200Hz

    /// <summary>Duration of <see cref="ScottiePostVisPulseFrequencyHz"/>'s pulse, `Main.cpp:7577`.</summary>
    public const double ScottiePostVisPulseDurationMs = 9.0;

    // --- AVT-specific header --- Main.cpp:7429 (`int e = (TxMode == smAVT) ? 3 : 1;`) repeats the
    // whole VIS block 3x for AVT only, then Main.cpp:7563-7575 appends a long sync/AFC training
    // sequence unique to this mode before any line data starts. Legacy's own RX budgets for exactly
    // this preamble length at sstv.cpp:2140 (9 + 910 + 910 + 5311.9424 + 0.30514375).

    /// <summary>Number of times the VIS block itself is transmitted for AVT (`Main.cpp:7429`) — 3,
    /// vs. 1 for every other mode.</summary>
    public const int AvtVisRepeatCount = 3;

    /// <summary>Duration of one full VIS transmission (leader/break/leader/start/7 data
    /// bits/parity/stop) — reused here since AVT repeats exactly this shape.</summary>
    public const double AvtVisBlockDurationMs = PrefixDurationMs + NormalTailDurationMs;

    /// <summary>Duration of the marker tone and each of the 16 data bits in the AVT training
    /// sequence, `Main.cpp:7566-7573` (all <c>Write(..., 9.7646)</c>).</summary>
    public const double AvtTrainingBitDurationMs = 9.7646;

    /// <summary>Number of 17-segment (1 marker + 16 bits) blocks in the training sequence,
    /// `Main.cpp:7564` (<c>for (i = 0; i &lt; 32; i++)</c>).</summary>
    public const int AvtTrainingBlockCount = 32;

    /// <summary>Bits per training block, `Main.cpp:7568` (<c>for (n = 0; n &lt; 16; n++)</c>).</summary>
    public const int AvtTrainingBitsPerBlock = 16;

    /// <summary>Marker tone preceding each training block's 16 bits, `Main.cpp:7566`
    /// (<c>Write(1900, ...)</c>) — same frequency as <see cref="LeaderFrequencyHz"/>, coincidentally.</summary>
    public const double AvtTrainingMarkerFrequencyHz = LeaderFrequencyHz;

    /// <summary>Bit=1 tone in the AVT training sequence, `Main.cpp:7568`
    /// (<c>d &amp; 0x8000 ? 1600 : 2200</c>).</summary>
    public const double AvtTrainingOneFrequencyHz = 1600;

    /// <summary>Bit=0 tone, see <see cref="AvtTrainingOneFrequencyHz"/>.</summary>
    public const double AvtTrainingZeroFrequencyHz = 2200;

    /// <summary>Trailing near-silent blip after the training sequence, `Main.cpp:7575`
    /// (<c>Write(0, 0.30514375)</c>) — legacy writes a literal 0Hz tone.</summary>
    public const double AvtTrainingTailFrequencyHz = 0;

    /// <summary>Duration of <see cref="AvtTrainingTailFrequencyHz"/>'s blip, `Main.cpp:7575`.</summary>
    public const double AvtTrainingTailDurationMs = 0.30514375;

    /// <summary>Total training-sequence duration: 32 blocks of (1 marker + 16 bits) at
    /// <see cref="AvtTrainingBitDurationMs"/> each, plus the trailing blip.</summary>
    public const double AvtTrainingSequenceDurationMs =
        AvtTrainingBlockCount * AvtTrainingBitDurationMs * (1 + AvtTrainingBitsPerBlock)
        + AvtTrainingTailDurationMs;

    /// <summary>Total extra duration AVT's header adds beyond one normal VIS transmission: two
    /// extra VIS repeats plus the training sequence. Used by the decoder to know how far to skip
    /// past the header once the mode has been identified from the first VIS repeat.</summary>
    public const double AvtExtraHeaderDurationMs =
        (AvtVisRepeatCount - 1) * AvtVisBlockDurationMs + AvtTrainingSequenceDurationMs;

    /// <summary>AVT-specific header: the VIS block repeated <see cref="AvtVisRepeatCount"/> times
    /// (`Main.cpp:7429`), then the training sequence (`Main.cpp:7563-7575`) — see this section's
    /// constants for exact source citations. The training sequence's bit pattern is generated
    /// exactly as legacy does: a 16-bit shift register <c>sd</c> seeded at <c>0x5fa0</c>, whose top
    /// byte decrements and bottom byte increments by 1 each of the 32 blocks
    /// (`Main.cpp:7574`: <c>sd = ((sd &amp; 0xff00) - 0x0100) | ((sd &amp; 0x00ff) + 0x0001)</c>).</summary>
    public static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateAvtSegments(int visCode)
    {
        for (var i = 0; i < AvtVisRepeatCount; i++)
        {
            foreach (var segment in GenerateSegments(visCode))
            {
                yield return segment;
            }
        }

        var sd = 0x5fa0;
        for (var i = 0; i < AvtTrainingBlockCount; i++)
        {
            yield return (AvtTrainingMarkerFrequencyHz, AvtTrainingBitDurationMs);

            var d = sd;
            for (var n = 0; n < AvtTrainingBitsPerBlock; n++)
            {
                yield return ((d & 0x8000) != 0 ? AvtTrainingOneFrequencyHz : AvtTrainingZeroFrequencyHz, AvtTrainingBitDurationMs);
                d <<= 1;
            }

            sd = ((sd & 0xff00) - 0x0100) | ((sd & 0x00ff) + 0x0001);
        }

        yield return (AvtTrainingTailFrequencyHz, AvtTrainingTailDurationMs);
    }
}
