using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Cw;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Worked-before plan (2026-09-01): tri-state, NOT a nullable string -- <see langword="null"/>
/// would mean both "not checked yet" and "confirmed no prior contact," which is un-renderable (no way
/// to show a "New station" state distinct from "nothing checked"). <see cref="Unknown"/> covers both
/// "no callsign to check" and "the check itself failed" -- a failed check is not the same claim as
/// "confirmed new," so it must never render as <see cref="None"/>. Top-level, not nested in
/// <see cref="RxImagePaneViewModel"/> -- CommunityToolkit's <c>[ObservableProperty]</c> generator names
/// the public property after the backing field (<c>WorkedBeforeStatus</c>), which collides with a
/// same-named nested type.</summary>
public enum WorkedBeforeStatus
{
    Unknown,
    None,
    Found,
}

/// <summary>The in-progress/completed decoded receive image, hosted in the fixed Receive tab's
/// "Incoming frame" card (spec/09-ui.md).
///
/// <b>Coalescing (Phase-3 plan finding #5)</b>: <see cref="IReceivedImageBuffer.Updated"/> fires
/// synchronously from the audio drain thread, once per decoded scanline group -- at most one
/// <c>Dispatcher.UIThread.Post</c> is ever in flight; a burst of updates while one is pending just
/// means the eventual post reads whatever <see cref="IReceivedImageBuffer.Current"/> is *then*
/// (latest-wins), not a queued backlog of every intermediate scanline.</summary>
public sealed partial class RxImagePaneViewModel : ViewModelBase, IDisposable
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

    /// <summary>Same value as <c>RxHistoryPaneViewModel.NotePersistDebounce</c> -- this card's Note
    /// field is the same underlying <see cref="IReceiveHistoryStore.SetNoteAsync"/> write, just a
    /// second UI surface for it (the Gallery tab's own Note field, on a user-SELECTED history entry,
    /// already used this debounce/persist shape first).</summary>
    private static readonly TimeSpan NotePersistDebounce = TimeSpan.FromMilliseconds(600);

    /// <summary>Worked-before plan (2026-09-01): absorbs per-keystroke manual typing into
    /// <see cref="OverrideCallsign"/> (the auto-decode write is already a single event, so this only
    /// matters on that path) -- imperceptible on both the auto-decode and log-just-happened
    /// (<see cref="NotifyQsoLogged"/>) triggers, neither of which is latency-sensitive.</summary>
    private static readonly TimeSpan WorkedBeforeDebounce = TimeSpan.FromMilliseconds(250);

    private readonly IReceivedImageBuffer _receivedImage;
    private readonly ISstvSessionService _sstvSession;
    private readonly ILocalizationService _localization;
    private readonly ILogbookSessionService _logbookSession;
    private readonly IFilePickerService _filePickerService;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<RxImagePaneViewModel> _logger;
    private readonly IUrlLauncher? _urlLauncher;
    private readonly IClipboardImageService? _clipboardImageService;
    private readonly ILogger<ImageViewerWindowViewModel>? _imageViewerLogger;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly object _gate = new();

    /// <summary>Captured (not fire-and-forget-discarded, unlike this class's other Load*Async
    /// calls) so <see cref="ReassignQuickModeSlotAsync"/> can await it first -- reassigning a slot
    /// before the persisted grid has loaded must not race the load's own later write. Awaiting the
    /// SAME task instance the load itself is running as makes the ordering deterministic: the load
    /// always finishes applying its result before a concurrent reassignment continues past this
    /// await, so a reassignment can never be reverted by, or race, the load's own write (round-2
    /// plan-review finding). An earlier draft of this fix ALSO gated the load's continuation behind
    /// a "user already touched this" flag as a second guard -- removed after it broke exactly the
    /// scenario it was meant to protect: both the load and a racing reassignment were parked on the
    /// same test gate, the flag was set (synchronously, before the reassignment's own await) the
    /// moment the reassignment command started, and by the time the gate released, the load's
    /// continuation saw the flag already set and skipped applying its own legitimately-pending
    /// result. The await alone is sufficient and doesn't have this failure mode.</summary>
    private Task _loadQuickModeGridTask = Task.CompletedTask;
    private bool _postScheduled;

    /// <summary>fsk_cwid.md §A5: this pane's own latched "current reception" identity, replacing the
    /// earlier <see cref="IReceivedImageBuffer.Generation"/>-based stale guard in
    /// <see cref="ApplyStationIdDecodedAsync"/> -- one source of truth
    /// (<see cref="ISstvSessionService.CurrentReceptionSequence"/>) instead of a second,
    /// independently-timed counter. Captured synchronously inside <see cref="OnModeDetected"/>'s own
    /// handler (the read-in-callback contract that property's doc comment requires), then closed over
    /// into that method's own <see cref="Dispatcher.UIThread"/> post -- same "no separate lock needed,
    /// the dispatcher queue itself serializes access" reasoning as every other cross-thread field on
    /// this class. 0 until the first real <see cref="ISstvSessionService.ModeDetected"/> fires,
    /// matching <see cref="ISstvSessionService.CurrentReceptionSequence"/>'s own "0 = unset" contract.</summary>
    private long _currentReceptionSequence;

    /// <summary>Path most recently handed to <see cref="OnSaved"/> -- correlation key for
    /// <see cref="OnHistoryRecorded"/> below (see that method's own doc comment for the full
    /// reasoning). UI-thread-only: written and read exclusively from inside a
    /// <see cref="Dispatcher.UIThread"/> post, same "no separate lock needed, the dispatcher queue
    /// itself serializes access" reasoning as every other cross-thread field on this class.</summary>
    private string? _lastSavedPath;

    /// <summary>The <see cref="ReceiveHistoryEntry.Id"/> this card's currently-displayed frame
    /// corresponds to, once known -- see <see cref="OnHistoryRecorded"/>. <see langword="null"/>
    /// until a save+record round-trip actually completes for the CURRENT frame (a still-decoding or
    /// just-blanked-by-restart frame has no history row yet) -- <see cref="CanEditFrameMetadata"/>
    /// gates Note/Flag on this being non-null, closing the real gap an earlier version of this pane
    /// left as a documented, disabled stub ("this pane has no way to learn a just-saved frame's
    /// ReceiveHistoryEntry id yet").</summary>
    private string? _currentEntryId;

    /// <summary>ui_transition_plan.md step 6 (T2-4). The rig's frequency/mode LATCHED from
    /// <see cref="ReceiveHistoryEntry.FrequencyHz"/>/<see cref="ReceiveHistoryEntry.RigMode"/> at
    /// <see cref="OnHistoryRecorded"/> -- read off the DB entry itself, NEVER re-read from live radio
    /// state, so the on-screen row and the stored row are identical by construction (auditor
    /// plan-review, 2026-08-29). <see langword="null"/> for the whole duration of a reception (from
    /// <see cref="OnModeDetected"/>'s reset until this frame's own <see cref="OnHistoryRecorded"/>
    /// fires) -- a deliberate, accepted behavior change from the old always-live row: showing the
    /// live VFO while still decoding would still be "correct" in the moment, but reintroduces
    /// exactly the machinery (a second live-vs-latched code path) this fix exists to remove, for a
    /// value that's about to be overwritten anyway once the frame completes.</summary>
    private long? _latchedFrequencyHz;

    private RadioMode? _latchedRigMode;

    /// <summary>Guards <see cref="OnNoteChanged"/>/<see cref="OnIsFlaggedChanged"/> while
    /// <see cref="OnHistoryRecorded"/>/<see cref="OnModeDetected"/> are themselves assigning
    /// <see cref="Note"/>/<see cref="IsFlagged"/> from a freshly-recorded/reset entry -- same
    /// "suppress the persist-on-load echo" convention as
    /// <c>RxHistoryPaneViewModel._suppressSelectedEntryEdits</c>.</summary>
    private bool _suppressFrameMetadataEdits;

    /// <summary>Receive tab's "Previous frames" strip -- a session-only rolling list of the last
    /// <see cref="PreviousFramesCapacity"/> COMPLETED receptions, newest first. Deliberately NOT the
    /// same collection/query as <c>RxHistoryPaneViewModel.Entries</c> (that one is DB-backed,
    /// filtered by the Gallery tab's own <c>ShowTodayOnly</c> toggle, persists across restarts) --
    /// user decision 2026-08-26: this strip must show only this session's own last
    /// <see cref="PreviousFramesCapacity"/> frames, independent of any Gallery UI state. Lives only
    /// in memory for this pane's own lifetime;
    /// starts empty on every app launch. See <see cref="AddPreviousFrameAsync"/> for how it's kept
    /// sorted/capped.</summary>
    public ObservableCollection<RxHistoryEntryViewModel> PreviousFrames { get; } = [];

    /// <summary>fsk_cwid.md A4: session-only "Stations heard" rolling list -- mirrors
    /// <see cref="PreviousFrames"/> in shape and lifetime (in-memory only, starts empty on every app
    /// launch, capped at <see cref="StationsHeardCapacity"/>, newest first). Deliberately NOT reset
    /// on <see cref="OnModeDetected"/> -- that is the entire point: unlike <see cref="OverrideCallsign"/>/
    /// <see cref="DecodedNrRst"/>/<see cref="CwIdText"/> (per-RECEPTION state that a second station's
    /// ID silently replaces), this list accumulates across the whole session so a second station
    /// heard right after the first never erases the first from the operator's view. See
    /// <see cref="AddStationHeard"/> for how entries are added/capped.</summary>
    public ObservableCollection<StationIdHeardViewModel> StationsHeard { get; } = [];

    // fsk_cwid.md A4: "capacity ~20" -- the plan's own stated figure, distinct from PreviousFramesCapacity
    // (6) just below, which is a separate, deliberately much smaller strip (Fable operator-perspective
    // punch list, see that constant's own comment).
    private const int StationsHeardCapacity = 20;

    /// <summary>fsk_cwid.md A4: inserts newest-first, capped at <see cref="StationsHeardCapacity"/> --
    /// same insert-at-0/trim-from-the-end shape as <see cref="AddPreviousFrameAsync"/>'s own list
    /// maintenance, just without that method's DB/thumbnail work (this list's own data is already
    /// fully in hand from the decode event itself, no async fetch needed). Called from BOTH
    /// <see cref="ApplyStationIdDecodedAsync"/> and <see cref="ApplyCwIdDecodedAsync"/> as their OWN
    /// FIRST statement -- before either method's stale-guard or (for FSK callsigns) self-filter check,
    /// deliberately: this list logs every decode EVENT that actually happened, independent of whether
    /// the field-level auto-fill below ends up applying it to the CURRENT reception's card row. A
    /// stale or self-filtered decode is still a real event worth keeping in the log -- the same
    /// "a second station never silently erases the first" reasoning this list exists for in the first
    /// place applies equally to "a late-arriving decode never silently vanishes from the log."</summary>
    private void AddStationHeard(string text, string source, long receptionSequence)
    {
        StationsHeard.Insert(0, new StationIdHeardViewModel(text, source, DateTimeOffset.UtcNow, DetectedMode?.Id, receptionSequence));
        while (StationsHeard.Count > StationsHeardCapacity)
        {
            StationsHeard.RemoveAt(StationsHeard.Count - 1);
        }
    }

    // Fable operator-perspective punch list (2026-09-01), priority #3: legacy kept 32 on the same
    // page; this port's own strip forced a Gallery tab switch after only 2, even though operators
    // routinely compare the last 4-6 receptions during a net or pileup. 6 covers that without
    // reproducing legacy's much larger on-page list -- the strip already scrolls
    // (MainWindow.axaml's ScrollViewer around the ItemsControl), so raising this is a pure capacity
    // change, no new layout needed.
    private const int PreviousFramesCapacity = 6;

    /// <summary>ui_transition_plan.md step 3 (T1-5). Handled in MainWindow.axaml.cs, same
    /// "source VM constructs the child view-model, the handler just wraps it in a View" convention
    /// <see cref="MainViewModel.MacrosReferenceRequested"/> already uses.</summary>
    public event Action<ImageViewerWindowViewModel>? ImageViewerRequested;

    /// <summary>Opens the full-size viewer over <see cref="PreviousFrames"/>. A null
    /// <paramref name="startEntry"/> (the live incoming-frame area's own double-tap -- it has no
    /// single <c>ReceiveHistoryEntry</c> of its own while still decoding) defaults to index 0:
    /// <see cref="PreviousFrames"/> is sorted most-recent-first (see its own doc comment), so index
    /// 0 is the most recently COMPLETED reception, the closest real stand-in for "the current
    /// picture" once one exists. A silent no-op before any reception has completed, or when the
    /// optional viewer dependencies are unset (see this class's own constructor doc comment).
    /// </summary>
    [RelayCommand]
    private void OpenImageViewer(RxHistoryEntryViewModel? startEntry)
    {
        if (PreviousFrames.Count == 0 || _urlLauncher is null || _clipboardImageService is null || _imageViewerLogger is null)
        {
            return;
        }

        var startIndex = startEntry is null ? 0 : Math.Max(PreviousFrames.IndexOf(startEntry), 0);
        var viewerViewModel = new ImageViewerWindowViewModel(
            new List<RxHistoryEntryViewModel>(PreviousFrames), startIndex, _historyStore, _urlLauncher, _clipboardImageService, _localization, _imageViewerLogger);
        ImageViewerRequested?.Invoke(viewerViewModel);
    }

    /// <summary>Same thumbnail size as <c>RxHistoryPaneViewModel.ThumbnailMaxDimension</c> -- kept as
    /// its own constant rather than a shared one since the two view-models have no common base to
    /// hang it on and this strip's thumbnails are a genuinely separate render (different card,
    /// different collection).</summary>
    private const int PreviousFramesThumbnailMaxDimension = 96;

    private CancellationTokenSource? _notePersistCts;

    /// <summary>Worked-before plan (2026-09-01): cancel-and-replace debounce, same shape as
    /// <see cref="_notePersistCts"/> -- guards both against per-keystroke manual-typing queries in
    /// <see cref="OnOverrideCallsignChanged"/> AND against a stale in-flight query for a callsign the
    /// operator has since cleared/changed latching a wrong result (cancelled unconditionally, before
    /// any other branching, in both <see cref="OnOverrideCallsignChanged"/> and
    /// <see cref="NotifyQsoLogged"/>).</summary>
    private CancellationTokenSource? _workedBeforeCts;

    /// <summary>Same ordering-safety shape as <c>RxHistoryPaneViewModel._pendingFlagPersist</c> --
    /// chained onto whatever's currently pending rather than fired independently, so a rapid
    /// double-toggle can't complete out of order (no per-write ordering guarantee otherwise).</summary>
    private Task _pendingFlagPersist = Task.CompletedTask;

    /// <summary>Same ordering-safety shape as <see cref="_pendingFlagPersist"/> immediately above --
    /// see <see cref="OnSenseLevelChanged"/>'s own doc comment.</summary>
    private Task _pendingSenseLevelPersist = Task.CompletedTask;

    /// <summary>Same ordering-safety shape as <see cref="_pendingSenseLevelPersist"/> immediately
    /// above -- rapid clicks on the DSP card's AFC toggle must not race their settings-file writes.</summary>
    private Task _pendingAfcPersist = Task.CompletedTask;

    /// <summary>True once <see cref="_currentEntryId"/> is known -- gates the Note/Flag controls'
    /// <c>IsEnabled</c>. See <see cref="_currentEntryId"/>'s own doc comment for why this can be
    /// false even for a fully-decoded, on-screen image (the save+record round-trip hasn't completed
    /// yet, or this is mid-decode with nothing saved at all).</summary>
    public bool CanEditFrameMetadata => _currentEntryId is not null;

    /// <summary>Fable UX-review finding, 2026-08-30: exposes <see cref="_currentEntryId"/> so
    /// <c>MainWindow.axaml.cs</c>'s <c>LogQsoRequested</c> handler can pass it through to
    /// <c>LogbookPaneViewModel.PrefillForNewEntry</c> -- previously a QSO logged straight from a
    /// decoded frame carried no <c>ReceivedImageId</c> at all, so it never linked back to this
    /// frame's own history entry (unlike Gallery's separate "Open in log" path, which does link).
    /// <see langword="null"/> for the same reasons <see cref="CanEditFrameMetadata"/> can be
    /// false.</summary>
    public string? CurrentEntryId => _currentEntryId;

    /// <summary>RxFrameMeta card's editable Note field -- the SAME underlying
    /// <see cref="IReceiveHistoryStore.SetNoteAsync"/> write <c>RxHistoryPaneViewModel.SelectedEntryNote</c>
    /// already uses, just reachable from the Receive tab's live frame directly instead of requiring a
    /// trip to the Gallery tab and re-selecting the entry there.</summary>
    [ObservableProperty]
    private string? _note;

    /// <summary>RxFrameMeta card's Flag toggle -- same reasoning as <see cref="Note"/>, backing
    /// <see cref="IReceiveHistoryStore.SetFlaggedAsync"/>.</summary>
    [ObservableProperty]
    private bool _isFlagged;

    /// <summary>Surfaces a Note/Flag persist failure -- same <c>RxHistoryPaneViewModel.ErrorMessage</c>
    /// convention (both underlying store methods' own doc comments: a missing-entry return "is a
    /// reachable case... the caller is expected to surface that to the user, not silently ignore
    /// it"). Deliberately SEPARATE from <see cref="QrzLookupErrorMessage"/> -- a Note/Flag persist
    /// failure and a QRZ lookup failure are unrelated actions in the same card; conflating them into
    /// one property would clear/overwrite one error while reporting the other.</summary>
    [ObservableProperty]
    private string? _frameMetadataErrorMessage;

    [ObservableProperty]
    private Bitmap? _image;

    // T0-11 (production_audit.md): 2 recycled buffers instead of a fresh WriteableBitmap on every
    // coalesced decode update -- see WriteableBitmapPool's own doc comment. IDisposable is added
    // below only because CA1001 requires it of any type owning a disposable field, not because
    // anything new calls Dispose() -- this VM is a DI singleton with no existing teardown hook
    // before this fix, and the only "teardown" that would ever run is final process-exit
    // container disposal (plan-review decision: not worth wiring a NEW explicit teardown call for
    // a 2-bitmap benefit).
    private readonly WriteableBitmapPool _imagePool = new();

    /// <summary>Real, already-computed fraction of the current decode's total rows -- see
    /// <see cref="IReceivedImageBuffer.Progress"/>'s own doc comment for the exact-1.0-on-completion
    /// guarantee this pane relies on. Read alongside <see cref="Image"/> in <see cref="OnUpdated"/>
    /// (same coalesced <see cref="IReceivedImageBuffer.Updated"/> event), not polled separately.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LineProgressText))]
    [NotifyPropertyChangedFor(nameof(ClipLoHiDisplay))]
    [NotifyPropertyChangedFor(nameof(RemainingText))]
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
    /// a blank image next to a live-looking "Started" readout. <see cref="ISstvSessionService.DecodeRestarted"/>
    /// is now exposed (2026-08-15, logging-coverage audit) and this VM already subscribes to it
    /// (see <see cref="OnDecodeRestarted"/>) -- but that subscriber is logging-only by deliberate
    /// scope decision (a logging-coverage fix, not a UI-behavior fix), so this edge case is still not
    /// wired up; not worth folding into that pass for one edge case.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartedDisplay))]
    private DateTimeOffset? _startedAt;

    /// <summary>The RX capture device's display name -- what
    /// <see cref="ISstvSessionService.StartReceivingAsync"/> would actually resolve and use right
    /// now, including a fallback to the backend-reported default device when nothing is explicitly
    /// configured (spec/18-path-to-1.0.md Critical item 1 / item 8; see
    /// <see cref="ISstvSessionService.GetConfiguredCaptureDeviceNameAsync"/>'s own doc comment for
    /// the full "what WOULD be resolved, not necessarily currently in-flight" caveat) -- backs
    /// mock2's Receive tab "Device" field, exact mirror of
    /// <c>TxControlsPaneViewModel.OutputDeviceName</c>'s own pattern. <see langword="null"/> until
    /// the best-effort initial load below completes, or if a configured device is no longer
    /// present, or (now the rare case) nothing is configured AND the backend reports no default
    /// either. Loaded once at construction, not re-fetched on a live settings change while this
    /// pane stays open -- same convention as the TX-side property. TX-side
    /// <c>TxControlsPaneViewModel.OutputDeviceName</c> now has the exact same
    /// <c>OutputDeviceNameDisplay</c> wrapper and is genuinely wired to `TxControlsPaneView.axaml`
    /// (fixed 2026-08-11, was a stale gap noted here before that).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CaptureDeviceNameDisplay))]
    private string? _captureDeviceName;

    public string CaptureDeviceNameDisplay => CaptureDeviceName ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlantPpmDisplay))]
    [NotifyPropertyChangedFor(nameof(SlantPpmStatusBarDisplay))]
    [NotifyPropertyChangedFor(nameof(AutoCorrectDisplay))]
    private double? _slantPpm;

    /// <summary>Live, polled telemetry (see <see cref="PollTelemetry"/>) -- NOT the swap-refreshed
    /// shape <see cref="RxBpfPreset"/> above uses. Defaults to
    /// <see cref="SstvSyncSource.Idle"/> (the CLR's own enum default, and also the correct value
    /// before this pane's first poll tick).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SyncSourceDisplay))]
    private SstvSyncSource _syncSource;

    /// <summary>Sync &amp; Slant card's "Source" row. Localized so a future locale can phrase these
    /// 3 states in whatever way reads naturally, not hardcoded English.</summary>
    public string SyncSourceDisplay => SyncSource switch
    {
        SstvSyncSource.Locked => _localization.GetString("Panes.RxSync.SourceValue.Locked"),
        SstvSyncSource.AvtTraining => _localization.GetString("Panes.RxSync.SourceValue.AvtTraining"),
        _ => _localization.GetString("Panes.RxSync.SourceValue.Idle"),
    };

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

    /// <summary>ui_transition_plan.md step 12, Step 4: real, varying backing for the main-window
    /// status bar's auto-save-audio chip -- see <see cref="ISstvSessionService.IsAudioAutoSaveActive"/>'s
    /// own doc comment for why this must never be a static reflection of the Options enable toggle.</summary>
    [ObservableProperty]
    private bool _isAudioAutoSaveActive;

    /// <summary>The currently (or most recently) auto-detected RX mode -- real data from
    /// <see cref="ISstvSessionService.ModeDetected"/>. spec/18-path-to-1.0.md High item 7 update:
    /// <see cref="ScanlineStudio.Abstractions.Sstv.ISstvDecoder"/> still always auto-detects via
    /// the VIS header by default, but a one-shot manual override now exists --
    /// <see cref="QuickSelectModeCommand"/> (backed by <see cref="ISstvSessionService.ForceMode"/>)
    /// forces the NEXT decode into a specific mode, matching legacy's real quick-mode-button click;
    /// it is NOT a persistent lock (auto-detect resumes for the transmission after). mock2's own
    /// Auto/Locked segmented control (which used to show "Locked" permanently disabled, with no
    /// feature behind it) was removed outright 2026-08-26; the real persistent-lock feature this
    /// summary used to describe as missing now exists as a SEPARATE, differently-shaped mechanism --
    /// see <see cref="HeldMode"/>/<see cref="HoldModeCommand"/> below.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogQsoCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveFrameCommand))]
    private SstvModeDefinition? _detectedMode;

    /// <summary>Mode card's "Listening / Paused" toggle -- port of legacy's RX-page <c>SBAuto</c>
    /// (<c>TMmsstv::RxAutoPush</c>, `Main.cpp:6042-6060`). Optimistic UI, same shape as
    /// <c>TxControlsPaneViewModel.AutoFollowRxMode</c>: set immediately on click, no round-trip poll
    /// needed. See <see cref="ISstvSessionService.SetAutoDetectPaused"/>'s own doc comment for the
    /// full session/decoder-layer design (four rounds of plan-readiness review).</summary>
    [ObservableProperty]
    private bool _isAutoDetectPaused;

    /// <summary>Frame-metadata card's "Size on disk" row -- real, but only for a COMPLETED save: set
    /// from <see cref="IReceivedImageBuffer.Saved"/>, the only hook a live pane has to "what file did
    /// this frame end up as" -- this fires for BOTH real production write paths:
    /// <c>ReceiveHistoryRecorder</c>'s silent auto-archival (a wholly separate class in a different
    /// layer, no reference back to this pane -- it writes its own already-captured pixel snapshot
    /// directly and raises this event via <see cref="IReceivedImageBuffer.NotifySaved"/>, rather than
    /// going through <see cref="IReceivedImageBuffer.SaveAsync"/> itself, since an async read of
    /// <see cref="IReceivedImageBuffer.Current"/> would race a <c>DecodeRestarted</c> that can blank
    /// it) and this pane's own manual <see cref="SaveFrameAsync"/> (which DOES go through
    /// <see cref="IReceivedImageBuffer.SaveAsync"/>) -- whichever one most recently finished is what
    /// this row reflects. Reset to <see langword="null"/> on every fresh
    /// <see cref="OnModeDetected"/> (a new/restarted decode has no saved file yet), same lifetime
    /// rule as <see cref="StartedAt"/>. An abandoned/partial image's own save (which
    /// <c>ReceiveHistoryRecorder</c> deliberately routes around both <see cref="IReceivedImageBuffer.SaveAsync"/>
    /// and <see cref="IReceivedImageBuffer.NotifySaved"/> for -- see that class's own doc comment)
    /// never raises this event, so this stays "—" for a frame that gets superseded before completing,
    /// matching every other placeholder in this pane.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSizeDisplay))]
    private long? _fileSizeBytes;

    /// <summary>Frame-metadata card's "Override callsign" field (spec/09-ui.md, legacy's real
    /// <c>HisCall</c> equivalent) -- also the input to <see cref="LookupQrzCommand"/>. Auto-filled by
    /// <see cref="OnStationIdDecoded"/> from a decoded FSK station-ID (CW-ID/FSK station-ID subsystem
    /// Phase 5), but still plain, directly user-editable state otherwise -- not backed by
    /// <see cref="IReceivedImageBuffer"/> or any session model (no "current QSO" tracker exists in
    /// this port, matching the auto-fill's own gate simplification, see
    /// <see cref="OnStationIdDecoded"/>'s doc comment). Also feeds the read-only "Callsign" row's
    /// <see cref="CallsignDisplay"/> (0.9-beta UI-honesty pass) -- the remaining, still-unseeded
    /// source is OCR, which that row no longer claims to be (caption trimmed from "Callsign · OCR" to
    /// plain "Callsign" since it now shows the real FSK-decoded value, not an OCR result). That plain
    /// caption itself is untouched by fsk_cwid.md A3 -- see <see cref="CallsignLabelDisplay"/> for the
    /// separate, SIBLING source/time annotation A3 added next to it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupQrzCommand))]
    [NotifyPropertyChangedFor(nameof(CallsignDisplay))]
    private string? _overrideCallsign;

    /// <summary>Read-only counterpart to the editable <see cref="OverrideCallsign"/> TextBox below it
    /// on the same card -- same value, same "—" empty-state convention as <see cref="NameDisplay"/>/
    /// <see cref="QthDisplay"/>, added so the card's top "Callsign" row shows the real FSK-decoded
    /// value instead of a hardcoded literal.</summary>
    public string CallsignDisplay => string.IsNullOrWhiteSpace(OverrideCallsign) ? "—" : OverrideCallsign;

    /// <summary>fsk_cwid.md A3: set alongside <see cref="OverrideCallsign"/> ONLY by the auto-fill
    /// write sites (<see cref="ApplyStationIdDecodedAsync"/>/<see cref="ApplyCwIdDecodedAsync"/>),
    /// AFTER the property write itself -- <see cref="OnOverrideCallsignChanged"/> clears both fields
    /// on every real change to <see cref="OverrideCallsign"/> (manual edit included), so a manually-
    /// typed callsign correctly reverts <see cref="CallsignLabelDisplay"/> to empty (no annotation)
    /// instead of keeping a stale source claim next to the static caption.</summary>
    private string? _callsignSource;

    private DateTimeOffset? _callsignDecodedAtUtc;

    /// <summary>fsk_cwid.md A3 auditor code-review finding: a SIBLING annotation next to the static
    /// "Callsign" caption (its own separate AXAML column), not a replacement for it -- an earlier
    /// version returned the static caption text as this property's own empty-state fallback, which
    /// silently overwrote the caption the instant a decode landed; the plan's own wording is "reads
    /// W1AW WITH a secondary FSK · 14:02:11Z", not "instead of." Empty string (not the caption) is
    /// this property's own empty-state value now -- "silence is indistinguishable from broken"
    /// reasoning (same as <see cref="WorkedBeforeDisplay"/>'s own "New station" text) still applies,
    /// it just applies to whether ANY annotation shows, not to what replaces the caption. Shows e.g.
    /// "FSK · 14:02:11Z" once a decode has actually auto-filled <see cref="OverrideCallsign"/>,
    /// reverting to empty otherwise (including after a manual edit -- see <see cref="_callsignSource"/>'s
    /// own doc comment). Timestamp is UTC, captured at VM receipt (same convention as
    /// <see cref="StartedDisplay"/>).</summary>
    public string CallsignLabelDisplay => _callsignSource is { } source && _callsignDecodedAtUtc is { } at
        ? _localization.GetString("Panes.RxFrameMeta.SourceTimeFormat", source, at.UtcDateTime)
        : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkedBeforeDisplay))]
    private WorkedBeforeStatus _workedBeforeStatus = WorkedBeforeStatus.Unknown;

    /// <summary>Populated only when <see cref="WorkedBeforeStatus"/> is <see cref="WorkedBeforeStatus.Found"/>
    /// -- e.g. "20m · 2026-08-12", or "3× · 20m · 2026-08-12" when more than one prior contact
    /// exists. Built by <see cref="RefreshWorkedBeforeCoreAsync"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkedBeforeDisplay))]
    private string? _workedBeforeSummary;

    /// <summary>The RxFrameMeta card's "Worked before" row binds this one string --
    /// <see cref="WorkedBeforeStatus"/>/<see cref="WorkedBeforeSummary"/> are two source properties,
    /// so BOTH carry <c>[NotifyPropertyChangedFor(nameof(WorkedBeforeDisplay))]</c> above (omitting it
    /// on either one reproduces the "derived property with no notification never updates" bug
    /// documented at <c>LogbookPaneViewModel.cs</c>'s own equivalent case). Same "—" empty-state
    /// convention as <see cref="CallsignDisplay"/>/<see cref="DecodedNrRstDisplay"/> for the
    /// not-checked-yet/failed state; the "New station" text for <see cref="WorkedBeforeStatus.None"/>
    /// is localized (Fable design-review: silence there is indistinguishable from "feature broken,"
    /// and a row that appears/disappears shifts layout mid-session).</summary>
    public string WorkedBeforeDisplay => WorkedBeforeStatus switch
    {
        WorkedBeforeStatus.Found => WorkedBeforeSummary ?? "—",
        WorkedBeforeStatus.None => _localization.GetString("Panes.RxFrameMeta.WorkedBefore.None"),
        _ => "—",
    };

    /// <summary>Legacy's real <c>MyRST</c> equivalent (<c>Main.cpp:3648</c>,
    /// <c>sprintf("595%s", pDem-&gt;m_fskNRS)</c>) -- the decoded NR/RST exchange from a station-ID's
    /// optional sub-packet, auto-filled by <see cref="OnStationIdDecoded"/>. Deliberately not named
    /// <c>MyRst</c> (a literal legacy-field-name port) -- follows <see cref="OverrideCallsign"/>'s
    /// own precedent of an English, descriptive name instead. Wired to a real card row 2026-08-26 --
    /// see <see cref="DecodedNrRstDisplay"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecodedNrRstDisplay))]
    private string? _decodedNrRst;

    /// <summary>Read-only "NR / RST" row on the RxFrameMeta card, right after Callsign -- both come
    /// from the same FSK station-ID decode event (<see cref="OnStationIdDecoded"/>), unlike Name/QTH
    /// below (a separate QRZ lookup). Same "—" empty-state convention as <see cref="NameDisplay"/>/
    /// <see cref="QthDisplay"/>.</summary>
    public string DecodedNrRstDisplay => DecodedNrRst ?? "—";

    /// <summary>fsk_cwid.md A3: set alongside <see cref="DecodedNrRst"/> in <see cref="ApplyDecodedNrRst"/>
    /// -- unlike <see cref="_callsignSource"/>, no "manual edit" case exists here (no TextBox binds
    /// to <see cref="DecodedNrRst"/> anywhere), so the source is always "FSK" and there is nothing to
    /// clear/revert.</summary>
    private DateTimeOffset? _nrRstDecodedAtUtc;

    /// <summary>fsk_cwid.md A3: same "sibling annotation, empty-string empty-state" shape as
    /// <see cref="CallsignLabelDisplay"/> (see that property's own doc comment for the auditor
    /// finding this fixed), for the "NR / RST" row.</summary>
    public string NrRstLabelDisplay => _nrRstDecodedAtUtc is { } at
        ? _localization.GetString("Panes.RxFrameMeta.SourceTimeFormat", "FSK", at.UtcDateTime)
        : string.Empty;

    /// <summary>fsk_cwid.md §9/B-P3: the CW-ID decoder's raw decoded text (e.g. <c>"DE W1AW"</c>),
    /// auto-filled by <see cref="ApplyCwIdDecodedAsync"/>. A3's row-label annotation (see
    /// <see cref="CwIdLabelDisplay"/>) landed after B-P3 shipped -- this row's own label now carries
    /// it too, closing the scope reduction an earlier version of this comment recorded. §9's WPM
    /// detail (<c>"CW · 14:02:19Z · 28 WPM"</c>) is deliberately NOT included -- A3's own plan text
    /// only shows WPM as part of an EXAMPLE for the CW row specifically, not a requirement stated for
    /// any row, and no other row carries a decode-quality figure in its label either; the
    /// low-confidence hint already carries the CW-specific quality signal, on the VALUE (see
    /// <see cref="CwIdDisplay"/>), not the label.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwIdDisplay))]
    private string? _cwIdText;

    /// <summary>Set alongside <see cref="CwIdText"/> in every <see cref="ApplyCwIdDecodedAsync"/>
    /// write (both come from the same <see cref="CwIdDecodedInfo"/>) -- a plain field, not an
    /// <c>[ObservableProperty]</c>, since nothing binds to it directly; it only ever changes together
    /// with <see cref="CwIdText"/>, so that property's own <c>[NotifyPropertyChangedFor]</c> already
    /// covers <see cref="CwIdDisplay"/>'s dependency on this value too.</summary>
    private double _cwIdConfidence;

    /// <summary>fsk_cwid.md A3: set alongside <see cref="CwIdText"/>/<see cref="_cwIdConfidence"/> in
    /// every <see cref="ApplyCwIdDecodedAsync"/> write -- source is always "CW" (this row has no
    /// other decode source), so there is nothing to clear/revert the way <see cref="_callsignSource"/>
    /// needs to for a manual edit.</summary>
    private DateTimeOffset? _cwIdDecodedAtUtc;

    /// <summary>fsk_cwid.md A3: same "sibling annotation, empty-string empty-state" shape as
    /// <see cref="CallsignLabelDisplay"/> (see that property's own doc comment for the auditor
    /// finding this fixed), for the "CW ID" row.</summary>
    public string CwIdLabelDisplay => _cwIdDecodedAtUtc is { } at
        ? _localization.GetString("Panes.RxFrameMeta.SourceTimeFormat", "CW", at.UtcDateTime)
        : string.Empty;

    /// <summary>fsk_cwid.md §9: "confidence shown only when below a threshold" -- the decoder's own
    /// accept floor (<c>ClassicalCwDecoder.MinAcceptableConfidence</c> = 0.4) is the boundary below
    /// which a result is never raised at all, so this UI-level threshold is deliberately set higher:
    /// it flags "accepted but marginal" results, not "would have been rejected." Not shared with the
    /// decoder's own constant (a different layer, a different question -- "should the UI mention
    /// this" vs. "should this result exist at all").</summary>
    private const double CwIdLowConfidenceThreshold = 0.6;

    /// <summary>Read-only "CW ID" row, immediately after "NR / RST" (fsk_cwid.md §9). Same "—"
    /// empty-state convention as the other decoded-from-frame rows. Appends the low-confidence hint
    /// inline (see <see cref="CwIdLowConfidenceThreshold"/>'s own doc comment for why this isn't the
    /// row-LABEL annotation §9 describes).</summary>
    public string CwIdDisplay => CwIdText is { } text
        ? (_cwIdConfidence < CwIdLowConfidenceThreshold
            ? $"{text} ({_localization.GetString("Panes.RxFrameMeta.CwId.LowConfidence")})"
            : text)
        : "—";

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

    /// <summary>Operator's own configured grid square, loaded once at construction via
    /// <see cref="LoadOperatorGridAsync"/> -- same cached-fire-and-forget pattern as
    /// <see cref="CaptureDeviceName"/>, not a per-call read, since <see cref="ISstvSessionService.GetOperatorGridAsync"/>
    /// reads uncached settings-file disk. Reflects the value at session start; editing the grid in
    /// Options mid-session does not retroactively update an already-open Receive tab (same staleness
    /// characteristic <see cref="CaptureDeviceNameDisplay"/> already has).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridDisplay))]
    private string? _operatorGrid;

    /// <summary>"Grid / dist · QRZ" row -- grid half is the worked station's own grid (from QRZ
    /// lookup); distance half is real, computed via <see cref="MaidenheadLocator.TryComputeDistanceBearing"/>
    /// from <see cref="OperatorGrid"/> (this operator's own configured grid) to <see cref="LookupGrid"/>
    /// (the worked station's grid). "--" (matching this row's own pre-existing placeholder shape) when
    /// either grid is missing or malformed, not an exception -- <see cref="MaidenheadLocator.TryComputeDistanceBearing"/>
    /// is a Try-pattern for exactly this reason.</summary>
    public string GridDisplay => $"{LookupGrid ?? "—"} / {(MaidenheadLocator.TryComputeDistanceBearing(OperatorGrid, LookupGrid, out var distanceKm, out _) ? MaidenheadLocator.FormatDistance(distanceKm) : "--")}";

    /// <summary>ui_transition_plan.md step 6 (T2-4). Same "{0:0.000000} MHz" format
    /// <see cref="RadioStatusViewModel.FrequencyDisplay"/> already uses -- duplicated deliberately
    /// (a one-line format string, same "two view-models, no common base to hang it on" precedent
    /// <see cref="PreviousFramesThumbnailMaxDimension"/>'s own doc comment already establishes in
    /// this class), not shared across layers for this. "—" while no reception has completed yet
    /// (see <see cref="_latchedFrequencyHz"/>'s own doc comment for why that's the whole decoding
    /// duration, not a transient gap).</summary>
    // T1-13 (production_audit.md): "MHz" used to be a hardcoded English literal -- decimal
    // formatting stays ambient-culture, deliberately, matching RadioStatusViewModel.FrequencyDisplay's
    // own reasoning (a live screen-only readout, not a persisted/round-tripped value); interpolation,
    // not ToString(format), dodges CA1305 the same way.
    public string LatchedFrequencyDisplay => _latchedFrequencyHz is { } hz
        ? _latchedRigMode is { } mode
            ? _localization.GetString("Panes.RxImage.LatchedFrequencyWithModeFormat", $"{hz / 1_000_000.0:0.000000}", mode)
            : _localization.GetString("Panes.RxImage.LatchedFrequencyFormat", $"{hz / 1_000_000.0:0.000000}")
        : "—";

    /// <summary>ui_transition_plan.md step 5 (T1-6): raw accessors for the Logbook prefill (Log QSO,
    /// wired in <c>MainWindow.axaml.cs</c>) -- <see cref="LatchedFrequencyDisplay"/> is a formatted
    /// STRING, not a value a form field can bind/edit.</summary>
    public long? LatchedFrequencyHz => _latchedFrequencyHz;

    public RadioMode? LatchedRigMode => _latchedRigMode;

    [ObservableProperty]
    private string? _qrzLookupErrorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupQrzCommand))]
    private bool _isLookingUpQrz;

    /// <summary>Whether QRZ lookup is actually configured (enabled + credentials saved in
    /// Options) -- gates <see cref="LookupQrzCommand"/> ahead of time instead of only failing
    /// after the click. Defaults <c>true</c> (optimistic) until <see cref="LoadQrzLookupConfiguredAsync"/>
    /// resolves in the constructor, matching every other Load*Async property's own "don't flash a
    /// wrong state before the real one loads" convention in this class.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupQrzCommand))]
    private bool _isQrzLookupConfigured = true;

    /// <summary>ui_transition_plan.md step 3 (T1-5): the trailing 3 dependencies are optional
    /// (nullable, default null) purely so this class's ~100 existing direct-construction test call
    /// sites don't all need updating for a feature most of them never exercise -- same trade-off
    /// <see cref="TxImageEditorPaneViewModel"/>'s own <c>canTransmitNow</c> parameter documents.
    /// Production DI always supplies all three; <see cref="OpenImageViewerCommand"/> is a silent
    /// no-op without them (nothing a test that doesn't care about this feature would ever notice).
    /// </summary>
    public RxImagePaneViewModel(
        ISstvSessionService sstvSession,
        ILocalizationService localization,
        ILogbookSessionService logbookSession,
        IFilePickerService filePickerService,
        IReceiveHistoryStore historyStore,
        ISettingsStore settingsStore,
        ILogger<RxImagePaneViewModel> logger,
        IUrlLauncher? urlLauncher = null,
        IClipboardImageService? clipboardImageService = null,
        ILogger<ImageViewerWindowViewModel>? imageViewerLogger = null)
    {
        _receivedImage = sstvSession.ReceivedImage;
        _sstvSession = sstvSession;
        _localization = localization;
        _logbookSession = logbookSession;
        _filePickerService = filePickerService;
        _historyStore = historyStore;
        _settingsStore = settingsStore;
        _logger = logger;
        _urlLauncher = urlLauncher;
        _clipboardImageService = clipboardImageService;
        _imageViewerLogger = imageViewerLogger;
        // Direct field assignment, NOT the generated property setter -- this property has no
        // per-change side effect (unlike SenseLevel below), but seeding the field directly keeps
        // both settings' construction-time init consistent.
        _autoSlantEnabled = sstvSession.AutoSlantEnabled;
        _afcEnabled = sstvSession.AfcEnabled;
        // Direct field assignment, NOT the generated property setter (round-2 plan-review finding):
        // going through the setter would fire OnSenseLevelChanged for a value that's already correct
        // and already saved, spuriously re-requesting/re-persisting it at construction. See
        // SenseLevel's own doc comment. Clamped defensively here too, matching every other layer in
        // this feature (AnalogFmSstvDecoder's own constructor, RestartableSstvDecoder's) -- in
        // production sstvSession.SenseLevel is always already 0-3 (RestartableSstvDecoder's own
        // getter clamps), but this VM shouldn't silently trust that invariant across a layer
        // boundary when the fix is one ternary.
        _senseLevel = sstvSession.SenseLevel is >= 0 and <= 3 ? sstvSession.SenseLevel : 0;
        _lastValidSenseLevel = _senseLevel;
        SenseLevelOptions =
        [
            _localization.GetString("Options.Decode.SenseLevel.VeryLow"),
            _localization.GetString("Options.Decode.SenseLevel.Low"),
            _localization.GetString("Options.Decode.SenseLevel.High"),
            _localization.GetString("Options.Decode.SenseLevel.VeryHigh"),
        ];
        // Direct field assignment, NOT the generated property setter -- same reasoning as
        // _autoSlantEnabled above used to have BEFORE this row became live (restart-required-settings
        // backlog item 6, 2026-08-28) -- the setter now has a real side effect (OnRxBpfPresetChanged
        // below), which the constructor's own initial seed must not trigger.
        _rxBpfPreset = sstvSession.RxBpfPreset;
        // Input Chain card's "BPF" row -- same shape as SenseLevelOptions above, reusing the Options
        // window's own real "Options.Decode.RxBpf.*" locale keys, in RxBpfPreset's own enum order
        // (Off=0, Wide=1, Narrow=2, VeryNarrow=3) -- matches the Options window's own radio button
        // order too.
        RxBpfPresetOptions =
        [
            _localization.GetString("Options.Decode.RxBpf.Normal"),
            _localization.GetString("Options.Decode.RxBpf.Wide"),
            _localization.GetString("Options.Decode.RxBpf.Sharp"),
            _localization.GetString("Options.Decode.RxBpf.VerySharp"),
        ];

        _receivedImage.Updated += OnUpdated;
        _receivedImage.Saved += OnSaved;
        _historyStore.Recorded += OnHistoryRecorded;
        sstvSession.ModeDetected += OnModeDetected;
        sstvSession.DecodeRestarted += OnDecodeRestarted;
        sstvSession.StationIdDecoded += OnStationIdDecoded;
        sstvSession.CwIdDecoded += OnCwIdDecoded;
        // Restart-required-settings backlog item 2 (2026-08-27): the ONLY trigger for
        // RefreshRxBpfPresetFromSession -- see that method's own doc comment for why a pull-based
        // Options-Closed refresh (like AutoSlantEnabled/SenseLevel above use) would be redundant here.
        sstvSession.DecoderInstanceReplaced += OnDecoderInstanceReplaced;
        // Restart-required-settings backlog item 6 (2026-08-28): the BPF row is now a live EDITOR,
        // not a read-only mirror -- a rejected reconfiguration (from ANY source, not just this row's
        // own request; see OnReconfigurationRejected's own doc comment) must re-sync this row back to
        // whatever's actually applied, or it would permanently show a preset that was requested but
        // never took effect.
        sstvSession.ReconfigurationRejected += OnReconfigurationRejected;

        _telemetryTimer = new DispatcherTimer(TelemetryPollInterval, DispatcherPriority.Background, (_, _) => PollTelemetry());
        _telemetryTimer.Start();

        BuildQuickModeSlots(QuickModeGridDefaults.Ids);
        _loadQuickModeGridTask = LoadQuickModeGridAsync();

        _ = LoadCaptureDeviceNameAsync();
        _ = LoadOperatorGridAsync();
        _ = LoadQrzLookupConfiguredAsync();
    }

    // T0-11: satisfies CA1001 (owns a disposable field, _imagePool). This VM IS a DI singleton, so
    // ServiceProvider disposal on app shutdown DOES call this, off the UI thread (Program.cs runs
    // host.DisposeAsync() inside Task.Run) -- code-review finding: MS.DI's disposal loop aborts at
    // the first thrown exception, which would skip every earlier-registered singleton's teardown
    // (including the audio engine). Swallow rather than risk that on an exit-only path.
    public void Dispose()
    {
        try
        {
            _imagePool.Dispose();
            // Worked-before plan (2026-09-01): AFTER _imagePool.Dispose(), not before -- a throw from
            // Cancel() ahead of it would skip pool disposal and mislog as ImagePoolDisposeFailed.
            // Cancellation itself is thread-safe even though this whole block runs off the UI thread.
            _workedBeforeCts?.Cancel();
        }
        catch (Exception ex)
        {
            Log.ImagePoolDisposeFailed(_logger, ex);
        }
    }

    public string DetectedModeText => DetectedMode?.DisplayName ?? "—";

    /// <summary>"Scottie 1 — VIS 60"-shaped, matching mock2's own active-mode dropdown content
    /// exactly (DisplayName + real VisCode, not a placeholder).</summary>
    public string DetectedModeDisplay => DetectedMode is { } mode ? $"{mode.DisplayName} — VIS {mode.VisCode}" : "—";

    // T1-13 (production_audit.md): "ms" used to be a hardcoded English literal.
    public string LineTimeText => DetectedMode is { } mode ? _localization.GetString("Panes.RxImage.LineTimeFormat", $"{mode.LineDurationMs:0.0}") : "—";

    public string LinesText => DetectedMode is { } mode ? mode.ImageHeight.ToString(CultureInfo.InvariantCulture) : "—";

    /// <summary>Status bar's "line N / total" readout -- real, derived from <see cref="Progress"/>
    /// times the detected mode's own <c>ImageHeight</c>. No separate line-index property exists on
    /// this pane or the decoder; <see cref="Progress"/> is the one already-exposed source for this
    /// (spec/17-rx-telemetry-feasibility.md).</summary>
    public string LineProgressText => Progress is { } progress && DetectedMode is { } mode
        ? _localization.GetString("MainWindow.StatusBar.LineProgressValueFormat", (int)Math.Round(progress * mode.ImageHeight), mode.ImageHeight)
        : _localization.GetString("MainWindow.StatusBar.LineProgressValueNoLock");

    /// <summary>Mode card's "Remaining" row -- lines and time left in the current decode.
    /// <c>linesRemaining</c> (image rows) is derived from the same <see cref="Progress"/> x
    /// <c>DetectedMode.ImageHeight</c> source as <see cref="LineProgressText"/>, not a separate
    /// tracked quantity. The time figure is NOT <c>linesRemaining * mode.LineDurationMs</c> --
    /// <c>LineDurationMs</c> is per TRANSMISSION line, not per image row, and
    /// <see cref="ColorEncoding.YCbCrLinePaired"/>/<see cref="ColorEncoding.MonoAveragedPaired"/>
    /// modes transmit one line per 2 image rows (same double-counting bug class
    /// <see cref="TxControlsPaneViewModel.GetFrameSeconds"/> was fixed for on the TX side, found
    /// during that fix's own code-review -- PD90-family modes were showing ~2x the real remaining
    /// time here). Correct instead: total decode time is proportional to <see cref="Progress"/>
    /// regardless of family (each transmission-line-duration step advances both the row count and
    /// the elapsed-time fraction by the same proportional amount, since exactly 2 rows land per
    /// step for paired families), so <c>(1 - progress) * GetFrameSeconds(mode)</c> is exact without
    /// needing to separately reconstruct the transmission-line count here.</summary>
    public string RemainingText
    {
        get
        {
            if (Progress is not { } progress || DetectedMode is not { } mode)
            {
                return "—";
            }

            var linesRemaining = mode.ImageHeight - (int)Math.Round(progress * mode.ImageHeight);
            var secondsRemaining = (1.0 - progress) * TxControlsPaneViewModel.GetFrameSeconds(mode);
            return _localization.GetString("Panes.RxImage.RemainingValueFormat", linesRemaining, secondsRemaining);
        }
    }

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
    /// value string" convention (matching its sibling readouts, e.g. buffer/frames-today) needs a
    /// "slant" prefix the Sync&amp;Slant card's own separate label `TextBlock` doesn't.</summary>
    public string SlantPpmStatusBarDisplay => SlantPpm is { } ppm
        ? _localization.GetString("MainWindow.StatusBar.SlantValueFormat", ppm)
        : _localization.GetString("MainWindow.StatusBar.SlantValueNoLock");

    /// <summary>Legacy's <c>m_AutoStopPos</c> (<c>Main.cpp:3887</c>), which legacy itself never
    /// displays -- shown here in raw samples, not an invented "pixels" conversion (legacy has no
    /// on-screen px readout for this quantity to port).</summary>
    public string SyncOffsetSamplesDisplay => SyncOffsetSamples is { } offset ? _localization.GetString("Panes.RxSync.OffsetSamplesFormat", offset) : "—";

    /// <summary>Whether legacy's real <c>AutoSlant</c> setting (<c>ISstvSessionService.AutoSlantEnabled</c>)
    /// is on -- genuinely live now (2026-08-27, restart-required-settings backlog item 1), refreshed
    /// on the Options window's own Closed event, unlike <see cref="RxBpfPreset"/> below, which is
    /// refreshed on a decoder swap instead (see that property's own doc comment for why). Backs the
    /// Sync &amp; Slant card's "Auto-correct" status text (<see cref="AutoCorrectDisplay"/>) --
    /// <see cref="RefreshAutoSlantEnabledFromSession"/> re-syncs this from the session after an
    /// Options-driven change (see <c>MainWindow.axaml.cs</c>'s Options-Closed refresh block), same
    /// pattern as <see cref="RefreshSenseLevelFromSession"/> below. Seeded directly from the backing
    /// field in the constructor (NOT the generated property setter), same reasoning as
    /// <see cref="SenseLevel"/>'s own construction-time seed below -- this property has no per-change
    /// side effect today (unlike <see cref="SenseLevel"/>'s live-apply/persist), but seeding the
    /// field directly keeps both settings' construction-time init consistent.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoCorrectDisplay))]
    private bool _autoSlantEnabled;

    /// <summary>Re-syncs <see cref="AutoSlantEnabled"/> from the session -- called from the Options
    /// window's own Closed event (see <c>MainWindow.axaml.cs</c>), same reasoning and shape as
    /// <see cref="RefreshSenseLevelFromSession"/> below: a no-op if unchanged, and the ONLY way this
    /// pane's "Auto-correct" status text can go stale after Options makes AutoSlantEnabled live is if
    /// this call is missing from that refresh block.</summary>
    public void RefreshAutoSlantEnabledFromSession() => AutoSlantEnabled = _sstvSession.AutoSlantEnabled;

    /// <summary>Whether legacy's real <c>m_afc</c> is on -- the DSP card's AFC toggle. Live and
    /// mid-image, matching legacy: see <see cref="ISstvSessionService.RequestAfcEnabled"/> and
    /// <c>ISstvDecoder.AfcEnabled</c> for the citations. Seeded directly from the backing field in
    /// the constructor, same reasoning as <see cref="AutoSlantEnabled"/> above.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AfcToggleLabel))]
    private bool _afcEnabled;

    /// <summary>The toggle chip's own caption. Bound rather than a static loc-key literal, for the
    /// same reason the Notch chip binds its label: a static one reads "On" even when the toggle is
    /// off.</summary>
    public string AfcToggleLabel => _localization.GetString(AfcEnabled ? "Panes.RxDsp.AfcToggleOn" : "Panes.RxDsp.AfcToggleOff");

    /// <summary>Fires on every <see cref="AfcEnabled"/> PROPERTY assignment, so a real toggle click
    /// or <see cref="RefreshAfcEnabledFromSession"/> reaches it, but the constructor's own direct
    /// field write does not. Same live-apply-then-chained-persist shape as
    /// <see cref="OnSenseLevelChanged"/> below.</summary>
    partial void OnAfcEnabledChanged(bool value)
    {
        _sstvSession.RequestAfcEnabled(value);
        _pendingAfcPersist = PersistAfcEnabledAsync(value, _pendingAfcPersist);
    }

    /// <summary>Same ordering-safety and never-fault shape as <see cref="PersistSenseLevelAsync"/>
    /// below -- see that method's own doc comment for why the whole body, including
    /// <c>await previous</c>, sits inside the try.</summary>
    private async Task PersistAfcEnabledAsync(bool value, Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _sstvSession.PersistAfcEnabledAsync(value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistAfcEnabledFailed(_logger, ex);
        }
    }

    /// <summary>Re-syncs <see cref="AfcEnabled"/> from the session after a configuration-preset swap
    /// (see <c>MainWindow.axaml.cs</c>'s <c>RefreshAfterConfigurationChange</c>). Without this the
    /// DSP card keeps showing the old state while the decoder already changed. A no-op if unchanged.
    /// Not called from the Options-Closed hook: Options has no AFC control.</summary>
    public void RefreshAfcEnabledFromSession() => AfcEnabled = _sstvSession.AfcEnabled;

    /// <summary>Sync &amp; Slant card's "Squelch level" row (renamed from "VIS threshold"
    /// 2026-08-27 to match the Options window's own naming for this same setting) -- genuinely
    /// live and user-editable now (a ComboBox bound to <see cref="SenseLevelOptions"/>), refreshed
    /// on the Options window's own Closed event, unlike <see cref="RxBpfPreset"/> below (see that
    /// property's own doc comment for why it uses a decoder-swap refresh instead). Seeded
    /// directly from the backing field in the constructor (NOT the generated property setter -- see
    /// the constructor's own comment) so construction itself never fires a spurious
    /// live-apply/persist round-trip. See <see cref="OnSenseLevelChanged"/> for what a real
    /// user-driven change actually does.</summary>
    [ObservableProperty]
    private int _senseLevel;

    /// <summary>The last value actually forwarded to <see cref="ISstvSessionService.RequestSenseLevel"/>
    /// and persisted -- <see cref="OnSenseLevelChanged"/> reverts to this whenever an out-of-range
    /// value reaches <see cref="SenseLevel"/> (e.g. a ComboBox's <c>SelectedIndex</c> briefly going
    /// <c>-1</c> when its selection is cleared), rather than clamping-and-forwarding it like the
    /// decoder's own defensive clamp does. Clamping here would silently drop the user's real squelch
    /// setting to "Very low" and PERSIST that -- the decoder-level clamp is a backstop for
    /// genuinely-malformed input (e.g. a hand-edited settings.json), never meant to be reachable via
    /// this UI, and must not be relied on to prevent that.</summary>
    private int _lastValidSenseLevel;

    /// <summary>Sync &amp; Slant card's "Squelch level" dropdown's own item labels, in the exact
    /// index order <c>AnalogFmSstvDecoder.SenseLevelPresets</c> uses (0=Very low..3=Very high) --
    /// reuses the Options window's own real "Options.Decode.SenseLevel.*" locale keys for the same
    /// underlying setting, rather than a second duplicate copy of the same 4 strings under a
    /// Panes.* key. Built once at construction; the 4 options themselves never change.</summary>
    public IReadOnlyList<string> SenseLevelOptions { get; }

    /// <summary>Fires on every <see cref="SenseLevel"/> PROPERTY assignment -- NOT the constructor's
    /// own direct field write above (a real ComboBox selection, or <see cref="RefreshSenseLevelFromSession"/>
    /// below, are the only things that reach this). Guards against an out-of-range value (see
    /// <see cref="_lastValidSenseLevel"/>'s
    /// own doc comment) by reverting without forwarding/persisting anywhere -- this re-enters
    /// <see cref="OnSenseLevelChanged"/> once more with the reverted (valid) value, causing one
    /// harmless redundant re-apply/re-persist of the SAME value already in effect (safe: see
    /// <c>AnalogFmSstvDecoder.ApplyPendingSenseLevelRequest</c>'s own doc comment for why a bare
    /// re-application of an unchanged value is a legacy-matching no-op, not a special case to
    /// avoid).</summary>
    partial void OnSenseLevelChanged(int value)
    {
        if (value is < 0 or > 3)
        {
            SenseLevel = _lastValidSenseLevel;
            return;
        }

        _lastValidSenseLevel = value;
        _sstvSession.RequestSenseLevel(value);
        _pendingSenseLevelPersist = PersistSenseLevelAsync(value, _pendingSenseLevelPersist);
    }

    /// <summary>Same ordering-safety shape as <see cref="_pendingFlagPersist"/> above -- chained
    /// onto whatever's currently pending rather than fired independently, so rapid arrow-key/scroll
    /// changes on the ComboBox can't complete their settings-file writes out of order.</summary>
    private async Task PersistSenseLevelAsync(int value, Task previous)
    {
        // Entire body inside one try/catch, including `await previous` -- same reasoning as
        // PersistFlaggedAsync's own doc comment: a fault must not propagate into
        // _pendingSenseLevelPersist and permanently break every LATER change's own `await previous`.
        // Delegates the actual settings-file read-modify-write to _sstvSession (Application layer),
        // not ISettingsStore/SstvDecoderSettings directly -- this class (ScanlineStudio.UI) is never
        // allowed to reference a ScanlineStudio.Core.* project directly (UiLayeringArchitectureTests),
        // and SstvDecoderSettings lives in ScanlineStudio.Core.Sstv. See
        // ISstvSessionService.PersistSenseLevelAsync's own doc comment for the full reasoning.
        try
        {
            await previous.ConfigureAwait(false);
            await _sstvSession.PersistSenseLevelAsync(value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistSenseLevelFailed(_logger, ex);
        }
    }

    /// <summary>Options window's own "Squelch level" control also applies live now (see
    /// <c>OptionsWindowViewModel</c>'s save flow) -- called from this pane's own Options-Closed
    /// refresh hook (<c>MainWindow.axaml.cs</c>, same convention as every other refresh-on-close call
    /// in that block) so this dropdown reflects a change made in Options instead of showing a stale
    /// selection until the next app restart. A no-op if unchanged: the generated property setter's
    /// own equality check skips <see cref="OnSenseLevelChanged"/> entirely when the value didn't
    /// actually change, so this never fires a redundant persist/live-apply when Options was
    /// Cancelled, or Saved without touching Squelch level.</summary>
    public void RefreshSenseLevelFromSession() => SenseLevel = _sstvSession.SenseLevel;

    /// <summary>Genuinely live now (2026-08-27, restart-required-settings backlog item 2) -- see
    /// <see cref="ISstvSessionService.RxBpfPreset"/> for the full idle-gated contract.
    /// <see cref="RefreshRxBpfPresetFromSession"/> re-syncs this after a swap (NOT after the Options
    /// window's own Closed event, unlike <see cref="AutoSlantEnabled"/>/<see cref="SenseLevel"/>
    /// above -- see that method's own doc comment for why). Seeded directly from the backing field
    /// in the constructor (NOT the generated property setter), same reasoning as
    /// <see cref="AutoSlantEnabled"/>'s own construction-time seed above.
    ///
    /// Genuinely user-EDITABLE from the Receive tab too as of 2026-08-28 (restart-required-settings
    /// backlog item 6) -- see <see cref="OnRxBpfPresetChanged"/> for what a real ComboBox selection
    /// does. Still the single source of truth for both <see cref="RxBpfDisplay"/> and the new
    /// <see cref="RxBpfPresetIndex"/> int-proxy the ComboBox actually binds to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RxBpfDisplay))]
    [NotifyPropertyChangedFor(nameof(RxBpfPresetIndex))]
    private RxBpfPreset _rxBpfPreset;

    /// <summary>Bridges <see cref="RxBpfPreset"/> (enum) to the Input Chain card's ComboBox, whose
    /// <c>SelectedIndex</c> is an <see langword="int"/> -- restart-required-settings backlog item 6
    /// (2026-08-28). NOT an <c>[ObservableProperty]</c>: needs a custom setter, since an out-of-range
    /// value must never reach <see cref="RxBpfPreset"/> at all (unlike <see cref="SenseLevel"/>'s own
    /// <see cref="_lastValidSenseLevel"/> guard, which reverts THROUGH its own canonical property --
    /// there is no valid enum value to revert through here, so this guard just re-raises
    /// <see cref="PropertyChanged"/> for this property alone, snapping the view back to whatever
    /// <see cref="RxBpfPreset"/> already holds, without ever calling <see cref="OnRxBpfPresetChanged"/>).
    /// The -1 case is the same reachable edge case <see cref="_lastValidSenseLevel"/>'s own doc
    /// comment describes: a ComboBox's <c>SelectedIndex</c> can transiently go -1 when its selection
    /// is cleared.</summary>
    public int RxBpfPresetIndex
    {
        get => (int)RxBpfPreset;
        set
        {
            if (value is < 0 or > 3)
            {
                OnPropertyChanged(nameof(RxBpfPresetIndex));
                return;
            }

            RxBpfPreset = (RxBpfPreset)value;
        }
    }

    /// <summary>Input Chain card's "BPF" dropdown's own item labels, in <see cref="RxBpfPreset"/>'s
    /// exact enum order (Off=0..VeryNarrow=3) -- same shape as <see cref="SenseLevelOptions"/> above,
    /// reusing the Options window's own real "Options.Decode.RxBpf.*" locale keys for the same
    /// underlying setting. Built once at construction; the 4 options themselves never change.</summary>
    public IReadOnlyList<string> RxBpfPresetOptions { get; }

    /// <summary>Same ordering-safety shape as <see cref="_pendingSenseLevelPersist"/> above.</summary>
    private Task _pendingRxBpfPresetPersist = Task.CompletedTask;

    /// <summary>Fires on every <see cref="RxBpfPreset"/> PROPERTY assignment -- a real ComboBox
    /// selection (via <see cref="RxBpfPresetIndex"/>'s setter), OR <see cref="RefreshRxBpfPresetFromSession"/>
    /// below. Restart-required-settings backlog item 6 (2026-08-28) -- same live-request/persist
    /// shape as <see cref="OnSenseLevelChanged"/> above, but forwards to
    /// <see cref="ISstvSessionService.RequestRxBpfPreset"/>, NOT <see cref="ISstvSessionService.RequestReconfiguration"/>
    /// with some "current" DemodType/RxBufferMode -- see that method's own doc comment for why (it
    /// would silently clobber, or be clobbered by, a separately-queued Options change to either of
    /// those two fields).
    ///
    /// Can re-fire redundantly, harmlessly, from <see cref="RefreshRxBpfPresetFromSession"/> when an
    /// OPTIONS-originated BPF change lands (this field was stale, the refresh assigns a genuinely
    /// different value) -- NOT when THIS row originated the change (the field already matches, the
    /// generated setter's equality check skips this method entirely). The redundant call is a genuine
    /// no-op against <c>RestartableSstvDecoder</c>'s own committed field, and the redundant persist
    /// write is the same accepted cost <see cref="OnSenseLevelChanged"/>'s own doc comment already
    /// accepts for its own revert case. A periodic maintenance-only restart preserves the current
    /// preset, so this never fires for those (no value change).</summary>
    partial void OnRxBpfPresetChanged(RxBpfPreset value)
    {
        _sstvSession.RequestRxBpfPreset(value);
        _pendingRxBpfPresetPersist = PersistRxBpfPresetAsync(value, _pendingRxBpfPresetPersist);
    }

    /// <summary>Same ordering-safety/error-handling shape as <see cref="PersistSenseLevelAsync"/>
    /// above -- see that method's own doc comment for the full reasoning.</summary>
    private async Task PersistRxBpfPresetAsync(RxBpfPreset value, Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _sstvSession.PersistRxBpfPresetAsync(value).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistRxBpfPresetFailed(_logger, ex);
        }
    }

    /// <summary>Re-syncs <see cref="RxBpfPreset"/> from the session -- called from
    /// <see cref="OnDecoderInstanceReplaced"/>, NOT the Options window's own Closed event that
    /// <see cref="RefreshAutoSlantEnabledFromSession"/>/<see cref="RefreshSenseLevelFromSession"/> use.
    /// Round-2/round-3 plan-review: a pull-based Options-Closed refresh would read the exact same
    /// value a push-based one does here (both ultimately read <c>ISstvSessionService.RxBpfPreset</c>,
    /// which itself only changes once a swap actually happens) -- so keeping both would be pure
    /// redundancy, not extra safety. This is the ONLY way this pane's Input Chain "BPF" row can go
    /// stale after an Options-driven change: a change queued via <c>RequestReconfiguration</c> doesn't
    /// take visible effect until the decoder's next idle swap fires this refresh.</summary>
    private void OnDecoderInstanceReplaced() => Dispatcher.UIThread.Post(RefreshRxBpfPresetFromSession);

    /// <summary>Re-syncs <see cref="RxBpfPreset"/> back to whatever's actually applied when a queued
    /// reconfiguration is rejected (restart-required-settings backlog item 6, 2026-08-28) -- without
    /// this, a rejected Receive-tab-originated change would leave the row permanently showing a
    /// preset that was requested but never took effect (already persisted to disk, too). Fires for
    /// ANY rejection, not just a BPF-driven one (see <c>ISstvSessionService.ReconfigurationRejected</c>'s
    /// own doc comment for why) -- harmless either way, since this just re-reads current state.</summary>
    private void OnReconfigurationRejected() => Dispatcher.UIThread.Post(RefreshRxBpfPresetFromSession);

    public void RefreshRxBpfPresetFromSession() => RxBpfPreset = _sstvSession.RxBpfPreset;

    /// <summary>Was the Input Chain card's "BPF" row's own display text before that row became a
    /// live ComboBox (restart-required-settings backlog item 6, 2026-08-28, bound to
    /// <see cref="RxBpfPresetIndex"/>/<see cref="RxBpfPresetOptions"/> instead). Deliberately KEPT,
    /// not removed -- no view references it any more, but it's still covered by an existing test and
    /// costs nothing to keep; removing a tested public property is out of scope for that change. The
    /// preset NAME only, not a cutoff figure (see <see cref="ISstvDecoder.RxBpfPreset"/>'s own doc
    /// comment for why a cutoff would be wrong whenever unlocked). Reuses the Options window's own
    /// real "Options.Decode.RxBpf.*" locale keys for the same underlying setting (Off is labeled
    /// "Normal" there, Narrow/VeryNarrow are "Sharp"/"Very sharp" -- matching that existing
    /// user-facing naming, not the enum's own).</summary>
    public string RxBpfDisplay => RxBpfPreset switch
    {
        ScanlineStudio.Abstractions.Sstv.RxBpfPreset.Narrow => _localization.GetString("Options.Decode.RxBpf.Sharp"),
        ScanlineStudio.Abstractions.Sstv.RxBpfPreset.VeryNarrow => _localization.GetString("Options.Decode.RxBpf.VerySharp"),
        ScanlineStudio.Abstractions.Sstv.RxBpfPreset.Off => _localization.GetString("Options.Decode.RxBpf.Normal"),
        _ => _localization.GetString("Options.Decode.RxBpf.Wide"),
    };

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

    [RelayCommand]
    private void RequestCorrectSlant() => _sstvSession.RequestCorrectSlant();

    /// <summary>Backs the Receive tab's own Abort button. Mirrors <see cref="QuickSelectMode"/>'s
    /// own body-level <see cref="ISstvSessionService.IsReceiving"/> check rather than a
    /// view-layer <c>CanExecute</c> binding, for the same reason: this pane doesn't track
    /// <see cref="ISstvSessionService.IsReceiving"/> reactively.</summary>
    [RelayCommand]
    private void Abort()
    {
        if (!_sstvSession.IsReceiving)
        {
            return;
        }

        Log.AbortInvoked(_logger);
        _sstvSession.AbortReception();
    }

    /// <summary>Backs the quick-mode-button grid (spec/18-path-to-1.0.md High item 7) --
    /// <see cref="ISstvSessionService.ForceMode"/> is a one-shot "start decoding as this mode
    /// right now" kick (see its own doc comment), not a persistent lock, so this is safe to fire
    /// repeatedly. Gated on <see cref="ISstvSessionService.IsReceiving"/>: <c>ForceMode</c>'s
    /// request is otherwise deferred until whatever <c>PushSamples</c> call happens next, which
    /// while not receiving could be an arbitrarily-later, surprising moment -- a body-level check,
    /// not a view-layer <c>IsEnabled</c>/<c>CanExecute</c> binding, since this pane doesn't track
    /// <see cref="ISstvSessionService.IsReceiving"/> reactively (only <c>RadioStatusViewModel</c>
    /// does). A <paramref name="modeId"/> with no matching entry in
    /// <see cref="ISstvSessionService.AvailableModes"/> logs a warning and no-ops (defensive
    /// against a mistyped XAML <c>CommandParameter</c>, matching this file's existing style).
    ///
    /// <b>Code-review-noted divergence from legacy</b>: `Main.cpp:6114`'s real `SBMClick` gate
    /// (`!pDem-&gt;m_Sync || (SSTVSET.m_Mode != m_ModeAssignRX[m_ExtMode])`) skips the restart
    /// entirely when the clicked mode is ALREADY the one actively decoding -- this port's own
    /// call is unconditional, so re-clicking the mode already in progress abandons and restarts
    /// the current image rather than being a no-op. Not fixed: <see cref="DetectedMode"/> is
    /// deliberately never nulled at end-of-reception (its only assignment is
    /// <c>OnModeDetected</c>; nothing nulls it), so a naive `DetectedMode?.Id == modeId` check
    /// would also wrongly block a legitimate re-force after a PREVIOUS reception ended, and
    /// <see cref="ISstvSessionService"/> exposes no "currently synced" flag distinct from
    /// <see cref="ISstvSessionService.IsReceiving"/> to disambiguate the two cases correctly. A
    /// real, accepted UX rough edge -- LIMITED data loss, not none:
    /// <c>ReceiveHistoryRecorder</c>'s own <see cref="ISstvSessionService.DecodeRestarted"/>
    /// handler only saves the abandoned partial when it was already &gt;=65% complete
    /// (<c>ReceiveHistoryRecorder.cs</c>'s own completeness threshold); an early re-click
    /// discards it. Not silently missed, just not fixed here.</summary>
    [RelayCommand]
    private void QuickSelectMode(string modeId)
    {
        if (!_sstvSession.IsReceiving)
        {
            return;
        }

        var mode = _sstvSession.AvailableModes.FirstOrDefault(m => m.Id == modeId);
        if (mode is null)
        {
            Log.QuickSelectModeUnknownId(_logger, modeId);
            return;
        }

        Log.QuickSelectModeInvoked(_logger, modeId);
        // Forcing a mode always resumes auto-detect, matching legacy's Start()/Start(mode,f) sharing
        // SBAuto's own variable -- the underlying decoder-side ordering is already safe regardless
        // (RequestAbandonReception always drains before ForceMode, see that method's own doc
        // comment), this is purely so the UI toggle visibly flips back to "Listening" too. Setting
        // the property (not calling the session directly) so IsAutoDetectPaused's own bound segment
        // updates.
        IsAutoDetectPaused = false;
        _sstvSession.ForceMode(mode);
    }

    /// <summary>ui_transition_plan.md step 10 (T2-5): persistent RX mode lock -- genuinely NEW
    /// UI/workflow, no legacy precedent (see <see cref="ISstvSessionService.SetModeLock"/>'s own doc
    /// comment for the full design and the 2 rounds of plan-review it went through). Deliberately
    /// worded "Hold"/"Holding"/"Release" throughout, not "Lock"/"Locked" -- that word is already
    /// used for this SAME tab's sync-lock state (<see cref="SyncSourceDisplay"/>'s own
    /// "Locked"/"Searching" values), and legacy's own "Lock" (SBLK) is a different feature entirely
    /// (<c>SyncRestartEnabled</c>). <see langword="null"/> means nothing is held -- ordinary VIS
    /// auto-detection, unaffected by this feature at all.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeldModeDisplay))]
    [NotifyCanExecuteChangedFor(nameof(ReleaseHeldModeCommand))]
    private SstvModeDefinition? _heldMode;

    /// <summary>Status text for the quick-mode grid header -- <see langword="null"/> (hidden) when
    /// nothing is held.</summary>
    public string? HeldModeDisplay => HeldMode is { } mode
        ? _localization.GetString("Panes.RxImage.QuickMode.HoldingValue", QuickModeShortNames.GetShortName(mode))
        : null;

    /// <summary>Bound to each quick-mode slot's own "Hold this mode" context-menu entry
    /// (<see cref="QuickModeSlotViewModel.HoldModeCommand"/>) -- explicit target, not "whatever was
    /// last quick-selected" or "whatever is currently displayed as detected" (both real, cheaper
    /// options a plan-review round rejected: the first is undefined while not receiving, since
    /// <see cref="QuickSelectMode"/> early-returns in that state; the second,
    /// <see cref="DetectedMode"/>, is never nulled at end-of-reception and would silently hold a
    /// stale mode). Independent of <see cref="ISstvSessionService.ForceMode"/>: holding a mode does
    /// not force the current reception into it -- it takes effect starting with the NEXT detected
    /// reception (see <see cref="ISstvSessionService.SetModeLock"/>'s own timing contract).</summary>
    [RelayCommand(CanExecute = nameof(CanHoldMode))]
    private void HoldMode(SstvModeDefinition mode)
    {
        Log.HoldModeInvoked(_logger, mode.Id);
        HeldMode = mode;
        _sstvSession.SetModeLock(mode);
    }

    /// <summary>Code-review finding: <see cref="ISstvSessionService.SetModeLock"/>'s own doc comment
    /// documents AVT as unsupported ("locking to or from AVT is not supported" -- a detected AVT
    /// signal always trains/commits as AVT regardless, and substituting an AVT signal's own commit
    /// TO AVT would take <c>Commit</c>'s AVT branch for a non-AVT-shaped signal, skipping anchor
    /// correction/AFC/slant entirely), but nothing enforced it -- every one of the 43 reassignment
    /// entries, AVT included, was reachable via "Hold this mode" with no gate at all. Discriminated
    /// via <see cref="ISstvSessionService.GetVisHeaderInfo"/>, not a direct
    /// <c>ScanlineStudio.Core.Sstv.SstvModeRegistry</c> reference (UI must not reference
    /// <c>ScanlineStudio.Core.Sstv</c> directly -- same layering rule
    /// <see cref="TxControlsPaneViewModel.VisHeaderText"/> already follows for this exact call).</summary>
    private bool CanHoldMode(SstvModeDefinition mode) => _sstvSession.GetVisHeaderInfo(mode).Kind != VisHeaderKind.Avt;

    private bool CanReleaseHeldMode() => HeldMode is not null;

    [RelayCommand(CanExecute = nameof(CanReleaseHeldMode))]
    private void ReleaseHeldMode()
    {
        Log.ReleaseHeldModeInvoked(_logger);
        HeldMode = null;
        _sstvSession.SetModeLock(null);
    }

    /// <summary>The 16-slot quick-mode grid -- one entry per grid button, independent of the TX
    /// pane's own grid by design (confirmed with the user: reassigning here never touches
    /// <c>TxControlsPaneViewModel.QuickModeSlots</c>). Built synchronously from
    /// <see cref="QuickModeGridDefaults"/> in the constructor (never empty, even before
    /// <see cref="LoadQuickModeGridAsync"/> resolves), then mutated in place -- never
    /// cleared/repopulated -- once the persisted assignment loads or a slot is reassigned.</summary>
    public ObservableCollection<QuickModeSlotViewModel> QuickModeSlots { get; } = [];

    private void BuildQuickModeSlots(IReadOnlyList<string> ids)
    {
        var resolved = QuickModeGridAssignment.Resolve(ids, _sstvSession.AvailableModes);
        for (var i = 0; i < resolved.Count; i++)
        {
            var slot = new QuickModeSlotViewModel(i, resolved[i], QuickSelectModeCommand, HoldModeCommand);
            foreach (var mode in _sstvSession.AvailableModes)
            {
                slot.MenuEntries.Add(new QuickModeMenuEntryViewModel(mode, ReassignQuickModeSlotCommand, i));
            }

            QuickModeSlots.Add(slot);
        }

        RecomputeQuickModeMenuEntryStates();
    }

    /// <summary>Refreshes every slot's <see cref="QuickModeMenuEntryViewModel.IsChecked"/>/
    /// <see cref="QuickModeMenuEntryViewModel.IsEnabled"/> in place -- called after construction, a
    /// successful load, and every reassignment, since one slot's assignment changes what's
    /// checked/available in every OTHER slot's popup too (a mode may only occupy one slot at a
    /// time).</summary>
    private void RecomputeQuickModeMenuEntryStates()
    {
        foreach (var slot in QuickModeSlots)
        {
            foreach (var entry in slot.MenuEntries)
            {
                entry.IsChecked = entry.Mode.Id == slot.CurrentMode.Id;
                entry.IsEnabled = entry.Mode.Id == slot.CurrentMode.Id
                    || QuickModeSlots.All(other => other.SlotIndex == slot.SlotIndex || other.CurrentMode.Id != entry.Mode.Id);
            }
        }
    }

    private async Task LoadQuickModeGridAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var rxPaneUi = settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings) ?? new RxPaneUiSettings();

            var resolved = QuickModeGridAssignment.Resolve(rxPaneUi.QuickModeGridIds, _sstvSession.AvailableModes);
            for (var i = 0; i < resolved.Count; i++)
            {
                QuickModeSlots[i].CurrentMode = resolved[i];
            }

            RecomputeQuickModeMenuEntryStates();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadCaptureDeviceNameAsync/LoadOperatorGridAsync above
            // -- a failure here leaves the grid at its already-built default assignment rather than
            // blocking construction.
            Log.LoadQuickModeGridFailed(_logger, ex);
        }
    }

    /// <summary>Read-modify-write against whatever is currently persisted for this section, same
    /// reasoning as <c>TxControlsPaneViewModel.PersistTxPaneUiSettingsAsync</c>'s own doc
    /// comment.</summary>
    private async Task PersistQuickModeGridAsync()
    {
        try
        {
            // T0-2: QuickModeSlots is live VM state -- must be read here, before UpdateAsync, never
            // inside its mutate lambda (which may run on any thread, including this one synchronously,
            // but must not itself read UI-thread-affine state per ISettingsStore.UpdateAsync's own
            // contract).
            var quickModeGridIds = QuickModeSlots.Select(s => s.CurrentMode.Id).ToArray();
            await _settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings) ?? new RxPaneUiSettings();
                var updated = current with { QuickModeGridIds = quickModeGridIds };
                return settings.WithSection(RxPaneUiSettings.SectionKey, updated, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
            });
        }
        catch (Exception ex)
        {
            Log.PersistQuickModeGridFailed(_logger, ex);
        }
    }

    /// <summary>Backs the quick-mode grid's right-click popup -- reassigns which mode a slot targets
    /// going forward. Deliberately does NOT also apply/select <paramref name="reassignment"/>'s mode
    /// (no <see cref="ISstvSessionService.ForceMode"/> call): this is a passive relabel, matching the
    /// user's own framing ("from that point on the fast-select button is for that mode"), not a live
    /// mode change -- see the feature's plan-review for the full reasoning on why legacy's own
    /// active-apply behavior isn't ported here. Awaits <see cref="_loadQuickModeGridTask"/> FIRST
    /// (round-2 plan-review fix, see that field's own doc comment for why this await alone is
    /// sufficient): reassigning before the persisted grid has loaded must not race that load, which
    /// would otherwise either revert this reassignment or have the load's unrelated persist
    /// silently overwrite the user's own previously-saved OTHER slots.</summary>
    [RelayCommand]
    private async Task ReassignQuickModeSlotAsync(QuickModeReassignment reassignment)
    {
        await _loadQuickModeGridTask;

        if (reassignment.SlotIndex < 0 || reassignment.SlotIndex >= QuickModeSlots.Count)
        {
            Log.ReassignQuickModeSlotOutOfRange(_logger, reassignment.SlotIndex);
            return;
        }

        // Defensive re-check, same reasoning as QuickSelectMode's unknown-id guard above -- the
        // popup already disables any mode claimed by a DIFFERENT slot, so this should be
        // unreachable via the UI, but a direct/raced call must not be trusted to honor that.
        var alreadyUsedElsewhere = QuickModeSlots.Any(s => s.SlotIndex != reassignment.SlotIndex && s.CurrentMode.Id == reassignment.Mode.Id);
        if (alreadyUsedElsewhere)
        {
            Log.ReassignQuickModeSlotAlreadyUsedElsewhere(_logger, reassignment.SlotIndex, reassignment.Mode.Id);
            return;
        }

        Log.ReassignQuickModeSlotInvoked(_logger, reassignment.SlotIndex, reassignment.Mode.Id);
        QuickModeSlots[reassignment.SlotIndex].CurrentMode = reassignment.Mode;
        RecomputeQuickModeMenuEntryStates();
        _ = PersistQuickModeGridAsync();
    }

    /// <summary>Normally invoked only by <see cref="_telemetryTimer"/>'s own tick -- public so tests
    /// can poll deterministically instead of waiting on a real <see cref="DispatcherTimer"/>
    /// interval.</summary>
    public void PollTelemetry()
    {
        SlantPpm = _sstvSession.SlantPpm;
        SyncSource = _sstvSession.SyncSource;
        SyncOffsetSamples = _sstvSession.SyncOffsetSamples;
        SyncFrequencyCorrectionHz = _sstvSession.SyncFrequencyCorrectionHz;
        IsLevelOverdriven = _sstvSession.IsLevelOverdriven;
        BufferedSampleCount = _sstvSession.BufferedSampleCount;
        SignalPeakLevel = _sstvSession.SignalPeakLevel;
        CaptureOverrunCount = _sstvSession.CaptureOverrunCount;
        IsAudioAutoSaveActive = _sstvSession.IsAudioAutoSaveActive;
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
        // Same race/staleness reasoning as LineProgressText above -- RemainingText also depends on
        // DetectedMode.ImageHeight/LineDurationMs, not just Progress.
        OnPropertyChanged(nameof(RemainingText));
    }

    partial void OnIsAutoDetectPausedChanged(bool value)
    {
        Log.AutoDetectPausedChanged(_logger, value);
        _sstvSession.SetAutoDetectPaused(value);
    }

    private void OnModeDetected(SstvModeDefinition mode)
    {
        // fsk_cwid.md §A5: read-in-callback, synchronously on the decode thread -- ISstvSessionService
        // .CurrentReceptionSequence's own doc comment requires this, not a lazy read from inside the
        // Post below, which could observe a THIRD reception's value under back-to-back ModeDetected
        // events.
        var receptionSequence = _sstvSession.CurrentReceptionSequence;
        Log.ModeDetected(_logger, mode.Id);
        Dispatcher.UIThread.Post(() =>
        {
            _currentReceptionSequence = receptionSequence;
            DetectedMode = mode;
            StartedAt = DateTimeOffset.UtcNow;
            FileSizeBytes = null;
            // Same "per-RECEPTION state, not per-session" category as OverrideCallsign/Lookup*
            // below -- a fresh decode has no history row yet (OnHistoryRecorded hasn't fired for
            // it), so any stale entry id / note / flag from the PREVIOUS frame must not leak into
            // this one (accidentally flagging/annotating the wrong saved image).
            _lastSavedPath = null;
            _currentEntryId = null;
            // ui_transition_plan.md step 6 (T2-4): same per-RECEPTION category as the two resets
            // above -- station B's reception must start with no latched frequency/mode showing
            // (station A's, or worse, unset-and-stale) until B's own OnHistoryRecorded fires.
            _latchedFrequencyHz = null;
            _latchedRigMode = null;
            OnPropertyChanged(nameof(LatchedFrequencyDisplay));
            // Tier B audit finding: try/finally, not a bare set-then-reset -- these two property sets
            // raise PropertyChanged into live Avalonia bindings, which can throw; a throw here used to
            // leave _suppressFrameMetadataEdits stuck true for the process lifetime AND skip every
            // statement below it in this lambda, including the OverrideCallsign/Lookup* resets a few
            // lines down -- letting station A's callsign leak into station B's reception and, via
            // LogQsoCommand, into a real logbook row. Same fix
            // RxHistoryPaneViewModel._suppressSelectedEntryEdits already applied for the identical
            // flag shape earlier in this sweep.
            try
            {
                _suppressFrameMetadataEdits = true;
                Note = null;
                IsFlagged = false;
            }
            finally
            {
                _suppressFrameMetadataEdits = false;
            }

            OnPropertyChanged(nameof(CanEditFrameMetadata));
            // Round-1 plan-review finding (Log QSO / rx-log-qso.md): these 4 are per-RECEPTION
            // "who is this station" state, same category as DetectedMode/StartedAt above, but were
            // never reset here -- station A sends an FSK-decoded callsign, station B then
            // transmits with no FSK ID, and DetectedMode/StartedAt update to B's while
            // OverrideCallsign/the QRZ-lookup fields silently stay A's. Previously just cosmetic
            // staleness in the RX pane's own readout; now that LogQsoCommand can persist
            // OverrideCallsign into a real logbook row, a stale value there would produce a wrong
            // QSO record -- the other three (Lookup*) only ever feed this pane's own read-only
            // display plus this method's own prefill copy, so for them it's still display
            // staleness, just now also copied into the prefilled form rather than shown live.
            OverrideCallsign = null;
            LookupName = null;
            LookupQth = null;
            LookupGrid = null;
            // Tier B audit finding: DecodedNrRst is the same per-RECEPTION "who is this station"
            // category as the four fields above (also decoded from station A's FSK sub-packet, see
            // ApplyStationIdDecodedAsync) but was the one left out of this reset. Now bound to a real
            // card row (2026-08-26, see DecodedNrRstDisplay's own doc comment) -- this reset is
            // load-bearing, not just future-proofing.
            DecodedNrRst = null;
            // fsk_cwid.md A3: _nrRstDecodedAtUtc joins this reset -- same per-RECEPTION category,
            // and unlike _cwIdConfidence below, NOT inert if left stale: NrRstLabelDisplay reads it
            // independently of DecodedNrRst's own null-ness (there is no "only shown when non-null"
            // guard on the label the way CwIdDisplay's VALUE has for confidence).
            _nrRstDecodedAtUtc = null;
            OnPropertyChanged(nameof(NrRstLabelDisplay));
            // fsk_cwid.md §9: CwIdText joins this reset list -- same per-RECEPTION "who is this
            // station" category as DecodedNrRst just above. _cwIdConfidence is deliberately left
            // untouched: CwIdDisplay only reads it when CwIdText is non-null, so a stale confidence
            // value behind a null text is inert. _cwIdDecodedAtUtc (fsk_cwid.md A3) is NOT inert the
            // same way (same reasoning as _nrRstDecodedAtUtc just above) and is reset explicitly.
            CwIdText = null;
            _cwIdDecodedAtUtc = null;
            OnPropertyChanged(nameof(CwIdLabelDisplay));
            // Tier B audit finding: these three are per-RECEPTION error/status text, same category as
            // Note/IsFlagged above, but weren't cleared here -- a QRZ lookup failure or a stale
            // "entry no longer exists" from the PREVIOUS frame kept showing on the RxFrameMeta card
            // after a new reception started and blanked the fields the error text was actually about.
            // Same shape RxHistoryPaneViewModel_ExportFrameAsync_SwitchingSelectionAfterward_
            // ClearsStaleExportStatusMessage already fixed for the sibling pane earlier in this sweep.
            QrzLookupErrorMessage = null;
            FrameMetadataErrorMessage = null;
            SaveFrameErrorMessage = null;
        });
    }

    /// <summary>Logging-only subscriber (logging-coverage audit, 2026-08-15) -- picked as the
    /// canonical place to trace <see cref="ISstvSessionService.DecodeRestarted"/> for the same
    /// reason this VM already owns <see cref="OnModeDetected"/>. Deliberately does NOT touch any
    /// bound state here: this closes the "no reachable UI-layer subscriber" debugging gap the audit
    /// found (`ReceiveHistoryRecorder` already has its own, separate Core.Logbook-side subscriber
    /// for the >=65%-complete-abandoned-image case), it does not add new user-visible restart UI
    /// (out of scope for a logging fix). <paramref name="mode"/> is the *abandoned* mode, per the
    /// underlying event's own doc comment -- not the mode about to be detected next.</summary>
    private void OnDecodeRestarted(SstvModeDefinition mode) => Log.DecodeRestarted(_logger, mode.Id);

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
        // fsk_cwid.md A4: logged first, unconditionally -- see AddStationHeard's own doc comment for
        // why this runs before the stale-guard/self-filter checks below.
        AddStationHeard(
            info switch
            {
                { Callsign: { } callsign } => callsign,
                { CompactNr: { } compactNr } => $"595{compactNr.ToString("D3", CultureInfo.InvariantCulture)}",
                { NrText: { } nrText } => $"595{nrText}",
                _ => "—",
            },
            "FSK",
            info.ReceptionSequence);

        // fsk_cwid.md §A5 code-review nit: hoisted here (in ADDITION to the callsign branch's own
        // post-await re-check below, not instead of it -- that one guards a real race THIS check
        // cannot, see its own comment) so the CompactNr/NrText branches -- also per-reception state,
        // OnModeDetected's own reset above -- get the SAME "already stale on arrival" protection the
        // callsign branch has always had. Free: a pure comparison, no capture-before-await needed for
        // branches with no await at all.
        if (info.ReceptionSequence != _currentReceptionSequence)
        {
            Log.StationIdDroppedStale(_logger, info.ReceptionSequence, _currentReceptionSequence);
            return;
        }

        if (info.Callsign is { } decodedCallsign)
        {
            // Tier B audit finding: GetOperatorCallsignAsync's own await genuinely yields the UI
            // thread for a real, uncached settings-file disk read (JsonSettingsStore.LoadAsync has no
            // cache) -- during a bulk-WAV-decode's back-to-back transmissions (the SAME interleaving
            // OnSaved's own doc comment already calls "the likely interleaving, not an edge case"),
            // station B's ModeDetected/OverrideCallsign=null reset can land in this exact window,
            // after which the write below would silently re-apply station A's now-stale callsign onto
            // B's current frame.
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

            // fsk_cwid.md §A5: re-check AFTER the await -- the entry-time check at the top of this
            // method already covers "already stale on arrival"; this second check (same identity
            // comparison, against _currentReceptionSequence, this pane's own latch -- see that
            // field's own doc comment) is the one that actually matters here, since
            // GetOperatorCallsignAsync's own await can let a NEW reception's ModeDetected land in
            // the window between entry and now. Replaces an earlier IReceivedImageBuffer.Generation-diff
            // guard: a direct identity match is the "one source of truth" the plan calls for, and
            // naturally covers a late arrival even if MORE THAN ONE newer reception has started since,
            // not just "did generation change at all."
            if (info.ReceptionSequence != _currentReceptionSequence)
            {
                // A new reception has started while the settings read above was in flight -- this
                // callsign belongs to a frame that's no longer current. Drop it rather than write a
                // stale value onto the new one.
                Log.StationIdDroppedStale(_logger, info.ReceptionSequence, _currentReceptionSequence);
                return;
            }

            // fsk_cwid.md §A5: shared self-filter (Main.cpp:3628's strcmp) -- exact, case-sensitive
            // match against the OPERATOR's own callsign, routed through the ONE comparison every
            // decode source must use (StationIdCallsignNormalizer.IsOwnCallsign's own doc comment) --
            // NOT the same thing as the structurally-impossible "decoded my own live TX" case (RX is
            // paused during TX, see this method's own doc comment); this instead guards against
            // auto-filling "his callsign" with the operator's own when ANOTHER station's transmission
            // happens to reference/repeat it.
            if (StationIdCallsignNormalizer.IsOwnCallsign(decodedCallsign, ownCallsign))
            {
                Log.StationIdDroppedAsOwnCallsign(_logger, info.ReceptionSequence);
                return;
            }

            // Main.cpp:3631's strcmp against the CURRENT HisCall field, before writing, is a dedup
            // check -- no separate check needed here: [ObservableProperty]'s generated setter already
            // no-ops (skips the field write and PropertyChanged/CanExecuteChanged raises) when the
            // new value equals the current one, matching legacy's own behavior for free.
            OverrideCallsign = decodedCallsign;
            // fsk_cwid.md A3: set AFTER the write above -- OnOverrideCallsignChanged (fired
            // synchronously by that setter, on a real change) already cleared these; this re-sets
            // them for the auto-fill case specifically. See _callsignSource's own doc comment.
            _callsignSource = StationIdSources.Fsk;
            _callsignDecodedAtUtc = DateTimeOffset.UtcNow;
            OnPropertyChanged(nameof(CallsignLabelDisplay));
            Log.StationIdDecoded(_logger, decodedCallsign, info.ReceptionSequence);
        }
        else if (info.CompactNr is { } compactNr)
        {
            // Main.cpp:2537-2538/RX decoder's own compact-form contract: the compact NR is a raw
            // value here, rendered back to legacy's real on-air text via the same "%03u"-equivalent
            // zero-pad legacy itself uses before formatting into MyRST (Main.cpp:3648) -- D3 matches
            // %03u exactly for this value's range (always < CompactNrUpperBound=4096, so never more
            // than 4 digits either way, same as %03u's own "pad to minimum width, never truncate"
            // behavior).
            var nrRst = compactNr.ToString("D3", CultureInfo.InvariantCulture);
            ApplyDecodedNrRst(nrRst);
            Log.StationIdNrRstDecoded(_logger, nrRst, info.ReceptionSequence);
        }
        else if (info.NrText is { } nrText)
        {
            ApplyDecodedNrRst(nrText);
            Log.StationIdNrRstDecoded(_logger, nrText, info.ReceptionSequence);
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
        // fsk_cwid.md A3: no clear/revert case here -- see _nrRstDecodedAtUtc's own doc comment.
        _nrRstDecodedAtUtc = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(NrRstLabelDisplay));
    }

    /// <summary>fsk_cwid.md §9/B-P3: same trivial-dispatch shape as <see cref="OnStationIdDecoded"/>
    /// -- <see cref="ISstvSessionService.CwIdDecoded"/>'s own doc comment gives the same "raised from
    /// a background continuation, never assume a UI-thread caller" contract.</summary>
    private void OnCwIdDecoded(CwIdDecodedInfo info)
    {
        Dispatcher.UIThread.Post(() => _ = ApplyCwIdDecodedAsync(info));
    }

    /// <summary>fsk_cwid.md §9's routing rule: the CW ID row itself is filled unconditionally (once
    /// past the stale guard) -- "show CW text in its own row" even when FSK is authoritative and a
    /// disagreement is logged instead of applied. <see cref="OverrideCallsign"/> is filled from
    /// <see cref="CwIdDecodedInfo.Callsign"/> ONLY if it is still empty for this reception -- FSK is
    /// authoritative and is NEVER overwritten by CW (CTC/ML decoders can hallucinate plausible
    /// callsigns, `decoder_quality_improvement.md` §9.5's same warning for ML restoration). Routes
    /// through <see cref="ApplyStationIdDecodedAsync"/>'s own shared self-filter/stale-guard
    /// machinery, not a reimplementation.</summary>
    private async Task ApplyCwIdDecodedAsync(CwIdDecodedInfo info)
    {
        // fsk_cwid.md A4: logged first, unconditionally -- same reasoning as
        // ApplyStationIdDecodedAsync's own AddStationHeard call. One row per CwIdDecodedInfo receipt
        // (not split into a separate row for info.Callsign) -- unlike FSK's callsign packet and
        // NR/RST sub-packet (two genuinely separate wire transmissions), CW-ID's Text and Callsign
        // both come from the SAME single decode event.
        AddStationHeard(info.Text, "CW", info.ReceptionSequence);

        if (info.ReceptionSequence != _currentReceptionSequence)
        {
            Log.CwIdDroppedStale(_logger, info.ReceptionSequence, _currentReceptionSequence);
            return;
        }

        // _cwIdConfidence set BEFORE CwIdText -- CwIdText's own [NotifyPropertyChangedFor(nameof(CwIdDisplay))]
        // is the single notification for both (see _cwIdConfidence's own doc comment); setting it
        // first means CwIdDisplay's getter sees the correct confidence by the time that notification
        // fires.
        _cwIdConfidence = info.Confidence;
        // fsk_cwid.md A3: set before CwIdText for the same reason _cwIdConfidence is -- CwIdText's
        // own notification is the single trigger for both CwIdDisplay and CwIdLabelDisplay.
        _cwIdDecodedAtUtc = DateTimeOffset.UtcNow;
        CwIdText = info.Text;
        OnPropertyChanged(nameof(CwIdLabelDisplay));
        Log.CwIdDecoded(_logger, info.Text, info.Confidence, info.ReceptionSequence);

        if (info.Callsign is not { } decodedCallsign)
        {
            return;
        }

        if (!string.IsNullOrEmpty(OverrideCallsign))
        {
            if (!string.Equals(OverrideCallsign, decodedCallsign, StringComparison.Ordinal))
            {
                Log.CwIdCallsignDisagreesWithFsk(_logger, OverrideCallsign, decodedCallsign, info.ReceptionSequence);
            }

            return;
        }

        string? ownCallsign;
        try
        {
            ownCallsign = await _sstvSession.GetOperatorCallsignAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.GetOperatorCallsignFailed(_logger, ex);
            return;
        }

        // Re-check both conditions after the await, split (not combined) so each drop reason gets
        // its own log line -- auditor code-review nit: the FSK sibling logs every drop reason at
        // Debug, this path silently returned. Same reasoning as ApplyStationIdDecodedAsync's own
        // post-await re-check either way: a new reception's ModeDetected, or an FSK-decoded
        // callsign, can land in the window this settings read was in flight.
        if (info.ReceptionSequence != _currentReceptionSequence)
        {
            Log.CwIdDroppedStale(_logger, info.ReceptionSequence, _currentReceptionSequence);
            return;
        }

        if (!string.IsNullOrEmpty(OverrideCallsign))
        {
            Log.CwIdCallsignSkippedFskAlreadySet(_logger, info.ReceptionSequence);
            return;
        }

        if (StationIdCallsignNormalizer.IsOwnCallsign(decodedCallsign, ownCallsign))
        {
            Log.CwIdDroppedAsOwnCallsign(_logger, info.ReceptionSequence);
            return;
        }

        OverrideCallsign = decodedCallsign;
        // fsk_cwid.md A3: same "set after the write, OnOverrideCallsignChanged already cleared them"
        // shape as ApplyStationIdDecodedAsync's own FSK write -- see _callsignSource's own doc
        // comment. Source is "CW" here, not "FSK".
        _callsignSource = StationIdSources.Cw;
        _callsignDecodedAtUtc = DateTimeOffset.UtcNow;
        OnPropertyChanged(nameof(CallsignLabelDisplay));
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
    /// step. The residual sliver this comment used to describe (a <c>ModeDetected</c> landing in
    /// the recorder's own pre-save setup, invisible to this guard) no longer exists:
    /// <c>ReceiveHistoryRecorder</c>'s completed-image path now captures <c>Generation</c>
    /// synchronously, before that setup runs, and reports it through
    /// <see cref="IReceivedImageBuffer.NotifySaved"/> rather than <see cref="IReceivedImageBuffer.SaveAsync"/>
    /// (a Tier B functional-audit fix -- see that method's own doc comment). The only remaining
    /// <c>SaveAsync</c> caller is this pane's own manual <see cref="SaveFrameAsync"/>, which has no
    /// comparable pre-save setup to leave a window in.</summary>
    private void OnSaved(string path, int generation)
    {
        // Correlation tracking for OnHistoryRecorded below -- deliberately NOT inside the file-size
        // try/catch beneath: a FileInfo read failing (a bug in ITS OWN best-effort reasoning, not
        // this one) must not also suppress path correlation, since the save itself genuinely
        // happened -- ReceiveHistoryRecorder only calls RecordAsync (which is what
        // OnHistoryRecorded reacts to) AFTER this same SaveAsync call already completed
        // successfully. Same generation-staleness guard as the size read below, for the same reason.
        Dispatcher.UIThread.Post(() =>
        {
            if (_receivedImage.Generation != generation)
            {
                return;
            }

            _lastSavedPath = path;
        });

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

    /// <summary>Closes the real, previously-documented gap this pane's Note/Flag controls used to
    /// be disabled for: "no way to learn a just-saved frame's ReceiveHistoryEntry id yet."
    /// <see cref="IReceiveHistoryStore.Recorded"/> is the ONLY hook a live pane has for "a new frame
    /// just landed in history" (that event's own doc comment) -- correlated back to THIS pane's
    /// currently-displayed frame by matching <see cref="ReceiveHistoryEntry.FilePath"/> against
    /// <see cref="_lastSavedPath"/>, set by <see cref="OnSaved"/> just above.
    ///
    /// <b>Ordering reasoning</b>: <c>ReceiveHistoryRecorder.RecordCompletedImageAsync</c> always
    /// calls <c>IReceivedImageBuffer.NotifySaved</c> (which raises <see cref="IReceivedImageBuffer.Saved"/>,
    /// synchronously invoking <see cref="OnSaved"/>) and only THEN, after that call has fully
    /// returned, calls <see cref="IReceiveHistoryStore.RecordAsync"/> (which raises this event) --
    /// so <see cref="OnSaved"/>'s <see cref="Dispatcher.UIThread"/> post is always ENQUEUED before
    /// this method's own post, and the dispatcher runs same-priority posts in FIFO order, so
    /// <see cref="_lastSavedPath"/> is reliably set by the time this runs for the matching save. A
    /// mismatch (stale/absent <see cref="_lastSavedPath"/>, or an unrelated entry -- e.g. an
    /// abandoned-image record, which deliberately does NOT go through <see cref="IReceivedImageBuffer.SaveAsync"/>
    /// at all) is a silent, low-severity miss: Note/Flag simply stay disabled for that one frame,
    /// self-heals on the next real save, same "residual sliver deliberately accepted" tolerance
    /// <see cref="OnSaved"/>'s own doc comment already established for this exact class.
    ///
    /// <b>Round-1 audit-noted edge cases (both accepted, same tolerance class as above, not
    /// fixed)</b>: (1) <see cref="SaveFrameAsync"/> ALSO calls <see cref="IReceivedImageBuffer.SaveAsync"/>
    /// (a manual save-as, not the auto-recorder) and so ALSO overwrites <see cref="_lastSavedPath"/>
    /// -- a manual save landing in the narrow window between the recorder's own SaveAsync and
    /// RecordAsync calls would block correlation for that one frame the same way a mismatch does.
    /// (2) A <see cref="ISstvSessionService.ModeDetected"/> fan-out reaching THIS pane before it
    /// reaches <see cref="IReceivedImageBuffer"/>'s own internal <c>Generation</c> bump could in
    /// principle let a late <see cref="OnSaved"/> post pass its staleness guard and set
    /// <see cref="_lastSavedPath"/> AFTER <see cref="OnModeDetected"/>'s own reset already ran --
    /// correlating the just-finished PREVIOUS frame's entry onto the NEW frame now on screen. Both
    /// are microsecond-scale races with no observed real-world trigger, same severity class as the
    /// pre-existing residual sliver above.</summary>
    private void OnHistoryRecorded(ReceiveHistoryEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        // Unconditional -- deliberately NOT gated on entry.FilePath == _lastSavedPath like the
        // Note/Flag correlation below. That gate exists to find THIS card's own currently-displayed
        // frame specifically; the Previous-frames strip wants every completed reception, whichever
        // frame it belongs to.
        _ = AddPreviousFrameAsync(entry);

        if (entry.FilePath != _lastSavedPath)
        {
            return;
        }

        _currentEntryId = entry.Id;
        // ui_transition_plan.md step 6 (T2-4): read OFF THE ENTRY, never re-read from live radio
        // state -- see _latchedFrequencyHz's own doc comment for why.
        _latchedFrequencyHz = entry.FrequencyHz;
        _latchedRigMode = entry.RigMode;
        OnPropertyChanged(nameof(LatchedFrequencyDisplay));
        // Tier B audit finding: try/finally -- see OnModeDetected's own comment on the same fix for
        // the same flag.
        try
        {
            _suppressFrameMetadataEdits = true;
            Note = entry.Note;
            IsFlagged = entry.IsFlagged;
        }
        finally
        {
            _suppressFrameMetadataEdits = false;
        }

        OnPropertyChanged(nameof(CanEditFrameMetadata));
    });

    /// <summary>Builds <see cref="PreviousFrames"/>. Only <see cref="ReceiveDecodeState.Completed"/>
    /// receptions are added -- an abandoned/partial attempt isn't what an operator means by
    /// "a previous frame." Best-effort thumbnail load, same reasoning as every other thumbnail site
    /// in this app: a failed load renders that one entry without an image rather than dropping it
    /// from the list or blocking the others.
    ///
    /// Inserted by <see cref="ReceiveHistoryEntry.ReceivedAt"/>, not blindly at index 0: the
    /// thumbnail load below is a real async gap, and <see cref="IReceiveHistoryStore.Recorded"/> can
    /// fire back-to-back during a bulk-decoded WAV import (the same interleaving several sibling
    /// methods on this class already guard against, e.g. <see cref="ApplyStationIdDecodedAsync"/>'s
    /// own <see cref="IReceivedImageBuffer.Generation"/> check) -- sorting on insert keeps this list
    /// correctly ordered even if a later-fired event's thumbnail happens to resolve first.</summary>
    private async Task AddPreviousFrameAsync(ReceiveHistoryEntry entry)
    {
        if (entry.DecodeState != ReceiveDecodeState.Completed)
        {
            return;
        }

        Bitmap? thumbnail = null;
        try
        {
            var image = await _historyStore.LoadThumbnailAsync(entry, PreviousFramesThumbnailMaxDimension).ConfigureAwait(true);
            thumbnail = ImageSourceBitmapConverter.ToBitmap(image);
        }
        catch (Exception ex)
        {
            Log.LoadPreviousFrameThumbnailFailed(_logger, entry.Id, ex);
        }

        var insertAt = 0;
        while (insertAt < PreviousFrames.Count && PreviousFrames[insertAt].Entry.ReceivedAt > entry.ReceivedAt)
        {
            insertAt++;
        }

        PreviousFrames.Insert(insertAt, new RxHistoryEntryViewModel(entry, thumbnail));
        while (PreviousFrames.Count > PreviousFramesCapacity)
        {
            PreviousFrames.RemoveAt(PreviousFrames.Count - 1);
        }
    }

    /// <summary>Mirrors <c>RxHistoryPaneViewModel.OnSelectedEntryNoteChanged</c>/
    /// <c>PersistNoteDebouncedAsync</c> exactly -- same debounce/cancel-supersedes/error-surfacing
    /// shape, just against <see cref="_currentEntryId"/> instead of a list-selected entry's id (this
    /// pane only ever tracks the one currently-displayed frame, so there's no in-place list
    /// write-back needed -- <see cref="Note"/> already IS the display).</summary>
    partial void OnNoteChanged(string? value)
    {
        if (_suppressFrameMetadataEdits || _currentEntryId is not { } entryId)
        {
            return;
        }

        _notePersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _notePersistCts = cts;
        _ = PersistNoteDebouncedAsync(entryId, value, cts.Token);
    }

    private async Task PersistNoteDebouncedAsync(string entryId, string? note, CancellationToken ct)
    {
        try
        {
            await Task.Delay(NotePersistDebounce, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Normal control flow -- a newer edit superseded this one, same convention as
            // RxHistoryPaneViewModel's own identical catch.
            return;
        }

        Dispatcher.UIThread.Post(() => FrameMetadataErrorMessage = null);

        bool succeeded;
        try
        {
            succeeded = await _historyStore.SetNoteAsync(entryId, note, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Same reasoning as RxHistoryPaneViewModel's own identical guard: a keystroke arriving
            // while SetNoteAsync itself is in flight cancels it -- normal control flow, not a real
            // failure to surface.
            return;
        }
        catch (Exception ex)
        {
            Log.SetNoteFailed(_logger, entryId, ex);
            Dispatcher.UIThread.Post(() => FrameMetadataErrorMessage = _localization.GetString("Panes.RxFrameMeta.Error.SaveNoteFailed"));
            return;
        }

        if (!succeeded)
        {
            // Defensive -- IReceiveHistoryStore.SetNoteAsync's own doc comment: no automatic
            // deletion path exists in this port's production store today (docs/removed-features.md,
            // 2026-08-26), but a missing row is still a reachable state worth handling explicitly.
            Log.SetNoteEntryMissing(_logger, entryId);
            Dispatcher.UIThread.Post(() => FrameMetadataErrorMessage = _localization.GetString("Panes.RxFrameMeta.Error.EntryNoLongerExists"));
        }
    }

    partial void OnIsFlaggedChanged(bool value)
    {
        if (_suppressFrameMetadataEdits || _currentEntryId is not { } entryId)
        {
            return;
        }

        // Same chained-not-independent ordering-safety shape as RxHistoryPaneViewModel's own
        // PersistFlaggedAsync -- see _pendingFlagPersist's own doc comment.
        _pendingFlagPersist = PersistFlaggedAsync(entryId, value, _pendingFlagPersist);
    }

    private async Task PersistFlaggedAsync(string entryId, bool isFlagged, Task previous)
    {
        // Entire body inside one try/catch, including `await previous` -- same reasoning as
        // RxHistoryPaneViewModel's own identical method: if a Dispatcher.Post itself ever throws,
        // the resulting fault must not propagate into _pendingFlagPersist and permanently break
        // every LATER toggle's own `await previous`.
        try
        {
            await previous.ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => FrameMetadataErrorMessage = null);

            var succeeded = await _historyStore.SetFlaggedAsync(entryId, isFlagged).ConfigureAwait(false);
            if (!succeeded)
            {
                Log.SetFlaggedEntryMissing(_logger, entryId);
                Dispatcher.UIThread.Post(() => FrameMetadataErrorMessage = _localization.GetString("Panes.RxFrameMeta.Error.EntryNoLongerExists"));
            }
        }
        catch (Exception ex)
        {
            Log.SetFlaggedFailed(_logger, entryId, ex);
            Dispatcher.UIThread.Post(() => FrameMetadataErrorMessage = _localization.GetString("Panes.RxFrameMeta.Error.SaveFlagFailed"));
        }
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
            Image = _imagePool.Blit(current);
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

    private async Task LoadOperatorGridAsync()
    {
        try
        {
            OperatorGrid = await _sstvSession.GetOperatorGridAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadCaptureDeviceNameAsync -- a failure here leaves the
            // field null (GridDisplay's distance half falls back to "--") rather than blocking
            // construction.
            Log.LoadOperatorGridFailed(_logger, ex);
        }
    }

    private bool CanLookupQrz() => !IsLookingUpQrz && !string.IsNullOrWhiteSpace(OverrideCallsign) && IsQrzLookupConfigured;

    /// <summary>Refreshable, not just constructor-loaded -- <c>MainWindow.axaml.cs</c>'s
    /// Options-closed handler calls this again alongside its other pane-refresh calls, so
    /// enabling/disabling QRZ lookup in Options takes effect immediately without an app
    /// restart.</summary>
    public async Task LoadQrzLookupConfiguredAsync()
    {
        try
        {
            IsQrzLookupConfigured = await _logbookSession.IsQrzLookupConfiguredAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadCaptureDeviceNameAsync/LoadOperatorGridAsync
            // above -- a failure here leaves the button in its current (optimistic-default)
            // state rather than blocking construction. IsQrzLookupConfiguredAsync's own contract
            // already treats a settings-read failure as "not configured", so this catch is only
            // reached if something more unexpected (e.g. the call itself) goes wrong.
            Log.LoadQrzLookupConfiguredFailed(_logger, ex);
        }
    }

    /// <summary>Round-2 fix: this button used to rely ENTIRELY on
    /// <see cref="ILogbookSessionService.LookupCallsignAsync"/>'s own "not configured" result,
    /// surfaced via <see cref="QrzLookupErrorMessage"/> AFTER a click -- <see cref="IsQrzLookupConfigured"/>
    /// (backed by <see cref="ILogbookSessionService.IsQrzLookupConfiguredAsync"/>, no new
    /// <c>ScanlineStudio.Core.Logbook</c>/<c>ISettingsStore</c> dependency added to this
    /// <c>ScanlineStudio.UI</c> class) now gates the button ahead of time instead.</summary>
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

    /// <summary>Fires automatically on every real callsign change -- unlike <see cref="LookupQrzAsync"/>,
    /// no manual button: this is a local, indexed SQLite query (sub-millisecond), none of the
    /// network-call friction the QRZ button's manual-click gate exists to avoid. Covers BOTH writers
    /// of <see cref="OverrideCallsign"/> -- the once-per-decode auto-fill (already deduped by the
    /// generated setter's no-op-on-equal-value behavior) AND the editable TextBox
    /// (`MainWindow.axaml`'s <c>OverrideCallsign</c> binding), which updates per keystroke -- the
    /// debounce inside <see cref="RefreshWorkedBeforeCoreAsync"/> absorbs that.</summary>
    partial void OnOverrideCallsignChanged(string? oldValue, string? newValue)
    {
        // Cancelled FIRST, unconditionally, before any branching below -- clearing the callsign (or
        // starting a new reception) must not let an in-flight query for the OLD callsign resolve and
        // latch a stale Found/None onto the new, unrelated state.
        _workedBeforeCts?.Cancel();

        // fsk_cwid.md A3: unconditionally cleared here too, for the SAME reason -- ANY real change to
        // OverrideCallsign (auto-fill OR a manual edit) must not keep showing a stale source/time
        // claim for a value that's no longer accurate. ApplyStationIdDecodedAsync/ApplyCwIdDecodedAsync
        // re-set both fields immediately AFTER their own OverrideCallsign write (this handler runs
        // synchronously as part of that same write, so by the time control returns to them, the
        // clear below has already happened); a manual edit has no such re-set, so the label correctly
        // reverts to plain "Callsign" until the next real decode.
        _callsignSource = null;
        _callsignDecodedAtUtc = null;
        OnPropertyChanged(nameof(CallsignLabelDisplay));

        if (string.IsNullOrWhiteSpace(newValue))
        {
            WorkedBeforeStatus = WorkedBeforeStatus.Unknown;
            WorkedBeforeSummary = null;
            return;
        }

        var cts = new CancellationTokenSource();
        _workedBeforeCts = cts;
        _ = RefreshWorkedBeforeCoreAsync(newValue.Trim(), cts.Token);
    }

    /// <summary>Worked-before plan (2026-09-01): re-fires the SAME query
    /// <see cref="OnOverrideCallsignChanged"/> uses -- called from <c>MainWindow.axaml.cs</c>'s
    /// parent-pushed wiring of <c>LogbookPaneViewModel.QsoLogged</c>. Without this, the indicator's
    /// ONLY trigger is <see cref="OverrideCallsign"/> changing, so it would keep showing "New station"
    /// for a station just logged until the next decode. No-op unless <paramref name="callsign"/>
    /// matches the CURRENT <see cref="OverrideCallsign"/> (case-insensitive, matching the database's
    /// own <c>COLLATE NOCASE</c> semantics -- a plain ordinal compare would silently no-op on a case
    /// difference between what the operator typed in the Logbook form and what this pane currently
    /// holds, a real, expected case, not an edge case) -- a QSO logged for a DIFFERENT callsign than
    /// whatever this pane currently shows must not overwrite this indicator.</summary>
    public void NotifyQsoLogged(string callsign)
    {
        if (!string.Equals(callsign, OverrideCallsign?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _workedBeforeCts?.Cancel();
        var cts = new CancellationTokenSource();
        _workedBeforeCts = cts;
        _ = RefreshWorkedBeforeCoreAsync(callsign, cts.Token);
    }

    /// <summary>Shared by <see cref="OnOverrideCallsignChanged"/> and <see cref="NotifyQsoLogged"/> --
    /// debounced (absorbs per-keystroke manual typing; imperceptible on the once-per-decode auto
    /// path and on the log-just-happened path, neither of which is latency-sensitive), no in-flight
    /// "checking..." state (the query is sub-millisecond locally; a spinner would only ever flash for
    /// one frame -- the PREVIOUS value stays displayed during the debounce window instead of
    /// blanking). Threading: bare <see langword="await"/> throughout, matching
    /// <see cref="LookupQrzAsync"/>'s own shape -- simpler than <see cref="PersistNoteDebouncedAsync"/>'s
    /// <c>ConfigureAwait(false)</c> + <see cref="Dispatcher"/>.Post pairing, and safe here because
    /// both callers of this method already run on the UI thread.</summary>
    private async Task RefreshWorkedBeforeCoreAsync(string callsign, CancellationToken ct)
    {
        try
        {
            await Task.Delay(WorkedBeforeDebounce, ct);
            var lookup = await _logbookSession.GetWorkedBeforeAsync(callsign, ct);

            // Captured-callsign compare-before-apply -- a SECOND guard, not redundant with the CTS
            // cancel above: protects against out-of-order resolution between two in-flight queries
            // even with cancellation in play.
            if (!string.Equals(callsign, OverrideCallsign?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            switch (lookup.Outcome)
            {
                case WorkedBeforeOutcome.NotFound:
                    WorkedBeforeStatus = WorkedBeforeStatus.None;
                    WorkedBeforeSummary = null;
                    break;
                case WorkedBeforeOutcome.Found:
                    WorkedBeforeStatus = WorkedBeforeStatus.Found;
                    WorkedBeforeSummary = FormatWorkedBefore(lookup.Info!);
                    break;
                default:
                    // Failed -- deliberately NOT None: a failed check isn't "confirmed new" (see
                    // ILogbookSessionService.GetWorkedBeforeAsync's own doc comment for why this
                    // outcome exists separately from NotFound).
                    WorkedBeforeStatus = WorkedBeforeStatus.Unknown;
                    WorkedBeforeSummary = null;
                    Log.GetWorkedBeforeFailed(_logger, callsign);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal control flow -- a newer change superseded this one, same convention as
            // PersistNoteDebouncedAsync's own identical catch.
        }
    }

    private string FormatWorkedBefore(WorkedBeforeInfo info)
    {
        var segments = new List<string>();
        if (info.Count > 1)
        {
            segments.Add(_localization.GetString("Panes.RxFrameMeta.WorkedBefore.CountFormat", info.Count.ToString(CultureInfo.InvariantCulture)));
        }

        if (info.LastBand is { } band)
        {
            segments.Add(band);
        }

        segments.Add(info.LastStartUtc.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        return string.Join(" · ", segments);
    }

    /// <summary>Fires with no payload -- <c>MainWindow.axaml.cs</c>'s subscriber (the composition
    /// root holding both this VM and <see cref="LogbookPaneViewModel"/>) reads
    /// <see cref="OverrideCallsign"/>/<see cref="DetectedMode"/>/<see cref="StartedAt"/> directly
    /// off THIS instance rather than this event carrying a payload -- see the "Log QSO" plan's own
    /// design decision for why (same-callstack, same-tick hand-off, no new DTO type needed). The
    /// subscriber's read is safe specifically because every writer of those three properties
    /// already routes through <see cref="Dispatcher"/>, so a synchronous read on the UI thread
    /// (which is where <see cref="LogQsoCommand"/> itself always runs) can never observe a
    /// half-updated state.</summary>
    public event Action? LogQsoRequested;

    /// <summary>Enabled once a mode has EVER been detected this session, not "currently receiving"
    /// -- nothing nulls <see cref="DetectedMode"/> after a reception ends, which is the more useful
    /// behavior here (the natural moment to log a QSO is right after the frame finishes, not only
    /// mid-reception). Deliberately does NOT also require <see cref="OverrideCallsign"/> to be
    /// non-empty -- this button's role is "start logging what I'm currently/just received," not
    /// "auto-complete a decoded callsign"; gating on the FSK auto-fill specifically would disable
    /// it for the common case where the other station never sent one. The user can always type the
    /// callsign by hand on the Logbook tab -- that tab's own <c>CanLog</c> already requires a
    /// non-empty callsign before Log itself is clickable.</summary>
    private bool CanLogQso() => DetectedMode is not null;

    [RelayCommand(CanExecute = nameof(CanLogQso))]
    private void LogQso() => LogQsoRequested?.Invoke();

    /// <summary>Distinct from <see cref="IReceivedImageBuffer.SaveAsync"/>'s existing production
    /// caller (<c>ReceiveHistoryRecorder</c>, silent auto-archival on every completed frame) -- this
    /// is a manual "save a copy wherever I choose," so it needs its own file-picker round-trip rather
    /// than reusing the recorder's fixed directory/naming scheme. Same "has a mode ever been
    /// detected this session" gate as <see cref="CanLogQso"/> (nothing nulls <see cref="DetectedMode"/>
    /// after a reception ends -- saving the last-completed frame after it finishes is exactly the
    /// point, not just mid-reception).</summary>
    [ObservableProperty]
    private string? _saveFrameErrorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveFrameCommand))]
    private bool _isSavingFrame;

    private bool CanSaveFrame() => DetectedMode is not null && !IsSavingFrame;

    [RelayCommand(CanExecute = nameof(CanSaveFrame))]
    private async Task SaveFrameAsync(CancellationToken ct)
    {
        Log.SaveFrameInvoked(_logger);
        SaveFrameErrorMessage = null;
        IsSavingFrame = true;
        try
        {
            var suggestedFileName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{DetectedMode?.Id ?? "rx"}.png";
            var picked = await _filePickerService.PickSaveImageFileAsync(suggestedFileName);
            if (picked is not { } destination)
            {
                return;
            }

            await _receivedImage.SaveAsync(destination.Path, ct);
        }
        catch (Exception ex)
        {
            Log.SaveFrameFailed(_logger, ex);
            SaveFrameErrorMessage = _localization.GetString("Panes.RxImage.Error.SaveFrameFailed");
        }
        finally
        {
            IsSavingFrame = false;
        }
    }

    /// <summary>Piece C1 (RX tab Re-decode port): toggles <see cref="ISstvSessionService.StartRecordingAsync"/>/
    /// <see cref="ISstvSessionService.StopRecordingAsync"/>. Save location picked via
    /// <see cref="IFilePickerService.PickSaveWavFileAsync"/>, same round-trip shape as
    /// <see cref="SaveFrameAsync"/>. Reflects <see cref="IsRecording"/> as <see langword="false"/> on
    /// ANY failure (start or stop) -- <c>StopRecordingAsync</c>'s own finalize step clears the
    /// service's "is recording" state before attempting the file write, so "not recording" is
    /// accurate here regardless of which side threw.</summary>
    [ObservableProperty]
    private string? _recordErrorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonLabel))]
    private bool _isRecording;

    /// <summary>Same locale-recompute shape as <see cref="SyncSourceDisplay"/> -- a plain computed
    /// property recomputed via <see cref="NotifyPropertyChangedFor"/> on the state it depends on, not
    /// a live-language-switch subscription of its own.</summary>
    public string RecordButtonLabel => _localization.GetString(
        IsRecording ? "Panes.RxImage.StopRecording" : "Panes.RxImage.Record");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ToggleRecordingCommand))]
    private bool _isTogglingRecording;

    private bool CanToggleRecording() => !IsTogglingRecording;

    [RelayCommand(CanExecute = nameof(CanToggleRecording))]
    private async Task ToggleRecordingAsync()
    {
        RecordErrorMessage = null;
        IsTogglingRecording = true;
        try
        {
            if (IsRecording)
            {
                await _sstvSession.StopRecordingAsync();
                IsRecording = false;
                return;
            }

            var suggestedFileName = $"{DateTime.Now:yyyyMMdd-HHmmss}_rx.wav";
            var picked = await _filePickerService.PickSaveWavFileAsync(suggestedFileName);
            if (picked is null)
            {
                return;
            }

            await _sstvSession.StartRecordingAsync(picked);
            IsRecording = true;
        }
        catch (Exception ex)
        {
            Log.RecordToggleFailed(_logger, ex);
            RecordErrorMessage = _localization.GetString("Panes.RxImage.Error.RecordFailed");
            IsRecording = false;
        }
        finally
        {
            IsTogglingRecording = false;
        }
    }

    /// <summary>Piece C2 (RX tab Re-decode port): opens a WAV file via
    /// <see cref="IFilePickerService.PickOpenWavFileAsync"/> and decodes it via
    /// <see cref="ISstvSessionService.DecodeFromFileAsync"/> -- the SAME command backs both the RX
    /// pane's own "Decode WAV…" button and the Tools menu's "Decode WAV file…" item (they are one
    /// feature with two entry points, not two separate features).</summary>
    [ObservableProperty]
    private string? _redecodeErrorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RedecodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(RedecodeFromPathCommand))]
    private bool _isRedecoding;

    private bool CanRedecode() => !IsRedecoding;

    [RelayCommand(CanExecute = nameof(CanRedecode))]
    private async Task RedecodeAsync(CancellationToken ct)
    {
        RedecodeErrorMessage = null;
        var picked = await _filePickerService.PickOpenWavFileAsync();
        if (picked is null)
        {
            return;
        }

        await DecodeFileAsync(picked, ct);
    }

    /// <summary>Gallery/RX-details "Re-decode this frame" entry point (ui_transition_plan.md step 12,
    /// Step 4) -- same decode call as <see cref="RedecodeAsync"/> above, but <paramref name="path"/>
    /// (a completed reception's own auto-saved audio file) is already known, so there is no
    /// file-picker step. Wired from <c>RxHistoryPaneViewModel.RedecodeRequested</c> via
    /// <c>MainWindow.axaml.cs</c> -- see that event's own doc comment for why
    /// <c>RxHistoryPaneViewModel</c> itself never calls <see cref="ISstvSessionService"/> directly
    /// (it deliberately does not depend on it, to keep Gallery browsing from ever appearing to
    /// interrupt a live RX decode).</summary>
    [RelayCommand(CanExecute = nameof(CanRedecode))]
    private async Task RedecodeFromPathAsync(string path, CancellationToken ct) => await DecodeFileAsync(path, ct);

    /// <summary>Shared by both entry points above. Surfaces <see cref="ISstvSessionService.DecodeFromFileAsync"/>'s
    /// own REAL failure reason (already-in-progress, transmit-in-flight, auto-detect-paused, sample-
    /// rate mismatch, empty file -- see that method's own doc comment) rather than a generic message
    /// -- it already throws a descriptive <see cref="InvalidOperationException"/> for every one of
    /// those cases, so this just surfaces <see cref="Exception.Message"/> through a localized
    /// template, same interpolation precedent as <c>OptionsWindowViewModel</c>'s
    /// <c>Options.General.Storage.Error.SaveFailed</c>. Any OTHER exception type (e.g. a raw I/O
    /// failure reading the WAV) still falls back to the generic message -- its <c>Message</c> is not
    /// written to be operator-facing the way the documented <see cref="InvalidOperationException"/>
    /// reasons are.</summary>
    private async Task DecodeFileAsync(string path, CancellationToken ct)
    {
        // Auditor-caught (round 1 code-review): RedecodeFromPathCommand is invoked via a plain
        // ICommand.Execute(path) from MainWindow.axaml.cs's cross-pane wiring, which does NOT consult
        // CanExecute the way a bound Button would -- without this guard, a Gallery click while the
        // Receive tab's own file-picker decode is already in flight would still enter this method,
        // and its own `finally` would clear IsRedecoding out from under the FIRST (still-running)
        // decode, re-enabling that button mid-decode. ISstvSessionService.DecodeFromFileAsync's own
        // _fileDecodeInFlight guard already rejects the underlying decode either way (no data
        // hazard), but this stops the state-clobber the exception path would otherwise cause.
        if (IsRedecoding)
        {
            return;
        }

        RedecodeErrorMessage = null;
        IsRedecoding = true;
        try
        {
            await _sstvSession.DecodeFromFileAsync(path, ct);
        }
        catch (InvalidOperationException ex)
        {
            Log.RedecodeFailed(_logger, ex);
            RedecodeErrorMessage = _localization.GetString("Panes.RxImage.Error.RedecodeFailedWithReason", ex.Message);
        }
        catch (Exception ex)
        {
            Log.RedecodeFailed(_logger, ex);
            RedecodeErrorMessage = _localization.GetString("Panes.RxImage.Error.RedecodeFailed");
        }
        finally
        {
            IsRedecoding = false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Disposing the RX image pool during shutdown failed")]
        public static partial void ImagePoolDisposeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading configured RX capture device name failed")]
        public static partial void LoadCaptureDeviceNameFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading operator's own grid square failed")]
        public static partial void LoadOperatorGridFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading whether QRZ lookup is configured failed")]
        public static partial void LoadQrzLookupConfiguredFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "GetWorkedBeforeAsync({Callsign}) reported a failure; the worked-before row shows \"—\" instead of an answer")]
        public static partial void GetWorkedBeforeFailed(ILogger logger, string callsign);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading Previous-frames strip thumbnail failed for entry {EntryId}")]
        public static partial void LoadPreviousFrameThumbnailFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Mode detected: {ModeId}")]
        public static partial void ModeDetected(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Decode restarted, abandoning mode {AbandonedModeId} (new sync lock found mid-reception, forced mode, or Auto-Stop)")]
        public static partial void DecodeRestarted(ILogger logger, string abandonedModeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Quick-mode button forced mode {ModeId}")]
        public static partial void QuickSelectModeInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Quick-mode button pressed for unknown mode id {ModeId} -- no matching AvailableModes entry")]
        public static partial void QuickSelectModeUnknownId(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX mode hold set to {ModeId}")]
        public static partial void HoldModeInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX mode hold released")]
        public static partial void ReleaseHeldModeInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "Abort invoked, abandoning current reception")]
        public static partial void AbortInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX auto-detect pause toggled: paused={Paused}")]
        public static partial void AutoDetectPausedChanged(ILogger logger, bool paused);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the just-saved RX image's file size failed")]
        public static partial void ReadSavedFileSizeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "QRZ lookup failed: {Reason}")]
        public static partial void QrzLookupFailed(ILogger logger, string? reason);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QRZ lookup threw")]
        public static partial void QrzLookupThrew(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SaveFrame invoked")]
        public static partial void SaveFrameInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Manual SaveFrame failed")]
        public static partial void SaveFrameFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Record toggle failed")]
        public static partial void RecordToggleFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Re-decode from file failed")]
        public static partial void RedecodeFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the operator's own callsign (for the decoded station-ID self-filter) failed")]
        public static partial void GetOperatorCallsignFailed(ILogger logger, Exception ex);

        // fsk_cwid.md A3 (logging half, bundled into A-P1): today there is nothing to diagnose "my
        // ID never shows" from -- this decode event was previously silent end to end.
        [LoggerMessage(Level = LogLevel.Information, Message = "FSK station ID decoded: callsign={Callsign} receptionSequence={ReceptionSequence}")]
        public static partial void StationIdDecoded(ILogger logger, string callsign, long receptionSequence);

        [LoggerMessage(Level = LogLevel.Information, Message = "FSK station ID decoded: nrRst={NrRst} receptionSequence={ReceptionSequence}")]
        public static partial void StationIdNrRstDecoded(ILogger logger, string nrRst, long receptionSequence);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Decoded station ID dropped: matches the operator's own callsign, receptionSequence={ReceptionSequence}")]
        public static partial void StationIdDroppedAsOwnCallsign(ILogger logger, long receptionSequence);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Decoded station ID dropped: stale relative to the current reception, receptionSequence={ReceptionSequence} currentReceptionSequence={CurrentReceptionSequence}")]
        public static partial void StationIdDroppedStale(ILogger logger, long receptionSequence, long currentReceptionSequence);

        // fsk_cwid.md B-P3: same "nothing to diagnose this from before" reasoning as StationIdDecoded above.
        [LoggerMessage(Level = LogLevel.Information, Message = "CW ID decoded: text=\"{Text}\" confidence={Confidence} receptionSequence={ReceptionSequence}")]
        public static partial void CwIdDecoded(ILogger logger, string text, double confidence, long receptionSequence);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Decoded CW ID dropped: stale relative to the current reception, receptionSequence={ReceptionSequence} currentReceptionSequence={CurrentReceptionSequence}")]
        public static partial void CwIdDroppedStale(ILogger logger, long receptionSequence, long currentReceptionSequence);

        // fsk_cwid.md §9: FSK is authoritative and is never overwritten by a CW-decoded callsign --
        // this is the one case worth a Warning, since a genuine mismatch (not just CW's own decode
        // noise) is diagnostically useful and otherwise invisible.
        [LoggerMessage(Level = LogLevel.Warning, Message = "CW-decoded callsign disagrees with the already-set FSK callsign, keeping FSK: fskCallsign={FskCallsign} cwCallsign={CwCallsign} receptionSequence={ReceptionSequence}")]
        public static partial void CwIdCallsignDisagreesWithFsk(ILogger logger, string fskCallsign, string cwCallsign, long receptionSequence);

        // Auditor code-review nit: the FSK sibling (StationIdDroppedAsOwnCallsign/StationIdDroppedStale
        // above) logs every drop reason at Debug; this path's self-filter/post-await-FSK-arrived drops
        // silently returned. Same diagnostic value, same level.
        [LoggerMessage(Level = LogLevel.Debug, Message = "Decoded CW ID callsign dropped: matches the operator's own callsign, receptionSequence={ReceptionSequence}")]
        public static partial void CwIdDroppedAsOwnCallsign(ILogger logger, long receptionSequence);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Decoded CW ID callsign skipped: an FSK-decoded callsign was already set while the operator-callsign read was in flight, receptionSequence={ReceptionSequence}")]
        public static partial void CwIdCallsignSkippedFskAlreadySet(ILogger logger, long receptionSequence);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetNoteAsync failed for entry {EntryId}")]
        public static partial void SetNoteFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetNoteAsync: entry {EntryId} no longer exists")]
        public static partial void SetNoteEntryMissing(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetFlaggedAsync failed for entry {EntryId}")]
        public static partial void SetFlaggedFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetFlaggedAsync: entry {EntryId} no longer exists")]
        public static partial void SetFlaggedEntryMissing(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the persisted quick-mode grid failed")]
        public static partial void LoadQuickModeGridFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting the quick-mode grid failed")]
        public static partial void PersistQuickModeGridFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting the Squelch level failed")]
        public static partial void PersistSenseLevelFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting the AFC enable flag failed")]
        public static partial void PersistAfcEnabledFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting the RX BPF preset failed")]
        public static partial void PersistRxBpfPresetFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Quick-mode grid slot {SlotIndex} reassigned to mode {ModeId}")]
        public static partial void ReassignQuickModeSlotInvoked(ILogger logger, int slotIndex, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Quick-mode grid reassignment rejected: slot index {SlotIndex} is out of range")]
        public static partial void ReassignQuickModeSlotOutOfRange(ILogger logger, int slotIndex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Quick-mode grid reassignment rejected: mode {ModeId} is already used by a different slot than {SlotIndex}")]
        public static partial void ReassignQuickModeSlotAlreadyUsedElsewhere(ILogger logger, int slotIndex, string modeId);
    }
}

/// <summary>fsk_cwid.md A4: one row in <see cref="RxImagePaneViewModel.StationsHeard"/> -- one per
/// decode EVENT (a callsign packet, an NR/RST sub-packet, or a CW-ID decode are three separate
/// events, even when they belong to the same reception), not one per reception. <see cref="ModeId"/>
/// is a best-effort snapshot of <see cref="RxImagePaneViewModel.DetectedMode"/> at insertion time,
/// NOT looked up for the specific reception this row belongs to -- for a late/stale-arriving decode
/// (see <see cref="RxImagePaneViewModel.AddStationHeard"/>'s own doc comment for why those are still
/// logged), this can show a NEWER reception's mode than the one that actually decoded. Acceptable:
/// this list is a session-only convenience log, not the authoritative record (persistence is A2/
/// <c>RxStationIdAttacher</c>); <see cref="ReceptionSequence"/> is the field a future click-to-select
/// integration (fsk_cwid.md A4's own "once A2 exists" note) would actually key off.</summary>
public sealed record StationIdHeardViewModel(string Text, string Source, DateTimeOffset DecodedAtUtc, string? ModeId, long ReceptionSequence)
{
    /// <summary>The compact list's own one-line-per-row text -- e.g. "W1AW · FSK · 14:02:11Z ·
    /// Martin M1". Not run through <c>ILocalizationService</c> (this record has no reference to one,
    /// and every other "compact list" precedent in this pane -- <see cref="RxImagePaneViewModel.PreviousFrames"/>'s
    /// own captions -- is similarly a plain, untranslated timestamp format); the field NAMES this
    /// implicitly carries ("FSK"/"CW") are already untranslated protocol abbreviations, same as the
    /// row labels' own "FSK ·"/"CW ·" prefixes (fsk_cwid.md A3).</summary>
    public string Display => $"{Text} · {Source} · {DecodedAtUtc:HH:mm:ss}Z{(ModeId is { } modeId ? $" · {modeId}" : string.Empty)}";
}
