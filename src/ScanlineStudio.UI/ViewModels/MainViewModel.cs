using CommunityToolkit.Mvvm.ComponentModel;
using Dock.Model.Controls;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Docking;

namespace ScanlineStudio.UI.ViewModels;

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
