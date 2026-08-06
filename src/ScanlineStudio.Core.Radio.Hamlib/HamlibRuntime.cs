using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// Real <see cref="IHamlibRuntime"/>. Runs <see cref="HamlibLibraryLocator"/> + the version gate
/// <b>eagerly in the constructor</b> -- deliberately not lazily on first <see cref="Native"/> access,
/// which would put the first ~6 <c>NativeLibrary.TryLoad</c> attempts plus <c>rig_version()</c> inline
/// on whatever thread first calls <c>HamlibProtocolFactory.Create</c> (the UI thread, if a future
/// Application-layer caller invokes <c>IRadioController.ConnectAsync</c> from a click handler --
/// <c>RadioController.ConnectAsync</c> awaits a synchronously-completing <c>DisconnectAsync</c> when
/// nothing is connected yet, so <c>ResolveProtocol</c> genuinely runs inline). Whoever constructs a
/// <see cref="HamlibRuntime"/> controls exactly when that I/O happens.
/// </summary>
internal sealed partial class HamlibRuntime : IHamlibRuntime
{
    private readonly IHamlibNative? _native;
    private readonly IReadOnlyList<string>? _unavailableAttempts;
    private readonly ILogger _logger;

    /// <param name="nativeFactory">Defaults to the real <see cref="HamlibNativeFactory"/>; tests
    /// substitute a fake that ignores the (meaningless, in a test) handle value and returns a scripted
    /// <see cref="IHamlibNative"/> instead -- see <see cref="IHamlibNativeFactory"/>'s own doc
    /// comment.</param>
    /// <param name="logger">Optional, defaulting to a no-op logger: this type is constructed via `new`
    /// inside <see cref="HamlibProtocolFactory.Create(string?,ILoggerFactory?)"/> (a static factory
    /// method, not through DI). The composition root does pass a real logger through today.</param>
    public HamlibRuntime(INativeLibraryLoader loader, string? overridePath, IHamlibNativeFactory? nativeFactory = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        var factory = nativeFactory ?? new HamlibNativeFactory();
        try
        {
            var locator = new HamlibLibraryLocator(loader, overridePath);
            var (handle, resolvedPath) = locator.Locate();
            var native = factory.Create(loader, handle);
            var version = native.RigVersion();

            if (!HamlibVersionGate.IsSupported(version))
            {
                _unavailableAttempts =
                [
                    $"{resolvedPath}: unsupported Hamlib version '{version ?? "(null)"}' -- " +
                    "only major version 4 is supported",
                ];
                Log.UnsupportedVersion(_logger, resolvedPath, version ?? "(null)");
                return;
            }

            _native = native;
            Log.Available(_logger, resolvedPath, version ?? "(null)");
        }
        catch (HamlibUnavailableException ex)
        {
            _unavailableAttempts = ex.Attempts;
            Log.Unavailable(_logger, string.Join("; ", ex.Attempts));
        }
    }

    public bool IsAvailable => _native is not null;

    public IHamlibNative Native => _native ?? throw new HamlibUnavailableException(_unavailableAttempts!);

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Hamlib available: {Path}, version {Version}")]
        public static partial void Available(ILogger logger, string path, string version);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hamlib found but unsupported: {Path}, version {Version}")]
        public static partial void UnsupportedVersion(ILogger logger, string path, string version);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hamlib not available; discovery attempts: {Attempts}")]
        public static partial void Unavailable(ILogger logger, string attempts);
    }
}
