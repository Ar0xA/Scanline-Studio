using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Configurations-preset backlog, Phase 4b (2026-08-28, cascading-menu redesign, superseding
/// the earlier modal "Manage Configurations" dialog). Design history:
/// `docs/plans/configuration-presets-phase4b-cascading-menu-plan.md` (2 plan-review rounds).</summary>
public sealed class ConfigurationsManagerWindowViewModelTests
{
    private static (ConfigurationsManagerWindowViewModel Vm, FakeConfigurationPresetStore Store, FakeConfigurationPresetService Service, FakeSettingsStore SettingsStore, FakeLocalizationService Localization)
        Create(AppSettings? initialSettings = null)
    {
        var store = new FakeConfigurationPresetStore();
        var service = new FakeConfigurationPresetService();
        var settingsStore = new FakeSettingsStore { Settings = initialSettings ?? new AppSettings() };
        var localization = new FakeLocalizationService();
        var vm = new ConfigurationsManagerWindowViewModel(store, service, settingsStore, localization, NullLogger<ConfigurationsManagerWindowViewModel>.Instance);
        return (vm, store, service, settingsStore, localization);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_NoPresetsExist_SeedsDefaultAndListsIt()
    {
        var (vm, store, _, _, _) = Create();

        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.Presets);
        Assert.Equal(ConfigurationsManagerWindowViewModel.DefaultPresetName, vm.Presets[0].Name);
        Assert.True(vm.Presets[0].IsProtected);
        var listed = await store.ListPresetsAsync();
        Assert.Contains(ConfigurationsManagerWindowViewModel.DefaultPresetName, listed);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_NoActivePresetYet_SeededDefaultBecomesActive()
    {
        var (vm, _, _, settingsStore, _) = Create();

        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var marker = settingsStore.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal(ConfigurationsManagerWindowViewModel.DefaultPresetName, marker?.ActivePresetName);
        Assert.True(vm.Presets[0].IsActive);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_ARealPresetIsAlreadyActive_SeedingDefaultDoesNotStealTheMarker()
    {
        // Seeding "Default" must not overwrite a genuinely active real preset's own marker (e.g. a
        // presets folder emptied by hand while settings.json still names a real preset active).
        var initial = new AppSettings().WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "Field Day" },
            ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        var (vm, _, _, settingsStore, _) = Create(initial);

        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var marker = settingsStore.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("Field Day", marker?.ActivePresetName);
        Assert.False(vm.Presets.Single(p => p.Name == ConfigurationsManagerWindowViewModel.DefaultPresetName).IsActive);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_DefaultAlreadyExists_DoesNotReseedOrTouchItsContent()
    {
        var store = new FakeConfigurationPresetStore();
        var marker = new AppSettings().WithSection(
            OperatorSettings.SectionKey, new OperatorSettings { Callsign = "ORIGINAL" }, OperatorSettingsJsonContext.Default.OperatorSettings);
        store.Seed(ConfigurationsManagerWindowViewModel.DefaultPresetName, marker);
        var service = new FakeConfigurationPresetService();
        var settingsStore = new FakeSettingsStore { Settings = new AppSettings() };
        var vm = new ConfigurationsManagerWindowViewModel(store, service, settingsStore, new FakeLocalizationService(), NullLogger<ConfigurationsManagerWindowViewModel>.Instance);

        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        var stillThere = await store.LoadPresetAsync(ConfigurationsManagerWindowViewModel.DefaultPresetName);
        Assert.Equal("ORIGINAL", stillThere?.GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)?.Callsign);
    }

    [AvaloniaFact]
    public async Task RefreshAsync_DefaultSortsLast_RegardlessOfAlphabeticalOrder()
    {
        // User-requested (2026-08-28): flipped from sorting first to sorting last.
        var store = new FakeConfigurationPresetStore();
        store.Seed("Alpha");
        store.Seed(ConfigurationsManagerWindowViewModel.DefaultPresetName);
        store.Seed("Zulu");
        var vm = new ConfigurationsManagerWindowViewModel(store, new FakeConfigurationPresetService(), new FakeSettingsStore(), new FakeLocalizationService(), NullLogger<ConfigurationsManagerWindowViewModel>.Instance);

        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["Alpha", "Zulu", ConfigurationsManagerWindowViewModel.DefaultPresetName], vm.Presets.Select(p => p.Name));
    }

    [AvaloniaFact]
    public async Task DeleteAsync_OnTheProtectedDefaultRow_IsANoOp_ConfirmNeverShown()
    {
        var (vm, store, _, _, _) = Create();
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var defaultRow = vm.Presets.Single();
        var confirmShown = false;
        vm.ConfirmRequested = _ => { confirmShown = true; return Task.FromResult(true); };

        await vm.DeleteCommand.ExecuteAsync(defaultRow);
        Dispatcher.UIThread.RunJobs();

        Assert.False(confirmShown);
        var listed = await store.ListPresetsAsync();
        Assert.Contains(ConfigurationsManagerWindowViewModel.DefaultPresetName, listed);
    }

    [AvaloniaFact]
    public async Task DeleteAsync_OnTheActiveNonDefaultRow_IsBlocked_ConfirmNeverShown()
    {
        // Phase 4b fix: the earlier modal-dialog build never actually blocked deleting the currently
        // active (non-Default) configuration -- only IsProtected was checked. ConfigurationPresetRowViewModel.CanDelete
        // closes that gap.
        var store = new FakeConfigurationPresetStore();
        store.Seed(ConfigurationsManagerWindowViewModel.DefaultPresetName);
        store.Seed("Field Day");
        var initial = new AppSettings().WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "Field Day" },
            ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        var vm = new ConfigurationsManagerWindowViewModel(store, new FakeConfigurationPresetService(), new FakeSettingsStore { Settings = initial }, new FakeLocalizationService(), NullLogger<ConfigurationsManagerWindowViewModel>.Instance);
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var activeRow = vm.Presets.Single(p => p.Name == "Field Day");
        Assert.True(activeRow.IsActive);
        Assert.False(activeRow.CanDelete);
        var confirmShown = false;
        vm.ConfirmRequested = _ => { confirmShown = true; return Task.FromResult(true); };

        await vm.DeleteCommand.ExecuteAsync(activeRow);
        Dispatcher.UIThread.RunJobs();

        Assert.False(confirmShown);
        Assert.Contains("Field Day", await store.ListPresetsAsync());
    }

    [AvaloniaFact]
    public async Task DeleteAsync_ConfirmDeclined_DoesNotDelete()
    {
        var store = new FakeConfigurationPresetStore();
        store.Seed(ConfigurationsManagerWindowViewModel.DefaultPresetName);
        store.Seed("Field Day");
        var vm = new ConfigurationsManagerWindowViewModel(store, new FakeConfigurationPresetService(), new FakeSettingsStore(), new FakeLocalizationService(), NullLogger<ConfigurationsManagerWindowViewModel>.Instance);
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        vm.ConfirmRequested = _ => Task.FromResult(false);

        await vm.DeleteCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Field Day", await store.ListPresetsAsync());
    }

    [AvaloniaFact]
    public async Task DeleteAsync_ConfirmRequestedUnwired_DefaultsToDeclined_DoesNotDelete()
    {
        // The safe default for an unwired confirm on a destructive action is "decline," not
        // "confirmed" -- matches TextPromptRequested's own null-returns-cancel shape.
        var store = new FakeConfigurationPresetStore();
        store.Seed(ConfigurationsManagerWindowViewModel.DefaultPresetName);
        store.Seed("Field Day");
        var vm = new ConfigurationsManagerWindowViewModel(store, new FakeConfigurationPresetService(), new FakeSettingsStore(), new FakeLocalizationService(), NullLogger<ConfigurationsManagerWindowViewModel>.Instance);
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");

        await vm.DeleteCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Field Day", await store.ListPresetsAsync());
    }

    [AvaloniaFact]
    public async Task DeleteAsync_ConfirmAccepted_Deletes()
    {
        var store = new FakeConfigurationPresetStore();
        store.Seed(ConfigurationsManagerWindowViewModel.DefaultPresetName);
        store.Seed("Field Day");
        var vm = new ConfigurationsManagerWindowViewModel(store, new FakeConfigurationPresetService(), new FakeSettingsStore(), new FakeLocalizationService(), NullLogger<ConfigurationsManagerWindowViewModel>.Instance);
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        vm.ConfirmRequested = _ => Task.FromResult(true);

        await vm.DeleteCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain("Field Day", await store.ListPresetsAsync());
    }

    [AvaloniaFact]
    public async Task RenameAsync_OnTheProtectedDefaultRow_IsANoOp()
    {
        // User-caught trap (2026-08-28): Rename briefly allowed on Default mid-session, then reverted
        // -- protection is keyed on the literal string "Default," so renaming it doesn't customize
        // Default, it detaches the content under a new unprotected name while a fresh "Default"
        // auto-seeds behind it on the next refresh. Blocked outright now, same as Delete.
        var (vm, store, _, _, _) = Create();
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var defaultRow = vm.Presets.Single();
        var promptShown = false;
        vm.TextPromptRequested = _ => { promptShown = true; return Task.FromResult<string?>("My Rig"); };

        await vm.RenameCommand.ExecuteAsync(defaultRow);
        Dispatcher.UIThread.RunJobs();

        Assert.False(promptShown);
        var listed = await store.ListPresetsAsync();
        Assert.Contains(ConfigurationsManagerWindowViewModel.DefaultPresetName, listed);
        Assert.DoesNotContain("My Rig", listed);
    }

    [AvaloniaFact]
    public async Task ResetToDefaultAsync_ConfirmDeclined_IsANoOp()
    {
        var (vm, store, service, _, _) = Create();
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var defaultRow = vm.Presets.Single();
        vm.ConfirmRequested = _ => Task.FromResult(false);

        await vm.ResetToDefaultCommand.ExecuteAsync(defaultRow);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, service.SwitchCallCount);
    }

    [AvaloniaFact]
    public async Task ResetToDefaultAsync_Confirmed_SwitchesToDefault()
    {
        var (vm, store, service, _, _) = Create();
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var defaultRow = vm.Presets.Single();
        vm.ConfirmRequested = _ => Task.FromResult(true);
        service.ResultToReturn = new ConfigurationPresetSwitchResult(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);

        await vm.ResetToDefaultCommand.ExecuteAsync(defaultRow);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(ConfigurationsManagerWindowViewModel.DefaultPresetName, service.LastRequestedName);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_Applied_RefreshesAndClearsMessages()
    {
        var (vm, store, service, _, _) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        vm.ErrorMessage = "stale";
        service.ResultToReturn = new ConfigurationPresetSwitchResult(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Field Day", service.LastRequestedName);
        Assert.Null(vm.ErrorMessage);
        Assert.Null(vm.WarningMessage);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_PartiallyApplied_SetsWarningMessage_NotError()
    {
        var (vm, store, service, _, _) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ResultToReturn = new ConfigurationPresetSwitchResult(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: false, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: true);

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ErrorMessage);
        Assert.NotNull(vm.WarningMessage);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_RxAudioDeferredAndPartiallyApplied_ComposesBothIntoOneWarning()
    {
        // Deferred and PartiallyApplied are DISTINCT conditions that can both be true at once -- the
        // shown message must reflect both, not just the last one checked.
        var (vm, store, service, _, _) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ResultToReturn = new ConfigurationPresetSwitchResult(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: false, CultureToApply: null, RxAudioDeferred: true, PartiallyApplied: true);

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Configurations.SwitchDeferred", vm.WarningMessage);
        Assert.Contains("Configurations.SwitchPartiallyApplied", vm.WarningMessage);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_RejectedTransmitting_SetsErrorMessage_DoesNotRefreshList()
    {
        var (vm, store, service, _, _) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ResultToReturn = ConfigurationPresetSwitchResult.RejectedTransmitting;

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Configurations.SwitchRejectedTransmitting", vm.ErrorMessage);
        Assert.Null(vm.WarningMessage);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_PresetNotFound_RefreshesTheList()
    {
        // The list is stale by definition once this happens -- refresh drops the row rather than
        // leaving the error next to a row that no longer exists. Proven via ListPresetsCallCount's
        // delta (before/after), now that the earlier arm-state probes this test used are gone.
        var (vm, store, service, _, _) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ResultToReturn = ConfigurationPresetSwitchResult.NotFound;
        var callCountBefore = store.ListPresetsCallCount;

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Configurations.SwitchPresetNotFound", vm.ErrorMessage);
        Assert.True(store.ListPresetsCallCount > callCountBefore);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_ServiceThrows_SetsErrorMessage_DoesNotPropagate()
    {
        var (vm, store, service, _, _) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ThrowOnSwitch = new InvalidOperationException("simulated");

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Configurations.SwitchFailed", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_CultureChangedWithCode_CallsSetCultureAsync()
    {
        var (vm, store, service, _, localization) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ResultToReturn = new ConfigurationPresetSwitchResult(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: true, CultureToApply: "en", RxAudioDeferred: false, PartiallyApplied: false);
        var cultureChangedRaised = false;
        localization.CultureChanged += () => cultureChangedRaised = true;

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.True(cultureChangedRaised);
    }

    [AvaloniaFact]
    public async Task SwitchToRowAsync_CultureChangedWithNullCode_IsANoOp_DoesNotThrow()
    {
        // No reset-to-default-culture path exists through ILocalizationService -- treated as a no-op,
        // must not throw or call SetCultureAsync.
        var (vm, store, service, _, localization) = Create();
        store.Seed("Field Day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Field Day");
        service.ResultToReturn = new ConfigurationPresetSwitchResult(ConfigurationPresetSwitchOutcome.Applied, CultureChanged: true, CultureToApply: null, RxAudioDeferred: false, PartiallyApplied: false);
        var cultureChangedRaised = false;
        localization.CultureChanged += () => cultureChangedRaised = true;

        await vm.SwitchToRowCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.False(cultureChangedRaised);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task CloneAsync_ValidName_DoesNotTouchTheActiveMarker()
    {
        // Clone's source is a FILE, not live settings, so the live state may not actually match the
        // clone's content -- it must not set the active marker.
        var initial = new AppSettings().WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "Original" },
            ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        var (vm, store, _, settingsStore, _) = Create(initial);
        store.Seed("Original");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Original");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("Original Clone");

        await vm.CloneCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Original Clone", await store.ListPresetsAsync());
        var marker = settingsStore.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("Original", marker?.ActivePresetName);
    }

    [AvaloniaFact]
    public async Task CloneAsync_CollidingName_SetsErrorMessage_DoesNotClone()
    {
        var (vm, store, _, _, _) = Create();
        store.Seed("Source");
        store.Seed("AlreadyExists");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Source");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("AlreadyExists");

        await vm.CloneCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task CloneAsync_CollidingName_SetsLocalizedMessage_NotRawExceptionText()
    {
        var (vm, store, _, _, _) = Create();
        store.Seed("Source");
        store.Seed("AlreadyExists");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Source");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("AlreadyExists");

        await vm.CloneCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Configurations.CloneFailed", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RenameAsync_RenamingTheActivePreset_UpdatesTheMarkerToo()
    {
        var initial = new AppSettings().WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "Old Name" },
            ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        var (vm, store, _, settingsStore, _) = Create(initial);
        store.Seed("Old Name");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Old Name");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("New Name");

        await vm.RenameCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        var marker = settingsStore.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("New Name", marker?.ActivePresetName);
    }

    [AvaloniaFact]
    public async Task RenameAsync_RenamingANonActivePreset_LeavesTheMarkerAlone()
    {
        var initial = new AppSettings().WithSection(
            ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = "Someone Else" },
            ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        var (vm, store, _, settingsStore, _) = Create(initial);
        store.Seed("Old Name");
        store.Seed("Someone Else");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Old Name");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("New Name");

        await vm.RenameCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        var marker = settingsStore.Settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        Assert.Equal("Someone Else", marker?.ActivePresetName);
    }

    [AvaloniaFact]
    public async Task RenameAsync_CollidingName_SetsErrorMessage_DoesNotRename()
    {
        var (vm, store, _, _, _) = Create();
        store.Seed("Old Name");
        store.Seed("AlreadyExists");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Old Name");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("AlreadyExists");

        await vm.RenameCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Contains("Old Name", await store.ListPresetsAsync());
    }

    [AvaloniaFact]
    public async Task RenameAsync_CollidingName_SetsLocalizedMessage_NotRawExceptionText()
    {
        var (vm, store, _, _, _) = Create();
        store.Seed("Old Name");
        store.Seed("AlreadyExists");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "Old Name");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("AlreadyExists");

        await vm.RenameCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Configurations.RenameFailed", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RenameAsync_CaseOnlyRename_Succeeds()
    {
        // FakeConfigurationPresetStore must allow this, matching the real ConfigurationPresetStore,
        // which deliberately allows a case-only rename.
        var (vm, store, _, _, _) = Create();
        store.Seed("field day");
        await vm.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        var row = vm.Presets.Single(p => p.Name == "field day");
        vm.TextPromptRequested = _ => Task.FromResult<string?>("Field Day");

        await vm.RenameCommand.ExecuteAsync(row);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ErrorMessage);
        Assert.Contains("Field Day", await store.ListPresetsAsync());
    }
}
