using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Small reusable Yes/No confirm dialog — Configurations-preset backlog, Phase 4b
/// (2026-08-28, cascading-menu redesign, `docs/plans/configuration-presets-phase4b-cascading-menu-plan.md`).
/// Backs Delete's and Reset's "are you sure" step now that the modal "Manage Configurations" dialog
/// (which used an in-row two-click arm/confirm) is gone — a menu item has no row to arm. Constructed
/// with per-invocation title/message, always `new`'d directly by its opener
/// (<c>MainWindow.axaml.cs</c>), same shape as <see cref="TextPromptWindowViewModel"/>.</summary>
public sealed partial class ConfirmActionDialogViewModel : ObservableObject
{
    public ConfirmActionDialogViewModel(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }

    public string Message { get; }

    /// <summary>Carries the answer — <see langword="true"/> only on an explicit Yes. No/Cancel/a
    /// titlebar close all raise <see langword="false"/> (the view's own <c>IsCancel</c> button and
    /// <c>ShowDialog&lt;bool&gt;</c>'s own close-with-no-result default both agree on this), matching
    /// this dialog's whole purpose: the safe default is always "nothing happened."</summary>
    public event Action<bool>? RequestClose;

    [RelayCommand]
    private void Confirm() => RequestClose?.Invoke(true);

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);
}
