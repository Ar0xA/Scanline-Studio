using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Minimal OK-only acknowledgement dialog -- Configurations-preset backlog, Phase 4, round-1
/// code-review fix (2026-08-28): the WSJT-X-style quick-switch menu (`MainWindow.axaml.cs`) invokes
/// <see cref="ConfigurationsManagerWindowViewModel.SwitchToRowCommand"/> against a short-lived VM
/// instance that is NEVER shown -- before this fix, a rejected/failed quick switch (e.g. clicking a
/// configuration while transmitting) set that VM's own <c>ErrorMessage</c> with nothing bound to it,
/// so the click did visibly nothing at all. Same <c>RequestClose</c>/<c>CloseCommand</c> convention as
/// <see cref="RestartRequiredDialogViewModel"/>; same "carries a real payload" shape as
/// <see cref="HamlibLibraryReloadFailedDialogViewModel"/> (the message is whatever
/// <c>ConfigurationsManagerWindowViewModel.ErrorMessage</c> already resolved, not a static locale
/// string) -- switching-while-transmitting/recording/concurrent-switch-in-progress/an outright throw
/// are all real, reachable outcomes with different text, so a single static message would be wrong
/// for most of them.</summary>
public sealed partial class QuickSwitchFailedDialogViewModel : ObservableObject
{
    public event Action? RequestClose;

    public QuickSwitchFailedDialogViewModel(string message)
    {
        Message = message;
    }

    public string Message { get; }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
