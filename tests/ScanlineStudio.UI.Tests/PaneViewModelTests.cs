using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Cw;
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
using System.Globalization;
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
        var vm = new RadioStatusViewModel(radioSession, new FakeSstvSessionService(), new FakeLocalizationService(), new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);

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
        var vm = new RadioStatusViewModel(new FakeRadioSessionService(), new FakeSstvSessionService(), localization, new FakeAppearanceSettingsService(), NullLogger<RadioStatusViewModel>.Instance);

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
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);
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
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);
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
    public void WaterfallPaneViewModel_Constructor_LoadsZeroAndGainFromPersistedSettings()
    {
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RxPaneUiSettings.SectionKey,
                new RxPaneUiSettings { ZeroDb = -12.0, GainDb = 30.0 },
                RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings),
        };

        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), settingsStore, NullLogger<WaterfallPaneViewModel>.Instance);

        Assert.Equal(-12.0, vm.ZeroDb);
        Assert.Equal(30.0, vm.GainDb);
    }

    [AvaloniaFact]
    public async Task WaterfallPaneViewModel_ChangingZeroOrGain_PersistsAfterDebounceWithoutClobberingQuickModeGridIds()
    {
        // RxImagePaneViewModel also writes RxPaneUiSettings.QuickModeGridIds into this same section --
        // the read-modify-write in PersistGainZeroSettingsAsync must preserve it, same reasoning as
        // TxControlsAutoFollowAndQuickModeGridTests' own sibling-field test. Debounced (same reasoning
        // as RadioStatusViewModelTests.TxVolumePercentChange_PersistsAfterDebounceDelay) -- a slider
        // drag fires many changes; only the settled value after the delay should hit the store.
        var customIds = new[] { "robot36" };
        var settingsStore = new FakeSettingsStore
        {
            Settings = new AppSettings().WithSection(
                RxPaneUiSettings.SectionKey,
                new RxPaneUiSettings { QuickModeGridIds = customIds },
                RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings),
        };
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), settingsStore, NullLogger<WaterfallPaneViewModel>.Instance);

        vm.ZeroDb = -20.0;
        vm.GainDb = 40.0;
        Dispatcher.UIThread.RunJobs();
        var beforeDebounce = settingsStore.Settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        Assert.Equal(0.0, beforeDebounce?.ZeroDb); // not persisted yet -- still debouncing

        await Task.Delay(600);
        Dispatcher.UIThread.RunJobs();

        var rxPaneUi = settingsStore.Settings.GetSection(RxPaneUiSettings.SectionKey, RxPaneUiSettingsJsonContext.Default.RxPaneUiSettings);
        Assert.Equal(-20.0, rxPaneUi?.ZeroDb);
        Assert.Equal(40.0, rxPaneUi?.GainDb);
        Assert.Equal(customIds, rxPaneUi?.QuickModeGridIds);
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
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);
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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

        Assert.Equal(WaterfallViewMode.Both, vm.ViewMode);
        Assert.True(vm.IsViewBoth);
        Assert.False(vm.IsViewSpectrumOnly);
        Assert.False(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_SettingIsViewSpectrumOnly_UpdatesViewModeAndTheOtherComputedBools()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

        vm.IsViewSpectrumOnly = true;

        Assert.Equal(WaterfallViewMode.SpectrumOnly, vm.ViewMode);
        Assert.False(vm.IsViewBoth);
        Assert.True(vm.IsViewSpectrumOnly);
        Assert.False(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_SettingIsViewWaterfallOnly_UpdatesViewModeAndTheOtherComputedBools()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

        vm.IsViewWaterfallOnly = true;

        Assert.Equal(WaterfallViewMode.WaterfallOnly, vm.ViewMode);
        Assert.False(vm.IsViewBoth);
        Assert.False(vm.IsViewSpectrumOnly);
        Assert.True(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_StartHzSpanHzPeakHoldEnabled_HaveTheDocumentedDefaults()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), localization, new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

        Assert.False(vm.NotchEnabled);
        Assert.Equal(2400.0, vm.NotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TogglingNotchEnabled_ForwardsToTheSessionWithTheCurrentFrequency()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance) { NotchFrequencyHz = 1750.0 };

        vm.NotchEnabled = true;

        Assert.Equal(1, sstvSession.RequestNotchCallCount);
        Assert.True(sstvSession.LastNotchEnabled);
        Assert.Equal(1750.0, sstvSession.LastNotchFrequencyHz);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_TogglingNotchDisabled_ForwardsNullFrequency()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance) { NotchEnabled = true };

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
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

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
        var vm = new WaterfallPaneViewModel(sstvSession, new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance) { NotchEnabled = true };
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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);
        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.NotchEnabled = true;

        Assert.Contains(nameof(WaterfallPaneViewModel.NotchStatusDisplay), raisedProperties);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_NotchStatusDisplay_ShowsFrequencyWhenEnabled_AndOffLiteralWhenNot()
    {
        var localization = new FakeLocalizationService();
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), localization, new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);
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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService(), localization, new FakeSettingsStore(), NullLogger<WaterfallPaneViewModel>.Instance);

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

    // Previous-frames strip (user decision, 2026-08-26): a session-only rolling list of the last
    // PreviousFramesCapacity COMPLETED receptions, independent of RxHistoryPaneViewModel.Entries/the
    // Gallery tab's own ShowTodayOnly toggle.

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
        // yoniq-auditor nit (2026-09-15): pins the 96->240 thumbnail-resolution bump for this strip
        // too, not just the Gallery's own (RefreshAsync_LoadsGalleryThumbnails_AtThe240pxCap).
        Assert.Contains(historyStore.ThumbnailLoadCalls, c => c.EntryId == "entry1" && c.MaxDimension == 240);
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

    /// <summary>Fable operator-perspective punch list (2026-09-01), priority #3: capacity raised
    /// 2 -> 6, so operators comparing the last 4-6 receptions during a net/pileup no longer need a
    /// Gallery tab switch. Adds ONE more entry than the new capacity (7 total), not just enough to
    /// prove eviction happens at all -- pins the exact new boundary, not just "some eviction."</summary>
    [AvaloniaFact]
    public void PreviousFrames_MoreThanCapacityRecorded_KeepsOnlyTheNewestSix()
    {
        var sstvSession = new FakeSstvSessionService();
        var historyStore = new FakeReceiveHistoryStore { ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), historyStore, new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        var now = DateTimeOffset.UtcNow;

        for (var i = 1; i <= 7; i++)
        {
            historyStore.RaiseRecorded(new ReceiveHistoryEntry($"entry{i}", now.AddSeconds(i), "sc1", $"/tmp/frame{i}.png", null, ReceiveDecodeState.Completed));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(6, vm.PreviousFrames.Count);
        Assert.Equal(["entry7", "entry6", "entry5", "entry4", "entry3", "entry2"], vm.PreviousFrames.Select(f => f.Entry.Id));
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
    public void RxImagePaneViewModel_CallsignLabelDisplay_NoDecodeYet_IsEmpty()
    {
        // fsk_cwid.md A3 auditor code-review finding: this is a SIBLING annotation next to the
        // static "Callsign" caption (its own separate AXAML column, always bound to the static
        // loc:Translate key directly) -- not a replacement for it, so the empty state is an empty
        // string, not the caption text.
        var localization = new FakeLocalizationService();
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal(string.Empty, vm.CallsignLabelDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_FillsCallsign_CallsignLabelDisplayShowsFskSource()
    {
        // fsk_cwid.md A3: "the card reads e.g. W1AW with a secondary FSK · 14:02:11Z".
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxFrameMeta.SourceTimeFormat", vm.CallsignLabelDisplay);
        Assert.Equal("FSK", localization.LastArgs?[0]);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_FillsCallsign_CallsignLabelDisplayShowsCwSource()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxFrameMeta.SourceTimeFormat", vm.CallsignLabelDisplay);
        Assert.Equal("CW", localization.LastArgs?[0]);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_OverrideCallsignManuallyEdited_CallsignLabelDisplayRevertsToEmpty()
    {
        // fsk_cwid.md A3: a manual edit is not attributable to any decode source -- the label must
        // not keep claiming a stale "FSK · <old time>" for a value the operator just typed over.
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Panes.RxFrameMeta.SourceTimeFormat", vm.CallsignLabelDisplay);

        vm.OverrideCallsign = "N0CALL";

        Assert.Equal(string.Empty, vm.CallsignLabelDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ClearsStaleCallsignNrRstAndCwIdLabels()
    {
        // fsk_cwid.md A3: pins the "stale label survives past a value reset" bug class -- the label
        // fields are independent of the VALUE fields' own null-ness, so each needs its own explicit
        // reset in OnModeDetected, not just an inert-behind-a-null-value assumption.
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(NrText: "0012"));
        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE K1ABC", Callsign: null, Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Panes.RxFrameMeta.SourceTimeFormat", vm.CallsignLabelDisplay);
        Assert.Equal("Panes.RxFrameMeta.SourceTimeFormat", vm.NrRstLabelDisplay);
        Assert.Equal("Panes.RxFrameMeta.SourceTimeFormat", vm.CwIdLabelDisplay);

        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(string.Empty, vm.CallsignLabelDisplay);
        Assert.Equal(string.Empty, vm.NrRstLabelDisplay);
        Assert.Equal(string.Empty, vm.CwIdLabelDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationsHeard_StartsEmpty()
    {
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Empty(vm.StationsHeard);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_CallsignAndNrRst_EachAddOwnStationsHeardRow()
    {
        // fsk_cwid.md A4: "one row per decode event" -- a callsign packet and an NR/RST sub-packet
        // are two separate wire transmissions, so they get two separate rows, newest first.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC", ReceptionSequence: 1));
        Dispatcher.UIThread.RunJobs();
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 12, ReceptionSequence: 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.StationsHeard.Count);
        Assert.Equal("595012", vm.StationsHeard[0].Text);
        Assert.Equal("FSK", vm.StationsHeard[0].Source);
        Assert.Equal("K1ABC", vm.StationsHeard[1].Text);
        Assert.Equal("FSK", vm.StationsHeard[1].Source);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_AddsOneStationsHeardRowWithCwSource()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        var heard = Assert.Single(vm.StationsHeard);
        Assert.Equal("DE W1AW", heard.Text);
        Assert.Equal("CW", heard.Source);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_OwnCallsignSelfFiltered_StillAddsAStationsHeardRow()
    {
        // fsk_cwid.md A4 design decision (this row's own doc comment): the self-filter only gates
        // OverrideCallsign, not the log -- another station transmitting the operator's own callsign
        // is still a real decode event worth keeping in the session log.
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW"));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
        var heard = Assert.Single(vm.StationsHeard);
        Assert.Equal("W1AW", heard.Text);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_ForAnOlderReception_StillAddsAStationsHeardRow()
    {
        // fsk_cwid.md A4 design decision: the stale guard only gates the card row, not the log -- a
        // late-arriving decode from a superseded reception is still a real event that happened.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.CurrentReceptionSequence = 2;
        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC", ReceptionSequence: 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.CallsignDisplay);
        var heard = Assert.Single(vm.StationsHeard);
        Assert.Equal("K1ABC", heard.Text);
        Assert.Equal(1, heard.ReceptionSequence);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_DoesNotClearStationsHeard()
    {
        // fsk_cwid.md A4: "Not reset on ModeDetected -- that is the point" -- a second station's ID
        // must never silently erase the first from this list, unlike OverrideCallsign/DecodedNrRst/
        // CwIdText (all per-RECEPTION state that IS reset here).
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC"));
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.StationsHeard);

        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.StationsHeard);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationsHeard_CappedAtTwenty_EvictsTheOldest()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        for (var i = 1; i <= 21; i++)
        {
            sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: (uint)i));
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(20, vm.StationsHeard.Count);
        Assert.Equal("595021", vm.StationsHeard[0].Text);
        Assert.Equal("595002", vm.StationsHeard[19].Text);
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
        // back-to-back transmissions, a NEW reception's ModeDetected/reception-sequence bump can land
        // in that exact window, after which the write below would silently re-apply the OLD
        // reception's decoded callsign onto the NEW one now on screen. fsk_cwid.md §A5: guard is now
        // ReceptionSequence-identity-based, not IReceivedImageBuffer.Generation-based -- this test
        // simulates the race by bumping CurrentReceptionSequence and raising a real ModeDetected
        // (the actual production trigger for the guard's own latch to move) while the settings read
        // is still in flight.
        var gate = new TaskCompletionSource<string?>();
        var sstvSession = new FakeSstvSessionService { OperatorCallsignGate = gate };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC", ReceptionSequence: 1));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.OverrideCallsign);

        // A new reception starts while the settings read above is still in flight.
        sstvSession.CurrentReceptionSequence = 2;
        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        gate.SetResult("W1AW");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_ForAnOlderReception_DropsTheStaleWrite_WithNoRaceRequired()
    {
        // fsk_cwid.md §A5's own suggested test: "ID for reception A arrives after B's ModeDetected"
        // asserting a drop, with NO in-flight-await race needed at all (unlike the sibling test
        // above): reception B's ModeDetected has already fully landed, including its own
        // OverrideCallsign=null reset, by the time reception A's late decode event arrives. This
        // pins the guard's own identity-comparison logic in isolation -- it does NOT reproduce a
        // known real-world trigger for a late reception-A event (auditor code-review finding on an
        // earlier version of this comment: both real stamp sites, RestartableSstvDecoder and
        // AnalogFmSstvDecoder, read ReceptionSequence at forward/current time, not at the reception
        // the decoded bits actually belong to, so a lagging narrow-FSK scan bound would stamp the
        // NEWER sequence, not an older one -- a genuinely late-A-after-B event is not known to be
        // reachable from any current code path).
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.CurrentReceptionSequence = 2;
        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // Reception A (sequence 1) decodes late, after reception B (sequence 2) is already current.
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "K1ABC", ReceptionSequence: 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_StationIdDecodedEvent_CompactNrForAnOlderReception_DropsTheStaleWrite()
    {
        // fsk_cwid.md §A5 code-review nit: the stale guard is hoisted above the whole Callsign/
        // CompactNr/NrText dispatch, not just inside the Callsign branch -- pins that CompactNr (also
        // per-reception state, same as OverrideCallsign) gets the same protection.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.CurrentReceptionSequence = 2;
        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // Reception A (sequence 1) decodes late, after reception B (sequence 2) is already current.
        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(CompactNr: 12, ReceptionSequence: 1));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.DecodedNrRst);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_FillsCwIdDisplay()
    {
        // fsk_cwid.md §9/B-P3: the CW ID row shows the decoder's raw text.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Equal("—", vm.CwIdDisplay);
        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("DE W1AW", vm.CwIdDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_LowConfidence_AppendsHintToCwIdDisplay()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.5, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("DE W1AW (Panes.RxFrameMeta.CwId.LowConfidence)", vm.CwIdDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_HighConfidence_NoHintOnCwIdDisplay()
    {
        // Mutation-relevant boundary check: 0.6 itself (the threshold) must NOT trigger the hint --
        // pins the guard's "<" (strictly below), not "<=".
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.6, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("DE W1AW", vm.CwIdDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_CallsignFillsOverrideCallsignWhenEmpty()
    {
        // fsk_cwid.md §9's routing rule: fills OverrideCallsign when it's still empty.
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "K2ABC" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.OverrideCallsign);
        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("W1AW", vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_CallsignDoesNotOverwriteAnAlreadySetFskCallsign()
    {
        // fsk_cwid.md §9: "an FSK-decoded callsign is authoritative and is never overwritten by CW"
        // -- FSK arrives first and fills OverrideCallsign, then CW decodes a DIFFERENT callsign for
        // the same reception; FSK's value must survive, and the CW text still fills its own row.
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "K2ABC" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseStationIdDecoded(new FskStationIdDecodedInfo(Callsign: "W1AW"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("W1AW", vm.OverrideCallsign);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE K9XYZ", Callsign: "K9XYZ", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("W1AW", vm.OverrideCallsign);
        Assert.Equal("DE K9XYZ", vm.CwIdDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_CallsignMatchesOwnExactly_DoesNotAutoFill()
    {
        // Same self-filter as the FSK path, routed through the same shared comparison (A5).
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "W1AW" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
        Assert.Equal("DE W1AW", vm.CwIdDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_ForAnOlderReception_DropsTheStaleWrite()
    {
        // fsk_cwid.md §9/B-P3's own hard prerequisite: A5's guard rejects a late CW decode from a
        // previous reception -- CW results are structurally late (window close + background decode),
        // so this is the realistic trigger A5's own sibling test doesn't claim to be.
        var sstvSession = new FakeSstvSessionService { OperatorCallsign = "K2ABC" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.CurrentReceptionSequence = 2;
        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        // Reception A (sequence 1)'s CW ID decodes late, well after reception B (sequence 2) is current.
        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 1, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.CwIdDisplay);
        Assert.Null(vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_CwIdDecodedEvent_NewReceptionStartsWhileAwaitingOwnCallsign_DropsTheStaleWrite()
    {
        // Auditor code-review nit (B-P3): the FSK sibling test of the same name exercises the
        // post-await re-check via OperatorCallsignGate; the CW path's own post-await re-check
        // (ApplyCwIdDecodedAsync's split stale/FSK-already-set checks) had no equivalent -- covered
        // only incidentally by synchronous-fake timing until now.
        var gate = new TaskCompletionSource<string?>();
        var sstvSession = new FakeSstvSessionService { OperatorCallsignGate = gate };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE K1ABC", Callsign: "K1ABC", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.OverrideCallsign);

        // A new reception starts while the settings read above is still in flight.
        sstvSession.CurrentReceptionSequence = 2;
        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        gate.SetResult("W1AW");
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.OverrideCallsign);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ClearsStaleCwIdText()
    {
        // fsk_cwid.md §9: "CwIdText joins the OnModeDetected reset list" -- same per-RECEPTION
        // category as DecodedNrRst.
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.9, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("DE W1AW", vm.CwIdDisplay);

        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.CwIdDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_ClearsStaleCwIdText_EvenAfterALowConfidenceHint()
    {
        // Mutation-relevant: a stale _cwIdConfidence surviving the reset would be inert as long as
        // CwIdText is null (CwIdDisplay only reads confidence when text is non-null) -- pins that a
        // reset after a LOW-confidence decode still renders the plain "—" placeholder, not a
        // leftover "— (low confidence)".
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        sstvSession.RaiseCwIdDecoded(new CwIdDecodedInfo(ReceptionSequence: 0, Text: "DE W1AW", Callsign: "W1AW", Confidence: 0.5, ToneHz: 800, Wpm: 20, Backend: CwDecoderBackend.Classical));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("DE W1AW (Panes.RxFrameMeta.CwId.LowConfidence)", vm.CwIdDisplay);

        var mode = new SstvModeDefinition(
            Id: "m1", DisplayName: "Martin M1", VisCode: 44, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 146.432)]);
        sstvSession.RaiseModeDetected(mode);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("—", vm.CwIdDisplay);
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RxImagePaneViewModel_LookupQrzAsync_NewReceptionRejectsOldResult(bool fail)
    {
        var gate = new TaskCompletionSource<QrzCallsignLookupResult>();
        var session = new FakeSstvSessionService();
        var logbook = new FakeLogbookSessionService { LookupGate = gate.Task };
        var vm = new RxImagePaneViewModel(session, new FakeLocalizationService(), logbook,
            new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.OverrideCallsign = "W1AW";
        var pending = vm.LookupQrzCommand.ExecuteAsync(null);
        session.RaiseModeDetected(TestMode);
        Dispatcher.UIThread.RunJobs();
        vm.OverrideCallsign = "K1ABC";
        if (fail) gate.SetException(new IOException("old lookup failed"));
        else gate.SetResult(new QrzCallsignLookupResult(true, "Old name", "Old place", "FN31", null));
        await pending;
        Assert.Null(vm.LookupName);
        Assert.Null(vm.LookupQth);
        Assert.Null(vm.LookupGrid);
        Assert.Null(vm.QrzLookupErrorMessage);
        Assert.False(vm.IsLookingUpQrz);
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

    /// <summary>Worked-before plan (2026-09-01): auto-fires on a real callsign change, no manual
    /// button. Real wall-clock wait, same established pattern as this file's
    /// RxHistoryPaneViewModel note-persist debounce tests -- 400ms comfortably clears the 250ms
    /// WorkedBeforeDebounce.</summary>
    [AvaloniaFact]
    public async Task RxImagePaneViewModel_OverrideCallsignChanged_PriorContactFound_ShowsSummary()
    {
        var logbookSession = new FakeLogbookSessionService { WorkedBeforeBandToReturn = "20m" };
        logbookSession.Records.Add(new QsoRecord("1", "N0CALL", new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero), null, 14_230_000, null, null, null, null, null, null, null, null, null, null, false, false));
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.OverrideCallsign = "N0CALL";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WorkedBeforeStatus.Found, vm.WorkedBeforeStatus);
        Assert.Equal("20m · 2026-08-12", vm.WorkedBeforeDisplay);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_OverrideCallsignChanged_NoPriorContact_ShowsNewStation()
    {
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), new FakeLogbookSessionService(), new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.OverrideCallsign = "N0CALL";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WorkedBeforeStatus.None, vm.WorkedBeforeStatus);
        // FakeLocalizationService.GetString returns the raw key -- asserting it (not just NotNull)
        // proves the "New station" loc key was actually used, not a hardcoded string.
        Assert.Equal("Panes.RxFrameMeta.WorkedBefore.None", vm.WorkedBeforeDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_OverrideCallsignCleared_ShowsDashImmediately_NoQuery()
    {
        var logbookSession = new FakeLogbookSessionService();
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.OverrideCallsign = "N0CALL";
        vm.OverrideCallsign = null;

        // No debounce wait -- clearing the callsign must show Unknown/"—" immediately, not "New
        // station" (which would require a query the empty branch deliberately skips).
        Assert.Equal(WorkedBeforeStatus.Unknown, vm.WorkedBeforeStatus);
        Assert.Equal("—", vm.WorkedBeforeDisplay);
        Assert.Empty(logbookSession.GetWorkedBeforeCalls);
    }

    /// <summary>Confirming auditor round finding: the CancellationTokenSource cancel-and-replace
    /// must actually suppress superseded queries, not just fire one alongside another -- rapid
    /// retyping (5 synchronous changes, no delay between them) must result in exactly ONE real
    /// query, for the FINAL callsign only.</summary>
    [AvaloniaFact]
    public async Task RxImagePaneViewModel_RapidRetyping_OnlyTheFinalCallsignIsQueried()
    {
        var logbookSession = new FakeLogbookSessionService();
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.OverrideCallsign = "P";
        vm.OverrideCallsign = "PA";
        vm.OverrideCallsign = "PA3";
        vm.OverrideCallsign = "PA3B";
        vm.OverrideCallsign = "PA3BX";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        var call = Assert.Single(logbookSession.GetWorkedBeforeCalls);
        Assert.Equal("PA3BX", call);
    }

    /// <summary>Deliberately distinct from the "no prior contact" test above -- a failed check must
    /// never render as "confirmed new," the harmful direction for a dupe-avoidance indicator. Uses
    /// <see cref="FakeLogbookSessionService.WorkedBeforeOutcomeOverride"/>, NOT a thrown exception --
    /// code-review finding: the real <see cref="ILogbookSessionService.GetWorkedBeforeAsync"/>
    /// contract never throws (Failed is a returned outcome value), and
    /// <c>RxImagePaneViewModel.RefreshWorkedBeforeCoreAsync</c>'s own catch only handles
    /// <see cref="OperationCanceledException"/> -- an earlier version of this test threw a plain
    /// exception, which escaped uncaught as an unobserved task fault and left
    /// <see cref="RxImagePaneViewModel.WorkedBeforeStatus"/> at its untouched <c>Unknown</c>
    /// initializer, passing vacuously without ever exercising the real Failed-branch mapping.</summary>
    [AvaloniaFact]
    public async Task RxImagePaneViewModel_GetWorkedBeforeFails_ShowsUnknownDash_NotNewStation()
    {
        var logbookSession = new FakeLogbookSessionService { WorkedBeforeOutcomeOverride = WorkedBeforeOutcome.Failed };
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.OverrideCallsign = "N0CALL";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WorkedBeforeStatus.Unknown, vm.WorkedBeforeStatus);
        Assert.Equal("—", vm.WorkedBeforeDisplay);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_NotifyQsoLogged_MatchingCallsignCaseInsensitive_RefreshesIndicator()
    {
        var logbookSession = new FakeLogbookSessionService();
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.OverrideCallsign = "N0CALL";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(WorkedBeforeStatus.None, vm.WorkedBeforeStatus);

        // The QSO was just logged (real callsign case may differ from what this pane holds -- the
        // database itself is case-insensitive) -- simulate the record now existing and notify with
        // a DIFFERENT case than OverrideCallsign currently holds.
        logbookSession.Records.Add(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, 14_230_000, null, null, null, null, null, null, null, null, null, null, false, false));
        vm.NotifyQsoLogged("n0call");
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(WorkedBeforeStatus.Found, vm.WorkedBeforeStatus);
    }

    [AvaloniaFact]
    public async Task RxImagePaneViewModel_NotifyQsoLogged_DifferentCallsign_DoesNotOverwriteIndicator()
    {
        var logbookSession = new FakeLogbookSessionService();
        var vm = new RxImagePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService(), logbookSession, new FakeFilePickerService(), new FakeReceiveHistoryStore(), new FakeSettingsStore(), NullLogger<RxImagePaneViewModel>.Instance);
        vm.OverrideCallsign = "N0CALL";
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        var callCountBefore = logbookSession.GetWorkedBeforeCalls.Count;

        // A QSO logged for a DIFFERENT callsign than whatever this pane currently shows must not
        // touch this indicator at all.
        vm.NotifyQsoLogged("W1AW");
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(callCountBefore, logbookSession.GetWorkedBeforeCalls.Count);
        Assert.Equal(WorkedBeforeStatus.None, vm.WorkedBeforeStatus);
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
    public void TxControlsPaneViewModel_LiveAlcLevelSet_LiveAlcPercentDisplayConvertsFractionToPercent()
    {
        // 2026-09-19 user request ("power and alc meter in Output card should show numbers, not
        // bars"): PowerMeterFillPercent/AlcMeterFillPercent (the fill-bar meters' own [0,100] clamp)
        // are removed along with the bars themselves -- LivePowerPercent is now shown directly, and
        // LiveAlcPercentDisplay's own fraction-to-percent conversion is the only remaining behavior
        // worth a dedicated test here.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        Assert.Null(vm.LiveAlcPercentDisplay);

        vm.LiveAlcLevel = 0.75f; // RadioState.AlcLevel is a 0.0-1.0 fraction, not 0-100 -- plan-review finding

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

    // TX history plan (2026-09-01, Fable operator-perspective punch list, "No TX history"). Resend
    // was reworked 2026-09-19 (user request: "leave the current TX editor image alone") to route
    // through a shared TransmitCoreAsync that never reads SelectedMode/_editState/_loadedImage at
    // all, replacing the original IsEditorOpen-based refusal. These tests target exactly what the
    // review rounds for both designs found, not exhaustive coverage of every CanExecute predicate
    // already well-tested elsewhere in this file.

    private static (TxControlsPaneViewModel Vm, FakeSstvSessionService SstvSession, FakeFilePickerService FilePicker, FakeImageSourceWriter ImageSourceWriter) CreateTxHistoryTestSetup()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var filePicker = new FakeFilePickerService();
        var imageSourceWriter = new FakeImageSourceWriter();
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), imageSourceWriter, NullLogger<ReadyRackViewModel>.Instance);
        return (vm, sstvSession, filePicker, imageSourceWriter);
    }

    /// <summary>2026-09-19: Apply no longer closes the editor, so a SECOND call reuses whatever
    /// editor is already open (real content and all) instead of trying to open a new one via
    /// SelectImageCommand, which is now correctly refused while real content is live.</summary>
    private static async Task TransmitOnceAsync(TxControlsPaneViewModel vm)
    {
        var editor = ExtractCurrentEditor(vm) ?? await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await vm.TransmitCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitCompletes_RecordsSentFrameWithTheActuallyTransmittedImage()
    {
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();

        await TransmitOnceAsync(vm);

        var entry = Assert.Single(vm.SentFrames);
        Assert.Equal(TxHistoryOutcome.Completed, entry.Outcome);
        Assert.Equal(TestMode, entry.Mode);
        // Reference-equal to what was actually handed to TransmitAsync -- not some other snapshot.
        Assert.Same(sstvSession.TransmitCalls[0].Image, entry.Image);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitStoppedManuallyAfterProgress_RecordsStoppedOutcome()
    {
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();
        var gate = new TaskCompletionSource();
        sstvSession.TransmitGate = gate;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);

        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Dispatcher.UIThread.RunJobs();
        gate.SetCanceled();
        await transmitTask;

        var entry = Assert.Single(vm.SentFrames);
        Assert.Equal(TxHistoryOutcome.Stopped, entry.Outcome);
    }

    /// <summary>Plan-review finding: recording all 3 outcomes unconditionally would let a zero-RF
    /// cancel/failure (SWR cutoff before audio starts, a device-open failure during radio setup)
    /// evict a real sent frame from the bounded strip. Gated on _anyProgressReported -- no progress,
    /// no entry at all, not even a Stopped one.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitStoppedBeforeAnyProgress_RecordsNothing()
    {
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();
        var gate = new TaskCompletionSource();
        sstvSession.TransmitGate = gate;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);

        gate.SetCanceled();
        await transmitTask;

        Assert.Empty(vm.SentFrames);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SentFrames_EvictsOldestFirst_KeepsNewestFirstOrder()
    {
        // Code-review finding: a Count==6-only assertion survives a mutation swapping
        // RemoveAt(SentFrames.Count - 1) for RemoveAt(0) (evict newest, keep the 6 oldest forever
        // instead). A first attempt at fixing this used only 2 ALTERNATING modes -- mutation-tested
        // and found still vacuous: with Insert(0, ...) always fronting the newest entry, evicting
        // from the front instead of the back happens to keep the same period-2 alternating PATTERN
        // either way, just shifted -- the two outcomes were indistinguishable by mode alone. Each
        // iteration now gets its OWN uniquely-Id'd mode so the two eviction behaviors produce
        // genuinely different, non-coincidentally-matching sequences.
        var modes = Enumerable.Range(0, 8)
            .Select(i => new SstvModeDefinition(Id: $"mode{i}", DisplayName: $"Mode {i}", VisCode: i, ImageWidth: 1, ImageHeight: 1, ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []))
            .ToList();
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();
        sstvSession.AvailableModes = modes;

        foreach (var mode in modes)
        {
            vm.SelectedMode = mode;
            await TransmitOnceAsync(vm);
        }

        Assert.Equal(6, vm.SentFrames.Count);
        // The oldest 2 (mode0, mode1) were evicted; the remaining 6 (mode2..mode7) survive, newest first.
        var expectedNewestFirst = modes.Skip(2).Reverse();
        Assert.Equal(expectedNewestFirst, vm.SentFrames.Select(e => e.Mode));
    }

    /// <summary>TransmitCoreAsync split (2026-09-19): Resend must transmit the exact recorded
    /// image/mode untouched, and must NOT rebake against the live editor's current state (the
    /// opposite of ordinary Transmit, which always refreshes macros). Mirrors
    /// TxControlsPaneViewModel_Transmit_FrequencyMacroChangedSinceApply_RebakesWithFreshValue's own
    /// {freq}-overlay/radio-state-change recipe specifically to prove a resend does NOT rebake from
    /// it, and does not touch SelectedMode either.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ResendSentFrame_TransmitsTheExactRecordedImageAndMode()
    {
        var otherMode = new SstvModeDefinition(
            Id: "other", DisplayName: "Other", VisCode: 1, ImageWidth: 2, ImageHeight: 2,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);
        var preparer = new FakeTransmitImagePreparer();
        var radioSession = new FakeRadioSessionService
        {
            LastKnownState = new RadioState(14_230_000, RadioMode.Usb, IsTransmitting: false, SignalStrengthDb: null, ObservedAt: DateTimeOffset.UtcNow),
        };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode, otherMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) }, new FakeStockImageLibrary(), preparer, new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), radioSession, new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = (OverlayElementViewModel)editor.OverlayElements[0];
        element.Text = "{freq}";
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        await vm.TransmitCommand.ExecuteAsync(null);
        var entry = vm.SentFrames[0];

        // Changed AFTER the first send, before resend -- must NOT cause a rebake, since a resend
        // never reads the live editor's _editState at all.
        radioSession.LastKnownState = radioSession.LastKnownState.Value with { FrequencyHz = 7_045_000 };
        vm.SelectedMode = otherMode;

        await vm.ResendSentFrameCommand.ExecuteAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, sstvSession.TransmitCalls.Count);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[1].Mode); // the entry's own recorded mode
        Assert.Same(entry.Image, sstvSession.TransmitCalls[1].Image); // exact recorded image, not rebaked
        Assert.Equal(otherMode, vm.SelectedMode); // untouched by the resend
    }

    /// <summary>2026-09-19 user request ("leave the current TX editor image alone"): a resend must
    /// succeed even with a real, non-blank editor open at a DIFFERENT mode than the resent entry,
    /// and must not touch that editor's SelectedMode/CurrentSource/OverlayElements at all -- the
    /// opposite of this test's own pre-TransmitCoreAsync-split behavior, which used to refuse
    /// outright to protect an invariant that a shared TransmitCoreAsync now makes structurally
    /// impossible to violate instead.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ResendSentFrame_RealEditorOpenAtDifferentMode_DoesNotDisturbTheLiveEditor()
    {
        var otherMode = new SstvModeDefinition(
            Id: "other", DisplayName: "Other", VisCode: 1, ImageWidth: 2, ImageHeight: 2,
            ColorEncoding: ColorEncoding.RgbSequential, LineSegments: []);
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();
        sstvSession.AvailableModes = [TestMode, otherMode];
        await TransmitOnceAsync(vm); // records the entry at TestMode, leaves the SAME editor open
        var entry = vm.SentFrames[0];

        vm.SelectedMode = otherMode; // reflows the still-open editor via ReplaceEditorForModeSwitch
        var editor = ExtractCurrentEditor(vm)!;
        var originalSource = editor.CurrentSource;
        var originalElementCount = editor.OverlayElements.Count;

        Assert.True(vm.ResendSentFrameCommand.CanExecute(entry));
        await vm.ResendSentFrameCommand.ExecuteAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, sstvSession.TransmitCalls.Count);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[1].Mode);
        Assert.Equal(otherMode, vm.SelectedMode); // untouched
        Assert.Same(originalSource, editor.CurrentSource); // untouched -- no second ReplaceEditorForModeSwitch
        Assert.Equal(originalElementCount, editor.OverlayElements.Count);
        Assert.True(vm.IsEditorOpen);
        Assert.Same(editor, ExtractCurrentEditor(vm));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ResendSentFrame_BlankUntouchedEditorOpen_StillWorks()
    {
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();
        await TransmitOnceAsync(vm);
        var entry = vm.SentFrames[0];
        var appliedEditor = ExtractCurrentEditor(vm)!;
        appliedEditor.ConfirmRequested = _ => Task.FromResult(true);
        await appliedEditor.CancelCommand.ExecuteAsync(null); // "New Template" -- fresh blank editor
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsEditorOpen);

        Assert.True(vm.ResendSentFrameCommand.CanExecute(entry));
        await vm.ResendSentFrameCommand.ExecuteAsync(entry);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, sstvSession.TransmitCalls.Count);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SaveSentFrame_WritesThroughToTheChosenPath()
    {
        var (vm, _, filePicker, imageSourceWriter) = CreateTxHistoryTestSetup();
        filePicker.SavePngPathToReturn = "/tmp/chosen-sent-frame.png";
        await TransmitOnceAsync(vm);
        var entry = vm.SentFrames[0];

        await vm.SaveSentFrameCommand.ExecuteAsync(entry);

        var call = Assert.Single(imageSourceWriter.Calls);
        Assert.Same(entry.Image, call.Source);
        Assert.Equal("/tmp/chosen-sent-frame.png", call.Path);
        // Code-review finding: NOT entry.SourceFileName's own extension (a Browse-sourced photo's
        // real filename, e.g. "vacation.jpg") -- always a fresh ".png" name, matching
        // RxImagePaneViewModel.SaveFrameAsync's own "{timestamp}_{mode}.png" shape.
        Assert.EndsWith($"_{entry.Mode.Id}.png", filePicker.LastSuggestedPngFileName);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SaveSentFrame_PickerCancelled_DoesNotWrite()
    {
        var (vm, _, filePicker, imageSourceWriter) = CreateTxHistoryTestSetup();
        filePicker.SavePngPathToReturn = null;
        await TransmitOnceAsync(vm);
        var entry = vm.SentFrames[0];

        await vm.SaveSentFrameCommand.ExecuteAsync(entry);

        Assert.Empty(imageSourceWriter.Calls);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_Dispose_DisposesEverySentFrameThumbnail()
    {
        var (vm, _, _, _) = CreateTxHistoryTestSetup();
        await TransmitOnceAsync(vm);
        await TransmitOnceAsync(vm);
        var thumbnails = vm.SentFrames.Select(e => (WriteableBitmap)e.Thumbnail).ToList();

        vm.Dispose();

        Assert.All(thumbnails, t => Assert.True(IsWriteableBitmapDisposed(t)));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitFailsAfterProgress_RecordsFailedOutcome()
    {
        var (vm, sstvSession, _, _) = CreateTxHistoryTestSetup();
        var gate = new TaskCompletionSource();
        sstvSession.TransmitGate = gate;

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);

        sstvSession.RaiseTransmitProgress(new TransmitProgressInfo(0.5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));
        Dispatcher.UIThread.RunJobs();
        gate.SetException(new InvalidOperationException("device failed mid-transmit"));
        await transmitTask;

        var entry = Assert.Single(vm.SentFrames);
        Assert.Equal(TxHistoryOutcome.Failed, entry.Outcome);
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
    /// separately find and click Transmit in the sidebar afterward. 2026-09-19 user request: Apply
    /// (plain or with Transmit) no longer closes the editor -- the canvas stays exactly as it was.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ApplyAndTransmitCommand_OnTheOpenEditor_KeepsEditorOpenAndStartsTransmit()
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

        Assert.True(vm.IsEditorOpen);
        Assert.Same(editor, ExtractCurrentEditor(vm));
        Assert.Single(sstvSession.TransmitCalls);
        Assert.Equal(TestMode, sstvSession.TransmitCalls[0].Mode);
    }

    /// <summary>Companion to the test above: while the SIDEBAR Transmit button is running (a resend
    /// of the same applied image), the still-open editor's own Apply &amp; Transmit button must
    /// live-disable -- otherwise clicking it would try to start a second, overlapping transmission.
    /// 2026-09-19: opening a SECOND editor is no longer reachable once Apply keeps the first one
    /// open with real content, so this now exercises the SAME editor instance throughout, which is
    /// the only shape this scenario can take anymore.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ApplyAndTransmitCommand_DisablesWhileSidebarTransmitIsRunning()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.True(editor.ApplyAndTransmitCommand.CanExecute(null));

        sstvSession.BlockUntilCancelled = true;
        var transmitTask = vm.TransmitCommand.ExecuteAsync(null);

        // The SAME still-open editor's Apply & Transmit must reflect the PARENT's live
        // IsTransmitting, not just its own freshly-constructed state.
        Assert.False(editor.ApplyAndTransmitCommand.CanExecute(null));

        vm.StopTransmitCommand.Execute(null);
        await transmitTask;
        Dispatcher.UIThread.RunJobs();

        Assert.True(editor.ApplyAndTransmitCommand.CanExecute(null));
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
    public async Task TxControlsPaneViewModel_CancellingEditor_DisposesTheDiscardedEditor()
    {
        // Tier-0 audit follow-up (production_audit.md): TxImageEditorPaneViewModel.Dispose() is now
        // wired into all 6 of this class's own editor-discard sites (previously dead code). Proxy for
        // "Dispose() actually ran": WorkingCopyBitmap is fed by _workingCopyPool, which
        // WriteableBitmapPool.Dispose() disposes SYNCHRONOUSLY (no Dispatcher.UIThread.Post needed
        // for this one, unlike each element's own bitmap) -- Lock() on the captured reference throws
        // the instant Dispose() has run. WriteableBitmapPoolTests' own established convention:
        // NullReferenceException (Dispose() nulls the internal platform impl), not
        // ObjectDisposedException.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var discardedBitmap = editor.WorkingCopyBitmap;
        Assert.NotNull(discardedBitmap);

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Throws<NullReferenceException>(() => ((WriteableBitmap)discardedBitmap!).Lock());
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ApplyingEditor_KeepsTheAppliedEditorOpenAndAlive()
    {
        // 2026-09-19 user request: Apply no longer closes/disposes the editor -- the operator's
        // canvas stays exactly as it was. Same WorkingCopyBitmap-liveness proxy the old Dispose()-
        // wiring test used, now asserting the OPPOSITE outcome.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        var appliedEditorBitmap = editor.WorkingCopyBitmap;
        Assert.NotNull(appliedEditorBitmap);

        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        ((WriteableBitmap)appliedEditorBitmap!).Lock().Dispose(); // does not throw -- still alive
        Assert.True(vm.IsEditorOpen);
        Assert.Same(editor, ExtractCurrentEditor(vm));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ReplacingABlankEditorWithARealPick_DisposesTheDiscardedBlankEditor()
    {
        // Same Dispose()-wiring follow-up, for the CloseBlankEditorForReplacement discard site -- a
        // DIFFERENT code path from Cancel/Apply above (no EditorClosed-triggered auto-reopen; the
        // caller immediately opens a replacement editor instead).
        // Moved from SelectImageCommand to CopyReceivedImageToTxCommand (2026-09-15): Browse/Stock no
        // longer discard a blank editor at all -- they now load the picked photo directly into the
        // SAME instance (TxImageEditorPaneViewModel.LoadBackground, see OpenEditorForSourceAsync's
        // own new branch), so CloseBlankEditorForReplacement is unreachable from that path anymore.
        // Copy-to-TX still goes through TryClaimEditorSlotForNewSource/CloseBlankEditorForReplacement
        // directly (CopyReceivedImageToTxAsync doesn't route through OpenEditorForSourceAsync at
        // all), so it's still the real, reachable exerciser of this exact dispose site.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var receivedImage = new FakeReceivedImageBuffer { Current = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, receivedImage, new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var blankEditor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var discardedBitmap = blankEditor.WorkingCopyBitmap;
        Assert.NotNull(discardedBitmap);

        var realEditor = await OpenEditorAsync(vm, () => vm.CopyReceivedImageToTxCommand.ExecuteAsync(null));

        Assert.NotSame(blankEditor, realEditor);
        Assert.Throws<NullReferenceException>(() => ((WriteableBitmap)discardedBitmap!).Lock());
    }

    // User-requested (2026-09-15): "even if there are elements on the canvas, if no background has
    // been picked before, i should be able to load one later also, not only as first canvas element."
    // The core scenario this whole feature is for: elements/edits already exist, no real background
    // yet -- Browse/Stock must stay reachable AND must not discard any of that existing work.

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectImageCommand_WithOverlayElementsButNoBackground_LoadsInPlacePreservingTheElements()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = Assert.Single(editor.OverlayElements);
        Assert.True(vm.CanLoadBackground, "no real background yet, so Browse/Stock must stay reachable even with an element already on the canvas.");
        Assert.False(vm.CanChangeSourceOrMode, "mode-select stays locked regardless -- a mode change is disruptive independent of background state.");

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(element, Assert.Single(editor.OverlayElements));
        Assert.True(editor.HasRealBackground);
        Assert.Equal(9, editor.CurrentSource.Width);
        Assert.False(vm.CanLoadBackground, "a real background now exists, so Browse/Stock must re-lock.");
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectStockImageCommand_WithOverlayElementsButNoBackground_LoadsInPlacePreservingTheElements()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var stockEntry = new StockImageEntry("s1", "stock.jpg", "/tmp/stock.jpg");
        var stockLibrary = new FakeStockImageLibrary { FullImageToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), stockLibrary, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = Assert.Single(editor.OverlayElements);

        await vm.SelectStockImageCommand.ExecuteAsync(stockEntry);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(element, Assert.Single(editor.OverlayElements));
        Assert.True(editor.HasRealBackground);
        Assert.Equal(9, editor.CurrentSource.Width);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_SelectImageCommand_LoadFailsWithOverlayElementsButNoBackground_LeavesTheEditorUntouched()
    {
        // Unlike the wholesale-replace failure path (which used to nuke the whole editor even for a
        // Browse failure), a failed IN-PLACE background load must not touch anything -- there's real,
        // otherwise-untouched work (the element) still sitting there the operator hasn't lost yet.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader();
        imageFileLoader.FailForPath["/tmp/broken.png"] = new InvalidOperationException("boom");
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/broken.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        var element = Assert.Single(editor.OverlayElements);

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Same(element, Assert.Single(editor.OverlayElements));
        Assert.False(editor.HasRealBackground);
        Assert.True(vm.IsEditorOpen);
        Assert.Same(editor, ExtractCurrentEditor(vm));
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CancellingEditorWithAnImageElement_DisposesTheElementsOwnBitmapToo()
    {
        // Auditor code-review nit (Tier-0 audit follow-up, production_audit.md): the 3 Dispose()-
        // wiring tests above only prove the pool half (WorkingCopyBitmap/PreviewImage) -- none of them
        // add an OverlayElements entry, so the new foreach-dispose loop in
        // TxImageEditorPaneViewModel.Dispose() went unexercised. This one adds a real
        // ImageElementViewModel first. ImageElementViewModel.Dispose() defers its own
        // CanvasBitmap.Dispose() via Dispatcher.UIThread.Post (Background priority, same as the
        // editor's own pool disposal after the auditor's deferred-dispose fix) -- RunJobs() flushes
        // both.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(2, 2, new Rgb24[4]) };
        var vm = new TxControlsPaneViewModel(sstvSession, loader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), picker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        await editor.AddImageFromFileCommand.ExecuteAsync(null);
        var element = (ImageElementViewModel)Assert.Single(editor.OverlayElements);
        var elementBitmap = element.CanvasBitmap;

        // Adding an image element sets HasUnsavedEdits, so Cancel's own confirm dialog gate needs
        // ConfirmRequested wired here, unlike the untouched-blank-editor Cancel tests above.
        editor.ConfirmRequested = _ => Task.FromResult(true);
        await editor.CancelCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Throws<NullReferenceException>(() => elementBitmap.Lock());
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ImageLoadCompletingAfterEditorIsDisposed_DoesNotResumeThePreviewPipeline()
    {
        // Auditor code-review blocker (Tier-0 audit follow-up, production_audit.md): before
        // RecomputePreview()/NotifyWorkingCopyGeometryChanged() gained their own _disposed guards, an
        // await-crossing operation still in flight when Dispose() ran (here: AddImageFromFileAsync's
        // own await on the image loader) would resume into RecomputePreviewPipeline() ->
        // _previewPool.Blit() against an already-disposed pool -- wasted work against a discarded
        // instance at best, an unhandled WriteableBitmap crash on a real (non-headless) render target
        // at worst, since Cancel has no busy gate to block a still-loading Add-image click.
        //
        // Asserted via PreviewImage's own object IDENTITY, not a thrown exception -- confirmed by
        // direct experiment that a thrown-exception assertion here would NOT be mutation-sensitive:
        // WriteableBitmapPool.Dispose()'s own idempotency fix (nulls _a/_b after disposing, see that
        // method's own comment) means EnsureSized's `slot is not null` check is false post-dispose, so
        // Blit() silently REALLOCATES a fresh bitmap instead of touching (and crashing on) the
        // disposed one -- removing ONLY the guards here, with that nulling fix still in place, produces
        // no exception at all, just a silent, wrong reassignment. Identity is the one signal that
        // catches that: the guard's early-return is what keeps PreviewImage from being reassigned to a
        // different reference once the late continuation resumes; without it, PreviewImage silently
        // "resurrects" content on a discarded editor even though nothing crashes.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var picker = new FakeFilePickerService { PathToReturn = "/tmp/picked.jpg" };
        var loader = new FakeImageFileLoader { UseManualGating = true };
        var vm = new TxControlsPaneViewModel(sstvSession, loader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), picker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var previewBeforeDispose = editor.PreviewImage;

        // FakeFilePickerService.PickImageFileAsync completes synchronously (its own established
        // convention), so by the time ExecuteAsync returns control here (unawaited), execution has
        // already run synchronously through to the genuinely-gated loader call below.
        var addTask = editor.AddImageFromFileCommand.ExecuteAsync(null);
        var pending = Assert.Single(loader.PendingLoads);

        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        pending.SetResult(new ArrayImageSource(2, 2, new Rgb24[4]));
        await addTask;

        Assert.Same(previewBeforeDispose, editor.PreviewImage);
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
    public async Task TxControlsPaneViewModel_SelectImageCommand_AllowedWhileTheBlankPlaceholderEditorIsOpen_LoadsItIntoTheSameEditor()
    {
        // User-requested (2026-09-15): "even if there are elements on the canvas, if no background
        // has been picked before, i should be able to load one later also, not only as first canvas
        // element." Superseded this test's own original premise (Browse REPLACED the blank editor
        // with a brand-new instance) -- it now loads the picked photo directly into the SAME editor
        // via TxImageEditorPaneViewModel.LoadBackground, so OverlayElements/adjustments/crop survive
        // too, not just the blank-editor case this test happens to cover. No EditorOpened fires (same
        // instance stays open), so this drives SelectImageCommand directly instead of through
        // OpenEditorAsync's own "wait for EditorOpened" choreography.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var blankEditor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        Assert.True(vm.CanChangeSourceOrMode);
        Assert.False(blankEditor.HasRealBackground);

        await vm.SelectImageCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsEditorOpen);
        Assert.True(blankEditor.HasRealBackground);
        Assert.Equal(9, blankEditor.CurrentSource.Width);
        Assert.False(vm.CanChangeSourceOrMode, "a real photo pick must re-lock mode-select, same as before this fix.");
        Assert.False(vm.CanLoadBackground, "and re-lock Browse/Background too, now that a real background exists.");
    }

    // User-reported bug (2026-09-15): "once i removed the background however stock browse and open
    // editor are still greyed out." RemoveBackgroundCommand pushes a real undo step (HasUnsavedEdits
    // flips true), and _currentEditorIsBlank is a one-shot snapshot from editor-open time that never
    // retroactively flips true just because the editor's CURRENT state went blank mid-session -- so
    // the OLD gate stayed locked forever after, even though there was nothing left to protect. Fixed
    // via TxImageEditorPaneViewModel.HasNoBackgroundOrOverlayElements, a live check OR'd into
    // IsCurrentEditorBlankAndUntouched alongside the existing snapshot check.

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RemoveBackgroundOnARealPhotoWithNoOtherElements_ReEnablesBrowseStockAndOpenEditor()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        Assert.False(vm.CanChangeSourceOrMode, "sanity: a real photo pick starts locked, same as the existing coverage above.");

        editor.RemoveBackgroundCommand.Execute(null);

        Assert.True(vm.CanChangeSourceOrMode, "removing the background left nothing to protect, so Browse/Stock/Open Editor must re-enable.");
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_RemoveBackgroundWithAnOtherOverlayElementStillPresent_StaysLocked()
    {
        // Must NOT relax when OTHER real work (an added element) would still be silently discarded --
        // only the specific "genuinely nothing left" case.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);

        editor.RemoveBackgroundCommand.Execute(null);

        Assert.False(vm.CanChangeSourceOrMode, "an added text element is still real work a Browse/Stock click would silently discard.");
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
        // Real, UI-reachable sequence (2026-09-19: reached via "New Template" now that Apply no
        // longer closes the editor by itself): Apply an image (sets _editState/_loadedImage at
        // modeA's dimensions, editor stays open with real content), THEN click "New Template"
        // (CancelCommand -- the only remaining editor-closing action, and OnEditorCancelled always
        // auto-reopens a fresh blank editor). _editState survives untouched (Cancel doesn't clear a
        // prior Apply's state). Changing mode with the blank editor now open used to leave
        // _loadedImage stale at modeA's pixel dimensions while SelectedMode moved to modeB -- a
        // mismatch AnalogFmSstvEncoder throws on, surfacing only as a context-free "Transmit failed".
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

        editor.ConfirmRequested = _ => Task.FromResult(true);
        await OpenEditorAsync(vm, () => editor.CancelCommand.ExecuteAsync(null));
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
        // OpenEditorWithLoadedSourceAsync) sets ErrorMessage on failure -- this one didn't, so a
        // picker failure was indistinguishable from the user pressing Cancel.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var filePicker = new FakeFilePickerService { ThrowOnPickImageFile = new InvalidOperationException("picker unavailable") };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        await vm.SelectImageCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditorOpen);
        Assert.NotNull(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_DecliningNewTemplateConfirm_LeavesThePreviouslyAppliedImageUntouched()
    {
        // 2026-09-19: opening a SECOND editor after Apply is no longer reachable (Apply keeps the
        // first one open with real content) -- the still-relevant scenario this test now covers is
        // declining "New Template"'s own confirm dialog, which must leave the applied state exactly
        // as it was, same editor instance and all.
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.TransmitCommand.CanExecute(null));
        var loadedAfterApply = ExtractLoadedImage(vm);

        editor.AddOverlayElementCommand.Execute(null); // a real unsaved edit, so Cancel must confirm
        editor.ConfirmRequested = _ => Task.FromResult(false); // operator declines "New Template"
        await editor.CancelCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsEditorOpen);
        Assert.Same(editor, ExtractCurrentEditor(vm));
        Assert.True(vm.TransmitCommand.CanExecute(null));
        Assert.Same(loadedAfterApply, ExtractLoadedImage(vm));
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
    public async Task TxControlsPaneViewModel_EditorOpenWithRealContent_QuickSelectModeSwitchesTheEditorToTheNewMode()
    {
        // Mode-switch-mid-edit feature: this used to be
        // TxControlsPaneViewModel_EditorOpen_DisablesTheQuickSelectModeCommand, regression coverage
        // for High item 2's blanket block on any mode change while the editor had real content.
        // That block is gone -- ReplaceEditorForModeSwitch now handles this case instead of
        // refusing it, so a populated editor's mode CAN be switched, replacing the editor instance
        // with one targeting the new mode.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(9, 7, new Rgb24[63]) };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        Assert.True(vm.QuickSelectModeCommand.CanExecute("other"));

        var firstEditor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        Assert.True(vm.IsEditorOpen);
        Assert.True(vm.QuickSelectModeCommand.CanExecute("other"));

        var reopened = await OpenEditorAsync(vm, () =>
        {
            vm.QuickSelectModeCommand.Execute("other");
            return Task.CompletedTask;
        });

        Assert.Equal("other", vm.SelectedMode?.Id);
        Assert.True(vm.IsEditorOpen);
        Assert.NotSame(firstEditor, reopened);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_QuickSelectMode_AllowedOnceTheBlankEditorHasARealEdit_ReflowsElementsToTheNewMode()
    {
        // Mode-switch-mid-edit feature: this used to be
        // TxControlsPaneViewModel_QuickSelectMode_DisallowedOnceTheBlankEditorHasARealEdit,
        // asserting the OLD blanket block on any unsaved edit. Also verifies the reflow itself: the
        // added element's X/Y/Width/Height are 0..1 fractions of the target mode's own
        // ImageWidth/ImageHeight, so the SAME normalized values are correct unchanged against a
        // differently-sized new mode -- no rescale math needed, confirmed against a mode with
        // different ImageWidth/ImageHeight than TestMode's degenerate 1x1.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        var canExecuteChangedCount = 0;
        vm.QuickSelectModeCommand.CanExecuteChanged += (_, _) => canExecuteChangedCount++;

        editor.AddOverlayElementCommand.Execute(null);
        var original = Assert.Single(editor.OverlayElements);
        var (originalX, originalY, originalWidth, originalHeight) = (original.X, original.Y, original.Width, original.Height);

        Assert.True(editor.HasUnsavedEdits);
        Assert.True(vm.QuickSelectModeCommand.CanExecute(modeB.Id));
        Assert.True(canExecuteChangedCount > 0, "HasUnsavedEdits flipping must re-notify CanExecute via OnCurrentEditorPropertyChanged.");

        var reopened = await OpenEditorAsync(vm, () =>
        {
            vm.QuickSelectModeCommand.Execute(modeB.Id);
            return Task.CompletedTask;
        });

        Assert.Equal(modeB.Id, vm.SelectedMode?.Id);
        Assert.NotSame(editor, reopened);
        var reflowed = Assert.Single(reopened.OverlayElements);
        Assert.Equal(originalX, reflowed.X);
        Assert.Equal(originalY, reflowed.Y);
        Assert.Equal(originalWidth, reflowed.Width);
        Assert.Equal(originalHeight, reflowed.Height);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeSwitchMidEdit_CarriesForwardHasUnsavedEditsForCancelConfirm()
    {
        // Auditor round-1 blocker: a freshly constructed editor has an empty undo stack, so without
        // carrying HasUnsavedEdits forward, Cancel's confirm dialog would silently skip after a
        // mode switch and discard a populated canvas with no warning.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        var editor = await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));
        editor.AddOverlayElementCommand.Execute(null);
        Assert.True(editor.HasUnsavedEdits);

        var reopened = await OpenEditorAsync(vm, () =>
        {
            vm.QuickSelectModeCommand.Execute(modeB.Id);
            return Task.CompletedTask;
        });

        Assert.True(reopened.HasUnsavedEdits);
        var confirmRequested = false;
        reopened.ConfirmRequested = _ =>
        {
            confirmRequested = true;
            return Task.FromResult(false);
        };
        await reopened.CancelCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(confirmRequested, "Cancel must still ask for confirmation after a mode switch carried forward unsaved edits.");
        Assert.True(vm.IsEditorOpen, "Declining the confirm dialog must leave the (reflowed) editor open, same as any other Cancel decline.");
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_IsTransmitting_BlocksModeSwitch()
    {
        // Policy choice, not a safety requirement (TransmitAsync captures its own image/mode
        // locals) -- switching resolution while actively sending the current image is confusing UX
        // the user didn't ask for.
        var modeA = TestMode;
        var modeB = TestMode with { Id = "other", ImageWidth = 2, ImageHeight = 2 };
        var sstvSession = new FakeSstvSessionService { AvailableModes = [modeA, modeB] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance, NullLogger<TxImageEditorPaneViewModel>.Instance, new FakeReceivedImageBuffer(), new FakeReceiveHistoryStore(), new FakeTemplateStore(), new FakeImageSourceWriter(), NullLogger<ReadyRackViewModel>.Instance);
        vm.SelectedMode = modeA;
        await OpenEditorAsync(vm, () => vm.OpenBlankEditorCommand.ExecuteAsync(null));

        // Auditor round-3 blocker: asserting the property/CanExecute VALUES alone is false
        // confidence -- CanChangeMode/CanQuickSelectMode must actually be RE-NOTIFIED when
        // IsTransmitting flips, or a bound ComboBox/Button would stay visibly enabled for the
        // whole transmission even though the property itself now reads false underneath.
        var canChangeModeChanged = false;
        vm.PropertyChanged += (_, e) => canChangeModeChanged |= e.PropertyName == nameof(vm.CanChangeMode);
        var quickSelectCanExecuteChangedCount = 0;
        vm.QuickSelectModeCommand.CanExecuteChanged += (_, _) => quickSelectCanExecuteChangedCount++;

        vm.IsTransmitting = true;

        Assert.True(canChangeModeChanged, "IsTransmitting must raise PropertyChanged for CanChangeMode, or the ComboBox's IsEnabled binding never re-evaluates.");
        Assert.True(quickSelectCanExecuteChangedCount > 0, "IsTransmitting must re-notify QuickSelectModeCommand's CanExecute.");
        Assert.False(vm.CanChangeMode);
        Assert.False(vm.QuickSelectModeCommand.CanExecute(modeB.Id));
        vm.QuickSelectModeCommand.Execute(modeB.Id);
        Assert.Equal(modeA.Id, vm.SelectedMode?.Id);
    }

    /// <summary>2026-09-19: this used to block auto-follow entirely whenever the editor was open,
    /// as a blunt guard against the stale-mode crash (see this file's own historical incident notes)
    /// from before <c>ReplaceEditorForModeSwitch</c> existed. That mechanism (built and audited
    /// earlier this session specifically to make an editor-open mode switch safe) is the real fix
    /// now, and legacy's own <c>TrackTxMode</c> (<c>Main.cpp:4907-4914</c>) has no editor/window-open
    /// check at all -- it follows RX mode regardless of what's on the TX side, gated only on
    /// Fixed-TX-Mode/TX-active/width-match. Rewritten to prove the real fix: auto-follow now goes
    /// through even with a real, non-blank editor open, and the editor reflows safely instead of
    /// crashing or going stale.</summary>
    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_ModeDetected_WhileEditorOpen_FollowsAndSafelyReflowsTheEditor()
    {
        var modeA = TestMode;
        // Same width as modeA -- auto-follow's own width-match guard (ported from legacy's
        // TrackTxMode) requires this; a different HEIGHT is enough to still prove a genuine reflow.
        var modeB = TestMode with { Id = "other", ImageWidth = 1, ImageHeight = 3 };
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

        var editor = await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));
        editor.ApplyCommand.Execute(null); // populates _editState/_loadedImage -- Apply no longer closes the editor
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsEditorOpen);
        Assert.True(vm.AutoFollowRxMode);

        sstvSession.RaiseModeDetected(modeB);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("other", vm.SelectedMode?.Id);
        Assert.True(vm.IsEditorOpen);
        var reflowedEditor = ExtractCurrentEditor(vm)!;
        Assert.NotSame(editor, reflowedEditor); // TargetModeHeightPx is init-only -- a new instance
        // CurrentSource stays the ORIGINAL, un-resized photo (the editor carries the same picture
        // forward, it doesn't pre-resize it) -- the actual TX-ready pipeline output is _loadedImage.
        Assert.Equal((modeB.ImageWidth, modeB.ImageHeight), (ExtractLoadedImage(vm)!.Width, ExtractLoadedImage(vm)!.Height));
    }

    /// <summary>Covers <see cref="TxControlsPaneViewModel.IsEditorOpen"/>'s own open/close lifecycle
    /// only -- NOT the actual XAML <c>IsEnabled="{Binding !IsEditorOpen}"</c> binding on the mode
    /// ComboBox in <c>TxControlsPaneView.axaml</c> (code-review nit; still not exercised by any
    /// automated test here). That binding sits directly under this view's own root
    /// <c>x:DataType="vm:TxControlsPaneViewModel"</c>, so Avalonia compiles and type-checks it at
    /// build time -- a path typo would be a build error, not a silent runtime failure.
    /// 2026-09-19: Apply no longer closes the editor -- only "New Template" (CancelCommand) does,
    /// so that is what this test now exercises for the close half of the lifecycle.</summary>
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
        Assert.True(vm.IsEditorOpen, "Apply must keep the editor open.");

        editor.ConfirmRequested = _ => Task.FromResult(true);
        await editor.CancelCommand.ExecuteAsync(null); // "New Template" -- the only remaining close path

        Assert.True(vm.IsEditorOpen); // auto-reopened blank, per OnEditorCancelled's own contract
        Assert.NotSame(editor, ExtractCurrentEditor(vm));
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

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_Constructed_SetsIsLoadingTrueUntilTheInitialQueryCompletes()
    {
        // User-reported (2026-09-20): switching Today/All (or the initial startup load) can be
        // noticeably slow against a large SQLite history -- IsLoading must be true for the WHOLE
        // duration of the constructor's own fire-and-forget initial RefreshAsync call, not just a
        // manually-triggered RefreshCommand, since that's the exact case the user asked about.
        var gate = new TaskCompletionSource<IReadOnlyList<ReceiveHistoryEntry>>();
        var historyStore = new FakeReceiveHistoryStore { QueryGate = gate };

        var vm = CreateRxHistoryPaneViewModel(historyStore);

        Assert.True(vm.IsLoading);
        // Entries is still empty (the gated query hasn't resolved yet) -- the "No history yet"
        // empty-state message must stay hidden while IsLoading, not flash on before the real
        // result lands.
        Assert.False(vm.ShowNoHistoryMessage);

        gate.SetResult([]);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsLoading);
        Assert.True(vm.ShowNoHistoryMessage);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_RefreshCommand_ClearsIsLoadingEvenWhenTheQueryThrows()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsLoading);

        historyStore.ThrowOnQuery = new InvalidOperationException("simulated DB failure");
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsLoading);
        Assert.NotNull(vm.ErrorMessage);
    }

    /// <summary>ui_transition_plan.md step 9 (T2-7): the Storage card's folder path was previously
    /// read-only text -- this makes the already-auto-archived location actually reachable.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_OpenStorageFolderCommand_OpensTheResolvedImagesDirectory()
    {
        // One value, used as both the input and the expectation. The command passes the store's
        // directory through untouched, so rebuilding the expectation from Path.GetTempPath() -- as an
        // earlier fix here did -- asserts against a DIFFERENT path on Windows and fails for a reason
        // that has nothing to do with the code under test.
        const string imagesDirectory = "/tmp/scanlinestudio-history";

        var historyStore = new FakeReceiveHistoryStore { ImagesDirectory = imagesDirectory };
        var urlLauncher = new FakeUrlLauncher();
        var vm = CreateRxHistoryPaneViewModel(historyStore, urlLauncher: urlLauncher);
        await vm.LoadImagesDirectoryAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.OpenStorageFolderCommand.CanExecute(null));

        vm.OpenStorageFolderCommand.Execute(null);

        Assert.Contains(imagesDirectory, urlLauncher.OpenedUrls);
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
        // Derived, not a literal: the command opens the CONTAINING DIRECTORY of the entry's file,
        // and Path.GetDirectoryName produces platform separators. A "/tmp" literal only matches on
        // POSIX.
        Assert.Equal(
            Path.GetDirectoryName(requested.Current!.Entry.FilePath),
            Assert.Single(urlLauncher.OpenedUrls));
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
            new ReceiveHistoryEntry("gone", DateTimeOffset.UtcNow, "robot36", "/tmp/gone.png", null, ReceiveDecodeState.Completed), thumbnail: null);

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
    public async Task RefreshAsync_LoadsGalleryThumbnails_AtThe240pxCap()
    {
        // yoniq-auditor nit (2026-09-15): the 96->240 thumbnail-resolution bump itself was
        // previously untested -- only the separate 512px PreviewImage cap had a pinning test
        // (RxHistoryPaneViewModel_SelectingAnEntry_LoadsFullPreview... above uses MaxDimension == 512).
        var entry = new ReceiveHistoryEntry("e1", DateTimeOffset.Now, "robot36", "/tmp/e1.png", null, ReceiveDecodeState.Completed);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [entry],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };

        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(historyStore.ThumbnailLoadCalls, c => c.EntryId == "e1" && c.MaxDimension == 240);
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
        // a refresh rebuilds the instance of any changed row (no identity beyond reference), so
        // without re-selecting by Entry.Id after repopulating, a user
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
        // A refresh keeps an unchanged row's instance; the selected row also changed here (callsign
        // attached), so it is rebuilt and only the re-select-by-Id logic can keep it selected.
        historyStore.EntriesToReturn[0] = selectedEntry with { DecodedCallsign = "N0CALL" };
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
    public async Task RxHistoryPaneViewModel_RapidNotesOnDifferentEntries_BothSurviveReload()
    {
        var store = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [
                new ReceiveHistoryEntry("A", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("B", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
        };
        var vm = CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "A");
        vm.SelectedEntryNote = "note A";
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "B");
        vm.SelectedEntryNote = "note B";
        await Task.Delay(750);
        Dispatcher.UIThread.RunJobs();
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("note A", vm.Entries.Single(e => e.Entry.Id == "A").Entry.Note);
        Assert.Equal("note B", vm.Entries.Single(e => e.Entry.Id == "B").Entry.Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_NewerNoteWaitsForOlderWrite_AndWins()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [
                new ReceiveHistoryEntry("A", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("B", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
            SetNoteGate = async (id, note) =>
            {
                if (id == "A" && note == "old") { entered.SetResult(); await release.Task; }
            },
        };
        var vm = CreateRxHistoryPaneViewModel(store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "A");
        vm.SelectedEntryNote = "old";
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "B");
        vm.SelectedEntryNote = "B";
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "A");
        vm.SelectedEntryNote = "new";
        await Task.Delay(750);
        Assert.DoesNotContain(store.SetNoteCalls, c => c.Note == "new");
        release.SetResult();
        await Task.Delay(100);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("new", vm.SelectedEntryNote);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("new", vm.Entries.Single(e => e.Entry.Id == "A").Entry.Note);
        Assert.Equal("B", vm.Entries.Single(e => e.Entry.Id == "B").Entry.Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_FailedNoteWriteWithThrowingLogger_DoesNotPoisonLaterEdits()
    {
        // The per-entry write chain stores the task it just awaited. If that task can fault, the
        // faulted instance stays in the chain and every later edit for the entry rethrows it at its
        // own `await previous`, silently -- the caller is fire-and-forget. A throwing dispatcher post
        // is not injectable here (static dispatcher, real headless pump), so this drives the same
        // mechanism through the catch arm: the store throws, then the logger inside that catch throws.
        var store = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [
                new ReceiveHistoryEntry("A", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
            ],
            SetNoteGate = (id, note) => note == "first"
                ? throw new IOException("Injected note write failure")
                : Task.CompletedTask,
        };
        var vm = CreateRxHistoryPaneViewModel(store, logger: new ThrowOnSetNoteFailedLogger());
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "A");

        vm.SelectedEntryNote = "first";
        await Task.Delay(750);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntryNote = "second";
        await Task.Delay(750);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(store.SetNoteCalls, c => c.EntryId == "A" && c.Note == "second");
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("second", vm.Entries.Single(e => e.Entry.Id == "A").Entry.Note);
    }

    /// <summary>Throws only for the note-failure message, so the rest of the pane's setup logging
    /// still works -- a blanket throwing logger would derail RefreshCommand before the assertion.</summary>
    private sealed class ThrowOnSetNoteFailedLogger : ILogger<RxHistoryPaneViewModel>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).Contains("note", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("logger provider failed");
            }
        }
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

    /// <summary>Gallery right-click "Flag"/"Unflag" (2026-09-19 user request). Unlike the details
    /// panel's own CheckBox (which the test above drives via SelectedEntryIsFlagged directly), this
    /// command is the code-behind's own entry point -- exercises the SAME persistence path with no
    /// prior read of the current value from the test itself, proving the command's own
    /// !SelectedEntryIsFlagged toggle reads the freshly-selected entry's REAL current state, not a
    /// stale default.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_ToggleSelectedEntryFlagCommand_TogglesAndPersists()
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
        Assert.False(vm.SelectedEntryIsFlagged);

        vm.ToggleSelectedEntryFlagCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(50);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.SelectedEntryIsFlagged);
        Assert.True(historyStore.EntriesToReturn[0].IsFlagged);
    }

    /// <summary>Gallery right-click "Add note..." (2026-09-19 user request). Confirms three things at
    /// once: the dialog is prefilled with the entry's CURRENT note (not blank), the write persists
    /// IMMEDIATELY with no debounce wait (unlike continuous typing in the details panel's own
    /// TextBox), and SelectedEntryNote reflects it right away for the still-visible panel.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AddNoteToSelectedEntryCommand_PrefillsAndPersistsImmediately()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "old note")],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        string? prefilledText = null;
        vm.TextPromptRequested = promptVm =>
        {
            prefilledText = promptVm.Text;
            return Task.FromResult<string?>("new note from dialog");
        };

        await vm.AddNoteToSelectedEntryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("old note", prefilledText);
        Assert.Equal("new note from dialog", vm.SelectedEntryNote);
        Assert.Equal("new note from dialog", historyStore.EntriesToReturn[0].Note);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AddNoteToSelectedEntryCommand_CancelledDialog_LeavesNoteUnchanged()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "keep me")],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        vm.TextPromptRequested = _ => Task.FromResult<string?>(null); // Cancel

        await vm.AddNoteToSelectedEntryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("keep me", vm.SelectedEntryNote);
        Assert.Equal("keep me", historyStore.EntriesToReturn[0].Note);
    }

    /// <summary>Code-review-class finding, pinned proactively: the dialog await is a genuine,
    /// potentially long gap during which a background Recorded-triggered RefreshAsync could reassign
    /// SelectedEntry to a DIFFERENT entry before OK is clicked -- same race class
    /// DeleteSelectedEntryAsync/ExportFrameAsync's own doc comments already guard against for their
    /// own awaits. The note must land on the entry the user actually right-clicked (captured before
    /// the dialog), never on whatever SelectedEntry has since become.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AddNoteToSelectedEntryCommand_SelectionChangesDuringDialog_StillTargetsTheOriginalEntry()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var originalEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        vm.SelectedEntry = originalEntry;
        Dispatcher.UIThread.RunJobs();

        vm.TextPromptRequested = promptVm =>
        {
            // Simulates a background refresh reassigning SelectedEntry WHILE the dialog is open.
            vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
            Dispatcher.UIThread.RunJobs();
            return Task.FromResult<string?>("note for entry 1");
        };

        await vm.AddNoteToSelectedEntryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("note for entry 1", historyStore.EntriesToReturn[0].Note);
        Assert.Null(historyStore.EntriesToReturn[1].Note);
        // The now-current selection's own note field must NOT have been overwritten either.
        Assert.NotEqual("note for entry 1", vm.SelectedEntryNote);
    }

    /// <summary>yoniq-auditor code-review blocker (2026-09-19): the first draft's "still selected"
    /// re-check used <c>ReferenceEquals(SelectedEntry, entry)</c>, which reads FALSE even when the
    /// SAME logical entry is still selected, because RefreshAsync reselects by Id with a freshly
    /// constructed RxHistoryEntryViewModel (its own doc comment: "every item above is a
    /// freshly-constructed record"). That left the visible SelectedEntryNote TextBox showing the
    /// stale pre-dialog note after a refresh landed mid-dialog on the SAME entry -- this test drives
    /// a real RefreshCommand (not a hand-swapped reference) to prove the fix (Id comparison) actually
    /// refreshes the display, which the sibling "different entry" test above cannot exercise.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AddNoteToSelectedEntryCommand_RefreshLandsOnTheSameEntryDuringDialog_StillUpdatesTheVisibleNoteAfterward()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, Note: "old note")],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var originalEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        vm.SelectedEntry = originalEntry;
        Dispatcher.UIThread.RunJobs();

        vm.TextPromptRequested = async promptVm =>
        {
            // A real RefreshAsync (e.g. IReceiveHistoryStore.Recorded firing for an unrelated new
            // frame) landing WHILE the dialog is open, reselecting entry "1" by Id with a brand-new
            // RxHistoryEntryViewModel instance -- NOT the same reference as originalEntry. A refresh
            // keeps an unchanged row's instance, so the row changes (callsign attached) to force one.
            historyStore.EntriesToReturn[0] = historyStore.EntriesToReturn[0] with { DecodedCallsign = "N0CALL" };
            await vm.RefreshCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            var reselected = vm.SelectedEntry;
            Assert.NotNull(reselected);
            Assert.Equal("1", reselected!.Entry.Id);
            Assert.NotSame(originalEntry, reselected); // confirms this is genuinely a NEW instance
            return "new note from dialog";
        };

        await vm.AddNoteToSelectedEntryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("new note from dialog", historyStore.EntriesToReturn[0].Note);
        Assert.Equal("new note from dialog", vm.SelectedEntryNote); // must NOT still show the stale pre-dialog note
    }

    /// <summary>yoniq-auditor code-review finding (2026-09-19): typing in the Selected-frame panel's
    /// own TextBox arms a 600ms debounce (OnSelectedEntryNoteChanged); if the operator then opens and
    /// OKs the Add-note dialog for the SAME entry within that window, the debounce's OLD captured
    /// text would otherwise fire afterward and chain onto _noteWrites AFTER the dialog's write,
    /// silently clobbering it -- a last-writer-wins loss with no error. AddNoteToSelectedEntryAsync
    /// must cancel that pending debounce before persisting its own write.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_AddNoteToSelectedEntryCommand_CancelsAPendingPanelDebounce_SoItCannotClobberTheDialogsWrite()
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

        vm.SelectedEntryNote = "stale typed text"; // arms a 600ms debounce, not yet persisted
        Dispatcher.UIThread.RunJobs();
        Assert.Null(historyStore.EntriesToReturn[0].Note);

        vm.TextPromptRequested = _ => Task.FromResult<string?>("note from dialog");
        await vm.AddNoteToSelectedEntryCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("note from dialog", historyStore.EntriesToReturn[0].Note);

        // The pending debounce from before must be CANCELLED, not just outrun by timing -- wait past
        // its own 600ms window and confirm the dialog's write survives instead of being overwritten.
        await Task.Delay(700);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("note from dialog", historyStore.EntriesToReturn[0].Note);
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

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_NoLinkedQsoButDecodedCallsign_SeedsHisCallFromDecodedCallsign()
    {
        // fsk_cwid.md A-P3b: "seed his_call from DecodedCallsign in SendSelectedEntryToTxAsync ...
        // when no QSO is linked" -- only when no QSO is linked (LinkedQsoId null); a linked QSO's own
        // callsign, tested above, is the authoritative source and takes precedence.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, DecodedCallsign: "W1AW")],
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

        Assert.NotNull(captured!.ContactVariables);
        Assert.Equal("W1AW", captured.ContactVariables!["his_call"]);
        Assert.False(captured.ContactVariables!.ContainsKey("his_grid"));
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SendSelectedEntryToTx_LinkedQsoAndDecodedCallsign_QsoTakesPrecedence()
    {
        // Auditor code-review nit (A-P3b): the two "no QSO" / "has QSO" tests above never construct
        // an entry with BOTH present -- a mutated `else if` -> `if` (decoded callsign always applied
        // regardless of a linked QSO) survives the whole suite without this. Pins that the linked
        // QSO's own callsign, not DecodedCallsign, wins when both exist.
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Completed, DecodedCallsign: "K9XYZ")],
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

        Assert.Equal("W1AW", captured!.ContactVariables!["his_call"]);
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

    // Gallery log-entry-summary plan (2026-09-01): "Log entry" row shows the linked QSO's
    // callsign/date instead of a plain logged/not-logged boolean. FakeLocalizationService.GetString
    // returns the raw KEY, not a formatted string (see its own doc comment) -- so asserting the
    // exact key below genuinely distinguishes "resolved from the QSO lookup" from "fell back to the
    // plain Logged text", not just "something non-null happened".

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingEntryWithLinkedQso_ResolvesLinkedQsoDisplayFromTheQsoLookup()
    {
        var qsoStart = DateTimeOffset.UtcNow;
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Completed)],
        };
        var logbookSession = new FakeLogbookSessionService
        {
            Records = { new QsoRecord("qso-1", "W1AW", qsoStart, null, null, null, null, null, null, null, null, "FN31pr", null, null, null, false, false) },
        };
        var localization = new FakeLocalizationService();
        var vm = CreateRxHistoryPaneViewModel(historyStore, logbookSession: logbookSession, localization: localization);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.LogEntryLoggedWithSummaryFormat", vm.LinkedQsoDisplay);
        // Auditor-caught: a key-only assertion can't tell "W1AW" from a swapped-in wrong field (e.g.
        // GridSquare) or a dropped arg -- LastArgs proves the CALLER passed the actual QSO's own
        // callsign/date, not just that the right loc key was chosen.
        Assert.Equal(["W1AW", qsoStart.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], localization.LastArgs);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingEntryWithLinkedQsoLookupFailure_FallsBackToThePlainLoggedText()
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
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.LogEntryLoggedValue", vm.LinkedQsoDisplay);
        Assert.Null(vm.LinkedQsoSummary);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectingEntryWithNoLinkedQso_LinkedQsoDisplayIsThePlainLoggedFallback()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.LogEntryLoggedValue", vm.LinkedQsoDisplay);
        Assert.Null(vm.LinkedQsoSummary);
    }

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SwitchingBetweenTwoLinkedEntries_ResolvesEachOnesOwnQso()
    {
        var firstStart = DateTimeOffset.UtcNow;
        var secondStart = firstStart.AddDays(-3);
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Completed),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow.AddMinutes(1), "robot36", "/tmp/b.png", "qso-2", ReceiveDecodeState.Completed),
            ],
        };
        var logbookSession = new FakeLogbookSessionService
        {
            Records =
            {
                new QsoRecord("qso-1", "W1AW", firstStart, null, null, null, null, null, null, null, null, "FN31pr", null, null, null, false, false),
                new QsoRecord("qso-2", "K1ABC", secondStart, null, null, null, null, null, null, null, null, null, null, null, null, false, false),
            },
        };
        var localization = new FakeLocalizationService();
        var vm = CreateRxHistoryPaneViewModel(historyStore, logbookSession: logbookSession, localization: localization);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        var first = vm.Entries.Single(e => e.Entry.Id == "1");
        var second = vm.Entries.Single(e => e.Entry.Id == "2");

        vm.SelectedEntry = first;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Panes.RxHistory.LogEntryLoggedWithSummaryFormat", vm.LinkedQsoDisplay);
        Assert.Equal(["W1AW", firstStart.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], localization.LastArgs);

        vm.SelectedEntry = second;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Panes.RxHistory.LogEntryLoggedWithSummaryFormat", vm.LinkedQsoDisplay);
        Assert.Equal(["K1ABC", secondStart.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], localization.LastArgs);
    }

    /// <summary>OpenInLog's Linked handler reassigns SelectedEntry to a NEW instance with the SAME
    /// Entry.Id but a freshly-set LinkedQsoId (UpdateEntryInPlace) -- without the (entryId,
    /// LinkedQsoId) pair check in OnSelectedEntryChanged (keyed on Id ALONE it would look like a
    /// no-op re-select), this row would keep showing the plain "Logged" fallback until the operator
    /// reselects it, exactly the earlier boolean-only behavior this feature exists to fix.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_LinkingTheCurrentlySelectedEntry_ResolvesLinkedQsoDisplayWithoutAReselect()
    {
        var qsoStart = DateTimeOffset.UtcNow;
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var logbookSession = new FakeLogbookSessionService
        {
            Records = { new QsoRecord("qso-42", "K1ABC", qsoStart, null, null, null, null, null, null, null, null, null, null, null, null, false, false) },
        };
        var localization = new FakeLocalizationService();
        var vm = CreateRxHistoryPaneViewModel(historyStore, logbookSession: logbookSession, localization: localization);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.LogEntryLoggedValue", vm.LinkedQsoDisplay);

        QsoLinkWindowViewModel? qsoLinkVm = null;
        vm.QsoLinkRequested += requested => qsoLinkVm = requested;
        vm.OpenInLogCommand.Execute(null);
        RaiseLinkedViaReflection(qsoLinkVm!, "qso-42");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Panes.RxHistory.LogEntryLoggedWithSummaryFormat", vm.LinkedQsoDisplay);
        Assert.Equal(["K1ABC", qsoStart.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)], localization.LastArgs);
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

    /// <summary>fsk_cwid.md A-P3b auditor-caught BLOCKER (same class as the AudioAttached one just
    /// above): IRxStationIdAttacher.StationIdAttached had ZERO subscribers anywhere in the app -- a
    /// just-received frame's decoded callsign/NR-RST never reached this pane's in-memory entry until
    /// some unrelated later refresh re-queried the DB, silently defeating the "Callsign" row
    /// and its Send-to-TX/Log-entry seeding for the one frame anyone actually uses them on. Pins that
    /// subscribing IRxStationIdAttacher.StationIdAttached actually patches the live, already-held
    /// entry in place.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_StationIdAttachedEvent_PatchesTheAlreadyHeldEntryInPlace()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var stationIdAttacher = new FakeRxStationIdAttacher();
        var vm = CreateRxHistoryPaneViewModel(historyStore, stationIdAttacher: stationIdAttacher);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Entries[0].Entry.DecodedCallsign);
        Assert.Null(vm.Entries[0].Entry.DecodedNrRst);

        stationIdAttacher.RaiseStationIdAttached("1", "W1AW", nrRst: "595001");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("W1AW", vm.Entries[0].Entry.DecodedCallsign);
        Assert.Equal("595001", vm.Entries[0].Entry.DecodedNrRst);
    }

    /// <summary>fsk_cwid.md B-P5: same shape as the test just above, for the 2 fields added at B-P5 --
    /// pins that a CW-sourced attach patches BOTH DecodedCallsignSource and DecodedCwId onto the
    /// live, already-held entry (not just the original DecodedCallsign/DecodedNrRst pair).</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_StationIdAttachedEvent_PatchesCallsignSourceAndCwId()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var stationIdAttacher = new FakeRxStationIdAttacher();
        var vm = CreateRxHistoryPaneViewModel(historyStore, stationIdAttacher: stationIdAttacher);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.Entries[0].Entry.DecodedCallsignSource);
        Assert.Null(vm.Entries[0].Entry.DecodedCwId);

        stationIdAttacher.RaiseStationIdAttached("1", "W1AW", nrRst: null, callsignSource: StationIdSources.Cw, cwId: "DE W1AW");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("W1AW", vm.Entries[0].Entry.DecodedCallsign);
        Assert.Equal(StationIdSources.Cw, vm.Entries[0].Entry.DecodedCallsignSource);
        Assert.Equal("DE W1AW", vm.Entries[0].Entry.DecodedCwId);
    }

    /// <summary>fsk_cwid.md B-P5, code-review finding: pins SelectedEntryCallsignSourceDisplay's
    /// actual GetString call for the 2 reachable DecodedCallsignSource states -- an earlier
    /// AXAML-only approach (StringFormat + TargetNullValue) looked correct but could never fire
    /// TargetNullValue (StringFormat runs first as a converter), which this VM-side computation
    /// replaced. FakeLocalizationService returns the raw key, not a formatted string (this project's
    /// own convention -- see that fake's own doc comment), so this asserts the key/args CALLER
    /// passed, not a rendered "· CW"/"· FSK" string.</summary>
    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_SelectedEntryCallsignSourceDisplay_PassesCwAndFallsBackToFskForNull()
    {
        var localization = new FakeLocalizationService();
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn =
            [
                new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, DecodedCallsign: "W1AW", DecodedCallsignSource: StationIdSources.Cw),
                new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed, DecodedCallsign: "K9XYZ"),
            ],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };
        var vm = CreateRxHistoryPaneViewModel(historyStore, localization: localization);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "1");
        _ = vm.SelectedEntryCallsignSourceDisplay;
        Assert.Equal("Panes.RxHistory.CallsignSourceFormat", localization.LastKey);
        Assert.Equal([StationIdSources.Cw], localization.LastArgs);

        // DecodedCallsignSource is null here -- either a pre-B-P5 row, or (as constructed) never set
        // by this test either way; both reachable cases fall back to FSK, never a dangling "· ".
        vm.SelectedEntry = vm.Entries.Single(e => e.Entry.Id == "2");
        _ = vm.SelectedEntryCallsignSourceDisplay;
        Assert.Equal([StationIdSources.Fsk], localization.LastArgs);
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

    /// <summary>Waits until the Gallery search debounce has published (polls, 10 s cap) — no fixed sleep,
    /// so a loaded test host cannot race the 200 ms window.</summary>
    internal static async Task SettleGallerySearchAsync(RxHistoryPaneViewModel vm)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        do
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        while (vm.IsSearchPublishPending && DateTime.UtcNow < deadline);

        Assert.False(vm.IsSearchPublishPending, "Gallery search debounce did not publish within 10 s.");
    }

    internal static RxHistoryPaneViewModel CreateRxHistoryPaneViewModel(
        FakeReceiveHistoryStore historyStore,
        FakeReceivedFrameExporter? frameExporter = null,
        FakeFilePickerService? filePicker = null,
        FakeSettingsStore? settingsStore = null,
        FakeUrlLauncher? urlLauncher = null,
        FakeClipboardImageService? clipboardImageService = null,
        FakeRxAudioAutoSaver? audioAutoSaver = null,
        FakeLogbookSessionService? logbookSession = null,
        FakeLocalizationService? localization = null,
        FakeRxStationIdAttacher? stationIdAttacher = null,
        ILogger<RxHistoryPaneViewModel>? logger = null) =>
        new(
            historyStore,
            localization ?? new FakeLocalizationService(),
            logger ?? NullLogger<RxHistoryPaneViewModel>.Instance,
            logbookSession ?? new FakeLogbookSessionService(),
            NullLogger<QsoLinkWindowViewModel>.Instance,
            frameExporter ?? new FakeReceivedFrameExporter(),
            filePicker ?? new FakeFilePickerService(),
            settingsStore ?? new FakeSettingsStore(),
            urlLauncher ?? new FakeUrlLauncher(),
            clipboardImageService ?? new FakeClipboardImageService(),
            NullLogger<ImageViewerWindowViewModel>.Instance,
            audioAutoSaver ?? new FakeRxAudioAutoSaver(),
            stationIdAttacher ?? new FakeRxStationIdAttacher());

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
        await SettleGallerySearchAsync(vm);
        Assert.Equal(["by-note"], vm.FilteredEntries.Select(e => e.Entry.Id));

        vm.SearchText = "SCOTTIE";
        await SettleGallerySearchAsync(vm);
        Assert.Equal(["by-mode"], vm.FilteredEntries.Select(e => e.Entry.Id));

        vm.SearchText = null;
        await SettleGallerySearchAsync(vm);
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
    public async Task LogbookPaneViewModel_LogAsync_InvokesQsoLoggedWithTheLoggedCallsign()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        string? notified = null;
        vm.QsoLogged = callsign => notified = callsign;

        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("N0CALL", notified);
    }

    /// <summary>Confirming auditor round finding: a throwing subscriber must never report an
    /// already-persisted QSO as failed -- a retry would create a real duplicate row, the exact
    /// failure class LogbookSessionService.cs's own PostPersistError already exists to prevent one
    /// layer down. The record must still be there, and StatusMessage must still read success, not
    /// the LogFailed text.</summary>
    [AvaloniaFact]
    public async Task LogbookPaneViewModel_LogAsync_QsoLoggedSubscriberThrows_StillReportsSuccess()
    {
        var logbook = new FakeLogbookSessionService();
        var vm = CreateLogbookPaneViewModel(logbook);
        Dispatcher.UIThread.RunJobs();
        vm.QsoLogged = _ => throw new InvalidOperationException("subscriber exploded");

        vm.FormCallsign = "N0CALL";
        await vm.LogCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(logbook.Records);
        Assert.NotEqual("Panes.Logbook.Error.LogFailed", vm.StatusMessage);
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
        ConfirmActionDialogViewModel? seenConfirmVm = null;
        vm.ConfirmRequested = confirmVm =>
        {
            seenConfirmVm = confirmVm;
            return Task.FromResult(true);
        };

        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        // The dialog message names the callsign being deleted (auditor plan-review round 2
        // finding). FakeLocalizationService.GetString returns the raw key, so confirmVm.Message
        // equals the key itself; the callsign arg is verified via Calls (not LastKey/LastArgs --
        // the confirm/cancel BUTTON labels are also GetString'd while building the same
        // ConfirmActionDialogViewModel constructor call, all before ConfirmRequested's callback
        // ever fires, so only the full call history can still identify THIS specific call).
        Assert.Equal("Panes.Logbook.ConfirmDeleteMessage", seenConfirmVm!.Message);
        var messageCall = localization.Calls.Single(c => c.Key == "Panes.Logbook.ConfirmDeleteMessage");
        Assert.Equal(["N0CALL"], messageCall.Args);
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

    /// <summary>RST default plan (2026-09-01): both fields seed from the ONE passed value when no
    /// decoded RST is available -- SSTV's real-world convention doesn't distinguish direction.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_WithDefaultRst_SetsBothRstFields()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("W1AW", "martin1", DateTimeOffset.UtcNow, null, null, null, defaultRst: "595");

        Assert.Equal("595", vm.FormRstSent);
        Assert.Equal("595", vm.FormRstReceived);
    }

    /// <summary>fsk_cwid.md A-P3b: FormRstReceived prefers the OTHER station's own decoded FSK
    /// NR/RST over the operator's own default when both are present -- FormRstSent (what WE sent)
    /// stays the operator's own default regardless, since decodedRstReceived only ever describes
    /// what the other station reported to US.</summary>
    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_WithDecodedRstReceived_PrefersItOverDefaultRst_ForReceivedOnly()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("W1AW", "martin1", DateTimeOffset.UtcNow, null, null, null, defaultRst: "595", decodedRstReceived: "595001");

        Assert.Equal("595", vm.FormRstSent);
        Assert.Equal("595001", vm.FormRstReceived);
    }

    [AvaloniaFact]
    public void LogbookPaneViewModel_PrefillForNewEntry_WithNoDecodedRstReceived_FallsBackToDefaultRst()
    {
        var vm = CreateLogbookPaneViewModel(new FakeLogbookSessionService());
        Dispatcher.UIThread.RunJobs();

        vm.PrefillForNewEntry("W1AW", "martin1", DateTimeOffset.UtcNow, null, null, null, defaultRst: "595", decodedRstReceived: null);

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
