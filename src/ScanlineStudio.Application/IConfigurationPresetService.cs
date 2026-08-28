namespace ScanlineStudio.Application;

/// <summary>Configurations-preset backlog, Phase 3 (2026-08-28) -- switches the live application
/// configuration to a saved preset, fully live, no restart. See
/// <see cref="ConfigurationPresetSwitchResult"/>/<see cref="ConfigurationPresetSwitchOutcome"/> for
/// the full per-outcome contract, including what the CALLER must still do afterward.
///
/// Deliberately NOT a member of <see cref="ISstvSessionService"/> -- this orchestrates ACROSS
/// multiple already-existing session services (<see cref="ISstvSessionService"/>,
/// <see cref="IRadioSessionService"/>) plus the settings store and preset store directly, none of
/// which is any single existing service's own job to own.</summary>
public interface IConfigurationPresetService
{
    /// <summary>See this interface's own doc comment, and <see cref="ConfigurationPresetSwitchResult"/>'s.
    /// Safe to call from any thread -- runs its own work on whatever thread calls it, with
    /// <c>ConfigureAwait(false)</c> throughout (matching this codebase's own established convention
    /// for anything that touches <c>ISettingsStore</c>), which is exactly why the CALLER, not this
    /// method, must handle the two UI-thread-only follow-ups the returned result describes.</summary>
    Task<ConfigurationPresetSwitchResult> SwitchToPresetAsync(string name, CancellationToken ct = default);
}
