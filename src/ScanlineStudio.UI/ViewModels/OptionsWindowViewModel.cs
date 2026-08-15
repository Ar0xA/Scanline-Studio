using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Audio;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
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
    private readonly ILogger<OptionsWindowViewModel> _logger;

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

    public OptionsWindowViewModel(
        OptionsSettingsService optionsSettingsService,
        ILocalizationService localization,
        IAudioDeviceEnumerator audioDeviceEnumerator,
        ILogbookSessionService logbookSession,
        ISettingsStore settingsStore,
        ILogger<OptionsWindowViewModel> logger)
    {
        _optionsSettingsService = optionsSettingsService;
        _localization = localization;
        _audioDeviceEnumerator = audioDeviceEnumerator;
        _logbookSession = logbookSession;
        _settingsStore = settingsStore;
        _logger = logger;

        _ = LoadSafeAsync();
    }

    public IReadOnlyList<CultureInfo> AvailableCultures => _localization.AvailableCultures;

    public ObservableCollection<AudioDeviceInfo> CaptureDevices { get; } = [];

    public ObservableCollection<AudioDeviceInfo> PlaybackDevices { get; } = [];

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

    /// <summary>Unguarded fire-and-forget from the constructor before this wrap was added -- the
    /// audio enumerator's <c>RefreshAsync</c> call can throw, which used to mean the Options dialog
    /// could open completely blank with no explanation anywhere.</summary>
    private async Task LoadSafeAsync()
    {
        try
        {
            var snapshot = await _optionsSettingsService.LoadAsync();
            ApplyFromSnapshot(snapshot);

            var appSettings = await _settingsStore.LoadAsync();
            RememberWindowPosition = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings)?.RememberWindowPosition ?? false;

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

            SelectedCaptureDevice = CaptureDevices.FirstOrDefault(d => d.Id == snapshot.CaptureDeviceId);
            SelectedPlaybackDevice = PlaybackDevices.FirstOrDefault(d => d.Id == snapshot.PlaybackDeviceId);
        }
        catch (Exception ex)
        {
            Log.LoadFailed(_logger, ex);
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
    }

    [RelayCommand]
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
            SampleRate: SampleRate,
            RadioBackendId: RadioBackendId,
            RigctldHost: RigctldHost,
            RigctldPort: RigctldPort,
            HamlibModel: HamlibModel,
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
            FskIdRxEnabled: FskIdRxEnabled);

        try
        {
            await _optionsSettingsService.SaveAsync(snapshot);

            // See RememberWindowPosition's own doc comment for why this bypasses
            // _optionsSettingsService entirely. Preserves Left/Top/Width/Height as-is -- those are
            // MainWindow's own domain (captured passively on Closing), not user-edited fields here.
            var appSettings = await _settingsStore.LoadAsync();
            var currentGeometry = appSettings.GetSection(WindowGeometrySettings.SectionKey, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings) ?? new WindowGeometrySettings();
            await _settingsStore.SaveAsync(appSettings.WithSection(WindowGeometrySettings.SectionKey, currentGeometry with { RememberWindowPosition = RememberWindowPosition }, WindowGeometrySettingsJsonContext.Default.WindowGeometrySettings));

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
        SelectedCulture = AvailableCultures.FirstOrDefault(c => c.Name == OptionsSettingsService.Defaults.CultureCode);
        RememberWindowPosition = false;
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
        HamlibSerialPort = defaults.HamlibSerialPort;
        HamlibBaudRate = defaults.HamlibBaudRate;
        HamlibPttType = defaults.HamlibPttType;
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
    }
}
