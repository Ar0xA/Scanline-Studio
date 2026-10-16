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
    private readonly IAppearanceSettingsService _appearanceSettings;
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

    /// <summary>Index of the TX tab (source order: General=0, Appearance=1, Audio=2, Radio=3, Tx=4,
    /// ...) in this dialog's own `TabControl` -- named so the header-row's callsign chip (which
    /// jumps straight here, since Callsign/OperatorName/OperatorGrid live on this tab) can request
    /// it without a magic number, same "named constant + a source-order test" pattern as
    /// <see cref="ScanlineStudio.UI.ViewModels.MainViewModel.LogbookTabIndex"/>. Shifted by 1 when
    /// the Phase 1 dark mode Appearance tab was inserted right after General.</summary>
    public const int TxTabIndex = 4;

    /// <summary>Index of the Radio (CAT) tab -- named so the radio-connection give-up popup's own
    /// "Config" button (which jumps straight here, since rig/CAT connection settings live on this
    /// tab) can request it without a magic number, same pattern as <see cref="TxTabIndex"/>
    /// above.</summary>
    public const int RadioTabIndex = 3;

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

    /// <summary>Backs the new Appearance tab's theme radio group (Phase 1 dark mode) -- same
    /// "deliberately NOT part of <see cref="OptionsSnapshot"/>" reasoning as
    /// <see cref="RememberWindowPosition"/> above: gates a UI-owned settings section
    /// (<see cref="AppearanceSettings"/>), loaded/saved directly via <see cref="_settingsStore"/>.
    /// Backed by 3 <c>IsAppThemeXSelected</c> computed properties below, same pattern as
    /// <see cref="SenseLevel"/>. Default <see cref="AppTheme.Light"/> matches <c>App.axaml</c>'s
    /// own hardcoded fallback.</summary>
    [ObservableProperty]
    private AppTheme _appTheme = AppTheme.Light;

    /// <summary>Backs the Appearance tab's second radio group (Phase 2 font-size presets) -- same
    /// "deliberately NOT part of <see cref="OptionsSnapshot"/>" reasoning as <see cref="AppTheme"/>
    /// directly above, sharing the same <see cref="AppearanceSettings"/> section. Backed by 2
    /// <c>IsFontScaleXSelected</c> computed properties below, same pattern as <see cref="AppTheme"/>.
    /// Default <see cref="AppFontScale.Normal"/> matches <c>App.axaml.cs</c>'s own seeded default
    /// (<c>Initialize()</c>'s <c>ApplyFontScale(AppFontScale.Normal)</c> call).</summary>
    [ObservableProperty]
    private AppFontScale _fontScale = AppFontScale.Normal;

    /// <summary>User-requested (2026-09-18): lets the "Decoding" indicator's blink
    /// (<c>RadioStatusViewModel.IsDecodingBlinkOn</c>) be turned off, default on. Same
    /// "deliberately NOT part of <see cref="OptionsSnapshot"/>" reasoning as <see cref="AppTheme"/>/
    /// <see cref="FontScale"/> above, sharing the same <see cref="AppearanceSettings"/> section --
    /// unlike those two, this one is a plain checkbox with no computed IsXSelected properties to
    /// drive. Default <see cref="AppearanceSettings.DefaultDecodingIndicatorBlinks"/>
    /// (<see langword="true"/>).</summary>
    [ObservableProperty]
    private bool _decodingIndicatorBlinks = AppearanceSettings.DefaultDecodingIndicatorBlinks;

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

    /// <summary>RST default plan (2026-09-01) -- see OperatorSettings.DefaultRst's own doc comment
    /// for why "595", not "599".</summary>
    [ObservableProperty]
    private string? _defaultRst;

    // User-reported (2026-09-20): grey watermark text in the empty Callsign/Name/Grid boxes,
    // showing what will actually be used (MacroTextResolver/CallsignDisplay's own fallback) if the
    // operator leaves the field blank -- reads the SAME canonical constants those consumers use
    // (OperatorSettings.CallsignFallback's own doc comment already forbids a second hardcoded
    // copy), not a duplicated literal. Instance properties, not static -- Avalonia's reflection
    // binding resolves {Binding X} via Type.GetProperty(name)'s default (Public | Instance) flags,
    // which doesn't see static members, so `static` (CA1822's own suggested fix) would silently
    // break the binding instead of throwing.
