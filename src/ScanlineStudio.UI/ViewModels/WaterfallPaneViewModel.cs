using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Controls;

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
/// gives a 0-50dB window comfortably covering the measured 38-46dB real-signal peak range.</summary>
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

    private readonly ILocalizationService _localization;

    public WaterfallPaneViewModel(ISstvSessionService sstvSession, ILocalizationService localization)
    {
        _localization = localization;
        sstvSession.Waterfall.Frames.Subscribe(OnFrame);
        sstvSession.ModeDetected += OnModeDetected;
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
}
