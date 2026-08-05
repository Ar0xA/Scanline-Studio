using CommunityToolkit.Mvvm.Input;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Localization;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Docking;

/// <summary>Builds the main window's dock layout — see the Phase-3 plan's decision #8 (revised):
/// each real pane view-model derives from Dock's <see cref="Tool"/>/<c>Document</c> directly (the
/// pane VM IS the dockable) — the proven-working <c>Dock.Model.Mvvm</c> convention, not a thin shell
/// wrapping a separate plain view-model via <c>Context</c> (that shape rendered a tab header but a
/// blank body; see the pre-build spike's own history for the full story).
///
/// Registered in DI (decision #7 — this, not a static locator, is how a Dock-constructed pane gets
/// its real dependencies: constructor-injected into THIS factory, and <see cref="CreateLayout"/>
/// passes them straight into each pane view-model's own constructor). Three real panes (decision #9's
/// scope trim — the radio/frequency readout is `MainWindow` chrome, not a 4th dockable pane).
///
/// Phase 4 added the minimal View menu (spec/09-ui.md's "Pane visibility" section): closing a
/// dockable `Tool` has no built-in reopen path, so this factory keeps a reference to each closable
/// pane instance and its home `ToolDock` after <see cref="CreateLayout"/> builds them, re-adding via
/// <see cref="Factory.AddDockable"/> (confirmed via reflection against the installed `Dock.Model`
/// package -- there is no `RestoreDockable` on `IFactory` itself, only `IDockState.Restore`, which is
/// a different save/restore-layout-to-disk mechanism this pass doesn't use) rather than the same VM
/// instance being destroyed and recreated.</summary>
public sealed partial class AppDockFactory : Factory
{
    private readonly ISstvSessionService _sstvSession;
    private readonly IImageFileLoader _imageFileLoader;
    private readonly IStockImageLibrary _stockLibrary;
    private readonly IReceiveHistoryStore _historyStore;
    private readonly IFilePickerService _filePickerService;
    private readonly ILocalizationService _localization;

    private WaterfallPaneViewModel? _waterfall;
    private RxImagePaneViewModel? _rxImage;
    private RxHistoryPaneViewModel? _rxHistory;
    private ToolDock? _waterfallToolDock;
    private ToolDock? _rxToolDock;

    public AppDockFactory(
        ISstvSessionService sstvSession,
        IImageFileLoader imageFileLoader,
        IStockImageLibrary stockLibrary,
        IReceiveHistoryStore historyStore,
        IFilePickerService filePickerService,
        ILocalizationService localization)
    {
        _sstvSession = sstvSession;
        _imageFileLoader = imageFileLoader;
        _stockLibrary = stockLibrary;
        _historyStore = historyStore;
        _filePickerService = filePickerService;
        _localization = localization;
    }

    public override IRootDock CreateLayout()
    {
        var waterfall = new WaterfallPaneViewModel(_sstvSession, _localization);
        var rxImage = new RxImagePaneViewModel(_sstvSession, _localization);
        var rxHistory = new RxHistoryPaneViewModel(_historyStore, _localization);
        var txControls = new TxControlsPaneViewModel(_sstvSession, _imageFileLoader, _stockLibrary, _filePickerService, _localization);

        // Waterfall is a fixed strip above RX Image/RX History, not tab-grouped with them --
        // real user feedback after actually seeing it running: a tabbed waterfall took over the
        // whole region when active (a spectrum/waterfall visualization doesn't need that much
        // space), and every real SDR-instrument reference this project's own Aesthetic Directive
        // cites (SDR++/cuSDR64/Perseus) keeps the waterfall as a persistent strip, never a tab a
        // user must switch away from RX to see. Supersedes spec/09-ui.md's earlier "reserve massive
        // grid cells for the waterfall" framing -- see that doc's updated "Main window layout"
        // section.
        var waterfallToolDock = new ToolDock
        {
            Id = "WaterfallToolDock",
            Title = "WaterfallToolDock",
            VisibleDockables = CreateList<IDockable>(waterfall),
            ActiveDockable = waterfall,
        };

        var rxToolDock = new ToolDock
        {
            Id = "RxToolDock",
            Title = "RxToolDock",
            VisibleDockables = CreateList<IDockable>(rxImage, rxHistory),
            ActiveDockable = rxImage,
        };

        _waterfall = waterfall;
        _rxImage = rxImage;
        _rxHistory = rxHistory;
        _waterfallToolDock = waterfallToolDock;
        _rxToolDock = rxToolDock;

        var leftStack = new ProportionalDock
        {
            Id = "LeftStack",
            Title = "LeftStack",
            Orientation = Orientation.Vertical,
            VisibleDockables = CreateList<IDockable>(waterfallToolDock, rxToolDock),
        };
        waterfallToolDock.Proportion = 0.2;
        rxToolDock.Proportion = 0.8;

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
            VisibleDockables = CreateList<IDockable>(leftStack, txToolDock),
        };
        leftStack.Proportion = 0.7;
        txToolDock.Proportion = 0.3;

        var rootDock = CreateRootDock();
        rootDock.Id = "Root";
        rootDock.Title = "Root";
        rootDock.VisibleDockables = CreateList<IDockable>(layout);
        rootDock.ActiveDockable = layout;
        rootDock.DefaultDockable = layout;

        return rootDock;
    }

    [RelayCommand]
    private void ShowWaterfall() => ShowPane(_waterfall, _waterfallToolDock);

    [RelayCommand]
    private void ShowRxImage() => ShowPane(_rxImage, _rxToolDock);

    [RelayCommand]
    private void ShowRxHistory() => ShowPane(_rxHistory, _rxToolDock);

    /// <summary>No-op if <paramref name="pane"/> is already visible in <paramref name="homeDock"/> --
    /// <see cref="Factory.AddDockable"/> doesn't itself guard against double-adding the same instance,
    /// and a menu item has no other way to know whether the pane is currently open (no per-pane
    /// `IsClosable`-visibility binding wired yet; this pass is "always offer to reopen," not a
    /// checked/toggleable menu state).</summary>
    private void ShowPane(IDockable? pane, IToolDock? homeDock)
    {
        if (pane is null || homeDock is null)
        {
            return;
        }

        if (homeDock.VisibleDockables?.Contains(pane) == true)
        {
            homeDock.ActiveDockable = pane;
            return;
        }

        AddDockable(homeDock, pane);
        homeDock.ActiveDockable = pane;
    }
}
