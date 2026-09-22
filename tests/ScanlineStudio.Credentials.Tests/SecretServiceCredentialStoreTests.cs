using ScanlineStudio.Abstractions.Settings;

namespace ScanlineStudio.Credentials.Tests;

/// <summary>Real Secret Service round trip. Every call passes <c>allowPrompt: false</c>, so no keyring
/// dialog can appear; the unique test item is deleted in <c>finally</c>.</summary>
public sealed class SecretServiceCredentialStoreTests
{
    [RequiresSecretServiceFact]
    public async Task RealStore_SetGetExistsDelete_RoundTripsWithoutPrompting()
    {
        var store = SecretServiceProbe.Store!;
        var key = $"test-{Guid.NewGuid():N}";
        const string secret = "pässwörd ✓ 日本 \"quoted\"";
        try
        {
            Assert.Equal(CredentialReadStatus.Absent, (await store.GetAsync(key, allowPrompt: false)).Status);
            Assert.False(await store.ExistsAsync(key));

            Assert.Equal(CredentialWriteStatus.Success, (await store.SetAsync(key, "first", allowPrompt: false)).Status);
            Assert.Equal(CredentialWriteStatus.Success, (await store.SetAsync(key, secret, allowPrompt: false)).Status);

            var read = await store.GetAsync(key, allowPrompt: false);
            Assert.Equal(CredentialReadStatus.Found, read.Status);
            Assert.Equal(secret, read.Secret);
            Assert.True(await store.ExistsAsync(key));

            Assert.Equal(CredentialWriteStatus.Success, (await store.DeleteAsync(key, allowPrompt: false)).Status);
            Assert.Equal(CredentialReadStatus.Absent, (await store.GetAsync(key, allowPrompt: false)).Status);
            Assert.False(await store.ExistsAsync(key));
            Assert.Equal(CredentialWriteStatus.Success, (await store.DeleteAsync(key, allowPrompt: false)).Status);
        }
        finally
        {
            await store.DeleteAsync(key, allowPrompt: false);
        }
    }

    [RequiresSecretServiceFact]
    public void RealStore_IsSecure()
    {
        Assert.True(SecretServiceProbe.Store!.IsSecure);
    }
}
