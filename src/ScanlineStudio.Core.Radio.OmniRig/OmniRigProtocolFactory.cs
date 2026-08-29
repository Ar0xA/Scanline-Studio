using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.OmniRig;

/// <summary>See spec/03-cat-layer.md's OmniRig section. Resolves <see cref="OmniRigConnectionSpec"/>
/// to an <see cref="OmniRigRadioProtocol"/> over a real <see cref="OmniRigComClient"/>.</summary>
public sealed class OmniRigProtocolFactory : IRadioProtocolFactory
{
    /// <summary>Bounds each whole public-method transaction on <see cref="OmniRigRadioProtocol"/> --
    /// see that class's own doc comment.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Separate, longer budget for COM activation specifically -- see
    /// <see cref="OmniRigRadioProtocol.EnsureConnectedAsync"/>'s own doc comment (code-review
    /// finding, round 1) for why this can't share <see cref="RequestTimeout"/>: activation may
    /// cold-start <c>OmniRig.exe</c> itself, a real process launch plus its own INI/serial-port
    /// startup work, not a call to an already-running server.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly ILoggerFactory _loggerFactory;

    public OmniRigProtocolFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public bool CanHandle(RadioConnectionSpec spec) => spec is OmniRigConnectionSpec;

    /// <summary>Guards <see cref="OperatingSystem.IsWindows"/> here rather than at registration time
    /// -- matches this project's "explicit selection fails loudly" contract (Hamlib's own convention
    /// for an unavailable backend): a user who specifically picks OmniRig on a non-Windows OS should
    /// see why it didn't work, not silently end up on a different backend.</summary>
    public IRadioProtocol Create(RadioConnectionSpec spec)
    {
        if (spec is not OmniRigConnectionSpec)
        {
            throw new ArgumentException(
                $"{nameof(OmniRigProtocolFactory)} can only create protocols for " +
                $"{nameof(OmniRigConnectionSpec)} -- call {nameof(CanHandle)} first.",
                nameof(spec));
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The OmniRig backend requires Windows (OmniRig is a Windows-only COM automation server).");
        }

        return new OmniRigRadioProtocol(
            new OmniRigComClient(), RequestTimeout, ConnectTimeout, _loggerFactory.CreateLogger<OmniRigRadioProtocol>());
    }
}
