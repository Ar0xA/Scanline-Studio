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

    /// <summary>User request: a "Config" button next to Close, jumping straight to the Radio (CAT)
    /// tab of Options -- this VM has no DI access of its own (see this class's own doc comment), so
    /// it can't resolve/show <c>OptionsWindowViewModel</c> itself; <c>MainWindow.axaml.cs</c>'s own
    /// give-up-popup handler (which already owns this window's lifecycle) subscribes to this event
    /// and calls <c>MainViewModel.OpenOptionsToRadioTabCommand</c> in response, same
    /// view-model-never-touches-a-Window reasoning as every other cross-window request in this
    /// app.</summary>
    public event Action? ConfigRequested;

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    [RelayCommand]
    private void OpenConfig()
    {
        // Close first, then request Options: MainWindow.axaml.cs's own ConfigRequested handler
        // opens a SECOND modal (Options) owned by the same MainWindow -- closing this one first
        // avoids two ShowDialog calls against the same owner briefly overlapping mid-transition.
        RequestClose?.Invoke();
        ConfigRequested?.Invoke();
    }
}
