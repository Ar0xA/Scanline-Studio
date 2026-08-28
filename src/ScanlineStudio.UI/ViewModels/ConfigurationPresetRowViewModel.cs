using CommunityToolkit.Mvvm.ComponentModel;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>One row of <see cref="ConfigurationsManagerWindowViewModel.Presets"/> — Configurations-
/// preset backlog. Keyed on <see cref="Name"/> (mutable).</summary>
public sealed partial class ConfigurationPresetRowViewModel : ObservableObject
{
    public ConfigurationPresetRowViewModel(string name, bool isActive, bool isProtected)
    {
        _name = name;
        _isActive = isActive;
        IsProtected = isProtected;
    }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private bool _isActive;

    /// <summary>User-requested (2026-08-28): the reserved "Default" configuration -- always present,
    /// always listed LAST, and immune to both Delete and Rename. Computed once at construction from
    /// <see cref="ConfigurationsManagerWindowViewModel.DefaultPresetName"/>. Rename WAS briefly allowed
    /// on Default mid-session, then reverted (user-caught): protection is keyed on the literal string
    /// "Default", not any persistent identity, so renaming it doesn't customize Default -- it detaches
    /// the content under a new, unprotected name while <see cref="ConfigurationsManagerWindowViewModel.RefreshAsync"/>'s
    /// own seed-if-missing check spawns a BRAND NEW "Default" from current live settings the next time
    /// the menu opens. Two near-identical presets where the user expected one renamed one.</summary>
    public bool IsProtected { get; }

    /// <summary>Phase 4b (cascading-menu redesign): the only real guard against deleting the
    /// currently-active, non-Default configuration -- absent from the earlier modal-dialog build,
    /// where only <see cref="IsProtected"/> was checked. Not annotated with
    /// <c>[NotifyPropertyChangedFor(nameof(IsActive))]</c>-driven notification since nothing binds to
    /// it yet (this app builds Configurations-menu items imperatively in code-behind, not via a
    /// template) -- add that wiring if a future XAML binding needs it to react live.</summary>
    public bool CanDelete => !IsProtected && !IsActive;
}
