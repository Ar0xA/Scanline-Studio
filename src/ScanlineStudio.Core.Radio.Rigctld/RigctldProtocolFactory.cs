using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Rigctld;

/// <summary>See spec/02-radio-layer.md, spec/04-rigctld.md. Resolves <see cref="RigctldConnectionSpec"/>
/// to a <see cref="RigctldClientProtocol"/> constructed over a real <see cref="TcpTransport"/>.</summary>
public sealed class RigctldProtocolFactory : IRadioProtocolFactory
{
    private readonly ILoggerFactory _loggerFactory;

    // ILoggerFactory, not a single ILogger<RigctldProtocolFactory> -- TcpTransport/RigctldClientProtocol
    // each get their own correctly-categorized logger this way (per-category level filtering, e.g.
    // "turn up ScanlineStudio.Core.Radio.Rigctld.RigctldClientProtocol only", stays possible; sharing
    // one ILogger<RigctldProtocolFactory> across all three types would collapse that).
    public RigctldProtocolFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

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

        var transport = new TcpTransport(rigctldSpec.Host, rigctldSpec.Port, _loggerFactory.CreateLogger<TcpTransport>());
        return new RigctldClientProtocol(transport, rigctldSpec.ConnectTimeout, _loggerFactory.CreateLogger<RigctldClientProtocol>());
    }
}
