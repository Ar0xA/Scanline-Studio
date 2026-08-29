using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Identifies which quick-mode-grid slot is being reassigned to which mode -- the single
/// parameter carried by both <c>RxImagePaneViewModel.ReassignQuickModeSlotCommand</c> and
/// <c>TxControlsPaneViewModel.ReassignQuickModeSlotCommand</c>.</summary>
public sealed record QuickModeReassignment(int SlotIndex, SstvModeDefinition Mode);

/// <summary>One row in a quick-mode-grid button's right-click popup. Carries its OWN
/// <see cref="ReassignCommand"/> reference (the parent's command, captured once at construction)
/// rather than the View reaching back into an ancestor's DataContext with a cross-DataTemplate
/// binding path -- that exact shape (<c>$parent[...].((vm:Type)DataContext)</c>) compiled fine but
/// crashed at runtime the first time this app actually populated a non-empty deferred
/// <c>ItemsControl</c> template (see <see cref="StockEntryViewModel"/>'s own doc comment for the
/// original incident). <see cref="IsChecked"/>/<see cref="IsEnabled"/> are mutated in place by
/// <c>RecomputeQuickModeMenuEntryStates</c> -- this collection of 43 entries is built exactly once
/// per slot and never cleared/repopulated, since which modes exist never changes, only which one is
/// currently checked/available does.</summary>
public sealed partial class QuickModeMenuEntryViewModel : ObservableObject
{
    public QuickModeMenuEntryViewModel(SstvModeDefinition mode, ICommand reassignCommand, int slotIndex)
    {
        Mode = mode;
        ReassignCommand = reassignCommand;
        CommandParameter = new QuickModeReassignment(slotIndex, mode);
    }

    public SstvModeDefinition Mode { get; }

    public ICommand ReassignCommand { get; }

    public QuickModeReassignment CommandParameter { get; }

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isEnabled = true;
}

/// <summary>Resolves a persisted (possibly missing/corrupt/foreign) quick-mode-grid id list into 16
/// real, distinct <see cref="SstvModeDefinition"/> assignments -- shared by RX and TX since the
/// algorithm itself has no RX/TX-specific state, only the settings section it reads from differs.
/// Per slot, in order: the persisted id if it resolves to a real mode not already claimed by an
/// earlier slot in this same pass; else <see cref="QuickModeGridDefaults.Ids"/>'s id for that slot,
/// same claim check; else the first not-yet-claimed mode in <c>availableModes</c> (always exists --
/// there are 43 modes and 16 slots). Guarantees: never throws, never returns a duplicate mode across
/// slots, never leaves a slot unresolved.</summary>
public static class QuickModeGridAssignment
{
    public static IReadOnlyList<SstvModeDefinition> Resolve(IReadOnlyList<string>? persistedIds, IReadOnlyList<SstvModeDefinition> availableModes)
    {
        // Capped at availableModes.Count, not a flat 16: production always has 43 modes
        // (SstvModeRegistry.All) so this never actually bites there, but plenty of this pane's OWN
        // unit tests construct it against a FakeSstvSessionService carrying only 1-2 modes (or none)
        // for unrelated reasons -- a flat 16-slot requirement would crash the constructor for every
        // one of those, since 16 distinct real modes wouldn't exist to assign. Fewer slots than 16
        // (down to zero) is a defensive, never-normally-reached degenerate case, not a real UX state.
        var slotCount = Math.Min(QuickModeGridDefaults.Ids.Count, availableModes.Count);
        var claimedIds = new HashSet<string>();
        var resolved = new SstvModeDefinition[slotCount];

        SstvModeDefinition? FindUnclaimed(string? id) =>
            id is not null && availableModes.FirstOrDefault(m => m.Id == id) is { } mode && !claimedIds.Contains(mode.Id)
                ? mode
                : null;

        for (var i = 0; i < slotCount; i++)
        {
            var persistedId = persistedIds is not null && persistedIds.Count == QuickModeGridDefaults.Ids.Count ? persistedIds[i] : null;
            var defaultId = i < QuickModeGridDefaults.Ids.Count ? QuickModeGridDefaults.Ids[i] : null;
            var mode = FindUnclaimed(persistedId) ?? FindUnclaimed(defaultId) ?? availableModes.First(m => !claimedIds.Contains(m.Id));
            claimedIds.Add(mode.Id);
            resolved[i] = mode;
        }

        return resolved;
    }
}

/// <summary>One button in the 16-slot quick-mode grid. Carries its own <see cref="SelectCommand"/>
/// (the parent's EXISTING <c>QuickSelectModeCommand</c>, captured once at construction, same
/// cross-DataTemplate-binding reasoning as <see cref="QuickModeMenuEntryViewModel"/> above) -- left-
/// click behavior is unchanged by this feature, only its <c>CommandParameter</c> becomes data-driven
/// (<see cref="CurrentMode"/>'s id) instead of one hardcoded string per XAML line.
/// <see cref="MenuEntries"/> is built once (43 entries, one per <c>AvailableModes</c>) and never
/// cleared/repopulated -- see that type's own doc comment.</summary>
public sealed partial class QuickModeSlotViewModel : ObservableObject
{
    public QuickModeSlotViewModel(int slotIndex, SstvModeDefinition currentMode, ICommand selectCommand, ICommand? holdModeCommand = null)
    {
        SlotIndex = slotIndex;
        SelectCommand = selectCommand;
        HoldModeCommand = holdModeCommand;
        _currentMode = currentMode;
        _shortCaption = QuickModeShortNames.GetShortName(currentMode);
    }

    public int SlotIndex { get; }

    public ICommand SelectCommand { get; }

    /// <summary>ui_transition_plan.md step 10 (T2-5): the RX quick-mode grid's own "Hold this mode"
    /// context-menu entry -- <see langword="null"/> for the TX pane's grid (this feature is RX-only;
    /// TX mode selection has no auto-detect to hold against, per the plan's own scoping). Same
    /// captured-once-at-construction pattern as <see cref="SelectCommand"/> above, not a
    /// cross-DataTemplate binding path.</summary>
    public ICommand? HoldModeCommand { get; }

    public ObservableCollection<QuickModeMenuEntryViewModel> MenuEntries { get; } = [];

    [ObservableProperty]
    private SstvModeDefinition _currentMode;

    /// <summary>The button's own caption -- <see cref="QuickModeShortNames.GetShortName"/> of
    /// <see cref="CurrentMode"/>, kept in sync by <see cref="OnCurrentModeChanged"/> rather than
    /// computed live in a converter, since <see cref="CurrentMode"/> mutates in place (this
    /// collection's identity never changes) and a plain computed property wouldn't raise its own
    /// change notification.</summary>
    [ObservableProperty]
    private string _shortCaption;

    partial void OnCurrentModeChanged(SstvModeDefinition value) => ShortCaption = QuickModeShortNames.GetShortName(value);
}
