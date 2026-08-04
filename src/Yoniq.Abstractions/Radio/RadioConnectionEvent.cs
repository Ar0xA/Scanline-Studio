namespace Yoniq.Abstractions.Radio;

public enum RadioConnectionState { Connecting, Connected, Disconnected, Reconnecting, Failed, CommandFailed }

/// <summary>See spec/02-radio-layer.md's "Polling" section and <see cref="IRadioController.ConnectionEvents"/>.
/// A discrete connection-lifecycle transition — unlike <see cref="RadioState"/>, dropping one of these
/// is genuinely lossy (there is no "current connection state" snapshot to fall back on the way
/// <see cref="IRadioController.LastKnownState"/> covers <see cref="RadioState"/>), so this stream is
/// never conflated/sampled the way a slow-consumer policy might for a high-rate snapshot stream.
///
/// Emitted for three structurally different reasons, on two different threads — see
/// <see cref="IRadioController.ConnectionEvents"/>'s own doc comment for the full per-stream scheduler
/// contract: <see cref="RadioConnectionState.Connecting"/>/<see cref="RadioConnectionState.Connected"/>/
/// <see cref="RadioConnectionState.Disconnected"/> from <c>ConnectAsync</c>/<c>DisconnectAsync</c> on
/// the caller's own thread; <see cref="RadioConnectionState.Reconnecting"/>/
/// <see cref="RadioConnectionState.Failed"/> from the internal poll loop's background thread when a
/// transport-level failure trips the backoff/reconnect path; <see cref="RadioConnectionState.CommandFailed"/>,
/// also from the poll loop, when a single command fails at the protocol level (e.g. rigctld's `RPRT -n`)
/// while the connection itself stays healthy — polling continues at normal cadence, no backoff, no
/// reconnect (spec/04-rigctld.md's error taxonomy: a protocol error alone never produces
/// <see cref="Reconnecting"/>/<see cref="Failed"/>, only <see cref="CommandFailed"/>).</summary>
public sealed record RadioConnectionEvent(
    RadioConnectionState State,
    string? Reason,
    Exception? Error,
    DateTimeOffset ObservedAt);
