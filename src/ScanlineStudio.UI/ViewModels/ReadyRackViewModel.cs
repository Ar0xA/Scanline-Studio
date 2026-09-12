using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One row of the Templates panel's "All templates" list -- also what a rack
/// <see cref="ReadyRackSlotViewModel"/> holds when its slot is filled. <see cref="Thumbnail"/> is
/// the pre-rendered <c>thumbnail.png</c> <see cref="ITemplateStore.SaveAsync"/> already wrote (never
/// recomputed here).</summary>
public sealed partial class TemplateListRowViewModel : ObservableObject
{
    public TemplateListRowViewModel(TemplateMetadata metadata, bool isPinned)
    {
        Id = metadata.Id;
        Name = metadata.Name;
        SavedAt = metadata.SavedAt;
        Thumbnail = TryLoadThumbnail(metadata.ThumbnailPath);
        _isPinned = isPinned;
    }

    public string Id { get; }

    public string Name { get; }

    public DateTimeOffset SavedAt { get; }

    public Bitmap? Thumbnail { get; }

    [ObservableProperty]
    private bool _isPinned;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "PIN silently no-ops on a full
    /// (9-slot) rack but the toggle visually latches 'pinned' anyway." Root cause: <c>ToggleButton</c>
    /// flips its own local <c>IsChecked</c> on click regardless of a <c>Mode=OneWay</c> binding, and
    /// with the command itself a no-op (<see cref="ReadyRackViewModel.TogglePinAsync"/> returns
    /// without saving on a full rack), nothing ever pushed <see cref="IsPinned"/> back through to
    /// correct the now-desynced visual state. Fixed at the SOURCE instead of chasing that desync:
    /// disabling the button pre-emptively (bound to this) means the no-op click can never happen in
    /// the first place. Computed fresh by <see cref="ReadyRackViewModel.RefreshAsync"/> alongside
    /// <see cref="IsPinned"/> itself, not a live subscription -- same "no lifecycle hook, recomputed
    /// on each real refresh" convention already used throughout this VM.</summary>
    [ObservableProperty]
    private bool _canPin = true;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Template DELETE is a single
    /// unconfirmed click ... in a dense row." Arm/confirm (see <see cref="ReadyRackViewModel.DeleteAsync"/>'s
    /// own doc comment) -- no dialog-service precedent exists anywhere in this codebase, same
    /// reasoning as <c>TxImageEditorPaneViewModel.IsCancelArmed</c>'s own doc comment.</summary>
    [ObservableProperty]
    private bool _isPendingDelete;

    // Assigned by ReadyRackViewModel right after construction -- same "child VM holds a direct
    // reference to the parent's command" wiring TxImageEditorPaneViewModel's own CreateOverlayElement
    // already establishes for RemoveCommand/MoveUpCommand/MoveDownCommand (this codebase's own
    // established alternative to a `$parent[...]`-ancestor-cast XAML binding, which throws
    // "ArgumentException: Unable to resolve type" at runtime with this project's classic, non-compiled
    // bindings -- see TxControlsPaneViewModel.RadioStatus's own doc comment for the same finding).
    public IRelayCommand<TemplateListRowViewModel>? LoadCommand { get; set; }

    public IRelayCommand<TemplateListRowViewModel>? TogglePinCommand { get; set; }

    public IRelayCommand<TemplateListRowViewModel>? DeleteCommand { get; set; }

    /// <summary>ui_transition_plan.md step 13 -- same "child VM holds a direct reference to the
    /// parent's command" wiring as the three above.</summary>
    public IRelayCommand<TemplateListRowViewModel>? ExportCommand { get; set; }

    /// <summary>Disabled while an export for THIS row is in flight -- an export is a real disk write
    /// (a file-save dialog await plus a zip write), unlike Load/TogglePin/Delete's own near-instant
    /// operations, so a double-click has a real window to land in without this.</summary>
    [ObservableProperty]
    private bool _isExporting;

    private static Bitmap? TryLoadThumbnail(string path)
    {
        try
        {
            return File.Exists(path) ? new Bitmap(path) : null;
        }
        catch (Exception)
        {
            // A corrupt/partially-written thumbnail must not crash the whole rack refresh -- the
            // row just renders without a preview image (same "best-effort, never throw into the
            // list" doctrine as RxHistoryPaneViewModel's own thumbnail loading).
            return null;
        }
    }
}

