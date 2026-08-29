namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>OmniRig's own <c>RigParamX</c> flag enum -- transcribed byte-for-byte from
/// <c>yoniq-old/YONIQ-main/OmniRig_TLB.h</c> (verified against source during plan-review, not
/// derived). Reused both as a capability bitmask (<c>IRigX.ReadableParams</c>/<c>WriteableParams</c>)
/// and as a single-flag property value (<c>IRigX.Tx</c>/<c>Mode</c>) -- OmniRig's own convention,
/// not invented here. Only the members this backend's real call sites (spec/03-cat-layer.md,
/// "Legacy's real OmniRig call sites") or capability negotiation need are transcribed; the rest
/// (Split/Rit/Xit/VFO-swap/etc.) are out of scope per the implementation plan and intentionally
/// omitted, not overlooked.</summary>
// Public, not internal, despite being reachable only through the internal IOmniRigComClient seam --
// xUnit [Theory]/[InlineData] test methods must be public, and a public test method can't take an
// internal enum parameter (CS0051) even with InternalsVisibleTo (that rule is about signature
// consistency, not cross-assembly visibility). Nothing public on this assembly's actual API surface
// exposes this type otherwise.
// CA1707 (no underscores in public identifiers) is deliberately suppressed for this enum's members
// only -- these are OmniRig's own literal constant names (OmniRig_TLB.h), kept verbatim rather than
// renamed to this project's PascalCase convention specifically so the transcription stays visually
// diffable against the header, per this file's own "byte-for-byte" design goal above.
#pragma warning disable CA1707
[Flags]
public enum RigParamX
{
    None = 0,
    PM_UNKNOWN = 1,
    PM_FREQ = 2,
    PM_RX = 2097152,
    PM_TX = 4194304,
    PM_CW_U = 8388608,
    PM_CW_L = 16777216,
    PM_SSB_U = 33554432,
    PM_SSB_L = 67108864,
    PM_DIG_U = 134217728,
    PM_DIG_L = 268435456,
    PM_AM = 536870912,
    PM_FM = 1073741824,
}
#pragma warning restore CA1707
