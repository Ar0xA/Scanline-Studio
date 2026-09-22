using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;

namespace ScanlineStudio.UI.Tests;

/// <summary>An <see cref="OptionsSettingsService"/> over <paramref name="credentialStore"/> (default: the
/// plaintext settings-file fallback, i.e. pre-keyring behaviour).</summary>
internal static class TestOptionsSettings
{
    public static OptionsSettingsService Create(ISettingsStore settingsStore, ICredentialStore? credentialStore = null) =>
        new(
            settingsStore,
            new QrzCredentialService(settingsStore, CredentialStoreResolver.ForStore(credentialStore ?? new SettingsFileCredentialStore()), NullLogger<QrzCredentialService>.Instance),
            NullLogger<OptionsSettingsService>.Instance);
}

/// <summary>In-memory secure <see cref="ICredentialStore"/> that records every call and can be told to
/// report the keyring as locked (Unavailable without a prompt).</summary>
internal sealed class FakeUiCredentialStore : ICredentialStore
{
    public Dictionary<string, string> Items { get; } = new();

    public bool Locked { get; set; }

    public List<(string Operation, bool AllowPrompt)> Calls { get; } = new();

    public bool IsSecure => true;

    public string BackendName => "fake";

    public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default)
    {
        Calls.Add(("get", allowPrompt));
        if (!Items.TryGetValue(key, out var value))
        {
            return Task.FromResult(CredentialRead.Absent);
        }

        if (Locked && !allowPrompt)
        {
            return Task.FromResult(CredentialRead.Unavailable("locked"));
        }

        Locked = false;
        return Task.FromResult(CredentialRead.Found(value));
    }

    public Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default)
    {
        Calls.Add(("set", allowPrompt));
        if (Locked && !allowPrompt)
        {
            return Task.FromResult(CredentialWrite.Unavailable("locked"));
        }

        Items[key] = secret;
        return Task.FromResult(CredentialWrite.Success);
    }

    public Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default)
    {
        Calls.Add(("delete", allowPrompt));
        if (Locked && !allowPrompt)
        {
            return Task.FromResult(CredentialWrite.Unavailable("locked"));
        }

        Items.Remove(key);
        return Task.FromResult(CredentialWrite.Success);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
    {
        Calls.Add(("exists", false));
        return Task.FromResult(Items.ContainsKey(key));
    }
}
