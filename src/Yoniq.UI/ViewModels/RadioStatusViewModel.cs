using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Yoniq.Abstractions.Localization;
using Yoniq.Abstractions.Radio;
using Yoniq.Application;

namespace Yoniq.UI.ViewModels;

/// <summary>The fixed, read-only radio/frequency status strip — see the Phase-3 plan's pane-count
/// scope trim: this is `MainWindow` chrome, not a 4th dockable pane (`spec/14-roadmap.md`'s Phase 3
/// bullet and `spec/09-ui.md`'s inventory table both only call for 3 dockable panes). Not
/// interactive — connecting happens once at `Yoniq.Host` startup from persisted settings, per that
/// same decision.</summary>
public sealed partial class RadioStatusViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _frequencyDisplay;

    [ObservableProperty]
    private string _modeDisplay;

    public RadioStatusViewModel(IRadioSessionService radioSession, ILocalizationService localization)
    {
        _frequencyDisplay = localization.GetString("RadioStatus.NoFrequency");
        _modeDisplay = string.Empty;

        radioSession.StateChanges.Subscribe(OnStateChanged);
        if (radioSession.LastKnownState is { } state)
        {
            OnStateChanged(state);
        }
    }

    private void OnStateChanged(RadioState state)
    {
        // IRadioSessionService.StateChanges pushes synchronously from IRadioController's own poll
        // loop thread (see that interface's doc comment) -- marshal to the UI thread here, the only
        // place in this codebase allowed to touch Avalonia's Dispatcher (spec/01-architecture.md).
        Dispatcher.UIThread.Post(() =>
        {
            FrequencyDisplay = $"{state.FrequencyHz / 1_000_000.0:0.000000} MHz";
            ModeDisplay = state.Mode.ToString();
        });
    }
}
