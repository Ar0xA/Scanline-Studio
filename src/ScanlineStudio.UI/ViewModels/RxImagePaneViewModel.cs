using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>The in-progress/completed decoded receive image, hosted in the fixed Receive tab's
/// "Incoming frame" card (spec/09-ui.md).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IReceivedImageBuffer.Updated"/> fires
/// synchronously from the audio drain thread, once per decoded scanline group -- at most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight; a burst of updates while one is pending just
/// means the eventual post reads whatever <see cref="IReceivedImageBuffer.Current"/> is *then*
/// (latest-wins), not a queued backlog of every intermediate scanline.</summary>
public sealed partial class RxImagePaneViewModel : ViewModelBase
{
    /// <summary>Poll interval for the decoder-telemetry properties below (Slant/SyncOffset/SyncTone/
    /// Buffer/Overdriven) -- no push/event mechanism exists for any of them, and their own doc
    /// comments on <see cref="ISstvSessionService"/>/<c>ISstvDecoder</c> explicitly describe a GUI
    /// polling them on a timer. <b>Not literally "plain field reads" in production</b> (auditor
    /// correction of an earlier version of this comment, itself corrected once already): the real
    /// registered <c>ISstvDecoder</c> is <c>RestartableSstvDecoder</c>, whose telemetry getters each
    /// take a lock -- but that lock covers only the cheap swap-decision check (and a <c>Swap()</c>
    /// when one actually fires), NOT the chunk decode itself, which runs on the fresh/current inner
    /// decoder outside the lock. So each tick's 5 separate short lock acquisitions can in principle
    /// contend briefly with a restart swap in progress, not with an entire in-flight chunk decode.
    /// Not a correctness bug either way, just not lock-free the way the property docs on the
    /// underlying `AnalogFmSstvDecoder` alone would suggest. 250ms is a deliberate choice, not copied
    /// from anywhere: well under legacy's own ~100ms `LevelAgc`/`CLVL::Fix` window (so a poll is
    /// never more than ~1.5 windows stale) while still cheap enough to run unconditionally for the
    /// pane's whole lifetime.</summary>
    private static readonly TimeSpan TelemetryPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly IReceivedImageBuffer _receivedImage;
    private readonly ISstvSessionService _sstvSession;
    private readonly ILocalizationService _localization;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly object _gate = new();
    private bool _postScheduled;

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlantPpmDisplay))]
    [NotifyPropertyChangedFor(nameof(SlantPpmStatusBarDisplay))]
    [NotifyPropertyChangedFor(nameof(AutoCorrectDisplay))]
    private double? _slantPpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncOffsetSamplesDisplay))]
    private int? _syncOffsetSamples;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncToneDisplay))]
    private double? _syncFrequencyCorrectionHz;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClippingDisplay))]
    private bool _isLevelOverdriven;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BufferedSampleCountDisplay))]
    private int _bufferedSampleCount;

    /// <summary>The currently (or most recently) auto-detected RX mode -- real data from
    /// <see cref="ISstvSessionService.ModeDetected"/>. There is no manual "lock to a specific
    /// mode" decode feature in this port (<see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder"/>
    /// always auto-detects via the VIS header) -- mock2's Auto/Locked segmented control is shown
    /// (Auto statically checked, matching this real always-auto-detect behavior) but "Locked" has
    /// no backing feature yet, same for the quick-mode-button grid below it
    /// (spec/14-roadmap.md backlog).</summary>
    [ObservableProperty]
    private SstvModeDefinition? _detectedMode;

    public RxImagePaneViewModel(ISstvSessionService sstvSession, ILocalizationService localization)
    {
        _receivedImage = sstvSession.ReceivedImage;
        _sstvSession = sstvSession;
        _localization = localization;

        _receivedImage.Updated += OnUpdated;
        sstvSession.ModeDetected += OnModeDetected;

        _telemetryTimer = new DispatcherTimer(TelemetryPollInterval, DispatcherPriority.Background, (_, _) => PollTelemetry());
        _telemetryTimer.Start();
    }

    public string DetectedModeText => DetectedMode?.DisplayName ?? "—";

    /// <summary>"Scottie 1 — VIS 60"-shaped, matching mock2's own active-mode dropdown content
    /// exactly (DisplayName + real VisCode, not a placeholder).</summary>
    public string DetectedModeDisplay => DetectedMode is { } mode ? $"{mode.DisplayName} — VIS {mode.VisCode}" : "—";

    public string LineTimeText => DetectedMode is { } mode ? $"{mode.LineDurationMs:0.0} ms" : "—";

    public string LinesText => DetectedMode is { } mode ? mode.ImageHeight.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>Legacy's own "Sync &amp; slant" readout formula (<c>TMmsstv::DrawSlantInfo</c>,
    /// <c>Main.cpp:5535-5544</c>) -- see <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SlantPpm"/>
    /// for the full null-case contract.</summary>
    public string SlantPpmDisplay => SlantPpm is { } ppm ? _localization.GetString("Panes.RxSync.SlantPpmValueFormat", ppm) : "—";

    /// <summary>Same <see cref="SlantPpm"/> value, second display site (status bar) -- a distinct
    /// property, not just the same string, because the status bar's compact "label baked into the
    /// value string" convention (matching its sibling readouts, e.g. `MainWindow.StatusBar.SnrValue`)
    /// needs a "slant" prefix the Sync&amp;Slant card's own separate label `TextBlock` doesn't.</summary>
    public string SlantPpmStatusBarDisplay => SlantPpm is { } ppm
        ? _localization.GetString("MainWindow.StatusBar.SlantValueFormat", ppm)
        : _localization.GetString("MainWindow.StatusBar.SlantValueNoLock");

    /// <summary>Legacy's <c>m_AutoStopPos</c> (<c>Main.cpp:3887</c>), which legacy itself never
    /// displays -- shown here in raw samples, not an invented "pixels" conversion (legacy has no
    /// on-screen px readout for this quantity to port).</summary>
    public string SyncOffsetSamplesDisplay => SyncOffsetSamples is { } offset ? _localization.GetString("Panes.RxSync.OffsetSamplesFormat", offset) : "—";

    /// <summary>Locked/not-locked half only -- <see cref="SlantPpm"/> non-null means the tracker is
    /// actively correcting. Deliberately does NOT expose an on/off toggle: legacy's real <c>AutoSlant</c>
    /// setting (<c>Mmsstv.ini AutoSlant=1</c>) is hardcoded on in this port, with no user-facing
    /// setting yet to reflect -- see spec/17-rx-telemetry-feasibility.md.</summary>
    public string AutoCorrectDisplay => SlantPpm is not null
        ? _localization.GetString("Panes.RxSync.AutoCorrectValue.Locked")
        : _localization.GetString("Panes.RxSync.AutoCorrectValue.NotLocked");

    /// <summary>Sync-tone nominal target -- 1900Hz for the narrow MN/MC family, 1200Hz otherwise
    /// (<c>AnalogFmSstvDecoder.InitializeAfc</c>'s own <c>syncTargetHz</c> selection, keyed off
    /// <see cref="SstvModeDefinition.NarrowModeCode"/>). <see cref="SyncFrequencyCorrectionHz"/>
    /// itself carries no mode tag, so this pane derives the right nominal from <see cref="DetectedMode"/>
    /// instead of assuming the normal-family 1200Hz always applies.</summary>
    private double SyncToneNominalHz => DetectedMode?.NarrowModeCode is not null ? 1900.0 : 1200.0;

    /// <summary>Legacy's own deliberate calibration nudge (<c>SyncFreq</c>'s <c>d -= 128</c>,
    /// `sstv.cpp:2347`, ported as <c>AfcTracker._calibrationOffsetHz = 128.0 * bandwidthHalfHz /
    /// 16384.0</c>) -- <see cref="SyncFrequencyCorrectionHz"/> is computed from a locked frequency
    /// that already has this nudge folded in, so recovering the real measured Hz needs it subtracted
    /// back out too, not just the correction alone (auditor-caught residual: an earlier version of
    /// this pane's math recovered the tracker's internal locked-frequency value, not the true
    /// measured frequency, off by exactly this amount -- +3.125Hz wide, +1.0Hz narrow).</summary>
    private double SyncToneCalibrationOffsetHz => DetectedMode?.NarrowModeCode is not null ? 1.0 : 3.125;

    /// <summary>Measured sync-tone frequency and its delta from nominal, matching mock2's own
    /// "measured · delta" layout (and the exact sibling-field convention already used by the
    /// Black/White tone placeholders in `en.json`, `delta = measured - nominal`). Legacy has no
    /// equivalent readout for the Black/White picture tones -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.SyncFrequencyCorrectionHz"/>'s own
    /// doc comment; this pane does not invent values for those.
    ///
    /// <b>Sign and calibration offset, both verified against source, not assumed from prose</b>:
    /// <see cref="AfcTracker.ProcessSample"/> computes <c>_lockedFrequencyHz</c> from
    /// <c>measuredFrequencyHz + _calibrationOffsetHz</c>, then <c>CorrectionHz = syncTargetHz -
    /// _lockedFrequencyHz</c> -- so recovering the true measured Hz needs BOTH terms undone:
    /// <c>measured = nominal - CorrectionHz - calibrationOffsetHz</c>, not just
    /// <c>nominal - CorrectionHz</c> (which recovers the tracker's internal locked value, ~3Hz off
    /// the true measurement on wide-band modes). Getting the correction's SIGN backwards (an earlier
    /// version of this code did <c>nominal + CorrectionHz</c>) would show a station that's actually
    /// 30Hz high as 30Hz low and vice versa -- caught by auditor review before this shipped.</summary>
    public string SyncToneDisplay => SyncFrequencyCorrectionHz is { } hz
        ? _localization.GetString("Panes.RxSignal.SyncToneValueFormat", SyncToneNominalHz - hz - SyncToneCalibrationOffsetHz, -hz - SyncToneCalibrationOffsetHz)
        : "—";

    /// <summary>The decoder's internal sample-history buffer -- a different quantity from the
    /// audio-engine capture-overrun/XRUN counter (spec/17-rx-telemetry-feasibility.md), which isn't
    /// wired here.</summary>
    public string BufferedSampleCountDisplay => _localization.GetString("Panes.RxInput.BufferValueFormat", BufferedSampleCount);

    /// <summary>Legacy's own red-meter-bar threshold, not an invented clipping percentage -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.IsLevelOverdriven"/>.</summary>
    public string ClippingDisplay => IsLevelOverdriven
        ? _localization.GetString("Panes.RxInput.ClippingValue.Overdriven")
        : _localization.GetString("Panes.RxInput.ClippingValue.Normal");

    [RelayCommand]
    private void RequestReSync() => _sstvSession.RequestReSync();

    /// <summary>Normally invoked only by <see cref="_telemetryTimer"/>'s own tick -- public so tests
    /// can poll deterministically instead of waiting on a real <see cref="DispatcherTimer"/>
    /// interval.</summary>
    public void PollTelemetry()
    {
        SlantPpm = _sstvSession.SlantPpm;
        SyncOffsetSamples = _sstvSession.SyncOffsetSamples;
        SyncFrequencyCorrectionHz = _sstvSession.SyncFrequencyCorrectionHz;
        IsLevelOverdriven = _sstvSession.IsLevelOverdriven;
        BufferedSampleCount = _sstvSession.BufferedSampleCount;
    }

    partial void OnDetectedModeChanged(SstvModeDefinition? value)
    {
        OnPropertyChanged(nameof(DetectedModeText));
        OnPropertyChanged(nameof(DetectedModeDisplay));
        OnPropertyChanged(nameof(LineTimeText));
        OnPropertyChanged(nameof(LinesText));
        OnPropertyChanged(nameof(SyncToneDisplay));
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        Dispatcher.UIThread.Post(() => DetectedMode = mode);
    }

    private void OnUpdated()
    {
        lock (_gate)
        {
            if (_postScheduled)
            {
                return;
            }

            _postScheduled = true;
        }

        Dispatcher.UIThread.Post(() =>
        {
            lock (_gate)
            {
                _postScheduled = false;
            }

            Image = ImageSourceBitmapConverter.ToBitmap(_receivedImage.Current);
        });
    }
}
