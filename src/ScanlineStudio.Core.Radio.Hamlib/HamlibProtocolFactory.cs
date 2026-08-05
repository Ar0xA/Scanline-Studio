using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// See spec/02-radio-layer.md, spec/03-cat-layer.md. Resolves a <see cref="HamlibConnectionSpec"/> to
/// a <see cref="HamlibRadioProtocol"/>. Depends on <see cref="IHamlibRuntime"/> (not
/// <see cref="HamlibLibraryLocator"/> directly) so discovery + the version gate run once, at whatever
/// point the runtime was constructed, never re-probed on <see cref="Create"/> -- <c>RadioController</c>
/// calls <see cref="Create"/> on every backoff reconnect (see <see cref="IHamlibRuntime"/>'s own doc
/// comment).
/// </summary>
public sealed class HamlibProtocolFactory : IRadioProtocolFactory
{
    private readonly IHamlibRuntime _runtime;

    // internal, not public: IHamlibRuntime is internal (an implementation seam, not part of this
    // assembly's public surface -- see Create(string?) below for the actual public entry point).
    // Tests construct this directly via InternalsVisibleTo.
    internal HamlibProtocolFactory(IHamlibRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Public entry point for a real composition root -- constructs the real
    /// <see cref="HamlibRuntime"/> (running discovery + the version gate once, right now, on whatever
    /// thread calls this) over the real <see cref="NativeLibraryLoader"/>.
    /// <paramref name="libraryOverridePath"/> is the discovery-order tier-3 manual path (spec/03's
    /// "Discovery order") -- a one-time app-level setting, not per-connection identity, which is why
    /// it's supplied here rather than on <see cref="HamlibConnectionSpec"/>.</summary>
    public static HamlibProtocolFactory Create(string? libraryOverridePath = null) =>
        new(new HamlibRuntime(new NativeLibraryLoader(), libraryOverridePath));

    public bool CanHandle(RadioConnectionSpec spec) => spec is HamlibConnectionSpec;

    IRadioProtocol IRadioProtocolFactory.Create(RadioConnectionSpec spec)
    {
        if (spec is not HamlibConnectionSpec hamlibSpec)
        {
            throw new ArgumentException(
                $"{nameof(HamlibProtocolFactory)} can only create protocols for " +
                $"{nameof(HamlibConnectionSpec)} -- call {nameof(CanHandle)} first.",
                nameof(spec));
        }

        return new HamlibRadioProtocol(
            _runtime.Native, hamlibSpec.Model, hamlibSpec.SerialPort, hamlibSpec.BaudRate, hamlibSpec.PttType);
    }
}
