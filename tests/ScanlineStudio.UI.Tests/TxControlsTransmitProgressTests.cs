using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>spec/18-path-to-1.0.md Medium item: "No TX send-progress feedback during transmit."
/// Same <see cref="FakeSstvSessionService.BlockUntilCancelled"/>/<c>StartBlockingTransmitAsync</c>
/// scaffold as <see cref="TxControlsTelemetryAndCutoffTests"/> -- kept in its own file since it's a
/// distinct feature slice, not because the setup differs.</summary>
public sealed class TxControlsTransmitProgressTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static TxControlsPaneViewModel CreateViewModel(FakeSstvSessionService sstvSession, FakeLocalizationService? localization = null)
        => new(
            sstvSession,
            new FakeImageFileLoader { ResultToReturn = TestImage },
            new FakeStockImageLibrary(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService { PathToReturn = "/tmp/a.png" },
            localization ?? new FakeLocalizationService(),
            new FakeSettingsStore(),
            new FakeRadioSessionService(),
            new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance,
            NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

    private static async Task StartBlockingTransmitAsync(TxControlsPaneViewModel vm)
    {
        TxImageEditorPaneViewModel? capturedEditor = null;
        vm.EditorOpened += e => capturedEditor = e;

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(capturedEditor);
        capturedEditor!.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        _ = vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTransmitting);
    }

    private static async Task WaitUntilNotTransmittingAsync(TxControlsPaneViewModel vm)
    {
        for (var i = 0; i < 50 && vm.IsTransmitting; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task TransmitProgress_SetToZero_AsSoonAsTransmitStarts()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(sstvSession);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.TransmitProgress);

        await StartBlockingTransmitAsync(vm);

        Assert.Equal(0.0, vm.TransmitProgress);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);
    }

    [AvaloniaFact]
    public async Task TransmitProgressChanged_UpdatesFractionAndText_WhileTransmitting()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var localization = new FakeLocalizationService();
        var vm = CreateViewModel(sstvSession, localization);
        Dispatcher.UIThread.RunJobs();

        await StartBlockingTransmitAsync(vm);

        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.5, vm.TransmitProgress);
        // FakeLocalizationService.GetString returns the raw key, not a real formatted string --
        // asserting on LastKey/LastArgs (this project's established pattern, e.g.
        // PaneViewModelTests' LineProgressText coverage) is what actually proves the VM computed
        // the right percentage/remaining-time arguments, not the localization plumbing.
        _ = vm.TransmitProgressText;
        Assert.Equal("Panes.TxControls.TransmitProgressFormat", localization.LastKey);
        Assert.Equal(new object[] { 50, "0:30" }, localization.LastArgs);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);
    }

    [AvaloniaFact]
    public async Task TransmitProgress_ResetsToNull_OnceTransmitCompletes()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(sstvSession);
        Dispatcher.UIThread.RunJobs();

        await StartBlockingTransmitAsync(vm);
        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0.5, vm.TransmitProgress);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);

        Assert.Null(vm.TransmitProgress);
        Assert.Equal(string.Empty, vm.TransmitProgressText);
    }

    [AvaloniaFact]
    public async Task TransmitProgressText_DoesNotShowAPreviousTransmissionsStaleRemainingTime_AtTheStartOfTheNextOne()
    {
        // Code-review finding: TransmitProgress resets to 0 at TX start, but the separate
        // _transmitRemaining backing field (not its own ObservableProperty) wasn't reset alongside
        // it -- a second transmission's very first TransmitProgressText read (before its own first
        // real progress report arrives) would otherwise show the FIRST transmission's last known
        // remaining time instead of 0:00.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var localization = new FakeLocalizationService();
        var vm = CreateViewModel(sstvSession, localization);
        Dispatcher.UIThread.RunJobs();

        await StartBlockingTransmitAsync(vm);
        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180)));
        Dispatcher.UIThread.RunJobs();
        _ = vm.TransmitProgressText;
        Assert.Equal(new object[] { 50, "1:30" }, localization.LastArgs);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);

        await StartBlockingTransmitAsync(vm);
        _ = vm.TransmitProgressText;

        Assert.Equal(new object[] { 0, "0:00" }, localization.LastArgs);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);
    }

    [AvaloniaFact]
    public void TransmitProgressChanged_RaisedWhileNotTransmitting_IsIgnored()
    {
        // A late-arriving report from a PREVIOUS transmission (or any report firing before this
        // pane has ever transmitted) must not populate TransmitProgress -- the IsTransmitting guard
        // inside the posted handler exists specifically for this case.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = CreateViewModel(sstvSession);
        Dispatcher.UIThread.RunJobs();

        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.TransmitProgress);
    }
}
