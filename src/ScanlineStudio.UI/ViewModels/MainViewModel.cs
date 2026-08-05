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
        DockFactory = dockFactory;

        RadioStatus = new RadioStatusViewModel(radioSession, localization);
    }

    public RadioStatusViewModel RadioStatus { get; }

    /// <summary>Exposed for `MainWindow.axaml`'s View menu -- see AppDockFactory's own doc comment
    /// for why pane reopen commands live there rather than being duplicated here.</summary>
    public AppDockFactory DockFactory { get; }
}
