using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Minimal OK-only acknowledgement dialog -- see
/// <see cref="OptionsWindowViewModel.HamlibLibraryReloadFailedWarningRequested"/>'s own doc comment
/// for why it exists. Same <c>RequestClose</c>/<c>CloseCommand</c> convention as
/// <see cref="RestartRequiredDialogViewModel"/>/<see cref="SampleRateChangeDeferredDialogViewModel"/>
/// -- unlike those two, which are fully static (their message is a locale string translated directly
/// in the View), this one carries a real payload: the specific failure detail
/// <c>ISstvSessionService.RequestSampleRateAsync</c>'s own sibling feature has no equivalent of,
/// since Hamlib discovery failures are per-attempt and worth showing verbatim rather than a generic
/// message.</summary>
public sealed partial class HamlibLibraryReloadFailedDialogViewModel : ObservableObject
{
    public event Action? RequestClose;

    public HamlibLibraryReloadFailedDialogViewModel(string message)
    {
        Message = message;
    }

    public string Message { get; }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
