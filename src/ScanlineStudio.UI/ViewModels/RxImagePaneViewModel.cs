using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
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
    private readonly ILogbookSessionService _logbookSession;
    private readonly ILogger<RxImagePaneViewModel> _logger;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly object _gate = new();
    private bool _postScheduled;

    [ObservableProperty]
    private Bitmap? _image;

    /// <summary>Real, already-computed fraction of the current decode's total rows -- see
    /// <see cref="IReceivedImageBuffer.Progress"/>'s own doc comment for the exact-1.0-on-completion
    /// guarantee this pane relies on. Read alongside <see cref="Image"/> in <see cref="OnUpdated"/>
    /// (same coalesced <see cref="IReceivedImageBuffer.Updated"/> event), not polled separately.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineProgressText))]
    [NotifyPropertyChangedFor(nameof(ClipLoHiDisplay))]
    private double? _progress;

    /// <summary>Fraction of the current image's pixels clipped to pure black/white -- see
    /// <see cref="ScanlineStudio.UI.Imaging.LuminanceClipStatistics"/> for the computation (pure
    /// image-domain arithmetic, no legacy grounding, not audio DSP). Recomputed over the whole image
    /// on every <see cref="OnUpdated"/>, same cadence/cost class as this pane's own existing
    /// <see cref="Image"/> bitmap conversion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipLoHiDisplay))]
    private double _clippedBlackFraction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ClipLoHiDisplay))]
    private double _clippedWhiteFraction;

    /// <summary>When the most recently DETECTED decode began -- captured at
    /// <see cref="ISstvSessionService.ModeDetected"/>, which fires both for a fresh detection and a
    /// mid-reception restart (<c>ISstvDecoder.DecodeRestarted</c>'s own doc comment); a restart
    /// legitimately starts a new "Started" time here, since the prior image was abandoned. NOT
    /// guaranteed to match "currently displayed" in one specific case (auditor-caught, deliberately
    /// not fixed): Auto Stop's erratic/weak-signal abandonment fires <c>DecodeRestarted</c> with NO
    /// follow-up <see cref="ModeDetected"/>, so <see cref="IReceivedImageBuffer"/> blanks
    /// <c>Current</c>/<c>Progress</c> but this stays pinned to the abandoned image's start time --
    /// a blank image next to a live-looking "Started" readout. <c>ISstvSessionService</c> doesn't
    /// expose <c>DecodeRestarted</c> at all today, so clearing this on that path would need new API
    /// surface; not worth adding for this one edge case.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartedDisplay))]
    private DateTimeOffset? _startedAt;

    /// <summary>The CONFIGURED RX capture device's display name (see
    /// <see cref="ISstvSessionService.GetConfiguredCaptureDeviceNameAsync"/>'s own doc comment for
    /// the "configured, not necessarily currently in-flight" caveat) -- backs mock2's Receive tab
    /// "Device" field, exact mirror of <c>TxControlsPaneViewModel.OutputDeviceName</c>'s own
    /// pattern. <see langword="null"/> until the best-effort initial load below completes, or if no
    /// device is configured / the configured device is no longer present. Loaded once at
    /// construction, not re-fetched on a live settings change while this pane stays open -- same
    /// convention as the TX-side property. TX-side <c>TxControlsPaneViewModel.OutputDeviceName</c>
    /// now has the exact same <c>OutputDeviceNameDisplay</c> wrapper and is genuinely wired to
    /// `TxControlsPaneView.axaml` (fixed 2026-08-11, was a stale gap noted here before that).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureDeviceNameDisplay))]
    private string? _captureDeviceName;

    public string CaptureDeviceNameDisplay => CaptureDeviceName ?? "—";

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
    [NotifyPropertyChangedFor(nameof(BufferedSampleCountStatusBarDisplay))]
    private int _bufferedSampleCount;

    /// <summary>The audio ENGINE's own dropped-frame overrun count (<see cref="ISstvSessionService.CaptureOverrunCount"/>)
    /// -- a genuinely different quantity from <see cref="BufferedSampleCount"/> above despite
    /// feeding the same combined "buffer · XRUN" readout mock2 shows; see that property's own doc
    /// comment for the distinction. Both halves are real now, so this pane recombines them back into
    /// mock2's original single format instead of the real-only-half split batches 1/2 shipped
    /// specifically because XRUN was still fake then.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BufferedSampleCountDisplay))]
    [NotifyPropertyChangedFor(nameof(BufferedSampleCountStatusBarDisplay))]
    private int _captureOverrunCount;

    /// <summary>Backs <see cref="AgcGainDisplay"/> -- see that property's own doc comment for the
    /// derivation. Never <see langword="null"/>, same lifetime as
    /// <see cref="ISstvSessionService.SignalPeakLevel"/> itself.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgcGainDisplay))]
    private double _signalPeakLevel;

    /// <summary>The currently (or most recently) auto-detected RX mode -- real data from
    /// <see cref="ISstvSessionService.ModeDetected"/>. There is no manual "lock to a specific
    /// mode" decode feature in this port (<see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder"/>
    /// always auto-detects via the VIS header) -- mock2's Auto/Locked segmented control is shown
    /// (Auto statically checked, matching this real always-auto-detect behavior) but "Locked" has
    /// no backing feature yet, same for the quick-mode-button grid below it
    /// (spec/14-roadmap.md backlog).</summary>
    [ObservableProperty]
    private SstvModeDefinition? _detectedMode;

    /// <summary>Frame-metadata card's "Size on disk" row -- real, but only for a COMPLETED save: set
    /// from <see cref="IReceivedImageBuffer.Saved"/> (fired once <see cref="IReceivedImageBuffer.SaveAsync"/>'s
    /// write finishes), the only hook a live pane has to "what file did this frame end up as" -- the
    /// production writer, <c>ReceiveHistoryRecorder</c>, is a wholly separate class in a different
    /// layer with no reference back to this pane. Reset to <see langword="null"/> on every fresh
    /// <see cref="OnModeDetected"/> (a new/restarted decode has no saved file yet), same lifetime
    /// rule as <see cref="StartedAt"/>. An abandoned/partial image's own save (which
    /// <c>ReceiveHistoryRecorder</c> deliberately routes around <see cref="IReceivedImageBuffer.SaveAsync"/>
    /// for -- see that class's own doc comment) never raises this event, so this stays "—" for a
    /// frame that gets superseded before completing, matching every other placeholder in this
    /// pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeDisplay))]
    private long? _fileSizeBytes;

    /// <summary>Frame-metadata card's "Override callsign" field (spec/09-ui.md, legacy's real
    /// <c>HisCall</c> equivalent) -- also the input to <see cref="LookupQrzCommand"/>. Auto-filled by
    /// <see cref="OnStationIdDecoded"/> from a decoded FSK station-ID (CW-ID/FSK station-ID subsystem
    /// Phase 5), but still plain, directly user-editable state otherwise -- not backed by
    /// <see cref="IReceivedImageBuffer"/> or any session model (no "current QSO" tracker exists in
    /// this port, matching the auto-fill's own gate simplification, see
    /// <see cref="OnStationIdDecoded"/>'s doc comment). The remaining, still-unseeded source is
    /// OCR (this pane's own "Callsign" row stays a separate, still-literal placeholder).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupQrzCommand))]
    private string? _overrideCallsign;

    /// <summary>Legacy's real <c>MyRST</c> equivalent (<c>Main.cpp:3648</c>,
    /// <c>sprintf("595%s", pDem-&gt;m_fskNRS)</c>) -- the decoded NR/RST exchange from a station-ID's
    /// optional sub-packet, auto-filled by <see cref="OnStationIdDecoded"/>. No card row binds this
    /// yet (the RxFrameMeta card's mockup has no RST field at all, unlike "Override callsign" which
    /// already had one to wire into) -- real, tested backing state ahead of its own UI exposure,
    /// same incremental pattern several sibling still-literal rows on this same card already follow
    /// (Frequency/ModeVis/SnrSlant/OcrConfidence/DroppedLines). Deliberately not named <c>MyRst</c>
    /// (a literal legacy-field-name port) -- follows <see cref="OverrideCallsign"/>'s own precedent
    /// of an English, descriptive name instead.</summary>
    [ObservableProperty]
    private string? _decodedNrRst;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameDisplay))]
    private string? _lookupName;

    /// <summary>Code-review nit fix: falls back to this pane's own "—" placeholder convention
    /// (matching <see cref="StartedDisplay"/>/<see cref="FileSizeDisplay"/>) instead of rendering
    /// blank pre-lookup, unlike every neighboring row in this card.</summary>
    public string NameDisplay => LookupName ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QthDisplay))]
    private string? _lookupQth;

    public string QthDisplay => LookupQth ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridDisplay))]
    private string? _lookupGrid;

    /// <summary>"Grid / dist · QRZ" row's real half -- distance needs the operator's own grid
    /// square plus a haversine calculation, out of scope for this pass (not requested); the
    /// distance side keeps the pane's existing "--" placeholder text (that specific "--" -- not
    /// this property's own "—" fallback -- matches the row's pre-existing literal
    /// GridDistanceValue's own wording, kept as-is for the half that's still unwired).</summary>
    public string GridDisplay => $"{LookupGrid ?? "—"} / --";

    [ObservableProperty]
    private string? _qrzLookupErrorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupQrzCommand))]
    private bool _isLookingUpQrz;

    public RxImagePaneViewModel(ISstvSessionService sstvSession, ILocalizationService localization, ILogbookSessionService logbookSession, ILogger<RxImagePaneViewModel> logger)
    {
        _receivedImage = sstvSession.ReceivedImage;
        _sstvSession = sstvSession;
        _localization = localization;
        _logbookSession = logbookSession;
        _logger = logger;
        AutoSlantEnabled = sstvSession.AutoSlantEnabled;

        _receivedImage.Updated += OnUpdated;
        _receivedImage.Saved += OnSaved;
        sstvSession.ModeDetected += OnModeDetected;
        sstvSession.StationIdDecoded += OnStationIdDecoded;

        _telemetryTimer = new DispatcherTimer(TelemetryPollInterval, DispatcherPriority.Background, (_, _) => PollTelemetry());
        _telemetryTimer.Start();

        _ = LoadCaptureDeviceNameAsync();
    }

    public string DetectedModeText => DetectedMode?.DisplayName ?? "—";

    /// <summary>"Scottie 1 — VIS 60"-shaped, matching mock2's own active-mode dropdown content
    /// exactly (DisplayName + real VisCode, not a placeholder).</summary>
    public string DetectedModeDisplay => DetectedMode is { } mode ? $"{mode.DisplayName} — VIS {mode.VisCode}" : "—";

    public string LineTimeText => DetectedMode is { } mode ? $"{mode.LineDurationMs:0.0} ms" : "—";

    public string LinesText => DetectedMode is { } mode ? mode.ImageHeight.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>Status bar's "line N / total" readout -- real, derived from <see cref="Progress"/>
    /// times the detected mode's own <c>ImageHeight</c>. No separate line-index property exists on
    /// this pane or the decoder; <see cref="Progress"/> is the one already-exposed source for this
    /// (spec/17-rx-telemetry-feasibility.md).</summary>
    public string LineProgressText => Progress is { } progress && DetectedMode is { } mode
        ? _localization.GetString("MainWindow.StatusBar.LineProgressValueFormat", (int)Math.Round(progress * mode.ImageHeight), mode.ImageHeight)
        : _localization.GetString("MainWindow.StatusBar.LineProgressValueNoLock");

    /// <summary>Signal-quality card's "Clip lo/hi" row -- real percentages over the rows actually
    /// decoded so far (see <see cref="ClippedBlackFraction"/>'s own doc comment for what's being
    /// measured and what it isn't: no legacy grounding, pure image-domain stat). "—" while idle
    /// (<see cref="Progress"/> null -- no decode in progress or completed image to measure yet),
    /// matching this pane's own placeholder convention for every other readout -- auditor-caught:
    /// an earlier version computed over the whole undecoded (all-black) canvas in this state and
    /// showed a misleading "100% clipped black."</summary>
    public string ClipLoHiDisplay => Progress is not null
        ? _localization.GetString("Panes.RxSignal.ClipLoHiValueFormat", ClippedBlackFraction * 100.0, ClippedWhiteFraction * 100.0)
        : "—";

    /// <summary>Frame-metadata card's "Started" row -- real, UTC time-of-day only (matching mock2's
    /// own "14:20:54Z" shape), not a full date (this pane has no multi-day session concept to
    /// disambiguate a bare time-of-day against).</summary>
    public string StartedDisplay => StartedAt is { } startedAt
        ? _localization.GetString("Panes.RxFrameMeta.StartedValueFormat", startedAt.UtcDateTime)
        : "—";

    /// <summary>See <see cref="FileSizeBytes"/>'s own doc comment for what sets/clears this. Shown in
    /// kB (matching mock2's own "198 kB" wording), not a raw byte count.</summary>
    public string FileSizeDisplay => FileSizeBytes is { } bytes
        ? _localization.GetString("Panes.RxFrameMeta.FileSizeValueFormat", bytes / 1024.0)
        : "—";

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

    /// <summary>Whether legacy's real <c>AutoSlant</c> setting (<c>ISstvSessionService.AutoSlantEnabled</c>)
    /// is on -- a plain synchronous read at construction, not an <c>[ObservableProperty]</c>: this is
    /// documented restart-only (the decoder is a DI singleton with no live-reconfigure path), unlike
    /// the genuinely-live telemetry polled every 250ms elsewhere in this pane.</summary>
    public bool AutoSlantEnabled { get; }

    /// <summary>Four-way state, not just locked/not-locked. Checked in this order:
    /// <list type="number">
    /// <item>AVT (<see cref="DetectedMode"/>'s <c>Id == "avt"</c> -- a plain string on a DTO already in
    /// <c>Abstractions.Sstv</c>, not a <c>Core.Sstv</c>/<c>SstvModeRegistry</c> reference, which this
    /// UI-layer class must not touch) -- literal <c>"—"</c>, matching every other "nothing to show"
    /// state on this pane (<see cref="CaptureDeviceNameDisplay"/>, <see cref="ClipLoHiDisplay"/>), not
    /// a locale key.</item>
    /// <item><see cref="AutoSlantEnabled"/> false -- genuinely off now, not a placeholder. Checked
    /// BEFORE <see cref="SlantPpm"/>, deliberately: <see cref="SlantPpm"/> is non-null (reading exactly
    /// <c>0.0</c>) from the moment a non-AVT mode locks, REGARDLESS of this flag's value -- see
    /// <see cref="ISstvSessionService.AutoSlantEnabled"/>'s own doc comment for why (a genuinely
    /// unverified assumption in an earlier version of this design, caught by a decoder-level test
    /// actually failing once written, not by inspection). A naive "<see cref="SlantPpm"/> is null"
    /// check could never distinguish "off" at all.</item>
    /// <item><see cref="AutoSlantEnabled"/> true and <see cref="SlantPpm"/> non-null -- "Locked". This
    /// fires from the very FIRST decoded line of a reception, not after genuine convergence: the
    /// underlying tracker reports a non-null <c>0.0</c> "no drift measured yet" the instant it's
    /// constructed, with no property-level distinction from a later genuine zero-ppm reading
    /// (pre-existing shape of this readout from before this batch, unchanged here). In practice this
    /// label tracks "a reception is active," not "a slant correction has actually locked" -- an
    /// auditor-caught imprecision in this label's own name, not a new bug this batch introduces or
    /// one worth renaming given it predates this batch.</item>
    /// <item><see cref="AutoSlantEnabled"/> true and <see cref="SlantPpm"/> null -- "on, not locked
    /// yet". Auditor-caught correction: an earlier version of this comment wrongly claimed this is
    /// only reachable in a brief pane-construction startup window. Actually reachable for the ENTIRE
    /// idle period whenever no reception is active: <see cref="ISstvDecoder.SlantPpm"/> returns null
    /// whenever the decoder's own mode is null, which is true both before the very first reception AND
    /// between every subsequent one (<c>EndOfImage</c>/<c>AbandonInProgressImage</c> both null it) --
    /// i.e. most of a typical session's runtime, not a narrow window.</item>
    /// </list></summary>
    public string AutoCorrectDisplay
    {
        get
        {
            if (DetectedMode?.Id == "avt")
            {
                return "—";
            }

            if (!AutoSlantEnabled)
            {
                return _localization.GetString("Panes.RxSync.AutoCorrectValue.Off");
            }

            return SlantPpm is not null
                ? _localization.GetString("Panes.RxSync.AutoCorrectValue.Locked")
                : _localization.GetString("Panes.RxSync.AutoCorrectValue.OnNotLocked");
        }
    }

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

    /// <summary>The decoder's internal sample-history buffer, combined with the audio-engine's own
    /// dropped-frame overrun count (<see cref="CaptureOverrunCount"/>) -- two genuinely different
    /// quantities feeding mock2's own single combined "buffer 512 · 0 XRUN" readout. Batches 1/2
    /// shipped only the buffer half here, deliberately dropping "· N XRUN" while it was still a
    /// hardcoded fake -- both halves are real now, so this recombines them back into mock2's
    /// original format (spec/17-rx-telemetry-feasibility.md).</summary>
    public string BufferedSampleCountDisplay => _localization.GetString("Panes.RxInput.BufferValueFormat", BufferedSampleCount, CaptureOverrunCount);

    /// <summary>Same values, second display site (status bar) -- a distinct property, same reasoning
    /// as <see cref="SlantPpmStatusBarDisplay"/>.</summary>
    public string BufferedSampleCountStatusBarDisplay => _localization.GetString("MainWindow.StatusBar.BufferValueFormat", BufferedSampleCount, CaptureOverrunCount);

    /// <summary>Legacy's own red-meter-bar threshold, not an invented clipping percentage -- see
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder.IsLevelOverdriven"/>.</summary>
    public string ClippingDisplay => IsLevelOverdriven
        ? _localization.GetString("Panes.RxInput.ClippingValue.Overdriven")
        : _localization.GetString("Panes.RxInput.ClippingValue.Normal");

    /// <summary>Legacy's own AGC gain (<c>CLVL::m_agc</c>, <c>sstv.h:233</c>), derived client-side
    /// from the already-real <see cref="SignalPeakLevel"/> -- no new backend property needed
    /// (spec/17-rx-telemetry-feasibility.md's own audit correction: <c>LevelAgc.cs:103</c>'s
    /// <c>_agc = curMax &gt; 32.0 ? 16384.0 / curMax : 16384.0 / 32.0</c> is a pure function of the
    /// already-exposed peak level). <see cref="SignalPeakLevel"/> is this port's own <c>[0,~1.0]</c>
    /// scale, so it's rescaled back to legacy's int16-ish domain (<c>*32768.0</c>) before applying
    /// legacy's exact formula, matching <c>SignalPeakLevel</c>'s own doc comment for that
    /// conversion.</summary>
    public string AgcGainDisplay
    {
        get
        {
            var curMax = SignalPeakLevel * 32768.0;
            var agc = curMax > 32.0 ? 16384.0 / curMax : 16384.0 / 32.0;
            return _localization.GetString("Panes.RxInput.AgcValueFormat", agc);
        }
    }

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
        SignalPeakLevel = _sstvSession.SignalPeakLevel;
        CaptureOverrunCount = _sstvSession.CaptureOverrunCount;
    }

    partial void OnDetectedModeChanged(SstvModeDefinition? value)
    {
        OnPropertyChanged(nameof(DetectedModeText));
        OnPropertyChanged(nameof(DetectedModeDisplay));
        OnPropertyChanged(nameof(LineTimeText));
        OnPropertyChanged(nameof(LinesText));
        OnPropertyChanged(nameof(SyncToneDisplay));
        // AutoCorrectDisplay's own AVT check reads DetectedMode.Id directly -- without this, switching
        // into/out of AVT wouldn't re-evaluate the "—" case until some OTHER property change happened
        // to fire first (e.g. SlantPpm's own [NotifyPropertyChangedFor]).
        OnPropertyChanged(nameof(AutoCorrectDisplay));
        // Auditor-caught gap: LineProgressText depends on DetectedMode.ImageHeight too, not just
        // Progress -- without this, a fresh detection renders "line -- / --" until the SECOND
        // decoded line (ReceivedImageBuffer subscribes to ModeDetected before this VM does, so its
        // own Progress=0.0 reset -- swallowed by the generated setter's equality check on the very
        // first line -- races ahead of DetectedMode updating here), and a mode change with a
        // different ImageHeight can briefly show the PREVIOUS mode's total.
        OnPropertyChanged(nameof(LineProgressText));
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        Dispatcher.UIThread.Post(() =>
        {
            DetectedMode = mode;
            StartedAt = DateTimeOffset.UtcNow;
            FileSizeBytes = null;
        });
    }

    /// <summary>CW-ID/FSK station-ID subsystem Phase 5: auto-fill wiring for a decoded FSK station-ID
    /// (<see cref="ISstvSessionService.StationIdDecoded"/>). Fires on the decode thread -- per that
    /// event's own concurrency contract, this dispatches to the UI thread IMMEDIATELY and does all
    /// real work (including the async operator-callsign lookup below) only after, never inline here.
    ///
    /// <b>Scope simplification vs. legacy, documented not silently dropped</b>
    /// (<c>Main.cpp:3618-3651</c>): legacy gates the callsign write on
    /// <c>!SBTX-&gt;Down &amp;&amp; (!SBQSO-&gt;Down || HisCall-&gt;Text.IsEmpty())</c> and the NR/RST
    /// write on a similar-but-different <c>!SBTX-&gt;Down &amp;&amp; (!SBQSO-&gt;Down || MyRST-len&lt;=3
    /// || !strcmp(HisCall, decoded))</c>. Neither check is implemented here as runtime logic --
    /// BOTH conditions are structurally always-true for the ONE call path that matters here --
    /// <c>!TX-active</c> because a call to <see cref="ISstvSessionService.TransmitAsync"/>/
    /// <see cref="ISstvSessionService.TuneAsync"/> fully pauses RX capture for its own duration (no
    /// samples can reach the decoder to raise this event while one is in flight, so the check can
    /// never observe a "TX active" state from THAT source to gate against), and <c>!QSO-active</c>
    /// because this port has no "current QSO" tracker at all (confirmed absent -- see the
    /// implementation plan's RX-side note making the same call), and <c>!QSOActive || X</c> is
    /// unconditionally true once <c>QSOActive</c> can never be true. <b>Narrower claim than an
    /// earlier version of this comment made (auditor round-1 finding on Phase 5)</b>:
    /// <see cref="ISstvSessionService.SetPttLockAsync"/> keys PTT WITHOUT pausing capture at all, so
    /// this event CAN still fire while that lock is engaged -- "TX-active is impossible" is true only
    /// for the <see cref="ISstvSessionService.TransmitAsync"/>/<see cref="ISstvSessionService.TuneAsync"/>
    /// case, not as a blanket "PTT keyed" statement. This is NOT the same simplification for both
    /// writes -- the underlying formulas genuinely differ (a real round-2 plan-review finding: an
    /// earlier draft of this plan applied the callsign gate to the NR/RST write too) -- kept as two
    /// separately-cited paragraphs here for that reason, even though both currently reduce to "no
    /// gate needed."
    ///
    /// Legacy's <c>AddCall</c> (call-history log), <c>qrzcom</c> auto-lookup thread, <c>FindCall</c>,
    /// <c>HisCallChange(NULL)</c> (`Main.cpp:10835-10840`'s `TempDelay`/log-UI-enable/`UpdateUI` --
    /// no logbook pane reads from <see cref="OverrideCallsign"/> for this port to update), and
    /// <c>RxAutoPush</c> (an unrelated auto-sync-restart trigger sharing the same outer
    /// <c>if (pDem-&gt;m_fskrec)</c> block, lines 3618-3627) are all deliberately NOT ported -- no
    /// call-history log or "current QSO" concept exists in this port to add to, automatic QRZ lookup
    /// on decode was an explicit user-approved scope boundary (fill the field only, no surprise
    /// network activity), and <c>RxAutoPush</c> is an unrelated legacy mechanism out of this phase's
    /// scope (one-line note, not chased further per this project's ADHD-scoping rule).</summary>
    private void OnStationIdDecoded(FskStationIdDecodedInfo info)
    {
        Dispatcher.UIThread.Post(() => _ = ApplyStationIdDecodedAsync(info));
    }

    /// <summary>The actual auto-fill logic, split from <see cref="OnStationIdDecoded"/> so that
    /// method can stay a trivial, guaranteed-non-blocking dispatch. Runs entirely on the UI thread
    /// (posted there before this is ever called) -- the one <see langword="await"/> below resumes
    /// there too (Avalonia's dispatcher establishes a UI-thread <c>SynchronizationContext</c>), so
    /// every <c>[ObservableProperty]</c> touch remains UI-thread-only.</summary>
    private async Task ApplyStationIdDecodedAsync(FskStationIdDecodedInfo info)
    {
        if (info.Callsign is { } decodedCallsign)
        {
            string? ownCallsign;
            try
            {
                ownCallsign = await _sstvSession.GetOperatorCallsignAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Best-effort, same reasoning as LoadCaptureDeviceNameAsync -- a failure here must not
                // crash the decode-event handling chain; the field simply doesn't auto-fill this time.
                Log.GetOperatorCallsignFailed(_logger, ex);
                return;
            }

            // Self-filter (Main.cpp:3628's strcmp): exact, case-sensitive match against the
            // OPERATOR's own callsign -- NOT the same thing as the structurally-impossible "decoded
            // my own live TX" case (RX is paused during TX, see this method's own doc comment);
            // this instead guards against auto-filling "his callsign" with the operator's own when
            // ANOTHER station's transmission happens to reference/repeat it.
            if (string.Equals(decodedCallsign, ownCallsign, StringComparison.Ordinal))
            {
                return;
            }

            // Main.cpp:3631's strcmp against the CURRENT HisCall field, before writing, is a dedup
            // check -- no separate check needed here: [ObservableProperty]'s generated setter already
            // no-ops (skips the field write and PropertyChanged/CanExecuteChanged raises) when the
            // new value equals the current one, matching legacy's own behavior for free.
            OverrideCallsign = decodedCallsign;
        }
        else if (info.CompactNr is { } compactNr)
        {
            // Main.cpp:2537-2538/RX decoder's own compact-form contract: the compact NR is a raw
            // value here, rendered back to legacy's real on-air text via the same "%03u"-equivalent
            // zero-pad legacy itself uses before formatting into MyRST (Main.cpp:3648) -- D3 matches
            // %03u exactly for this value's range (always < CompactNrUpperBound=4096, so never more
            // than 4 digits either way, same as %03u's own "pad to minimum width, never truncate"
            // behavior).
            ApplyDecodedNrRst(compactNr.ToString("D3", CultureInfo.InvariantCulture));
        }
        else if (info.NrText is { } nrText)
        {
            ApplyDecodedNrRst(nrText);
        }
    }

    /// <summary>Main.cpp:3648's <c>sprintf(bf, "595%s", pDem-&gt;m_fskNRS)</c> -- the "595" prefix is
    /// literal legacy behavior, not a typo for the conventional "599" RST report, verify against
    /// source again before ever changing it. Called only from the UI thread (see
    /// <see cref="ApplyStationIdDecodedAsync"/>'s own doc comment). No manual dedup check needed
    /// (Main.cpp:3649's strcmp against the CURRENT MyRST field) -- same reasoning as
    /// <see cref="OverrideCallsign"/>'s own write above: the generated <c>[ObservableProperty]</c>
    /// setter already no-ops on an equal value.</summary>
    private void ApplyDecodedNrRst(string decodedText)
    {
        DecodedNrRst = $"595{decodedText}";
    }

    /// <summary>Auditor round 2 finding: a round-1 version of this staleness guard captured its OWN
    /// counter at THIS method's own entry -- too late. The window that matters starts at
    /// <see cref="IReceivedImageBuffer.SaveAsync"/>'s own invocation (the encode + disk write it
    /// performs), and a superseding <see cref="ISstvSessionService.ModeDetected"/> landing anywhere
    /// in THAT window -- the likely interleaving for back-to-back bulk-WAV-decode restarts, not an
    /// edge case -- would already have bumped a same-class counter before this method ever ran,
    /// defeating the check. Comparing against <see cref="IReceivedImageBuffer.Generation"/> instead
    /// closes that window: it's captured by the buffer itself, under the same lock as its own
    /// snapshot, at <c>SaveAsync</c>'s true start (see <see cref="IReceivedImageBuffer.Saved"/>'s own
    /// doc comment) and handed to this method as
    /// <paramref name="generation"/>, then compared here against the buffer's OWN then-current value
    /// -- not a second, independently-incremented counter on this class that could drift out of
    /// step. A residual sliver stays open, deliberately accepted rather than fixed (auditor round 3):
    /// a <c>ModeDetected</c> landing in the recorder's own pre-<c>SaveAsync</c> setup (settings
    /// resolve + directory creation, no image work) is invisible to this guard -- closing it would
    /// need threading a generation from the recorder's own completion handler into
    /// <c>SaveAsync</c>'s caller, more API churn than a cosmetic readout justifies.</summary>
    private void OnSaved(string path, int generation)
    {
        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            // Best-effort -- a race against a concurrent delete/move of a file this pane itself just
            // finished writing is not expected, but must not crash the save-completion callback.
            Log.ReadSavedFileSizeFailed(_logger, ex);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_receivedImage.Generation != generation)
            {
                // A newer image has started (or the current one was blanked by a restart) at some
                // point between this save being invoked and this post actually running -- this size
                // belongs to a frame that's no longer the one on screen. Drop it rather than show a
                // stale reading.
                return;
            }

            FileSizeBytes = length;
        });
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

            var current = _receivedImage.Current;
            Image = ImageSourceBitmapConverter.ToBitmap(current);
            var progress = _receivedImage.Progress;
            Progress = progress;

            // Auditor-caught bug: computing over the WHOLE mode-sized canvas mid-decode measures how
            // much of the canvas hasn't been drawn yet (undecoded rows are zeroed Rgb24 -- pure
            // black), not real image content. Limit to rows actually written so far, using the same
            // Progress fraction LineProgressText already derives a row count from. Progress is
            // guaranteed exactly 1.0 on the completing event (IReceivedImageBuffer.Progress's own
            // doc comment), so the completed-image case still gets a full-image pass.
            var decodedRowCount = progress is { } p ? (int)Math.Round(p * current.Height) : 0;
            (ClippedBlackFraction, ClippedWhiteFraction) = LuminanceClipStatistics.Compute(current, decodedRowCount);
        });
    }

    private async Task LoadCaptureDeviceNameAsync()
    {
        try
        {
            CaptureDeviceName = await _sstvSession.GetConfiguredCaptureDeviceNameAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as TxControlsPaneViewModel.LoadOutputDeviceNameAsync -- a
            // failure here leaves the field null (no device shown) rather than blocking construction.
            Log.LoadCaptureDeviceNameFailed(_logger, ex);
        }
    }

    private bool CanLookupQrz() => !IsLookingUpQrz && !string.IsNullOrWhiteSpace(OverrideCallsign);

    /// <summary>Does NOT also check <c>QrzLookupSettings.Enabled</c> -- that would need a new
    /// settings-read path in a <c>ScanlineStudio.UI</c> class (this pane only ever talks to
    /// <see cref="ILogbookSessionService"/>, never <c>ISettingsStore</c> directly, per this
    /// project's layering rule). The disabled/unconfigured case is covered by
    /// <see cref="ILogbookSessionService.LookupCallsignAsync"/>'s own "not configured" result,
    /// surfaced via <see cref="QrzLookupErrorMessage"/> below, not by graying out this button.</summary>
    [RelayCommand(CanExecute = nameof(CanLookupQrz))]
    private async Task LookupQrzAsync(CancellationToken ct)
    {
        IsLookingUpQrz = true;
        try
        {
            var result = await _logbookSession.LookupCallsignAsync(OverrideCallsign!.Trim(), ct);
            if (result.Success)
            {
                LookupName = result.Name;
                LookupQth = result.Qth;
                LookupGrid = result.Grid;
                QrzLookupErrorMessage = null;
            }
            else
            {
                // Prior lookup values (if any) are left as-is -- a failed re-lookup shouldn't wipe
                // a previously-successful result off the screen. Wrapped through a loc format
                // string (same "{0}" pass-through convention as LogbookPaneViewModel's own
                // Panes.Logbook.Status.QrzFailed) rather than bound raw -- result.ErrorReason may
                // be QRZ's own untranslated API error text OR a fallback string from
                // LogbookSessionService, neither of which this app controls/can translate, same
                // established precedent as QrzLogbookUploader's own hardcoded fallback reason.
                QrzLookupErrorMessage = _localization.GetString("Panes.RxFrameMeta.Error.LookupFailed", result.ErrorReason ?? string.Empty);
                Log.QrzLookupFailed(_logger, result.ErrorReason);
            }
        }
        catch (Exception ex)
        {
            QrzLookupErrorMessage = _localization.GetString("Panes.RxFrameMeta.Error.LookupFailed", ex.Message);
            Log.QrzLookupThrew(_logger, ex);
        }
        finally
        {
            IsLookingUpQrz = false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading configured RX capture device name failed")]
        public static partial void LoadCaptureDeviceNameFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the just-saved RX image's file size failed")]
        public static partial void ReadSavedFileSizeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ lookup failed: {Reason}")]
        public static partial void QrzLookupFailed(ILogger logger, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ lookup threw")]
        public static partial void QrzLookupThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the operator's own callsign (for the decoded station-ID self-filter) failed")]
        public static partial void GetOperatorCallsignFailed(ILogger logger, Exception ex);
    }
}
