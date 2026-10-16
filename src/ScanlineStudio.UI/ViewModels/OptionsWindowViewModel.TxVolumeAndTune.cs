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
    public string TxVolumeDisplay => TxVolumePercent.ToString(CultureInfo.InvariantCulture);

    public string DrmTxVolumeDisplay => DrmTxVolumePercent.ToString(CultureInfo.InvariantCulture);

    /// <summary>Loads both Pwr levels once when the dialog opens; it subscribes to no change events.</summary>
    private async Task LoadTxVolumeSafeAsync()
    {
        try
        {
            var sstv = await _sstvSession.GetTxVolumePercentAsync(TxLevelFamily.Sstv).ConfigureAwait(false);
            var drm = await _sstvSession.GetTxVolumePercentAsync(TxLevelFamily.Drm).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _suppressTxVolumePersist = true;
                    TxVolumePercent = sstv;
                    DrmTxVolumePercent = drm;
                }
                finally
                {
                    _suppressTxVolumePersist = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.LoadTxVolumeFailed(_logger, ex);
        }
    }

    partial void OnTxVolumePercentChanged(int value) => SchedulePersistTxVolume(TxLevelFamily.Sstv, value);

    partial void OnDrmTxVolumePercentChanged(int value) => SchedulePersistTxVolume(TxLevelFamily.Drm, value);

    private void SchedulePersistTxVolume(TxLevelFamily family, int value)
    {
        if (_suppressTxVolumePersist)
        {
            return;
        }

        // Debounced, same reasoning as RadioStatusViewModel.OnTxVolumePercentChanged's own comment:
        // a slider drag can fire dozens of times a second, and overlapping un-awaited settings-store
        // writes could race each other and let a stale write clobber a fresher one.
        _ = _txVolumePersist.Schedule(family, ct => PersistTxVolumeAsync(family, value, ct));
    }

    private async Task PersistTxVolumeAsync(TxLevelFamily family, int value, CancellationToken ct)
    {
        try
        {
            await _sstvSession.SetTxVolumePercentAsync(family, value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistTxVolumeFailed(_logger, family, value, ex);
        }
    }

    /// <summary>Resolved (not a raw key) -- same "expose the already-localized display string"
    /// convention as <see cref="TxVolumeDisplay"/>/<c>RadioStatusViewModel.TxVolumeDisplay</c>, since
    /// <c>loc:Translate</c> takes a static key, not a bound one.</summary>
    public string TuneButtonLabel => _localization.GetString(IsTuning ? "Options.Radio.Tune.Stop" : "Options.Radio.Tune");

    /// <summary>Keys PTT and transmits a steady <see cref="TuneFrequencyHz"/> tone, same WSJT-X-style
    /// AFC-lock-aid shape as <see cref="RadioStatusViewModel.TuneCommand"/> -- but unlike that one,
    /// this is a real start/stop TOGGLE (matching WSJT-X's own Tune button) so the operator can key
    /// once, drag <see cref="TxVolumePercent"/> while watching the radio's own power meter for as
    /// long as needed, then stop manually -- capped at <see cref="MaxTuneDuration"/> either way as a
    /// safety backstop against a forgotten/stuck Tune keying the rig indefinitely.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TuneAsync()
    {
        if (IsTuning)
        {
            _tuneCts?.Cancel();
            return;
        }

        Log.TuneInvoked(_logger, TuneFrequencyHz, MaxTuneDuration.TotalSeconds);
        TuneErrorMessage = null;
        IsTuning = true;
        var cts = new CancellationTokenSource();
        _tuneCts = cts;
        try
        {
            // Options' Tune stays SSTV; the DRM level is tuned from the header or TX pane with DRM selected.
            await _sstvSession.TuneAsync(TxLevelFamily.Sstv, TuneFrequencyHz, MaxTuneDuration, ct: cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal control flow -- the operator clicked Stop (see the IsTuning branch above), or
            // this dialog closed mid-tone (CancelCommand/the window's own Closing handler cancels
            // _tuneCts the same way, see that command's own comment).
        }
        catch (Exception ex)
        {
            Log.TuneFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => TuneErrorMessage = _localization.GetString("RadioStatus.Error.TuneFailed"));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _tuneCts = null;
                cts.Dispose();
                IsTuning = false;
            });
        }
    }

    /// <summary>Called from the window's own Closing/Cancel path so a Tune tone left running (PTT
    /// still keyed) can't outlive the dialog that started it -- see <see cref="TuneAsync"/>'s own
    /// OperationCanceledException handling, which this feeds.</summary>
    public void StopTuneIfActive() => _tuneCts?.Cancel();
}
