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
public sealed partial class HamlibProtocolFactory : IRadioProtocolFactory, IHamlibLibraryReconfiguration, IDisposable
{
    // Restart-required-settings backlog item 5 (2026-08-28): bounded, not indefinite -- the thing
    // this gate protects is an UNCANCELLABLE native dlopen call (HamlibRuntime's own constructor doc
    // comment). An unbounded wait on a second overlapping reload (reachable across the Options
    // dialog's Save AND Restart Now commands, which both independently call SaveCoreAsync) would hang
    // the Options dialog's Save forever if the first reload wedged on a dead network-mounted path,
    // with no log and no dialog shown -- same failure class RestartableSstvDecoder's own
    // _rxTransitionGate bound exists to prevent, same shape reused here.
    private static readonly TimeSpan ReloadTimeout = TimeSpan.FromSeconds(30);

    // NOT readonly (2026-08-28) -- volatile, not lock-guarded. This is genuine safe publication, not
    // just "prompt visibility" -- code-review round-1 correction: an earlier version of this comment
    // named HamlibRuntime.ResolvedPath/Version (nobody ever reads those THROUGH this field -- the
    // only cross-thread read of _runtime is .Native, below, and a reload's own result comes from the
    // local `candidate`, never read back off this field). The real load-bearing reason is
    // HamlibRuntime._native itself: readonly there, but ECMA-335 gives a readonly field's constructor
    // write no publication guarantee on its own -- without volatile's release/acquire pair here, a
    // reader on a weak memory model (ARM64, a supported target) could observe a freshly-swapped,
    // non-null _runtime reference whose _native field hasn't become visible yet. A plain
    // reference-type field write is already atomic in .NET (no torn reads), so no lock is needed for
    // correctness -- ReloadLibraryAsync's own _reloadGate below exists for a SEPARATE reason
    // (determinism across overlapping reloads), not to protect this field itself.
    private volatile IHamlibRuntime _runtime;
    private readonly INativeLibraryLoader _loader;
    private readonly IHamlibNativeFactory _nativeFactory;
    private readonly ILogger? _runtimeLogger;
    private readonly ILogger _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    // internal, not public: IHamlibRuntime is internal (an implementation seam, not part of this
    // assembly's public surface -- see Create(string?) below for the actual public entry point).
    // Tests construct this directly via InternalsVisibleTo. loader/nativeFactory/runtimeLogger are
    // new (2026-08-28) -- ReloadLibraryAsync needs the SAME seams the initial runtime was built from
    // to construct a fresh one on demand, mirroring HamlibDiscoveryService's own identical ctor split
    // (that class's own doc comment: internal ctor takes the internal seam types, a public static
    // Create is the real composition-root entry point).
    internal HamlibProtocolFactory(
        IHamlibRuntime runtime,
        INativeLibraryLoader loader,
        IHamlibNativeFactory? nativeFactory = null,
        ILogger? logger = null,
        ILoggerFactory? loggerFactory = null)
    {
        _runtime = runtime;
        _loader = loader;
        _nativeFactory = nativeFactory ?? new HamlibNativeFactory();
        _runtimeLogger = loggerFactory?.CreateLogger<HamlibRuntime>();
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
        var loader = new NativeLibraryLoader();
        var nativeFactory = new HamlibNativeFactory();
        return new(
            new HamlibRuntime(loader, libraryOverridePath, nativeFactory, runtimeLogger),
            loader, nativeFactory, factoryLogger, loggerFactory);
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
            _runtime.Native, hamlibSpec.Model, hamlibSpec.SerialPort, hamlibSpec.BaudRate, hamlibSpec.PttType,
            hamlibSpec.PttPort, protocolLogger);
    }

    /// <summary>See <see cref="IHamlibLibraryReconfiguration.ReloadLibraryAsync"/>. Acquires
    /// <see cref="_reloadGate"/> OUTSIDE <see cref="Task.Run(Action)"/> via <c>await WaitAsync</c> --
    /// deliberately not inside the pool-thread lambda, and deliberately not a blocking <c>Wait()</c>
    /// on either side: acquiring outside with an async wait means no thread blocks for the wait
    /// itself (the calling thread returns to its own work; the continuation resumes once the gate is
    /// free), and exactly one pool thread is ever blocked at a time -- inside <see cref="Task.Run(Action)"/>,
    /// doing the actual native load. Acquiring INSIDE the lambda with a blocking <c>Wait()</c> would
    /// tie up a second pool thread for the whole wait+load duration per overlapping call; acquiring
    /// OUTSIDE with a blocking <c>Wait()</c> would freeze the calling (UI) thread for another reload's
    /// whole native-load duration -- exactly what <c>HamlibDiscoveryService.ProbeAsync</c>'s own
    /// <see cref="Task.Run(Action)"/> requirement exists to avoid.</summary>
    public async Task<HamlibLibraryReloadResult> ReloadLibraryAsync(string? overridePath, CancellationToken ct = default)
    {
        if (!await _reloadGate.WaitAsync(ReloadTimeout, ct).ConfigureAwait(false))
        {
            throw new TimeoutException("Timed out waiting for a concurrent Hamlib library reload to finish.");
        }

        try
        {
            return await Task.Run(() => ReloadLibraryCore(overridePath), ct).ConfigureAwait(false);
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>Synchronous core, called under <see cref="_reloadGate"/> (from
    /// <see cref="ReloadLibraryAsync"/>'s <see cref="Task.Run(Action)"/> lambda) -- also the direct
    /// test seam (internal, mirrors <c>HamlibDiscoveryService.Probe</c>'s own split from
    /// <c>ProbeAsync</c>), letting a test exercise this synchronously with fake seams instead of
    /// through the real gate/Task.Run machinery.</summary>
    internal HamlibLibraryReloadResult ReloadLibraryCore(string? overridePath)
    {
        var candidate = new HamlibRuntime(_loader, overridePath, _nativeFactory, _runtimeLogger);
        if (!candidate.IsAvailable)
        {
            Log.LibraryReloadRejected(_logger, overridePath ?? "(auto-detect)", string.Join("; ", candidate.Attempts));
            return new HamlibLibraryReloadResult(false, candidate.ResolvedPath, candidate.Version, candidate.Attempts);
        }

        // Deliberately never NativeLibrary.Free's the outgoing _runtime's handle -- see
        // IHamlibLibraryReconfiguration's own doc comment for why: an in-flight HamlibRadioProtocol
        // captured its own IHamlibNative snapshot at construction time, independent of this field, so
        // freeing the old handle here could crash that live connection's next native call out from
        // under it. A leaked mapping (a few hundred KB - few MB) per rare library-path change is the
        // same already-established cost HamlibDiscoveryService's own Probe path has paid on every
        // click since day one -- this is one more caller of that same accepted pattern, not a new one.
        _runtime = candidate;
        Log.LibraryReloaded(_logger, candidate.ResolvedPath ?? "(unknown)", candidate.Version ?? "(unknown)");
        return new HamlibLibraryReloadResult(true, candidate.ResolvedPath, candidate.Version, []);
    }

    /// <summary>Exists only to satisfy CA1001 (this class owns a disposable <see cref="_reloadGate"/>
    /// field) -- deliberately does NOT call <see cref="_reloadGate"/>'s own <c>Dispose()</c>, same
    /// reasoning as <c>HamlibRadioProtocol.DisposeAsync</c>'s own identical choice for its sibling
    /// <c>_lock</c> field (code-review round-1 finding): a <see cref="SemaphoreSlim"/> only needs
    /// disposal if its <c>AvailableWaitHandle</c> was ever touched (never is here), and disposing it
    /// WOULD actively break correctness -- a concurrent <see cref="ReloadLibraryAsync"/> call's own
    /// <c>finally { _reloadGate.Release(); }</c> would throw <see cref="ObjectDisposedException"/>
    /// instead of completing normally (masking whatever the real reload result was), and a waiter
    /// still queued at that moment would never complete at all (<c>Dispose()</c> does not fault
    /// pending waiters). <see cref="_runtime"/>'s own loaded native handle is separately, deliberately
    /// never freed either, at ANY point in this class's lifetime including process teardown (see
    /// <see cref="ReloadLibraryCore"/>'s own doc comment) -- the OS reclaims it at process exit
    /// either way.</summary>
    public void Dispose()
    {
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Creating HamlibRadioProtocol for model {Model}")]
        public static partial void CreatingProtocol(ILogger logger, uint model);

        [LoggerMessage(Level = LogLevel.Information, Message = "Hamlib library reloaded live: {Path}, version {Version}")]
        public static partial void LibraryReloaded(ILogger logger, string path, string version);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hamlib library reload rejected for {Path} -- previous library (if any) stays installed: {Attempts}")]
        public static partial void LibraryReloadRejected(ILogger logger, string path, string attempts);
    }
}
