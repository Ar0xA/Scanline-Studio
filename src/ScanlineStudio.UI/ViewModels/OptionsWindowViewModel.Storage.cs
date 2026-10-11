using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Settings;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

public sealed partial class OptionsWindowViewModel
{
    /// <summary>Pre-fills all 4 Storage rows with their REAL effective values (current directory,
    /// plus Database's own already-staged pending target) -- never left blank just
    /// because the underlying setting happens to be unset, same reasoning as
    /// <see cref="IReceiveHistoryStore.SetImagesDirectoryAsync"/>'s own doc comment (this row's
    /// original source, from the former standalone "Configurations &gt; Storage" dialog). A
    /// failure here must not take down the rest of the dialog's own load -- own try/catch,
    /// separate from <see cref="LoadSafeAsync"/>.</summary>
    private async Task LoadStorageLocationsSafeAsync()
    {
        try
        {
            ImagesDirectory = await _historyStore.GetImagesDirectoryAsync();
            var audioSettings = await _historyStore.GetAudioSettingsAsync();
            AudioSaveEnabled = audioSettings.Enabled;
            AudioDirectory = audioSettings.Directory;
            ConfigDirectory = await _appLocationsService.GetConfigDirectoryAsync();
            ConfigDirectoryInput = ConfigDirectory;
            DatabaseDirectory = await _appLocationsService.GetDatabaseDirectoryAsync();
            var pendingDatabaseDirectory = await _appLocationsService.GetPendingDatabaseDirectoryAsync();
            _hasPendingDatabaseRelocation = pendingDatabaseDirectory is not null;
            PendingDatabaseDirectory = pendingDatabaseDirectory ?? DatabaseDirectory;
            LogDirectory = await _appLocationsService.GetLogDirectoryAsync();

            // Whatever's pending as of THIS load is the baseline "already staged" state -- a general
            // Save that fires before the user ever touches this row must not treat it as a fresh
            // target to re-stage. See _lastStagedDatabaseDirectory's own doc comment.
            _lastStagedDatabaseDirectory = PendingDatabaseDirectory;
            _storageLocationsLoaded = true;
        }
        catch (Exception ex)
        {
            Log.StorageLocationsLoadFailed(_logger, ex);
        }
    }

    [RelayCommand]
    private async Task BrowseImagesDirectoryAsync()
    {
        var picked = await _filePickerService.PickFolderAsync(ImagesDirectory);
        if (picked is not null)
        {
            ImagesDirectory = picked;
        }
    }

