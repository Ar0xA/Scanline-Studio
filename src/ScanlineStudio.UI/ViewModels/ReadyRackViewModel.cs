using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;

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
        _editingName = metadata.Name;
    }

    public string Id { get; }

    public string Name { get; }

    public DateTimeOffset SavedAt { get; }

    /// <summary>Templates rack rework -- the rack action strip's inline-rename `TextBox` binds to
    /// THIS, not <see cref="Name"/>, so an in-progress edit never touches the real display name
    /// until <see cref="ReadyRackViewModel.RenameAsync"/> actually commits it (and
    /// <see cref="ReadyRackViewModel.RefreshAsync"/> rebuilds this row from the store's own fresh
    /// data either way). Defaults to <see cref="Name"/>; a revert (empty input, or Esc in the code-
    /// behind key handler) just resets this back to <see cref="Name"/>, no store call.</summary>
    [ObservableProperty]
    private string _editingName;

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

    /// <summary>Templates rack rework -- same wiring convention as the four above.
    /// <see cref="ReadyRackViewModel.DeleteFromRackAsync"/>, a SEPARATE command from
    /// <see cref="DeleteCommand"/> above only because their confirm dialogs word the consequence
    /// differently (naming the rack slot vs. not) -- both now go through the same real
    /// <see cref="ReadyRackViewModel.ConfirmRequested"/> dialog (user-reported feedback, 2026-09-15:
    /// the Library list's own Delete used to keep an inline two-click arm/confirm here, the one
    /// remaining "click twice" holdout in this class -- migrated to match).</summary>
    public IRelayCommand<TemplateListRowViewModel>? DeleteFromRackCommand { get; set; }

    /// <summary>Templates rack rework -- same wiring convention. <see cref="ReadyRackViewModel.RenameAsync"/>.</summary>
    public IRelayCommand<TemplateListRowViewModel>? RenameCommand { get; set; }

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

    /// <summary>Templates rack rework -- true when this slot's own template is the one the live
    /// editor canvas was last loaded from. Pushed by <see cref="ReadyRackViewModel.SetLoadedTemplate"/>,
    /// not a live binding (see that method's own doc comment) -- survives
    /// <see cref="ReadyRackViewModel.RefreshAsync"/> rebuilding <see cref="Template"/> with a fresh
    /// instance, since this property lives on the SLOT, not the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadedOnly))]
    private bool _isLoaded;

    /// <summary>True only when <see cref="IsLoaded"/> AND the canvas has been edited since this
    /// specific template was loaded (not merely "since the editor opened" -- see
    /// <see cref="ReadyRackViewModel.SetCanvasDirty"/>'s own doc comment) -- backs the "Loaded •
    /// edited" badge text distinct from the plain "Loaded" one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoadedOnly))]
    private bool _isLoadedAndEdited;

    /// <summary>yoniq-auditor finding: the view had TWO independently-visible sibling `TextBlock`s
    /// (one per bool above), so an edited slot rendered "Loaded" AND "Loaded • edited" stacked on
    /// top of each other -- <see cref="IsLoadedAndEdited"/> is only ever true when
    /// <see cref="IsLoaded"/> already is, so the two badges were never actually mutually exclusive
    /// in the view even though the underlying booleans always agreed on that relationship. This
    /// property makes the exclusivity explicit for the view to bind against, instead of the view
    /// trying to express "IsLoaded AND NOT IsLoadedAndEdited" itself.</summary>
    public bool IsLoadedOnly => IsLoaded && !IsLoadedAndEdited;
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

    /// <summary>Guards the one-time <see cref="TemplateLibraryUiSettings"/> load inside
    /// <see cref="RefreshAsync"/> below -- deliberately NOT a fire-and-forget task started from the
    /// constructor (an earlier draft did this, matching `TxControlsPaneViewModel`'s own
    /// `TxPaneUiSettings` load shape, but that pattern assumes ONE long-lived VM instance; this VM
    /// is constructed FRESH per editor AND per test, and a test suite constructing hundreds of them
    /// left hundreds of abandoned, never-pumped async chains piling up, degrading the whole test
    /// class to a near-hang. Folded into `RefreshAsync` instead -- the SAME already-awaited
    /// initialization path every real caller and every test already uses, so there's no second,
    /// unawaited async entry point to abandon.</summary>
    private bool _templateLibraryUiSettingsLoaded;

    /// <summary>Templates rack rework, expanded template selector -- the Library panel's own List/
    /// Grid toggle. Persisted separately from <see cref="ReadyRackSettings.PinnedTemplateIds"/> (a
    /// domain concept -- what's pinned) since this is a pure display preference, same layering
    /// `AppearanceSettings`/`TxPaneUiSettings` already established.
    /// <para>The view binds this ONE-WAY only (see <see cref="SelectListView"/>/<see cref="SelectGridView"/>
    /// below) -- a real bug caught during testing, yoniq-auditor-refined root cause: a TwoWay-bound
    /// negated pair (<c>!IsGridView</c>/<c>IsGridView</c>) on two `RadioButton`s sharing one
    /// `GroupName` is SAFE for a single live view instance (this exact negated-pair shape already
    /// ships elsewhere in this codebase, e.g. `ImageViewerWindowView`'s `IsFitToWindow`,
    /// `MainWindow`'s `IsAutoDetectPaused`/`ShowTodayOnly` -- do not treat those as latent bugs). The
    /// real trigger is negated pair + TwoWay + MULTIPLE LIVE INSTANCES sharing one GroupName
    /// registry (this VM/view is constructed fresh per editor AND per test, unlike those
    /// single-instance cases) -- one instance's own check unchecks every OTHER instance's button via
    /// GroupName, and because the pair is NEGATED, "unchecked" flips that instance's own bound value
    /// and re-fires the group, an N-squared avalanche across instances with no click needed. Two
    /// explicit, parameterless commands avoid any TwoWay write path on `IsChecked` at all, which
    /// closes this regardless of instance count.</para></summary>
    [ObservableProperty]
    private bool _isGridView;

    /// <summary>yoniq-auditor finding: without this guard, the one-time settings load in
    /// <see cref="RefreshAsync"/> setting <c>IsGridView = true</c> (a stored "grid" preference) fires
    /// THIS handler, which immediately re-persists the value it just read -- redundant I/O on every
    /// editor open, and reintroduces a milder version of the fire-and-forget-task problem
    /// <see cref="_templateLibraryUiSettingsLoaded"/>'s own doc comment describes. Suppressed during
    /// the load itself; real operator toggles still persist normally.</summary>
    private bool _suppressPersistDuringLoad;

    partial void OnIsGridViewChanged(bool value)
    {
        if (!_suppressPersistDuringLoad)
        {
            _ = PersistTemplateLibraryUiSettingsAsync();
        }
    }

    [RelayCommand]
    private void SelectListView() => IsGridView = false;

    [RelayCommand]
    private void SelectGridView() => IsGridView = true;

    private async Task PersistTemplateLibraryUiSettingsAsync()
    {
        try
        {
            var isGridView = IsGridView;
            await _settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(TemplateLibraryUiSettings.SectionKey, TemplateLibraryUiSettingsJsonContext.Default.TemplateLibraryUiSettings) ?? new TemplateLibraryUiSettings();
                var updated = current with { IsGridView = isGridView };
                return settings.WithSection(TemplateLibraryUiSettings.SectionKey, updated, TemplateLibraryUiSettingsJsonContext.Default.TemplateLibraryUiSettings);
            });
        }
        catch (Exception ex)
        {
            Log.PersistTemplateLibraryUiSettingsFailed(_logger, ex);
        }
    }

    /// <summary>The Template Library panel's own `SelectedItem` -- independent of <see cref="SelectedSlot"/>
    /// (a different collection, a different UI region). Unlike the rack's slots (a stable wrapper
    /// whose own `.Template` swaps), <see cref="AllTemplates"/>/<see cref="FilteredTemplates"/> are
    /// rebuilt with FRESH row instances on every <see cref="RefreshAsync"/> -- see that method's own
    /// re-resolve-by-id tail for why this can't just be left alone across a refresh the way
    /// <see cref="SelectedSlot"/> can.</summary>
    [ObservableProperty]
    private TemplateListRowViewModel? _selectedLibraryItem;

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

    /// <summary>Templates rack rework -- which template id the live editor canvas was last loaded
    /// from, or <see langword="null"/> if none (a blank/new editor, or the loaded template was
    /// since deleted/unpinned). Drives <see cref="ReadyRackSlotViewModel.IsLoaded"/> via
    /// <see cref="ApplyLoadedState"/> -- never the other way around, so a rack refresh (rename/pin/
    /// delete elsewhere) can never silently change WHICH template the canvas actually holds.</summary>
    private string? _loadedTemplateId;

    /// <summary>Mirrors <c>TxImageEditorPaneViewModel.HasUnsavedEdits</c>, pushed via
    /// <see cref="SetCanvasDirty"/> -- this VM has no reference to the editor VM (constructed
    /// before it, per <c>TxControlsPaneViewModel</c>'s own construction order), so it can't read
    /// that property directly.</summary>
    private bool _isCanvasDirty;

    /// <summary>Templates rack rework -- the rack `ListBox`'s own `SelectedItem`, backing the
    /// single-click action strip (Load/Unpin/Export/Delete/Rename). Native `ListBox` selection, not
    /// a Tapped-handler-set field -- same "no code-behind needed for selection" precedent as the RX
    /// History gallery's own `SelectedEntry` (`RxHistoryPaneViewModel`).</summary>
    [ObservableProperty]
    private ReadyRackSlotViewModel? _selectedSlot;

    /// <summary>Templates rack rework -- true while <see cref="TxImageEditorPaneViewModel.OnReadyRackTemplateSelected"/>
    /// is awaiting a load (including any confirm-dialog await before it). Dims/disables the rack so
    /// a confused extra click during that window can't land mid-load -- same "disable rather than
    /// debounce" idiom this file already uses for <see cref="TemplateListRowViewModel.IsExporting"/>.</summary>
    [ObservableProperty]
    private bool _isLoadInFlight;

    /// <summary>Templates rack rework -- same delegate-property shape as
    /// <c>RxHistoryPaneViewModel.ConfirmRequested</c>/<c>LogbookPaneViewModel.ConfirmRequested</c>.
    /// Backs <see cref="DeleteFromRackAsync"/>'s own real confirm dialog (the rack's Delete,
    /// deliberately NOT the Library list's existing inline arm/confirm -- see
    /// <see cref="TemplateListRowViewModel.DeleteFromRackCommand"/>'s own doc comment). Set once per
    /// editor instance by <c>MainWindow.axaml.cs</c>'s <c>EditorOpened</c> subscription (this VM is
    /// constructed fresh per editor, unlike RxHistory/Logbook). Returns <see langword="false"/>
    /// (decline) when unwired -- the safe default for a destructive action.</summary>
    public Func<ConfirmActionDialogViewModel, Task<bool>>? ConfirmRequested { get; set; }

    partial void OnLibraryFilterTextChanged(string value) => RefreshFilteredTemplates();

    /// <summary>Templates rack rework -- called by <c>TxImageEditorPaneViewModel</c> after a
    /// successful load (and cleared on delete/unpin of the loaded template) to update
    /// <see cref="ReadyRackSlotViewModel.IsLoaded"/>. Push-based, not a live cross-VM subscription --
    /// same "parent pushes on its own state changes" convention <see cref="WireRowCommands"/>
    /// already establishes for commands, applied here to a second kind of cross-VM state.</summary>
    public void SetLoadedTemplate(string? templateId)
    {
        _loadedTemplateId = templateId;
        ApplyLoadedState();
    }

    /// <summary>Templates rack rework -- <paramref name="isDirty"/> is
    /// <c>TxImageEditorPaneViewModel.IsDirtySinceLastTemplateLoad</c>, NOT its plain
    /// <c>HasUnsavedEdits</c> (yoniq-auditor finding: reusing the raw undo-stack-non-empty flag made
    /// the badge read "edited" the instant ANY template loaded, since a load itself pushes its own
    /// undo snapshot -- see that property's own doc comment). Called at every point the caller
    /// already raises <c>OnPropertyChanged(nameof(HasUnsavedEdits))</c>, so the "Loaded • edited"
    /// badge never lags the real dirty-since-load state.</summary>
    public void SetCanvasDirty(bool isDirty)
    {
        _isCanvasDirty = isDirty;
        ApplyLoadedState();
    }

    public void SetLoadInFlight(bool inFlight) => IsLoadInFlight = inFlight;

    private void ApplyLoadedState()
    {
        foreach (var slot in Slots)
        {
            var isLoaded = slot.Template is { } template && template.Id == _loadedTemplateId;
            slot.IsLoaded = isLoaded;
            slot.IsLoadedAndEdited = isLoaded && _isCanvasDirty;
        }
    }

    /// <summary>Templates rack rework -- small lookup for the discard-changes confirm dialog's own
    /// body text, which only has a template id (<c>OnReadyRackTemplateSelected</c>'s own parameter)
    /// and needs the display name. Checks <see cref="Slots"/> first (the common case -- a rack
    /// recall/double-click already has the row right there) before falling back to
    /// <see cref="AllTemplates"/> (the Template Library's own Load button).</summary>
    public string GetTemplateName(string templateId) =>
        Slots.FirstOrDefault(s => s.Template?.Id == templateId)?.Template?.Name
        ?? AllTemplates.FirstOrDefault(t => t.Id == templateId)?.Name
        ?? templateId;

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

        // yoniq-auditor finding: if the just-typed filter now excludes the selected row, keep the
        // VM's own SelectedLibraryItem consistent with what FilteredTemplates (what the ListBox can
        // actually select) contains -- explicit here rather than relying on however Avalonia's own
        // SelectedItem binding happens to behave on a collection reset, so the action strip always
        // hides cleanly instead of risking a VM/view disagreement.
        if (SelectedLibraryItem is not null && !FilteredTemplates.Contains(SelectedLibraryItem))
        {
            SelectedLibraryItem = null;
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
            // Expanded template selector: one-time settings load, see _templateLibraryUiSettingsLoaded's
            // own doc comment for why this lives here and not a constructor-fired task. yoniq-auditor
            // finding: this is its OWN try/catch, separate from the outer one below -- a corrupt/
            // hand-edited TemplateLibraryUi section must not blank the entire rack/Library (it used
            // to be the first statement inside the outer try, so a throw here skipped ListAsync
            // entirely). _templateLibraryUiSettingsLoaded is only set AFTER success, so a transient
            // failure (e.g. a momentarily locked settings file) retries on the NEXT refresh instead
            // of being permanently stuck at the List-view default for this editor session.
            if (!_templateLibraryUiSettingsLoaded)
            {
                try
                {
                    var libraryUiSettings = await _settingsStore.LoadAsync();
                    var librarySection = libraryUiSettings.GetSection(TemplateLibraryUiSettings.SectionKey, TemplateLibraryUiSettingsJsonContext.Default.TemplateLibraryUiSettings) ?? new TemplateLibraryUiSettings();
                    _suppressPersistDuringLoad = true;
                    IsGridView = librarySection.IsGridView;
                    _suppressPersistDuringLoad = false;
                    _templateLibraryUiSettingsLoaded = true;
                }
                catch (Exception ex)
                {
                    _suppressPersistDuringLoad = false;
                    Log.LoadTemplateLibraryUiSettingsFailed(_logger, ex);
                }
            }

            var metadataList = await _templateStore.ListAsync();
            var metadataById = metadataList.ToDictionary(m => m.Id);
            var (validPinnedIds, _) = await UpdatePinnedIdsAsync(current =>
            {
                var valid = current.Where(metadataById.ContainsKey).ToList();
                return valid.Count == current.Count ? current : valid;
            });

            // Expanded template selector: captured BEFORE AllTemplates is rebuilt below -- unlike
            // Slots (a stable wrapper), AllTemplates/FilteredTemplates get entirely FRESH row
            // instances every refresh, so SelectedLibraryItem must be re-resolved by id afterward or
            // it would point at a discarded instance no ListBox could ever show as selected again.
            var selectedLibraryItemId = SelectedLibraryItem?.Id;

            AllTemplates.Clear();
            foreach (var metadata in metadataList)
            {
                var isPinned = validPinnedIds.Contains(metadata.Id);
                var row = WireRowCommands(new TemplateListRowViewModel(metadata, isPinned));
                row.CanPin = isPinned || validPinnedIds.Count < SlotCount;
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

            // Templates rack rework: Slots[i].Template is a FRESH instance every refresh (see the
            // loop above), so IsLoaded/IsLoadedAndEdited must be reapplied here or a rename/pin/
            // delete elsewhere would silently clear the "Loaded" badge on an otherwise-unrelated
            // refresh. SelectedSlot itself is a stable ReadyRackSlotViewModel instance (Slots isn't
            // rebuilt, only each slot's own Template swaps) -- but its Template can now be null (its
            // template got deleted/unpinned by this same refresh), which would leave the action
            // strip bound to a null Template; clearing the selection in that case hides the strip
            // instead of rendering blank.
            ApplyLoadedState();
            if (SelectedSlot?.Template is null)
            {
                SelectedSlot = null;
            }

            // Expanded template selector: re-resolve to the NEW instance with the same id (a
            // rename/pin-elsewhere refresh must not collapse the action strip the operator is
            // actively looking at), or clear it if that template no longer exists (deleted).
            SelectedLibraryItem = selectedLibraryItemId is null
                ? null
                : AllTemplates.FirstOrDefault(t => t.Id == selectedLibraryItemId);

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
        row.DeleteFromRackCommand = DeleteFromRackCommand;
        row.RenameCommand = RenameCommand;
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
    /// this project's own "no silent surprising side effect" doctrine elsewhere). Templates rack
    /// rework: also backs the rack's own "Unpin" action strip button/context-menu item -- a rack
    /// slot's row is by definition already pinned, so it's the SAME toggle command with a different
    /// label at that call site, not a separate command.</summary>
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
            // Templates rack rework: unpinning the currently-loaded template clears its own "Loaded"
            // badge -- the canvas itself is untouched, only the rack's own indicator of what it came
            // from, since that template is no longer even IN the rack to point at.
            if (row.Id == _loadedTemplateId)
            {
                SetLoadedTemplate(null);
            }

            await RefreshAsync();
        }
    }

    /// <summary>Templates rack rework -- the rack's own Delete, a REAL confirm dialog via
    /// <see cref="ConfirmRequested"/> (deliberately not the Library list's inline arm/confirm -- see
    /// <see cref="TemplateListRowViewModel.DeleteFromRackCommand"/>'s own doc comment). Declining
    /// (or <see cref="ConfirmRequested"/> being unwired) leaves everything untouched.</summary>
    [RelayCommand]
    private async Task DeleteFromRackAsync(TemplateListRowViewModel? row)
    {
        if (row is null || ConfirmRequested is null)
        {
            return;
        }

        // yoniq-auditor finding: the confirm-dialog await now lives inside the same try as the
        // delete itself -- a throw from ConfirmRequested (e.g. the owner window closing mid-await)
        // used to be outside it. [RelayCommand]'s AsyncRelayCommand happens to swallow an unhandled
        // exception into the command's own faulted Task rather than crashing the process the way an
        // async void handler would, but that's an accident of the command type, not a deliberate
        // choice -- treated the same as a real delete failure here instead.
        try
        {
            var slotNumber = Slots.FirstOrDefault(s => s.Template?.Id == row.Id)?.SlotNumber ?? 0;
            var confirmVm = new ConfirmActionDialogViewModel(
                _localization.GetString("Panes.TxImageEditor.ConfirmDeleteRackTitle", row.Name),
                _localization.GetString("Panes.TxImageEditor.ConfirmDeleteRackBody", slotNumber),
                _localization.GetString("Panes.TxImageEditor.ConfirmDeleteRackButton"),
                _localization.GetString("Panes.TxImageEditor.DialogCancel"));
            if (!await ConfirmRequested(confirmVm).ConfigureAwait(true))
            {
                return;
            }

            await _templateStore.DeleteAsync(row.Id);
            if (row.Id == _loadedTemplateId)
            {
                SetLoadedTemplate(null);
            }

            StatusMessage = null;
        }
        catch (Exception ex)
        {
            Log.DeleteFailed(_logger, row.Id, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.DeleteTemplateFailed");
            return;
        }

        await RefreshAsync();
    }

    /// <summary>Templates rack rework -- commits the rack action strip's inline-rename `TextBox`
    /// (<see cref="TemplateListRowViewModel.EditingName"/>). An empty/whitespace-only name reverts
    /// (no store call, per the approved design's own "empty name reverts" rule) rather than erroring
    /// -- <see cref="RefreshAsync"/> would overwrite <see cref="TemplateListRowViewModel.EditingName"/>
    /// with the unchanged real name anyway, this just avoids the round-trip.</summary>
    [RelayCommand]
    private async Task RenameAsync(TemplateListRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var trimmed = row.EditingName.Trim();
        if (trimmed.Length == 0)
        {
            row.EditingName = row.Name;
            return;
        }

        if (trimmed == row.Name)
        {
            return;
        }

        // yoniq-auditor finding: renaming to an already-used name used to go straight to the store
        // with no check -- SaveTemplateAsync's own overwrite-by-matching-name lookup
        // (case-insensitive) would then silently repoint a LATER save at whichever of the two
        // same-named templates ListAsync happens to enumerate first. Rejected up front instead,
        // same case-insensitive comparison SaveTemplateAsync itself already uses.
        if (AllTemplates.Any(t => t.Id != row.Id && string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            row.EditingName = row.Name;
            StatusMessage = _localization.GetString("Panes.TxImageEditor.RenameTemplateNameInUse");
            return;
        }

        try
        {
            await _templateStore.RenameAsync(row.Id, trimmed);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            Log.RenameFailed(_logger, row.Id, ex);
            StatusMessage = _localization.GetString("Panes.TxImageEditor.RenameTemplateFailed");
            return;
        }

        await RefreshAsync();
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17): "Template DELETE is a single
    /// unconfirmed click ... in a dense row." Real confirm dialog via <see cref="ConfirmRequested"/>,
    /// same shape as <see cref="DeleteFromRackAsync"/> -- user-reported feedback (2026-09-15): this
    /// command used to arm/confirm inline instead (a second click on the SAME row's Delete button),
    /// the one remaining "click twice" holdout in this class once the rack got its own dialog.
    /// Migrated to match; declining (or <see cref="ConfirmRequested"/> being unwired) leaves
    /// everything untouched, same safe-default reasoning as the rack's own version.</summary>
    [RelayCommand]
    private async Task DeleteAsync(TemplateListRowViewModel? row)
    {
        if (row is null || ConfirmRequested is null)
        {
            return;
        }

        try
        {
            var confirmVm = new ConfirmActionDialogViewModel(
                _localization.GetString("Panes.TxImageEditor.ConfirmDeleteTitle", row.Name),
                _localization.GetString("Panes.TxImageEditor.ConfirmDeleteBody"),
                _localization.GetString("Panes.TxImageEditor.ConfirmDeleteButton"),
                _localization.GetString("Panes.TxImageEditor.DialogCancel"));
            if (!await ConfirmRequested(confirmVm).ConfigureAwait(true))
            {
                return;
            }

            await _templateStore.DeleteAsync(row.Id);
            if (row.Id == _loadedTemplateId)
            {
                SetLoadedTemplate(null);
            }

            StatusMessage = null;
        }
        catch (Exception ex)
        {
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "Template Library UI settings load failed")]
        public static partial void LoadTemplateLibraryUiSettingsFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Template Library UI settings persist failed")]
        public static partial void PersistTemplateLibraryUiSettingsFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack delete failed: templateId={TemplateId}")]
        public static partial void DeleteFailed(ILogger logger, string templateId, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack rename failed: templateId={TemplateId}")]
        public static partial void RenameFailed(ILogger logger, string templateId, Exception exception);

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
