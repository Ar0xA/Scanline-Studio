using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Credentials.SecretService;

namespace ScanlineStudio.Credentials.Tests;

/// <summary>Runs only against a real, already-unlocked Secret Service default collection. Skips when there
/// is no session bus, no <c>org.freedesktop.secrets</c>, no default collection, or the collection is locked.
/// The probe never unlocks and never prompts.</summary>
public sealed class RequiresSecretServiceFactAttribute : FactAttribute
{
    public RequiresSecretServiceFactAttribute()
    {
        if (SecretServiceProbe.SkipReason.Value is { } reason)
        {
            Skip = reason;
        }
    }
}

internal static class SecretServiceProbe
{
    /// <summary>Null when a usable, unlocked store exists; else why the tests skip.</summary>
    public static readonly Lazy<string?> SkipReason = new(() => Task.Run(ProbeAsync).GetAwaiter().GetResult());

    public static SecretServiceCredentialStore? Store { get; private set; }

    private static async Task<string?> ProbeAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Secret Service tests are Linux-only.";
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var store = await SecretServiceCredentialStore.TryCreateAsync(NullLogger.Instance, timeout.Token).ConfigureAwait(false);
            if (store is null)
            {
                return "No usable Secret Service (no session bus, no org.freedesktop.secrets, or no default collection).";
            }

            if (await store.IsDefaultCollectionLockedAsync(timeout.Token).ConfigureAwait(false) != false)
            {
                return "The Secret Service default collection is locked or missing; these tests never unlock it.";
            }

            Store = store;
            return null;
        }
        catch (Exception ex)
        {
            return $"Secret Service probe failed: {ex.Message}";
        }
    }
}
