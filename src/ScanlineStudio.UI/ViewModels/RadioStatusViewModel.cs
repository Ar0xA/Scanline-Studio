using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The fixed radio/frequency status strip — see the Phase-3 plan's pane-count scope trim:
/// this is `MainWindow` chrome, not a 4th dockable pane (`spec/14-roadmap.md`'s Phase 3 bullet and
/// `spec/09-ui.md`'s inventory table both only call for 3 dockable panes). Piece 5
/// (spec/14-roadmap.md's re-scoped Phase 4) makes it interactive: an editable frequency/mode
/// control, frequency memory presets, a WSJT-X-style TX volume slider, and a Tune button — on top
/// of the original read-only frequency/mode readout.</summary>
public sealed partial class RadioStatusViewModel : ViewModelBase
{
    private static readonly TimeSpan VolumePersistDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Tick interval for <see cref="UtcClockDisplay"/> -- a plain wall-clock readout with
    /// no backend dependency at all (spec/17-rx-telemetry-feasibility.md), 1 second is the readout's
    /// own display resolution (`HH:mm:ss`), no finer granularity would be visible.</summary>
    private static readonly TimeSpan UtcClockTickInterval = TimeSpan.FromSeconds(1);

    private readonly IRadioSessionService _radioSession;
    private readonly ISstvSessionService _sstvSession;
    private readonly ILocalizationService _localization;
    private readonly ILogger<RadioStatusViewModel> _logger;
    private readonly DispatcherTimer _utcClockTimer;
    private bool _suppressVolumePersist;
    private CancellationTokenSource? _volumePersistCts;

    [ObservableProperty]
    private string _frequencyDisplay;

    [ObservableProperty]
    private string _modeDisplay;

    [ObservableProperty]
    private string _frequencyInputMhz = string.Empty;

    /// <summary>Any of this strip's commands can call into <see cref="IRadioSessionService"/> while no
    /// radio is connected (a routine, common state, not an edge case) -- that throws
    /// <c>InvalidOperationException</c>, and left uncaught that crashes the whole app (a real crash
    /// found via hands-on verification, not hypothetical). Same catch-and-report-locally pattern as
    /// <c>TxControlsPaneViewModel.ErrorMessage</c>.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Ultracode audit finding #34's automatic-restart mechanism -- deliberately a SEPARATE
    /// property from <see cref="ErrorMessage"/>, not a reuse of it: <see cref="ErrorMessage"/> already
    /// has 5 write sites with no priority order, 3 of which null it unconditionally on entry to an
    /// unrelated action (editing the frequency box, applying a preset, hitting Tune), and
    /// <see cref="SetReceivingSafeAsync"/>'s own success path nulls it too -- reusing it here would let
    /// any of those silently wipe a maintenance message the user hasn't acted on yet.</summary>
    [ObservableProperty]
    private string? _maintenanceMessage;

    [ObservableProperty]
    private RadioMode _selectedRadioMode = RadioMode.Usb;

    private bool _suppressModeCommand;

    [ObservableProperty]
    private int _txVolumePercent = 100;

    [ObservableProperty]
    private double _tuneFrequencyHz = 1750;

    [ObservableProperty]
    private double _tuneDurationSeconds = 5;

    /// <summary>Backs the Transceiver card's Receiving/Halt toggle -- real state, mirrors
    /// <see cref="ISstvSessionService.IsReceiving"/> exactly (including the case where a startup
    /// auto-start silently failed for lack of an audio device).</summary>
    [ObservableProperty]
    private bool _isReceiving;

    private bool _suppressReceivingCommand;

    /// <summary>Real state: true only while <see cref="IRadioSessionService.ConnectionEvents"/>'s most
    /// recent lifecycle transition was <see cref="RadioConnectionState.Connected"/> -- a
    /// <see cref="RadioConnectionState.CommandFailed"/> event is deliberately ignored here (that state
    /// means a single command failed while the connection itself stays healthy, per that enum's own
    /// doc comment; it must not flip this indicator off).</summary>
    [ObservableProperty]
    private bool _catLinked;

