using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Backs the Configurations menu's per-configuration cascading submenus — Configurations-
/// preset backlog, Phase 4b (2026-08-28, cascading-menu redesign, superseding the earlier modal
/// "Manage Configurations" dialog; see `docs/plans/configuration-presets-phase4b-cascading-menu-plan.md`).
/// Resolved fresh from DI every time the "Configurations" menu opens (`MainWindow.axaml.cs`'s own
/// `SubmenuOpened` handler) — never shown as its own window, only its commands are invoked from menu
/// items built imperatively in that code-behind.</summary>
public sealed partial class ConfigurationsManagerWindowViewModel : ObservableObject
{
    /// <summary>User-requested: a reserved configuration name, always present, always listed LAST,
    /// immune to Delete -- "the one we start with." Auto-created from whatever the live settings are
    /// the first time <see cref="RefreshAsync"/> ever finds it missing (a fresh install, a presets
    /// folder emptied by hand, or Default itself renamed away), same save mechanism every other save
    /// path in this feature uses. Case-insensitive, matching every other name comparison here.</summary>
    public const string DefaultPresetName = "Default";

    private readonly IConfigurationPresetStore _presetStore;
    private readonly IConfigurationPresetService _configurationPresetService;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly ILogger<ConfigurationsManagerWindowViewModel> _logger;

    public ConfigurationsManagerWindowViewModel(
        IConfigurationPresetStore presetStore, IConfigurationPresetService configurationPresetService,
        ISettingsStore settingsStore, ILocalizationService localization,
        ILogger<ConfigurationsManagerWindowViewModel> logger)
    {
        _presetStore = presetStore;
        _configurationPresetService = configurationPresetService;
        _settingsStore = settingsStore;
        _localization = localization;
        _logger = logger;
    }

    public ObservableCollection<ConfigurationPresetRowViewModel> Presets { get; } = [];

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Amber/attention-styled, distinct from <see cref="ErrorMessage"/> -- carries a
    /// deferred-or-partially-applied switch result, which is still a SUCCESS, not a rejection.</summary>
    [ObservableProperty]
    private string? _warningMessage;

    /// <summary>Deliberately a settable delegate PROPERTY, not an <c>event Action&lt;T&gt;</c> -- a
    /// genuine request/response (the command awaits the typed answer before continuing). Set exactly
    /// once, by <c>MainWindow.axaml.cs</c>, to a function that constructs a <c>TextPromptWindowView</c>
    /// for the given view-model and awaits <c>ShowDialog&lt;string?&gt;</c>.</summary>
    public Func<TextPromptWindowViewModel, Task<string?>>? TextPromptRequested { get; set; }

    /// <summary>Same shape as <see cref="TextPromptRequested"/>, for the Yes/No "are you sure" step
    /// Delete and Reset both need now that there is no dialog row left to arm/confirm in place. Returns
    /// <see langword="false"/> (decline) when unwired -- the safe default for a destructive action,
    /// same reasoning as <see cref="RequestTextPromptAsync"/>'s null-returns-cancel shape.</summary>
    public Func<ConfirmActionDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    private async Task<string?> RequestTextPromptAsync(TextPromptWindowViewModel promptVm) =>
        TextPromptRequested is null ? null : await TextPromptRequested(promptVm).ConfigureAwait(true);

    private async Task<bool> RequestConfirmAsync(string title, string message)
    {
        if (ConfirmRequested is null)
        {
            return false;
        }

        var confirmVm = new ConfirmActionDialogViewModel(title, message);
        return await ConfirmRequested(confirmVm).ConfigureAwait(true);
    }

