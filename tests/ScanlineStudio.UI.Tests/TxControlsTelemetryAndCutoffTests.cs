using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Piece 6 (spec/14-roadmap.md) -- live PWR/ALC/SWR telemetry, meter-visibility refresh,
/// and the SWR auto-cutoff/manual Stop TX mechanism on <see cref="TxControlsPaneViewModel"/>.</summary>
public sealed class TxControlsTelemetryAndCutoffTests
{
    private static readonly SstvModeDefinition TestMode = new(
        Id: "test", DisplayName: "Test", VisCode: 0, ImageWidth: 1, ImageHeight: 1,
        ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);

    private static readonly IImageSource TestImage = new ArrayImageSource(1, 1, new Rgb24[1]);

    private static TxControlsPaneViewModel CreateViewModel(FakeRadioSessionService radioSession, FakeSstvSessionService sstvSession)
        => new(
            sstvSession,
            new FakeImageFileLoader { ResultToReturn = TestImage },
            new FakeStockImageLibrary(),
            new FakeTransmitImagePreparer(),
            new FakeFilePickerService { PathToReturn = "/tmp/a.png" },
            new FakeLocalizationService(),
            new FakeSettingsStore(),
            radioSession,
            new MacroTextResolver(),
            NullLogger<TxControlsPaneViewModel>.Instance,
            NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore());

