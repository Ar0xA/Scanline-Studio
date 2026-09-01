using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.Core.Sstv;
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

        Assert.Equal("RadioStatus.FrequencyDisplayFormat", vm.FrequencyDisplay);
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
        //
        // Test-suite fixes phase 1, item 8 (round-2 correction): raising the event directly from
        // this test method previously ran it ON [AvaloniaFact]'s own headless UI thread -- so
        // Dispatcher.UIThread.Post and a direct assignment would have passed identically, proving
        // nothing about marshaling. Task.Run(...).GetAwaiter().GetResult() genuinely raises it from
        // a non-UI thread instead, and the captured CheckAccess() below makes the marshaling claim
        // an explicit assertion rather than an inference from the property just happening to update.
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService());
        Assert.Null(vm.CurrentMode);
        var handlerRanOnUiThread = (bool?)null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.CurrentMode))
            {
                handlerRanOnUiThread = Dispatcher.UIThread.CheckAccess();
            }
        };

        var mode = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        Task.Run(() => sstvSession.RaiseModeDetected(mode)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(mode, vm.CurrentMode);
        Assert.True(handlerRanOnUiThread, "CurrentMode's PropertyChanged handler must run on the UI thread.");
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
    public void WaterfallPaneViewModel_TogglingNotchEnabled_RaisesPropertyChangedForNotchToggleLabel()
    {
        // User-reported regression (2026-08-27): the toggle chip's Content used to be a static
        // "On" loc-key literal in the AXAML, so it never changed regardless of NotchEnabled --
        // fixed by binding it to this new property instead. Same "only a real PropertyChanged
        // subscription proves it" reasoning as the sibling NotchStatusDisplay test above.
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.NotchEnabled = true;

        Assert.Contains(nameof(WaterfallPaneViewModel.NotchToggleLabel), raisedProperties);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_NotchToggleLabel_IsAnActionLabel_OppositeOfCurrentState()
    {
        // User-corrected (2026-08-27, same day as the initial fix): this is an ACTION label (what
        // clicking the chip will DO next), not a state label -- "On" while disabled (click to turn
        // it on), "Off" while enabled (click to turn it off).
        var localization = new FakeLocalizationService();
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), localization);

        _ = vm.NotchToggleLabel;
        Assert.Equal("Panes.RxInput.NotchToggle", localization.LastKey); // "On" -- disabled, click to enable

        vm.NotchEnabled = true;
        _ = vm.NotchToggleLabel;

        Assert.Equal("Panes.RxInput.NotchValue", localization.LastKey); // "Off" -- enabled, click to disable
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_RefreshesImageOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.Image);
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Image);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_UpdatesModeCardTextsOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        Assert.Equal("Panes.RxImage.LineTimeFormat", vm.LineTimeText);
        Assert.Equal("256", vm.LinesText);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RemainingText_NoLockShowsPlaceholder_ThenReflectsProgress()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), logger);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.Empty(sstvSession.StartRecordingCalls);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ToggleRecordingCommand_StartFails_ShowsErrorAndStaysNotRecording()
    {
        var sstvSession = new FakeSstvSessionService { ThrowOnStartRecording = new InvalidOperationException("Cannot start recording while not receiving.") };
        var filePicker = new FakeFilePickerService { SaveWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.ToggleRecordingCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecording);
        Assert.NotNull(vm.RecordErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeCommand_DecodesThePickedFile()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { OpenWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeCommand.ExecuteAsync(null);

        Assert.Empty(sstvSession.DecodeFromFileCalls);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeCommand_DecodeFails_ShowsError()
    {
        var sstvSession = new FakeSstvSessionService { ThrowOnDecodeFromFile = new InvalidOperationException("File sample rate mismatch.") };
        var filePicker = new FakeFilePickerService { OpenWavPathToReturn = "/tmp/chosen.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeCommand.ExecuteAsync(null);

        Assert.NotNull(vm.RedecodeErrorMessage);
        Assert.False(vm.IsRedecoding);
    }

    // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: Gallery/RX-details "Re-decode this
    // frame" entry point -- shares DecodeFileAsync with RedecodeCommand above (see that method's own
    // doc comment), so these pin the two behaviors specific to this second entry point: no
    // file-picker step, and the real failure reason (not a generic message) for a documented
    // InvalidOperationException.

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeFromPathCommand_DecodesTheGivenPath_NoFilePickerInvolved()
    {
        var sstvSession = new FakeSstvSessionService();
        var filePicker = new FakeFilePickerService { OpenWavPathToReturn = "/tmp/should-not-be-used.wav" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), filePicker, new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeFromPathCommand.ExecuteAsync("/tmp/from-history.wav");

        Assert.Null(vm.RedecodeErrorMessage);
        Assert.False(vm.IsRedecoding);
        Assert.Equal(["/tmp/from-history.wav"], sstvSession.DecodeFromFileCalls);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeFromPathCommand_InvalidOperationException_ShowsTheRealReasonNotAGenericMessage()
    {
        var sstvSession = new FakeSstvSessionService { ThrowOnDecodeFromFile = new InvalidOperationException("Cannot decode a file while auto-detect is paused.") };
        var localization = new FakeLocalizationService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeFromPathCommand.ExecuteAsync("/tmp/from-history.wav");

        Assert.Equal("Panes.RxImage.Error.RedecodeFailedWithReason", vm.RedecodeErrorMessage);
        Assert.Equal(["Cannot decode a file while auto-detect is paused."], localization.LastArgs);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RedecodeFromPathCommand_NonInvalidOperationException_ShowsTheGenericMessage()
    {
        // A raw I/O failure's own Message is not written to be operator-facing the way the
        // documented InvalidOperationException reasons are -- see DecodeFileAsync's own doc comment.
        var sstvSession = new FakeSstvSessionService { ThrowOnDecodeFromFile = new IOException("Disk read error at offset 0x4000.") };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        await vm.RedecodeFromPathCommand.ExecuteAsync("/tmp/from-history.wav");

        Assert.Equal("Panes.RxImage.Error.RedecodeFailed", vm.RedecodeErrorMessage);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.Empty(vm.PreviousFrames);

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();

        var frame = Assert.Single(vm.PreviousFrames);
        Assert.Equal("entry1", frame.Entry.Id);
        Assert.NotNull(frame.Thumbnail);
    }

    /// <summary>ui_transition_plan.md step 6 (T2-4) -- the exact regression shape the plan item
    /// itself specifies: "station A frame, retune, station B frame -- A's stored frequency
    /// unchanged." "Retune" here means station B's OnHistoryRecorded latches a DIFFERENT
    /// FrequencyHz/RigMode than A's, proving the display reflects the CURRENT frame's own entry,
    /// never live radio state re-read later, and that A's own already-recorded row is untouched by
    /// B ever completing.</summary>
    [AvaloniaFact]
    public async Task LatchedFrequencyDisplay_StationAThenRetuneThenStationB_EachFrameKeepsItsOwnFrequency()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var modeA = TestMode;
        var receivedImage = (FakeReceivedImageBuffer)sstvSession.ReceivedImage;

        Assert.Equal("—", vm.LatchedFrequencyDisplay);

        // Station A completes on 14.230000 MHz USB.
        sstvSession.RaiseModeDetected(modeA);
        Dispatcher.UIThread.RunJobs();
        receivedImage.RaiseSaved("/tmp/stationA.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();
        var entryA = new ReceiveHistoryEntry("a", DateTimeOffset.UtcNow, modeA.Id, "/tmp/stationA.png", null, ReceiveDecodeState.Completed, FrequencyHz: 14_230_000, RigMode: RadioMode.Usb);
        await historyStore.RecordAsync(entryA);
        Dispatcher.UIThread.RunJobs();

        // T1-13 (production_audit.md): now routes through FakeLocalizationService, which echoes
        // the raw key -- not a formatted string. Same convention as RadioStatusViewModelTests.cs.
        Assert.Equal("Panes.RxImage.LatchedFrequencyWithModeFormat", vm.LatchedFrequencyDisplay);

        // A new reception starts (the operator retunes in between) -- must go back to "—", not
        // keep showing A's frequency, until B's own entry actually lands.
        sstvSession.RaiseModeDetected(modeA);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("—", vm.LatchedFrequencyDisplay);

        // Station B completes on a different frequency/mode.
        receivedImage.RaiseSaved("/tmp/stationB.png", receivedImage.Generation);
        Dispatcher.UIThread.RunJobs();
        var entryB = new ReceiveHistoryEntry("b", DateTimeOffset.UtcNow, modeA.Id, "/tmp/stationB.png", null, ReceiveDecodeState.Completed, FrequencyHz: 7_171_000, RigMode: RadioMode.Lsb);
        await historyStore.RecordAsync(entryB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxImage.LatchedFrequencyWithModeFormat", vm.LatchedFrequencyDisplay);

        // A's own row, independently, is untouched by B ever completing.
        var storedA = historyStore.RecordedEntries.Single(e => e.Id == "a");
        Assert.Equal(14_230_000, storedA.FrequencyHz);
        Assert.Equal(RadioMode.Usb, storedA.RigMode);
    }

    // ui_transition_plan.md step 3 (T1-5): full-size viewer entry points off this pane
    // (live incoming-frame double-tap and a Previous-Frames thumbnail double-tap).

    [AvaloniaFact]
    public void OpenImageViewerCommand_WithNoDependenciesSupplied_IsASilentNoOp()
    {
        // The 3 trailing optional constructor params are omitted here -- same convention as
        // TxImageEditorPaneViewModel's own canTransmitNow default, so this class's ~100 other
        // direct-construction test call sites don't need updating for a feature they don't
        // exercise.
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        Assert.NotEmpty(vm.PreviousFrames);

        var raised = false;
        vm.ImageViewerRequested += _ => raised = true;
        vm.OpenImageViewerCommand.Execute(null);

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void OpenImageViewerCommand_BeforeAnyReceptionCompleted_IsASilentNoOp()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(
            sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance,
            new FakeUrlLauncher(), new FakeClipboardImageService(), NullLogger<ImageViewerWindowViewModel>.Instance);
        Assert.Empty(vm.PreviousFrames);

        var raised = false;
        vm.ImageViewerRequested += _ => raised = true;
        vm.OpenImageViewerCommand.Execute(null);

        Assert.False(raised);
    }

    [AvaloniaFact]
    public void OpenImageViewerCommand_NullStartEntry_OpensAtTheMostRecentFrame()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(
            sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance,
            new FakeUrlLauncher(), new FakeClipboardImageService(), NullLogger<ImageViewerWindowViewModel>.Instance);
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("older", DateTimeOffset.UtcNow.AddMinutes(-1), "sc1", "/tmp/older.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("newer", DateTimeOffset.UtcNow, "sc1", "/tmp/newer.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        // PreviousFrames is sorted most-recent-first -- see that property's own doc comment.
        Assert.Equal(["newer", "older"], vm.PreviousFrames.Select(f => f.Entry.Id));

        ImageViewerWindowViewModel? requested = null;
        vm.ImageViewerRequested += viewerVm => requested = viewerVm;
        vm.OpenImageViewerCommand.Execute(null);

        Assert.NotNull(requested);
        Assert.Equal(0, requested!.CurrentIndex);
        Assert.Equal("newer", requested.Current!.Entry.Id);
    }

    [AvaloniaFact]
    public void OpenImageViewerCommand_WithASpecificEntry_OpensAtThatEntrysIndex()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(
            sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance,
            new FakeUrlLauncher(), new FakeClipboardImageService(), NullLogger<ImageViewerWindowViewModel>.Instance);
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("older", DateTimeOffset.UtcNow.AddMinutes(-1), "sc1", "/tmp/older.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();
        historyStore.RaiseRecorded(new ReceiveHistoryEntry("newer", DateTimeOffset.UtcNow, "sc1", "/tmp/newer.png", null, ReceiveDecodeState.Completed));
        Dispatcher.UIThread.RunJobs();

        ImageViewerWindowViewModel? requested = null;
        vm.ImageViewerRequested += viewerVm => requested = viewerVm;
        vm.OpenImageViewerCommand.Execute(vm.PreviousFrames.Single(f => f.Entry.Id == "older"));

        Assert.NotNull(requested);
        Assert.Equal("older", requested!.Current!.Entry.Id);
    }

    [AvaloniaFact]
    public void PreviousFrames_AbandonedEntryRecorded_IsNotAdded()
    {
        // An aborted/partial attempt isn't what an operator means by "a previous frame."
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        historyStore.RaiseRecorded(new ReceiveHistoryEntry("entry1", DateTimeOffset.UtcNow, "sc1", "/tmp/frame1.png", null, ReceiveDecodeState.Abandoned));
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(vm.PreviousFrames);
    }

    [AvaloniaFact]
    public void PreviousFrames_MoreThanCapacityRecorded_KeepsOnlyTheNewestTwo()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.NotEqual("—", vm.SlantPpmDisplay);
        Assert.NotEqual("—", vm.SyncOffsetSamplesDisplay);
        Assert.NotEqual("—", vm.SyncToneDisplay);
        Assert.Equal("Panes.RxSync.AutoCorrectValue.Locked", vm.AutoCorrectDisplay);
        Assert.Equal("Panes.RxInput.ClippingValue.Overdriven", vm.ClippingDisplay);
        Assert.Equal("Panes.RxSync.SourceValue.Locked", vm.SyncSourceDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_PollTelemetry_PollsIsAudioAutoSaveActive()
    {
        // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: real, varying backing for the
        // main-window status bar's auto-save-audio chip -- see ISstvSessionService.
        // IsAudioAutoSaveActive's own doc comment for why this must poll a real property, not
        // reflect the Options enable toggle alone.
        var sstvSession = new FakeSstvSessionService { IsAudioAutoSaveActive = false };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        Assert.False(vm.IsAudioAutoSaveActive);

        sstvSession.IsAudioAutoSaveActive = true;
        vm.PollTelemetry();
        Assert.True(vm.IsAudioAutoSaveActive);
    }

    [AvaloniaTheory]
    [InlineData(SstvSyncSource.Idle, "Panes.RxSync.SourceValue.Idle")]
    [InlineData(SstvSyncSource.Locked, "Panes.RxSync.SourceValue.Locked")]
    [InlineData(SstvSyncSource.AvtTraining, "Panes.RxSync.SourceValue.AvtTraining")]
    public void RxImagePaneViewModel_PollTelemetry_SyncSourceDisplay_ReflectsAllThreeStates(SstvSyncSource source, string expectedKey)
    {
        var sstvSession = new FakeSstvSessionService { SyncSource = source };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.Equal(expectedKey, vm.SyncSourceDisplay);
    }

    [AvaloniaTheory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(99, 0)] // out-of-range construction-time seed clamps to 0 ("Very low"), same as every other layer
    public void RxImagePaneViewModel_SenseLevel_ConstructionTimeRead_ClampsOutOfRange(int sessionSenseLevel, int expectedSenseLevel)
    {
        var sstvSession = new FakeSstvSessionService { SenseLevel = sessionSenseLevel };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(expectedSenseLevel, vm.SenseLevel);
        Assert.Equal(4, vm.SenseLevelOptions.Count);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_SettingSenseLevel_AppliesLiveAndPersists()
    {
        var sstvSession = new FakeSstvSessionService { SenseLevel = 1 };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.SenseLevel = 3;
        await Task.Delay(1); // let the fire-and-forget persist actually run

        Assert.Equal(3, vm.SenseLevel);
        Assert.Equal(1, sstvSession.RequestSenseLevelCallCount);
        Assert.Equal(3, sstvSession.LastRequestedSenseLevel);
        Assert.Equal(1, sstvSession.PersistSenseLevelCallCount);
        Assert.Equal(3, sstvSession.LastPersistedSenseLevel);
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task RxImagePaneViewModel_SettingSenseLevelOutOfRange_RevertsWithoutApplyingOrPersisting(int outOfRangeValue)
    {
        // Real-app regression this guards against: a ComboBox's SelectedIndex briefly going -1 when
        // its selection is cleared must not silently drop the squelch level to "Very low" and
        // PERSIST that -- see OnSenseLevelChanged's own doc comment.
        var sstvSession = new FakeSstvSessionService { SenseLevel = 2 };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.SenseLevel = outOfRangeValue;
        await Task.Delay(1);

        Assert.Equal(2, vm.SenseLevel); // reverted to the last valid value
        // The revert itself re-applies/re-persists the SAME (already-correct) value EXACTLY once --
        // see OnSenseLevelChanged's own doc comment for why that's accepted -- but the invalid value
        // itself must never be the one forwarded or persisted. Asserting the CALL COUNT (not just
        // the Last* value) matters here: a buggy implementation that forwards the invalid value
        // first and THEN reverts would also end on LastRequestedSenseLevel == 2, but would call
        // RequestSenseLevel/PersistSenseLevelAsync twice, not once (auditor round-1 finding).
        Assert.Equal(1, sstvSession.RequestSenseLevelCallCount);
        Assert.Equal(2, sstvSession.LastRequestedSenseLevel);
        Assert.Equal(1, sstvSession.PersistSenseLevelCallCount);
        Assert.Equal(2, sstvSession.LastPersistedSenseLevel);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_PersistSenseLevelAsync_WhenItThrows_LogsInsteadOfCrashing()
    {
        var sstvSession = new FakeSstvSessionService { SenseLevel = 1, PersistSenseLevelException = new InvalidOperationException("boom") };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.SenseLevel = 3;
        await Task.Delay(1);

        Assert.Equal(3, vm.SenseLevel); // live-apply still happened; only the persist failed
        Assert.Equal(1, sstvSession.RequestSenseLevelCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RefreshSenseLevelFromSession_PicksUpAnOptionsWindowChange()
    {
        // Options' own Squelch level control now ALSO applies live (OptionsWindowViewModel's save
        // flow) -- this is the Receive tab's own re-sync, called from the app's Options-Closed
        // refresh hook (MainWindow.axaml.cs), same convention as every other refresh-on-close call
        // there.
        var sstvSession = new FakeSstvSessionService { SenseLevel = 1 };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.Equal(1, vm.SenseLevel);

        // Simulates Options changing the value out from under this VM (its own save flow calling
        // RequestSenseLevel, which the fake also reflects onto its own SenseLevel property).
        sstvSession.SenseLevel = 3;

        vm.RefreshSenseLevelFromSession();

        Assert.Equal(3, vm.SenseLevel);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RefreshSenseLevelFromSession_WhenUnchanged_IsANoOp()
    {
        var sstvSession = new FakeSstvSessionService { SenseLevel = 2 };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RefreshSenseLevelFromSession();
        await Task.Delay(1);

        // The generated property setter's own equality check skips OnSenseLevelChanged entirely when
        // the value doesn't actually change -- no redundant live-apply/persist.
        Assert.Equal(0, sstvSession.RequestSenseLevelCallCount);
        Assert.Equal(0, sstvSession.PersistSenseLevelCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RefreshAutoSlantEnabledFromSession_PicksUpAnOptionsWindowChange()
    {
        // Restart-required-settings backlog item 1 (2026-08-27): AutoSlantEnabled is now ALSO live
        // via Options -- same reasoning and refresh-on-Options-Closed convention as
        // RefreshSenseLevelFromSession above. This is what keeps the Receive tab's "Auto-correct"
        // status text from going stale after an Options-driven change.
        var sstvSession = new FakeSstvSessionService { AutoSlantEnabled = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.True(vm.AutoSlantEnabled);

        // Simulates Options changing the value out from under this VM (its own save flow calling
        // RequestAutoSlantEnabled, which the fake also reflects onto its own AutoSlantEnabled property).
        sstvSession.AutoSlantEnabled = false;

        vm.RefreshAutoSlantEnabledFromSession();

        Assert.False(vm.AutoSlantEnabled);
        // AutoCorrectDisplay reads AutoSlantEnabled directly -- proves the refresh's value actually
        // reaches that computed property (FakeLocalizationService.GetString echoes its key back
        // unchanged, so this pins the exact locale key AutoCorrectDisplay's "off" branch reads).
        Assert.Equal("Panes.RxSync.AutoCorrectValue.Off", vm.AutoCorrectDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RefreshRxBpfPresetFromSession_PicksUpADecoderInstanceReplacedEvent()
    {
        // Restart-required-settings backlog item 2 (2026-08-27): RxBpfPreset is now ALSO live, but
        // refreshed by ISstvSessionService.DecoderInstanceReplaced (a decoder swap actually
        // happening), NOT the Options window's own Closed event AutoSlantEnabled/SenseLevel use above
        // -- see RxBpfPreset's own doc comment for why a pull-based Closed-event refresh would be
        // redundant here. Fires synchronously on (what production treats as) the audio drain thread,
        // so the handler must marshal via Dispatcher.UIThread.Post -- this proves that marshaling
        // actually reaches the bound property, not just that the underlying field changed.
        //
        // Test-suite fixes phase 1, item 8 (round-2 correction): raising the event directly from
        // this test method previously ran it ON [AvaloniaFact]'s own headless UI thread -- so
        // Dispatcher.UIThread.Post and a direct assignment would have passed identically, proving
        // nothing about marshaling. Task.Run(...).GetAwaiter().GetResult() genuinely raises it from
        // a non-UI thread instead, and the captured CheckAccess() below makes the marshaling claim
        // an explicit assertion rather than an inference from the property just happening to update.
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Wide };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset);
        var handlerRanOnUiThread = (bool?)null;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.RxBpfPreset))
            {
                handlerRanOnUiThread = Dispatcher.UIThread.CheckAccess();
            }
        };

        // Simulates a queued RequestReconfiguration finally applying on the decoder's next idle swap.
        sstvSession.RxBpfPreset = RxBpfPreset.Narrow;
        Task.Run(() => sstvSession.RaiseDecoderInstanceReplaced()).GetAwaiter().GetResult();

        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBpfPreset.Narrow, vm.RxBpfPreset);
        Assert.Equal("Options.Decode.RxBpf.Sharp", vm.RxBpfDisplay);
        Assert.True(handlerRanOnUiThread, "RxBpfPreset's PropertyChanged handler must run on the UI thread.");
    }

    [AvaloniaTheory]
    [InlineData(RxBpfPreset.Off, "Options.Decode.RxBpf.Normal")]
    [InlineData(RxBpfPreset.Wide, "Options.Decode.RxBpf.Wide")]
    [InlineData(RxBpfPreset.Narrow, "Options.Decode.RxBpf.Sharp")]
    [InlineData(RxBpfPreset.VeryNarrow, "Options.Decode.RxBpf.VerySharp")]
    public void RxImagePaneViewModel_RxBpfDisplay_ReflectsRxBpfPreset_ConstructionTimeRead(RxBpfPreset preset, string expectedKey)
    {
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = preset };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(expectedKey, vm.RxBpfDisplay);
    }

    // Restart-required-settings backlog item 6 (RX BPF Receive-tab live dropdown, 2026-08-28) --
    // same shapes as the SenseLevel tests above, but proving RequestRxBpfPreset is called (a
    // per-field-merge sibling of RequestReconfiguration, never that combined call), and with the
    // corrected (round-2 plan-review) out-of-range/redundant-refresh expectations documented on
    // RxBpfPresetIndex/OnRxBpfPresetChanged's own doc comments.

    [AvaloniaFact]
    public void RxImagePaneViewModel_RxBpfPresetIndex_ConstructionTimeRead_MatchesEnumOrdinal()
    {
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Narrow };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal((int)RxBpfPreset.Narrow, vm.RxBpfPresetIndex);
        Assert.Equal(4, vm.RxBpfPresetOptions.Count);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_SettingRxBpfPresetIndex_AppliesLiveAndPersists()
    {
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Wide };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RxBpfPresetIndex = (int)RxBpfPreset.VeryNarrow;
        await Task.Delay(1); // let the fire-and-forget persist actually run

        Assert.Equal(RxBpfPreset.VeryNarrow, vm.RxBpfPreset);
        Assert.Equal((int)RxBpfPreset.VeryNarrow, vm.RxBpfPresetIndex);
        Assert.Equal(1, sstvSession.RequestRxBpfPresetCallCount);
        Assert.Equal(RxBpfPreset.VeryNarrow, sstvSession.LastRequestedRxBpfPreset);
        Assert.Equal(1, sstvSession.PersistRxBpfPresetCallCount);
        Assert.Equal(RxBpfPreset.VeryNarrow, sstvSession.LastPersistedRxBpfPreset);
    }

    [AvaloniaTheory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task RxImagePaneViewModel_SettingRxBpfPresetIndexOutOfRange_RevertsWithoutApplyingOrPersisting(int outOfRangeValue)
    {
        // Round-2 plan-review correction: unlike SenseLevel's own out-of-range guard (which reverts
        // THROUGH its own canonical property, causing one redundant re-apply/re-persist),
        // RxBpfPresetIndex's guard never touches RxBpfPreset at all -- there is no valid enum value
        // to revert through. Zero RequestRxBpfPreset/persist calls must result, not one.
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Narrow };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        // Code-review round-1 finding: the snapback mechanism itself (re-raising PropertyChanged for
        // RxBpfPresetIndex so a real bound ComboBox visually snaps back) was previously unproven --
        // the call-count/getter assertions below would stay green even if that re-raise were deleted,
        // since RxBpfPresetIndex's getter always reflects the unchanged RxBpfPreset field regardless.
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.RxBpfPresetIndex = outOfRangeValue;
        await Task.Delay(1);

        Assert.Contains(nameof(RxImagePaneViewModel.RxBpfPresetIndex), raisedProperties);

        Assert.Equal((int)RxBpfPreset.Narrow, vm.RxBpfPresetIndex); // snapped back to the last real value
        Assert.Equal(RxBpfPreset.Narrow, vm.RxBpfPreset);
        Assert.Equal(0, sstvSession.RequestRxBpfPresetCallCount);
        Assert.Equal(0, sstvSession.PersistRxBpfPresetCallCount);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_PersistRxBpfPresetAsync_WhenItThrows_LogsInsteadOfCrashing()
    {
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Wide, PersistRxBpfPresetException = new InvalidOperationException("boom") };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RxBpfPresetIndex = (int)RxBpfPreset.Narrow;
        await Task.Delay(1);

        Assert.Equal(RxBpfPreset.Narrow, vm.RxBpfPreset); // live-apply still happened; only the persist failed
        Assert.Equal(1, sstvSession.RequestRxBpfPresetCallCount);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RefreshRxBpfPresetFromSession_ReceiveTabOriginated_DoesNotReapply()
    {
        // Round-2 plan-review correction of an inverted round-1 claim: when THIS row originated the
        // change, RxBpfPreset already holds the new value by the time a post-swap refresh runs -- the
        // generated property setter's own equality check skips OnRxBpfPresetChanged entirely, so no
        // redundant RequestRxBpfPreset/persist call results.
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Wide };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RxBpfPresetIndex = (int)RxBpfPreset.Narrow;
        await Task.Delay(1);
        Assert.Equal(1, sstvSession.RequestRxBpfPresetCallCount); // sanity: the initial change did apply

        // Simulates the queued change actually landing (the fake's own RequestRxBpfPreset does NOT
        // auto-update RxBpfPreset, matching real idle-gated semantics) -- the swap makes the session
        // agree with what this VM already holds.
        sstvSession.RxBpfPreset = RxBpfPreset.Narrow;
        sstvSession.RaiseDecoderInstanceReplaced();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBpfPreset.Narrow, vm.RxBpfPreset);
        Assert.Equal(1, sstvSession.RequestRxBpfPresetCallCount); // unchanged -- no redundant re-fire
        Assert.Equal(1, sstvSession.PersistRxBpfPresetCallCount); // unchanged
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RefreshRxBpfPresetFromSession_OptionsOriginated_ReappliesOnceHarmlessly()
    {
        // The genuine redundant-refresh case: Options changed BPF preset (this VM's own field is
        // stale), so the post-swap refresh assigns a DIFFERENT value -- OnRxBpfPresetChanged DOES
        // fire once more here, a documented-accepted no-op re-apply/re-persist (same accepted cost as
        // SenseLevel's own revert case).
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Wide };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset);

        // Simulates an Options Save changing BPF preset out from under this VM.
        sstvSession.RxBpfPreset = RxBpfPreset.VeryNarrow;
        sstvSession.RaiseDecoderInstanceReplaced();
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(1);

        Assert.Equal(RxBpfPreset.VeryNarrow, vm.RxBpfPreset);
        Assert.Equal(1, sstvSession.RequestRxBpfPresetCallCount); // the redundant, harmless re-apply
        Assert.Equal(RxBpfPreset.VeryNarrow, sstvSession.LastRequestedRxBpfPreset);
        Assert.Equal(1, sstvSession.PersistRxBpfPresetCallCount); // the redundant, harmless re-persist
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ReconfigurationRejected_RevertsToWhatIsActuallyApplied()
    {
        // The BPF row is now a live EDITOR (restart-required-settings backlog item 6) -- without
        // this, a rejected Receive-tab-originated change would leave the row permanently showing a
        // preset that was requested but never took effect.
        var sstvSession = new FakeSstvSessionService { RxBpfPreset = RxBpfPreset.Wide };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RxBpfPresetIndex = (int)RxBpfPreset.VeryNarrow; // requested, but never actually applied
        // sstvSession.RxBpfPreset deliberately left at Wide -- the swap never happened.

        sstvSession.RaiseReconfigurationRejected();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RxBpfPreset.Wide, vm.RxBpfPreset); // reverted to what's actually applied
        Assert.Equal((int)RxBpfPreset.Wide, vm.RxBpfPresetIndex);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_BufferedSampleCountStatusBarDisplay_ReflectsSessionValue()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { BufferedSampleCount = 1583, CaptureOverrunCount = 7 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        _ = vm.AgcGainDisplay;

        Assert.Equal(512.0, (double)localization.LastArgs[0], precision: 6);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_Constructed_LoadsTheConfiguredCaptureDeviceName()
    {
        var sstvSession = new FakeSstvSessionService { ConfiguredCaptureDeviceName = "hw:2,0 L" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("hw:2,0 L", vm.CaptureDeviceName);
        Assert.Equal("hw:2,0 L", vm.CaptureDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_NoCaptureDeviceConfigured_CaptureDeviceNameDisplayShowsPlaceholder()
    {
        var sstvSession = new FakeSstvSessionService { ConfiguredCaptureDeviceName = null };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.CaptureDeviceName);
        Assert.Equal("—", vm.CaptureDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SavedEvent_SetsFileSizeDisplayFromTheActualFileOnDisk()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.Progress);
        Assert.Equal("—", vm.ClipLoHiDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RequestReSyncCommand_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RequestReSyncCommand.Execute(null);

        Assert.Equal(1, sstvSession.RequestReSyncCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RequestCorrectSlantCommand_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RequestCorrectSlantCommand.Execute(null);

        Assert.Equal(1, sstvSession.RequestCorrectSlantCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AbortCommand_WhileReceiving_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.AbortCommand.Execute(null);

        Assert.Equal(1, sstvSession.AbortReceptionCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_AbortCommand_WhileNotReceiving_IsASafeNoOp()
    {
        var sstvSession = new FakeSstvSessionService { IsReceiving = false };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.AbortCommand.Execute(null);

        Assert.Equal(0, sstvSession.AbortReceptionCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickSelectMode_WhileReceiving_CallsForceMode()
    {
        // spec/18-path-to-1.0.md High item 7.
        var mode = TestMode;
        var sstvSession = new FakeSstvSessionService { AvailableModes = [mode], IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.QuickSelectModeCommand.Execute(mode.Id);

        Assert.Equal(0, sstvSession.ForceModeCallCount);
    }

    /// <summary>ui_transition_plan.md step 10 (T2-5): persistent RX mode lock -- explicit target via
    /// the quick-mode grid's own "Hold this mode" context-menu entry, not "whatever was last
    /// quick-selected" or "whatever is currently detected" (both rejected in plan-review: the first
    /// is undefined while not receiving, the second is never nulled at end-of-reception and would
    /// hold a stale value).</summary>
    [AvaloniaFact]
    public void RxImagePaneViewModel_HoldModeCommand_SetsHeldModeAndCallsSetModeLock()
    {
        var mode = TestMode;
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.HoldModeCommand.Execute(mode);

        Assert.Equal(mode.Id, vm.HeldMode?.Id);
        Assert.Equal(1, sstvSession.SetModeLockCallCount);
        Assert.Equal(mode.Id, sstvSession.LastLockedMode?.Id);
        // FakeLocalizationService.GetString echoes the raw key -- the real interpolated text isn't
        // asserted here (see PaneViewModelTests' own established convention for this fake).
        Assert.Equal("Panes.RxImage.QuickMode.HoldingValue", vm.HeldModeDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ReleaseHeldModeCommand_ClearsHeldModeAndCallsSetModeLockNull()
    {
        var mode = TestMode;
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.HoldModeCommand.Execute(mode);
        Assert.True(vm.ReleaseHeldModeCommand.CanExecute(null));

        vm.ReleaseHeldModeCommand.Execute(null);

        Assert.Null(vm.HeldMode);
        Assert.Null(vm.HeldModeDisplay);
        Assert.Equal(2, sstvSession.SetModeLockCallCount);
        Assert.Null(sstvSession.LastLockedMode);
    }

    /// <summary>Code-review finding: ISstvSessionService.SetModeLock's own doc comment documents AVT
    /// as an unsupported lock target (locking to it takes Commit's own AVT branch for a signal that
    /// isn't AVT-shaped, skipping anchor correction/AFC/slant entirely) -- nothing enforced this
    /// before the fix, so every one of the 43 reassignment entries, AVT included, was reachable via
    /// "Hold this mode" with no gate at all.</summary>
    [AvaloniaFact]
    public void RxImagePaneViewModel_HoldModeCommand_AvtMode_CannotExecute()
    {
        var avtMode = TestMode;
        var sstvSession = new FakeSstvSessionService { VisHeaderInfoToReturn = (VisHeaderKind.Avt, 0) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.False(vm.HoldModeCommand.CanExecute(avtMode));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_HoldModeCommand_NonAvtMode_CanExecute()
    {
        var sstvSession = new FakeSstvSessionService { VisHeaderInfoToReturn = (VisHeaderKind.Standard, 8) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.True(vm.HoldModeCommand.CanExecute(TestMode));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ReleaseHeldModeCommand_NothingHeld_CannotExecute()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.HeldModeDisplay);
        Assert.False(vm.ReleaseHeldModeCommand.CanExecute(null));
    }

    /// <summary>Each quick-mode slot's own HoldModeCommand must be the SAME captured-once-at-
    /// construction command as the parent's (same pattern as SelectCommand/ReassignCommand) -- not a
    /// cross-DataTemplate binding path.</summary>
    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickModeSlots_ShareTheSameHoldModeCommandInstance()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.NotEmpty(vm.QuickModeSlots);
        Assert.All(vm.QuickModeSlots, slot => Assert.Same(vm.HoldModeCommand, slot.HoldModeCommand));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickSelectMode_UnknownModeId_IsASafeNoOp()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.QuickSelectModeCommand.Execute("no-such-mode");

        Assert.Equal(0, sstvSession.ForceModeCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_IsAutoDetectPaused_Toggle_CallsSetAutoDetectPausedOnSession()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.IsAutoDetectPaused = true;

        vm.QuickSelectModeCommand.Execute(mode.Id);

        Assert.False(vm.IsAutoDetectPaused);
        Assert.False(sstvSession.IsAutoDetectPaused);
        Assert.Equal(1, sstvSession.ForceModeCallCount);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickModeSlots_DefaultsTo16SlotsFromQuickModeGridDefaults()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(16, vm.QuickModeSlots.Count);
        for (var i = 0; i < QuickModeGridDefaults.Ids.Count; i++)
        {
            Assert.Equal(QuickModeGridDefaults.Ids[i], vm.QuickModeSlots[i].CurrentMode.Id);
            Assert.Equal(43, vm.QuickModeSlots[i].MenuEntries.Count);
        }
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickModeSlots_LoadsAValidPersistedAssignment()
    {
        var settingsStore = new FakeSettingsStore();
        var customIds = QuickModeGridDefaults.Ids.Reverse().ToArray(); // any valid permutation, distinct from the defaults
        settingsStore.Settings = settingsStore.Settings.WithSection(RxPaneUiSettings.SectionKey, new RxPaneUiSettings { QuickModeGridIds = customIds }, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };

        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), settingsStore, NullLogger<RxImagePaneViewModel>.Instance);

        for (var i = 0; i < customIds.Length; i++)
        {
            Assert.Equal(customIds[i], vm.QuickModeSlots[i].CurrentMode.Id);
        }
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_QuickModeSlots_InvalidPersistedList_FallsBackPerSlotWithoutDuplicates()
    {
        // Wrong length (not 16) -> QuickModeGridAssignment.Resolve treats it as "no persisted list",
        // same as a fresh install -- must not throw and must not duplicate any mode across slots.
        var settingsStore = new FakeSettingsStore();
        settingsStore.Settings = settingsStore.Settings.WithSection(RxPaneUiSettings.SectionKey, new RxPaneUiSettings { QuickModeGridIds = ["not-a-real-id", "scottie-s1"] }, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };

        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), settingsStore, NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(16, vm.QuickModeSlots.Count);
        Assert.Equal(16, vm.QuickModeSlots.Select(s => s.CurrentMode.Id).Distinct().Count());
        for (var i = 0; i < QuickModeGridDefaults.Ids.Count; i++)
        {
            Assert.Equal(QuickModeGridDefaults.Ids[i], vm.QuickModeSlots[i].CurrentMode.Id);
        }
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ReassignQuickModeSlot_UpdatesCurrentModeAndPersists()
    {
        var settingsStore = new FakeSettingsStore();
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), settingsStore, NullLogger<RxImagePaneViewModel>.Instance);
        var newMode = SstvModeRegistry.Mn73; // not one of the default 16

        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, newMode));

        Assert.Equal(newMode.Id, vm.QuickModeSlots[0].CurrentMode.Id);
        Assert.Equal("mn73", vm.QuickModeSlots[0].CurrentMode.Id);

        var persisted = settingsStore.Settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        Assert.Equal(newMode.Id, persisted!.QuickModeGridIds[0]);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ReassignQuickModeSlot_ToModeAlreadyUsedElsewhere_IsASafeNoOp()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var slot1Mode = vm.QuickModeSlots[1].CurrentMode; // already assigned to slot 1

        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, slot1Mode));

        Assert.Equal(QuickModeGridDefaults.Ids[0], vm.QuickModeSlots[0].CurrentMode.Id); // unchanged
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ReassignQuickModeSlot_UpdatesOtherSlotsMenuEntryStates()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var oldMode = vm.QuickModeSlots[0].CurrentMode; // scottie-s1, freed up by the reassignment below
        var newMode = SstvModeRegistry.Mn73;

        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, newMode));

        // The old mode is now unclaimed -- every OTHER slot's popup should offer it again.
        var slot1EntryForOldMode = vm.QuickModeSlots[1].MenuEntries.Single(e => e.Mode.Id == oldMode.Id);
        Assert.True(slot1EntryForOldMode.IsEnabled);
        Assert.False(slot1EntryForOldMode.IsChecked);

        // The new mode is now claimed by slot 0 -- every OTHER slot's popup should gray it out.
        var slot1EntryForNewMode = vm.QuickModeSlots[1].MenuEntries.Single(e => e.Mode.Id == newMode.Id);
        Assert.False(slot1EntryForNewMode.IsEnabled);
        Assert.False(slot1EntryForNewMode.IsChecked);

        // Slot 0's own entry for its new mode is checked and (as the current holder) still enabled.
        var slot0EntryForNewMode = vm.QuickModeSlots[0].MenuEntries.Single(e => e.Mode.Id == newMode.Id);
        Assert.True(slot0EntryForNewMode.IsEnabled);
        Assert.True(slot0EntryForNewMode.IsChecked);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_QuickSelectModeCommand_StillWorksWithAReassignedSlotsId()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All, IsReceiving = true };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var newMode = SstvModeRegistry.Mn73;
        await vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, newMode));

        vm.QuickSelectModeCommand.Execute(vm.QuickModeSlots[0].CurrentMode.Id);

        Assert.Equal(1, sstvSession.ForceModeCallCount);
        Assert.Equal(newMode.Id, sstvSession.LastForcedMode?.Id);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_ReassignQuickModeSlot_WhileLoadIsPending_AwaitsTheLoad_ThenAppliesCleanly()
    {
        // Round-2 plan-review fix: reassigning before the persisted grid finishes loading must not
        // race that load -- awaiting the stored load task at the top of the reassign command is the
        // fix, not a flag that could otherwise let the user's own save wipe their OTHER already-saved
        // slots (round-1/round-2 finding). Gate is a real controllable TaskCompletionSource, not an
        // assumption about scheduling order (this project's own "deterministic gates" convention).
        var settingsStore = new FakeSettingsStore();
        var customIds = QuickModeGridDefaults.Ids.ToArray();
        customIds[5] = "mc110"; // a mode outside the default 16, so no collision with any other slot's default
        settingsStore.Settings = settingsStore.Settings.WithSection(RxPaneUiSettings.SectionKey, new RxPaneUiSettings { QuickModeGridIds = customIds }, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        var gate = new TaskCompletionSource();
        settingsStore.Gate = gate.Task;
        var sstvSession = new FakeSstvSessionService { AvailableModes = SstvModeRegistry.All };

        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), settingsStore, NullLogger<RxImagePaneViewModel>.Instance);
        var newMode = SstvModeRegistry.Mn73;
        var reassignTask = vm.ReassignQuickModeSlotCommand.ExecuteAsync(new QuickModeReassignment(0, newMode));

        gate.SetResult();
        await reassignTask;

        Assert.Equal(newMode.Id, vm.QuickModeSlots[0].CurrentMode.Id);
        // Slot 5's persisted assignment must have survived the load that resolved AFTER the
        // reassignment was requested -- not been clobbered by a stale in-flight write.
        Assert.Equal("mc110", vm.QuickModeSlots[5].CurrentMode.Id);
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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.False(vm.LookupQrzCommand.CanExecute(null));

        vm.OverrideCallsign = "W1AW";
        Assert.True(vm.LookupQrzCommand.CanExecute(null));

        vm.OverrideCallsign = "   ";
        Assert.False(vm.LookupQrzCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_LookupQrzCommand_DisabledWhenQrzLookupNotConfigured()
    {
        // Round-2 fix: the button used to stay enabled with a real callsign typed in, only
        // failing after the click, when QRZ lookup wasn't actually configured in Options.
        var logbookSession = new FakeLogbookSessionService { IsQrzLookupConfiguredResult = false };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.OverrideCallsign = "W1AW";

        Assert.False(vm.LookupQrzCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_Constructor_LoadsIsQrzLookupConfigured_FromLogbookSession()
    {
        var logbookSession = new FakeLogbookSessionService { IsQrzLookupConfiguredResult = false };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.False(vm.IsQrzLookupConfigured);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_LoadQrzLookupConfiguredAsync_RefreshesTheGate()
    {
        // Exercises the same refresh path MainWindow.axaml.cs calls on Options close, so
        // toggling QRZ lookup on/off while the app is running takes effect without a restart.
        var logbookSession = new FakeLogbookSessionService { IsQrzLookupConfiguredResult = true };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.True(vm.IsQrzLookupConfigured);

        logbookSession.IsQrzLookupConfiguredResult = false;
        await vm.LoadQrzLookupConfiguredAsync();

        Assert.False(vm.IsQrzLookupConfigured);
        vm.OverrideCallsign = "W1AW";
        Assert.False(vm.LookupQrzCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_LoadQrzLookupConfiguredAsync_WhenTheCallThrows_LeavesTheGateAtItsCurrentValue()
    {
        // Best-effort, same reasoning as LoadCaptureDeviceNameAsync/LoadOperatorGridAsync's own
        // catch blocks -- a failure here must not throw out of a fire-and-forget constructor call
        // or block a later refresh; the gate just stays at whatever it already was.
        var logbookSession = new FakeLogbookSessionService { IsQrzLookupConfiguredResult = true };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        Assert.True(vm.IsQrzLookupConfigured);

        logbookSession.ThrowOnIsQrzLookupConfigured = new InvalidOperationException("boom");
        var exception = await Record.ExceptionAsync(() => vm.LoadQrzLookupConfiguredAsync());

        Assert.Null(exception);
        Assert.True(vm.IsQrzLookupConfigured);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_LookupQrzAsync_Success_PopulatesNameQthGrid_ClearsError()
    {
        var logbookSession = new FakeLogbookSessionService
        {
            LookupResultToReturn = new QrzCallsignLookupResult(true, "Hiram Maxim", "Newington (United States)", "FN31pr", null),
        };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), localization, logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

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
    public void TxControlsPaneViewModel_Constructed_SoundFileIdOnlyEnabled_TailShowsSoundFileOnly()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None with { SoundFileIdEnabled = true },
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.TxId.TailSoundFileOnly", vm.TailDisplay);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_Constructed_FskAndSoundFileIdEnabled_TailShowsBothSoundFile()
    {
        var sstvSession = new FakeSstvSessionService
        {
            AvailableModes = [TestMode],
            StationIdTransmitOptionsToReturn = StationIdTransmitOptions.None with { FskIdEnabled = true, SoundFileIdEnabled = true },
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.TxId.TailBothSoundFile", vm.TailDisplay);
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

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 1: Copy-to-TX must switch the operator to
    /// the Transmit tab on a successful open, not leave them stranded on the RX tab.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_Succeeds_RequestsTransmitTabFocus()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var focusRequestedCount = 0;
        vm.RequestTransmitTabFocus = () => focusRequestedCount++;

        await OpenEditorAsync(vm, () => vm.CopyReceivedImageToTxCommand.ExecuteAsync(null));

        Assert.Equal(1, focusRequestedCount);
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 1: even when a genuine in-progress edit
    /// refuses the claim (<see cref="TxControlsPaneViewModel_CopyReceivedImageToTx_WhileGenuineEditInProgress_Refuses"/>),
    /// the operator should still land on the Transmit tab -- their in-progress edit is there.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_ClaimRefused_StillRequestsTransmitTabFocus()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        var focusRequestedCount = 0;
        vm.RequestTransmitTabFocus = () => focusRequestedCount++;

        await vm.CopyReceivedImageToTxCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, focusRequestedCount);
        Assert.Same(firstEditor, ExtractCurrentEditor(vm));
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 1: nothing received yet must not request
    /// tab focus -- there's nothing to switch to look at.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_NothingReceivedYet_DoesNotRequestTransmitTabFocus()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var focusRequestedCount = 0;
        vm.RequestTransmitTabFocus = () => focusRequestedCount++;

        await vm.CopyReceivedImageToTxCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, focusRequestedCount);
    }

    // RX/TX pipeline fix plan (2026-09-01), item 3: no path from the Gallery into the TX editor.
    // OpenEditorForExternalFileAsync is the public entry point RxHistoryPaneViewModel's
    // SendSelectedEntryToTxCommand reaches through SendToTxRequested.

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenEditorForExternalFileAsync_OpensEditorAndRequestsTransmitTabFocus()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var focusRequestedCount = 0;
        vm.RequestTransmitTabFocus = () => focusRequestedCount++;
        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += e => opened = e;

        var result = await vm.OpenEditorForExternalFileAsync("/tmp/gallery-frame.png", null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(result);
        Assert.NotNull(opened);
        Assert.Same(imageFileLoader.ResultToReturn, opened!.CurrentSource);
        Assert.Equal(1, focusRequestedCount);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenEditorForExternalFileAsync_SeedsProvidedContactVariables()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += e => opened = e;

        var contactVariables = new Dictionary<string, string>(StringComparer.Ordinal) { ["his_call"] = "W1AW", ["his_grid"] = "FN31pr" };
        await vm.OpenEditorForExternalFileAsync("/tmp/gallery-frame.png", contactVariables);
        Dispatcher.UIThread.RunJobs();

        var editor = opened!;
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call} {his_grid}";

        Assert.Equal("W1AW", editor.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
        Assert.Equal("FN31pr", editor.TemplateVariableRows.Single(r => r.Key == "his_grid").Value);
    }

    /// <summary>Auditor round 2's blocker B, the regression this plan explicitly fixed: passing
    /// <see langword="null"/> must mean "seed truly nothing," NOT silently fall back to the live RX
    /// pane's own contact the way an unspecified-parameter default would have.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenEditorForExternalFileAsync_NullContactVariables_LeavesTokenUnresolved_EvenWithARxContactWired()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance)
        {
            // Wired the same way MainWindow.axaml.cs wires it for a REAL, currently-active RX
            // contact -- if OpenEditorForExternalFileAsync's null somehow fell back to this (the
            // exact bug shape blocker B fixed), the assertion below would fail.
            CurrentContactRequested = () => ("W1AW", "FN31pr"),
        };
        TxImageEditorPaneViewModel? opened = null;
        vm.EditorOpened += e => opened = e;

        await vm.OpenEditorForExternalFileAsync("/tmp/gallery-frame.png", null);
        Dispatcher.UIThread.RunJobs();

        var editor = opened!;
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call}";

        Assert.Equal("DE {his_call}", element.ResolvedText);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenEditorForExternalFileAsync_ClaimRefused_ReturnsFalse_DoesNotRequestTransmitTabFocus()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.AddOverlayElementCommand.Execute(null); // a genuine in-progress edit, not blank/untouched
        var focusRequestedCount = 0;
        vm.RequestTransmitTabFocus = () => focusRequestedCount++;

        var result = await vm.OpenEditorForExternalFileAsync("/tmp/gallery-frame.png", null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(result);
        Assert.Equal(0, focusRequestedCount);
        Assert.Same(firstEditor, ExtractCurrentEditor(vm));
    }

    /// <summary>Macros help plan (2026-09-01), item B, plan-review's own blocker finding: the
    /// editor's macrosReferenceRequested parameter MUST be threaded through as a closure over
    /// <see cref="TxControlsPaneViewModel.RequestMacrosReference"/>, not that property's captured
    /// value at construction time -- otherwise setting it AFTER an editor is already open (a real,
    /// reachable ordering in production: MainViewModel can construct an initial blank editor before
    /// MainWindow.axaml.cs's DataContextChanged handler finishes wiring cross-VM delegates,
    /// TxControlsPaneViewModel.cs's own EditorOpened doc comment) would leave that editor's button
    /// permanently dead. This test constructs the editor FIRST, wires the delegate SECOND -- the
    /// order a captured-value bug would fail against.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RequestMacrosReference_SetAfterEditorAlreadyOpen_StillReachesTheEditorsCommand()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null)); // RequestMacrosReference still unset here

        var invokedCount = 0;
        vm.RequestMacrosReference = () => invokedCount++; // wired only AFTER the editor already exists

        editor.OpenMacrosReferenceCommand.Execute(null);

        Assert.Equal(1, invokedCount);
    }

    /// <summary>ui_transition_plan.md step 5 (T1-6): "Copy to TX" seeds the new editor's HIS
    /// CALL/HIS GRID template variables from CurrentContactRequested, so a reply-card template
    /// comes up pre-filled with the received station's own callsign/grid.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_SeedsHisCallAndHisGridFromCurrentContactRequested()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance)
        {
            CurrentContactRequested = () => ("W1AW", "FN31pr"),
        };

        var editor = await OpenEditorAsync(vm, () => vm.CopyReceivedImageToTxCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call} {his_grid}";

        Assert.Equal("W1AW", editor.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
        Assert.Equal("FN31pr", editor.TemplateVariableRows.Single(r => r.Key == "his_grid").Value);
    }

    /// <summary>Code-review finding: a whitespace-only callsign must NOT seed "his_call" as an empty
    /// string -- MacroTextResolver resolves a present-but-empty variable to "" (token vanishes from
    /// the transmitted card), while an absent one resolves verbatim to "{his_call}" (an obvious
    /// unfilled placeholder). Asserts on ResolvedText, not the fill-bar row value, since the row
    /// reads empty either way -- a row-value-only assertion would pass against the bug.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_WhitespaceOnlyCallsign_LeavesTokenUnresolved()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance)
        {
            CurrentContactRequested = () => ("   ", null),
        };

        var editor = await OpenEditorAsync(vm, () => vm.CopyReceivedImageToTxCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call}";

        Assert.Contains("{his_call}", element.ResolvedText);
    }

    /// <summary>No RX pane wired (CurrentContactRequested left null, e.g. a headless/host-not-fully-
    /// constructed scenario) must not throw -- the editor opens with genuinely empty rows instead.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CopyReceivedImageToTx_CurrentContactRequestedUnwired_OpensWithEmptyRows()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.CopyReceivedImageToTxCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call}";

        Assert.Equal(string.Empty, editor.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
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

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 2: a template referencing {freq} must send
    /// the frequency at the moment of TRANSMIT, not the frequency frozen at Apply time.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_Transmit_FrequencyMacroChangedSinceApply_RebakesWithFreshValue()
    {
        var preparer = new FakeTransmitImagePreparer();
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), preparer, new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), radioSession, new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "{freq}";
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var loadedImageAfterApply = ExtractLoadedImage(vm);
        radioSession.LastKnownState = radioSession.LastKnownState.Value with { FrequencyHz = 7_045_000 };

        await vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var lastDocument = preparer.TemplateDocuments[^1];
        var textElement = Assert.IsType<TemplateTextElement>(Assert.Single(lastDocument.Elements));
        Assert.Equal("7.045000 MHz", textElement.Content);
        Assert.NotSame(loadedImageAfterApply, sstvSession.TransmitCalls[^1].Image);
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 2: when nothing macro-driven has changed
    /// since Apply, Transmit must skip the (expensive, native-resolution) rebake and send the
    /// already-baked image as-is -- the compare-then-conditionally-rebake gate's whole point.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_Transmit_NoMacroChangeSinceApply_DoesNotRebake()
    {
        var preparer = new FakeTransmitImagePreparer();
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), preparer, new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), radioSession, new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "{freq}";
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var applyTemplateCallCountAfterApply = preparer.ApplyTemplateCallCount;
        var loadedImageAfterApply = ExtractLoadedImage(vm);

        await vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(applyTemplateCallCountAfterApply, preparer.ApplyTemplateCallCount);
        Assert.Same(loadedImageAfterApply, sstvSession.TransmitCalls[^1].Image);
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 2, auditor round 2's blocker A: the
    /// compare-gate's baseline must update after a successful rebake, or retuning BACK to a
    /// previously-transmitted value silently transmits the stale image from the intervening
    /// rebake. Apply at freq A, Transmit at freq B (rebakes to B), retune to A, Transmit again --
    /// must transmit A, not the leftover B image from the first rebake.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_Transmit_RetuneBackToOriginalValueAfterRebake_TransmitsFreshValueNotStaleRebake()
    {
        var preparer = new FakeTransmitImagePreparer();
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), preparer, new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), radioSession, new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "{freq}";
        editor.ApplyCommand.Execute(null); // baseline: 14.230000 MHz baked at Apply
        Dispatcher.UIThread.RunJobs();

        radioSession.LastKnownState = radioSession.LastKnownState.Value with { FrequencyHz = 7_045_000 };
        await vm.TransmitCommand.ExecuteAsync(null); // rebakes to 7.045000 MHz
        Dispatcher.UIThread.RunJobs();

        radioSession.LastKnownState = radioSession.LastKnownState.Value with { FrequencyHz = 14_230_000 }; // retune back to the ORIGINAL Apply-time value
        await vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var lastDocument = preparer.TemplateDocuments[^1];
        var textElement = Assert.IsType<TemplateTextElement>(Assert.Single(lastDocument.Elements));
        Assert.Equal("14.230000 MHz", textElement.Content);
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 2, code-review finding: the macro
    /// freshness check awaits a real settings load, so <c>_transmitCts</c> must exist BEFORE that
    /// await starts, not just before the encode call -- otherwise a Stop TX click during that window
    /// hits a null <c>_transmitCts</c> (a silent no-op via <c>_transmitCts?.Cancel()</c>) and the
    /// transmission proceeds anyway despite the click.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_Transmit_StopClickedDuringMacroRefreshSettingsLoad_CancelsBeforeSending()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var settingsStore = new FakeSettingsStore();
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var gate = new TaskCompletionSource();
        settingsStore.Gate = gate.Task;

        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsTransmitting);
        Assert.Empty(sstvSession.TransmitCalls); // still parked inside the macro-refresh settings load
        vm.StopTransmitCommand.Execute(null);

        gate.SetResult();
        await transmitTask;
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(sstvSession.TransmitCalls); // the cancellation must be observed BEFORE encoding starts
        Assert.False(vm.IsTransmitting);
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 2, code-review finding: the mode ComboBox
    /// stays enabled during a transmit (<c>CanChangeSourceOrMode</c> has no <c>IsTransmitting</c>
    /// term), so <c>SelectedMode</c> can change during the macro-refresh settings-load await. If it
    /// does, <c>OnSelectedModeChanged</c> already reflowed <c>_loadedImage</c>/<c>PreviewImage</c> to
    /// the NEW mode's own dimensions -- the rebake must NOT overwrite that with an image baked
    /// against the stale captured mode, or the "_loadedImage always matches SelectedMode" invariant
    /// breaks and a LATER Transmit hands a wrong-sized image to the encoder. The in-flight
    /// transmission itself must still stay internally self-consistent (captured mode + its own
    /// correctly-sized rebake), even though the retained state update is skipped.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_Transmit_SelectedModeChangedDuringMacroRefresh_DoesNotClobberTheNewModesReflow()
    {
        var testMode2 = TestMode with { Id = "test2", ImageWidth = 2, ImageHeight = 2 };
        var preparer = new FakeTransmitImagePreparer();
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode, testMode2] };
        var settingsStore = new FakeSettingsStore();
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), preparer, new FakeFilePickerService(), new FakeLocalizationService(), settingsStore, radioSession, new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "{freq}";
        editor.ApplyCommand.Execute(null); // baked against TestMode (1x1) at 14.230000 MHz
        Dispatcher.UIThread.RunJobs();

        radioSession.LastKnownState = radioSession.LastKnownState.Value with { FrequencyHz = 7_045_000 }; // forces a rebake
        var gate = new TaskCompletionSource();
        settingsStore.Gate = gate.Task;

        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedMode = testMode2; // fires OnSelectedModeChanged's own reflow to 2x2 while the rebake is still parked
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, ExtractLoadedImage(vm)!.Width);

        gate.SetResult();
        await transmitTask;
        Dispatcher.UIThread.RunJobs();

        // The retained state must still reflect testMode2's own reflow, not get clobbered by the
        // rebake (which was baked against the STALE captured TestMode).
        Assert.Equal(2, ExtractLoadedImage(vm)!.Width);
        Assert.Equal(2, ExtractLoadedImage(vm)!.Height);

        // The in-flight transmission itself must still be self-consistent: TestMode (captured before
        // the await) paired with an image actually sized for TestMode, not testMode2.
        var sent = Assert.Single(sstvSession.TransmitCalls);
        Assert.Equal(TestMode, sent.Mode);
        Assert.Equal(1, sent.Image.Width);
        Assert.Equal(1, sent.Image.Height);
    }

    /// <summary>ui_transition_plan.md step 2 (T1-2): one click applies AND starts the transmit --
    /// the whole point of the SEND row's new primary action is that the operator doesn't have to
    /// separately find and click Transmit in the sidebar afterward.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ApplyAndTransmitCommand_OnTheOpenEditor_ClosesEditorAndStartsTransmit()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        Assert.True(vm.IsEditorOpen);
        Assert.True(editor.ApplyAndTransmitCommand.CanExecute(null));

        editor.ApplyAndTransmitCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEditorOpen);
        Assert.Single(sstvSession.TransmitCalls);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[0].Mode);
    }

    /// <summary>Companion to the test above: while an editor is already open and the SIDEBAR
    /// Transmit button starts transmitting a PREVIOUSLY applied image (a re-edit-in-progress
    /// scenario), the open editor's own Apply &amp; Transmit button must live-disable -- otherwise
    /// clicking it would try to start a second, overlapping transmission.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ApplyAndTransmitCommand_DisablesWhileSidebarTransmitIsRunning()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        firstEditor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));

        sstvSession.BlockUntilCancelled = true;
        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);

        // A second editor open (e.g. re-editing while the first transmission is still running) --
        // its Apply & Transmit must reflect the PARENT's live IsTransmitting, not just its own
        // freshly-constructed state.
        var secondEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        Assert.False(secondEditor.ApplyAndTransmitCommand.CanExecute(null));

        vm.StopTransmitCommand.Execute(null);
        await transmitTask;
        Dispatcher.UIThread.RunJobs();

        Assert.True(secondEditor.ApplyAndTransmitCommand.CanExecute(null));
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

    // Fable UX-review finding, 2026-08-30: previously only Copy-to-TX seeded {his_call}/{his_grid}
    // -- this pane's own OpenBlankEditorAsync doc comment documents "Load a Ready Rack/Template
    // Library entry" as the intended way to reach a reply-card template after opening blank, which
    // is at least as common a "replying to a station" path as Copy-to-TX. Same seed source
    // (CurrentContactRequested), now shared via BuildCurrentContactVariables.
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenBlankEditorCommand_AlsoSeedsHisCallAndHisGridFromCurrentContactRequested()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance)
        {
            CurrentContactRequested = () => ("W1AW", "FN31pr"),
        };

        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call} {his_grid}";

        Assert.Equal("W1AW", editor.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
        Assert.Equal("FN31pr", editor.TemplateVariableRows.Single(r => r.Key == "his_grid").Value);
    }

    // Nothing received yet is the common cold-start case -- CurrentContactRequested unwired/null
    // must not throw, and the row must resolve genuinely empty, matching the existing Copy-to-TX
    // "unwired" test's own contract.
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_OpenBlankEditorCommand_CurrentContactRequestedUnwired_OpensWithEmptyRows()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call}";

        Assert.Equal(string.Empty, editor.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
    }

    // Round-1 code-review finding (test-gap nit): the Blank-editor seeding tests above cover
    // OpenBlankEditorAsync, but OpenEditorForSourceAsync (Browse/Stock's shared call site) is the
    // OTHER newly-seeded path and had no dedicated coverage. Stock, not Browse, since Browse needs a
    // file-picker round trip -- both funnel through the same OpenEditorForSourceAsync call.
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectStockImage_AlsoSeedsHisCallAndHisGridFromCurrentContactRequested()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var stockEntry = new StockImageEntry("s1", "stock.png", "/stock/stock.png");
        var stockLibrary = new FakeStockImageLibrary
        {
            EntriesToReturn = [stockEntry],
            FullImageToReturn = new ArrayImageSource(1, 1, [new Rgb24(4, 5, 6)]),
        };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), stockLibrary, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance)
        {
            CurrentContactRequested = () => ("W1AW", "FN31pr"),
        };

        var editor = await OpenEditorAsync(vm, () => vm.SelectStockImageCommand.ExecuteAsync(stockEntry));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "DE {his_call} {his_grid}";

        Assert.Equal("W1AW", editor.TemplateVariableRows.Single(r => r.Key == "his_call").Value);
        Assert.Equal("FN31pr", editor.TemplateVariableRows.Single(r => r.Key == "his_grid").Value);
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
    public async Task TxControlsPaneViewModel_QuickSelectMode_AllowedWhileTheBlankPlaceholderEditorIsOpenAndUntouched()
    {
        // AskUserQuestion decision (2026-08-17, recommended option): a BLANK auto-opened editor is
        // safe to switch mode away from, unlike a manually-picked real photo (see the sibling
        // "DisablesTheQuickSelectModeCommand" test below, which still asserts CanExecute false for
        // exactly that SelectImageCommand case).
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        Assert.True(vm.QuickSelectModeCommand.CanExecute(modeB.Id));
        vm.QuickSelectModeCommand.Execute(modeB.Id);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("other", vm.SelectedMode?.Id);
        Assert.True(vm.IsEditorOpen);
    }

    // Auditor-found regression (2026-08-17, usability-gap review): with the editor now open by
    // default, a bare IsEditorOpen guard made Browse/STOCK/the mode ComboBox permanently
    // unreachable after the very first blank auto-open -- there was no way to ever load a real
    // photo or change SSTV mode via the dropdown. Fixed the same way the quick-mode grid already
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
        // established "body-level check is the real backstop" pattern, matching
        // QuickSelectMode's own documented reasoning) -- CanChangeSourceOrMode is the real gate
        // (drives the AXAML IsEnabled binding); a direct Execute call must still be a safe no-op.
        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, editorOpenedCount);
        Assert.True(vm.IsEditorOpen);
        Assert.True(editor.HasUnsavedEdits);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_QuickSelectMode_DisallowedOnceTheBlankEditorHasARealEdit()
    {
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var canExecuteChangedCount = 0;
        vm.QuickSelectModeCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        editor.AddOverlayElementCommand.Execute(null);

        Assert.True(editor.HasUnsavedEdits);
        Assert.False(vm.QuickSelectModeCommand.CanExecute(modeB.Id));
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
        // High item 2 fix's own regression coverage for QuickSelectMode's CanExecute/body-level
        // guard (a separate command, not a wrapper around anything else).
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

        // Also exercises QuickSelectMode's own body-level IsEditorOpen guard directly
        // (CanExecute isn't a hard gate -- RelayCommand<T>.Execute doesn't consult it, only
        // Avalonia's Button.OnClick does).
        vm.QuickSelectModeCommand.Execute("other");
        Assert.Equal("test", vm.SelectedMode?.Id);

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // Backlog item (user request, 2026-08-17): Cancel now auto-reopens a fresh BLANK editor
        // rather than leaving the column empty.
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
        // ComboBox/quick-grid buttons/RX auto-follow (not just re-entrant picking), an unhandled throw
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
        Assert.True(vm.QuickSelectModeCommand.CanExecute(modeA.Id));
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

    /// <summary>ui_transition_plan.md step 9 (T2-7): the Storage card's folder path was previously
    /// read-only text -- this makes the already-auto-archived location actually reachable.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenStorageFolderCommand_OpensTheResolvedImagesDirectory()
    {
        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = "/tmp/scanlinestudio-history" };
        var urlLauncher = new FakeUrlLauncher();
        var vm = CreateRxHistoryPaneViewModel(historyStore, urlLauncher: urlLauncher);
        await vm.LoadImagesDirectoryAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.OpenStorageFolderCommand.CanExecute(null));

        vm.OpenStorageFolderCommand.Execute(null);

        Assert.Contains("/tmp/scanlinestudio-history", urlLauncher.OpenedUrls);
    }

    /// <summary>Code-review finding: the resolved default images directory is only ever CREATED on
    /// the first saved frame -- on a fresh profile with nothing received yet, opening it must not
    /// silently no-op (IUrlLauncher.Open's own swallow-and-log Process.Start failure against a
    /// nonexistent path).</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenStorageFolderCommand_FreshProfile_CreatesTheDirectoryFirst()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanlinestudio-test-{Guid.NewGuid():N}");
        Assert.False(Directory.Exists(directory));
        try
        {
            var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = directory };
            var urlLauncher = new FakeUrlLauncher();
            var vm = CreateRxHistoryPaneViewModel(historyStore, urlLauncher: urlLauncher);
            await vm.LoadImagesDirectoryAsync();
            Dispatcher.UIThread.RunJobs();

            vm.OpenStorageFolderCommand.Execute(null);

            Assert.True(Directory.Exists(directory));
            Assert.Contains(directory, urlLauncher.OpenedUrls);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory);
            }
        }
    }

    /// <summary>A path that can never become a real directory (it already exists as a FILE) must
    /// surface an error, not silently do nothing.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenStorageFolderCommand_CreateFails_SurfacesErrorAndDoesNotOpen()
    {
        var blockingFile = Path.Combine(Path.GetTempPath(), $"scanlinestudio-test-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        try
        {
            var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = blockingFile };
            var urlLauncher = new FakeUrlLauncher();
            var vm = CreateRxHistoryPaneViewModel(historyStore, urlLauncher: urlLauncher);
            await vm.LoadImagesDirectoryAsync();
            Dispatcher.UIThread.RunJobs();

            vm.OpenStorageFolderCommand.Execute(null);

            Assert.Empty(urlLauncher.OpenedUrls);
            Assert.NotNull(vm.ErrorMessage);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    // ui_transition_plan.md step 3 (T1-5 + T2-6): full-size viewer entry point off the Gallery grid.

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenImageViewerCommand_OpensOverFilteredEntries_AtTheTappedEntrysIndex()
    {
        var entryA = new ReceiveHistoryEntry("a", DateTimeOffset.UtcNow.AddMinutes(-1), "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var entryB = new ReceiveHistoryEntry("b", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entryA, entryB],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var urlLauncher = new FakeUrlLauncher();
        var vm = CreateRxHistoryPaneViewModel(historyStore, urlLauncher: urlLauncher);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var tapped = vm.FilteredEntries.Single(e => e.Entry.Id == "b");

        ImageViewerWindowViewModel? requested = null;
        vm.ImageViewerRequested += viewerVm => requested = viewerVm;
        vm.OpenImageViewerCommand.Execute(tapped);

        Assert.NotNull(requested);
        Assert.Equal("b", requested!.Current!.Entry.Id);

        // Round-trips through to the SAME url launcher this VM was constructed with -- confirms
        // the viewer VM was actually wired to this pane's own dependencies, not fresh no-op ones.
        requested.OpenFileLocationCommand.Execute(null);
        Assert.Equal("/tmp", Assert.Single(urlLauncher.OpenedUrls));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenImageViewerCommand_WithAnEntryNotInFilteredEntries_IsASilentNoOp()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("a", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var notInList = new RxHistoryEntryViewModel(
            new ReceiveHistoryEntry("gone", DateTimeOffset.UtcNow, "robot36", "/tmp/gone.png", null, ReceiveDecodeState.Completed), Thumbnail: null);

        var raised = false;
        vm.ImageViewerRequested += _ => raised = true;
        vm.OpenImageViewerCommand.Execute(notInList);

        Assert.False(raised);
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

    // RX/TX pipeline fix plan (2026-09-01), item 3: no path from the Gallery into the TX editor.

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_SendSelectedEntryToTxCommand_CanExecute_MatchesSelection()
    {
        var vm = CreateRxHistoryPaneViewModel(new FakeReceiveHistoryStore());

        Assert.False(vm.SendSelectedEntryToTxCommand.CanExecute(null));

        vm.SelectedEntry = new RxHistoryEntryViewModel(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed), null);

        Assert.True(vm.SendSelectedEntryToTxCommand.CanExecute(null));
    }

    /// <summary>Auditor-caught regression (2026-09-01): the same recurring omission as the
    /// 2026-08-29 Delete-button incident (see that test's own doc comment) -- OnSelectedEntryChanged
    /// originally didn't fan out to SendSelectedEntryToTxCommand.NotifyCanExecuteChanged(), so the
    /// button stayed disabled on the ordinary click-a-thumbnail path. CanExecute(null) alone (the
    /// sibling test above) stays green even with the fan-out call removed -- only subscribing to the
    /// real CanExecuteChanged event catches it.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTxCommand_CanExecuteChangedFiresWhenSelectionChanges()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var fireCount = 0;
        vm.SendSelectedEntryToTxCommand.CanExecuteChanged += (_, _) => fireCount++;

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(fireCount > 0, "Expected CanExecuteChanged to fire when SelectedEntry became non-null.");

        fireCount = 0;
        vm.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Assert.True(fireCount > 0, "Expected CanExecuteChanged to fire when SelectedEntry was cleared.");
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_NoLinkedQso_SendsWithNoContactVariables()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        RxHistoryPaneViewModel.SendToTxRequest? captured = null;
        vm.SendToTxRequested = request =>
        {
            captured = request;
            return Task.FromResult(true);
        };

        await vm.SendSelectedEntryToTxCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal("/tmp/a.png", captured!.FilePath);
        Assert.Null(captured.ContactVariables);
        Assert.Null(vm.ErrorMessage);
    }

    /// <summary>Mirrors <c>TxControlsPaneViewModel_CopyReceivedImageToTx_SeedsHisCallAndHisGridFromCurrentContactRequested</c>'s
    /// own "absent, never blank" convention -- a linked QSO's callsign/grid seed the same
    /// his_call/his_grid keys Copy-to-TX uses, so the same template resolves the same way regardless
    /// of which entry point supplied the contact.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_LinkedQso_SeedsHisCallAndHisGridFromTheQso()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Completed)],
        };
        var logbookSession = new FakeLogbookSessionService
        {
            Records = { new QsoRecord("qso-1", "W1AW", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, "FN31pr", null, null, null, false, false) },
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore, logbookSession: logbookSession);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        RxHistoryPaneViewModel.SendToTxRequest? captured = null;
        vm.SendToTxRequested = request =>
        {
            captured = request;
            return Task.FromResult(true);
        };

        await vm.SendSelectedEntryToTxCommand.ExecuteAsync(null);

        Assert.NotNull(captured!.ContactVariables);
        Assert.Equal("W1AW", captured.ContactVariables!["his_call"]);
        Assert.Equal("FN31pr", captured.ContactVariables!["his_grid"]);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_LinkedQsoLookupFails_SendsWithNoContactVariables_DoesNotThrow()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Completed)],
        };
        var logbookSession = new FakeLogbookSessionService { ThrowOnGetById = new InvalidOperationException("db locked") };
        var vm = CreateRxHistoryPaneViewModel(historyStore, logbookSession: logbookSession);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        RxHistoryPaneViewModel.SendToTxRequest? captured = null;
        vm.SendToTxRequested = request =>
        {
            captured = request;
            return Task.FromResult(true);
        };

        await vm.SendSelectedEntryToTxCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Null(captured!.ContactVariables);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_Refused_SetsErrorMessage()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        vm.SendToTxRequested = _ => Task.FromResult(false);

        await vm.SendSelectedEntryToTxCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_UnwiredSendToTxRequested_DoesNotThrow()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        await vm.SendSelectedEntryToTxCommand.ExecuteAsync(null);

        Assert.Null(vm.ErrorMessage);
    }

    // ui_transition_plan.md step 4 (T1-4, reframed): per-item manual delete.

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_CanExecute_MatchesSelection()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.DeleteSelectedEntryCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.DeleteSelectedEntryCommand.CanExecute(null));
    }

    /// <summary>Auditor-caught regression (2026-08-29): OnSelectedEntryChanged originally omitted
    /// DeleteSelectedEntryCommand.NotifyCanExecuteChanged() from its fan-out (only OpenInLog/Export
    /// were included) -- the button rendered as a live, full-strength IndustryBtnDanger (no disabled
    /// style, by design) that silently did nothing on the ordinary click-a-thumbnail path. Same
    /// "subscribe to the real event, don't just poll CanExecute(null)" discipline as
    /// RxHistoryPaneViewModel_ExportFrameCommand_CanExecuteChangedFiresWhenSelectionChanges's own doc
    /// comment explains -- confirmed via mutation testing here too: CanExecute(null) alone stayed
    /// green with the NotifyCanExecuteChanged() call removed.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_CanExecuteChangedFiresWhenSelectionChanges()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var fireCount = 0;
        vm.DeleteSelectedEntryCommand.CanExecuteChanged += (_, _) => fireCount++;

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(fireCount > 0, "Expected CanExecuteChanged to fire when SelectedEntry became non-null.");

        fireCount = 0;
        vm.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Assert.True(fireCount > 0, "Expected CanExecuteChanged to fire when SelectedEntry was cleared.");
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_Confirmed_DeletesAndRefreshesTheList()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        vm.ConfirmRequested = _ => Task.FromResult(true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.Equal("1", Assert.Single(historyStore.DeletedEntries).Id);
        Assert.Empty(vm.Entries);
        Assert.Null(vm.SelectedEntry);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_Declined_DoesNotDelete()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        vm.ConfirmRequested = _ => Task.FromResult(false);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.Empty(historyStore.DeletedEntries);
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_UnwiredConfirmRequested_DeclinesByDefault()
    {
        // Safe default for a destructive action -- same reasoning as
        // ConfigurationsManagerWindowViewModel.RequestConfirmAsync's own doc comment.
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

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.Empty(historyStore.DeletedEntries);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_EntryWithInvestment_UsesTheStrongerConfirmMessage()
    {
        var flagged = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, IsFlagged: true);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [flagged],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        string? messageSeen = null;
        vm.ConfirmRequested = confirmVm =>
        {
            messageSeen = confirmVm.Message;
            return Task.FromResult(false);
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.Equal("Panes.RxHistory.ConfirmDeleteFlaggedMessage", messageSeen);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_PlainEntry_UsesTheOrdinaryConfirmMessage()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        string? messageSeen = null;
        vm.ConfirmRequested = confirmVm =>
        {
            messageSeen = confirmVm.Message;
            return Task.FromResult(false);
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.Equal("Panes.RxHistory.ConfirmDeleteMessage", messageSeen);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_StoreThrows_SetsErrorMessage_LeavesEntryInPlace()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
            ThrowOnDelete = new IOException("disk error"),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        vm.ConfirmRequested = _ => Task.FromResult(true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.NotNull(vm.ErrorMessage);
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_EntryAlreadyGone_SetsEntryNoLongerExistsError()
    {
        var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        vm.ConfirmRequested = _ => Task.FromResult(true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        // Simulates another session/tab deleting it first -- DeleteAsync returns false, not throw.
        historyStore.EntriesToReturn.Remove(entry);

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);

        Assert.Equal("Panes.RxHistory.Error.EntryNoLongerExists", vm.ErrorMessage);
    }

    /// <summary>Auditor-caught (2026-08-29): the delete path originally only called RefreshAsync,
    /// unlike OnRecorded's own arrival path which fires RefreshAsync AND LoadFramesTodayCountAsync
    /// -- deleting a frame received today left the status bar's count inflated.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_DeleteSelectedEntryCommand_Confirmed_RefreshesFramesTodayCount()
    {
        var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        vm.ConfirmRequested = _ => Task.FromResult(true);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, vm.FramesTodayCount);
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        await vm.DeleteSelectedEntryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, vm.FramesTodayCount);
    }

    // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: Gallery/RX-details audio actions.

    /// <summary>Auditor-caught BLOCKER (round 1 code-review): RxAudioAutoSaver.AudioAttached had ZERO
    /// subscribers anywhere in the app -- a just-received frame's in-memory ReceiveHistoryEntry never
    /// picked up its AudioFilePath until some unrelated later refresh re-queried the DB, so the two
    /// new Gallery buttons stayed hidden for the frame the operator just received (the primary use
    /// case), and DeleteSelectedEntryAsync's own stale in-memory snapshot would silently orphan the
    /// WAV on delete. This pins that subscribing IRxAudioAutoSaver.AudioAttached actually patches the
    /// live, already-held entry in place -- not just that a fresh RefreshAsync would eventually see it.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AudioAttachedEvent_PatchesTheAlreadyHeldEntryInPlace()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var audioAutoSaver = new FakeRxAudioAutoSaver();
        var vm = CreateRxHistoryPaneViewModel(historyStore, audioAutoSaver: audioAutoSaver);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Entries[0].Entry.AudioFilePath);

        audioAutoSaver.RaiseAudioAttached("1", "/tmp/a.wav");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/tmp/a.wav", vm.Entries[0].Entry.AudioFilePath);
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.OpenAudioFileLocationCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenAudioFileLocationCommand_CanExecute_MatchesAudioFilePathPresence()
    {
        var withAudio = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed) { AudioFilePath = "/tmp/a.wav" };
        var withoutAudio = new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [withAudio, withoutAudio],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.OpenAudioFileLocationCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.OpenAudioFileLocationCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.OpenAudioFileLocationCommand.CanExecute(null));
    }

    /// <summary>Auditor-caught (round 1 code-review): a bare CanExecute(null) assertion alone can't
    /// distinguish "OnSelectedEntryChanged correctly re-notifies this command" from "it doesn't, but
    /// re-evaluating the predicate live happens to return the right answer anyway" -- same discipline
    /// RxHistoryPaneViewModel_ExportFrameCommand_CanExecuteChangedFiresWhenSelectionChanges's own doc
    /// comment explains. Both new commands share ONE OnSelectedEntryChanged fan-out, so one test
    /// covering both is sufficient (not two near-identical copies).</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AudioActionCommands_CanExecuteChangedFiresWhenSelectionChanges()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed) { AudioFilePath = "/tmp/a.wav" }],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var openFireCount = 0;
        var redecodeFireCount = 0;
        vm.OpenAudioFileLocationCommand.CanExecuteChanged += (_, _) => openFireCount++;
        vm.RedecodeSelectedEntryCommand.CanExecuteChanged += (_, _) => redecodeFireCount++;

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();
        Assert.True(openFireCount > 0, "Expected OpenAudioFileLocationCommand.CanExecuteChanged to fire when SelectedEntry became non-null.");
        Assert.True(redecodeFireCount > 0, "Expected RedecodeSelectedEntryCommand.CanExecuteChanged to fire when SelectedEntry became non-null.");

        openFireCount = 0;
        redecodeFireCount = 0;
        vm.SelectedEntry = null;
        Dispatcher.UIThread.RunJobs();
        Assert.True(openFireCount > 0, "Expected OpenAudioFileLocationCommand.CanExecuteChanged to fire when SelectedEntry was cleared.");
        Assert.True(redecodeFireCount > 0, "Expected RedecodeSelectedEntryCommand.CanExecuteChanged to fire when SelectedEntry was cleared.");
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenAudioFileLocationCommand_OpensTheAudioFilesOwnContainingFolder()
    {
        // Deliberately the file's OWN folder, not the currently-configured AudioDirectory setting --
        // the operator may have changed that setting since this particular file was saved.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed) { AudioFilePath = "/some/other/folder/a.wav" }],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var urlLauncher = new FakeUrlLauncher();
        var vm = CreateRxHistoryPaneViewModel(historyStore, urlLauncher: urlLauncher);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        vm.OpenAudioFileLocationCommand.Execute(null);

        Assert.Equal(Path.Combine("/some", "other", "folder"), Assert.Single(urlLauncher.OpenedUrls));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_RedecodeSelectedEntryCommand_CanExecute_MatchesAudioFilePathPresence()
    {
        var withAudio = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed) { AudioFilePath = "/tmp/a.wav" };
        var withoutAudio = new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [withAudio, withoutAudio],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.RedecodeSelectedEntryCommand.CanExecute(null));

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.RedecodeSelectedEntryCommand.CanExecute(null));
    }

    /// <summary>RxHistoryPaneViewModel deliberately does not depend on ISstvSessionService (see
    /// RedecodeRequested's own doc comment) -- this pins that the command raises the event with the
    /// entry's own audio path rather than calling any decode service directly.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_RedecodeSelectedEntryCommand_RaisesRedecodeRequestedWithThePath()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed) { AudioFilePath = "/tmp/a.wav" }],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        string? raisedPath = null;
        vm.RedecodeRequested += path => raisedPath = path;

        vm.RedecodeSelectedEntryCommand.Execute(null);

        Assert.Equal("/tmp/a.wav", raisedPath);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_LoadAudioStorageInfoAsync_SumsRealFileSizesInTheAudioDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"scanline-studio-audio-storage-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, "a.wav"), new byte[1000]);
            await File.WriteAllBytesAsync(Path.Combine(directory, "b.wav"), new byte[2000]);

            var historyStore = new FakeReceiveHistoryStore { AutoSaveAudioEnabled = true, AudioDirectory = directory };
            var vm = CreateRxHistoryPaneViewModel(historyStore);

            await vm.LoadAudioStorageInfoAsync();

            Assert.Equal(3000L, vm.AudioStorageBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_LoadAudioStorageInfoAsync_DirectoryDoesNotExistYet_RendersAnHonestEmDash()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            AudioDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-audio-storage-test-{Guid.NewGuid()}-does-not-exist"),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);

        await vm.LoadAudioStorageInfoAsync();

        Assert.Null(vm.AudioStorageBytes);
        Assert.Equal("—", vm.AudioStorageBytesDisplay);
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
        new(id, "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false);

    private static LogbookPaneViewModel CreateLogbookPaneViewModel(
        FakeLogbookSessionService? logbook = null,
        FakeFilePickerService? filePicker = null,
        FakeLocalizationService? localization = null,
        FakeReceiveHistoryStore? historyStore = null) =>
        new(
            logbook ?? new FakeLogbookSessionService(),
            filePicker ?? new FakeFilePickerService(),
            new FakeSstvSessionService { AvailableModes = [TestMode] },
            localization ?? new FakeLocalizationService(),
            historyStore ?? new FakeReceiveHistoryStore(),
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

    [AvaloniaFact]
    public void PreviewImage_Reassigned_DisposesTheOldBitmap_DeferredViaDispatcherPost()
    {
        // T0-11 (production_audit.md): OnPreviewImageChanged fires on every writer of this
        // property (including the null-clear sites, per the generated hook -- this test exercises
        // the mechanism directly rather than driving the whole async preview-load pipeline).
        var vm = CreateRxHistoryPaneViewModel(new FakeReceiveHistoryStore());
        Dispatcher.UIThread.RunJobs();
        var first = new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        vm.PreviewImage = first;

        vm.PreviewImage = null;

        Assert.False(IsWriteableBitmapDisposed(first), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsWriteableBitmapDisposed(first));
    }

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique WriteableBitmapPoolTests
    // uses: a disposed instance throws NullReferenceException (not ObjectDisposedException) from
    // any real operation, here .Lock().
    private static bool IsWriteableBitmapDisposed(WriteableBitmap bitmap)
    {
        try
        {
            using (bitmap.Lock())
            {
            }

            return false;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    }

    private static RxHistoryPaneViewModel CreateRxHistoryPaneViewModel(
        FakeReceiveHistoryStore historyStore,
        FakeReceivedFrameExporter? frameExporter = null,
        FakeFilePickerService? filePicker = null,
        FakeSettingsStore? settingsStore = null,
        FakeUrlLauncher? urlLauncher = null,
        FakeClipboardImageService? clipboardImageService = null,
        FakeRxAudioAutoSaver? audioAutoSaver = null,
        FakeLogbookSessionService? logbookSession = null) =>
        new(
            historyStore,
            new FakeLocalizationService(),
            NullLogger<RxHistoryPaneViewModel>.Instance,
            logbookSession ?? new FakeLogbookSessionService(),
            NullLogger<QsoLinkWindowViewModel>.Instance,
            frameExporter ?? new FakeReceivedFrameExporter(),
            filePicker ?? new FakeFilePickerService(),
            settingsStore ?? new FakeSettingsStore(),
            urlLauncher ?? new FakeUrlLauncher(),
            clipboardImageService ?? new FakeClipboardImageService(),
            NullLogger<ImageViewerWindowViewModel>.Instance,
            audioAutoSaver ?? new FakeRxAudioAutoSaver());

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

    // Fable UX-review finding, 2026-08-30: a QSO logged via "Log QSO" from a decoded RX frame
    // (PrefillForNewEntry's new receivedImageId param) previously carried no link back to that
    // frame's own history entry at all -- unlike Gallery's separate "Open in log" path, which does
    // link. LogAsync's BuildRecordFromForm call must thread _editingReceivedImageId through.
    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_AfterPrefillWithReceivedImageId_LinksTheNewRecordToTheFrame()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("N0CALL", "martin1", DateTimeOffset.UtcNow, null, null, null, receivedImageId: "history-entry-1");
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.Equal("history-entry-1", logbook.Records[0].ReceivedImageId);
    }

    // Round-1 code-review finding: QsoRecord.ReceivedImageId above is only the reverse FK --
    // ReceiveHistoryEntry.LinkedQsoId is what Gallery's own "Logged / Not logged" row, its Unlogged
    // filter, and the stronger delete-confirm all actually read (QsoLinkWindowViewModel's own doc
    // comment). Without this, a QSO logged from a decoded frame set the reverse FK but Gallery still
    // showed the frame as unlogged.
    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_AfterPrefillWithReceivedImageId_AlsoSetsTheEntrysLinkedQsoId()
    {
        var logbook = new FakeLogbookSessionService();
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("history-entry-1", DateTimeOffset.UtcNow, "martin1", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = CreateLogbookPaneViewModel(logbook, historyStore: historyStore);
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("N0CALL", "martin1", DateTimeOffset.UtcNow, null, null, null, receivedImageId: "history-entry-1");
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var loggedQsoId = logbook.Records[0].Id;
        Assert.Equal(loggedQsoId, historyStore.EntriesToReturn[0].LinkedQsoId);
    }

    // Plain "type a callsign and click Log" (no prior PrefillForNewEntry call) must still log with
    // no link -- New()/the constructor's own ResetForm() already clears _editingReceivedImageId to
    // null, this just confirms LogAsync doesn't fabricate one.
    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_WithoutPrefill_LogsWithNoReceivedImageId()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.Null(logbook.Records[0].ReceivedImageId);
    }

    // Fable UX-review finding, 2026-08-30: LoadTotalLoggedCountAsync was only ever called at
    // construction and after a delete -- logging a new QSO left the status bar's "log N entries"
    // stale until the next delete.
    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_RefreshesTotalLoggedCount()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, vm.TotalLoggedCount);

        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, vm.TotalLoggedCount);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_QslFlagsPersist_AndFormResetsToFalseAfterward()
    {
        // ui_transition_plan.md step 15, piece (b) -- threaded through BuildRecordFromForm/ResetForm.
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "N0CALL";
        vm.FormQslSent = true;
        vm.FormQslReceived = true;
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(logbook.Records[0].QslSent);
        Assert.True(logbook.Records[0].QslReceived);
        // ResetForm() ran (same regression class as above) -- QSL checkboxes must not stay checked
        // for the NEXT QSO the operator logs.
        Assert.False(vm.FormQslSent);
        Assert.False(vm.FormQslReceived);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_SelectingAnEntry_LoadsQslFlagsIntoTheForm()
    {
        var record = SampleQsoRecord("1") with { QslSent = true, QslReceived = false };
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(record);
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];

        Assert.True(vm.FormQslSent);
        Assert.False(vm.FormQslReceived);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_UpdateAsync_QslFlagsPersist()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        vm.FormQslSent = true;
        vm.FormQslReceived = true;
        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(logbook.Records[0].QslSent);
        Assert.True(logbook.Records[0].QslReceived);
    }

    // ui_transition_plan.md step 15, piece (c) (duplicate-QSO detection) -----------------------

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_DuplicateFound_WarnsAndDoesNotLog_SecondClickProceeds()
    {
        var logbook = new FakeLogbookSessionService { DuplicateResultToReturn = SampleQsoRecord("existing") };
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.FormCallsign = "N0CALL";

        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(logbook.Records);
        Assert.True(vm.IsConfirmingDuplicate);
        Assert.NotNull(vm.StatusMessage);

        // Second click on the SAME button, same form contents -- proceeds without re-warning.
        logbook.DuplicateResultToReturn = null; // irrelevant on this path -- the arm short-circuits the check
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.False(vm.IsConfirmingDuplicate);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_NoDuplicateFound_LogsImmediately_NeverArms()
    {
        var logbook = new FakeLogbookSessionService(); // DuplicateResultToReturn defaults to null
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.FormCallsign = "N0CALL";

        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.False(vm.IsConfirmingDuplicate);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_WarnedThenCallsignEdited_ReChecksInsteadOfSilentlyConfirming()
    {
        // Code-review concern (round 1): editing the callsign after a warning must not silently
        // confirm-log a DIFFERENT callsign than the one actually flagged as a duplicate.
        var logbook = new FakeLogbookSessionService { DuplicateResultToReturn = SampleQsoRecord("existing") };
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsConfirmingDuplicate);

        vm.FormCallsign = "K1ABC";
        logbook.DuplicateResultToReturn = null; // K1ABC has no duplicate
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Code-review finding: asserting only the final logged callsign doesn't prove a re-check
        // happened -- a silently-confirming implementation (bug: the armed state satisfies ANY
        // subsequent click regardless of what changed) would log this exact same callsign too. The
        // real proof is that FindLikelyDuplicateAsync was actually called a SECOND time.
        Assert.Equal(2, logbook.FindLikelyDuplicateCalls.Count);
        Assert.Equal("K1ABC", logbook.FindLikelyDuplicateCalls[1].Callsign);
        var logged = Assert.Single(logbook.Records);
        Assert.Equal("K1ABC", logged.Callsign);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_DuplicateCheckThrows_FailsOpen_StillLogs()
    {
        var logbook = new FakeLogbookSessionService { ThrowOnFindLikelyDuplicate = new InvalidOperationException("DB locked") };
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.FormCallsign = "N0CALL";

        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.False(vm.IsConfirmingDuplicate);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_UpdateAsync_DuplicateFound_WarnsAndDoesNotUpdate_SecondClickProceeds()
    {
        var logbook = new FakeLogbookSessionService { DuplicateResultToReturn = SampleQsoRecord("existing") };
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        vm.FormNotes = "edited";

        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(logbook.Records[0].Notes);
        Assert.True(vm.IsConfirmingDuplicate);

        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("edited", logbook.Records[0].Notes);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_UpdateAsync_PassesItsOwnIdAsExcludeId_NeverFlagsItself()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var call = Assert.Single(logbook.FindLikelyDuplicateCalls);
        Assert.Equal("1", call.ExcludeId);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogArmedWhileEditing_DoesNotSatisfyUpdatesOwnGuard()
    {
        // Code-review nit (round 2), and code-review finding (this test itself, round after
        // implementation): the ORIGINAL version of this test selected a different row between the
        // Log click and the Update click, which clears ANY arm via OnSelectedEntryChanged -- so it
        // passed even with the ForUpdate discriminator deleted entirely (Update always re-checked
        // for the unrelated reason that selection itself resets the arm, not because ForUpdate did
        // its job). The real reachable sequence needing ForUpdate: select a row (LogCommand has no
        // IsEditing-based CanExecute gate at the VM layer -- only the View hides its button), click
        // Log (arms with ForUpdate:false for THIS row's own callsign/frequency), then click Update
        // WITHOUT changing selection. If ForUpdate were removed, Update's guard would see the
        // matching (Callsign, FrequencyHz) arm and incorrectly treat itself as already-confirmed.
        var logbook = new FakeLogbookSessionService { DuplicateResultToReturn = SampleQsoRecord("existing") };
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsConfirmingDuplicate);
        Assert.Single(logbook.FindLikelyDuplicateCalls);

        // An observable change Update would persist IF (bug) it incorrectly treated itself as
        // already-confirmed by Log's arm -- without this, Records[0].Notes would stay null either
        // way and the final assertion would pass vacuously.
        vm.FormNotes = "edited";
        await vm.UpdateCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // Update ran its OWN check (2 total calls now) instead of being satisfied by Log's arm, and
        // therefore did NOT persist the edit on this first Update click.
        Assert.Equal(2, logbook.FindLikelyDuplicateCalls.Count);
        Assert.True(vm.IsConfirmingDuplicate);
        Assert.Null(logbook.Records[0].Notes);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_SelectingADifferentRowWhileWarningIsShowing_ClearsTheArm()
    {
        var logbook = new FakeLogbookSessionService { DuplicateResultToReturn = SampleQsoRecord("existing") };
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsConfirmingDuplicate);

        vm.SelectedEntry = vm.Entries[0];

        Assert.False(vm.IsConfirmingDuplicate);
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
    public async Task LogbookPaneViewModel_LogAsync_PostPersistStepFails_ShowsNeutralMessage_NotQrzFailed()
    {
        // Auditor code-review finding (2026-08-31): a non-QRZ post-persist failure (settings load,
        // ADIF export, ADIF-UDP send) used to be reported through LogQsoResult.QrzError, which
        // BuildLogStatusMessage renders through the QRZ-specific "QRZ: failed (...)" string --
        // misattributing e.g. a settings.json permissions error to QRZ, even when QRZ upload is
        // disabled entirely. LogQsoResult.PostPersistError is the distinct, correctly-attributed
        // field for this case.
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.FormCallsign = "N0CALL";
        logbook.LogResultToReturn = new LogQsoResult(SampleQsoRecord("1"), 0, 0, false, QrzError: null, PostPersistError: "settings.json access denied");
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.Logbook.Status.LoggedWithPostPersistError", vm.StatusMessage);
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
            "1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, "rx-entry-42", false, false);
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

    // ui_transition_plan.md step 15 (QSO delete) -------------------------------------------------

    [AvaloniaFact]
    public void LogbookPaneViewModel_DeleteSelectedCommand_DisabledWithNoSelection()
    {
        var vm = CreateLogbookPaneViewModel();

        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_DeleteSelectedCommand_EnabledAfterSelectingARow_DisabledAfterNew()
    {
        // Code-review finding (RxHistory already hit this exact regression once): CanExecute alone
        // is not exercised by any of the other Delete tests below (AsyncRelayCommand.ExecuteAsync
        // bypasses CanExecute entirely), so a broken/missing [RelayCommand] CanExecute wiring or a
        // missing NotifyCanExecuteChanged call would stay green everywhere else.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.NewCommand.Execute(null);
        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_SelectedEntryClearedByRefresh_StaysEnabledAndUsesFormIdentity()
    {
        // Code-review finding (the real bug this round fixed): RefreshInternalAsync's Entries.Clear()
        // nulls SelectedEntry back through the ListBox's own TwoWay binding, but OnSelectedEntryChanged
        // early-returns on null so IsEditing/_editingId (and therefore the visible Delete button) stay
        // live. Keying CanDeleteSelected on SelectedEntry left the button visible but permanently dead
        // after any Refresh/Search while a row was loaded -- this proves the fix (keying on _editingId
        // instead) actually resolves the delete target from form identity, not the live selection.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        // Simulate what a real ListBox's TwoWay SelectedItem binding does when the bound collection
        // is cleared out from under it.
        vm.SelectedEntry = null;
        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        vm.ConfirmRequested = _ => Task.FromResult(true);
        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("1", logbook.DeletedIds);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_UnwiredConfirmRequested_DeclinesByDefault()
    {
        // Same "safe default is always nothing happened" contract as
        // RxHistoryPaneViewModel_DeleteSelectedEntryCommand_UnwiredConfirmRequested_DeclinesByDefault.
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(logbook.DeletedIds);
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_UserDeclines_DoesNotDelete()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        vm.ConfirmRequested = _ => Task.FromResult(false);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(logbook.DeletedIds);
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_Confirmed_DeletesRefreshesAndSetsStatus()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var localization = new FakeLocalizationService();
        var vm = CreateLogbookPaneViewModel(logbook, localization: localization);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        string? confirmMessageKey = null;
        object[]? confirmMessageArgs = null;
        vm.ConfirmRequested = _ =>
        {
            // Captured HERE, not after ExecuteAsync completes -- the success-path StatusMessage's
            // own GetString call afterward would otherwise overwrite LastKey/LastArgs before this
            // test ever reads them.
            confirmMessageKey = localization.LastKey;
            confirmMessageArgs = localization.LastArgs;
            return Task.FromResult(true);
        };

        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // The dialog message names the callsign being deleted (auditor plan-review round 2
        // finding) -- verified via LastKey/LastArgs, since FakeLocalizationService.GetString
        // returns the raw key, not a formatted string.
        Assert.Equal("Panes.Logbook.ConfirmDeleteMessage", confirmMessageKey);
        Assert.Equal(["N0CALL"], confirmMessageArgs);
        Assert.Contains("1", logbook.DeletedIds);
        Assert.Empty(vm.Entries);
        Assert.NotNull(vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_DeletedRecordWasLoadedInForm_ResetsTheForm()
    {
        var logbook = new FakeLogbookSessionService();
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Assert.True(vm.IsEditing);
        vm.ConfirmRequested = _ => Task.FromResult(true);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsEditing);
        Assert.Null(vm.FormCallsign);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_ServiceThrows_SetsErrorMessage_LeavesEntryInList()
    {
        var logbook = new FakeLogbookSessionService { ThrowOnDelete = new InvalidOperationException("disk full") };
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        vm.ConfirmRequested = _ => Task.FromResult(true);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.NotNull(vm.StatusMessage);
        Assert.Single(vm.Entries);
    }

    [AvaloniaFact]
    public async Task LogbookPaneViewModel_DeleteSelectedCommand_AlreadyDeletedElsewhere_ShowsNotFoundMessage_RefreshesList_ResetsForm()
    {
        var logbook = new FakeLogbookSessionService { DeleteResultToReturn = false };
        logbook.Records.Add(SampleQsoRecord("1"));
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        vm.ConfirmRequested = _ => Task.FromResult(true);
        // Simulates another window/process having already deleted it by the time this confirms.
        logbook.Records.Clear();

        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.StatusMessage);
        Assert.Empty(vm.Entries);
        // Code-review finding: this branch used to skip ResetForm entirely, unlike the real-delete
        // path -- Update would then "succeed" against a row SqliteLogbookRepository.UpdateAsync
        // silently affects zero rows for.
        Assert.False(vm.IsEditing);
        Assert.Null(vm.FormCallsign);
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
        // ui_transition_plan.md step 5 (T1-6): frequencyHz/radioMode are now real trailing optional
        // params (see PrefillForNewEntry's own doc comment) -- omitted here, so they stay null, same
        // as every other unset nullable field, not a fake default.
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

    /// <summary>ui_transition_plan.md step 5 (T1-6): the actual new behavior -- when the caller DOES
    /// pass a frequency/mode (sourced from the RX frame's latched metadata or live radio state, per
    /// MainWindow.axaml.cs's own LogQsoRequested handler), it lands in the form, still freely
    /// editable.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_WithFrequencyAndMode_SetsBothFormFields()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("W1AW", "martin1", DateTimeOffset.UtcNow, null, null, null, 14_230_000, RadioMode.Usb);

        Assert.Equal(14_230_000, vm.FormFrequencyHz);
        Assert.Equal(RadioMode.Usb, vm.FormMode);
        Assert.Equal("14.230000", vm.FormFrequencyMhzText);
    }

    /// <summary>RST default plan (2026-09-01): both fields seed from the ONE passed value -- SSTV's
    /// real-world convention doesn't distinguish direction.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_WithDefaultRst_SetsBothRstFields()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("W1AW", "martin1", DateTimeOffset.UtcNow, null, null, null, defaultRst: "595");

        Assert.Equal("595", vm.FormRstSent);
        Assert.Equal("595", vm.FormRstReceived);
    }

    /// <summary>No default configured (an operator who cleared the Options field) must prefill
    /// nothing, not a fabricated value.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_WithNullDefaultRst_LeavesRstFieldsNull()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("W1AW", "martin1", DateTimeOffset.UtcNow, null, null, null);

        Assert.Null(vm.FormRstSent);
        Assert.Null(vm.FormRstReceived);
    }

    /// <summary>ui_transition_plan.md step 5 (T2-8): FormFrequencyHz stays the stored/ADIF unit;
    /// FormFrequencyMhzText is the MHz-facing edit surface the form actually binds to.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_FormFrequencyMhzText_RoundTripsThroughFormFrequencyHz()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, vm.FormFrequencyMhzText);

        vm.FormFrequencyMhzText = "7.171000";
        Assert.Equal(7_171_000, vm.FormFrequencyHz);

        vm.FormFrequencyHz = 14_230_000;
        Assert.Equal("14.230000", vm.FormFrequencyMhzText);

        // Auditor-anticipated finding: an in-progress/invalid keystroke must not blank out an
        // otherwise-valid stored value -- Avalonia's default TextBox binding trigger fires on every
        // keystroke, not just on lost-focus.
        vm.FormFrequencyMhzText = "14.2x";
        Assert.Equal(14_230_000, vm.FormFrequencyHz);

        // Explicitly clearing the field IS the recognized way to null it.
        vm.FormFrequencyMhzText = string.Empty;
        Assert.Null(vm.FormFrequencyHz);
    }

    /// <summary>Code-review finding: an out-of-range double (e.g. a pasted "1e20") converts to
    /// `long` with an unspecified result if unchecked -- must be rejected like any other unparseable
    /// input, not stored as garbage into the QSO row/ADIF FREQ.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_FormFrequencyMhzText_OutOfRangeValue_IsRejected()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();
        vm.FormFrequencyHz = 14_230_000;

        vm.FormFrequencyMhzText = "1e20";

        Assert.Equal(14_230_000, vm.FormFrequencyHz);
    }

    /// <summary>Code-review finding: FormFrequencyMhzText was originally a computed proxy re-raising
    /// its own PropertyChanged from inside the FormFrequencyHz setter's own write -- a real risk of
    /// Avalonia rewriting the TextBox mid-keystroke. This pins the fix's actual mechanism: setting
    /// the text property must not re-enter and overwrite itself with the reformatted value while the
    /// edit is still "in progress" (an in-progress edit is only observable via the guard flag's
    /// effect -- an unparseable value mid-edit leaves the text AS TYPED, not reformatted/reverted).</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_FormFrequencyMhzText_InProgressEdit_TextIsNotRewrittenMidKeystroke()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.FormFrequencyMhzText = "14.2";

        // "14.2" parses fine (partial-but-valid mid-entry), so FormFrequencyHz updates -- but the
        // text the operator is looking at must stay exactly what they typed, not "14.200000".
        Assert.Equal("14.2", vm.FormFrequencyMhzText);
        Assert.Equal(14_200_000, vm.FormFrequencyHz);
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
