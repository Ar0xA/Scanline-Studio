using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>See spec/02-radio-layer.md's "no radio" case. Resolves <see cref="NoneConnectionSpec"/> to
/// <see cref="NoneRadioProtocol"/> -- always registered alongside every other backend factory so "no
/// radio" is a normal, first-class resolution rather than something <see cref="RadioController"/>
/// special-cases.</summary>
public sealed class NoneRadioProtocolFactory : IRadioProtocolFactory
{
    public bool CanHandle(RadioConnectionSpec spec) => spec is NoneConnectionSpec;

    public IRadioProtocol Create(RadioConnectionSpec spec) => new NoneRadioProtocol();
}
