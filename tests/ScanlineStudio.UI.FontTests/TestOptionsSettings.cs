using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;

namespace ScanlineStudio.UI.FontTests;

/// <summary>An <see cref="OptionsSettingsService"/> whose QRZ password uses the plaintext settings-file fallback.</summary>
internal static class TestOptionsSettings
{
    public static OptionsSettingsService Create(ISettingsStore settingsStore) =>
        new(
            settingsStore,
            new QrzCredentialService(settingsStore, CredentialStoreResolver.ForStore(new SettingsFileCredentialStore()), NullLogger<QrzCredentialService>.Instance),
            NullLogger<OptionsSettingsService>.Instance);
}
