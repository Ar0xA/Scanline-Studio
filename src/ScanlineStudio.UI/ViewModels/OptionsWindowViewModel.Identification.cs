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
    /// <summary>ui_transition_plan.md step 8 (T2-2): a leftover validation message from a
    /// PREVIOUSLY selected sound-file path must not still show once the operator switches to
    /// Off/Cw -- and switching back TO SoundFile with a path already typed should show its status
    /// immediately, not wait for the next edit. <see cref="OnSoundFileMmvPathChanged"/> already does
    /// exactly this same clear-or-validate branch for a path edit; reused here for a method-switch
    /// so there's exactly one place that decides "does the currently-configured sound-file path need
    /// re-checking".</summary>
    partial void OnCwIdModeChanged(CwIdMode value)
    {
        if (value == CwIdMode.SoundFile)
        {
            OnSoundFileMmvPathChanged(SoundFileMmvPath);
        }
        else
        {
            _soundFileIdValidation.Cancel();
            SoundFileIdSuccessMessage = null;
            SoundFileIdErrorMessage = null;
        }
    }

    partial void OnSoundFileMmvPathChanged(string? value)
    {
        _soundFileIdValidation.Cancel();
        // Code-review finding: a path edit that lands while the OFF/Cw method is selected (e.g.
        // ApplyFromSnapshot sets CwIdMode before SoundFileMmvPath on load, so OnCwIdModeChanged's own
        // clear-on-switch-away branch never fires for that ordering) must not validate at all --
        // the fields aren't even visible in that state.
        if (!IsIdMethodSoundFileSelected || string.IsNullOrWhiteSpace(value))
        {
            SoundFileIdSuccessMessage = null;
            SoundFileIdErrorMessage = null;
            return;
        }

        _ = _soundFileIdValidation.Schedule(ct => ValidateSoundFileIdAsync(value, ct));
    }

    private async Task ValidateSoundFileIdAsync(string path, CancellationToken ct)
    {
        SoundFileIdValidationResult result;
        try
        {
            // ISstvSessionService.ValidateStationIdSoundFileAsync's own contract is "never throws" --
            // but Task.Run(f, ct) itself throws OperationCanceledException if ct fires before the
            // delegate starts, on this fire-and-forget task, an unobserved exception. Caught below.
            result = await _sstvSession.ValidateStationIdSoundFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal control flow -- a newer edit/pick/mode-switch superseded this one. Not worth a log line.
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // Code-review finding: a SLOWER validation for an OLDER path can otherwise resolve after
            // a faster one for a NEWER path already posted its result (last-writer-wins by
            // completion order, not by which edit is actually current) -- re-check cancellation here
            // too, since a mode-switch-away can cancel `ct` after this continuation already resumed
            // but before the Post callback actually runs.
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (result.IsValid)
            {
                SoundFileIdSuccessMessage = _localization.GetString("Options.Radio.SoundFileId.Valid", result.DurationSeconds ?? 0);
                SoundFileIdErrorMessage = null;
            }
            else
            {
                SoundFileIdSuccessMessage = null;
                SoundFileIdErrorMessage = ResolveSoundFileIdFailureMessage(result.Failure);
            }
        });
    }

    private string ResolveSoundFileIdFailureMessage(SoundFileIdValidationFailure failure) => _localization.GetString(failure switch
    {
        SoundFileIdValidationFailure.FileNotFound => "Options.Radio.SoundFileId.Error.NotFound",
        SoundFileIdValidationFailure.FileTooLarge => "Options.Radio.SoundFileId.Error.TooLarge",
        SoundFileIdValidationFailure.UnplayableHeader => "Options.Radio.SoundFileId.Error.UnplayableHeader",
        _ => "Options.Radio.SoundFileId.Error.ReadError",
    });

    /// <summary>"Browse..." next to the sound-file ID path field -- same shape as
    /// <see cref="BrowseHamlibLibraryAsync"/> (marshal the result back to the UI thread via
    /// <c>Dispatcher.UIThread.Post</c>, not a bare property write after the off-thread picker
    /// `await` -- that exact omission crashed this dialog once, see that method's own doc
    /// comment).</summary>
    [RelayCommand]
    private async Task BrowseSoundFileAsync()
    {
        var path = await _filePickerService.PickMmvFileAsync().ConfigureAwait(false);
        if (path is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => SoundFileMmvPath = path);
    }
}