    [AvaloniaFact]
    public void MeterVisibility_RefreshesFromLatestCapabilities_NotJustAtConstruction()
    {
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.None };
        var sstvSession = new FakeSstvSessionService();
        var vm = CreateViewModel(radioSession, sstvSession);
        Dispatcher.UIThread.RunJobs();

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ShowSwrMeter);

        // Capability negotiation completing (a real connect finishing) after construction --
        // Capabilities flips to include SwrMeter; the NEXT poll must reflect that, proving this
        // isn't a one-time computed property evaluated back when Capabilities was still None.
        radioSession.Capabilities = RadioCapabilities.SwrMeter;
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowSwrMeter);
    }

    [AvaloniaFact]
    public async Task LiveMeters_OnlyPopulateWhileThisPaneAndTheRigBothAgreeItIsTransmitting()
    {
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.SwrMeter };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(radioSession, sstvSession);
        Dispatcher.UIThread.RunJobs();

        // Not transmitting yet -- a state carrying a SWR value must not populate the live readout.
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 1.5f));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.LiveSwrRatio);

        await StartBlockingTransmitAsync(vm);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 1.5f));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1.5f, vm.LiveSwrRatio);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);
        Assert.Null(vm.LiveSwrRatio);
    }

    [AvaloniaFact]
    public async Task TelemetryHistory_OnlyAppendsWhileActuallyTransmitting_AndCapsAtTheConfiguredLimit()
    {
        var radioSession = new FakeRadioSessionService { Capabilities = RadioCapabilities.SwrMeter };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(radioSession, sstvSession);
        Dispatcher.UIThread.RunJobs();

        // Not transmitting yet -- must not append.
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 1.5f));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.TelemetryHistory);

        await StartBlockingTransmitAsync(vm);

        const int capacity = 120;
        for (var i = 0; i < capacity + 10; i++)
        {
            radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: i));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(capacity, vm.TelemetryHistory.Count);
        // Oldest-evicted-first: the surviving samples are the most recent `capacity` of the
        // `capacity + 10` pushed above, i.e. SwrRatio 10..129, in order.
        Assert.Equal(10f, vm.TelemetryHistory.First().SwrRatio);
        Assert.Equal((float)(capacity + 9), vm.TelemetryHistory.Last().SwrRatio);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);

        // Stopping does not clear history -- only gates further appends.
        Assert.Equal(capacity, vm.TelemetryHistory.Count);
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 999f));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(capacity, vm.TelemetryHistory.Count);
    }

    [AvaloniaFact]
    public async Task SwrCutoff_TripsOnlyAfterTwoConsecutiveOverThresholdSamples_NotASingleGlitch()
    {
        var radioSession = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.SwrMeter,
            SafetySpec = new RadioSafetySpec(SwrCutoffEnabled: true, SwrCutoffThreshold: 3.0),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(radioSession, sstvSession);
        await AwaitConstructorLoadsAsync();

        await StartBlockingTransmitAsync(vm);

        // One glitchy over-threshold sample -- must not trip the cutoff by itself.
        radioSession.Push(HighSwrState());
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTransmitting);

        // A normal reading in between resets the consecutive-sample counter.
        radioSession.Push(LowSwrState());
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTransmitting);

        radioSession.Push(HighSwrState());
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsTransmitting); // still only 1 consecutive sample after the reset

        radioSession.Push(HighSwrState());
        Dispatcher.UIThread.RunJobs();

        await WaitUntilNotTransmittingAsync(vm);
        Assert.False(vm.IsTransmitting);
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task SwrCutoff_RequiresTheRigsOwnPttReadback_NotJustThisPanesIsTransmittingFlag()
    {
        // A rig with no PTT-readback capability always reports IsTransmitting=false -- the cutoff
        // must never trip in that case, since there's no reliable TX-state confirmation to trust an
        // SWR reading against (fail-closed, not fail-open, on a safety feature).
        var radioSession = new FakeRadioSessionService
        {
            Capabilities = RadioCapabilities.SwrMeter,
            SafetySpec = new RadioSafetySpec(SwrCutoffEnabled: true, SwrCutoffThreshold: 3.0),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(radioSession, sstvSession);
        await AwaitConstructorLoadsAsync();

        await StartBlockingTransmitAsync(vm);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 9.9f));
        Dispatcher.UIThread.RunJobs();
        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 9.9f));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsTransmitting);

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);
    }

    [AvaloniaFact]
    public async Task StopTransmitCommand_CancelsWithoutShowingAnErrorMessage()
    {
        var radioSession = new FakeRadioSessionService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], BlockUntilCancelled = true };
        var vm = CreateViewModel(radioSession, sstvSession);
        Dispatcher.UIThread.RunJobs();

        await StartBlockingTransmitAsync(vm);
        Assert.True(vm.StopTransmitCommand.CanExecute(null));

        vm.StopTransmitCommand.Execute(null);
        await WaitUntilNotTransmittingAsync(vm);

        Assert.False(vm.IsTransmitting);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public void Constructor_LoadsPersistedSwrCutoffSettings()
    {
        var radioSession = new FakeRadioSessionService { SafetySpec = new RadioSafetySpec(true, 2.5) };
        var vm = CreateViewModel(radioSession, new FakeSstvSessionService());
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SwrCutoffEnabled);
        Assert.Equal(2.5, vm.SwrCutoffThreshold);
    }

    [AvaloniaFact]
    public void TogglingSwrCutoffEnabled_Persists()
    {
        var radioSession = new FakeRadioSessionService();
        var vm = CreateViewModel(radioSession, new FakeSstvSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.SwrCutoffEnabled = true;
        Dispatcher.UIThread.RunJobs();

        Assert.True(radioSession.SafetySpec.SwrCutoffEnabled);
    }

    private static RadioState HighSwrState() =>
        new(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 5.0f);

    private static RadioState LowSwrState() =>
        new(14_230_000, RadioMode.Usb, IsTransmitting: true, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow, SwrRatio: 1.2f);

    /// <summary>Constructor kicks off an async <c>LoadSafetySettingsAsync</c> -- give it a tick to
    /// complete and post its Dispatcher update before a test relies on SwrCutoffEnabled/Threshold
    /// already reflecting a non-default persisted value.</summary>
    private static async Task AwaitConstructorLoadsAsync()
    {
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Drives the pane's real pick-image -> open editor -> Apply -> Transmit flow (same
    /// shape <c>PaneViewModelTests</c> uses elsewhere in this project), so <c>TransmitCommand</c>
    /// actually has a loaded image + selected mode to work with. Leaves the transmit in flight
    /// (<see cref="FakeSstvSessionService.BlockUntilCancelled"/> on the session passed to
    /// <see cref="CreateViewModel"/>) so a test can push RadioState updates against it.</summary>
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
}
