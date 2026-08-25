using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Backs the give-up-after-5 feature's modal popup (fired from
/// <see cref="RadioStatusViewModel.ConnectionGaveUp"/>) -- replaces that feature's earlier
/// dismissible-toast per direct user feedback ("should be a popup window, not a tiny text under the
/// VFO"); a persistent header text line was tried alongside the popup for a while too, then removed
/// (redundant once the popup is must-acknowledge). Constructed directly with <see langword="new"/>
/// (no DI dependencies), same reasoning as <see cref="AboutWindowViewModel"/>'s own doc comment:
/// this VM only ever needs the already-localized message text handed to it, nothing
/// injectable.</summary>
public sealed partial class RadioConnectionGaveUpWindowViewModel : ObservableObject
{
    public string Message { get; }

    public RadioConnectionGaveUpWindowViewModel(string message)
    {
        Message = message;
    }

    /// <summary>Same convention as <see cref="AboutWindowViewModel.RequestClose"/> -- the View's
    /// code-behind subscribes <c>vm.RequestClose += Close;</c>.</summary>
    public event Action? RequestClose;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
