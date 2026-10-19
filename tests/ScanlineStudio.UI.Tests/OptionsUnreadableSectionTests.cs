using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Logbook;
using ScanlineStudio.Core.Radio;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>One unreadable settings.json section must not stop the Options dialog from loading or saving.</summary>
public sealed class OptionsUnreadableSectionTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    private static FakeSettingsStore StoreWith(string badKey) => new()
    {
        Settings = new AppSettings { Sections = new() { [badKey] = Json("\"not an object\"") } }
            .WithSection(OperatorSettings.SectionKey, new OperatorSettings { Callsign = "PA0XYZ" }, OperatorSettingsJsonContext.Default.OperatorSettings),
    };

    private static (OptionsWindowViewModel Vm, FakeLocalizationService Localization) CreateVm(
        FakeSettingsStore store, FakeRadioSessionService? radio = null, FakeSstvSessionService? session = null, ICredentialStore? credentialStore = null)
    {
        var localization = new FakeLocalizationService();
        var vm = new OptionsWindowViewModel(TestOptionsSettings.Create(store, credentialStore), localization, new FakeAudioDeviceEnumerator(), new FakeLogbookSessionService(), store, radio ?? new FakeRadioSessionService(), new FakeHamlibDiscoveryService(), new FakeFilePickerService(), session ?? new FakeSstvSessionService(), new FakeSerialPortEnumerator(), new FakeReceiveHistoryStore(), new FakeAppLocationsService(), new FakeApplicationRestarter(), new FakeAppearanceSettingsService(), NullLogger<OptionsWindowViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        return (vm, localization);
    }

    private static string[] ReportedSections(FakeLocalizationService localization) =>
        localization.Calls.Where(c => c.Key == "Options.Load.UnreadableSections").Select(c => (string)c.Args[0]).ToArray();

    [Fact]
    public async Task Service_Load_UnreadableRadioSection_ShowsDefaultsKeepsOtherSectionsAndReportsIt()
    {
        var store = StoreWith(RadioConnectionSettings.SectionKey);
        var service = TestOptionsSettings.Create(store);

        var snapshot = await service.LoadAsync();

        Assert.Equal("PA0XYZ", snapshot.Callsign);
        Assert.Equal([RadioConnectionSettings.SectionKey], snapshot.UnreadableSections);
    }

    [Fact]
    public async Task Service_Save_UnreadableRadioSection_IsKeptAsideAndRewritten_OtherSectionsUnchanged()
    {
        var store = StoreWith(RadioConnectionSettings.SectionKey);
        var raw = store.Settings.Sections[RadioConnectionSettings.SectionKey].GetRawText();
        var service = TestOptionsSettings.Create(store);
        var snapshot = await service.LoadAsync();

        await service.SaveAsync(snapshot);

        Assert.Equal(raw, store.Settings.Sections[RadioConnectionSettings.SectionKey + ".unreadable"].GetRawText());
        Assert.NotNull(store.Settings.GetSection(RadioConnectionSettings.SectionKey, RadioSettingsJsonContext.Default.RadioConnectionSettings));
        Assert.Equal("PA0XYZ", store.Settings.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)!.Callsign);
    }

    [Fact]
    public async Task Service_NormalLoadAndSave_ReportsNothing_AndAddsNoUnreadableKey()
    {
        var store = new FakeSettingsStore();
        var service = TestOptionsSettings.Create(store);

        var snapshot = await service.LoadAsync();
        await service.SaveAsync(snapshot);

        Assert.Empty(snapshot.UnreadableSections!);
        Assert.DoesNotContain(store.Settings.Sections.Keys, k => k.EndsWith(".unreadable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Service_UnreadableAdifUdpSection_LoadsReportsIt_AndSaveKeepsItAside()
    {
        var store = StoreWith(AdifUdpStreamingSettings.SectionKey);
        var service = TestOptionsSettings.Create(store);

        var snapshot = await service.LoadAsync();
        Assert.Equal([AdifUdpStreamingSettings.SectionKey], snapshot.UnreadableSections);

        await service.SaveAsync(snapshot);
        Assert.True(store.Settings.Sections.ContainsKey(AdifUdpStreamingSettings.SectionKey + ".unreadable"));
        Assert.NotNull(store.Settings.GetSection(AdifUdpStreamingSettings.SectionKey, AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings));
    }

    [Fact]
    public async Task Service_UnreadableLegacyGridTrackerSection_LoadsAndReportsIt()
    {
        var store = StoreWith(LegacyGridTrackerStreamingSettings.SectionKey);
        var raw = store.Settings.Sections[LegacyGridTrackerStreamingSettings.SectionKey].GetRawText();
        var service = TestOptionsSettings.Create(store);

        var snapshot = await service.LoadAsync();
        await service.SaveAsync(snapshot);

        Assert.Equal([LegacyGridTrackerStreamingSettings.SectionKey], snapshot.UnreadableSections);
        Assert.Equal(raw, store.Settings.Sections[LegacyGridTrackerStreamingSettings.SectionKey + ".unreadable"].GetRawText());
        Assert.NotNull(store.Settings.GetSection(AdifUdpStreamingSettings.SectionKey, AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings));
    }

    [AvaloniaFact]
    public async Task Dialog_UnreadableAppearanceSection_LoadsWithANotice_AndSaveKeepsItAsideAndRewritesIt()
    {
        var store = StoreWith(AppearanceSettings.SectionKey);
        var raw = store.Settings.Sections[AppearanceSettings.SectionKey].GetRawText();
        var (vm, localization) = CreateVm(store);

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal("Options.Load.UnreadableSections", vm.SaveErrorMessage);
        Assert.Equal([AppearanceSettings.SectionKey], ReportedSections(localization));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(raw, store.Settings.Sections[AppearanceSettings.SectionKey + ".unreadable"].GetRawText());
        Assert.NotNull(store.Settings.GetSection(AppearanceSettings.SectionKey, AppearanceSettingsJsonContext.Default.AppearanceSettings));
    }

    [AvaloniaFact]
    public void Dialog_UnreadableServiceSection_IsNamedInTheNotice()
    {
        var (vm, localization) = CreateVm(StoreWith(RadioConnectionSettings.SectionKey));

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Equal([RadioConnectionSettings.SectionKey], ReportedSections(localization));
    }

    [AvaloniaFact]
    public void Dialog_UnreadableRadioSafetyAndDrmRx_StillLoad_WithDefaultsAndANotice()
    {
        var radio = new FakeRadioSessionService { GetSafetySettingsFailure = new JsonException("bad") };
        var session = new FakeSstvSessionService { SavedDrmReceiveEnabled = !DrmReceiveDefaultsForTest, GetDrmReceiveEnabledFailure = new JsonException("bad") };

        var (vm, localization) = CreateVm(new FakeSettingsStore(), radio, session);

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.SwrCutoffEnabled);
        Assert.Equal(DrmReceiveDefaultsForTest, vm.DrmReceiveEnabled);
        Assert.Equal(["DrmRx, RadioSafety"], ReportedSections(localization));
    }

    [AvaloniaFact]
    public async Task Dialog_UnreadableDrmRx_FirstSaveRewritesItEvenWithTheToggleUntouched_SecondSaveDoesNot()
    {
        var session = new FakeSstvSessionService { GetDrmReceiveEnabledFailure = new JsonException("bad") };
        var (vm, _) = CreateVm(new FakeSettingsStore(), session: session);
        session.GetDrmReceiveEnabledFailure = null;

        await vm.SaveCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal([DrmReceiveDefaultsForTest], session.DrmReceiveEnabledCalls);
    }

    [AvaloniaFact]
    public async Task Dialog_UnreadableQrzLookup_EnteringAPasswordAndOneSave_RepairsIt()
    {
        var store = StoreWith(QrzLookupSettings.SectionKey);
        var raw = store.Settings.Sections[QrzLookupSettings.SectionKey].GetRawText();
        var (vm, _) = CreateVm(store);

        vm.QrzLookupEnabled = true;
        vm.QrzLookupUsername = "PA0XYZ";
        vm.QrzLookupPassword = "pw";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(vm.SaveErrorMessage);
        Assert.Equal(raw, store.Settings.Sections[QrzLookupSettings.SectionKey + ".unreadable"].GetRawText());
        var section = store.Settings.GetSection(QrzLookupSettings.SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings)!;
        Assert.True(section.Enabled);
        Assert.Equal("PA0XYZ", section.Username);
        Assert.Equal("pw", section.Password);
    }

    [AvaloniaFact]
    public void Dialog_UnreadableQrzLookup_UnlockedKeyring_LoadsThePassword_AndIsNotShownAsLocked()
    {
        var keyring = new FakeUiCredentialStore();
        keyring.Items[QrzCredentialService.CredentialKey] = "pw";

        var (vm, _) = CreateVm(StoreWith(QrzLookupSettings.SectionKey), credentialStore: keyring);

        Assert.False(vm.IsQrzKeyringLocked);
        Assert.Equal("pw", vm.QrzLookupPassword);
    }

    [AvaloniaFact]
    public void Dialog_UnreadableQrzLookup_NoKeyring_IsNotShownAsUnreachable()
    {
        var (vm, _) = CreateVm(StoreWith(QrzLookupSettings.SectionKey));

        Assert.False(vm.IsQrzKeyringLocked);
        Assert.False(vm.IsQrzKeyringUnreachable);
    }

    [AvaloniaFact]
    public void Dialog_NoUnreadableSection_ShowsNoNotice()
    {
        var store = new FakeSettingsStore();
        var (vm, localization) = CreateVm(store);

        Assert.Null(vm.SaveErrorMessage);
        Assert.Empty(ReportedSections(localization));
        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.True(vm.ApplyCommand.CanExecute(null));
        Assert.True(store.LoadCount >= 3, $"only {store.LoadCount} settings loads; the fallback theory below needs at least 3");
    }

    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Dialog_AnySettingsLoadServedAFallback_DisablesSaveAndApply_AndSaysWhy(int fallbackLoad)
    {
        var store = StoreWith(AppearanceSettings.SectionKey);
        store.FallbackOnLoads.Add(fallbackLoad);

        var (vm, _) = CreateVm(store);

        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal("Options.Load.SettingsUnavailable", vm.SaveErrorMessage);
    }

    [AvaloniaFact]
    public void Dialog_FallbackServedBeforeTheDialogOpened_DoesNotDisableSave()
    {
        var store = new FakeSettingsStore { FallbackServeCount = 5 };

        var (vm, _) = CreateVm(store);

        Assert.True(vm.SaveCommand.CanExecute(null));
        Assert.Null(vm.SaveErrorMessage);
    }

    private static bool DrmReceiveDefaultsForTest => ScanlineStudio.Application.DrmReceiveDefaults.Enabled;
}
