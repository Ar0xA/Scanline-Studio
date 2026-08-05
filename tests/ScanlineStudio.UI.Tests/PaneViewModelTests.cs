using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class PaneViewModelTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test",
        DisplayName: "Test",
        VisCode: 0,
        ImageWidth: 1,
        ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential,
        LineSegments: []);

    [AvaloniaFact]
    public void RadioStatusViewModel_PushedState_UpdatesDisplayOnUiThread()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = new RadioStatusViewModel(radioSession, new FakeLocalizationService());

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("14.230000 MHz", vm.FrequencyDisplay);
        Assert.Equal("Usb", vm.ModeDisplay);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_PushedFrame_UpdatesLatestFrameOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService());
        var frame = new WaterfallFrame([0f, 1f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        ((FakeWaterfallSource)sstvSession.Waterfall).Emit(frame);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(frame, vm.LatestFrame);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_RefreshesImageOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService());

        Assert.Null(vm.Image);
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Image);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitCommand_InvokesSstvSessionServiceWhenImageLoaded()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), filePicker, new FakeLocalizationService());

        Assert.False(vm.TransmitCommand.CanExecute(null));

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));

        await vm.TransmitCommand.ExecuteAsync(null);

        Assert.Single(sstvSession.TransmitCalls);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[0].Mode);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_SelectingANewMode_ClearsAlreadyLoadedImage()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeFilePickerService(), new FakeLocalizationService());

        vm.SelectedMode = TestMode with { Id = "other" };

        Assert.False(vm.TransmitCommand.CanExecute(null));
        Assert.Null(vm.PreviewImage);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectingAStockImage_LoadsItAndAllowsTransmit()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var stockEntry = new StockImageEntry("s1", "stock.png", "/stock/stock.png");
        var stockLibrary = new FakeStockImageLibrary
        {
            EntriesToReturn = [stockEntry],
            FullImageToReturn = new ArrayImageSource(1, 1, [new Rgb24(4, 5, 6)]),
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), stockLibrary, new FakeFilePickerService(), new FakeLocalizationService());

        await vm.SelectStockImageCommand.ExecuteAsync(stockEntry);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Equal("stock.png", vm.SelectedFileName);

        await vm.TransmitCommand.ExecuteAsync(null);
        Assert.Single(sstvSession.TransmitCalls);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeChangeWithAnAlreadyLoadedSource_RetainsAndReloadsIt()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), filePicker, new FakeLocalizationService());

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));

        imageFileLoader.ResultToReturn = new ArrayImageSource(2, 2, [new Rgb24(9, 9, 9), new Rgb24(9, 9, 9), new Rgb24(9, 9, 9), new Rgb24(9, 9, 9)]);
        vm.SelectedMode = modeB;
        Dispatcher.UIThread.RunJobs();

        // Retained and successfully reloaded at the new mode's dimensions -- NOT nulled out the way
        // the pre-Phase-4 behavior did.
        Assert.Equal("a.png", vm.SelectedFileName);
        Assert.NotNull(vm.PreviewImage);
        Assert.True(vm.TransmitCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeChangeReload_DisablesTransmitForTheWholeInFlightWindow()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other" };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), filePicker, new FakeLocalizationService());

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));

        imageFileLoader.UseManualGating = true;
        vm.SelectedMode = modeB;
        Dispatcher.UIThread.RunJobs();

        // The reload hasn't resolved yet -- must stay disabled for the whole window, not just the
        // instant the mode change was triggered.
        Assert.False(vm.TransmitCommand.CanExecute(null));

        imageFileLoader.PendingLoads[0].SetResult(new ArrayImageSource(1, 1, [new Rgb24(7, 7, 7)]));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RapidModeChanges_AStaleReloadCompletingLateNeverOverwritesTheNewerOne()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "b" };
        var modeC = TestMode with { Id = "c" };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB, modeC] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), filePicker, new FakeLocalizationService());

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        imageFileLoader.UseManualGating = true;
        vm.SelectedMode = modeB; // reload #1 (for modeB) queued, not yet resolved
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = modeC; // cancels reload #1's token, queues reload #2 (for modeC)
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, imageFileLoader.PendingLoads.Count);

        var newerResult = new ArrayImageSource(1, 1, [new Rgb24(42, 42, 42)]);
        imageFileLoader.PendingLoads[1].SetResult(newerResult); // the CURRENT (modeC) reload finishes first
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Same(newerResult, ExtractLoadedImage(vm));

        // The stale (modeB) reload finally completes late -- must not clobber modeC's already-applied result.
        var staleResult = new ArrayImageSource(1, 1, [new Rgb24(1, 1, 1)]);
        imageFileLoader.PendingLoads[0].TrySetResult(staleResult);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Same(newerResult, ExtractLoadedImage(vm));
    }

    private static IImageSource? ExtractLoadedImage(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_loadedImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as IImageSource;

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_Constructed_LoadsEntriesFromTheHistoryStore()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };

        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService());
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("1", entry.Entry.Id);
        Assert.NotNull(entry.Thumbnail);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingAnEntry_LoadsAReadOnlyPreview_NeverTouchingTheLiveReceivedImageBuffer()
    {
        var historyEntry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [historyEntry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(9, 9, 9)]),
        };

        // RxHistoryPaneViewModel's constructor has no parameter that reaches IReceivedImageBuffer at
        // all (unlike RxImagePaneViewModel, which takes ISstvSessionService specifically for that
        // live binding) -- a live-buffer interaction is structurally impossible here, not just
        // unobserved, so there is nothing to fake/assert against for that half of the guarantee.
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService());
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.PreviewImage);
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.PreviewImage);
    }
}
