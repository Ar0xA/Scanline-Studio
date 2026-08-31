using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Imaging;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Browsable list of past received images, sourced by the fixed Gallery tab (and the
/// Receive tab's "Previous frames" strip) — see spec/07-image-pipeline.md's "RX history" section.
/// Deliberately does NOT depend on <c>ISstvSessionService</c> or touch
/// <see cref="ScanlineStudio.Abstractions.Imaging.IReceivedImageBuffer"/> at all — selecting a
/// history entry loads a separate, read-only <see cref="PreviewImage"/>; browsing history must
/// never appear to interrupt or corrupt a live RX decode in progress
/// (<c>RxImagePaneViewModel</c> owns that live binding exclusively).
///
/// <b>Live-updates now (batch 7, spec/16-gui-wiring-survey.md's own PARTIAL finding fixed)</b>:
/// subscribes to <see cref="IReceiveHistoryStore.Recorded"/>, so both the Gallery tab's own list and
/// the Receive tab's "Previous frames" strip (same shared DI-singleton instance,
/// <c>MainViewModel.RxHistory</c>) refresh automatically as new frames land during an active
/// session, not just at construction/manual-refresh/filter-change as before.</summary>
public sealed partial class RxHistoryPaneViewModel : ViewModelBase
{
    private const int ThumbnailMaxDimension = 96;
    private const int PreviewMaxDimension = 512;

    /// <summary>Same debounce window/rationale as <c>RadioStatusViewModel.TxVolumePercentChanged</c>'s
    /// own persist debounce -- avoids one settings write per keystroke, and avoids overlapping
    /// un-awaited <see cref="IReceiveHistoryStore.SetNoteAsync"/> calls racing each other.</summary>
    private static readonly TimeSpan NotePersistDebounce = TimeSpan.FromMilliseconds(600);

    private readonly IReceiveHistoryStore _historyStore;
    private readonly ILocalizationService _localization;
    private readonly ILogger<RxHistoryPaneViewModel> _logger;
    private readonly ILogbookSessionService _logbookSession;
    private readonly ILogger<QsoLinkWindowViewModel> _qsoLinkLogger;
    private readonly IReceivedFrameExporter _frameExporter;
    private readonly IFilePickerService _filePicker;
    private readonly ISettingsStore _settingsStore;
    private readonly IUrlLauncher _urlLauncher;
    private readonly IClipboardImageService _clipboardImageService;
    private readonly ILogger<ImageViewerWindowViewModel> _imageViewerLogger;

    /// <summary>Chains <see cref="PersistFlaggedAsync"/> calls so a rapid double-toggle can't
    /// complete out of order -- see that method's own doc comment. Deliberately a plain
    /// <see cref="Task"/> field, not a <see cref="SemaphoreSlim"/>/other <see cref="IDisposable"/>
    /// primitive: this ViewModel isn't (and doesn't otherwise need to be) disposable, matching the
    /// existing convention elsewhere in this class of preferring non-disposable coordination (e.g.
    /// <c>_notePersistCts</c>'s own cancel-and-drop pattern over anything requiring cleanup).</summary>
    private Task _pendingFlagPersist = Task.CompletedTask;

    // Auditor-caught race (batch 7): RefreshAsync captures its own `filter` at entry and is called
    // fire-and-forget from multiple independent triggers now (OnRecorded, OnShowTodayOnlyChanged, the
    // user's own RefreshCommand click) -- two overlapping calls can't corrupt Entries itself (no
    // await between Clear() and the Add loop, both resume on the UI thread), but WHICHEVER call
    // finishes LAST wins, even if it started first and is now describing a stale filter. A live
    // session raises the trigger rate from "user clicks" to "every decoded frame," making this newly
    // reachable in practice, not just in theory. Bumped at the START of every RefreshAsync call
    // (before its own await); a call whose captured generation no longer matches this field's
    // then-current value by the time it's about to mutate Entries silently discards its own results
    // instead of applying them.
    private int _refreshGeneration;

    /// <summary>Guards <see cref="ReconcileDiskThenRefreshAsync"/> so a disk scan only runs once per
    /// app session (repeatedly flipping back to the Gallery tab must not re-scan the whole images
    /// folder every time) -- see that method's own doc comment for the full reasoning.</summary>
    private bool _hasReconciledDiskThisSession;

    // Auditor-caught UX issue (batch 7): every RefreshAsync call builds brand-new
    // RxHistoryEntryViewModel instances, so even after re-selecting the SAME logical entry by
    // Entry.Id, OnSelectedEntryChanged still sees a different object reference and (without this
    // field) would null PreviewImage and re-decode the same file from disk -- a visible flicker on
    // every incoming frame while a user is just looking at an old one. Tracks which entry's preview
    // is ACTUALLY currently loaded, independent of RxHistoryEntryViewModel's own object identity.
    private string? _previewedEntryId;

    /// <summary>Tracks which entry's <see cref="SelectedEntryNote"/>/<see cref="SelectedEntryIsFlagged"/>
    /// are currently loaded -- a DELIBERATELY SEPARATE field from <see cref="_previewedEntryId"/>
    /// (auditor round-3 blocker fix): <see cref="_previewedEntryId"/> gets nulled by
    /// <see cref="LoadPreviewAsync"/> on a failed preview load, which is unrelated to whether the
    /// user's in-progress note/flag edit for that same entry is still live -- reusing one field for
    /// both let a same-Id re-select (live refresh, or <see cref="UpdateEntryInPlace"/>'s own
    /// <see cref="SelectedEntry"/> reassignment) reload and clobber the edit whenever the two
    /// concerns' state happened to disagree.</summary>
    private string? _loadedEditsEntryId;

    // Auditor round-2 catch (batch 7): the Gallery/Previous-frames ListBox's SelectedItem binding is
    // TwoWay by default (confirmed via reflection against Avalonia.Controls.Primitives
    // .SelectingItemsControl.SelectedItemProperty's DirectPropertyMetadata), so RefreshAsync's own
    // Entries.Clear() synchronously pushes SelectedEntry = null back into this VM *before* the
    // re-select a few lines later runs -- without this flag, that transient null would already have
    // cleared _previewedEntryId and PreviewImage, making the _previewedEntryId skip in
    // OnSelectedEntryChanged a no-op for the exact case it exists to fix. Set around the
    // Clear()/repopulate/re-select block only.
    private bool _isRepopulating;

    // Auditor round-2 nit (batch 7): LoadPreviewAsync has no ordering guard against overlapping
    // calls (rapid selection changes) -- bumped at the start of every LoadPreviewAsync call, checked
    // immediately before it applies its own result, same "discard a superseded async result" pattern
    // as _refreshGeneration above.
    private int _previewGeneration;

    // Tier B audit finding: same "discard a superseded async result" pattern as _refreshGeneration,
    // for LoadFramesTodayCountAsync -- see that method's own comment.
    private int _framesTodayGeneration;

    /// <summary>Guards <see cref="OnSelectedEntryNoteChanged"/>/<see cref="OnSelectedEntryIsFlaggedChanged"/>
    /// while <see cref="OnSelectedEntryChanged"/> is itself assigning <see cref="SelectedEntryNote"/>/
    /// <see cref="SelectedEntryIsFlagged"/> from the newly-selected entry -- same "suppress the
    /// persist-on-load echo" convention as <c>RadioStatusViewModel._suppressVolumePersist</c>, without
    /// this a fresh selection would immediately re-save its own just-loaded value back to the store.</summary>
    private bool _suppressSelectedEntryEdits;

    private CancellationTokenSource? _notePersistCts;

    [ObservableProperty]
    private RxHistoryEntryViewModel? _selectedEntry;

    [ObservableProperty]
    private Bitmap? _previewImage;

    // T0-11 (production_audit.md): disposes the OLD bitmap on every reassignment (including the
    // `= null` clear sites, not just the ToBitmap-call ones) -- CommunityToolkit's generated
    // On<Prop>Changed hook fires on every writer of this property automatically. Deferred, not
    // synchronous: the hook itself fires BEFORE PropertyChanged, so a synchronous dispose here
    // would predate the binding seeing the new value; Background priority also gives Avalonia's
    // compositor a chance to finish any render pass still referencing the old bitmap. Guarded on
    // WriteableBitmap specifically -- only the bitmaps this codebase itself constructs via
    // ImageSourceBitmapConverter are safe to assume disposable here. Deliberately does NOT apply
    // to thumbnails (RxHistoryEntryViewModel.Thumbnail) -- those are shared with
    // ImageViewerWindowViewModel while a viewer window is open, and UpdateEntryInPlace
    // deliberately carries an old thumbnail forward into a replacement record; see this class's
    // own doc comment / production_audit.md's T0-11 entry for why thumbnail disposal is a
    // separate, deliberately-deferred fix.
    partial void OnPreviewImageChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        if (oldValue is WriteableBitmap old)
        {
            Dispatcher.UIThread.Post(() => old.Dispose(), DispatcherPriority.Background);
        }
    }

    /// <summary>Gallery Selected-frame panel's editable Note field -- backs the real, already-built
    /// <see cref="IReceiveHistoryStore.SetNoteAsync"/> (its own doc comment explicitly names this
    /// exact UI as its intended consumer; nothing called it before this). New UI, no mock2 slot for
    /// it -- same "new UI, real gap" precedent as <see cref="SelectLatestCommand"/>. Deliberately
    /// SEPARATE from <c>SelectedEntry.Entry.Note</c> (an immutable record field) rather than binding
    /// the `TextBox` directly to it -- needs its own settable property to debounce-persist through.</summary>
    [ObservableProperty]
    private string? _selectedEntryNote;

    /// <summary>Gallery Selected-frame panel's Flag toggle -- same reasoning as
    /// <see cref="SelectedEntryNote"/>, backing the real <see cref="IReceiveHistoryStore.SetFlaggedAsync"/>.
    /// Persisted immediately on toggle (a discrete click, not a continuous drag like the Note
    /// `TextBox` -- no debounce needed, same distinction <c>RadioStatusViewModel.TxVolumePercent</c>'s
    /// own debounced slider draws against a discrete-click control).</summary>
    [ObservableProperty]
    private bool _selectedEntryIsFlagged;

    /// <summary>Surfaces a <see cref="IReceiveHistoryStore.SetNoteAsync"/>/<see cref="IReceiveHistoryStore.SetFlaggedAsync"/>
    /// failure to the user -- both methods' own doc comments say a missing-entry return "the caller
    /// ... is expected to surface that to the user, not silently ignore it," even though no
    /// automatic deletion path exists in production today (see `docs/removed-features.md`). Same
    /// `ErrorMessage` convention as every other pane ViewModel in this app.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Export-frame-only success feedback (`ExportFrameAsync`) -- deliberately a SEPARATE
    /// property from <see cref="ErrorMessage"/>, not reused for the success case: that one renders
    /// in `IndustryDanger` red (`MainWindow.axaml`), and a re-encode's actual effect (did the chosen
    /// JPEG quality really apply?) is otherwise invisible to the user, unlike this pane's other
    /// silent-on-success actions (Note/Flag persist) which have no equivalent "did it really work"
    /// ambiguity. Mirrors <c>LogbookPaneViewModel.StatusMessage</c>'s own established pattern for
    /// the same kind of "confirm an export actually happened" feedback.</summary>
    [ObservableProperty]
    private string? _exportStatusMessage;

    /// <summary>Gallery tab's All/Today filter (spec/09-ui.md) -- real, backed by
    /// <see cref="IReceiveHistoryStore.QueryAsync"/>'s own <c>From</c>/<c>To</c> filter fields.
    /// Defaults to <see langword="true"/>, matching the mock2 draft's own default selection. A
    /// server-side query filter, unlike <see cref="SearchText"/>/<see cref="FilterUnloggedOnly"/>/
    /// <see cref="FilterFlaggedOnly"/> below, which run client-side over <see cref="Entries"/> --
    /// see <see cref="FilteredEntries"/>'s own doc comment for why.</summary>
    [ObservableProperty]
    private bool _showTodayOnly = true;

    /// <summary>Gallery tab's free-text search box -- client-side substring match (case-insensitive)
    /// against <see cref="ReceiveHistoryEntry.Note"/> and <see cref="ReceiveHistoryEntry.ModeId"/>,
    /// the only two fields on the entry a free-text search can honestly claim to cover today --
    /// there is still no callsign/grid field on <see cref="ReceiveHistoryEntry"/> to search
    /// (spec/16-gui-wiring-survey.md), so this does NOT search those, despite an earlier watermark
    /// implying it did (fixed alongside this, see <c>Panes.RxHistory.SearchWatermark</c>).</summary>
    [ObservableProperty]
    private string? _searchText;

    /// <summary>Gallery tab's "Unlogged" filter toggle -- <see cref="ReceiveHistoryEntry.LinkedQsoId"/>
    /// IS now set by real code (<c>QsoLinkWindowViewModel.LinkSelectedAsync</c>/
    /// <c>CreateAndLinkAsync</c>), unlike when this filter was first scoped out as unbuildable
    /// (spec/14-roadmap.md's own now-stale backlog note, corrected 2026-08-22). Client-side, not a
    /// new <see cref="ReceiveHistoryFilter"/> field -- worth revisiting for a server-side query
    /// parameter if the row count grows large enough to matter now that the store has no automatic
    /// retention cap (`docs/removed-features.md`, 2026-08-26); not a problem in practice yet.</summary>
    [ObservableProperty]
    private bool _filterUnloggedOnly;

    /// <summary>Gallery tab's "Flagged" filter toggle -- client-side over <see cref="Entries"/>' own
    /// already-real <see cref="ReceiveHistoryEntry.IsFlagged"/>, same "revisit if the row count grows"
    /// caveat as <see cref="FilterUnloggedOnly"/> above.</summary>
    [ObservableProperty]
    private bool _filterFlaggedOnly;

    /// <summary>Resolved saved-image folder for the Gallery tab's Storage card -- real, loaded
    /// once via <see cref="IReceiveHistoryStore.GetImagesDirectoryAsync"/>.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenStorageFolderCommand))]
    private string? _imagesDirectory;

    /// <summary>ui_transition_plan.md step 9 (T2-7): every received frame is already auto-archived
    /// here (ReceiveHistoryRecorder writes it on every completed decode, not just on an explicit
    /// Export/Save) -- this makes that folder actually reachable, not just readable as text, same
    /// "open the containing folder" idiom as <c>ImageViewerWindowViewModel.OpenFileLocation</c>.</summary>
    private bool CanOpenStorageFolder() => ImagesDirectory is not null;

    [RelayCommand(CanExecute = nameof(CanOpenStorageFolder))]
    private void OpenStorageFolder()
    {
        if (ImagesDirectory is not { } directory)
        {
            return;
        }

        // Cleared on entry, same convention as every other command in this class (e.g.
        // DeleteSelectedEntryAsync/ExportFrameAsync below) -- otherwise a stale error from a
        // PREVIOUS failed attempt keeps showing after the operator fixes the underlying problem
        // and retries successfully.
        ErrorMessage = null;

        // Code-review finding: the resolved default (~/Pictures/ScanlineStudio/History) is only
        // ever CREATED on the first saved frame (ReceiveHistoryRecorder's own doc comment) -- on a
        // fresh profile with nothing received yet, this button was enabled but pointed at a folder
        // that doesn't exist, so IUrlLauncher.Open's own swallow-and-log Process.Start failure left
        // the operator with no folder and no explanation. Same "validate by creating" precedent as
        // IReceiveHistoryStore.SetImagesDirectoryAsync's own doc comment for a user-chosen folder.
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            Log.OpenStorageFolderFailed(_logger, directory, ex);
            ErrorMessage = _localization.GetString("Panes.RxHistory.Error.OpenStorageFolderFailed");
            return;
        }

        _urlLauncher.Open(directory);
    }

    /// <summary>Free space, in gibibytes, on <see cref="ImagesDirectory"/>'s own volume -- real,
    /// via <see cref="DriveInfo"/>. <see langword="null"/> when the read fails (path unmounted,
    /// permission denied, etc.), rendered as an honest em-dash by <see cref="DiskFreeDisplay"/>.
    /// Refreshed on the same cadence as <see cref="ImagesDirectory"/> itself (construction +
    /// Storage settings dialog close) -- not polled live, matching the existing "loaded when the
    /// directory resolves" convention, not a new live-telemetry class.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiskFreeDisplay))]
    private double? _diskFreeGigabytes;

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: resolved auto-save-audio
    /// folder for the Gallery tab's Storage card -- loaded once via
    /// <see cref="IReceiveHistoryStore.GetAudioSettingsAsync"/>, same "loaded when the tab loads,
    /// not polled live" cadence as <see cref="ImagesDirectory"/> above.</summary>
    [ObservableProperty]
    private string? _audioDirectory;

    /// <summary>Total bytes across every <c>*.wav</c> file directly in <see cref="AudioDirectory"/> --
    /// real, non-recursive (see <see cref="UpdateAudioStorageBytesAsync"/>'s own doc comment for why
    /// that also keeps <c>SstvSessionService</c>'s own <c>scratch/</c> subtree out of this figure).
    /// <see langword="null"/> when the directory doesn't exist yet (nothing has auto-saved there yet)
    /// or the read fails, rendered as an honest em-dash by <see cref="AudioStorageBytesDisplay"/> --
    /// same convention as <see cref="DiskFreeGigabytes"/> above.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioStorageBytesDisplay))]
    private long? _audioStorageBytes;

    /// <summary>Gallery tab's "Received" header count caption -- real, recomputed off
    /// <see cref="Entries"/>' own <see cref="ObservableCollection{T}.CollectionChanged"/> rather
    /// than duplicated as a separately-maintained counter.</summary>
    [ObservableProperty]
    private string _entryCountText = string.Empty;

    /// <summary>Status bar's "frames today" readout -- a SEPARATE, independent query from
    /// <see cref="ShowTodayOnly"/>'s own Gallery-tab filter (always "today," regardless of whatever
    /// the Gallery tab's own All/Today toggle currently shows), so the status bar doesn't silently
    /// change meaning based on unrelated Gallery UI state. Loaded once at construction, same
    /// "best-effort, not re-fetched live" convention as <c>TxControlsPaneViewModel.OutputDeviceName</c>/
    /// <c>RxImagePaneViewModel.CaptureDeviceName</c> -- a new frame arriving doesn't currently bump
    /// this count until the pane is reconstructed (spec/17-rx-telemetry-feasibility.md: no
    /// history-changed event exists anywhere in this codebase to hook a live refresh off of).
    ///
    /// <b>Auditor-caught correction</b>: an earlier version of this comment/implementation anchored
    /// to UTC midnight on the claim that <c>ReceivedAt</c> is always stored UTC -- FALSE.
    /// <c>ReceiveHistoryRecorder.RecordCompletedImageAsync</c>/<c>RecordAbandonedImageAsync</c> both
    /// write <c>DateTimeOffset.Now</c> (LOCAL offset). Fixed to match <see cref="ShowTodayOnly"/>'s
    /// own `DateTime.Today` (local) convention below -- the two filters are now genuinely consistent,
    /// not a "separate, pre-existing inconsistency" as an earlier version of this comment claimed.
    /// T1-16 (production_audit.md) update: <c>SqliteReceiveHistoryStore</c>'s own `From`/`To` filter
    /// used to be a lexicographic TEXT compare on `ReceivedAt`'s own `ToString("O")` value, which
    /// only stayed correct when the query's own offset matched the stored rows' -- since fixed
    /// (queries now filter on a separate, always-UTC `ReceivedAtUtc` column, genuinely instant-based
    /// regardless of either side's offset). The local-anchoring choice here remains correct and
    /// unchanged for a DIFFERENT reason: "today" means the local calendar day, which only
    /// `DateTime.Today` (not a UTC anchor) actually captures -- it was never the offset-matching
    /// workaround alone that made this right.
    ///
    /// Also includes <see cref="ReceiveDecodeState.Abandoned"/> (partial) entries alongside
    /// <see cref="ReceiveDecodeState.Completed"/> ones -- <see cref="ReceiveHistoryFilter"/> has no
    /// `DecodeState` field to narrow by, so this counts every row received today, not just
    /// successfully completed images.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FramesTodayDisplay))]
    private int _framesTodayCount;

    /// <summary>Fires with a freshly-constructed <see cref="QsoLinkWindowViewModel"/> whenever
    /// <see cref="OpenInLogCommand"/> runs -- same "carries the freshly-resolved dialog VM" shape as
    /// <c>MainViewModel.OptionsRequested</c>, consumed by <c>MainWindow.axaml.cs</c> to construct and
    /// show the actual <c>QsoLinkWindowView</c>.</summary>
    public event Action<QsoLinkWindowViewModel>? QsoLinkRequested;

    /// <summary>ui_transition_plan.md step 3 (T1-5 + T2-6). Same "carries the freshly-resolved
    /// dialog VM" shape as <see cref="QsoLinkRequested"/> above, handled in MainWindow.axaml.cs.
    /// </summary>
    public event Action<ImageViewerWindowViewModel>? ImageViewerRequested;

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: "Re-decode this frame"
    /// carries the already-known audio file path, NOT a request this class fulfills itself -- this
    /// class deliberately does not depend on <c>ISstvSessionService</c> (see this file's own top
    /// doc comment: browsing history must never appear to interrupt a live RX decode), so the actual
    /// decode call is made by <c>RxImagePaneViewModel</c> (the class that already owns that
    /// responsibility for the Receive tab's own "Decode WAV…" button), wired from
    /// <c>MainWindow.axaml.cs</c> the same way <see cref="QsoLinkRequested"/>/<see cref="ImageViewerRequested"/>
    /// reach across to a sibling pane.</summary>
    public event Action<string>? RedecodeRequested;

    /// <summary>Opens the full-size viewer over <see cref="FilteredEntries"/> -- the Gallery grid's
    /// own currently-filtered/visible list, NOT the unfiltered <see cref="Entries"/>, so
    /// Previous/Next inside the viewer only ever steps through what the operator can actually see
    /// selected behind it. A no-op if <paramref name="startEntry"/> can't be found there (e.g. a
    /// filter changed between the double-tap and this running -- not reachable synchronously
    /// today, but cheap to guard).</summary>
    [RelayCommand]
    private void OpenImageViewer(RxHistoryEntryViewModel startEntry)
    {
        var startIndex = FilteredEntries.IndexOf(startEntry);
        if (startIndex < 0)
        {
            return;
        }

        var viewerViewModel = new ImageViewerWindowViewModel(
            new List<RxHistoryEntryViewModel>(FilteredEntries), startIndex, _historyStore, _urlLauncher, _clipboardImageService, _localization, _imageViewerLogger);
        ImageViewerRequested?.Invoke(viewerViewModel);
    }

    public RxHistoryPaneViewModel(
        IReceiveHistoryStore historyStore,
        ILocalizationService localization,
        ILogger<RxHistoryPaneViewModel> logger,
        ILogbookSessionService logbookSession,
        ILogger<QsoLinkWindowViewModel> qsoLinkLogger,
        IReceivedFrameExporter frameExporter,
        IFilePickerService filePicker,
        ISettingsStore settingsStore,
        IUrlLauncher urlLauncher,
        IClipboardImageService clipboardImageService,
        ILogger<ImageViewerWindowViewModel> imageViewerLogger,
        IRxAudioAutoSaver audioAutoSaver)
    {
        _historyStore = historyStore;
        _localization = localization;
        _logger = logger;
        _logbookSession = logbookSession;
        _qsoLinkLogger = qsoLinkLogger;
        _frameExporter = frameExporter;
        _filePicker = filePicker;
        _settingsStore = settingsStore;
        _urlLauncher = urlLauncher;
        _clipboardImageService = clipboardImageService;
        _imageViewerLogger = imageViewerLogger;

        // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: auditor-caught round 1 -- this
        // was the ONLY missing subscriber for RxAudioAutoSaver.AudioAttached anywhere in the app, so a
        // just-received frame's in-memory entry never picked up AudioFilePath until some unrelated
        // later refresh re-queried the DB. Marshaled through Dispatcher -- see UpdateEntryInPlace's
        // own doc comment for why every OTHER in-place patch in this class already does the same
        // (AudioAttached's own contract also documents it can fire on an arbitrary background thread).
        audioAutoSaver.AudioAttached += (entryId, path) => Dispatcher.UIThread.Post(() => UpdateEntryInPlace(entryId, e => e with { AudioFilePath = path }));

        Entries.CollectionChanged += (_, _) =>
        {
            UpdateEntryCountText();
            SelectLatestCommand.NotifyCanExecuteChanged();
            UpdateFilteredEntries();
        };
        UpdateEntryCountText();
        _historyStore.Recorded += OnRecorded;

        // Best-effort initial load -- a failure here (e.g. history store not reachable yet) leaves
        // the pane empty rather than blocking construction; RefreshCommand lets the user retry.
        _ = RefreshAsync();
        _ = LoadImagesDirectoryAsync();
        _ = LoadAudioStorageInfoAsync();
        _ = LoadFramesTodayCountAsync();
    }

    public ObservableCollection<RxHistoryEntryViewModel> Entries { get; } = [];

    /// <summary>The Gallery grid's actual `ItemsSource` -- <see cref="Entries"/> narrowed by
    /// <see cref="SearchText"/>/<see cref="FilterUnloggedOnly"/>/<see cref="FilterFlaggedOnly"/>,
    /// recomputed client-side (see <see cref="UpdateFilteredEntries"/>) rather than as a new
    /// <see cref="IReceiveHistoryStore.QueryAsync"/> parameter. Deliberately a SEPARATE collection
    /// from <see cref="Entries"/>, not a replacement for it: the Receive tab's Previous-frames strip
    /// binds directly to <see cref="Entries"/> and must keep showing every recent frame regardless of
    /// whatever the Gallery tab's own filter row currently has active.</summary>
    public ObservableCollection<RxHistoryEntryViewModel> FilteredEntries { get; } = [];

    partial void OnSearchTextChanged(string? value) => UpdateFilteredEntries();

    partial void OnFilterUnloggedOnlyChanged(bool value) => UpdateFilteredEntries();

    partial void OnFilterFlaggedOnlyChanged(bool value) => UpdateFilteredEntries();

    private void UpdateFilteredEntries()
    {
        IEnumerable<RxHistoryEntryViewModel> query = Entries;

        if (FilterUnloggedOnly)
        {
            query = query.Where(e => e.Entry.LinkedQsoId is null);
        }

        if (FilterFlaggedOnly)
        {
            query = query.Where(e => e.Entry.IsFlagged);
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var needle = SearchText.Trim();
            query = query.Where(e =>
                (e.Entry.Note is not null && e.Entry.Note.Contains(needle, StringComparison.OrdinalIgnoreCase)) ||
                e.Entry.ModeId.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToList();

        FilteredEntries.Clear();
        foreach (var item in filtered)
        {
            FilteredEntries.Add(item);
        }
    }

    public string FramesTodayDisplay => _localization.GetString("MainWindow.StatusBar.FramesTodayValueFormat", FramesTodayCount);

    /// <summary>Status bar's Disk chip and the Gallery Storage card's "Disk free" row -- same
    /// underlying value, single source of truth rather than two independently-drifting reads.</summary>
    public string DiskFreeDisplay => DiskFreeGigabytes is { } gb
        ? _localization.GetString("Panes.RxHistory.DiskFreeValueFormat", gb)
        : "—";

    public string AudioStorageBytesDisplay => AudioStorageBytes is { } bytes
        ? _localization.GetString("Panes.RxHistory.AudioStorageValueFormat", bytes / 1_048_576.0)
        : "—";

    private void UpdateEntryCountText() => EntryCountText = Entries.Count switch
    {
        1 => _localization.GetString("Panes.RxHistory.EntryCountSingular"),
        var count => _localization.GetString("Panes.RxHistory.EntryCountFormat", count),
    };

    private async Task LoadFramesTodayCountAsync()
    {
        // Tier B audit finding: this used to have no ordering guard against overlapping calls,
        // unlike its co-dispatched sibling RefreshAsync (both are fired un-awaited from the same
        // OnRecorded callback, see that method's own doc comment) -- a burst of Recorded events
        // (e.g. a bulk-decoded WAV import) could race N overlapping QueryAsync calls here, and
        // whichever completed LAST would win even if it started FIRST and is now describing a
        // stale, lower count -- the exact same "last-completer-wins on a stale result" class
        // _refreshGeneration already guards RefreshAsync against. Same fix, own field.
        var generation = ++_framesTodayGeneration;
        try
        {
            var todayEntries = await _historyStore.QueryAsync(new ReceiveHistoryFilter(From: new DateTimeOffset(DateTime.Today)));
            if (generation != _framesTodayGeneration)
            {
                return;
            }

            FramesTodayCount = todayEntries.Count;
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as LoadImagesDirectoryAsync -- the status bar just shows 0.
            Log.LoadFramesTodayCountFailed(_logger, ex);
        }
    }

    partial void OnShowTodayOnlyChanged(bool value) => _ = RefreshAsync();

    /// <summary>Marshals to the UI thread itself -- <see cref="IReceiveHistoryStore.Recorded"/>'s own
    /// doc comment documents that it can fire from a decode-thread <c>Task.Run</c>, not the UI
    /// thread, same "subscriber's own responsibility" contract already established for
    /// <c>RxImagePaneViewModel.OnSaved</c>. Re-runs the SAME full query+re-thumbnail pass a manual
    /// Refresh click does (no lighter "just prepend one entry" path) -- simplest correct option, and
    /// this event fires at most once per completed/abandoned image (not once per line), so the cost
    /// class matches an ordinary user-triggered refresh, not a hot per-sample path that would need
    /// coalescing the way <see cref="IReceivedImageBuffer.Updated"/> does.</summary>
    private void OnRecorded(ReceiveHistoryEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        _ = RefreshAsync();
        _ = LoadFramesTodayCountAsync();
    });

    private bool CanSelectLatest() => Entries.Count > 0;

    /// <summary>"Jump to most recent" -- port of legacy's real <c>SBPrim</c> speed button, NOT
    /// <c>SBLatest</c> despite that name's misleading English reading (auditor-caught citation error
    /// in an earlier version of this comment): legacy's history nav is a ring buffer read via
    /// <c>UDHist-&gt;Position</c>, mapped in <c>UpdateHist</c> (<c>Main.cpp:6277-6281</c>) as
    /// <c>n = (m_wPnt-1) - Position</c> -- <c>SBPrimClick</c> (<c>Main.cpp:15851-15857</c>) sets
    /// <c>Position = 0</c>, which resolves to <c>n = m_wPnt-1</c>, the ring buffer's own most-recently-
    /// written slot (newest). <c>SBLatestClick</c> (<c>Main.cpp:6407-6413</c>) actually sets
    /// <c>Position = RxHist.m_Head.m_Cnt-1</c> -- the OLDEST slot still in the buffer -- and is
    /// deliberately NOT carried over here (see `docs/removed-features.md`'s own updated entry). No
    /// slot for either legacy button exists in mock2's own Gallery-tab draft (a plain
    /// click-any-thumbnail grid, no step-nav spinner or jump button drawn), so this is new UI, not a
    /// wiring pass; added because the underlying "jump to newest" gap is real and legacy-documented,
    /// not invented. <see cref="Entries"/> is already newest-first
    /// (<c>SqliteReceiveHistoryStore.QueryAsync</c>'s own <c>ORDER BY ReceivedAtUtc DESC</c>), so
    /// "newest" is simply the first entry already loaded -- no new query needed, unlike
    /// <see cref="RefreshAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectLatest))]
    private void SelectLatest() => SelectedEntry = Entries.FirstOrDefault();

    /// <summary>Not private: also called from <c>MainWindow.axaml.cs</c>'s own Storage settings
    /// dialog Closed handler (stub survey Tier 2, plan-review finding) so the Gallery Storage card
    /// picks up a just-saved directory change immediately, matching the existing precedent for
    /// re-running a load unconditionally on dialog close (<c>OptionsWindowViewModel.RequestClose</c>'s
    /// own Closed handler re-runs <c>LoadCallsignAsync</c>/output-device/identification loads the
    /// same way, whether or not anything on that specific tab actually changed).</summary>
    public async Task LoadImagesDirectoryAsync()
    {
        try
        {
            ImagesDirectory = await _historyStore.GetImagesDirectoryAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as RefreshAsync -- the Storage card just shows nothing.
            Log.GetImagesDirectoryFailed(_logger, ex);
            return;
        }

        UpdateDiskFreeSpace();
    }

    /// <summary>Best-effort, same reasoning as <see cref="LoadImagesDirectoryAsync"/> -- a failed
    /// read just leaves <see cref="DiskFreeGigabytes"/> <see langword="null"/> (shown as "—"),
    /// never surfaced as a user-facing error for a non-essential status readout.
    ///
    /// <b>Auditor-caught fix</b>: <see cref="ImagesDirectory"/> itself may not exist yet --
    /// <c>ReceiveHistorySettings.ResolveDirectoryAsync</c>'s own default (<c>~/Pictures/
    /// ScanlineStudio/History</c>) is only actually created on the FIRST saved frame
    /// (<c>ReceiveHistoryRecorder</c>) or by explicitly picking a folder in Storage settings
    /// (validate-by-creating) -- on a fresh profile with nothing received yet, constructing
    /// <see cref="DriveInfo"/> directly against it throws (<c>DriveNotFoundException</c> on Unix)
    /// and both readouts would stay stuck at "—" for the entire first session. Walks up to the
    /// nearest existing ancestor first -- the volume containing an about-to-exist directory is the
    /// same volume that will actually receive the images once it's created.</summary>
    private void UpdateDiskFreeSpace()
    {
        if (ImagesDirectory is null)
        {
            DiskFreeGigabytes = null;
            return;
        }

        try
        {
            var probePath = ImagesDirectory;
            while (!Directory.Exists(probePath))
            {
                var parent = Path.GetDirectoryName(probePath);
                if (string.IsNullOrEmpty(parent) || parent == probePath)
                {
                    break;
                }

                probePath = parent;
            }

            var drive = new DriveInfo(probePath);
            DiskFreeGigabytes = drive.AvailableFreeSpace / 1_073_741_824.0;
        }
        catch (Exception ex)
        {
            DiskFreeGigabytes = null;
            Log.GetDiskFreeSpaceFailed(_logger, ex);
        }
    }

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: same "loaded when the tab
    /// loads, not polled live" cadence as <see cref="LoadImagesDirectoryAsync"/> -- also re-run from
    /// <c>MainWindow.axaml.cs</c>'s own Storage settings dialog Closed handler, same reasoning as that
    /// method's own doc comment.</summary>
    public async Task LoadAudioStorageInfoAsync()
    {
        try
        {
            var audioSettings = await _historyStore.GetAudioSettingsAsync();
            AudioDirectory = audioSettings.Directory;
        }
        catch (Exception ex)
        {
            Log.GetAudioSettingsFailed(_logger, ex);
            return;
        }

        await UpdateAudioStorageBytesAsync();
    }

    /// <summary>Best-effort, same reasoning as <see cref="UpdateDiskFreeSpace"/> -- a directory that
    /// doesn't exist yet (nothing has auto-saved there yet) is NOT an error, just an honest "—".
    /// Only <c>*.wav</c> files count (auditor-caught round 1: an unfiltered sum would also count any
    /// foreign file a user happens to keep in a shared, non-dedicated folder, mislabeled as
    /// "auto-saved audio") -- non-recursive, so <c>SstvSessionService</c>'s own
    /// <c>{AudioDirectory}/scratch/{sessionGuid}/</c> subtree (still-in-flight receptions, up to its
    /// own separate 256 MB retention cap) is never double-counted here regardless.
    ///
    /// The enumeration + per-file <see cref="FileInfo"/> stat runs off the UI thread (auditor-caught
    /// round 1: a folder with many/large files would otherwise visibly hitch the UI, unlike
    /// <see cref="UpdateDiskFreeSpace"/>'s O(1) <see cref="DriveInfo"/> read next to it).</summary>
    private async Task UpdateAudioStorageBytesAsync()
    {
        if (AudioDirectory is not { } directory || !Directory.Exists(directory))
        {
            AudioStorageBytes = null;
            return;
        }

        try
        {
            AudioStorageBytes = await Task.Run(() => Directory.EnumerateFiles(directory, "*.wav").Sum(f => new FileInfo(f).Length));
        }
        catch (Exception ex)
        {
            AudioStorageBytes = null;
            Log.GetAudioStorageBytesFailed(_logger, ex);
        }
    }

    /// <summary>User-reported gap, 2026-08-26: files can exist in <see cref="ImagesDirectory"/> with
    /// no matching history row (e.g. the DB was lost/reset independently of the images folder --
    /// real report was ~85 images on disk against 1 DB row). Called when the Gallery tab is selected
    /// (<see cref="MainViewModel.OnSelectedTabIndexChanged"/>) -- matches the user's own stated
    /// trigger point exactly, not app startup, so this scan never runs for a session that never
    /// visits Gallery. Runs at most once per session (<see cref="_hasReconciledDiskThisSession"/>):
    /// the folder's own contents don't change from outside this app mid-session in any way a repeat
    /// scan would need to catch, and a large history folder makes repeated re-scans a real, avoidable
    /// cost. <see cref="IReceiveHistoryStore.ReconcileWithDiskAsync"/> deliberately does not raise
    /// <see cref="IReceiveHistoryStore.Recorded"/> for backfilled entries (see that method's own doc
    /// comment), so <see cref="RefreshAsync"/> is called explicitly here to make newly-imported rows
    /// actually visible -- nothing else would pick them up.</summary>
    public async Task ReconcileDiskThenRefreshAsync()
    {
        if (_hasReconciledDiskThisSession)
        {
            return;
        }

        _hasReconciledDiskThisSession = true;

        try
        {
            var imported = await _historyStore.ReconcileWithDiskAsync().ConfigureAwait(true);
            if (imported > 0)
            {
                Log.ReconcileImported(_logger, imported);
                await RefreshAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as RefreshAsync's own failure handling -- Gallery just
            // keeps showing whatever it already had.
            Log.ReconcileFailed(_logger, ex);
            ErrorMessage = _localization.GetString("Panes.RxHistory.Error.ReconcileFailed");
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Log.RefreshInvoked(_logger, ShowTodayOnly);

        // Captured BEFORE the query -- batch 7 made this method run far more often than before
        // (every IReceiveHistoryStore.Recorded event during an active session, not just a manual
        // click/filter change), and every refresh below builds brand-new RxHistoryEntryViewModel
        // instances (a record, no identity beyond reference equality) -- without re-selecting by ID
        // after repopulating, a user actively browsing history would have their selection (and the
        // preview it drives) silently wiped every time a new frame lands, a real UX regression this
        // batch would otherwise introduce.
        var selectedEntryId = SelectedEntry?.Entry.Id;
        var generation = ++_refreshGeneration;

        var filter = ShowTodayOnly
            ? new ReceiveHistoryFilter(From: new DateTimeOffset(DateTime.Today))
            : new ReceiveHistoryFilter();

        IReadOnlyList<ReceiveHistoryEntry> entries;
        try
        {
            entries = await _historyStore.QueryAsync(filter);
        }
        catch (Exception ex)
        {
            // Tier B audit finding: this used to log and return with no ErrorMessage set -- since
            // RefreshAsync is auto-fired on every incoming frame during an active session (OnRecorded),
            // a persistent failure (a locked/corrupt SQLite file) left the Gallery frozen on stale
            // contents forever with zero user-visible indication anything was wrong. Same
            // set-on-failure/clear-on-success pattern LogbookPaneViewModel.RefreshAsync already
            // established for the identical sibling gap (chunk 4 of this sweep).
            Log.QueryFailed(_logger, ex);
            ErrorMessage = _localization.GetString("Panes.RxHistory.Error.RefreshFailed");
            return;
        }

        var thumbnails = new List<RxHistoryEntryViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            Bitmap? thumbnail = null;
            try
            {
                var image = await _historyStore.LoadThumbnailAsync(entry, ThumbnailMaxDimension);
                thumbnail = ImageSourceBitmapConverter.ToBitmap(image);
            }
            catch (Exception ex)
            {
                // A missing/corrupt file for one entry must not blank the whole list -- that entry
                // just renders without a thumbnail.
                Log.LoadThumbnailFailed(_logger, ex);
            }

            thumbnails.Add(new RxHistoryEntryViewModel(entry, thumbnail));
        }

        if (generation != _refreshGeneration)
        {
            // A newer RefreshAsync call has already started (and will apply ITS OWN results) since
            // this one began -- discard this stale result rather than overwrite Entries with an
            // out-of-date filter's data.
            Log.RefreshDiscardedAsStale(_logger);
            return;
        }

        // Tier B audit finding (round 2): nulled here -- success path only, and only once this
        // call is confirmed to be the newest one (past the stale-discard check above) -- rather
        // than right after the try/catch. Clearing it earlier let an already-superseded refresh
        // wipe an error a NEWER, still-in-flight refresh had just set (overlapping refreshes from
        // a burst of OnRecorded events could race: B starts, B's query throws and sets
        // ErrorMessage, A's earlier query then returns and unconditionally nulled it before
        // discarding itself as stale). A stale error from an unrelated failure (export, note/flag
        // persist) still must not flash away and back just because a query happened to start --
        // that's still true, just checked after staleness instead of before.
        ErrorMessage = null;

        // Guards the transient SelectedEntry = null that Entries.Clear() below pushes back through
        // the Gallery ListBox's TwoWay SelectedItem binding, before the re-select a few lines down
        // runs -- see _isRepopulating's own doc comment.
        _isRepopulating = true;
        try
        {
            Entries.Clear();
            foreach (var item in thumbnails)
            {
                Entries.Add(item);
            }

            // Re-select by Entry.Id, not by object reference (every item above is a freshly-constructed
            // record) -- if the previously-selected frame no longer matches the current filter (e.g. it
            // aged out of ShowTodayOnly's own window), SelectedEntry simply stays null, matching what
            // already happens on a manual Refresh/filter-change today; this isn't a regression, only a
            // preservation of the CASE that already worked.
            if (selectedEntryId is not null)
            {
                SelectedEntry = Entries.FirstOrDefault(e => e.Entry.Id == selectedEntryId);
            }
        }
        finally
        {
            _isRepopulating = false;
        }

        // The previously-selected entry genuinely didn't survive this refresh (filtered out/trimmed)
        // -- reconcile the preview/edits state OnSelectedEntryChanged was prevented from touching
        // above. Checked independently (auditor round-3 fix, see _loadedEditsEntryId's own doc
        // comment for why these two fields can disagree, e.g. after a failed preview load already
        // nulled _previewedEntryId while _loadedEditsEntryId was still set).
        if (SelectedEntry is null && _previewedEntryId is not null)
        {
            _previewedEntryId = null;
            PreviewImage = null;
            // Auditor round-3 nit: also invalidate any in-flight LoadPreviewAsync for the
            // now-cleared entry -- otherwise a slow decode racing this reconcile could still land
            // afterward and set PreviewImage for an entry that's no longer selected or listed.
            _previewGeneration++;
        }

        if (SelectedEntry is null && _loadedEditsEntryId is not null)
        {
            _loadedEditsEntryId = null;
            // Auditor round-2 risk fix: without this, the greyed-out Note/Flagged controls kept
            // showing the vanished entry's last-loaded text/state (IsEnabled=false via the
            // SelectedEntry-is-null binding hides the CONTROLS, but the stale VALUES were still
            // sitting in these properties for whenever a NEW entry happens to reuse them transiently).
            //
            // Tier B audit finding: try/finally, not a bare set-then-reset -- these two property
            // sets raise PropertyChanged into live Avalonia bindings, which can throw; a throw here
            // used to leave _suppressSelectedEntryEdits stuck true for the process lifetime, silently
            // and permanently breaking every future note/flag edit for the rest of this VM's life
            // (same failure shape _isRepopulating's own two guarded sites already avoid).
            try
            {
                _suppressSelectedEntryEdits = true;
                SelectedEntryNote = null;
                SelectedEntryIsFlagged = false;
            }
            finally
            {
                _suppressSelectedEntryEdits = false;
            }
        }

        Log.RefreshCompleted(_logger, Entries.Count);
    }

    private bool CanOpenInLog() => SelectedEntry is not null;

    /// <summary>Opens the "Open in Log" picker/create dialog (spec: link an already-logged QSO to
    /// this frame, or log a new one on the spot) -- see <see cref="QsoLinkWindowViewModel"/>'s own
    /// doc comment for the full write-ordering/thread-affinity design. Posts the resulting
    /// <see cref="UpdateEntryInPlace"/> call through <see cref="Dispatcher"/> as harmless defense,
    /// not because it's load-bearing: <see cref="QsoLinkWindowViewModel.Linked"/> fires from a
    /// SEPARATE view-model instance, and while that VM's own async methods all resume on the UI
    /// thread in practice (see its class doc comment), routing this cross-VM callback through the
    /// dispatcher costs nothing and doesn't depend on that fact staying true.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenInLog))]
    private void OpenInLog()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        Log.OpenInLogInvoked(_logger, entry.Entry.Id);
        var qsoLinkVm = new QsoLinkWindowViewModel(_logbookSession, _historyStore, _localization, _qsoLinkLogger, entry.Entry);
        qsoLinkVm.Linked += qsoId => Dispatcher.UIThread.Post(() => UpdateEntryInPlace(entry.Entry.Id, e => e with { LinkedQsoId = qsoId }));
        QsoLinkRequested?.Invoke(qsoLinkVm);
    }

    /// <summary>ui_transition_plan.md step 4 (T1-4, reframed). Deliberately a settable delegate
    /// PROPERTY, not an event -- same "genuine request/response, the command awaits the typed
    /// answer" reasoning as <see cref="ConfigurationsManagerWindowViewModel.ConfirmRequested"/>'s
    /// own doc comment. Set exactly once, by <c>MainWindow.axaml.cs</c>. Returns
    /// <see langword="false"/> (decline) when unwired -- the safe default for a destructive action.
    /// </summary>
    public Func<ConfirmActionDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    private async Task<bool> RequestConfirmAsync(string title, string message)
    {
        if (ConfirmRequested is null)
        {
            return false;
        }

        var confirmVm = new ConfirmActionDialogViewModel(title, message);
        return await ConfirmRequested(confirmVm).ConfigureAwait(true);
    }

    private bool CanDeleteSelectedEntry() => SelectedEntry is not null;

    /// <summary>Per-item manual delete -- the retention-cap AUTO-delete stays removed
    /// (`docs/removed-features.md`); this is the operator explicitly discarding one bad capture.
    /// Captures <see cref="SelectedEntry"/> as <c>entry</c> BEFORE the confirm dialog's own
    /// await (same discipline <see cref="ExportFrameAsync"/>'s own doc comment describes) and never
    /// reads <see cref="SelectedEntry"/> again afterward -- a <see cref="IReceiveHistoryStore.Recorded"/>-
    /// triggered <see cref="RefreshAsync"/> could null/replace it while the confirm dialog is open,
    /// and this method must keep deleting the entry the operator actually clicked. A stronger
    /// confirmation message when the entry carries a note, a flag, or a QSO link -- the same
    /// "this row represents real investment" signal the old (now-removed) retention-trim logic used
    /// to exempt such rows from auto-deletion for.</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedEntry))]
    private async Task DeleteSelectedEntryAsync()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        var hasInvestment = entry.Entry.IsFlagged || !string.IsNullOrEmpty(entry.Entry.Note) || entry.Entry.LinkedQsoId is not null;
        var confirmed = await RequestConfirmAsync(
            _localization.GetString("Panes.RxHistory.ConfirmDeleteTitle"),
            _localization.GetString(hasInvestment ? "Panes.RxHistory.ConfirmDeleteFlaggedMessage" : "Panes.RxHistory.ConfirmDeleteMessage"));
        if (!confirmed)
        {
            return;
        }

        Log.DeleteInvoked(_logger, entry.Entry.Id);
        ErrorMessage = null;
        try
        {
            var deleted = await _historyStore.DeleteAsync(entry.Entry).ConfigureAwait(true);
            if (!deleted)
            {
                ErrorMessage = _localization.GetString("Panes.RxHistory.Error.EntryNoLongerExists");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.DeleteFailed(_logger, entry.Entry.Id, ex);
            ErrorMessage = _localization.GetString("Panes.RxHistory.Error.DeleteFailed");
            return;
        }

        await RefreshAsync();
        // Auditor-caught (2026-08-29): OnRecorded's own arrival path fires both RefreshAsync AND
        // this, so the status bar's "frames today" count would otherwise stay inflated after
        // deleting a frame received today, until the pane is reconstructed.
        _ = LoadFramesTodayCountAsync();
    }

    private bool CanOpenAudioFileLocation() => SelectedEntry?.Entry.AudioFilePath is not null;

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio), Step 4 -- same "open the
    /// containing folder" idiom as <see cref="ImageViewerWindowViewModel.OpenFileLocation"/>, applied
    /// to the linked audio file instead of the image. Opens the file's OWN containing folder, not the
    /// currently-configured <c>AudioDirectory</c> setting -- the operator may have changed that
    /// setting since this particular file was saved.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenAudioFileLocation))]
    private void OpenAudioFileLocation()
    {
        if (SelectedEntry?.Entry.AudioFilePath is not { } path)
        {
            return;
        }

        var directory = Path.GetDirectoryName(path);
        _urlLauncher.Open(string.IsNullOrEmpty(directory) ? path : directory);
    }

    private bool CanRedecodeSelectedEntry() => SelectedEntry?.Entry.AudioFilePath is not null;

    /// <summary>See <see cref="RedecodeRequested"/>'s own doc comment for why this class raises an
    /// event here instead of calling <c>ISstvSessionService.DecodeFromFileAsync</c> directly.</summary>
    [RelayCommand(CanExecute = nameof(CanRedecodeSelectedEntry))]
    private void RedecodeSelectedEntry()
    {
        if (SelectedEntry?.Entry.AudioFilePath is not { } path)
        {
            return;
        }

        RedecodeRequested?.Invoke(path);
    }

    private bool CanExportFrame() => SelectedEntry is not null;

    /// <summary>Gallery pane's "Export" button -- saves the selected frame's already-auto-saved PNG
    /// file to a user-chosen location, optionally re-encoded as JPEG at the quality configured in
    /// Options (<see cref="ImageExportSettings"/>). Captures <c>sourcePath</c> from
    /// <see cref="SelectedEntry"/> BEFORE the first <c>await</c> (the picker call) and never reads
    /// <see cref="SelectedEntry"/> again afterward -- a <see cref="IReceiveHistoryStore.Recorded"/>-
    /// triggered <see cref="RefreshAsync"/> could null/replace it while the save dialog is open, and
    /// this method must keep exporting the frame the user actually clicked, not whatever happens to
    /// be selected once the dialog closes.</summary>
    [RelayCommand(CanExecute = nameof(CanExportFrame))]
    private async Task ExportFrameAsync()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        var sourcePath = entry.Entry.FilePath;
        ErrorMessage = null;
        ExportStatusMessage = null;

        Log.ExportFrameInvoked(_logger, entry.Entry.Id);

        var suggestedFileName = Path.GetFileName(sourcePath);

        try
        {
            // Tier B audit finding: the picker call used to sit OUTSIDE this try -- an exception
            // from the platform storage provider (FilePickerService.PickSaveImageFileAsync has no
            // internal guard of its own) escaped uncaught, with no log line and no ErrorMessage --
            // the Export button just appeared to silently do nothing. Sibling
            // RxImagePaneViewModel.SaveFrameAsync already puts its own identical picker call inside
            // its try; matched here.
            var picked = await _filePicker.PickSaveImageFileAsync(suggestedFileName);
            if (picked is not { } result)
            {
                return;
            }

            var appSettings = await _settingsStore.LoadAsync();
            var quality = Math.Clamp(appSettings.GetSection(ImageExportSettings.SectionKey, ImageExportSettingsJsonContext.Default.ImageExportSettings)?.JpegQuality ?? 85, 1, 100);

            await _frameExporter.ExportAsync(sourcePath, result.Path, quality);

            Log.ExportFrameSucceeded(_logger, result.Path);
            ExportStatusMessage = _localization.GetString("Panes.RxHistory.Status.Exported", result.Path);
        }
        catch (Exception ex)
        {
            Log.ExportFrameFailed(_logger, ex);
            ErrorMessage = _localization.GetString("Panes.RxHistory.Error.ExportFrameFailed");
        }
    }

    partial void OnSelectedEntryChanged(RxHistoryEntryViewModel? value)
    {
        Log.SelectedEntryChanged(_logger);

        // Auditor-caught (plan-review): must run BEFORE the _isRepopulating early-return below --
        // otherwise a live refresh that drops the selection to null skips this requery, leaving
        // OpenInLogCommand enabled with nothing selected until some LATER unrelated change happens
        // to fire it.
        OpenInLogCommand.NotifyCanExecuteChanged();
        ExportFrameCommand.NotifyCanExecuteChanged();
        // Auditor-caught (2026-08-29): omitted here originally -- left the Delete button rendering
        // as a live, full-strength IndustryBtnDanger (no disabled style, by design, see that class's
        // own comment) that silently did nothing on the ordinary click-a-thumbnail path, recovering
        // only if the Gallery tab was detached/reattached (which re-runs Avalonia's own attach-time
        // CanExecute probe).
        DeleteSelectedEntryCommand.NotifyCanExecuteChanged();
        OpenAudioFileLocationCommand.NotifyCanExecuteChanged();
        RedecodeSelectedEntryCommand.NotifyCanExecuteChanged();

        if (_isRepopulating && value is null)
        {
            // See _isRepopulating's doc comment: this is Entries.Clear()'s own transient null flowing
            // back through the TwoWay SelectedItem binding, not a real user deselection -- RefreshAsync
            // is about to either re-select the same entry or reconcile _previewedEntryId itself once
            // it knows whether the entry actually survived the refresh.
            return;
        }

        // Auditor round-3 BLOCKER fix: gated on its OWN field (_loadedEditsEntryId), NOT
        // _previewedEntryId -- an earlier version reused _previewedEntryId for both "which entry's
        // preview is loaded" and "which entry's edits are loaded", but LoadPreviewAsync nulls
        // _previewedEntryId on a FAILED preview load (missing/corrupt PNG, a real and already-handled
        // case, see that method's own null-image branch below). From that point on, EVERY subsequent
        // same-Id re-select (a live refresh, or UpdateEntryInPlace's own SelectedEntry reassignment
        // after a successful persist) would see `value.Entry.Id != _previewedEntryId` (null) and
        // reload Note/IsFlagged anyway -- reopening the exact clobber this field split exists to
        // prevent, for any entry whose thumbnail/preview file happens to be unreadable. These two
        // concerns are genuinely independent; riding one field conflates them.
        if (value?.Entry.Id != _loadedEditsEntryId)
        {
            _loadedEditsEntryId = value?.Entry.Id;
            // _notePersistCts is deliberately NOT cancelled here -- PersistNoteDebouncedAsync
            // captures its own target entryId at schedule time (not "whatever's currently selected"),
            // so a pending save for the entry just switched AWAY from is still correct to let
            // complete; cancelling on every selection change would silently drop an edit made just
            // before switching. The one acknowledged narrow gap: switching back to that same entry
            // before its ~600ms debounce has fired shows the pre-edit value here (Entries' own copy
            // isn't updated until PersistNoteDebouncedAsync's/PersistFlaggedAsync's success path
            // runs) -- the pending save still lands correctly regardless, only the interim display
            // is stale.
            // Tier B audit finding: try/finally -- see the reconcile branch's own comment above
            // (_previewedEntryId's sibling block) on the same fix for this same flag.
            try
            {
                _suppressSelectedEntryEdits = true;
                SelectedEntryNote = value?.Entry.Note;
                SelectedEntryIsFlagged = value?.Entry.IsFlagged ?? false;
            }
            finally
            {
                _suppressSelectedEntryEdits = false;
            }

            ErrorMessage = null;
            // Code-review finding: without this, "Exported to /tmp/a.png." from a PREVIOUS
            // selection kept showing under the Selected-frame panel after switching to a different
            // entry -- ExportStatusMessage has no other clear point besides ExportFrameAsync's own
            // start-of-attempt reset.
            ExportStatusMessage = null;
        }

        if (value?.Entry.Id == _previewedEntryId)
        {
            // Auditor-caught (batch 7): a live-refresh-triggered re-select lands here with a
            // brand-new RxHistoryEntryViewModel instance for the SAME logical entry (every refresh
            // rebuilds all of them) -- without this check, every incoming frame would null
            // PreviewImage and re-decode the same file from disk, a visible flicker for a user just
            // looking at an old frame while new ones keep landing. The already-loaded PreviewImage
            // is still correct for the same Entry.Id; skip the reload entirely.
            return;
        }

        _previewedEntryId = value?.Entry.Id;
        PreviewImage = null;
        if (value is null)
        {
            return;
        }

        _ = LoadPreviewAsync(value.Entry);
    }

    private async Task LoadPreviewAsync(ReceiveHistoryEntry entry)
    {
        var generation = ++_previewGeneration;
        Bitmap? image = null;
        try
        {
            var loaded = await _historyStore.LoadThumbnailAsync(entry, PreviewMaxDimension);
            image = ImageSourceBitmapConverter.ToBitmap(loaded);
        }
        catch (Exception ex)
        {
            Log.LoadPreviewFailed(_logger, ex);
        }

        if (generation != _previewGeneration)
        {
            // A newer selection has already started its own load since this one began -- applying
            // this result now would show a preview for an entry the user is no longer looking at.
            // T0-11 code-review finding: image was never assigned to PreviewImage, so it never reaches
            // OnPreviewImageChanged's disposal hook -- dispose it directly here, it was never bound to
            // anything.
            image?.Dispose();
            return;
        }

        PreviewImage = image;
        if (image is null && entry.Id == _previewedEntryId)
        {
            // Auditor round-2 nit (batch 7): a failed load must not leave _previewedEntryId pointing
            // at an entry whose preview never actually loaded -- otherwise re-selecting the SAME entry
            // later hits the skip above and the preview stays blank forever instead of retrying.
            _previewedEntryId = null;
        }
    }

    partial void OnSelectedEntryNoteChanged(string? value)
    {
        if (_suppressSelectedEntryEdits || SelectedEntry is not { } entry)
        {
            return;
        }

        _notePersistCts?.Cancel();
        var cts = new CancellationTokenSource();
        _notePersistCts = cts;
        _ = PersistNoteDebouncedAsync(entry.Entry.Id, value, cts.Token);
    }

    private async Task PersistNoteDebouncedAsync(string entryId, string? note, CancellationToken ct)
    {
        try
        {
            await Task.Delay(NotePersistDebounce, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException)
        {
            // Normal control flow -- a newer edit (to whichever entry is selected when it fires)
            // superseded this one. Not worth a log line, same convention as
            // RadioStatusViewModel.PersistVolumeDebouncedAsync's own identical catch.
            return;
        }

        Dispatcher.UIThread.Post(() => ErrorMessage = null);

        bool succeeded;
        try
        {
            succeeded = await _historyStore.SetNoteAsync(entryId, note, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Auditor round-2 risk fix: the SAME ct also guards this call, not just the delay above
            // -- a keystroke arriving while SetNoteAsync itself is in flight cancels it, and that
            // must be treated as the identical "superseded by a newer edit" normal control flow as
            // the TaskCanceledException catch above, not routed into the generic catch below (which
            // used to show a false "Could not save the note" error for something that isn't an
            // error).
            return;
        }
        catch (Exception ex)
        {
            Log.SetNoteFailed(_logger, entryId, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("Panes.RxHistory.Error.SaveNoteFailed"));
            return;
        }

        if (!succeeded)
        {
            // Defensive -- IReceiveHistoryStore.SetNoteAsync's own doc comment: no automatic
            // deletion path exists in this port's production store today (docs/removed-features.md,
            // 2026-08-26), but a missing row is still a reachable state worth handling explicitly.
            Log.SetNoteEntryMissing(_logger, entryId);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("Panes.RxHistory.Error.EntryNoLongerExists"));
            return;
        }

        // Auditor round-2 BLOCKER fix: without writing the confirmed-persisted value back into
        // Entries, ReceiveHistoryEntry's own immutability means nothing else ever updates it --
        // switching away and back showed the pre-edit value even though the store had the new one,
        // and the next keystroke on the stale display could re-persist the OLD text over the good
        // one. UpdateEntryInPlace no-ops harmlessly if the entry was removed by a refresh that raced
        // this same persist (TryUpdateEntry-equivalent lookup miss).
        Dispatcher.UIThread.Post(() => UpdateEntryInPlace(entryId, e => e with { Note = note }));
    }

    partial void OnSelectedEntryIsFlaggedChanged(bool value)
    {
        if (_suppressSelectedEntryEdits || SelectedEntry is not { } entry)
        {
            return;
        }

        // Auditor round-2 risk fix: chained onto whatever's currently pending, not fired
        // independently -- SetFlaggedAsync has no debounce (a discrete click should persist
        // immediately, unlike the Note TextBox's continuous typing), but firing each call
        // independently left overlapping un-awaited calls from a rapid double-toggle free to
        // complete out of order (each SqliteReceiveHistoryStore write opens its own connection, no
        // ordering guarantee otherwise). Chaining preserves call order without reintroducing a
        // debounce delay the UX doesn't want here.
        _pendingFlagPersist = PersistFlaggedAsync(entry.Entry.Id, value, _pendingFlagPersist);
    }

    private async Task PersistFlaggedAsync(string entryId, bool isFlagged, Task previous)
    {
        // Auditor round-3 risk fix: the ENTIRE body, including `await previous` itself and every
        // Dispatcher.Post call, is now inside this one try/catch -- an earlier version claimed
        // "previous is always already-completed successfully, nothing to catch here" and left both
        // the await and the first Post call unguarded, which was FALSE: if Dispatcher.UIThread.Post
        // itself throws (e.g. the dispatcher is shutting down during app close), the returned Task
        // faults, gets stored in _pendingFlagPersist, and every LATER toggle rethrows that same stale
        // exception at its own `await previous` -- permanently breaking flag persistence for the rest
        // of this VM's lifetime, silently (nothing observes the fault). Wrapping everything guarantees
        // this method's returned Task can never fault, which by induction keeps the whole chain safe
        // from the very first call.
        try
        {
            await previous.ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => ErrorMessage = null);

            var succeeded = await _historyStore.SetFlaggedAsync(entryId, isFlagged).ConfigureAwait(false);
            if (!succeeded)
            {
                Log.SetFlaggedEntryMissing(_logger, entryId);
                Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("Panes.RxHistory.Error.EntryNoLongerExists"));
                return;
            }

            // Same reasoning as PersistNoteDebouncedAsync's own identical write-back.
            Dispatcher.UIThread.Post(() => UpdateEntryInPlace(entryId, e => e with { IsFlagged = isFlagged }));
        }
        catch (Exception ex)
        {
            Log.SetFlaggedFailed(_logger, entryId, ex);
            Dispatcher.UIThread.Post(() => ErrorMessage = _localization.GetString("Panes.RxHistory.Error.SaveFlagFailed"));
        }
    }

    /// <summary>Writes a confirmed-persisted edit back into <see cref="Entries"/> (and
    /// <see cref="SelectedEntry"/> if it's still the same logical entry) -- <see cref="ReceiveHistoryEntry"/>
    /// is an immutable record, so nothing else ever reflects a successful <see cref="IReceiveHistoryStore.SetNoteAsync"/>/
    /// <see cref="IReceiveHistoryStore.SetFlaggedAsync"/> call back into the in-memory list. Must run
    /// on the UI thread (mutates the bound <see cref="Entries"/> collection); every call site posts
    /// through <see cref="Dispatcher"/> first. A no-op if the entry was removed by a refresh that
    /// raced this same persist -- same "quietly drop, the store is already the source of truth"
    /// reasoning as
    /// <c>RxHistoryEntryViewModel</c> instances themselves being rebuilt wholesale on every
    /// refresh.
    ///
    /// <see cref="_isRepopulating"/> guards the <c>Entries[index] = ...</c> replace below (auditor
    /// round-3 fix) -- the real Gallery ListBox's <c>SelectedItem</c> binding is TwoWay (see
    /// <see cref="_previewedEntryId"/>'s neighboring field comment for the confirmed-via-reflection
    /// citation), and an `IList` indexer replace of the currently-selected item raises a
    /// <c>NotifyCollectionChangedAction.Replace</c> that some Avalonia selection-model versions
    /// process as remove-then-add, pushing a transient <see langword="null"/> back through that
    /// binding into <see cref="SelectedEntry"/> exactly like <c>Entries.Clear()</c> already does in
    /// <see cref="RefreshAsync"/> -- reusing the same established suppression flag rather than
    /// leaving this path unguarded.</summary>
    private void UpdateEntryInPlace(string entryId, Func<ReceiveHistoryEntry, ReceiveHistoryEntry> update)
    {
        var index = Entries.ToList().FindIndex(e => e.Entry.Id == entryId);
        if (index < 0)
        {
            return;
        }

        var current = Entries[index];
        var updated = new RxHistoryEntryViewModel(update(current.Entry), current.Thumbnail);
        var wasSelected = SelectedEntry?.Entry.Id == entryId;
        // Auditor round-3 risk fix: captured regardless of wasSelected -- the explicitly-supported
        // "edit A, switch to B, A's debounce fires later" flow (its own test above) replaces a
        // NON-selected index while B is selected. If Avalonia's selection model also nulls the
        // selection for a Replace at a non-selected index (unverified, same open question as the
        // selected-index case _isRepopulating already guards), B's selection would otherwise be
        // silently lost with nothing here to restore it.
        var previousSelection = SelectedEntry;

        _isRepopulating = true;
        try
        {
            Entries[index] = updated;
        }
        finally
        {
            _isRepopulating = false;
        }

        if (wasSelected)
        {
            SelectedEntry = updated;
        }
        else if (SelectedEntry is null && previousSelection is not null)
        {
            SelectedEntry = previousSelection;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "GetImagesDirectoryAsync failed")]
        public static partial void GetImagesDirectoryFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "OpenStorageFolder: creating '{Directory}' failed")]
        public static partial void OpenStorageFolderFailed(ILogger logger, string directory, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading disk free space failed")]
        public static partial void GetDiskFreeSpaceFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "GetAudioSettingsAsync failed")]
        public static partial void GetAudioSettingsFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Reading total auto-saved audio storage bytes failed")]
        public static partial void GetAudioStorageBytesFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the frames-today count failed")]
        public static partial void LoadFramesTodayCountFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh invoked: showTodayOnly={ShowTodayOnly}")]
        public static partial void RefreshInvoked(ILogger logger, bool showTodayOnly);

        [LoggerMessage(Level = LogLevel.Information, Message = "Disk/DB reconcile imported {Count} entries; refreshing")]
        public static partial void ReconcileImported(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Disk/DB reconcile failed")]
        public static partial void ReconcileFailed(ILogger logger, Exception ex);

        // Tier B audit finding: was "history list stays empty" -- false, since the return happens
        // BEFORE Entries.Clear() runs, so the list stays at whatever it last successfully loaded,
        // not empty.
        [LoggerMessage(Level = LogLevel.Warning, Message = "QueryAsync failed; history list stays at its last-loaded contents")]
        public static partial void QueryFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Loading a history thumbnail failed")]
        public static partial void LoadThumbnailFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh completed: {Count} entries")]
        public static partial void RefreshCompleted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh discarded -- a newer refresh already started")]
        public static partial void RefreshDiscardedAsStale(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectedEntry changed")]
        public static partial void SelectedEntryChanged(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "OpenInLog invoked: entryId={EntryId}")]
        public static partial void OpenInLogInvoked(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Information, Message = "DeleteSelectedEntry invoked: entryId={EntryId}")]
        public static partial void DeleteInvoked(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "DeleteAsync failed for entry {EntryId}")]
        public static partial void DeleteFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading preview image failed")]
        public static partial void LoadPreviewFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetNoteAsync failed for entry {EntryId}")]
        public static partial void SetNoteFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetFlaggedAsync failed for entry {EntryId}")]
        public static partial void SetFlaggedFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetNoteAsync: entry {EntryId} no longer exists")]
        public static partial void SetNoteEntryMissing(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Warning, Message = "SetFlaggedAsync: entry {EntryId} no longer exists")]
        public static partial void SetFlaggedEntryMissing(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Debug, Message = "ExportFrame invoked: entryId={EntryId}")]
        public static partial void ExportFrameInvoked(ILogger logger, string entryId);

        [LoggerMessage(Level = LogLevel.Information, Message = "Frame exported to {DestinationPath}")]
        public static partial void ExportFrameSucceeded(ILogger logger, string destinationPath);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ExportFrameAsync failed")]
        public static partial void ExportFrameFailed(ILogger logger, Exception ex);
    }
}

public sealed record RxHistoryEntryViewModel(ReceiveHistoryEntry Entry, Bitmap? Thumbnail);
