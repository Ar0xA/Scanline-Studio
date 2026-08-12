namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Mirrors legacy's <c>sys.m_CWID</c> tri-state (<c>Main.cpp:7021-7025</c>,
/// <c>Option.cpp:586-592</c>) -- NOT a bool. <see cref="SoundFile"/> corresponds to
/// <c>OutputMMV</c> (sound-file station ID), deliberately unimplemented in v1 (out of scope per the
/// CW-ID/FSK station-ID subsystem plan) -- modeled as its own enum member anyway so a future
/// sound-file feature doesn't need a breaking settings migration. Selecting it today silently
/// transmits no CW-ID at all, matching legacy's own behavior when no sound file is configured
/// (<c>!sys.m_MMVID.IsEmpty()</c> gate failing), not a bug.
///
/// Lives here (not <c>ScanlineStudio.Core.Sstv</c>, where <c>StationIdSettings</c> -- the settings
/// record this enum is a field of -- actually lives) so <c>ScanlineStudio.Application</c>'s
/// <c>OptionsSnapshot</c> and <c>ScanlineStudio.UI</c>'s <c>OptionsWindowViewModel</c> can both
/// reference it without <c>ScanlineStudio.UI</c> needing a <c>ScanlineStudio.Core.*</c> project
/// reference (banned by <c>UiLayeringArchitectureTests</c>) -- same reasoning as
/// <see cref="FskStationIdDecodedInfo"/>'s own move to this namespace in an earlier phase.</summary>
public enum CwIdMode
{
    Off = 0,
    Cw = 1,
    SoundFile = 2,
}
