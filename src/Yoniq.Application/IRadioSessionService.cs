using Yoniq.Abstractions.Radio;

namespace Yoniq.Application;

/// <summary>Thin facade <c>Yoniq.UI</c> talks to instead of <see cref="IRadioController"/> directly
/// (spec/01-architecture.md's layering rule) — adds exactly one thing beyond a pass-through:
/// settings-driven connection-spec resolution (<see cref="ConnectUsingSettingsAsync"/>), per the
/// Phase-3 plan's decision #6. Everything else is a direct pass-through; no new radio logic lives
/// here, <see cref="IRadioController"/> already owns polling/backoff/error-taxonomy.</summary>
public interface IRadioSessionService
{
    RadioState? LastKnownState { get; }

    IObservable<RadioState> StateChanges { get; }

    IObservable<RadioConnectionEvent> ConnectionEvents { get; }

    /// <summary>Reads the persisted <c>Yoniq.Core.Radio.RadioConnectionSettings</c> section, maps it
    /// to a <see cref="RadioConnectionSpec"/>, and connects. A missing/unset section (or one that maps
    /// to <see cref="NoneConnectionSpec"/>) is a normal, fully-supported outcome — SSTV still works
    /// with no radio connected (spec/02-radio-layer.md) — not an error.</summary>
    Task ConnectUsingSettingsAsync(CancellationToken ct = default);

    Task DisconnectAsync();

    Task SetFrequencyAsync(long hz, CancellationToken ct = default);

    Task SetModeAsync(RadioMode mode, CancellationToken ct = default);

    Task SetPttAsync(bool tx, CancellationToken ct = default);
}
