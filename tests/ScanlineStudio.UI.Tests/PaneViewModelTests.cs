using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.Application;
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
        var vm = new WaterfallPaneViewModel(sstvSession);
        var frame = new WaterfallFrame([0f, 1f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        ((FakeWaterfallSource)sstvSession.Waterfall).Emit(frame);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(frame, vm.LatestFrame);
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
        var vm = new WaterfallPaneViewModel(sstvSession);
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
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService());

        Assert.Equal(WaterfallViewMode.Both, vm.ViewMode);
        Assert.True(vm.IsViewBoth);
        Assert.False(vm.IsViewSpectrumOnly);
        Assert.False(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_SettingIsViewSpectrumOnly_UpdatesViewModeAndTheOtherComputedBools()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService());

        vm.IsViewSpectrumOnly = true;

        Assert.Equal(WaterfallViewMode.SpectrumOnly, vm.ViewMode);
        Assert.False(vm.IsViewBoth);
        Assert.True(vm.IsViewSpectrumOnly);
        Assert.False(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_SettingIsViewWaterfallOnly_UpdatesViewModeAndTheOtherComputedBools()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService());

        vm.IsViewWaterfallOnly = true;

        Assert.Equal(WaterfallViewMode.WaterfallOnly, vm.ViewMode);
        Assert.False(vm.IsViewBoth);
        Assert.False(vm.IsViewSpectrumOnly);
        Assert.True(vm.IsViewWaterfallOnly);
    }

    [AvaloniaFact]
    public void WaterfallPaneViewModel_StartHzSpanHzPeakHoldEnabled_HaveTheDocumentedDefaults()
    {
        var vm = new WaterfallPaneViewModel(new FakeSstvSessionService());

        Assert.Equal(1000.0, vm.StartHz);
        Assert.Equal(1600.0, vm.SpanHz);
        Assert.False(vm.PeakHoldEnabled);
        Assert.Equal(0.0, vm.BinsPerPixel);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_UpdatedEvent_RefreshesImageOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.Image);
        ((FakeReceivedImageBuffer)sstvSession.ReceivedImage).RaiseUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Image);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_ModeDetectedEvent_UpdatesModeCardTextsOnUiThread()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
    public void RxImagePaneViewModel_ModeDetectedEvent_SetsStartedAt()
    {
        var sstvSession = new FakeSstvSessionService();
        var localization = new FakeLocalizationService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

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
    public void RxImagePaneViewModel_UpdatedEvent_SetsProgress_AndLineProgressTextReflectsIt()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
            SyncOffsetSamples = -12,
            SyncFrequencyCorrectionHz = -0.18,
            IsLevelOverdriven = true,
            BufferedSampleCount = 1583,
        };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();

        Assert.NotEqual("—", vm.SlantPpmDisplay);
        Assert.NotEqual("—", vm.SyncOffsetSamplesDisplay);
        Assert.NotEqual("—", vm.SyncToneDisplay);
        Assert.Equal("Panes.RxSync.AutoCorrectValue.Locked", vm.AutoCorrectDisplay);
        Assert.Equal("Panes.RxInput.ClippingValue.Overdriven", vm.ClippingDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_BufferedSampleCountStatusBarDisplay_ReflectsSessionValue()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService { BufferedSampleCount = 1583, CaptureOverrunCount = 7 };
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

        vm.PollTelemetry();
        _ = vm.AgcGainDisplay;

        Assert.Equal(512.0, (double)localization.LastArgs[0], precision: 6);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_Constructed_LoadsTheConfiguredCaptureDeviceName()
    {
        var sstvSession = new FakeSstvSessionService { ConfiguredCaptureDeviceName = "hw:2,0 L" };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("hw:2,0 L", vm.CaptureDeviceName);
        Assert.Equal("hw:2,0 L", vm.CaptureDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_NoCaptureDeviceConfigured_CaptureDeviceNameDisplayShowsPlaceholder()
    {
        var sstvSession = new FakeSstvSessionService { ConfiguredCaptureDeviceName = null };
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(vm.CaptureDeviceName);
        Assert.Equal("—", vm.CaptureDeviceNameDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_SavedEvent_SetsFileSizeDisplayFromTheActualFileOnDisk()
    {
        var localization = new FakeLocalizationService();
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

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
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

        Assert.Null(vm.Progress);
        Assert.Equal("—", vm.ClipLoHiDisplay);
    }

    [AvaloniaFact]
    public void RxImagePaneViewModel_RequestReSyncCommand_DelegatesToTheSessionService()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new RxImagePaneViewModel(sstvSession, new FakeLocalizationService(), NullLogger<RxImagePaneViewModel>.Instance);

        vm.RequestReSyncCommand.Execute(null);

        Assert.Equal(1, sstvSession.RequestReSyncCallCount);
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
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);
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
        var vm = new RxImagePaneViewModel(sstvSession, localization, NullLogger<RxImagePaneViewModel>.Instance);
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
    public void TxControlsPaneViewModel_Constructed_LoadsTheConfiguredOutputDeviceName()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode], ConfiguredPlaybackDeviceName = "USB Audio CODEC" };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);
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
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);
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
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        Assert.Null(vm.SelectedMode);
        Assert.Null(vm.ToneMapText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_ModeSelected_ToneMapTextIsPopulated()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        Assert.NotNull(vm.SelectedMode);
        Assert.NotNull(vm.ToneMapText);
    }

    [AvaloniaFact]
    public void TxControlsPaneViewModel_SelectedModeChanges_RaisesPropertyChangedForToneMapText()
    {
        var narrowMode = TestMode with { Id = "narrow" }; // LuminanceMinHz/MaxHz not overridden here -- this test only needs a DIFFERENT mode instance, not different Hz values, to prove the reactive wiring fires
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode, narrowMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var raisedProperties = new List<string?>();
        vm.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        vm.SelectedMode = narrowMode;

        Assert.Contains(nameof(vm.ToneMapText), raisedProperties);
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
    public void TxControlsPaneViewModel_ModeTimingRows_ComputedFromEachAvailableModesRealTiming()
    {
        var scottie1 = new SstvModeDefinition(
            Id: "sc1", DisplayName: "Scottie 1", VisCode: 60, ImageWidth: 320, ImageHeight: 256,
            ColorEncoding: ColorEncoding.RgbSequential,
            LineSegments: [new ScanSegment("R", 138.24)]);
        var sstvSession = new FakeSstvSessionService { AvailableModes = [scottie1] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        var row = Assert.Single(vm.ModeTimingRows);
        Assert.Equal("Scottie 1", row.ModeName);
        Assert.Equal(256, row.Lines);
        Assert.Equal(138.24, row.LineMs, precision: 2);
        Assert.Equal(138.24 * 256 / 1000.0, row.FrameSeconds, precision: 2);
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_TransmitCommand_InvokesSstvSessionServiceWhenImageLoaded()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService();
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

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
    public void TxControlsPaneViewModel_SelectingANewMode_ClearsAlreadyLoadedImage()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

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
        var vm = new TxControlsPaneViewModel(sstvSession, new FakeImageFileLoader(), stockLibrary, new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

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
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), new FakeFilePickerService(), new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

        await OpenEditorAsync(vm, () => vm.SelectImageCommand.ExecuteAsync(null));

        // Opening the editor must not, by itself, make the old flat-load path's image transmittable
        // -- only Apply does that now.
        Assert.False(vm.TransmitCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task TxControlsPaneViewModel_CancellingTheEditor_LeavesAnyPreviouslyAppliedImageUntouched()
    {
        var sstvSession = new FakeSstvSessionService { AvailableModes = [TestMode] };
        var imageFileLoader = new FakeImageFileLoader { ResultToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]) };
        var filePicker = new FakeFilePickerService { PathToReturn = "/tmp/a.png" };
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

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
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), new FakeTransmitImagePreparer(), filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);
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
        var vm = new TxControlsPaneViewModel(sstvSession, imageFileLoader, new FakeStockImageLibrary(), preparer, filePicker, new FakeLocalizationService(), new FakeSettingsStore(), new FakeRadioSessionService(), new MacroTextResolver(), NullLogger<TxControlsPaneViewModel>.Instance);

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

    [AvaloniaFact]
    public async Task RxHistoryPaneViewModel_Constructed_LoadsEntriesFromTheHistoryStore()
    {
        var historyStore = new FakeReceiveHistoryStore
        {
            EntriesToReturn = [new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed)],
            ThumbnailToReturn = new ArrayImageSource(1, 1, [new Rgb24(1, 2, 3)]),
        };

        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("/tmp/scanlinestudio-history", vm.ImagesDirectory);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
        Dispatcher.UIThread.RunJobs();

        vm.SelectLatestCommand.Execute(null);

        Assert.NotNull(vm.SelectedEntry);
        Assert.Equal("newer", vm.SelectedEntry!.Entry.Id);
    }

    [AvaloniaFact]
    public void RxHistoryPaneViewModel_SelectLatestCommand_DisabledWhenNoEntries()
    {
        var historyStore = new FakeReceiveHistoryStore();
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
        await vm.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        vm.SelectedEntry = vm.Entries[0];
        Dispatcher.UIThread.RunJobs();

        // Simulates the documented race: the entry aged out of retention between load and edit.
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
        var vm = new RxHistoryPaneViewModel(historyStore, new FakeLocalizationService(), NullLogger<RxHistoryPaneViewModel>.Instance);
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
