using Yoniq.Abstractions.Radio;

namespace Yoniq.Core.Radio.Rigctld;

/// <summary>See spec/02-radio-layer.md, spec/04-rigctld.md. Resolves <see cref="RigctldConnectionSpec"/>
/// to a <see cref="RigctldClientProtocol"/> constructed over a real <see cref="TcpTransport"/>.</summary>
public sealed class RigctldProtocolFactory : IRadioProtocolFactory
{
    public bool CanHandle(RadioConnectionSpec spec) => spec is RigctldConnectionSpec;

    public IRadioProtocol Create(RadioConnectionSpec spec)
    {
        if (spec is not RigctldConnectionSpec rigctldSpec)
        {
            throw new ArgumentException(
                $"{nameof(RigctldProtocolFactory)} can only create protocols for " +
                $"{nameof(RigctldConnectionSpec)} -- call {nameof(CanHandle)} first.",
                nameof(spec));
        }

        var transport = new TcpTransport(rigctldSpec.Host, rigctldSpec.Port);
        return new RigctldClientProtocol(transport, rigctldSpec.ConnectTimeout);
    }
}