    /// <summary>User-requested (2026-08-28): WSJT-X-style quick switch straight from the
    /// "Configurations" menu's per-row submenu ("Switch To", shown only for inactive, non-Default
    /// rows). Also the shared underlying action <see cref="ResetToDefaultAsync"/> confirms before
    /// invoking -- Default's own submenu calls this same logic under the "Reset" label instead.</summary>
    [RelayCommand]
    public async Task SwitchToRowAsync(ConfigurationPresetRowViewModel? selected)
    {
        if (selected is null)
        {
            return;
        }

        ErrorMessage = null;
        WarningMessage = null;

        // Manual-verification finding: IConfigurationPresetService.SwitchToPresetAsync uses
        // ConfigureAwait(false) internally throughout, so every observable-state write below runs on
        // whatever thread its continuation lands on, not necessarily the UI thread -- see
        // RefreshAsync's own doc comment for the reproduced symptom. Every ErrorMessage/WarningMessage
        // write below is wrapped in Dispatcher.UIThread.InvokeAsync.
        ConfigurationPresetSwitchResult result;
        try
        {
            result = await _configurationPresetService.SwitchToPresetAsync(selected.Name);
        }
        catch (Exception ex)
        {
            Log.SwitchThrew(_logger, selected.Name, ex);
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.SwitchFailed"));
            return;
        }

        switch (result.Outcome)
        {
            case ConfigurationPresetSwitchOutcome.Applied:
                await ApplyCultureIfChangedAsync(selected.Name, result);
                var warnings = new List<string>();
                if (result.RxAudioDeferred)
                {
                    warnings.Add(_localization.GetString("Configurations.SwitchDeferred"));
                }

                if (result.PartiallyApplied)
                {
                    warnings.Add(_localization.GetString("Configurations.SwitchPartiallyApplied"));
                }

                var warningMessage = warnings.Count > 0 ? string.Join(" ", warnings) : null;
                await Dispatcher.UIThread.InvokeAsync(() => WarningMessage = warningMessage);
                await RefreshAsync();
                break;

            case ConfigurationPresetSwitchOutcome.PresetNotFound:
                // The list is stale by definition once this happens (a concurrent external delete of
                // the preset file) -- refresh drops the row rather than leaving the message next to a
                // row that no longer exists.
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.SwitchPresetNotFound", selected.Name));
                await RefreshAsync();
                break;

            case ConfigurationPresetSwitchOutcome.RejectedTransmitting:
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.SwitchRejectedTransmitting"));
                break;

            case ConfigurationPresetSwitchOutcome.RejectedRecording:
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.SwitchRejectedRecording"));
                break;

            case ConfigurationPresetSwitchOutcome.RejectedConcurrentSwitch:
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.SwitchRejectedConcurrent"));
                break;
        }
    }

    /// <summary>Default's own submenu entry -- functionally identical to <see cref="SwitchToRowAsync"/>
    /// but always confirmed first (user-requested, verbatim: "this resets to the default configuration
    /// are you sure"). A declined confirm is a no-op; nothing is touched.</summary>
    [RelayCommand]
    public async Task ResetToDefaultAsync(ConfigurationPresetRowViewModel? defaultRow)
    {
        if (defaultRow is null)
        {
            return;
        }

        var confirmed = await RequestConfirmAsync(
            _localization.GetString("Configurations.ConfirmResetTitle"),
            _localization.GetString("Configurations.ConfirmResetMessage"));
        if (!confirmed)
        {
            return;
        }

        await SwitchToRowAsync(defaultRow);
    }

    /// <summary>Plan point 5 -- applies culture IMMEDIATELY, not deferred to a dialog Close, mirroring
    /// <c>OptionsWindowViewModel.SaveCoreAsync</c>'s own established shape.</summary>
    private async Task ApplyCultureIfChangedAsync(string presetName, ConfigurationPresetSwitchResult result)
    {
        if (!result.CultureChanged)
        {
            return;
        }

        if (result.CultureToApply is not { } code)
        {
            // No "reset to default culture" path is reachable through ILocalizationService --
            // treated as a no-op, logged so it stays diagnosable.
            Log.CultureResetToDefaultNoOp(_logger, presetName);
            return;
        }

        try
        {
            await _localization.SetCultureAsync(CultureInfo.GetCultureInfo(code));
        }
        catch (Exception ex)
        {
            Log.SetCultureFailed(_logger, code, ex);
        }
    }

