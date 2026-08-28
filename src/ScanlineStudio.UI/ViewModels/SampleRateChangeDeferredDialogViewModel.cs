using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Minimal OK-only acknowledgement dialog -- see
/// <see cref="OptionsWindowViewModel.SampleRateChangeDeferredWarningRequested"/>'s own doc comment
/// for why it exists. No state beyond the close command; the message itself is static, translated
/// directly in the View. Same <c>RequestClose</c>/<c>CloseCommand</c> convention as
/// <see cref="RestartRequiredDialogViewModel"/>.</summary>
public sealed partial class SampleRateChangeDeferredDialogViewModel : ObservableObject
{
    public event Action? RequestClose;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
