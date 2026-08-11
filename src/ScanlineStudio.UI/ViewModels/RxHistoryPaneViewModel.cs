using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.UI.Imaging;

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
    /// `TextBox` -- no debounce needed, same distinction <c>TxControlsPaneViewModel.SwrCutoffEnabled</c>
    /// draws between its own immediate-persist toggle and <c>RadioStatusViewModel.TxVolumePercent</c>'s
    /// debounced slider).</summary>
    [ObservableProperty]
    private bool _selectedEntryIsFlagged;

    /// <summary>Surfaces a <see cref="IReceiveHistoryStore.SetNoteAsync"/>/<see cref="IReceiveHistoryStore.SetFlaggedAsync"/>
    /// failure to the user -- both methods' own doc comments say a missing-entry return is "a
    /// reachable case, not just defensive programming" (the retention-trim ring buffer can delete an
    /// untouched row between load and edit) "the caller ... is expected to surface that to the user,
    /// not silently ignore it." Same `ErrorMessage` convention as every other pane ViewModel in this
    /// app.</summary>
    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>Gallery tab's All/Today filter (spec/09-ui.md) -- real, backed by
    /// <see cref="IReceiveHistoryStore.QueryAsync"/>'s own <c>From</c>/<c>To</c> filter fields.
    /// Defaults to <see langword="true"/>, matching the mock2 draft's own default selection.
    /// Band/Unlogged/Flagged/free-text filters from that same draft are omitted -- there is no
    /// frequency/callsign/grid field on <see cref="ReceiveHistoryEntry"/> to filter by, and
    /// <c>LinkedQsoId</c> is never set to anything but <see langword="null"/> by any code path
    /// today (spec/14-roadmap.md backlog).</summary>
    [ObservableProperty]
    private bool _showTodayOnly = true;

    /// <summary>Resolved saved-image folder for the Gallery tab's Storage card -- real, loaded
    /// once via <see cref="IReceiveHistoryStore.GetImagesDirectoryAsync"/>.</summary>
    [ObservableProperty]
    private string? _imagesDirectory;

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
    /// write <c>DateTimeOffset.Now</c> (LOCAL offset), and <c>SqliteReceiveHistoryStore</c>'s
    /// `From`/`To` filter is a lexicographic TEXT compare on `ToString("O")`, which only stays
    /// correct when the query's own offset matches the stored rows' -- a UTC-anchored query against
    /// locally-offset rows silently misses or double-counts several hours' worth of frames around
    /// every day boundary on any non-UTC machine. Fixed to match <see cref="ShowTodayOnly"/>'s own
    /// `DateTime.Today` (local) convention below -- the two filters are now genuinely consistent, not
    /// a "separate, pre-existing inconsistency" as an earlier version of this comment claimed.
    ///
    /// Also includes <see cref="ReceiveDecodeState.Abandoned"/> (partial) entries alongside
    /// <see cref="ReceiveDecodeState.Completed"/> ones -- <see cref="ReceiveHistoryFilter"/> has no
    /// `DecodeState` field to narrow by, so this counts every row received today, not just
    /// successfully completed images.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FramesTodayDisplay))]
    private int _framesTodayCount;

    public RxHistoryPaneViewModel(IReceiveHistoryStore historyStore, ILocalizationService localization, ILogger<RxHistoryPaneViewModel> logger)
    {
        _historyStore = historyStore;
        _localization = localization;
        _logger = logger;

        Entries.CollectionChanged += (_, _) =>
        {
            UpdateEntryCountText();
            SelectLatestCommand.NotifyCanExecuteChanged();
        };
        UpdateEntryCountText();
        _historyStore.Recorded += OnRecorded;

        // Best-effort initial load -- a failure here (e.g. history store not reachable yet) leaves
        // the pane empty rather than blocking construction; RefreshCommand lets the user retry.
        _ = RefreshAsync();
        _ = LoadImagesDirectoryAsync();
        _ = LoadFramesTodayCountAsync();
    }

    public ObservableCollection<RxHistoryEntryViewModel> Entries { get; } = [];

    public string FramesTodayDisplay => _localization.GetString("MainWindow.StatusBar.FramesTodayValueFormat", FramesTodayCount);

    private void UpdateEntryCountText() => EntryCountText = Entries.Count switch
    {
        1 => _localization.GetString("Panes.RxHistory.EntryCountSingular"),
        var count => _localization.GetString("Panes.RxHistory.EntryCountFormat", count),
    };

    private async Task LoadFramesTodayCountAsync()
    {
        try
        {
            var todayEntries = await _historyStore.QueryAsync(new ReceiveHistoryFilter(From: new DateTimeOffset(DateTime.Today)));
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
    /// (<c>SqliteReceiveHistoryStore.QueryAsync</c>'s own <c>ORDER BY ReceivedAt DESC</c>), so
    /// "newest" is simply the first entry already loaded -- no new query needed, unlike
    /// <see cref="RefreshAsync"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanSelectLatest))]
    private void SelectLatest() => SelectedEntry = Entries.FirstOrDefault();

    private async Task LoadImagesDirectoryAsync()
    {
        try
        {
            ImagesDirectory = await _historyStore.GetImagesDirectoryAsync();
        }
        catch (Exception ex)
        {
            // Best-effort, same reasoning as RefreshAsync -- the Storage card just shows nothing.
            Log.GetImagesDirectoryFailed(_logger, ex);
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
            Log.QueryFailed(_logger, ex);
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
            // aged out of ShowTodayOnly's own window) or was trimmed by retention, SelectedEntry simply
            // stays null, matching what already happens on a manual Refresh/filter-change today; this
            // isn't a regression, only a preservation of the CASE that already worked.
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
            _suppressSelectedEntryEdits = true;
            SelectedEntryNote = null;
            SelectedEntryIsFlagged = false;
            _suppressSelectedEntryEdits = false;
        }

        Log.RefreshCompleted(_logger, Entries.Count);
    }

    partial void OnSelectedEntryChanged(RxHistoryEntryViewModel? value)
    {
        Log.SelectedEntryChanged(_logger);

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
            _suppressSelectedEntryEdits = true;
            SelectedEntryNote = value?.Entry.Note;
            SelectedEntryIsFlagged = value?.Entry.IsFlagged ?? false;
            _suppressSelectedEntryEdits = false;
            ErrorMessage = null;
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
            // Reachable, not defensive -- IReceiveHistoryStore.SetNoteAsync's own doc comment: the
            // retention-trim ring buffer can delete this row between load and edit.
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
    /// raced this same persist (retention trim, or the store row genuinely no longer exists) --
    /// same "quietly drop, the store is already the source of truth" reasoning as
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Loading the frames-today count failed")]
        public static partial void LoadFramesTodayCountFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh invoked: showTodayOnly={ShowTodayOnly}")]
        public static partial void RefreshInvoked(ILogger logger, bool showTodayOnly);

        [LoggerMessage(Level = LogLevel.Warning, Message = "QueryAsync failed; history list stays empty")]
        public static partial void QueryFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Loading a history thumbnail failed")]
        public static partial void LoadThumbnailFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh completed: {Count} entries")]
        public static partial void RefreshCompleted(ILogger logger, int count);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Refresh discarded -- a newer refresh already started")]
        public static partial void RefreshDiscardedAsStale(ILogger logger);

        [LoggerMessage(Level = LogLevel.Debug, Message = "SelectedEntry changed")]
        public static partial void SelectedEntryChanged(ILogger logger);

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
    }
}

public sealed record RxHistoryEntryViewModel(ReceiveHistoryEntry Entry, Bitmap? Thumbnail);
