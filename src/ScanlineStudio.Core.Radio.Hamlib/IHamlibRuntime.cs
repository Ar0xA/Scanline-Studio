namespace ScanlineStudio.Core.Radio.Hamlib;

/// <summary>
/// Caches Hamlib discovery + the version gate for the lifetime of the instance -- see spec/03's
/// "Discovery order" ("the whole probe runs once at startup ... not re-run per connect attempt") and
/// the implementation plan's round-2 resolution: <see cref="HamlibProtocolFactory.Create"/> is called
/// on every <c>RadioController</c> backoff reconnect, so re-probing/re-loading the library each cycle
/// would leak handle refcounts and repeat the version check pointlessly. Also the hook a future
/// cross-backend-demotion policy (not built yet) would query.
/// </summary>
internal interface IHamlibRuntime
{
    bool IsAvailable { get; }

    /// <summary>The library path that actually loaded, captured immediately once
    /// <see cref="HamlibLibraryLocator.Locate"/> succeeds -- even if a later step (the version gate, or
    /// the native shim failing to resolve an export) still leaves <see cref="IsAvailable"/> false, so a
    /// caller can distinguish "wrong file" from "no file found" instead of just seeing "not available"
    /// either way. <see langword="null"/> if no candidate ever loaded.</summary>
    string? ResolvedPath { get; }

    /// <summary>The raw <c>rig_version()</c> string, if a candidate loaded far enough to call it.
    /// <see langword="null"/> otherwise (including when the version gate itself rejected it).</summary>
    string? Version { get; }

    /// <summary>Per-candidate discovery failure reasons, for diagnostics -- empty when
    /// <see cref="IsAvailable"/> is true.</summary>
    IReadOnlyList<string> Attempts { get; }

    /// <summary>The ready <see cref="IHamlibNative"/>, or throws <see cref="HamlibUnavailableException"/>
    /// (fresh instance each call, not a cached exception object) if discovery/the version gate failed.</summary>
    IHamlibNative Native { get; }
}
