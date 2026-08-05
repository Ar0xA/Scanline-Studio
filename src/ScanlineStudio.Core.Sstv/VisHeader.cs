namespace ScanlineStudio.Core.Sstv;

/// <summary>
/// VIS (Vertical Interval Signaling) header: a fixed leader/break/leader tone sequence followed by
/// a start bit, 7 data bits (LSB first) encoding the mode's VIS code, an even parity bit, and a
/// stop bit. Shared by the encoder and decoder so both sides agree on bit order — see the parity
/// caveat on <see cref="ScanlineStudio.Abstractions.Sstv.SstvModeDefinition"/>: this bit order is internally
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

    /// <summary>S10 fix (spec/14-roadmap.md): legacy's real <c>m_VisData</c> accumulator is always 8
    /// bits wide (<c>m_VisCnt</c> starts at 8, `sstv.cpp:1966-1967`/`2068-2069`) — 7 data bits PLUS
    /// the parity bit — and its mode-lookup <c>switch(m_VisData)</c> (`sstv.cpp:1993-2074`) matches
    /// the FULL 8-bit byte for every arm, the escape check (<c>case 0x23</c>, `sstv.cpp:2066`) included
    /// — they're arms of the same switch, not two separately-timed decisions. This is the bit count
    /// needed before the normal-vs-extended decision can be made at all; see
    /// <see cref="AnalogFmSstvDecoder.TryDecodeVisHeader"/> and <see cref="ExtendedVisEscapeCode"/>'s
    /// own doc comment for why decoding only 7 (parity-stripped) bits was a real, previously-shipped
    /// divergence from legacy on real (noisy) audio.</summary>
    public const int FirstByteBitCount = DataBitCount + 1;

    /// <summary>Escape marker for the two-byte "extended VIS" mechanism (MR/MP/ML families) — see
    /// <see cref="GenerateExtendedSegments"/>. Legacy's raw literal for this byte is 0x23, which
    /// happens to already have its top bit 0 without matching the even-parity formula normal codes
    /// use (verified: 0x23's low 7 bits have odd parity, but bit7 is 0, not the 1 an even-parity
    /// scheme would compute) — legacy's RX doesn't check parity VALIDITY (there's no formula check),
    /// but it does match the full 8-bit RAW pattern including whatever parity bit was actually
    /// received (`sstv.cpp`'s `case 2: case 9:` VIS decode, full-byte <c>switch(m_VisData)</c>) — a
    /// 7-bit-only match (this port's own pre-S10 behavior) would accept a real 0x23-pattern byte
    /// REGARDLESS of its 8th bit, which legacy would reject outright (falls to `default:`,
    /// `sstv.cpp:2071-2073`) whenever that bit doesn't happen to be 0. Compared as a full 8-bit value
    /// (<see cref="FirstByteBitCount"/>), not 7, as of the S10 fix.</summary>
    public const int ExtendedVisEscapeCode = 0x23;

    public const double PrefixDurationMs =
        LeaderDurationMs + BreakDurationMs + LeaderDurationMs // leader-break-leader
        + BitDurationMs // start bit
        + BitDurationMs * DataBitCount; // first 7 data bits -- the parity bit is part of the "tail"
                                        // duration constants below, NOT this prefix (matches
                                        // GenerateSegments' own real TX segment boundary); the
                                        // DECODER's own "enough samples to decide normal-vs-extended"
                                        // gate needs PrefixDurationMs + BitDurationMs (FirstByteBitCount
                                        // total bit-slots), one more than this constant alone -- see
                                        // TryDecodeVisHeader's own gate.

    public const double NormalTailDurationMs =
        BitDurationMs // parity bit
        + BitDurationMs; // stop bit

    public const double ExtendedTailDurationMs =
        BitDurationMs // 8th bit of the escape byte
        + BitDurationMs * 8 // second byte (the actual extended mode code), raw, 8 bits
        + BitDurationMs; // stop bit

    public const double TotalDurationMs = PrefixDurationMs + NormalTailDurationMs;

    /// <summary>Bit count of <see cref="GenerateExtendedSegments"/>'s combined escape+code word: the
    /// full first byte (<see cref="FirstByteBitCount"/>, 8 bits — S10 fix) plus the second (real
    /// extended-mode code) byte's own 8 raw bits, no separate start/stop bit between them. Named here
    /// so <see cref="ExtendedSearchCeilingMs"/> and <c>AnalogFmSstvDecoder.TryDecodeVisHeader</c>'s own
    /// call site share one constant instead of each repeating the arithmetic independently.</summary>
    public const int ExtendedDataBitCount = FirstByteBitCount + 8;

    /// <summary>Shared retry margin every local, bounded header-detection scan in this file's
    /// consumers (<c>AnalogFmSstvDecoder.TryDecodeVisDataBits</c>/<c>TryDecodeNarrowModeHeader</c>)
    /// budgets for one or two short spurious candidates (e.g. the break tone) before the real start
    /// bit, rather than aborting outright — see those methods' own doc comments for the full
    /// reasoning; this constant exists so the three search-ceiling constants below share one
    /// literal instead of three independently-copied 200s.</summary>
    public const double RetryMarginMs = 200;

    /// <summary>15ms, `sstv.cpp:1948` — how long the case-0 trigger must hold before
    /// <c>TryDecodeVisDataBits</c> starts racing the data-bit tones. Shared here so
    /// <see cref="NormalSearchCeilingMs"/>/<see cref="ExtendedSearchCeilingMs"/> don't repeat the
    /// derivation independently of that method's own identical constant.</summary>
    public const double ConfirmHoldMs = BitDurationMs / 2;

    /// <summary><c>AnalogFmSstvDecoder.TryDecodeVisDataBits</c>'s own local search-ceiling formula
    /// (see that method's doc comment for the full reasoning), for the <see cref="FirstByteBitCount"/>-bit
    /// first-byte decision (S10 fix: 8 bits, not <see cref="DataBitCount"/>'s 7 — see that constant's
    /// own doc comment) — the ONLY other consumer of this exact arithmetic (Band-1 S3 fix, pre-Phase-2
    /// audit) is <see cref="MaxSearchCeilingMs"/>, computed from this and its two siblings below rather
    /// than hand-transcribed, so the two can never silently desync.</summary>
    public const double NormalSearchCeilingMs =
        LeaderDurationMs * 2 + BreakDurationMs + RetryMarginMs
        + ConfirmHoldMs
        + FirstByteBitCount * BitDurationMs;

    /// <summary>Same formula as <see cref="NormalSearchCeilingMs"/>, for the extended
    /// (<see cref="ExtendedDataBitCount"/>-bit) VIS code.</summary>
    public const double ExtendedSearchCeilingMs =
        LeaderDurationMs * 2 + BreakDurationMs + RetryMarginMs
        + ConfirmHoldMs
        + ExtendedDataBitCount * BitDurationMs;

    /// <summary><c>AnalogFmSstvDecoder.TryDecodeNarrowModeHeader</c>'s own local search-ceiling
    /// formula (see that method's doc comment) — guard-tone hold + mode-2's own timeout window,
    /// start-bit training pulse, 24 data bits, plus the same <see cref="RetryMarginMs"/> every other
    /// local scan in this file budgets.</summary>
    public const double NarrowSearchCeilingMs =
        NarrowGuardDurationMs * 2
        + NarrowBitDurationMs * (1 + 24)
        + RetryMarginMs;

    /// <summary>The maximum of the three search ceilings above — the latest possible sample, relative
    /// to a header's own start, at which ANY of this port's fixed-window header-detection paths could
    /// still succeed. Band-1 S3 fix (pre-Phase-2 audit): <c>AnalogFmSstvDecoder</c>'s interleaved
    /// fallback scan (<c>TryInterleavedHeaderScan</c>, which races a fixed-window path that refuses to
    /// commit until its own full header duration is buffered) must not be allowed to commit before
    /// this many ms have elapsed since the current epoch's `_consumedSamples`, or it can win a race
    /// the fixed-window path was never given a real chance to lose fairly — see the roadmap's
    /// "Band-1 items 2+3" entry for the full empirical confirmation and the auditor plan-review that
    /// caught the original draft using the wrong (too small) quantity here. <c>static readonly</c>,
    /// not <c>const</c>, specifically so this is computed by the compiler from the three ceilings
    /// above rather than risking a fourth, independently-drifting hand-transcribed literal.</summary>
    public static readonly double MaxSearchCeilingMs = Math.Max(Math.Max(NormalSearchCeilingMs, ExtendedSearchCeilingMs), NarrowSearchCeilingMs);

    /// <summary>Legacy's real RM12 VIS byte is <c>0x86</c> (`sstv.cpp:1997`) — its parity bit (1)
    /// does not match even parity computed from its 7 data bits (6 = 0b0000110, two set bits,
    /// already even → computed parity would be 0, producing 0x06 instead). Every other normal VIS
    /// byte in the legacy table was independently re-checked bit-by-bit and does follow even
    /// parity, so this is a one-off quirk in legacy's own assigned byte, not a flaw in this port's
    /// parity computation — and since legacy's VIS-decode switch (`sstv.cpp:1993-2074`) matches the
    /// literal received byte, transmitting a correctly-computed-but-different parity bit would make
    /// this port's RM12 unrecognizable to a real legacy receiver. Passed to
    /// <see cref="GenerateSegments"/>'s <c>forcedParityBit</c> parameter for this one mode only.
    /// S10 fix (spec/14-roadmap.md): decode now matches this exact forced byte too --
    /// <see cref="SstvModeRegistry.FindByFullVisByte"/> special-cases RM12 via this same constant
    /// rather than computing parity, so this quirk stays correctly recognized end-to-end.</summary>
    public const int Rm12ForcedParityBit = 1;

    // SHOULD item 5 (spec/14-roadmap.md): comprehensive-review correction -- this doc comment used to
    // sit here, on OutHeadToneDurationMs, but it documents GenerateOutHeadSegments' own behavior; a
    // sibling comment elsewhere referenced "GenerateOutHeadSegments' own doc comment" and found none
    // there. Moved to sit on the method it actually describes; these three constants are its
    // building blocks.
    public const double OutHeadToneDurationMs = 100.0;
    public const double OutHeadNarrowDurationMs = OutHeadToneDurationMs * 4; // 400ms
    public const double OutHeadNormalDurationMs = OutHeadToneDurationMs * 8; // 800ms

    /// <summary>
    /// SHOULD item 5 (spec/14-roadmap.md): direct port of legacy's <c>TMmsstv::OutHEAD</c>
    /// (`Main.cpp:7270-7292`), the pre-VIS leader-tone burst -- called UNCONDITIONALLY at the very
    /// start of every real transmission (`Main.cpp:7393`, <c>OutHEAD()</c>, immediately before the
    /// VIS/narrow-FSK header block at `:7395`), at legacy's shipped <c>sys.m_VOX==0</c> default (the
    /// only branch this port can model -- no VOX/PTT layer exists yet, matching
    /// <see cref="AnalogFmSstvEncoder.GenerateFooterSegments"/>'s own identical <c>m_VOX</c> scoping
    /// note). This was a real missing TX segment before this fix -- no code comment, no
    /// docs/removed-features.md entry, same class of gap S27's CQ100 omission was before it got
    /// fixed. Not a decode blocker for a real legacy RX (it still locks on the VIS leader itself
    /// regardless of what precedes it), but genuinely the FIRST audio any real transmission carries,
    /// missing from this port's own TX output until now.
    ///
    /// Exact tone sequences, both 100ms/tone (`Main.cpp:7274-7292`, the <c>case 0:</c> arm of
    /// <c>switch(sys.m_VOX)</c>):
    /// <list type="bullet">
    /// <item>Narrow: 1900, 2300, 1900, 2300 (400ms total)</item>
    /// <item>Normal: 1900, 1500, 1900, 1500, 2300, 1500, 2300, 1500 (800ms total)</item>
    /// </list>
    /// </summary>
    public static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateOutHeadSegments(bool narrow)
    {
        const double toneDurationMs = OutHeadToneDurationMs;

        if (narrow)
        {
            yield return (1900, toneDurationMs);
            yield return (2300, toneDurationMs);
            yield return (1900, toneDurationMs);
            yield return (2300, toneDurationMs);
        }
        else
        {
            yield return (1900, toneDurationMs);
            yield return (1500, toneDurationMs);
            yield return (1900, toneDurationMs);
            yield return (1500, toneDurationMs);
            yield return (2300, toneDurationMs);
            yield return (1500, toneDurationMs);
            yield return (2300, toneDurationMs);
            yield return (1500, toneDurationMs);
        }
    }

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

    /// <summary>S8 fix (spec/14-roadmap.md): duration from <see cref="NarrowFskHeaderDecoder"/>'s own
    /// internal "bit-clock origin" (the mode-3-&gt;4 transition, i.e. the first sample of the 24-bit
    /// data-bit sampling phase) to the packet's real end -- <see cref="NarrowBitDurationMs"/>/2 (the
    /// mode-3 recheck's own 11ms offset from where the origin lands) plus the 24 data bits themselves
    /// (<see cref="NarrowBitDurationMs"/>*6*<see cref="NarrowByteCount"/> = 528ms). Auditor plan-review
    /// derived and independently re-confirmed this exact formula (539ms) as the anchor this class's
    /// persistent mid-stream scan (<c>AnalogFmSstvDecoder.TryNarrowFskScan</c>) should use, in
    /// preference to anchoring off the mode-0 guard-tone trigger instead (which has real,
    /// unbounded-in-practice jitter the mode-3 recheck doesn't) -- see that method's own doc comment
    /// for the full reasoning and the measured-tolerance test that verifies this in practice rather
    /// than trusting the derivation alone.</summary>
    public const double NarrowPostBitClockOriginDurationMs =
        NarrowBitDurationMs / 2 + NarrowBitDurationMs * 6 * NarrowByteCount;

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

    // --- AVT-specific header --- Main.cpp:7430 (`int e = (TxMode == smAVT) ? 3 : 1;`) repeats the
    // whole VIS block 3x for AVT only, then Main.cpp:7563-7575 appends a long sync/AFC training
    // sequence unique to this mode before any line data starts. Legacy's own RX budgets for exactly
    // this preamble length at sstv.cpp:2140 (9 + 910 + 910 + 5311.9424 + 0.30514375).

    /// <summary>Number of times the VIS block itself is transmitted for AVT (`Main.cpp:7430`) — 3,
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
    /// (`Main.cpp:7430`), then the training sequence (`Main.cpp:7563-7575`) — see this section's
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
