namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Mirrors legacy's <c>sys.m_CWID</c> tri-state (<c>Main.cpp:7021-7025</c>,
/// <c>Option.cpp:586-592</c>) -- NOT a bool. <see cref="SoundFile"/> corresponds to
/// <c>OutputMMV</c> (sound-file station ID, `docs/plans/sound-file-id-plan.md`): parses and resamples
/// the file at <c>StationIdSettings.SoundFileMmvPath</c> (via <c>ScanlineStudio.Core.Sstv.MmvSoundFile</c>)
/// and plays it back at the same TX dispatch point as CW-ID/FSK-ID. Selecting it with no path
/// configured (or an unreadable/unplayable file) silently transmits nothing, matching legacy's own
/// behavior when no sound file is configured (<c>!sys.m_MMVID.IsEmpty()</c> gate failing, or an
/// unreadable one), not a bug.
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