/// <summary>One of the ready rack's 9 fixed slots (spec/15-template-designer.md Phase 5) --
/// <see cref="SlotNumber"/> is 1-9, recalled by the matching number/numpad key.</summary>
public sealed partial class ReadyRackSlotViewModel : ObservableObject
{
    public ReadyRackSlotViewModel(int slotNumber, IRelayCommand<int> recallCommand)
    {
        SlotNumber = slotNumber;
        RecallCommand = recallCommand;
    }

    public int SlotNumber { get; }

    public IRelayCommand<int> RecallCommand { get; }

    [ObservableProperty]
    private TemplateListRowViewModel? _template;
}

/// <summary>The TX template editor's Templates panel: full saved-template list + the pinned,
/// ordered 9-slot rack (spec/15-template-designer.md Phase 5). Constructed fresh per editor
/// instance by <c>TxControlsPaneViewModel</c> (same "no DI singleton" reasoning as the editor
/// itself) -- the underlying storage (<see cref="ITemplateStore"/>/pin-list settings) is shared and
/// durable, only this VM's own in-memory projection of it is per-editor-instance.</summary>
public sealed partial class ReadyRackViewModel : ObservableObject
{
    private const int SlotCount = 9;

    private readonly ITemplateStore _templateStore;
    private readonly ISettingsStore _settingsStore;
    private readonly ILocalizationService _localization;
    private readonly IFilePickerService _filePickerService;
    private readonly ILogger<ReadyRackViewModel> _logger;

    public ReadyRackViewModel(
        ITemplateStore templateStore, ISettingsStore settingsStore, ILocalizationService localization, IFilePickerService filePickerService, ILogger<ReadyRackViewModel> logger)
    {
        _templateStore = templateStore;
        _settingsStore = settingsStore;
        _localization = localization;
        _filePickerService = filePickerService;
        _logger = logger;
        Slots = new ObservableCollection<ReadyRackSlotViewModel>(Enumerable.Range(1, SlotCount).Select(n => new ReadyRackSlotViewModel(n, RecallSlotCommand)));
    }

    public ObservableCollection<ReadyRackSlotViewModel> Slots { get; }

    public ObservableCollection<TemplateListRowViewModel> AllTemplates { get; } = [];

    /// <summary>The TEMPLATE LIBRARY panel's own filtered view of <see cref="AllTemplates"/>
    /// (design-fidelity Phase D, mockups/Editwindow) -- case-insensitive substring match against
    /// <see cref="TemplateListRowViewModel.Name"/>, empty filter shows all. First text-filter UI in
    /// this codebase (no <c>ICollectionView</c>/existing filter precedent to copy -- confirmed via
    /// grep before writing this), so kept as a plain re-populated collection matching this project's
    /// own established "no CollectionView" convention rather than introducing one for a single call
    /// site.</summary>
    public ObservableCollection<TemplateListRowViewModel> FilteredTemplates { get; } = [];

