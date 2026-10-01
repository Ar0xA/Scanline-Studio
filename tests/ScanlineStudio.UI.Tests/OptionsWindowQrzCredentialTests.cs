using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Options dialog ↔ QRZ password credential store (QRZ-PW).</summary>
public sealed class OptionsWindowQrzCredentialTests
{
    private const string Key = QrzCredentialService.CredentialKey;

    private static OptionsWindowViewModel CreateVm(FakeSettingsStore settingsStore, ICredentialStore credentialStore, FakeLocalizationService? localization = null)
    {
        var vm = new OptionsWindowViewModel(TestOptionsSettings.Create(settingsStore, credentialStore), localization ?? new FakeLocalizationService(), new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), settingsStore, new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), new FakeSstvSessionService(), new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return vm;
    }

    private static FakeSettingsStore StoreWith(QrzLookupSettings section) =>
        new() { Settings = new AppSettings().WithSection(QrzLookupSettings.SectionKey, section, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) };

    private static QrzLookupSettings Section(FakeSettingsStore store) =>
        store.Settings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();

    // B1
    [AvaloniaFact]
    public async Task Save_AfterUnavailableLoad_WithFieldUntouched_NeitherDeletesNorWritesThePassword()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user", PasswordInCredentialStore = true });
        var credentialStore = new FakeUiCredentialStore { Locked = true };
        credentialStore.Items[Key] = "secret";
        var vm = CreateVm(settingsStore, credentialStore);
        Assert.True(vm.IsQrzKeyringLocked);
        Assert.Null(vm.QrzLookupPassword);

        vm.Callsign = "N0CALL";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(vm.SaveErrorMessage);
        Assert.Equal("secret", credentialStore.Items[Key]);
        Assert.DoesNotContain(credentialStore.Calls, c => c.Operation is "set" or "delete");
        Assert.True(Section(settingsStore).PasswordInCredentialStore);
    }

    // R4
    [AvaloniaFact]
    public async Task Load_NeverPrompts_UnlockButtonIsTheOnlyPromptingPath_AndFillsTheField()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user", PasswordInCredentialStore = true });
        var credentialStore = new FakeUiCredentialStore { Locked = true };
        credentialStore.Items[Key] = "secret";
        var vm = CreateVm(settingsStore, credentialStore);

        Assert.All(credentialStore.Calls, c => Assert.False(c.AllowPrompt));
        Assert.Equal("Options.Qrz.KeyringLockedWatermark", vm.QrzLookupPasswordWatermark);
        Assert.True(vm.UnlockQrzKeyringCommand.CanExecute(null));

        await vm.UnlockQrzKeyringCommand.ExecuteAsync(null);

        Assert.Contains(("get", true), credentialStore.Calls);
        Assert.Equal("secret", vm.QrzLookupPassword);
        Assert.False(vm.IsQrzKeyringLocked);
        Assert.Equal("Options.Qrz.QrzLookupPasswordWatermark", vm.QrzLookupPasswordWatermark);
    }

    // R1
    [AvaloniaFact]
    public async Task Save_KeyringWriteFails_ShowsSaveError_KeepsDialogOpen_AndLeavesSettingsJsonUntouched()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user" });
        var credentialStore = new FailingCredentialStore();
        var vm = CreateVm(settingsStore, credentialStore);
        var closed = false;
        vm.RequestClose += () => closed = true;
        var before = settingsStore.Settings;

        vm.Callsign = "N0CALL";
        vm.QrzLookupPassword = "secret";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Options.Qrz.Error.KeyringWriteFailed", vm.SaveErrorMessage);
        Assert.False(closed);
        Assert.Same(before, settingsStore.Settings);
        Assert.Null(Section(settingsStore).Password);
    }

    [AvaloniaFact]
    public void Load_FallbackSessionWithCredentialStoreFlag_ShowsKeyringNotAvailable_NoUnlockButton()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user", PasswordInCredentialStore = true });
        var vm = CreateVm(settingsStore, new SettingsFileCredentialStore());

        Assert.False(vm.IsQrzKeyringLocked);
        Assert.False(vm.UnlockQrzKeyringCommand.CanExecute(null));
        Assert.True(vm.IsQrzKeyringUnreachable);
        Assert.Equal("Options.Qrz.KeyringUnavailableWatermark", vm.QrzLookupPasswordWatermark);
        Assert.Null(vm.QrzLookupPassword);
    }

    [AvaloniaFact]
    public async Task Save_KeyringVerifyFails_ShowsTheVerifyMessage_NotTheWriteFailedOne()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user" });
        var credentialStore = new GarblingCredentialStore();
        var vm = CreateVm(settingsStore, credentialStore);
        var before = settingsStore.Settings;

        vm.QrzLookupPassword = "secret";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Options.Qrz.Error.KeyringVerifyFailed", vm.SaveErrorMessage);
        Assert.Same(before, settingsStore.Settings);
    }

    [AvaloniaFact]
    public async Task Save_SecureStore_ChangedPassword_GoesToKeyring_NotSettingsJson()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user" });
        var credentialStore = new FakeUiCredentialStore();
        var vm = CreateVm(settingsStore, credentialStore);

        vm.QrzLookupPassword = "secret";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(vm.SaveErrorMessage);
        Assert.Equal("secret", credentialStore.Items[Key]);
        Assert.Null(Section(settingsStore).Password);
        Assert.True(Section(settingsStore).PasswordInCredentialStore);
        Assert.Equal("user", Section(settingsStore).Username);
    }

    [AvaloniaFact]
    public async Task Save_UntouchedField_DoesNotRewriteAnUnchangedKeyringPassword()
    {
        var settingsStore = StoreWith(new QrzLookupSettings { Enabled = true, Username = "user", PasswordInCredentialStore = true });
        var credentialStore = new FakeUiCredentialStore();
        credentialStore.Items[Key] = "secret";
        var vm = CreateVm(settingsStore, credentialStore);
        Assert.Equal("secret", vm.QrzLookupPassword);

        vm.Callsign = "N0CALL";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.DoesNotContain(credentialStore.Calls, c => c.Operation is "set" or "delete");
    }

    [AvaloniaFact]
    public void PlaintextWarning_VisibleOnlyWhenNotSecureAndAPasswordIsSet()
    {
        var fallbackVm = CreateVm(new FakeSettingsStore(), new SettingsFileCredentialStore());
        Assert.False(fallbackVm.ShowQrzPlaintextWarning);
        Assert.Equal("Options.Qrz.QrzLookupPasswordHint", fallbackVm.QrzLookupPasswordHint);
        fallbackVm.QrzLookupPassword = "secret";
        Assert.True(fallbackVm.ShowQrzPlaintextWarning);

        var secureVm = CreateVm(new FakeSettingsStore(), new FakeUiCredentialStore());
        secureVm.QrzLookupPassword = "secret";
        Assert.False(secureVm.ShowQrzPlaintextWarning);
        Assert.Equal("Options.Qrz.QrzLookupPasswordHint.Keyring", secureVm.QrzLookupPasswordHint);
    }

    /// <summary>Accepts every write but reads back a different value (verify mismatch).</summary>
    private sealed class GarblingCredentialStore : ICredentialStore
    {
        private bool _written;

        public bool IsSecure => true;

        public string BackendName => "garbling";

        public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
            Task.FromResult(_written ? CredentialRead.Found("garbled") : CredentialRead.Absent);

        public Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default)
        {
            _written = true;
            return Task.FromResult(CredentialWrite.Success);
        }

        public Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default) => Task.FromResult(CredentialWrite.Success);

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(_written);
    }

    private sealed class FailingCredentialStore : ICredentialStore
    {
        public bool IsSecure => true;

        public string BackendName => "failing";

        public Task<CredentialRead> GetAsync(string key, bool allowPrompt, CancellationToken ct = default) => Task.FromResult(CredentialRead.Absent);

        public Task<CredentialWrite> SetAsync(string key, string secret, bool allowPrompt, CancellationToken ct = default) =>
            Task.FromResult(CredentialWrite.Failed("bus error"));

        public Task<CredentialWrite> DeleteAsync(string key, bool allowPrompt, CancellationToken ct = default) =>
            Task.FromResult(CredentialWrite.Failed("bus error"));

        public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(false);
    }
}
