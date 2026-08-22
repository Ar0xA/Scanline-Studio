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
    /// property from <see cref="ErrorMessage"/>, not a reuse of it: <see cref="ErrorMessage"/> has
    /// many write sites with no priority order across this class's near-identical command methods
    /// (Tier B audit finding: stale count corrected here, was "5" -- now 10 assignment sites across
    /// 7 methods and still growing as sibling methods get fixed to match each other, e.g.
    /// <see cref="SetModeSafeAsync"/>'s own added null-on-entry), several of which null it
    /// unconditionally on entry to an unrelated action (editing the frequency box, applying a
    /// preset, hitting Tune, changing mode), and <see cref="SetReceivingSafeAsync"/>'s own success
    /// path nulls it too -- reusing it here would let any of those silently wipe a maintenance
    /// message the user hasn't acted on yet.</summary>
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

    /// <summary>User-reported gap (2026-08-18): "while TX lights up, receiving should not stay
    /// green" -- the Transceiver card's Receiving toggle stayed visually lit the same color
    /// throughout a local transmission, even though <see cref="ISstvSessionService.TransmitAsync"/>
    /// genuinely pauses capture for that window. Deliberately does NOT touch <see cref="IsReceiving"/>
    /// itself (the toggle's own Checked/click-handling semantics stay "capture is armed" --
    /// unchecking it still really stops capture) -- this is a SEPARATE, purely visual dim flag, so
    /// the fix is exactly the surgical scope the user asked for, not a redefinition of what
    /// "Receiving" means or does. Mirrors <see cref="ISstvSessionService.CapturePausedForTransmitChanged"/>
    /// exactly -- see that event's own doc comment for why it only fires for a TX-caused pause, not
    /// a manual Halt click.</summary>
    [ObservableProperty]
    private bool _isCapturePausedForTx;

    /// <summary>Auditor usability review follow-up (2026-08-18): the VFO card's "rig meters" pill
    /// (`RadioHeaderView.axaml`) was a literal "—" stub, disabled + tooltipped, on the STATED
    /// assumption that "no rig-meters concept exists on IRadioSessionService today"
    /// (spec/16-gui-wiring-survey.md:347) -- verified that assumption directly against
    /// <c>RigctldClientProtocol.PollAsync</c>/<c>HamlibRadioProtocol.PollAsync</c> before touching
    /// anything: it was WRONG. <see cref="RadioState.SwrRatio"/>/<see cref="RadioState.AlcLevel"/>/
    /// <see cref="RadioState.PowerPercent"/> are real, live, already-polled TX-only meter readings
    /// (real `l SWR`/`l ALC`/`l RFPOWER_METER` rigctld queries, gated on <c>IsTransmitting</c> +
    /// per-meter capability flags) -- this VM's own <see cref="OnStateChanged"/> just never read them
    /// out. (The ADJACENT "RX level" meter, a SEPARATE stub in the Transceiver card, was genuinely
    /// still a stub at the time this comment was written -- see <see cref="RxLevelDb"/> below for
    /// that gap's own since-closed follow-up.)
    /// <para>Joins whichever of the three meters are actually non-null this poll (independently
    /// absent per <see cref="RadioState"/>'s own doc comment -- capability absent, RX-time, or a
    /// failed read), "—" only when none are -- a rig missing one meter capability still shows the
    /// other two instead of the whole pill going blank.</para></summary>
    [ObservableProperty]
    private string _rigMetersDisplay = "—";

    /// <summary>Tier 2/3 follow-up to <see cref="RigMetersDisplay"/> above: the Transceiver card's
    /// "RX level" meter was a literal fixed-63%/78% stub with no real gain parameter behind it
    /// (<see cref="RadioState.SignalStrengthDb"/> was hardcoded <see langword="null"/> in both
    /// protocol implementations). Now real: <c>l STRENGTH</c> (rigctld) / <c>RIG_LEVEL_STRENGTH</c>
    /// (Hamlib), RX-time-gated (opposite of the TX-only meters above -- see
    /// <see cref="RadioState.SignalStrengthDb"/>'s own doc comment). Raw dB-relative-to-S9 value;
    /// see <see cref="RxLevelDisplay"/>/<see cref="RxLevelFillPercent"/> for the derived, bindable
    /// display forms.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RxLevelDisplay))]
    [NotifyPropertyChangedFor(nameof(RxLevelFillPercent))]
    private int? _rxLevelDb;

    /// <summary>"+14 dB"/"−14 dB"/"0 dB" (relative to S9, matching Hamlib's own documented unit --
    /// see <see cref="RadioState.SignalStrengthDb"/>'s own doc comment), "—" when
    /// <see cref="RxLevelDb"/> is <see langword="null"/> (RX capability absent, currently
    /// transmitting, or a failed read this poll -- all indistinguishable here by design, same as
    /// every other per-poll-optional field on this VM).</summary>
    public string RxLevelDisplay => RxLevelDb is { } db ? $"{db:+0;−0;0} dB" : "—";

    /// <summary>Standard ham-radio S-meter convention (not a legacy port -- this is new UI, no
    /// legacy precedent to match): S0..S9 spans roughly 54 dB at ~6 dB/S-unit, and S9+60 is a common
    /// real bargraph max on modern rigs -- so this meter's track spans S0 (-54 dB relative to S9) to
    /// S9+60 (+60 dB), clamped at both ends rather than pinning silently past either edge. Design
    /// decision, not a measured fact -- a specific rig's own S-meter calibration may differ; this is
    /// a reasonable, commonly-used default for a generic cross-rig display.</summary>
    private const int SMeterFloorDb = -54;
    private const int SMeterCeilingDb = 60;

    /// <summary>0-100 fill fraction for the meter <c>Border</c>'s Star-weighted column (see
    /// <see cref="ScanlineStudio.UI.Converters.DoubleToStarGridLengthConverter"/>) -- <c>0</c> (empty bar, not a
    /// missing-data indicator of its own) when <see cref="RxLevelDb"/> is <see langword="null"/>,
    /// same "unknown reads as the low end, not a special state" convention <see cref="RxLevelDisplay"/>'s
    /// sibling "—" text already carries the actual missing-data signal for.</summary>
    public double RxLevelFillPercent => RxLevelDb is { } db
        ? Math.Clamp((db - SMeterFloorDb) / (double)(SMeterCeilingDb - SMeterFloorDb) * 100.0, 0.0, 100.0)
        : 0.0;

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
        sstvSession.CapturePausedForTransmitChanged += OnCapturePausedForTransmitChanged;

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
        // introduces -- not fixed here. A no-op when the earlier attempt already succeeded (the
        // overwhelmingly common case): the `!_isReceiving` guard above skips the property set
        // entirely, so OnIsReceivingChanged never fires and no redundant StartReceivingAsync call
        // happens.
        //
        // Tier B audit finding, correcting this comment's own prior claim: the settings-file read
        // this retry's own await chain reaches (JsonSettingsStore.LoadAsync -> File.Exists/
        // File.OpenRead) is NOT "inline regardless" -- it's the identical synchronous prefix
        // SstvSessionService.StartReceivingAsync's OWN doc comment cites as the reason IT wraps that
        // same call in Task.Run (a network-mounted/wedged settings path can make it genuinely slow
        // or hang), and calling an async method runs synchronously up to its first real await --
        // meaning this used to run that prefix inline on THIS constructor's own calling thread
        // (App.axaml.cs resolves MainViewModel, and hence this VM, on the UI thread), a real
        // startup-stall risk this comment previously argued away. Deferred via Dispatcher.UIThread.Post
        // instead: the constructor returns immediately either way, and the property set (which
        // raises PropertyChanged for the toggle binding) still happens on the UI thread, just as a
        // queued callback rather than inline -- matching this class's own established
        // deferred-cross-thread-work convention everywhere else in this file.
        if (!_isReceiving)
        {
            Dispatcher.UIThread.Post(() => IsReceiving = true);
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

            // Tier B audit finding: try/finally, not a bare set-then-reset -- a throw from
            // SelectedRadioMode's own PropertyChanged fan-out (a binding/converter/subscriber) used
            // to leave _suppressModeCommand stuck true for the process lifetime, permanently and
            // silently suppressing every future user mode change (OnSelectedRadioModeChanged would
            // keep early-returning forever). Same fix applied to every other suppression-flag
            // set/reset pair in this class.
            try
            {
                _suppressModeCommand = true;
                SelectedRadioMode = state.Mode;
            }
            finally
            {
                _suppressModeCommand = false;
            }

            IsKeyed = state.IsTransmitting;
            RigMetersDisplay = FormatRigMeters(state);
            RxLevelDb = state.SignalStrengthDb;
        });
    }

    /// <summary>Split out from <see cref="OnStateChanged"/> so it's unit-testable without a real
    /// <see cref="Dispatcher.UIThread"/> pump around it (same reasoning precedent as
    /// <c>TxImageEditorPaneView.ComputeElementResize</c>'s own doc comment: pure formatting logic
    /// doesn't need cross-thread machinery wrapped around it just to verify). Invariant-culture: this
    /// is a live UI readout, not a persisted/round-tripped value, so culture-formatted decimals are
    /// fine here (unlike <c>MacroTextResolver.FormatFrequency</c>'s own baked-into-the-transmitted-
    /// image reasoning for InvariantCulture, which doesn't apply to a screen-only readout).</summary>
    private static string FormatRigMeters(RadioState state)
    {
        List<string> parts = [];
        if (state.SwrRatio is { } swr)
        {
            parts.Add($"SWR {swr:0.0}");
        }

        if (state.AlcLevel is { } alc)
        {
            parts.Add($"ALC {alc:0}%");
        }

        if (state.PowerPercent is { } power)
        {
            parts.Add($"PWR {power:0}%");
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "—";
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
            // Auditor-caught (2026-08-18, RX signal-strength meter review): RigMetersDisplay/
            // RxLevelDb had the exact same staleness gap as IsKeyed above -- both are only ever
            // refreshed by OnStateChanged, so without this a rig that read "SWR 1.2 · PWR 75%" or
            // "−14 dB" when the link dropped would keep showing that live-looking reading
            // indefinitely with nothing behind it. Same CommandFailed-only exemption as IsKeyed.
            if (!CatLinked)
            {
                IsKeyed = false;
                RigMetersDisplay = "—";
                RxLevelDb = null;
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
                // Tier B audit finding: try/finally -- see OnStateChanged's own comment on the same
                // fix for _suppressModeCommand.
                try
                {
                    _suppressVolumePersist = true;
                    TxVolumePercent = percent;
                }
                finally
                {
                    _suppressVolumePersist = false;
                }
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
            // Math.Round, not a bare cast (Tier B audit finding -- the sibling SavePresetsAsync had
            // this exact fix already, auditor-caught 2026-08-11, but it was never applied here): the
            // mhz * 1_000_000 product can land 1 ULP below the target integer for some real radio
            // frequencies, and a bare (long) cast truncates that down to N-1 Hz instead of N.
            await _radioSession.SetFrequencyAsync((long)Math.Round(mhz * 1_000_000)).ConfigureAwait(false);
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
        var row = new FrequencyPresetEditorRowViewModel(new FrequencyPreset(string.Empty, _currentFrequencyHz, SelectedRadioMode), RemovePresetRowCommand);
        EditorRows.Add(row);

        // Tier B audit finding: this used to call the SavePresetsCommand's own method and ignore
        // the outcome -- if the save failed, the just-added row stayed in EditorRows with nothing
        // ever removing it (SavePresetsInternalAsync's own failure path deliberately leaves
        // EditorRows untouched, matching its "report the error, don't discard unsaved edits"
        // contract). Since no shipped UI exposes EditorRows/RemovePresetRowCommand at all ("Edit
        // favourites list" is deliberately unmapped, see this method's own class-level doc comment),
        // a retry after the failure appended a SECOND row on top of the first, persisting a
        // duplicate, permanently undeletable preset the moment the save eventually succeeded.
        if (!await SavePresetsInternalAsync().ConfigureAwait(false))
        {
            Dispatcher.UIThread.Post(() => EditorRows.Remove(row));
        }
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

    /// <summary>Bound to the "Save presets" button -- the actual work (and its success/failure
    /// signal) lives in <see cref="SavePresetsInternalAsync"/> below, same
    /// command-wraps-internal-bool split as <c>LogbookPaneViewModel.RefreshAsync</c>/
    /// <c>RefreshInternalAsync</c>'s own established pattern, needed so
    /// <see cref="StoreCurrentPresetAsync"/> can react to a failed save without duplicating this
    /// method's own body.</summary>
    [RelayCommand]
    private async Task SavePresetsAsync() => await SavePresetsInternalAsync().ConfigureAwait(false);

    private async Task<bool> SavePresetsInternalAsync()
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
            return true;
        }
        catch (Exception ex)
        {
            Log.SavePresetsFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.SavePresetsFailed"));
            return false;
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
            // Tier B audit finding: try/finally -- see OnStateChanged's own comment on the same fix
            // for _suppressModeCommand.
            try
            {
                _suppressReceivingCommand = true;
                IsReceiving = false;
            }
            finally
            {
                _suppressReceivingCommand = false;
            }
        });
    }

    // User-reported gap (2026-08-18): same cross-thread marshaling contract as every other
    // ISstvSessionService event this class already subscribes to above (fires synchronously from
    // whatever thread paused/resumed capture, not the UI thread).
    private void OnCapturePausedForTransmitChanged(bool paused) => Dispatcher.UIThread.Post(() => IsCapturePausedForTx = paused);

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
                // Tier B audit finding: try/finally -- see OnStateChanged's own comment on the same
                // fix for _suppressModeCommand.
                try
                {
                    _suppressReceivingCommand = true;
                    IsReceiving = !value;
                }
                finally
                {
                    _suppressReceivingCommand = false;
                }
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
                // Tier B audit finding: try/finally -- see OnStateChanged's own comment on the same
                // fix for _suppressModeCommand.
                try
                {
                    _suppressReceivingCommand = true;
                    IsReceiving = false;
                }
                finally
                {
                    _suppressReceivingCommand = false;
                }

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
        // Tier B audit finding: every sibling command (SetFrequencyAsync/ApplyPresetAsync/
        // SavePresetsAsync/TuneAsync null ErrorMessage on entry; SetReceivingSafeAsync/
        // HaltReceivingAsync null it on success) manages ErrorMessage around its own outcome -- this
        // one didn't, so a stale "No radio connected" from an earlier failed action could survive a
        // later, genuinely successful mode change with nothing to clear it.
        ErrorMessage = null;
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
