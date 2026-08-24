using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
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
/// per-module settings-section types directly -- see that service's own doc comment for why),
/// committed back only on <see cref="SaveCommand"/>; <see cref="CancelCommand"/> discards every
/// edit by simply closing without saving. Per-section resets are a single click each (nothing is
/// persisted until Save, so an accidental reset costs nothing); the global "reset ALL" is the one
/// genuinely destructive action here and requires an explicit confirm step.
///
/// Radio/CAT offers None/rigctld/Hamlib -- all three <c>IRadioProtocolFactory</c> backends are
/// registered in DI (spec/14-roadmap.md's Piece 3).</summary>
public sealed partial class OptionsWindowViewModel : ViewModelBase
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
    private readonly ILogger<OptionsWindowViewModel> _logger;

    private static readonly TimeSpan TxVolumePersistDebounce = TimeSpan.FromMilliseconds(400);

    /// <summary>WSJT-X's own Tune button has no fixed duration -- it keys PTT and holds a steady
    /// tone until the operator clicks it again, with an internal safety timeout so a forgotten Tune
    /// can't key the rig forever. This is that same shape: <see cref="TuneCommand"/> auto-stops after
    /// this long if the operator doesn't click Stop first.</summary>
    private static readonly TimeSpan MaxTuneDuration = TimeSpan.FromSeconds(30);

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

    [ObservableProperty]
    private int _sampleRate = 11025;

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

    /// <summary>OS process scheduling priority -- real, already-wired backend field
    /// (<c>AppPerformanceSettings.ProcessPriority</c>, applied once at startup in
    /// <c>ScanlineStudio.Host.Program</c>) that had no UI path before. Only Normal/High are offered,
    /// matching legacy's own real UI scope (<c>Option.dfm</c>'s <c>AppPriority</c> radio group only
    /// ever exposed 2 of <see cref="System.Diagnostics.ProcessPriorityClass"/>'s 6 real values,
    /// deliberately -- Realtime priority can hang/crash a misbehaving process's own host).</summary>
    [ObservableProperty]
    private bool _appPriorityIsHigh;

    [ObservableProperty]
    private string _radioBackendId = "none";

    [ObservableProperty]
    private string? _rigctldHost;

    [ObservableProperty]
    private int? _rigctldPort;

    [ObservableProperty]
    private uint? _hamlibModel;

    /// <summary>Discovery-order tier-1 user override path (spec/03-cat-layer.md) -- browsed to,
    /// auto-detected, or hand-typed. See <see cref="BrowseHamlibLibraryAsync"/>/
    /// <see cref="AutoDetectHamlibAsync"/>/<see cref="ProbeHamlibAsync"/> below.</summary>
    [ObservableProperty]
    private string? _hamlibLibraryPath;

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
    private string? _hamlibPttType;

    [ObservableProperty]
    private string? _callsign;

    [ObservableProperty]
    private string? _operatorName;

    [ObservableProperty]
    private string? _operatorGrid;

    [ObservableProperty]
    private bool _isConfirmingResetAll;

    /// <summary>Backs the Decode tab's real toggles -- see <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoSyncEnabled"/>/
    /// <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoSlantEnabled"/>'s own doc comments
    /// for what each genuinely gates in <c>AnalogFmSstvDecoder</c>. Deliberately NOT the tab's other
    /// two checkboxes (Auto-stop/Auto-restart) -- <c>Options.Decode.AutoStop</c>'s loc text ("Auto-stop
    /// when sync looks stable") doesn't match what <c>AutoStopEnabled</c> actually gates (stops on
    /// erratic/weak signal, not stable sync) and <c>AutoRestart</c>'s wording is a similar mismatch
    /// against <c>SyncRestartEnabled</c>'s real semantics -- left STUB pending a wording fix, not
    /// wired here to avoid shipping a control that lies about what it does. Takes effect on next
    /// app restart, same as every other Options-dialog setting baked into a DI singleton at startup
    /// (Radio backend, Audio device, etc. -- no live-reconfiguration path exists for any of them).</summary>
    [ObservableProperty]
    private bool _autoSyncEnabled = true;

    [ObservableProperty]
    private bool _autoSlantEnabled = true;

    /// <summary>Legacy fresh-install default is OFF (unlike every other decoder toggle here) --
    /// see <see cref="ScanlineStudio.Core.Sstv.SstvDecoderSettings.AutoStopEnabled"/>'s own doc
    /// comment for the citation.</summary>
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
    /// <see cref="CwIdMode"/>'s own doc comment for what each value means. Unlike every OTHER
    /// value this group's radio buttons could carry, <see cref="ScanlineStudio.Abstractions.Sstv.CwIdMode.SoundFile"/>
    /// is NOT selectable here -- the sound-file ID feature itself is unimplemented (out of v1 scope),
    /// so its `RadioButton` stays individually disabled with a not-implemented tooltip, matching this
    /// dialog's own established per-control (not whole-group) disable convention already used for
    /// the sound-file text/browse row directly below it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdMethodOffSelected))]
    [NotifyPropertyChangedFor(nameof(IsIdMethodCwSelected))]
    private CwIdMode _cwIdMode;

    [ObservableProperty]
    private string? _cwText;

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
        _logger = logger;

        _ = LoadSafeAsync();
        _ = LoadTxVolumeSafeAsync();
    }

    public IReadOnlyList<CultureInfo> AvailableCultures => _localization.AvailableCultures;

    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];

    /// <summary>Populated by a successful <see cref="BrowseHamlibLibraryAsync"/>/
    /// <see cref="AutoDetectHamlibAsync"/>/<see cref="ProbeHamlibAsync"/> probe -- empty (never
    /// re-populated with stale entries from a previous library) whenever the probe fails, so the
    /// existing numeric <see cref="HamlibModel"/> TextBox stays the fallback entry path.</summary>
    public ObservableCollection<HamlibRigModelInfo> HamlibRigModels { get; } = [];

    /// <summary>Forwarding tab's list-editable destination rows -- see
    /// <see cref="AdifUdpDestinationRowViewModel"/>'s own doc comment for the list-editable-row
    /// pattern this mirrors. Repopulated (not mutated in place) by <see cref="ApplyFromSnapshot"/>
    /// and <see cref="ResetForwardingToDefault"/>, same "Clear() then re-Add" shape as
    /// <see cref="CaptureDevices"/>/<see cref="PlaybackDevices"/> above.</summary>
    public ObservableCollection<AdifUdpDestinationRowViewModel> AdifUdpDestinations { get; } = [];

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
        TestConnectionStatusMessage = _localization.GetString("Options.Radio.TestConnection.Testing");
        try
        {
            var result = await _radioSession.TestConnectionAsync(new RigctldConnectionSpec(host, port)).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
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

    partial void OnIsTestingConnectionChanged(bool value) => TestRigctldConnectionCommand.NotifyCanExecuteChanged();

    partial void OnRigctldHostChanged(string? value) => TestRigctldConnectionCommand.NotifyCanExecuteChanged();

    partial void OnRigctldPortChanged(int? value) => TestRigctldConnectionCommand.NotifyCanExecuteChanged();

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
        IsProbingHamlib = true;
        HamlibDiscoveryStatusMessage = _localization.GetString("Options.Radio.Hamlib.Probing");
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
    /// as <see cref="IsSenseLevelVeryLowSelected"/>/etc above. No <c>IsIdMethodSoundFileSelected</c>
    /// counterpart -- see <see cref="CwIdMode"/>'s own doc comment for why that option's `RadioButton`
    /// stays individually disabled rather than wired.</summary>
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

    /// <summary>Backs the Audio tab's 2-way App priority radio group.</summary>
    public bool IsAppPriorityNormalSelected
    {
        get => !AppPriorityIsHigh;
        set
        {
            if (value)
            {
                AppPriorityIsHigh = false;
            }
        }
    }

    public bool IsAppPriorityHighSelected
    {
        get => AppPriorityIsHigh;
        set
        {
            if (value)
            {
                AppPriorityIsHigh = true;
            }
        }
    }

    /// <summary>Fired on Save (after a successful persist) and on Cancel -- the View closes the
    /// window either way; it does not need to distinguish which.</summary>
    public event Action? RequestClose;

    /// <summary>Gates <see cref="SaveCommand"/> -- see <see cref="CanSave"/>.</summary>
    private bool _loadSucceeded;

    private bool CanSave() => _loadSucceeded;

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

            var appSettings = await _settingsStore.LoadAsync();
            RememberWindowPosition = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings)?.RememberWindowPosition ?? false;
            JpegQuality = Math.Clamp(appSettings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings)?.JpegQuality ?? 85, 1, 100);

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
        }
    }

    private void ApplyFromSnapshot(OptionsSnapshot snapshot)
    {
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == snapshot.CultureCode) ?? _localization.CurrentCulture;
        SampleRate = snapshot.SampleRate;
        CaptureChannelSource = Enum.IsDefined(snapshot.CaptureChannelSource) ? snapshot.CaptureChannelSource : AudioChannelSource.Mono;
        StereoTxEnabled = snapshot.StereoTxEnabled;
        AppPriorityIsHigh = snapshot.AppPriorityIsHigh;
        RadioBackendId = snapshot.RadioBackendId is "none" or "rigctld" or "hamlib" ? snapshot.RadioBackendId : "none";
        RigctldHost = snapshot.RigctldHost;
        RigctldPort = snapshot.RigctldPort;
        HamlibModel = snapshot.HamlibModel;
        HamlibLibraryPath = snapshot.HamlibLibraryPath;
        HamlibSerialPort = snapshot.HamlibSerialPort;
        HamlibBaudRate = snapshot.HamlibBaudRate;
        HamlibPttType = snapshot.HamlibPttType;
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
            RadioBackendId: RadioBackendId,
            RigctldHost: RigctldHost,
            RigctldPort: RigctldPort,
            HamlibModel: HamlibModel,
            HamlibLibraryPath: HamlibLibraryPath,
            HamlibSerialPort: HamlibSerialPort,
            HamlibBaudRate: HamlibBaudRate,
            HamlibPttType: HamlibPttType,
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
            QrzLookupEnabled: QrzLookupEnabled,
            QrzLookupUsername: QrzLookupUsername,
            QrzLookupPassword: QrzLookupPassword,
            CaptureChannelSource: CaptureChannelSource,
            StereoTxEnabled: StereoTxEnabled,
            AppPriorityIsHigh: AppPriorityIsHigh,
            CwIdMode: CwIdMode,
            CwText: CwText,
            CwWpm: CwWpm,
            CwToneFrequencyHz: CwToneFrequencyHz,
            FskIdTxEnabled: FskIdTxEnabled,
            FskIdRxEnabled: FskIdRxEnabled,
            NrRstEnabled: NrRstEnabled,
            NrRstText: NrRstText,
            AdifUdpDestinations: AdifUdpDestinations.Select(row => row.ToDestination()).ToList());

        try
        {
            await _optionsSettingsService.SaveAsync(snapshot);

            // See RememberWindowPosition's own doc comment for why this bypasses
            // _optionsSettingsService entirely. Preserves Left/Top/Width/Height as-is -- those are
            // MainWindow's own domain (captured passively on Closing), not user-edited fields here.
            var appSettings = await _settingsStore.LoadAsync();
            var currentGeometry = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings) ?? new WindowGeometrySettings();
            var updatedAppSettings = appSettings.WithSection(WindowGeometrySettings.SectionKey, currentGeometry with { RememberWindowPosition = RememberWindowPosition }, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings);
            // Chained onto the SAME loaded/updated instance above (one load, one save) -- a second
            // independent LoadAsync/SaveAsync round-trip here would race the geometry write above.
            updatedAppSettings = updatedAppSettings.WithSection(ImageExportSettings.SectionKey, new ImageExportSettings { JpegQuality = JpegQuality }, ImageExportSettingsJsonContext.Default.ImageExportSettings);
            await _settingsStore.SaveAsync(updatedAppSettings);

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
            return;
        }

        RequestClose?.Invoke();
    }

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
        RememberWindowPosition = false;
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
        CaptureChannelSource = defaults.CaptureChannelSource;
        StereoTxEnabled = defaults.StereoTxEnabled;
        AppPriorityIsHigh = defaults.AppPriorityIsHigh;
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
        HamlibPttType = defaults.HamlibPttType;
        // Tier B audit finding: sibling ResetQrzToDefault already clears its own test-result status
        // (TestQrzLookupStatus) -- this one didn't, so a prior "Connected to IC-7300" success line
        // stayed visible under the now-blank host field after a reset.
        TestConnectionStatusMessage = null;
        HamlibDiscoveryStatusMessage = null;
        HamlibRigModels.Clear();
        SelectedHamlibRigModel = null;
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
        OnPropertyChanged(nameof(IsNoneBackendSelected));
        OnPropertyChanged(nameof(IsRigctldBackendSelected));
        OnPropertyChanged(nameof(IsHamlibBackendSelected));
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

    partial void OnAppPriorityIsHighChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAppPriorityNormalSelected));
        OnPropertyChanged(nameof(IsAppPriorityHighSelected));
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
    }
}
