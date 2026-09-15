using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Small reusable Yes/No confirm dialog — Configurations-preset backlog, Phase 4b
/// (2026-08-28, cascading-menu redesign, `docs/plans/configuration-presets-phase4b-cascading-menu-plan.md`).
/// Backs Delete's and Reset's "are you sure" step now that the modal "Manage Configurations" dialog
/// (which used an in-row two-click arm/confirm) is gone — a menu item has no row to arm. Constructed
/// with per-invocation title/message, always `new`'d directly by its opener
/// (<c>MainWindow.axaml.cs</c>/<c>FilePickerService.cs</c>), same shape as
/// <see cref="TextPromptWindowViewModel"/>.
/// <paramref name="confirmLabel"/>/<paramref name="cancelLabel"/> (Templates rack rework) are
/// REQUIRED, not defaulted -- yoniq-auditor finding: this VM has no <c>ILocalizationService</c>, so
/// an earlier draft tried to default a null label to <c>{loc:Translate ...}</c> via the XAML's own
/// <c>TargetNullValue</c>, but <c>TranslateExtension.ProvideValue</c> returns a <c>Binding</c>
/// object (needed so it also works inside a <c>MultiBinding</c>), and <c>TargetNullValue</c> stores
/// whatever it's given as a plain VALUE, not applied as a nested binding -- the button rendered the
/// literal text "Avalonia.Data.Binding" for every 2-arg caller. Every caller (old and new) now
/// passes its own already-localized label text explicitly, same "explicit per-invocation, no hidden
/// default" discipline this class's own title/message already follow.</summary>
public sealed partial class ConfirmActionDialogViewModel : ObservableObject
{
    public ConfirmActionDialogViewModel(string title, string message, string confirmLabel, string cancelLabel)
    {
        Title = title;
        Message = message;
        ConfirmLabel = confirmLabel;
        CancelLabel = cancelLabel;
    }

    public string Title { get; }

    public string Message { get; }

    public string ConfirmLabel { get; }

    public string CancelLabel { get; }

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
