using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

public sealed class DecoderTracePaneViewModelTests
{
    [AvaloniaFact]
    public void Defaults_MatchLegacysOwnScopeCppDefaults()
    {
        // m_XW=2048, m_XOFF=(8192-2048)/2=3072, m_Gain=2.0 -- Scope.cpp:29-32.
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService());

        Assert.Equal(3072, vm.XOffset);
        Assert.Equal(2048, vm.XWindow);
        Assert.Equal(2.0, vm.Gain);
        Assert.Null(vm.Channel0Snapshot);
        Assert.Null(vm.Channel1Snapshot);
        Assert.False(vm.IsCapturing);
    }

    [AvaloniaFact]
    public void Capture_ArmsTheSessionAtTheFullScopeSize_AndSetsIsCapturing()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new DecoderTracePaneViewModel(sstvSession, new FakeLocalizationService());

        vm.CaptureCommand.Execute(null);

        Assert.Equal(1, sstvSession.ArmScopeCaptureCallCount);
        Assert.Equal(DecoderTracePaneViewModel.ScopeSize, sstvSession.LastScopeCaptureSize);
        Assert.True(vm.IsCapturing);
    }

    [AvaloniaFact]
    public async Task Capture_PollsUntilChannel0Fills_ThenStopsAndReportsWhateverChannel1Had()
    {
        // Channel 0 always eventually fills; channel 1 may legitimately stay null (no active
        // reception) -- once channel 0 is done, that's the stopping condition regardless of
        // channel 1's own state.
        var sstvSession = new FakeSstvSessionService();
        var vm = new DecoderTracePaneViewModel(sstvSession, new FakeLocalizationService());

        vm.CaptureCommand.Execute(null);
        Assert.True(vm.IsCapturing);

        var channel1Data = new double[] { 1.0, 2.0 };
        sstvSession.ScopeCaptureChannel1ToReturn = channel1Data;
        // Channel 0 not ready yet -- poll must not stop early.
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCapturing);
        Assert.Equal(channel1Data, vm.Channel1Snapshot);
        Assert.Null(vm.Channel0Snapshot);

        var channel0Data = new double[] { 3.0, 4.0, 5.0 };
        sstvSession.ScopeCaptureChannel0ToReturn = channel0Data;
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCapturing);
        Assert.Equal(channel0Data, vm.Channel0Snapshot);
        Assert.Equal(channel1Data, vm.Channel1Snapshot);
    }

    [AvaloniaFact]
    public async Task Capture_WhenNoPushSamplesEverArrivesAfterArming_DoesNotPresentTheStalePreviousCaptureAsFresh()
    {
        // Auditor code-review finding: ArmScopeCapture only latches a deferred request -- the
        // decoder-side buffer doesn't actually reset until the NEXT PushSamples call drains it. If
        // no reception is active between the click and the first poll tick, a naive
        // TryGetScopeCaptureChannel0()-is-not-null check would present the PREVIOUS capture's
        // still-published array as a fresh one, silently.
        var sstvSession = new FakeSstvSessionService();
        var staleChannel0 = new double[] { 1.0, 2.0 };
        var staleChannel1 = new double[] { 3.0, 4.0 };
        sstvSession.ScopeCaptureChannel0ToReturn = staleChannel0;
        sstvSession.ScopeCaptureChannel1ToReturn = staleChannel1;
        var vm = new DecoderTracePaneViewModel(sstvSession, new FakeLocalizationService());

        vm.CaptureCommand.Execute(null); // arms, but the fake never actually re-Arm()s its own returned arrays
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        // Still capturing -- the stale arrays must NOT have been accepted as a fresh completion.
        Assert.True(vm.IsCapturing);
        Assert.Null(vm.Channel0Snapshot);
        Assert.Null(vm.Channel1Snapshot);
    }

    [AvaloniaFact]
    public async Task Capture_WhenAGenuinelyNewArrayArrives_IsAcceptedEvenIfItsContentMatchesTheStaleOne()
    {
        // The fix must key off object identity (ReferenceEquals), not content equality -- a real
        // re-capture of an unchanged/quiet signal can legitimately produce byte-identical values to
        // the previous capture, and that must still be accepted as fresh.
        var sstvSession = new FakeSstvSessionService();
        var staleChannel0 = new double[] { 1.0, 2.0 };
        sstvSession.ScopeCaptureChannel0ToReturn = staleChannel0;
        var vm = new DecoderTracePaneViewModel(sstvSession, new FakeLocalizationService());

        vm.CaptureCommand.Execute(null);
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsCapturing); // still holding the stale array, per the test above

        var freshChannel0 = new double[] { 1.0, 2.0 }; // same VALUES, different array instance
        sstvSession.ScopeCaptureChannel0ToReturn = freshChannel0;
        await Task.Delay(400);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsCapturing);
        Assert.Same(freshChannel0, vm.Channel0Snapshot);
    }

    [AvaloniaFact]
    public void Capture_ResetsAnyPreviousSnapshotsImmediately_NotJustOnceThePollLands()
    {
        var sstvSession = new FakeSstvSessionService();
        var vm = new DecoderTracePaneViewModel(sstvSession, new FakeLocalizationService())
        {
            Channel0Snapshot = [1.0],
            Channel1Snapshot = [2.0],
        };

        vm.CaptureCommand.Execute(null);

        Assert.Null(vm.Channel0Snapshot);
        Assert.Null(vm.Channel1Snapshot);
    }

    [AvaloniaFact]
    public void PanLeft_MovesBackByAQuarterWindow_ClampedToZero()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService())
        {
            XOffset = 100,
            XWindow = 2048, // quarter = 512
        };

        vm.PanLeftCommand.Execute(null);
        Assert.Equal(0, vm.XOffset); // 100-512 clamped to 0, not negative

        vm.XOffset = 3072;
        vm.PanLeftCommand.Execute(null);
        Assert.Equal(2560, vm.XOffset); // 3072 - 2048/4
    }

    [AvaloniaFact]
    public void PanLeft_AtZero_IsANoOp_MatchingLegacysOwnGuard()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { XOffset = 0 };

        vm.PanLeftCommand.Execute(null);

        Assert.Equal(0, vm.XOffset);
    }

    [AvaloniaFact]
    public void PanRight_MovesForwardByAQuarterWindow_ClampedSoTheWindowNeverExceedsScopeSize()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService())
        {
            XOffset = 3072,
            XWindow = 2048,
        };

        vm.PanRightCommand.Execute(null);
        Assert.Equal(3584, vm.XOffset); // 3072 + 2048/4

        vm.XOffset = 8000;
        vm.PanRightCommand.Execute(null);
        Assert.Equal(DecoderTracePaneViewModel.ScopeSize - 2048, vm.XOffset); // clamped, not run past ScopeSize
    }

    [AvaloniaFact]
    public void NarrowWindow_StepsByFiveTwelve_ThenByThirtyTwoBelowOneThousandTwentyFour()
    {
        // Scope.cpp:282-296 -- two distinct step sizes, NOT a single halving. Pinned at both tiers
        // and their boundary.
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { XWindow = 2048 };

        vm.NarrowWindowCommand.Execute(null);
        Assert.Equal(1536, vm.XWindow); // 2048-512, still >=1024 tier

        vm.NarrowWindowCommand.Execute(null);
        Assert.Equal(1024, vm.XWindow); // 1536-512

        vm.NarrowWindowCommand.Execute(null);
        Assert.Equal(512, vm.XWindow); // 1024-512 -- last -512 step (>=1024 was still true at 1024)

        vm.NarrowWindowCommand.Execute(null);
        Assert.Equal(480, vm.XWindow); // now in the <1024 tier -- steps by 32
    }

    [AvaloniaFact]
    public void NarrowWindow_TrueFloorIsThirtyTwo_NotSixtyFour()
    {
        // Legacy's own SBDownW->Enabled condition is `m_XW>=64` (Scope.cpp:227-232) -- the button
        // stays enabled AT exactly 64 and one more click steps it to 32 (64>=64 matches the -32
        // branch), which is the actual point nothing moves any further (32 matches neither >=1024
        // nor >=64). CanNarrowWindow's own doc comment already ports this exact condition; this
        // test pins the resulting floor value, not just the enabled-state boundary.
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { XWindow = 64 };

        vm.NarrowWindowCommand.Execute(null);
        Assert.Equal(32, vm.XWindow);

        vm.NarrowWindowCommand.Execute(null);
        Assert.Equal(32, vm.XWindow); // genuinely stuck now -- neither branch matches 32
    }

    [AvaloniaFact]
    public void WidenWindow_StepsByThirtyTwoBelowFiveTwelve_ThenByFiveTwelve()
    {
        // Scope.cpp:298-312.
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { XWindow = 480 };

        vm.WidenWindowCommand.Execute(null);
        Assert.Equal(512, vm.XWindow); // 480+32, still <512 tier at the moment of the check

        vm.WidenWindowCommand.Execute(null);
        Assert.Equal(1024, vm.XWindow); // now >=512 -- steps by 512
    }

    [AvaloniaFact]
    public void WidenWindow_StopsAtScopeSize()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService())
        {
            XWindow = DecoderTracePaneViewModel.ScopeSize,
        };

        vm.WidenWindowCommand.Execute(null);

        Assert.Equal(DecoderTracePaneViewModel.ScopeSize, vm.XWindow);
    }

    [AvaloniaFact]
    public void NarrowingOrWideningTheWindow_ClampsXOffsetBackIntoBounds()
    {
        // A previously-valid XOffset can go out of range once XWindow grows -- Scope.cpp's own
        // AdjXoff (this port's simplified ClampXOffsetToWindow, see the VM's own doc comment for
        // the deliberate cursor-preservation simplification) must re-clamp it.
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService())
        {
            XOffset = 8180, // 8180+32=8212 already narrowly overruns ScopeSize=8192
            XWindow = 32,
        };

        vm.WidenWindowCommand.Execute(null); // XWindow -> 64 (8180+64=8244 overruns further, without a clamp)

        Assert.Equal(DecoderTracePaneViewModel.ScopeSize - vm.XWindow, vm.XOffset);
        Assert.True(vm.XOffset + vm.XWindow <= DecoderTracePaneViewModel.ScopeSize);
    }

    [AvaloniaFact]
    public void GainUp_MultipliesByOnePointTwo_NotAnAdditiveStep()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { Gain = 2.0 };

        vm.GainUpCommand.Execute(null);

        Assert.Equal(2.4, vm.Gain, precision: 10);
    }

    [AvaloniaFact]
    public void GainDown_DividesByOnePointTwo()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { Gain = 2.4 };

        vm.GainDownCommand.Execute(null);

        Assert.Equal(2.0, vm.Gain, precision: 10);
    }

    [AvaloniaFact]
    public void AutoGain_SetsGainSoThePeakInTheVisibleWindowFillsEightyPercent()
    {
        // Scope.cpp:334-348 -- m_Gain = 16384.0*0.8/peak, peak taken over ONLY the visible window
        // [XOffset, XOffset+XWindow), not the whole buffer.
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService())
        {
            XOffset = 2,
            XWindow = 3,
            Channel1Snapshot = [100.0, -9000.0, 500.0, -300.0, 8000.0], // indices 2,3,4 visible; the -9000 outlier at index 1 must NOT count
        };

        vm.AutoGainCommand.Execute(null);

        Assert.Equal(16384.0 * 0.8 / 8000.0, vm.Gain, precision: 10);
    }

    [AvaloniaFact]
    public void AutoGain_WithNoChannel1Data_IsANoOp()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService()) { Gain = 2.0 };

        vm.AutoGainCommand.Execute(null);

        Assert.Equal(2.0, vm.Gain);
    }

    [AvaloniaFact]
    public void AutoGain_WithAllZerosInTheVisibleWindow_IsANoOp_MatchingLegacysOwnIfPeakGuard()
    {
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), new FakeLocalizationService())
        {
            Gain = 2.0,
            XOffset = 0,
            XWindow = 3,
            Channel1Snapshot = [0.0, 0.0, 0.0],
        };

        vm.AutoGainCommand.Execute(null);

        Assert.Equal(2.0, vm.Gain);
    }

    [AvaloniaFact]
    public void CanPanLeftAndCanPanRight_ReflectLegacysOwnUpdateBtnConditions()
    {
        var localization = new FakeLocalizationService();
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), localization) { XOffset = 0 };
        Assert.False(vm.CanPanLeft);

        vm.XOffset = 1;
        Assert.True(vm.CanPanLeft);

        vm.XWindow = 100;
        vm.XOffset = DecoderTracePaneViewModel.ScopeSize - 100;
        Assert.False(vm.CanPanRight);

        vm.XOffset -= 1;
        Assert.True(vm.CanPanRight);
    }

    [AvaloniaFact]
    public void WindowRangeDisplay_PassesTheCurrentOffsetEndAndScopeSize()
    {
        var localization = new FakeLocalizationService();
        var vm = new DecoderTracePaneViewModel(new FakeSstvSessionService(), localization) { XOffset = 100, XWindow = 200 };

        _ = vm.WindowRangeDisplay;

        Assert.Equal("Panes.RxDecodeLog.TraceRangeFormat", localization.LastKey);
        Assert.Equal([100, 300, DecoderTracePaneViewModel.ScopeSize], localization.LastArgs);
    }
}
