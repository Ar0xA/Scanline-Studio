using ScanlineStudio.Abstractions.Settings;

namespace ScanlineStudio.Application;

/// <summary>The "no system keyring" fallback (macOS, a Linux session without Secret Service, a probe
/// failure/timeout). Holds nothing itself: with <see cref="IsSecure"/> false,
/// <see cref="QrzCredentialService"/> keeps the password in plaintext settings.json exactly as before
/// this store existed, so every method here is a no-op answer rather than a second storage location.</summary>
public sealed class SettingsFileCredentialStore : ICredentialStore
{
    public bool IsSecure => false;

    public string BackendName => "settings.json";

    public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
        Task.FromResult(CredentialRead.Absent);

    public Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default) =>
        Task.FromResult(CredentialWrite.Failed("The settings-file fallback does not store credentials itself."));

    public Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
        Task.FromResult(CredentialWrite.Success);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
}
