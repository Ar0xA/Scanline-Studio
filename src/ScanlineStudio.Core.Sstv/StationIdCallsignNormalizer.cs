namespace ScanlineStudio.Core.Sstv;

/// <summary>Option.cpp:445-448 (the settings-boundary normalization where legacy actually sets
/// <c>sys.m_Call</c>) -- a faithful port, not new hardening. Order matters and is preserved exactly:
/// <c>StrCopy</c> caps at <c>MLCALL</c>=16 chars FIRST, THEN <c>jstrupr</c> uppercases, THEN
/// <c>clipsp</c>/<c>SkipSpace</c> trims -- not trim-then-cap. A pathological &gt;16-char string
/// that's mostly leading whitespace truncates away the real callsign entirely under this order; that
/// matches legacy exactly, it is not "fixed" here.
///
/// Extracted into its own shared class (round-2 finding on Phase 5's auditor code-review) because it
/// genuinely has two call sites that both need byte-for-byte the SAME normalization to stay correct
/// relative to each other, not just to legacy: <see cref="AnalogFmSstvEncoder"/>'s TX wire path
/// (<c>OperatorSettings.Callsign</c> normalized immediately before encoding) and
/// <c>ScanlineStudio.Application.SstvSessionService.GetOperatorCallsignAsync</c>'s RX self-filter
/// comparison (a decoded FSK-ID callsign, always TX-side-normalized by construction since every
/// transmitter -- including this port's own -- runs the same normalization before sending, must be
/// compared against an equally-normalized operator callsign or an unnormalized stored value like
/// <c>"w1aw"</c> silently fails to self-filter against a decoded <c>"W1AW"</c>). A single shared
/// implementation means the two can never drift apart from each other, even though
/// <c>OperatorSettings.Callsign</c> itself is still left as-typed in storage (used for macros/display/
/// QRZ elsewhere) -- this normalization is applied fresh at each read site that needs it, never
/// written back.</summary>
public static class StationIdCallsignNormalizer
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        // FskStationIdWireFormat.MaxCallsignLength (sstv.h:718-722 sizes m_fskcall[20] etc; legacy
        // aborts RX at 17 chars, sstv.cpp:2478, so 16 is the real maximum) -- referenced directly
        // (same assembly) rather than duplicated as a second constant, so the two can never drift.
        var capped = raw.Length > FskStationIdWireFormat.MaxCallsignLength
            ? raw[..FskStationIdWireFormat.MaxCallsignLength]
            : raw;

        // Doc correction (Tier A Batch 8 chunk 8b): the ORDER here (cap, then uppercase, then trim)
        // is re-verified correct against Option.cpp:445-448, but `.Trim()`'s character set is wider
        // than legacy's real trim -- legacy's `clipsp`/`SkipSpace` (ComLib.cpp:740-752/1022-1028)
        // strip only ' ' and '\t', while .NET's `Trim()` strips the full Unicode whitespace set (e.g.
        // '\n', '\r'). Unreachable from a single-line callsign settings field in practice.
        return capped.ToUpperInvariant().Trim();
    }
}
