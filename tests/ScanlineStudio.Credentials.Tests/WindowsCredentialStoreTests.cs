using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Credentials.Windows;

namespace ScanlineStudio.Credentials.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStoreTests
{
    private static WindowsCredentialStore CreateStore() => new(NullLogger.Instance);

    [WindowsFact]
    public async Task RoundTrip_IncludingNonAscii()
    {
        var store = CreateStore();
        var key = $"test-{Guid.NewGuid():N}";
        const string secret = "pässwörd ✓ 日本 \"quoted\"";
        try
        {
            Assert.Equal(CredentialWriteStatus.Success, (await store.SetAsync(key, secret, allowPrompt: false)).Status);

            var read = await store.GetAsync(key, allowPrompt: false);
            Assert.Equal(CredentialReadStatus.Found, read.Status);
            Assert.Equal(secret, read.Secret);
            Assert.True(await store.ExistsAsync(key));
        }
        finally
        {
            await store.DeleteAsync(key, allowPrompt: false);
        }
    }

    [WindowsFact]
    public async Task SecretOverTheBlobLimit_IsRejected()
    {
        var store = CreateStore();
        var key = $"test-{Guid.NewGuid():N}";
        try
        {
            var tooLong = new string('x', (WindowsCredentialStore.MaxBlobBytes / 2) + 1);

            var result = await store.SetAsync(key, tooLong, allowPrompt: false);

            Assert.Equal(CredentialWriteStatus.Failed, result.Status);
            Assert.False(await store.ExistsAsync(key));
        }
        finally
        {
            await store.DeleteAsync(key, allowPrompt: false);
        }
    }

    [WindowsFact]
    public async Task AbsentKey_ReadsAbsent()
    {
        var read = await CreateStore().GetAsync($"test-{Guid.NewGuid():N}", allowPrompt: false);

        Assert.Equal(CredentialReadStatus.Absent, read.Status);
    }

    [WindowsFact]
    public async Task DeletingAMissingKey_Succeeds()
    {
        var result = await CreateStore().DeleteAsync($"test-{Guid.NewGuid():N}", allowPrompt: false);

        Assert.Equal(CredentialWriteStatus.Success, result.Status);
    }

    [WindowsFact]
    public void IsAvailable_OnANormalDesktopLogon()
    {
        Assert.True(WindowsCredentialStore.IsAvailable());
    }
}