    /// <summary>spec/18-path-to-1.0.md High item 10: real, rig-REPORTED PTT/keyed state --
    /// <see cref="RadioState.IsTransmitting"/>, refreshed every successful poll
    /// (<see cref="OnStateChanged"/>) and cleared whenever <see cref="CatLinked"/> drops
    /// (<see cref="OnConnectionEvent"/>), so it can never stay stuck showing "keyed" once the link
    /// itself is gone. NOT the same thing as <c>TxControlsPaneViewModel.IsTransmitting</c> (this
    /// app's own "a TransmitAsync call is currently in flight" flag) -- this one is the RIG'S OWN
    /// readback of whether it's actually keyed, true regardless of the reason (a real transmit, a
    /// manual <see cref="ISstvSessionService.SetPttLockAsync"/> lock, or an external Tune), false
    /// whenever the connected rig doesn't support PTT readback at all (VOX/DTR-only keying --
    /// see this indicator's own tooltip). Lags the rig's real key-down/key-up by roughly one poll
    /// interval (<see cref="RadioConnectionSpec.PollInterval"/>, ~250ms default) plus rig
    /// round-trip -- not instantaneous.</summary>
    [ObservableProperty]
    private bool _isKeyed;

    /// <summary>VFO card's UTC clock -- real, ticking, zero backend dependency
    /// (spec/17-rx-telemetry-feasibility.md).</summary>
    [ObservableProperty]
    private string _utcClockDisplay = string.Empty;

    /// <summary>Raw Hz mirror of <see cref="FrequencyDisplay"/> -- that property is a formatted
    /// string, not round-trippable, so <see cref="StoreCurrentPresetAsync"/> needs its own copy of
    /// the last <see cref="RadioState.FrequencyHz"/> to build a <see cref="FrequencyPreset"/> from.</summary>
    private long _currentFrequencyHz;

    public RadioStatusViewModel(IRadioSessionService radioSession, ISstvSessionService sstvSession, ILocalizationService localization, ILogger<RadioStatusViewModel> logger)
    {
        _radioSession = radioSession;
        _sstvSession = sstvSession;
        _localization = localization;
        _logger = logger;
        _frequencyDisplay = localization.GetString("RadioStatus.NoFrequency");
        _modeDisplay = string.Empty;
        _isReceiving = sstvSession.IsReceiving;
        _catLinked = radioSession.LastKnownState is not null;

        radioSession.StateChanges.Subscribe(OnStateChanged);
        radioSession.ConnectionEvents.Subscribe(OnConnectionEvent);
        if (radioSession.LastKnownState is { } state)
        {
            OnStateChanged(state);
        }

        sstvSession.MaintenanceWarningRaised += OnMaintenanceWarningRaised;
        sstvSession.MaintenanceWarningCleared += OnMaintenanceWarningCleared;
        sstvSession.MaintenanceCriticalStopRaised += OnMaintenanceCriticalStopRaised;

        _ = LoadPresetsSafeAsync();
        _ = LoadTxVolumeSafeAsync();

        UpdateUtcClock();
        _utcClockTimer = new DispatcherTimer(UtcClockTickInterval, DispatcherPriority.Background, (_, _) => UpdateUtcClock());
        _utcClockTimer.Start();

        // spec/18-path-to-1.0.md High item 8: Program.cs's own automatic StartReceivingAsync()
        // attempt at app launch (the ONLY place capture starts -- this app has no explicit "Start
        // Receiving" first-run step) runs before this ViewModel exists, so a failure there (e.g.
        // no configured/default audio device) is caught and only logged -- nothing in the UI ever
        // explained it, "first-run silent dead end." If that attempt already failed, `_isReceiving`
        // above is false; setting the PUBLIC property here (not the backing field) is exactly what
        // a real user click on the Receiving toggle does -- OnIsReceivingChanged fires and reuses
        // SetReceivingSafeAsync's already-working retry/ErrorMessage machinery unchanged. Two
        // outcomes, both already correct via existing code: the retry SUCCEEDS (a transient timing
        // issue at the earlier attempt -- silently self-heals, strictly better than explaining a
        // failure a retry would have avoided), or it FAILS AGAIN (a genuinely unavailable device --
        // the toggle correctly reverts to Halt and ErrorMessage becomes visible in the header,
        // satisfying the roadmap's own "status-bar message" minimum-fix option). Code-review
        // correction: the toggle/status-LED DOES visibly claim "Receiving" for a real, possibly
        // non-trivial window before that revert lands -- not merely "one frame." The revert is
        // deferred through SetReceivingSafeAsync's full await chain (device enumeration/probing,
        // MiniAudioDeviceEnumerator.RefreshAsync's own doc comment notes this can be slow or, on a
        // wedged audio server, hang outright), not a single Dispatcher.Post hop. Accepted anyway:
        // on any healthy machine this resolves in well under a second, and the pathological-hang
        // case is a pre-existing risk in SetReceivingSafeAsync itself, not something this retry
        // introduces -- not fixed here. Safe to fire synchronously from THIS constructor either
        // way: RefreshAsync offloads via Task.Run, so this always yields before any of that device
        // I/O runs, and the awaited chain in front of it (settings load) is a small local file read
        // that completes inline regardless -- the constructor itself never blocks waiting for a
        // result, it only kicks off work that continues after the constructor has already
        // returned. A no-op when the earlier attempt already succeeded (the overwhelmingly common
        // case): the `!_isReceiving` guard above skips the property set entirely, so
        // OnIsReceivingChanged never fires and no redundant StartReceivingAsync call happens.
        if (!_isReceiving)
        {
            IsReceiving = true;
        }
    }