    [ObservableProperty]
    private string _libraryFilterText = string.Empty;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): error surface for a failed
    /// Refresh/Delete -- same plain nullable-string shape as
    /// <c>TxImageEditorPaneViewModel.StatusMessage</c>, kept separate rather than threaded up to the
    /// parent VM (this VM already owns its own failure logging; a local status string is the smaller
    /// change and matches this codebase's own established per-VM ErrorMessage/StatusMessage
    /// convention, e.g. RadioStatusViewModel/LogbookPaneViewModel).</summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Backlog item (auditor usability review, 2026-08-17): arm/confirm target for
    /// <see cref="DeleteAsync"/> -- see <see cref="TemplateListRowViewModel.IsPendingDelete"/>'s own
    /// doc comment.</summary>
    private string? _pendingDeleteId;

    partial void OnLibraryFilterTextChanged(string value) => RefreshFilteredTemplates();

    /// <summary>Backs the Templates panel's empty-state text -- explicitly re-raised at the end of
    /// <see cref="RefreshAsync"/> (a plain computed property over <see cref="AllTemplates"/>.Count
    /// has no INotifyPropertyChanged of its own; <see cref="ObservableCollection{T}.CollectionChanged"/>
    /// isn't the same event WPF/Avalonia bindings listen for on a property path).</summary>
    public bool HasNoTemplates => AllTemplates.Count == 0;

    /// <summary>Backs the TEMPLATE LIBRARY header's count readout -- same re-raise-after-RefreshAsync
    /// discipline as <see cref="HasNoTemplates"/>, same reason (a plain <c>AllTemplates.Count</c>
    /// computed property has no property-changed notification of its own).</summary>
    public int TemplateCount => AllTemplates.Count;

    /// <summary>Distinct from <see cref="HasNoTemplates"/> -- that one covers a genuinely empty
    /// library (nothing saved yet); this one covers a non-empty library that the active
    /// <see cref="LibraryFilterText"/> happens to exclude entirely, which would otherwise render as
    /// a silent blank list with no explanation (found during Phase D real-window verification).</summary>
    public bool HasNoFilteredTemplates => AllTemplates.Count > 0 && FilteredTemplates.Count == 0;

    private void RefreshFilteredTemplates()
    {
        FilteredTemplates.Clear();
        var filter = LibraryFilterText.Trim();
        foreach (var row in AllTemplates)
        {
            if (filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredTemplates.Add(row);
            }
        }

        OnPropertyChanged(nameof(HasNoFilteredTemplates));
    }

    /// <summary>Fired when a template should be loaded into the live editor -- a rack slot
    /// click/number-key (<see cref="RecallSlot"/>) or an "All templates" row's Load action
    /// (<see cref="Load"/>) both funnel through here; <c>TxImageEditorPaneViewModel</c> subscribes
    /// once in its own constructor and calls its <c>LoadTemplateAsync</c>.</summary>
    public event Action<string>? TemplateSelected;

    /// <summary>Ready Rack direct-fire plan (2026-09-01): Ctrl+number's own signal, separate from
    /// <see cref="TemplateSelected"/> -- plain-number recall stays load-only, unchanged; Ctrl+number
    /// is an ADDITIVE sibling gesture routed through <c>TxImageEditorPaneViewModel.OnReadyRackDirectFireRequested</c>'s
    /// own load + re-seed + bake + fire chain instead.</summary>
    public event Action<string>? TemplateDirectFireRequested;

    /// <summary>Re-lists every saved template (<see cref="ITemplateStore.ListAsync"/> re-enumerates
    /// the directory every call -- a template folder copied in by hand shows up here without an app
    /// restart) and re-resolves the pin list against it. Real edge case, not ignored (plan-review):
    /// a pinned id whose template was deleted outside this app is dropped from the pin list here,
    /// not left dangling as a broken/blank slot.</summary>
    public async Task RefreshAsync()
    {
        // Code-review finding: the settings I/O below (LoadPinnedIdsAsync/SavePinnedIdsAsync) was
        // OUTSIDE this try -- a corrupt/hand-edited ReadyRack settings section or a locked settings
        // file threw uncaught, which is a UI-thread crash via TogglePinAsync/DeleteAsync's own
        // AsyncRelayCommand and a fully silent (nothing logged, panel just stays empty forever) via
        // the fire-and-forget `_ = editor.ReadyRack.RefreshAsync()` at both TxControlsPaneViewModel
        // editor-open call sites. The whole method is now one unit -- any failure anywhere in it
        // logs and leaves the rack/list in whatever state they were already in, same "best-effort,
        // never throw into the caller" doctrine ListAsync's own catch already established.
        try
        {
            var metadataList = await _templateStore.ListAsync();
            var metadataById = metadataList.ToDictionary(m => m.Id);
            var (validPinnedIds, _) = await UpdatePinnedIdsAsync(current =>
            {
                var valid = current.Where(metadataById.ContainsKey).ToList();
                return valid.Count == current.Count ? current : valid;
            });

            AllTemplates.Clear();
            foreach (var metadata in metadataList)
            {
                var isPinned = validPinnedIds.Contains(metadata.Id);
                var row = WireRowCommands(new TemplateListRowViewModel(metadata, isPinned));
                row.CanPin = isPinned || validPinnedIds.Count < SlotCount;
                row.IsPendingDelete = metadata.Id == _pendingDeleteId;
                AllTemplates.Add(row);
            }

            for (var i = 0; i < SlotCount; i++)
            {
                var hasPin = i < validPinnedIds.Count;
                Slots[i].Template = hasPin && metadataById.TryGetValue(validPinnedIds[i], out var pinnedMetadata)
                    ? WireRowCommands(new TemplateListRowViewModel(pinnedMetadata, isPinned: true))
                    : null;
            }

            RefreshFilteredTemplates();
            OnPropertyChanged(nameof(HasNoTemplates));
            OnPropertyChanged(nameof(TemplateCount));

            // Tier B audit finding: only DeleteAsync's own success path used to clear this -- a
            // transient failure here (e.g. a locked settings file) left the error banner up forever
            // afterward, even once a later refresh succeeded cleanly.
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            Log.RefreshFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.RackRefreshFailed");
        }
    }

    private TemplateListRowViewModel WireRowCommands(TemplateListRowViewModel row)
    {
        row.LoadCommand = LoadCommand;
        row.TogglePinCommand = TogglePinCommand;
        row.DeleteCommand = DeleteCommand;
        row.ExportCommand = ExportCommand;
        return row;
    }

    /// <summary>Backs the rack's number-key accelerator (<c>Key.D1</c>..<c>Key.D9</c>/
    /// <c>Key.NumPad1</c>..<c>Key.NumPad9</c>, plan-review-decided plain <c>KeyDown</c> handler --
    /// no keybinding-registration service exists anywhere in this codebase) and a slot's own click.
    /// A no-op on an empty slot.</summary>
    [RelayCommand]
    private void RecallSlot(int slotNumber)
    {
        if (slotNumber is < 1 or > SlotCount)
        {
            return;
        }

        if (Slots[slotNumber - 1].Template is { } template)
        {
            TemplateSelected?.Invoke(template.Id);
        }
    }

    /// <summary>Ready Rack direct-fire plan (2026-09-01): Ctrl+number's own accelerator (`Key.D1`..
    /// `Key.D9`/`Key.NumPad1`..`Key.NumPad9`, `OnRootKeyDown`'s `ctrlOrCmd`-gated digit branch) --
    /// literal structural clone of <see cref="RecallSlot"/>, a no-op on an empty slot, but raises
    /// <see cref="TemplateDirectFireRequested"/> instead of <see cref="TemplateSelected"/> so
    /// <c>TxImageEditorPaneViewModel</c> can route it through its own separate arm/confirm +
    /// load + re-seed + bake + fire chain (<c>OnReadyRackDirectFireRequested</c>) rather than the
    /// plain load-only path.</summary>
    [RelayCommand]
    private void DirectFireSlot(int slotNumber)
    {
        if (slotNumber is < 1 or > SlotCount)
        {
            return;
        }

        if (Slots[slotNumber - 1].Template is { } template)
        {
            TemplateDirectFireRequested?.Invoke(template.Id);
        }
    }

    [RelayCommand]
    private void Load(TemplateListRowViewModel? row)
    {
        if (row is not null)
        {
            TemplateSelected?.Invoke(row.Id);
        }
    }

    /// <summary>Pin/unpin toggle -- appends to the first empty slot (up to <see cref="SlotCount"/>);
    /// a full rack is a no-op (no eviction policy -- the operator unpins something first, same as
    /// this project's own "no silent surprising side effect" doctrine elsewhere).</summary>
    [RelayCommand]
    private async Task TogglePinAsync(TemplateListRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var (_, changed) = await UpdatePinnedIdsAsync(current =>
        {
            var pinnedIds = current.ToList();
            if (pinnedIds.Contains(row.Id))
            {
                pinnedIds.Remove(row.Id);
            }
            else if (pinnedIds.Count < SlotCount)
            {
                pinnedIds.Add(row.Id);
            }
            else
            {
                return current; // full rack, no eviction policy -- no-op
            }

            return pinnedIds;
        });

        if (changed)
        {
            await RefreshAsync();
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Template DELETE is a single
    /// unconfirmed click ... in a dense row." Arm/confirm, not a modal dialog (no precedent anywhere
    /// in this codebase -- same reasoning as <c>TxImageEditorPaneViewModel.IsCancelArmed</c>'s own
    /// doc comment): the first click on a row arms it (<see cref="TemplateListRowViewModel.IsPendingDelete"/>
    /// flips true for THAT row only, false for every other -- clicking a different row's Delete re-arms
    /// for the new target rather than confirming an unrelated one), the second click on the SAME
    /// already-armed row actually deletes. <see cref="RefreshAsync"/>'s own re-populate naturally
    /// clears the pending state on success (fresh rows default <c>IsPendingDelete</c> false unless
    /// <see cref="_pendingDeleteId"/> still matches).</summary>
    [RelayCommand]
    private async Task DeleteAsync(TemplateListRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        if (_pendingDeleteId != row.Id)
        {
            _pendingDeleteId = row.Id;
            foreach (var candidate in AllTemplates)
            {
                candidate.IsPendingDelete = candidate.Id == row.Id;
            }

            return;
        }

        try
        {
            await _templateStore.DeleteAsync(row.Id);
            _pendingDeleteId = null;
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            // Tier B audit finding: this used to clear _pendingDeleteId unconditionally BEFORE the
            // try, so a failed delete left the row's own IsPendingDelete=true (still rendering
            // "confirm delete") while _pendingDeleteId was already null -- the next click on that
            // SAME row read as a fresh arm (no visible change, since it was already showing armed)
            // instead of a confirm, taking three clicks to actually retry. Stays armed on failure
            // instead, so the very next click on this row retries the delete directly.
            Log.DeleteFailed(_logger, row.Id, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DeleteTemplateFailed");
            return;
        }

        // Real edge case, not ignored (plan-review): deleting a still-pinned template must also
        // drop it from the pin list, or its rack slot would otherwise show a dangling id. No
        // separate pin-list write needed here though (mutation-testing this file's own test suite
        // caught that an earlier draft's explicit removal-then-save was dead code) --
        // RefreshAsync's own dangling-id sweep below already re-validates every pinned id against
        // the just-updated template list and persists the correction, unconditionally, every time.
        await RefreshAsync();
    }

    /// <summary>ui_transition_plan.md step 13 -- zips <paramref name="row"/>'s own template folder to
    /// a user-chosen <c>.sstemplate</c> path via <see cref="ITemplateStore.ExportAsync"/>. Suggested
    /// file name reuses the template's own display <see cref="TemplateListRowViewModel.Name"/> (same
    /// "suggest the obvious name, let the picker's own overwrite-confirm handle a collision"
    /// convention as every other <c>PickSave*</c> call site in this codebase).</summary>
    [RelayCommand]
    private async Task ExportAsync(TemplateListRowViewModel? row)
    {
        if (row is null || row.IsExporting)
        {
            return;
        }

        row.IsExporting = true;
        try
        {
            var destinationPath = await _filePickerService.PickSaveTemplateBundleAsync($"{row.Name}.sstemplate");
            if (destinationPath is null)
            {
                return;
            }

            await _templateStore.ExportAsync(row.Id, destinationPath);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            Log.ExportFailed(_logger, row.Id, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ExportTemplateFailed");
        }
        finally
        {
            row.IsExporting = false;
        }
    }

    /// <summary>ui_transition_plan.md step 13 -- panel-level (no row target), unlike
    /// Export/Load/TogglePin/Delete above. Imports via <see cref="ITemplateStore.ImportAsync"/>
    /// (which always mints a fresh id -- see its own doc comment) and refreshes the list so the
    /// newly imported template appears immediately, matching <see cref="DeleteAsync"/>'s own
    /// success-path refresh.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        try
        {
            var sourcePath = await _filePickerService.PickOpenTemplateBundleAsync();
            if (sourcePath is null)
            {
                return;
            }

            await _templateStore.ImportAsync(sourcePath);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            Log.ImportFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ImportTemplateFailed");
            return;
        }

        await RefreshAsync();
    }

    /// <summary>Legacy `.mtm`/`.mti` template import, reversed 2026-09-12 from its 2026-08-29
    /// rejection (`BACKLOG.md`). Mirrors <see cref="ImportAsync"/>'s own shape exactly, with one
    /// addition: <see cref="ITemplateStore.ImportLegacyMtmAsync"/> can succeed with NOTES (elements
    /// approximated or dropped because the modern model has no legacy equivalent) -- those are
    /// ALWAYS surfaced in <see cref="StatusMessage"/> when present, never silently swallowed, and the
    /// full list is logged at Information so a user who wants the exact detail can find it, without
    /// this panel growing a dedicated multi-line results dialog for what is expected to be a rare,
    /// one-off action.
    /// <para><see cref="LegacyMtmFormatException"/> (thrown for a corrupt, truncated, or
    /// OLE-embedded file) carries a message already written to be shown to a user directly -- surfaced
    /// as-is rather than the generic failure string every other exception here falls back to.</para></summary>
    [RelayCommand]
    private async Task ImportLegacyMtmAsync()
    {
        LegacyMtmImportResult result;
        try
        {
            var sourcePath = await _filePickerService.PickOpenLegacyMtmTemplateAsync();
            if (sourcePath is null)
            {
                return;
            }

            result = await _templateStore.ImportLegacyMtmAsync(sourcePath);
        }
        catch (LegacyMtmFormatException ex)
        {
            Log.ImportLegacyMtmFailed(_logger, ex);
            StatusMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            Log.ImportLegacyMtmFailed(_logger, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ImportLegacyMtmTemplateFailed");
            return;
        }

        // RefreshAsync's own success path unconditionally clears StatusMessage (a deliberate
        // convention -- see its own doc comment) -- so the partial-import notice must be set AFTER
        // it runs, not before, or RefreshAsync would immediately wipe it out.
        await RefreshAsync();

        if (result.Notes.Count > 0)
        {
            Log.ImportLegacyMtmNotes(_logger, result.TemplateId, string.Join(" | ", result.Notes));
            StatusMessage = _localization.GetString("Panes.TxImageEditor.ImportLegacyMtmTemplatePartial", result.Notes.Count);
        }
    }

    /// <summary>T0-2: atomic read-modify-write for the pinned-template-ids section. Both callers
    /// (<see cref="RefreshAsync"/>'s prune, <see cref="TogglePinAsync"/>) used to be a separate
    /// LoadAsync then a conditional SaveAsync -- a check-then-act with the lock released in between,
    /// the exact race T0-2 closes. <paramref name="mutate"/> must return the SAME reference it was
    /// given when nothing should change (matches <see cref="ISettingsStore.UpdateAsync"/>'s own
    /// no-op-skip contract) -- that's how <see cref="Changed"/> below is computed, and callers use it
    /// to skip a redundant <see cref="RefreshAsync"/>.</summary>
    private async Task<(IReadOnlyList<string> PinnedIds, bool Changed)> UpdatePinnedIdsAsync(Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate)
    {
        var changed = false;
        var updatedAppSettings = await _settingsStore.UpdateAsync(appSettings =>
        {
            // currentSection itself (not just PinnedTemplateIds) can be a genuine absent-section null;
            // separately, a hand-edited settings.json can carry an EXPLICIT `"PinnedTemplateIds":
            // null` -- an explicit JSON null overrides the `= []` initializer on an init property, so
            // PinnedTemplateIds can be null even when currentSection itself is not. Both guarded
            // separately -- ?? new ReadyRackSettings() alone would still let the second case NRE
            // inside mutate (TogglePinAsync has no try/catch around this call, unlike RefreshAsync).
            var currentSection = appSettings.GetSection(ReadyRackSettings.SectionKey, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);
            var current = currentSection?.PinnedTemplateIds ?? [];
            var updated = mutate(current);
            if (ReferenceEquals(updated, current))
            {
                return appSettings;
            }

            changed = true;
            // `with`, not a fresh `new ReadyRackSettings { ... }` -- matches this codebase's own
            // established convention for a targeted single-field write, and stays correct the day a
            // second field is added to this record (single-field today, confirmed).
            return appSettings.WithSection(
                ReadyRackSettings.SectionKey, (currentSection ?? new ReadyRackSettings()) with { PinnedTemplateIds = updated }, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);
        }).ConfigureAwait(false);

        var finalIds = updatedAppSettings.GetSection(ReadyRackSettings.SectionKey, ReadyRackSettingsJsonContext.Default.ReadyRackSettings)?.PinnedTemplateIds ?? [];
        return (finalIds, changed);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack refresh failed")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack delete failed: templateId={TemplateId}")]
        public static partial void DeleteFailed(ILogger logger, string templateId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack export failed: templateId={TemplateId}")]
        public static partial void ExportFailed(ILogger logger, string templateId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack import failed")]
        public static partial void ImportFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack legacy .mtm/.mti import failed")]
        public static partial void ImportLegacyMtmFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Information, Message = "Legacy .mtm/.mti import of template '{TemplateId}' succeeded with notes: {Notes}")]
        public static partial void ImportLegacyMtmNotes(ILogger logger, string templateId, string notes);
    }
}
