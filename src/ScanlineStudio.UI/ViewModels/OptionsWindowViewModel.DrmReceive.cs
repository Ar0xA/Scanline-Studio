using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Options › Decode: HamDRM receive on/off. Applied through <see cref="ISstvSessionService.SetDrmReceiveEnabledAsync"/>
/// (it persists and applies live) only when the value differs from the last applied one.</summary>
public sealed partial class OptionsWindowViewModel
{
    [ObservableProperty]
    private bool _drmReceiveEnabled = DrmReceiveDefaults.Enabled;

    // The value the session last confirmed; refreshed on a successful apply only, so a failed apply is retried.
    private bool _drmReceiveEnabledApplied = DrmReceiveDefaults.Enabled;

    // The last save wrote everything else; only the HamDRM apply failed, so the post-save warnings still apply.
    private bool _onlyDrmReceiveApplyFailed;

    private async Task LoadDrmReceiveAsync(List<string> unreadable)
    {
        bool saved;
        try
        {
            saved = await _sstvSession.GetDrmReceiveEnabledAsync();
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Stays at the default; an apply only writes on a change and then fails without overwriting.
            Log.SectionUnreadable(_logger, DrmRxSectionKey, ex);
            unreadable.Add(DrmRxSectionKey);
            saved = DrmReceiveDefaults.Enabled;
        }

        _drmReceiveEnabledApplied = saved;
        DrmReceiveEnabled = saved;
    }

    /// <summary>False when the change could not be applied; the caller finishes its other writes, then reports it.</summary>
    private async Task<bool> ApplyDrmReceiveIfChangedAsync()
    {
        bool value = DrmReceiveEnabled;
        if (value == _drmReceiveEnabledApplied)
            return true;
        try
        {
            await _sstvSession.SetDrmReceiveEnabledAsync(value);
            _drmReceiveEnabledApplied = value;
            DrmReceiveLog.Applied(_logger, value);
            return true;
        }
        catch (Exception ex)
        {
            DrmReceiveLog.ApplyFailed(_logger, value, ex);
            return false;
        }
    }

    private static partial class DrmReceiveLog
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "HamDRM receive set to {Enabled} from Options")]
        public static partial void Applied(ILogger logger, bool enabled);

        [LoggerMessage(Level = LogLevel.Error, Message = "Setting HamDRM receive to {Enabled} from Options failed")]
        public static partial void ApplyFailed(ILogger logger, bool enabled, Exception ex);
    }
}
