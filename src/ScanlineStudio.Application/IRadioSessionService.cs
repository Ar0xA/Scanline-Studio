using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Application;

/// <summary>Thin facade <c>ScanlineStudio.UI</c> talks to instead of <see cref="IRadioController"/> directly
/// (spec/01-architecture.md's layering rule) — adds exactly one thing beyond a pass-through:
/// settings-driven connection-spec resolution (<see cref="ConnectUsingSettingsAsync"/>), per the
/// Phase-3 plan's decision #6. Everything else is a direct pass-through; no new radio logic lives
/// here, <see cref="IRadioController"/> already owns polling/backoff/error-taxonomy.</summary>
public interface IRadioSessionService
{
    RadioState? LastKnownState { get; }

    /// <summary>What the currently-connected rig/backend actually negotiated -- <see cref="RadioState"/>'s
    /// SWR/ALC/power fields are only ever populated when the matching flag is set here. Absent any
    /// connection, this is <see cref="RadioCapabilities.None"/> (a normal, fully-supported state, same
    /// as <see cref="LastKnownState"/> being <see langword="null"/>).</summary>
    RadioCapabilities Capabilities { get; }

    IObservable<RadioState> StateChanges { get; }

    IObservable<RadioConnectionEvent> ConnectionEvents { get; }

    /// <summary>Reads the persisted <c>ScanlineStudio.Core.Radio.RadioConnectionSettings</c> section, maps it
    /// to a <see cref="RadioConnectionSpec"/>, and connects. A missing/unset section (or one that maps
    /// to <see cref="NoneConnectionSpec"/>) is a normal, fully-supported outcome — SSTV still works
    /// with no radio connected (spec/02-radio-layer.md) — not an error.</summary>
    Task ConnectUsingSettingsAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    Task SetFrequencyAsync(long hz, CancellationToken ct = default);

    Task SetModeAsync(RadioMode mode, CancellationToken ct = default);

    Task SetPttAsync(bool tx, CancellationToken ct = default);

    /// <summary>User-defined quick-jump frequency/mode entries (the frequency strip's memory-button
    /// row) -- returns <see cref="FrequencyPreset"/> (Abstractions, not the
    /// <c>ScanlineStudio.Core.Radio.FrequencyPresetsSettings</c> section type that actually persists
    /// them) so <c>ScanlineStudio.UI</c> can consume this without referencing a
    /// <c>ScanlineStudio.Core.*</c> concrete assembly.</summary>
    Task<IReadOnlyList<FrequencyPreset>> GetFrequencyPresetsAsync(CancellationToken ct = default);

    Task SaveFrequencyPresetsAsync(IReadOnlyList<FrequencyPreset> presets, CancellationToken ct = default);

    /// <summary>Returns <see cref="RadioSafetySpec"/> (Abstractions, not the
    /// <c>ScanlineStudio.Core.Radio.RadioSafetySettings</c> section type that actually persists it) so
    /// <c>ScanlineStudio.UI</c> can consume this without referencing a <c>ScanlineStudio.Core.*</c>
    /// concrete assembly -- same reasoning as <see cref="FrequencyPreset"/>.</summary>
    Task<RadioSafetySpec> GetSafetySettingsAsync(CancellationToken ct = default);

    Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default);
}