#pragma warning disable CA1822 // Mark members as static
    public string CallsignWatermark => OperatorSettings.CallsignFallback;

    public string OperatorNameWatermark => OperatorSettings.NameFallback;

    public string OperatorGridWatermark => OperatorSettings.GridFallback;
#pragma warning restore CA1822 // Mark members as static

    /// <summary>Unlike Callsign/Name/Grid, <see cref="DefaultRst"/> is pre-filled with the real
    /// "595" value at load time (OperatorSettings.DefaultRst's own doc comment: the fallback is
    /// applied at OptionsSettingsService.LoadAsync, a documented "read site"), so its TextBox can't
    /// use a native Watermark -- <see cref="DefaultRst"/> is never actually empty. This flags "still
    /// showing the auto-applied default, not something you typed" so the view can render it in the
    /// SAME muted style Watermark text gets -- purely visual, doesn't change what Save persists. An
    /// operator whose own deliberate choice happens to also be "595" sees the same muted color;
    /// harmless, since SSTV operators near-universally use "595" anyway (see that doc comment).</summary>
    public bool IsDefaultRstAtFallback => string.Equals(DefaultRst, OperatorSettings.DefaultRstFallback, StringComparison.Ordinal);

    partial void OnDefaultRstChanged(string? value) => OnPropertyChanged(nameof(IsDefaultRstAtFallback));

    [ObservableProperty]
    private bool _isConfirmingResetAll;

    /// <summary>General tab's Storage section -- 4 rows (Images/Config/Database/Log), moved in
    /// from the former standalone "Configurations &gt; Storage" dialog (Images) plus 3 new rows.
    /// Deliberately NOT part of <see cref="OptionsSnapshot"/>/staged-until-Save like every field
    /// above -- each row commits (Images/Config/Log) or stages (Database only, restart-required by
    /// standing user decision) immediately on its own Apply. User-reported bug (2026-09-01):
    /// <see cref="SaveCommand"/>/<see cref="ApplyCommand"/> used to never touch any of these 5 rows
    /// at all, so a value edited here only took effect via its own row's Apply button -- both now
    /// call each row's own Apply*Async method from <see cref="SaveCoreUnguardedAsync"/>, gated on
    /// that row's input actually having something to apply (see that call site's own doc comment).
    /// <see cref="CancelCommand"/>/<see cref="ResetGeneralToDefaultCommand"/> still never reference
    /// these fields at all (by omission, not a guard) -- Cancel must not apply an unconfirmed edit,
    /// and resetting a user's real data folder locations to app defaults was never asked for.</summary>
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

    /// <summary>Last-applied value -- refreshed after a successful
    /// <see cref="ApplyConfigDirectoryAsync"/> (restart-required-settings backlog item 3, 2026-08-27:
    /// Config directory now applies live, same shape as <see cref="LogDirectory"/>'s own
    /// display/refresh, no longer staged-until-restart like <see cref="DatabaseDirectory"/> still
    /// is). NOT the TextBox/Browse binding target -- see <see cref="ConfigDirectoryInput"/> for
    /// that -- but used as its seed value and as the baseline a general Save diffs against (see
    /// <see cref="SaveCoreUnguardedAsync"/>'s own Config gate).</summary>
    [ObservableProperty]
    private string _configDirectory = string.Empty;

    /// <summary>TextBox/Browse binding target -- a SEPARATE property from
    /// <see cref="ConfigDirectory"/> (round-3 plan-review finding), seeded from
    /// <see cref="ConfigDirectory"/> on load and after every Apply so the row shows the real current
    /// path instead of an empty box (user-reported 2026-09-18). <see cref="ApplyConfigDirectoryAsync"/>'s
    /// blank-means-error guard still applies (a user who clears the box and clicks Apply), but a
    /// general Save now diffs this against <see cref="ConfigDirectory"/> rather than just checking
    /// non-blank -- see <see cref="SaveCoreUnguardedAsync"/>'s own Config gate, same reasoning as
    /// <see cref="DatabaseDirectory"/>'s own equivalent <c>PendingDatabaseDirectory</c> gate.</summary>
    [ObservableProperty]
    private string? _configDirectoryInput;

    [ObservableProperty]
    private string? _configDirectoryErrorMessage;

    [ObservableProperty]
    private string _databaseDirectory = string.Empty;

    /// <summary>TextBox/Browse binding target. The nullable
    /// <c>IAppLocationsService.GetPendingDatabaseDirectoryAsync</c> result means "nothing staged," not
    /// "show an empty box" -- both <see cref="LoadStorageLocationsSafeAsync"/> and
    /// <see cref="ApplyDatabaseDirectoryAsync"/> fall back to <see cref="DatabaseDirectory"/> before
    /// assigning here, so the row always shows the real current path (user-reported 2026-09-18).</summary>
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

    /// <summary>Decode tab "Measure reception SNR" -- see
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.SnrMeasurementEnabled"/>. Live-applied on
    /// Save via <see cref="ISstvSessionService.RequestSnrMeasurementEnabled"/>.</summary>
    [ObservableProperty]
    private bool _snrMeasurementEnabled = ScanlineStudio.Core.Sstv.SstvDecoderSettings.DefaultSnrMeasurementEnabled;

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
    [NotifyPropertyChangedFor(nameof(ShowQrzPlaintextWarning))]
    private string? _qrzLookupPassword;

    /// <summary>What <see cref="LoadQrzPasswordFieldAsync"/> put in <see cref="QrzLookupPassword"/>. Save
    /// writes the password only when the field differs from this, so an untouched field (including one
    /// left empty by a locked keyring) is never written or deleted.</summary>
    private string? _loadedQrzPassword;

    /// <summary>False until the password load completed; Save never writes the password before that.</summary>
    private bool _qrzPasswordLoaded;

    /// <summary>The password exists in the system keyring but could not be read without a prompt.
    /// Shows the "keyring locked" watermark and the Unlock button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrzLookupPasswordWatermark))]
    [NotifyCanExecuteChangedFor(nameof(UnlockQrzKeyringCommand))]
    private bool _isQrzKeyringLocked;

    /// <summary>This session stores the password in a system keyring (not plaintext settings.json).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQrzPlaintextWarning))]
    [NotifyPropertyChangedFor(nameof(QrzLookupPasswordHint))]
    private bool _isQrzPasswordStoreSecure;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UnlockQrzKeyringCommand))]
    private bool _isUnlockingQrzKeyring;

    /// <summary>The password was stored in a keyring in an earlier session, but no keyring is reachable
    /// now (plaintext fallback). No Unlock button: shows the "keyring not available" watermark and hint.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QrzLookupPasswordWatermark))]
    private bool _isQrzKeyringUnreachable;

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

    private readonly Debouncer _soundFileIdValidation;

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

    [ObservableProperty]
    private bool _cwIdRxEnabled;

    /// <summary>Literal `12` (not a reference to
    /// `ScanlineStudio.Core.Sstv.StationIdSettings.DefaultCwIdRxWindowSeconds`, which
    /// `ScanlineStudio.UI` cannot reference -- `UiLayeringArchitectureTests`), same reasoning as
    /// <see cref="CwWpm"/>'s own doc comment.</summary>
    [ObservableProperty]
    private int _cwIdRxWindowSeconds = 12;

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
        IAppearanceSettingsService appearanceSettings,
        ILogger<OptionsWindowViewModel> logger,
        TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        _txVolumePersist = new Debouncer(TxVolumePersistDebounce, time, resumeOnCapturedContext: false);
        _soundFileIdValidation = new Debouncer(SoundFileIdValidationDebounce, time, resumeOnCapturedContext: false);
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
        _appearanceSettings = appearanceSettings;
        _logger = logger;

        _isRadioConnected = radioSession.RigId != "none";
        _connectionEventsSubscription = radioSession.ConnectionEvents.Subscribe(OnConnectionEvent);

        _ = LoadSafeAsync();
        _ = LoadTxVolumeSafeAsync();
        _ = LoadStorageLocationsSafeAsync();

        // Live-locale-switch review (2026-09-20): same leak class as ConnectionEvents just above --
        // this VM is a fresh instance per Options open (Program.cs's factory), so a permanent subscription to
        // the long-lived ILocalizationService singleton needs the same Dispose()-paired unsubscribe.
        // Every stored GetString-backed member here is a transient status/error message tied to one
        // in-flight action (Test Connection/PTT, Hamlib probe, a directory save) -- self-heals on
        // the next attempt or accepted stale in the meantime, same convention as every other VM in
        // this pass; every other GetString-backed member is a computed getter, so the blanket
        // refresh alone covers the rest. Last statement, deliberately (see MainViewModel's own
        // identical reasoning).
        localization.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged() => OnPropertyChanged(string.Empty);

    private readonly IDisposable _connectionEventsSubscription;

    /// <summary>Code-review finding: this dialog's own view-model is a fresh instance per Options
    /// open (created by Program.cs's registered factory, outside the container's tracking) --
    /// without unsubscribing, every dialog open added a permanent subscriber to the singleton
    /// <see cref="IRadioSessionService.ConnectionEvents"/> stream, rooting the whole dead
    /// view-model graph for the app's remaining lifetime. Called from <c>OptionsWindowView</c>'s own
    /// <c>Closed</c> handler, same place <see cref="StopTuneIfActive"/>/<see cref="StopTestPttIfActive"/>
    /// are already called from. Live-locale-switch review (2026-09-20): also unsubscribes from
    /// <see cref="ILocalizationService.CultureChanged"/>, same leak class, same fix shape.</summary>
    public void Dispose()
    {
        _connectionEventsSubscription.Dispose();
        _localization.CultureChanged -= OnCultureChanged;
    }

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

    [ObservableProperty]
    private string? _connectRadioErrorMessage;

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

    // Owned by the UI thread: edit/reset invalidation is checked when queued results publish.
    private int _radioTestConfigurationGeneration;
    private int _pttTestRequestGeneration;

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

    private bool _suppressTxVolumePersist;
    private readonly Debouncer _txVolumePersist;

    /// <summary>Toggle state for <see cref="TuneCommand"/> -- <see langword="true"/> while a tone is
    /// keyed, drives the button's own label/affordance between "Tune" and "Stop" (mirrors WSJT-X's
    /// own Tune button, which is a toggle, not a fire-and-forget action).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TuneButtonLabel))]
    private bool _isTuning;

    [ObservableProperty]
    private string? _tuneErrorMessage;

    private CancellationTokenSource? _tuneCts;

    /// <summary>Toggle state for <see cref="TestPttCommand"/>/<see cref="TestFlrigPttCommand"/> --
    /// shared across both (only one backend section is ever visible at a time, same reasoning as
    /// <see cref="TestConnectionStatusMessage"/>'s own sharing) -- see <see cref="IsTuning"/>'s own
    /// doc comment for the identical pattern.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestPttButtonLabel))]
    [NotifyPropertyChangedFor(nameof(TestFlrigPttButtonLabel))]
    private bool _isTestingPtt;

    [ObservableProperty]
    private string? _testPttErrorMessage;

    private CancellationTokenSource? _testPttCts;

    /// <summary>Fired on Save (after a successful persist) and on Cancel -- the View closes the
    /// window either way; it does not need to distinguish which.</summary>
    public event Action? RequestClose;

    /// <summary>User-reported (2026-09-20): fired at the end of every successful
    /// <see cref="SaveCoreUnguardedAsync"/> -- both <see cref="SaveAsync"/> and
    /// <see cref="ApplyAsync"/> route through it. The header-row callsign chip
    /// (<c>MainViewModel.CallsignDisplay</c>) and macro-consuming previews only refreshed on this
    /// window's own Closed event (<c>MainWindow.axaml.cs</c>) before this -- fine for Save, which
    /// closes the dialog, but <see cref="ApplyAsync"/> deliberately does NOT close it, so a
    /// callsign/grid change made via Apply used to sit stale on screen until the operator also
    /// closed the window. Same "genuinely live, applied unconditionally on every successful save"
    /// convention this method already uses for SenseLevel/AutoSync/PLL tuning above -- this is a
    /// UI-refresh signal rather than a live decoder push, so it's a plain event instead of an
    /// <c>ISstvSessionService.Request*</c> call.</summary>
    public event Action? OperatorSettingsSaved;

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

    /// <summary>Code-review finding: <see cref="LoadStorageLocationsSafeAsync"/> is a SEPARATE
    /// fire-and-forget from <see cref="LoadSafeAsync"/> (whose own success is what
    /// <see cref="_loadSucceeded"/>/<see cref="CanSave"/> track) -- <see cref="CanSave"/> could
    /// already return true (enabling Save) while this dialog's own Storage rows are still their
    /// zeroed/default construction values, if the storage load is unusually slow or throws. Gates
    /// <see cref="SaveCoreUnguardedAsync"/>'s own Storage-section apply block so a Save that races
    /// ahead of (or outlives a failed) storage load can never write those still-default/blank values
    /// out as if they were the user's real, current settings -- a blank <see cref="ImagesDirectory"/>/
    /// <see cref="AudioDirectory"/>/<see cref="LogDirectory"/> is NOT a no-op downstream (each
    /// resolves to "reset to app default"), unlike every OTHER field this dialog owns.</summary>
    private bool _storageLocationsLoaded;

    /// <summary>Code-review finding: the value of <see cref="PendingDatabaseDirectory"/> the last
    /// time this dialog itself (this load, or a prior explicit <see cref="ApplyDatabaseDirectoryAsync"/>
    /// this session) actually staged it -- lets <see cref="SaveCoreUnguardedAsync"/> tell "the user
    /// typed a genuinely NEW target since the last stage" apart from "nothing changed, this is just
    /// what's already pending." Without this, a general Save/Apply/Connect re-calls
    /// <see cref="ApplyDatabaseDirectoryAsync"/> on EVERY call while anything is pending (even a
    /// relocation staged in an earlier session the user never touched this time), which re-sets
    /// <see cref="IsConfirmingDatabaseRestart"/> to true unconditionally -- undoing an explicit
    /// "Not Now" dismissal on the very next unrelated Save, and re-running a real directory-exists
    /// check against the staged target every time.</summary>
    private string? _lastStagedDatabaseDirectory;

    /// <summary>True only while <c>IAppLocationsService.GetPendingDatabaseDirectoryAsync</c> itself
    /// returns non-null -- a genuine on-disk staged relocation awaiting a restart, independent of
    /// <see cref="IsConfirmingDatabaseRestart"/> (which a "Not Now" dismissal clears even though the
    /// stage itself is still on disk) and of <see cref="PendingDatabaseDirectory"/> (which, since it
    /// now always shows a real path for display -- user-reported 2026-09-18 -- can no longer double as
    /// this signal the way its null-ness used to). <see cref="ShowPostSaveWarningsAsync"/>'s own
    /// restart-warning check reads this, not <see cref="PendingDatabaseDirectory"/>.</summary>
    private bool _hasPendingDatabaseRelocation;

    /// <summary>Code-review finding (step 7): also excludes Save/Apply while Connect's own implicit
    /// save is in flight -- see <see cref="IsConnectingRadio"/>'s own doc comment for why an
    /// interleaved Save/Apply must not resume and clear shared save-result fields before Connect's
    /// continuation reads them.</summary>
    private bool CanSave() => _loadSucceeded && !IsConnectingRadio;

    /// <summary>Plan-review finding (step 7): four callers now reach <see cref="SaveCoreAsync"/>
    /// (Save, Apply, Connect's implicit save, Restart Now) and its body does several SEPARATE
    /// settings-store round trips (a load-modify-write is only safe "because sequential, not
    /// concurrent" per this method's own comments further down) plus a real, never-unloaded native
    /// Hamlib library load -- two overlapping calls could double-load the native library or lose an
    /// update to settings.json. Serializes every call; not re-entrant-safe by design (a caller
    /// awaiting this while another is in flight simply waits its turn).</summary>
    private readonly SemaphoreSlim _saveGate = new(1, 1);

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
    private void RequestResetAll() => IsConfirmingResetAll = true;

    [RelayCommand]
    private void ConfirmResetAll()
    {
        Log.ConfirmResetAllInvoked(_logger);
        ResetGeneralToDefault();
        ResetAppearanceToDefault();
        ResetAudioToDefault();
        ResetRadioToDefault();
        ResetTxToDefault();
        ResetDecodeToDefault();
        ResetAdvancedToDefault();
        ResetQrzToDefault();
        ResetIdentificationToDefault();
        ResetForwardingToDefault();
        IsConfirmingResetAll = false;
    }

    [RelayCommand]
    private void CancelResetAll() => IsConfirmingResetAll = false;

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

        [LoggerMessage(Level = LogLevel.Error, Message = "Applying theme {AppTheme} live failed after a successful settings save")]
        public static partial void AppThemeApplyFailed(ILogger logger, AppTheme appTheme, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Applying font scale {FontScale} live failed after a successful settings save")]
        public static partial void FontScaleApplyFailed(ILogger logger, AppFontScale fontScale, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sample rate change to {SampleRate}Hz deferred -- a recording is in progress")]
        public static partial void SampleRateChangeDeferred(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Sample rate change to {SampleRate}Hz rejected -- the decoder kept its previous rate")]
        public static partial void SampleRateChangeRejected(ILogger logger, int sampleRate);

        [LoggerMessage(Level = LogLevel.Error, Message = "Sample rate change to {SampleRate}Hz failed after a successful settings save")]
        public static partial void SampleRateChangeFailed(ILogger logger, int sampleRate, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture device change to {DeviceId} deferred -- a recording is in progress")]
        public static partial void CaptureDeviceChangeDeferred(ILogger logger, string? deviceId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Capture device change to {DeviceId} rejected -- RX capture is still running, but on a fallback device, not the one requested")]
        public static partial void CaptureDeviceChangeRejected(ILogger logger, string? deviceId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Capture device change to {DeviceId} failed after a successful settings save -- RX capture may now be stopped, and the unopenable device stays persisted")]
        public static partial void CaptureDeviceChangeFailed(ILogger logger, string? deviceId, Exception ex);

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

        [LoggerMessage(Level = LogLevel.Debug, Message = "QRZ password field loaded: status={Status}, secureStore={IsSecure}")]
        public static partial void QrzPasswordLoaded(ILogger logger, CredentialReadStatus status, bool isSecure);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ password save failed ({Outcome}); settings not saved")]
        public static partial void QrzPasswordSaveFailed(ILogger logger, QrzPasswordWriteOutcome outcome);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ keyring unlock failed")]
        public static partial void QrzKeyringUnlockFailed(ILogger logger, Exception ex);

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
