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

    public static IEnumerable<(double FrequencyHz, double DurationMs)> GenerateSegments(int visCode)
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

        yield return (parity == 1 ? Bit1FrequencyHz : Bit0FrequencyHz, BitDurationMs);
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
}
