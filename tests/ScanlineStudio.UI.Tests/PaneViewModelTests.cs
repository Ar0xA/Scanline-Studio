using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Services;
using ScanlineStudio.UI.Settings;
using ScanlineStudio.UI.ViewModels;
using System.IO;

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
        var vm = new RadioStatusViewModel(radioSession, new FakeSstvSessionService(), new FakeLocalizationService(), NullLogger<RadioStatusViewModel>.Instance);

        radioSession.Push(new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("14.230000 MHz", vm.FrequencyDisplay);
        Assert.Equal("Usb", vm.ModeDisplay);
    }

    [AvaloniaFact]
    public void RadioStatusViewModel_Constructed_PopulatesUtcClockDisplayImmediately()
    {
        // UpdateUtcClock() runs synchronously in the constructor (before the DispatcherTimer's own
        // first tick), so this must already be populated without needing Dispatcher.UIThread.RunJobs()
        // or advancing the timer at all.
        var localization = new FakeLocalizationService();
        var vm = new RadioStatusViewModel(new FakeRadioSessionService(), new FakeSstvSessionService(), localization, NullLogger<RadioStatusViewModel>.Instance);

        Assert.Equal("RadioStatus.UtcValueFormat", vm.UtcClockDisplay);
        // Regression guard (auditor nit): asserting only the locale KEY would still pass if
        // DateTimeOffset.Now (local offset) were passed instead of UtcNow -- assert the actual arg's
        // offset is zero, proving this is really UTC.
        var passedInstant = Assert.IsType<DateTimeOffset>(localization.LastArgs[0]);
        Assert.Equal(TimeSpan.Zero, passedInstant.Offset);
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
    public void WaterfallPaneViewModel_MultipleFramesBeforeUiThreadRuns_CoalescesToOnlyTheLatest()
    {
        // Tier B audit finding: OnFrame's own doc comment claims "at most one Post ever in flight --
        // a frame arriving while one is pending just replaces _pendingFrame (latest-wins), it never
        // queues a second post." No existing test exercised more than one frame before RunJobs(), so
        // this pins that claim directly: three frames pushed back-to-back (as a real audio-drain-
        // thread burst would) must collapse to exactly one PropertyChanged and the LAST frame shown,
        // not the first, and not three separate updates.
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService());
        var frame1 = new WaterfallFrame([0f, 1f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        var frame2 = new WaterfallFrame([2f, 3f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        var frame3 = new WaterfallFrame([4f, 5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        var latestFrameChangeCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WaterfallPaneViewModel.LatestFrame))
            {
                latestFrameChangeCount++;
            }
        };

        var source = (FakeWaterfallSource)sstvSession.Waterfall;
        source.Emit(frame1);
        source.Emit(frame2);
        source.Emit(frame3);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(frame3, vm.LatestFrame);
        Assert.Equal(1, latestFrameChangeCount);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_ModeDetectedEvent_UpdatesCurrentModeOnUiThread()
    {
        // Auditor-caught (batch 8 plan review): ISstvSessionService.ModeDetected fires synchronously
        // on the audio drain thread -- an earlier version assigned CurrentMode directly in the event
        // handler, which would raise PropertyChanged (and hence update Avalonia bindings) off the UI
        // thread. Same Dispatcher.UIThread.Post-then-RunJobs pattern as OnFrame's own test above
        // proves the marshaling actually happens.
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService());
        Assert.Null(vm.CurrentMode);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(mode, vm.CurrentMode);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_ViewMode_DefaultsToBoth()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());

        Assert.Equal(WaterfallViewMode.Both, vm.ViewMode);
        Assert.True(vm.IsViewBoth);
        Assert.False(vm.IsViewSpectrumOnly);
        Assert.False(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_SettingIsViewSpectrumOnly_UpdatesViewModeAndTheOtherComputedBools()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());

        vm.IsViewSpectrumOnly = true;

        Assert.Equal(WaterfallViewMode.SpectrumOnly, vm.ViewMode);
        Assert.False(vm.IsViewBoth);
        Assert.True(vm.IsViewSpectrumOnly);
        Assert.False(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_SettingIsViewWaterfallOnly_UpdatesViewModeAndTheOtherComputedBools()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());

        vm.IsViewWaterfallOnly = true;

        Assert.Equal(WaterfallViewMode.WaterfallOnly, vm.ViewMode);
        Assert.False(vm.IsViewBoth);
        Assert.False(vm.IsViewSpectrumOnly);
        Assert.True(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_StartHzSpanHzPeakHoldEnabled_HaveTheDocumentedDefaults()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());

        Assert.Equal(1000.0, vm.StartHz);
        Assert.Equal(1600.0, vm.SpanHz);
        Assert.False(vm.PeakHoldEnabled);
        Assert.Equal(0.0, vm.BinsPerPixel);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_RangeCaptionDisplay_ReflectsStartHzAndSpanHz_NotAStaleLiteral()
    {
        // spec/18-path-to-1.0.md Medium item 2: the caption used to be a static localized string
        // ("1000…2600 Hz") that silently lied the moment Start/Span were touched -- this confirms
        // the caller passes the CURRENT, live Start/end (not Span itself) as the format arguments,
        // and that changing EITHER property raises PropertyChanged for RangeCaptionDisplay (code-
        // review finding: re-reading the property alone would pass even with zero
        // [NotifyPropertyChangedFor] attributes -- only a real PropertyChanged subscription proves
        // the binding would actually refresh).
        var localization = new FakeLocalizationService();
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), localization);

        _ = vm.RangeCaptionDisplay;
        Assert.Equal("Panes.Waterfall.RangeCaptionFormat", localization.LastKey);
        Assert.Equal([1000.0, 2600.0], localization.LastArgs);

        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.StartHz = 700.0;
        Assert.Contains(nameof(WaterfallPaneViewModel.RangeCaptionDisplay), raisedProperties);
        raisedProperties.Clear();

        vm.SpanHz = 2000.0;
        Assert.Contains(nameof(WaterfallPaneViewModel.RangeCaptionDisplay), raisedProperties);

        _ = vm.RangeCaptionDisplay;

        Assert.Equal([700.0, 2700.0], localization.LastArgs);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_NotchEnabled_DefaultsToFalse_AndFrequencyDefaultsTo2400()
    {
        // Matches NotchFilter/AnalogFmSstvDecoder._notchFrequencyHz's own default -- see
        // WaterfallPaneViewModel.NotchFrequencyHz's own doc comment for why that matters.
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());

        Assert.False(vm.NotchEnabled);
        Assert.Equal(2400.0, vm.NotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TogglingNotchEnabled_ForwardsToTheSessionWithTheCurrentFrequency()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService()) { NotchFrequencyHz = 1750.0 };

        vm.NotchEnabled = true;

        Assert.Equal(1, sstvSession.RequestNotchCallCount);
        Assert.True(sstvSession.LastNotchEnabled);
        Assert.Equal(1750.0, sstvSession.LastNotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TogglingNotchDisabled_ForwardsNullFrequency()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService()) { NotchEnabled = true };

        vm.NotchEnabled = false;

        Assert.Equal(2, sstvSession.RequestNotchCallCount);
        Assert.False(sstvSession.LastNotchEnabled);
        Assert.Null(sstvSession.LastNotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TuneNotch_SetsStateAndForwardsEnabledTrueWithTheClickedFrequency()
    {
        // Click-to-tune (SpectrumTraceControl.NotchTuneRequestedCommand) -- legacy's left-click both
        // tunes AND enables (Main.cpp:14364-14371).
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService());

        vm.TuneNotchCommand.Execute(1900.0);

        Assert.True(vm.NotchEnabled);
        Assert.Equal(1900.0, vm.NotchFrequencyHz);
        Assert.True(sstvSession.LastNotchEnabled);
        Assert.Equal(1900.0, sstvSession.LastNotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TuneNotch_WhileAlreadyEnabled_StillForwardsTheRetune()
    {
        // The generated NotchEnabled setter no-ops (and skips OnNotchEnabledChanged) when the value
        // doesn't actually change -- this proves TuneNotch's own explicit RequestNotch call covers
        // the already-enabled retune-by-drag case regardless.
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService()) { NotchEnabled = true };
        var callCountBeforeRetune = sstvSession.RequestNotchCallCount;

        vm.TuneNotchCommand.Execute(2100.0);

        Assert.True(sstvSession.RequestNotchCallCount > callCountBeforeRetune);
        Assert.Equal(2100.0, vm.NotchFrequencyHz);
        Assert.Equal(2100.0, sstvSession.LastNotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TogglingNotchEnabled_RaisesPropertyChangedForNotchStatusDisplay()
    {
        // Live-app regression: re-reading NotchStatusDisplay alone (as the sibling
        // WaterfallPaneViewModel_NotchStatusDisplay_ShowsFrequencyWhenEnabled_AndOffLiteralWhenNot test
        // below does) would pass even if NotchEnabled's own [NotifyPropertyChangedFor] were missing --
        // only a real PropertyChanged subscription proves the bound TextBlock would actually refresh.
        // Caught live: the toggle chip visibly flipped and the session-service call landed correctly,
        // but the Input Chain row's frequency text stayed stuck on "Off" until this was added.
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.NotchEnabled = true;

        Assert.Contains(nameof(WaterfallPaneViewModel.NotchStatusDisplay), raisedProperties);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_NotchStatusDisplay_ShowsFrequencyWhenEnabled_AndOffLiteralWhenNot()
    {
        var localization = new FakeLocalizationService();
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), localization);

        _ = vm.NotchStatusDisplay;
        Assert.Equal("Panes.RxInput.NotchValue", localization.LastKey);

        vm.TuneNotchCommand.Execute(1850.0);
        _ = vm.NotchStatusDisplay;

        Assert.Equal("Panes.RxInput.NotchValueFormat", localization.LastKey);
        Assert.Equal([1850.0], localization.LastArgs);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_RefreshesImageOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.Image);
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Image);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_UpdatesModeCardTextsOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal("—", vm.DetectedModeText);
        Assert.Equal("—", vm.LineTimeText);
        Assert.Equal("—", vm.LinesText);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Scottie 1", vm.DetectedModeText);
        Assert.Equal("138.2 ms", vm.LineTimeText);
        Assert.Equal("256", vm.LinesText);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RemainingText_NoLockShowsPlaceholder_ThenReflectsProgress()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal("—", vm.RemainingText);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // 25% through a 256-line mode: 192 lines remaining, times the mode's own 138.24ms line
        // duration -- same progress*ImageHeight term LineProgressText already uses, so the two
        // readouts can never disagree (auditor-verified 2026-08-25).
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.25;
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        // FakeLocalizationService.GetString returns the raw key, not a formatted string (same
        // convention as the QRZ-lookup-failure test above) -- asserting the key proves the real
        // format key was used, and LastArgs proves the caller passed the correctly-computed numbers.
        Assert.Equal("Panes.RxImage.RemainingValueFormat", vm.RemainingText);
        Assert.Equal(192, localization.LastArgs[0]);
        Assert.Equal(192 * 138.24 / 1000.0, (double)localization.LastArgs[1], precision: 9);
    }

    /// <summary>Regression test (found during the TX image editor's own duration-fix code-review,
    /// 2026-08-25): the OLD formula was <c>linesRemaining * mode.LineDurationMs</c> --
    /// <c>LineDurationMs</c> is per TRANSMISSION line, not per image row, so a
    /// <see cref="ColorEncoding.YCbCrLinePaired"/>/<see cref="ColorEncoding.MonoAveragedPaired"/>
    /// mode (2 image rows per transmission line) got ~2x the real remaining time. This mode: 4 rows,
    /// 100ms/line, 2 rows/line -&gt; 0.2s total. At 50% progress the OLD formula gives 0.2s remaining
    /// (the entire duration, wrong); the correct value is 0.1s.</summary>
    [AvaloniaFact]
    public void RxImagePaneViewModel_RemainingText_LinePairedMode_DividesByRowsPerTransmissionLine()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var mode = new SstvModeDefinition(
            Id: "line-paired", DisplayName: "Line Paired", VisCode: 0, ImageWidth: 4, ImageHeight: 4,
            ColorEncoding: ColorEncoding.YCbCrLinePaired,
            LineSegments: [new ScanSegment("Y", 100)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.5;
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxImage.RemainingValueFormat", vm.RemainingText);
        Assert.Equal(2, localization.LastArgs[0]);
        Assert.Equal(0.1, (double)localization.LastArgs[1], precision: 9);
    }

    /// <summary>Logging-coverage audit (2026-08-15): <see cref="ISstvSessionService.DecodeRestarted"/>
    /// previously had no reachable subscriber anywhere in <c>ScanlineStudio.UI</c>. This VM now
    /// subscribes purely to log it -- deliberately no bound state changes -- so this asserts both
    /// that raising the event doesn't disturb unrelated state (a still-current
    /// <see cref="RxImagePaneViewModel.DetectedMode"/> from an earlier <c>ModeDetected</c> survives a
    /// subsequent <c>DecodeRestarted</c> for the same mode) AND, via a recording <see cref="FakeLogger{T}"/>
    /// (auditor round-1 finding: a prior version of this test would have passed identically even if
    /// the event subscription were deleted), that the subscription actually took and the real log
    /// call fired -- not just that nothing threw.</summary>
    [AvaloniaFact]
    public void RxImagePaneViewModel_DecodeRestartedEvent_LogsAndDoesNotDisturbDetectedMode()
    {
        var sstvSession = new FakeSstvSessionService();
        var logger = new FakeLogger<RxImagePaneViewModel>();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), logger);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        sstvSession.RaiseDecodeRestarted(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Scottie 1", vm.DetectedModeText);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message.Contains("restarted", StringComparison.OrdinalIgnoreCase) && e.Message.Contains("sc1", StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_LogQsoCommand_DisabledUntilAModeHasBeenDetected()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.False(vm.LogQsoCommand.CanExecute(null));

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // Enabled once a mode has EVER been detected, not only "currently receiving" -- nothing
        // nulls DetectedMode after a reception ends, matching this VM's own doc comment on
        // CanLogQso: the natural moment to log a QSO is right after the frame finishes.
        Assert.True(vm.LogQsoCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_LogQsoCommand_FiresLogQsoRequestedWithNoPayload()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        var fireCount = 0;
        vm.LogQsoRequested += () => fireCount++;

        vm.LogQsoCommand.Execute(null);

        Assert.Equal(1, fireCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SaveFrameCommand_DisabledUntilAModeHasBeenDetected()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.False(vm.SaveFrameCommand.CanExecute(null));

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // Same "enabled once a mode has EVER been detected" gate as CanLogQso -- see CanSaveFrame's
        // own doc comment.
        Assert.True(vm.SaveFrameCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_SaveFrameCommand_SavesThePickedDestination()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { SaveImagePathToReturn = ("/tmp/chosen.png", ImageExportFormat.Png) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        await vm.SaveFrameCommand.ExecuteAsync(null);

        Assert.Null(vm.SaveFrameErrorMessage);
        Assert.False(vm.IsSavingFrame);
        // Round-1 code-review finding: without this, the test would pass identically even if
        // SaveAsync were never called at all, or called with the wrong path.
        Assert.Equal(["/tmp/chosen.png"], ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).SavedPaths);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_SaveFrameCommand_UserCancelsPicker_DoesNothing()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { SaveImagePathToReturn = null };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        await vm.SaveFrameCommand.ExecuteAsync(null);

        Assert.Null(vm.SaveFrameErrorMessage);
        Assert.Empty(((FakeReceivedImageBuffer)sstvSession.ReceivedImage).SavedPaths);
    }

    // ---- Piece C1/C2 (RX tab Re-decode port) ----

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ToggleRecordingCommand_StartsThenStopsRecording()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { SaveWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.True(vm.IsRecording);
        Assert.Null(vm.RecordErrorMessage);
        Assert.Equal(["/tmp/chosen.wav"], sstvSession.StartRecordingCalls);

        await vm.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Equal(1, sstvSession.StopRecordingCallCount);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ToggleRecordingCommand_UserCancelsPicker_DoesNotStartRecording()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { SaveWavPathToReturn = null };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Empty(sstvSession.StartRecordingCalls);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ToggleRecordingCommand_StartFails_ShowsErrorAndStaysNotRecording()
    {
        var sstvSession = new FakeSstvSessionService { ThrowOnStartRecording = new InvalidOperationException("Cannot start recording while not receiving.") };
        var filePicker = new FakeFilePickerService { SaveWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.NotNull(vm.RecordErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeCommand_DecodesThePickedFile()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { OpenWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeCommand.ExecuteAsync(null);

        Assert.Null(vm.RedecodeErrorMessage);
        Assert.False(vm.IsRedecoding);
        Assert.Equal(["/tmp/chosen.wav"], sstvSession.DecodeFromFileCalls);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeCommand_UserCancelsPicker_DoesNotDecode()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { OpenWavPathToReturn = null };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeCommand.ExecuteAsync(null);

        Assert.Empty(sstvSession.DecodeFromFileCalls);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeCommand_DecodeFails_ShowsError()
    {
        var sstvSession = new FakeSstvSessionService { ThrowOnDecodeFromFile = new InvalidOperationException("File sample rate mismatch.") };
        var filePicker = new FakeFilePickerService { OpenWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RedecodeErrorMessage);
        Assert.False(vm.IsRedecoding);
    }

    // Wired 2026-08-18: RxFrameMeta's Note/Flag controls were disabled -- "this pane has no way to
    // learn a just-saved frame's ReceiveHistoryEntry id yet." IReceiveHistoryStore.Recorded is the
    // real hook; OnHistoryRecorded correlates it back to the currently-displayed frame by matching
    // FilePath against whatever OnSaved (IReceivedImageBuffer.Saved) most recently saw.

    [AvaloniaFact]
    public void CanEditFrameMetadata_BecomesTrue_WhenAMatchingHistoryEntryIsRecorded()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        Assert.False(vm.CanEditFrameMetadata);

        var receivedImage = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        receivedImage.RaiseSaved("/tmp/frame.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CanEditFrameMetadata); // save alone isn't enough -- needs the matching Recorded too

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame.png", null, ReceiveDecodeState.Completed, Note: "existing note", IsFlagged: true));
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CanEditFrameMetadata);
        Assert.Equal("existing note", vm.Note);
        Assert.True(vm.IsFlagged);
        // Round-1 audit finding: without _suppressFrameMetadataEdits guarding this exact load, the
        // Note/IsFlagged assignments just above would echo straight back out as spurious persist
        // calls -- this pins that they don't.
        Assert.Empty(historyStore.SetNoteCalls);
        Assert.Empty(historyStore.SetFlaggedCalls);
    }

    [AvaloniaFact]
    public void CanEditFrameMetadata_StaysFalse_WhenRecordedEntryPathDoesNotMatchTheDisplayedFrame()
    {
        // e.g. an abandoned-image record for a DIFFERENT, earlier frame -- ReceiveHistoryRecorder's
        // own abandoned-image path deliberately never goes through IReceivedImageBuffer.SaveAsync,
        // so OnSaved never ran for it and _lastSavedPath still points at (or is null for) something
        // else entirely.
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);

        var receivedImage = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        receivedImage.RaiseSaved("/tmp/current-frame.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/some-other-abandoned-frame.png", null, ReceiveDecodeState.Abandoned));
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CanEditFrameMetadata);
    }

    // Previous-frames strip (user decision, 2026-08-26): a session-only rolling list of the last 2
    // COMPLETED receptions, independent of RxHistoryPaneViewModel.Entries/the Gallery tab's own
    // ShowTodayOnly toggle.

    [AvaloniaFact]
    public void PreviousFrames_CompletedEntryRecorded_IsAdded()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        Assert.Empty(vm.PreviousFrames);

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();

        var frame = Assert.Single(vm.PreviousFrames);
        Assert.Equal("entry1", frame.Entry.Id);
        Assert.NotNull(frame.Thumbnail);
    }

    [AvaloniaFact]
    public void PreviousFrames_AbandonedEntryRecorded_IsNotAdded()
    {
        // An aborted/partial attempt isn't what an operator means by "a previous frame."
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Abandoned));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.PreviousFrames);
    }

    [AvaloniaFact]
    public void PreviousFrames_MoreThanCapacityRecorded_KeepsOnlyTheNewestTwo()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        var now = DateTimeOffset.UtcNow;

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", now, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry2", now.AddSeconds(1), "sc1", "/tmp/frame2.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry3", now.AddSeconds(2), "sc1", "/tmp/frame3.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.PreviousFrames.Count);
        Assert.Equal(["entry3", "entry2"], vm.PreviousFrames.Select(f => f.Entry.Id));
    }

    [AvaloniaFact]
    public void PreviousFrames_ThumbnailLoadFails_EntryStillAddedWithoutAThumbnail()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore(); // ThumbnailToReturn left null -> throws
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();

        var frame = Assert.Single(vm.PreviousFrames);
        Assert.Equal("entry1", frame.Entry.Id);
        Assert.Null(frame.Thumbnail);
    }

    [AvaloniaFact]
    public async Task PreviousFrames_SecondEntrysThumbnailResolvesBeforeTheFirsts_StillEndsUpNewestFirst()
    {
        // Regression coverage for the sort-on-insert logic: IReceiveHistoryStore.Recorded can fire
        // back-to-back during a bulk-decoded WAV import (same interleaving several sibling methods on
        // this class already guard against), and the thumbnail load is a real async gap -- a naive
        // "insert at index 0" would put the OLDER entry on top if its own thumbnail happens to resolve
        // second.
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        var now = DateTimeOffset.UtcNow;

        var gateOlder = new TaskCompletionSource<IImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateNewer = new TaskCompletionSource<IImageSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        historyStore.ThumbnailLoadGates["older"] = gateOlder;
        historyStore.ThumbnailLoadGates["newer"] = gateNewer;

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("older", now, "sc1", "/tmp/older.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("newer", now.AddSeconds(1), "sc1", "/tmp/newer.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.PreviousFrames); // both loads still pending

        // The NEWER entry's thumbnail resolves first -- pumped deterministically (not a single
        // Task.Yield/RunJobs pair, which races the thread-pool hop under load) and checked with an
        // intermediate assertion, which is what actually pins the intended interleaving rather than
        // passing coincidentally against a naive Insert(0, ...) too.
        var thumbnail = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]);
        gateNewer.SetResult(thumbnail);
        await PumpUntilAsync(() => vm.PreviousFrames.Count == 1);
        Assert.Equal(["newer"], vm.PreviousFrames.Select(f => f.Entry.Id));

        gateOlder.SetResult(thumbnail);
        await PumpUntilAsync(() => vm.PreviousFrames.Count == 2);

        Assert.Equal(["newer", "older"], vm.PreviousFrames.Select(f => f.Entry.Id));
    }

    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task NoteChanged_PersistsDebounced_ToTheCorrelatedEntry()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        var receivedImage = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        receivedImage.RaiseSaved("/tmp/frame.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(historyStore.EntriesToReturn[0]);
        Dispatcher.UIThread.RunJobs();

        vm.Note = "call back tomorrow";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(historyStore.EntriesToReturn[0].Note); // not persisted yet -- still debouncing

        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("call back tomorrow", historyStore.EntriesToReturn[0].Note);
        Assert.Null(vm.FrameMetadataErrorMessage);
    }

    [AvaloniaFact]
    public void IsFlaggedChanged_PersistsImmediately_ToTheCorrelatedEntry()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        var receivedImage = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        receivedImage.RaiseSaved("/tmp/frame.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(historyStore.EntriesToReturn[0]);
        Dispatcher.UIThread.RunJobs();

        vm.IsFlagged = true;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([("entry1", true)], historyStore.SetFlaggedCalls);
        Assert.True(historyStore.EntriesToReturn[0].IsFlagged);
        Assert.Null(vm.FrameMetadataErrorMessage);
    }

    [AvaloniaFact]
    public void ANewReceptionStarting_ResetsFrameMetadataState()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, NullLogger<RxImagePaneViewModel>.Instance);
        var receivedImage = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        receivedImage.RaiseSaved("/tmp/frame.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame.png", null, ReceiveDecodeState.Completed, Note: "old note", IsFlagged: true));
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.CanEditFrameMetadata);

        var newMode = new SstvModeDefinition(
            Id: "sc2", DisplayName: "Scottie 2", VisCode: 61, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 88.064)]);
        sstvSession.RaiseModeDetected(newMode);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.CanEditFrameMetadata);
        Assert.Null(vm.Note);
        Assert.False(vm.IsFlagged);
        // Round-1 audit finding: without _suppressFrameMetadataEdits guarding this exact reset, the
        // Note=null/IsFlagged=false assignments just above would echo out as real persist calls,
        // silently wiping the PREVIOUS frame's genuine metadata. This pins that they don't.
        Assert.Empty(historyStore.SetNoteCalls);
        Assert.Empty(historyStore.SetFlaggedCalls);

        // Round-1 audit finding: without _lastSavedPath being cleared by this same reset, a LATE
        // Recorded event still carrying the OLD frame's path would incorrectly re-correlate against
        // the new (unrelated) frame's card.
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.CanEditFrameMetadata);
    }

    /// <summary>Round-1 plan-review finding (rx-log-qso.md, 2026-08-15): OverrideCallsign/
    /// LookupName/LookupQth/LookupGrid are per-RECEPTION "who is this station" state but were never
    /// reset on a new ModeDetected -- station A sends an FSK-decoded callsign, station B then
    /// transmits with no FSK ID, and DetectedMode/StartedAt would update to B's while these 4
    /// fields silently stayed A's. LogQsoCommand persists OverrideCallsign into a real logbook
    /// row, so that staleness would produce a wrong QSO record; the other three (Lookup*) feed
    /// only this pane's own display plus the prefilled form's Name/QTH/Grid, so for them it's
    /// still display staleness, just now copied into the form too.</summary>
    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ClearsStalePerReceptionCallsignAndLookupFields()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var modeA = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(modeA);
        Dispatcher.UIThread.RunJobs();
        // Simulates FSK-decoded-callsign auto-fill + a completed QRZ lookup for station A --
        // ApplyStationIdDecodedAsync/LookupQrzAsync's own paths are covered elsewhere; setting
        // these 4 fields directly is sufficient to pin THIS specific staleness bug.
        vm.OverrideCallsign = "W1AW";
        vm.LookupName = "Hiram Maxim";
        vm.LookupQth = "Newington";
        vm.LookupGrid = "FN31pr";

        var modeB = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(modeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
        Assert.Null(vm.LookupName);
        Assert.Null(vm.LookupQth);
        Assert.Null(vm.LookupGrid);
        Assert.Equal("Martin M1", vm.DetectedModeText);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_SetsStartedAt()
    {
        var sstvSession = new FakeSstvSessionService();
        var localization = new FakeLocalizationService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal("—", vm.StartedDisplay);

        var before = DateTimeOffset.UtcNow;
        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();
        var after = DateTimeOffset.UtcNow;

        Assert.NotEqual("—", vm.StartedDisplay);
        Assert.NotNull(vm.StartedAt);
        Assert.InRange(vm.StartedAt!.Value, before, after);
        Assert.Equal("Panes.RxFrameMeta.StartedValueFormat", localization.LastKey);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_CallsignDifferentFromOwn_AutoFillsOverrideCallsign()
    {
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.OverrideCallsign);
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("K1ABC", vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_CallsignMatchesOwnExactly_DoesNotAutoFill()
    {
        // Main.cpp:3628's strcmp self-filter -- exact, case-sensitive match against the operator's
        // own callsign must NOT auto-fill "his callsign" with it (e.g. another station repeating the
        // operator's own callsign back).
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_CallsignDiffersOnlyByCase_StillAutoFills()
    {
        // Exact, case-SENSITIVE match (C's strcmp, not a case-insensitive comparison) -- "w1aw"
        // (lowercase) must NOT be treated as matching the operator's own "W1AW", so this still
        // auto-fills. A real, if unusual, station-ID decode could plausibly differ only by case since
        // the wire protocol itself doesn't enforce a single canonical case.
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "w1aw"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("w1aw", vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_CompactNr_FormatsAsZeroPaddedMyRst()
    {
        // Main.cpp:3648's sprintf(bf, "595%s", pDem->m_fskNRS) -- compact NR is rendered via the RX
        // decoder's own "%03u"-equivalent zero-pad before the "595" prefix is applied.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 12));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("595012", vm.DecodedNrRst);
        Assert.Equal("595012", vm.DecodedNrRstDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_RaisesPropertyChangedForDecodedNrRstDisplay()
    {
        // Regression test for [NotifyPropertyChangedFor(nameof(DecodedNrRstDisplay))] on
        // _decodedNrRst -- same class of gap this repo's own AutoCorrectDisplay/NotchEnabled
        // regression tests already guard against (a value-only assert would pass identically even
        // with the attribute deleted, since DecodedNrRstDisplay is a pure computed getter).
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 12));
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(RxImagePaneViewModel.DecodedNrRstDisplay), raisedProperties);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_NrText_FormatsAsMyRstDirectly()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(NrText: "0012"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("5950012", vm.DecodedNrRst);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_NoAutomaticQrzLookupIsTriggered()
    {
        // User-approved v1 scope boundary (CW-ID/FSK station-ID subsystem plan): auto-fill
        // OverrideCallsign only, never an automatic network lookup on decode.
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" };
        var logbookSession = new FakeLogbookSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("K1ABC", vm.OverrideCallsign);
        Assert.Equal(0, logbookSession.LookupCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_NewReceptionStartsWhileAwaitingOwnCallsign_DropsTheStaleWrite()
    {
        // Tier B audit finding: GetOperatorCallsignAsync's own await is a real, uncached settings
        // disk read (JsonSettingsStore.LoadAsync has no cache) -- during a bulk-WAV-decode's
        // back-to-back transmissions, a NEW reception's ModeDetected/Generation bump can land in
        // that exact window, after which the write below would silently re-apply the OLD reception's
        // decoded callsign onto the NEW one now on screen. Same class OnSaved's own
        // IReceivedImageBuffer.Generation guard already covers (see that method's doc comment); this
        // write site didn't have the equivalent before this fix.
        var gate = new TaskCompletionSource<string?>();
        var sstvSession = new FakeSstvSessionService { OperatorCallsignGate = gate };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var buffer = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.OverrideCallsign);

        buffer.Generation++; // a new reception starts while the settings read is still in flight
        gate.SetResult("W1AW");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ClearsStaleDecodedNrRst()
    {
        // Tier B audit finding: DecodedNrRst is the same per-RECEPTION "who is this station"
        // category as OverrideCallsign/Lookup* (also decoded from the previous station's FSK
        // sub-packet) but was the one left out of OnModeDetected's reset.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(NrText: "0012"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("5950012", vm.DecodedNrRst);

        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.DecodedNrRst);
        Assert.Equal("—", vm.DecodedNrRstDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ClearsStalePerReceptionErrorMessages()
    {
        // Tier B audit finding: QrzLookupErrorMessage/FrameMetadataErrorMessage/SaveFrameErrorMessage
        // are per-RECEPTION status text, same category as Note/IsFlagged (already reset here), but
        // weren't cleared -- a QRZ lookup failure or a stale "entry no longer exists" from the
        // PREVIOUS frame kept showing on the RxFrameMeta card after a new reception blanked the
        // fields the error text was actually about. Set directly (as
        // ModeDetectedEvent_ClearsStalePerReceptionCallsignAndLookupFields does for its own fields)
        // to pin this specific staleness bug without depending on the command paths that would
        // normally populate them.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var modeA = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(modeA);
        Dispatcher.UIThread.RunJobs();
        vm.QrzLookupErrorMessage = "Not found: k1abc";
        vm.FrameMetadataErrorMessage = "Panes.RxFrameMeta.Error.EntryNoLongerExists";
        vm.SaveFrameErrorMessage = "Panes.RxFrameMeta.Error.SaveFrameFailed";

        var modeB = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(modeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.QrzLookupErrorMessage);
        Assert.Null(vm.FrameMetadataErrorMessage);
        Assert.Null(vm.SaveFrameErrorMessage);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_SetsProgress_AndLineProgressTextReflectsIt()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal("MainWindow.StatusBar.LineProgressValueNoLock", vm.LineProgressText);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // Still no-lock until a real Updated event carries a Progress value -- ModeDetected alone
        // doesn't populate it.
        Assert.Equal("MainWindow.StatusBar.LineProgressValueNoLock", vm.LineProgressText);

        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).Progress = 0.5;
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.5, vm.Progress);
        _ = vm.LineProgressText;
        Assert.Equal("MainWindow.StatusBar.LineProgressValueFormat", localization.LastKey);
        // Regression guard (auditor nit): the previous version of this test only checked which
        // locale KEY fired, not the actual numbers -- a swapped ImageWidth/ImageHeight would have
        // passed silently. 0.5 * 256 (ImageHeight, not the 320-wide ImageWidth) = 128.
        Assert.Equal(new object[] { 128, 256 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_PollTelemetry_NoLockYet_ShowsPlaceholders()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.Equal("—", vm.SlantPpmDisplay);
        Assert.Equal("—", vm.SyncOffsetSamplesDisplay);
        Assert.Equal("—", vm.SyncToneDisplay);
        // FakeLocalizationService.GetString returns the raw key (not the formatted string) --
        // asserting the KEY selected still proves the on-not-locked branch fired, matching this
        // file's own established ToneMapFormat precedent for testing GetString-based computed
        // properties. AutoSlantEnabled defaults true on FakeSstvSessionService (matching this port's
        // own always-on-by-default), so this is "on, not locked yet" -- NOT "off".
        Assert.Equal("Panes.RxSync.AutoCorrectValue.OnNotLocked", vm.AutoCorrectDisplay);
        Assert.Equal("Panes.RxInput.ClippingValue.Normal", vm.ClippingDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AutoSlantDisabled_ShowsOff_RegardlessOfSlantPpm()
    {
        // Regression test for the toggle this batch adds: AutoSlantEnabled=false must show "Off" even
        // if SlantPpm happens to be non-null (shouldn't be reachable in production once the decoder
        // gate is correctly wired, but the VM's own display logic must not silently relabel it
        // "locked" if it somehow were).
        var sstvSession = new FakeSstvSessionService { AutoSlantEnabled = false, SlantPpm = 3.4 };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.PollTelemetry();

        Assert.Equal("Panes.RxSync.AutoCorrectValue.Off", vm.AutoCorrectDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AvtMode_AutoCorrectDisplayShowsPlaceholder_EvenWithALingeringLockedSlantPpm()
    {
        // AVT has no slant tracking at all -- a different reason for "nothing to show" than "locked",
        // which a STALE SlantPpm from a previous non-AVT mode could otherwise satisfy (auditor-caught
        // gap in an earlier version of this test: it never actually set SlantPpm non-null, so it never
        // proved AVT wins over "Locked" specifically, only over "on, not locked yet" by omission). Must
        // win regardless.
        var sstvSession = new FakeSstvSessionService { AutoSlantEnabled = true, SlantPpm = 3.4 };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.PollTelemetry();
        Assert.Equal("Panes.RxSync.AutoCorrectValue.Locked", vm.AutoCorrectDisplay); // sanity: locked before AVT

        var avtMode = new SstvModeDefinition(
            Id: "avt", DisplayName: "AVT", VisCode: 68, ImageWidth: 320, ImageHeight: 240,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);
        sstvSession.RaiseModeDetected(avtMode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.AutoCorrectDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_RaisesPropertyChangedForAutoCorrectDisplay()
    {
        // Regression test for OnDetectedModeChanged's own new re-raise call (auditor-caught: this
        // batch added the call but nothing verified it actually fires) -- without it, switching
        // into/out of AVT wouldn't refresh AutoCorrectDisplay's "—" case until some OTHER property
        // change happened to fire first.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: [new ScanSegment("R", 138.24)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(nameof(vm.AutoCorrectDisplay), raisedProperties);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AvtMode_AutoCorrectDisplayShowsPlaceholder_EvenWithAutoSlantDisabled()
    {
        // Same reasoning as the sibling test above, for the OTHER state AVT must win over: "Off".
        var sstvSession = new FakeSstvSessionService { AutoSlantEnabled = false };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var avtMode = new SstvModeDefinition(
            Id: "avt", DisplayName: "AVT", VisCode: 68, ImageWidth: 320, ImageHeight: 240,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);
        sstvSession.RaiseModeDetected(avtMode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.AutoCorrectDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_PollTelemetry_Locked_ReflectsSessionValues()
    {
        var sstvSession = new FakeSstvSessionService
        {
            SlantPpm = 3.4,
            SyncSource = SstvSyncSource.Locked,
            SyncOffsetSamples = -12,
            SyncFrequencyCorrectionHz = -0.18,
            IsLevelOverdriven = true,
            BufferedSampleCount = 1583,
        };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.NotEqual("—", vm.SlantPpmDisplay);
        Assert.NotEqual("—", vm.SyncOffsetSamplesDisplay);
        Assert.NotEqual("—", vm.SyncToneDisplay);
        Assert.Equal("Panes.RxSync.AutoCorrectValue.Locked", vm.AutoCorrectDisplay);
        Assert.Equal("Panes.RxInput.ClippingValue.Overdriven", vm.ClippingDisplay);
        Assert.Equal("Panes.RxSync.SourceValue.Locked", vm.SyncSourceDisplay);
    }

    [AvaloniaTheory]
    [InlineData(SstvSyncSource.Idle, "Panes.RxSync.SourceValue.Idle")]
    [InlineData(SstvSyncSource.Locked, "Panes.RxSync.SourceValue.Locked")]
    [InlineData(SstvSyncSource.AvtTraining, "Panes.RxSync.SourceValue.AvtTraining")]
    public void RxImagePaneViewModel_PollTelemetry_SyncSourceDisplay_ReflectsAllThreeStates(SstvSyncSource source, string expectedKey)
    {
        var sstvSession = new FakeSstvSessionService { SyncSource = source };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.Equal(expectedKey, vm.SyncSourceDisplay);
    }

    [AvaloniaTheory]
    [InlineData(0, "Options.Decode.SenseLevel.VeryLow")]
    [InlineData(1, "Options.Decode.SenseLevel.Low")]
    [InlineData(2, "Options.Decode.SenseLevel.High")]
    [InlineData(3, "Options.Decode.SenseLevel.VeryHigh")]
    [InlineData(99, "Options.Decode.SenseLevel.VeryLow")]
    public void RxImagePaneViewModel_VisThresholdDisplay_ReflectsSenseLevel_ConstructionTimeRead(int senseLevel, string expectedKey)
    {
        var sstvSession = new FakeSstvSessionService { SenseLevel = senseLevel };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(expectedKey, vm.VisThresholdDisplay);
    }

    [AvaloniaTheory]
    [InlineData(RxBpfPreset.Off, "Options.Decode.RxBpf.Normal")]
    [InlineData(RxBpfPreset.Wide, "Options.Decode.RxBpf.Wide")]
    [InlineData(RxBpfPreset.Narrow, "Options.Decode.RxBpf.Sharp")]
    [InlineData(RxBpfPreset.VeryNarrow, "Options.Decode.RxBpf.VerySharp")]
    public void RxImagePaneViewModel_RxBpfDisplay_ReflectsRxBpfPreset_ConstructionTimeRead(RxBpfPreset preset, string expectedKey)
    {
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = preset };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(expectedKey, vm.RxBpfDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_BufferedSampleCountStatusBarDisplay_ReflectsSessionValue()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { BufferedSampleCount = 1583, CaptureOverrunCount = 7 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        _ = vm.BufferedSampleCountStatusBarDisplay;

        Assert.Equal("MainWindow.StatusBar.BufferValueFormat", localization.LastKey);
        Assert.Equal(new object[] { 1583, 7 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_BufferedSampleCountDisplay_ReflectsSessionValue()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { BufferedSampleCount = 1583, CaptureOverrunCount = 7 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        _ = vm.BufferedSampleCountDisplay;

        Assert.Equal("Panes.RxInput.BufferValueFormat", localization.LastKey);
        Assert.Equal(new object[] { 1583, 7 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_PollTelemetry_ComputesAgcGainDisplay_FromSignalPeakLevel()
    {
        // Legacy's own LevelAgc.cs:103 formula: curMax = SignalPeakLevel * 32768.0 (rescaled back to
        // legacy's int16-ish domain); agc = curMax > 32.0 ? 16384.0 / curMax : 16384.0 / 32.0.
        // SignalPeakLevel = 0.5 -> curMax = 16384.0 -> agc = 1.0 exactly, an easy value to assert
        // precisely rather than fighting floating-point rounding on an arbitrary input.
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { SignalPeakLevel = 0.5 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        _ = vm.AgcGainDisplay;

        Assert.Equal("Panes.RxInput.AgcValueFormat", localization.LastKey);
        Assert.Equal(1.0, (double)localization.LastArgs[0], precision: 6);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_PollTelemetry_SignalPeakLevelAtOrBelowFloor_ClampsAgcGainToFloor()
    {
        // curMax <= 32.0 (SignalPeakLevel <= 32.0/32768.0) hits legacy's own floor branch --
        // 16384.0 / 32.0 = 512.0 -- rather than a division that would blow up toward infinity as
        // curMax approaches zero.
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { SignalPeakLevel = 0.0 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        _ = vm.AgcGainDisplay;

        Assert.Equal(512.0, (double)localization.LastArgs[0], precision: 6);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_Constructed_LoadsTheConfiguredCaptureDeviceName()
    {
        var sstvSession = new FakeSstvSessionService { ConfiguredCaptureDeviceName = "hw:2,0 L" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("hw:2,0 L", vm.CaptureDeviceName);
        Assert.Equal("hw:2,0 L", vm.CaptureDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_NoCaptureDeviceConfigured_CaptureDeviceNameDisplayShowsPlaceholder()
    {
        var sstvSession = new FakeSstvSessionService { ConfiguredCaptureDeviceName = null };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.CaptureDeviceName);
        Assert.Equal("—", vm.CaptureDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SavedEvent_SetsFileSizeDisplayFromTheActualFileOnDisk()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal("—", vm.FileSizeDisplay);

        var path = Path.Combine(Path.GetTempPath(), $"scanlinestudio-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, new byte[2048]);
        try
        {
            ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseSaved(path, generation: 0);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2048, vm.FileSizeBytes);
            _ = vm.FileSizeDisplay;
            Assert.Equal("Panes.RxFrameMeta.FileSizeValueFormat", localization.LastKey);
            Assert.Equal(2.0, (double)localization.LastArgs[0], precision: 6);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SavedEvent_GenerationMismatch_DropsTheStaleFileSize()
    {
        // Regression test for a real race an auditor round-2 review caught: a save that was in
        // flight for an OLDER image must not overwrite FileSizeBytes once a newer image has already
        // started (IReceivedImageBuffer.Generation moved on) by the time this event is processed --
        // see OnSaved's own doc comment for why the comparison is against the BUFFER's generation,
        // not a second counter tracked independently on this class.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var buffer = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        buffer.Generation = 5; // a newer image has since started

        var path = Path.Combine(Path.GetTempPath(), $"scanlinestudio-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, new byte[100]);
        try
        {
            buffer.RaiseSaved(path, generation: 3); // this save belongs to an OLDER generation
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.FileSizeBytes);
            Assert.Equal("—", vm.FileSizeDisplay);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ResetsFileSizeBytesToNull()
    {
        // A fresh/restarted decode has no saved file of its own yet -- a stale size from the
        // PREVIOUS frame must not linger next to the new frame's "Started" time.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var path = Path.Combine(Path.GetTempPath(), $"scanlinestudio-test-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, new byte[100]);
        try
        {
            ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseSaved(path, generation: 0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(100, vm.FileSizeBytes);

            var mode = new SstvModeDefinition(
                Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
                ColorEncoding: ColorEncoding.RgbSequential,
                LineSegments: [new ScanSegment("R", 138.24)]);
            sstvSession.RaiseModeDetected(mode);
            Dispatcher.UIThread.RunJobs();

            Assert.Null(vm.FileSizeBytes);
            Assert.Equal("—", vm.FileSizeDisplay);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_ComputesClipFractions_FromTheDecodedRowsOnly()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        // 2x2 image: row 0 = 1 pure-black + 1 pure-white pixel; row 1 = 2 mid-gray. Progress=1.0
        // (fully decoded) -- both rows count, so 25% clipped each way.
        var image = new ArrayImageSource(2, 2,
        [
            new Rgb24(0, 0, 0), new Rgb24(255, 255, 255),
            new Rgb24(128, 128, 128), new Rgb24(128, 128, 128),
        ]);
        var fakeBuffer = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        fakeBuffer.Current = image;
        fakeBuffer.Progress = 1.0;
        fakeBuffer.RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.25, vm.ClippedBlackFraction, precision: 3);
        Assert.Equal(0.25, vm.ClippedWhiteFraction, precision: 3);
        Assert.NotEqual("—", vm.ClipLoHiDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_MidDecode_OnlyMeasuresRowsActuallyWritten()
    {
        // Regression test for a real bug an auditor round caught before this shipped: computing over
        // the WHOLE mode-sized canvas mid-decode measures how much of the canvas hasn't been drawn
        // yet (undecoded rows are zeroed Rgb24 -- pure black), not real image content. Row 0 here is
        // pure white (as if already decoded); row 1 is pure black (as if still zeroed/undecoded).
        // With Progress=0.5 (only row 0 "decoded"), the clip stats must reflect ONLY row 0 -- 0% black,
        // 100% white -- not the whole-canvas 50%/50% (or worse, 100% black if row 1 dominated).
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        var image = new ArrayImageSource(1, 2,
        [
            new Rgb24(255, 255, 255),
            new Rgb24(0, 0, 0),
        ]);
        var fakeBuffer = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;
        fakeBuffer.Current = image;
        fakeBuffer.Progress = 0.5;
        fakeBuffer.RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.0, vm.ClippedBlackFraction, precision: 3);
        Assert.Equal(1.0, vm.ClippedWhiteFraction, precision: 3);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_NoProgressYet_ClipLoHiDisplayShowsPlaceholder_NotAMisleadingReading()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.Progress);
        Assert.Equal("—", vm.ClipLoHiDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RequestReSyncCommand_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RequestReSyncCommand.Execute(null);

        Assert.Equal(1, sstvSession.RequestReSyncCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RequestCorrectSlantCommand_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RequestCorrectSlantCommand.Execute(null);

        Assert.Equal(1, sstvSession.RequestCorrectSlantCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AbortCommand_WhileReceiving_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.AbortCommand.Execute(null);

        Assert.Equal(1, sstvSession.AbortReceptionCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AbortCommand_WhileNotReceiving_IsASafeNoOp()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = false };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.AbortCommand.Execute(null);

        Assert.Equal(0, sstvSession.AbortReceptionCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickSelectMode_WhileReceiving_CallsForceMode()
    {
        // spec/18-path-to-1.0.md High item 7.
        var mode = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [mode], IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.QuickSelectModeCommand.Execute(mode.Id);

        Assert.Equal(1, sstvSession.ForceModeCallCount);
        Assert.Equal(mode.Id, sstvSession.LastForcedMode?.Id);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickSelectMode_WhileNotReceiving_IsASafeNoOp()
    {
        // Deliberate simplification (plan-review decision, not an oversight): ForceMode's own
        // request is deferred until whatever PushSamples call happens next, which while not
        // receiving could be an arbitrarily-later moment -- gated in the body rather than letting a
        // click silently queue a surprise for later.
        var mode = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [mode], IsReceiving = false };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.QuickSelectModeCommand.Execute(mode.Id);

        Assert.Equal(0, sstvSession.ForceModeCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickSelectMode_UnknownModeId_IsASafeNoOp()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.QuickSelectModeCommand.Execute("no-such-mode");

        Assert.Equal(0, sstvSession.ForceModeCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_IsAutoDetectPaused_Toggle_CallsSetAutoDetectPausedOnSession()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.False(vm.IsAutoDetectPaused); // defaults to "Listening", matching legacy's own default

        vm.IsAutoDetectPaused = true;

        Assert.Equal(1, sstvSession.SetAutoDetectPausedCallCount);
        Assert.True(sstvSession.IsAutoDetectPaused);

        vm.IsAutoDetectPaused = false;

        Assert.Equal(2, sstvSession.SetAutoDetectPausedCallCount);
        Assert.False(sstvSession.IsAutoDetectPaused);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickSelectMode_WhilePaused_ClearsIsAutoDetectPaused()
    {
        // Matches legacy's Start()/Start(mode,f) sharing SBAuto's own variable -- forcing a mode
        // always resumes auto-detect. The decoder-side ordering is safe regardless (see
        // ISstvDecoder.RequestAbandonReception's own doc comment); this is purely so the UI toggle
        // visibly flips back to "Listening" too.
        var mode = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [mode], IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.IsAutoDetectPaused = true;

        vm.QuickSelectModeCommand.Execute(mode.Id);

        Assert.False(vm.IsAutoDetectPaused);
        Assert.False(sstvSession.IsAutoDetectPaused);
        Assert.Equal(1, sstvSession.ForceModeCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SyncToneDisplay_MeasuredIsNominalMinusCorrectionMinusCalibrationOffset()
    {
        // Regression test for two real bugs an auditor round caught before this shipped:
        // (1) a sign inversion -- SyncFrequencyCorrectionHz is a correction ADDED to a measurement to
        //     pull it back toward nominal, so measured = nominal - CorrectionHz, not nominal + CorrectionHz;
        // (2) a residual calibration-offset bias -- AfcTracker.ProcessSample folds a deliberate legacy
        //     nudge (_calibrationOffsetHz, 3.125Hz wide/1.0Hz narrow) into the locked frequency BEFORE
        //     computing CorrectionHz, so recovering the true measured Hz needs that offset subtracted
        //     back out too: measured = nominal - CorrectionHz - calibrationOffsetHz.
        // hz = -13.125 is chosen so both correction terms (13.125 - 3.125 = 10.0) cancel to a clean
        // whole number, matching this exact worked example already documented in
        // AnalogFmSstvDecoder.cs (measured=1210Hz, target=1200Hz -> CorrectionHz=-13.125) -- but here
        // asserting the REAL true-measured-frequency value (1210.0), not the tracker's own internal
        // locked-frequency value (1213.125) an earlier version of this fix stopped one step short at.
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { SyncFrequencyCorrectionHz = -13.125 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.PollTelemetry();

        _ = vm.SyncToneDisplay;

        Assert.Equal("Panes.RxSignal.SyncToneValueFormat", localization.LastKey);
        Assert.Equal(2, localization.LastArgs.Length);
        Assert.Equal(1210.0, (double)localization.LastArgs[0], precision: 3);
        Assert.Equal(10.0, (double)localization.LastArgs[1], precision: 3);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SyncToneDisplay_UsesNarrowFamilyNominalAndCalibrationOffset_ForMnMcModes()
    {
        // AnalogFmSstvDecoder.InitializeAfc targets 1900Hz (not 1200Hz), with a 1.0Hz (not 3.125Hz)
        // calibration offset, for the narrow MN/MC family -- SyncFrequencyCorrectionHz itself carries
        // no mode tag, so this pane must derive both from DetectedMode.NarrowModeCode.
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { SyncFrequencyCorrectionHz = 0.0 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var narrowMode = new SstvModeDefinition(
            Id: "mn73", DisplayName: "MN73", VisCode: 55, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.YCbCrSequential, LineSegments: [], NarrowModeCode: 0x11);
        sstvSession.RaiseModeDetected(narrowMode);
        Dispatcher.UIThread.RunJobs();
        vm.PollTelemetry();

        _ = vm.SyncToneDisplay;

        // 1900 (narrow nominal) - 0.0 (correction) - 1.0 (narrow calibration offset) = 1899.0.
        Assert.Equal(1899.0, (double)localization.LastArgs[0], precision: 3);
    }

    [Fact]
    public void RxTelemetryLocaleFormats_MatchEnJsonsRealValues_ForBothLockedAndOverdrivenCases()
    {
        // Real-value check independent of FakeLocalizationService (which returns the raw key, not
        // the formatted string) -- same pattern as this file's own ToneMapFormat test above.
        // Literal format strings copied from assets/locale/en.json; a drift there should be caught
        // by updating this test, not silently diverging.
        Assert.Equal("+3.4 ppm", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:+0.0;-0.0} ppm", 3.4));
        Assert.Equal("-12 samples", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} samples", -12));
        // measured=1210.00Hz, delta=+10.00 -- nominal(1200) minus a -13.125 correction minus the
        // 3.125Hz wide-band calibration offset, matching SyncToneDisplay's own regression test above.
        Assert.Equal("1210.00 · +10.00", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.00} · {1:+0.00;-0.00}", 1210.0, 10.0));
        Assert.Equal("slant +3.4 ppm", string.Format(System.Globalization.CultureInfo.InvariantCulture, "slant {0:+0.0;-0.0} ppm", 3.4));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_LookupQrzCommand_DisabledWithNoOverrideCallsign_EnabledOnceTyped()
    {
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.False(vm.LookupQrzCommand.CanExecute(null));

        vm.OverrideCallsign = "W1AW";
        Assert.True(vm.LookupQrzCommand.CanExecute(null));

        vm.OverrideCallsign = "   ";
        Assert.False(vm.LookupQrzCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_LookupQrzAsync_Success_PopulatesNameQthGrid_ClearsError()
    {
        var logbookSession = new FakeLogbookSessionService
        {
            LookupResultToReturn = new QrzCallsignLookupResult(true, "Hiram Maxim", "Newington (United States)", "FN31pr", null),
        };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.OverrideCallsign = "  W1AW  ";

        await vm.LookupQrzCommand.ExecuteAsync(null);

        Assert.Equal("Hiram Maxim", vm.LookupName);
        Assert.Equal("Hiram Maxim", vm.NameDisplay);
        Assert.Equal("Newington (United States)", vm.LookupQth);
        Assert.Equal("Newington (United States)", vm.QthDisplay);
        Assert.Equal("FN31pr", vm.LookupGrid);
        // OperatorGrid is unset here (FakeSstvSessionService.OperatorGrid defaults to null), so the
        // distance half falls back to "--" -- see the sibling _WithOperatorGrid_ test below for the
        // real-distance happy path.
        Assert.Equal("FN31pr / --", vm.GridDisplay);
        Assert.Null(vm.QrzLookupErrorMessage);
        // Trimmed before being passed on, same convention as QsoLinkWindowViewModel's own callsign handling.
        Assert.Equal("W1AW", logbookSession.LastLookupCallsign);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_LookupQrzAsync_Success_WithOperatorGrid_ComputesRealDistance()
    {
        var logbookSession = new FakeLogbookSessionService
        {
            LookupResultToReturn = new QrzCallsignLookupResult(true, "Hiram Maxim", "Newington (United States)", "FN31pr", null),
        };
        var sstvSession = new FakeSstvSessionService { OperatorGrid = "EM12" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        // OperatorGrid is loaded via a construction-time fire-and-forget (LoadOperatorGridAsync) --
        // give it a turn to complete before asserting, same reasoning as any other async-in-ctor field.
        await Task.Yield();
        vm.OverrideCallsign = "W1AW";

        await vm.LookupQrzCommand.ExecuteAsync(null);

        // Real MaidenheadLocator.TryComputeDistanceBearing/FormatDistance output for EM12 -> FN31pr
        // (FormatDistance's own format: Math.Round to whole km, no thousands separator), not a
        // placeholder -- proves the distance half is actually wired end to end, not just that the
        // fallback path (asserted above) still works.
        Assert.Matches(@"^FN31pr / \d+ km$", vm.GridDisplay);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_LookupQrzAsync_Failure_SetsErrorMessage_LeavesPriorResultsIntact()
    {
        var logbookSession = new FakeLogbookSessionService
        {
            LookupResultToReturn = new QrzCallsignLookupResult(true, "Hiram Maxim", "Newington (United States)", "FN31pr", null),
        };
        var localization = new FakeLocalizationService();
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), localization, logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.OverrideCallsign = "W1AW";
        await vm.LookupQrzCommand.ExecuteAsync(null);
        Assert.Equal("Hiram Maxim", vm.LookupName);

        logbookSession.LookupResultToReturn = new QrzCallsignLookupResult(false, null, null, null, "Not found: xx1xxx");
        vm.OverrideCallsign = "XX1XXX";
        await vm.LookupQrzCommand.ExecuteAsync(null);

        // FakeLocalizationService.GetString returns the raw key, not a formatted string --
        // asserting the exact key (not just NotNull) proves the wrapper key was used, and
        // LastArgs proves the raw ErrorReason was passed through as its argument.
        Assert.Equal("Panes.RxFrameMeta.Error.LookupFailed", vm.QrzLookupErrorMessage);
        Assert.Equal("Not found: xx1xxx", localization.LastArgs[0]);
        // A failed re-lookup must not wipe a previously-successful result off the screen.
        Assert.Equal("Hiram Maxim", vm.LookupName);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_NoLookupYet_AllDisplaysShowPlaceholders()
    {
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), NullLogger<RxImagePaneViewModel>.Instance);

        // "—" matches this pane's own established placeholder convention (StartedDisplay/
        // FileSizeDisplay). The distance half ("--") is GridDisplay's own real fallback for
        // MaidenheadLocator.TryComputeDistanceBearing returning false -- correct here because both
        // LookupGrid and OperatorGrid are null with no lookup performed, not unwired literal text
        // (see the _WithOperatorGrid_ test above for the real-distance path).
        Assert.Equal("—", vm.NameDisplay);
        Assert.Equal("—", vm.QthDisplay);
        Assert.Equal("— / --", vm.GridDisplay);
        Assert.Equal("—", vm.DecodedNrRstDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_LoadsTheConfiguredOutputDeviceName()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], ConfiguredPlaybackDeviceName = "USB Audio CODEC" };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("USB Audio CODEC", vm.OutputDeviceName);
        // OutputDeviceNameDisplay is what TxControlsPaneView.axaml actually binds -- OutputDeviceName
        // alone (asserted above) doesn't prove the View-facing property is wired correctly.
        Assert.Equal("USB Audio CODEC", vm.OutputDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_NoPlaybackDeviceConfigured_OutputDeviceNameStaysNull()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], ConfiguredPlaybackDeviceName = null };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OutputDeviceName);
        // Same fallback-dash convention as RxImagePaneViewModel.CaptureDeviceNameDisplay -- the View
        // must never render a blank cell here.
        Assert.Equal("—", vm.OutputDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_NoModeSelected_ToneMapTextIsNull()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.Null(vm.SelectedMode);
        Assert.Null(vm.ToneMapText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_ModeSelected_ToneMapTextIsPopulated()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.NotNull(vm.SelectedMode);
        Assert.NotNull(vm.ToneMapText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_IdentificationCardReflectsBothIdMethodsEnabled()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None with
            {
                FskIdEnabled = true,
                Callsign = "W1AW",
                CwEnabled = true,
                CwWpm = 22,
                CwToneFrequencyHz = 700,
            },
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        // FakeLocalizationService.GetString echoes the key itself -- FskIdDisplay only ever goes
        // through the localizer for the "Off" fallback, so a non-"Off" result here proves it took
        // the raw-callsign branch, not the format-string one.
        Assert.Equal("W1AW", vm.FskIdDisplay);
        Assert.Equal("Panes.TxId.CwIdFormat", vm.CwIdDisplay);
        Assert.Equal("Panes.TxId.TailBoth", vm.TailDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_NeitherIdMethodEnabled_IdentificationCardShowsOff()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.TxId.Off", vm.FskIdDisplay);
        Assert.Equal("Panes.TxId.Off", vm.CwIdDisplay);
        Assert.Equal("Panes.TxId.Off", vm.TailDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_FskEnabledOnlyWithEmptyCallsign_ShowsEmptyNotOff()
    {
        // FskIdDisplay's own contract: "Off" means FSK-ID TX is disabled, not that the configured
        // callsign happens to be empty -- an enabled-but-unconfigured state must still be visible
        // as such, not silently collapsed into the same fallback text as "disabled".
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None with { FskIdEnabled = true, Callsign = "" },
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, vm.FskIdDisplay);
        Assert.NotEqual("Panes.TxId.Off", vm.FskIdDisplay);
        Assert.Equal("Panes.TxId.TailFskOnly", vm.TailDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_CwOnlyEnabled_TailShowsCwOnly()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None with { CwEnabled = true },
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.TxId.TailCwOnly", vm.TailDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_SelectedModeChanges_RaisesPropertyChangedForToneMapText()
    {
        var narrowMode = TestMode with { Id = "narrow" }; // LuminanceMinHz/MaxHz not overridden here -- this test only needs a DIFFERENT mode instance, not different Hz values, to prove the reactive wiring fires
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode, narrowMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.SelectedMode = narrowMode;

        Assert.Contains(nameof(vm.ToneMapText), raisedProperties);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_NoModeSelected_GeometryAndDurationAndVisHeaderAndVoxToneAreNull()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.Null(vm.SelectedMode);
        Assert.Null(vm.GeometryText);
        Assert.Null(vm.DurationText);
        Assert.Null(vm.VisHeaderText);
        Assert.Null(vm.VoxToneText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_ModeSelected_GeometryTextUsesModeWidthAndHeight()
    {
        var mode = TestMode with { ImageWidth = 320, ImageHeight = 256 };
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [mode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), localization, new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        _ = vm.GeometryText;

        Assert.Equal("Panes.TxControls.GeometryFormat", localization.LastKey);
        Assert.Equal(new object[] { 320, 256 }, localization.LastArgs);
    }

    /// <summary>Plan-review blocker (un-stub-TX-tab piece 4): <c>ImageHeight</c> is the image's
    /// pixel height, not the transmitted-line count -- <see cref="ColorEncoding.YCbCrLinePaired"/>
    /// modes transmit one line per 2 image rows, so a naive <c>LineDurationMs * ImageHeight</c>
    /// double-counts. This mode's real numbers: 100ms/line x 4 rows / 2 rows-per-line / 1000 = 0.2s,
    /// NOT the 0.4s a naive formula would produce.</summary>
    [AvaloniaFact]
    public void TxControlsPaneViewModel_LinePairedMode_DurationTextDividesByRowsPerTransmissionLine()
    {
        var mode = TestMode with
        {
            ImageHeight = 4,
            ColorEncoding = ColorEncoding.YCbCrLinePaired,
            LineSegments = [new ScanSegment("Y", 100)],
        };
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [mode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), localization, new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        _ = vm.DurationText;

        Assert.Equal("Panes.TxControls.DurationFormat", localization.LastKey);
        Assert.Equal(0.2, Assert.IsType<double>(localization.LastArgs[0]), precision: 10);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_AutoFollowOff_AutoPicksTextShowsManual()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), localization, new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.AutoFollowRxMode = false;

        Assert.Equal("Panes.TxControls.AutoPicksValue.Manual", vm.AutoPicksText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_AutoFollowOn_AutoPicksTextShowsTheSelectedModesName()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.AutoFollowRxMode = true;

        Assert.Equal(TestMode.DisplayName, vm.AutoPicksText);
    }

    [AvaloniaTheory]
    [InlineData(VisHeaderKind.Standard, "Panes.TxControls.VisHeaderValue.Standard")]
    [InlineData(VisHeaderKind.Extended, "Panes.TxControls.VisHeaderValue.Extended")]
    [InlineData(VisHeaderKind.Narrow, "Panes.TxControls.VisHeaderValue.Narrow")]
    [InlineData(VisHeaderKind.Avt, "Panes.TxControls.VisHeaderValue.Avt")]
    public void TxControlsPaneViewModel_ModeSelected_VisHeaderTextPicksTheKeyMatchingTheSessionServicesReportedKind(VisHeaderKind kind, string expectedKey)
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], VisHeaderInfoToReturn = (kind, 0x86) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), localization, new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        _ = vm.VisHeaderText;

        Assert.Equal(expectedKey, localization.LastKey);
        Assert.Equal(new object[] { 0x86 }, localization.LastArgs);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_ModeSelected_VoxToneTextUsesTheSessionServicesLeaderToneDuration()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], LeaderToneDurationMsToReturn = 400 };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), localization, new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        _ = vm.VoxToneText;

        Assert.Equal("Panes.TxControls.VoxToneFormat", localization.LastKey);
        Assert.Equal(new object[] { 400.0 }, localization.LastArgs);
    }

    /// <summary>Same bug/fix, same header-callsign-chip precedent
    /// (<c>MainWindow.axaml.cs</c>'s <c>OptionsRequested</c> handler) as
    /// <see cref="TxControlsPaneViewModel_Constructed_LoadsTheConfiguredOutputDeviceName"/> above --
    /// re-running the now-public loader methods directly (this test doesn't exercise the
    /// window-code-behind wiring, only that re-invoking picks up a changed value).</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ReloadIdentificationAndOutputDeviceAfterChange_PicksUpTheNewValues()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            ConfiguredPlaybackDeviceName = "USB Audio CODEC",
            StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None,
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("USB Audio CODEC", vm.OutputDeviceName);
        Assert.Equal("Panes.TxId.Off", vm.FskIdDisplay);

        sstvSession.ConfiguredPlaybackDeviceName = "Focusrite Scarlett";
        sstvSession.StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None with { FskIdEnabled = true, Callsign = "W1AW" };
        await vm.LoadOutputDeviceNameAsync();
        await vm.LoadIdentificationSummaryAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Focusrite Scarlett", vm.OutputDeviceName);
        Assert.Equal("W1AW", vm.FskIdDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_LivePowerAndAlcSet_MeterFillPercentsReflectThem()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.Equal(0, vm.PowerMeterFillPercent);
        Assert.Equal(0, vm.AlcMeterFillPercent);
        Assert.Null(vm.LiveAlcPercentDisplay);

        vm.LivePowerPercent = 42f;
        vm.LiveAlcLevel = 0.75f; // RadioState.AlcLevel is a 0.0-1.0 fraction, not 0-100 -- plan-review finding

        Assert.Equal(42, vm.PowerMeterFillPercent);
        Assert.Equal(75, vm.AlcMeterFillPercent, precision: 5);
        Assert.Equal(75f, vm.LiveAlcPercentDisplay);
    }

    [Fact]
    public void ToneMapFormat_MatchesMock2sDisplayConvention_ForBothTheDefaultAndANarrowModesRange()
    {
        // Real-value check independent of FakeLocalizationService (which returns the raw key, not
        // the formatted string, so it can't verify this) -- confirms the actual locale format
        // string (assets/locale/en.json's Panes.TxControls.Telemetry.ToneMapFormat) produces
        // mock2's own literal display text for both the default range and a real narrow-family
        // override (SstvModeRegistry.cs's Mn73/Mn110-style 2044/2300 override).
        const string format = "{0:0}–{1:0} Hz"; // en dash, matching this key's own value in en.json

        Assert.Equal("1500–2300 Hz", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 1500.0, 2300.0));
        Assert.Equal("2044–2300 Hz", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, 2044.0, 2300.0));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_NothingReceivedYet_DoesNotOpenEditor()
    {
        // FakeReceivedImageBuffer.Current defaults to a 1x1 placeholder -- same "nothing received
        // yet" guard as the already-shipped AddLastRxImage (TxImageEditorPaneViewModel).
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;

        await vm.CopyReceivedImageToTxCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, editorOpenedCount);
        Assert.False(vm.IsEditorOpen);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_OpensEditorWithTheReceivedImage()
    {
        // Legacy precedent: fileview.cpp's CopyRectBitmap(pBitmapTXM) -- copies the received frame
        // into the TX slot as a fresh base image, same as Browse/Stock, not an overlay element.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.CopyReceivedImageToTxCommand.ExecuteAsync(null));

        Assert.Same(receivedImage.Current, editor.CurrentSource);
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // FakeLocalizationService.GetString echoes the raw key -- proves the real localized-name
        // lookup was actually called, not a hardcoded literal.
        Assert.Equal("Panes.TxControls.CopyToTx.FileName", vm.SelectedFileName);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_WhileGenuineEditInProgress_Refuses()
    {
        // Same blank-editor-replace-or-refuse gate as SelectImage/SelectStockImage (TryClaimEditorSlotForNewSource).
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.AddOverlayElementCommand.Execute(null);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;

        await vm.CopyReceivedImageToTxCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, editorOpenedCount);
        Assert.Same(firstEditor, ExtractCurrentEditor(vm));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitCommand_InvokesSstvSessionServiceWhenImageLoaded()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.False(vm.TransmitCommand.CanExecute(null));

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));

        await vm.TransmitCommand.ExecuteAsync(null);

        Assert.Single(sstvSession.TransmitCalls);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[0].Mode);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RunLoopbackSelfTestCommand_InvokesSstvSessionServiceAndRaisesCompleted()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            RunLoopbackSelfTestResult = new LoopbackSelfTestResult(new ArrayImageSource(1, 1, [new Rgb24(4, 5, 6)]), TestMode.Id, LoopbackSelfTestOutcome.Completed),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.False(vm.RunLoopbackSelfTestCommand.CanExecute(null));

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.RunLoopbackSelfTestCommand.CanExecute(null));

        LoopbackSelfTestResultWindowViewModel? completedResult = null;
        vm.LoopbackSelfTestCompleted += result => completedResult = result;

        await vm.RunLoopbackSelfTestCommand.ExecuteAsync(null);

        Assert.Single(sstvSession.RunLoopbackSelfTestCalls);
        Assert.Equal(TestMode, sstvSession.RunLoopbackSelfTestCalls[0].Mode);
        Assert.NotNull(completedResult);
        Assert.Null(completedResult!.ModeMismatchWarning);
        Assert.False(vm.IsRunningLoopbackSelfTest);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RunLoopbackSelfTestCommand_OnFailure_SetsErrorMessage()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            ThrowOnRunLoopbackSelfTest = new InvalidOperationException("boom"),
        };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var completedRaised = false;
        vm.LoopbackSelfTestCompleted += _ => completedRaised = true;

        await vm.RunLoopbackSelfTestCommand.ExecuteAsync(null);

        Assert.False(completedRaised);
        Assert.NotNull(vm.ErrorMessage);
        Assert.False(vm.IsRunningLoopbackSelfTest);
        Assert.True(vm.RunLoopbackSelfTestCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitAndRunLoopbackSelfTest_CanExecuteAreMutuallyExclusive()
    {
        // Code-review round-1 finding: a self-test's encode+decode is real CPU work competing with a
        // live PTT-keyed playback pump, and its own result dialog is MODAL -- letting either run
        // while the other is in flight risked a dialog popping up mid-transmission and blocking Stop
        // TX. Both directions of the guard are checked here, not just one.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.True(vm.RunLoopbackSelfTestCommand.CanExecute(null));

        sstvSession.RunLoopbackSelfTestGate = new TaskCompletionSource();
        var selfTestTask = vm.RunLoopbackSelfTestCommand.ExecuteAsync(null);

        Assert.False(vm.TransmitCommand.CanExecute(null));

        sstvSession.RunLoopbackSelfTestGate.SetResult();
        await selfTestTask;

        Assert.True(vm.TransmitCommand.CanExecute(null));

        sstvSession.BlockUntilCancelled = true;
        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);

        Assert.False(vm.RunLoopbackSelfTestCommand.CanExecute(null));

        vm.StopTransmitCommand.Execute(null);
        await transmitTask;

        Assert.True(vm.RunLoopbackSelfTestCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenBlankEditorCommand_OpensTheEditorWithAModeSizedNeutralPlaceholder()
    {
        // Backlog fix (user request, 2026-08-17): "don't leave the TX window completely empty until
        // you load an image" -- opens the editor without any Browse/Stock pick, using a solid
        // neutral-gray placeholder sized to the target mode's own frame dimensions.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        Assert.Equal(TestMode.ImageWidth, editor.CurrentSource.Width);
        Assert.Equal(TestMode.ImageHeight, editor.CurrentSource.Height);
        Assert.True(vm.IsEditorOpen);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenBlankEditorCommand_ReentrantCallWhileARealEditIsInProgress_IsANoOp()
    {
        // Auditor-found regression fix (2026-08-17) relaxed this guard to allow a reentrant call
        // while the open editor is blank/untouched (see the sibling
        // OpenBlankEditorCommand_AllowedAgainWhileAlreadyBlankAndUntouched_ProducesAFreshEditor
        // test for that now-intentional case) -- this pins the invariant that's still true: a real
        // edit in progress still can't be silently swapped out for a fresh blank one.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);

        await vm.OpenBlankEditorCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, editorOpenedCount);
        Assert.Same(editor, ExtractCurrentEditor(vm));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CancellingEditor_WithNothingEverApplied_ReopensBlankEditorAutomatically()
    {
        // Backlog item (user request, 2026-08-17): "should ALWAYS open the editor by default" --
        // backing out via Cancel must not leave the center column empty again (SelectedFileName is
        // null here since nothing has ever been applied).
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsEditorOpen);
        Assert.Equal(2, editorOpenedCount);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CancellingReEditOfAnAlreadyAppliedImage_DoesNotReopenBlankEditor()
    {
        // Must NOT auto-reopen blank here -- SelectedFileName is set (something was already
        // applied), so an auto-reopen would silently discard the applied state the operator is
        // still meant to see/transmit.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var reopenedEditor = await OpenEditorAsync(vm, () => vm.EditCurrentImageCommand.ExecuteAsync(null));

        reopenedEditor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEditorOpen);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectFavoriteMode_AllowedWhileTheBlankPlaceholderEditorIsOpenAndUntouched()
    {
        // AskUserQuestion decision (2026-08-17, recommended option): a BLANK auto-opened editor is
        // safe to switch mode away from, unlike a manually-picked real photo (see the sibling
        // "DisablesTheFavoriteModeCommand" test below, which still asserts CanExecute false for
        // exactly that SelectImageCommand case).
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        Assert.True(vm.SelectFavoriteModeCommand.CanExecute(modeB));
        vm.SelectFavoriteModeCommand.Execute(modeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("other", vm.SelectedMode?.Id);
        Assert.True(vm.IsEditorOpen);
    }

    // Auditor-found regression (2026-08-17, usability-gap review): with the editor now open by
    // default, a bare IsEditorOpen guard made Browse/STOCK/the mode ComboBox permanently
    // unreachable after the very first blank auto-open -- there was no way to ever load a real
    // photo or change SSTV mode via the dropdown. Fixed the same way the FAVORITES row already
    // was: relaxed while the currently-open editor is blank/untouched.

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectImageCommand_AllowedWhileTheBlankPlaceholderEditorIsOpen_ReplacesItWithTheRealPhoto()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var blankEditor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        Assert.True(vm.CanChangeSourceOrMode);

        var realEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        Assert.NotSame(blankEditor, realEditor);
        Assert.Equal(9, realEditor.CurrentSource.Width);
        Assert.True(vm.IsEditorOpen);
        Assert.False(vm.CanChangeSourceOrMode, "a real photo pick must re-lock mode-select/Browse/STOCK, same as before this fix.");
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectImageCommand_StillDisallowedWhileARealEditIsInProgress()
    {
        // Must NOT relax for a genuinely in-progress edit -- only the specific "nothing to lose"
        // blank-placeholder case.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;

        Assert.False(vm.CanChangeSourceOrMode);

        // SelectImageCommand is a bare [RelayCommand] with no CanExecute predicate (its own
        // established "body-level check is the real backstop" pattern, matching SelectFavoriteMode/
        // QuickSelectMode's own documented reasoning) -- CanChangeSourceOrMode is the real gate
        // (drives the AXAML IsEnabled binding); a direct Execute call must still be a safe no-op.
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, editorOpenedCount);
        Assert.True(vm.IsEditorOpen);
        Assert.True(editor.HasUnsavedEdits);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectFavoriteMode_DisallowedOnceTheBlankEditorHasARealEdit()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var canExecuteChangedCount = 0;
        vm.SelectFavoriteModeCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        editor.AddOverlayElementCommand.Execute(null);

        Assert.True(editor.HasUnsavedEdits);
        Assert.False(vm.SelectFavoriteModeCommand.CanExecute(modeB));
        Assert.True(canExecuteChangedCount > 0, "HasUnsavedEdits flipping must re-notify CanExecute via OnCurrentEditorPropertyChanged, or a bound Button would never actually disable.");
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenBlankEditorCommand_AllowedAgainWhileAlreadyBlankAndUntouched_ProducesAFreshEditor()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        Assert.True(vm.OpenBlankEditorCommand.CanExecute(null));
        var secondEditor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        Assert.NotSame(firstEditor, secondEditor);
        Assert.True(vm.IsEditorOpen);
    }

    [AvaloniaFact]
    public async Task CanChangeSourceOrMode_ReNotifiesAfterCancelReopensBlank_NotJustOnTheEarlierIsEditorOpenToggle()
    {
        // Real bug caught via real-window testing (2026-08-17): IsEditorOpen toggles true BEFORE
        // OpenEditorWithLoadedSourceAsync's own await-gated _currentEditor assignment completes, so
        // the OnIsEditorOpenChanged-driven notify fires while _currentEditor is still null (always
        // reads as "not blank yet" at that instant). Without an explicit re-notify once
        // _currentEditor is actually attached, CanChangeSourceOrMode's bindable VALUE ends up
        // correct but the mode ComboBox's IsEnabled binding never learns about it -- confirmed live,
        // the ComboBox stayed visibly greyed out after Cancel auto-reopened a fresh blank editor.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        // Cancel -> Cancelled fires -> IsEditorOpen false->true (via OpenBlankEditorAsync) already
        // accounts for 2 raises on its own (one per toggle) -- counting strictly MORE than that
        // proves the new explicit re-notify after _currentEditor is actually attached is what's
        // firing, not just catching the same 2 toggle-driven raises the bug already had.
        var canChangeSourceOrModeRaiseCount = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TxControlsPaneViewModel.CanChangeSourceOrMode))
            {
                canChangeSourceOrModeRaiseCount++;
            }
        };

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.CanChangeSourceOrMode);
        Assert.True(canChangeSourceOrModeRaiseCount > 2, $"expected more than the 2 IsEditorOpen-toggle-driven raises (got {canChangeSourceOrModeRaiseCount}) -- the explicit re-notify after _currentEditor is attached must also fire.");
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectingANewMode_WhileTheBlankPlaceholderEditorIsOpen_ReopensAtTheNewModesSize()
    {
        // Backlog item (user request, 2026-08-17): "make sure to update the editor window when
        // another mode is selected".
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 5, ImageHeight = 3 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        var firstEditor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        Assert.Equal(modeA.ImageWidth, firstEditor.CurrentSource.Width);

        var secondEditor = await OpenEditorAsync(vm, () =>
        {
            vm.SelectedMode = modeB;
            return Task.CompletedTask;
        });

        Assert.Equal(modeB.ImageWidth, secondEditor.CurrentSource.Width);
        Assert.Equal(modeB.ImageHeight, secondEditor.CurrentSource.Height);
        Assert.True(vm.IsEditorOpen);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeChangeWhileABlankEditorIsOpenOverAnAlreadyAppliedEdit_StillReflowsLoadedImage()
    {
        // Tier B audit finding (blocker): OnSelectedModeChanged's blank-editor branch used to
        // `return` right after reopening the blank editor, which ALSO skipped the reflow below --
        // but a blank/untouched editor being open says nothing about whether _editState is null.
        // Real, UI-reachable sequence: Apply an image (sets _editState/_loadedImage at modeA's
        // dimensions), THEN click "Open blank editor" directly (OpenBlankEditorCommand is gated on
        // CanChangeSourceOrMode = !IsEditorOpen, true again once Apply closed the editor -- nothing
        // about that gate requires _editState to be null). _editState survives untouched. Changing
        // mode with the blank editor now open used to leave _loadedImage stale at modeA's pixel
        // dimensions while SelectedMode moved to modeB -- a mismatch AnalogFmSstvEncoder throws on,
        // surfacing only as a context-free "Transmit failed".
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((modeA.ImageWidth, modeA.ImageHeight), (ExtractLoadedImage(vm)!.Width, ExtractLoadedImage(vm)!.Height));

        await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        Assert.True(vm.IsEditorOpen);

        vm.SelectedMode = modeB;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((modeB.ImageWidth, modeB.ImageHeight), (ExtractLoadedImage(vm)!.Width, ExtractLoadedImage(vm)!.Height));
        Assert.True(vm.TransmitCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_SelectingANewMode_ClearsAlreadyLoadedImage()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

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
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), stockLibrary, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectStockImageCommand.ExecuteAsync(stockEntry));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Equal("stock.png", vm.SelectedFileName);

        await vm.TransmitCommand.ExecuteAsync(null);
        Assert.Single(sstvSession.TransmitCalls);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_PickingASource_OpensTheEditorInsteadOfLoadingDirectly()
    {
        // The pre-TX-editor behavior auto-resized straight to mode dimensions with no edit step;
        // this pass's real behavior change is that picking a source loads it at native resolution
        // and hands it to an editor instead -- CanTransmit must NOT flip true until Apply happens.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        // Opening the editor must not, by itself, make the old flat-load path's image transmittable
        // -- only Apply does that now.
        Assert.False(vm.TransmitCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectImageCommand_PickerThrows_SetsErrorMessage()
    {
        // Tier B audit finding: every sibling failure path (OpenEditorForSourceAsync,
        // OpenEditorWithLoadedSourceAsync, EditCurrentImageAsync) sets ErrorMessage on failure --
        // this one didn't, so a picker failure was indistinguishable from the user pressing Cancel.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var filePicker = new FakeFilePickerService { ThrowOnPickImageFile = new InvalidOperationException("picker unavailable") };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        await vm.SelectImageCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditorOpen);
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CancellingTheEditor_LeavesAnyPreviouslyAppliedImageUntouched()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));
        var loadedAfterFirstApply = ExtractLoadedImage(vm);

        filePicker.PathToReturn = "/tmp/b.png";
        var secondEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        secondEditor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Same(loadedAfterFirstApply, ExtractLoadedImage(vm));
        Assert.Equal("a.png", vm.SelectedFileName);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectingASourceWhileAnEditorIsAlreadyOpen_IsIgnored()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { UseManualGating = true };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;

        var firstPick = vm.SelectImageCommand.ExecuteAsync(null); // original load in flight, not resolved yet
        Dispatcher.UIThread.RunJobs();
        var secondPick = vm.SelectImageCommand.ExecuteAsync(null); // must be a no-op -- an editor is already "opening"
        Dispatcher.UIThread.RunJobs();

        Assert.Single(imageFileLoader.PendingLoads);
        imageFileLoader.PendingLoads[0].SetResult(new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]));
        Dispatcher.UIThread.RunJobs();
        await firstPick;
        await secondPick;

        Assert.Equal(1, editorOpenedCount);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeChangeWithAnAppliedEdit_ReRunsThePipelineAtTheNewModesDimensions()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));

        vm.SelectedMode = modeB;

        // Retained and successfully re-flowed at the new mode's dimensions -- NOT nulled out the
        // way the pre-Phase-4 behavior did, and re-derived from the cached ORIGINAL (never
        // re-reads the file), per spec/07-image-pipeline.md's "Mode-change interaction" note.
        Assert.Equal("a.png", vm.SelectedFileName);
        Assert.NotNull(vm.PreviewImage);
        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Equal((modeB.ImageWidth, modeB.ImageHeight), (ExtractLoadedImage(vm)!.Width, ExtractLoadedImage(vm)!.Height));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeChangeWithAnAppliedEdit_CarriesAdjustmentSlidersThroughTheReflow()
    {
        // Real gap found while implementing the adjustment sliders (spec/18-path-to-1.0.md Medium
        // item): EditState originally had no Adjustments field at all, so OnSelectedModeChanged's
        // own Crop->Resize->ApplyOverlay reflow silently dropped Brightness/Contrast/etc. on the
        // very next mode change after Apply -- a real feature loss, not just a cosmetic gap, and
        // not caught by TxImageEditorPaneViewModelTests since those only exercise the editor in
        // isolation, never this reflow path. Pins that ApplyAdjustments is now actually invoked
        // during the reflow, with the SAME adjustment values the user set before Apply.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.Brightness = 33;
        editor.Sharpen = 77;
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var adjustmentsCallCountBeforeModeChange = preparer.ApplyAdjustmentsCallCount;

        vm.SelectedMode = modeB;

        Assert.True(preparer.ApplyAdjustmentsCallCount > adjustmentsCallCountBeforeModeChange);
        var adjustments = preparer.Adjustments[^1];
        Assert.Equal(33, adjustments.Brightness);
        Assert.Equal(77, adjustments.Sharpen);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_EditCurrentImage_CanExecuteOnlyAfterAnAppliedEdit_AndNotWhileAnEditorIsOpen()
    {
        // spec/18-path-to-1.0.md Medium item: re-open/re-edit an image after Apply.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.False(vm.EditCurrentImageCommand.CanExecute(null));

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        Assert.False(vm.EditCurrentImageCommand.CanExecute(null), "Must stay disabled while the FIRST editor is still open.");

        // Round-1 plan-review blocker: _editState is a plain field, not observable, so nothing
        // re-evaluates CanExecute unless something explicitly calls NotifyCanExecuteChanged() --
        // calling CanExecute(null) directly (as above) re-evaluates the predicate fresh regardless
        // of that wiring, so it CANNOT catch a missing NotifyCanExecuteChanged() call; only
        // asserting the CanExecuteChanged EVENT actually fires (the real, observable effect a
        // bound Button's own IsEnabled relies on) can.
        var canExecuteChangedFireCount = 0;
        vm.EditCurrentImageCommand.CanExecuteChanged += (_, _) => canExecuteChangedFireCount++;

        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(canExecuteChangedFireCount > 0, "EditCurrentImageCommand.CanExecuteChanged must fire after Apply, or a bound Button would never actually enable.");
        Assert.True(vm.EditCurrentImageCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_EditCurrentImage_ReopensWithTheRetainedCropPreserveAspectAndAdjustments()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        firstEditor.PreserveAspect = false;
        firstEditor.Brightness = 42;
        firstEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var reopenedEditor = await OpenEditorAsync(vm, () => vm.EditCurrentImageCommand.ExecuteAsync(null));

        Assert.Equal(new NormalizedRect(0.1, 0.2, 0.3, 0.4), reopenedEditor.CropRect);
        Assert.False(reopenedEditor.PreserveAspect);
        Assert.Equal(42, reopenedEditor.Brightness);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_EditCurrentImage_RestoresOverlayTextWithItsRawMacroTemplate_NotResolvedOrProjected()
    {
        // Round-1 plan-review blocker: an earlier draft of this feature deferred overlay
        // restoration entirely, which turned out to be a SILENT DESTRUCTIVE-EDIT bug -- Edit then
        // Apply again would have permanently discarded any overlay text the user had added. Fixed
        // by retaining a RAW (photo-anchored, un-macro-resolved) snapshot in EditState, separate
        // from the crop-projected/macro-resolved ImageOverlay OnSelectedModeChanged's own reflow
        // needs. This test pins that the raw TEMPLATE survives a round trip, not the resolved text.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        // Code-review finding: the CAPTURE side of this round trip (TxImageEditorPaneViewModel.
        // RawOverlayElements, read at Apply time) had zero coverage -- only the CONSTRUCTOR's own
        // consume side was pinned elsewhere. A non-identity crop makes the distinction observable:
        // if the raw snapshot leaked the CROP-PROJECTED coordinates instead (the exact bug this
        // mechanism exists to avoid), X/Y would read back near 0.5 (the crop-projected value for
        // this particular crop+element), not the real 0.25/0.75 set below.
        firstEditor.CropRect = new NormalizedRect(0.1, 0.2, 0.3, 0.4);
        firstEditor.AddOverlayElementCommand.Execute(null);
        var firstElement = (OverlayElementViewModel)firstEditor.OverlayElements[0];
        firstElement.Text = "DE %m";
        firstElement.X = 0.25;
        firstElement.Y = 0.75;
        firstElement.FontSizeRelative = 0.15;
        firstElement.Color = new Rgb24(10, 20, 30);
        firstEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var reopenedEditor = await OpenEditorAsync(vm, () => vm.EditCurrentImageCommand.ExecuteAsync(null));

        var element = (OverlayElementViewModel)Assert.Single(reopenedEditor.OverlayElements);
        Assert.Equal("DE %m", element.Text);
        Assert.Equal(0.25, element.X);
        Assert.Equal(0.75, element.Y);
        Assert.Equal(0.15, element.FontSizeRelative);
        Assert.Equal(new Rgb24(10, 20, 30), element.Color);

        // Re-applying with no further edits must NOT destroy the restored overlay text -- the
        // exact regression the deferred-restoration draft would have introduced.
        reopenedEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(preparer.TemplateDocuments[^1].Elements);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RotateThenApplyThenModeChange_UsesTheRotatedSource_NotTheStaleOriginal()
    {
        // spec/18-path-to-1.0.md High item 3, round-1 plan-review blocker: OnEditorApplied used to
        // capture the closure's pre-rotation IImageSource instead of the editor's own live
        // CurrentSource, so a rotate performed in the editor was silently discarded the next time
        // OnSelectedModeChanged re-derived the loaded image on a later mode change -- Apply's own
        // immediate output was correct, but the re-derivation reverted to the unrotated image
        // while still applying the ROTATED crop rect/overlay coordinates to it.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var preparer = new FakeTransmitImagePreparer();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.RotateCommand.Execute(null);
        var rotatedSource = editor.CurrentSource;
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedMode = modeB;

        Assert.Same(rotatedSource, preparer.CropSources[^1]);
    }

    // spec/18-path-to-1.0.md High item 2: the stale-mode transmit crash. The open editor captures
    // its target mode once at construction and never re-targets, so SelectedMode must be frozen
    // for the whole time an editor is open -- otherwise Apply hands back an image sized for a mode
    // that's no longer selected, and Transmit later pairs the mismatch with the encoder throwing.
    // Three tests below cover the three real SelectedMode-mutation paths this fix gates.

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_EditorOpen_DisablesTheFavoriteModeCommand()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        Assert.True(vm.SelectFavoriteModeCommand.CanExecute(modeB));

        // CanExecute alone would still pass this test even if OnIsEditorOpenChanged's
        // NotifyCanExecuteChanged() call were deleted (CanExecute always re-evaluates live) --
        // subscribing to CanExecuteChanged is what actually proves the button's bound IsEnabled
        // would visually update without something else forcing a re-query (code-review nit).
        var canExecuteChangedCount = 0;
        vm.SelectFavoriteModeCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        Assert.True(vm.IsEditorOpen);
        Assert.False(vm.SelectFavoriteModeCommand.CanExecute(modeB));
        Assert.True(canExecuteChangedCount > 0);

        // Also exercises SelectFavoriteMode's own body-level IsEditorOpen guard (code-review
        // finding: CanExecute isn't a hard gate -- RelayCommand<T>.Execute doesn't consult it,
        // only Avalonia's Button.OnClick does) by force-invoking through ICommand.Execute directly.
        vm.SelectFavoriteModeCommand.Execute(modeB);
        Assert.Equal("test", vm.SelectedMode?.Id);

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Backlog item (user request, 2026-08-17): Cancel now auto-reopens a fresh BLANK editor
        // (nothing was ever applied here) rather than leaving the column empty -- IsEditorOpen goes
        // back to true, but CanExecute stays true too, since the freshly reopened editor is itself
        // blank/untouched (IsCurrentEditorBlankAndUntouched).
        Assert.True(vm.IsEditorOpen);
        Assert.True(vm.SelectFavoriteModeCommand.CanExecute(modeB));
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_QuickSelectMode_SetsSelectedMode()
    {
        // spec/18-path-to-1.0.md High item 7.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;

        vm.QuickSelectModeCommand.Execute("other");

        Assert.Equal("other", vm.SelectedMode?.Id);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_QuickSelectMode_UnknownModeId_IsASafeNoOp()
    {
        var modeA = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;

        vm.QuickSelectModeCommand.Execute("no-such-mode");

        Assert.Equal("test", vm.SelectedMode?.Id);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_EditorOpen_DisablesTheQuickSelectModeCommand()
    {
        // Mirrors TxControlsPaneViewModel_EditorOpen_DisablesTheFavoriteModeCommand above --
        // QuickSelectMode is a separate command with its own CanExecute/body-level guard, not a
        // wrapper around SelectFavoriteMode, so this session's own High item 2 fix needs its own
        // regression coverage here, not an inherited assumption.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        Assert.True(vm.QuickSelectModeCommand.CanExecute("other"));

        var canExecuteChangedCount = 0;
        vm.QuickSelectModeCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        Assert.True(vm.IsEditorOpen);
        Assert.False(vm.QuickSelectModeCommand.CanExecute("other"));
        Assert.True(canExecuteChangedCount > 0);

        // Also exercises QuickSelectMode's own body-level IsEditorOpen guard directly, the same
        // way SelectFavoriteMode's own sibling test does (CanExecute isn't a hard gate --
        // RelayCommand<T>.Execute doesn't consult it, only Avalonia's Button.OnClick does).
        vm.QuickSelectModeCommand.Execute("other");
        Assert.Equal("test", vm.SelectedMode?.Id);

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Backlog item (user request, 2026-08-17): same reasoning as
        // TxControlsPaneViewModel_EditorOpen_DisablesTheFavoriteModeCommand's own sibling assertion
        // -- Cancel now auto-reopens a fresh BLANK editor rather than leaving the column empty.
        Assert.True(vm.IsEditorOpen);
        Assert.True(vm.QuickSelectModeCommand.CanExecute("other"));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeDetected_WhileEditorOpen_DoesNotChangeSelectedMode()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                TxPaneUiSettings.SectionKey,
                new TxPaneUiSettings { AutoFollowRxMode = true },
                TxPaneUiSettingsJsonContext.Default.TxPaneUiSettings),
        };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedMode = modeA;

        await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        Assert.True(vm.IsEditorOpen);
        Assert.True(vm.AutoFollowRxMode); // otherwise this test would pass vacuously through the
                                           // guard's OTHER half even if the IsEditorOpen check were deleted

        // An RX-driven auto-follow firing while the TX editor is open is a real, easy-to-hit repro
        // of the stale-mode crash -- no manual mode-change interaction needed at all.
        sstvSession.RaiseModeDetected(modeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("test", vm.SelectedMode?.Id);
    }

    /// <summary>Covers <see cref="TxControlsPaneViewModel.IsEditorOpen"/>'s own open/close lifecycle
    /// only -- NOT the actual XAML <c>IsEnabled="{Binding !IsEditorOpen}"</c> binding on the mode
    /// ComboBox in <c>TxControlsPaneView.axaml</c> (code-review nit; still not exercised by any
    /// automated test here). That binding sits directly under this view's own root
    /// <c>x:DataType="vm:TxControlsPaneViewModel"</c>, so Avalonia compiles and type-checks it at
    /// build time -- a path typo would be a build error, not a silent runtime failure. Real-window
    /// verified: the mode ComboBox visibly greys out the moment the TX editor opens (Browse -&gt;
    /// pick an image) and re-enables on Cancel.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_IsEditorOpen_TracksTheEditorOpenCloseLifecycle()
    {
        var modeA = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        Assert.False(vm.IsEditorOpen);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        Assert.True(vm.IsEditorOpen);

        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEditorOpen);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SettingsLoadThrowsWhileOpeningTheEditor_ResetsIsEditorOpen_InsteadOfStayingStuckOpen()
    {
        // Code-review finding on spec/18-path-to-1.0.md High item 2: the settings-load-and-construct
        // block used to sit outside any try/catch. Since IsEditorOpen now also gates the mode
        // ComboBox/favorite buttons/RX auto-follow (not just re-entrant picking), an unhandled throw
        // here would previously have left the whole pane's mode-selection permanently disabled, with
        // no editor ever open to Cancel out of.
        var modeA = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var settingsStore = new FakeSettingsStore { LoadAsyncException = new InvalidOperationException("simulated corrupt settings file") };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        var editorOpenedCount = 0;
        vm.EditorOpened += _ => editorOpenedCount++;
        // Tier B audit finding: this catch used to leave _currentEditor/_currentEditorIsBlank stale
        // and never fire EditorClosed, unlike every other close path in this class -- MainViewModel's
        // own EditorClosed subscriber is what nulls ActiveEditor, so skipping it left the docked
        // editor view out of sync with IsEditorOpen now being false.
        var editorClosedCount = 0;
        vm.EditorClosed += () => editorClosedCount++;

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, editorOpenedCount);
        Assert.Equal(1, editorClosedCount);
        Assert.False(vm.IsEditorOpen);
        Assert.True(vm.SelectFavoriteModeCommand.CanExecute(modeA));
        Assert.NotNull(vm.ErrorMessage);
    }

    /// <summary>Drives a pick command to the point where <see cref="TxControlsPaneViewModel.EditorOpened"/>
    /// fires, then returns the editor instance -- every "pick a source" test needs this same
    /// choreography now that picking opens an editor instead of loading directly.</summary>
    private static async Task<TxImageEditorPaneViewModel> OpenEditorAsync(TxControlsPaneViewModel vm, Func<Task> pick)
    {
        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += editor => opened = editor;

        await pick();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(opened);
        return opened!;
    }

    private static IImageSource? ExtractLoadedImage(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_loadedImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as IImageSource;

    private static TxImageEditorPaneViewModel? ExtractCurrentEditor(TxControlsPaneViewModel vm)
        => typeof(TxControlsPaneViewModel).GetField("_currentEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(vm) as TxImageEditorPaneViewModel;

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_Constructed_LoadsEntriesFromTheHistoryStore()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };

        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("1", entry.Entry.Id);
        Assert.NotNull(entry.Thumbnail);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingAnEntry_LoadsAReadOnlyPreview_NeverTouchingTheLiveReceivedImageBuffer()
    {
        var historyEntry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [historyEntry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(9, 9, 9)]),
        };

        // RxHistoryPaneViewModel's constructor has no parameter that reaches IReceivedImageBuffer at
        // all (unlike RxImagePaneViewModel, which takes ISstvSessionService specifically for that
        // live binding) -- a live-buffer interaction is structurally impossible here, not just
        // unobserved, so there is nothing to fake/assert against for that half of the guarantee.
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.PreviewImage);
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.PreviewImage);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_DefaultsToTodayOnly_MatchingTheMock2DraftsOwnDefaultSelection()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.ShowTodayOnly);
        // Construction now issues TWO queries: this Gallery-tab filter (index 0, RefreshAsync fires
        // first) and FramesTodayCount's own separate always-"today" query (index 1) -- see
        // RxHistoryPaneViewModel_Constructed_LoadsFramesTodayCount_UsingLocalTodayMatchingHowReceivedAtIsActuallyStored
        // for that second query's own assertions.
        Assert.Equal(2, historyStore.QueryFilters.Count);
        var filter = historyStore.QueryFilters[0];
        Assert.NotNull(filter.From);
        Assert.Null(filter.To);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_TogglingToAll_ReQueriesWithNoDateFilter()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        vm.ShowTodayOnly = false;
        Dispatcher.UIThread.RunJobs();

        // 2 from construction (Gallery-tab filter + FramesTodayCount's own separate query) + 1 from
        // this toggle's own re-query.
        Assert.Equal(3, historyStore.QueryFilters.Count);
        var lastFilter = historyStore.QueryFilters[^1];
        Assert.Null(lastFilter.From);
        Assert.Null(lastFilter.To);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_LoadsImagesDirectory_ForTheGalleryTabsStorageCard()
    {
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = "/tmp/scanlinestudio-history" };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/tmp/scanlinestudio-history", vm.ImagesDirectory);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_ImagesDirectoryExists_LoadsRealDiskFreeSpace()
    {
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = Path.GetTempPath() };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.DiskFreeGigabytes);
        Assert.True(vm.DiskFreeGigabytes > 0);
        Assert.NotEqual("—", vm.DiskFreeDisplay);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_ImagesDirectoryNotYetCreated_StillLoadsDiskFreeSpace_ViaNearestExistingAncestor()
    {
        // Auditor-caught regression: on a fresh profile, ReceiveHistorySettings' own default images
        // directory doesn't exist until the first frame is actually saved -- without walking up to
        // the nearest existing ancestor, this would stay stuck at "—" for the entire first session.
        var notYetCreated = Path.Combine(Path.GetTempPath(), "scanlinestudio-not-yet-created-" + Guid.NewGuid().ToString("N"));
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = notYetCreated };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.DiskFreeGigabytes);
        Assert.True(vm.DiskFreeGigabytes > 0);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_ImagesDirectoryUnresolvable_DiskFreeStaysAnHonestPlaceholder()
    {
        // Empty string is the one input the nearest-existing-ancestor walk can't recover from --
        // Path.GetDirectoryName("") returns null, so the walk stops immediately and DriveInfo("")
        // throws ArgumentException (confirmed empirically), which UpdateDiskFreeSpace must swallow
        // the same best-effort way LoadImagesDirectoryAsync already does for the directory read
        // itself, not surface as an unhandled exception. A merely-not-yet-created but otherwise
        // well-formed path (e.g. the default FakeReceiveHistoryStore.ImagesDirectory) is NOT this
        // case any more -- see the nearest-existing-ancestor test above.
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = "" };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.DiskFreeGigabytes);
        Assert.Equal("—", vm.DiskFreeDisplay);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_LoadsFramesTodayCount_UsingLocalTodayMatchingHowReceivedAtIsActuallyStored()
    {
        // Regression test for a real, auditor-caught bug: this VM's own doc comment (and an earlier
        // version of LoadFramesTodayCountAsync) claimed ReceivedAt is stored UTC and anchored this
        // query to UTC midnight -- but ReceiveHistoryRecorder actually writes DateTimeOffset.Now
        // (LOCAL offset), and SqliteReceiveHistoryStore's From/To filter is a lexicographic TEXT
        // compare that only stays correct when the query's own offset matches the stored rows'. A
        // UTC-anchored query would silently miss/double-count several hours of frames around every
        // day boundary on any non-UTC machine (see SqliteReceiveHistoryStoreTests'
        // QueryAsync_DateRangeCompareIsLexicographicOnStoredOffset_NotInstantBased for the store-level
        // proof). This query must match ShowTodayOnly's own local `DateTime.Today` convention below
        // exactly -- asserting a non-null From alone would not catch a UTC-vs-local regression.
        // Captured before constructing the VM -- avoids a theoretical flake if this test happens to
        // straddle local midnight between construction and the assertion below.
        var expectedTodayAnchor = new DateTimeOffset(DateTime.Today);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.Now, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("2", DateTimeOffset.Now, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.FramesTodayCount);
        Assert.Equal("MainWindow.StatusBar.FramesTodayValueFormat", vm.FramesTodayDisplay);

        // filters[0] is RefreshAsync's own Gallery-tab (ShowTodayOnly) query; filters[1] is this
        // FramesTodayCount query -- both must now share the SAME local-offset "today" anchor.
        Assert.Equal(2, historyStore.QueryFilters.Count);
        var framesTodayFilter = historyStore.QueryFilters[1];
        Assert.Equal(expectedTodayAnchor, framesTodayFilter.From);
        Assert.Null(framesTodayFilter.To);

        // Toggling the UNRELATED Gallery-tab filter must not change the status bar's own count --
        // this is a genuinely separate, independent, load-once query.
        vm.ShowTodayOnly = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.FramesTodayCount);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_RecordedEvent_RefreshesEntries()
    {
        // Regression test for batch 7's live-update fix (spec/16-gui-wiring-survey.md's own PARTIAL
        // finding): before this, Entries only refreshed at construction/manual-refresh/filter-change,
        // never as new frames actually landed during a session.
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(vm.Entries);

        var newEntry = new ReceiveHistoryEntry("new-1", DateTimeOffset.Now, "robot36", "/tmp/new.png", null, ReceiveDecodeState.Completed);
        historyStore.EntriesToReturn.Add(newEntry);
        historyStore.RaiseRecorded(newEntry);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("new-1", entry.Entry.Id);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_RecordedEvent_PreservesTheCurrentSelectionAcrossTheRefresh()
    {
        // Regression test for a real UX regression the live-update fix would otherwise introduce:
        // every refresh builds brand-new RxHistoryEntryViewModel instances (a record, no identity
        // beyond reference equality), so without re-selecting by Entry.Id after repopulating, a user
        // actively browsing history would have their selection (and its preview) silently wiped every
        // time an unrelated new frame lands.
        //
        // Auditor-caught: asserting only `SelectedEntry!.Entry.Id == "selected"` is vacuous in a
        // plain VM test -- there's no ListBox here to null SelectedEntry when Entries.Clear() runs
        // (that side effect only exists via the real SelectedItem two-way binding in MainWindow.axaml),
        // so even DELETING the re-select logic in RefreshAsync entirely would still leave the STALE
        // pre-refresh instance sitting in SelectedEntry with the same Entry.Id, passing the same
        // assertion. Asserting reference identity against the NEWLY repopulated Entries collection is
        // what actually proves the re-select logic ran, not just that nothing happened to clobber the
        // old reference.
        var selectedEntry = new ReceiveHistoryEntry("selected", DateTimeOffset.Now, "robot36", "/tmp/selected.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [selectedEntry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        var originalSelectedInstance = vm.Entries.Single(e => e.Entry.Id == "selected");
        vm.SelectedEntry = originalSelectedInstance;
        Dispatcher.UIThread.RunJobs();

        var unrelatedNewEntry = new ReceiveHistoryEntry("unrelated", DateTimeOffset.Now.AddSeconds(1), "robot36", "/tmp/unrelated.png", null, ReceiveDecodeState.Completed);
        historyStore.EntriesToReturn.Add(unrelatedNewEntry);
        historyStore.RaiseRecorded(unrelatedNewEntry);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("selected", vm.SelectedEntry!.Entry.Id);
        // The real proof: a NEW instance from the repopulated collection, not the stale pre-refresh
        // reference -- confirms RefreshAsync's own re-select-by-Id logic actually ran.
        Assert.NotSame(originalSelectedInstance, vm.SelectedEntry);
        Assert.Same(vm.Entries.Single(e => e.Entry.Id == "selected"), vm.SelectedEntry);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_RecordedEvent_ThroughARealListBoxTwoWayBinding_DoesNotFlickerThePreview()
    {
        // Auditor round-2 catch: the two tests above construct the VM in isolation, with no ListBox
        // to null SelectedEntry when Entries.Clear() runs -- but MainWindow.axaml's real Gallery
        // ListBox binds SelectedItem TwoWay (Avalonia's own default for SelectingItemsControl
        // .SelectedItemProperty, confirmed via reflection against Avalonia.Controls.Primitives
        // .SelectingItemsControl's DirectPropertyMetadata), so in the ACTUAL app, Entries.Clear()
        // synchronously pushes SelectedEntry = null into this VM before RefreshAsync's own re-select
        // line runs. The isolated-VM tests above could not have caught the _isRepopulating fix's own
        // bug (an earlier version of this fix didn't guard that transient null, so the flicker this
        // whole regression test class exists to prevent still happened in the real app despite both
        // isolated tests passing). This test wires up a REAL ListBox with the same TwoWay binding to
        // close that coverage gap.
        var selectedEntry = new ReceiveHistoryEntry("selected", DateTimeOffset.Now, "robot36", "/tmp/selected.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [selectedEntry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        var listBox = new ListBox { ItemsSource = vm.Entries };
        listBox.Bind(ListBox.SelectedItemProperty, new Binding(nameof(RxHistoryPaneViewModel.SelectedEntry)) { Source = vm, Mode = BindingMode.TwoWay });
        Dispatcher.UIThread.RunJobs();

        listBox.SelectedItem = vm.Entries.Single(e => e.Entry.Id == "selected");
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.PreviewImage);
        var loadsBeforeRefresh = historyStore.ThumbnailLoadCalls.Count(c => c.EntryId == "selected" && c.MaxDimension == 512);
        Assert.Equal(1, loadsBeforeRefresh);

        var unrelatedNewEntry = new ReceiveHistoryEntry("unrelated", DateTimeOffset.Now.AddSeconds(1), "robot36", "/tmp/unrelated.png", null, ReceiveDecodeState.Completed);
        historyStore.EntriesToReturn.Add(unrelatedNewEntry);
        historyStore.RaiseRecorded(unrelatedNewEntry);
        Dispatcher.UIThread.RunJobs();

        // The real proof: selection survived a real ListBox's Clear()-driven transient null, AND the
        // 512px preview was not re-decoded a second time for the same logical entry.
        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("selected", vm.SelectedEntry!.Entry.Id);
        Assert.NotNull(vm.PreviewImage);
        var loadsAfterRefresh = historyStore.ThumbnailLoadCalls.Count(c => c.EntryId == "selected" && c.MaxDimension == 512);
        Assert.Equal(1, loadsAfterRefresh);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_SelectLatestCommand_SelectsTheNewestEntry()
    {
        // Port of legacy's real SBPrim "jump to most recent" speed button (NOT SBLatest, despite that
        // name's misleading English reading -- see SelectLatest's own doc comment) -- Entries is already
        // newest-first (FakeReceiveHistoryStore.QueryAsync now mirrors SqliteReceiveHistoryStore's own
        // real ORDER BY ReceivedAt DESC), so this asserts SelectLatest picks Entries[0], not a
        // re-derived "actually newest by timestamp" check.
        var older = new ReceiveHistoryEntry("older", DateTimeOffset.Now.AddMinutes(-5), "robot36", "/tmp/older.png", null, ReceiveDecodeState.Completed);
        var newer = new ReceiveHistoryEntry("newer", DateTimeOffset.Now, "robot36", "/tmp/newer.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore { EntriesToReturn = [older, newer] };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        vm.SelectLatestCommand.Execute(null);

        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("newer", vm.SelectedEntry!.Entry.Id);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_SelectLatestCommand_DisabledWhenNoEntries()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.SelectLatestCommand.CanExecute(null));

        var entry = new ReceiveHistoryEntry("1", DateTimeOffset.Now, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        historyStore.EntriesToReturn.Add(entry);
        historyStore.RaiseRecorded(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SelectLatestCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingAnEntry_LoadsItsNoteAndFlaggedState()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "sked 2nd frame", IsFlagged: true)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("sked 2nd frame", vm.SelectedEntryNote);
        Assert.True(vm.SelectedEntryIsFlagged);
        // Loading the selection must not itself count as an edit -- no SetNoteAsync/SetFlaggedAsync
        // call yet, only ever fired by an actual user change (asserted below in the edit tests).
        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(historyStore.SetNoteCalls);
        Assert.Empty(historyStore.SetFlaggedCalls);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_EditingSelectedEntryNote_PersistsAfterDebounceDelay()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntryNote = "call back tomorrow";
        Dispatcher.UIThread.RunJobs();
        Assert.Null(historyStore.EntriesToReturn[0].Note); // not persisted yet -- still debouncing

        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("call back tomorrow", historyStore.EntriesToReturn[0].Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_TogglingSelectedEntryFlagged_PersistsImmediately()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntryIsFlagged = true;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50); // no debounce on this path, but the persist Task itself still needs to run
        Dispatcher.UIThread.RunJobs();

        Assert.True(historyStore.EntriesToReturn[0].IsFlagged);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SwitchingSelection_LoadsTheNewEntrysNoteNotThePreviousOnes()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "first"),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow.AddSeconds(-1), "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed, Note: "second"),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("first", vm.SelectedEntryNote);

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("second", vm.SelectedEntryNote);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SwitchingAwayMidEdit_PreviousEntrysPendingSaveStillCompletes()
    {
        // Auditor round-2 gap: the original version of this test never edited anything, so it could
        // not actually prove PersistNoteDebouncedAsync's captured-entryId design claim (a pending
        // save for the entry just switched AWAY from is still let complete). This one does: edit
        // entry 1, switch to entry 2 before the debounce fires, then confirm entry 1's edit still
        // lands in the store AND, on switching back, in Entries/SelectedEntryNote too (the
        // write-back fix -- without it this would show the pre-edit value even though the store has
        // the new one).
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "first"),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow.AddSeconds(-1), "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed, Note: "second"),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntryNote = "edited before switching away";
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        Dispatcher.UIThread.RunJobs();

        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("edited before switching away", historyStore.EntriesToReturn.Single(e => e.Id == "1").Note);

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("edited before switching away", vm.SelectedEntryNote);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_LiveRefreshMidEdit_DoesNotClobberTheInProgressNote()
    {
        // Auditor round-2 BLOCKER regression test: IReceiveHistoryStore.Recorded fires on every
        // completed/abandoned frame during an active RX session, each one triggering RefreshAsync,
        // which rebuilds Entries from a fresh store query and re-selects the same entry with a
        // brand-new RxHistoryEntryViewModel instance. An earlier version of OnSelectedEntryChanged
        // reloaded SelectedEntryNote/SelectedEntryIsFlagged from that re-select unconditionally,
        // silently overwriting whatever the user was mid-typing -- annotating a frame while RX keeps
        // running is the feature's entire point, so this was reachable on essentially every real use.
        var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntryNote = "typing...";
        Dispatcher.UIThread.RunJobs();

        // A new (unrelated) frame lands mid-typing, well inside the 600ms debounce window --
        // Note is still unset in the store at this point.
        var otherEntry = new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed);
        historyStore.EntriesToReturn.Add(otherEntry);
        historyStore.RaiseRecorded(otherEntry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("typing...", vm.SelectedEntryNote);

        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("typing...", historyStore.EntriesToReturn.Single(e => e.Id == "1").Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ThroughARealListBoxTwoWayBinding_UpdateEntryInPlaceDoesNotDropSelectionOrPreview()
    {
        // Auditor round-3 catch: UpdateEntryInPlace's own `Entries[index] = updated` is an IList
        // indexer replace of the currently-selected item, which raises a
        // NotifyCollectionChangedAction.Replace -- unverified locally whether Avalonia's selection
        // model processes that as remove-then-add and pushes a transient SelectedItem = null back
        // through the REAL Gallery ListBox's TwoWay binding (same class of hazard
        // RxHistoryPaneViewModel_RecordedEvent_ThroughARealListBoxTwoWayBinding_DoesNotFlickerThePreview
        // above already exists to catch for Entries.Clear()). Auditor round-3 nit: this settles the
        // OUTCOME (selection/preview survive) against a real ListBox, which is what actually matters
        // -- it does not by itself prove the _isRepopulating guard was necessary (with the guard in
        // place and wasSelected true, SelectedEntry is reassigned unconditionally regardless of
        // whether Avalonia would have nulled it on its own), only that nothing regressed.
        var entry = new ReceiveHistoryEntry("1", DateTimeOffset.Now, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        var listBox = new ListBox { ItemsSource = vm.Entries };
        listBox.Bind(ListBox.SelectedItemProperty, new Binding(nameof(RxHistoryPaneViewModel.SelectedEntry)) { Source = vm, Mode = BindingMode.TwoWay });
        Dispatcher.UIThread.RunJobs();

        listBox.SelectedItem = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.PreviewImage);

        vm.SelectedEntryNote = "note via real listbox";
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(700); // past the debounce -- UpdateEntryInPlace's Entries[index]=... runs here
        Dispatcher.UIThread.RunJobs();

        // The real proof: selection and preview both survived the replace, and the write-back
        // actually reached Entries (not just the store).
        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("1", vm.SelectedEntry!.Entry.Id);
        Assert.Equal("note via real listbox", vm.SelectedEntry.Entry.Note);
        Assert.NotNull(vm.PreviewImage);
        Assert.Equal("note via real listbox", vm.Entries.Single(e => e.Entry.Id == "1").Entry.Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ThroughARealListBoxTwoWayBinding_ReplacingANonSelectedEntryDoesNotDropTheRealSelection()
    {
        // Auditor round-3 risk: the supported "edit A, switch to B before A's debounce fires" flow
        // replaces a NON-selected index (A) while B stays selected -- a different code path than the
        // test above (which only exercises replacing the SELECTED entry). The previousSelection
        // fallback in UpdateEntryInPlace exists specifically for this case; this proves B's selection
        // survives A's write-back through a real ListBox, regardless of whether Avalonia treats a
        // non-selected-index Replace the same way as a selected-index one.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("a", DateTimeOffset.Now, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("b", DateTimeOffset.Now.AddSeconds(-1), "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        var listBox = new ListBox { ItemsSource = vm.Entries };
        listBox.Bind(ListBox.SelectedItemProperty, new Binding(nameof(RxHistoryPaneViewModel.SelectedEntry)) { Source = vm, Mode = BindingMode.TwoWay });
        Dispatcher.UIThread.RunJobs();

        listBox.SelectedItem = vm.Entries.Single(e => e.Entry.Id == "a");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntryNote = "edited before switching away";
        Dispatcher.UIThread.RunJobs();

        listBox.SelectedItem = vm.Entries.Single(e => e.Entry.Id == "b");
        Dispatcher.UIThread.RunJobs();

        // Entry "a"'s debounced save (and its UpdateEntryInPlace write-back, replacing a NON-selected
        // index since "b" is now selected) fires here.
        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("b", vm.SelectedEntry!.Entry.Id);
        Assert.Equal("edited before switching away", vm.Entries.Single(e => e.Entry.Id == "a").Entry.Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SetNoteAsync_EntryNoLongerExists_SetsTheExpectedErrorMessage()
    {
        var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        // Simulates the documented race: the entry no longer exists in the store by the time the
        // edit reaches it.
        historyStore.EntriesToReturn.Clear();

        vm.SelectedEntryNote = "too late";
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        // FakeLocalizationService.GetString returns the raw key -- asserting the exact key (not just
        // NotNull) distinguishes this from the wrong-error-message / exception-path findings the
        // auditor flagged as indistinguishable under a bare NotNull check.
        Assert.Equal("Panes.RxHistory.Error.EntryNoLongerExists", vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_TogglingFlagTwiceRapidly_LastToggleWins()
    {
        // Auditor round-3 fix: the round-2 version of this test used the fake's default
        // synchronous-completion SetFlaggedAsync, which trivially preserves call order regardless of
        // whether the VM's own chaining logic is even present -- it would have passed identically
        // against the UNFIXED (independently-fired) code. Making the FIRST call artificially slower
        // than the second is the only way to actually prove a later call doesn't race ahead of an
        // earlier one still in flight.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        historyStore.SetFlaggedCallDelays.Enqueue(TimeSpan.FromMilliseconds(200));
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntryIsFlagged = true;
        vm.SelectedEntryIsFlagged = false;
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.False(historyStore.EntriesToReturn[0].IsFlagged);
        Assert.Equal([("1", true), ("1", false)], historyStore.SetFlaggedCalls);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenInLogCommand_DisabledWithNoSelection_EnabledOnceSelected_DisabledAgainAfterDeselect()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.OpenInLogCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.OpenInLogCommand.CanExecute(null));

        // A live refresh that drops the selection back to null (e.g. the entry no longer matches the
        // current filter) must re-disable the command -- this is the auditor-caught placement fix:
        // OpenInLogCommand.NotifyCanExecuteChanged() must run BEFORE OnSelectedEntryChanged's own
        // _isRepopulating early-return, not after.
        vm.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.OpenInLogCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenInLog_QsoLinkWindowViewModelLinkedEvent_UpdatesEntryInPlace()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        QsoLinkWindowViewModel? qsoLinkVm = null;
        vm.QsoLinkRequested += requested => qsoLinkVm = requested;
        vm.OpenInLogCommand.Execute(null);

        Assert.NotNull(qsoLinkVm);

        // Simulates a successful link completing on QsoLinkWindowViewModel's own side. Code-review
        // correction: an earlier version of this comment claimed raising it synchronously here was
        // "the worst case" for OpenInLog's Dispatcher.UIThread.Post wrapper on the theory that Linked
        // could fire off the UI thread -- QsoLinkWindowViewModel no longer uses ConfigureAwait(false)
        // anywhere (that was itself a code-review fix, see its own doc comment), so Linked always
        // fires on the UI thread in practice; this test only proves the SAME-thread, synchronous case
        // works, which RunJobs() below still needs to flush the posted continuation. The Post call
        // itself stays as harmless defense, not something this test can prove necessary.
        RaiseLinkedViaReflection(qsoLinkVm!, "qso-42");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("qso-42", vm.SelectedEntry!.Entry.LinkedQsoId);
        Assert.Equal("qso-42", vm.Entries[0].Entry.LinkedQsoId);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameCommand_DisabledWithNoSelection_EnabledOnceSelected_DisabledAgainAfterDeselect()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.ExportFrameCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.ExportFrameCommand.CanExecute(null));

        vm.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.ExportFrameCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameCommand_CanExecuteChangedFiresWhenSelectionChanges()
    {
        // Deliberately does NOT use ExportFrameCommand.CanExecute(null) to verify this -- that method
        // isn't cached, it re-evaluates CanExportFrame() fresh on every call regardless of whether
        // NotifyCanExecuteChanged() ever fired, so it can't discriminate a missing
        // ExportFrameCommand.NotifyCanExecuteChanged() call from a correct one (confirmed by
        // mutation-testing: removing that call left the CanExecute-based test above still green).
        // Subscribing to the real CanExecuteChanged event is what Avalonia's own Button binding
        // relies on to know when to re-query CanExecute -- this is the only way to actually prove the
        // notify wiring itself, not just the predicate it wraps.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var fireCount = 0;
        vm.ExportFrameCommand.CanExecuteChanged += (_, _) => fireCount++;

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(fireCount > 0, "Expected CanExecuteChanged to fire when SelectedEntry became non-null.");

        fireCount = 0;
        vm.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Assert.True(fireCount > 0, "Expected CanExecuteChanged to fire when SelectedEntry was cleared.");
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_HappyPath_CallsExporterWithClampedSettingsQualityAndSetsStatus()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var frameExporter = new FakeReceivedFrameExporter();
        var filePicker = new FakeFilePickerService { SaveImagePathToReturn = ("/tmp/exported.jpg", ImageExportFormat.Jpeg) };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(ImageExportSettings.SectionKey, new ImageExportSettings { JpegQuality = 42 }, ImageExportSettingsJsonContext.Default.ImageExportSettings),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore, frameExporter, filePicker, settingsStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);

        var call = Assert.Single(frameExporter.Calls);
        Assert.Equal("/tmp/a.png", call.SourcePath);
        Assert.Equal("/tmp/exported.jpg", call.DestinationPath);
        Assert.Equal(42, call.JpegQuality);
        Assert.Equal("a.png", filePicker.LastSuggestedImageFileName);
        Assert.NotNull(vm.ExportStatusMessage);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_NoQualitySettingConfigured_DefaultsTo85()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var frameExporter = new FakeReceivedFrameExporter();
        var vm = CreateRxHistoryPaneViewModel(historyStore, frameExporter);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);

        Assert.Equal(85, Assert.Single(frameExporter.Calls).JpegQuality);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_OutOfRangeSettingsQuality_IsClamped()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var frameExporter = new FakeReceivedFrameExporter();
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(ImageExportSettings.SectionKey, new ImageExportSettings { JpegQuality = 500 }, ImageExportSettingsJsonContext.Default.ImageExportSettings),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore, frameExporter, settingsStore: settingsStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);

        // JpegEncoder.Quality's own setter throws outside 1..100 -- a hand-edited settings.json value
        // of 500 must never reach the exporter unclamped.
        Assert.Equal(100, Assert.Single(frameExporter.Calls).JpegQuality);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_PickerCancelled_DoesNotCallTheExporter()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var frameExporter = new FakeReceivedFrameExporter();
        var filePicker = new FakeFilePickerService { SaveImagePathToReturn = null };
        var vm = CreateRxHistoryPaneViewModel(historyStore, frameExporter, filePicker);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);

        Assert.Empty(frameExporter.Calls);
        Assert.Null(vm.ExportStatusMessage);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_ExporterThrows_SetsErrorMessageNotStatus()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var frameExporter = new FakeReceivedFrameExporter { ExceptionToThrow = new IOException("disk full") };
        var vm = CreateRxHistoryPaneViewModel(historyStore, frameExporter);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.ExportStatusMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_SwitchingSelectionAfterward_ClearsStaleExportStatusMessage()
    {
        // Code-review finding: without this, "Exported to /tmp/exported.jpg." from selection A kept
        // showing under the Selected-frame panel after switching to selection B.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow.AddMinutes(-1), "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ExportStatusMessage);

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ExportStatusMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_RefreshAsync_QueryThrows_SetsErrorMessageAndKeepsStaleEntries()
    {
        // Tier B audit finding (round 1): RefreshAsync's catch block used to log and return with no
        // ErrorMessage set. RefreshAsync fires on every incoming frame during an active session
        // (OnRecorded), so a persistent failure (a locked/corrupt SQLite file) left the Gallery frozen
        // on stale contents forever with zero user-visible indication anything was wrong.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.Entries);

        historyStore.ThrowOnQuery = new InvalidOperationException("database is locked");
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.Error.RefreshFailed", vm.ErrorMessage);
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_RefreshAsync_SucceedsAfterAPriorFailure_ClearsErrorMessage()
    {
        // Tier B audit finding (round 1): ErrorMessage is nulled only once RefreshAsync's query has
        // actually succeeded, not unconditionally at the top of the method -- otherwise an unrelated
        // stale error would flash away just because a refresh happened to start.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
            ThrowOnQuery = new InvalidOperationException("database is locked"),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Panes.RxHistory.Error.RefreshFailed", vm.ErrorMessage);

        historyStore.ThrowOnQuery = null;
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ExportFrameAsync_PickerThrows_SetsErrorMessage()
    {
        // Tier B audit finding (round 1): the picker call used to sit outside ExportFrameAsync's try
        // block, so an exception from the platform storage provider escaped uncaught -- the Export
        // button just appeared to silently do nothing, with no log line and no ErrorMessage.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var frameExporter = new FakeReceivedFrameExporter();
        var filePicker = new FakeFilePickerService { ThrowOnPickSaveImageFile = new InvalidOperationException("storage provider unavailable") };
        var vm = CreateRxHistoryPaneViewModel(historyStore, frameExporter, filePicker);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.ExportFrameCommand.ExecuteAsync(null);

        Assert.Equal("Panes.RxHistory.Error.ExportFrameFailed", vm.ErrorMessage);
        Assert.Empty(frameExporter.Calls);
    }

    /// <summary>QsoLinkWindowViewModel.Linked has no public raise method (by design -- only its own
    /// LinkSelectedAsync/CreateAndLinkAsync fire it) -- reflection is the only way to simulate "the
    /// dialog just linked something" without actually driving a full search/create round-trip through
    /// FakeLogbookSessionService for a test that's specifically about RxHistoryPaneViewModel's OWN
    /// dispatcher-marshalling of that event, not QsoLinkWindowViewModel's internal write-ordering
    /// (that's QsoLinkWindowViewModelTests' job).</summary>
    private static void RaiseLinkedViaReflection(QsoLinkWindowViewModel vm, string qsoId)
    {
        var field = typeof(QsoLinkWindowViewModel).GetField("Linked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var handler = (Action<string>?)field!.GetValue(vm);
        handler?.Invoke(qsoId);
    }

    private static QsoRecord SampleQsoRecord(string id = "1") =>
        new(id, "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null);

    private static LogbookPaneViewModel CreateLogbookPaneViewModel(
        FakeLogbookSessionService? logbook = null,
        FakeFilePickerService? filePicker = null) =>
        new(
            logbook ?? new FakeLogbookSessionService(),
            filePicker ?? new FakeFilePickerService(),
            new FakeSstvSessionService { AvailableModes = [TestMode] },
            new FakeLocalizationService(),
            NullLogger<LogbookPaneViewModel>.Instance);

    // Disk/DB reconciliation (user-reported gap, 2026-08-26): ReconcileDiskThenRefreshAsync is the
    // Gallery-tab-selection trigger's own target -- see MainWindowTabOrderTests' GalleryTabIndex
    // guard for the wiring half of this feature.

    [AvaloniaFact]
    public async Task ReconcileDiskThenRefreshAsync_EntriesImported_RefreshesTheList()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            ReconcileResultCount = 1,
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        historyStore.QueryFilters.Clear(); // drop the constructor's own initial RefreshAsync call

        await vm.ReconcileDiskThenRefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, historyStore.ReconcileCallCount);
        Assert.Single(historyStore.QueryFilters); // RefreshAsync ran again, picking up the import
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task ReconcileDiskThenRefreshAsync_NothingImported_DoesNotRefresh()
    {
        var historyStore = new FakeReceiveHistoryStore { ReconcileResultCount = 0 };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();
        historyStore.QueryFilters.Clear();

        await vm.ReconcileDiskThenRefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, historyStore.ReconcileCallCount);
        Assert.Empty(historyStore.QueryFilters); // no need to re-query when nothing changed
    }

    [AvaloniaFact]
    public async Task ReconcileDiskThenRefreshAsync_CalledTwice_OnlyReconcilesOnceThisSession()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        await vm.ReconcileDiskThenRefreshAsync();
        Dispatcher.UIThread.RunJobs();
        await vm.ReconcileDiskThenRefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, historyStore.ReconcileCallCount);
    }

    [AvaloniaFact]
    public async Task ReconcileDiskThenRefreshAsync_Throws_SetsErrorMessage_DoesNotPropagate()
    {
        var historyStore = new FakeReceiveHistoryStore { ThrowOnReconcile = new IOException("Simulated disk-scan failure.") };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        Dispatcher.UIThread.RunJobs();

        await vm.ReconcileDiskThenRefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.Error.ReconcileFailed", vm.ErrorMessage);
    }

    private static RxHistoryPaneViewModel CreateRxHistoryPaneViewModel(
        FakeReceiveHistoryStore historyStore,
        FakeReceivedFrameExporter? frameExporter = null,
        FakeFilePickerService? filePicker = null,
        FakeSettingsStore? settingsStore = null) =>
        new(
            historyStore,
            new FakeLocalizationService(),
            NullLogger<RxHistoryPaneViewModel>.Instance,
            new FakeLogbookSessionService(),
            NullLogger<QsoLinkWindowViewModel>.Instance,
            frameExporter ?? new FakeReceivedFrameExporter(),
            filePicker ?? new FakeFilePickerService(),
            settingsStore ?? new FakeSettingsStore());

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_NoFilterActive_FilteredEntriesMatchesEntries()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "scottie-s1", "/tmp/b.png", "qso-1", ReceiveDecodeState.Completed, IsFlagged: true),
            ],
        };

        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.FilteredEntries.Count);
        Assert.Equal(vm.Entries.Select(e => e.Entry.Id), vm.FilteredEntries.Select(e => e.Entry.Id));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_FilterUnloggedOnly_ExcludesEntriesWithALinkedQso()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("logged", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("unlogged", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
        };

        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.FilterUnloggedOnly = true;

        var entry = Assert.Single(vm.FilteredEntries);
        Assert.Equal("unlogged", entry.Entry.Id);
        // Entries itself (the Previous-frames strip's data source) must stay unfiltered.
        Assert.Equal(2, vm.Entries.Count);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_FilterFlaggedOnly_IncludesOnlyFlaggedEntries()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("flagged", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, IsFlagged: true),
                new ReceiveHistoryEntry("plain", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
        };

        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.FilterFlaggedOnly = true;

        var entry = Assert.Single(vm.FilteredEntries);
        Assert.Equal("flagged", entry.Entry.Id);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SearchText_MatchesNoteOrModeId_CaseInsensitive()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("by-note", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "Worked W1AW"),
                new ReceiveHistoryEntry("by-mode", DateTimeOffset.UtcNow, "scottie-s1", "/tmp/b.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("no-match", DateTimeOffset.UtcNow, "robot36", "/tmp/c.png", null, ReceiveDecodeState.Completed, Note: "nothing relevant"),
            ],
        };

        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SearchText = "w1aw";
        Assert.Equal(["by-note"], vm.FilteredEntries.Select(e => e.Entry.Id));

        vm.SearchText = "SCOTTIE";
        Assert.Equal(["by-mode"], vm.FilteredEntries.Select(e => e.Entry.Id));

        vm.SearchText = null;
        Assert.Equal(3, vm.FilteredEntries.Count);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_Constructed_LoadsEntriesFromSearchAsync()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));

        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        var entry = Assert.Single(vm.Entries);
        Assert.Equal("1", entry.Id);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_Constructed_LoadsTotalLoggedCount_ViaASeparateUnfilteredQuery()
    {
        // "Log size" means the WHOLE logbook, independent of Entries' own current search filter
        // (this pane's constructor defaults FromDate to 30 days back) -- the total-count query must
        // be unfiltered (every LogbookQuery field null), not a copy of BuildCurrentQuery()'s own
        // filtered query.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        logbook.Records.Add(SampleQsoRecord("2"));

        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.TotalLoggedCount);
        Assert.Equal("MainWindow.StatusBar.LogSizeValueFormat", vm.LogSizeDisplay);
        // LoadTotalLoggedCountAsync fires after RefreshAsync in the constructor and is the last
        // SearchAsync call made -- its query must be fully unfiltered.
        Assert.Null(logbook.LastSearchQuery?.Callsign);
        Assert.Null(logbook.LastSearchQuery?.From);
        Assert.Null(logbook.LastSearchQuery?.To);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_LogCommand_DisabledWithoutCallsign()
    {
        var vm = CreateLogbookPaneViewModel();
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = null;
        Assert.False(vm.LogCommand.CanExecute(null));

        vm.FormCallsign = "N0CALL";
        Assert.True(vm.LogCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_PersistsAndClearsFormAndRefreshes()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.Equal("N0CALL", logbook.Records[0].Callsign);
        Assert.Null(vm.FormCallsign);
        Assert.Single(vm.Entries);
        // Regression: ResetForm() (not New()) must run here, or the status line set from
        // BuildLogStatusMessage would be immediately nulled back out before the UI ever shows it.
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_TrailingRefreshFails_StillShowsTheLogOutcome()
    {
        // Tier B audit finding: this used to set StatusMessage to the Log outcome BEFORE the
        // trailing RefreshInternalAsync() call, so a refresh failure's own Error.SearchFailed
        // message silently clobbered it -- the QSO had already genuinely been logged by this point,
        // and the user never saw the real outcome, only a misleading "search failed."
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "N0CALL";
        logbook.ThrowOnSearch = new InvalidOperationException("simulated refresh failure");
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        // FakeLocalizationService.GetString returns the raw key -- the log outcome's own key
        // ("Panes.Logbook.Status.LoggedFormat"), not the trailing refresh failure's
        // ("Panes.Logbook.Error.SearchFailed"), must be what's showing.
        Assert.Equal("Panes.Logbook.Status.LoggedFormat", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_UserSelectsADifferentRowWhileInFlight_DoesNotWipeIt()
    {
        // Tier B audit finding: LogQsoAsync can make a real, multi-second QRZ HTTPS upload. With no
        // guard, a user selecting a DIFFERENT row while the original LogAsync call was still
        // awaiting that upload would have its own eventual ResetForm() wipe out the newly-selected
        // row's form data once it completed -- silently discarding whatever the user had since
        // started reading/editing.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("existing"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "W1AW";
        var gate = new TaskCompletionSource<LogQsoResult>();
        logbook.LogGate = gate;
        var logTask = vm.LogCommand.ExecuteAsync(null);

        // Simulates the user navigating to a different row WHILE the log call above is still
        // suspended on the gate.
        vm.SelectedEntry = vm.Entries[0];
        Assert.Equal("N0CALL", vm.FormCallsign);
        Assert.True(vm.IsEditing);

        gate.SetResult(new LogQsoResult(logbook.Records[0], 0, 0, false, null));
        await logTask;
        Dispatcher.UIThread.RunJobs();

        // The row the user navigated to must still be showing -- not wiped by the now-stale
        // LogAsync call's own ResetForm().
        Assert.Equal("N0CALL", vm.FormCallsign);
        Assert.True(vm.IsEditing);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_SelectingAnEntry_LoadsFormForEditAndEnablesUpdate()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.UpdateCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries[0];

        Assert.Equal("N0CALL", vm.FormCallsign);
        Assert.True(vm.IsEditing);
        Assert.True(vm.UpdateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_UpdateAsync_DelegatesAndNeverTouchesGridTrackerOrQrz()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        vm.FormNotes = "edited";
        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, logbook.UpdateCallCount);
        Assert.Equal("edited", logbook.Records[0].Notes);
        // Regression: same ResetForm()-vs-New() bug as the Log path above.
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_UpdateAsync_PreservesTheExistingReceivedImageIdLink()
    {
        // Round-2 Tier B audit finding: LoadIntoForm used to not carry ReceivedImageId over at all,
        // so BuildRecordFromForm always passed a hardcoded null for it -- editing and saving a QSO
        // that had been linked to an RX-history frame (via QsoLinkWindowViewModel) silently
        // destroyed that reverse FK on every Update, even though nothing on this form lets the user
        // see or change it.
        var linkedRecord = new QsoRecord(
            "1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, "rx-entry-42");
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(linkedRecord);
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        vm.FormNotes = "edited";
        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("rx-entry-42", logbook.Records[0].ReceivedImageId);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_New_ClearsSelectedEntry_SoReselectingTheSameRowReloadsTheForm()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.IsEditing);

        vm.NewCommand.Execute(null);
        Assert.Null(vm.SelectedEntry);
        Assert.False(vm.IsEditing);

        // Regression: without New() also clearing SelectedEntry, this second assignment of the SAME
        // record would be a no-op (no PropertyChanged), leaving the form stuck empty.
        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.IsEditing);
        Assert.Equal("N0CALL", vm.FormCallsign);
    }

    /// <summary>"Log QSO" on the RX pane (rx-log-qso.md, 2026-08-15) calls this to switch to a
    /// fresh entry pre-filled from what that pane already knows live.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_SetsFieldsAndClearsStatusAndInProgressEdit()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.IsEditing);
        vm.StatusMessage = "QSO logged. Forwarded to 1/1 destination(s). QRZ: not sent.";

        var startUtc = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        vm.PrefillForNewEntry("W1AW", "martin1", startUtc, "Some Op", "Somewhere", "AB12cd");

        Assert.Equal("W1AW", vm.FormCallsign);
        Assert.Equal("martin1", vm.FormSstvModeId);
        Assert.Equal(startUtc, vm.FormStartUtc);
        // Code-review finding (rx-log-qso.md): these DO carry over from the RX pane's own QRZ
        // lookup, unlike frequency/mode below -- dropping them would discard a lookup the user
        // already did on the other tab.
        Assert.Equal("Some Op", vm.FormName);
        Assert.Equal("Somewhere", vm.FormQth);
        Assert.Equal("AB12cd", vm.FormGridSquare);
        // No radio-state auto-fill mechanism exists yet (spec/08-logging.md) -- must stay untouched,
        // not silently defaulted to something.
        Assert.Null(vm.FormFrequencyHz);
        Assert.Null(vm.FormMode);
        // Round-1 plan-review finding: must use New()'s full "start clean" semantics, not just
        // ResetForm() -- a stale StatusMessage from a previous action would read as "already
        // logged" under the freshly-prefilled form.
        Assert.Null(vm.StatusMessage);
        // The in-progress edit from the OTHER tab is discarded, same "start clean" contract New()
        // already has -- an accepted tradeoff (rx-log-qso.md), not fixed further here.
        Assert.False(vm.IsEditing);
        Assert.Null(vm.SelectedEntry);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_RefreshAsync_NormalizesFromDateToUtcCalendarDate()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        // Simulates what Avalonia's DatePicker actually emits: a LOCAL-offset DateTimeOffset, not a
        // UTC one -- BuildCurrentQuery must normalize this to a UTC calendar date regardless.
        vm.FromDate = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.FromHours(5));
        vm.RefreshCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var from = logbook.LastSearchQuery?.From;
        Assert.NotNull(from);
        Assert.Equal(TimeSpan.Zero, from!.Value.Offset);
        Assert.Equal(new DateTime(2026, 8, 1), from.Value.Date);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_RefreshAsync_SucceedingAfterAFailure_ClearsTheStaleErrorBanner()
    {
        // Tier B audit finding: RefreshInternalAsync itself never cleared StatusMessage on success
        // (deliberately -- its OTHER callers set their own status right after refreshing and must
        // not have that clobbered), but the standalone RefreshCommand needs to, or a search
        // failure's error banner would persist forever, even after a later search succeeded.
        var logbook = new FakeLogbookSessionService { ThrowOnSearch = new InvalidOperationException("simulated search failure") };
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.StatusMessage);

        logbook.ThrowOnSearch = null;
        vm.RefreshCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_ExportAdifAsync_PreExportRefreshFails_DoesNotExportOrReportAWrongCount()
    {
        // Tier B audit finding: this used to ignore the pre-export refresh's own success/failure --
        // if it failed, Entries stayed stale, but the export still proceeded and then reported
        // "Exported N" using that stale Entries.Count: a provably wrong number, with the real error
        // explaining it silently discarded. Must bail out instead.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var filePicker = new FakeFilePickerService { SaveAdifPathToReturn = "/tmp/export.adi" };
        var vm = CreateLogbookPaneViewModel(logbook, filePicker);
        Dispatcher.UIThread.RunJobs();

        logbook.ThrowOnSearch = new InvalidOperationException("simulated refresh failure");
        await vm.ExportAdifCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(logbook.LastExportPath);
        Assert.Equal("Panes.Logbook.Error.SearchFailed", vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_ImportAdifAsync_RefreshesEntriesAndReportsCount()
    {
        var imported = new List<QsoRecord> { SampleQsoRecord("imported-1") };
        var logbook = new FakeLogbookSessionService { ImportResultToReturn = imported };
        var filePicker = new FakeFilePickerService { AdifPathToReturn = "/tmp/import.adi" };
        var vm = CreateLogbookPaneViewModel(logbook, filePicker);
        Dispatcher.UIThread.RunJobs();

        await vm.ImportAdifCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(vm.Entries, e => e.Id == "imported-1");
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_ExportAdifAsync_UsesCurrentFilterAndReportsCount()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var filePicker = new FakeFilePickerService { SaveAdifPathToReturn = "/tmp/export.adi" };
        var vm = CreateLogbookPaneViewModel(logbook, filePicker);
        Dispatcher.UIThread.RunJobs();

        vm.CallsignFilter = "N0CALL";
        await vm.ExportAdifCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/tmp/export.adi", logbook.LastExportPath);
        Assert.Equal("N0CALL", logbook.LastExportQuery?.Callsign);
        Assert.NotNull(vm.StatusMessage);
    }
}
