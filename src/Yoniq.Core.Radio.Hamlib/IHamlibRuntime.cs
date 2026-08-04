namespace Yoniq.Core.Radio.Hamlib;

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

    /// <summary>The ready <see cref="IHamlibNative"/>, or throws <see cref="HamlibUnavailableException"/>
    /// (fresh instance each call, not a cached exception object) if discovery/the version gate failed.</summary>
    IHamlibNative Native { get; }
}
