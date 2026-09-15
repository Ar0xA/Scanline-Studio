namespace ScanlineStudio.Core.Radio;

/// <summary>User-reported gap (2026-09-15): the Transceiver card's "SSB as PKT" checkbox
/// (<c>RadioStatusViewModel.SsbAsPkt</c>) used to be purely in-memory, resetting to
/// <see langword="false"/> on every app restart with no way to keep it on for an operator who runs
/// PKTUSB/PKTLSB routinely. Its own SEPARATE section (not folded into
/// <see cref="RadioConnectionSettings"/>) deliberately, since that section is connection CONFIG --
/// backend/host/port -- and is wholesale replaced by the Configurations preset feature
/// (<c>ConfigurationPresetService</c>) and read fresh by <c>OptionsSettingsService</c>'s own
/// dialog-open snapshot; a mid-session operating toggle folded in there would get silently reset by
/// either of those unrelated flows. Mirrors <see cref="RadioSafetySettings"/>'s own "one small
/// section per independent concern" shape.</summary>
public sealed record RadioOperatingPreferencesSettings
{
    public const string SectionKey = "RadioOperatingPreferences";

    public bool SsbAsPkt { get; init; }
}
