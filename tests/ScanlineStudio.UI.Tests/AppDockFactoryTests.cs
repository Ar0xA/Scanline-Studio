using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using ScanlineStudio.UI.Docking;

namespace ScanlineStudio.UI.Tests;

/// <summary>Phase 4's View menu (spec/09-ui.md's "Pane visibility" section) drives these commands
/// directly -- see AppDockFactory's own doc comment for why `Factory.AddDockable` is used instead of
/// a `RestoreDockable` that doesn't actually exist on `IFactory` (confirmed by reflection against the
/// installed `Dock.Model` package before writing this). Tested against the real `AppDockFactory`/
/// `Factory` API rather than real X11 mouse clicks -- this project's headless Avalonia test platform
/// already exercises every other pane this way (see PaneViewModelTests), and driving a synthetic
/// window-manager click through XTEST proved unreliable in this sandbox for a Dock.Avalonia tab
/// strip specifically.</summary>
public sealed class AppDockFactoryTests
{
    [AvaloniaFact]
    public void ShowRxHistoryCommand_AfterClosingThePane_ReAddsTheSameInstanceToItsHomeToolDock()
    {
        var factory = new AppDockFactory(
            new FakeSstvSessionService(),
            new FakeImageFileLoader(),
            new FakeStockImageLibrary(),
            new FakeReceiveHistoryStore(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService(),
            new FakeLocalizationService());

        var layout = factory.CreateLayout();
        factory.InitLayout(layout);

        var rxToolDock = FindToolDockById(layout, "RxToolDock");
        var rxHistory = rxToolDock.VisibleDockables!.Single(d => d.Id == "RxHistory");

        factory.CloseDockable(rxHistory);
        Assert.DoesNotContain(rxHistory, rxToolDock.VisibleDockables!);

        factory.ShowRxHistoryCommand.Execute(null);

        Assert.Contains(rxHistory, rxToolDock.VisibleDockables!);
        Assert.Same(rxHistory, rxToolDock.ActiveDockable);
    }

    [AvaloniaFact]
    public void ShowWaterfallCommand_WhenThePaneIsAlreadyOpen_JustActivatesItWithoutDuplicating()
    {
        var factory = new AppDockFactory(
            new FakeSstvSessionService(),
            new FakeImageFileLoader(),
            new FakeStockImageLibrary(),
            new FakeReceiveHistoryStore(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService(),
            new FakeLocalizationService());

        var layout = factory.CreateLayout();
        factory.InitLayout(layout);

        var waterfallToolDock = FindToolDockById(layout, "WaterfallToolDock");
        var countBefore = waterfallToolDock.VisibleDockables!.Count;

        factory.ShowWaterfallCommand.Execute(null);

        Assert.Equal(countBefore, waterfallToolDock.VisibleDockables!.Count);
    }

    [AvaloniaFact]
    public void CreateLayout_PutsTheWaterfallInItsOwnDockSeparateFromRxImageAndRxHistory()
    {
        // Real user feedback after seeing it running: a tab-grouped waterfall took over the whole
        // region when active; it must be a separate, always-visible strip, not a fourth tab
        // alongside RX Image/RX History.
        var factory = new AppDockFactory(
            new FakeSstvSessionService(),
            new FakeImageFileLoader(),
            new FakeStockImageLibrary(),
            new FakeReceiveHistoryStore(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService(),
            new FakeLocalizationService());

        var layout = factory.CreateLayout();

        var waterfallToolDock = FindToolDockById(layout, "WaterfallToolDock");
        var rxToolDock = FindToolDockById(layout, "RxToolDock");

        Assert.Single(waterfallToolDock.VisibleDockables!);
        Assert.Equal("Waterfall", waterfallToolDock.VisibleDockables!.Single().Id);
        Assert.Equal(["RxImage", "RxHistory"], rxToolDock.VisibleDockables!.Select(d => d.Id));
    }

    private static IToolDock FindToolDockById(IRootDock layout, string id)
    {
        return (IToolDock)FindDockable(layout, id)!;
    }

    private static IDockable? FindDockable(IDockable node, string id)
    {
        if (node.Id == id)
        {
            return node;
        }

        if (node is IDock { VisibleDockables: { } children })
        {
            foreach (var child in children)
            {
                var found = FindDockable(child, id);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }
}
