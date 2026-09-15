using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Real pane #3 of 3 (Phase-3 plan decision #9). Phase 4 added the inline stock/template
/// picker (spec/07-image-pipeline.md's "Stock image library" section) alongside the original browse
/// flow. The TX image editor pass (spec/07-image-pipeline.md's "TX image editor" section) changed
/// picking a source from "auto-resize straight to mode dimensions" to "load at native resolution,
/// then open the editor for crop/resize/stretch/overlay" -- this VM owns that hand-off (constructing
/// the editor, reacting to Applied/Cancelled) but never touches Dock/window placement itself; see
/// <see cref="EditorOpened"/>/<see cref="EditorClosed"/>.</summary>
public sealed partial class TxControlsPaneViewModel : ViewModelBase, IDisposable
{
    private readonly ISstvSessionService _sstvSession;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IStockImageLibrary _stockLibrary;
    private readonly ITransmitImagePreparer _preparer;
    private readonly IFilePickerService _filePickerService;
    private readonly ILocalizationService _localization;
    private readonly ISettingsStore _settingsStore;
    private readonly IRadioSessionService _radioSession;
    private readonly IMacroTextResolver _macroTextResolver;
    private readonly ILogger<TxControlsPaneViewModel> _logger;
    private readonly ILogger<TxImageEditorPaneViewModel> _imageEditorLogger;

    /// <summary>Captured (not fire-and-forget-discarded) so <see cref="ReassignQuickModeSlotAsync"/>
    /// can await it first -- see <c>RxImagePaneViewModel</c>'s own identically-named field for the
    /// full race/why-a-flag-was-tried-and-removed reasoning, which applies here unchanged.</summary>
    private Task _loadTxPaneUiSettingsTask = Task.CompletedTask;

    // Phase 2 (spec/15-template-designer.md) -- threaded through to TxImageEditorPaneViewModel's own
    // constructor at both construction sites below; this VM doesn't otherwise consume either itself.
    private readonly IReceivedImageBuffer _receivedImageBuffer;
    private readonly IReceiveHistoryStore _receiveHistoryStore;

    // Phase 5 (spec/15-template-designer.md) -- template persistence, threaded through the same way.
    private readonly ITemplateStore _templateStore;
    private readonly IImageSourceWriter _imageSourceWriter;
    private readonly ILogger<ReadyRackViewModel> _readyRackLogger;

    private const int SwrCutoffConsecutiveSamplesRequired = 2;

    /// <summary>Owns the token passed into <see cref="ISstvSessionService.TransmitAsync"/> -- both
    /// <see cref="StopTransmitCommand"/> (manual) and <see cref="CheckSwrCutoff"/> (automatic) cancel
    /// this same source. Set to <see langword="null"/> only inside <see cref="TransmitAsync"/>'s own
    /// <c>finally</c>, which runs on the UI thread (no <c>ConfigureAwait(false)</c> on the awaited
    /// call -- deliberate) immediately after disposing it, with no <see langword="await"/> between
    /// the two statements -- so a queued <see cref="Dispatcher.UIThread.Post"/> callback (the
    /// StateChanges handler driving the cutoff check) can only ever observe this field as either a
    /// live, non-disposed source or <see langword="null"/>, never a disposed-but-non-null one. Keep
    /// it that way -- adding <c>ConfigureAwait(false)</c> to the transmit call would reintroduce
    /// exactly that race.</summary>
    private CancellationTokenSource? _transmitCts;

    /// <summary>Set right before <see cref="_transmitCts"/> is cancelled by <see cref="CheckSwrCutoff"/>
    /// (never by <see cref="StopTransmitCommand"/>) so <see cref="TransmitAsync"/>'s
    /// <c>catch (OperationCanceledException)</c> can tell an auto-cutoff apart from a manual Stop TX
    /// click and show the right message for each. Reset at <see cref="TransmitAsync"/>'s own entry,
    /// not only inside that catch -- a cutoff firing after the transmit loop had already drained
    /// naturally (no exception at all) would otherwise leave this <see langword="true"/> forever,
    /// misattributing the *next* manual Stop TX to a stale cutoff.</summary>
    private bool _cutoffTriggered;

    private int _consecutiveSwrOverThreshold;

    /// <summary>Stub survey follow-up (2026-08-26, user request): the SWR-cutoff enable/threshold
    /// control moved to Options -&gt; Radio/CAT -- this pane no longer owns editing or persisting it,
    /// only enforcing it. Loaded once at construction (<see cref="LoadSafetySettingsAsync"/>) and kept
    /// live via <see cref="IRadioSessionService.SafetySettingsChanged"/> so an edit made in Options
    /// while this pane is already constructed (including mid-transmission) takes effect without
    /// reconstructing the VM. A SINGLE <see cref="RadioSafetySpec"/> field, not two loose
    /// enabled/threshold fields, deliberately -- both values must update together atomically as seen
    /// by <see cref="CheckSwrCutoff"/>, which reads it from inside the SAME
    /// <see cref="Dispatcher.UIThread.Post"/> callback <see cref="OnRadioStateChanged"/> already runs
    /// in (<see cref="OnSafetySettingsChanged"/>'s own write is posted the same way) -- both sides are
    /// UI-thread-only by construction, so two loose fields would risk observing a torn
    /// enabled/threshold pair from two independent field writes, one record field cannot. Initialized
    /// at declaration (not left default/null) since a poll can arrive before the constructor's own
    /// fire-and-forget <see cref="LoadSafetySettingsAsync"/> completes.</summary>
    private RadioSafetySpec _currentSafetySpec = new(false, RadioSafetySpec.DefaultSwrCutoffThreshold);

    private IImageSource? _loadedImage;

    /// <summary>Backlog item (user request, 2026-08-17) -- tracks the currently-open editor (null
    /// while none is open) so <see cref="CanQuickSelectMode"/>
    /// can distinguish a BLANK/untouched editor (safe to silently replace on a mode switch) from a
    /// real photo pick or an in-progress edit (never silently discarded). Set in
    /// <see cref="OpenEditorWithLoadedSourceAsync"/>, cleared in
    /// <see cref="OnEditorApplied"/>/<see cref="OnEditorCancelled"/>.</summary>
    private TxImageEditorPaneViewModel? _currentEditor;

    /// <summary>True only when <see cref="_currentEditor"/> was opened via
    /// <see cref="OpenBlankEditorAsync"/> specifically -- <see cref="TxImageEditorPaneViewModel.HasUnsavedEdits"/>
    /// alone can't distinguish this from a REAL photo the operator explicitly picked via Browse/Stock
    /// that just hasn't been edited yet (both start with an empty undo stack); only the blank
    /// placeholder is safe to silently swap out on a mode switch, a manually-picked real photo never
    /// is, edited or not. Set alongside every <c>IsEditorOpen = true</c> assignment (this class's own
    /// existing 3-writer convention: <see cref="OpenEditorForSourceAsync"/>/
    /// <see cref="OpenBlankEditorAsync"/>/<see cref="EditCurrentImageAsync"/>).</summary>
    private bool _currentEditorIsBlank;

    /// <summary>The native-resolution original plus the crop/stretch/overlay/adjustment choices
    /// applied to it -- retained (not just the final mode-sized image) so a later mode change can
    /// re-run Crop→Resize→ApplyAdjustments→ApplyTemplate against the *new* mode's dimensions instead
    /// of stretching/cropping an already-cropped image a second time. Normalized (0..1) coordinates
    /// make this composition valid across modes with different pixel dimensions.
    /// <see cref="Adjustments"/> was added after the fact (spec/18-path-to-1.0.md Medium item, the
    /// adjustment-sliders sub-piece) -- its absence here was a real, silent feature-loss bug:
    /// without it, a mode change after Apply dropped Brightness/Contrast/etc. entirely, not just
    /// re-projected them incorrectly. <see cref="RawOverlay"/> is DELIBERATELY separate from
    /// <see cref="Document"/> (Phase 1 renamed from <c>Overlay</c>/<c>ImageOverlay</c> -- spec/15-
    /// template-designer.md's polymorphic element model, spec/18-path-to-1.0.md's own re-open/
    /// re-edit sub-piece, round-1 plan-review finding) -- <see cref="Document"/> is already
    /// crop-projected and macro-resolved (correct for <see cref="OnSelectedModeChanged"/>'s own
    /// direct pipeline call), while re-seeding a re-opened <see cref="TxImageEditorPaneViewModel"/>
    /// needs the raw, photo-anchored, un-resolved form instead -- feeding the editor from
    /// <see cref="Document"/> would silently misplace existing content and permanently bake macro
    /// templates like "DE %m" into their currently-resolved value. **Phase 1 plan-review finding,
    /// explicitly accepted rather than fixed this pass**: <see cref="Document"/>'s elements are
    /// projected against the mode/crop/PreserveAspect combination active at Apply time --
    /// <see cref="OnSelectedModeChanged"/>'s reflow reuses those SAME already-projected coordinates
    /// against a NEW mode's dimensions, a pre-existing, already-tracked defect (this doc comment's
    /// own history) that Phase 1 makes materially worse: with real per-element bounds and
    /// shrink-to-fit, a mode change can now visibly RESCALE text, not just nudge its position. A
    /// real fix needs <c>ProjectRectToCropRelative</c> extracted into a static pure helper reusable
    /// from both this class and <see cref="TxImageEditorPaneViewModel"/>, re-projecting from
    /// <see cref="RawOverlay"/> at the new mode's dimensions (and re-resolving macros freshly rather
    /// than reusing <see cref="Document"/>'s frozen <c>ResolvedText</c>) -- deliberately out of
    /// Phase 1's scope; tracked as a known gap, not silently shipped as a discovery.</summary>
    private sealed record EditState(
        IImageSource Original, NormalizedRect CropRect, bool PreserveAspect, TemplateDocument Document,
        ImageAdjustments Adjustments, IReadOnlyList<TxImageEditorPaneViewModel.RawElementSnapshot> RawOverlay,
        IReadOnlyDictionary<string, string> TemplateVariables);

    private EditState? _editState;

