using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Yoniq.Abstractions.Imaging;
using Yoniq.Abstractions.Localization;
using Yoniq.Abstractions.Sstv;
using Yoniq.Application;
using Yoniq.UI.Services;
using Yoniq.UI.ViewModels;

namespace Yoniq.UI.Docking;

/// <summary>Builds the main window's dock layout — see the Phase-3 plan's decision #8 (revised):
/// each real pane view-model derives from Dock's <see cref="Tool"/>/<c>Document</c> directly (the
/// pane VM IS the dockable) — the proven-working <c>Dock.Model.Mvvm</c> convention, not a thin shell
/// wrapping a separate plain view-model via <c>Context</c> (that shape rendered a tab header but a
/// blank body; see the pre-build spike's own history for the full story).
///
/// Registered in DI (decision #7 — this, not a static locator, is how a Dock-constructed pane gets
/// its real dependencies: constructor-injected into THIS factory, and <see cref="CreateLayout"/>
/// passes them straight into each pane view-model's own constructor). Three real panes (decision #9's
/// scope trim — the radio/frequency readout is `MainWindow` chrome, not a 4th dockable pane).</summary>
public sealed class AppDockFactory : Factory
{
    private readonly ISstvSessionService _sstvSession;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IFilePickerService _filePickerService;
    private readonly ILocalizationService _localization;

    public AppDockFactory(
        ISstvSessionService sstvSession,
        IImageFileLoader imageFileLoader,
        IFilePickerService filePickerService,
        ILocalizationService localization)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _filePickerService = filePickerService;
        _localization = localization;
    }

    public override IRootDock CreateLayout()
    {
        var waterfall = new WaterfallPaneViewModel(_sstvSession, _localization);
        var rxImage = new RxImagePaneViewModel(_sstvSession, _localization);
        var txControls = new TxControlsPaneViewModel(_sstvSession, _imageFileLoader, _filePickerService, _localization);

        var mainToolDock = new ToolDock
        {
            Id = "MainToolDock",
            Title = "MainToolDock",
            VisibleDockables = CreateList<IDockable>(waterfall, rxImage),
            ActiveDockable = waterfall,
        };

        var txToolDock = new ToolDock
        {
            Id = "TxToolDock",
            Title = "TxToolDock",
            VisibleDockables = CreateList<IDockable>(txControls),
            ActiveDockable = txControls,
        };

        var layout = new ProportionalDock
        {
            Id = "MainLayout",
            Title = "MainLayout",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(mainToolDock, txToolDock),
        };
        mainToolDock.Proportion = 0.7;
        txToolDock.Proportion = 0.3;

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.VisibleDockables = CreateList<IDockable>(layout);
        rootDock.ActiveDockable = layout;
        rootDock.DefaultDockable = layout;

        return rootDock;
    }
}
