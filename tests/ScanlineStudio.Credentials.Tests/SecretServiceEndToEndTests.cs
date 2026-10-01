using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Credentials.Tests;

/// <summary>Options save path end to end against the real Secret Service: OptionsSettingsService →
/// QrzCredentialService → SecretServiceCredentialStore, over a temporary settings.json. Uses a unique
/// credential key (and so a unique item label) per run, never the real <c>qrz-lookup-password</c> key.
/// Gated like the other real-keyring tests: skips on a locked or missing default collection; the
/// throwaway password is written without a prompt and the item is deleted in <c>finally</c>.</summary>
public sealed class SecretServiceEndToEndTests
{
    [RequiresSecretServiceFact]
    public async Task OptionsSave_WithRealKeyring_KeepsThePasswordOutOfSettingsJson_AndDeleteRemovesIt()
    {
        var keyring = SecretServiceProbe.Store!;
        var key = $"test-e2e-{Guid.NewGuid():N}";
        var password = $"throwaway-{Guid.NewGuid():N}";
        var directory = Directory.CreateTempSubdirectory("scanline-qrz-e2e-").FullName;
        var settingsPath = Path.Combine(directory, "settings.json");
        try
        {
            var settingsStore = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, settingsPath);
            var credentials = new QrzCredentialService(
                settingsStore,
                CredentialStoreResolver.ForStore(keyring),
                NullLogger<QrzCredentialService>.Instance,
                new QrzCredentialServiceOptions { CredentialKey = key });
            var options = new OptionsSettingsService(settingsStore, credentials, NullLogger<OptionsSettingsService>.Instance);

            var initial = await options.LoadQrzPasswordAsync(allowPrompt: false);
            Assert.Equal(CredentialReadStatus.Absent, initial.Status);
            Assert.True(initial.IsSecure);

            Assert.Equal(QrzPasswordWriteOutcome.Saved, await options.SaveQrzPasswordAsync(password, loadedPassword: null));

            Assert.True(File.Exists(settingsPath));
            Assert.DoesNotContain(password, await File.ReadAllTextAsync(settingsPath), StringComparison.Ordinal);
            Assert.True(await keyring.ExistsAsync(key));
            var section = (await settingsStore.LoadAsync()).GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings);
            Assert.Null(section?.Password);
            Assert.True(section?.PasswordInCredentialStore);
            var reloaded = await options.LoadQrzPasswordAsync(allowPrompt: false);
            Assert.Equal(password, reloaded.Password);

            Assert.Equal(QrzPasswordWriteOutcome.Saved, await options.SaveQrzPasswordAsync(null, loadedPassword: password));

            Assert.False(await keyring.ExistsAsync(key));
            Assert.Equal(CredentialReadStatus.Absent, (await options.LoadQrzPasswordAsync(allowPrompt: false)).Status);
            Assert.DoesNotContain(password, await File.ReadAllTextAsync(settingsPath), StringComparison.Ordinal);
        }
        finally
        {
            await keyring.DeleteAsync(key, allowPrompt: false);
            Directory.Delete(directory, recursive: true);
        }
    }
}
