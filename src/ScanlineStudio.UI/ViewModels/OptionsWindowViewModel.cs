using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The first `Window`/dialog-backed view-model in the app (every other view-model so far
/// backs a dockable `Tool` pane) -- holds one editable in-memory copy of every settings field it
/// covers, loaded via <see cref="OptionsSettingsService"/> on construction (never the concrete
/// per-module settings-section types directly -- see that service's own doc comment for why).
/// ui_transition_plan.md step 7: committed by <see cref="SaveCommand"/> (saves and closes),
/// <see cref="ApplyCommand"/> (saves, stays open), or implicitly by the Connect branch of
/// <see cref="ToggleRadioConnectionCommand"/> (saves first so Connect always uses what Test just
/// validated, not a stale prior Save -- see that method's own doc comment). <see cref="CancelCommand"/>
/// discards only edits made since the LAST of those three, not necessarily since the dialog
/// opened. Per-section resets are a single click each (an accidental reset before the next
/// Save/Apply/Connect costs nothing); the global "reset ALL" is the one genuinely destructive
/// action here and requires an explicit confirm step.
///
/// Radio/CAT offers None/rigctld/Hamlib -- all three <c>IRadioProtocolFactory</c> backends are
/// registered in DI (spec/14-roadmap.md's Piece 3).</summary>
public sealed partial class OptionsWindowViewModel : ViewModelBase, IDisposable
{
    private readonly OptionsSettingsService _optionsSettingsService;
    private readonly ILocalizationService _localization;
    private readonly IAudioDeviceEnumerator _audioDeviceEnumerator;
    private readonly ILogbookSessionService _logbookSession;
    private readonly ISettingsStore _settingsStore;
    private readonly IRadioSessionService _radioSession;
    private readonly IHamlibDiscoveryService _hamlibDiscovery;
    private readonly IFilePickerService _filePickerService;
    private readonly ISstvSessionService _sstvSession;
    private readonly ISerialPortEnumerator _serialPortEnumerator;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly IAppLocationsService _appLocationsService;
    private readonly IApplicationRestarter _applicationRestarter;
    private readonly ILogger<OptionsWindowViewModel> _logger;

    private static readonly TimeSpan TxVolumePersistDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>ui_transition_plan.md step 8 (T2-2): same debounce shape as
    /// <see cref="TxVolumePersistDebounce"/> above -- a typed path fires this on every keystroke,
    /// and each attempt is a real file read, not just a settings write.</summary>
    private static readonly TimeSpan SoundFileIdValidationDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>Test PTT opens its own throwaway connection, invisible to the app's real SWR
    /// auto-cutoff (<c>TxControlsPaneViewModel.CheckSwrCutoff</c> only bounds an in-flight
    /// <c>ISstvSessionService.TransmitAsync</c>) -- deliberately NOT <see cref="MaxTuneDuration"/>.
    /// Short enough to see an LED/hear a relay click with minimal exposure if something is wrong.</summary>
    private static readonly TimeSpan MaxTestPttDuration = TimeSpan.FromSeconds(5);

    /// <summary>WSJT-X's own Tune button has no fixed duration -- it keys PTT and holds a steady
    /// tone until the operator clicks it again, with an internal safety timeout so a forgotten Tune
    /// can't key the rig forever. This is that same shape: <see cref="TuneCommand"/> auto-stops after
    /// this long if the operator doesn't click Stop first.</summary>
    private static readonly TimeSpan MaxTuneDuration = TimeSpan.FromSeconds(30);

    /// <summary>T1-11 (production_audit.md): the 4 "Test Connection" commands (rigctld/flrig/
    /// OmniRig/Hamlib) used to call <c>TestConnectionAsync</c> with no cancellation token at all,
    /// unlike the sibling <see cref="MaxTestPttDuration"/>-bounded PTT tests -- a hung
    /// <c>PollAsync</c> left "Testing…" stuck forever with no way to retry (the command auto-disables
    /// while running). Same value as <see cref="MaxTuneDuration"/>, no particular relationship to it
    /// otherwise -- both just need "long enough for a real device, short enough a human doesn't wonder
    /// if the app hung."
    /// <para>Code-review finding: closes the hang for rigctld/flrig/OmniRig, but NOT fully for
    /// Hamlib -- <c>HamlibRadioProtocol</c> only honors its token at the semaphore wait boundary
    /// (uncontended here, so instant), and <c>PollAsync</c>'s own <c>EnsureConnectedAsync</c>
    /// (<c>rig_open</c>) takes no cancellation token at all. A wedged native <c>rig_open</c> can
    /// still leave "Testing…" stuck for the Hamlib backend specifically -- same pre-existing
    /// limitation <c>Program.cs</c> already documents elsewhere, not something this fix
    /// introduces or fully closes.</para></summary>
    private static readonly TimeSpan MaxTestConnectionDuration = TimeSpan.FromSeconds(30);

    /// <summary>Fixed AFC-lock tone, matching <see cref="RadioStatusViewModel"/>'s own
    /// <c>TuneFrequencyHz</c> default -- this tab's Tune button is scoped to the "key a tone, dial
    /// Pwr to the wattage I want" workflow only, not a general-purpose configurable test-tone
    /// generator, so no separate frequency input is exposed here.</summary>
    private const double TuneFrequencyHz = 1750;

    /// <summary>Index of the TX tab (source order: General=0, Audio=1, Radio=2, Tx=3, ...) in this
    /// dialog's own `TabControl` -- named so the header-row's callsign chip (which jumps straight
    /// here, since Callsign/OperatorName/OperatorGrid live on this tab) can request it without a
    /// magic number, same "named constant + a source-order test" pattern as
    /// <see cref="ScanlineStudio.UI.ViewModels.MainViewModel.LogbookTabIndex"/>.</summary>
    public const int TxTabIndex = 3;

    /// <summary>Index of the Radio (CAT) tab -- named so the radio-connection give-up popup's own
    /// "Config" button (which jumps straight here, since rig/CAT connection settings live on this
    /// tab) can request it without a magic number, same pattern as <see cref="TxTabIndex"/>
    /// above.</summary>
    public const int RadioTabIndex = 2;

    /// <summary>Backs this dialog's own `TabControl`'s `SelectedIndex` (`Mode=TwoWay` -- both
    /// directions matter: the user's own manual tab clicks flow back here, and
    /// <see cref="ScanlineStudio.UI.ViewModels.MainViewModel"/>'s callsign chip needs to jump straight
    /// to <see cref="TxTabIndex"/> on open). Same pattern as
    /// <see cref="ScanlineStudio.UI.ViewModels.MainViewModel.SelectedTabIndex"/>.</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private CultureInfo? _selectedCulture;

    /// <summary>Backs the General tab's "Remember window position and size" checkbox -- unlike
    /// every other Options field, this one is deliberately NOT part of <see cref="OptionsSnapshot"/>/
    /// <see cref="OptionsSettingsService"/>: it gates <see cref="WindowGeometrySettings"/>, a
    /// UI-owned settings section (see that record's own doc comment for why -- routing it through
    /// <see cref="OptionsSettingsService"/>, which lives in <c>ScanlineStudio.Application</c>, would
    /// require that project to reference a <c>ScanlineStudio.UI</c> type, inverting the layering).
    /// Loaded/saved directly via <see cref="_settingsStore"/> instead, same pattern
    /// <c>TxControlsPaneViewModel</c> already uses for its own UI-owned section.</summary>
    [ObservableProperty]
    private bool _rememberWindowPosition;

    /// <summary>Backs the General tab's "JPEG quality" stepper -- same "deliberately NOT part of
    /// <see cref="OptionsSnapshot"/>" reasoning as <see cref="RememberWindowPosition"/> directly
    /// above: gates a UI-owned settings section (<see cref="ImageExportSettings"/>), loaded/saved
    /// directly via <see cref="_settingsStore"/>. 1..100, default 85 matches this dialog's own
    /// pre-existing (previously disabled) `NumericUpDown` placeholder value.</summary>
    [ObservableProperty]
    private int _jpegQuality = 85;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedCaptureDevice;

    [ObservableProperty]
    private AudioDeviceInfo? _selectedPlaybackDevice;

    /// <summary>Legacy's <c>EditSamp</c> is a <c>TComboBox</c> (<c>Option.h:97</c>), not the plain
    /// text field this port originally assumed -- its <c>Option.dfm</c>-defined <c>Items.Strings</c>
    /// list is exactly <see cref="AvailableSampleRates"/> below. Still editable (legacy parses
    /// whatever text is typed via <c>sscanf</c>, same as this port's own out-of-list-value
    /// round-trip convention already established for <see cref="AvailableBaudRates"/>), so the
    /// preset list is a convenience, not a restriction.</summary>
    [ObservableProperty]
    private int _sampleRate = 11025;

    /// <summary>Legacy's own <c>EditSamp</c> dropdown preset list, verbatim (<c>Option.dfm</c>'s
    /// <c>Items.Strings</c>) -- NOT <c>ComLib.cpp</c>'s unrelated <c>SampTable[]</c> (a different,
    /// internal processing-bucket table with a different value set, never shown to the user).</summary>
    public IReadOnlyList<int> AvailableSampleRates { get; } = [8000, 11025, 12000, 14000, 16000, 18000, 22050, 24000, 44100, 48000];

    /// <summary>Stub survey Tier 3, "Clock calibration" piece 1 -- see
    /// <c>AudioDeviceSettings.TxSampleRateOffsetHz</c>'s own doc comment. Lives next to
    /// <see cref="SampleRate"/> in the Audio tab, matching legacy's own placement
    /// (<c>Option.h:91/124/166</c>, <c>TxSampOff</c>/<c>UDTxSamp</c> sit beside <c>EditSamp</c> on
    /// legacy's Options dialog -- this port has no separate Calibration-menu equivalent to that
    /// specific control, a round-2 plan-review finding).</summary>
    [ObservableProperty]
    private double _txSampleRateOffsetHz;

    /// <summary>Which channel of a stereo capture device to decode -- real, already-wired backend
    /// field (<see cref="ScanlineStudio.Application.OptionsSnapshot.CaptureChannelSource"/> -&gt;
    /// <c>AudioDeviceSettings.CaptureChannelSource</c> -&gt; <c>SstvSessionService.StartReceivingAsync</c>
    /// -&gt; the real native channel-extraction path) that had no UI path before. Backed by 3
    /// <c>IsCaptureChannelXSelected</c> computed properties below, same pattern as
    /// <see cref="SenseLevel"/>/<see cref="RadioBackendId"/>.</summary>
    [ObservableProperty]
    private AudioChannelSource _captureChannelSource = AudioChannelSource.Mono;

    /// <summary>Duplicates the TX signal to both output channels -- real, already-wired backend
    /// field (<c>AudioDeviceSettings.StereoTxEnabled</c>, consumed by <c>SstvSessionService</c>'s TX
    /// path) that had no UI path before.</summary>
    [ObservableProperty]
    private bool _stereoTxEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private string _radioBackendId = "none";

    [ObservableProperty]
    private string? _rigctldHost;

    [ObservableProperty]
    private int? _rigctldPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTestPtt))]
    private uint? _hamlibModel;

    /// <summary>Discovery-order tier-1 user override path (spec/03-cat-layer.md) -- browsed to,
    /// auto-detected, or hand-typed. See <see cref="BrowseHamlibLibraryAsync"/>/
    /// <see cref="AutoDetectHamlibAsync"/>/<see cref="ProbeHamlibAsync"/> below.</summary>
    [ObservableProperty]
    private string? _hamlibLibraryPath;

    /// <summary>Value of <see cref="HamlibLibraryPath"/> at dialog-load time -- restart-required-
    /// settings backlog item 5 (2026-08-28): now ALSO the no-op guard baseline for the live Hamlib
    /// library reload (see <see cref="SaveCoreAsync"/>'s own comment at that call site), not just the
    /// old restart-warning comparison. Refreshed to the just-saved value on a SUCCESSFUL reload
    /// (never on a failed one, so a later Save legitimately retries) -- without that refresh, every
    /// subsequent Save in the same dialog session would re-trigger a real native library reload (and
    /// permanent leak, see <c>IHamlibLibraryReconfiguration</c>'s own doc comment) even though
    /// nothing changed.</summary>
    private string? _originalHamlibLibraryPath;

    /// <summary>Set inside <see cref="SaveCoreAsync"/> (reset to <see langword="false"/> at its own
    /// start, every call) when this Save's sample-rate request came back
    /// <see cref="SampleRateApplyResult.DeferredRecordingInProgress"/> -- see
    /// <see cref="SampleRateChangeDeferredWarningRequested"/>'s own doc comment for why
    /// <see cref="SaveAsync"/> reads this afterward instead of <see cref="SaveCoreAsync"/> returning
    /// it directly (that method's own <c>bool</c> return already means something else: whether the
    /// whole save succeeded).</summary>
    private bool _sampleRateChangeDeferred;

    /// <summary>Restart-required-settings backlog item 5 (2026-08-28): same shape/reset contract as
    /// <see cref="_sampleRateChangeDeferred"/> immediately above, set when this Save's Hamlib
    /// library-path reload request came back <c>Applied: false</c>, OR threw
    /// <see cref="TimeoutException"/> (a concurrent reload still in progress -- treated as a failure,
    /// NOT silently swallowed the way <see cref="SetCultureAsync"/>'s nearest-shaped catch below
    /// would if copied naively; see <see cref="HamlibLibraryReloadFailedWarningRequested"/>'s own
    /// doc comment).</summary>
    private bool _hamlibLibraryReloadFailed;

    /// <summary>The composed failure message for <see cref="HamlibLibraryReloadFailedWarningRequested"/>
    /// -- unlike <see cref="SampleRateChangeDeferredWarningRequested"/>'s fully-static sibling dialog,
    /// this one carries a real per-attempt failure detail, so it can't be a plain locale string
    /// translated directly in the View. Set alongside <see cref="_hamlibLibraryReloadFailed"/>,
    /// read by <c>OptionsWindowView.axaml.cs</c>'s own event handler to construct the dialog's
    /// ViewModel.</summary>
    public string? HamlibLibraryReloadFailedMessage { get; private set; }

    [ObservableProperty]
    private string? _hamlibDiscoveryStatusMessage;

    [ObservableProperty]
    private bool _isProbingHamlib;

    [ObservableProperty]
    private HamlibRigModelInfo? _selectedHamlibRigModel;

    [ObservableProperty]
    private string? _hamlibSerialPort;

    [ObservableProperty]
    private int? _hamlibBaudRate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPttMethodVoxSelected))]
    [NotifyPropertyChangedFor(nameof(IsPttMethodCatSelected))]
    [NotifyPropertyChangedFor(nameof(IsPttMethodRtsSelected))]
    [NotifyPropertyChangedFor(nameof(IsPttMethodDtrSelected))]
    [NotifyPropertyChangedFor(nameof(IsPttPortEnabled))]
    [NotifyPropertyChangedFor(nameof(CanTestPtt))]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private string? _hamlibPttType;

    /// <summary>Hamlib's own <c>ptt_pathname</c> -- a PTT-only serial device, separate from
    /// <see cref="HamlibSerialPort"/> (the CAT port). Only meaningful for the RTS/DTR PTT methods --
    /// see <see cref="IsPttPortEnabled"/>.</summary>
    [ObservableProperty]
    private string? _hamlibPttPort;

    /// <summary>Own field set, not shared with <see cref="RigctldHost"/>/<see cref="RigctldPort"/> --
    /// different service, different default port (12345, flrig's own default), and sharing would let
    /// editing one backend's panel spuriously invalidate the other's test state.</summary>
    [ObservableProperty]
    private string? _flrigHost = "127.0.0.1";

    [ObservableProperty]
    private int? _flrigPort = 12345;

    [ObservableProperty]
    private string? _callsign;

    [ObservableProperty]
    private string? _operatorName;

    [ObservableProperty]
    private string? _operatorGrid;

    [ObservableProperty]
    private bool _isConfirmingResetAll;

    /// <summary>General tab's Storage section -- 4 rows (Images/Config/Database/Log), moved in
    /// from the former standalone "Configurations &gt; Storage" dialog (Images) plus 3 new rows.
    /// Deliberately NOT part of <see cref="OptionsSnapshot"/>/staged-until-Save like every field
    /// above -- each row commits (Images/Config/Log) or stages (Database only, restart-required by
    /// standing user decision) immediately on its own Apply, and <see cref="SaveCommand"/>/<see cref="CancelCommand"/>/
    /// <see cref="ResetGeneralToDefaultCommand"/> must never touch any of them (see those methods'
    /// own bodies -- none reference these fields at all, by omission, not a guard).</summary>
    [ObservableProperty]
    private string _imagesDirectory = string.Empty;

    [ObservableProperty]
    private string? _imagesDirectoryErrorMessage;

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: same immediate-commit
    /// shape as <see cref="ImagesDirectory"/> above, but a combined enable+directory row -- both
    /// halves are always applied together via <see cref="ApplyAudioDirectoryAsync"/>, matching
    /// <c>IReceiveHistoryStore.SetAudioSettingsAsync</c>'s own combined-arguments contract, and both
    /// the persisted store call AND the live-apply <see cref="ISstvSessionService.SetAutoSaveAudioEnabled"/>/
    /// <see cref="ISstvSessionService.SetAudioDirectory"/> calls happen on that one Apply, same
    /// "persist + live-apply together" pairing those two live-apply methods' own doc comments
    /// require.</summary>
    [ObservableProperty]
    private bool _audioSaveEnabled;

    [ObservableProperty]
    private string _audioDirectory = string.Empty;

    [ObservableProperty]
    private string? _audioDirectoryErrorMessage;

    /// <summary>"Currently using" display value -- refreshed after a successful
    /// <see cref="ApplyConfigDirectoryAsync"/> (restart-required-settings backlog item 3, 2026-08-27:
    /// Config directory now applies live, same shape as <see cref="LogDirectory"/>'s own
    /// display/refresh, no longer staged-until-restart like <see cref="DatabaseDirectory"/> still
    /// is). NOT the TextBox/Browse binding target -- see <see cref="ConfigDirectoryInput"/> for
    /// that.</summary>
    [ObservableProperty]
    private string _configDirectory = string.Empty;

    /// <summary>TextBox/Browse binding target -- deliberately a SEPARATE property from
    /// <see cref="ConfigDirectory"/> (round-3 plan-review finding), empty-seeded exactly like
    /// <see cref="DatabaseDirectory"/>'s own equivalent <c>PendingDatabaseDirectory</c> input, so
    /// <see cref="ApplyConfigDirectoryAsync"/>'s existing blank-means-error guard (a user who clicks
    /// Apply without typing or browsing anything) keeps working unchanged now that Config no longer
    /// has a real "pending" concept to double as that same input.</summary>
    [ObservableProperty]
    private string? _configDirectoryInput;

    [ObservableProperty]
    private string? _configDirectoryErrorMessage;

    [ObservableProperty]
    private string _databaseDirectory = string.Empty;

    [ObservableProperty]
    private string? _pendingDatabaseDirectory;

    [ObservableProperty]
    private string? _databaseDirectoryErrorMessage;

    [ObservableProperty]
    private bool _isConfirmingDatabaseRestart;

    [ObservableProperty]
    private string _logDirectory = string.Empty;

    [ObservableProperty]
    private string? _logDirectoryErrorMessage;

    /// <summary>Backs the Decode tab's real toggles -- see <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoSyncEnabled"/>/
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoSlantEnabled"/>'s own doc comments
    /// for what each genuinely gates in <c>AnalogFmSstvDecoder</c>. All four of this tab's decoder
    /// toggles (this one, <see cref="AutoStopEnabled"/>, <see cref="AutoSlantEnabled"/>,
    /// <see cref="SyncRestartEnabled"/>) are real and wired -- an earlier version of this comment
    /// wrongly claimed the other two were left stub pending a wording fix; both the wiring
    /// (<c>OptionsWindowView.axaml</c>) and the loc text (<c>assets/locale/en.json</c>) were already
    /// corrected before this note was fixed (2026-08-27). Genuinely LIVE now too (2026-08-27,
    /// restart-required-settings backlog item 1) -- see <see cref="SaveCoreAsync"/>'s own
    /// <c>RequestAutoSyncEnabled</c>/etc. calls, unlike every OTHER Options-dialog setting baked into
    /// a DI singleton at startup (Radio backend, Audio device, etc. -- no live-reconfiguration path
    /// exists for those).</summary>
    [ObservableProperty]
    private bool _autoSyncEnabled = true;

    [ObservableProperty]
    private bool _autoSlantEnabled = true;

    /// <summary>Legacy fresh-install default is OFF (unlike every other decoder toggle here) --
    /// see <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoStopEnabled"/>'s own doc
    /// comment for the citation. Genuinely live now (2026-08-27) -- see <see cref="AutoSyncEnabled"/>'s
    /// own doc comment above.</summary>
    [ObservableProperty]
    private bool _autoStopEnabled;

    [ObservableProperty]
    private bool _syncRestartEnabled = true;

    /// <summary>Squelch/sense-level preset index (0-3, "Very low".."Very high") -- see
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.SenseLevel"/>'s own doc comment for
    /// the legacy basis and the absent-vs-out-of-range fallback distinction. Backed by 4
    /// <c>IsSenseLevelXSelected</c> computed properties below, same pattern as
    /// <see cref="IsNoneBackendSelected"/>/etc.</summary>
    [ObservableProperty]
    private int _senseLevel = 1;

    /// <summary>Main-picture FM demodulator algorithm -- see
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.DemodType"/>'s own doc comment for the
    /// legacy basis and the absent-vs-out-of-range fallback (unlike <see cref="SenseLevel"/>, both
    /// fallbacks here are the SAME value, <see cref="DemodType.Hilbert"/>, not different ones).
    /// Backed by 3 <c>IsDemodTypeXSelected</c> computed properties below, same pattern as
    /// <see cref="IsSenseLevelVeryLowSelected"/>/etc.</summary>
    [ObservableProperty]
    private DemodType _demodType = DemodType.Hilbert;

    /// <summary>RX bandpass-filter sharpness -- see
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.RxBpfPreset"/>'s own doc comment for
    /// the legacy basis and the absent-vs-out-of-range fallback (both fallbacks here are the SAME
    /// value, <see cref="RxBpfPreset.Wide"/>, same shape as <see cref="DemodType"/> above, not
    /// <see cref="SenseLevel"/>'s "different fallback" shape). Backed by 4 <c>IsRxBpfXSelected</c>
    /// computed properties below, same pattern as <see cref="IsDemodTypePllSelected"/>/etc.</summary>
    [ObservableProperty]
    private RxBpfPreset _rxBpfPreset = RxBpfPreset.Wide;

    /// <summary>RX buffering mode -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.RxBufferMode"/>'s own doc comment for the legacy
    /// basis and the absent-vs-out-of-range fallback (both fallbacks here are the SAME value,
    /// <see cref="RxBufferMode.On"/>, same shape as <see cref="DemodType"/>/<see cref="RxBpfPreset"/>
    /// above, not <see cref="SenseLevel"/>'s "different fallback" shape). Backed by 3
    /// <c>IsRxBufferXSelected</c> computed properties below, same pattern as
    /// <see cref="IsRxBpfOffSelected"/>/etc. Also gates <see cref="IsAutoSlantRowEnabled"/> -- Auto
    /// Slant has no effect with RX buffering off (legacy's own <c>CBASlant->Enabled</c> gate,
    /// `Option.cpp:222`).</summary>
    [ObservableProperty]
    private RxBufferMode _rxBufferMode = RxBufferMode.On;

    /// <summary>PLL demodulator tuning (Options stub backlog item 1,
    /// `docs/plans/options-stub-item1-pll-tuning-plan.md`) -- see
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.PllVcoGain"/>'s own doc comment for the
    /// legacy basis and real defaults (1.0/1/1500.0/3/900.0, NOT the AXAML's own earlier hardcoded
    /// placeholder values). Enabled for editing only while <see cref="IsDemodTypePllSelected"/> (the
    /// AXAML group's own `IsEnabled` binding), matching legacy's real `GBPLL.Enabled` gate
    /// (`Option.cpp:179-196`, driven by the PENDING radio selection, not the committed DemodType).</summary>
    [ObservableProperty]
    private double _pllVcoGain = 1.0;

    [ObservableProperty]
    private int _pllLoopOrder = 1;

    [ObservableProperty]
    private double _pllLoopCutoffHz = 1500;

    [ObservableProperty]
    private int _pllOutputOrder = 3;

    [ObservableProperty]
    private double _pllOutputCutoffHz = 900;

    /// <summary>Zero-crossing demodulator tuning (Options stub backlog item 2,
    /// `docs/plans/options-stub-item2-zerocrossing-tuning-plan.md`) -- see
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.ZeroCrossingSmoothingMode"/>'s own doc
    /// comment for the legacy basis and real defaults (Iir/3/900.0/2200.0). Enabled for editing only
    /// while <see cref="IsDemodTypeZeroCrossingSelected"/>, matching legacy's real `GBCROSS.Enabled`
    /// gate (`Option.cpp:179-196`). The IIR sub-group (order+cutoff) and FIR sub-group (smoothing
    /// frequency) are further greyed based on <see cref="ZeroCrossingSmoothingMode"/> itself, matching
    /// legacy's real `GBCOI`/`GBCOF` gate (`Option.cpp:197-212`).</summary>
    [ObservableProperty]
    private ZeroCrossingSmoothingMode _zeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Iir;

    [ObservableProperty]
    private int _zeroCrossingOutputOrder = 3;

    [ObservableProperty]
    private double _zeroCrossingOutputCutoffHz = 900;

    [ObservableProperty]
    private double _zeroCrossingSmoothingFrequencyHz = 2200;

    /// <summary>TX output bandpass filter tuning (Options stub backlog item 3,
    /// `docs/plans/options-stub-item3-tx-bpf-lpf-plan.md`) -- see
    /// <see cref="ScanlineStudio.Core.Audio.AudioDeviceSettings.TxBpfEnabled"/>'s own doc comment for
    /// the legacy basis and real defaults (true/24). This is a REVERSAL of a documented, deliberate
    /// prior removal (`docs/removed-features.md`'s former "TX output bandpass filter toggle/tap
    /// setting" entry, deleted now that the capability is restored) -- the underlying filter math was
    /// already correctly ported and applied unconditionally; only the user-facing toggle/tap control
    /// was missing.</summary>
    [ObservableProperty]
    private bool _txBpfEnabled = true;

    [ObservableProperty]
    private int _txBpfTapCount = 24;

    /// <summary>TX pre-VCO frequency-smoothing tuning (Options stub backlog item 3) -- see
    /// <see cref="ScanlineStudio.Core.Audio.AudioDeviceSettings.TxBpfEnabled"/>'s own doc comment for
    /// the legacy basis and real defaults (false/2000.0). Genuinely new work, never ported before.</summary>
    [ObservableProperty]
    private bool _txLpfEnabled;

    [ObservableProperty]
    private double _txLpfFrequencyHz = 2000;

    [ObservableProperty]
    private bool _qrzLookupEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestQrzLookupCommand))]
    private string? _qrzLookupUsername;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestQrzLookupCommand))]
    private string? _qrzLookupPassword;

    /// <summary>Testing/✓ succeeded/✗ &lt;reason&gt; -- always tests the CURRENT in-memory
    /// <see cref="QrzLookupUsername"/>/<see cref="QrzLookupPassword"/>, not yet-saved values, via
    /// <see cref="ILogbookSessionService.TestQrzLookupCredentialsAsync"/>.</summary>
    [ObservableProperty]
    private string? _testQrzLookupStatus;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestQrzLookupCommand))]
    private bool _isTestingQrzLookup;

    /// <summary>Backs the Identification tab's "ID method" 3-way radio group -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.CwIdMode"/>'s own doc comment for what each value
    /// means.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdMethodOffSelected))]
    [NotifyPropertyChangedFor(nameof(IsIdMethodCwSelected))]
    [NotifyPropertyChangedFor(nameof(IsIdMethodSoundFileSelected))]
    private CwIdMode _cwIdMode;

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
            _soundFileIdValidationCts?.Cancel();
            SoundFileIdSuccessMessage = null;
            SoundFileIdErrorMessage = null;
        }
    }

    [ObservableProperty]
    private string? _cwText;

    /// <summary><c>StationIdSettings.SoundFileMmvPath</c> -- the sound-file station ID's own file
    /// path, edited via <see cref="BrowseSoundFileCommand"/> or typed directly. Every change (browse
    /// pick or manual edit alike, since both go through this one setter) re-validates via
    /// <see cref="OnSoundFileMmvPathChanged"/> -- ui_transition_plan.md step 8 (T2-2).</summary>
    [ObservableProperty]
    private string? _soundFileMmvPath;

    /// <summary>Duration text for the CURRENTLY typed/picked <see cref="SoundFileMmvPath"/>, once it
    /// validates successfully -- <see langword="null"/> whenever there's nothing to show (blank
    /// path, still validating, or currently failing -- see <see cref="SoundFileIdErrorMessage"/> for
    /// that case). The two are deliberately separate properties, not one message plus a bool, so the
    /// AXAML can gate each one's own <c>IsVisible</c>/color with the same
    /// <c>ObjectConverters.IsNotNull</c> convention already used throughout this file, instead of a
    /// combined-boolean expression AXAML bindings can't express directly.</summary>
    [ObservableProperty]
    private string? _soundFileIdSuccessMessage;

    /// <summary>Failure text for the CURRENTLY typed/picked <see cref="SoundFileMmvPath"/> --
    /// <see langword="null"/> whenever it's blank, still validating, or currently valid. See
    /// <see cref="SoundFileIdSuccessMessage"/>'s own doc comment for why this is a separate property.
    /// Non-null also blocks Save (<see cref="SaveCoreUnguardedAsync"/> re-validates fresh rather than
    /// trusting this live UI value, but keeps it in sync so the message shown here always matches
    /// what Save/Apply/Connect will decide).</summary>
    [ObservableProperty]
    private string? _soundFileIdErrorMessage;

    private CancellationTokenSource? _soundFileIdValidationCts;

    partial void OnSoundFileMmvPathChanged(string? value)
    {
        _soundFileIdValidationCts?.Cancel();
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

        var cts = new CancellationTokenSource();
        _soundFileIdValidationCts = cts;
        _ = ValidateSoundFileIdDebouncedAsync(value, cts.Token);
    }

    private async Task ValidateSoundFileIdDebouncedAsync(string path, CancellationToken ct)
    {
        SoundFileIdValidationResult result;
        try
        {
            await Task.Delay(SoundFileIdValidationDebounce, ct).ConfigureAwait(false);
            // Code-review finding: ISstvSessionService.ValidateStationIdSoundFileAsync's own contract
            // is "never throws" -- but Task.Run(f, ct) itself throws OperationCanceledException if ct
            // fires between the delay completing and the delegate starting, on this fire-and-forget
            // task, an unobserved exception. Caught below alongside the delay's own cancellation.
            result = await _sstvSession.ValidateStationIdSoundFileAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal control flow -- a newer edit/pick/mode-switch superseded this one. Not worth a
            // log line, same reasoning as PersistTxVolumeDebouncedAsync's own identical catch.
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

    /// <summary>WPM, wired to actually drive CW-ID dot length -- see <c>CwMorseGenerator</c>'s own
    /// doc comment for why this is a deliberate, user-approved deviation from an apparent legacy bug
    /// (the WPM UI value never actually applied to post-image CW-ID timing there). Literal `28`
    /// (not a reference to `ScanlineStudio.Core.Sstv.StationIdSettings.DefaultCwWpm`, which
    /// `ScanlineStudio.UI` cannot reference -- `UiLayeringArchitectureTests`) is only a brief
    /// pre-load placeholder, same as <see cref="SampleRate"/>/<see cref="SenseLevel"/>'s own
    /// hardcoded-literal-default convention elsewhere in this class; <see cref="LoadSafeAsync"/>
    /// overwrites it immediately via <see cref="ApplyFromSnapshot"/>.</summary>
    [ObservableProperty]
    private int _cwWpm = 28;

    /// <summary>Literal `1000`, same reasoning as <see cref="CwWpm"/>'s own doc comment.</summary>
    [ObservableProperty]
    private double _cwToneFrequencyHz = 1000;

    [ObservableProperty]
    private bool _fskIdTxEnabled;

    [ObservableProperty]
    private bool _fskIdRxEnabled;

    /// <summary>Real, tested backend setting (<c>StationIdSettings.NrRstEnabled</c>, default
    /// <see langword="true"/> per that record's own doc comment) previously had no Options-dialog
    /// control at all -- see <c>OptionsSnapshot</c>'s own doc comment, which this closes out. Static,
    /// user-typed text, not a live "current QSO" exchange field -- that design decision was already
    /// made on <c>StationIdSettings.NrRstText</c>'s own doc comment, not new here.</summary>
    [ObservableProperty]
    private bool _nrRstEnabled = true;

    [ObservableProperty]
    private string? _nrRstText;

    public OptionsWindowViewModel(
        OptionsSettingsService optionsSettingsService,
        ILocalizationService localization,
        IAudioDeviceEnumerator audioDeviceEnumerator,
        ILogbookSessionService logbookSession,
        ISettingsStore settingsStore,
        IRadioSessionService radioSession,
        IHamlibDiscoveryService hamlibDiscovery,
        IFilePickerService filePickerService,
        ISstvSessionService sstvSession,
        ISerialPortEnumerator serialPortEnumerator,
        IReceiveHistoryStore historyStore,
        IAppLocationsService appLocationsService,
        IApplicationRestarter applicationRestarter,
        ILogger<OptionsWindowViewModel> logger)
    {
        _optionsSettingsService = optionsSettingsService;
        _localization = localization;
        _audioDeviceEnumerator = audioDeviceEnumerator;
        _logbookSession = logbookSession;
        _settingsStore = settingsStore;
        _radioSession = radioSession;
        _hamlibDiscovery = hamlibDiscovery;
        _filePickerService = filePickerService;
        _sstvSession = sstvSession;
        _serialPortEnumerator = serialPortEnumerator;
        _historyStore = historyStore;
        _appLocationsService = appLocationsService;
        _applicationRestarter = applicationRestarter;
        _logger = logger;

        _isRadioConnected = radioSession.RigId != "none";
        _connectionEventsSubscription = radioSession.ConnectionEvents.Subscribe(OnConnectionEvent);

        _ = LoadSafeAsync();
        _ = LoadTxVolumeSafeAsync();
        _ = LoadStorageLocationsSafeAsync();
    }

    private readonly IDisposable _connectionEventsSubscription;

    /// <summary>Code-review finding: this dialog's own view-model is <c>AddTransient</c> (a fresh
    /// instance per Options open, <c>MainViewModel</c>'s own <c>GetRequiredService</c> call site) --
    /// without unsubscribing, every dialog open added a permanent subscriber to the singleton
    /// <see cref="IRadioSessionService.ConnectionEvents"/> stream, rooting the whole dead
    /// view-model graph for the app's remaining lifetime. Called from <c>OptionsWindowView</c>'s own
    /// <c>Closed</c> handler, same place <see cref="StopTuneIfActive"/>/<see cref="StopTestPttIfActive"/>
    /// are already called from.</summary>
    public void Dispose() => _connectionEventsSubscription.Dispose();

    /// <summary>Deliberately NOT the same idiom as <see cref="RadioStatusViewModel.OnConnectionEvent"/>'s
    /// own <c>CatLinked</c> -- that property is a "genuinely reachable RIGHT NOW" status light,
    /// intentionally flapping false during a backoff/reconnect retry. This button instead answers
    /// "would <see cref="IRadioSessionService.DisconnectAsync"/> actually do something" -- exactly
    /// <see cref="IRadioSessionService.RigId"/>'s own contract (<c>RadioController</c>'s own doc
    /// comment: reset to "none" ONLY on an explicit Disconnect, never by a transient
    /// reconnect-backoff cycle). Live-reproduced bug this fixes: mirroring CatLinked's flapping
    /// semantics here made the button's own label disagree with what <c>RigId</c> (the actual gate
    /// Test CAT/Test PTT check) said during a poll retry -- a click landed on the wrong action
    /// entirely. <see cref="RadioConnectionState.Connecting"/>/<see cref="RadioConnectionState.Reconnecting"/>/
    /// <see cref="RadioConnectionState.Failed"/>/<see cref="RadioConnectionState.CommandFailed"/> are
    /// ALL excluded -- none of them change <c>RigId</c>, only <see cref="RadioConnectionState.Connected"/>/
    /// <see cref="RadioConnectionState.Disconnected"/> do. Code-review finding: this holds because
    /// every backend's <c>RigId</c> is a fixed compile-time constant per protocol TYPE (e.g.
    /// <c>"hamlib-native"</c>, <c>"rigctld-client"</c>), so <c>RadioController</c>'s own internal
    /// reconnect-after-backoff path (which reassigns <c>_rigId</c> with no published event) always
    /// reassigns the SAME value -- if a future backend ever derives <c>RigId</c> at connect time
    /// instead, that path could change it silently and this property would go stale.</summary>
    private void OnConnectionEvent(RadioConnectionEvent evt)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // IsRadioConnected itself stays exactly as documented above -- only Connected/Disconnected
            // touch it, deliberately, so the button's action/enablement never flaps during a normal
            // backoff retry. RadioLinkStatusMessage is a SEPARATE, purely presentational signal built
            // on IsGenuinelyConnected, which DOES reset on Reconnecting/Failed -- so it must be
            // re-evaluated on every event, not just these two, or it would freeze (stop updating, not
            // show wrong text) for the whole duration of a later backoff episode. Order matters:
            // IsRadioConnected is assigned FIRST so RadioLinkStatusMessage's own getter (which reads
            // IsRadioConnected) sees the settled value when the explicit notify below runs, not the
            // stale one from before this event.
            if (evt.State is RadioConnectionState.Connected or RadioConnectionState.Disconnected)
            {
                IsRadioConnected = evt.State == RadioConnectionState.Connected;
            }

            // Give-up-after-5 feature: a Disconnected event carrying a non-null Reason means the poll
            // loop gave up automatically (RadioController's own give-up branch), not a real Disconnect
            // click (which always publishes reason: null). Surfaced only here, not woven into
            // IsRadioConnected/ConnectRadioTooltip's own logic above -- this dialog may not even be
            // open when a give-up happens; see RadioStatusViewModel.ConnectionGaveUp for the
            // always-reachable must-acknowledge popup that also fires for this same signal.
            if (evt.State == RadioConnectionState.Disconnected && evt.Reason is not null)
            {
                ConnectRadioErrorMessage = _localization.GetString("Options.Radio.Connect.GaveUp", evt.Reason);
            }

            OnPropertyChanged(nameof(RadioLinkStatusMessage));
            OnPropertyChanged(nameof(ShowSwrCutoffCapabilityHint));
        });
    }

    /// <summary>Purely presentational -- see <see cref="OnConnectionEvent"/>'s own comment for why this
    /// is deliberately separate from <see cref="IsRadioConnected"/> rather than folded into it. User-
    /// reported gap: the button correctly says "Disconnect" the instant a backend resolves (see
    /// <see cref="IsRadioConnected"/>'s own doc comment for why that's correct), but nothing told the
    /// operator whether that session had ever actually been verified reachable -- this fills that gap
    /// without touching the button's own action/enablement contract at all. `RadioBackendId != "none"`
    /// guards the "None" backend specifically: its own poll never returns (there is nothing to
    /// confirm), so without this a stray Connected event reaching an open dialog for "no radio
    /// configured" could show "not yet confirmed reachable" for a config with nothing to reach.</summary>
    public string? RadioLinkStatusMessage =>
        IsRadioConnected && !_radioSession.IsGenuinelyConnected && RadioBackendId != "none"
            ? _localization.GetString("Options.Radio.Connect.NotYetConfirmed")
            : null;

    /// <summary>SWR auto-cutoff (2026-08-26, relocated from TxControlsPaneView's own Output card --
    /// see <see cref="LoadSafeAsync"/>'s own doc comment for the load, <see cref="SaveAsync"/>'s for
    /// the save). Deliberately UNCONDITIONALLY editable regardless of <see cref="ShowSwrCutoffCapabilityHint"/>
    /// below -- Options configures ahead of a connection, gating a settings-dialog control on what's
    /// live RIGHT NOW would break that contract (unlike the control this replaced, whose
    /// <c>IsEnabled="{Binding ShowSwrMeter}"</c> greying made sense only because it sat on a live pane
    /// showing a live rig's current state).</summary>
    [ObservableProperty]
    private bool _swrCutoffEnabled;

    [ObservableProperty]
    private double _swrCutoffThreshold = RadioSafetySpec.DefaultSwrCutoffThreshold;

    /// <summary>The capability-awareness the old TX-pane control's <c>IsEnabled="{Binding
    /// ShowSwrMeter}"</c> greying used to provide, in non-disabling form (see
    /// <see cref="SwrCutoffEnabled"/>'s own doc comment for why this control stays editable instead)
    /// -- without this, an flrig user (confirmed zero SWR support) could tick the checkbox above and
    /// get no indication anywhere that it will never fire. Gated on <see cref="IsRadioConnected"/>
    /// (a real session actually exists, matching that property's own "would Disconnect do something"
    /// contract), deliberately NOT <see cref="RadioBackendId"/> (the dropdown SELECTION, which can be
    /// edited to a different, not-yet-connected backend while a genuinely different one stays live --
    /// gating on the dropdown pick would show a stale/wrong capability read for whatever backend is
    /// ACTUALLY connected right now). <see langword="true"/> only once a session exists AND the
    /// currently connected/negotiated backend does not report <see cref="RadioCapabilities.SwrMeter"/>.
    /// Re-evaluated on every <see cref="OnConnectionEvent"/>, not just at load -- <see cref="OnConnectionEvent"/>'s
    /// own doc comment.</summary>
    public bool ShowSwrCutoffCapabilityHint =>
        IsRadioConnected && !_radioSession.Capabilities.HasFlag(RadioCapabilities.SwrMeter);

    public IReadOnlyList<CultureInfo> AvailableCultures => _localization.AvailableCultures;

    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];

    /// <summary>Populated by a successful <see cref="BrowseHamlibLibraryAsync"/>/
    /// <see cref="AutoDetectHamlibAsync"/>/<see cref="ProbeHamlibAsync"/> probe -- empty (never
    /// re-populated with stale entries from a previous library) whenever the probe fails, so the
    /// existing numeric <see cref="HamlibModel"/> TextBox stays the fallback entry path.</summary>
    public ObservableCollection<HamlibRigModelInfo> HamlibRigModels { get; } = [];

    /// <summary>Populated from <see cref="ISerialPortEnumerator.GetPortNames"/> on load and via
    /// <see cref="RefreshSerialPortsCommand"/> -- shared by both the CAT serial port field
    /// (<see cref="HamlibSerialPort"/>) and the PTT port field (<see cref="HamlibPttPort"/>); both
    /// ComboBoxes are editable, so a port not in this list can still be typed by hand.</summary>
    public ObservableCollection<string> AvailableSerialPorts { get; } = [];

    /// <summary>Fixed standard serial baud rates, 1200-115200 -- matches the range the user asked
    /// for.</summary>
    // Code-review finding: was `static`, bound via a plain {Binding} -- that resolves against the
    // DataContext INSTANCE, not a static member, the classic case {x:Static} exists for. Instance
    // property matches AvailableCultures' own established binding shape directly above.
    public IReadOnlyList<int> AvailableBaudRates { get; } = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200];

    [RelayCommand]
    private void RefreshSerialPorts()
    {
        AvailableSerialPorts.Clear();
        foreach (var port in _serialPortEnumerator.GetPortNames())
        {
            AvailableSerialPorts.Add(port);
        }
    }

    /// <summary>Forwarding tab's list-editable destination rows -- see
    /// <see cref="AdifUdpDestinationRowViewModel"/>'s own doc comment for the list-editable-row
    /// pattern this mirrors. Repopulated (not mutated in place) by <see cref="ApplyFromSnapshot"/>
    /// and <see cref="ResetForwardingToDefault"/>, same "Clear() then re-Add" shape as
    /// <see cref="CaptureDevices"/>/<see cref="PlaybackDevices"/> above.</summary>
    public ObservableCollection<AdifUdpDestinationRowViewModel> AdifUdpDestinations { get; } = [];

    /// <summary>User-reported gap: (1) Test CAT/Test PTT's own "disconnect the active radio
    /// connection first" guard (<see cref="TestHamlibConnectionAsync"/>/<see cref="TestPttAsync"/>
    /// below) had no way to actually be satisfied -- <see cref="IRadioSessionService.DisconnectAsync"/>
    /// existed on the interface but no control anywhere in the app called it; (2) the real session
    /// only ever connected at app startup (<c>ConnectUsingSettingsAsync</c> called once from
    /// <c>Program.cs</c>) -- a Save in this dialog needed a full app restart before the main window
    /// would show anything connected at all. Tracks the REAL, persistent session's live connection
    /// state (not the throwaway TEST protocol's, which is what
    /// <see cref="IsTestingConnection"/>/<see cref="IsTestingPtt"/> track) -- see
    /// <see cref="OnConnectionEvent"/>'s own doc comment for why this is deliberately NOT the same
    /// idiom as <see cref="RadioStatusViewModel.CatLinked"/>, despite the superficial similarity.
    /// Do not "simplify" this to match CatLinked -- that was this property's actual first
    /// implementation, and it was a real, live-reproduced bug.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectRadioButtonLabel))]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _isRadioConnected;

    public string ConnectRadioButtonLabel => _localization.GetString(IsRadioConnected ? "Options.Radio.Disconnect" : "Options.Radio.Connect");

    /// <summary>User-reported gap: Connect used to be enabled unconditionally -- including for
    /// "None" (nothing to connect to at all) and for rigctld/Hamlib configs that were never
    /// actually verified to work. Set true (via <see cref="TestRigctldConnectionAsync"/>/
    /// <see cref="TestHamlibConnectionAsync"/>) only by a test that just succeeded for the
    /// CURRENTLY on-screen fields; every partial <c>OnXxxChanged</c> method below that touches a
    /// field the corresponding test spec is built from resets it back to
    /// <see langword="false"/>, so a stale "it worked" from different settings can never authorize
    /// connecting under new, untested ones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _rigctldTestSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _hamlibCatTestSucceeded;

    /// <summary>Deliberately NOT required when <see cref="IsPttMethodVoxSelected"/> -- Hamlib sends
    /// no PTT command at all for VOX (see that property's own doc comment), so there is nothing to
    /// test; requiring it would make Hamlib+VOX permanently unconnectable. See
    /// <see cref="CanConnectRadio"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _hamlibPttTestSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _flrigTestSucceeded;

    /// <summary>Unlike <see cref="HamlibPttTestSucceeded"/>, no VOX-style exemption -- flrig has
    /// exactly one PTT mechanism (<c>rig.set_ptt</c>), always a real keying action, so this is always
    /// required to connect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _flrigPttTestSucceeded;

    /// <summary>No separate PTT test, unlike flrig/Hamlib -- OmniRig's own <c>Rig1.Tx</c> is the
    /// same property this test connection exercises reading from, and legacy never treated PTT as a
    /// separately-verifiable OmniRig mechanism either (spec/03-cat-layer.md's OmniRig section).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnectRadio))]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyPropertyChangedFor(nameof(ConnectRadioTooltip))]
    private bool _omniRigTestSucceeded;

    public bool CanConnectRadio =>
        IsRigctldBackendSelected ? RigctldTestSucceeded :
        IsHamlibBackendSelected ? HamlibCatTestSucceeded && (HamlibPttTestSucceeded || IsPttMethodVoxSelected) :
        IsFlrigBackendSelected ? FlrigTestSucceeded && FlrigPttTestSucceeded :
        IsOmniRigBackendSelected ? OmniRigTestSucceeded :
        false; // None (nothing to connect to) or no backend selected.

    /// <summary>Bound to the Connect/Disconnect button's own <c>IsEnabled</c> -- Connect is gated by
    /// <see cref="CanConnectRadio"/>, but Disconnect never is: you must always be able to
    /// disconnect regardless of test state. <see cref="IsConnectingRadio"/> disables the button for
    /// the duration of Connect's own implicit save (ui_transition_plan.md step 7) -- that save can
    /// take several seconds (a Hamlib native library reload, a sample-rate device reopen), and this
    /// is the only path through this dialog where a button click now kicks off I/O that slow.</summary>
    public bool CanToggleRadioConnection => !IsConnectingRadio && (IsRadioConnected || CanConnectRadio);

    /// <summary>See <see cref="CanToggleRadioConnection"/>'s own doc comment. Code-review finding
    /// (step 7): also gates <see cref="SaveCommand"/>/<see cref="ApplyCommand"/> -- without this, a
    /// Save/Apply click during Connect's own implicit save could resume (once the shared
    /// <see cref="_saveGate"/> is released) and clear <see cref="_hamlibLibraryReloadFailed"/>/
    /// <see cref="SaveErrorMessage"/> BEFORE Connect's own continuation reads them, hiding a genuine
    /// Hamlib-reload failure from the Connect path this step exists to protect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleRadioConnection))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isConnectingRadio;

    /// <summary>Shown via <c>ToolTip.ShowOnDisabled</c> (Avalonia doesn't show tooltips on a
    /// disabled control by default) so a disabled Connect button actually explains why, instead of
    /// just sitting there greyed out with no explanation.</summary>
    public string ConnectRadioTooltip => _localization.GetString(IsRadioConnected switch
    {
        true => "Options.Radio.Disconnect.Help",
        false when IsNoneBackendSelected => "Options.Radio.Connect.Help.None",
        false when IsRigctldBackendSelected && !RigctldTestSucceeded => "Options.Radio.Connect.Help.NeedsTestConnection",
        false when IsHamlibBackendSelected && !HamlibCatTestSucceeded => "Options.Radio.Connect.Help.NeedsTestCat",
        false when IsHamlibBackendSelected && !HamlibPttTestSucceeded && !IsPttMethodVoxSelected => "Options.Radio.Connect.Help.NeedsTestPtt",
        false when IsFlrigBackendSelected && !FlrigTestSucceeded => "Options.Radio.Connect.Help.NeedsTestConnection",
        false when IsFlrigBackendSelected && !FlrigPttTestSucceeded => "Options.Radio.Connect.Help.NeedsTestPtt",
        false when IsOmniRigBackendSelected && !OmniRigTestSucceeded => "Options.Radio.Connect.Help.NeedsTestConnection",
        false => "Options.Radio.Connect.Help",
    });

    [ObservableProperty]
    private string? _connectRadioErrorMessage;

    /// <summary>Toggle, same shape as <see cref="TuneCommand"/>/<see cref="TestPttCommand"/> above --
    /// connects using PERSISTED settings (<see cref="IRadioSessionService.ConnectUsingSettingsAsync"/>'s
    /// own contract, same as app startup), not whatever is currently typed into this dialog. Prior
    /// to ui_transition_plan.md step 7, that meant Test could pass against a freshly-typed value
    /// while Connect silently used the last SAVED one -- Connect now saves first (via
    /// <see cref="SaveCoreAsync"/>, same call <see cref="ApplyAsync"/> uses) so it always connects
    /// with whatever Test just validated. Only when <see cref="_loadSucceeded"/>: mirrors
    /// <see cref="RestartNowAsync"/>'s own guard -- a dialog that failed to load has no real
    /// snapshot to save, and must not persist hardcoded constructor defaults over real settings. The
    /// <see cref="CanConnectRadio"/> check below is a defensive no-op mirroring the button's own
    /// <c>IsEnabled</c> binding (<see cref="CanToggleRadioConnection"/> in XAML) -- same "internal
    /// guard alongside an IsEnabled binding, not a formal CanExecute" idiom <see cref="TestPttAsync"/>
    /// already uses, so a direct <c>ExecuteAsync</c> call (e.g. from a test) can't bypass it
    /// either.</summary>
    [RelayCommand]
    private async Task ToggleRadioConnectionAsync()
    {
        if (IsRadioConnected)
        {
            Log.DisconnectRadioInvoked(_logger);
            try
            {
                // T1-8 (production_audit.md): RadioController.DisconnectAsync now throws
                // TimeoutException if another lifecycle call holds the internal lock past its own
                // bound (a new possibility this method never had to handle before -- every prior
                // internal failure was swallowed and logged inside RadioController itself). Uncaught,
                // this would surface as an unhandled exception on the UI thread via
                // AsyncRelayCommand's own rethrow-on-fault behavior.
                await _radioSession.DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.DisconnectRadioFailed(_logger, ex);
                Dispatcher.UIThread.Post(() => ConnectRadioErrorMessage = _localization.GetString("Options.Radio.Disconnect.Failed", ex.Message));
                return;
            }

            // Stale test-status messages ("already connected") no longer apply once actually
            // disconnected -- same reasoning as OnRadioBackendIdChanged's own clearing below.
            Dispatcher.UIThread.Post(() =>
            {
                TestConnectionStatusMessage = null;
                TestPttErrorMessage = null;
                ConnectRadioErrorMessage = null;
            });
            return;
        }

        if (!CanConnectRadio)
        {
            return;
        }

        Log.ConnectRadioInvoked(_logger);
        ConnectRadioErrorMessage = null;
        IsConnectingRadio = true;
        try
        {
            if (_loadSucceeded && !await SaveCoreAsync())
            {
                ConnectRadioErrorMessage = _localization.GetString("Options.Radio.Connect.SaveFailed", SaveErrorMessage ?? string.Empty);
                return;
            }

            // Hamlib's own library-path reload can fail INSIDE a save that otherwise succeeds
            // (SaveCoreUnguardedAsync sets _hamlibLibraryReloadFailed but still returns true for the
            // rest of the snapshot) -- connecting anyway would silently CAT against the OLD loaded
            // library with no indication anything was wrong. No modal here (unlike
            // ShowPostSaveWarningsAsync, which Save/Apply use) -- this reuses the same inline error
            // slot Connect already shows failures in.
            if (_hamlibLibraryReloadFailed && HamlibLibraryReloadFailedMessage is { } hamlibMessage)
            {
                ConnectRadioErrorMessage = hamlibMessage;
                return;
            }

            await _radioSession.ConnectUsingSettingsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ConnectUsingSettingsAsync's own contract only throws for a genuine factory-resolution
            // failure (e.g. an ambiguous/unregistered backend) -- a real connection/poll problem
            // instead surfaces later via ConnectionEvents (Failed/Reconnecting), same as it always
            // has at app startup, not as an exception here.
            Log.ConnectRadioFailed(_logger, ex);
            Dispatcher.UIThread.Post(() => ConnectRadioErrorMessage = ex.Message);
        }
        finally
        {
            // Code-review finding (step 7): the success path's ConnectUsingSettingsAsync runs
            // ConfigureAwait(false) I/O, so this continuation can land off the UI thread -- every
            // other post-ConfigureAwait(false) property write in this file already goes through
            // Dispatcher.UIThread.Post (see e.g. the ConnectRadioErrorMessage assignment just above)
            // specifically because IsConnectingRadio now also drives NotifyCanExecuteChangedFor on
            // SaveCommand/ApplyCommand, which touches the bound Button.Command directly -- a plain
            // property-changed push (this field's OLD only effect, via CanToggleRadioConnection) has
            // shipped fine off-thread elsewhere in this file, but a direct CanExecute re-check does not.
            Dispatcher.UIThread.Post(() => IsConnectingRadio = false);
        }
    }

    public bool IsRigctldSelected => RadioBackendId == "rigctld";

    /// <summary>Plain computed bool pair backing the Radio/CAT tab's two <c>RadioButton</c>s --
    /// Avalonia's own <c>StringConverters</c> has no "equals this parameter" converter, so this is
    /// simpler than writing a one-off converter class, and matches this ViewModel's existing
    /// <see cref="IsRigctldSelected"/> computed-property idiom.</summary>
    public bool IsNoneBackendSelected
    {
        get => RadioBackendId == "none";
        set
        {
            if (value)
            {
                RadioBackendId = "none";
            }
        }
    }

    public bool IsRigctldBackendSelected
    {
        get => RadioBackendId == "rigctld";
        set
        {
            if (value)
            {
                RadioBackendId = "rigctld";
            }
        }
    }

    public bool IsHamlibBackendSelected
    {
        get => RadioBackendId == "hamlib";
        set
        {
            if (value)
            {
                RadioBackendId = "hamlib";
            }
        }
    }

    public bool IsHamlibSelected => RadioBackendId == "hamlib";

    public bool IsFlrigBackendSelected
    {
        get => RadioBackendId == "flrig";
        set
        {
            if (value)
            {
                RadioBackendId = "flrig";
            }
        }
    }

    public bool IsFlrigSelected => RadioBackendId == "flrig";

    public bool IsOmniRigBackendSelected
    {
        get => RadioBackendId == "omnirig";
        set
        {
            if (value)
            {
                RadioBackendId = "omnirig";
            }
        }
    }

    public bool IsOmniRigSelected => RadioBackendId == "omnirig";

    /// <summary>Gates the OmniRig RadioButton's own <c>IsEnabled</c> -- OmniRig is a Windows-only COM
    /// automation server (<c>OmniRigProtocolFactory.Create</c> throws
    /// <see cref="PlatformNotSupportedException"/> off Windows), so a non-Windows user should never be
    /// able to pick it in the first place rather than discovering the failure only at Connect/Test
    /// time. The OS a process runs on never changes mid-session, so this is a plain computed property
    /// with no change notification needed.</summary>
    public static bool IsOmniRigBackendAvailable => OperatingSystem.IsWindows();

    /// <summary>Shown via <c>ToolTip.ShowOnDisabled</c> (same pattern as
    /// <see cref="ConnectRadioTooltip"/>) so a disabled OmniRig option actually explains why, instead
    /// of just sitting there greyed out.</summary>
    public string OmniRigBackendTooltip => _localization.GetString(
        IsOmniRigBackendAvailable ? "Options.Radio.Backend.OmniRig.Help" : "Options.Radio.Backend.OmniRig.Help.NotWindows");

    /// <summary>Backs the Radio/CAT tab's PTT-method 4-way radio group -- same computed-property
    /// idiom as <see cref="IsNoneBackendSelected"/>/etc., over the existing <see cref="HamlibPttType"/>
    /// string field (no new persisted field for the method itself). Mapping verified against the
    /// real Hamlib source (<c>hamlib/src/conf.c</c>'s <c>ptt_type</c> combo list): CAT sends PTT over
    /// the CAT link itself (<c>"RIG"</c>); VOX sends no PTT command at all -- the rig's own hardware
    /// VOX keys on audio presence (<c>"None"</c>).</summary>
    public bool IsPttMethodVoxSelected
    {
        get => HamlibPttType == "None";
        set
        {
            if (value)
            {
                HamlibPttType = "None";
            }
        }
    }

    public bool IsPttMethodCatSelected
    {
        get => HamlibPttType == "RIG";
        set
        {
            if (value)
            {
                HamlibPttType = "RIG";
            }
        }
    }

    public bool IsPttMethodRtsSelected
    {
        get => HamlibPttType == "RTS";
        set
        {
            if (value)
            {
                HamlibPttType = "RTS";
            }
        }
    }

    public bool IsPttMethodDtrSelected
    {
        get => HamlibPttType == "DTR";
        set
        {
            if (value)
            {
                HamlibPttType = "DTR";
            }
        }
    }

    /// <summary>Gates the PTT-port field -- only RTS/DTR key over a dedicated serial line
    /// (<c>ptt_pathname</c>); CAT keys over the CAT link itself, VOX over none at all.</summary>
    public bool IsPttPortEnabled => IsPttMethodRtsSelected || IsPttMethodDtrSelected;

    /// <summary>Gates the "Test PTT" button -- disabled under VOX (Hamlib sends no PTT command for
    /// it at all, so a test would report false success with nothing actually keyed -- verified
    /// against <c>hamlib/src/rig.c</c>'s <c>RIG_PTT_NONE</c> case) and until a rig model is chosen.</summary>
    public bool CanTestPtt => !IsPttMethodVoxSelected && HamlibModel is > 0;

    /// <summary>Auditor usability review follow-up (2026-08-18) -- Radio/CAT tab's "Test Connection"
    /// button. Deliberately tests whatever is CURRENTLY TYPED into <see cref="RigctldHost"/>/
    /// <see cref="RigctldPort"/> (possibly not yet saved), via <see cref="IRadioSessionService.TestConnectionAsync"/>
    /// -- a fresh, disposable connection attempt that never disturbs the app's own real, persistent
    /// radio session (see that method's own doc comment). <see langword="null"/> means "no test run
    /// yet this dialog session," not a result -- so a freshly opened Options window doesn't show a
    /// stale pass/fail from nothing.</summary>
    [ObservableProperty]
    private string? _testConnectionStatusMessage;

    [ObservableProperty]
    private bool _isTestingConnection;

    private bool CanTestRigctldConnection() => !IsTestingConnection && !string.IsNullOrWhiteSpace(RigctldHost) && RigctldPort is > 0;

    [RelayCommand(CanExecute = nameof(CanTestRigctldConnection))]
    private async Task TestRigctldConnectionAsync()
    {
        // Empty/zero guarded by CanTestRigctldConnection above -- RigctldHost/Port are still
        // nullable properties (a TextBox/NumericUpDown can transiently clear to null while the
        // operator is editing), so the compiler-narrowed local copies below are what actually get
        // passed to the spec, not the possibly-changed-since-CanExecute-ran live properties.
        if (RigctldHost is not { } host || RigctldPort is not { } port)
        {
            return;
        }

        Log.TestRigctldConnectionInvoked(_logger, host, port);
        IsTestingConnection = true;
        // Reset for the duration of this fresh attempt -- Connect must not stay authorized on a
        // stale PREVIOUS success while a new verification is genuinely in flight and could fail.
        RigctldTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(new RigctldConnectionSpec(host, port), cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                RigctldTestSucceeded = result.Success;
                // Tier B audit finding: this posted lambda runs after the outer try/catch has already
                // exited, so its GetString calls were previously unguarded. A locale file with a
                // mismatched format placeholder throws FormatException out of GetString, which used to
                // both escape uncaught onto the dispatcher loop AND leave IsTestingConnection stuck
                // true (Test Connection permanently disabled for the life of the dialog). Caught (not
                // just finally'd) because the exception is real and reachable -- confirmed via a
                // dedicated regression test that failed with an unhandled FormatException from
                // Dispatcher.RunJobs before this catch was added. TestConnectionStatusMessage falls
                // back to null rather than another GetString call, since a broken locale key can't be
                // trusted to safely produce ANY string here.
                try
                {
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestRigctldConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            // TestConnectionAsync's own contract already catches connection/poll failures into
            // RadioConnectionTestResult.Success=false -- this only guards against something
            // unexpected escaping that contract (e.g. a DI/factory-resolution bug), so the dialog
            // never gets stuck showing "Testing..." forever.
            Log.TestRigctldConnectionFailed(_logger, host, port, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestRigctldConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    /// <summary>Radio/CAT tab's flrig "Test connection" button -- same throwaway-protocol contract as
    /// <see cref="TestRigctldConnectionAsync"/> above (tests whatever is CURRENTLY TYPED into
    /// <see cref="FlrigHost"/>/<see cref="FlrigPort"/>, possibly not yet saved), reusing the same
    /// <see cref="TestConnectionStatusMessage"/>/<see cref="IsTestingConnection"/> fields -- safe
    /// since only one backend section is ever visible at a time. Never keys PTT -- reachability only,
    /// see <see cref="TestFlrigPttAsync"/> for the actual PTT-key test.</summary>
    private bool CanTestFlrigConnection() => !IsTestingConnection && !string.IsNullOrWhiteSpace(FlrigHost) && FlrigPort is > 0;

    [RelayCommand(CanExecute = nameof(CanTestFlrigConnection))]
    private async Task TestFlrigConnectionAsync()
    {
        if (FlrigHost is not { } host || FlrigPort is not { } port)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestConnectionStatusMessage = _localization.GetString("Options.Radio.Flrig.AlreadyConnected");
            return;
        }

        Log.TestFlrigConnectionInvoked(_logger, host, port);
        IsTestingConnection = true;
        FlrigTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(new FlrigConnectionSpec(host, port), cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                FlrigTestSucceeded = result.Success;
                try
                {
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestFlrigConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.TestFlrigConnectionFailed(_logger, host, port, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestFlrigConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    /// <summary>Radio/CAT tab's OmniRig "Test connection" button -- same throwaway-protocol
    /// contract as <see cref="TestFlrigConnectionAsync"/> above, but no host/port to validate first
    /// (<see cref="OmniRigConnectionSpec"/> is parameterless -- OmniRig's own config dialog owns rig
    /// selection). No separate PTT test either, see <see cref="OmniRigTestSucceeded"/>'s own doc
    /// comment.</summary>
    private bool CanTestOmniRigConnection() => !IsTestingConnection;

    [RelayCommand(CanExecute = nameof(CanTestOmniRigConnection))]
    private async Task TestOmniRigConnectionAsync()
    {
        if (_radioSession.RigId != "none")
        {
            TestConnectionStatusMessage = _localization.GetString("Options.Radio.OmniRig.AlreadyConnected");
            return;
        }

        Log.TestOmniRigConnectionInvoked(_logger);
        IsTestingConnection = true;
        OmniRigTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(new OmniRigConnectionSpec(), cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                OmniRigTestSucceeded = result.Success;
                try
                {
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestOmniRigConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.TestOmniRigConnectionFailed(_logger, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestOmniRigConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    partial void OnIsTestingConnectionChanged(bool value)
    {
        TestRigctldConnectionCommand.NotifyCanExecuteChanged();
        TestHamlibConnectionCommand.NotifyCanExecuteChanged();
        TestFlrigConnectionCommand.NotifyCanExecuteChanged();
        TestOmniRigConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnRigctldHostChanged(string? value)
    {
        TestRigctldConnectionCommand.NotifyCanExecuteChanged();
        RigctldTestSucceeded = false;
    }

    partial void OnRigctldPortChanged(int? value)
    {
        TestRigctldConnectionCommand.NotifyCanExecuteChanged();
        RigctldTestSucceeded = false;
    }

    partial void OnFlrigHostChanged(string? value)
    {
        TestFlrigConnectionCommand.NotifyCanExecuteChanged();
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
    }

    partial void OnFlrigPortChanged(int? value)
    {
        TestFlrigConnectionCommand.NotifyCanExecuteChanged();
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
    }

    partial void OnHamlibModelChanged(uint? value)
    {
        TestHamlibConnectionCommand.NotifyCanExecuteChanged();
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    // User-reported gap: Connect's own success gate (CanConnectRadio) must go stale the instant any
    // field the Test CAT/Test PTT spec is built from changes -- otherwise a success from BEFORE the
    // edit could wrongly authorize connecting under fields that were never actually tested.
    partial void OnHamlibSerialPortChanged(string? value)
    {
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    partial void OnHamlibBaudRateChanged(int? value)
    {
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    partial void OnHamlibPttTypeChanged(string? value)
    {
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    partial void OnHamlibPttPortChanged(string? value)
    {
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
    }

    /// <summary>Radio/CAT tab's "Test CAT" button -- same throwaway-protocol contract as
    /// <see cref="TestRigctldConnectionAsync"/> above (tests whatever is CURRENTLY TYPED into the
    /// Hamlib fields, possibly not yet saved), reusing the same <see cref="TestConnectionStatusMessage"/>/
    /// <see cref="IsTestingConnection"/> fields -- safe since only one backend section is ever visible
    /// at a time. Refuses to attempt a second, contending connection while the app's real session is
    /// already connected (<see cref="IRadioSessionService.RigId"/> != <c>"none"</c>).</summary>
    private bool CanTestHamlibConnection() => !IsTestingConnection && HamlibModel is > 0;

    [RelayCommand(CanExecute = nameof(CanTestHamlibConnection))]
    private async Task TestHamlibConnectionAsync()
    {
        if (HamlibModel is not { } model)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestConnectionStatusMessage = _localization.GetString("Options.Radio.Hamlib.AlreadyConnected");
            return;
        }

        Log.TestHamlibConnectionInvoked(_logger, model);
        IsTestingConnection = true;
        // Reset for the duration of this fresh attempt -- same reasoning as
        // TestRigctldConnectionAsync's own RigctldTestSucceeded reset above.
        HamlibCatTestSucceeded = false;
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        var spec = new HamlibConnectionSpec(model)
        {
            SerialPort = HamlibSerialPort,
            BaudRate = HamlibBaudRate,
            PttType = HamlibPttType,
            PttPort = HamlibPttPort,
        };
        try
        {
            using var cts = new CancellationTokenSource(MaxTestConnectionDuration);
            var result = await _radioSession.TestConnectionAsync(spec, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                HamlibCatTestSucceeded = result.Success;
                try
                {
                    TestConnectionStatusMessage = result.Success
                        ? _localization.GetString("Options.Radio.TestConnection.Success", result.RigId ?? string.Empty)
                        : _localization.GetString("Options.Radio.TestConnection.Failed", result.ErrorMessage ?? string.Empty);
                }
                catch (Exception ex)
                {
                    Log.TestHamlibConnectionStatusDisplayFailed(_logger, ex);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.TestHamlibConnectionFailed(_logger, model, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.TestHamlibConnectionStatusDisplayFailed(_logger, formatEx);
                    TestConnectionStatusMessage = null;
                }
                finally
                {
                    IsTestingConnection = false;
                }
            });
        }
    }

    /// <summary>Radio/CAT tab's "Test PTT" button -- a real start/stop TOGGLE, same shape as
    /// <see cref="TuneCommand"/> (see that command's own doc comment), but scoped to Hamlib CAT/PTT
    /// (not <see cref="ISstvSessionService"/>) and capped at <see cref="MaxTestPttDuration"/>, not
    /// <see cref="MaxTuneDuration"/> -- this opens its own throwaway connection with no SWR-cutoff
    /// watchdog of its own, see that constant's own doc comment. Disabled in XAML under VOX
    /// (<see cref="CanTestPtt"/>) and refuses to run while the app's real session is already
    /// connected, same reasoning as <see cref="TestHamlibConnectionAsync"/> above.
    ///
    /// Code-review finding: <c>AllowConcurrentExecutions = true</c> is required, not decorative --
    /// CommunityToolkit.Mvvm's generated <c>AsyncRelayCommand</c> defaults to
    /// <see langword="false"/>, which ANDs into <c>CanExecute</c> for the whole duration a command is
    /// already running. Without this, the button greys out the instant the tone starts and the
    /// "Stop test" click (the <see cref="IsTestingPtt"/> branch below) becomes unreachable from the
    /// real UI -- only reachable from a test calling <c>ExecuteAsync</c> directly, which bypasses
    /// <c>CanExecute</c> entirely. <see cref="TuneCommand"/> has this same latent shape; left
    /// untouched here as out of this diff's scope.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestPttAsync()
    {
        if (IsTestingPtt)
        {
            _testPttCts?.Cancel();
            return;
        }

        if (HamlibModel is not { } model || IsPttMethodVoxSelected)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestPttErrorMessage = _localization.GetString("Options.Radio.Hamlib.AlreadyConnected");
            return;
        }

        Log.TestPttInvoked(_logger, model, MaxTestPttDuration.TotalSeconds);
        TestPttErrorMessage = null;
        IsTestingPtt = true;
        // Reset for the duration of this fresh attempt -- same reasoning as
        // TestRigctldConnectionAsync's own RigctldTestSucceeded reset.
        HamlibPttTestSucceeded = false;
        var cts = new CancellationTokenSource();
        _testPttCts = cts;
        var spec = new HamlibConnectionSpec(model)
        {
            SerialPort = HamlibSerialPort,
            BaudRate = HamlibBaudRate,
            PttType = HamlibPttType,
            PttPort = HamlibPttPort,
        };
        try
        {
            // RadioSessionService.TestPttAsync's own contract: always returns a result, never
            // throws -- an early Stop click (cts.Cancel() above) is folded into Success=true there,
            // same "normal control flow" philosophy as TuneAsync's own OperationCanceledException
            // handling. A non-null ErrorMessage here means a REAL failure -- including the
            // un-key-failed-after-every-retry case, which must reach the operator, not be silently
            // dropped.
            var result = await _radioSession.TestPttAsync(spec, MaxTestPttDuration, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => HamlibPttTestSucceeded = result.Success);
            if (!result.Success)
            {
                // T1-7 (production_audit.md): used to assign result.ErrorMessage directly -- raw
                // English, unlike the sibling TestConnectionAsync path, which already wraps its own
                // result through this same localized-template convention. Nested try/catch, same
                // reasoning as TestRigctldConnectionAsync's own posted lambda above -- this runs
                // after the outer try/catch has already exited, so a broken locale key's GetString
                // FormatException would otherwise escape uncaught onto the dispatcher loop.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", result.ErrorMessage ?? string.Empty);
                    }
                    catch (Exception ex)
                    {
                        Log.TestHamlibPttStatusDisplayFailed(_logger, ex);
                        TestPttErrorMessage = null;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.TestPttFailed(_logger, model, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", ex.Message);
                }
                catch (Exception displayEx)
                {
                    Log.TestHamlibPttStatusDisplayFailed(_logger, displayEx);
                    TestPttErrorMessage = null;
                }
            });
        }
        finally
        {
            // Code-review finding: IsTestingPtt goes false LAST, not first -- AllowConcurrentExecutions
            // means a click can re-enter this method the instant IsTestingPtt observably flips, and
            // the only thing gating that re-entry is this exact flag. Clearing it before _testPttCts
            // is nulled/disposed left a narrow window where a click's `_testPttCts?.Cancel()` could
            // race this finally's own cts.Dispose(), throwing ObjectDisposedException. With this
            // order, any click that observes IsTestingPtt == true finds _testPttCts still live.
            _testPttCts = null;
            cts.Dispose();
            IsTestingPtt = false;
        }
    }

    /// <summary>flrig's own PTT-key test -- same start/stop toggle shape as <see cref="TestPttAsync"/>
    /// above (shares <see cref="IsTestingPtt"/>/<see cref="TestPttErrorMessage"/>/<see cref="_testPttCts"/>,
    /// same "only one backend section visible at a time" safety as the shared test-status fields
    /// elsewhere in this class), capped at the same <see cref="MaxTestPttDuration"/>. No
    /// <see cref="CanTestPtt"/>-style VOX exemption check -- flrig has exactly one PTT mechanism.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task TestFlrigPttAsync()
    {
        if (IsTestingPtt)
        {
            _testPttCts?.Cancel();
            return;
        }

        if (FlrigHost is not { } host || FlrigPort is not { } port)
        {
            return;
        }

        if (_radioSession.RigId != "none")
        {
            TestPttErrorMessage = _localization.GetString("Options.Radio.Flrig.AlreadyConnected");
            return;
        }

        Log.TestFlrigPttInvoked(_logger, host, port, MaxTestPttDuration.TotalSeconds);
        TestPttErrorMessage = null;
        IsTestingPtt = true;
        FlrigPttTestSucceeded = false;
        var cts = new CancellationTokenSource();
        _testPttCts = cts;
        var spec = new FlrigConnectionSpec(host, port);
        try
        {
            // RadioSessionService.TestPttAsync's own contract: always returns a result, never
            // throws -- same reasoning as TestPttAsync's own call above.
            var result = await _radioSession.TestPttAsync(spec, MaxTestPttDuration, cts.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => FlrigPttTestSucceeded = result.Success);
            if (!result.Success)
            {
                // T1-7 (production_audit.md): same fix and reasoning as TestPttAsync's own equivalent
                // site above.
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", result.ErrorMessage ?? string.Empty);
                    }
                    catch (Exception ex)
                    {
                        Log.TestFlrigPttStatusDisplayFailed(_logger, ex);
                        TestPttErrorMessage = null;
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Log.TestFlrigPttFailed(_logger, host, port, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    TestPttErrorMessage = _localization.GetString("Options.Radio.TestPtt.Failed", ex.Message);
                }
                catch (Exception displayEx)
                {
                    Log.TestFlrigPttStatusDisplayFailed(_logger, displayEx);
                    TestPttErrorMessage = null;
                }
            });
        }
        finally
        {
            // Same ordering reasoning as TestPttAsync's own finally block above.
            _testPttCts = null;
            cts.Dispose();
            IsTestingPtt = false;
        }
    }

    /// <summary>Called from the window's own Closing/Cancel path so a PTT test left running (rig
    /// possibly still keyed) can't outlive the dialog that started it -- see
    /// <see cref="StopTuneIfActive"/>'s own doc comment for the identical reasoning.</summary>
    public void StopTestPttIfActive() => _testPttCts?.Cancel();

    /// <summary>"Browse..." next to the Hamlib library-path field -- lets the user pick the shared
    /// library file directly instead of typing a path by hand. A successful pick both fills
    /// <see cref="HamlibLibraryPath"/> and immediately probes it (<see cref="RunHamlibProbeAsync"/>),
    /// matching the user-facing "find it, then list the rigs" flow in one action.</summary>
    private bool CanBrowseOrAutoDetectHamlib() => !IsProbingHamlib;

    [RelayCommand(CanExecute = nameof(CanBrowseOrAutoDetectHamlib))]
    private async Task BrowseHamlibLibraryAsync()
    {
        var path = await _filePickerService.PickHamlibLibraryFileAsync().ConfigureAwait(false);
        if (path is null)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => HamlibLibraryPath = path);
        await RunHamlibProbeAsync(path).ConfigureAwait(false);
    }

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

    /// <summary>"Auto-detect" -- runs spec/03-cat-layer.md's discovery tiers 2/3 (no override path)
    /// instead of tier 1. On success, overwrites <see cref="HamlibLibraryPath"/> with whatever was
    /// actually found, so Save persists a concrete tier-1 path from then on rather than leaving the
    /// field blank (which would silently re-run auto-detection every future launch instead of
    /// pinning down what was just confirmed to work).</summary>
    [RelayCommand(CanExecute = nameof(CanBrowseOrAutoDetectHamlib))]
    private Task AutoDetectHamlibAsync() => RunHamlibProbeAsync(overridePath: null, applyResolvedPathOnSuccess: true);

    /// <summary>"Test" next to the hand-typed path field -- re-probes whatever is CURRENTLY TYPED
    /// into <see cref="HamlibLibraryPath"/> (possibly not yet saved), same "test what's on screen,
    /// not what's persisted" contract as <see cref="TestRigctldConnectionAsync"/> above.</summary>
    [RelayCommand(CanExecute = nameof(CanBrowseOrAutoDetectHamlib))]
    private Task ProbeHamlibAsync() => RunHamlibProbeAsync(HamlibLibraryPath);

    private async Task RunHamlibProbeAsync(string? overridePath, bool applyResolvedPathOnSuccess = false)
    {
        Log.HamlibProbeInvoked(_logger, overridePath ?? "(auto-detect)");

        // User-caught crash: BrowseHamlibLibraryAsync reaches this method AFTER an off-thread
        // await (PickHamlibLibraryFileAsync().ConfigureAwait(false)), so these two entry writes
        // used to run on a threadpool thread, not the UI thread -- IsProbingHamlib's generated
        // OnIsProbingHamlibChanged calls NotifyCanExecuteChanged() on the three Hamlib commands,
        // which touches Button.Command and trips Avalonia's VerifyAccess(), an unhandled
        // InvalidOperationException that terminated the process. AutoDetectHamlibAsync/
        // ProbeHamlibAsync never hit this (they call in directly from command invocation, still on
        // the UI thread), which is exactly why only Browse crashed. Matches the exit path below,
        // which already does this correctly.
        Dispatcher.UIThread.Post(() =>
        {
            IsProbingHamlib = true;
            HamlibDiscoveryStatusMessage = _localization.GetString("Options.Radio.Hamlib.Probing");
        });
        try
        {
            var result = await _hamlibDiscovery.ProbeAsync(overridePath).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    ApplyHamlibProbeResult(result, applyResolvedPathOnSuccess);
                }
                catch (Exception ex)
                {
                    Log.HamlibProbeStatusDisplayFailed(_logger, ex);
                    HamlibDiscoveryStatusMessage = null;
                }
                finally
                {
                    IsProbingHamlib = false;
                }
            });
        }
        catch (Exception ex)
        {
            // ProbeAsync's own contract already catches discovery failures into
            // HamlibProbeResult.IsAvailable=false -- this only guards against something unexpected
            // escaping that contract, same reasoning as TestRigctldConnectionAsync's outer catch.
            Log.HamlibProbeFailed(_logger, ex);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    HamlibDiscoveryStatusMessage = _localization.GetString("Options.Radio.Hamlib.Probe.Failed", ex.Message);
                }
                catch (Exception formatEx)
                {
                    Log.HamlibProbeStatusDisplayFailed(_logger, formatEx);
                    HamlibDiscoveryStatusMessage = null;
                }
                finally
                {
                    IsProbingHamlib = false;
                }
            });
        }
    }

    private void ApplyHamlibProbeResult(HamlibProbeResult result, bool applyResolvedPathOnSuccess)
    {
        HamlibRigModels.Clear();
        SelectedHamlibRigModel = null;

        if (!result.IsAvailable)
        {
            HamlibDiscoveryStatusMessage = _localization.GetString(
                "Options.Radio.Hamlib.Probe.NotFound", string.Join("; ", result.Attempts));
            return;
        }

        if (applyResolvedPathOnSuccess && result.ResolvedPath is { } resolvedPath)
        {
            HamlibLibraryPath = resolvedPath;
        }

        foreach (var model in result.RigModels)
        {
            HamlibRigModels.Add(model);
        }

        HamlibDiscoveryStatusMessage = result.RigModels.Count > 0
            ? _localization.GetString("Options.Radio.Hamlib.Probe.Found", result.Version ?? string.Empty, result.ResolvedPath ?? string.Empty, result.RigModels.Count.ToString(CultureInfo.InvariantCulture))
            : _localization.GetString("Options.Radio.Hamlib.Probe.FoundNoModels", result.Version ?? string.Empty, result.ResolvedPath ?? string.Empty);
    }

    /// <summary>Restart-required-settings backlog item 5 (2026-08-28): treats any blank value
    /// (<see langword="null"/>, empty, or whitespace-only) as equal to any other blank value before
    /// comparing -- a plain <c>!=</c> would count clearing an already-blank field (a TextBox binding
    /// yields <c>""</c>, not <see langword="null"/>) as a real change, triggering a pointless native
    /// reload + permanent leak for a no-op. Matches <c>HamlibLibraryLocator</c>'s own
    /// <c>!string.IsNullOrWhiteSpace</c> blank test exactly (`:45`) -- NOT a stricter rule invented
    /// here, so a genuine "clear the field to fall back to auto-detection" case (non-blank -> blank)
    /// still correctly counts as a change and still reloads.
    ///
    /// Configurations-preset backlog, Phase 3 (2026-08-28) update: the preset-switch orchestrator
    /// needs this SAME rule and now has its own copy,
    /// <see cref="ScanlineStudio.Core.Radio.RadioConnectionSettingsExtensions.HamlibPathsAreEquivalent"/>
    /// -- NOT forwarded to from here. Round-2 code-review correction: <c>ScanlineStudio.UI</c> DOES
    /// see <c>ScanlineStudio.Core.Radio</c>'s types, transitively, via its own reference to
    /// <c>ScanlineStudio.Application</c> (which references Core.Radio directly, with no
    /// <c>PrivateAssets</c>) -- it is not that the type is literally unreachable. The real reason for
    /// two copies is architectural discipline, not a compile error: this dialog is meant to reach
    /// Radio settings ONLY through <c>IRadioSessionService</c>'s own Application-layer surface, never
    /// a Core project's types directly, and writing `RadioConnectionSettingsExtensions.HamlibPathsAreEquivalent(...)`
    /// here would be exactly that kind of direct reach-through, however harmless this one specific
    /// helper looks. Two copies, deliberately, kept identical -- see that method's own doc
    /// comment too.</summary>
    private static bool HamlibPathsAreEquivalent(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) && string.IsNullOrWhiteSpace(b) || a == b;

    partial void OnIsProbingHamlibChanged(bool value)
    {
        BrowseHamlibLibraryCommand.NotifyCanExecuteChanged();
        AutoDetectHamlibCommand.NotifyCanExecuteChanged();
        ProbeHamlibCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Radio/CAT tab's own Pwr slider -- deliberately NOT part of this dialog's usual
    /// edit-buffer-committed-only-on-<see cref="SaveCommand"/> pattern (see this class's own doc
    /// comment at the top of the file): it reads/writes <see cref="ISstvSessionService.GetTxVolumePercentAsync"/>/
    /// <see cref="ISstvSessionService.SetTxVolumePercentAsync"/> directly and immediately (debounced,
    /// same shape as <see cref="RadioStatusViewModel.TxVolumePercent"/>'s own header-strip slider),
    /// because the whole point of pairing it with <see cref="TuneCommand"/> is dragging it WHILE a
    /// tone is already playing and watching the radio's own power meter -- gated behind a Save click
    /// it would be useless for that. Same underlying persisted setting as the header's own Pwr
    /// slider, just a separate live-loaded copy (this dialog is a fresh DI-resolved instance each
    /// time it opens, same as every other field here) -- not instantly two-way-bound to the header
    /// while both happen to be open at once, only synced on each one's own load/save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TxVolumeDisplay))]
    private int _txVolumePercent = 100;

    public string TxVolumeDisplay => TxVolumePercent.ToString(CultureInfo.InvariantCulture);

    private bool _suppressTxVolumePersist;
    private CancellationTokenSource? _txVolumePersistCts;

    private async Task LoadTxVolumeSafeAsync()
    {
        try
        {
            var percent = await _sstvSession.GetTxVolumePercentAsync().ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _suppressTxVolumePersist = true;
                    TxVolumePercent = percent;
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

    partial void OnTxVolumePercentChanged(int value)
    {
        if (_suppressTxVolumePersist)
        {
            return;
        }

        // Debounced, same reasoning as RadioStatusViewModel.OnTxVolumePercentChanged's own comment:
        // a slider drag can fire dozens of times a second, and overlapping un-awaited settings-store
        // writes could race each other and let a stale write clobber a fresher one.
        _txVolumePersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _txVolumePersistCts = cts;
        _ = PersistTxVolumeDebouncedAsync(value, cts.Token);
    }

    private async Task PersistTxVolumeDebouncedAsync(int value, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TxVolumePersistDebounce, ct).ConfigureAwait(false);
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

    /// <summary>Toggle state for <see cref="TuneCommand"/> -- <see langword="true"/> while a tone is
    /// keyed, drives the button's own label/affordance between "Tune" and "Stop" (mirrors WSJT-X's
    /// own Tune button, which is a toggle, not a fire-and-forget action).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TuneButtonLabel))]
    private bool _isTuning;

    /// <summary>Resolved (not a raw key) -- same "expose the already-localized display string"
    /// convention as <see cref="TxVolumeDisplay"/>/<c>RadioStatusViewModel.TxVolumeDisplay</c>, since
    /// <c>loc:Translate</c> takes a static key, not a bound one.</summary>
    public string TuneButtonLabel => _localization.GetString(IsTuning ? "Options.Radio.Tune.Stop" : "Options.Radio.Tune");

    [ObservableProperty]
    private string? _tuneErrorMessage;

    private CancellationTokenSource? _tuneCts;

    /// <summary>Keys PTT and transmits a steady <see cref="TuneFrequencyHz"/> tone, same WSJT-X-style
    /// AFC-lock-aid shape as <see cref="RadioStatusViewModel.TuneCommand"/> -- but unlike that one,
    /// this is a real start/stop TOGGLE (matching WSJT-X's own Tune button) so the operator can key
    /// once, drag <see cref="TxVolumePercent"/> while watching the radio's own power meter for as
    /// long as needed, then stop manually -- capped at <see cref="MaxTuneDuration"/> either way as a
    /// safety backstop against a forgotten/stuck Tune keying the rig indefinitely.</summary>
    [RelayCommand]
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
            await _sstvSession.TuneAsync(TuneFrequencyHz, MaxTuneDuration, ct: cts.Token).ConfigureAwait(false);
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
            IsTuning = false;
            _tuneCts = null;
            cts.Dispose();
        }
    }

    /// <summary>Called from the window's own Closing/Cancel path so a Tune tone left running (PTT
    /// still keyed) can't outlive the dialog that started it -- see <see cref="TuneAsync"/>'s own
    /// OperationCanceledException handling, which this feeds.</summary>
    public void StopTuneIfActive() => _tuneCts?.Cancel();

    /// <summary>Toggle state for <see cref="TestPttCommand"/>/<see cref="TestFlrigPttCommand"/> --
    /// shared across both (only one backend section is ever visible at a time, same reasoning as
    /// <see cref="TestConnectionStatusMessage"/>'s own sharing) -- see <see cref="IsTuning"/>'s own
    /// doc comment for the identical pattern.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestPttButtonLabel))]
    [NotifyPropertyChangedFor(nameof(TestFlrigPttButtonLabel))]
    private bool _isTestingPtt;

    public string TestPttButtonLabel => _localization.GetString(IsTestingPtt ? "Options.Radio.Hamlib.TestPtt.Stop" : "Options.Radio.Hamlib.TestPtt");

    public string TestFlrigPttButtonLabel => _localization.GetString(IsTestingPtt ? "Options.Radio.Flrig.TestPtt.Stop" : "Options.Radio.Flrig.TestPtt");

    [ObservableProperty]
    private string? _testPttErrorMessage;

    private CancellationTokenSource? _testPttCts;

    partial void OnSelectedHamlibRigModelChanged(HamlibRigModelInfo? value)
    {
        if (value is not null)
        {
            HamlibModel = value.ModelId;
        }
    }

    /// <summary>Backs the Decode tab's 4-way Sense level radio group -- same computed-bool-property
    /// idiom as <see cref="IsNoneBackendSelected"/>/etc. above. Index order (0=Very low..3=Very high)
    /// matches <c>Option.dfm</c>'s real <c>RGSLvl</c> item order and
    /// <see cref="ScanlineStudio.Core.Sstv.AnalogFmSstvDecoder.SenseLevelPresets"/>.</summary>
    public bool IsSenseLevelVeryLowSelected
    {
        get => SenseLevel == 0;
        set
        {
            if (value)
            {
                SenseLevel = 0;
            }
        }
    }

    public bool IsSenseLevelLowSelected
    {
        get => SenseLevel == 1;
        set
        {
            if (value)
            {
                SenseLevel = 1;
            }
        }
    }

    public bool IsSenseLevelHighSelected
    {
        get => SenseLevel == 2;
        set
        {
            if (value)
            {
                SenseLevel = 2;
            }
        }
    }

    public bool IsSenseLevelVeryHighSelected
    {
        get => SenseLevel == 3;
        set
        {
            if (value)
            {
                SenseLevel = 3;
            }
        }
    }

    /// <summary>Backs the Decode tab's 3-way Demod type radio group -- same computed-bool-property
    /// idiom as <see cref="IsSenseLevelVeryLowSelected"/>/etc above. Item order (0=PLL/1=Zero
    /// crossing/2=Hilbert) matches <c>Option.dfm</c>'s real <c>RGDemType</c> item order and
    /// <see cref="DemodType"/>'s own enum values.</summary>
    public bool IsDemodTypePllSelected
    {
        get => DemodType == DemodType.Pll;
        set
        {
            if (value)
            {
                DemodType = DemodType.Pll;
            }
        }
    }

    public bool IsDemodTypeZeroCrossingSelected
    {
        get => DemodType == DemodType.ZeroCrossing;
        set
        {
            if (value)
            {
                DemodType = DemodType.ZeroCrossing;
            }
        }
    }

    public bool IsDemodTypeHilbertSelected
    {
        get => DemodType == DemodType.Hilbert;
        set
        {
            if (value)
            {
                DemodType = DemodType.Hilbert;
            }
        }
    }

    /// <summary>Backs the Advanced tab's 3-way zero-crossing smoothing-mode radio group (Options stub
    /// backlog item 2) -- same computed-bool-property idiom as <see cref="IsDemodTypePllSelected"/>
    /// above. Item order (0=Iir/1=Fir/2=Off) matches <see cref="ZeroCrossingSmoothingMode"/>'s own
    /// enum values, corroborated (not source-confirmed, see the plan doc) against legacy's real
    /// <c>RGcrossType</c> labels "IIR"/"FIR"/"OFF".</summary>
    public bool IsZeroCrossingSmoothingIirSelected
    {
        get => ZeroCrossingSmoothingMode == ZeroCrossingSmoothingMode.Iir;
        set
        {
            if (value)
            {
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Iir;
            }
        }
    }

    public bool IsZeroCrossingSmoothingFirSelected
    {
        get => ZeroCrossingSmoothingMode == ZeroCrossingSmoothingMode.Fir;
        set
        {
            if (value)
            {
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Fir;
            }
        }
    }

    public bool IsZeroCrossingSmoothingOffSelected
    {
        get => ZeroCrossingSmoothingMode == ZeroCrossingSmoothingMode.Off;
        set
        {
            if (value)
            {
                ZeroCrossingSmoothingMode = ZeroCrossingSmoothingMode.Off;
            }
        }
    }

    /// <summary>Greys the IIR sub-group (order+cutoff fields) unless IIR smoothing is selected --
    /// legacy's real `GBCOI.Enabled` gate (`Option.cpp:197-212`; this port shows the group always
    /// rather than replicating legacy's own hide/show elsewhere, same precedent as
    /// <see cref="IsDemodTypePllSelected"/>'s own AXAML binding).</summary>
    public bool IsZeroCrossingIirGroupEnabled => IsZeroCrossingSmoothingIirSelected;

    /// <summary>Greys the FIR sub-group (smoothing-frequency field) unless FIR smoothing is selected --
    /// legacy's real `GBCOF.Enabled` gate, same reasoning as <see cref="IsZeroCrossingIirGroupEnabled"/>
    /// above.</summary>
    public bool IsZeroCrossingFirGroupEnabled => IsZeroCrossingSmoothingFirSelected;

    /// <summary>Backs the Decode tab's 4-way RX BPF radio group -- same computed-bool-property idiom
    /// as <see cref="IsDemodTypePllSelected"/>/etc above (N-way-exclusive, not
    /// <see cref="CwIdMode"/>'s 2-plus-disabled shape). Item order (0=Off/1=Wide/2=Narrow/
    /// 3=VeryNarrow) matches <c>Option.dfm</c>'s real <c>RGRxBPF</c> item order and
    /// <see cref="RxBpfPreset"/>'s own enum values. "Off" keeps its existing UI label "Normal" (a
    /// deliberate, prior, already-committed relabeling of legacy's real "OFF" item -- not this
    /// property's own naming choice).</summary>
    public bool IsRxBpfOffSelected
    {
        get => RxBpfPreset == RxBpfPreset.Off;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.Off;
            }
        }
    }

    public bool IsRxBpfWideSelected
    {
        get => RxBpfPreset == RxBpfPreset.Wide;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.Wide;
            }
        }
    }

    public bool IsRxBpfNarrowSelected
    {
        get => RxBpfPreset == RxBpfPreset.Narrow;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.Narrow;
            }
        }
    }

    public bool IsRxBpfVeryNarrowSelected
    {
        get => RxBpfPreset == RxBpfPreset.VeryNarrow;
        set
        {
            if (value)
            {
                RxBpfPreset = RxBpfPreset.VeryNarrow;
            }
        }
    }

    /// <summary>Backs the Decode tab's 3-way RX buffering radio group -- same computed-bool-property
    /// idiom as <see cref="IsRxBpfOffSelected"/>/etc above. Item order (0=Off/1=On/2=Extended)
    /// matches <c>Option.dfm</c>'s real <c>RGRBuf</c> item order and <see cref="RxBufferMode"/>'s own
    /// enum values.</summary>
    public bool IsRxBufferOffSelected
    {
        get => RxBufferMode == RxBufferMode.Off;
        set
        {
            if (value)
            {
                RxBufferMode = RxBufferMode.Off;
            }
        }
    }

    public bool IsRxBufferOnSelected
    {
        get => RxBufferMode == RxBufferMode.On;
        set
        {
            if (value)
            {
                RxBufferMode = RxBufferMode.On;
            }
        }
    }

    public bool IsRxBufferExtendedSelected
    {
        get => RxBufferMode == RxBufferMode.Extended;
        set
        {
            if (value)
            {
                RxBufferMode = RxBufferMode.Extended;
            }
        }
    }

    /// <summary>Gates the Decode tab's Auto Slant checkbox row -- legacy's own
    /// <c>CBASlant->Enabled = RGRBuf->ItemIndex ? TRUE : FALSE</c> (`Option.cpp:222`): Auto Slant has
    /// no effect with RX buffering off (no staging buffer to track a correction against), so the
    /// checkbox is disabled, not hidden, matching this dialog's own established per-control disable
    /// convention. Not persisted itself -- purely a UI-enablement derivation from
    /// <see cref="RxBufferMode"/>.</summary>
    public bool IsAutoSlantRowEnabled => RxBufferMode != RxBufferMode.Off;

    /// <summary>Backs the Identification tab's "ID method" radio group -- same computed-bool idiom
    /// as <see cref="IsSenseLevelVeryLowSelected"/>/etc above.</summary>
    public bool IsIdMethodOffSelected
    {
        get => CwIdMode == CwIdMode.Off;
        set
        {
            if (value)
            {
                CwIdMode = CwIdMode.Off;
            }
        }
    }

    public bool IsIdMethodCwSelected
    {
        get => CwIdMode == CwIdMode.Cw;
        set
        {
            if (value)
            {
                CwIdMode = CwIdMode.Cw;
            }
        }
    }

    public bool IsIdMethodSoundFileSelected
    {
        get => CwIdMode == CwIdMode.SoundFile;
        set
        {
            if (value)
            {
                CwIdMode = CwIdMode.SoundFile;
            }
        }
    }

    /// <summary>Backs the Audio tab's 3-way Stereo capture source radio group -- same computed-bool
    /// idiom as <see cref="IsSenseLevelVeryLowSelected"/>/etc above.</summary>
    public bool IsCaptureChannelMonoSelected
    {
        get => CaptureChannelSource == AudioChannelSource.Mono;
        set
        {
            if (value)
            {
                CaptureChannelSource = AudioChannelSource.Mono;
            }
        }
    }

    public bool IsCaptureChannelLeftSelected
    {
        get => CaptureChannelSource == AudioChannelSource.Left;
        set
        {
            if (value)
            {
                CaptureChannelSource = AudioChannelSource.Left;
            }
        }
    }

    public bool IsCaptureChannelRightSelected
    {
        get => CaptureChannelSource == AudioChannelSource.Right;
        set
        {
            if (value)
            {
                CaptureChannelSource = AudioChannelSource.Right;
            }
        }
    }

    /// <summary>Fired on Save (after a successful persist) and on Cancel -- the View closes the
    /// window either way; it does not need to distinguish which.</summary>
    public event Action? RequestClose;

    /// <summary>Fired from <see cref="SaveAsync"/> only (not <see cref="RestartNowAsync"/>, which
    /// already has its own explicit Restart Now/Not Now confirm) when a Save leaves a
    /// database-directory relocation still pending -- genuinely restart-required (2026-08-27 audit,
    /// see <c>PROJECT_BRIEF.md</c>), with no live-apply planned. Round-2 correction (2026-08-28,
    /// restart-required-settings backlog item 5): this used to ALSO fire for a changed
    /// <see cref="HamlibLibraryPath"/> -- that setting is now genuinely live, see
    /// <see cref="HamlibLibraryReloadFailedWarningRequested"/> for its own (differently-shaped)
    /// notice instead. The View awaits this before actually closing the dialog, so the
    /// acknowledgement is seen before the window disappears. <c>Func&lt;Task&gt;</c>, not
    /// <c>Action</c>, specifically so the View can show a real modal dialog and have Save wait for
    /// the user's OK -- restarting itself stays entirely up to the user, this only informs them.</summary>
    public event Func<Task>? RestartRequiredWarningRequested;

    /// <summary>Fired from <see cref="SaveAsync"/> only, when this Save's own
    /// <see cref="IRadioSessionService.RequestHamlibLibraryPathAsync"/> call returned
    /// <c>Applied: false</c>, or threw <see cref="TimeoutException"/> (a concurrent reload still in
    /// progress). Same shape/placement as <see cref="SampleRateChangeDeferredWarningRequested"/>
    /// immediately below -- independent of every other check in <see cref="SaveAsync"/>, all three
    /// can fire on the same Save. Unlike that event, this one's dialog needs a real payload (the
    /// specific failure detail), read from <see cref="HamlibLibraryReloadFailedMessage"/> by the
    /// View's own handler rather than carried on the event itself (this stays a plain
    /// <c>Func&lt;Task&gt;</c>, matching every other dialog-request event on this class).
    /// The path itself was still PERSISTED regardless -- <see cref="OptionsSettingsService.SaveAsync"/>
    /// runs unconditionally, before this call -- so a restart still picks it up; this notice is only
    /// about the LIVE apply not having happened, and that the failure is worth the user's attention
    /// (a typo'd path silently doing nothing would otherwise only surface much later, at Connect
    /// time, with no link back to the Save that caused it).</summary>
    public event Func<Task>? HamlibLibraryReloadFailedWarningRequested;

    /// <summary>Fired from <see cref="SaveAsync"/> only, when this Save's own
    /// <see cref="ISstvSessionService.RequestSampleRateAsync"/> call returned
    /// <see cref="SampleRateApplyResult.DeferredRecordingInProgress"/> -- the new rate WAS persisted
    /// (the whole-dialog snapshot save happens unconditionally, before this call, see
    /// <see cref="SaveCoreAsync"/>) but NOT applied live, because a recording was in progress. Same
    /// <c>Func&lt;Task&gt;</c>/await-before-close shape as <see cref="RestartRequiredWarningRequested"/>
    /// immediately above -- the user must see this before the window closes, since it's the only
    /// place this outcome is surfaced (2026-08-28, user-requested after the live-apply feature
    /// itself shipped).</summary>
    public event Func<Task>? SampleRateChangeDeferredWarningRequested;

    /// <summary>Fired by Restart Now (Config/Database rows), after any dirty edit elsewhere in this
    /// dialog has already been saved successfully -- see <see cref="RestartNowAsync"/>. Distinct
    /// from <see cref="RequestClose"/>: this must close the WHOLE app, not just this dialog, so
    /// <c>MainWindow.axaml.cs</c>'s handler closes the Options window first, then the main window
    /// itself, reusing the exact same shutdown path <c>MainViewModel.ExitRequested</c> already
    /// uses for File &gt; Exit. No process is spawned from here or from that handler -- only
    /// <see cref="IApplicationRestarter.RestartRequested"/> is set; the actual spawn happens from
    /// <c>Program.cs</c>'s own shutdown sequence, strictly after its existing teardown completes
    /// (see <c>Program.HandleLifetimeExit</c>'s own doc comment for why).</summary>
    public event Action? RestartRequested;

    /// <summary>Gates <see cref="SaveCommand"/> -- see <see cref="CanSave"/>.</summary>
    private bool _loadSucceeded;

    /// <summary>Code-review finding (step 7): also excludes Save/Apply while Connect's own implicit
    /// save is in flight -- see <see cref="IsConnectingRadio"/>'s own doc comment for why an
    /// interleaved Save/Apply must not resume and clear shared save-result fields before Connect's
    /// continuation reads them.</summary>
    private bool CanSave() => _loadSucceeded && !IsConnectingRadio;

    /// <summary>Unguarded fire-and-forget from the constructor before this wrap was added -- the
    /// audio enumerator's <c>RefreshAsync</c> call can throw, which used to mean the Options dialog
    /// could open completely blank with no explanation anywhere. Tier B audit finding: on ANY
    /// exception here (not just the audio enumerator's -- LoadAsync/settingsStore.LoadAsync can also
    /// throw), the fields left at their hardcoded constructor defaults used to be exactly what
    /// SaveAsync would persist if the user touched anything else and hit Save, silently overwriting
    /// real settings (callsign, JPEG quality, capture/playback device IDs, ...) with defaults with
    /// zero warning. _loadSucceeded now gates SaveCommand's CanExecute so Save is simply unavailable
    /// until a load has actually completed -- the general-case fix, not a per-field patch, since the
    /// failure isn't specific to any one field.</summary>
    private async Task LoadSafeAsync()
    {
        try
        {
            var snapshot = await _optionsSettingsService.LoadAsync();
            ApplyFromSnapshot(snapshot);
            RefreshSerialPorts();

            var appSettings = await _settingsStore.LoadAsync();
            RememberWindowPosition = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings)?.RememberWindowPosition ?? true;
            JpegQuality = Math.Clamp(appSettings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings)?.JpegQuality ?? 85, 1, 100);

            // SWR auto-cutoff (2026-08-26, relocated here from TxControlsPaneView's own Output card
            // per user request -- see RadioSafetySpec's own doc comment for why this is Abstractions,
            // not Core.Radio, and IRadioSessionService.SafetySettingsChanged's own doc comment for how
            // TxControlsPaneViewModel's live enforcement picks up a Save made here without being
            // reconstructed). NOT part of OptionsSnapshot/_optionsSettingsService -- RadioSafety is its
            // own settings section, saved via _radioSession.SaveSafetySettingsAsync (SaveAsync below),
            // same reasoning as why RadioConnectionSettings itself isn't in the snapshot either.
            var safetySpec = await _radioSession.GetSafetySettingsAsync();
            SwrCutoffEnabled = safetySpec.SwrCutoffEnabled;
            SwrCutoffThreshold = safetySpec.SwrCutoffThreshold;

            await _audioDeviceEnumerator.RefreshAsync();
            CaptureDevices.Clear();
            foreach (var device in _audioDeviceEnumerator.InputDevices)
            {
                CaptureDevices.Add(device);
            }

            PlaybackDevices.Clear();
            foreach (var device in _audioDeviceEnumerator.OutputDevices)
            {
                PlaybackDevices.Add(device);
            }

            // User-reported fix (2026-08-23): an exact Id match can genuinely fail even for the
            // SAME physical device -- backend device ids can churn across a reconnect/profile
            // change (observed live: a PipeWire USB capture node re-created under a new id after a
            // mute toggle). Falls back to the last-known device Name (AudioDeviceSettings.
            // CaptureDeviceName/PlaybackDeviceName's own doc comment) before giving up.
            //
            // Round 3, same day (explicit product decision, overriding this method's own prior
            // "leave the selection blank" fallback for a genuinely-missing device): "if in the file
            // there is an RX/TX device that is not currently attached to the computer, just set it
            // to the OS defaults" -- a configured device matching NEITHER by id nor by name now
            // falls through to whatever device is currently the backend-reported default, same as
            // "nothing configured," instead of leaving the dropdown blank. Mirrors
            // SstvSessionService.TryResolveDeviceAsync's identical three-step fallback (id -> name ->
            // backend default) for the actual runtime resolution.
            SelectedCaptureDevice = snapshot.CaptureDeviceId is { } captureId
                ? CaptureDevices.FirstOrDefault(d => d.Id == captureId)
                    ?? (snapshot.CaptureDeviceName is { } captureName ? CaptureDevices.FirstOrDefault(d => d.Name == captureName) : null)
                    ?? CaptureDevices.FirstOrDefault(d => d.IsDefault)
                : CaptureDevices.FirstOrDefault(d => d.IsDefault);
            SelectedPlaybackDevice = snapshot.PlaybackDeviceId is { } playbackId
                ? PlaybackDevices.FirstOrDefault(d => d.Id == playbackId)
                    ?? (snapshot.PlaybackDeviceName is { } playbackName ? PlaybackDevices.FirstOrDefault(d => d.Name == playbackName) : null)
                    ?? PlaybackDevices.FirstOrDefault(d => d.IsDefault)
                : PlaybackDevices.FirstOrDefault(d => d.IsDefault);

            _loadSucceeded = true;
        }
        catch (Exception ex)
        {
            Log.LoadFailed(_logger, ex);
        }
        finally
        {
            SaveCommand.NotifyCanExecuteChanged();
            // Plan-review finding (step 7): RelayCommand caches CanExecute -- without this,
            // ApplyCommand stays permanently disabled even after a successful load, since nothing
            // else ever notifies it (unlike SaveCommand, which this method already covered).
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }

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
            DatabaseDirectory = await _appLocationsService.GetDatabaseDirectoryAsync();
            PendingDatabaseDirectory = await _appLocationsService.GetPendingDatabaseDirectoryAsync();
            LogDirectory = await _appLocationsService.GetLogDirectoryAsync();
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
            ConfigDirectoryInput = null;
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
            PendingDatabaseDirectory = await _appLocationsService.GetPendingDatabaseDirectoryAsync();
            IsConfirmingDatabaseRestart = PendingDatabaseDirectory is not null;
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

    private void ApplyFromSnapshot(OptionsSnapshot snapshot)
    {
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == snapshot.CultureCode) ?? _localization.CurrentCulture;
        SampleRate = snapshot.SampleRate;
        TxSampleRateOffsetHz = snapshot.TxSampleRateOffsetHz;
        CaptureChannelSource = Enum.IsDefined(snapshot.CaptureChannelSource) ? snapshot.CaptureChannelSource : AudioChannelSource.Mono;
        StereoTxEnabled = snapshot.StereoTxEnabled;
        RadioBackendId = snapshot.RadioBackendId is "none" or "rigctld" or "hamlib" or "flrig" or "omnirig" ? snapshot.RadioBackendId : "none";
        RigctldHost = snapshot.RigctldHost;
        RigctldPort = snapshot.RigctldPort;
        HamlibModel = snapshot.HamlibModel;
        HamlibLibraryPath = snapshot.HamlibLibraryPath;
        // Save-time restart-warning baseline -- see RestartRequiredWarningRequested's own doc
        // comment. Captured here (dialog-open time), not re-captured by ResetRadioToDefault, so a
        // Save after "Reset to default" still correctly detects a real change against what was
        // actually loaded.
        _originalHamlibLibraryPath = snapshot.HamlibLibraryPath;
        HamlibSerialPort = snapshot.HamlibSerialPort;
        HamlibBaudRate = snapshot.HamlibBaudRate;
        // Normalize, not trust -- the old free-text field's own help text used to tell users to
        // type e.g. "RIG_PTT_SERIAL_DTR", which HamlibRadioProtocol's ctor actually rejects, and a
        // persisted null (a fresh install) selects nothing among the 4 fixed radio buttons either.
        // Falls back to "RIG" (CAT) -- the safest common default, needs no separate PTT port.
        // Code-review finding: a real-but-rarer Hamlib token this UI doesn't offer a button for
        // (RIGMICDATA/Parallel/CM108/GPIO/GPION) used to pass this check unchanged and render as a
        // blank radio group -- same symptom this normalization exists to prevent, just narrower.
        // Only the 4 values this UI can actually represent survive as-is.
        HamlibPttType = snapshot.HamlibPttType is "RIG" or "DTR" or "RTS" or "None"
            ? snapshot.HamlibPttType
            : "RIG";
        HamlibPttPort = snapshot.HamlibPttPort;
        FlrigHost = snapshot.FlrigHost;
        FlrigPort = snapshot.FlrigPort;
        Callsign = snapshot.Callsign;
        OperatorName = snapshot.OperatorName;
        OperatorGrid = snapshot.OperatorGrid;
        AutoSyncEnabled = snapshot.AutoSyncEnabled;
        AutoSlantEnabled = snapshot.AutoSlantEnabled;
        AutoStopEnabled = snapshot.AutoStopEnabled;
        SyncRestartEnabled = snapshot.SyncRestartEnabled;
        // Clamp, not trust -- a hand-edited settings.json can persist an out-of-range value; falls
        // back to index 0 ("Very low"), matching legacy's own SetSenseLvl switch `default:` branch
        // (see SstvDecoderSettings.SenseLevel's own doc comment for why 0, not 1, is the fallback
        // here specifically -- deliberately different from the absent-key default).
        SenseLevel = snapshot.SenseLevel is >= 0 and <= 3 ? snapshot.SenseLevel : 0;
        // Clamp, not trust -- same reasoning as SenseLevel above, but unlike SenseLevel both
        // fallbacks (absent AND out-of-range) resolve to the SAME value here (Hilbert), matching
        // SstvDecoderSettings.DemodType's own doc comment, not SenseLevel's "different fallback"
        // shape.
        DemodType = Enum.IsDefined(snapshot.DemodType) ? snapshot.DemodType : DemodType.Hilbert;
        // Clamp, not trust -- same reasoning as DemodType above, both fallbacks (absent AND
        // out-of-range) resolve to the SAME value here (Wide), matching
        // SstvDecoderSettings.RxBpfPreset's own doc comment.
        RxBpfPreset = Enum.IsDefined(snapshot.RxBpfPreset) ? snapshot.RxBpfPreset : RxBpfPreset.Wide;
        // Clamp, not trust -- same reasoning as DemodType/RxBpfPreset above, both fallbacks (absent
        // AND out-of-range) resolve to the SAME value here (On), matching
        // SstvDecoderSettings.RxBufferMode's own doc comment.
        RxBufferMode = Enum.IsDefined(snapshot.RxBufferMode) ? snapshot.RxBufferMode : RxBufferMode.On;
        // Options stub backlog item 1 -- no clamp needed (unlike the enums above), these are plain
        // numeric fields whose only "invalid" case is an unreachable-Nyquist cutoff, clamped
        // decoder-side (PllFmDemodulator.SetTuning) at apply time, not here at load time.
        PllVcoGain = snapshot.PllVcoGain;
        PllLoopOrder = snapshot.PllLoopOrder;
        PllLoopCutoffHz = snapshot.PllLoopCutoffHz;
        PllOutputOrder = snapshot.PllOutputOrder;
        PllOutputCutoffHz = snapshot.PllOutputCutoffHz;
        // Options stub backlog item 2 -- ZeroCrossingSmoothingMode clamped (same reasoning as
        // DemodType/RxBpfPreset/RxBufferMode above, falls back to Iir, matching
        // SstvDecoderSettings.ZeroCrossingSmoothingMode's own absent-value default -- this snapshot
        // field is always already-resolved, never genuinely absent, but a hand-edited settings.json
        // could still carry an out-of-range value through to here); the 3 numeric fields need no
        // clamp, same reasoning as the PLL numeric fields above.
        ZeroCrossingSmoothingMode = Enum.IsDefined(snapshot.ZeroCrossingSmoothingMode) ? snapshot.ZeroCrossingSmoothingMode : ZeroCrossingSmoothingMode.Iir;
        ZeroCrossingOutputOrder = snapshot.ZeroCrossingOutputOrder;
        ZeroCrossingOutputCutoffHz = snapshot.ZeroCrossingOutputCutoffHz;
        ZeroCrossingSmoothingFrequencyHz = snapshot.ZeroCrossingSmoothingFrequencyHz;
        // Options stub backlog item 3 -- no clamp needed, same reasoning as the PLL/zero-crossing
        // numeric fields above (OptionsSettingsService's own load path already resolves absent/
        // out-of-range values before this snapshot is ever constructed).
        TxBpfEnabled = snapshot.TxBpfEnabled;
        TxBpfTapCount = snapshot.TxBpfTapCount;
        TxLpfEnabled = snapshot.TxLpfEnabled;
        TxLpfFrequencyHz = snapshot.TxLpfFrequencyHz;
        QrzLookupEnabled = snapshot.QrzLookupEnabled;
        QrzLookupUsername = snapshot.QrzLookupUsername;
        QrzLookupPassword = snapshot.QrzLookupPassword;
        // Clamp, not trust -- same reasoning as SenseLevel above: a hand-edited settings.json could
        // in principle carry an out-of-range enum value. Falls back to Off, matching CwIdMode's own
        // CLR/legacy default.
        CwIdMode = Enum.IsDefined(snapshot.CwIdMode) ? snapshot.CwIdMode : CwIdMode.Off;
        CwText = snapshot.CwText;
        CwWpm = snapshot.CwWpm;
        CwToneFrequencyHz = snapshot.CwToneFrequencyHz;
        FskIdTxEnabled = snapshot.FskIdTxEnabled;
        FskIdRxEnabled = snapshot.FskIdRxEnabled;
        NrRstEnabled = snapshot.NrRstEnabled;
        NrRstText = snapshot.NrRstText;
        SoundFileMmvPath = snapshot.SoundFileMmvPath;

        AdifUdpDestinations.Clear();
        foreach (var destination in snapshot.AdifUdpDestinations)
        {
            // Tier B audit finding: a null element (a hand-edited/partially-written settings.json can
            // produce "Destinations": [null] via System.Text.Json) used to NRE here and take down the
            // whole load -- matches AdifUdpStreamer.SendAsync's own `d?.Enabled == true` guard on the
            // same data, but skips the row entirely rather than showing a disabled placeholder for
            // data that was never really there.
            if (destination is null)
            {
                continue;
            }

            AdifUdpDestinations.Add(new AdifUdpDestinationRowViewModel
            {
                Enabled = destination.Enabled == true,
                Name = destination.Name,
                Host = destination.Host,
                Port = destination.Port,
                RemoveCommand = RemoveAdifUdpDestinationCommand,
            });
        }
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (await SaveCoreAsync())
        {
            await ShowPostSaveWarningsAsync();
            RequestClose?.Invoke();
        }
    }

    /// <summary>ui_transition_plan.md step 7 (T2-3): saves via <see cref="SaveCoreAsync"/> like
    /// <see cref="SaveAsync"/>, but does NOT close the dialog -- lets an operator persist a Radio/CAT
    /// change (or anything else) and keep editing, instead of Save-close-reopen just to reach
    /// Connect with the values Test just validated.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task ApplyAsync()
    {
        if (await SaveCoreAsync())
        {
            await ShowPostSaveWarningsAsync();
        }
    }

    /// <summary>Extracted from <see cref="SaveAsync"/>'s own post-save sequence (mechanical,
    /// behavior-preserving split) so <see cref="ApplyAsync"/> can run the identical three warnings
    /// without also closing the window.</summary>
    private async Task ShowPostSaveWarningsAsync()
    {
        // See RestartRequiredWarningRequested's own doc comment -- genuinely restart-required
        // with no live-apply, unlike everything else this dialog saves. PendingDatabaseDirectory
        // (not IsConfirmingDatabaseRestart) so a relocation staged in an earlier dialog session,
        // then left pending, still warns here even if the user never revisits that row this
        // time. HamlibLibraryPath is NOT part of this condition anymore (restart-required-settings
        // backlog item 5, 2026-08-28) -- it applies live now, see
        // HamlibLibraryReloadFailedWarningRequested below for its own (different-shaped) notice.
        var needsRestartWarning = PendingDatabaseDirectory is not null;
        if (needsRestartWarning && RestartRequiredWarningRequested is not null)
        {
            await RestartRequiredWarningRequested.Invoke();
        }

        // See SampleRateChangeDeferredWarningRequested's own doc comment. Independent of the
        // restart-warning check above -- both can fire on the same Save/Apply.
        if (_sampleRateChangeDeferred && SampleRateChangeDeferredWarningRequested is not null)
        {
            await SampleRateChangeDeferredWarningRequested.Invoke();
        }

        // See HamlibLibraryReloadFailedWarningRequested's own doc comment. Independent of both
        // checks above -- all three can fire on the same Save/Apply.
        if (_hamlibLibraryReloadFailed && HamlibLibraryReloadFailedWarningRequested is not null)
        {
            await HamlibLibraryReloadFailedWarningRequested.Invoke();
        }
    }

    /// <summary>Plan-review finding (step 7): four callers now reach <see cref="SaveCoreAsync"/>
    /// (Save, Apply, Connect's implicit save, Restart Now) and its body does several SEPARATE
    /// settings-store round trips (a load-modify-write is only safe "because sequential, not
    /// concurrent" per this method's own comments further down) plus a real, never-unloaded native
    /// Hamlib library load -- two overlapping calls could double-load the native library or lose an
    /// update to settings.json. Serializes every call; not re-entrant-safe by design (a caller
    /// awaiting this while another is in flight simply waits its turn).</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    /// <summary>Extracted from <see cref="SaveAsync"/> (mechanical, behavior-preserving split) so
    /// <see cref="RestartNowAsync"/> (Config/Database rows' Restart Now), <see cref="ApplyAsync"/>,
    /// and <see cref="ToggleRadioConnectionCommand"/>'s own Connect branch can save any dirty edit
    /// elsewhere in this dialog WITHOUT closing the window itself, and can check whether it
    /// actually succeeded -- the real <see cref="SaveAsync"/> used to signal failure only by
    /// silently not closing, with no way for another caller to observe that. Returns whether the
    /// save succeeded; <see cref="SaveAsync"/> itself still only closes the window on
    /// <c>true</c>, identical to its own prior externally-observable behavior.</summary>
    private async Task<bool> SaveCoreAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            return await SaveCoreUnguardedAsync();
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> SaveCoreUnguardedAsync()
    {
        // Reset every call -- see the fields' own doc comments for why SaveAsync reads these
        // afterward rather than this method's own bool return.
        _sampleRateChangeDeferred = false;
        _hamlibLibraryReloadFailed = false;
        HamlibLibraryReloadFailedMessage = null;
        SaveErrorMessage = null;

        // ui_transition_plan.md step 8 (T2-2): block Save only when the sound-file ID method is
        // actually SELECTED and a path is configured -- an unconfigured/off sound-file ID must never
        // block Save, matching every other field's own "nothing to validate when unused" convention.
        // Re-validates FRESH here rather than trusting SoundFileIdIsValid's live UI state, which can
        // be mid-debounce or stale (e.g. the file changed on disk since the last keystroke-triggered
        // check).
        if (IsIdMethodSoundFileSelected && !string.IsNullOrWhiteSpace(SoundFileMmvPath))
        {
            // Code-review finding: deliberately NOT ConfigureAwait(false) -- every other await in
            // this method stays on the UI thread so its property writes (this branch's own included)
            // are safe; a dropped SynchronizationContext here would carry through the rest of the
            // method's tail, not just this one write.
            var soundFileResult = await _sstvSession.ValidateStationIdSoundFileAsync(SoundFileMmvPath);
            if (!soundFileResult.IsValid)
            {
                SoundFileIdSuccessMessage = null;
                SoundFileIdErrorMessage = ResolveSoundFileIdFailureMessage(soundFileResult.Failure);
                SaveErrorMessage = _localization.GetString("Options.Radio.SoundFileId.Error.BlocksSave", SoundFileIdErrorMessage);
                return false;
            }
        }

        // The single most useful Debug line in the app for "why didn't my settings take effect"
        // bugs -- logs only the fields that are actually safe to log as-is (host/port/device ids/
        // sample rate/culture/backend id). Code-review correction: an earlier version of this
        // comment claimed "nothing secret-shaped exists in this snapshot today" -- FALSE as of
        // QrzLookupPassword's addition below; that field is deliberately never passed to this log
        // call.
        Log.SaveInvoked(_logger, RadioBackendId, SampleRate, SelectedCulture?.Name);

        var snapshot = new OptionsSnapshot(
            CultureCode: SelectedCulture?.Name,
            CaptureDeviceId: SelectedCaptureDevice?.Id,
            PlaybackDeviceId: SelectedPlaybackDevice?.Id,
            CaptureDeviceName: SelectedCaptureDevice?.Name,
            PlaybackDeviceName: SelectedPlaybackDevice?.Name,
            SampleRate: SampleRate,
            TxSampleRateOffsetHz: TxSampleRateOffsetHz,
            TxBpfEnabled: TxBpfEnabled,
            TxBpfTapCount: TxBpfTapCount,
            TxLpfEnabled: TxLpfEnabled,
            TxLpfFrequencyHz: TxLpfFrequencyHz,
            RadioBackendId: RadioBackendId,
            RigctldHost: RigctldHost,
            RigctldPort: RigctldPort,
            HamlibModel: HamlibModel,
            HamlibLibraryPath: HamlibLibraryPath,
            HamlibSerialPort: HamlibSerialPort,
            HamlibBaudRate: HamlibBaudRate,
            HamlibPttType: HamlibPttType,
            HamlibPttPort: HamlibPttPort,
            FlrigHost: FlrigHost,
            FlrigPort: FlrigPort,
            Callsign: Callsign,
            OperatorName: OperatorName,
            OperatorGrid: OperatorGrid,
            AutoSyncEnabled: AutoSyncEnabled,
            AutoSlantEnabled: AutoSlantEnabled,
            AutoStopEnabled: AutoStopEnabled,
            SyncRestartEnabled: SyncRestartEnabled,
            SenseLevel: SenseLevel,
            DemodType: DemodType,
            RxBpfPreset: RxBpfPreset,
            RxBufferMode: RxBufferMode,
            PllVcoGain: PllVcoGain,
            PllLoopOrder: PllLoopOrder,
            PllLoopCutoffHz: PllLoopCutoffHz,
            PllOutputOrder: PllOutputOrder,
            PllOutputCutoffHz: PllOutputCutoffHz,
            ZeroCrossingSmoothingMode: ZeroCrossingSmoothingMode,
            ZeroCrossingOutputOrder: ZeroCrossingOutputOrder,
            ZeroCrossingOutputCutoffHz: ZeroCrossingOutputCutoffHz,
            ZeroCrossingSmoothingFrequencyHz: ZeroCrossingSmoothingFrequencyHz,
            QrzLookupEnabled: QrzLookupEnabled,
            QrzLookupUsername: QrzLookupUsername,
            QrzLookupPassword: QrzLookupPassword,
            CaptureChannelSource: CaptureChannelSource,
            StereoTxEnabled: StereoTxEnabled,
            CwIdMode: CwIdMode,
            CwText: CwText,
            CwWpm: CwWpm,
            CwToneFrequencyHz: CwToneFrequencyHz,
            FskIdTxEnabled: FskIdTxEnabled,
            FskIdRxEnabled: FskIdRxEnabled,
            NrRstEnabled: NrRstEnabled,
            NrRstText: NrRstText,
            SoundFileMmvPath: SoundFileMmvPath,
            AdifUdpDestinations: AdifUdpDestinations.Select(row => row.ToDestination()).ToList());

        try
        {
            await _optionsSettingsService.SaveAsync(snapshot);

            // Squelch level (user-reported 2026-08-27, "Squelch level" live control): unlike every
            // OTHER SstvDecoderSettings field this dialog owns, SenseLevel is now genuinely live --
            // matches legacy's own Option.cpp:612-613 (SetSenseLvl() called on the live demodulator
            // on every OK, unconditionally). A plain synchronous call, not fire-and-forget: this is
            // Interlocked.Exchange plus a short lock, effectively non-throwing, unlike the real async
            // I/O just above/below it. Idempotent if unchanged (re-applying the same value is a safe
            // no-op, see AnalogFmSstvDecoder.ApplyPendingSenseLevelRequest's own doc comment), so no
            // need to diff against the previously-saved value first. The Receive tab's own "Squelch
            // level" dropdown (RxImagePaneViewModel) is refreshed separately, on this window's own
            // Closed event (see MainWindow.axaml.cs) -- this call is what keeps the running decoder
            // itself in sync with an Options-driven change.
            _sstvSession.RequestSenseLevel(SenseLevel);

            // Auto-Sync/Auto-Stop/Auto-Slant/Sync-Restart (2026-08-27, restart-required-settings
            // backlog item 1): same reasoning as RequestSenseLevel immediately above -- these four
            // are now ALSO genuinely live, no separate PersistXAsync needed (see
            // ISstvSessionService.RequestAutoSyncEnabled's own doc comment for why: none of these
            // four has a standalone out-of-dialog control, so OptionsSettingsService.SaveAsync above
            // already persists them as part of the whole-dialog snapshot). AutoSlantEnabled's own
            // Receive-tab status text is refreshed separately, on this window's own Closed event
            // (see MainWindow.axaml.cs), same pattern as SenseLevel's dropdown above.
            _sstvSession.RequestAutoSyncEnabled(AutoSyncEnabled);
            _sstvSession.RequestAutoStopEnabled(AutoStopEnabled);
            _sstvSession.RequestAutoSlantEnabled(AutoSlantEnabled);
            _sstvSession.RequestSyncRestartEnabled(SyncRestartEnabled);

            // RX BPF preset/Demod type/RX buffer mode (2026-08-27, restart-required-settings backlog
            // item 2): same unconditional-every-Save convention as RequestSenseLevel/RequestAutoSyncEnabled
            // above -- ISstvSessionService.RequestReconfiguration's own doc comment covers why no diff
            // check is needed first (an equality guard against the currently-COMMITTED decoder value
            // already lives inside RestartableSstvDecoder itself). Unlike those, this one is idle-GATED,
            // not applied immediately -- the Receive tab's own Input Chain "BPF" row is refreshed
            // separately, via ISstvSessionService.DecoderInstanceReplaced (RxImagePaneViewModel), not
            // this window's Closed event (see that property's own doc comment for why a pull-based
            // Closed-event refresh would be redundant here).
            _sstvSession.RequestReconfiguration(RxBpfPreset, DemodType, RxBufferMode);

            // PLL demodulator tuning (Options stub backlog item 1, docs/plans/options-stub-item1-pll-tuning-plan.md):
            // same unconditional-every-Save convention as RequestSenseLevel above -- applied
            // immediately (not idle-gated like RequestReconfiguration just above), matching legacy's
            // own live-edit shape (Option.cpp's Save handler calls CPLL::SetVcoGain/MakeLoopLPF/
            // MakeOutLPF directly on the live decoder, no restart).
            _sstvSession.RequestPllTuning(PllVcoGain, PllLoopOrder, PllLoopCutoffHz, PllOutputOrder, PllOutputCutoffHz);

            // Zero-crossing demodulator tuning (Options stub backlog item 2,
            // docs/plans/options-stub-item2-zerocrossing-tuning-plan.md): same shape as PLL tuning
            // immediately above -- legacy's own live-edit shape (Option.cpp's Save handler calls
            // CFQC::CalcLPF directly on the live decoder, no restart).
            _sstvSession.RequestZeroCrossingTuning(ZeroCrossingSmoothingMode, ZeroCrossingOutputOrder, ZeroCrossingOutputCutoffHz, ZeroCrossingSmoothingFrequencyHz);

            // Sample rate (2026-08-27/28, restart-required-settings backlog item 4): genuinely live
            // now too, no longer requiring a restart. Different in
            // kind from every Request* call above -- this can reopen the actual RX capture device, so
            // it gets its OWN try/catch (not folded into the shared try this method is already inside)
            // -- a thrown TimeoutException/failure here must not abort the unrelated writes already
            // committed above (window geometry/JpegQuality/culture below), matching the established
            // SetCultureAsync precedent just below. DeferredRecordingInProgress: the new rate was
            // ALREADY persisted by the whole-dialog snapshot save above (OptionsSnapshot.SampleRate,
            // `await _optionsSettingsService.SaveAsync(snapshot)`), so it takes effect on the next
            // restart regardless of what happens here -- this branch only decides whether the LIVE
            // apply happens now or not, and sets _sampleRateChangeDeferred so SaveAsync can tell the
            // user (2026-08-28, user-requested).
            try
            {
                var sampleRateResult = await _sstvSession.RequestSampleRateAsync(SampleRate);
                if (sampleRateResult == SampleRateApplyResult.DeferredRecordingInProgress)
                {
                    _sampleRateChangeDeferred = true;
                    Log.SampleRateChangeDeferred(_logger, SampleRate);
                }
                else if (sampleRateResult == SampleRateApplyResult.Rejected)
                {
                    Log.SampleRateChangeRejected(_logger, SampleRate);
                }
            }
            catch (Exception ex)
            {
                Log.SampleRateChangeFailed(_logger, SampleRate, ex);
            }

            // Hamlib library path (2026-08-28, restart-required-settings backlog item 5 -- the LAST
            // item of this backlog): genuinely live now too. Different in kind from every other
            // Request* call above -- it performs a real, uncancellable native library load, so it
            // gets its OWN try/catch (a thrown TimeoutException from a concurrent reload still in
            // progress must not abort the unrelated writes below, same reasoning as the sample-rate
            // block above). Only called when the path actually changed (normalized so a blank-to-blank
            // reformat, e.g. null vs "", doesn't count as a change -- IHamlibLibraryReconfiguration's
            // own doc comment: every call performs a fresh native load that is NEVER unloaded, so
            // calling this unconditionally on every Save would leak a new loaded copy every time,
            // whether or not the field was actually touched).
            if (!HamlibPathsAreEquivalent(HamlibLibraryPath, _originalHamlibLibraryPath))
            {
                try
                {
                    var reloadResult = await _radioSession.RequestHamlibLibraryPathAsync(HamlibLibraryPath);
                    if (reloadResult is { Applied: true })
                    {
                        // Only refresh the baseline on success -- see _originalHamlibLibraryPath's own
                        // doc comment for why a failed reload must NOT refresh it (a later Save must
                        // still be able to retry).
                        _originalHamlibLibraryPath = HamlibLibraryPath;
                        Log.HamlibLibraryPathApplied(_logger, reloadResult.ResolvedPath ?? "(unknown)");
                    }
                    else if (reloadResult is { Applied: false } rejected)
                    {
                        _hamlibLibraryReloadFailed = true;
                        HamlibLibraryReloadFailedMessage = _localization.GetString(
                            "Options.HamlibLibraryReloadFailedDialog.Message", string.Join("; ", rejected.Attempts));
                        Log.HamlibLibraryPathRejected(_logger, string.Join("; ", rejected.Attempts));
                    }

                    // reloadResult is null when the registered IRadioProtocolFactory doesn't
                    // implement IHamlibLibraryReconfiguration at all (e.g. a fake used by a test) --
                    // matches every other is-test-gated Request* call's own silent-skip convention.
                }
                catch (Exception ex)
                {
                    // Round-2 plan-review finding: must NOT be a silent swallow (the nearest-shaped
                    // precedent, SetCultureAsync's own catch below, logs-and-drops) -- a
                    // TimeoutException here means the path change genuinely did not apply, and the
                    // user needs to see that, not just have it vanish into a log line.
                    _hamlibLibraryReloadFailed = true;
                    HamlibLibraryReloadFailedMessage = _localization.GetString(
                        "Options.HamlibLibraryReloadFailedDialog.Message", ex.Message);
                    Log.HamlibLibraryPathReloadThrew(_logger, ex);
                }
            }

            // SWR auto-cutoff (2026-08-26): NOT part of `snapshot`/OptionsSnapshot above -- RadioSafety
            // is its own settings section, saved through IRadioSessionService.SaveSafetySettingsAsync.
            // T0-2: both this call and the geometry/ImageExport write below now go through
            // ISettingsStore.UpdateAsync, so they're safe against each other (and any other concurrent
            // writer) regardless of sequencing -- no longer safe "only because this call happens to be
            // sequenced between two whole-document saves," as it was before UpdateAsync existed.
            // Also raises IRadioSessionService.SafetySettingsChanged, which is how
            // TxControlsPaneViewModel's live enforcement picks up the new value without being
            // reconstructed.
            await _radioSession.SaveSafetySettingsAsync(new RadioSafetySpec(SwrCutoffEnabled, SwrCutoffThreshold));

            // See RememberWindowPosition's own doc comment for why this bypasses
            // _optionsSettingsService entirely. Preserves Left/Top/Width/Height as-is -- those are
            // MainWindow's own domain (captured passively on Closing), not user-edited fields here.
            // T0-2: both captured into locals here, before UpdateAsync -- both are [ObservableProperty]
            // (UI-thread-affine), and UpdateAsync's own mutate lambda may run on any thread, so they
            // must not be read from inside it. One atomic UpdateAsync call, not two independent
            // LoadAsync/SaveAsync round trips (that's what let this whole-document save race a
            // concurrent writer of e.g. AudioDeviceSettings.TxVolumePercent in the first place).
            var rememberWindowPosition = RememberWindowPosition;
            var jpegQuality = JpegQuality;
            await _settingsStore.UpdateAsync(appSettings =>
            {
                var currentGeometry = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings) ?? new WindowGeometrySettings();
                var updatedAppSettings = appSettings.WithSection(
                    WindowGeometrySettings.SectionKey, currentGeometry with { RememberWindowPosition = rememberWindowPosition }, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
                var currentImageExport = appSettings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings) ?? new ImageExportSettings();
                return updatedAppSettings.WithSection(
                    ImageExportSettings.SectionKey, currentImageExport with { JpegQuality = jpegQuality }, ImageExportSettingsJsonContext.Default.ImageExportSettings);
            });

            if (SelectedCulture is { } culture && !culture.Equals(_localization.CurrentCulture))
            {
                try
                {
                    await _localization.SetCultureAsync(culture);
                }
                catch (Exception ex)
                {
                    Log.SetCultureFailed(_logger, culture.Name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.SaveFailed(_logger, ex);
            // ui_transition_plan.md step 7 (T2-3): this used to signal failure ONLY by the caller
            // not closing the dialog, with no message anywhere -- now every SaveCoreAsync caller
            // (Save, Apply, Connect's implicit save) can show the operator what actually went wrong.
            SaveErrorMessage = ex.Message;
            return false;
        }

        return true;
    }

    /// <summary>ui_transition_plan.md step 7 (T2-3): non-null after a failed Save/Apply/Connect
    /// implicit-save, cleared at the top of every <see cref="SaveCoreUnguardedAsync"/> call. Bound
    /// to a TextBlock near the bottom button row.</summary>
    [ObservableProperty]
    private string? _saveErrorMessage;

    [RelayCommand]
    private void Cancel()
    {
        Log.CancelInvoked(_logger);
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void ResetGeneralToDefault()
    {
        Log.ResetSectionInvoked(_logger, "General");
        // Tier B audit finding: OptionsSettingsService.Defaults.CultureCode is always null (the
        // record default), so a bare FirstOrDefault here always misses and used to blank the
        // Language ComboBox on every Reset -- ApplyFromSnapshot already has this exact fallback for
        // the same reason (see its own line), applied here too.
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == OptionsSettingsService.Defaults.CultureCode) ?? _localization.CurrentCulture;
        RememberWindowPosition = true;
        JpegQuality = 85;
    }

    [RelayCommand]
    private void ResetAudioToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Audio");
        var defaults = OptionsSettingsService.Defaults;
        SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == defaults.CaptureDeviceId);
        SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.Id == defaults.PlaybackDeviceId);
        SampleRate = defaults.SampleRate;
        TxSampleRateOffsetHz = defaults.TxSampleRateOffsetHz;
        CaptureChannelSource = defaults.CaptureChannelSource;
        StereoTxEnabled = defaults.StereoTxEnabled;
    }

    [RelayCommand]
    private void ResetRadioToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Radio");
        var defaults = OptionsSettingsService.Defaults;
        RadioBackendId = defaults.RadioBackendId;
        RigctldHost = defaults.RigctldHost;
        RigctldPort = defaults.RigctldPort;
        HamlibModel = defaults.HamlibModel;
        HamlibLibraryPath = defaults.HamlibLibraryPath;
        HamlibSerialPort = defaults.HamlibSerialPort;
        HamlibBaudRate = defaults.HamlibBaudRate;
        // "RIG" (CAT), not defaults.HamlibPttType (null) -- see ApplyFromSnapshot's own comment for
        // why null selects nothing among the 4 fixed radio buttons.
        HamlibPttType = "RIG";
        HamlibPttPort = defaults.HamlibPttPort;
        FlrigHost = defaults.FlrigHost;
        FlrigPort = defaults.FlrigPort;
        // Tier B audit finding: sibling ResetQrzToDefault already clears its own test-result status
        // (TestQrzLookupStatus) -- this one didn't, so a prior "Connected to IC-7300" success line
        // stayed visible under the now-blank host field after a reset.
        TestConnectionStatusMessage = null;
        TestPttErrorMessage = null;
        HamlibDiscoveryStatusMessage = null;
        HamlibRigModels.Clear();
        SelectedHamlibRigModel = null;
        // Explicit, not relying on the OnXxxChanged resets above firing as a side effect --
        // [ObservableProperty]'s generated setter skips the changed-callback entirely when the new
        // value equals the old one (e.g. resetting an already-default field), which would otherwise
        // leave a stale "tested" flag from BEFORE the reset still authorizing Connect afterward.
        RigctldTestSucceeded = false;
        HamlibCatTestSucceeded = false;
        HamlibPttTestSucceeded = false;
        FlrigTestSucceeded = false;
        FlrigPttTestSucceeded = false;
        OmniRigTestSucceeded = false;
        // Not part of OptionsSettingsService.Defaults -- RadioSafety is its own settings section, not
        // in OptionsSnapshot (see SaveAsync's own comment for why). RadioSafetySpec.DefaultSwrCutoffThreshold
        // is the single source of truth for this default, shared with RadioSafetySettings' own
        // property initializer.
        SwrCutoffEnabled = false;
        SwrCutoffThreshold = RadioSafetySpec.DefaultSwrCutoffThreshold;
    }

    [RelayCommand]
    private void ResetTxToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Tx");
        Callsign = OptionsSettingsService.Defaults.Callsign;
        OperatorName = OptionsSettingsService.Defaults.OperatorName;
        OperatorGrid = OptionsSettingsService.Defaults.OperatorGrid;
    }

    [RelayCommand]
    private void ResetDecodeToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Decode");
        var defaults = OptionsSettingsService.Defaults;
        AutoSyncEnabled = defaults.AutoSyncEnabled;
        AutoSlantEnabled = defaults.AutoSlantEnabled;
        AutoStopEnabled = defaults.AutoStopEnabled;
        SyncRestartEnabled = defaults.SyncRestartEnabled;
        SenseLevel = defaults.SenseLevel;
        DemodType = defaults.DemodType;
        RxBpfPreset = defaults.RxBpfPreset;
        RxBufferMode = defaults.RxBufferMode;
    }

    /// <summary>Advanced tab's own "Reset section" button -- the first real, resettable content on
    /// that tab (Options stub backlog item 1); scoped to the PLL, zero-crossing, and TX BPF/LPF
    /// tuning fields wired so far (items 2 and 3 added the latter two), same incremental-growth
    /// pattern as every other <c>Reset*ToDefault</c> command here.</summary>
    [RelayCommand]
    private void ResetAdvancedToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Advanced");
        var defaults = OptionsSettingsService.Defaults;
        PllVcoGain = defaults.PllVcoGain;
        PllLoopOrder = defaults.PllLoopOrder;
        PllLoopCutoffHz = defaults.PllLoopCutoffHz;
        PllOutputOrder = defaults.PllOutputOrder;
        PllOutputCutoffHz = defaults.PllOutputCutoffHz;
        ZeroCrossingSmoothingMode = defaults.ZeroCrossingSmoothingMode;
        ZeroCrossingOutputOrder = defaults.ZeroCrossingOutputOrder;
        ZeroCrossingOutputCutoffHz = defaults.ZeroCrossingOutputCutoffHz;
        ZeroCrossingSmoothingFrequencyHz = defaults.ZeroCrossingSmoothingFrequencyHz;
        TxBpfEnabled = defaults.TxBpfEnabled;
        TxBpfTapCount = defaults.TxBpfTapCount;
        TxLpfEnabled = defaults.TxLpfEnabled;
        TxLpfFrequencyHz = defaults.TxLpfFrequencyHz;
    }

    [RelayCommand]
    private void ResetQrzToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Qrz");
        var defaults = OptionsSettingsService.Defaults;
        QrzLookupEnabled = defaults.QrzLookupEnabled;
        QrzLookupUsername = defaults.QrzLookupUsername;
        QrzLookupPassword = defaults.QrzLookupPassword;
        TestQrzLookupStatus = null;
    }

    [RelayCommand]
    private void ResetIdentificationToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Identification");
        var defaults = OptionsSettingsService.Defaults;
        CwIdMode = defaults.CwIdMode;
        CwText = defaults.CwText;
        CwWpm = defaults.CwWpm;
        CwToneFrequencyHz = defaults.CwToneFrequencyHz;
        FskIdTxEnabled = defaults.FskIdTxEnabled;
        FskIdRxEnabled = defaults.FskIdRxEnabled;
        NrRstEnabled = defaults.NrRstEnabled;
        NrRstText = defaults.NrRstText;
        SoundFileMmvPath = defaults.SoundFileMmvPath;
    }

    [RelayCommand]
    private void ResetForwardingToDefault()
    {
        Log.ResetSectionInvoked(_logger, "Forwarding");
        // Reads OptionsSettingsService.Defaults (today an empty list) rather than hardcoding
        // Clear() -- same "never hardcoded again at the call site" convention that property's own
        // doc comment states, so this stays correct if a future default ever seeds a destination.
        AdifUdpDestinations.Clear();
        foreach (var destination in OptionsSettingsService.Defaults.AdifUdpDestinations)
        {
            AdifUdpDestinations.Add(new AdifUdpDestinationRowViewModel
            {
                Enabled = destination.Enabled == true,
                Name = destination.Name,
                Host = destination.Host,
                Port = destination.Port,
                RemoveCommand = RemoveAdifUdpDestinationCommand,
            });
        }
    }

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

    [RelayCommand]
    private void RequestResetAll() => IsConfirmingResetAll = true;

    [RelayCommand]
    private void ConfirmResetAll()
    {
        Log.ConfirmResetAllInvoked(_logger);
        ResetGeneralToDefault();
        ResetAudioToDefault();
        ResetRadioToDefault();
        ResetTxToDefault();
        ResetDecodeToDefault();
        ResetQrzToDefault();
        ResetIdentificationToDefault();
        ResetForwardingToDefault();
        IsConfirmingResetAll = false;
    }

    [RelayCommand]
    private void CancelResetAll() => IsConfirmingResetAll = false;

    partial void OnRadioBackendIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsRigctldSelected));
        OnPropertyChanged(nameof(IsHamlibSelected));
        OnPropertyChanged(nameof(IsFlrigSelected));
        OnPropertyChanged(nameof(IsOmniRigSelected));
        OnPropertyChanged(nameof(IsNoneBackendSelected));
        OnPropertyChanged(nameof(IsRigctldBackendSelected));
        OnPropertyChanged(nameof(IsHamlibBackendSelected));
        OnPropertyChanged(nameof(IsFlrigBackendSelected));
        OnPropertyChanged(nameof(IsOmniRigBackendSelected));

        // Plan-review finding: without this, a stale rigctld test result stays visible after
        // switching to the Hamlib panel (they share TestConnectionStatusMessage), and vice versa --
        // both panels are never shown at once, but the message field is.
        TestConnectionStatusMessage = null;
        TestPttErrorMessage = null;

        // Code-review finding: RadioLinkStatusMessage reads RadioBackendId (the "None" guard) but
        // nothing re-raised it on a backend switch -- since ApplyFromSnapshot sets the real backend
        // AFTER the dialog's first binding evaluation (it runs from LoadSafeAsync, a fire-and-forget
        // task started in the constructor), a dialog opened on an already-broken session could show
        // nothing until the next Reconnecting tick, up to 30s away at a saturated backoff cap.
        OnPropertyChanged(nameof(RadioLinkStatusMessage));
    }

    partial void OnSenseLevelChanged(int value)
    {
        OnPropertyChanged(nameof(IsSenseLevelVeryLowSelected));
        OnPropertyChanged(nameof(IsSenseLevelLowSelected));
        OnPropertyChanged(nameof(IsSenseLevelHighSelected));
        OnPropertyChanged(nameof(IsSenseLevelVeryHighSelected));
    }

    partial void OnDemodTypeChanged(DemodType value)
    {
        OnPropertyChanged(nameof(IsDemodTypePllSelected));
        OnPropertyChanged(nameof(IsDemodTypeZeroCrossingSelected));
        OnPropertyChanged(nameof(IsDemodTypeHilbertSelected));
    }

    partial void OnZeroCrossingSmoothingModeChanged(ZeroCrossingSmoothingMode value)
    {
        OnPropertyChanged(nameof(IsZeroCrossingSmoothingIirSelected));
        OnPropertyChanged(nameof(IsZeroCrossingSmoothingFirSelected));
        OnPropertyChanged(nameof(IsZeroCrossingSmoothingOffSelected));
        OnPropertyChanged(nameof(IsZeroCrossingIirGroupEnabled));
        OnPropertyChanged(nameof(IsZeroCrossingFirGroupEnabled));
    }

    partial void OnRxBpfPresetChanged(RxBpfPreset value)
    {
        OnPropertyChanged(nameof(IsRxBpfOffSelected));
        OnPropertyChanged(nameof(IsRxBpfWideSelected));
        OnPropertyChanged(nameof(IsRxBpfNarrowSelected));
        OnPropertyChanged(nameof(IsRxBpfVeryNarrowSelected));
    }

    partial void OnRxBufferModeChanged(RxBufferMode value)
    {
        OnPropertyChanged(nameof(IsRxBufferOffSelected));
        OnPropertyChanged(nameof(IsRxBufferOnSelected));
        OnPropertyChanged(nameof(IsRxBufferExtendedSelected));
        OnPropertyChanged(nameof(IsAutoSlantRowEnabled));
    }

    partial void OnCaptureChannelSourceChanged(AudioChannelSource value)
    {
        OnPropertyChanged(nameof(IsCaptureChannelMonoSelected));
        OnPropertyChanged(nameof(IsCaptureChannelLeftSelected));
        OnPropertyChanged(nameof(IsCaptureChannelRightSelected));
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "Loading Options failed; dialog may render with defaults")]
        public static partial void LoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Save invoked: radioBackend={RadioBackendId}, sampleRate={SampleRate}, culture={CultureCode}")]
        public static partial void SaveInvoked(ILogger logger, string radioBackendId, int sampleRate, string? cultureCode);

        [LoggerMessage(Level = LogLevel.Error, Message = "Save failed; settings not persisted")]
        public static partial void SaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetCultureAsync({Culture}) failed after a successful settings save")]
        public static partial void SetCultureFailed(ILogger logger, string culture, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sample rate change to {SampleRate}Hz deferred -- a recording is in progress")]
        public static partial void SampleRateChangeDeferred(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sample rate change to {SampleRate}Hz rejected -- the decoder kept its previous rate")]
        public static partial void SampleRateChangeRejected(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Error, Message = "Sample rate change to {SampleRate}Hz failed after a successful settings save")]
        public static partial void SampleRateChangeFailed(ILogger logger, int sampleRate, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Hamlib library path applied live: {ResolvedPath}")]
        public static partial void HamlibLibraryPathApplied(ILogger logger, string resolvedPath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hamlib library path reload rejected -- previous library (if any) stays installed: {Attempts}")]
        public static partial void HamlibLibraryPathRejected(ILogger logger, string attempts);

        [LoggerMessage(Level = LogLevel.Error, Message = "Hamlib library path reload threw after a successful settings save")]
        public static partial void HamlibLibraryPathReloadThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the Storage section's current locations failed")]
        public static partial void StorageLocationsLoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Saving the RX images directory failed")]
        public static partial void ImagesDirectorySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Saving the auto-save-audio settings failed")]
        public static partial void AudioDirectorySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Staging the config directory failed")]
        public static partial void ConfigDirectorySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Staging the database directory failed")]
        public static partial void DatabaseDirectorySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Relocating the log directory failed")]
        public static partial void LogDirectorySaveFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TestRigctldConnection invoked: host={Host}, port={Port}")]
        public static partial void TestRigctldConnectionInvoked(ILogger logger, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestRigctldConnection({Host}:{Port}) threw unexpectedly")]
        public static partial void TestRigctldConnectionFailed(ILogger logger, string host, int port, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the connection-test result status message failed; status left blank")]
        public static partial void TestRigctldConnectionStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Hamlib probe invoked: path={Path}")]
        public static partial void HamlibProbeInvoked(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Hamlib probe threw unexpectedly")]
        public static partial void HamlibProbeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the Hamlib probe result status message failed; status left blank")]
        public static partial void HamlibProbeStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Cancel invoked")]
        public static partial void CancelInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Reset section to default: {Section}")]
        public static partial void ResetSectionInvoked(ILogger logger, string section);

        [LoggerMessage(Level = LogLevel.Information, Message = "Reset ALL to defaults confirmed")]
        public static partial void ConfirmResetAllInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QRZ credentials test completed: success={Success}")]
        public static partial void TestQrzLookupCompleted(ILogger logger, bool success);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ credentials test threw")]
        public static partial void TestQrzLookupFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading Pwr for the Radio/CAT tab failed; left at its default")]
        public static partial void LoadTxVolumeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting Pwr={Percent} from the Radio/CAT tab failed")]
        public static partial void PersistTxVolumeFailed(ILogger logger, int percent, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Tune invoked: {FrequencyHz}Hz for up to {MaxSeconds}s")]
        public static partial void TuneInvoked(ILogger logger, double frequencyHz, double maxSeconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Tune failed")]
        public static partial void TuneFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TestHamlibConnection invoked: model={Model}")]
        public static partial void TestHamlibConnectionInvoked(ILogger logger, uint model);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestHamlibConnection(model={Model}) threw unexpectedly")]
        public static partial void TestHamlibConnectionFailed(ILogger logger, uint model, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the Hamlib connection-test result status message failed; status left blank")]
        public static partial void TestHamlibConnectionStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TestPtt invoked: model={Model} for up to {MaxSeconds}s")]
        public static partial void TestPttInvoked(ILogger logger, uint model, double maxSeconds);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestPtt(model={Model}) threw unexpectedly")]
        public static partial void TestPttFailed(ILogger logger, uint model, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the Hamlib PTT-test result status message failed; status left blank")]
        public static partial void TestHamlibPttStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TestFlrigConnection invoked: host={Host}, port={Port}")]
        public static partial void TestFlrigConnectionInvoked(ILogger logger, string host, int port);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestFlrigConnection({Host}:{Port}) threw unexpectedly")]
        public static partial void TestFlrigConnectionFailed(ILogger logger, string host, int port, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the flrig connection-test result status message failed; status left blank")]
        public static partial void TestFlrigConnectionStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TestFlrigPtt invoked: {Host}:{Port} for up to {MaxSeconds}s")]
        public static partial void TestFlrigPttInvoked(ILogger logger, string host, int port, double maxSeconds);

        [LoggerMessage(Level = LogLevel.Debug, Message = "TestOmniRigConnection invoked")]
        public static partial void TestOmniRigConnectionInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestOmniRigConnection threw unexpectedly")]
        public static partial void TestOmniRigConnectionFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the OmniRig connection-test result status message failed; status left blank")]
        public static partial void TestOmniRigConnectionStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "TestFlrigPtt({Host}:{Port}) threw unexpectedly")]
        public static partial void TestFlrigPttFailed(ILogger logger, string host, int port, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Formatting the flrig PTT-test result status message failed; status left blank")]
        public static partial void TestFlrigPttStatusDisplayFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "DisconnectRadio invoked")]
        public static partial void DisconnectRadioInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "ConnectRadio invoked")]
        public static partial void ConnectRadioInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ConnectRadio failed")]
        public static partial void ConnectRadioFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DisconnectRadio failed")]
        public static partial void DisconnectRadioFailed(ILogger logger, Exception ex);
    }
}
