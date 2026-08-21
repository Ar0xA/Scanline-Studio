namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. The single facade <c>ScanlineStudio.Application</c> talks to regardless
/// of which backend (spec/03-cat-layer.md) is active — including "no radio" (<see cref="NoneConnectionSpec"/>),
/// a first-class, fully supported case.
///
/// <b>Concurrency/scheduler contract</b> (CLAUDE.md requires every cross-thread stream state its
/// scheduler and slow-subscriber behavior; settled after an auditor design-review pass — see
/// spec/14-roadmap.md's "rigctld client" entry):
/// <list type="bullet">
/// <item><see cref="LastKnownState"/> and <see cref="StateChanges"/> never race: the reference
/// implementation backs both with the same <c>BehaviorSubject&lt;RadioState?&gt;</c> — its current
/// value *is* <see cref="LastKnownState"/> — rather than a plain field updated separately from a
/// <c>Subject</c>, which would otherwise let a subscriber that reads the property then subscribes see a
/// duplicate, or the reverse ordering miss one.</item>
/// <item>Both streams push synchronously to subscribers from whichever thread produced the value — no
/// buffering, no scheduler marshaling. <see cref="StateChanges"/> always fires from the internal poll
/// loop's background thread. <see cref="ConnectionEvents"/> fires from <em>either</em> the caller's own
/// thread (<see cref="ConnectAsync"/>/<see cref="DisconnectAsync"/> emit <c>Connecting</c>/
/// <c>Connected</c>/<c>Disconnected</c> synchronously) or the poll loop's background thread
/// (<c>Reconnecting</c>/<c>Failed</c>, emitted when a transport-level failure trips backoff — see
/// <see cref="IRadioProtocol"/>/spec/04-rigctld.md's error taxonomy: a protocol-level error alone never
/// produces one of these). A slow subscriber on either stream stalls the poll loop's own cadence
/// (acceptable at a 250ms default interval — unlike a real-time audio callback, this is low-consequence
/// — see <c>ScanlineStudio.Abstractions.Audio.IAudioEngine</c> for the contrasting case that does need a
/// drop policy) — subscribers doing real work must marshal to their own scheduler
/// (<c>ObserveOn</c>), not block here.</item>
/// <item>An exception thrown by a subscriber's <c>OnNext</c> handler is caught by the controller, never
/// allowed to propagate out of the poll loop or a <see cref="ConnectAsync"/>/<see cref="DisconnectAsync"/>
/// call — logged and surfaced as a <see cref="RadioConnectionEvent"/>, and polling continues. An
/// unhandled subscriber exception must never silently kill the loop.</item>
/// <item>See <see cref="RadioState"/>'s own doc comment for why <see cref="StateChanges"/> is never
/// deduplicated by record equality.</item>
/// </list>
///
/// Backend resolution: <see cref="ConnectAsync"/> resolves the given <see cref="RadioConnectionSpec"/>
/// to a concrete <see cref="IRadioProtocol"/> via the registered <see cref="IRadioProtocolFactory"/>
/// instances, requiring exactly one match — zero or more than one is a configuration error (a typed
/// exception), never a silent first-match-wins.</summary>
public interface IRadioController
{
    RadioState? LastKnownState { get; }
    RadioCapabilities Capabilities { get; }

    /// <summary>Which backend is currently connected — <c>"none"</c> for the null-object "no radio"
    /// backend (<see cref="NoneConnectionSpec"/>) or before any <see cref="ConnectAsync"/> call,
    /// matching <see cref="IRadioProtocol.RigId"/>'s own <c>"none"</c> sentinel for that case.
    /// Deliberately NOT the same signal as <see cref="Capabilities"/>: real backends (rigctld/
    /// Hamlib) connect lazily, so <see cref="Capabilities"/> reads <see cref="RadioCapabilities.None"/>
    /// for a real window during startup (before the first successful poll) and during reconnect
    /// backoff — a capability-flag check can't distinguish "genuinely no radio" from "a real,
    /// PTT-capable rig that just hasn't finished negotiating yet." This is a stable identity
    /// instead, known immediately at connect time (no negotiation required) and never flapping —
    /// an implementation must cache the resolved id separately from its live protocol handle and
    /// only clear it back to <c>"none"</c> on an explicit disconnect, NOT on a transient
    /// reconnect-backoff disposal (the reference implementation does this) — otherwise this
    /// property would flap to <c>"none"</c> during every backoff window even for a genuinely
    /// configured, momentarily unreachable rig, defeating the whole point. See
    /// <c>SstvSessionService.PlayWithPttAsync</c>'s own PTT-capability guard for why that
    /// distinction is safety-relevant, not cosmetic.</summary>
    string RigId { get; }

    IObservable<RadioState> StateChanges { get; }
    IObservable<RadioConnectionEvent> ConnectionEvents { get; }

    /// <summary><b>Lifecycle calls are not internally serialized.</b> <see cref="ConnectAsync"/>,
    /// <see cref="DisconnectAsync"/>, and <see cref="IAsyncDisposable.DisposeAsync"/> (where implemented)
    /// must not overlap each other — the caller owns that mutual exclusion. Overlapping them is
    /// undefined: e.g. a <see cref="DisconnectAsync"/> landing inside a concurrent
    /// <see cref="ConnectAsync"/>'s own teardown-then-resolve window can observe a fully torn-down
    /// controller, no-op, and return as if it disconnected successfully — while the session it meant to
    /// stop finishes coming up and keeps polling. The <c>Set*Async</c> members and the two observable
    /// streams ARE safe to call/subscribe concurrently with each other and with a lifecycle call.</summary>
    Task ConnectAsync(RadioConnectionSpec spec, CancellationToken ct);

    /// <summary>See <see cref="ConnectAsync"/>'s own doc comment for the lifecycle-serialization
    /// requirement this method shares.</summary>
    Task DisconnectAsync();
    Task SetFrequencyAsync(long hz, CancellationToken ct);
    Task SetModeAsync(RadioMode mode, CancellationToken ct);
    Task SetPttAsync(bool tx, CancellationToken ct);
}
