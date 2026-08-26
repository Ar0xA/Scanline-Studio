namespace ScanlineStudio.Abstractions.Radio;

/// <summary>Plain DTO mirror of <c>ScanlineStudio.Core.Radio.RadioSafetySettings</c> (which owns the
/// actual persistence) -- lives here, not there, for the same reason <see cref="FrequencyPreset"/>
/// does: a method on <c>IRadioSessionService</c> returning/accepting the <c>Core.Radio</c> type
/// directly would leak a <c>ScanlineStudio.Core.Radio</c> assembly reference into
/// <c>ScanlineStudio.UI.dll</c> through the method signature alone, even though the UI never touches
/// that type's members -- the same layering violation class as a direct field-type reference.
///
/// A detection-latency note, not just a threshold picker: this is a convenience backstop riding the
/// existing ~250ms poll cadence plus a PTT-off round trip -- it is not a substitute for the rig's own
/// hardware SWR protection, and should never be presented to the user as an instant cutoff.</summary>
public sealed record RadioSafetySpec(bool SwrCutoffEnabled, double SwrCutoffThreshold)
{
    /// <summary>Single source of truth for the SWR-cutoff default -- lives here (not on
    /// <c>Core.Radio.RadioSafetySettings</c>, which owns persistence) because <c>ScanlineStudio.UI</c>
    /// (specifically <c>OptionsWindowViewModel.ResetRadioToDefault</c>) needs it too and cannot
    /// reference any <c>ScanlineStudio.Core.*</c> assembly (enforced by
    /// <c>UiLayeringArchitectureTests</c>). User-set default (2026-08-26), not a legacy value --
    /// this feature has no legacy precedent at all.</summary>
    public const double DefaultSwrCutoffThreshold = 2.5;
}
