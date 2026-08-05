using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using Yoniq.Abstractions.Localization;
using Yoniq.Abstractions.Radio;
using Yoniq.Application;
using Yoniq.UI.Docking;

namespace Yoniq.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private IRootDock? _layout;

    public MainViewModel(AppDockFactory dockFactory, IRadioSessionService radioSession, ILocalizationService localization)
    {
        var layout = dockFactory.CreateLayout();
        dockFactory.InitLayout(layout);
        Layout = layout;

        RadioStatus = new RadioStatusViewModel(radioSession, localization);
    }

    public RadioStatusViewModel RadioStatus { get; }
}
