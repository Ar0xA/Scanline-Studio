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
            NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(),
            new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

    /// <summary>2026-09-19: Apply no longer closes the editor, so a SECOND call reuses whatever
    /// editor is already open instead of trying to open a new one via SelectImageCommand, which is
    /// now correctly refused while real content is live.</summary>
    private static async Task StartBlockingTransmitAsync(TxControlsPaneViewModel vm)
    {
        var existingEditor = ExtractCurrentEditor(vm);
        TxImageEditorPaneViewModel capturedEditor;
        if (existingEditor is not null)
        {
            capturedEditor = existingEditor;
        }
        else
        {
            TxImageEditorPaneViewModel? opened = null;
            vm.EditorOpened += e => opened = e;
            await vm.SelectImageCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(opened);
            capturedEditor = opened!;
        }
        capturedEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        _ = vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTransmitting);
    }

    private static TxImageEditorPaneViewModel? ExtractCurrentEditor(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_currentEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as TxImageEditorPaneViewModel;

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
    public async Task TxClockText_ShowsIdleTextBeforeTransmitting_ThenElapsedWhileTransmitting_ThenIdleAgainAfter()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(sstvSession, localization);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.TxControls.Telemetry.TxClockIdle", vm.TxClockText);

        await StartBlockingTransmitAsync(vm);
        // Elapsed (30s) deliberately != the implied remaining (180-30=150s) so this test can't pass
        // by accident if TxClockText were wired to the wrong field.
        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(180)));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("0:30", vm.TxClockText);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);

        Assert.Equal("Panes.TxControls.Telemetry.TxClockIdle", vm.TxClockText);
    }

    [AvaloniaFact]
    public async Task TxClockText_DoesNotShowAPreviousTransmissionsStaleElapsedTime_AtTheStartOfTheNextOne()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(sstvSession);
        Dispatcher.UIThread.RunJobs();

        await StartBlockingTransmitAsync(vm);
        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180)));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("1:30", vm.TxClockText);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);

        await StartBlockingTransmitAsync(vm);

        Assert.Equal("0:00", vm.TxClockText);

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
