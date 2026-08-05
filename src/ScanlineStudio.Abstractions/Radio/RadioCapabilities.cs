namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md. What a connected rig/backend combination actually supports —
/// not every <see cref="IRadioProtocol"/> can do everything <see cref="IRadioController"/> exposes
/// (e.g. a rig with no signal-strength readback). rigctld-client negotiates this by probing (see
/// spec/04-rigctld.md's "Capability negotiation" section), not by parsing a capability-description
/// blob — see that spec for why.</summary>
[Flags]
public enum RadioCapabilities
{
    None = 0,
    ReadFrequency = 1 << 0,
    SetFrequency  = 1 << 1,
    ReadMode      = 1 << 2,
    SetMode       = 1 << 3,
    PttControl    = 1 << 4,
    SignalMeter   = 1 << 5,
    SwrMeter      = 1 << 6,
    AlcMeter      = 1 << 7,
    PowerMeter    = 1 << 8,
}