    private void UpdateUtcClock() => UtcClockDisplay = _localization.GetString("RadioStatus.UtcValueFormat", DateTimeOffset.UtcNow);

    public IReadOnlyList<RadioMode> AvailableModes { get; } = Enum.GetValues<RadioMode>();

    public ObservableCollection<FrequencyPresetButtonViewModel> Presets { get; } = [];

    public ObservableCollection<FrequencyPresetEditorRowViewModel> EditorRows { get; } = [];

    private void OnStateChanged(RadioState state)
    {
        // IRadioSessionService.StateChanges pushes synchronously from IRadioController's own poll
        // loop thread (see that interface's doc comment) -- marshal to the UI thread here, the only
        // place in this codebase allowed to touch Avalonia's Dispatcher (spec/01-architecture.md).
        Dispatcher.UIThread.Post(() =>
        {
            _currentFrequencyHz = state.FrequencyHz;
            StoreCurrentPresetCommand.NotifyCanExecuteChanged();
            FrequencyDisplay = $"{state.FrequencyHz / 1_000_000.0:0.000000} MHz";
            ModeDisplay = state.Mode.ToString();

            _suppressModeCommand = true;
            SelectedRadioMode = state.Mode;
            _suppressModeCommand = false;

            IsKeyed = state.IsTransmitting;
        });
    }

