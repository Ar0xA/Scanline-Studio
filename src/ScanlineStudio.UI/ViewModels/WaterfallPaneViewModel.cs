using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Controls;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Live spectrum/waterfall pane, hosted in the fixed Receive tab's "Spectrum & waterfall"
/// card (spec/09-ui.md).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IWaterfallSource.Frames"/> pushes
/// synchronously from the audio drain thread and can arrive faster than the UI renders. At most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight -- a frame arriving while one is already pending
/// just replaces <see cref="_pendingFrame"/> (latest-wins), it never queues a second post.
///
/// <b>ZeroDb/GainDb defaults (batch 8)</b>: wired to mock2's "Zero"/"Gain" sliders. Chosen from real
/// measurement, not guessed -- fed all 8 real legacy-captured `.mmv` golden-vector fixtures
/// (tests/ScanlineStudio.Core.Sstv.Tests/Fixtures/GoldenVectors) through the actual
/// <see cref="IWaterfallSource"/>: real signal peaks consistently land 38-46dB, background/noise
/// floor consistently -50 to -65dB (this port's FFT magnitude is unnormalized, so its dB scale does
/// NOT match a conventional -100..0 dBFS convention -- see <c>ScanlineStudio.Core.Sstv.WaterfallSource</c>'s
/// own magnitude computation). Default <see cref="ZeroDb"/>=0 sits above the real noise floor (so ambient
/// noise stays near-black rather than painting the whole display); default <see cref="GainDb"/>=50
/// gives a 0-50dB window comfortably covering the measured 38-46dB real-signal peak range. Both are
/// persisted into the shared <see cref="RxPaneUiSettings"/> section (loaded once at construction,
/// saved on every change) so a user's chosen levels survive an app restart.</summary>
public sealed partial class WaterfallPaneViewModel : ViewModelBase
{
    private readonly object _gate = new();
    private WaterfallFrame? _pendingFrame;
    private bool _postScheduled;

    [ObservableProperty]
    private WaterfallFrame? _latestFrame;

    [ObservableProperty]
    private double _zeroDb = 0.0;

    [ObservableProperty]
    private double _gainDb = 50.0;

