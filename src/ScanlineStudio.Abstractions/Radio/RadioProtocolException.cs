namespace ScanlineStudio.Abstractions.Radio;

/// <summary>A backend-level protocol error (e.g. rigctld's `RPRT -n`, spec/04-rigctld.md) — the
/// connection/transport is fine, the specific command failed. <see cref="IRadioController"/>'s poll
/// loop distinguishes this from every other exception type (which it treats as a transport failure —
/// see spec/04-rigctld.md's error taxonomy): a <see cref="RadioProtocolException"/> surfaces via
/// <see cref="RadioConnectionEvent"/> (<see cref="RadioConnectionState.CommandFailed"/>) and polling
/// continues at normal cadence; anything else trips backoff and a transport reconnect. Any
/// <see cref="IRadioProtocol"/> implementation must throw this specifically for its own backend's
/// equivalent of a command-level error, never let it masquerade as a transport-level exception type.</summary>
public sealed class RadioProtocolException : Exception
{
    public RadioProtocolException(string message)
        : base(message)
    {
    }

    public RadioProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
