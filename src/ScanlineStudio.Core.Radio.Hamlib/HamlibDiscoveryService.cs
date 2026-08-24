using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>Real <see cref="IHamlibDiscoveryService"/>. <c>internal</c> constructor (mirrors
/// <see cref="HamlibProtocolFactory"/>'s own split) because it takes <see cref="INativeLibraryLoader"/>/
/// <see cref="IHamlibNativeFactory"/>, both <c>internal</c> to this assembly -- <see cref="Create"/> is
/// the public entry point a composition root actually calls.</summary>
public sealed partial class HamlibDiscoveryService : IHamlibDiscoveryService
{
    // rig_load_all_backends() resets Hamlib's process-global backend registry with no internal
    // locking (confirmed against the vendored hamlib source, src/register.c -- an unguarded memset
    // over its hash table). Nothing else in this codebase calls it -- HamlibRadioProtocol/RadioController
    // only ever call rig_init(model) for one already-known model, which never touches this registry --
    // but two Options-dialog probes running back-to-back (or a double-click) could still race each
    // other, so every call into it is serialized through this process-wide gate.
    private static readonly SemaphoreSlim RigListGate = new(1, 1);

    private readonly INativeLibraryLoader _loader;
    private readonly IHamlibNativeFactory _nativeFactory;
    private readonly ILogger<HamlibRuntime> _runtimeLogger;
    private readonly ILogger<HamlibDiscoveryService> _logger;

    internal HamlibDiscoveryService(
        INativeLibraryLoader loader,
        IHamlibNativeFactory nativeFactory,
        ILogger<HamlibRuntime>? runtimeLogger = null,
        ILogger<HamlibDiscoveryService>? logger = null)
    {
        _loader = loader;
        _nativeFactory = nativeFactory;
        _runtimeLogger = runtimeLogger ?? NullLogger<HamlibRuntime>.Instance;
        _logger = logger ?? NullLogger<HamlibDiscoveryService>.Instance;
    }

    /// <summary>Public entry point for a real composition root -- see
    /// <see cref="HamlibProtocolFactory.Create"/>'s own doc comment for why this shape exists.</summary>
    public static HamlibDiscoveryService Create(ILoggerFactory? loggerFactory = null) =>
        new(
            new NativeLibraryLoader(),
            new HamlibNativeFactory(),
            loggerFactory?.CreateLogger<HamlibRuntime>(),
            loggerFactory?.CreateLogger<HamlibDiscoveryService>());

    public Task<HamlibProbeResult> ProbeAsync(string? overridePath, CancellationToken cancellationToken = default) =>
        // HamlibRuntime's constructor is blocking synchronous native I/O (its own doc comment says
        // so explicitly) -- Task.Run is required, not decorative, or this freezes the UI thread that
        // calls it before the first await ever runs.
        Task.Run(() => Probe(overridePath, cancellationToken), cancellationToken);

    private HamlibProbeResult Probe(string? overridePath, CancellationToken cancellationToken)
    {
        var runtime = new HamlibRuntime(_loader, overridePath, _nativeFactory, _runtimeLogger);

        if (!runtime.IsAvailable)
        {
            return new HamlibProbeResult(false, runtime.ResolvedPath, runtime.Version, runtime.Attempts, []);
        }

        var rigModels = ListRigModels(runtime.Native, cancellationToken);
        return new HamlibProbeResult(true, runtime.ResolvedPath, runtime.Version, [], rigModels);
    }

    private List<HamlibRigModelInfo> ListRigModels(IHamlibNative native, CancellationToken cancellationToken)
    {
        RigListGate.Wait(cancellationToken);
        try
        {
            native.RigLoadAllBackends();
            var modelIds = native.RigListModelIds();
            var models = new List<HamlibRigModelInfo>(modelIds.Count);

            foreach (var modelId in modelIds)
            {
                var mfg = native.RigGetCapsMfgName(modelId);
                var modelName = native.RigGetCapsModelName(modelId);
                if (mfg is null || modelName is null)
                {
                    continue;
                }

                models.Add(new HamlibRigModelInfo(modelId, mfg, modelName));
            }

            return models
                .OrderBy(m => m.Manufacturer, StringComparer.OrdinalIgnoreCase)
                .ThenBy(m => m.ModelName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.RigListFailed(_logger, ex);
            return [];
        }
        finally
        {
            RigListGate.Release();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to list Hamlib rig models")]
        public static partial void RigListFailed(ILogger logger, Exception ex);
    }
}
