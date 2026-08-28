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

    /// <summary>Pass-through of <see cref="IRadioController.RigId"/> — see that member's own doc
    /// comment for why this is a stable "none"-or-not identity check, not derived from
    /// <see cref="Capabilities"/>.</summary>
    string RigId { get; }

    /// <summary>Pass-through of <see cref="IRadioController.IsGenuinelyConnected"/> — see that
    /// member's own doc comment for how it differs from <see cref="RigId"/>.</summary>
    bool IsGenuinelyConnected { get; }

    IObservable<RadioState> StateChanges { get; }

    IObservable<RadioConnectionEvent> ConnectionEvents { get; }

    /// <summary>Reads the persisted <c>ScanlineStudio.Core.Radio.RadioConnectionSettings</c> section, maps it
    /// to a <see cref="RadioConnectionSpec"/>, and connects. A missing/unset section (or one that maps
    /// to <see cref="NoneConnectionSpec"/>) is a normal, fully-supported outcome — SSTV still works
    /// with no radio connected (spec/02-radio-layer.md) — not an error.</summary>
    Task ConnectUsingSettingsAsync(CancellationToken ct = default);

    /// <summary>Auditor usability review follow-up (2026-08-18) -- a settings-dialog "Test
    /// Connection" button. Deliberately takes an explicit <paramref name="spec"/> rather than
    /// reading persisted settings like <see cref="ConnectUsingSettingsAsync"/> does, so the caller
    /// can test whatever is CURRENTLY TYPED into the dialog (possibly not yet saved). Resolves and
    /// polls a fresh, disposable <see cref="IRadioProtocol"/> directly (same "exactly one factory
    /// match" resolution <see cref="IRadioController.ConnectAsync"/> itself uses internally) --
    /// deliberately does NOT touch the app's real, persistent <see cref="IRadioController"/> session,
    /// so a test attempt (success or failure) never disconnects or otherwise disturbs an
    /// already-working live connection.</summary>
    Task<RadioConnectionTestResult> TestConnectionAsync(RadioConnectionSpec spec, CancellationToken ct = default);

    /// <summary>Options-dialog "Test PTT" button -- same throwaway-protocol contract as
    /// <see cref="TestConnectionAsync"/> (never touches the real session), but also keys PTT for up
    /// to <paramref name="duration"/> (cancellable early via <paramref name="ct"/> -- a Stop click or
    /// the dialog closing) and un-keys before returning. Un-keying is retried a bounded number of
    /// times regardless of why the wait ended, on an internal token independent of
    /// <paramref name="ct"/>, since Hamlib's own <c>rig_close</c> only auto-un-keys RTS/DTR/
    /// Parallel/CM108/GPIO on dispose -- CAT ("RIG") PTT has no such rescue path, so the explicit
    /// un-key call is the only one. If every retry still fails, <see cref="RadioConnectionTestResult.Success"/>
    /// is <see langword="false"/> with a message telling the operator to check the rig manually --
    /// this is reported, never silently swallowed, since a stuck-keyed rig needs a human to
    /// intervene. Returns a no-PTT-capability failure (no keying attempted at all) if the resolved
    /// protocol's <see cref="RadioCapabilities"/> lack <see cref="RadioCapabilities.PttControl"/>.
    /// At most one call runs at a time -- a concurrent call is rejected, not queued.</summary>
    Task<RadioConnectionTestResult> TestPttAsync(RadioConnectionSpec spec, TimeSpan duration, CancellationToken ct = default);

    /// <summary>Restart-required-settings backlog item 5 (2026-08-28): applies a new Hamlib library
    /// path LIVE, without an app restart. Resolves the registered <see cref="IRadioProtocolFactory"/>
    /// that implements <c>ScanlineStudio.Abstractions.Radio.IHamlibLibraryReconfiguration</c> (a
    /// no-op if none does -- returns <see langword="null"/>, matching every other optional-side-channel
    /// convention in this backlog) and forwards the call. See that interface's own doc comment for
    /// the full contract: an already-open Hamlib connection is unaffected and keeps running on the
    /// previous library until it naturally reconnects; each call performs a fresh native load that is
    /// never unloaded, so a caller must not invoke this unless the configured path actually changed.
    /// Can throw <see cref="TimeoutException"/> if a concurrent reload is still in progress -- not
    /// swallowed here, a caller must treat that as a real failure.</summary>
    Task<HamlibLibraryReloadResult?> RequestHamlibLibraryPathAsync(string? overridePath, CancellationToken ct = default);

    Task DisconnectAsync();

    Task SetFrequencyAsync(long hz, CancellationToken ct = default);

    Task SetModeAsync(RadioMode mode, CancellationToken ct = default);

    Task SetPttAsync(bool tx, CancellationToken ct = default);

    /// <summary>Pass-through of <see cref="IRadioController.SetBandwidthAsync"/> -- see
    /// <see cref="IRadioProtocol.SetBandwidthAsync"/> for the null/not-supported contract. Callers
    /// should check <see cref="RadioCapabilities.SetBandwidth"/> against <see cref="Capabilities"/>
    /// before calling, since an unsupported backend throws rather than no-op'ing.</summary>
    Task SetBandwidthAsync(int? bandwidthHz, CancellationToken ct = default);

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

    /// <summary>Persists <paramref name="spec"/> and raises <see cref="SafetySettingsChanged"/> once
    /// the write succeeds -- the only way a live subscriber (e.g. <c>TxControlsPaneViewModel</c>'s SWR
    /// auto-cutoff enforcement) finds out the persisted setting changed without being reconstructed.
    /// <see cref="SaveSafetySettingsAsync"/> is the SOLE raise site for that event.</summary>
    Task SaveSafetySettingsAsync(RadioSafetySpec spec, CancellationToken ct = default);

    /// <summary>Fires once <see cref="SaveSafetySettingsAsync"/>'s write succeeds, carrying the newly
    /// persisted value. Deliberately a plain multicast <see langword="event"/>, not an
    /// <see cref="IObservable{T}"/> like <see cref="StateChanges"/>/<see cref="ConnectionEvents"/> --
    /// this is a one-shot "something changed" signal with no history/replay need, unlike those two.
    /// Same concurrency contract as those streams: raised SYNCHRONOUSLY on whatever thread
    /// <see cref="SaveSafetySettingsAsync"/>'s own write continuation runs on (NOT the UI thread --
    /// the reference implementation's continuation is <c>ConfigureAwait(false)</c>d), no buffering,
    /// never blocks the raiser beyond a subscriber's own handler body -- a subscriber that needs UI
    /// thread access must marshal itself (<c>Dispatcher.UIThread.Post</c>), the same "subscriber's own
    /// responsibility" contract <see cref="StateChanges"/> already establishes.</summary>
    event Action<RadioSafetySpec>? SafetySettingsChanged;
}
