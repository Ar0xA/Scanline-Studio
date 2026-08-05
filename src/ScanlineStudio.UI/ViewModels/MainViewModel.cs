using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Microsoft.Extensions.DependencyInjection;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Docking;

namespace ScanlineStudio.UI.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private IRootDock? _layout;

    public MainViewModel(AppDockFactory dockFactory, IRadioSessionService radioSession, ISstvSessionService sstvSession, ILocalizationService localization, IServiceProvider services)
    {
        _services = services;

        var layout = dockFactory.CreateLayout();
        dockFactory.InitLayout(layout);
        Layout = layout;
        DockFactory = dockFactory;

        RadioStatus = new RadioStatusViewModel(radioSession, sstvSession, localization);
    }

    public RadioStatusViewModel RadioStatus { get; }

    /// <summary>Exposed for `MainWindow.axaml`'s View menu -- see AppDockFactory's own doc comment
    /// for why pane reopen commands live there rather than being duplicated here.</summary>
    public AppDockFactory DockFactory { get; }

    /// <summary>Carries the freshly-DI-resolved <see cref="OptionsWindowViewModel"/> so
    /// `MainWindow`'s code-behind can construct/show the actual `Window` -- a view-model must never
    /// construct a View itself, and there is no `IDialogService` abstraction yet (first
    /// dialog/Window in the app; not worth building one for a single caller).</summary>
    public event Action<OptionsWindowViewModel>? OptionsRequested;

    [RelayCommand]
    private void OpenOptions() => OptionsRequested?.Invoke(_services.GetRequiredService<OptionsWindowViewModel>());
}
