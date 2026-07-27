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

    public const double TotalDurationMs =
        LeaderDurationMs + BreakDurationMs + LeaderDurationMs // leader-break-leader
        + BitDurationMs // start bit
        + BitDurationMs * DataBitCount // data bits
        + BitDurationMs // parity bit
        + BitDurationMs; // stop bit

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
}