    /// <summary>Real now (batch 8b), wired to mock2's Start/Span <c>NumericUpDown</c>s -- continuous
    /// rather than legacy's 3 discrete zoom presets (`Main.cpp:11515-11531`'s `GetFFTRect`: (0,3.0k)/
    /// (700,2.0k)/(1000,1.5k)), a deliberate generalization matching mock2's own richer continuous
    /// design (spec/06 exempts this visualization from strict port-first fidelity). Default 1000/1600
    /// sits on legacy's own 1.5k preset.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeCaptionDisplay))]
    private double _startHz = 1000.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RangeCaptionDisplay))]
    private double _spanHz = 1600.0;

    /// <summary>Read-only computed telemetry pushed FROM <c>SpectrumTraceControl.BinsPerPixel</c> via
    /// a <c>Mode=OneWayToSource</c> binding (auditor round-2 finding: an <c>ElementName</c> binding
    /// can't cross the <c>MainWindow.axaml</c>/<c>WaterfallPaneView.axaml</c> XAML name-scope boundary
    /// -- this property is the shared point both sides can reach). Never set directly by this
    /// view-model's own code.</summary>
    [ObservableProperty]
    private double _binsPerPixel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsViewBoth), nameof(IsViewSpectrumOnly), nameof(IsViewWaterfallOnly))]
    private WaterfallViewMode _viewMode = WaterfallViewMode.Both;

    [ObservableProperty]
    private bool _peakHoldEnabled;

    /// <summary>Tracks the currently-locked mode for <see cref="SpectrumTraceMath.ComputeMarkerFrequencies"/>
    /// -- <see langword="null"/> before any mode has locked this session (or once RX stops without a
    /// dedicated "idle" event to react to, see <see cref="OnModeDetected"/>'s own doc comment), which
    /// <see cref="SpectrumTraceMath.ComputeMarkerFrequencies"/> already handles as "use the wide-mode
    /// default marker set."</summary>
    [ObservableProperty]
    private SstvModeDefinition? _currentMode;

    /// <summary>Un-stub-RX-tab Piece A3. This VM (not <c>RxImagePaneViewModel</c>, despite the Input
    /// Chain card's "Notch" row living on THAT card's own <c>DataContext="{Binding RxImage}"</c>
    /// scope) is the canonical owner: it already owns every other spectrum-trace-adjacent setting
    /// (<see cref="ZeroDb"/>/<see cref="GainDb"/>/<see cref="StartHz"/>/<see cref="SpanHz"/>/
    /// <see cref="PeakHoldEnabled"/>) and is where the click-to-tune gesture on
    /// <see cref="SpectrumTraceControl"/> naturally lands -- the Input Chain row reaches up to it via
    /// the same <c>$parent[Window].DataContext.X.Y</c> pattern the RxFrameMeta card's own Frequency row
    /// already uses to reach <c>RadioStatus</c> from inside a different card's DataContext scope.
    /// No getter exists on <see cref="ISstvSessionService.RequestNotch"/> (fire-and-forget-shaped,
    /// like <see cref="ISstvSessionService.RequestReSync"/>) -- this VM is the sole source of truth
    /// for "what does the user currently want," not a poll of decoder state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotchStatusDisplay))]
    [NotifyPropertyChangedFor(nameof(NotchToggleLabel))]
    private bool _notchEnabled;

    /// <summary>Default matches <c>NotchFilter</c>/<c>AnalogFmSstvDecoder._notchFrequencyHz</c>'s own
    /// default center frequency, so a first-ever enable via the toggle chip (no prior click-to-tune)
    /// requests the exact frequency the decoder would already default to on its own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotchStatusDisplay))]
    private double _notchFrequencyHz = 2400.0;

    private static readonly TimeSpan GainZeroPersistDebounce = TimeSpan.FromMilliseconds(400);

    private readonly ISstvSessionService _sstvSession;
    private readonly ILocalizationService _localization;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<WaterfallPaneViewModel> _logger;
    private bool _suppressGainZeroPersist;
    private CancellationTokenSource? _gainZeroPersistCts;

    public WaterfallPaneViewModel(
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        ISettingsStore settingsStore,
        ILogger<WaterfallPaneViewModel> logger)
    {
        _sstvSession = sstvSession;
        _localization = localization;
        _settingsStore = settingsStore;
        _logger = logger;
        sstvSession.Waterfall.Frames.Subscribe(OnFrame);
        sstvSession.ModeDetected += OnModeDetected;

        _ = LoadGainZeroSettingsAsync();

        // Live-locale-switch review (2026-09-20): every GetString-backed member here is a computed
        // getter, so the blanket refresh alone is enough. DI-singleton pane, never disposed --
        // permanent subscription, last statement (see MainViewModel's own identical reasoning).
        localization.CultureChanged += OnCultureChanged;
    }

    private void OnCultureChanged() => OnPropertyChanged(string.Empty);

    /// <summary>Read-modify-write against whatever is currently persisted for
    /// <see cref="RxPaneUiSettings.SectionKey"/> -- <see cref="RxImagePaneViewModel"/> also writes
    /// this same section (<c>QuickModeGridIds</c>), so a from-scratch write here would silently
    /// clobber that field.</summary>
    private async Task LoadGainZeroSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var rxPaneUi = settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings) ?? new RxPaneUiSettings();

            _suppressGainZeroPersist = true;
            try
            {
                ZeroDb = rxPaneUi.ZeroDb;
                GainDb = rxPaneUi.GainDb;
            }
            finally
            {
                _suppressGainZeroPersist = false;
            }
        }
        catch (Exception ex)
        {
            Log.LoadGainZeroSettingsFailed(_logger, ex);
        }
    }

    /// <summary>Debounced, same reasoning as <see cref="RadioStatusViewModel.OnTxVolumePercentChanged"/>
    /// -- a slider drag can fire dozens of change notifications a second, and a full-document settings
    /// write (read+parse+serialize+temp-file+rename) per tick is real per-drag I/O cost, not just a
    /// theoretical race.</summary>
    private async Task PersistGainZeroSettingsAsync(double zeroDb, double gainDb, CancellationToken ct)
    {
        try
        {
            await Task.Delay(GainZeroPersistDebounce, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Normal control flow -- a newer slider tick superseded this one. Not worth a log line.
            return;
        }

        try
        {
            await _settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings) ?? new RxPaneUiSettings();
                var updated = current with { ZeroDb = zeroDb, GainDb = gainDb };
                return settings.WithSection(RxPaneUiSettings.SectionKey, updated, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistGainZeroSettingsFailed(_logger, ex);
        }
    }

    private void RequestPersistGainZero()
    {
        if (_suppressGainZeroPersist)
        {
            return;
        }

        _gainZeroPersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _gainZeroPersistCts = cts;
        _ = PersistGainZeroSettingsAsync(ZeroDb, GainDb, cts.Token);
    }

    partial void OnZeroDbChanged(double value) => RequestPersistGainZero();

    partial void OnGainDbChanged(double value) => RequestPersistGainZero();

    /// <summary>Input Chain card's "Notch" row value -- real frequency in Hz while enabled, "Off"
    /// otherwise. Replaces the placeholder <c>Panes.RxInput.NotchValue</c> literal "—" this row
    /// showed before Piece A3.</summary>
    public string NotchStatusDisplay => NotchEnabled
        ? _localization.GetString("Panes.RxInput.NotchValueFormat", NotchFrequencyHz)
        : _localization.GetString("Panes.RxInput.NotchValue");

    /// <summary>The toggle chip's own label -- user-reported (2026-08-27, then corrected the same
    /// day): this used to be a static "On" loc-key literal in the AXAML that never changed. An
    /// initial fix made it mirror <see cref="NotchStatusDisplay"/> (show "On" while enabled), but
    /// the user's real intent is an ACTION label, not a state label -- it names what clicking the
    /// chip will DO next, the opposite of the current state (same convention as a play/pause
    /// button showing "Pause" while playing): "On" while disabled (click to turn it on), "Off"
    /// while enabled (click to turn it off). Reuses <see cref="NotchStatusDisplay"/>'s own two
    /// existing loc keys (<c>Panes.RxInput.NotchToggle</c>="On" / <c>Panes.RxInput.NotchValue</c>=
    /// "Off") rather than adding new ones -- same two words, no need for a second pair of keys.</summary>
    public string NotchToggleLabel => NotchEnabled
        ? _localization.GetString("Panes.RxInput.NotchValue")
        : _localization.GetString("Panes.RxInput.NotchToggle");

    /// <summary>Toggle-chip path -- see <see cref="NotchEnabled"/>'s own doc comment for why this VM
    /// forwards rather than <c>RxImagePaneViewModel</c>. <see langword="null"/> frequency on disable
    /// (matches <see cref="ISstvSessionService.RequestNotch"/>'s own contract); on enable, forwards
    /// THIS VM's own currently-tracked <see cref="NotchFrequencyHz"/> explicitly rather than relying on
    /// the decoder's own last-tuned-frequency persistence -- this VM is the UI's source of truth for
    /// "what frequency does the user expect," independent of that decoder-side behavior.</summary>
    partial void OnNotchEnabledChanged(bool value) => _sstvSession.RequestNotch(value, value ? NotchFrequencyHz : null);

    /// <summary>Click-to-tune path (<see cref="SpectrumTraceControl.NotchTuneRequestedCommand"/>) --
    /// legacy's left-click both tunes AND enables (<c>Main.cpp:14364-14371</c>), matching
    /// <see cref="ISstvSessionService.RequestNotch"/>'s own "frequencyHz implies enabled" contract.
    /// Calls <see cref="ISstvSessionService.RequestNotch"/> directly rather than relying on
    /// <see cref="OnNotchEnabledChanged"/>'s own forward: a retune while ALREADY enabled (the drag
    /// case) needs to reach the decoder every time, but the generated <see cref="NotchEnabled"/>
    /// setter no-ops -- and skips its own changed handler -- when the value doesn't actually change.
    /// The resulting harmless double-dispatch on a disabled-to-enabled transition (both this method
    /// and the now-firing <see cref="OnNotchEnabledChanged"/> call <c>RequestNotch(true, ...)</c> with
    /// the same arguments) is intentional, not a bug -- <c>ISstvDecoder.RequestNotch</c> is a plain
    /// <c>Interlocked.Exchange</c> overwrite of a pending request, so a same-value re-request the
    /// decoder hasn't drained yet is a no-op, not a double-application.</summary>
    [RelayCommand]
    private void TuneNotch(double frequencyHz)
    {
        NotchFrequencyHz = frequencyHz;
        NotchEnabled = true;
        _sstvSession.RequestNotch(true, frequencyHz);
    }

    /// <summary>spec/18-path-to-1.0.md Medium item 2: was a static localized literal
    /// ("1000…2600 Hz") that silently lied the moment the Start/Span steppers below were touched.
    /// Real now, derived from the same <see cref="StartHz"/>/<see cref="SpanHz"/> the spectrum plot
    /// itself already windows against.</summary>
    public string RangeCaptionDisplay => _localization.GetString("Panes.Waterfall.RangeCaptionFormat", StartHz, StartHz + SpanHz);

    /// <summary>Same "worry, don't fire-and-forget" concurrency contract as <see cref="OnFrame"/>:
    /// <see cref="ISstvSessionService.ModeDetected"/>'s own doc comment states it fires synchronously
    /// on the audio drain thread (auditor round-2 catch -- an earlier version of this method assigned
    /// <see cref="CurrentMode"/> directly, which would have raised <c>PropertyChanged</c>, and hence
    /// updated Avalonia bindings, off the UI thread). No corresponding "mode un-locked"/RX-stopped
    /// event exists on <see cref="ISstvSessionService"/> today (only a polled <c>IsReceiving</c> bool),
    /// so <see cref="CurrentMode"/> is deliberately NOT reset to null when RX stops -- it just keeps
    /// showing the last-locked mode's markers until a new one locks, which
    /// <see cref="SpectrumTraceMath.ComputeMarkerFrequencies"/>'s own default (wide-mode set when null)
    /// makes a reasonable idle fallback for regardless.</summary>
    private void OnModeDetected(SstvModeDefinition mode) => Dispatcher.UIThread.Post(() => CurrentMode = mode);

    public bool IsViewBoth
    {
        get => ViewMode == WaterfallViewMode.Both;
        set
        {
            if (value)
            {
                ViewMode = WaterfallViewMode.Both;
            }
        }
    }

    public bool IsViewSpectrumOnly
    {
        get => ViewMode == WaterfallViewMode.SpectrumOnly;
        set
        {
            if (value)
            {
                ViewMode = WaterfallViewMode.SpectrumOnly;
            }
        }
    }

    public bool IsViewWaterfallOnly
    {
        get => ViewMode == WaterfallViewMode.WaterfallOnly;
        set
        {
            if (value)
            {
                ViewMode = WaterfallViewMode.WaterfallOnly;
            }
        }
    }

    private void OnFrame(WaterfallFrame frame)
    {
        lock (_gate)
        {
            _pendingFrame = frame;
            if (_postScheduled)
            {
                return;
            }

            _postScheduled = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            WaterfallFrame frameToShow;
            lock (_gate)
            {
                frameToShow = _pendingFrame!;
                _postScheduled = false;
            }

            LatestFrame = frameToShow;
        });
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading RxPaneUi Zero/Gain settings failed")]
        public static partial void LoadGainZeroSettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting RxPaneUi Zero/Gain settings failed")]
        public static partial void PersistGainZeroSettingsFailed(ILogger logger, Exception ex);
    }
}
