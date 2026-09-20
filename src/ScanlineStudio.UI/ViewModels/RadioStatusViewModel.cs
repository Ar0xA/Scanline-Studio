using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

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

    /// <summary>Same poll interval <see cref="ScanlineStudio.UI.ViewModels.RxImagePaneViewModel"/>'s
    /// own telemetry timer already uses -- see <see cref="RxAudioPeakLevel"/>'s own doc comment.</summary>
    private static readonly TimeSpan RxAudioLevelPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IRadioSessionService _radioSession;
    private readonly ISstvSessionService _sstvSession;
    private readonly ILocalizationService _localization;
    private readonly IAppearanceSettingsService _appearanceSettings;
    private readonly ILogger<RadioStatusViewModel> _logger;
    private readonly DispatcherTimer _utcClockTimer;
    private readonly DispatcherTimer _rxAudioLevelTimer;
    private bool _suppressVolumePersist;
    private CancellationTokenSource? _txVolumePersistCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrequencyDisplayOrPlaceholder))]
    private string _frequencyDisplay;

    /// <summary>Same value as <see cref="FrequencyDisplay"/>, except while no rig has ever reported
    /// a frequency this session (<c>_currentFrequencyHz == 0</c>) -- for a consumer outside the
    /// status bar's own deliberate 7-segment-style idle readout ("000.000.000",
    /// <c>RadioStatus.NoFrequency</c>), which reads as a real fake-live value everywhere else in
    /// this app (e.g. sitting among the Frame Metadata card's own "—" placeholder rows). Auditor
    /// finding, 2026-08-25 code-review round: <see cref="FrequencyDisplay"/> itself is left
    /// untouched -- the status bar's idle readout is intentional there, this is a second, narrower
    /// property for a different display context.</summary>
    public string FrequencyDisplayOrPlaceholder => _currentFrequencyHz > 0 ? FrequencyDisplay : "—";

    /// <summary>ui_transition_plan.md step 5 (T1-6): the raw live value behind
    /// <see cref="FrequencyDisplayOrPlaceholder"/>, for a consumer that needs the number itself (the
    /// Logbook prefill's fallback when the RX frame has no latched frequency of its own -- see
    /// <c>MainWindow.axaml.cs</c>'s own <c>LogQsoRequested</c> handler). Same "no rig has ever
    /// reported a frequency this session" null convention as that property.</summary>
    public long? CurrentFrequencyHz => _currentFrequencyHz > 0 ? _currentFrequencyHz : null;

    /// <summary>Companion to <see cref="CurrentFrequencyHz"/> -- gated on the SAME "has a rig ever
    /// reported" condition, deliberately NOT just <see cref="SelectedRadioMode"/> directly: that
    /// property defaults to <see cref="RadioMode.Usb"/> even with no radio ever connected (a
    /// TX-mode-picker default, not a claim about a real rig), so using it unconditionally as a
    /// Logbook fallback would fabricate "USB" out of thin air for an operator who never had a radio
    /// connected at all.</summary>
    public RadioMode? CurrentRadioModeOrNull => _currentFrequencyHz > 0 ? SelectedRadioMode : null;

    [ObservableProperty]
    private string _modeDisplay;

    [ObservableProperty]
    private string _frequencyInputMhz = string.Empty;

    /// <summary>ui_transition_plan.md step 11 (T2-1): the VFO card's 40px frequency readout was
    /// display-only -- <see cref="SetFrequencyCommand"/>/<see cref="FrequencyInputMhz"/> above were
    /// already fully built and tested (this class' own doc comment already called out "an editable
    /// frequency/mode control" as the piece's intent), just never reachable from any View. This flag
    /// swaps the readout for an inline <c>TextBox</c> bound to <see cref="FrequencyInputMhz"/> --
    /// Enter applies via <see cref="SetFrequencyCommand"/> (which now also exits edit mode on
    /// success only, per <c>RadioHeaderView.axaml.cs</c>'s own key-handling doc comment), Escape
    /// reverts without applying.</summary>
    [ObservableProperty]
    private bool _isEditingFrequency;

    /// <summary>Backs the readout's click-to-edit affordance -- pre-fills
    /// <see cref="FrequencyInputMhz"/> from whatever is CURRENTLY displayed (not a blank field), so
    /// clicking to fix a typo or nudge the frequency doesn't require retyping the whole value. Empty
    /// when no rig has ever reported a frequency this session (<see cref="CurrentFrequencyHz"/> is
    /// null), matching every other "no radio yet" placeholder convention in this class -- editing
    /// still works from blank, it just doesn't fabricate a starting number.</summary>
    [RelayCommand]
    private void BeginEditFrequency()
    {
        // Auditor-caught gap: without this increment, a double-Enter on a slow backend (Community
        // Toolkit's [RelayCommand] defaults to AllowConcurrentExecutions=true, confirmed directly
        // against AsyncRelayCommand.Execute -- no CanExecute check blocks the second call) starts a
        // second in-flight SetFrequencyAsync under the SAME session id. If the operator closes that
        // edit and reopens a new one before the second call finishes, its stale completion still
        // matches _frequencyEditSessionId and clobbers the new session -- the exact race
        // _frequencyEditSessionId exists to prevent.
        _frequencyEditSessionId++;
        FrequencyInputMhz = CurrentFrequencyHz is { } hz
            ? (hz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture)
            : string.Empty;
        ErrorMessage = null;
        IsEditingFrequency = true;
    }

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

    // NotifyCanExecuteChangedFor is load-bearing, not decorative: OnStateChanged calls the command's
    // own NotifyCanExecuteChanged BEFORE it assigns this property, so without this attribute the poll
    // that first reports an unsupported mode re-evaluates CanExecute against the PREVIOUS mode and
    // leaves "Store current" live for one poll interval on exactly the transition it must block.
    [NotifyPropertyChangedFor(nameof(IsSidebandUsb), nameof(IsSidebandLsb), nameof(IsSidebandFm))]
    [NotifyCanExecuteChangedFor(nameof(StoreCurrentPresetCommand))]
    [ObservableProperty]
    private RadioMode _selectedRadioMode = RadioMode.Usb;

    /// <summary>User bug/feature report (2026-09-01): "SSB as PKT" -- when checked, the USB/LSB
    /// segment buttons target Hamlib's own PKTUSB/PKTLSB modes (<see cref="RadioMode.Data"/>/
    /// <see cref="RadioMode.DataR"/> in this app's own generic enum -- <see langword="not"/> new
    /// values; every backend already maps these correctly: confirmed against
    /// <c>HamlibRadioProtocol</c>, <c>RigctldClientProtocol</c>, and <c>FlrigModeTokens</c>, which
    /// already round-trip PKTUSB/PKTLSB ↔ USB-D/DATA-U/DATA-L for exactly this "digital mode on an
    /// SSB-family sideband" concept -- this feature needed no new CAT-layer plumbing, only a UI
    /// mapping choice). Placement confirmed with the user: main header, not Options, since it's
    /// touched mid-session, not a one-time rig-setup preference.
    /// User-reported gap, fixed 2026-09-15: this used to be session-only (reset to
    /// <see langword="false"/> on every restart) -- persisted now via
    /// <see cref="IRadioSessionService.GetSsbAsPktPreferenceAsync"/>/
    /// <see cref="IRadioSessionService.SaveSsbAsPktPreferenceAsync"/>, same
    /// <see cref="_suppressSsbAsPktPersist"/>-guarded restore-then-write shape
    /// <see cref="TxVolumePercent"/> already established (see that property's own
    /// <c>LoadTxStateSafeAsync</c>/<c>OnTxVolumePercentChanged</c> for the pattern this mirrors).</summary>
    [NotifyPropertyChangedFor(nameof(IsSidebandUsb), nameof(IsSidebandLsb))]
    [ObservableProperty]
    private bool _ssbAsPkt;

    private bool _suppressSsbAsPktPersist;

    /// <summary>Stub survey Tier 4 (2026-08-26): the VFO card's sideband segment
    /// (<c>RadioHeaderView.axaml</c>) was a fully-clickable but entirely unwired literal group --
    /// <see cref="SelectedRadioMode"/> itself was already real (<see cref="SetModeSafeAsync"/> keys
    /// PTT-adjacent CAT mode changes), just never bound to this specific control. Three plain
    /// get/set booleans, not a generic enum-to-bool converter -- matches this class's own established
    /// pattern for a bound 2/3-way segment (see the Receive tab's Listening/Paused segment, bound
    /// directly to a plain bool with Avalonia's <c>!</c> negation operator, no converter). <see
    /// cref="RadioMode"/> has 12 members total; only these 3 slots are exposed here since they're the
    /// only ones this segment offers -- selecting any of them while the rig is actually in, say, CW or
    /// RTTY is a real, silent mode change, same as any other <see cref="SelectedRadioMode"/> write.
    /// (<see cref="RadioMode.Data"/>/<see cref="RadioMode.DataR"/> reach the USB/LSB slots too, as the
    /// PKT variant of each, once <see cref="SsbAsPkt"/> is checked -- 5 reachable modes total, not 3.)
    /// USB/LSB (not FM -- <see cref="SsbAsPkt"/> only affects the two sideband slots, matching the
    /// user's own exact scope) route through <see cref="SsbAsPkt"/> so a future click also lands on
    /// the PKT variant while it's checked, not just the one-time conversion
    /// <see cref="OnSsbAsPktChanged"/> performs.</summary>
    public bool IsSidebandUsb
    {
        get => SelectedRadioMode == (SsbAsPkt ? RadioMode.Data : RadioMode.Usb);
        set
        {
            if (value)
            {
                // User-reported bug (2026-09-19): must claim operator ownership of SsbAsPkt (see
                // _ssbAsPktOperatorOwned's own doc comment) BEFORE reading it here -- this click is
                // ABOUT to interpret SsbAsPkt's CURRENT value to decide which mode to command, and if
                // a same-poll-tick SyncSsbAsPktFromPolledMode assignment is still free to overwrite it
                // afterward, a click landing right after a poll could still read a value the operator
                // never actually chose.
                _ssbAsPktOperatorOwned = true;
                SelectedRadioMode = SsbAsPkt ? RadioMode.Data : RadioMode.Usb;
            }
        }
    }

    public bool IsSidebandLsb
    {
        get => SelectedRadioMode == (SsbAsPkt ? RadioMode.DataR : RadioMode.Lsb);
        set
        {
            if (value)
            {
                _ssbAsPktOperatorOwned = true;
                SelectedRadioMode = SsbAsPkt ? RadioMode.DataR : RadioMode.Lsb;
            }
        }
    }

    public bool IsSidebandFm
    {
        get => SelectedRadioMode == RadioMode.Fm;
        set
        {
            if (value)
            {
                SelectedRadioMode = RadioMode.Fm;
            }
        }
    }

    /// <summary>Immediately converts whatever's CURRENTLY selected, matching the checkbox's own new
    /// intent -- the operator doesn't have to re-click USB/LSB to see the effect ("when checked, sets
    /// USB/LSB to PKTUSB/PKTLSB", the user's own exact wording). Symmetric: unchecking converts a
    /// currently-Data/DataR mode back to plain Usb/Lsb too, so the segment buttons don't end up with
    /// neither showing selected the moment the box is unchecked. A real CAT mode-set call either way
    /// (via <see cref="SelectedRadioMode"/>'s own change hook), same as directly clicking USB/LSB --
    /// a true no-op (falls to the <c>_ =&gt; SelectedRadioMode</c> self-assignment arm, equality-
    /// guarded by the generated setter, so no PropertyChanged/CAT call either) when the rig is in any
    /// other mode (CW/RTTY/etc.), matching how the USB/LSB buttons themselves never touch an unrelated
    /// mode. The same arm also catches the already-in-target cases (e.g. checking while already on
    /// Data) -- there <see cref="SsbAsPkt"/>'s own <c>NotifyPropertyChangedFor</c> still re-lights the
    /// segment display even though this switch itself is a no-op.</summary>
    partial void OnSsbAsPktChanged(bool value)
    {
        // Restore-time set (LoadSsbAsPktPreferenceSafeAsync) must do NEITHER of the two things
        // below: the mode-conversion switch would silently issue a real CAT mode-set to the rig on
        // every app startup before the operator asked for one, and the save is a pointless (though
        // harmless) rewrite of the exact value just read back.
        if (_suppressSsbAsPktPersist)
        {
            return;
        }

        _ssbAsPktOperatorOwned = true;

        SelectedRadioMode = (value, SelectedRadioMode) switch
        {
            (true, RadioMode.Usb) => RadioMode.Data,
            (true, RadioMode.Lsb) => RadioMode.DataR,
            (false, RadioMode.Data) => RadioMode.Usb,
            (false, RadioMode.DataR) => RadioMode.Lsb,
            _ => SelectedRadioMode,
        };

        _ = SaveSsbAsPktPreferenceSafeAsync(value);
    }

    private async Task SaveSsbAsPktPreferenceSafeAsync(bool value)
    {
        try
        {
            await _radioSession.SaveSsbAsPktPreferenceAsync(value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.SaveSsbAsPktPreferenceFailed(_logger, ex);
        }
    }

    /// <summary>Set true the first time <see cref="SyncSsbAsPktFromPolledMode"/> derives a real value
    /// from the rig -- lets <see cref="LoadSsbAsPktPreferenceSafeAsync"/>'s own restore skip itself if
    /// a poll already won the race (auditor finding: the constructor subscribes to live state and
    /// replays <c>LastKnownState</c> synchronously, but the persisted-preference read is an async
    /// settings-store round trip, so without this flag the restore could land AFTER the first poll and
    /// clobber the rig-derived value for up to one poll interval -- self-healing on the next poll
    /// either way, but not deterministic on the very first frame, which is exactly the "at
    /// connect/startup" moment the user reported).</summary>
    private bool _ssbAsPktCameFromRig;

    /// <summary>User-reported regression (2026-09-19), root-caused by yoniq-auditor + yoniq-principal:
    /// commit 37ea13d turned <see cref="SyncSsbAsPktFromPolledMode"/> into an UNCONDITIONAL per-poll
    /// (~250ms) mirror of the rig's current mode -- correct for the "seed the checkbox from a rig
    /// already sitting in PKTUSB/PKTLSB at connect" case that commit fixed, but wrong the instant the
    /// operator has an opinion of their own: <see cref="SsbAsPkt"/> is a PREFERENCE about how a future
    /// click should be interpreted (see <see cref="IsSidebandUsb"/>/<see cref="IsSidebandLsb"/>'s own
    /// setters, which read it to choose which mode to command), not a pure readback like
    /// <see cref="SelectedRadioMode"/> itself -- unlike that 3-way segment, forcing it back to match
    /// whatever the rig currently reports fights the operator until the rig's own mode has fully
    /// caught up (which for some rigs/CAT links, never happens for a given target mode). Set true the
    /// first time the operator interacts with anything that reads <see cref="SsbAsPkt"/> at
    /// click-time (<see cref="OnSsbAsPktChanged"/>'s own non-suppressed branch,
    /// <see cref="IsSidebandUsb"/>/<see cref="IsSidebandLsb"/>'s setters) -- never reset, same
    /// permanent-latch shape as <see cref="_ssbAsPktCameFromRig"/> above (this app has no "give
    /// control back to the rig" gesture for either flag). <see cref="SyncSsbAsPktFromPolledMode"/>
    /// early-returns once this is true, so a rig that never adopts the commanded mode leaves the
    /// checkbox showing the operator's own last choice instead of rubber-banding back to the rig's
    /// stale mode every poll.</summary>
    private bool _ssbAsPktOperatorOwned;

    /// <summary>Called once from the constructor -- restores the persisted checkbox state before the
    /// operator ever touches it, same "constructor-time restore" shape as
    /// <see cref="LoadTxStateSafeAsync"/>. Guarded the same way: the restore-time write must not
    /// re-trigger <see cref="OnSsbAsPktChanged"/>'s own save (a pointless but harmless extra disk
    /// write) or its CAT mode-conversion side effect (NOT harmless -- it would silently issue a real
    /// mode-set command to the rig on every app startup, before the operator asked for one). Skips the
    /// assignment entirely if <see cref="_ssbAsPktCameFromRig"/> is already true -- see that field's
    /// own doc comment for why a live poll must win this race, not lose it.</summary>
    private async Task LoadSsbAsPktPreferenceSafeAsync()
    {
        try
        {
            var value = await _radioSession.GetSsbAsPktPreferenceAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (_ssbAsPktCameFromRig)
                {
                    return;
                }

                try
                {
                    _suppressSsbAsPktPersist = true;
                    SsbAsPkt = value;
                }
                finally
                {
                    _suppressSsbAsPktPersist = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.LoadSsbAsPktPreferenceFailed(_logger, ex);
        }
    }

    /// <summary>Keeps <see cref="SsbAsPkt"/> live from every poll ONLY UNTIL the operator has an
    /// opinion of their own -- see <see cref="_ssbAsPktOperatorOwned"/>'s own doc comment for the
    /// 2026-09-19 regression this early-return fixes. Before that ownership handoff, mirrors how
    /// <see cref="SelectedRadioMode"/> itself is refreshed in <see cref="OnStateChanged"/>.
    /// User-reported gap this was originally built for: CAT-linked startup already populated
    /// frequency, bandwidth and <see cref="SelectedRadioMode"/> from the rig, but left this checkbox
    /// at its last manual/persisted value -- a rig sitting in PKTUSB/PKTLSB at connect time then
    /// showed neither the USB nor the LSB segment button selected (see <see cref="IsSidebandUsb"/>/
    /// <see cref="IsSidebandLsb"/>, both gated on <see cref="SsbAsPkt"/>). Guarded with
    /// <see cref="_suppressSsbAsPktPersist"/>, same as <see cref="LoadSsbAsPktPreferenceSafeAsync"/>'s
    /// own restore-time set: a poll-driven change must not re-trigger <see cref="OnSsbAsPktChanged"/>'s
    /// CAT mode-set conversion (the mode it would convert TO is the exact mode this poll just reported
    /// FROM) or a disk write (this mirrors the rig's live state, it isn't the operator's own choice to
    /// persist). Only a polled USB/LSB/PKTUSB/PKTLSB mode touches this -- any other mode (FM, CW, RTTY,
    /// ...) leaves it unchanged, matching <see cref="IsSidebandUsb"/>/<see cref="IsSidebandLsb"/>'s own
    /// scope (USB/LSB only, not FM).</summary>
    private void SyncSsbAsPktFromPolledMode(RadioMode mode)
    {
        if (_ssbAsPktOperatorOwned)
        {
            return;
        }

        bool? derived = mode switch
        {
            RadioMode.Data or RadioMode.DataR => true,
            RadioMode.Usb or RadioMode.Lsb => false,
            _ => null,
        };

        if (derived is not { } value)
        {
            return;
        }

        _ssbAsPktCameFromRig = true;

        try
        {
            _suppressSsbAsPktPersist = true;
            SsbAsPkt = value;
        }
        finally
        {
            _suppressSsbAsPktPersist = false;
        }
    }

    private bool _suppressModeCommand;

    /// <summary>"Pwr" -- app-internal TX playback gain (0-100), same shape WSJT-X's/fldigi's own
    /// Pwr controls use: a linear multiplier on the audio THIS app generates, applied right before
    /// playback, never touching the OS mixer (user-directed reversal of an earlier same-session
    /// design that briefly made this a real OS device volume control instead).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TxVolumeDisplay))]
    private int _txVolumePercent = 100;

    /// <summary>Read-only -- there is no user-facing mute toggle in this app, only a display of the
    /// OS's own current mute state for the TX playback device (see
    /// <c>IAudioDeviceMuteQuery.IsDeviceMutedAsync</c>'s own doc comment for why) -- independent of
    /// <see cref="TxVolumePercent"/>'s own app-internal gain: the OS device can still be muted
    /// regardless of what Pwr is set to. Loaded once alongside Pwr (<see cref="LoadTxStateSafeAsync"/>),
    /// not on a live poll -- reflects OS state as of last load, not a continuous watch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TxVolumeDisplay))]
    private bool _txIsMuted;

    /// <summary>Muted-speaker glyph (U+1F507) in place of the Pwr percent number when the OS
    /// reports the TX playback device muted -- the slider's own Value stays bound to the real Pwr
    /// percent underneath either way (OS mute is independent of this app's own gain), only this
    /// readout swaps.</summary>
    public string TxVolumeDisplay => TxIsMuted ? "\U0001F507" : TxVolumePercent.ToString(CultureInfo.InvariantCulture);

    /// <summary>User-directed redesign (same session): "RX level" is a plain incoming-audio-level
    /// meter, same idea as WSJT-X's own RX meter, not a slider. Backed by
    /// <see cref="ISstvSessionService.SignalPeakLevel"/> -- the peak amplitude AFTER the receive
    /// bandpass filter, [0.0, 1.0] -- not <see cref="ISstvSessionService.RawInputPeakLevel"/> (the
    /// raw captured buffer's own pre-filter peak). Switched 2026-09-03, user-reported/-directed:
    /// the raw pre-filter reading includes hum/noise/adjacent-channel energy that never helps a
    /// decode, so a quiet-but-clean SSTV tone read as an alarmingly low raw percentage even when it
    /// decoded perfectly fine -- the post-filter value is the actual demodulated tone strength, a
    /// meaningfully better answer to "does the operator need to change anything." Polled on a
    /// dedicated timer (<see cref="_rxAudioLevelTimer"/>), same 250ms interval
    /// <see cref="ScanlineStudio.UI.ViewModels.RxImagePaneViewModel"/>'s own telemetry timer
    /// already uses.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RxLevelDisplay))]
    [NotifyPropertyChangedFor(nameof(RxLevelFillPercent))]
    [NotifyPropertyChangedFor(nameof(RxLevelInGoodRange))]
    private double _rxAudioPeakLevel;

    /// <summary>The single <see cref="_rxAudioLevelTimer"/> tick handler (2026-09-18) -- folds
    /// <see cref="IsDecodingImage"/>/<see cref="IsDecodingBlinkOn"/> into the same 250ms poll that
    /// already refreshes <see cref="RxAudioPeakLevel"/>, rather than a second timer. Blink toggles
    /// every OTHER tick (~500ms) via <see cref="_decodingBlinkTickCounter"/>, reset to a known
    /// false/0 state the instant a decode isn't in progress so it always starts a fresh cycle (not
    /// mid-phase) the next time one begins.</summary>
    private void OnRxAudioLevelTick()
    {
        RxAudioPeakLevel = _sstvSession.SignalPeakLevel;
        IsSstvTransmitting = _sstvSession.IsTransmitting;

        IsDecodingImage = _sstvSession.ReceivedImage.Progress is { } progress && progress < 1.0;

        if (!IsDecodingImage)
        {
            _decodingBlinkTickCounter = 0;
            IsDecodingBlinkOn = false;
            return;
        }

        // User-requested (2026-09-18): the blink itself can be turned off in Options > Appearance,
        // default on. Disabled means steady amber for the whole decode (IsDecodingBlinkOn just
        // mirrors IsDecodingImage, true the entire time), not "never light up at all" -- the
        // Decoding state itself is not what this setting controls.
        if (!_decodingIndicatorBlinksEnabled)
        {
            _decodingBlinkTickCounter = 0;
            IsDecodingBlinkOn = true;
            return;
        }

        _decodingBlinkTickCounter++;
        IsDecodingBlinkOn = _decodingBlinkTickCounter % 2 == 0;
    }

    /// <summary>Constructor-time initial read of the Appearance checkbox (mirrors
    /// <see cref="LoadSsbAsPktPreferenceSafeAsync"/>'s own shape) -- <see cref="_appearanceSettings"/>'s
    /// own <see cref="IAppearanceSettingsService.DecodingIndicatorBlinksChanged"/> subscription
    /// (constructor, above) covers every change AFTER this instance exists; this covers the value as
    /// it already was when this instance was constructed. Re-runs <see cref="OnRxAudioLevelTick"/>
    /// immediately once the real value lands (auditor-class finding, applied proactively this time --
    /// see <see cref="RadioStatusViewModel"/>'s own SsbAsPkt startup-ordering fix from the same
    /// session): this constructor's own synchronous initial <c>OnRxAudioLevelTick()</c> call runs
    /// BEFORE this async load resolves, so without the re-run, a decode already active at construction
    /// would blink for up to one 250ms poll interval using the field-initializer default instead of
    /// the real persisted preference.</summary>
    private async Task LoadDecodingIndicatorBlinksPreferenceSafeAsync()
    {
        try
        {
            var value = await _appearanceSettings.GetDecodingIndicatorBlinksAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                _decodingIndicatorBlinksEnabled = value;
                OnRxAudioLevelTick();
            });
        }
        catch (Exception ex)
        {
            Log.LoadDecodingIndicatorBlinksPreferenceFailed(_logger, ex);
        }
    }

    /// <summary>Live-update handler for <see cref="IAppearanceSettingsService.DecodingIndicatorBlinksChanged"/>
    /// -- fired by <c>OptionsWindowViewModel</c> on the UI thread already (a plain post-save
    /// in-process event raise, not a background-thread signal like <see cref="OnStateChanged"/>'s own
    /// poll-loop origin), but posted through <see cref="Dispatcher.UIThread"/> regardless for
    /// consistency with every other cross-component write to this instance's own fields. Re-runs
    /// <see cref="OnRxAudioLevelTick"/> immediately for the same reason
    /// <see cref="LoadDecodingIndicatorBlinksPreferenceSafeAsync"/> does -- an operator toggling this
    /// mid-decode should see the effect instantly, not up to 250ms later.</summary>
    private void OnDecodingIndicatorBlinksChanged(bool value) => Dispatcher.UIThread.Post(() =>
    {
        _decodingIndicatorBlinksEnabled = value;
        OnRxAudioLevelTick();
    });

    /// <summary>0-100 fill fraction for the meter bar -- a direct percentage of
    /// <see cref="RxAudioPeakLevel"/>'s <c>[0.0, 1.0]</c> range, clamped defensively (that range
    /// should already be a hard guarantee for <c>SignalPeakLevel</c>).</summary>
    public double RxLevelFillPercent => Math.Clamp(RxAudioPeakLevel, 0.0, 1.0) * 100.0;

    /// <summary>"62" -- a linear amplitude fraction, not a dB value, so a plain 0-100 number is this
    /// meter's display unit. No "%" suffix, per direct user request (it read as redundant next to
    /// the bar itself).</summary>
    public string RxLevelDisplay => $"{RxLevelFillPercent:0}";

    /// <summary>Lower bound of the "good for SSTV decoding" band -- below this, the meter reads red
    /// (essentially no usable signal: wrong input device, unplugged cable, dead air). Deliberately
    /// NOT calibrated to "the level below which decode starts failing" -- no such line exists
    /// anywhere in this codebase or in legacy YONIQ to measure against (neither ever computed an
    /// SNR/decode-confidence estimate; legacy's own closest analogue, the VIS-sync squelch
    /// `m_SLvl`/`sstv.cpp:1795-1816`, operates on a Goertzel sync-tone magnitude, a different
    /// quantity this meter has no access to). User-directed 2026-09-03: FM-SSTV decodes reliably at
    /// levels far below what looked "alarmingly low" on the meter's own linear scale (e.g. a real
    /// user report: 3-4% raw input decoded a clean image) -- so this floor is set low enough to
    /// flag only a near-silent input, not a working-but-quiet one. A judgment call, not a measured
    /// threshold.</summary>
    private const double RxLevelTooLowThreshold = 0.01;

    /// <summary>True (green) when <see cref="RxAudioPeakLevel"/> is at or above the "something is
    /// actually there" floor AND <see cref="ISstvSessionService.IsLevelOverdriven"/> is false; false
    /// (red) otherwise. The too-hot side reuses <c>IsLevelOverdriven</c> -- legacy's own real,
    /// already-ported clipping-adjacent threshold (`AnalogFmSstvDecoder.cs`'s doc comment on that
    /// property, mirroring `Main.cpp:6174`'s `DrawLvl` at 75% of legacy's int16 scale) -- rather
    /// than a second, independently-calibrated raw-input percentage: one legacy-grounded "too hot"
    /// concept instead of two differently-calibrated ones (2026-09-03 redesign, same session as the
    /// too-low floor change above). <see cref="ISstvSessionService.IsLevelOverdriven"/> is read live
    /// here (not cached/polled into its own field) -- it changes on the same underlying decoder
    /// state <see cref="RxAudioPeakLevel"/>'s own poll already re-notifies this property from, so no
    /// separate notification wiring is needed.</summary>
    public bool RxLevelInGoodRange => RxAudioPeakLevel >= RxLevelTooLowThreshold && !_sstvSession.IsLevelOverdriven;

    [ObservableProperty]
    private double _tuneFrequencyHz = 1750;

    [ObservableProperty]
    private double _tuneDurationSeconds = 5;

    /// <summary>Toggle state for <see cref="TuneCommand"/> -- same shape as
    /// <see cref="OptionsWindowViewModel.IsTuning"/>, added per that class's own established Stop/
    /// cts safety pattern (plan-review finding, 2026-08-26): this command lets the OPERATOR pick the
    /// duration, up to <c>ISstvSessionService</c>'s own 5-minute safety backstop, so it needs the
    /// same start/stop toggle and window-close cancellation that dialog already has -- shipping the
    /// general-purpose Tune with weaker transmitter safety than the fixed-30s AFC one would be
    /// backwards.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TuneButtonLabel))]
    private bool _isTuning;

    public string TuneButtonLabel => _localization.GetString(IsTuning ? "RadioStatus.Tune.Stop" : "RadioStatus.Tune");

    private CancellationTokenSource? _tuneCts;

    /// <summary>Backs the Transceiver card's Receiving/Stop TX toggle -- real state, mirrors
    /// <see cref="ISstvSessionService.IsReceiving"/> exactly (including the case where a startup
    /// auto-start silently failed for lack of an audio device).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceivingButtonLabel), nameof(IsReceivingIdle))]
    private bool _isReceiving;

    /// <summary>User-reported gap (2026-09-18): "Receiving" (green) was the only state -- an
    /// operator in the Transmit/Gallery/Logbook tab had no way to tell an image was actually
    /// decoding right now versus the app merely listening for one. True while
    /// <see cref="IReceivedImageBuffer.Progress"/> is a real in-progress fraction (not
    /// <see langword="null"/>, and strictly less than <c>1.0</c> -- that exact value marks
    /// completion, not an ongoing decode, per that property's own doc comment). Polled on
    /// <see cref="_rxAudioLevelTimer"/> (same 250ms cadence as <see cref="RxAudioPeakLevel"/>,
    /// reusing its own tick rather than a second timer) -- not event-driven off
    /// <see cref="ISstvSessionService.ModeDetected"/>/<see cref="ISstvSessionService.DecodeRestarted"/>,
    /// since both fire synchronously on the decoder's own audio-drain thread (that interface's own
    /// concurrency doc comment) and a plain UI-thread poll of a snapshot value avoids adding a new
    /// cross-thread marshal path for this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceivingButtonLabel), nameof(RxStatusBarText), nameof(IsReceivingIdle))]
    private bool _isDecodingImage;

    /// <summary>Drives the actual blink: true/false alternating every other <see cref="_rxAudioLevelTimer"/>
    /// tick (~500ms) while <see cref="IsDecodingImage"/> is true, forced back to
    /// <see langword="false"/> the instant it isn't (see the poll site in the constructor) -- both
    /// AXAML call sites (<c>RadioHeaderView.axaml</c>'s Transceiver toggle,
    /// <c>MainWindow.axaml</c>'s status-bar chip) bind their amber ".decoding"/"attentionTint"
    /// class to this, not to <see cref="IsDecodingImage"/> directly, so the color itself pulses
    /// rather than sitting solid.</summary>
    [ObservableProperty]
    private bool _isDecodingBlinkOn;

    private int _decodingBlinkTickCounter;

    /// <summary>Backs the Options > Appearance "Decoding indicator blinks" checkbox
    /// (<see cref="ScanlineStudio.UI.Settings.AppearanceSettings.DecodingIndicatorBlinks"/>, default
    /// <see langword="true"/>) -- read once at startup via <see cref="LoadDecodingIndicatorBlinksPreferenceSafeAsync"/>
    /// and kept live via <see cref="IAppearanceSettingsService.DecodingIndicatorBlinksChanged"/>
    /// (constructor-subscribed), since Options is a separate, independently-alive ViewModel that can
    /// change this while this instance is already running. Plain field, not an
    /// <c>[ObservableProperty]</c> -- nothing in AXAML binds to it directly, only
    /// <see cref="OnRxAudioLevelTick"/> reads it.</summary>
    private bool _decodingIndicatorBlinksEnabled = AppearanceSettings.DefaultDecodingIndicatorBlinks;

    /// <summary>User-reported gap (2026-09-15): the Receiving toggle's Content was a static loc
    /// string regardless of state, so unchecking it (which really does stop RX capture, see
    /// <see cref="OnIsReceivingChanged"/>) produced no visible feedback at all -- same
    /// bool-driven-label pattern as <see cref="TuneButtonLabel"/>. <see cref="IsDecodingImage"/>
    /// takes priority over both other states (2026-09-18) -- a decode in progress necessarily
    /// means <see cref="IsReceiving"/> is also true, so checking it first is both correct and
    /// sufficient, no three-way ambiguity.
    /// <para>User-requested (2026-09-19): "Transmitting" now takes priority over ALL other states,
    /// checked via <see cref="IsTransmittingDisplay"/> -- see that property's own doc comment for why
    /// it is NOT just <see cref="IsCapturePausedForTx"/> alone.</para></summary>
    public string ReceivingButtonLabel => _localization.GetString(
        IsTransmittingDisplay ? "RadioStatus.Transmitting"
        : IsDecodingImage ? "RadioStatus.Decoding"
        : IsReceiving ? "RadioStatus.Receiving"
        : "RadioStatus.ReceivingMuted");

    /// <summary>The status-bar RX chip's own label (2026-09-18) -- separate from
    /// <see cref="ReceivingButtonLabel"/> because that chip's rest/receiving states use a
    /// DIFFERENT loc key ("MainWindow.StatusBar.Rx") than the Transceiver toggle's own
    /// ("RadioStatus.Receiving") despite showing the same word today; keeping them as two
    /// independently-keyed strings preserves that separation instead of silently coupling the two
    /// surfaces' text. Same 2026-09-19 "Transmitting" addition as <see cref="ReceivingButtonLabel"/>,
    /// for the same reason -- see that property's own doc comment.</summary>
    public string RxStatusBarText => _localization.GetString(
        IsTransmittingDisplay ? "MainWindow.StatusBar.Transmitting"
        : IsDecodingImage ? "MainWindow.StatusBar.Decoding"
        : "MainWindow.StatusBar.Rx");

    /// <summary>Code-review finding (yoniq-auditor, 2026-09-19, same day as the "Transmitting" label
    /// addition): <see cref="IsCapturePausedForTx"/> ALONE misses a real, reachable case -- it only
    /// fires when a genuinely RUNNING capture got paused (see its own event's doc comment), so
    /// starting a Transmit/Tune while <see cref="IsReceiving"/> is already off (RX Muted) never
    /// raises it at all, and <see cref="ReceivingButtonLabel"/>/<see cref="RxStatusBarText"/> would
    /// silently stay stuck on "RX Muted" for the entire transmission -- the exact case the removed
    /// TX KEYED chip used to still cover (on rigs with PTT readback). OR'd with
    /// <see cref="IsSstvTransmitting"/> (a polled fallback covering that gap unconditionally) closes
    /// it: <see cref="IsCapturePausedForTx"/> gives a near-instant, event-driven update for the common
    /// case (RX running), and <see cref="IsSstvTransmitting"/> guarantees correctness (within one
    /// ~250ms poll tick) for the RX-halted case neither this property nor the old chip depended on
    /// each other to cover alone.</summary>
    public bool IsTransmittingDisplay => IsCapturePausedForTx || IsSstvTransmitting;

    /// <summary>Polled fallback for <see cref="IsTransmittingDisplay"/> -- see that property's own
    /// doc comment for why <see cref="IsCapturePausedForTx"/> alone isn't enough. Mirrors
    /// <see cref="ISstvSessionService.IsTransmitting"/> ("genuinely in flight right now ... regardless
    /// of prior RX state," that property's own doc comment), polled on the same
    /// <see cref="_rxAudioLevelTimer"/> tick as <see cref="RxAudioPeakLevel"/>/<see cref="IsDecodingImage"/>
    /// rather than a dedicated event -- the underlying property is a plain, thread-safe read with no
    /// matching changed-event to subscribe to instead.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceivingButtonLabel), nameof(RxStatusBarText), nameof(IsTransmittingDisplay))]
    private bool _isSstvTransmitting;

    /// <summary>True exactly when the plain green "Receiving" look applies -- <see cref="IsReceiving"/>
    /// with an active decode NOT already claiming the amber "Decoding" look instead. Both AXAML
    /// call sites bind their green ".healthyTint"/"active" classes to this instead of
    /// <see cref="IsReceiving"/> directly, so the two looks stay mutually exclusive by construction
    /// rather than relying on style declaration order to arbitrate a simultaneous true/true.</summary>
    public bool IsReceivingIdle => IsReceiving && !IsDecodingImage;

    private bool _suppressReceivingCommand;

    /// <summary>Real state: mirrors <see cref="IRadioSessionService.IsGenuinelyConnected"/>, refreshed
    /// on every <see cref="IRadioSessionService.ConnectionEvents"/> transition except
    /// <see cref="RadioConnectionState.CommandFailed"/> (that state means a single command failed while
    /// the connection itself stays healthy, per that enum's own doc comment; it must not flip this
    /// indicator off). Not the same thing as matching <c>evt.State == Connected</c> directly --
    /// <c>Connected</c> can fire twice for one real connection (once optimistically, the instant a
    /// backend resolves; again, only once a poll actually confirms it -- see
    /// <see cref="IRadioController.IsGenuinelyConnected"/>'s own doc comment) and reading the live
    /// latch instead of the event's own state field is what lets this correctly go dark for the first
    /// one and relight for the second, including after a genuine recovery from
    /// <see cref="RadioConnectionState.Reconnecting"/>/<see cref="RadioConnectionState.Failed"/>.</summary>
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
    [NotifyPropertyChangedFor(nameof(VfoKickerDisplay))]
    private bool _isKeyed;

    /// <summary>VFO card kicker text -- just "RX"/"TX", reflecting <see cref="IsKeyed"/> for real.
    /// ui_transition_plan.md step 11 (T2-1): the "VFO A" prefix this used to carry was dropped
    /// outright -- no dual-VFO/memory-channel concept exists anywhere in
    /// <see cref="IRadioSessionService"/> to back a real "A" (the former "M1" segment was dropped
    /// for the same reason, stub sweep 2026-08-26), and no CAT backend this app supports (Hamlib,
    /// rigctld, flrig) reports which VFO is actually active, so claiming "A" specifically would be
    /// fabricated, not just imprecise. Add it back for real only if a CAT layer ever exposes the
    /// active VFO -- don't guess "A" as a placeholder in the meantime.</summary>
    public string VfoKickerDisplay => _localization.GetString(
        IsKeyed ? "RadioStatus.VfoCaptionTx" : "RadioStatus.VfoCaptionRx");

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
    /// a manual Halt click.
    /// <para>User-requested (2026-09-19): also drives <see cref="ReceivingButtonLabel"/>/
    /// <see cref="RxStatusBarText"/>'s "Transmitting" text now (via <see cref="IsTransmittingDisplay"/>),
    /// not just the Opacity dim -- see that property's own doc comment for why this signal alone
    /// isn't sufficient by itself.</para></summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReceivingButtonLabel), nameof(RxStatusBarText), nameof(IsTransmittingDisplay))]
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

    /// <summary>VFO card's "BW" pill -- Tier 4 stub-removal item (2026-08-26). This project's CAT
    /// layer can't universally read/set filter bandwidth: Hamlib and rigctld both genuinely support
    /// it (their `rig_get/set_mode`/`m`/`M` calls already carry a passband value), flrig deliberately
    /// does not (see <c>FlrigClientProtocol.SetBandwidthAsync</c>'s own doc comment for why -- its
    /// readback isn't reliably convertible to Hz). <see cref="CanReadBandwidth"/>/
    /// <see cref="CanSetBandwidth"/> below gate the display/edit controls per-backend, refreshed from
    /// <see cref="IRadioSessionService.Capabilities"/> on every successful poll (<see cref="OnStateChanged"/>)
    /// AND seeded in the constructor -- <see cref="OnStateChanged"/> alone only fires on a successful
    /// poll, which would otherwise leave these stuck at their constructor-time value for the whole
    /// duration of a reconnect-backoff window (same staleness class <see cref="IsKeyed"/>/
    /// <see cref="RigMetersDisplay"/> are already fixed for -- see the <c>!CatLinked</c> clearing arm
    /// in <see cref="OnConnectionEvent"/>, which resets these two right alongside them).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetBandwidthCommand))]
    private bool _canReadBandwidth;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetBandwidthCommand))]
    private bool _canSetBandwidth;

    /// <summary>Read-only, populated ONLY from the next poll's <see cref="RadioState.BandwidthHz"/> --
    /// never set optimistically from <see cref="BandwidthInputHz"/> on <see cref="SetBandwidthCommand"/>
    /// (plan-review risk: a rig can silently snap an arbitrary requested Hz to its nearest real
    /// filter -- e.g. several Icom models via Hamlib -- so echoing the user's raw request back would
    /// lie about what the rig actually did). "BW —" when unsupported, not yet polled, or the rig
    /// reports Hamlib's <c>RIG_PASSBAND_NORMAL</c> sentinel (<see cref="RadioState.BandwidthHz"/>'s
    /// own null-vs-real-0 convention) -- carries its own "BW " label prefix (code-review finding: the
    /// stub pill this replaces read "BW —", a bare value here read as unlabeled against the SPLIT/RIT
    /// pills sharing the same row).</summary>
    [ObservableProperty]
    private string _bandwidthDisplay;

    /// <summary>Staged, NOT live two-way bound to the rig -- same "explicit apply" shape as
    /// <see cref="TuneFrequencyHz"/>/<see cref="TuneCommand"/> above (plan-review finding: a
    /// live-bound Hz field would fight this VM's own 250ms poll write-back the way a naively-bound
    /// numeric field would, unlike a 3-way segment toggle where a poll write-back is idempotent).
    /// 2400 Hz default confirmed sound by an auditor domain-judgment review, 2026-09-03 (not a
    /// legacy port -- legacy YONIQ has no CAT bandwidth control at all to be equivalent to): this
    /// app can only ever set WIDTH via CAT, never the rig's own passband CENTER/shift
    /// (<see cref="IRadioSessionService.SetBandwidthAsync"/>'s own doc comment -- no shift/passband-
    /// center command exists anywhere in this codebase's CAT layer), so a wide-ish default is what
    /// actually tolerates an unknown/uncontrolled center offset -- narrowing below ~2400 Hz without
    /// also shifting risks clipping SSTV's own ~2300 Hz white tone at the filter's skirt, a worse
    /// result than the extra QRM a wider filter admits. 2400 Hz also maps to a filter width most HF
    /// rigs genuinely have as a real preset, unlike an oddball wider value a CAT backend might
    /// silently re-snap.</summary>
    [ObservableProperty]
    private double _bandwidthInputHz = 2400;

    /// <summary>User-directed 2026-09-03: quick-pick presets alongside the still-freely-editable
    /// <see cref="BandwidthInputHz"/> field (same "ItemsSource + IsEditable ComboBox, Text bound to
    /// the real numeric property" pattern already established for
    /// <c>OptionsWindowViewModel.AvailableSampleRates</c>/<c>SampleRate</c> -- an out-of-list value
    /// still round-trips, this is a convenience list, not a validation constraint). 1800/2400/2800 Hz
    /// bracket the practical range a ham operator would actually reach for: 1800 Hz is the narrowed
    /// choice some operators use (normally paired with IF shift, which this app can't send -- see
    /// <see cref="BandwidthInputHz"/>'s own doc comment for why that makes 1800 Hz a riskier pick
    /// here specifically), 2400 Hz is the recommended default, 2800 Hz is a wider standard SSB
    /// filter width for operators who'd rather trade QRM rejection for zero clipping risk.</summary>
    public IReadOnlyList<double> AvailableBandwidthPresetsHz { get; } = [1800, 2400, 2800];

    /// <summary>Raw Hz mirror of <see cref="FrequencyDisplay"/> -- that property is a formatted
    /// string, not round-trippable, so <see cref="StoreCurrentPresetAsync"/> needs its own copy of
    /// the last <see cref="RadioState.FrequencyHz"/> to build a <see cref="FrequencyPreset"/> from.</summary>
    private long _currentFrequencyHz;

    /// <summary>Raw mirror of the last polled <see cref="RadioState.BandwidthHz"/>, same reason as
    /// <see cref="_currentFrequencyHz"/> above -- <see cref="BandwidthDisplay"/> is formatted text.
    /// <see langword="null"/> when the backend can't read a bandwidth, or when it reported Hamlib's
    /// <c>RIG_PASSBAND_NORMAL</c> sentinel.</summary>
    private int? _currentBandwidthHz;

    /// <summary>Set once by <see cref="MainViewModel"/>'s own constructor, right after both VMs
    /// exist, mirroring the existing reverse reference (<see cref="TxControlsPaneViewModel.RadioStatus"/>,
    /// see that property's own doc comment for why a plain reference is this codebase's established
    /// pattern here rather than DI or an XAML ancestor lookup). Lets the Transceiver card's "Stop TX"
    /// button (<see cref="RadioHeaderView"/>) bind straight to <see cref="TxControlsPaneViewModel.StopTransmitCommand"/>
    /// -- the actual transmit-cancel logic stays owned by the pane that started the transmission,
    /// this VM just needs a way to reach it.</summary>
    public TxControlsPaneViewModel? TxControls { get; set; }

    public RadioStatusViewModel(IRadioSessionService radioSession, ISstvSessionService sstvSession, ILocalizationService localization, IAppearanceSettingsService appearanceSettings, ILogger<RadioStatusViewModel> logger)
    {
        _radioSession = radioSession;
        _sstvSession = sstvSession;
        _localization = localization;
        _appearanceSettings = appearanceSettings;
        _logger = logger;
        _frequencyDisplay = localization.GetString("RadioStatus.NoFrequency");
        // T1-13 (production_audit.md): was a hardcoded "BW —" field initializer -- moved into the
        // constructor body, same reasoning as _frequencyDisplay just above (a field initializer runs
        // before _localization is assigned).
        _bandwidthDisplay = localization.GetString("RadioStatus.BandwidthUnavailable");
        _modeDisplay = string.Empty;
        _isReceiving = sstvSession.IsReceiving;
        _catLinked = radioSession.IsGenuinelyConnected;
        _canReadBandwidth = radioSession.Capabilities.HasFlag(RadioCapabilities.ReadBandwidth);
        _canSetBandwidth = radioSession.Capabilities.HasFlag(RadioCapabilities.SetBandwidth);
        PresetModeChoices = RadioModeFamilies.PresetModes
            .Select(mode => new RadioModeChoice(mode, localization.GetString($"RadioMode.{mode}")))
            .ToList();

        radioSession.StateChanges.Subscribe(OnStateChanged);
        radioSession.ConnectionEvents.Subscribe(OnConnectionEvent);
        if (radioSession.LastKnownState is { } state)
        {
            OnStateChanged(state);
        }

        appearanceSettings.DecodingIndicatorBlinksChanged += OnDecodingIndicatorBlinksChanged;
        _ = LoadDecodingIndicatorBlinksPreferenceSafeAsync();

        sstvSession.MaintenanceWarningRaised += OnMaintenanceWarningRaised;
        sstvSession.MaintenanceWarningCleared += OnMaintenanceWarningCleared;
        sstvSession.MaintenanceCriticalStopRaised += OnMaintenanceCriticalStopRaised;
        sstvSession.CapturePausedForTransmitChanged += OnCapturePausedForTransmitChanged;

        _ = LoadPresetsSafeAsync();
        _ = LoadTxStateSafeAsync();
        _ = LoadSsbAsPktPreferenceSafeAsync();

        UpdateUtcClock();
        _utcClockTimer = new DispatcherTimer(UtcClockTickInterval, DispatcherPriority.Background, (_, _) => UpdateUtcClock());
        _utcClockTimer.Start();

        // Always running, not gated on IsReceiving -- same "no Start/Stop pairing, runs for this
        // ViewModel's whole lifetime" shape as RxImagePaneViewModel's own telemetry timer. Reads 0
        // while not actually capturing because StopReceivingLockedAsync calls _decoder.ResetAgc()
        // on RX stop (SstvSessionService.cs:2244, zeroes LevelAgc._curMax) -- which is the correct
        // "meter shows silence" state, not a special case to gate around. Code-review nit
        // (2026-09-03): unlike RawInputPeakLevel (explicitly zeroed at SstvSessionService.cs:2142),
        // SignalPeakLevel has no capture-state awareness of its own -- this comment records WHERE
        // the zeroing actually comes from so a future change to ResetAgc's own call site doesn't
        // silently break this meter's idle-state behavior with nothing here to explain why.
        // Calls the tick body directly (not just the RxAudioPeakLevel assignment it used to be)
        // so IsDecodingImage/IsDecodingBlinkOn also read correctly from construction, not just
        // after the first real 250ms timer tick.
        OnRxAudioLevelTick();
        _rxAudioLevelTimer = new DispatcherTimer(RxAudioLevelPollInterval, DispatcherPriority.Background, (_, _) => OnRxAudioLevelTick());
        _rxAudioLevelTimer.Start();

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
        // introduces -- not fixed here. A no-op when the earlier attempt already succeeded: the
        // `!_isReceiving` guard above skips the property set entirely, so OnIsReceivingChanged never
        // fires and no redundant StartReceivingAsync call happens.
        //
        // T0-1 correction (production_audit.md): "already succeeded" is no longer the overwhelmingly
        // common case this comment used to claim. Program.cs's own StartReceivingAsync launch attempt
        // now runs backgrounded (Task.Run), not synchronously before this ViewModel is constructed --
        // so on a healthy launch, this retry now typically fires WHILE the background attempt is
        // still in flight, not just on genuine failure. SetReceivingFailed's own catch handles that
        // race by re-syncing from _sstvSession.IsReceiving rather than assuming failure -- see that
        // method's own comment.
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

        // Live-locale-switch review (2026-09-20): this VM never unsubscribes from ANY of its many
        // event subscriptions above (StateChanges/ConnectionEvents/appearance/sstvSession) -- it's a
        // DI-singleton pane resolved exactly once (RadioStatusViewModel.cs's own sole `new` site is
        // MainViewModel.cs), so a permanent subscription here is consistent with the rest of this
        // constructor, not a new leak risk. Last statement, deliberately -- see MainViewModel's own
        // identical reasoning for why (a throw earlier in this constructor must not leave a
        // half-constructed instance subscribed).
        localization.CultureChanged += OnCultureChanged;
    }

    /// <summary>Most of this VM's ~30 GetString-backed members are computed get-only properties
    /// (e.g. <see cref="ReceivingButtonLabel"/>, <see cref="RxStatusBarText"/>), which
    /// <c>OnPropertyChanged(string.Empty)</c> alone re-evaluates correctly. A handful are STORED
    /// strings, only ever (re)computed inside <see cref="OnStateChanged"/> from raw state this VM
    /// already caches (<see cref="_currentFrequencyHz"/>/<see cref="_currentBandwidthHz"/>) -- those
    /// need explicit re-derivation here, using the SAME branch conditions <see cref="OnStateChanged"/>
    /// itself uses, so a disconnected rig doesn't get a stale connected-looking value re-painted in
    /// the new language. <see cref="MaintenanceMessage"/>/error-style stored strings are accepted as
    /// known-stale until their next triggering event, same as everywhere else in this codebase.</summary>
    private void OnCultureChanged()
    {
        OnPropertyChanged(string.Empty);

        FrequencyDisplay = _currentFrequencyHz > 0
            ? _localization.GetString("RadioStatus.FrequencyDisplayFormat", $"{_currentFrequencyHz / 1_000_000.0:0.000000}")
            : _localization.GetString("RadioStatus.NoFrequency");

        // Mirrors OnStateChanged's own !CatLinked reset: a stale _currentBandwidthHz from before a
        // disconnect must not repaint as if still live (that reset exists precisely to avoid the
        // "BW pill shows its last live reading against a dead connection" bug this would otherwise
        // reintroduce).
        BandwidthDisplay = CanReadBandwidth && _currentBandwidthHz is { } bandwidthHz
            ? _localization.GetString("RadioStatus.BandwidthDisplayFormat", bandwidthHz)
            : _localization.GetString("RadioStatus.BandwidthUnavailable");

        UpdateUtcClock();
    }

    private void UpdateUtcClock() => UtcClockDisplay = _localization.GetString("RadioStatus.UtcValueFormat", DateTimeOffset.UtcNow);

    /// <summary>Give-up-after-5 feature: fired once per give-up, carrying the already-localized
    /// message text -- the must-acknowledge popup (direct user feedback: "should be a popup window,
    /// not a tiny text under the VFO" -- this used to feed a dismissible toast, then a persistent
    /// header text line alongside the popup, before the header line was removed too; see
    /// <c>MainWindow.axaml.cs</c>'s own handler for the modal it opens). Raised from the same
    /// <see cref="Dispatcher.UIThread.Post"/> callback as every other cross-thread signal in this
    /// class.</summary>
    public event Action<string>? ConnectionGaveUp;

    /// <summary>Stub survey Tier 2 (2026-08-26) -- same view-model-never-touches-a-Window pattern as
    /// <see cref="ConnectionGaveUp"/>/<c>MainViewModel.OptionsRequested</c>; <c>MainWindow.axaml.cs</c>
    /// owns the actual window construction/<c>ShowDialog</c> call. Parameterless: the Favourites
    /// Editor's DataContext is THIS live instance, not a fresh child view-model.</summary>
    public event Action? FavouritesEditorRequested;

    /// <summary>Same shape as <see cref="FavouritesEditorRequested"/>.</summary>
    public event Action? ToneGeneratorRequested;

    [RelayCommand]
    private void OpenFavouritesEditor()
    {
        // Reload from persisted state on open (plan-review finding, 2026-08-26): EditorRows is live
        // shared state with no rollback -- without this, a prior session's un-Saved Add/Remove/edit
        // would keep showing indefinitely instead of reflecting what's actually on disk.
        _ = LoadPresetsSafeAsync();
        FavouritesEditorRequested?.Invoke();
    }

    [RelayCommand]
    private void OpenToneGenerator() => ToneGeneratorRequested?.Invoke();

    /// <summary>The Favourites editor's mode picker, built once here because this view-model already
    /// holds the localization service and constructs every editor row itself. Six modes only -- AM,
    /// CW and RTTY aren't used for SSTV, and <see cref="RadioMode.Unknown"/> is a readback sentinel
    /// with no entry in any backend's mode map, so selecting it would throw out of
    /// <c>SetModeAsync</c>.</summary>
    public IReadOnlyList<RadioModeChoice> PresetModeChoices { get; }

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
            // T1-13 (production_audit.md): "MHz" used to be a hardcoded English literal, violating
            // CLAUDE.md's no-hardcoded-UI-strings rule regardless of the decimal formatting itself
            // (see FormatRigMeters's own doc comment below for why the DECIMAL formatting stays
            // ambient-culture, deliberately -- only the unit word needed a localization key).
            // Interpolation, not ToString(format) -- CA1305 (error) flags an explicit ambient-culture
            // ToString(format) call but not interpolation, matching this codebase's existing dodge.
            FrequencyDisplay = _localization.GetString("RadioStatus.FrequencyDisplayFormat", $"{state.FrequencyHz / 1_000_000.0:0.000000}");
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

            SyncSsbAsPktFromPolledMode(state.Mode);

            IsKeyed = state.IsTransmitting;
            RigMetersDisplay = FormatRigMeters(state);

            CanReadBandwidth = _radioSession.Capabilities.HasFlag(RadioCapabilities.ReadBandwidth);
            CanSetBandwidth = _radioSession.Capabilities.HasFlag(RadioCapabilities.SetBandwidth);
            // "BW " prefix (code-review finding): the stub pill this replaces read "BW —", and its
            // row-mates (SPLIT/RIT) keep their own label prefix -- a bare "2400 Hz" here read as an
            // unlabeled value against those neighbors.
            // T1-13 (production_audit.md): both branches used to be hardcoded English literals.
            // Raw mirror alongside _currentFrequencyHz: BandwidthDisplay is a formatted string, so
            // StoreCurrentPresetAsync can't round-trip it back into a preset.
            _currentBandwidthHz = state.BandwidthHz;
            BandwidthDisplay = state.BandwidthHz is { } bandwidthHz
                ? _localization.GetString("RadioStatus.BandwidthDisplayFormat", bandwidthHz)
                : _localization.GetString("RadioStatus.BandwidthUnavailable");
        });
    }

    /// <summary>Split out from <see cref="OnStateChanged"/> so it's unit-testable without a real
    /// <see cref="Dispatcher.UIThread"/> pump around it (same reasoning precedent as
    /// <c>TxImageEditorPaneView.ComputeElementResize</c>'s own doc comment: pure formatting logic
    /// doesn't need cross-thread machinery wrapped around it just to verify). Invariant-culture: this
    /// is a live UI readout, not a persisted/round-tripped value, so culture-formatted decimals are
    /// fine here (unlike <c>MacroTextResolver.FormatFrequency</c>'s own baked-into-the-transmitted-
    /// image reasoning for InvariantCulture, which doesn't apply to a screen-only readout). T1-13
    /// (production_audit.md): the "SWR"/"ALC"/"PWR" labels used to be hardcoded English literals --
    /// only that half needed a localization key; the decimal formatting above is deliberately
    /// unchanged. No longer static -- needs <see cref="_localization"/>.</summary>
    private string FormatRigMeters(RadioState state)
    {
        List<string> parts = [];
        if (state.SwrRatio is { } swr)
        {
            parts.Add(_localization.GetString("RadioStatus.Meters.SwrFormat", $"{swr:0.0}"));
        }

        if (state.AlcLevel is { } alc)
        {
            parts.Add(_localization.GetString("RadioStatus.Meters.AlcFormat", $"{alc:0}"));
        }

        if (state.PowerPercent is { } power)
        {
            parts.Add(_localization.GetString("RadioStatus.Meters.PwrFormat", $"{power:0}"));
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
            // Give-up-after-5 feature: a Disconnected event with a non-null Reason means
            // RadioController's own poll loop gave up automatically, not a real Disconnect click
            // (always reason: null) -- kept separate from ErrorMessage. giveUpMessage's own
            // ConnectionGaveUp?.Invoke is deferred to the END of this callback (code-review
            // finding) -- ConnectionGaveUp's subscriber is arbitrary external code
            // (MainWindow.axaml.cs's popup handler) that this class cannot guarantee won't throw;
            // invoking it before the CatLinked/IsKeyed/RigMetersDisplay staleness resets below would
            // let such a throw abort this whole callback, leaving the header claiming a live link
            // with stale keyed/meter readings right after the very give-up this feature exists to
            // surface.
            string? giveUpMessage = null;
            if (evt.State == RadioConnectionState.Disconnected && evt.Reason is not null)
            {
                giveUpMessage = _localization.GetString("Options.Radio.Connect.GaveUp", evt.Reason);
            }

            // Reads the live latch, not evt.State directly -- Connected can now fire twice for one
            // real connection (see CatLinked's own doc comment), and this is what lets CatLinked go
            // dark for the first, optimistic one and relight only for the genuinely-confirmed second.
            CatLinked = _radioSession.IsGenuinelyConnected;

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
            // Auditor-caught (2026-08-18, RX signal-strength meter review): RigMetersDisplay had the
            // exact same staleness gap as IsKeyed above -- only ever refreshed by OnStateChanged, so
            // without this a rig that read "SWR 1.2 · PWR 75%" when the link dropped would keep
            // showing that live-looking reading indefinitely with nothing behind it. Same
            // CommandFailed-only exemption as IsKeyed.
            if (!CatLinked)
            {
                IsKeyed = false;
                RigMetersDisplay = "—";

                // Same staleness gap as IsKeyed/RigMetersDisplay above: Capabilities also drops to
                // None during reconnect backoff (RadioController.Capabilities' own doc comment), and
                // OnStateChanged (the only other place these are refreshed) doesn't fire again until
                // a poll genuinely succeeds -- without this, a lost link would leave the BW pill
                // showing its last live reading and an Apply button a user could still click into a
                // dead connection.
                CanReadBandwidth = false;
                CanSetBandwidth = false;
                BandwidthDisplay = _localization.GetString("RadioStatus.BandwidthUnavailable");
            }

            if (giveUpMessage is not null)
            {
                ConnectionGaveUp?.Invoke(giveUpMessage);
            }
        });
    }

    /// <summary>Unguarded fire-and-forget from the constructor before this wrap was added -- a
    /// failure here (e.g. settings store not reachable yet) would have thrown on a thread nothing
    /// observes, an unlogged latent crash risk.
    ///
    /// Configurations-preset backlog, Phase 4 (2026-08-28): made public (was private) -- the
    /// Configurations dialog's own post-switch refresh needs to re-run this, since a preset switch can
    /// change <c>FrequencyPresetsSettings</c> (the header's M1-M7 favourite buttons) and this was the
    /// ONLY thing that ever rebuilt <see cref="Presets"/>/<see cref="PresetVisibility"/> from settings
    /// before now, previously reachable only from this VM's own constructor and the Favourites-editor
    /// open path -- neither of which a preset switch goes anywhere near.</summary>
    public async Task LoadPresetsSafeAsync()
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

    /// <summary>Same reasoning as <see cref="LoadPresetsSafeAsync"/>. Loads Pwr's real app-settings
    /// gain AND the TX device's real OS mute state independently -- one failing (or reading as its
    /// own default) must not prevent the other from loading. RX has no equivalent load: the level
    /// meter is driven entirely by <see cref="_rxAudioLevelTimer"/>, not a one-time load.</summary>
    private async Task LoadTxStateSafeAsync()
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

        // Mute has no persist-on-change handler (read-only, see TxIsMuted's own doc comment) -- no
        // _suppressVolumePersist dance needed here, a plain Post is enough.
        try
        {
            var muted = await _sstvSession.GetTxDeviceMutedAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => TxIsMuted = muted);
        }
        catch (Exception ex)
        {
            Log.LoadTxMuteFailed(_logger, ex);
        }
    }

    private void RebuildPresetCollections(IReadOnlyList<FrequencyPreset> presets)
    {
        Presets.Clear();
        EditorRows.Clear();
        foreach (var preset in presets)
        {
            Presets.Add(new FrequencyPresetButtonViewModel(preset, ApplyPresetCommand));
            EditorRows.Add(new FrequencyPresetEditorRowViewModel(preset, RemovePresetRowCommand, PresetModeChoices));
        }
    }

    /// <summary>ui_transition_plan.md step 11 (T2-1): a plausible RADIO range, not a protocol/rig-
    /// specific limit -- code-review correction: an amateur-transceiver-only range (0.1-1500 MHz)
    /// was too tight for what this app's own Hamlib backend actually enumerates
    /// (<see cref="ScanlineStudio.Core.Radio.Hamlib.HamlibDiscoveryService"/> walks EVERY Hamlib rig
    /// model with no transceiver-only curation -- real, in-use wideband receivers like the IC-R8600
    /// (10 kHz-3 GHz) or SDR backends easily exceed both ends). Widened to 1 kHz-30 GHz: still
    /// catches the actual failure mode this guard exists for (a raw Hz value like "14230000" typed
    /// into an MHz field, ~4 orders of magnitude over any real antenna/rig range) without rejecting
    /// real hardware this app supports. A rig's own real range (if narrower) still rejects
    /// out-of-band values as a genuine <see cref="RadioProtocolException"/> -&gt;
    /// <see cref="ErrorMessage"/>, same as <see cref="SetBandwidthAsync"/>'s own doc comment already
    /// accepts for bandwidth.</summary>
    private const double MinPlausibleFrequencyMhz = 0.001;

    private const double MaxPlausibleFrequencyMhz = 30_000.0;

    /// <summary>Code-review finding: a slow CAT backend (flrig's own frequency-set verifies via a
    /// readback poll loop, up to ~2.65s) can complete well after the operator cancelled that edit
    /// and opened a NEW one -- without this, the stale completion's own
    /// <c>Dispatcher.UIThread.Post</c> (success OR failure) would close/error the WRONG, unrelated,
    /// still-in-progress edit session, discarding whatever the operator had already typed into it.
    /// UI-thread-only (every read/write happens on a RelayCommand invocation or inside a
    /// Dispatcher.UIThread.Post callback, never a background thread), so a plain int is sufficient --
    /// no Interlocked needed, unlike this class' own cross-thread request latches in
    /// ScanlineStudio.Core.Sstv.</summary>
    private int _frequencyEditSessionId;

    [RelayCommand]
    private async Task SetFrequencyAsync()
    {
        if (!double.TryParse(FrequencyInputMhz, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz)
            || !double.IsFinite(mhz) || mhz is < MinPlausibleFrequencyMhz or > MaxPlausibleFrequencyMhz)
        {
            // Stays in edit mode (IsEditingFrequency unchanged) -- an invalid entry is rejected
            // inline so the operator can see why and correct it, not silently reverted.
            ErrorMessage = _localization.GetString("RadioStatus.Error.ImplausibleFrequency");
            return;
        }

        // Captured BEFORE the first await, on the UI thread -- see _frequencyEditSessionId's own
        // doc comment for why a stale completion must not touch a DIFFERENT, later edit session.
        var session = _frequencyEditSessionId;
        // Also captured before the first await, same reasoning as ApplyPresetAsync's own
        // canSetBandwidth capture below.
        var canSetBandwidth = CanSetBandwidth;
        Log.SetFrequencyInvoked(_logger, mhz);
        try
        {
            ErrorMessage = null;
            // Math.Round, not a bare cast (Tier B audit finding -- the sibling SavePresetsAsync had
            // this exact fix already, auditor-caught 2026-08-11, but it was never applied here): the
            // mhz * 1_000_000 product can land 1 ULP below the target integer for some real radio
            // frequencies, and a bare (long) cast truncates that down to N-1 Hz instead of N.
            await _radioSession.SetFrequencyAsync((long)Math.Round(mhz * 1_000_000)).ConfigureAwait(false);
            // Success only -- exits the inline editor back to the plain readout. Posted, not a bare
            // assignment: this continuation can resume off the UI thread (ConfigureAwait(false)
            // above), same reasoning as the catch block's own Dispatcher.UIThread.Post immediately
            // below. Posted BEFORE the bandwidth reapply below (yoniq-auditor finding, 2026-09-18):
            // the frequency set already succeeded at this point, so a LATER bandwidth failure must
            // not leave the editor stuck open with a misleading "No radio connected" -- the rig HAS
            // retuned, only the filter-width reapply failed.
            Dispatcher.UIThread.Post(() =>
            {
                if (_frequencyEditSessionId == session)
                {
                    IsEditingFrequency = false;
                }
            });
            // User-reported 2026-09-18: some rigs recall a per-band filter default (e.g. a narrow CW
            // width) the moment a manually-typed frequency crosses into a new band, entirely on the
            // rig's own side -- this app never calls SetModeAsync here, so ApplyPresetAsync's own
            // "mode set resets the passband" guard doesn't apply, yet the symptom is the same.
            // Re-applying the BW pill's own staged value overrides whatever the rig just recalled,
            // same reapply this VM already does for ApplyPresetAsync (gated on the same capability for
            // the same reason -- flrig/OmniRig throw here rather than no-op).
            if (canSetBandwidth)
            {
                await _radioSession.SetBandwidthAsync((int)Math.Round(BandwidthInputHz)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.SetFrequencyFailed(_logger, mhz, ex);
            Dispatcher.UIThread.Post(() =>
            {
                if (_frequencyEditSessionId == session)
                {
                    ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected");
                }
            });
        }
    }

    /// <summary>Bound to the inline frequency-entry TextBox's own Escape handling
    /// (<c>RadioHeaderView.axaml.cs</c>) -- reverts without applying, same "Escape means cancel, no
    /// side effect" contract as every other inline-edit surface in this app
    /// (<c>OverlayElementViewModel.IsEditingText</c>'s own Escape branch).</summary>
    [RelayCommand]
    private void CancelEditFrequency()
    {
        _frequencyEditSessionId++;
        ErrorMessage = null;
        IsEditingFrequency = false;
    }

    /// <summary>Explicit-apply command backing the BW pill's staged <see cref="BandwidthInputHz"/>
    /// edit control -- gated by <see cref="CanSetBandwidth"/>, same shape as
    /// <see cref="SetFrequencyAsync"/> above. Sends the value as-typed; this port does not add its
    /// own narrow-bandwidth floor/clamp (a rig that rejects an out-of-range value already surfaces
    /// that as a real <see cref="RadioProtocolException"/> -&gt; <see cref="ErrorMessage"/>, same as any
    /// other invalid CAT command).</summary>
    [RelayCommand(CanExecute = nameof(CanSetBandwidth))]
    private async Task SetBandwidthAsync()
    {
        var hz = (int)Math.Round(BandwidthInputHz);
        Log.SetBandwidthInvoked(_logger, hz);
        try
        {
            ErrorMessage = null;
            await _radioSession.SetBandwidthAsync(hz).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.SetBandwidthFailed(_logger, hz, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    [RelayCommand]
    private async Task ApplyPresetAsync(FrequencyPreset preset)
    {
        // Code-review finding: a preset persisted before this validation existed, or hand-edited in
        // the settings file, could still carry an implausible frequency -- SavePresetsInternalAsync's
        // own new guard only protects presets saved AFTER this fix, not ones already on disk.
        var presetMhz = preset.FrequencyHz / 1_000_000.0;
        if (!double.IsFinite(presetMhz) || presetMhz is < MinPlausibleFrequencyMhz or > MaxPlausibleFrequencyMhz)
        {
            Log.ApplyPresetFailed(_logger, preset.Label, new InvalidOperationException("Implausible preset frequency."));
            // InvalidPresetFrequency, not ImplausibleFrequency (auditor nit): this fires from a
            // button click with nothing typed by the operator -- the direct-entry field's wording
            // ("Enter a frequency between...") doesn't fit; the preset-labeled wording does.
            ErrorMessage = _localization.GetString("RadioStatus.Error.InvalidPresetFrequency", preset.Label);
            return;
        }

        // Mode guard, BEFORE any CAT call. RadioMode.Unknown has no entry in any backend's mode map,
        // so SetModeAsync throws ArgumentOutOfRangeException for it -- and the catch below maps every
        // exception to "no radio connected", which the user would see AFTER SetFrequencyAsync had
        // already retuned the rig. A preset can hold Unknown without anyone picking it: "Store
        // current" copies whatever the rig reports, and an unmapped rig token resolves to Unknown.
        if (preset.Mode == RadioMode.Unknown)
        {
            Log.ApplyPresetFailed(_logger, preset.Label, new InvalidOperationException("Preset mode is not settable."));
            ErrorMessage = _localization.GetString("RadioStatus.Error.InvalidPresetMode", preset.Label);
            return;
        }

        // Captured before the first await: this method uses ConfigureAwait(false), so everything past
        // it resumes off the UI thread, and CanSetBandwidth is only ever written on the UI thread.
        var canSetBandwidth = CanSetBandwidth;
        var bandwidthHz = ResolvePresetBandwidth(preset);

        Log.ApplyPresetInvoked(_logger, preset.Label, preset.FrequencyHz, preset.Mode, bandwidthHz);
        try
        {
            ErrorMessage = null;
            await _radioSession.SetFrequencyAsync(preset.FrequencyHz).ConfigureAwait(false);
            await _radioSession.SetModeAsync(preset.Mode).ConfigureAwait(false);
            // After the mode, never before: setting the mode is what makes the rig fall back to its
            // own default passband for that mode, which is the whole reason this feature exists.
            // Gated on the capability because flrig and OmniRig THROW here rather than no-op'ing --
            // an ungated call would fail the click after the frequency and mode had already changed.
            if (canSetBandwidth && bandwidthHz is { } hz)
            {
                await _radioSession.SetBandwidthAsync(hz).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.ApplyPresetFailed(_logger, preset.Label, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.NoRadioConnected"));
        }
    }

    /// <summary>The width <see cref="ApplyPresetAsync"/> sends, or <see langword="null"/> to send no
    /// bandwidth command at all.
    ///
    /// <para>Checked against the PERSISTENCE range, never against the mode family's own band. A width
    /// the operator deliberately typed, saved, and can still see in the editor is applied as typed --
    /// 12000 Hz on a USB Favourite stays 12000 Hz. The family band drives only the editor's
    /// snap-on-mode-change, per <see cref="RadioModeFamilies"/>'s own doc comment.</para>
    ///
    /// <para>A stored value outside that range substitutes the family fallback instead of aborting
    /// the click, deliberately unlike the mode and frequency guards above: a bad bandwidth has a
    /// safe, correct substitute and a bad mode does not. The substitution is logged rather than
    /// surfaced -- the apply itself succeeded, and an error banner on a successful action misleads.
    /// A hand-edited <c>0</c> is the case that matters: <c>0</c> IS Hamlib's
    /// <c>RIG_PASSBAND_NORMAL</c>, so sending it would silently reinstate the rig-picks-its-own-width
    /// behaviour this feature replaces.</para></summary>
    private int? ResolvePresetBandwidth(FrequencyPreset preset)
    {
        if (RadioModeFamilies.FallbackFor(preset.Mode) is not { } fallback)
        {
            // No family: send nothing, even if a bandwidth is stored. A row on such a mode gets a
            // saveable seed so it can't block the editor, and that seed then persists -- without
            // this branch, a hand-edited AM Favourite would end up narrowing the rig to an SSB width.
            return null;
        }

        if (preset.BandwidthHz is not { } stored)
        {
            return fallback;
        }

        if (RadioModeFamilies.IsValidBandwidth(stored))
        {
            return stored;
        }

        Log.ApplyPresetBandwidthSubstituted(_logger, preset.Label, stored, fallback);
        return fallback;
    }

    /// <summary>Backs the Favourites card's "Store current" button -- adds the currently-tuned
    /// frequency/mode as a new preset and persists immediately. Deliberately does NOT reuse
    /// <see cref="SavePresetsCommand"/> alone (that command only re-persists whatever's already in
    /// <see cref="EditorRows"/>) -- it needs to append the current radio state as a genuinely new row
    /// first. Label is left empty, same precedent as <see cref="AddPresetRow"/>'s manual-add default:
    /// this command has no rename UI to fill it in either way (the Favourites Editor dialog does).
    /// Gated by <see cref="CanStoreCurrentPreset"/> (auditor-caught, 2026-08-11):
    /// <see cref="_currentFrequencyHz"/> is <c>0</c> until the first <see cref="OnStateChanged"/>
    /// call, which never fires with no radio connected/before the first poll -- without the guard
    /// this would silently persist a "0.000000 USB" preset the operator would then need to open the
    /// Favourites Editor dialog to delete.</summary>
    [RelayCommand(CanExecute = nameof(CanStoreCurrentPreset))]
    private async Task StoreCurrentPresetAsync()
    {
        Log.StoreCurrentPresetInvoked(_logger, _currentFrequencyHz, SelectedRadioMode);
        // Clamped here, never handed to SavePresets' validator raw: this is a live rig reading, and
        // an operator sitting on a filter outside the accepted range would otherwise have the save
        // aborted, the row removed, and the frequency/mode capture lost too -- over a value they
        // never typed. CanStoreCurrentPreset guarantees the mode has a family, so the ?? is
        // unreachable and present only to keep the expression total.
        var capturedBandwidth = _currentBandwidthHz is { } live && RadioModeFamilies.IsValidBandwidth(live)
            ? live
            : RadioModeFamilies.FallbackFor(SelectedRadioMode) ?? RadioModeFamilies.SsbFallbackHz;
        var row = new FrequencyPresetEditorRowViewModel(
            new FrequencyPreset(string.Empty, _currentFrequencyHz, SelectedRadioMode, capturedBandwidth),
            RemovePresetRowCommand,
            PresetModeChoices);
        EditorRows.Add(row);

        // Tier B audit finding: this used to call the SavePresetsCommand's own method and ignore
        // the outcome -- if the save failed, the just-added row stayed in EditorRows with nothing
        // ever removing it (SavePresetsInternalAsync's own failure path deliberately leaves
        // EditorRows untouched, matching its "report the error, don't discard unsaved edits"
        // contract), so a retry after the failure appended a SECOND row on top of the first,
        // persisting a duplicate preset the moment the save eventually succeeded. The Favourites
        // Editor dialog's own RemoveCommand can clean up a lingering failed-add row today, but this
        // explicit removal keeps the "Store current" button itself honest either way.
        if (!await SavePresetsInternalAsync().ConfigureAwait(false))
        {
            Dispatcher.UIThread.Post(() => EditorRows.Remove(row));
        }
    }

    /// <summary>The mode check is the second half of the gate, not decoration: this command copies
    /// whatever mode the rig reports, so parking the rig on CW, AM or RTTY -- or on a token no
    /// backend maps, which resolves to <see cref="RadioMode.Unknown"/> -- would otherwise create a
    /// Favourite the editor's own six-mode picker cannot display.</summary>
    private bool CanStoreCurrentPreset() =>
        _currentFrequencyHz > 0 && RadioModeFamilies.PresetModes.Contains(SelectedRadioMode);

    [RelayCommand]
    private void AddPresetRow()
    {
        Log.AddPresetRowInvoked(_logger);
        EditorRows.Add(new FrequencyPresetEditorRowViewModel(
            new FrequencyPreset(string.Empty, 14_230_000, RadioMode.Usb, RadioModeFamilies.SsbFallbackHz),
            RemovePresetRowCommand,
            PresetModeChoices));
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
            if (!double.TryParse(row.FrequencyMhzText, NumberStyles.Float, CultureInfo.InvariantCulture, out var mhz))
            {
                // Plan-review finding, 2026-08-26: this used to silently SKIP an unparseable row --
                // unreachable while EditorRows' only sources were persisted values and AddPresetRow's
                // own valid default, reachable the instant the Favourites Editor dialog lets a user
                // type into FrequencyMhzText directly. A typo (or InvariantCulture rejecting a
                // comma-decimal "14,230000") silently deleted the row on Save while reporting
                // success. Now aborts the whole save instead, leaving EditorRows untouched -- same
                // "report the error, don't discard unsaved edits" contract as the exception handler
                // below.
                Log.SavePresetsInvalidFrequency(_logger, row.Label);
                Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.InvalidPresetFrequency", row.Label));
                return false;
            }

            // Code-review finding: this parse path had no plausibility check at all, so a Favourite
            // could store/apply a value SetFrequencyAsync's own direct-entry field would reject
            // (same bounds as MinPlausibleFrequencyMhz/MaxPlausibleFrequencyMhz above).
            if (!double.IsFinite(mhz) || mhz is < MinPlausibleFrequencyMhz or > MaxPlausibleFrequencyMhz)
            {
                Log.SavePresetsInvalidFrequency(_logger, row.Label);
                Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.InvalidPresetFrequency", row.Label));
                return false;
            }

            // Deliberately the wide persistence range, not the row's own mode-family band: the field
            // is freely editable by design, so a 12 kHz filter on a USB Favourite is the operator's
            // call to make. This check only rejects values that are not a filter width at all -- and
            // 0 in particular, which is Hamlib's RIG_PASSBAND_NORMAL sentinel. The Transceiver
            // header's own BW control deliberately has no such check; the asymmetry is intended,
            // since a persisted value outlives the one-shot command that pill sends.
            if (!RadioModeFamilies.IsValidBandwidth(row.BandwidthHz))
            {
                Log.SavePresetsInvalidBandwidth(_logger, row.Label, row.BandwidthHz);
                Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.InvalidPresetBandwidth", row.Label));
                return false;
            }

            // Math.Round, not a bare cast (auditor-caught, 2026-08-11): the "0.000000"-formatted
            // mhz * 1_000_000 product can land 1 ULP below the target integer for some real radio
            // frequencies, and a bare (long) cast truncates that down to N-1 Hz instead of N.
            presets.Add(new FrequencyPreset(row.Label, (long)Math.Round(mhz * 1_000_000), row.SelectedMode, (int)Math.Round(row.BandwidthHz)));
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

    /// <summary>TX-pane Tune button plan (2026-09-01), auditor code-review finding:
    /// <c>AllowConcurrentExecutions = true</c> is required, not decorative -- CommunityToolkit.Mvvm's
    /// generated <c>AsyncRelayCommand</c> defaults to <see langword="false"/>, which ANDs into
    /// <c>CanExecute</c> for the whole duration the command is already running. Without this, the
    /// bound button greys out the instant the tone starts and the "Stop tune" click (the
    /// <see cref="IsTuning"/> branch below) becomes unreachable from real UI -- only reachable from a
    /// test calling <c>ExecuteAsync</c> directly, which bypasses <c>CanExecute</c> entirely. This
    /// exact bug was already found and fixed on the sibling command
    /// <see cref="OptionsWindowViewModel.TestPttCommand"/>, which explicitly left THIS command
    /// untouched as "out of that diff's scope" -- harmless at the time, since this command's only
    /// surface was the Tone Generator dialog, which has its own close-to-cancel backstop
    /// (<see cref="StopTuneIfActive"/>). It stopped being harmless once
    /// <c>TxControlsPaneView.axaml</c> bound this same command directly on the TX pane, next to a
    /// live Transmit control and with NO dialog to close as a backstop -- a dead Stop button there
    /// would key PTT with a steady test tone for up to <see cref="TuneDurationSeconds"/> seconds with
    /// no way to abort from that screen.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TuneAsync()
    {
        if (IsTuning)
        {
            _tuneCts?.Cancel();
            return;
        }

        Log.TuneInvoked(_logger, TuneFrequencyHz, TuneDurationSeconds);
        ErrorMessage = null;
        IsTuning = true;
        var cts = new CancellationTokenSource();
        _tuneCts = cts;
        try
        {
            await _sstvSession.TuneAsync(TuneFrequencyHz, TimeSpan.FromSeconds(TuneDurationSeconds), ct: cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal control flow -- the operator clicked Stop (see the IsTuning branch above), or
            // the Tone Generator dialog closed mid-tone (StopTuneIfActive below, called from that
            // window's own Closing path).
        }
        catch (Exception ex)
        {
            Log.TuneFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.TuneFailed"));
        }
        finally
        {
            IsTuning = false;
            _tuneCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Called from the Tone Generator window's own Closing path so a Tune tone left running
    /// (PTT still keyed) can't outlive the dialog that started it -- same reasoning as
    /// <see cref="OptionsWindowViewModel.StopTuneIfActive"/>.</summary>
    public void StopTuneIfActive() => _tuneCts?.Cancel();

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
                    // T0-1 code-review correction: re-sync from the actual current state, not a
                    // blind `!value` flip. Since Program.cs's own StartReceivingAsync launch attempt
                    // now runs backgrounded (T0-1, production_audit.md) rather than completing before
                    // this ViewModel is constructed, a call here that throws (e.g. a TimeoutException
                    // racing that still-in-flight background attempt) does NOT necessarily mean the
                    // operation ultimately failed -- the background attempt can still succeed AFTER
                    // this one times out. Reading _sstvSession.IsReceiving reflects ground truth
                    // either way, instead of risking the toggle showing "Halt" while capture is
                    // actually live (or vice versa).
                    IsReceiving = _sstvSession.IsReceiving;
                }
                finally
                {
                    _suppressReceivingCommand = false;
                }
            });
        }
    }

    partial void OnTxVolumePercentChanged(int value)
    {
        if (_suppressVolumePersist)
        {
            return;
        }

        // Debounced (not one settings write per slider tick, per the Piece 5 plan) -- a slider drag
        // can fire dozens of times a second, and overlapping un-awaited settings-store SaveAsync
        // calls could race each other and let a stale write clobber a fresher one.
        _txVolumePersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _txVolumePersistCts = cts;
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
        // SavePresetsAsync/TuneAsync null ErrorMessage on entry; SetReceivingSafeAsync nulls it on
        // success) manages ErrorMessage around its own outcome -- this
        // one didn't, so a stale "No radio connected" from an earlier failed action could survive a
        // later, genuinely successful mode change with nothing to clear it.
        ErrorMessage = null;
        // Captured before the first await (auditor + principal finding, 2026-09-19), matching
        // ApplyPresetAsync/SetFrequencyAsync's own established convention: this method uses
        // ConfigureAwait(false), so a read after the first await could run off the UI thread, and
        // CanSetBandwidth is only ever written on the UI thread.
        var canSetBandwidth = CanSetBandwidth;
        try
        {
            await _radioSession.SetModeAsync(value).ConfigureAwait(false);
            // User-reported bug (2026-09-19): this is the ONE caller of SelectedRadioMode's own
            // change hook (the mode picker AND OnSsbAsPktChanged's mode-conversion both funnel here
            // via SelectedRadioMode), and it was sending a bare mode-set with no follow-up bandwidth
            // -- exactly the RadioModeFamilies landmine its own doc comment describes: a mode set
            // asks the rig for its own default passband (Hamlib's RIG_PASSBAND_NORMAL), which on
            // some rigs resolves to a destructively narrow width (reported: 500 Hz, which then
            // desyncs the rig badly enough to visibly fall back to its prior mode on the next poll).
            // ApplyPresetAsync already avoids this exact landmine with this exact follow-up call --
            // mirrored here instead of inventing a second mechanism. Gated on CanSetBandwidth for the
            // same reason ApplyPresetAsync is: flrig/OmniRig THROW here rather than no-op when
            // bandwidth isn't settable, which would otherwise fail an already-successful mode change.
            if (canSetBandwidth && RadioModeFamilies.FallbackFor(value) is { } hz)
            {
                await _radioSession.SetBandwidthAsync(hz).ConfigureAwait(false);
            }
        }
        // Auditor + principal finding (2026-09-19): a bare `catch (Exception)` flattened every
        // failure to "No radio connected" -- including a genuine rig rejection (RadioProtocolException,
        // whose message already carries the literal rigctld "RPRT -<n>" text) and an unsupported mode
        // (ArgumentOutOfRangeException, e.g. RadioMode.Unknown or a mode this backend has no wire-format
        // equivalent for -- see SetModeAsync's own doc comment on every backend). Splitting this out is
        // what makes a genuine rig-side rejection diagnosable instead of indistinguishable from a
        // dropped CAT link.
        catch (ArgumentOutOfRangeException ex)
        {
            Log.SetModeFailed(_logger, value, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.ModeNotSupported"));
        }
        catch (RadioProtocolException ex)
        {
            Log.SetModeFailed(_logger, value, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("RadioStatus.Error.ModeRejected", ex.Message));
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading TX Pwr failed")]
        public static partial void LoadTxVolumeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading TX device mute state failed")]
        public static partial void LoadTxMuteFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading SSB as PKT preference failed")]
        public static partial void LoadSsbAsPktPreferenceFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Saving SSB as PKT preference failed")]
        public static partial void SaveSsbAsPktPreferenceFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading decoding-indicator-blinks preference failed")]
        public static partial void LoadDecodingIndicatorBlinksPreferenceFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SetFrequency invoked: {Mhz} MHz")]
        public static partial void SetFrequencyInvoked(ILogger logger, double mhz);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetFrequency failed: {Mhz} MHz")]
        public static partial void SetFrequencyFailed(ILogger logger, double mhz, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SetBandwidth invoked: {Hz} Hz")]
        public static partial void SetBandwidthInvoked(ILogger logger, int hz);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetBandwidth failed: {Hz} Hz")]
        public static partial void SetBandwidthFailed(ILogger logger, int hz, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ApplyPreset invoked: {Label} ({FrequencyHz}Hz, {Mode}, {BandwidthHz}Hz BW)")]
        public static partial void ApplyPresetInvoked(ILogger logger, string label, long frequencyHz, RadioMode mode, int? bandwidthHz);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ApplyPreset bandwidth out of range: {Label} stored {StoredHz}Hz, using {SubstitutedHz}Hz")]
        public static partial void ApplyPresetBandwidthSubstituted(ILogger logger, string label, int storedHz, int substitutedHz);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SavePresets rejected bandwidth: {Label} ({BandwidthHz}Hz)")]
        public static partial void SavePresetsInvalidBandwidth(ILogger logger, string label, double bandwidthHz);

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "SavePresets aborted: row {Label} has an unparseable frequency")]
        public static partial void SavePresetsInvalidFrequency(ILogger logger, string label);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Tune invoked: {FrequencyHz}Hz for {Seconds}s")]
        public static partial void TuneInvoked(ILogger logger, double frequencyHz, double seconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Tune failed")]
        public static partial void TuneFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "IsReceiving changed: {Value}")]
        public static partial void IsReceivingChanged(ILogger logger, bool value);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetReceiving({Value}) failed")]
        public static partial void SetReceivingFailed(ILogger logger, bool value, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting TX Pwr ({Value}%) failed")]
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
/// <c>StockEntryViewModel</c> and for the identical reason (avoids a cross-DataTemplate
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
    public FrequencyPresetEditorRowViewModel(
        FrequencyPreset preset,
        System.Windows.Input.ICommand removeCommand,
        IReadOnlyList<RadioModeChoice> modeChoices)
    {
        _label = preset.Label;
        _frequencyMhzText = (preset.FrequencyHz / 1_000_000.0).ToString("0.000000", CultureInfo.InvariantCulture);
        _selectedMode = preset.Mode;
        // A row must NEVER hold a value SavePresets rejects: one such row aborts the whole save and
        // takes every other row's unsaved edits with it. Two untrusted inputs both get a saveable
        // seed here -- a mode this editor doesn't list (hand-edited settings file, no family, so no
        // fallback of its own), and a stored width that is out of range (a hand-edited 0, which is
        // Hamlib's RIG_PASSBAND_NORMAL, or an absurd value).
        var seedFallback = RadioModeFamilies.FallbackFor(preset.Mode) ?? RadioModeFamilies.SsbFallbackHz;
        _bandwidthHz = preset.BandwidthHz is { } stored && RadioModeFamilies.IsValidBandwidth(stored)
            ? stored
            : seedFallback;
        // Seeded here, not via OnSelectedModeChanged: the constructor assigns _selectedMode as a bare
        // field write, which does not invoke the generated setter's hook.
        _availableBandwidthPresetsHz = PresetsForMode(preset.Mode);
        RemoveCommand = removeCommand;
        ModeChoices = modeChoices;
    }

    public System.Windows.Input.ICommand RemoveCommand { get; }

    /// <summary>The six modes this editor offers, already paired with their localized display text
    /// by <see cref="RadioStatusViewModel"/> -- one shared list, built once, never mutated per row.
    /// Mutating a live <c>ItemsSource</c> under an active selection is its own Avalonia hazard.</summary>
    public IReadOnlyList<RadioModeChoice> ModeChoices { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private string _frequencyMhzText;

    [ObservableProperty]
    private RadioMode _selectedMode;

    /// <summary>This row's requested filter width. <see cref="double"/> rather than a parsed string,
    /// matching the Transceiver header's own BW control, which this row's editable ComboBox reuses.
    /// The consequence, so it doesn't read as an oversight: an unparseable typed value never reaches
    /// this property at all -- the binding converter fails and the previous value stays -- so
    /// <c>SavePresetsInternalAsync</c>'s bandwidth error covers the range check only, never a parse
    /// failure. The sibling <see cref="FrequencyMhzText"/> is a string precisely because a
    /// comma-decimal typo there once silently deleted rows.</summary>
    [ObservableProperty]
    private double _bandwidthHz;

    /// <summary>Quick picks for the row's CURRENT mode -- observable, not get-only, or the ComboBox
    /// never picks up a new family's list.</summary>
    [ObservableProperty]
    private IReadOnlyList<double> _availableBandwidthPresetsHz;

    /// <summary>Follows the mode across families: USB to FM turns 2400 into 15000, FM to USB turns
    /// 15000 back into 2400. A value already sensible for the new family survives untouched, so a
    /// deliberately typed width isn't lost to an unrelated edit. The list is raised BEFORE the value:
    /// the control is an editable ComboBox whose Text is bound to <see cref="BandwidthHz"/>, so the
    /// two writes are not independent.</summary>
    partial void OnSelectedModeChanged(RadioMode value)
    {
        AvailableBandwidthPresetsHz = PresetsForMode(value);
        if (!RadioModeFamilies.IsInFamilyBand(value, BandwidthHz) && RadioModeFamilies.FallbackFor(value) is { } fallback)
        {
            BandwidthHz = fallback;
        }
    }

    // Cached, never rebuilt per call: a fresh array on every mode change swaps the bound ComboBox's
    // ItemsSource IDENTITY even for a within-family change like USB->LSB, which resets the control's
    // own selection for no reason. Same instance in, no swap.
    private static readonly double[] SsbPresetsHz = [1800, 2400, 2800];
    private static readonly double[] FmPresetsHz = [9000, 12_000, 15_000];
    private static readonly double[] NoPresetsHz = [];

    private static double[] PresetsForMode(RadioMode mode) => RadioModeFamilies.FamilyOf(mode) switch
    {
        RadioModeFamily.Ssb => SsbPresetsHz,
        RadioModeFamily.Fm => FmPresetsHz,
        _ => NoPresetsHz,
    };
}

/// <summary>One entry in the Favourites editor's mode picker: the real <see cref="RadioMode"/> that
/// gets persisted, paired with the localized text shown for it. Built once by
/// <see cref="RadioStatusViewModel"/>, which already holds the localization service, and handed to
/// every row as data -- deliberately not a converter (a stateless <c>IValueConverter</c> can't reach
/// <c>ILocalizationService</c>) and not a service injected into the row (that would be a dependency
/// where a prebuilt list suffices).</summary>
public sealed record RadioModeChoice(RadioMode Mode, string Display);
