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

    private bool _suppressSafetyPersist;

    /// <summary>Tier B audit finding: matches <c>RadioStatusViewModel.PersistVolumeDebouncedAsync</c>'s
    /// own established shape/reasoning for the identical hazard class -- SwrCutoffThreshold's TextBox
    /// is TwoWay/PropertyChanged-triggered, so typing "12" used to fire TWO overlapping, un-awaited
    /// PersistSafetySettingsAsync calls, each capturing its own value before its own await; if the
    /// stale "1" call's SaveAsync happened to complete AFTER the fresh "12" call's, the persisted SWR
    /// safety cutoff would silently end up at 1.0 (an always-trips value) while the UI still showed
    /// 12. Debounce-and-cancel-supersedes closes the same race the volume slider's own doc comment
    /// already names ("overlapping un-awaited SaveAsync calls racing each other could let a stale
    /// write clobber a fresher one").</summary>
    private static readonly TimeSpan SafetyPersistDebounce = TimeSpan.FromMilliseconds(400);

    private CancellationTokenSource? _safetyPersistCts;

    private IImageSource? _loadedImage;

    /// <summary>Backlog item (user request, 2026-08-17) -- tracks the currently-open editor (null
    /// while none is open) so <see cref="CanSelectFavoriteMode"/>/<see cref="CanQuickSelectMode"/>
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

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? _selectedFileName;

    [ObservableProperty]
    private bool _isTransmitting;

    /// <summary>spec/18-path-to-1.0.md Medium item: "No TX send-progress feedback during
    /// transmit." Nullable, mirroring <c>RxImagePaneViewModel.Progress</c>'s own pattern -- null
    /// while idle (no ProgressBar fill), a real <c>[0,1]</c> value while
    /// <see cref="IsTransmitting"/>. Set to <c>0</c> at TX start (not left null until the first
    /// report arrives) and reset to <c>null</c> in <see cref="TransmitAsync"/>'s own
    /// <c>finally</c>, alongside the existing telemetry resets there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransmitProgressText))]
    private double? _transmitProgress;

    /// <summary>Backing value for <see cref="TransmitProgressText"/> only -- deliberately not an
    /// <c>[ObservableProperty]</c> itself; <see cref="OnTransmitProgressChanged"/> always sets this
    /// immediately before <see cref="TransmitProgress"/>, whose own setter is what actually raises
    /// the <see cref="TransmitProgressText"/> change notification (via
    /// <c>NotifyPropertyChangedFor</c> above) -- a second independent notification here would be
    /// redundant.</summary>
    private TimeSpan _transmitRemaining;

    public string TransmitProgressText => TransmitProgress is { } progress
        ? _localization.GetString("Panes.TxControls.TransmitProgressFormat", (int)Math.Round(progress * 100), FormatRemaining(_transmitRemaining))
        : string.Empty;

    private static string FormatRemaining(TimeSpan remaining) => remaining.TotalHours >= 1
        ? $"{(int)remaining.TotalHours}:{remaining.Minutes:D2}:{remaining.Seconds:D2}"
        : $"{remaining.Minutes}:{remaining.Seconds:D2}";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _autoFollowRxMode;

    /// <summary>TX-only telemetry (see <see cref="RadioState"/>'s own doc comment) -- populated only
    /// while this pane's own <see cref="IsTransmitting"/> AND the rig's own PTT readback both agree
    /// transmission is actually in progress; <see langword="null"/> otherwise, never a stale/meaningless
    /// last-known value.</summary>
    [ObservableProperty]
    private float? _liveSwrRatio;

    [ObservableProperty]
    private float? _liveAlcLevel;

    [ObservableProperty]
    private float? _livePowerPercent;

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
    private bool _showAlcMeter;

    [ObservableProperty]
    private bool _showPowerMeter;

    [ObservableProperty]
    private bool _swrCutoffEnabled;

    [ObservableProperty]
    private double _swrCutoffThreshold = 3.0;

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
    /// surface an error banner). Loaded once at construction, same convention as
    /// <see cref="AutoFollowRxMode"/>/<see cref="SwrCutoffEnabled"/> above -- not re-fetched on a
    /// live settings change while this pane stays open; a future pass can add that if it turns out
    /// to matter in practice.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDeviceNameDisplay))]
    private string? _outputDeviceName;

    public string OutputDeviceNameDisplay => OutputDeviceName ?? "—";

    /// <summary>Frame-metadata-style read-only summary of what <see cref="ISstvSessionService.TransmitAsync"/>
    /// would actually resolve right now (CW-ID/FSK station-ID subsystem Phase 6) -- backs the
    /// Transmit tab's "Identification" card. Loaded once at construction via
    /// <see cref="ISstvSessionService.GetStationIdTransmitOptionsAsync"/>, same "not re-fetched on a
    /// live settings change while this pane stays open" convention as <see cref="OutputDeviceName"/>
    /// above.</summary>
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
    /// (<c>Main.cpp:7018-7025</c>'s real order: FSK-ID packet first, then CW-ID -- independent, not
    /// mutually exclusive, matching <c>AnalogFmSstvEncoder.GenerateFrequencySegments</c>'s own
    /// append order). Genuinely new wording (mockup's own "CW after frame" is one specific
    /// combination, not a format this reuses verbatim) -- a reasonable, low-risk reading of what
    /// this row is for, not a citation-backed legacy string.</summary>
    public string TailDisplay => (FskIdEnabled, CwIdEnabled) switch
    {
        (true, true) => _localization.GetString("Panes.TxId.TailBoth"),
        (true, false) => _localization.GetString("Panes.TxId.TailFskOnly"),
        (false, true) => _localization.GetString("Panes.TxId.TailCwOnly"),
        (false, false) => _localization.GetString("Panes.TxId.Off"),
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
        ModeTimingRows = AvailableModes
            .Select(m => new ModeTimingRowViewModel(m.DisplayName, m.ImageHeight, m.LineDurationMs, m.LineDurationMs * m.ImageHeight / 1000.0))
            .ToList();

        sstvSession.ModeDetected += OnModeDetected;
        sstvSession.TransmitProgressChanged += OnTransmitProgressChanged;
        radioSession.StateChanges.Subscribe(OnRadioStateChanged);

        // Best-effort initial load, same reasoning as RxHistoryPaneViewModel's constructor -- a
        // failure here leaves the strip empty rather than blocking construction.
        _ = RefreshStockLibraryAsync();
        _ = LoadTxPaneUiSettingsAsync();
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
    /// pattern here (see FavoriteModeButtonViewModel/StockEntryViewModel/
    /// FrequencyPresetButtonViewModel, each carrying its own pre-resolved command for the same
    /// reason).</summary>
    public RadioStatusViewModel? RadioStatus { get; set; }

    private const int StockThumbnailMaxDimension = 64;

    public IReadOnlyList<SstvModeDefinition> AvailableModes { get; }

    /// <summary>Mode-timing-reference table (mock2's own card) -- fully real, zero new data:
    /// computed once from <see cref="AvailableModes"/>'s own <c>LineDurationMs</c>/<c>ImageHeight</c>.
    /// "Frame" is an approximation (<c>LineDurationMs * ImageHeight</c>) -- some color families
    /// transmit 2 scan lines per image row (see <c>SstvModeDefinition.ImageHeight</c>'s own doc
    /// history), so the true total transmitted-line count can differ slightly from
    /// <c>ImageHeight</c> for those modes; not exact for every family, close enough for a
    /// reference table.</summary>
    public IReadOnlyList<ModeTimingRowViewModel> ModeTimingRows { get; }

    public ObservableCollection<StockEntryViewModel> StockEntries { get; } = [];

    /// <summary>Every available mode as a checkable option, for the "Edit favorites..." popup --
    /// membership never changes after construction, only <see cref="FavoriteModeOptionViewModel.IsSelected"/>
    /// does.</summary>
    public ObservableCollection<FavoriteModeOptionViewModel> FavoriteModeOptions { get; } = [];

    /// <summary>The selected subset of <see cref="FavoriteModeOptions"/>, in <see cref="AvailableModes"/>'s
    /// own order -- the actual quick-select button row. Simpler than a separate user-reorderable list;
    /// revisit only if fixed ordering turns out to matter in practice. Each entry carries its own
    /// <see cref="FavoriteModeButtonViewModel.SelectCommand"/> rather than the View reaching back
    /// into an ancestor's DataContext with a cross-DataTemplate type-cast binding -- that exact
    /// pattern (<c>$parent[ItemsControl].((vm:TxControlsPaneViewModel)DataContext)</c>) already
    /// crashed this app at runtime once, for <see cref="StockEntryViewModel"/> (see that type's own
    /// doc comment) -- same fix applied here up front, not repeated by hand-editing later.</summary>
    public ObservableCollection<FavoriteModeButtonViewModel> FavoriteModes { get; } = [];

    private async Task LoadTxPaneUiSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var txPaneUi = settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings) ?? new TxPaneUiSettings();

            AutoFollowRxMode = txPaneUi.AutoFollowRxMode;

            foreach (var mode in AvailableModes)
            {
                var option = new FavoriteModeOptionViewModel(mode, txPaneUi.FavoriteModeIds.Contains(mode.Id));
                option.PropertyChanged += OnFavoriteModeOptionChanged;
                FavoriteModeOptions.Add(option);
            }

            RebuildFavoriteModes();
        }
        catch (Exception ex)
        {
            Log.LoadTxPaneUiSettingsFailed(_logger, ex);
        }
    }

    private async Task LoadOutputDeviceNameAsync()
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

    private async Task LoadIdentificationSummaryAsync()
    {
        try
        {
            var stationId = await _sstvSession.GetStationIdTransmitOptionsAsync();
            FskIdEnabled = stationId.FskIdEnabled;
            IdentificationCallsign = stationId.Callsign;
            CwIdEnabled = stationId.CwEnabled;
            IdentificationCwWpm = stationId.CwWpm;
            IdentificationCwToneFrequencyHz = stationId.CwToneFrequencyHz;
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadOutputDeviceNameAsync above -- a failure here
            // leaves the card showing "Off" for everything rather than blocking construction.
            Log.LoadIdentificationSummaryFailed(_logger, ex);
        }
    }

    private void OnFavoriteModeOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildFavoriteModes();
        _ = PersistTxPaneUiSettingsAsync();
    }

    private void RebuildFavoriteModes()
    {
        FavoriteModes.Clear();
        foreach (var option in FavoriteModeOptions)
        {
            if (option.IsSelected)
            {
                FavoriteModes.Add(new FavoriteModeButtonViewModel(option.Mode, SelectFavoriteModeCommand));
            }
        }
    }

    /// <summary>Read-modify-write against whatever is currently persisted for this section, not a
    /// fresh <c>new TxPaneUiSettings { ... }</c> -- a from-scratch write here would silently clobber
    /// whichever of <see cref="TxPaneUiSettings.FavoriteModeIds"/>/<see cref="TxPaneUiSettings.AutoFollowRxMode"/>
    /// this particular call isn't updating.</summary>
    private async Task PersistTxPaneUiSettingsAsync()
    {
        try
        {
            var settings = await _settingsStore.LoadAsync();
            var current = settings.GetSection(TxPaneUiSettings.SectionKey, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings) ?? new TxPaneUiSettings();
            var updated = current with
            {
                FavoriteModeIds = FavoriteModeOptions.Where(o => o.IsSelected).Select(o => o.Mode.Id).ToArray(),
                AutoFollowRxMode = AutoFollowRxMode,
            };

            await _settingsStore.SaveAsync(settings.WithSection(TxPaneUiSettings.SectionKey, updated, TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings));
        }
        catch (Exception ex)
        {
            Log.PersistTxPaneUiSettingsFailed(_logger, ex);
        }
    }

    partial void OnAutoFollowRxModeChanged(bool value) => _ = PersistTxPaneUiSettingsAsync();

    private async Task LoadSafetySettingsAsync()
    {
        try
        {
            var spec = await _radioSession.GetSafetySettingsAsync();
            Dispatcher.UIThread.Post(() =>
            {
                // Tier B audit finding: try/finally, not a bare set-then-reset -- these two property
                // sets raise PropertyChanged into live Avalonia bindings, which can throw; a throw
                // here used to leave _suppressSafetyPersist stuck true for the process lifetime,
                // silently and permanently breaking PersistSafetySettingsAsync -- the exact failure
                // this method's own doc comment warns about ("the user's safety setting didn't take
                // effect with nothing telling them so"), just triggered a different way.
                try
                {
                    _suppressSafetyPersist = true;
                    SwrCutoffEnabled = spec.SwrCutoffEnabled;
                    SwrCutoffThreshold = spec.SwrCutoffThreshold;
                }
                finally
                {
                    _suppressSafetyPersist = false;
                }
            });
        }
        catch (Exception ex)
        {
            Log.LoadSafetySettingsFailed(_logger, ex);
        }
    }

    private void SchedulePersistSafetySettings()
    {
        // Debounced (not one settings write per keystroke) -- also avoids the correctness hazard
        // SafetyPersistDebounce's own doc comment names: overlapping un-awaited SaveAsync calls
        // racing each other could let a stale write clobber a fresher one.
        _safetyPersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _safetyPersistCts = cts;
        _ = PersistSafetySettingsDebouncedAsync(new RadioSafetySpec(SwrCutoffEnabled, SwrCutoffThreshold), cts.Token);
    }

    /// <summary>Error, not Warning -- a silently-failed SWR-cutoff-setting write means the user's
    /// safety setting didn't take effect with nothing telling them so.</summary>
    private async Task PersistSafetySettingsDebouncedAsync(RadioSafetySpec spec, CancellationToken ct)
    {
        try
        {
            await Task.Delay(SafetyPersistDebounce, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Normal control flow -- a newer edit superseded this one. Not worth a log line, same
            // convention as RadioStatusViewModel.PersistVolumeDebouncedAsync's own identical catch.
            return;
        }

        try
        {
            await _radioSession.SaveSafetySettingsAsync(spec, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.PersistSafetySettingsFailed(_logger, ex);
        }
    }

    partial void OnSwrCutoffEnabledChanged(bool value)
    {
        if (_suppressSafetyPersist)
        {
            return;
        }

        SchedulePersistSafetySettings();
    }

    partial void OnSwrCutoffThresholdChanged(double value)
    {
        if (_suppressSafetyPersist)
        {
            return;
        }

        SchedulePersistSafetySettings();
    }

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
            TransmitProgress = info.Fraction;
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
        if (!IsTransmitting || !state.IsTransmitting || !SwrCutoffEnabled || state.SwrRatio is not { } swr || swr <= SwrCutoffThreshold)
        {
            _consecutiveSwrOverThreshold = 0;
            return;
        }

        _consecutiveSwrOverThreshold++;
        if (_consecutiveSwrOverThreshold >= SwrCutoffConsecutiveSamplesRequired)
        {
            Log.SwrCutoffTriggered(_logger, swr, SwrCutoffThreshold);
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
        // again once already marshalled onto the UI thread is race-free.
        Dispatcher.UIThread.Post(() =>
        {
            if (!AutoFollowRxMode || IsEditorOpen)
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
    /// true) -- real work (or a deliberate photo pick) is never silently made switchable-away-from.</summary>
    private bool IsCurrentEditorBlankAndUntouched() => _currentEditorIsBlank && _currentEditor is { HasUnsavedEdits: false };

    /// <summary>Auditor-found regression (2026-08-17, usability-gap review): the mode ComboBox and
    /// Browse/STOCK were still hard-gated on a plain <c>!IsEditorOpen</c> binding in AXAML, which
    /// <see cref="CanSelectFavoriteMode"/>/<see cref="CanQuickSelectMode"/>'s own relaxation never
    /// reached (those drive <c>Command.CanExecute</c>, not a separate <c>IsEnabled</c> binding) --
    /// with the editor now always open by default, this made mode-select-via-dropdown and Browse/
    /// STOCK permanently unreachable after the very first blank auto-open. Bindable form of the
    /// same <see cref="IsCurrentEditorBlankAndUntouched"/> relaxation for controls with no
    /// Command/CanExecute mechanism to hang the check on instead. The 16 quick-mode-grid buttons
    /// needed no equivalent fix -- removing their own redundant <c>IsEnabled="{Binding !IsEditorOpen}"</c>
    /// (which was overriding <see cref="CanQuickSelectMode"/>'s already-correct CanExecute) was
    /// enough, since Avalonia's Button already auto-disables when a bound Command's CanExecute is
    /// false and nothing else overrides it.</summary>
    public bool CanChangeSourceOrMode => !IsEditorOpen || IsCurrentEditorBlankAndUntouched();

    private bool CanSelectFavoriteMode() => !IsEditorOpen || IsCurrentEditorBlankAndUntouched();

    [RelayCommand(CanExecute = nameof(CanSelectFavoriteMode))]
    private void SelectFavoriteMode(SstvModeDefinition mode)
    {
        // CanExecute alone isn't a hard gate -- CommunityToolkit's RelayCommand<T>.Execute doesn't
        // consult it, only Avalonia's Button.OnClick does (code-review finding). This body-level
        // check is the real backstop, matching the doctrine IsEditorOpen's own doc comment already
        // states ("the view is expected to disable picking while an editor is open; this is the
        // view-model-level backstop") and OpenEditorForSourceAsync already honors.
        if (IsEditorOpen && !IsCurrentEditorBlankAndUntouched())
        {
            return;
        }

        Log.SelectFavoriteModeInvoked(_logger, mode.Id);
        SelectedMode = mode;
    }

    private bool CanQuickSelectMode() => !IsEditorOpen || IsCurrentEditorBlankAndUntouched();

    /// <summary>Backs the fixed 16-pill quick-mode grid (spec/18-path-to-1.0.md High item 7) --
    /// distinct from <see cref="SelectFavoriteMode"/> above (the user-configurable Favorites row);
    /// the roadmap's own plan-review explicitly calls for wiring both, even though they do the
    /// same thing to <see cref="SelectedMode"/>, since the fixed grid is what the mockup shows.
    /// Mirrors <see cref="SelectFavoriteMode"/>'s own triple guard exactly, for the identical
    /// reason: <c>CanExecute</c> alone isn't a hard gate for a direct <c>Execute()</c> call, only
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

    /// <summary>Keeps the favorite-mode AND quick-mode-grid buttons' enabled state in sync with
    /// <see cref="IsEditorOpen"/> -- <see cref="CanSelectFavoriteMode"/>/
    /// <see cref="CanQuickSelectMode"/> alone only re-evaluate when something explicitly requests
    /// it. <see cref="EditCurrentImageCommand"/> piggybacks on this same hook (round-1 plan-review
    /// finding) -- <see cref="_editState"/> is a plain field, not observable, so nothing else would
    /// ever re-evaluate its own CanExecute; this fires after BOTH <see cref="OnEditorApplied"/>
    /// (which just set <see cref="_editState"/>) and <see cref="OnEditorCancelled"/>, since both set
    /// <see cref="IsEditorOpen"/> = <see langword="false"/>.</summary>
    partial void OnIsEditorOpenChanged(bool value)
    {
        SelectFavoriteModeCommand.NotifyCanExecuteChanged();
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

        await OpenEditorForSourceAsync(path, Path.GetFileName(path));
    }

    [RelayCommand]
    private async Task SelectStockImageAsync(StockImageEntry entry)
    {
        Log.SelectStockImageInvoked(_logger, entry.FileName);
        ErrorMessage = null;
        await OpenEditorForSourceAsync(entry, entry.FileName);
    }

    /// <summary>RX pane's "Copy to TX" stub (legacy precedent: <c>fileview.cpp</c>'s
    /// <c>CopyRectBitmap(pBitmapTXM)</c> -- copies the received bitmap into the TX slot as a fresh
    /// base image, not an overlay). Distinct from the already-shipped <c>AddLastRxImage</c> (the "+
    /// IMAGE" flyout's "Last RX" source, INSIDE an in-progress edit, inserted as an overlay element)
    /// -- this one replaces/opens the editor itself, same as Browse/Stock/Ready Rack. Same
    /// "nothing received yet" guard as <c>AddLastRxImage</c> (that command's own doc comment covers
    /// why the buffer's 1x1 black placeholder default needs an explicit check, and why this is a
    /// body-level no-op rather than a CanExecute gate -- no lifecycle hook to unsubscribe from
    /// <see cref="IReceivedImageBuffer.Updated"/> from this VM).</summary>
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

        if (!TryClaimEditorSlotForNewSource())
        {
            return;
        }

        var fileName = _localization.GetString("Panes.TxControls.CopyToTx.FileName");
        await OpenEditorWithLoadedSourceAsync(current, fileName);
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
    /// pick; a genuinely in-progress edit still refuses, exactly as before.</para></summary>
    private async Task OpenEditorForSourceAsync(object source, string fileName)
    {
        if (!TryClaimEditorSlotForNewSource())
        {
            return;
        }

        IImageSource original;
        try
        {
            original = source switch
            {
                StockImageEntry stockEntry => await _stockLibrary.LoadOriginalAsync(stockEntry),
                string path => await _imageFileLoader.LoadOriginalAsync(path),
                _ => throw new InvalidOperationException($"Unrecognized TX source type: {source.GetType()}"),
            };
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
            _currentEditor = null;
            _currentEditorIsBlank = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            EditorClosed?.Invoke();
            return;
        }

        await OpenEditorWithLoadedSourceAsync(original, fileName);
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
        var placeholder = new BlankImageSource(mode.ImageWidth, mode.ImageHeight, BlankPlaceholderColor);
        await OpenEditorWithLoadedSourceAsync(placeholder, _localization.GetString("Panes.TxControls.BlankImageName"));
    }

    /// <summary>Light neutral gray (matches this app's own Industry design system's neutral-surface
    /// family, e.g. AtomsTokens.axaml's IndustrySurfaceColor #E9E9EA) -- reads clearly as "no real
    /// photo loaded yet" without being visually jarring against the rest of the chrome.</summary>
    private static readonly Rgb24 BlankPlaceholderColor = new(0xE9, 0xE9, 0xEA);

    private async Task OpenEditorWithLoadedSourceAsync(IImageSource original, string fileName)
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
                _templateStore, _imageSourceWriter, new ReadyRackViewModel(_templateStore, _settingsStore, _localization, _readyRackLogger));
            editor.Applied += final => OnEditorApplied(fileName, editor, final);
            editor.Cancelled += OnEditorCancelled;
            editor.PropertyChanged += OnCurrentEditorPropertyChanged;
            _currentEditor = editor;
            // Real bug caught via real-window testing (2026-08-17): IsEditorOpen already toggled
            // true BEFORE this await-gated assignment runs (OnIsEditorOpenChanged already fired,
            // re-evaluating IsCurrentEditorBlankAndUntouched() while _currentEditor was still null
            // -- always false at that instant, regardless of whether this editor turns out to be
            // blank). Nothing else re-notifies once _currentEditor actually gets attached, so
            // SelectFavoriteMode/QuickSelectMode's CanExecute and CanChangeSourceOrMode's own
            // bindable value would silently stay stuck at their pre-open (disabled) reading forever
            // -- confirmed live: the mode ComboBox stayed visibly greyed out after Cancel
            // auto-reopened a fresh blank editor, even though the underlying state was correct.
            SelectFavoriteModeCommand.NotifyCanExecuteChanged();
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
            _currentEditor = null;
            _currentEditorIsBlank = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            EditorClosed?.Invoke();
        }
    }

    private void OnEditorApplied(string fileName, TxImageEditorPaneViewModel editor, IImageSource final)
    {
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
        _currentEditor = null;
        _currentEditorIsBlank = false;
        TransmitCommand.NotifyCanExecuteChanged();
        EditorClosed?.Invoke();
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
    /// invoke them again once the View swaps <c>ActiveEditor</c> to the new instance).</summary>
    private void CloseBlankEditorForReplacement()
    {
        IsEditorOpen = false;
        _currentEditor = null;
        _currentEditorIsBlank = false;
        EditorClosed?.Invoke();
    }

    private void OnEditorCancelled()
    {
        IsEditorOpen = false;
        _currentEditor = null;
        _currentEditorIsBlank = false;
        EditorClosed?.Invoke();

        // Backlog item (user request, 2026-08-17): "should ALWAYS open the editor by default" --
        // backing out via Cancel would otherwise leave the center column empty again, reintroducing
        // the exact friction this whole item was about. Re-open blank immediately, but ONLY when
        // nothing has ever been applied yet (SelectedFileName is set exclusively by
        // OnEditorApplied) -- this must NOT fire after cancelling a re-edit of an ALREADY applied
        // image (EditCurrentImageCommand), which would silently discard the applied state the
        // operator is still meant to see/transmit.
        if (SelectedFileName is null)
        {
            _ = OpenBlankEditorAsync();
        }
    }

    /// <summary>Re-evaluates <see cref="SelectFavoriteModeCommand"/>/<see cref="QuickSelectModeCommand"/>'s
    /// own <c>CanExecute</c> the moment the currently-open editor's <see cref="TxImageEditorPaneViewModel.HasUnsavedEdits"/>
    /// flips (typically false-&gt;true, the first real edit) -- <see cref="OnIsEditorOpenChanged"/>
    /// alone only re-evaluates on open/close, not on this finer-grained transition
    /// <see cref="IsCurrentEditorBlankAndUntouched"/> now also depends on.</summary>
    private void OnCurrentEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TxImageEditorPaneViewModel.HasUnsavedEdits))
        {
            SelectFavoriteModeCommand.NotifyCanExecuteChanged();
            QuickSelectModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanChangeSourceOrMode));
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
    /// <see cref="SelectedMode"/>/favorites/quick-grid/RX auto-follow (see
    /// <see cref="IsEditorOpen"/>'s own doc comment).</summary>
    [RelayCommand(CanExecute = nameof(CanEditCurrentImage))]
    private async Task EditCurrentImageAsync()
    {
        // CanExecute alone isn't a hard gate -- CommunityToolkit's IAsyncRelayCommand.ExecuteAsync
        // doesn't consult it, only Avalonia's Button.OnClick does (code-review finding, same
        // doctrine SelectFavoriteMode/QuickSelectMode already follow above). This body-level
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
                _templateStore, _imageSourceWriter, new ReadyRackViewModel(_templateStore, _settingsStore, _localization, _readyRackLogger),
                new TxImageEditorPaneViewModel.EditorInitialState(edit.CropRect, edit.PreserveAspect, edit.Adjustments, edit.RawOverlay, edit.TemplateVariables));
            // SelectedFileName! is safe here: only OnEditorApplied ever writes it, always in the
            // same assignment that sets _editState (:868-871 below) -- _editState being non-null at
            // this point (the guard above) guarantees SelectedFileName was set at the same time.
            var fileName = SelectedFileName!;
            editor.Applied += final => OnEditorApplied(fileName, editor, final);
            editor.Cancelled += OnEditorCancelled;
            editor.PropertyChanged += OnCurrentEditorPropertyChanged;
            _currentEditor = editor;
            // Real bug caught via real-window testing (2026-08-17): IsEditorOpen already toggled
            // true BEFORE this await-gated assignment runs (OnIsEditorOpenChanged already fired,
            // re-evaluating IsCurrentEditorBlankAndUntouched() while _currentEditor was still null
            // -- always false at that instant, regardless of whether this editor turns out to be
            // blank). Nothing else re-notifies once _currentEditor actually gets attached, so
            // SelectFavoriteMode/QuickSelectMode's CanExecute and CanChangeSourceOrMode's own
            // bindable value would silently stay stuck at their pre-open (disabled) reading forever
            // -- confirmed live: the mode ComboBox stayed visibly greyed out after Cancel
            // auto-reopened a fresh blank editor, even though the underlying state was correct.
            SelectFavoriteModeCommand.NotifyCanExecuteChanged();
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
    /// without re-checking it.</summary>
    private bool CanTransmit() => _loadedImage is not null && !IsTransmitting;

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
        TransmitProgress = 0;
        TransmitCommand.NotifyCanExecuteChanged();
        StopTransmitCommand.NotifyCanExecuteChanged();

        _transmitCts = new CancellationTokenSource();
        try
        {
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
        }
        catch (Exception ex)
        {
            Log.TransmitFailed(_logger, mode.Id, ex);
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.TransmitFailed");
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
        }
    }

    private bool CanStopTransmit() => IsTransmitting;

    [RelayCommand(CanExecute = nameof(CanStopTransmit))]
    private void StopTransmit()
    {
        Log.StopTransmitInvoked(_logger);
        _transmitCts?.Cancel();
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
    /// completion on its own with nothing left to stop it early.</summary>
    public void Dispose() => _transmitCts?.Cancel();

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

        [LoggerMessage(Level = LogLevel.Error, Message = "Persisting radio safety settings failed")]
        public static partial void PersistSafetySettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SWR auto-cutoff triggered: SWR={Swr}, threshold={Threshold}")]
        public static partial void SwrCutoffTriggered(ILogger logger, float swr, double threshold);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectFavoriteMode invoked: {ModeId}")]
        public static partial void SelectFavoriteModeInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "QuickSelectMode invoked: {ModeId}")]
        public static partial void QuickSelectModeInvoked(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QuickSelectMode pressed for unknown mode id {ModeId} -- no matching AvailableModes entry")]
        public static partial void QuickSelectModeUnknownId(ILogger logger, string modeId);

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

        [LoggerMessage(Level = LogLevel.Information, Message = "Transmit stopped manually: mode={ModeId}")]
        public static partial void TransmitStoppedManually(ILogger logger, string modeId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Transmit failed: mode={ModeId}")]
        public static partial void TransmitFailed(ILogger logger, string modeId, Exception ex);

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

/// <summary>One checkable row in the "Edit favorites..." popup -- <see cref="Mode"/> never changes
/// after construction, only <see cref="IsSelected"/> does (toggled by the checkbox).</summary>
public sealed partial class FavoriteModeOptionViewModel : ObservableObject
{
    public FavoriteModeOptionViewModel(SstvModeDefinition mode, bool isSelected)
    {
        Mode = mode;
        _isSelected = isSelected;
    }

    public SstvModeDefinition Mode { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>One quick-select button in the favorites row -- carries its own <see cref="SelectCommand"/>
/// (the parent's <see cref="TxControlsPaneViewModel.SelectFavoriteModeCommand"/>, set once at
/// construction), same shape as <see cref="StockEntryViewModel"/> and for the identical reason (see
/// that type's own doc comment).</summary>
public sealed record FavoriteModeButtonViewModel(SstvModeDefinition Mode, System.Windows.Input.ICommand SelectCommand);

/// <summary>One row of the Mode-timing-reference table (mock2's own card) -- see
/// <see cref="TxControlsPaneViewModel.ModeTimingRows"/>'s doc comment for how it's computed.</summary>
public sealed record ModeTimingRowViewModel(string ModeName, int Lines, double LineMs, double FrameSeconds);

/// <summary>One appended sample of <see cref="TxControlsPaneViewModel.TelemetryHistory"/> -- see that
/// property's own doc comment.</summary>
public sealed record TxTelemetrySample(DateTimeOffset Timestamp, float? SwrRatio, float? AlcLevel, float? PowerPercent);