    private void OnConnectionEvent(RadioConnectionEvent evt)
    {
        if (evt.State == RadioConnectionState.CommandFailed)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            CatLinked = evt.State == RadioConnectionState.Connected;

            // spec/18-path-to-1.0.md High item 10, round-1 plan-review blocker: IsKeyed is only
            // ever refreshed by a SUCCESSFUL poll (OnStateChanged above) -- a transport failure
            // publishes only a connection event with no new RadioState, so without this, a rig
            // that was keyed when the link dropped would stay showing "keyed" indefinitely with
            // nothing behind it. A CommandFailed-only failure (a single command failed, connection
            // itself still healthy -- see CatLinked's own established reasoning a few lines up)
            // deliberately does NOT reach this method at all (the early return above), so it does
            // NOT clear IsKeyed either -- accepted, documented residual gap: polling continues, the
            // next good poll self-heals it; a staleness timer for that narrower case is
            // disproportionate to this item's scope.
            if (!CatLinked)
            {
                IsKeyed = false;
            }
        });
    }

    /// <summary>Unguarded fire-and-forget from the constructor before this wrap was added -- a
    /// failure here (e.g. settings store not reachable yet) would have thrown on a thread nothing
    /// observes, an unlogged latent crash risk.</summary>
    private async Task LoadPresetsSafeAsync()
    {
        try
        {
            var presets = await _radioSession.GetFrequencyPresetsAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => RebuildPresetCollections(presets));
        }
        catch (Exception ex)
        {
            Log.LoadPresetsFailed(_logger, ex);
        }
    }

    /// <summary>Same reasoning as <see cref="LoadPresetsSafeAsync"/>.</summary>
    private async Task LoadTxVolumeSafeAsync()
    {
        try
        {
            var percent = await _sstvSession.GetTxVolumePercentAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _suppressVolumePersist = true;
                TxVolumePercent = percent;
                _suppressVolumePersist = false;
            });
        }
        catch (Exception ex)
        {
            Log.LoadTxVolumeFailed(_logger, ex);
        }
    }

    private void RebuildPresetCollections(IReadOnlyList<FrequencyPreset> presets)
    {
        Presets.Clear();
        EditorRows.Clear();
        foreach (var preset in presets)
        {
            Presets.Add(new FrequencyPresetButtonViewModel(preset, ApplyPresetCommand));
            EditorRows.Add(new FrequencyPresetEditorRowViewModel(preset, RemovePresetRowCommand));
        }
    }

    [RelayCommand]
    private async Task SetFrequencyAsync()
    {
        if (!double.TryParse(FrequencyInputMhz, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
        {
            return;
        }

        Log.SetFrequencyInvoked(_logger, mhz);
        try
        {
            ErrorMessage = null;
            await _radioSession.SetFrequencyAsync((long)(mhz * 1_000_000)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.SetFrequencyFailed(_logger, mhz, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(FrequencyPreset preset)
    {
        Log.ApplyPresetInvoked(_logger, preset.Label, preset.FrequencyHz, preset.Mode);
        try
        {
            ErrorMessage = null;
            await _radioSession.SetFrequencyAsync(preset.FrequencyHz).ConfigureAwait(false);
            await _radioSession.SetModeAsync(preset.Mode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ApplyPresetFailed(_logger, preset.Label, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    /// <summary>Backs the Favourites card's "Store current" button -- adds the currently-tuned
    /// frequency/mode as a new preset and persists immediately. Deliberately does NOT reuse
    /// <see cref="SavePresetsCommand"/> alone (that command only re-persists whatever's already in
    /// <see cref="EditorRows"/>, which mirrors the existing <see cref="Presets"/> 1:1 since no editor
    /// UI is shown anywhere in this view -- "Edit favourites list" is deliberately unmapped, see
    /// this file's own comment) -- it needs to append the current radio state as a genuinely new row
    /// first. Label is left empty, same precedent as <see cref="AddPresetRow"/>'s manual-add default:
    /// no rename UI exists to fill it in either way. Gated by <see cref="CanStoreCurrentPreset"/>
    /// (auditor-caught, 2026-08-11): <see cref="_currentFrequencyHz"/> is <c>0</c> until the first
    /// <see cref="OnStateChanged"/> call, which never fires with no radio connected/before the first
    /// poll -- without the guard this would silently persist an unremovable "0.000000 USB" preset
    /// (no in-app UI ever exposes <see cref="EditorRows"/>/<see cref="RemovePresetRowCommand"/> to
    /// delete it, "Edit favourites list" is deliberately unmapped, see above).</summary>
    [RelayCommand(CanExecute = nameof(CanStoreCurrentPreset))]
    private async Task StoreCurrentPresetAsync()
    {
        Log.StoreCurrentPresetInvoked(_logger, _currentFrequencyHz, SelectedRadioMode);
        EditorRows.Add(new FrequencyPresetEditorRowViewModel(new FrequencyPreset(string.Empty, _currentFrequencyHz, SelectedRadioMode), RemovePresetRowCommand));
        await SavePresetsAsync().ConfigureAwait(false);
    }

    private bool CanStoreCurrentPreset() => _currentFrequencyHz > 0;

    [RelayCommand]
    private void AddPresetRow()
    {
        Log.AddPresetRowInvoked(_logger);
        EditorRows.Add(new FrequencyPresetEditorRowViewModel(new FrequencyPreset(string.Empty, 14_230_000, RadioMode.Usb), RemovePresetRowCommand));
    }

    [RelayCommand]
    private void RemovePresetRow(FrequencyPresetEditorRowViewModel row)
    {
        Log.RemovePresetRowInvoked(_logger);
        EditorRows.Remove(row);
    }

    [RelayCommand]
    private async Task SavePresetsAsync()
    {
        var presets = new List<FrequencyPreset>();
        foreach (var row in EditorRows)
        {
            if (double.TryParse(row.FrequencyMhzText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
            {
                // Math.Round, not a bare cast (auditor-caught, 2026-08-11): the "0.000000"-formatted
                // mhz * 1_000_000 product can land 1 ULP below the target integer for some real radio
                // frequencies, and a bare (long) cast truncates that down to N-1 Hz instead of N.
                presets.Add(new FrequencyPreset(row.Label, (long)Math.Round(mhz * 1_000_000), row.SelectedMode));
            }
        }

        Log.SavePresetsInvoked(_logger, presets.Count);
        try
        {
            ErrorMessage = null;
            await _radioSession.SaveFrequencyPresetsAsync(presets).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => RebuildPresetCollections(presets));
        }
        catch (Exception ex)
        {
            Log.SavePresetsFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.SavePresetsFailed"));
        }
    }

    [RelayCommand]
    private async Task TuneAsync()
    {
        Log.TuneInvoked(_logger, TuneFrequencyHz, TuneDurationSeconds);
        try
        {
            ErrorMessage = null;
            await _sstvSession.TuneAsync(TuneFrequencyHz, TimeSpan.FromSeconds(TuneDurationSeconds)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.TuneFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.TuneFailed"));
        }
    }

    // ISstvSessionService's own doc comment states these three fire synchronously on the audio drain
    // thread (same contract as ModeDetected) -- marshal to the UI thread before touching any
    // [ObservableProperty] or the _suppressReceivingCommand guard, same as every other cross-thread
    // handler in this class (e.g. OnStateChanged above).
    private void OnMaintenanceWarningRaised()
    {
        Log.MaintenanceWarningRaised(_logger);
        Dispatcher.UIThread.Post(() => MaintenanceMessage = _localization.GetString("RadioStatus.Warning.RxMaintenanceApproaching"));
    }

    private void OnMaintenanceWarningCleared()
    {
        Log.MaintenanceWarningCleared(_logger);
        Dispatcher.UIThread.Post(() => MaintenanceMessage = null);
    }

    private void OnMaintenanceCriticalStopRaised()
    {
        // By the time this fires, ISstvSessionService.StopReceivingAsync has already completed (see
        // that event's own doc comment) -- this only needs to reflect the already-stopped state to
        // the UI, not request the stop itself.
        Log.MaintenanceCriticalStopRaised(_logger);
        Dispatcher.UIThread.Post(() =>
        {
            MaintenanceMessage = _localization.GetString("RadioStatus.Error.RxMaintenanceRequired");
            _suppressReceivingCommand = true;
            IsReceiving = false;
            _suppressReceivingCommand = false;
        });
    }

    partial void OnIsReceivingChanged(bool value)
    {
        if (_suppressReceivingCommand)
        {
            return;
        }

        Log.IsReceivingChanged(_logger, value);
        _ = SetReceivingSafeAsync(value);
    }

    private async Task SetReceivingSafeAsync(bool value)
    {
        try
        {
            if (value)
            {
                await _sstvSession.StartReceivingAsync().ConfigureAwait(false);
            }
            else
            {
                await _sstvSession.StopReceivingAsync().ConfigureAwait(false);
            }

            Dispatcher.UIThread.Post(() => ErrorMessage = null);
        }
        catch (Exception ex)
        {
            Log.SetReceivingFailed(_logger, value, ex);
            Dispatcher.UIThread.Post(() =>
            {
                ErrorMessage = _localization.GetString("RadioStatus.Error.ReceivingFailed");
                _suppressReceivingCommand = true;
                IsReceiving = !value;
                _suppressReceivingCommand = false;
            });
        }
    }

    /// <summary>Separate from unchecking the Receiving toggle -- mirrors mock2's own explicit
    /// Receiving/Halt button pair, not just a single two-state toggle.</summary>
    [RelayCommand]
    private async Task HaltReceivingAsync()
    {
        Log.HaltReceivingInvoked(_logger);
        try
        {
            await _sstvSession.StopReceivingAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _suppressReceivingCommand = true;
                IsReceiving = false;
                _suppressReceivingCommand = false;
                ErrorMessage = null;
            });
        }
        catch (Exception ex)
        {
            Log.HaltReceivingFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.ReceivingFailed"));
        }
    }

    partial void OnTxVolumePercentChanged(int value)
    {
        if (_suppressVolumePersist)
        {
            return;
        }

        // Debounced (not one settings write per slider tick, per the Piece 5 plan) -- also avoids
        // a real correctness hazard: overlapping un-awaited SaveAsync calls racing each other could
        // let a stale write clobber a fresher one.
        _volumePersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _volumePersistCts = cts;
        _ = PersistVolumeDebouncedAsync(value, cts.Token);
    }

    private async Task PersistVolumeDebouncedAsync(int value, CancellationToken ct)
    {
        try
        {
            await Task.Delay(VolumePersistDebounce, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Normal control flow -- a newer slider tick superseded this one. Not worth a log line.
            return;
        }

        try
        {
            await _sstvSession.SetTxVolumePercentAsync(value, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistTxVolumeFailed(_logger, value, ex);
        }
    }

    partial void OnSelectedRadioModeChanged(RadioMode value)
    {
        if (_suppressModeCommand)
        {
            return;
        }

        Log.SelectedRadioModeChanged(_logger, value);
        _ = SetModeSafeAsync(value);
    }

    private async Task SetModeSafeAsync(RadioMode value)
    {
        try
        {
            await _radioSession.SetModeAsync(value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.SetModeFailed(_logger, value, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading frequency presets failed")]
        public static partial void LoadPresetsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading TX volume percent failed")]
        public static partial void LoadTxVolumeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SetFrequency invoked: {Mhz} MHz")]
        public static partial void SetFrequencyInvoked(ILogger logger, double mhz);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetFrequency failed: {Mhz} MHz")]
        public static partial void SetFrequencyFailed(ILogger logger, double mhz, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ApplyPreset invoked: {Label} ({FrequencyHz}Hz, {Mode})")]
        public static partial void ApplyPresetInvoked(ILogger logger, string label, long frequencyHz, RadioMode mode);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ApplyPreset failed: {Label}")]
        public static partial void ApplyPresetFailed(ILogger logger, string label, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "StoreCurrentPreset invoked: {FrequencyHz}Hz ({Mode})")]
        public static partial void StoreCurrentPresetInvoked(ILogger logger, long frequencyHz, RadioMode mode);

        [LoggerMessage(Level = LogLevel.Debug, Message = "AddPresetRow invoked")]
        public static partial void AddPresetRowInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RemovePresetRow invoked")]
        public static partial void RemovePresetRowInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SavePresets invoked: {Count} preset(s)")]
        public static partial void SavePresetsInvoked(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Error, Message = "SavePresets failed")]
        public static partial void SavePresetsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Tune invoked: {FrequencyHz}Hz for {Seconds}s")]
        public static partial void TuneInvoked(ILogger logger, double frequencyHz, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Tune failed")]
        public static partial void TuneFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "IsReceiving changed: {Value}")]
        public static partial void IsReceivingChanged(ILogger logger, bool value);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetReceiving({Value}) failed")]
        public static partial void SetReceivingFailed(ILogger logger, bool value, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "HaltReceiving invoked")]
        public static partial void HaltReceivingInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "HaltReceiving failed")]
        public static partial void HaltReceivingFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting TX volume ({Value}%) failed")]
        public static partial void PersistTxVolumeFailed(ILogger logger, int value, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectedRadioMode changed: {Value}")]
        public static partial void SelectedRadioModeChanged(ILogger logger, RadioMode value);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetMode({Value}) failed")]
        public static partial void SetModeFailed(ILogger logger, RadioMode value, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning received from session service")]
        public static partial void MaintenanceWarningRaised(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX maintenance warning cleared")]
        public static partial void MaintenanceWarningCleared(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "RX critical maintenance stop received from session service")]
        public static partial void MaintenanceCriticalStopRaised(ILogger logger);
    }
}

/// <summary>One frequency memory button -- carries its own <see cref="SelectCommand"/> (the parent's
/// <see cref="RadioStatusViewModel.ApplyPresetCommand"/>, set once at construction), same shape as
/// <c>FavoriteModeButtonViewModel</c> and for the identical reason (avoids a cross-DataTemplate
/// binding cast, which crashes at runtime the first time it renders with real data -- see that
/// type's own doc comment).</summary>
public sealed record FrequencyPresetButtonViewModel(FrequencyPreset Preset, System.Windows.Input.ICommand SelectCommand)
{
    /// <summary>Uppercased for display only (mock2's label line is CSS
    /// <c>text-transform:uppercase</c>, a rendering rule, not a data rule) -- the user's own typed
    /// casing is preserved in <see cref="Preset"/> and in the separate editor row view-model used
    /// by the "Edit list..." flyout.</summary>
    public string Label => Preset.Label.ToUpperInvariant();

    /// <summary>Top line of the two-line button content mock2's own Favourites card uses
    /// (frequency + mode, e.g. "14.230.000 USB"), real -- computed from the same
    /// <see cref="FrequencyPreset"/> the button already carries, not a second data source.
    /// 6-decimal (Hz-precision) MHz format matches this same view-model's own
    /// <see cref="RadioStatusViewModel.FrequencyDisplay"/> and
    /// <see cref="FrequencyPresetEditorRowViewModel"/>'s text field, not the truncated 3-decimal
    /// format this used before -- 3 decimals silently drops real sub-kHz precision on a preset
    /// frequency, not just a cosmetic gap. Mode is uppercased for display (ham-radio sideband
    /// abbreviations are conventionally written uppercase; <see cref="RadioMode"/>'s own enum
    /// member casing, e.g. "Usb", is not).</summary>
    public string FrequencyWithMode => $"{Preset.FrequencyHz / 1_000_000.0:0.000000} {Preset.Mode.ToString().ToUpperInvariant()}";
}

/// <summary>One editable row in the "Edit presets..." flyout. A plain mutable view-model (not a
/// record like <see cref="FrequencyPresetButtonViewModel"/>) since its fields are two-way bound
/// text/combo inputs, not fixed-at-construction display data.</summary>
public sealed partial class FrequencyPresetEditorRowViewModel : ObservableObject
{
    public FrequencyPresetEditorRowViewModel(FrequencyPreset preset, System.Windows.Input.ICommand removeCommand)
    {
        _label = preset.Label;
        _frequencyMhzText = (preset.FrequencyHz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture);
        _selectedMode = preset.Mode;
        RemoveCommand = removeCommand;
    }

    public System.Windows.Input.ICommand RemoveCommand { get; }

    public IReadOnlyList<RadioMode> AvailableModes { get; } = Enum.GetValues<RadioMode>();

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _frequencyMhzText;

    [ObservableProperty]
    private RadioMode _selectedMode;
}
