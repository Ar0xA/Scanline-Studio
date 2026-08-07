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
    private readonly ILogger<TxControlsPaneViewModel> _logger;

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

    private IImageSource? _loadedImage;

    /// <summary>The native-resolution original plus the crop/stretch/overlay choices applied to it --
    /// retained (not just the final mode-sized image) so a later mode change can re-run
    /// Crop→Resize→ApplyOverlay against the *new* mode's dimensions instead of stretching/cropping an
    /// already-cropped image a second time. Normalized (0..1) coordinates make this composition valid
    /// across modes with different pixel dimensions.</summary>
    private sealed record EditState(IImageSource Original, NormalizedRect CropRect, bool PreserveAspect, ImageOverlay Overlay);

    private EditState? _editState;

    /// <summary>Guards against a second pick starting while an editor is already open/loading --
    /// simpler than a cancel-and-replace token for what's a single, short-lived, user-driven
    /// sequence (the view is expected to disable picking while an editor is open; this is the
    /// view-model-level backstop).</summary>
    private bool _isEditorOpen;

    [ObservableProperty]
    private SstvModeDefinition? _selectedMode;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? _selectedFileName;

    [ObservableProperty]
    private bool _isTransmitting;

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

    public TxControlsPaneViewModel(
        ISstvSessionService sstvSession,
        IImageFileLoader imageFileLoader,
        IStockImageLibrary stockLibrary,
        ITransmitImagePreparer preparer,
        IFilePickerService filePickerService,
        ILocalizationService localization,
        ISettingsStore settingsStore,
        IRadioSessionService radioSession,
        ILogger<TxControlsPaneViewModel> logger)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _stockLibrary = stockLibrary;
        _preparer = preparer;
        _filePickerService = filePickerService;
        _localization = localization;
        _settingsStore = settingsStore;
        _radioSession = radioSession;
        _logger = logger;

        AvailableModes = sstvSession.AvailableModes;
        _selectedMode = AvailableModes.Count > 0 ? AvailableModes[0] : null;
        ModeTimingRows = AvailableModes
            .Select(m => new ModeTimingRowViewModel(m.DisplayName, m.ImageHeight, m.LineDurationMs, m.LineDurationMs * m.ImageHeight / 1000.0))
            .ToList();

        sstvSession.ModeDetected += OnModeDetected;
        radioSession.StateChanges.Subscribe(OnRadioStateChanged);

        // Best-effort initial load, same reasoning as RxHistoryPaneViewModel's constructor -- a
        // failure here leaves the strip empty rather than blocking construction.
        _ = RefreshStockLibraryAsync();
        _ = LoadTxPaneUiSettingsAsync();
        _ = LoadSafetySettingsAsync();
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
    /// exist, so the Output card's Drive slider (moved here from the header) can bind straight to
    /// <c>RadioStatus.TxVolumePercent</c> -- the single real source of truth for that value.
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
    /// fresh <c>new TxPaneUiSettings { ... }</c> -- the TX volume slider (a different view-model,
    /// spec/14-roadmap.md's Piece 5) owns <see cref="TxPaneUiSettings.TxVolumePercent"/> in this same
    /// section, and a from-scratch write here would silently clobber it.</summary>
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
                _suppressSafetyPersist = true;
                SwrCutoffEnabled = spec.SwrCutoffEnabled;
                SwrCutoffThreshold = spec.SwrCutoffThreshold;
                _suppressSafetyPersist = false;
            });
        }
        catch (Exception ex)
        {
            Log.LoadSafetySettingsFailed(_logger, ex);
        }
    }

    /// <summary>Error, not Warning -- a silently-failed SWR-cutoff-setting write means the user's
    /// safety setting didn't take effect with nothing telling them so.</summary>
    private async Task PersistSafetySettingsAsync()
    {
        try
        {
            await _radioSession.SaveSafetySettingsAsync(new RadioSafetySpec(SwrCutoffEnabled, SwrCutoffThreshold));
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

        _ = PersistSafetySettingsAsync();
    }

    partial void OnSwrCutoffThresholdChanged(double value)
    {
        if (_suppressSafetyPersist)
        {
            return;
        }

        _ = PersistSafetySettingsAsync();
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
        if (!AutoFollowRxMode)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => SelectedMode = mode);
    }

    [RelayCommand]
    private void SelectFavoriteMode(SstvModeDefinition mode)
    {
        Log.SelectFavoriteModeInvoked(_logger, mode.Id);
        SelectedMode = mode;
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
            Log.PickImageFileFailed(_logger, ex);
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

    /// <summary><paramref name="source"/> is either a <see cref="StockImageEntry"/> or a
    /// <see cref="string"/> file path. Loads it at native resolution and hands the resulting
    /// <see cref="TxImageEditorPaneViewModel"/> to whoever is listening on <see cref="EditorOpened"/> --
    /// this VM does not touch Dock/window placement itself.</summary>
    private async Task OpenEditorForSourceAsync(object source, string fileName)
    {
        if (_isEditorOpen || SelectedMode is not { })
        {
            return;
        }

        _isEditorOpen = true;
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
            Log.LoadTxSourceImageFailed(_logger, fileName, ex);
            _isEditorOpen = false;
            ErrorMessage = _localization.GetString("Panes.TxControls.Error.LoadFailed");
            return;
        }

        // SelectedMode may have changed while the original was loading -- always target whatever
        // mode is current NOW, not the one in effect when the pick started.
        if (SelectedMode is not { } mode)
        {
            _isEditorOpen = false;
            return;
        }

        var editor = new TxImageEditorPaneViewModel(original, mode, _preparer);
        editor.Applied += final => OnEditorApplied(fileName, original, editor, final);
        editor.Cancelled += OnEditorCancelled;
        EditorOpened?.Invoke(editor);
    }

    private void OnEditorApplied(string fileName, IImageSource original, TxImageEditorPaneViewModel editor, IImageSource final)
    {
        _editState = new EditState(original, editor.CropRect, editor.PreserveAspect, editor.Overlay);
        _loadedImage = final;
        PreviewImage = ImageSourceBitmapConverter.ToBitmap(final);
        SelectedFileName = fileName;
        ErrorMessage = null;
        _isEditorOpen = false;
        TransmitCommand.NotifyCanExecuteChanged();
        EditorClosed?.Invoke();
    }

    private void OnEditorCancelled()
    {
        _isEditorOpen = false;
        EditorClosed?.Invoke();
    }

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

    /// <summary>Re-runs Crop→Resize→ApplyOverlay against the retained <see cref="EditState"/>'s
    /// original at the new mode's dimensions -- normalized (0..1) crop/overlay coordinates make this
    /// valid across modes. This is synchronous CPU work against an already-in-memory original (no
    /// file/network I/O like the old flat resize-reload did), so there is no async race to guard
    /// against here -- the old cancel-and-replace <c>CancellationTokenSource</c> machinery existed
    /// specifically for that I/O race and would be unused complexity now that the source is cached.</summary>
    partial void OnSelectedModeChanged(SstvModeDefinition? value)
    {
        Log.SelectedModeChanged(_logger, value?.Id);
        if (value is null || _editState is not { } edit)
        {
            _loadedImage = null;
            PreviewImage = null;
            TransmitCommand.NotifyCanExecuteChanged();
            return;
        }

        var cropped = _preparer.Crop(edit.Original, edit.CropRect);
        var resized = _preparer.Resize(cropped, value.ImageWidth, value.ImageHeight, edit.PreserveAspect);
        var final = _preparer.ApplyOverlay(resized, edit.Overlay);

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading radio safety settings failed")]
        public static partial void LoadSafetySettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Persisting radio safety settings failed")]
        public static partial void PersistSafetySettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SWR auto-cutoff triggered: SWR={Swr}, threshold={Threshold}")]
        public static partial void SwrCutoffTriggered(ILogger logger, float swr, double threshold);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectFavoriteMode invoked: {ModeId}")]
        public static partial void SelectFavoriteModeInvoked(ILogger logger, string modeId);

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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading TX source image failed: {FileName}")]
        public static partial void LoadTxSourceImageFailed(ILogger logger, string fileName, Exception ex);

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
