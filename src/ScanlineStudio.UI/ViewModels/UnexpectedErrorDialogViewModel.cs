using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>OK-only notice for an error the global UI-thread handler caught; the text is static and
/// translated in the View. Same <c>RequestClose</c>/<c>CloseCommand</c> convention as
/// <see cref="HamlibLibraryReloadFailedDialogViewModel"/>.</summary>
public sealed partial class UnexpectedErrorDialogViewModel : ObservableObject
{
    public event Action? RequestClose;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