    /// <summary>Guards against a second pick starting while an editor is already open/loading --
    /// simpler than a cancel-and-replace token for what's a single, short-lived, user-driven
    /// sequence (the view is expected to disable picking while an editor is open; this is the
    /// view-model-level backstop). Also gates every <see cref="SelectedMode"/> mutation path
    /// (spec/18-path-to-1.0.md High item 2, the stale-mode transmit crash): the open
    /// <see cref="TxImageEditorPaneViewModel"/> captures its target mode once at construction and
    /// never re-targets, so letting <see cref="SelectedMode"/> change underneath it lets Apply
    /// hand back an image sized for a mode that's no longer selected -- <see cref="TransmitAsync"/>
    /// then pairs the NEW <see cref="SelectedMode"/> with that stale-sized image and the encoder
    /// throws a dimension-mismatch exception. An <see cref="ObservableProperty"/> (not a plain
    /// field) so the View can bind the mode ComboBox's <c>IsEnabled</c> to it directly.</summary>
    [ObservableProperty]
    private bool _isEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToneMapText))]
    [NotifyPropertyChangedFor(nameof(GeometryText))]
    [NotifyPropertyChangedFor(nameof(AutoPicksText))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(VisHeaderText))]
    [NotifyPropertyChangedFor(nameof(VoxToneText))]
    private SstvModeDefinition? _selectedMode;

    /// <summary>The selected mode's own baseband tone range -- backs mock2's Transmit tab "Tone
    /// map" field. A static per-mode lookup (`SstvModeDefinition.LuminanceMinHz`/`MaxHz`, e.g.
    /// narrow-family modes override the 1500/2300 default to 2044/2300 -- confirmed real,
    /// mode-dependent data, not a constant, see `SstvModeRegistry.cs`), not a live measurement --
    /// deliberately NOT the roadmap's separate, harder "Occupied BW" item (a live spectral estimate
    /// that research found likely wouldn't vary meaningfully by content anyway; explicitly deferred,
    /// see `~/.claude/plans/wandering-glowing-otter.md`). <see langword="null"/> only when no mode
    /// is selected, matching every other <see cref="SelectedMode"/>-derived state in this
    /// class.</summary>
    public string? ToneMapText => SelectedMode is { } mode
        ? _localization.GetString("Panes.TxControls.Telemetry.ToneMapFormat", mode.LuminanceMinHz, mode.LuminanceMaxHz)
        : null;

    /// <summary>The selected mode's raw image dimensions -- backs mock2's Transmit tab "Geometry"
    /// field. Static per-mode data (<see cref="SstvModeDefinition.ImageWidth"/>/<c>ImageHeight</c>),
    /// no calculation needed.</summary>
    public string? GeometryText => SelectedMode is { } mode
        ? _localization.GetString("Panes.TxControls.GeometryFormat", mode.ImageWidth, mode.ImageHeight)
        : null;

    /// <summary>The selected mode's total transmit duration -- backs mock2's Transmit tab
    /// "Duration" field. See <see cref="GetFrameSeconds"/>'s own doc comment for the formula.</summary>
    public string? DurationText => SelectedMode is { } mode
        ? _localization.GetString("Panes.TxControls.DurationFormat", GetFrameSeconds(mode))
        : null;

    /// <summary>The selected mode's real VIS-header shape and on-air value -- backs mock2's
    /// Transmit tab "VIS header" field. See <see cref="ISstvSessionService.GetVisHeaderInfo"/>'s own
    /// doc comment.</summary>
    public string? VisHeaderText => SelectedMode is { } mode
        ? _sstvSession.GetVisHeaderInfo(mode) switch
        {
            (VisHeaderKind.Avt, var value) => _localization.GetString("Panes.TxControls.VisHeaderValue.Avt", value),
            (VisHeaderKind.Extended, var value) => _localization.GetString("Panes.TxControls.VisHeaderValue.Extended", value),
            (VisHeaderKind.Narrow, var value) => _localization.GetString("Panes.TxControls.VisHeaderValue.Narrow", value),
            (_, var value) => _localization.GetString("Panes.TxControls.VisHeaderValue.Standard", value),
        }
        : null;

    /// <summary>The selected mode's fixed pre-VIS leader-tone burst duration -- backs mock2's
    /// Transmit tab "VOX tone" field, renamed to "Leader tone" in the app itself
    /// (ui_transition_plan.md step 9, T2-9). See <see cref="ISstvSessionService.GetLeaderToneDurationMs"/>'s
    /// own doc comment for why this isn't legacy's actual (unported) VOX feature.</summary>
    public string? VoxToneText => SelectedMode is { } mode
        ? _localization.GetString("Panes.TxControls.VoxToneFormat", _sstvSession.GetLeaderToneDurationMs(mode))
        : null;

    [ObservableProperty]
    private Bitmap? _previewImage;

    // T0-11 (production_audit.md): same dispose-old-on-change pattern as RxHistoryPaneViewModel's
    // own PreviewImage -- see that property's own comment for the full reasoning (deferred via
    // Dispatcher.UIThread.Post at Background priority, guarded on WriteableBitmap specifically).
    partial void OnPreviewImageChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if (oldValue is WriteableBitmap old)
        {
            Dispatcher.UIThread.Post(() => old.Dispose(), DispatcherPriority.Background);
        }
    }

    [ObservableProperty]
    private string? _selectedFileName;

    /// <summary>TX history plan (2026-09-01, Fable operator-perspective punch list, "No TX
    /// history"): the last <see cref="SentFramesCapacity"/> transmissions, newest first --
    /// session-only, no persistence, direct mirror of <see cref="RxImagePaneViewModel.PreviousFrames"/>.
    /// Recorded by <see cref="TransmitAsync"/>'s own <c>finally</c> block for EVERY outcome
    /// (Completed/Stopped/Failed), not just successful sends -- gated on <see cref="_anyProgressReported"/>
    /// so a cancel/failure before any real audio played produces no entry at all.</summary>
    public ObservableCollection<TransmittedFrameViewModel> SentFrames { get; } = [];

    private const int SentFramesCapacity = 6;

    /// <summary>Gates whether a non-Completed <see cref="TransmitAsync"/> outcome gets recorded into
    /// <see cref="SentFrames"/> -- reset <see langword="false"/> at that method's own start, set
    /// <see langword="true"/> by <see cref="OnTransmitProgressChanged"/> the first time a real
    /// progress report lands. Without this, every SWR-cutoff/manual-stop/device-open-failure during
    /// radio setup or tuning (zero RF ever produced) would push a real sent frame out of the bounded
    /// strip -- see the recording block's own comment for the plan-review finding this fixes.</summary>
    private bool _anyProgressReported;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TxClockText))]
    private bool _isTransmitting;

    /// <summary>spec/18-path-to-1.0.md Medium item: "No TX send-progress feedback during
    /// transmit." Nullable, mirroring <c>RxImagePaneViewModel.Progress</c>'s own pattern -- null
    /// while idle (no ProgressBar fill), a real <c>[0,1]</c> value while
    /// <see cref="IsTransmitting"/>. Set to <c>0</c> at TX start (not left null until the first
    /// report arrives) and reset to <c>null</c> in <see cref="TransmitAsync"/>'s own
    /// <c>finally</c>, alongside the existing telemetry resets there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransmitProgressText))]
    [NotifyPropertyChangedFor(nameof(TxClockText))]
    private double? _transmitProgress;

    /// <summary>Backing value for <see cref="TransmitProgressText"/> only -- deliberately not an
    /// <c>[ObservableProperty]</c> itself; <see cref="OnTransmitProgressChanged"/> always sets this
    /// immediately before <see cref="TransmitProgress"/>, whose own setter is what actually raises
    /// the <see cref="TransmitProgressText"/> change notification (via
    /// <c>NotifyPropertyChangedFor</c> above) -- a second independent notification here would be
    /// redundant.</summary>
    private TimeSpan _transmitRemaining;

    /// <summary>Backing value for <see cref="TxClockText"/> only -- same non-<c>[ObservableProperty]</c>
    /// pattern as <see cref="_transmitRemaining"/> above, set immediately before <see cref="TransmitProgress"/>
    /// in <see cref="OnTransmitProgressChanged"/>, whose own setter raises the notification.</summary>
    private TimeSpan _transmitElapsed;

    public string TransmitProgressText => TransmitProgress is { } progress
        ? _localization.GetString("Panes.TxControls.TransmitProgressFormat", (int)Math.Round(progress * 100), FormatRemaining(_transmitRemaining))
        : string.Empty;

    /// <summary>Elapsed transmit time -- backs mock2's Transmit tab "TX clock" field. Idle text
    /// while not transmitting, otherwise <see cref="_transmitElapsed"/> formatted the same way as
    /// <see cref="TransmitProgressText"/>'s own remaining-time figure.</summary>
    public string TxClockText => IsTransmitting
        ? FormatRemaining(_transmitElapsed)
        : _localization.GetString("Panes.TxControls.Telemetry.TxClockIdle");

    private static string FormatRemaining(TimeSpan remaining) => remaining.TotalHours >= 1
        ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
        : $"{remaining.Minutes}:{remaining.Seconds:D2}";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoPicksText))]
    private bool _autoFollowRxMode;

    /// <summary>Backs mock2's Transmit tab "Auto picks" field. Not just a restatement of the
    /// Auto/Manual segmented control above it (<see cref="AutoFollowRxMode"/>'s own toggle) --
    /// while Auto is on, shows the mode that toggle actually picked last (<see cref="OnModeDetected"/>
    /// already assigns <see cref="SelectedMode"/>), so the row carries real information instead of
    /// duplicating the control two rows up.</summary>
    public string AutoPicksText => AutoFollowRxMode
        ? SelectedMode?.DisplayName ?? _localization.GetString("Panes.TxControls.AutoPicksValue.None")
        : _localization.GetString("Panes.TxControls.AutoPicksValue.Manual");

    /// <summary>TX-only telemetry (see <see cref="RadioState"/>'s own doc comment) -- populated only
    /// while this pane's own <see cref="IsTransmitting"/> AND the rig's own PTT readback both agree
    /// transmission is actually in progress; <see langword="null"/> otherwise, never a stale/meaningless
    /// last-known value.</summary>
    [ObservableProperty]
    private float? _liveSwrRatio;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveAlcPercentDisplay))]
    [NotifyPropertyChangedFor(nameof(AlcMeterFillPercent))]
    private float? _liveAlcLevel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PowerMeterFillPercent))]
    private float? _livePowerPercent;

    /// <summary>Plan-review finding: <see cref="LiveAlcLevel"/> (<see cref="RadioState.AlcLevel"/>)
    /// is a 0.0-1.0 fraction, NOT 0-100 like <see cref="LivePowerPercent"/> -- displaying it raw
    /// (this row's pre-fix binding) rendered "0.42" instead of a percentage. <see langword="null"/>
    /// passes through unchanged, same convention as every other TX-only telemetry field.</summary>
    public float? LiveAlcPercentDisplay => LiveAlcLevel is { } alc ? alc * 100 : null;

    /// <summary>Fill-bar percent for the POWER meter (<see cref="Atoms.axaml"/>'s <c>IndustryMeter</c>
    /// atom) -- clamped to [0,100], 0 (empty bar) rather than null while idle/ungated, since a
    /// <see langword="double"/>-typed grid-length converter has no meaningful "no value" rendering.</summary>
    public double PowerMeterFillPercent => Math.Clamp(LivePowerPercent ?? 0, 0, 100);

    /// <summary>Same as <see cref="PowerMeterFillPercent"/>, for ALC -- built off
    /// <see cref="LiveAlcPercentDisplay"/> (already 0-100-scaled), not the raw 0.0-1.0
    /// <see cref="LiveAlcLevel"/>.</summary>
    public double AlcMeterFillPercent => Math.Clamp(LiveAlcPercentDisplay ?? 0, 0, 100);

    /// <summary>Bounded (<see cref="TelemetryHistoryCapacity"/>-sample, oldest-evicted-first) history
    /// of the same TX-only telemetry as <see cref="LiveSwrRatio"/>/<see cref="LiveAlcLevel"/>/
    /// <see cref="LivePowerPercent"/> above, appended alongside them in <see cref="OnRadioStateChanged"/>
    /// under the identical "actually transmitting" gate -- no history is recorded while idle/receiving.
    /// Backend-only for now: nothing in this pass renders it, this just makes the data available for a
    /// future historical power/ALC chart. An <see cref="ObservableCollection{T}"/> (not a plain
    /// <see cref="Queue{T}"/>) so a future chart control can bind directly without a translation
    /// step.</summary>
    public ObservableCollection<TxTelemetrySample> TelemetryHistory { get; } = [];

    /// <summary>~30s of history at the radio poll loop's default 250ms interval
    /// (<c>RadioConnectionSpec.PollInterval</c>) -- not tied to that value exactly (this pane has no
    /// visibility into the configured interval), just a reasonable fixed cap for a QoL feature with no
    /// consumer yet.</summary>
    private const int TelemetryHistoryCapacity = 120;

    /// <summary>Refreshed every <see cref="OnRadioStateChanged"/> callback, not a one-time computed
    /// property -- both rigctld and Hamlib backends connect lazily, so <c>Capabilities</c> is
    /// <see cref="RadioCapabilities.None"/> until the first successful poll; a plain computed property
    /// evaluated once at bind time would never become <see langword="true"/> for a rig that's still
    /// connecting when this view first renders.</summary>
    [ObservableProperty]
    private bool _showSwrMeter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnyMeter))]
    private bool _showAlcMeter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAnyMeter))]
    private bool _showPowerMeter;

    /// <summary>Gates the POWER/ALC meter section's shared kicker caption -- true when at least one
    /// of the two meter rows it captions is actually showing (a rig can report Power without ALC, or
    /// vice versa).</summary>
    public bool ShowAnyMeter => ShowPowerMeter || ShowAlcMeter;

    /// <summary>The TX playback device's display name -- what
    /// <see cref="ISstvSessionService.TransmitAsync"/> would actually resolve and use right now,
    /// including a fallback to the backend-reported default device when nothing is explicitly
    /// configured (spec/18-path-to-1.0.md Critical item 1 / item 8; see
    /// <see cref="ISstvSessionService.GetConfiguredPlaybackDeviceNameAsync"/>'s own doc comment for
    /// the full "what WOULD be resolved, not necessarily currently in-flight" caveat) -- backs
    /// mock2's Transmit tab "Device" field. <see langword="null"/> until the best-effort initial
    /// load below completes, or if a configured device is no longer present, or (now the rare case)
    /// nothing is configured AND the backend reports no default either (same non-throwing contract
    /// the service method itself has -- this is a passive display field, not something that should
    /// surface an error banner). Loaded at construction and re-loaded whenever the Options dialog
    /// closes (<c>MainWindow.axaml.cs</c>'s <c>OptionsRequested</c> handler, same fix as
    /// <c>MainViewModel.LoadOperatorSettingsAsync</c>'s own header-callsign-chip bug) -- <see cref="LoadOutputDeviceNameAsync"/>
    /// is <see langword="public"/> for exactly that call site.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDeviceNameDisplay))]
    private string? _outputDeviceName;

    public string OutputDeviceNameDisplay => OutputDeviceName ?? "—";

    /// <summary>Frame-metadata-style read-only summary of what <see cref="ISstvSessionService.TransmitAsync"/>
    /// would actually resolve right now (CW-ID/FSK station-ID subsystem Phase 6) -- backs the
    /// Transmit tab's "Identification" card. Loaded via <see cref="ISstvSessionService.GetStationIdTransmitOptionsAsync"/>
    /// at construction and re-loaded whenever the Options dialog closes, same fix/convention as
    /// <see cref="OutputDeviceName"/> above.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FskIdDisplay))]
    [NotifyPropertyChangedFor(nameof(TailDisplay))]
    private bool _fskIdEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FskIdDisplay))]
    private string? _identificationCallsign;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwIdDisplay))]
    [NotifyPropertyChangedFor(nameof(TailDisplay))]
    private bool _cwIdEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwIdDisplay))]
    private int _identificationCwWpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CwIdDisplay))]
    private double _identificationCwToneFrequencyHz;

    /// <summary><c>CwIdMode.SoundFile</c> configured (`docs/plans/sound-file-id-plan.md`) --
    /// mutually exclusive with <see cref="CwIdEnabled"/> by construction (both come from
    /// <c>StationIdTransmitOptions</c>'s own CW/sound-file exclusivity), so <see cref="TailDisplay"/>
    /// treats "both true" as CW-wins (matching every other exclusivity check this feature already
    /// has), the same defensive posture as an unreachable-but-handled case.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TailDisplay))]
    private bool _soundFileIdEnabled;

    /// <summary>Mockup's own "DL2QSK"-shaped value -- the callsign as-configured (not the
    /// wire-normalized form <c>AnalogFmSstvEncoder</c> actually sends; see
    /// <c>StationIdTransmitOptions.Callsign</c>'s own doc comment for why normalization happens
    /// there, not here). "Off" (localized) when FSK-ID TX is disabled -- <b>not</b> when the
    /// callsign happens to be empty while enabled (that's a real, if unusual, configuration state
    /// this card should still describe accurately, not hide behind the same fallback text).</summary>
    public string FskIdDisplay => FskIdEnabled
        ? IdentificationCallsign ?? string.Empty
        : _localization.GetString("Panes.TxId.Off");

    /// <summary>Mockup's own "18 WPM · 800 Hz"-shaped value.</summary>
    public string CwIdDisplay => CwIdEnabled
        ? _localization.GetString("Panes.TxId.CwIdFormat", IdentificationCwWpm, IdentificationCwToneFrequencyHz)
        : _localization.GetString("Panes.TxId.Off");

    /// <summary>Summary of what actually gets appended after the image
    /// (<c>Main.cpp:7018-7025</c>'s real order: FSK-ID packet first, then CW-ID/sound-file ID --
    /// independent of FSK, not mutually exclusive with it, matching
    /// <c>AnalogFmSstvEncoder.GenerateFrequencySegments</c>'s own append order). Genuinely new
    /// wording (mockup's own "CW after frame" is one specific combination, not a format this reuses
    /// verbatim) -- a reasonable, low-risk reading of what this row is for, not a citation-backed
    /// legacy string. <c>(_, true, true)</c> (CW AND sound-file both reported enabled) is a state
    /// <c>SstvSessionService.ResolveTransmitSettingsAsync</c>'s own resolution should never actually
    /// produce (CW/sound-file are mutually exclusive, `docs/plans/sound-file-id-plan.md`) -- handled
    /// here as CW-wins anyway, matching every other exclusivity check this feature already has, not
    /// left as an unreachable gap.</summary>
    public string TailDisplay => (FskIdEnabled, CwIdEnabled, SoundFileIdEnabled) switch
    {
        (true, true, _) => _localization.GetString("Panes.TxId.TailBoth"),
        (true, false, true) => _localization.GetString("Panes.TxId.TailBothSoundFile"),
        (true, false, false) => _localization.GetString("Panes.TxId.TailFskOnly"),
        (false, true, _) => _localization.GetString("Panes.TxId.TailCwOnly"),
        (false, false, true) => _localization.GetString("Panes.TxId.TailSoundFileOnly"),
        (false, false, false) => _localization.GetString("Panes.TxId.Off"),
    };

    public TxControlsPaneViewModel(
        ISstvSessionService sstvSession,
        IImageFileLoader imageFileLoader,
        IStockImageLibrary stockLibrary,
        ITransmitImagePreparer preparer,
        IFilePickerService filePickerService,
        ILocalizationService localization,
        ISettingsStore settingsStore,
        IRadioSessionService radioSession,
        IMacroTextResolver macroTextResolver,
        ILogger<TxControlsPaneViewModel> logger,
        ILogger<TxImageEditorPaneViewModel> imageEditorLogger,
        IReceivedImageBuffer receivedImageBuffer,
        IReceiveHistoryStore receiveHistoryStore,
        ITemplateStore templateStore,
        IImageSourceWriter imageSourceWriter,
        ILogger<ReadyRackViewModel> readyRackLogger)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _stockLibrary = stockLibrary;
        _preparer = preparer;
        _filePickerService = filePickerService;
        _localization = localization;
        _settingsStore = settingsStore;
        _radioSession = radioSession;
        _macroTextResolver = macroTextResolver;
        _logger = logger;
        _imageEditorLogger = imageEditorLogger;
        _receivedImageBuffer = receivedImageBuffer;
        _receiveHistoryStore = receiveHistoryStore;
        _templateStore = templateStore;
        _imageSourceWriter = imageSourceWriter;
        _readyRackLogger = readyRackLogger;

        AvailableModes = sstvSession.AvailableModes;
        _selectedMode = AvailableModes.Count > 0 ? AvailableModes[0] : null;

        sstvSession.ModeDetected += OnModeDetected;
        sstvSession.TransmitProgressChanged += OnTransmitProgressChanged;
        radioSession.StateChanges.Subscribe(OnRadioStateChanged);
        radioSession.SafetySettingsChanged += OnSafetySettingsChanged;

        BuildQuickModeSlots(QuickModeGridDefaults.Ids);

        // Best-effort initial load, same reasoning as RxHistoryPaneViewModel's constructor -- a
        // failure here leaves the strip empty rather than blocking construction.
        _ = RefreshStockLibraryAsync();
        _loadTxPaneUiSettingsTask = LoadTxPaneUiSettingsAsync();
        _ = LoadSafetySettingsAsync();
        _ = LoadOutputDeviceNameAsync();
        _ = LoadIdentificationSummaryAsync();
    }

    /// <summary>Fired when a picked source's original image has loaded and a
    /// <see cref="TxImageEditorPaneViewModel"/> is ready to be shown -- the host (<c>AppDockFactory</c>)
    /// owns turning this into an actual dockable pane; this VM only knows about the editor's own
    /// Applied/Cancelled events, never about Dock/window placement.</summary>
    public event Action<TxImageEditorPaneViewModel>? EditorOpened;

    /// <summary>Fired once the editor resolves (Applied or Cancelled either one) -- the host should
    /// remove the editor pane at this point.</summary>
    public event Action? EditorClosed;

    /// <summary>Set once by <see cref="MainViewModel"/>'s own constructor, right after both VMs
    /// exist, so the Output card's TX volume slider (moved here from the header) can bind straight
    /// to <c>RadioStatus.TxVolumePercent</c> -- the single real source of truth for that value.
    /// Deliberately NOT constructor-injected: <see cref="RadioStatusViewModel"/> isn't
    /// DI-registered (<see cref="MainViewModel"/> constructs it by hand), so DI can't supply it
    /// here, and a separate DI registration would fork the value into two out-of-sync instances.
    /// Deliberately NOT an XAML ancestor-lookup-with-type-cast binding either (the
    /// `$parent[Window].((vm:MainViewModel)DataContext)...` this replaced) -- that pattern throws
    /// `ArgumentException: Unable to resolve type` at runtime the first time the view is actually
    /// realized, since this project uses classic (non-compiled) bindings and inline type casts in a
    /// binding path force a runtime type-resolution step that doesn't reliably find sibling
    /// view-model types. A plain reference assigned once from the parent is the established safe
    /// pattern here (see QuickModeSlotViewModel/StockEntryViewModel/
    /// FrequencyPresetButtonViewModel, each carrying its own pre-resolved command for the same
    /// reason).</summary>
    public RadioStatusViewModel? RadioStatus { get; set; }

    private const int StockThumbnailMaxDimension = 64;

    public IReadOnlyList<SstvModeDefinition> AvailableModes { get; }

    /// <summary>Total transmit duration for <paramref name="mode"/>. <c>ImageHeight</c> is the
    /// image's pixel height, not the transmitted-line count -- <see cref="ColorEncoding.YCbCrLinePaired"/>
    /// and <see cref="ColorEncoding.MonoAveragedPaired"/> modes transmit one line per 2 image rows
    /// (<c>AnalogFmSstvEncoder</c>'s <c>RowsPerTransmissionLine</c> = 2 for those families;
    /// <c>SstvModeRegistry</c> sets <c>ImageHeight = transmissionUnits * 2</c> for them), so dividing
    /// them back out here is required, not optional -- plan-review caught this double-counting PD90
    /// (703.04 ms/line x 256 rows / 1000 = 180.0 s displayed for an actual 90.0 s mode) before it
    /// shipped.</summary>
    internal static double GetFrameSeconds(SstvModeDefinition mode)
    {
        var rowsPerLine = mode.ColorEncoding is ColorEncoding.YCbCrLinePaired or ColorEncoding.MonoAveragedPaired ? 2 : 1;
        return mode.LineDurationMs * mode.ImageHeight / rowsPerLine / 1000.0;
    }

    public ObservableCollection<StockEntryViewModel> StockEntries { get; } = [];

    private async Task LoadTxPaneUiSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var txPaneUi = settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings) ?? new TxPaneUiSettings();

            var resolved = QuickModeGridAssignment.Resolve(txPaneUi.QuickModeGridIds, AvailableModes);
            for (var i = 0; i < resolved.Count; i++)
            {
                QuickModeSlots[i].CurrentMode = resolved[i];
            }

            // Setting auto-follow invokes persistence synchronously up to its first await.
            // Restore every slot before that callback captures the settings snapshot.
            AutoFollowRxMode = txPaneUi.AutoFollowRxMode;
            RecomputeQuickModeMenuEntryStates();
        }
        catch (Exception ex)
        {
            Log.LoadTxPaneUiSettingsFailed(_logger, ex);
        }
    }

    public async Task LoadOutputDeviceNameAsync()
    {
        try
        {
            OutputDeviceName = await _sstvSession.GetConfiguredPlaybackDeviceNameAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadTxPaneUiSettingsAsync above -- a failure here
            // leaves the field null (no device shown) rather than blocking construction.
            Log.LoadOutputDeviceNameFailed(_logger, ex);
        }
    }

    public async Task LoadIdentificationSummaryAsync()
    {
        try
        {
            var stationId = await _sstvSession.GetStationIdTransmitOptionsAsync();
            FskIdEnabled = stationId.FskIdEnabled;
            IdentificationCallsign = stationId.Callsign;
            CwIdEnabled = stationId.CwEnabled;
            IdentificationCwWpm = stationId.CwWpm;
            IdentificationCwToneFrequencyHz = stationId.CwToneFrequencyHz;
            SoundFileIdEnabled = stationId.SoundFileIdEnabled;
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadOutputDeviceNameAsync above -- a failure here
            // leaves the card showing "Off" for everything rather than blocking construction.
            Log.LoadIdentificationSummaryFailed(_logger, ex);
        }
    }

    /// <summary>Read-modify-write against whatever is currently persisted for this section, not a
    /// fresh <c>new TxPaneUiSettings { ... }</c> -- a from-scratch write here would silently clobber
    /// whichever of <see cref="TxPaneUiSettings.QuickModeGridIds"/>/<see cref="TxPaneUiSettings.AutoFollowRxMode"/>
    /// this particular call isn't updating.</summary>
    private async Task PersistTxPaneUiSettingsAsync()
    {
        try
        {
            // T0-2: both are live VM state -- must be read here, before UpdateAsync, never inside its
            // mutate lambda (which may run on any thread, including this one synchronously, but must
            // not itself read UI-thread-affine state per ISettingsStore.UpdateAsync's own contract).
            var autoFollowRxMode = AutoFollowRxMode;
            var quickModeGridIds = QuickModeSlots.Select(s => s.CurrentMode.Id).ToArray();
            await _settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings) ?? new TxPaneUiSettings();
                var updated = current with
                {
                    AutoFollowRxMode = autoFollowRxMode,
                    QuickModeGridIds = quickModeGridIds,
                };
                return settings.WithSection(TxPaneUiSettings.SectionKey, updated, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings);
            });
        }
        catch (Exception ex)
        {
            Log.PersistTxPaneUiSettingsFailed(_logger, ex);
        }
    }

    partial void OnAutoFollowRxModeChanged(bool value) => _ = PersistTxPaneUiSettingsAsync();

    /// <summary>Constructor-time load, still needed even though the control that used to edit this
    /// setting moved to Options -&gt; Radio/CAT (2026-08-26) -- without it, a persisted setting stays
    /// inert until the user happens to open and re-Save Options after every app restart, which is
    /// exactly the "safety feature looks armed and isn't" failure this pane exists to avoid.</summary>
    private async Task LoadSafetySettingsAsync()
    {
        try
        {
            var spec = await _radioSession.GetSafetySettingsAsync();
            Dispatcher.UIThread.Post(() => _currentSafetySpec = spec);
        }
        catch (Exception ex)
        {
            Log.LoadSafetySettingsFailed(_logger, ex);
        }
    }

    /// <summary>The live-update half of the Options relocation (2026-08-26) -- fires when
    /// <c>OptionsWindowViewModel</c> saves an edited value, so this pane's enforcement picks it up
    /// without being reconstructed (including mid-transmission, if Options happens to be open in
    /// parallel). Posted to the UI thread, same as <see cref="OnRadioStateChanged"/>'s own write --
    /// see <see cref="_currentSafetySpec"/>'s own doc comment for why both sides being UI-thread-only
    /// is what makes a single record-field write race-free.</summary>
    private void OnSafetySettingsChanged(RadioSafetySpec spec) => Dispatcher.UIThread.Post(() => _currentSafetySpec = spec);

    /// <summary>Marshaled to the UI thread, same pattern as <c>RadioStatusViewModel.OnStateChanged</c>.
    /// Refreshes the meter-visibility flags from the current <c>Capabilities</c> every poll (see
    /// <see cref="ShowSwrMeter"/>'s own doc comment for why this can't be a one-time computed
    /// property), updates the live meter readouts (TX-only), and runs the SWR auto-cutoff check.</summary>
    private void OnRadioStateChanged(RadioState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ShowSwrMeter = _radioSession.Capabilities.HasFlag(RadioCapabilities.SwrMeter);
            ShowAlcMeter = _radioSession.Capabilities.HasFlag(RadioCapabilities.AlcMeter);
            ShowPowerMeter = _radioSession.Capabilities.HasFlag(RadioCapabilities.PowerMeter);

            if (IsTransmitting && state.IsTransmitting)
            {
                LiveSwrRatio = state.SwrRatio;
                LiveAlcLevel = state.AlcLevel;
                LivePowerPercent = state.PowerPercent;

                if (TelemetryHistory.Count >= TelemetryHistoryCapacity)
                {
                    TelemetryHistory.RemoveAt(0);
                }
                TelemetryHistory.Add(new TxTelemetrySample(DateTimeOffset.UtcNow, state.SwrRatio, state.AlcLevel, state.PowerPercent));
            }
            else
            {
                LiveSwrRatio = null;
                LiveAlcLevel = null;
                LivePowerPercent = null;
            }

            CheckSwrCutoff(state);
        });
    }

    /// <summary>See <see cref="ISstvSessionService.TransmitProgressChanged"/>'s own doc comment for
    /// the raise-site threading contract this marshals from (playback pump thread, not the UI
    /// thread). Plan-review finding: the <see cref="IsTransmitting"/> guard here is a defense-in-
    /// depth measure, not strictly required for correctness under this codebase's actual dispatcher
    /// priorities -- but removes any dependency on <see cref="Dispatcher.UIThread.Post"/> ordering
    /// happening to match <see cref="TransmitAsync"/>'s own await-continuation priority for a late
    /// report arriving after that method's own <c>finally</c> reset has already run.</summary>
    private void OnTransmitProgressChanged(TransmitProgressInfo info)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsTransmitting)
            {
                return;
            }

            var remaining = info.EstimatedTotal - info.Elapsed;
            _transmitRemaining = remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
            _transmitElapsed = info.Elapsed;
            TransmitProgress = info.Fraction;
            _anyProgressReported = true;
        });
    }

    /// <summary>Gated on the rig's OWN PTT readback (<paramref name="state"/>'s <c>IsTransmitting</c>),
    /// not just this pane's <see cref="IsTransmitting"/> flag -- a rig with no PTT-readback capability
    /// always reports <c>IsTransmitting = false</c>, which correctly disables the cutoff entirely on
    /// VOX/DTR-keyed rigs (no reliable TX-state confirmation to trust a meter reading against) rather
    /// than arming on an unconfirmed guess. Requires <see cref="SwrCutoffConsecutiveSamplesRequired"/>
    /// consecutive over-threshold polls, not a single reading -- a lone glitchy sample (e.g. right as
    /// PTT keys, still carrying a stale RX-era value) must not trip a false cutoff.</summary>
    private void CheckSwrCutoff(RadioState state)
    {
        // Local snapshot, not repeated field reads (code-review finding): guarantees the guard check
        // and the Log.SwrCutoffTriggered call below test/report the SAME threshold, even though
        // _currentSafetySpec is also written from OnSafetySettingsChanged's own UI-thread-posted
        // callback -- both this method and that callback only ever run inside a Dispatcher.UIThread.Post
        // lambda (see _currentSafetySpec's own doc comment), so there's no torn read here either way,
        // this is purely about not reading the field twice for one logical check.
        var spec = _currentSafetySpec;
        if (!IsTransmitting || !state.IsTransmitting || !spec.SwrCutoffEnabled || state.SwrRatio is not { } swr || swr <= spec.SwrCutoffThreshold)
        {
            _consecutiveSwrOverThreshold = 0;
            return;
        }

        _consecutiveSwrOverThreshold++;
        if (_consecutiveSwrOverThreshold >= SwrCutoffConsecutiveSamplesRequired)
        {
            Log.SwrCutoffTriggered(_logger, swr, spec.SwrCutoffThreshold);
            _cutoffTriggered = true;
            _transmitCts?.Cancel();
        }
    }

    /// <summary><see cref="ISstvSessionService.ModeDetected"/> fires synchronously from the audio
    /// drain thread (that interface's own doc comment) -- marshal to the UI thread before touching
    /// <see cref="SelectedMode"/>, same pattern as <c>RadioStatusViewModel.OnStateChanged</c>.</summary>
    private void OnModeDetected(SstvModeDefinition mode)
    {
        // IsEditorOpen AND AutoFollowRxMode are both re-checked INSIDE the posted lambda, not before
        // Post -- this method runs on the audio drain thread (this method's own doc comment above)
        // but both are only ever written on the UI thread, so a bare pre-Post read here has no
        // guaranteed visibility of a UI-thread write that raced it (code-review finding on
        // spec/18-path-to-1.0.md High item 2: a stale "not open yet" read could let this slip
        // through right as the editor opens, reintroducing the exact crash this whole fix targets --
        // AutoFollowRxMode is the same class of read, Tier B audit finding: it was left outside the
        // Post when IsEditorOpen was moved in for this exact reason, risking a stale-true read right
        // after the operator un-ticked auto-follow performing an unwanted mode change). Checking
        // again once already marshalled onto the UI thread is race-free. IsTransmitting/_loadedImage
        // are the same class of UI-thread-only-written state (set from TransmitAsync/
        // OnSelectedModeChanged, both RelayCommand/partial-property-changed methods that only ever
        // run on the UI thread) -- checked inside the Post lambda for the identical reason.
        //
        // Two legacy guards added here (`TrackTxMode`, `Main.cpp:4907-4915`) that this method
        // previously lacked (spec/18-path-to-1.0.md gap, flagged 2026-08-25 during the SBAuto
        // RX-pause work):
        //
        // `!SBTX->Down` -- don't switch TX mode while actively transmitting. This port's
        // TransmitAsync captures SelectedMode/_loadedImage into locals at its own start, so an
        // in-flight transmission's audio can't be corrupted by a later SelectedMode change either
        // way -- but without this guard, a mid-transmission auto-detect would still silently swap
        // out the loaded/prepared image and mode the operator is looking at while they're actively
        // sending an unrelated one, and would corrupt whatever gets queued for the NEXT transmit.
        //
        // `m_RXW == pBitmapTX->Width` -- only follow when the detected RX mode's own image width
        // matches the currently loaded TX image's width. Load-bearing given this port's own
        // architecture, not just a literal port for its own sake: OnSelectedModeChanged
        // unconditionally crops/resizes/reflows _loadedImage to whatever mode is newly selected
        // (see that method's own doc comment) -- without this guard, auto-follow detecting a
        // different-width mode would silently re-crop an already-framed TX image the operator
        // deliberately prepared. No width check at all when nothing is loaded yet (_loadedImage is
        // null): there is nothing to protect. This IS a deliberate deviation from legacy, not a
        // literal-equivalence claim -- legacy's own pBitmapTX always exists (allocated at
        // construction, Main.cpp:968) and is always sized to the current TX mode, so legacy's width
        // check is always live even against a blank TX bitmap; this port's own _loadedImage
        // genuinely can be null (nothing picked for TX yet), and OnSelectedModeChanged's own
        // `value is null || _editState is null` branch makes a reflow attempt there a real no-op
        // regardless, so skipping the check in that case changes no IMAGE state -- SelectedMode
        // itself (and the mode dropdown/"Auto picks" row it drives) still follows, unlike legacy.
        Dispatcher.UIThread.Post(() =>
        {
            if (!AutoFollowRxMode || IsEditorOpen || IsTransmitting)
            {
                return;
            }

            if (_loadedImage is { } loaded && loaded.Width != mode.ImageWidth)
            {
                return;
            }

            SelectedMode = mode;
        });
    }

    /// <summary>Backlog item (user request, 2026-08-17): the editor now opens by default with a
    /// blank placeholder, so a strict <c>!IsEditorOpen</c> gate would leave mode-select shortcuts
    /// unreachable until the first Apply. Relaxed to also allow mode-select while the CURRENTLY
    /// open editor is the auto/manually-opened BLANK placeholder specifically
    /// (<see cref="_currentEditorIsBlank"/>) with no edits made yet -- see
    /// <see cref="OnSelectedModeChanged"/>'s own handling, which closes that stale blank editor and
    /// opens a fresh one at the new mode's size. Deliberately does NOT relax for a manually-picked
    /// REAL photo (Browse/Stock) that just hasn't been edited yet, a re-edit-of-an-already-applied-
    /// image session, or a genuinely in-progress edit (<see cref="TxImageEditorPaneViewModel.HasUnsavedEdits"/>
    /// true) -- real work (or a deliberate photo pick) is never silently made switchable-away-from.
    /// <para>User-reported bug (2026-09-15): "once i removed the background however stock browse and
    /// open editor are still greyed out." <c>_currentEditorIsBlank</c> is a ONE-SHOT snapshot from
    /// editor-open time -- it never flips true just because the operator later cleared the background
    /// mid-session, and Remove Background's own Undo step flips <c>HasUnsavedEdits</c> true, so the
    /// snapshot check above stayed locked forever after. OR'd with the editor's own LIVE
    /// <see cref="TxImageEditorPaneViewModel.HasNoBackgroundOrOverlayElements"/> instead of replacing
    /// the snapshot check -- see that property's own doc comment: a genuinely empty editor (no
    /// background, no elements, however it got that way) is always safe to switch away from, while
    /// one with OTHER real work still correctly stays locked either way.</para></summary>
    private bool IsCurrentEditorBlankAndUntouched() =>
        (_currentEditorIsBlank && _currentEditor is { HasUnsavedEdits: false })
        || _currentEditor is { HasNoBackgroundOrOverlayElements: true };

    /// <summary>Auditor-found regression (2026-08-17, usability-gap review): the mode ComboBox and
    /// Browse/STOCK were still hard-gated on a plain <c>!IsEditorOpen</c> binding in AXAML, which
    /// <see cref="CanQuickSelectMode"/>'s own relaxation never
    /// reached (that drives <c>Command.CanExecute</c>, not a separate <c>IsEnabled</c> binding) --
    /// with the editor now always open by default, this made mode-select-via-dropdown and Browse/
    /// STOCK permanently unreachable after the very first blank auto-open. Bindable form of the
    /// same <see cref="IsCurrentEditorBlankAndUntouched"/> relaxation for controls with no
    /// Command/CanExecute mechanism to hang the check on instead. The 16 quick-mode-grid buttons
    /// needed no equivalent fix -- removing their own redundant <c>IsEnabled="{Binding !IsEditorOpen}"</c>
    /// (which was overriding <see cref="CanQuickSelectMode"/>'s already-correct CanExecute) was
    /// enough, since Avalonia's Button already auto-disables when a bound Command's CanExecute is
    /// false and nothing else overrides it.</summary>
    public bool CanChangeSourceOrMode => !IsEditorOpen || IsCurrentEditorBlankAndUntouched();

    /// <summary>User-requested (2026-09-15): "even if there are elements on the canvas, if no
    /// background has been picked before, i should be able to load one later also, not only as first
    /// canvas element." <see cref="CanChangeSourceOrMode"/> above stays scoped to genuinely SAFE
    /// cases for changing MODE too (a mode change reflows dimensions/crop, disruptive regardless of
    /// whether a background exists) -- widening it broadly would have also relaxed the mode ComboBox
    /// and Copy-to-TX for a case that's only actually safe for loading a BACKGROUND specifically.
    /// This is the wider, background-loading-only gate: Browse/Stock/File&gt;Open now stay reachable
    /// whenever the currently open editor has no real background yet, REGARDLESS of overlay elements
    /// or other unsaved edits -- <see cref="TxImageEditorPaneViewModel.LoadBackground"/> installs the
    /// picked photo directly into that SAME editor instance instead of discarding it for a new one
    /// (<see cref="OpenEditorForSourceAsync"/>'s own new branch), so there is nothing left for the
    /// old wholesale-replace gate to protect in this specific case. Not used by the mode ComboBox or
    /// Copy-to-TX -- both keep binding to <see cref="CanChangeSourceOrMode"/>, unchanged.</summary>
    public bool CanLoadBackground => !IsEditorOpen || IsCurrentEditorBlankAndUntouched() || _currentEditor is { HasRealBackground: false };

    private bool CanQuickSelectMode() => !IsEditorOpen || IsCurrentEditorBlankAndUntouched();

    /// <summary>Backs the 16-pill quick-mode grid (spec/18-path-to-1.0.md High item 7).
    /// <c>CanExecute</c> alone isn't a hard gate for a direct <c>Execute()</c> call, only
    /// <c>Button.OnClick</c> consults it -- this body-level check is the real backstop. A
    /// <paramref name="modeId"/> with no matching entry in <see cref="AvailableModes"/> logs a
    /// warning and no-ops (mistyped XAML <c>CommandParameter</c> defense, matching this file's
    /// existing style).</summary>
    [RelayCommand(CanExecute = nameof(CanQuickSelectMode))]
    private void QuickSelectMode(string modeId)
    {
        if (IsEditorOpen && !IsCurrentEditorBlankAndUntouched())
        {
            return;
        }

        var mode = AvailableModes.FirstOrDefault(m => m.Id == modeId);
        if (mode is null)
        {
            Log.QuickSelectModeUnknownId(_logger, modeId);
            return;
        }

        Log.QuickSelectModeInvoked(_logger, modeId);
        SelectedMode = mode;
    }

    /// <summary>The 16-slot quick-mode grid -- independent of the RX pane's own grid by design
    /// (confirmed with the user: reassigning here never touches
    /// <c>RxImagePaneViewModel.QuickModeSlots</c>). Built synchronously from
    /// <see cref="QuickModeGridDefaults"/> in the constructor, then mutated in place once the
    /// persisted assignment loads or a slot is reassigned -- see <c>RxImagePaneViewModel</c>'s
    /// identically-shaped property for the full reasoning, which applies here unchanged.</summary>
    public ObservableCollection<QuickModeSlotViewModel> QuickModeSlots { get; } = [];

    private void BuildQuickModeSlots(IReadOnlyList<string> ids)
    {
        var resolved = QuickModeGridAssignment.Resolve(ids, AvailableModes);
        for (var i = 0; i < resolved.Count; i++)
        {
            var slot = new QuickModeSlotViewModel(i, resolved[i], QuickSelectModeCommand);
            foreach (var mode in AvailableModes)
            {
                slot.MenuEntries.Add(new QuickModeMenuEntryViewModel(mode, ReassignQuickModeSlotCommand, i));
            }

            QuickModeSlots.Add(slot);
        }

        RecomputeQuickModeMenuEntryStates();
    }

    /// <summary>See <c>RxImagePaneViewModel.RecomputeQuickModeMenuEntryStates</c>'s own doc
    /// comment -- identical reasoning, independent grid.</summary>
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

    /// <summary>Backs the quick-mode grid's right-click popup -- see
    /// <c>RxImagePaneViewModel.ReassignQuickModeSlotAsync</c>'s own doc comment for the full
    /// reasoning (passive relabel, never applies/selects the mode; awaits
    /// <see cref="_loadTxPaneUiSettingsTask"/> first to avoid racing that load), which applies here
    /// unchanged. Persists via the shared <see cref="PersistTxPaneUiSettingsAsync"/> (read-modify-
    /// write against the same <see cref="TxPaneUiSettings"/> section <see cref="AutoFollowRxMode"/>
    /// already uses) rather than a separate writer, so there is only ever one read-modify-write path
    /// for this settings section.</summary>
    [RelayCommand]
    private async Task ReassignQuickModeSlotAsync(QuickModeReassignment reassignment)
    {
        await _loadTxPaneUiSettingsTask;

        if (reassignment.SlotIndex < 0 || reassignment.SlotIndex >= QuickModeSlots.Count)
        {
            Log.ReassignQuickModeSlotOutOfRange(_logger, reassignment.SlotIndex);
            return;
        }

        var alreadyUsedElsewhere = QuickModeSlots.Any(s => s.SlotIndex != reassignment.SlotIndex && s.CurrentMode.Id == reassignment.Mode.Id);
        if (alreadyUsedElsewhere)
        {
            Log.ReassignQuickModeSlotAlreadyUsedElsewhere(_logger, reassignment.SlotIndex, reassignment.Mode.Id);
            return;
        }

        Log.ReassignQuickModeSlotInvoked(_logger, reassignment.SlotIndex, reassignment.Mode.Id);
        QuickModeSlots[reassignment.SlotIndex].CurrentMode = reassignment.Mode;
        RecomputeQuickModeMenuEntryStates();
        _ = PersistTxPaneUiSettingsAsync();
    }

    /// <summary>Keeps the quick-mode-grid buttons' enabled state in sync with
    /// <see cref="IsEditorOpen"/> -- <see cref="CanQuickSelectMode"/> alone only re-evaluates when
    /// something explicitly requests it. <see cref="EditCurrentImageCommand"/> piggybacks on this
    /// same hook (round-1 plan-review finding) -- <see cref="_editState"/> is a plain field, not
    /// observable, so nothing else would ever re-evaluate its own CanExecute; this fires after BOTH
    /// <see cref="OnEditorApplied"/> (which just set <see cref="_editState"/>) and
    /// <see cref="OnEditorCancelled"/>, since both set <see cref="IsEditorOpen"/> =
    /// <see langword="false"/>.</summary>
    partial void OnIsEditorOpenChanged(bool value)
    {
        QuickSelectModeCommand.NotifyCanExecuteChanged();
        EditCurrentImageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanChangeSourceOrMode));
    }

    [RelayCommand]
    private async Task RefreshStockLibraryAsync()
    {
        Log.RefreshStockLibraryInvoked(_logger);
        IReadOnlyList<StockImageEntry> entries;
        try
        {
            entries = await _stockLibrary.ListAsync();
        }
        catch (Exception ex)
        {
            Log.ListStockEntriesFailed(_logger, ex);
            return; // Best-effort -- see constructor's own comment.
        }

        var withThumbnails = new List<StockEntryViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            Bitmap? thumbnail = null;
            try
            {
                var image = await _stockLibrary.LoadThumbnailAsync(entry, StockThumbnailMaxDimension);
                thumbnail = ImageSourceBitmapConverter.ToBitmap(image);
            }
            catch (Exception ex)
            {
                // A missing/corrupt file for one entry must not blank the whole strip -- see
                // RxHistoryPaneViewModel.RefreshAsync's identical reasoning.
                Log.LoadStockThumbnailFailed(_logger, entry.FileName, ex);
            }

            withThumbnails.Add(new StockEntryViewModel(entry, thumbnail, SelectStockImageCommand));
        }

        StockEntries.Clear();
        foreach (var item in withThumbnails)
        {
            StockEntries.Add(item);
        }
    }

    [RelayCommand]
    private async Task SelectImageAsync()
    {
        Log.SelectImageInvoked(_logger);
        ErrorMessage = null;
        string? path;
        try
        {
            path = await _filePickerService.PickImageFileAsync();
        }
        catch (Exception ex)
        {
            // Tier B audit finding: every sibling failure path here (OpenEditorForSourceAsync,
            // OpenEditorWithLoadedSourceAsync, EditCurrentImageAsync) sets ErrorMessage on failure --
            // this one didn't, so a picker failure (platform picker unavailable, revoked filesystem
            // access) was indistinguishable from the user simply pressing Cancel.
            Log.PickImageFileFailed(_logger, ex);
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            return;
        }

        if (path is null)
        {
            return;
        }

        await OpenEditorForSourceAsync(path, Path.GetFileName(path), BuildCurrentContactVariables(), seedLiveContact: true);
    }

    [RelayCommand]
    private async Task SelectStockImageAsync(StockImageEntry entry)
    {
        Log.SelectStockImageInvoked(_logger, entry.FileName);
        ErrorMessage = null;
        await OpenEditorForSourceAsync(entry, entry.FileName, BuildCurrentContactVariables(), seedLiveContact: true);
    }

    /// <summary>ui_transition_plan.md step 5 (T1-6). Deliberately a settable delegate PROPERTY, not
    /// an event -- same "genuine request/response the command reads before continuing" reasoning as
    /// <see cref="RxHistoryPaneViewModel.ConfirmRequested"/>'s own doc comment, since this VM has no
    /// reference to <see cref="RxImagePaneViewModel"/> and never should (sibling panes, both
    /// coordinated by <c>MainViewModel</c>/<c>MainWindow.axaml.cs</c> only). Set exactly once, by
    /// <c>MainWindow.axaml.cs</c>, to read <c>RxImagePaneViewModel.OverrideCallsign</c>/
    /// <c>LookupGrid</c> -- the SAME two values the Logbook's own "Log QSO" prefill already trusts
    /// as "the received station's callsign/grid" (see <c>LogbookPaneViewModel.PrefillForNewEntry</c>).
    /// Returns <see langword="null"/> for either half (or is unwired entirely) when unwired/unknown
    /// -- Copy-to-TX must still work with no HIS CALL/HIS GRID auto-fill in that case, not fail.
    /// </summary>
    public Func<(string? Callsign, string? Grid)>? CurrentContactRequested { get; set; }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 1. Set once by <c>MainWindow.axaml.cs</c>,
    /// same "settable delegate property" shape as <see cref="CurrentContactRequested"/> -- this VM has
    /// no reference to <c>MainViewModel.SelectedTabIndex</c> and never should. Invoked from
    /// <see cref="CopyReceivedImageToTxAsync"/> on both the claim-success and claim-refused paths, so
    /// the operator always lands on the Transmit tab -- their in-progress edit if the claim was
    /// refused, the fresh one otherwise.</summary>
    public Action? RequestTransmitTabFocus { get; set; }

    /// <summary>Macros help plan (2026-09-01), item B. Same "settable delegate property" shape as
    /// <see cref="RequestTransmitTabFocus"/> above, set once by <c>MainWindow.axaml.cs</c> to
    /// <c>MainViewModel.OpenMacrosReferenceCommand</c> -- reuses that command's existing resolve-
    /// and-show logic as-is. Named <c>RequestMacrosReference</c>, not <c>MacrosReferenceRequested</c>
    /// (plan-review finding): <c>MainViewModel</c> already has its OWN pre-existing
    /// <c>MacrosReferenceRequested</c> event for a different purpose (constructing/showing the actual
    /// window); the two would sit ~15 lines apart in <c>MainWindow.axaml.cs</c> and read as the same
    /// thing. Threaded into <see cref="TxImageEditorPaneViewModel"/>'s constructor as a CLOSURE at
    /// both real construction call sites, not this property's captured value -- see that
    /// constructor's own parameter doc comment for why a captured value would risk a permanently
    /// dead button.</summary>
    public Action? RequestMacrosReference { get; set; }

    /// <summary>RX pane's "Copy to TX" stub (legacy precedent: <c>fileview.cpp</c>'s
    /// <c>CopyRectBitmap(pBitmapTXM)</c> -- copies the received bitmap into the TX slot as a fresh
    /// base image, not an overlay). Distinct from the already-shipped <c>AddLastRxImage</c> (the "+
    /// IMAGE" flyout's "Last RX" source, INSIDE an in-progress edit, inserted as an overlay element)
    /// -- this one replaces/opens the editor itself, same as Browse/Stock/Ready Rack. Same
    /// "nothing received yet" guard as <c>AddLastRxImage</c> (that command's own doc comment covers
    /// why the buffer's 1x1 black placeholder default needs an explicit check, and why this is a
    /// body-level no-op rather than a CanExecute gate -- no lifecycle hook to unsubscribe from
    /// <see cref="IReceivedImageBuffer.Updated"/> from this VM).
    ///
    /// ui_transition_plan.md step 5 (T1-6): also seeds the new editor's HIS CALL/HIS GRID template
    /// variables via <see cref="BuildCurrentContactVariables"/> (Fable UX-review finding, 2026-08-30:
    /// every "open the editor" entry point now gets this seed, not just this one -- see that
    /// method's own doc comment for why). This is what makes a template referencing
    /// {his_call}/{his_grid} come up already filled with the received station's own identity,
    /// instead of the operator retyping what the RX pane (or Logbook's own "Log QSO" prefill)
    /// already knows.</summary>
    [RelayCommand]
    private async Task CopyReceivedImageToTxAsync()
    {
        Log.CopyReceivedImageInvoked(_logger);
        ErrorMessage = null;
        // Round-1 code-review finding: reads _receivedImageBuffer.Current exactly once -- a second,
        // separate read here (e.g. re-reading it for OpenEditorWithLoadedSourceAsync below) could
        // race a concurrent decode-restart swapping in a fresh 1x1 placeholder BETWEEN the two
        // reads, silently bypassing the guard just below with a blank editor.
        var current = _receivedImageBuffer.Current;
        if (current is { Width: <= 1, Height: <= 1 })
        {
            return;
        }

        RequestTransmitTabFocus?.Invoke();

        if (!TryClaimEditorSlotForNewSource())
        {
            return;
        }

        var fileName = _localization.GetString("Panes.TxControls.CopyToTx.FileName");
        await OpenEditorWithLoadedSourceAsync(current, fileName, BuildCurrentContactVariables(), seedLiveContact: true);
    }

    /// <summary>Shared by every "open the editor" entry point (Copy-to-TX, Browse, Stock, Blank) --
    /// builds the seed dictionary for {his_call}/{his_grid} template variables from the current RX
    /// contact, if any. A blank/whitespace-only value stays UNSEEDED, not seeded as "" -- code-review
    /// finding preserved from this method's original Copy-to-TX-only form: MacroTextResolver
    /// resolves a present-but-empty variable to "" (token vanishes), while an absent one resolves
    /// verbatim to "{his_call}" (an obvious unfilled placeholder). Seeding "" would silently blank
    /// the token on the transmitted card.
    ///
    /// Fable UX-review finding, 2026-08-30: originally only <see cref="CopyReceivedImageToTxAsync"/>
    /// called this (this method's own doc comment used to argue "an arbitrary photo or a blank
    /// canvas isn't 'replying to a station'"). Live testing found that reasoning doesn't match actual
    /// use: this pane's own <see cref="OpenBlankEditorAsync"/> doc comment documents "Load a Ready
    /// Rack/Template Library entry" as the intended way to reach a template after opening blank --
    /// picking a reply template from the Ready Rack is at least as common a "replying to a station"
    /// path as Copy-to-TX, and QSO FILL fields stay freely editable regardless of how they were
    /// seeded. Extended to every entry point; when nothing has been received,
    /// <see cref="CurrentContactRequested"/> returns null/empty and this still correctly returns
    /// null, so Browse/Stock/Blank behavior is unchanged in the common cold-start case.</summary>
    private Dictionary<string, string>? BuildCurrentContactVariables()
    {
        var (callsign, grid) = CurrentContactRequested?.Invoke() ?? (null, null);
        Dictionary<string, string>? contactVariables = null;
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            (contactVariables ??= new Dictionary<string, string>(StringComparer.Ordinal))["his_call"] = callsign.Trim();
        }

        if (!string.IsNullOrWhiteSpace(grid))
        {
            (contactVariables ??= new Dictionary<string, string>(StringComparer.Ordinal))["his_grid"] = grid.Trim();
        }

        return contactVariables;
    }

    /// <summary>Shared gate for every "load a fresh source into the editor" entry point (Browse,
    /// Stock, Copy-to-TX) -- see <see cref="OpenEditorForSourceAsync"/>'s own doc comment for the
    /// blank-editor-replace-or-refuse reasoning this centralizes. Returns <see langword="false"/>
    /// (a silent no-op for the caller) when there's no target mode selected yet, or a genuinely
    /// in-progress edit refuses replacement; otherwise claims the slot (<see cref="IsEditorOpen"/>
    /// true, <see cref="_currentEditorIsBlank"/> false) and returns <see langword="true"/>.</summary>
    private bool TryClaimEditorSlotForNewSource()
    {
        if (SelectedMode is not { })
        {
            return false;
        }

        if (IsEditorOpen)
        {
            if (!IsCurrentEditorBlankAndUntouched())
            {
                return false;
            }

            CloseBlankEditorForReplacement();
        }

        IsEditorOpen = true;
        _currentEditorIsBlank = false;
        return true;
    }

    /// <summary><paramref name="source"/> is either a <see cref="StockImageEntry"/> or a
    /// <see cref="string"/> file path. Loads it at native resolution and hands the resulting
    /// <see cref="TxImageEditorPaneViewModel"/> to whoever is listening on <see cref="EditorOpened"/> --
    /// this VM does not touch Dock/window placement itself.
    /// <para>Auditor-found regression (2026-08-17): with the editor now ALWAYS open by default, a
    /// bare <c>IsEditorOpen</c> guard made Browse/STOCK permanently unreachable after the very
    /// first blank auto-open -- there was no way to ever load a real photo. Relaxed the same way
    /// mode-select already is: a BLANK/untouched editor gets closed (without its own auto-reopen
    /// side effect, see <see cref="CloseBlankEditorForReplacement"/>) and replaced by the real
    /// pick; a genuinely in-progress edit still refuses, exactly as before.</para>
    /// <para>RX/TX pipeline fix plan (2026-09-01), item 3, auditor round 2's blocker B:
    /// <paramref name="contactVariables"/> is REQUIRED, not an optional parameter defaulting to
    /// <see cref="BuildCurrentContactVariables"/> -- an optional default can't distinguish "caller
    /// explicitly wants no contact seeded" from "caller didn't specify," which would silently leak
    /// the live RX pane's contact onto a source that has nothing to do with it (e.g. a Gallery entry
    /// with no linked QSO). Every caller passes its own explicit value: Browse/Stock (below) pass
    /// <see cref="BuildCurrentContactVariables"/> themselves, exactly as this method used to do
    /// internally; <see cref="OpenEditorForExternalFileAsync"/> passes its own looked-up value, or
    /// <see langword="null"/> meaning truly nothing.
    /// <para>Ready Rack direct-fire plan (2026-09-01), code-review finding: <paramref name="seedLiveContact"/>
    /// is a SEPARATE required parameter, NOT derived from whether <paramref name="contactVariables"/>
    /// happens to be non-null at this particular call. <see cref="BuildCurrentContactVariables"/>
    /// returns <see langword="null"/> whenever no station has been decoded YET (the common cold-start
    /// case for Browse/Stock/Blank/Copy-to-TX) -- deriving live-tracking intent from that one-shot
    /// value's nullness would have left the direct-fire feature's whole re-seed capability dead for
    /// any editor opened before the first contact of a session, exactly the same "can't distinguish
    /// 'explicitly none' from 'happens to be none right now'" conflation the REQUIRED
    /// <paramref name="contactVariables"/> parameter above already exists to avoid. Browse/Stock/
    /// Blank/Copy-to-TX always pass <see langword="true"/> (they want ongoing live-tracking
    /// regardless of whether a contact exists yet); <see cref="OpenEditorForExternalFileAsync"/>
    /// always passes <see langword="false"/> (a Gallery-sourced image must never live-track the RX
    /// pane, matching its own <paramref name="contactVariables"/> intent).</para>
    /// <para>Returns <see langword="false"/> when <see cref="TryClaimEditorSlotForNewSource"/>
    /// refuses (no target mode selected, or a genuinely in-progress edit) -- nothing else happened,
    /// same "silent no-op" contract that method's own doc comment states. Returns
    /// <see langword="true"/> once the slot is claimed, even if the load itself subsequently fails
    /// (that failure surfaces via <see cref="ErrorMessage"/>/<see cref="EditorClosed"/> already, a
    /// caller doesn't need a second signal for it) -- <see cref="OpenEditorForExternalFileAsync"/>
    /// uses this to distinguish "should I switch the operator to the Transmit tab" from "should I
    /// surface a refusal on my own caller's error surface instead."</para></summary>
    private async Task<bool> OpenEditorForSourceAsync(object source, string fileName, IReadOnlyDictionary<string, string>? contactVariables, bool seedLiveContact)
    {
        // User-requested (2026-09-15): "even if there are elements on the canvas, if no background
        // has been picked before, i should be able to load one later also, not only as first canvas
        // element." When the currently open editor has no real background yet, install the picked
        // photo directly into THAT editor instead of discarding it for a brand-new one below --
        // OverlayElements/adjustments/crop are already completely independent of the background (see
        // TxImageEditorPaneViewModel.LoadBackground's own doc comment), so there is nothing to lose.
        // contactVariables/seedLiveContact are deliberately NOT reapplied here -- the already-open
        // editor's own contact-variable seeding (from however it was originally opened) stays as-is;
        // reseeding on a background swap alone isn't what this fixes.
        if (IsEditorOpen && _currentEditor is { HasRealBackground: false } editor)
        {
            IImageSource inPlaceSource;
            try
            {
                inPlaceSource = await LoadOriginalSourceAsync(source);
            }
            catch (Exception ex)
            {
                // Unlike the wholesale-replace failure path below, a failed in-place background load
                // must NOT touch the editor at all -- there's a real, otherwise-untouched session
                // (elements, adjustments, crop) still sitting there the operator hasn't lost yet.
                Log.LoadTxSourceImageFailed(_logger, fileName, ex);
                ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
                return true;
            }

            editor.LoadBackground(inPlaceSource);
            return true;
        }

        if (!TryClaimEditorSlotForNewSource())
        {
            return false;
        }

        IImageSource original;
        try
        {
            original = await LoadOriginalSourceAsync(source);
        }
        catch (Exception ex)
        {
            // Tier B audit finding: CloseBlankEditorForReplacement/OnEditorCancelled both null
            // _currentEditor/_currentEditorIsBlank and fire EditorClosed on every close -- this
            // failure path (and the two other construction-failure catches below) didn't, leaving
            // MainViewModel.ActiveEditor (set only by EditorOpened/EditorClosed) stale at whatever
            // editor was active before, out of sync with IsEditorOpen now being false.
            Log.LoadTxSourceImageFailed(_logger, fileName, ex);
            IsEditorOpen = false;
            _currentEditor?.Dispose();
            _currentEditor = null;
            _currentEditorIsBlank = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            EditorClosed?.Invoke();
            return true;
        }

        await OpenEditorWithLoadedSourceAsync(original, fileName, contactVariables, seedLiveContact);
        return true;
    }

    /// <summary><paramref name="source"/> is either a <see cref="StockImageEntry"/> or a
    /// <see cref="string"/> file path -- extracted so <see cref="OpenEditorForSourceAsync"/>'s own
    /// in-place-load branch and its ordinary wholesale-replace path share the exact same loading
    /// logic instead of two independently-maintained copies.</summary>
    private Task<IImageSource> LoadOriginalSourceAsync(object source) => source switch
    {
        StockImageEntry stockEntry => _stockLibrary.LoadOriginalAsync(stockEntry),
        string path => _imageFileLoader.LoadOriginalAsync(path),
        _ => throw new InvalidOperationException($"Unrecognized TX source type: {source.GetType()}"),
    };

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 3: public entry point for the RX Gallery's
    /// "Send to TX" -- the only other caller of <see cref="OpenEditorForSourceAsync"/> outside this
    /// class. Reuses that method rather than duplicating its slot-claim/4-failure-path logic (it
    /// would be ~95% a copy). <paramref name="contactVariables"/> is the caller's own explicit value
    /// (typically looked up from a linked QSO, or <see langword="null"/> when the RX-history entry
    /// has none) -- see <see cref="OpenEditorForSourceAsync"/>'s own doc comment for why this can't
    /// default to <see cref="BuildCurrentContactVariables"/> here. Always passes
    /// <c>seedLiveContact: false</c> -- a Gallery-sourced image must never live-track the RX pane's
    /// CURRENT contact regardless of what <paramref name="contactVariables"/> happened to snapshot at
    /// open time (see <see cref="OpenEditorForSourceAsync"/>'s own doc comment for why this is a
    /// separate signal from that one-shot value).
    /// <para>Unlike <see cref="CopyReceivedImageToTxAsync"/>, this doesn't share a call path with
    /// Copy-to-TX (that one passes an already-loaded <see cref="IImageSource"/>, this one passes a
    /// file path) -- <see cref="RequestTransmitTabFocus"/> is invoked here too, not assumed to come
    /// free from a shared method. Returns whether the slot claim succeeded, so the Gallery pane can
    /// surface a refusal on its own <c>ErrorMessage</c> (it has no view of
    /// <see cref="CanChangeSourceOrMode"/> to gate an <c>IsEnabled</c> binding on the way Copy-to-TX's
    /// button does).</para></summary>
    public async Task<bool> OpenEditorForExternalFileAsync(string filePath, IReadOnlyDictionary<string, string>? contactVariables)
    {
        var opened = await OpenEditorForSourceAsync(filePath, Path.GetFileName(filePath), contactVariables, seedLiveContact: false);
        if (opened)
        {
            RequestTransmitTabFocus?.Invoke();
        }

        return opened;
    }

    /// <summary>Backlog fix (user request, 2026-08-17): "don't leave the TX window completely empty
    /// until you load an image" -- opens the editor immediately with a solid neutral-gray
    /// <see cref="BlankImageSource"/> placeholder, sized to the target mode's own frame dimensions,
    /// instead of requiring Browse/Stock first. The operator can then Load a Ready Rack/Template
    /// Library entry, or use Browse/Stock inside the editor's own STOCK panel, to swap in a real
    /// photo -- both already-existing paths, unchanged by this. No RX-history dependency (a
    /// deliberate, simpler choice over auto-loading the last received image -- user's own call).</summary>
    [RelayCommand]
    private async Task OpenBlankEditorAsync()
    {
        // Same runtime guard as OpenEditorForSourceAsync's own -- re-entrancy is prevented at the
        // View level (IsEnabled bound to CanLoadNewSource, matching Browse/Stock's own established
        // convention, not a [RelayCommand(CanExecute=...)] gate) but this guard stays as the real
        // backstop, same reasoning as the sibling method. Also the reopen target
        // OnSelectedModeChanged calls right after CloseBlankEditorForReplacement -- relaxed the
        // same way (2026-08-17 auditor-found regression fix) so a direct "Open editor" click also
        // works while an already-blank editor is open (a harmless blank-replaces-blank no-op-ish
        // case), not just while fully closed.
        if (SelectedMode is not { } mode)
        {
            return;
        }

        if (IsEditorOpen)
        {
            if (!IsCurrentEditorBlankAndUntouched())
            {
                return;
            }

            CloseBlankEditorForReplacement();
        }

        IsEditorOpen = true;
        _currentEditorIsBlank = true;
        var placeholder = new BlankImageSource(mode.ImageWidth, mode.ImageHeight, BlankImageSource.DefaultColor);
        await OpenEditorWithLoadedSourceAsync(
            placeholder, _localization.GetString("Panes.TxControls.BlankImageName"), BuildCurrentContactVariables(), seedLiveContact: true);
    }

    private async Task OpenEditorWithLoadedSourceAsync(IImageSource original, string fileName, IReadOnlyDictionary<string, string>? currentContactVariables, bool seedLiveContact)
    {
        // SelectedMode may have changed while the original was loading -- always target whatever
        // mode is current NOW, not the one in effect when the pick started.
        if (SelectedMode is not { } mode)
        {
            IsEditorOpen = false;
            return;
        }

        // Code-review finding on spec/18-path-to-1.0.md High item 2: this whole block used to sit
        // outside any try/catch. Since IsEditorOpen now also gates the mode ComboBox/favorite
        // buttons/RX auto-follow (not just re-entrant picking), an unhandled throw here (a corrupt
        // settings file, an oversized image blowing up BuildWorkingCopy/ToBitmap inside the editor's
        // own constructor) would leave IsEditorOpen stuck true forever -- with no editor ever having
        // opened, there is no Cancel button to recover with.
        try
        {
            // Loaded fresh here rather than cached at construction -- the operator may have edited
            // Options (Callsign/Name/Grid) at any point before opening the editor.
            var operatorSettings = (await _settingsStore.LoadAsync())
                .GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
                ?? new OperatorSettings();

            var editor = new TxImageEditorPaneViewModel(
                original, mode, _preparer, _macroTextResolver, operatorSettings, _radioSession, _localization, _imageEditorLogger,
                _filePickerService, _imageFileLoader, _receivedImageBuffer, _receiveHistoryStore,
                _templateStore, _imageSourceWriter, new ReadyRackViewModel(_templateStore, _settingsStore, _localization, _filePickerService, _readyRackLogger),
                canTransmitNow: () => !IsTransmitting && !IsRunningLoopbackSelfTest,
                currentContactVariables: currentContactVariables,
                macrosReferenceRequested: () => RequestMacrosReference?.Invoke(),
                // Ready Rack direct-fire plan (2026-09-01), code-review finding: thread the CALLER's
                // own explicit seedLiveContact intent, NOT currentContactVariables' own null-ness --
                // BuildCurrentContactVariables() returns null whenever no station has been decoded
                // YET (the common cold-start case), so deriving live-tracking intent from that
                // one-shot value's nullness would have left this feature's whole re-seed capability
                // dead for any editor opened before the first contact of a session. seedLiveContact
                // is threaded explicitly by every caller instead -- see OpenEditorForSourceAsync's
                // own doc comment for the full reasoning.
                currentContactProvider: seedLiveContact ? BuildCurrentContactVariables : null);
            editor.Applied += final => OnEditorApplied(fileName, editor, final);
            editor.AppliedAndTransmitRequested += final => OnEditorAppliedAndTransmit(fileName, editor, final);
            editor.DirectFireRequested += final => OnEditorDirectFire(fileName, editor, final);
            editor.Cancelled += OnEditorCancelled;
            editor.PropertyChanged += OnCurrentEditorPropertyChanged;
            _currentEditor = editor;
            // Real bug caught via real-window testing (2026-08-17): IsEditorOpen already toggled
            // true BEFORE this await-gated assignment runs (OnIsEditorOpenChanged already fired,
            // re-evaluating IsCurrentEditorBlankAndUntouched() while _currentEditor was still null
            // -- always false at that instant, regardless of whether this editor turns out to be
            // blank). Nothing else re-notifies once _currentEditor actually gets attached, so
            // QuickSelectMode's CanExecute and CanChangeSourceOrMode's own
            // bindable value would silently stay stuck at their pre-open (disabled) reading forever
            // -- confirmed live: the mode ComboBox stayed visibly greyed out after Cancel
            // auto-reopened a fresh blank editor, even though the underlying state was correct.
            QuickSelectModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanChangeSourceOrMode));
            EditorOpened?.Invoke(editor);
            _ = editor.ReadyRack.RefreshAsync();
        }
        catch (Exception ex)
        {
            // Tier B audit finding: see OpenEditorForSourceAsync's own load-failure catch for why --
            // same fix here.
            Log.OpenTxEditorFailed(_logger, fileName, ex);
            IsEditorOpen = false;
            _currentEditor?.Dispose();
            _currentEditor = null;
            _currentEditorIsBlank = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            EditorClosed?.Invoke();
        }
    }

    private void OnEditorApplied(string fileName, TxImageEditorPaneViewModel editor, IImageSource final)
    {
        if (!ReferenceEquals(editor, _currentEditor)) return;
        // editor.CurrentSource, NOT the pre-rotation IImageSource this method used to receive as
        // its own "original" parameter -- round-1 plan-review finding on spec/18-path-to-1.0.md
        // High item 3: capturing the closure's original pre-rotation reference here meant a
        // rotate performed in the editor was silently discarded the next time OnSelectedModeChanged
        // re-derived _loadedImage from this EditState on a later mode change -- Apply's own
        // immediate output was correct (used the editor's own live rotated source), but the
        // re-derivation reverted to the unrotated image while still applying the ROTATED crop
        // rect/overlay coordinates to it. CurrentSource always reflects every Rotate call so far.
        _editState = new EditState(editor.CurrentSource, editor.CropRect, editor.PreserveAspect, editor.Document, editor.Adjustments, editor.RawOverlayElements, editor.TemplateVariables);
        _loadedImage = final;
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(final);
        SelectedFileName = fileName;
        ErrorMessage = null;
        IsEditorOpen = false;
        _currentEditor?.Dispose();
        _currentEditor = null;
        _currentEditorIsBlank = false;
        TransmitCommand.NotifyCanExecuteChanged();
        EditorClosed?.Invoke();
    }

    /// <summary>ui_transition_plan.md step 2 (T1-2): the SEND row's "Apply &amp; Transmit" chain --
    /// closes the editor exactly like a plain Apply (<see cref="OnEditorApplied"/>), then starts
    /// the transmission itself. Re-checks <see cref="CanTransmit"/> rather than trusting the
    /// editor's own (necessarily slightly stale) gate: OnEditorApplied above already ran by the
    /// time this checks, so _loadedImage is guaranteed non-null here, but IsTransmitting/
    /// IsRunningLoopbackSelfTest could in principle have flipped true between the button click and
    /// this handler running (both are UI-thread-only today, so not reachable in practice, but the
    /// guard costs nothing and avoids depending on that staying true).</summary>
    private void OnEditorAppliedAndTransmit(string fileName, TxImageEditorPaneViewModel editor, IImageSource final)
    {
        if (!ReferenceEquals(editor, _currentEditor)) return;
        OnEditorApplied(fileName, editor, final);
        if (TransmitCommand.CanExecute(null))
        {
            TransmitCommand.Execute(null);
        }
    }

    /// <summary>Ready Rack direct-fire plan (2026-09-01): the SAME close+transmit steps
    /// <see cref="OnEditorAppliedAndTransmit"/> does, PLUS an immediate reopen afterward -- the
    /// editor (and the Ready Rack living inside it) closing with nothing reopening it would end the
    /// pileup loop this whole feature exists for after exactly one fire (round-1 plan-review
    /// blocker). The reopen goes through the SAME <see cref="ReopenEditorFromCurrentStateAsync"/>
    /// path <see cref="EditCurrentImageAsync"/> uses (real photo + just-fired overlay both carry
    /// forward -- a template load only ever replaces the OVERLAY, never the photo, so the NEXT
    /// Ctrl+N correctly swaps only the overlay).
    ///
    /// Code-review finding: the reopen INHERITS <paramref name="editor"/>'s own
    /// <see cref="TxImageEditorPaneViewModel.CurrentContactProvider"/> -- it does NOT unconditionally
    /// pass <see cref="BuildCurrentContactVariables"/>. A Gallery-sourced editor (opened with a
    /// <see langword="null"/> provider specifically so the live RX contact never leaks onto a source
    /// with no linked QSO) must stay unseeded through every subsequent reopen in its own pileup-fire
    /// chain, not just its first fire -- unconditionally re-arming a live provider on reopen would
    /// reintroduce that exact leak, just delayed by one fire. An editor opened via Browse/Stock/
    /// Blank/Copy-to-TX (which DO carry a live provider from construction, see
    /// <see cref="OpenEditorWithLoadedSourceAsync"/>'s own doc comment) correctly keeps live-tracking
    /// across every reopen too, by the same inheritance. A parallel sibling to the ordinary
    /// Apply&amp;Transmit path, not a modification to it -- that path's own "close and leave empty"
    /// behavior is completely unchanged.</summary>
    private async void OnEditorDirectFire(string fileName, TxImageEditorPaneViewModel editor, IImageSource final)
    {
        if (!ReferenceEquals(editor, _currentEditor)) return;
        var contactProvider = editor.CurrentContactProvider;
        OnEditorAppliedAndTransmit(fileName, editor, final);
        await ReopenEditorFromCurrentStateAsync(contactProvider);
    }

    /// <summary>Auditor-found regression (2026-08-17, usability-gap review): with the editor now
    /// ALWAYS open by default, Browse/STOCK/mode-select all still hard-guarded on
    /// <c>IsEditorOpen</c> alone were permanently unreachable -- there was no way to load a real
    /// photo, or change SSTV mode, after the very first blank auto-open. Closes the current BLANK/
    /// untouched editor WITHOUT auto-reopening (unlike <see cref="OnEditorCancelled"/>'s own
    /// Cancel-button semantics) -- for use right before the caller immediately opens a DIFFERENT
    /// editor (a new mode's blank placeholder, or a real Browse/Stock pick). Going through
    /// <c>CancelCommand.Execute</c> instead would ALSO trigger <see cref="OnEditorCancelled"/>'s own
    /// auto-reopen-blank side effect, racing with the caller's own intended reopen (harmless when
    /// the caller ALSO wants blank, per <see cref="OnSelectedModeChanged"/>'s own reentrancy-safe
    /// double-call reasoning -- but wasteful and semantically messy when the caller wants a REAL
    /// source instead, since it would flicker the UI through an unwanted intermediate blank editor).
    /// Doesn't unsubscribe the discarded editor's own Applied/Cancelled/PropertyChanged handlers --
    /// same accepted precedent as every other editor-discard path in this class (nothing will ever
    /// invoke them again once the View swaps <c>ActiveEditor</c> to the new instance). DOES call
    /// <see cref="TxImageEditorPaneViewModel.Dispose"/> on it now (Tier-0 audit follow-up,
    /// production_audit.md) -- releases the discarded editor's own pooled bitmaps and every live
    /// element's canvas bitmap without waiting on GC/finalization; see that method's own doc
    /// comment for its UI-thread requirement, which this call site (a synchronous method on the UI
    /// thread) satisfies.</summary>
    private void CloseBlankEditorForReplacement()
    {
        IsEditorOpen = false;
        _currentEditor?.Dispose();
        _currentEditor = null;
        _currentEditorIsBlank = false;
        EditorClosed?.Invoke();
    }

    private void OnEditorCancelled()
    {
        IsEditorOpen = false;
        _currentEditor?.Dispose();
        _currentEditor = null;
        _currentEditorIsBlank = false;
        EditorClosed?.Invoke();

        // Backlog item (user request, 2026-08-17): "should ALWAYS open the editor by default" --
        // backing out via Cancel would otherwise leave the center column empty again, reintroducing
        // the exact friction this whole item was about. Re-open blank immediately, but ONLY when
        // nothing has ever been applied yet -- this must NOT fire after cancelling a re-edit of an
        // ALREADY applied image (EditCurrentImageCommand), which would silently discard the applied
        // state the operator is still meant to see/transmit. SelectedFileName is set by
        // OnEditorApplied AND by ResendSentFrame (TX history plan, 2026-09-01) -- both cases still
        // correctly represent "something is currently loaded," so this check is unaffected by the
        // second writer.
        if (SelectedFileName is null)
        {
            _ = OpenBlankEditorAsync();
        }
    }

    /// <summary>Re-evaluates <see cref="QuickSelectModeCommand"/>'s
    /// own <c>CanExecute</c> the moment the currently-open editor's <see cref="TxImageEditorPaneViewModel.HasUnsavedEdits"/>
    /// flips (typically false-&gt;true, the first real edit) -- <see cref="OnIsEditorOpenChanged"/>
    /// alone only re-evaluates on open/close, not on this finer-grained transition
    /// <see cref="IsCurrentEditorBlankAndUntouched"/> now also depends on. Also reacts to
    /// <see cref="TxImageEditorPaneViewModel.HasNoBackgroundOrOverlayElements"/> (user-reported bug,
    /// 2026-09-15: Browse/Stock/Open Editor staying greyed out after Remove Background) -- same
    /// reasoning, a DIFFERENT finer-grained transition <see cref="IsCurrentEditorBlankAndUntouched"/>
    /// now also depends on. <see cref="TxImageEditorPaneViewModel.HasRealBackground"/> gets its own
    /// separate re-raise (only <see cref="CanLoadBackground"/>, not <see cref="CanChangeSourceOrMode"/>
    /// or <see cref="QuickSelectModeCommand"/> -- see that property's own doc comment for why mode
    /// selection stays on the narrower gate) the instant <see cref="TxImageEditorPaneViewModel.LoadBackground"/>
    /// installs a real photo, so Browse/Stock re-lock immediately rather than staying visibly
    /// enabled-but-now-inert until some unrelated later notification happens to fire.</summary>
    private void OnCurrentEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TxImageEditorPaneViewModel.HasUnsavedEdits)
            || e.PropertyName == nameof(TxImageEditorPaneViewModel.HasNoBackgroundOrOverlayElements))
        {
            QuickSelectModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanChangeSourceOrMode));
            OnPropertyChanged(nameof(CanLoadBackground));
        }
        else if (e.PropertyName == nameof(TxImageEditorPaneViewModel.HasRealBackground))
        {
            OnPropertyChanged(nameof(CanLoadBackground));
        }
    }

    private bool CanEditCurrentImage() => _editState is not null && !IsEditorOpen;

    /// <summary>spec/18-path-to-1.0.md Medium item: re-open/re-edit an image after Apply -- no
    /// fresh <see cref="IImageFileLoader"/>/<see cref="IStockImageLibrary"/> I/O needed, unlike
    /// <see cref="OpenEditorForSourceAsync"/> (round-1 plan-review confirmed: <see cref="_editState"/>'s
    /// own <c>Original</c> is already fully in memory, reflecting every prior Rotate too -- see
    /// <see cref="OnEditorApplied"/>'s own doc comment on why it's captured from
    /// <c>editor.CurrentSource</c>). Still async (operator-settings reload) and still wrapped in
    /// the same construction try/catch <see cref="OpenEditorForSourceAsync"/> uses -- round-1
    /// finding: <c>BuildWorkingCopy</c>/<c>ToBitmap</c> inside the editor's own constructor can
    /// still throw, and an uncaught throw here would leave <see cref="IsEditorOpen"/> stuck
    /// <see langword="true"/> forever with no editor to Cancel, permanently freezing
    /// <see cref="SelectedMode"/>/quick-grid/RX auto-follow (see
    /// <see cref="IsEditorOpen"/>'s own doc comment).</summary>
    [RelayCommand(CanExecute = nameof(CanEditCurrentImage))]
    private async Task EditCurrentImageAsync() =>
        // Ready Rack direct-fire plan (2026-09-01): thin wrapper -- passes no contact provider, same
        // exact behavior as before this feature existed. See ReopenEditorFromCurrentStateAsync's own
        // doc comment for why the ordinary manual "Edit Image" click must never leak the live RX
        // contact into an unrelated re-edit.
        await ReopenEditorFromCurrentStateAsync(currentContactProvider: null);

    /// <summary>spec/18-path-to-1.0.md Medium item: re-open/re-edit an image after Apply -- no
    /// fresh <see cref="IImageFileLoader"/>/<see cref="IStockImageLibrary"/> I/O needed, unlike
    /// <see cref="OpenEditorForSourceAsync"/> (round-1 plan-review confirmed: <see cref="_editState"/>'s
    /// own <c>Original</c> is already fully in memory, reflecting every prior Rotate too -- see
    /// <see cref="OnEditorApplied"/>'s own doc comment on why it's captured from
    /// <c>editor.CurrentSource</c>). Still async (operator-settings reload) and still wrapped in
    /// the same construction try/catch <see cref="OpenEditorForSourceAsync"/> uses -- round-1
    /// finding: <c>BuildWorkingCopy</c>/<c>ToBitmap</c> inside the editor's own constructor can
    /// still throw, and an uncaught throw here would leave <see cref="IsEditorOpen"/> stuck
    /// <see langword="true"/> forever with no editor to Cancel, permanently freezing
    /// <see cref="SelectedMode"/>/quick-grid/RX auto-follow (see
    /// <see cref="IsEditorOpen"/>'s own doc comment).
    ///
    /// Ready Rack direct-fire plan (2026-09-01): extracted from <see cref="EditCurrentImageAsync"/>'s
    /// own former body (now a thin wrapper passing <see langword="null"/>) so
    /// <see cref="OnEditorDirectFire"/>'s own post-fire reopen can reuse this EXACT construction
    /// logic while ALSO passing a live <paramref name="currentContactProvider"/> -- the ordinary
    /// manual "Edit Image" click keeps its precise prior behavior (no live-contact leak introduced by
    /// this feature), only a direct-fire-triggered reopen gets the freshness capability.</summary>
    private async Task ReopenEditorFromCurrentStateAsync(Func<IReadOnlyDictionary<string, string>?>? currentContactProvider)
    {
        // CanExecute alone isn't a hard gate -- CommunityToolkit's IAsyncRelayCommand.ExecuteAsync
        // doesn't consult it, only Avalonia's Button.OnClick does (code-review finding, same
        // doctrine QuickSelectMode already follows above). This body-level
        // IsEditorOpen check is the real backstop -- without it, a direct ExecuteAsync call while
        // an editor is already open would construct a SECOND editor and orphan the first (the
        // first's own Applied/Cancelled handlers would still fire into a now-stale closure).
        if (_editState is not { } edit || SelectedMode is not { } mode || IsEditorOpen)
        {
            Log.EditCurrentImageWithNoEditState(_logger);
            return;
        }

        IsEditorOpen = true;
        _currentEditorIsBlank = false;
        ErrorMessage = null; // stale error from a prior failed pick/edit must not linger through this one
        try
        {
            var operatorSettings = (await _settingsStore.LoadAsync())
                .GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
                ?? new OperatorSettings();

            var editor = new TxImageEditorPaneViewModel(
                edit.Original, mode, _preparer, _macroTextResolver, operatorSettings, _radioSession, _localization, _imageEditorLogger,
                _filePickerService, _imageFileLoader, _receivedImageBuffer, _receiveHistoryStore,
                _templateStore, _imageSourceWriter, new ReadyRackViewModel(_templateStore, _settingsStore, _localization, _filePickerService, _readyRackLogger),
                new TxImageEditorPaneViewModel.EditorInitialState(edit.CropRect, edit.PreserveAspect, edit.Adjustments, edit.RawOverlay, edit.TemplateVariables),
                canTransmitNow: () => !IsTransmitting && !IsRunningLoopbackSelfTest,
                macrosReferenceRequested: () => RequestMacrosReference?.Invoke(),
                currentContactProvider: currentContactProvider);
            // SelectedFileName! is safe here: OnEditorApplied always writes it in the same
            // assignment that sets _editState. ResendSentFrame (TX history plan, 2026-09-01) is the
            // only other writer, and it always nulls _editState in that same assignment too -- so
            // _editState being non-null at this point (the guard above) still guarantees
            // SelectedFileName was set by an OnEditorApplied, never left stale by a resend.
            var fileName = SelectedFileName!;
            editor.Applied += final => OnEditorApplied(fileName, editor, final);
            editor.AppliedAndTransmitRequested += final => OnEditorAppliedAndTransmit(fileName, editor, final);
            editor.DirectFireRequested += final => OnEditorDirectFire(fileName, editor, final);
            editor.Cancelled += OnEditorCancelled;
            editor.PropertyChanged += OnCurrentEditorPropertyChanged;
            _currentEditor = editor;
            // Real bug caught via real-window testing (2026-08-17): IsEditorOpen already toggled
            // true BEFORE this await-gated assignment runs (OnIsEditorOpenChanged already fired,
            // re-evaluating IsCurrentEditorBlankAndUntouched() while _currentEditor was still null
            // -- always false at that instant, regardless of whether this editor turns out to be
            // blank). Nothing else re-notifies once _currentEditor actually gets attached, so
            // QuickSelectMode's CanExecute and CanChangeSourceOrMode's own
            // bindable value would silently stay stuck at their pre-open (disabled) reading forever
            // -- confirmed live: the mode ComboBox stayed visibly greyed out after Cancel
            // auto-reopened a fresh blank editor, even though the underlying state was correct.
            QuickSelectModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanChangeSourceOrMode));
            EditorOpened?.Invoke(editor);
            _ = editor.ReadyRack.RefreshAsync();
        }
        catch (Exception ex)
        {
            // Tier B audit finding: see OpenEditorForSourceAsync's own load-failure catch for why --
            // same fix here.
            Log.OpenTxEditorFailed(_logger, SelectedFileName ?? "(re-edit)", ex);
            IsEditorOpen = false;
            _currentEditor?.Dispose();
            _currentEditor = null;
            _currentEditorIsBlank = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            EditorClosed?.Invoke();
        }
    }

    /// <summary>Deliberately does NOT also require <c>!IsEditorOpen</c> -- confirmed sound (code
    /// review, spec/18-path-to-1.0.md High item 2), not just assumed: <see cref="_loadedImage"/> is
    /// only ever written sized to whatever <see cref="SelectedMode"/> was at that moment
    /// (<see cref="OnEditorApplied"/> uses the editor's own target mode; <see cref="OnSelectedModeChanged"/>
    /// re-flows to the new mode), and <see cref="IsEditorOpen"/> now freezes <see cref="SelectedMode"/>
    /// for its whole lifetime -- so <c>_loadedImage</c>'s dimensions can never diverge from
    /// <c>SelectedMode</c>'s while an editor is open, even mid-edit. This invariant is load-bearing:
    /// don't let <see cref="SelectedMode"/> become mutable again while <see cref="IsEditorOpen"/>
    /// without re-checking it -- <see cref="ResendSentFrame"/> (TX history plan, 2026-09-01) is the
    /// one other writer of <see cref="SelectedMode"/> besides the mode picker/quick-grid, and its own
    /// <see cref="CanResendSentFrame"/> gate does exactly that re-check (2-round plan-review finding:
    /// the first draft omitted it, which broke this exact invariant the moment a real, non-blank
    /// editor was open targeting a different mode than the resent entry).</summary>
    // Code-review round-1 finding: must also check !IsRunningLoopbackSelfTest -- a self-test's
    // encode+decode is real CPU work competing with a live PTT-keyed playback pump, and its own
    // result dialog is MODAL, so letting a transmit start while one is running risked a dialog
    // popping up mid-transmission and blocking Stop TX. Mirrored on the Application-layer side too
    // (SstvSessionService.PlayWithPttAsync now cross-checks _loopbackSelfTestInFlight).
    private bool CanTransmit() => _loadedImage is not null && !IsTransmitting && !IsRunningLoopbackSelfTest;

    [RelayCommand(CanExecute = nameof(CanTransmit))]
    private async Task TransmitAsync()
    {
        if (_loadedImage is not { } image || SelectedMode is not { } mode)
        {
            return;
        }

        Log.TransmitInvoked(_logger, mode.Id);
        ErrorMessage = null;
        _cutoffTriggered = false;
        _consecutiveSwrOverThreshold = 0;
        IsTransmitting = true;
        // Code-review finding: reset BOTH -- _transmitRemaining isn't its own ObservableProperty
        // (see its own doc comment), so leaving it at a previous transmission's last value would
        // show a stale "remaining" figure in TransmitProgressText for the brief window between TX
        // start and the first real progress report.
        _transmitRemaining = TimeSpan.Zero;
        _transmitElapsed = TimeSpan.Zero;
        TransmitProgress = 0;
        _anyProgressReported = false;
        TransmitCommand.NotifyCanExecuteChanged();
        StopTransmitCommand.NotifyCanExecuteChanged();
        RunLoopbackSelfTestCommand.NotifyCanExecuteChanged();
        _currentEditor?.NotifyTransmitAvailabilityChanged();

        // TX history plan: snapshotted HERE, before the try, not read from SelectedFileName/
        // RadioStatus inside finally -- plan-review finding. The editor stays usable and the rig
        // keeps reporting for the whole duration of a transmission that can run minutes; reading
        // these in finally would record whatever an Apply mid-transmit or the rig's post-transmit
        // frequency happened to be, not what was actually true when THIS transmission started.
        var sentFrameOutcome = TxHistoryOutcome.Completed;
        var sentFrameSourceFileName = SelectedFileName;
        var sentFrameFrequencyHz = RadioStatus?.CurrentFrequencyHz;
        // Local, not UTC -- code-review finding: the AXAML renders this with a bare HH:mm:ss (no
        // "Z"/UTC marker), same as RxImagePaneViewModel.PreviousFrames' own local-time stamp this
        // strip otherwise mirrors exactly. A UTC stamp rendered with no marker is the exact mislabel
        // class ImageViewerWindowView.axaml:91-97 was already fixed for once in this codebase.
        var sentFrameStartedAt = DateTimeOffset.Now;

        // Code-review finding: _transmitCts must exist BEFORE the (potentially awaiting) macro
        // re-resolution below, not just before the encode call -- otherwise a Stop TX click during
        // that window hits a null _transmitCts (a silent no-op) and the transmission starts anyway.
        _transmitCts = new CancellationTokenSource();
        try
        {
            // RX/TX pipeline fix plan (2026-09-01), item 2 -- re-resolve time/frequency macros fresh
            // at the moment of transmission, not the moment of Apply. See the helper's own doc
            // comment for the compare-then-conditionally-rebake gate and its fallback contract; it
            // never throws out of this call (its own try/catch always returns an image), so it's
            // safe to await inside this try without a separate handler for it.
            image = await RefreshTransmitImageIfMacrosChangedAsync(image, mode);
            _transmitCts.Token.ThrowIfCancellationRequested();

            await _sstvSession.TransmitAsync(mode, image, _transmitCts.Token);
        }
        catch (OperationCanceledException)
        {
            // A manual Stop TX click is an intentional user action -- no scary error text for that
            // case, only for an auto-cutoff (see _cutoffTriggered's own doc comment). The cutoff
            // itself was already logged at Warning by CheckSwrCutoff; a manual stop is Information,
            // not a failure.
            if (_cutoffTriggered)
            {
                ErrorMessage = _localization.GetString("Panes.TxControls.Error.SwrCutoff");
            }
            else
            {
                Log.TransmitStoppedManually(_logger, mode.Id);
                ErrorMessage = null;
            }

            sentFrameOutcome = TxHistoryOutcome.Stopped;
        }
        catch (Exception ex)
        {
            Log.TransmitFailed(_logger, mode.Id, ex);
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.TransmitFailed");
            sentFrameOutcome = TxHistoryOutcome.Failed;
        }
        finally
        {
            _transmitCts.Dispose();
            _transmitCts = null;
            LiveSwrRatio = null;
            LiveAlcLevel = null;
            LivePowerPercent = null;
            TransmitProgress = null;
            IsTransmitting = false;
            TransmitCommand.NotifyCanExecuteChanged();
            StopTransmitCommand.NotifyCanExecuteChanged();
            RunLoopbackSelfTestCommand.NotifyCanExecuteChanged();
            _currentEditor?.NotifyTransmitAvailabilityChanged();

            // TX history plan: recording is best-effort and must never affect TransmitAsync's own
            // completion -- plan-review finding. ImageSourceBitmapConverter.ToBitmap allocates a
            // WriteableBitmap and can throw (OOM/graphics-backend failure); an uncaught throw here
            // would escape into AsyncRelayCommand and crash right after every transmission that hits
            // it. Strictly synchronous (no await) -- an await here would defer this command's own
            // completion/IsTransmitting reset, which every caller (TransmitCommand.CanExecute,
            // OnEditorAppliedAndTransmit, etc.) depends on completing promptly.
            if (sentFrameOutcome == TxHistoryOutcome.Completed || _anyProgressReported)
            {
                try
                {
                    RecordSentFrame(image, mode, sentFrameStartedAt, sentFrameOutcome, sentFrameFrequencyHz, sentFrameSourceFileName);
                }
                catch (Exception ex)
                {
                    Log.RecordSentFrameFailed(_logger, mode.Id, ex);
                }
            }
        }
    }

    /// <summary>Appends to <see cref="SentFrames"/>, newest first, evicting past
    /// <see cref="SentFramesCapacity"/> -- direct mirror of <c>RxImagePaneViewModel.AddPreviousFrameAsync</c>'s
    /// own insert/evict shape. <paramref name="image"/> is the post-macro-rebake frame actually
    /// handed to <see cref="ISstvSessionService.TransmitAsync"/> (or, on a cancel/failure before the
    /// rebake completed, the pre-rebake frame that was about to be sent) -- never re-read from
    /// <see cref="_loadedImage"/>, which the operator may have already replaced by the time this
    /// runs. <see cref="TransmittedFrameViewModel.Thumbnail"/> is a FRESH conversion, never
    /// <see cref="PreviewImage"/> itself (disposed on every reassignment, see
    /// <see cref="OnPreviewImageChanged"/>). Not disposed on eviction here -- see <see cref="Dispose"/>
    /// and <c>RxImagePaneViewModel.PreviousFrames</c>' own identical non-disposing precedent.</summary>
    private void RecordSentFrame(IImageSource image, SstvModeDefinition mode, DateTimeOffset transmittedAt, TxHistoryOutcome outcome, long? frequencyHz, string? sourceFileName)
    {
        var outcomeLabel = _localization.GetString(outcome switch
        {
            TxHistoryOutcome.Stopped => "Panes.TxControls.SentFrames.Outcome.Stopped",
            TxHistoryOutcome.Failed => "Panes.TxControls.SentFrames.Outcome.Failed",
            _ => "Panes.TxControls.SentFrames.Outcome.Completed",
        });

        SentFrames.Insert(0, new TransmittedFrameViewModel(
            image, ImageSourceBitmapConverter.ToBitmap(image), mode, transmittedAt, outcome, outcomeLabel, frequencyHz, sourceFileName));
        while (SentFrames.Count > SentFramesCapacity)
        {
            SentFrames.RemoveAt(SentFrames.Count - 1);
        }
    }

    /// <summary>Plan-review finding: same "not just CanTransmit" gate <see cref="CanTransmit"/>'s
    /// own doc comment documents -- <see cref="_loadedImage"/>'s dimensions can only ever diverge
    /// from <see cref="SelectedMode"/>'s while NO editor is open (an open editor freezes
    /// <see cref="SelectedMode"/> for its own lifetime). <see cref="ResendSentFrame"/> changes
    /// <see cref="SelectedMode"/> to <paramref name="entry"/>'s own mode, so it needs the identical
    /// re-check <see cref="QuickSelectMode"/> already uses, not just the busy check.</summary>
    private bool CanResendSentFrame() => (!IsEditorOpen || IsCurrentEditorBlankAndUntouched()) && !IsTransmitting && !IsRunningLoopbackSelfTest;

    /// <summary>TX history plan (2026-09-01): re-transmits a <see cref="SentFrames"/> entry exactly
    /// as it went out originally.
    /// <para><b>Order is load-bearing</b> (2-round plan-review finding): <see cref="_editState"/> is
    /// cleared BEFORE <see cref="SelectedMode"/> changes, not after -- <see cref="OnSelectedModeChanged"/>
    /// runs synchronously off the <see cref="SelectedMode"/> setter, and with <see cref="_editState"/>
    /// still set it would reflow <see cref="_loadedImage"/>/<see cref="PreviewImage"/> from the STALE
    /// edit state (a real, expensive Crop/Resize/Adjust/Template chain run only to be thrown away) at
    /// <paramref name="entry"/>'s own mode's dimensions, and any throw inside that chain would abort
    /// this method before the correct values below are set. Clearing <see cref="_editState"/> first
    /// makes that setter take its cheap null-editState branch instead.</para>
    /// <para>Nulling <see cref="_editState"/> also disables <see cref="EditCurrentImageCommand"/>
    /// until the next Apply, and discards whatever the operator had prepared-but-not-yet-sent --
    /// deliberate, not an oversight: this is the SAME "loading a different source replaces whatever
    /// was applied, no confirm prompt" behavior Browse/Stock/Blank/Copy-to-TX already have. Resend is
    /// one more way to load a different source, not a special case that needs its own undo/confirm
    /// machinery.</para>
    /// <para>Skips <see cref="RefreshTransmitImageIfMacrosChangedAsync"/>'s rebake entirely (a null
    /// <see cref="_editState"/> is that method's own no-op branch) -- a resent card carries whatever
    /// <c>%T</c>/<c>{freq}</c> macro values were baked in at the ORIGINAL send, not refreshed ones.
    /// Intentional: "re-send this exact frame" means exactly that, not "re-run it with today's
    /// clock/frequency."</para></summary>
    [RelayCommand(CanExecute = nameof(CanResendSentFrame))]
    private void ResendSentFrame(TransmittedFrameViewModel entry)
    {
        if (IsEditorOpen && !IsCurrentEditorBlankAndUntouched())
        {
            return;
        }

        if (IsTransmitting || IsRunningLoopbackSelfTest)
        {
            return;
        }

        Log.ResendSentFrameInvoked(_logger, entry.Mode.Id);
        _editState = null;
        SelectedMode = entry.Mode;
        _loadedImage = entry.Image;
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(entry.Image);
        SelectedFileName = entry.SourceFileName;
        TransmitCommand.NotifyCanExecuteChanged();
        EditCurrentImageCommand.NotifyCanExecuteChanged();
        if (TransmitCommand.CanExecute(null))
        {
            TransmitCommand.Execute(null);
        }
    }

    /// <summary>TX history plan (2026-09-01): saves a <see cref="SentFrames"/> entry's exact
    /// transmitted pixels to a PNG file, via the same <see cref="IImageSourceWriter"/> Phase 5
    /// template persistence already uses. Uses <see cref="IFilePickerService.PickSavePngFileAsync"/>,
    /// NOT <see cref="IFilePickerService.PickSaveImageFileAsync"/> -- that method's own doc comment
    /// documents its Format return value as "the source of truth," and this app has no JPEG encoder
    /// reachable from an in-memory <see cref="IImageSource"/> (unlike <c>IReceivedFrameExporter</c>,
    /// which needs a source FILE PATH on disk); silently overriding a JPEG pick to <c>.png</c> would
    /// violate that documented contract, so the dialog itself never offers JPEG here (plan-review
    /// finding).</summary>
    [RelayCommand]
    private async Task SaveSentFrameAsync(TransmittedFrameViewModel entry)
    {
        Log.SaveSentFrameInvoked(_logger, entry.Mode.Id);
        ErrorMessage = null;
        try
        {
            // Code-review finding: NOT entry.SourceFileName's own extension -- that's the ORIGINAL
            // photo's filename (e.g. a Browse-sourced "vacation.jpg"), and DefaultExtension doesn't
            // rewrite an extension that's already present, so the dialog would pre-fill "vacation.jpg"
            // while WritePngAsync writes PNG bytes under it. Same "{timestamp}_{mode}.png" shape as
            // RxImagePaneViewModel.SaveFrameAsync's own suggested name.
            var suggestedFileName = $"{DateTime.Now:yyyyMMdd-HHmmss}_{entry.Mode.Id}.png";
            if (await _filePickerService.PickSavePngFileAsync(suggestedFileName) is not { } path)
            {
                return;
            }

            await _imageSourceWriter.WritePngAsync(entry.Image, path);
        }
        catch (Exception ex)
        {
            Log.SaveSentFrameFailed(_logger, entry.Mode.Id, ex);
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.SaveSentFrameFailed");
        }
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 2: time/frequency macros (<c>%T</c>/
    /// <c>%D</c>/<c>{freq}</c>/<c>{mode}</c> etc.) resolve once at Apply and get baked into
    /// <see cref="_loadedImage"/> -- a manual Transmit some real time later, or a repeat Transmit on
    /// an already-applied card, would otherwise send a stale value. Re-resolves each text element's
    /// RAW (macro-tokens-intact) content fresh from <see cref="_editState"/>'s own
    /// <see cref="EditState.RawOverlay"/> (index-parallel with <see cref="EditState.Document"/> by
    /// construction -- both are <c>OverlayElements.Select(...)</c> projections read in the same
    /// synchronous statement when <see cref="_editState"/> is built), compares each fresh value
    /// against the already-frozen <see cref="TemplateTextElement.Content"/> already baked into
    /// <see cref="EditState.Document"/>; only re-bakes (a full native-resolution
    /// <see cref="ITransmitImagePreparer.ComposePreview"/> against <see cref="EditState.Original"/> --
    /// NOT the cheap downscaled-working-copy cost the hot per-keystroke preview path pays elsewhere)
    /// when at least one text element's resolved value actually differs. Templates containing
    /// <c>{freq}</c>/<c>{mode}</c> will legitimately differ on most real Apply-then-Transmit presses
    /// (live radio state polled at Hz granularity) -- this gate usually skips the redundant render
    /// for <c>%T</c>/<c>%D</c>-only templates, it does not claim those tokens provably never change.
    /// <para>On a rebake, replaces <see cref="_loadedImage"/>/<see cref="PreviewImage"/> together
    /// (so the visible preview never shows a different value than what was actually sent -- a
    /// changed macro can legitimately shrink-to-fit at a different font size) AND replaces
    /// <see cref="_editState"/>'s own <see cref="EditState.Document"/> with the freshly-baked one,
    /// not just the returned image -- otherwise the compare baseline the NEXT Transmit reads would
    /// silently describe a Document from before this rebake, not what <see cref="_loadedImage"/> now
    /// actually holds. Concretely: Apply at frequency A, Transmit at frequency B (rebakes, baseline
    /// stays A), retune back to A, Transmit again -- fresh(A) would match the STILL-A baseline and
    /// skip the rebake, transmitting the stale B image. Verified safe to replace: the only other
    /// reader of <see cref="EditState.Document"/> is <see cref="OnSelectedModeChanged"/>'s reflow,
    /// which re-bakes from it unconditionally (never compares), so a fresher <see cref="EditState.Document"/>
    /// there changes nothing about that path's own behavior.</para>
    /// <para>Wrapped in its own try/catch, separate from <see cref="TransmitAsync"/>'s own
    /// transmission try -- a broken freshness check (a corrupt operator-settings file, an
    /// <c>ApplyTemplate</c> throw on an unsupported font combination) must not block an operator who
    /// just pressed Transmit from sending their card. Falls back to the unmodified
    /// <paramref name="loadedImage"/>, unmodified <see cref="_editState"/>/<see cref="_loadedImage"/>/
    /// <see cref="PreviewImage"/>, on any failure -- including when <see cref="_editState"/> is
    /// somehow null despite <see cref="_loadedImage"/> being set (shouldn't happen, cheap to
    /// guard).</para></summary>
    private async Task<IImageSource> RefreshTransmitImageIfMacrosChangedAsync(IImageSource loadedImage, SstvModeDefinition mode)
    {
        if (_editState is not { } edit)
        {
            return loadedImage;
        }

        try
        {
            // Loaded fresh here, not cached -- the operator may have edited Options (Callsign/Name/
            // Grid) at any point between Apply and this Transmit press.
            var operatorSettings = (await _settingsStore.LoadAsync())
                .GetSection(OperatorSettings.SectionKey, OperatorSettingsJsonContext.Default.OperatorSettings)
                ?? new OperatorSettings();
            var radioState = _radioSession.LastKnownState;

            var changed = false;
            var freshElements = new List<TemplateElement>(edit.Document.Elements.Count);
            for (var i = 0; i < edit.Document.Elements.Count; i++)
            {
                var element = edit.Document.Elements[i];
                if (element is TemplateTextElement text && edit.RawOverlay[i] is TxImageEditorPaneViewModel.RawTextElementSnapshot raw)
                {
                    var fresh = _macroTextResolver.Resolve(raw.Text, operatorSettings, radioState, edit.TemplateVariables);
                    if (!string.Equals(fresh, text.Content, StringComparison.Ordinal))
                    {
                        changed = true;
                    }

                    freshElements.Add(text with { Content = fresh });
                }
                else
                {
                    freshElements.Add(element);
                }
            }

            if (!changed)
            {
                return loadedImage;
            }

            var freshDocument = edit.Document with { Elements = freshElements };
            var final = _preparer.ComposePreview(
                edit.Original, edit.CropRect, mode.ImageWidth, mode.ImageHeight, edit.PreserveAspect, edit.Adjustments, freshDocument);

            // Code-review finding: SelectedMode can change during the settings-load await above --
            // the mode ComboBox stays enabled during a transmit (CanChangeSourceOrMode has no
            // IsTransmitting term). If it did, OnSelectedModeChanged already reflowed
            // _loadedImage/_editState to the NEW mode's own dimensions; overwriting that here with
            // an image baked against the stale captured `mode` would silently break the
            // "_loadedImage always matches SelectedMode" invariant CanTransmit's own doc comment
            // states. `final` is still returned either way -- the in-flight transmit uses the
            // captured `mode`/`final` pairing, which stays internally self-consistent regardless.
            if (SelectedMode == mode)
            {
                // Computed into a local FIRST (code-review finding): if ToBitmap itself throws, the
                // catch below must return the pre-rebake image with _editState/_loadedImage/
                // PreviewImage genuinely untouched, not left one field ahead of the other two.
                var bitmap = ImageSourceBitmapConverter.ToBitmap(final);
                _editState = edit with { Document = freshDocument };
                _loadedImage = final;
                PreviewImage = bitmap;
            }

            return final;
        }
        catch (Exception ex)
        {
            Log.TransmitMacroRebakeFailed(_logger, mode.Id, ex);
            return loadedImage;
        }
    }

    private bool CanStopTransmit() => IsTransmitting;

    [RelayCommand(CanExecute = nameof(CanStopTransmit))]
    private void StopTransmit()
    {
        Log.StopTransmitInvoked(_logger);
        _transmitCts?.Cancel();
    }

    /// <summary>Whether <see cref="RunLoopbackSelfTestCommand"/> is currently executing -- toggled
    /// around the whole call, same shape as <see cref="IsTransmitting"/>/<see cref="TransmitAsync"/>
    /// (a fresh encode+decode of a full image is real CPU work, not instant).</summary>
    [ObservableProperty]
    private bool _isRunningLoopbackSelfTest;

    /// <summary>Fires once <see cref="RunLoopbackSelfTestCommand"/> completes successfully -- the
    /// result surfaces ONLY via this dedicated dialog, never through the live RX pane's own
    /// <c>Current</c>/<c>Progress</c> (round-5 plan-review: the self-test's private decoder is by
    /// design unreachable from the RX pane, and must stay that way).</summary>
    public event Action<LoopbackSelfTestResultWindowViewModel>? LoopbackSelfTestCompleted;

    private bool CanRunLoopbackSelfTest() => _loadedImage is not null && !IsTransmitting && !IsRunningLoopbackSelfTest;

    [RelayCommand(CanExecute = nameof(CanRunLoopbackSelfTest))]
    private async Task RunLoopbackSelfTestAsync()
    {
        if (_loadedImage is not { } image || SelectedMode is not { } mode)
        {
            return;
        }

        Log.LoopbackSelfTestInvoked(_logger, mode.Id);
        ErrorMessage = null;
        IsRunningLoopbackSelfTest = true;
        RunLoopbackSelfTestCommand.NotifyCanExecuteChanged();
        // Code-review round-1 finding: TransmitCommand's CanExecute also depends on
        // IsRunningLoopbackSelfTest now -- must be notified at both toggle points, same as
        // RunLoopbackSelfTestCommand itself is notified at TransmitAsync's own toggle points.
        TransmitCommand.NotifyCanExecuteChanged();
        _currentEditor?.NotifyTransmitAvailabilityChanged();
        try
        {
            var result = await _sstvSession.RunLoopbackSelfTestAsync(mode, image);
            LoopbackSelfTestCompleted?.Invoke(new LoopbackSelfTestResultWindowViewModel(mode, result, AvailableModes, _localization));
        }
        catch (Exception ex)
        {
            Log.LoopbackSelfTestFailed(_logger, mode.Id, ex);
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoopbackSelfTestFailed");
        }
        finally
        {
            IsRunningLoopbackSelfTest = false;
            RunLoopbackSelfTestCommand.NotifyCanExecuteChanged();
            TransmitCommand.NotifyCanExecuteChanged();
            _currentEditor?.NotifyTransmitAvailabilityChanged();
        }
    }

    /// <summary>Re-runs Crop→Resize→ApplyAdjustments→ApplyTemplate against the retained
    /// <see cref="EditState"/>'s original at the new mode's dimensions -- normalized (0..1)
    /// crop/element coordinates make this valid across modes (adjustment slider values are already
    /// mode-independent, no re-projection needed for those). **Known, explicitly-accepted gap
    /// (see <see cref="EditState"/>'s own doc comment)**: <see cref="EditState.Document"/>'s element
    /// bounds were projected against the OLD mode/crop/PreserveAspect combination at Apply time and
    /// are reused as-is here against the NEW mode's dimensions -- unlike the editor's own real-time
    /// preview, which re-derives fresh from the crop rect on every frame. Pre-existing (tracked
    /// separately, spec/18-path-to-1.0.md Medium item) and amplified, not introduced, by Phase 1's
    /// real per-element bounds/shrink-to-fit. This is synchronous CPU work against an already-in-
    /// memory original (no file/network I/O like the old flat resize-reload did), so there is no
    /// async race to guard against here -- the old cancel-and-replace <c>CancellationTokenSource</c>
    /// machinery existed specifically for that I/O race and would be unused complexity now that the
    /// source is cached.</summary>
    partial void OnSelectedModeChanged(SstvModeDefinition? value)
    {
        Log.SelectedModeChanged(_logger, value?.Id);

        // Backlog item (user request, 2026-08-17): "make sure to update the editor window when
        // another mode is selected" -- a currently-open BLANK placeholder editor (nothing else,
        // see IsCurrentEditorBlankAndUntouched's own doc comment) auto-updates to the new mode's
        // own frame size instead of sitting stale at the old one. CloseBlankEditorForReplacement
        // (not CancelCommand.Execute -- see its own doc comment) closes the stale editor without
        // any auto-reopen side effect, then this unconditionally reopens blank at whatever
        // SelectedMode is NOW (this property's own new value, already committed by the time this
        // partial method runs).
        //
        // Tier B audit finding: this branch used to `return` right after reopening the blank
        // editor, which ALSO skipped the reflow below -- a blank/untouched editor being open says
        // nothing about whether _editState is null. _editState survives Cancel of a re-edit (Cancel
        // doesn't clear a prior Apply's state, only the in-progress edit), and OnEditorCancelled
        // auto-reopens a fresh blank editor right after -- so "blank editor open" + "_editState
        // still set from an earlier Apply" is a real, reachable combination, not a hypothetical.
        // With the early return, CanTransmit's own doc comment's invariant ("_loadedImage is only
        // ever sized to whatever SelectedMode was at that moment") silently broke: _loadedImage
        // kept the OLD mode's pixel dimensions while SelectedMode moved on, and Transmit had no
        // extra gate to catch the mismatch before handing a wrong-sized image straight to
        // AnalogFmSstvEncoder (which throws ArgumentException, surfacing only as a context-free
        // "Transmit failed"). Falling through instead of returning lets the shared reflow/clear
        // logic below run exactly as it would with no editor open at all -- what's shown inside the
        // (separately reopened) blank editor is unaffected either way.
        if (IsCurrentEditorBlankAndUntouched())
        {
            CloseBlankEditorForReplacement();
            _ = OpenBlankEditorAsync();
        }

        if (value is null || _editState is not { } edit)
        {
            _loadedImage = null;
            PreviewImage = null;
            TransmitCommand.NotifyCanExecuteChanged();
            return;
        }

        var cropped = _preparer.Crop(edit.Original, edit.CropRect);
        var resized = _preparer.Resize(cropped, value.ImageWidth, value.ImageHeight, edit.PreserveAspect);
        var adjusted = _preparer.ApplyAdjustments(resized, edit.Adjustments);
        var final = _preparer.ApplyTemplate(adjusted, edit.Document);

        _loadedImage = final;
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(final);
        TransmitCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Cancels and disposes an in-flight transmit's <see cref="_transmitCts"/> if the pane
    /// is torn down mid-transmission -- otherwise that transmit (and the rig's PTT) would run to
    /// completion on its own with nothing left to stop it early. Also disposes every
    /// <see cref="SentFrames"/> thumbnail -- unlike <c>RxImagePaneViewModel.PreviousFrames</c>,
    /// which never disposes at all (backed by a persisted store, so a leaked thumbnail bitmap is
    /// bounded by that pane's own lifetime either way), <see cref="SentFrames"/> is this pane's
    /// only reference to those bitmaps; not disposing here would leak them for good past teardown.
    /// Deliberately NOT disposed on eviction (<see cref="RecordSentFrame"/>'s own doc comment) --
    /// only here, at the pane's own end of life.</summary>
    public void Dispose()
    {
        _transmitCts?.Cancel();
        foreach (var entry in SentFrames)
        {
            entry.Thumbnail.Dispose();
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading TxPaneUiSettings failed")]
        public static partial void LoadTxPaneUiSettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Persisting TxPaneUiSettings failed")]
        public static partial void PersistTxPaneUiSettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading configured TX output device name failed")]
        public static partial void LoadOutputDeviceNameFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the CW-ID/FSK-ID Identification card summary failed")]
        public static partial void LoadIdentificationSummaryFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading radio safety settings failed")]
        public static partial void LoadSafetySettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SWR auto-cutoff triggered: SWR={Swr}, threshold={Threshold}")]
        public static partial void SwrCutoffTriggered(ILogger logger, float swr, double threshold);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RunLoopbackSelfTest invoked: {ModeId}")]
        public static partial void LoopbackSelfTestInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loopback self-test failed: {ModeId}")]
        public static partial void LoopbackSelfTestFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QuickSelectMode invoked: {ModeId}")]
        public static partial void QuickSelectModeInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QuickSelectMode pressed for unknown mode id {ModeId} -- no matching AvailableModes entry")]
        public static partial void QuickSelectModeUnknownId(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Quick-mode grid slot {SlotIndex} reassigned to mode {ModeId}")]
        public static partial void ReassignQuickModeSlotInvoked(ILogger logger, int slotIndex, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Quick-mode grid reassignment rejected: slot index {SlotIndex} is out of range")]
        public static partial void ReassignQuickModeSlotOutOfRange(ILogger logger, int slotIndex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Quick-mode grid reassignment rejected: mode {ModeId} is already used by a different slot than {SlotIndex}")]
        public static partial void ReassignQuickModeSlotAlreadyUsedElsewhere(ILogger logger, int slotIndex, string modeId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "RefreshStockLibrary invoked")]
        public static partial void RefreshStockLibraryInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Listing stock image entries failed")]
        public static partial void ListStockEntriesFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Loading stock thumbnail failed: {FileName}")]
        public static partial void LoadStockThumbnailFailed(ILogger logger, string fileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectImage invoked")]
        public static partial void SelectImageInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "PickImageFileAsync failed")]
        public static partial void PickImageFileFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectStockImage invoked: {FileName}")]
        public static partial void SelectStockImageInvoked(ILogger logger, string fileName);

        [LoggerMessage(Level = LogLevel.Debug, Message = "CopyReceivedImageToTx invoked")]
        public static partial void CopyReceivedImageInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading TX source image failed: {FileName}")]
        public static partial void LoadTxSourceImageFailed(ILogger logger, string fileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Opening the TX image editor failed: {FileName}")]
        public static partial void OpenTxEditorFailed(ILogger logger, string fileName, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "EditCurrentImage invoked with no retained edit state or selected mode -- no-op")]
        public static partial void EditCurrentImageWithNoEditState(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Transmit invoked: mode={ModeId}")]
        public static partial void TransmitInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Fresh macro re-resolution at Transmit failed: mode={ModeId} -- falling back to the last-applied image")]
        public static partial void TransmitMacroRebakeFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Transmit stopped manually: mode={ModeId}")]
        public static partial void TransmitStoppedManually(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Transmit failed: mode={ModeId}")]
        public static partial void TransmitFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Recording sent frame into TX history failed: mode={ModeId}")]
        public static partial void RecordSentFrameFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ResendSentFrame invoked: mode={ModeId}")]
        public static partial void ResendSentFrameInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SaveSentFrame invoked: mode={ModeId}")]
        public static partial void SaveSentFrameInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Saving sent frame failed: mode={ModeId}")]
        public static partial void SaveSentFrameFailed(ILogger logger, string modeId, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "StopTransmit invoked")]
        public static partial void StopTransmitInvoked(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectedMode changed: {ModeId}")]
        public static partial void SelectedModeChanged(ILogger logger, string? modeId);
    }
}

/// <summary>Carries its own <see cref="SelectCommand"/> (the parent's
/// <see cref="TxControlsPaneViewModel.SelectStockImageCommand"/>, set once at construction) rather
/// than making the View reach back into an ancestor's DataContext with a cross-DataTemplate
/// type-cast binding -- that pattern (<c>$parent[ListBox].((vm:TxControlsPaneViewModel)DataContext)</c>)
/// compiled fine but crashed at runtime the first time this app actually ran with a non-empty stock
/// library ("Unable to resolve type ... from any of the following locations" -- Avalonia falls back
/// to a reflection-mode binding for this shape of path inside a deferred `ItemsControl` template,
/// and that resolver couldn't find the type). Found and fixed during Piece 5c's own hands-on
/// verification, not part of that piece's original scope.</summary>
public sealed record StockEntryViewModel(StockImageEntry Entry, Bitmap? Thumbnail, System.Windows.Input.ICommand SelectCommand);

/// <summary>One appended sample of <see cref="TxControlsPaneViewModel.TelemetryHistory"/> -- see that
/// property's own doc comment.</summary>
public sealed record TxTelemetrySample(DateTimeOffset Timestamp, float? SwrRatio, float? AlcLevel, float? PowerPercent);

/// <summary>TX history plan (2026-09-01, Fable operator-perspective punch list, "No TX history").
/// Matches <c>ReceiveHistoryEntry</c>'s own <c>Completed</c>/two-other-outcomes shape, not a straight
/// success/failure boolean -- an SWR cutoff or a manual Stop TX is neither a clean send nor a genuine
/// failure, and <see cref="TxControlsPaneViewModel"/>'s own recording logic needs to tell all three
/// apart (see <c>_anyProgressReported</c>'s own doc comment for why <c>Stopped</c>/<c>Failed</c> are
/// gated separately from <c>Completed</c>).</summary>
public enum TxHistoryOutcome
{
    Completed,
    Stopped,
    Failed,
}

/// <summary>One entry in <see cref="TxControlsPaneViewModel.SentFrames"/> -- see that property's own
/// doc comment. A plain class, not a record (auditor plan-review nit): record value-equality over a
/// <see cref="Bitmap"/>/<see cref="IImageSource"/> payload would make any future <c>IndexOf</c>/
/// <c>Remove</c> against this collection compare pixel-object identity, not entry identity, which is
/// surprising and unnecessary here.
/// <para><see cref="Thumbnail"/> is ALWAYS a fresh <c>ImageSourceBitmapConverter.ToBitmap</c>
/// conversion of <see cref="Image"/>, never a reference to <see cref="TxControlsPaneViewModel.PreviewImage"/>
/// itself -- that property is disposed on every reassignment (its own <c>OnPreviewImageChanged</c>
/// hook), so aliasing it here would show a disposed bitmap the moment anything else sets it.
/// Deliberately full-resolution, not downscaled like <c>RxImagePaneViewModel.PreviousFrames</c>' own
/// 96px thumbnails -- full-res is required anyway for <c>ResendSentFrame</c>/<c>SaveSentFrame</c>,
/// and at this app's SSTV frame sizes (max ~640x496) the bounded
/// <see cref="TxControlsPaneViewModel.SentFrames"/> strip costs roughly 12MB total, which doesn't
/// justify a second resize pass RX's own store-backed thumbnail load doesn't need here (no backing
/// file on disk to reload a downscaled copy from later).</para></summary>
public sealed class TransmittedFrameViewModel
{
    public TransmittedFrameViewModel(IImageSource image, Bitmap thumbnail, SstvModeDefinition mode, DateTimeOffset transmittedAt, TxHistoryOutcome outcome, string outcomeLabel, long? frequencyHz, string? sourceFileName)
    {
        Image = image;
        Thumbnail = thumbnail;
        Mode = mode;
        TransmittedAt = transmittedAt;
        Outcome = outcome;
        OutcomeLabel = outcomeLabel;
        FrequencyHz = frequencyHz;
        SourceFileName = sourceFileName;
    }

    public IImageSource Image { get; }

    public Bitmap Thumbnail { get; }

    public SstvModeDefinition Mode { get; }

    public DateTimeOffset TransmittedAt { get; }

    public TxHistoryOutcome Outcome { get; }

    /// <summary>Pre-resolved at record time, not re-resolved on read -- same "language change is
    /// restart-gated anyway" precedent <c>RxHistoryPaneViewModel.LinkedQsoDisplay</c>'s own doc
    /// comment establishes; <see cref="TransmittedFrameViewModel"/> carries no
    /// <c>ILocalizationService</c> reference of its own to re-resolve with regardless.</summary>
    public string OutcomeLabel { get; }

    public long? FrequencyHz { get; }

    public string? SourceFileName { get; }
}
