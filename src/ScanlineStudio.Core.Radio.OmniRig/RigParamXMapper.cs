using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>Maps between OmniRig's own <see cref="RigParamX"/> mode flags and this project's
/// backend-agnostic <see cref="RadioMode"/>. See the implementation plan's "Files" section:
/// Usb/Lsb/Fm are the only modes legacy ever exercised through this path (verified against
/// <c>Main.cpp</c>'s real <c>set_Mode</c> call sites); Cw/CwR/Am/Rtty/Data/Pkt/RttyR/DataR are
/// best-effort, no legacy precedent -- flagged as an assumption, not confirmed behavior, per
/// CLAUDE.md §3.</summary>
internal static class RigParamXMapper
{
    /// <summary>Get direction: an OmniRig flag with no mapping resolves to
    /// <see cref="RadioMode.Unknown"/>, matching <see cref="RadioMode"/>'s own "never throw on an
    /// unrecognized wire value" contract.</summary>
    public static RadioMode ToRadioMode(RigParamX omniRigMode) => omniRigMode switch
    {
        RigParamX.PM_SSB_U => RadioMode.Usb,
        RigParamX.PM_SSB_L => RadioMode.Lsb,
        RigParamX.PM_FM => RadioMode.Fm,
        RigParamX.PM_CW_U => RadioMode.Cw,
        RigParamX.PM_CW_L => RadioMode.CwR,
        RigParamX.PM_AM => RadioMode.Am,
        RigParamX.PM_DIG_U => RadioMode.Data,
        RigParamX.PM_DIG_L => RadioMode.DataR,
        _ => RadioMode.Unknown,
    };

    /// <summary>Set direction: a <see cref="RadioMode"/> with no <see cref="RigParamX"/> equivalent
    /// throws <see cref="ArgumentOutOfRangeException"/>, matching <c>FlrigModeTokens</c>'s identical
    /// convention rather than silently no-op'ing.</summary>
    public static RigParamX ToRigParamX(RadioMode mode) => mode switch
    {
        RadioMode.Usb => RigParamX.PM_SSB_U,
        RadioMode.Lsb => RigParamX.PM_SSB_L,
        RadioMode.Fm => RigParamX.PM_FM,
        RadioMode.Cw => RigParamX.PM_CW_U,
        RadioMode.CwR => RigParamX.PM_CW_L,
        RadioMode.Am => RigParamX.PM_AM,
        RadioMode.Rtty or RadioMode.Data or RadioMode.Pkt => RigParamX.PM_DIG_U,
        RadioMode.RttyR or RadioMode.DataR => RigParamX.PM_DIG_L,
        _ => throw new ArgumentOutOfRangeException(
            nameof(mode), mode, "This RadioMode has no OmniRig RigParamX equivalent."),
    };
}
