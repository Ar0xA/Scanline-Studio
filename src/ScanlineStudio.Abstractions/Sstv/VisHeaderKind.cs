namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Which VIS-header shape a mode's TX actually sends -- see
/// <c>ScanlineStudio.Application.ISstvSessionService.GetVisHeaderInfo</c>. Mirrors
/// <c>AnalogFmSstvEncoder.GenerateFrequencySegments</c>'s own real branch order (AVT checked
/// first, then narrow, then extended, then standard) -- kept in <c>Abstractions</c>, not
/// <c>Core.Sstv</c>, so <c>ScanlineStudio.UI</c> can consume it without an architecturally-barred
/// reference to <c>Core.Sstv</c> (see that interface method's own doc comment).</summary>
public enum VisHeaderKind
{
    /// <summary>A normal single-byte VIS code. <c>Value</c> is the real transmitted byte (7 data
    /// bits plus parity), not the bare <see cref="SstvModeDefinition.VisCode"/> -- RM12's forced,
    /// non-computed parity bit means those two differ for that one mode.</summary>
    Standard,

    /// <summary>MR/MP/ML family: two-byte "extended VIS" (escape code, then <c>Value</c> =
    /// <see cref="SstvModeDefinition.ExtendedVisCode"/>).</summary>
    Extended,

    /// <summary>MN/MC family: no VIS code at all, a different fixed 4-byte FSK packet instead.
    /// <c>Value</c> = <see cref="SstvModeDefinition.NarrowModeCode"/>.</summary>
    Narrow,

    /// <summary>AVT: VIS block repeated 3x plus a training sequence, no post-VIS pulse. <c>Value</c>
    /// is the real transmitted byte for <see cref="SstvModeDefinition.VisCode"/> (computed parity,
    /// same as <see cref="Standard"/> -- AVT has no forced-parity quirk of its own).</summary>
    Avt,
}
