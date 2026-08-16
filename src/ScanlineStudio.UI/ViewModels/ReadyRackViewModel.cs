using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Application;
using ScanlineStudio.Settings;

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

    // Assigned by ReadyRackViewModel right after construction -- same "child VM holds a direct
    // reference to the parent's command" wiring TxImageEditorPaneViewModel's own CreateOverlayElement
    // already establishes for RemoveCommand/MoveUpCommand/MoveDownCommand (this codebase's own
    // established alternative to a `$parent[...]`-ancestor-cast XAML binding, which throws
    // "ArgumentException: Unable to resolve type" at runtime with this project's classic, non-compiled
    // bindings -- see TxControlsPaneViewModel.RadioStatus's own doc comment for the same finding).
    public IRelayCommand<TemplateListRowViewModel>? LoadCommand { get; set; }

    public IRelayCommand<TemplateListRowViewModel>? TogglePinCommand { get; set; }

    public IRelayCommand<TemplateListRowViewModel>? DeleteCommand { get; set; }

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
    private readonly ILogger<ReadyRackViewModel> _logger;

    public ReadyRackViewModel(ITemplateStore templateStore, ISettingsStore settingsStore, ILogger<ReadyRackViewModel> logger)
    {
        _templateStore = templateStore;
        _settingsStore = settingsStore;
        _logger = logger;
        Slots = new ObservableCollection<ReadyRackSlotViewModel>(Enumerable.Range(1, SlotCount).Select(n => new ReadyRackSlotViewModel(n, RecallSlotCommand)));
    }

    public ObservableCollection<ReadyRackSlotViewModel> Slots { get; }

    public ObservableCollection<TemplateListRowViewModel> AllTemplates { get; } = [];

    /// <summary>Backs the Templates panel's empty-state text -- explicitly re-raised at the end of
    /// <see cref="RefreshAsync"/> (a plain computed property over <see cref="AllTemplates"/>.Count
    /// has no INotifyPropertyChanged of its own; <see cref="ObservableCollection{T}.CollectionChanged"/>
    /// isn't the same event WPF/Avalonia bindings listen for on a property path).</summary>
    public bool HasNoTemplates => AllTemplates.Count == 0;

    /// <summary>Fired when a template should be loaded into the live editor -- a rack slot
    /// click/number-key (<see cref="RecallSlot"/>) or an "All templates" row's Load action
    /// (<see cref="Load"/>) both funnel through here; <c>TxImageEditorPaneViewModel</c> subscribes
    /// once in its own constructor and calls its <c>LoadTemplateAsync</c>.</summary>
    public event Action<string>? TemplateSelected;

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
            var pinnedIds = await LoadPinnedIdsAsync();
            var validPinnedIds = pinnedIds.Where(metadataById.ContainsKey).ToList();
            if (validPinnedIds.Count != pinnedIds.Count)
            {
                await SavePinnedIdsAsync(validPinnedIds);
            }

            AllTemplates.Clear();
            foreach (var metadata in metadataList)
            {
                AllTemplates.Add(WireRowCommands(new TemplateListRowViewModel(metadata, validPinnedIds.Contains(metadata.Id))));
            }

            for (var i = 0; i < SlotCount; i++)
            {
                var hasPin = i < validPinnedIds.Count;
                Slots[i].Template = hasPin && metadataById.TryGetValue(validPinnedIds[i], out var pinnedMetadata)
                    ? WireRowCommands(new TemplateListRowViewModel(pinnedMetadata, isPinned: true))
                    : null;
            }

            OnPropertyChanged(nameof(HasNoTemplates));
        }
        catch (Exception ex)
        {
            Log.RefreshFailed(_logger, ex);
        }
    }

    private TemplateListRowViewModel WireRowCommands(TemplateListRowViewModel row)
    {
        row.LoadCommand = LoadCommand;
        row.TogglePinCommand = TogglePinCommand;
        row.DeleteCommand = DeleteCommand;
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

        var pinnedIds = (await LoadPinnedIdsAsync()).ToList();
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
            return;
        }

        await SavePinnedIdsAsync(pinnedIds);
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task DeleteAsync(TemplateListRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        try
        {
            await _templateStore.DeleteAsync(row.Id);
        }
        catch (Exception ex)
        {
            Log.DeleteFailed(_logger, row.Id, ex);
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

    private async Task<IReadOnlyList<string>> LoadPinnedIdsAsync()
    {
        var settings = await _settingsStore.LoadAsync();
        return settings.GetSection(ReadyRackSettings.SectionKey, ReadyRackSettingsJsonContext.Default.ReadyRackSettings)?.PinnedTemplateIds ?? [];
    }

    private async Task SavePinnedIdsAsync(IReadOnlyList<string> pinnedIds)
    {
        var settings = await _settingsStore.LoadAsync();
        var updated = settings.WithSection(
            ReadyRackSettings.SectionKey, new ReadyRackSettings { PinnedTemplateIds = pinnedIds }, ReadyRackSettingsJsonContext.Default.ReadyRackSettings);
        await _settingsStore.SaveAsync(updated);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack refresh failed")]
        public static partial void RefreshFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Warning, Message = "ReadyRack delete failed: templateId={TemplateId}")]
        public static partial void DeleteFailed(ILogger logger, string templateId, Exception exception);
    }
}
