namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Option.cpp:445-448 (the settings-boundary normalization where legacy actually sets
/// <c>sys.m_Call</c>) -- a faithful port, not new hardening. Order matters and is preserved exactly:
/// <c>StrCopy</c> caps at <c>MLCALL</c>=16 chars FIRST, THEN <c>jstrupr</c> uppercases, THEN
/// <c>clipsp</c>/<c>SkipSpace</c> trims -- not trim-then-cap. A pathological &gt;16-char string
/// that's mostly leading whitespace truncates away the real callsign entirely under this order; that
/// matches legacy exactly, it is not "fixed" here.
///
/// Extracted into its own shared class (round-2 finding on Phase 5's auditor code-review) because it
/// genuinely has two call sites that both need byte-for-byte the SAME normalization to stay correct
/// relative to each other, not just to legacy: <c>ScanlineStudio.Core.Sstv.AnalogFmSstvEncoder</c>'s
/// TX wire path (<c>OperatorSettings.Callsign</c> normalized immediately before encoding) and
/// <c>ScanlineStudio.Application.SstvSessionService.GetOperatorCallsignAsync</c>'s RX self-filter
/// comparison (a decoded FSK-ID callsign, always TX-side-normalized by construction since every
/// transmitter -- including this port's own -- runs the same normalization before sending, must be
/// compared against an equally-normalized operator callsign or an unnormalized stored value like
/// <c>"w1aw"</c> silently fails to self-filter against a decoded <c>"W1AW"</c>). A single shared
/// implementation means the two can never drift apart from each other, even though
/// <c>OperatorSettings.Callsign</c> itself is still left as-typed in storage (used for macros/display/
/// QRZ elsewhere) -- this normalization is applied fresh at each read site that needs it, never
/// written back.
///
/// fsk_cwid.md B-P1 layering move: lives here (not <c>ScanlineStudio.Core.Sstv</c>, where it
/// originated) so <c>ScanlineStudio.Core.Cw</c>'s own <c>CwIdCallsignExtractor</c> can share it
/// without a <c>Core.Cw -&gt; Core.Sstv</c> project reference, which this codebase's own layering
/// convention (every <c>Core.*</c> project references only <c>Abstractions</c>/its own family
/// parent -- see <c>UiLayeringArchitectureTests.cs</c>) reserves for genuine DSP dependencies, not
/// a shared string utility. <see cref="MaxCallsignLength"/> moved here too, for the same reason
/// (<see cref="Normalize"/> needs it) -- <c>ScanlineStudio.Core.Sstv.FskStationIdWireFormat
/// .MaxCallsignLength</c> is now an alias to this constant, not a second definition, so the two can
/// still never drift.</summary>
public static class StationIdCallsignNormalizer
{
    /// <summary><c>sstv.h:718-722</c> sizes <c>m_fskcall[20]</c> etc; legacy aborts RX at 17 chars
    /// (<c>sstv.cpp:2478</c>), so 16 is the real maximum.</summary>
    public const int MaxCallsignLength = 16;

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var capped = raw.Length > MaxCallsignLength
            ? raw[..MaxCallsignLength]
            : raw;

        // Doc correction (Tier A Batch 8 chunk 8b): the ORDER here (cap, then uppercase, then trim)
        // is re-verified correct against Option.cpp:445-448, but `.Trim()`'s character set is wider
        // than legacy's real trim -- legacy's `clipsp`/`SkipSpace` (ComLib.cpp:740-752/1022-1028)
        // strip only ' ' and '\t', while .NET's `Trim()` strips the full Unicode whitespace set (e.g.
        // '\n', '\r'). Unreachable from a single-line callsign settings field in practice.
        return capped.ToUpperInvariant().Trim();
    }

    /// <summary>fsk_cwid.md §A5: the ONE shared self-filter comparison -- Main.cpp:3628's exact,
    /// case-sensitive <c>strcmp</c> against the operator's own callsign. Every decode source (FSK
    /// today via <c>RxImagePaneViewModel.ApplyStationIdDecodedAsync</c>/<c>RxStationIdAttacher</c>,
    /// CW once B-P3 wires <c>CwIdCallsignExtractor</c>'s output through the same VM method) must
    /// route through this rather than reimplementing the ordinal compare inline, so a future call
    /// site cannot silently skip the filter or drift to a different comparison (e.g. case-insensitive)
    /// against the others. Both sides are expected already-normalized by construction (every producer
    /// in this codebase -- TX encode, <c>GetOperatorCallsignAsync</c>, <c>CwIdCallsignExtractor</c> --
    /// runs <see cref="Normalize"/> before this is ever called); this method does not normalize
    /// itself, matching <c>strcmp</c>'s own "caller's job" contract.</summary>
    public static bool IsOwnCallsign(string? decodedCallsign, string? ownCallsign) =>
        string.Equals(decodedCallsign, ownCallsign, StringComparison.Ordinal);
}
