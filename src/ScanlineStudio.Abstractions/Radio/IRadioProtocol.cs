namespace ScanlineStudio.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md, spec/03-cat-layer.md. One concrete external CAT backend client
/// (rigctld, linked Hamlib, flrig, OmniRig-as-client — never a hand-written per-rig protocol, see
/// spec/03-cat-layer.md's "Decision: no hand-written per-rig CAT protocols").
///
/// <b>Owns its own transport.</b> Deliberately does <em>not</em> take an <c>IRadioTransport</c>
/// parameter on any member (a deviation from an earlier spec/02-radio-layer.md draft, corrected after
/// an auditor design-review pass — see spec/14-roadmap.md's "rigctld client" entry): threading a
/// transport parameter through every call would force <see cref="IRadioController"/>/
/// <see cref="IRadioProtocolFactory"/> to know which concrete transport a given backend needs (e.g. "a
/// rigctld backend needs a TCP transport"), which is exactly the concrete-backend coupling the factory
/// pattern exists to remove — and call-based future backends (linked Hamlib via P/Invoke, OmniRig via
/// COM) have no byte-stream transport at all. Each implementation constructs and owns whatever
/// transport (or native/COM handle) it needs internally, typically via constructor injection from its
/// own <see cref="IRadioProtocolFactory"/>.
///
/// <see cref="IAsyncDisposable"/>: implementations backed by native/COM handles (future Hamlib/OmniRig
/// backends) must release them on dispose; TCP-based implementations close their transport.
/// <see cref="IRadioController"/> disposes the active protocol on disconnect/reconnect.</summary>
public interface IRadioProtocol : IAsyncDisposable
{
    /// <summary>e.g. "rigctld-client", "hamlib-native", "flrig-client" — identifies the backend, not a
    /// specific rig model (see spec/02-radio-layer.md's "Rig identification" section: there is no
    /// per-rig <c>RigId</c> registry).</summary>
    string RigId { get; }

    RadioCapabilities Capabilities { get; }

    Task<RadioState> PollAsync(CancellationToken ct);
    Task SetFrequencyAsync(long hz, CancellationToken ct);
    Task SetModeAsync(RadioMode mode, CancellationToken ct);
    Task SetPttAsync(bool tx, CancellationToken ct);
}
