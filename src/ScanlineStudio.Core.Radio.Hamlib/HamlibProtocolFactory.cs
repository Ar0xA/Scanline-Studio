using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
public sealed partial class HamlibProtocolFactory : IRadioProtocolFactory
{
    private readonly IHamlibRuntime _runtime;
    private readonly ILogger _logger;
    private readonly ILoggerFactory? _loggerFactory;

    // internal, not public: IHamlibRuntime is internal (an implementation seam, not part of this
    // assembly's public surface -- see Create(string?) below for the actual public entry point).
    // Tests construct this directly via InternalsVisibleTo.
    internal HamlibProtocolFactory(IHamlibRuntime runtime, ILogger? logger = null, ILoggerFactory? loggerFactory = null)
    {
        _runtime = runtime;
        _logger = logger ?? NullLogger.Instance;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Public entry point for a real composition root -- constructs the real
    /// <see cref="HamlibRuntime"/> (running discovery + the version gate once, right now, on whatever
    /// thread calls this) over the real <see cref="NativeLibraryLoader"/>.
    /// <paramref name="libraryOverridePath"/> is the discovery-order TIER-1 manual path (spec/03's
    /// "Discovery order" -- 1: user override, 2: bare soname, 3: known extra directories; doc
    /// correction, Tier A Batch 9 chunk 9d: this comment previously said "tier-3," which is the
    /// spec's OTHER manual fallback, not the override) -- a one-time app-level setting, not per-
    /// connection identity, which is why it's supplied here rather than on
    /// <see cref="HamlibConnectionSpec"/>.
    /// <paramref name="loggerFactory"/> is optional -- the composition root calls this as a static
    /// factory method, not through DI. When supplied (as <c>Program.cs</c> does today), each
    /// constructed type (<see cref="HamlibRuntime"/>, this factory, each
    /// <see cref="HamlibRadioProtocol"/>) gets its own correctly-categorized logger instead of
    /// sharing one <c>ILogger&lt;HamlibProtocolFactory&gt;</c> category for everything -- keeps
    /// per-category level filtering meaningful. Falls back to a no-op logger when omitted (e.g. in
    /// tests).</summary>
    public static HamlibProtocolFactory Create(string? libraryOverridePath = null, ILoggerFactory? loggerFactory = null)
    {
        var runtimeLogger = loggerFactory?.CreateLogger<HamlibRuntime>();
        var factoryLogger = loggerFactory?.CreateLogger<HamlibProtocolFactory>();
        return new(new HamlibRuntime(new NativeLibraryLoader(), libraryOverridePath, logger: runtimeLogger), factoryLogger, loggerFactory);
    }

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

        Log.CreatingProtocol(_logger, hamlibSpec.Model);
        var protocolLogger = _loggerFactory?.CreateLogger<HamlibRadioProtocol>() ?? _logger;
        return new HamlibRadioProtocol(
            _runtime.Native, hamlibSpec.Model, hamlibSpec.SerialPort, hamlibSpec.BaudRate, hamlibSpec.PttType, protocolLogger);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Creating HamlibRadioProtocol for model {Model}")]
        public static partial void CreatingProtocol(ILogger logger, uint model);
    }
}
