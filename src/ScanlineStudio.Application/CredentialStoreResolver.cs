using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Settings;

namespace ScanlineStudio.Application;

/// <summary>Picks the credential store once per process without ever blocking startup or the UI thread:
/// the platform probe runs on the thread pool, bounded by <see cref="DefaultProbeTimeout"/>, and every
/// caller awaits the same cached task. A probe that returns <see langword="null"/>, throws, or times out
/// resolves to the plaintext <see cref="SettingsFileCredentialStore"/>.</summary>
public sealed partial class CredentialStoreResolver
{
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly Lazy<Task<ICredentialStore>> _resolved;

    public CredentialStoreResolver(Func<CancellationToken, Task<ICredentialStore?>> probe, TimeSpan probeTimeout, ILogger<CredentialStoreResolver> logger)
    {
        _resolved = new Lazy<Task<ICredentialStore>>(() => ResolveAsync(probe, probeTimeout, logger));
    }

    private CredentialStoreResolver(ICredentialStore store)
    {
        _resolved = new Lazy<Task<ICredentialStore>>(Task.FromResult(store));
    }

    /// <summary>An already-resolved instance — tests, and any composition that knows its store up front.</summary>
    public static CredentialStoreResolver ForStore(ICredentialStore store) => new(store);

    /// <summary>Starts the probe if it has not started yet; never throws.</summary>
    public Task<ICredentialStore> GetAsync(CancellationToken ct = default) => _resolved.Value.WaitAsync(ct);

    private static async Task<ICredentialStore> ResolveAsync(Func<CancellationToken, Task<ICredentialStore?>> probe, TimeSpan probeTimeout, ILogger logger)
    {
        // Not disposed here: a timed-out probe may still be running and observing this token.
        var timeoutCts = new CancellationTokenSource(probeTimeout);
        try
        {
            // Task.Run + WaitAsync: a probe that blocks synchronously or ignores cancellation still cannot hold callers past the timeout.
            var store = await Task.Run(() => probe(timeoutCts.Token)).WaitAsync(probeTimeout).ConfigureAwait(false);
            if (store is not null)
            {
                Log.BackendChosen(logger, store.BackendName, store.IsSecure);
                return store;
            }

            Log.NoSecureBackend(logger);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && timeoutCts.IsCancellationRequested))
        {
            Log.ProbeTimedOut(logger, probeTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            Log.ProbeFailed(logger, ex);
        }

        var fallback = new SettingsFileCredentialStore();
        Log.BackendChosen(logger, fallback.BackendName, fallback.IsSecure);
        return fallback;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "Credential store backend: {Backend} (secure={IsSecure})")]
        public static partial void BackendChosen(ILogger logger, string backend, bool isSecure);

        [LoggerMessage(Level = LogLevel.Information, Message = "No system keyring found; the QRZ password stays in settings.json")]
        public static partial void NoSecureBackend(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Credential store probe timed out after {Seconds} s; using settings.json")]
        public static partial void ProbeTimedOut(ILogger logger, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Credential store probe failed; using settings.json")]
        public static partial void ProbeFailed(ILogger logger, Exception ex);
    }
}
