namespace Yoniq.Abstractions.Radio;

/// <summary>See spec/02-radio-layer.md, spec/03-cat-layer.md. One DI-registered implementation per
/// backend (rigctld, linked Hamlib, flrig, OmniRig-as-client, ...) — <see cref="IRadioController"/>
/// resolves a <see cref="RadioConnectionSpec"/> to a concrete <see cref="IRadioProtocol"/> by asking
/// every registered <c>IEnumerable&lt;IRadioProtocolFactory&gt;</c> instance's <see cref="CanHandle"/>
/// and requiring <b>exactly one</b> match — zero matches or more than one is a configuration error (a
/// typed exception, per spec/01-architecture.md's error-handling rule), never a silent first-match-wins
/// (which would make DI registration order load-bearing and invisible). Lives in
/// <c>Yoniq.Abstractions</c> rather than <c>Yoniq.Core.Radio</c> specifically so an optional future
/// backend module (e.g. a linked-Hamlib project) can register its own factory without needing to
/// reference <c>Yoniq.Core.Radio</c> at all.
///
/// "No radio" (<see cref="NoneConnectionSpec"/>) is handled by its own factory returning a null-object
/// <see cref="IRadioProtocol"/> — <see cref="IRadioController"/> has no special-cased branch for it,
/// keeping "no radio is first-class" true structurally rather than by convention.</summary>
public interface IRadioProtocolFactory
{
    bool CanHandle(RadioConnectionSpec spec);

    /// <summary>Only called after <see cref="CanHandle"/> returned <c>true</c> for the same
    /// <paramref name="spec"/> — implementations may assume this and cast without re-checking.</summary>
    IRadioProtocol Create(RadioConnectionSpec spec);
}