    [RelayCommand]
    private async Task CloneAsync(ConfigurationPresetRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var promptVm = new TextPromptWindowViewModel(
            _presetStore,
            _localization,
            _localization.GetString("Configurations.ClonePrompt.Title"),
            _localization.GetString("Configurations.ClonePrompt.Message"),
            prefillText: row.Name + " " + _localization.GetString("Configurations.CloneSuffix"));
        var name = await RequestTextPromptAsync(promptVm);
        if (name is null)
        {
            return;
        }

        try
        {
            // Clone's source is a FILE, not live settings -- deliberately does NOT touch the active
            // marker, unlike a Save-As-New style capture would.
            await _presetStore.ClonePresetAsync(row.Name, name);
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = null);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.CloneFailed(_logger, row.Name, name, ex);
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.CloneFailed"));
        }
    }

    /// <summary>User-requested (2026-08-28): guards on <see cref="ConfigurationPresetRowViewModel.IsProtected"/>
    /// too, not just a null row -- same "guard both the menu shape and the command" posture already
    /// used for <see cref="DeleteAsync"/>. Renaming Default doesn't customize it: protection is keyed
    /// on the literal string "Default", so the renamed content becomes an ordinary, deletable preset
    /// while <see cref="RefreshAsync"/>'s own seed-if-missing check spawns a brand new "Default" from
    /// current live settings the next time the menu opens -- a real trap, not a feature. The menu
    /// itself already omits "Rename..." from Default's own submenu; this is defense in depth.</summary>
    [RelayCommand]
    private async Task RenameAsync(ConfigurationPresetRowViewModel? row)
    {
        if (row is null || row.IsProtected)
        {
            return;
        }

        var oldName = row.Name;
        var promptVm = new TextPromptWindowViewModel(
            _presetStore,
            _localization,
            _localization.GetString("Configurations.RenamePrompt.Title"),
            _localization.GetString("Configurations.RenamePrompt.Message"),
            prefillText: oldName);
        var newName = await RequestTextPromptAsync(promptVm);
        if (newName is null)
        {
            return;
        }

        try
        {
            await _presetStore.RenamePresetAsync(oldName, newName);
        }
        catch (Exception ex)
        {
            Log.RenameFailed(_logger, oldName, newName, ex);
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.RenameFailed"));
            return;
        }

        // RenamePresetAsync deliberately does NOT touch the active-preset marker -- the caller
        // renaming the currently-active preset is responsible for it.
        try
        {
            await UpdateActiveMarkerAsync(newName, activeName =>
                activeName is not null && string.Equals(activeName, oldName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.MarkerUpdateFailed(_logger, ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = null);
        await RefreshAsync();
    }

    /// <summary>Phase 4b: guards on <see cref="ConfigurationPresetRowViewModel.CanDelete"/> (neither
    /// Default nor the currently-active configuration) and confirms via <see cref="ConfirmRequested"/>
    /// before deleting -- no more in-row two-click arm, since there is no row to arm in a menu.</summary>
    [RelayCommand]
    private async Task DeleteAsync(ConfigurationPresetRowViewModel? row)
    {
        // Defense in depth -- the menu-building code-behind already omits "Delete..." for a row this
        // is false for, same "guard both the menu shape and the command" posture this codebase already
        // uses for other destructive actions.
        if (row is null || !row.CanDelete)
        {
            return;
        }

        var confirmed = await RequestConfirmAsync(
            _localization.GetString("Configurations.ConfirmDeleteTitle"),
            _localization.GetString("Configurations.ConfirmDeleteMessage", row.Name));
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _presetStore.DeletePresetAsync(row.Name);
        }
        catch (Exception ex)
        {
            Log.DeleteFailed(_logger, row.Name, ex);
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.DeleteFailed"));
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = null);
        await RefreshAsync();
    }

    /// <summary>User-requested: <see cref="DefaultPresetName"/> always exists and always sorts LAST
    /// (2026-08-28 correction -- originally sorted first) --
    /// auto-created from current live settings the first time it's found missing (a fresh install, a
    /// presets folder emptied by hand, or Default itself renamed away).</summary>
    public async Task RefreshAsync()
    {
        // Manual-verification finding: every dependency this method awaits (IConfigurationPresetStore/
        // ISettingsStore) uses ConfigureAwait(false) internally, so by the time any of THESE awaits
        // resume, this method is very likely running on a thread-pool thread, not the UI thread.
        // Mutating Presets (an ObservableCollection the Configurations menu reads to build items) from
        // off the UI thread doesn't throw -- it can silently corrupt a bound render (reproduced live
        // against the earlier dialog build). Marshal the mutation itself onto the UI thread via
        // Dispatcher.UIThread, not just hope the continuation lands there.
        try
        {
            var names = await _presetStore.ListPresetsAsync();
            if (!names.Any(n => string.Equals(n, DefaultPresetName, StringComparison.OrdinalIgnoreCase)))
            {
                await SeedDefaultPresetAsync();
                names = await _presetStore.ListPresetsAsync();
            }

            var activeName = await GetActivePresetNameAsync();
            var ordered = names
                .OrderBy(n => string.Equals(n, DefaultPresetName, StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

            var rows = ordered
                .Select(name =>
                {
                    var isProtected = string.Equals(name, DefaultPresetName, StringComparison.OrdinalIgnoreCase);
                    return new ConfigurationPresetRowViewModel(name, string.Equals(name, activeName, StringComparison.OrdinalIgnoreCase), isProtected);
                })
                .ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Presets.Clear();
                foreach (var row in rows)
                {
                    Presets.Add(row);
                }
            });
        }
        catch (Exception ex)
        {
            Log.RefreshFailed(_logger, ex);
            await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _localization.GetString("Configurations.RefreshFailed"));
        }
    }

    /// <summary>Marks it active ONLY if nothing else already is -- "it's the one we start with" on a
    /// genuinely fresh install (no marker set at all), but seeding must NOT silently steal the marker
    /// away from a real preset that's already active (e.g. a presets folder emptied by hand while a
    /// real preset was still active in settings.json).</summary>
    private async Task SeedDefaultPresetAsync()
    {
        var live = await _settingsStore.LoadAsync();
        await _presetStore.SavePresetAsync(DefaultPresetName, live);

        try
        {
            await UpdateActiveMarkerAsync(DefaultPresetName, activeName => activeName is null);
        }
        catch (Exception ex)
        {
            Log.MarkerUpdateFailed(_logger, ex);
        }

        Log.DefaultPresetSeeded(_logger);
    }

    private async Task<string?> GetActivePresetNameAsync()
    {
        var settings = await _settingsStore.LoadAsync();
        return settings.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings)?.ActivePresetName;
    }

    /// <summary>Same targeted read-modify-write marker shape <c>ConfigurationPresetService</c>'s own
    /// <c>MergeIntoLiveSettingsAsync</c> uses. T0-2: <paramref name="shouldUpdate"/> is checked
    /// INSIDE the mutate lambda, against the lambda's own snapshot -- not read-then-decided outside
    /// -- so this method's two callers' opposite preconditions (seed: marker is null; rename: marker
    /// equals the just-renamed old name) are each evaluated atomically with the write, not against a
    /// stale outer read that could have gone stale between the check and the write.</summary>
    private async Task UpdateActiveMarkerAsync(string newName, Func<string?, bool> shouldUpdate)
    {
        await _settingsStore.UpdateAsync(current =>
        {
            var activeName = current.GetSection(ConfigurationPresetSettings.SectionKey, ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings)?.ActivePresetName;
            if (!shouldUpdate(activeName))
            {
                return current;
            }

            return current.WithSection(
                ConfigurationPresetSettings.SectionKey, new ConfigurationPresetSettings { ActivePresetName = newName },
                ConfigurationPresetSettingsJsonContext.Default.ConfigurationPresetSettings);
        });
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Refreshing the Configurations list failed")]
        public static partial void RefreshFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Seeded the 'Default' configuration from current live settings")]
        public static partial void DefaultPresetSeeded(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting configuration '{Name}' failed")]
        public static partial void DeleteFailed(ILogger logger, string name, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Switching to configuration '{Name}' threw")]
        public static partial void SwitchThrew(ILogger logger, string name, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Applying culture '{CultureCode}' after a configuration switch failed")]
        public static partial void SetCultureFailed(ILogger logger, string cultureCode, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Configuration '{Name}' explicitly resets culture to default -- no-op, no reset path exists through ILocalizationService")]
        public static partial void CultureResetToDefaultNoOp(ILogger logger, string name);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Updating the active-configuration marker failed")]
        public static partial void MarkerUpdateFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Cloning configuration '{SourceName}' to '{TargetName}' failed")]
        public static partial void CloneFailed(ILogger logger, string sourceName, string targetName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Renaming configuration '{OldName}' to '{NewName}' failed")]
        public static partial void RenameFailed(ILogger logger, string oldName, string newName, Exception ex);
    }
}
