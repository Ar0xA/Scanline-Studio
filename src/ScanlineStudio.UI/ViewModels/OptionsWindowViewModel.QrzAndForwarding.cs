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
    /// <summary>One-line warning row: only when a password is set and no system keyring was found.</summary>
    public bool ShowQrzPlaintextWarning => _qrzPasswordLoaded && !IsQrzPasswordStoreSecure && !string.IsNullOrEmpty(QrzLookupPassword);

    public string QrzLookupPasswordWatermark => _localization.GetString(
        IsQrzKeyringLocked ? "Options.Qrz.KeyringLockedWatermark"
        : IsQrzKeyringUnreachable ? "Options.Qrz.KeyringUnavailableWatermark"
        : "Options.Qrz.QrzLookupPasswordWatermark");

    public string QrzLookupPasswordHint => _localization.GetString(IsQrzPasswordStoreSecure ? "Options.Qrz.QrzLookupPasswordHint.Keyring" : "Options.Qrz.QrzLookupPasswordHint");

    [RelayCommand]
    private void AddAdifUdpDestination() => AdifUdpDestinations.Add(new AdifUdpDestinationRowViewModel { RemoveCommand = RemoveAdifUdpDestinationCommand });

    [RelayCommand]
    private void RemoveAdifUdpDestination(AdifUdpDestinationRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        AdifUdpDestinations.Remove(row);
    }

    /// <summary>Loads the password field. <paramref name="allowPrompt"/> is true only from the Unlock
    /// button; the dialog load never prompts. A field the user already edited is left as typed.</summary>
    private async Task LoadQrzPasswordFieldAsync(bool allowPrompt)
    {
        var load = await _optionsSettingsService.LoadQrzPasswordAsync(allowPrompt);
        var fieldUntouched = !_qrzPasswordLoaded || string.Equals(NullIfEmpty(QrzLookupPassword), NullIfEmpty(_loadedQrzPassword), StringComparison.Ordinal);
        _loadedQrzPassword = load.Password;
        _qrzPasswordLoaded = true;
        if (fieldUntouched)
        {
            QrzLookupPassword = load.Password;
        }

        IsQrzPasswordStoreSecure = load.IsSecure;
        // Unlock only helps when a keyring is reachable; a keyring-less session that has the flag set gets the "not available" hint instead.
        IsQrzKeyringLocked = load.IsSecure && load.Status == CredentialReadStatus.Unavailable;
        IsQrzKeyringUnreachable = !load.IsSecure && load.Status == CredentialReadStatus.Unavailable;
        OnPropertyChanged(nameof(ShowQrzPlaintextWarning));
        Log.QrzPasswordLoaded(_logger, load.Status, load.IsSecure);
    }

    /// <summary>Returns false (with <see cref="SaveErrorMessage"/> set) when the keyring write failed.</summary>
    private async Task<bool> SaveQrzPasswordIfChangedAsync()
    {
        var current = NullIfEmpty(QrzLookupPassword);
        if (!_qrzPasswordLoaded || string.Equals(current, NullIfEmpty(_loadedQrzPassword), StringComparison.Ordinal))
        {
            return true;
        }

        var outcome = await _optionsSettingsService.SaveQrzPasswordAsync(current, _loadedQrzPassword);
        if (outcome != QrzPasswordWriteOutcome.Saved)
        {
            // Backend reasons are logged by QrzCredentialService; the UI shows localized text only.
            Log.QrzPasswordSaveFailed(_logger, outcome);
            SaveErrorMessage = _localization.GetString(outcome switch
            {
                QrzPasswordWriteOutcome.KeyringUnavailable => "Options.Qrz.Error.KeyringUnavailable",
                QrzPasswordWriteOutcome.KeyringVerifyFailed => "Options.Qrz.Error.KeyringVerifyFailed",
                _ => "Options.Qrz.Error.KeyringWriteFailed",
            });
            return false;
        }

        _loadedQrzPassword = current;
        IsQrzKeyringLocked = false;
        IsQrzKeyringUnreachable = false;
        return true;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private bool CanUnlockQrzKeyring() => IsQrzKeyringLocked && !IsUnlockingQrzKeyring;

    /// <summary>The one Options action allowed to show a keyring unlock prompt.</summary>
    [RelayCommand(CanExecute = nameof(CanUnlockQrzKeyring))]
    private async Task UnlockQrzKeyringAsync()
    {
        IsUnlockingQrzKeyring = true;
        try
        {
            await LoadQrzPasswordFieldAsync(allowPrompt: true);
        }
        catch (Exception ex)
        {
            Log.QrzKeyringUnlockFailed(_logger, ex);
        }
        finally
        {
            IsUnlockingQrzKeyring = false;
        }
    }

    private bool CanTestQrzLookup() => !IsTestingQrzLookup && !string.IsNullOrWhiteSpace(QrzLookupUsername) && !string.IsNullOrWhiteSpace(QrzLookupPassword);

    [RelayCommand(CanExecute = nameof(CanTestQrzLookup))]
    private async Task TestQrzLookupAsync(CancellationToken ct)
    {
        IsTestingQrzLookup = true;
        TestQrzLookupStatus = _localization.GetString("Options.Qrz.TestResult.Testing");
        try
        {
            var result = await _logbookSession.TestQrzLookupCredentialsAsync(QrzLookupUsername!, QrzLookupPassword!, ct);
            TestQrzLookupStatus = result.Success
                ? _localization.GetString("Options.Qrz.TestResult.Success")
                : _localization.GetString("Options.Qrz.TestResult.Failure", result.ErrorReason ?? string.Empty);
            Log.TestQrzLookupCompleted(_logger, result.Success);
        }
        catch (Exception ex)
        {
            Log.TestQrzLookupFailed(_logger, ex);
            TestQrzLookupStatus = _localization.GetString("Options.Qrz.TestResult.Failure", ex.Message);
        }
        finally
        {
            IsTestingQrzLookup = false;
        }
    }
}