    [RelayCommand]
    private async Task ApplyImagesDirectoryAsync()
    {
        try
        {
            ImagesDirectoryErrorMessage = null;
            await _historyStore.SetImagesDirectoryAsync(ImagesDirectory);
        }
        catch (Exception ex)
        {
            Log.ImagesDirectorySaveFailed(_logger, ex);
            ImagesDirectoryErrorMessage = _localization.GetString("Options.General.Storage.Error.SaveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task BrowseAudioDirectoryAsync()
    {
        var picked = await _filePickerService.PickFolderAsync(AudioDirectory);
        if (picked is not null)
        {
            AudioDirectory = picked;
        }
    }

    [RelayCommand]
    private async Task ApplyAudioDirectoryAsync()
    {
        try
        {
            AudioDirectoryErrorMessage = null;
            await _historyStore.SetAudioSettingsAsync(AudioSaveEnabled, AudioDirectory);

            // Auditor-caught (round 1 code-review): SetAudioSettingsAsync above resolves the typed
            // text to a full, normalized path (SqliteReceiveHistoryStore.SetAudioSettingsAsync's own
            // Path.GetFullPath call) before persisting it -- passing the RAW typed text (a relative
            // path, "~/x", etc.) to the live-apply call below would let the decode path's scratch
            // root diverge from the actually-persisted directory RxAudioAutoSaver reads back later.
            // Re-reading here, rather than duplicating the resolution logic, guarantees the two can
            // never disagree. Also re-displayed, so the row shows what's actually in effect, not what
            // was typed.
            var resolved = await _historyStore.GetAudioSettingsAsync();
            AudioDirectory = resolved.Directory;

            // Live-apply, same call the Host's own startup DI factory makes from the persisted
            // setting -- see ISstvSessionService.SetAutoSaveAudioEnabled/SetAudioDirectory's own doc
            // comments for why an Apply/Save flow must call both this and the store write above.
            _sstvSession.SetAutoSaveAudioEnabled(AudioSaveEnabled);
            _sstvSession.SetAudioDirectory(resolved.Directory);
        }
        catch (Exception ex)
        {
            Log.AudioDirectorySaveFailed(_logger, ex);
            AudioDirectoryErrorMessage = _localization.GetString("Options.General.Storage.Error.SaveFailed", ex.Message);
        }
    }

    [RelayCommand]
    private async Task BrowseConfigDirectoryAsync()
    {
        var picked = await _filePickerService.PickFolderAsync(ConfigDirectoryInput ?? ConfigDirectory);
        if (picked is not null)
        {
            ConfigDirectoryInput = picked;
        }
    }

    /// <summary>Restart-required-settings backlog item 3 (2026-08-27): applies LIVE now, same
    /// immediate-apply-then-refresh shape as <see cref="ApplyLogDirectoryAsync"/> below -- no more
    /// staging, no restart confirmation. The blank-means-error guard is UNCHANGED from before this
    /// feature (round-1 finding on the ORIGINAL staged version, kept deliberately -- see
    /// <see cref="ConfigDirectoryInput"/>'s own doc comment for why it still needs a guard despite
    /// no longer being a "pending" value).</summary>
    [RelayCommand]
    private async Task ApplyConfigDirectoryAsync()
    {
        if (string.IsNullOrWhiteSpace(ConfigDirectoryInput))
        {
            ConfigDirectoryErrorMessage = _localization.GetString("Options.General.Storage.Error.NoFolderChosen");
            return;
        }

        try
        {
            ConfigDirectoryErrorMessage = null;
            await _appLocationsService.SetConfigDirectoryAsync(ConfigDirectoryInput);
            ConfigDirectory = await _appLocationsService.GetConfigDirectoryAsync();
            ConfigDirectoryInput = ConfigDirectory;
        }
        catch (Exception ex)
        {
            Log.ConfigDirectorySaveFailed(_logger, ex);
            ConfigDirectoryErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task BrowseDatabaseDirectoryAsync()
    {
        var picked = await _filePickerService.PickFolderAsync(PendingDatabaseDirectory ?? DatabaseDirectory);
        if (picked is not null)
        {
            PendingDatabaseDirectory = picked;
        }
    }

    [RelayCommand]
    private async Task ApplyDatabaseDirectoryAsync()
    {
        // Same round-1 finding as ApplyConfigDirectoryAsync above.
        if (string.IsNullOrWhiteSpace(PendingDatabaseDirectory))
        {
            DatabaseDirectoryErrorMessage = _localization.GetString("Options.General.Storage.Error.NoFolderChosen");
            return;
        }

        try
        {
            DatabaseDirectoryErrorMessage = null;
            await _appLocationsService.SetDatabaseDirectoryAsync(PendingDatabaseDirectory);
            var pending = await _appLocationsService.GetPendingDatabaseDirectoryAsync();
            _hasPendingDatabaseRelocation = pending is not null;
            IsConfirmingDatabaseRestart = pending is not null;
            // Falls back to DatabaseDirectory (e.g. the picked folder matched the current one, so
            // nothing was actually staged) so the row keeps showing the real current path instead of
            // going blank -- see PendingDatabaseDirectory's own doc comment.
            PendingDatabaseDirectory = pending ?? DatabaseDirectory;
            // This stage just succeeded -- refresh the baseline so a general Save that follows
            // (including RestartNowAsync's own SaveCoreAsync call right before restarting) doesn't
            // treat this same, already-just-staged value as a fresh target to re-apply.
            _lastStagedDatabaseDirectory = PendingDatabaseDirectory;
        }
        catch (Exception ex)
        {
            Log.DatabaseDirectorySaveFailed(_logger, ex);
            DatabaseDirectoryErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private Task ConfirmDatabaseRestartAsync() => RestartNowAsync(
        dismissConfirm: () => IsConfirmingDatabaseRestart = false,
        setError: message => DatabaseDirectoryErrorMessage = message);

    [RelayCommand]
    private void CancelDatabaseRestart() => IsConfirmingDatabaseRestart = false;

    /// <summary>Backs Database's own Restart Now action (Config used to share this same helper
    /// before restart-required-settings backlog item 3, 2026-08-27, made it apply live instead) --
    /// see
    /// <see cref="RestartRequested"/>'s own doc comment for the full ordering rationale. Saves any
    /// dirty edit elsewhere in the dialog FIRST (only if a load actually succeeded, mirroring
    /// <see cref="CanSave"/>'s own guard -- a dialog that failed to load has no real snapshot to
    /// save) and aborts the restart entirely on failure, rather than silently discarding whatever
    /// the user typed. Never calls <see cref="IApplicationRestarter.StartNewInstance"/> directly --
    /// only sets the flag; the actual spawn happens from <c>Program.cs</c>, after this process's
    /// own teardown completes.</summary>
    private async Task RestartNowAsync(Action dismissConfirm, Action<string?> setError)
    {
        if (_loadSucceeded && !await SaveCoreAsync())
        {
            setError(_localization.GetString("Options.General.Storage.Error.SaveBeforeRestartFailed"));
            return;
        }

        dismissConfirm();
        _applicationRestarter.RestartRequested = true;
        RestartRequested?.Invoke();
    }

    [RelayCommand]
    private async Task BrowseLogDirectoryAsync()
    {
        var picked = await _filePickerService.PickFolderAsync(LogDirectory);
        if (picked is not null)
        {
            LogDirectory = picked;
        }
    }

    [RelayCommand]
    private async Task ApplyLogDirectoryAsync()
    {
        try
        {
            LogDirectoryErrorMessage = null;
            await _appLocationsService.SetLogDirectoryAsync(LogDirectory);
            LogDirectory = await _appLocationsService.GetLogDirectoryAsync();
        }
        catch (Exception ex)
        {
            Log.LogDirectorySaveFailed(_logger, ex);
            LogDirectoryErrorMessage = ex.Message;
        }
    }
}
