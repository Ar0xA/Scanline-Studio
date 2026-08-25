using System.Reflection;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class SpectrumTraceControlTests
{
    private static float[]? GetPeakDb(SpectrumTraceControl control)
    {
        var field = typeof(SpectrumTraceControl).GetField("_peakDb", BindingFlags.NonPublic | BindingFlags.Instance);
        return (float[]?)field!.GetValue(control);
    }

    [AvaloniaFact]
    public void SettingFrame_DoesNotThrow_AndCanBeUpdatedRepeatedly()
    {
        var control = new SpectrumTraceControl();

        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        control.Frame = new WaterfallFrame([-10f, -30f, -60f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow.AddSeconds(1));

        Assert.NotNull(control.Frame);
    }

    [AvaloniaFact]
    public void SettingFrame_WithDifferentBinCount_ResizesPeakBufferWithoutThrowing()
    {
        var control = new SpectrumTraceControl();

        control.Frame = new WaterfallFrame([-10f, -20f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        control.Frame = new WaterfallFrame([-10f, -20f, -30f, -40f], BinWidthHz: 50, ObservedAt: DateTimeOffset.UtcNow);

        Assert.Equal(4, GetPeakDb(control)!.Length);
    }

    [AvaloniaFact]
    public void FirstFrame_SeedsThePeakBufferWithItsOwnValues_NotZeroOrLeftAtTheSentinel()
    {
        // Auditor-caught pattern from WaterfallControl's own equivalent bug (batch 8a): a peak buffer
        // that starts zero-filled is indistinguishable from a real 0dB reading. _peakDb starts at
        // float.NegativeInfinity, and DecayPeak's own "newDb >= currentPeakDb -> jump immediately"
        // branch means the very first real frame becomes the peak outright (elapsedSeconds=0 on the
        // first frame regardless, so decay can't suppress it either).
        var control = new SpectrumTraceControl();

        control.Frame = new WaterfallFrame([25f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        var peaks = GetPeakDb(control)!;
        Assert.Equal(25f, peaks[0]);
        Assert.Equal(-5f, peaks[1]);
    }

    [AvaloniaFact]
    public void SubsequentWeakerFrame_DoesNotLowerThePeakImmediately()
    {
        var control = new SpectrumTraceControl();
        control.Frame = new WaterfallFrame([25f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        control.Frame = new WaterfallFrame([-5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow.AddMilliseconds(10));

        // A negligible elapsed time can only decay a negligible amount -- the peak must still be
        // much closer to 25 than to -5.
        Assert.True(GetPeakDb(control)![0] > 20f);
    }

    [AvaloniaFact]
    public void ChangingBinCount_ResetsThePeakBuffer_NotCarryingStaleValuesIntoTheNewSize()
    {
        var control = new SpectrumTraceControl();
        control.Frame = new WaterfallFrame([25f, 25f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);

        control.Frame = new WaterfallFrame([-5f, -5f, -5f], BinWidthHz: 50, ObservedAt: DateTimeOffset.UtcNow);

        var peaks = GetPeakDb(control)!;
        Assert.Equal(3, peaks.Length);
        Assert.All(peaks, p => Assert.Equal(-5f, p));
    }

    [AvaloniaFact]
    public void ArrangeOverride_UpdatesBinsPerPixel_FromTheControlsOwnRealRenderedWidth()
    {
        // Auditor-caught (batch 8b round 2): computing BinsPerPixel inside Render() mutated the
        // binding graph (SetAndRaise -> OneWayToSource -> view-model -> a sibling's own layout)
        // mid-render. Moved to ArrangeOverride, the canonical "my final size" hook -- this test
        // needs a REAL layout pass (a real Window's Show() + RunJobs(), not just setting Frame like
        // the other tests above) to actually exercise it, confirmed empirically that a headless
        // Window does run real layout.
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600 };
        var window = new Window { Content = control, Width = 420, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        control.Frame = new WaterfallFrame(new float[512], BinWidthHz: 10.766, ObservedAt: DateTimeOffset.UtcNow);
        Dispatcher.UIThread.RunJobs();

        // Expected: SpanHz / BinWidthHz / width = 1600 / 10.766 / 400 ~= 0.3716.
        Assert.True(control.BinsPerPixel is > 0.37 and < 0.373, $"BinsPerPixel was {control.BinsPerPixel}");
    }

    [AvaloniaFact]
    public void ArrangeOverride_WidthCollapsedToZero_HoldsTheLastBinsPerPixelValue()
    {
        // ViewMode=WaterfallOnly collapses this control's column to zero width (the
        // WaterfallViewModeToColumnWidthConverter) -- BinsPerPixel must hold its last real value,
        // not reset to 0 or throw, matching the auditor's own "preserve the hold-last-value behavior
        // explicitly" round-3 finding.
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600 };
        var window = new Window { Content = control, Width = 420, Height = 220 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        control.Frame = new WaterfallFrame(new float[512], BinWidthHz: 10.766, ObservedAt: DateTimeOffset.UtcNow);
        Dispatcher.UIThread.RunJobs();
        var beforeCollapse = control.BinsPerPixel;
        Assert.True(beforeCollapse > 0);

        control.Width = 0;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(beforeCollapse, control.BinsPerPixel);
    }

    /// <summary>Records every frequency an <see cref="ICommand"/> bound to
    /// <see cref="SpectrumTraceControl.NotchTuneRequestedCommand"/> was invoked with -- test double,
    /// not a mock framework dependency (this project's own established style, matching e.g.
    /// <c>FakeSstvDecoder.cs</c>'s hand-rolled call-tracking).</summary>
    private sealed class RecordingCommand : ICommand
    {
        public List<double> InvokedFrequenciesHz { get; } = [];
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => InvokedFrequenciesHz.Add((double)parameter!);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }

    /// <summary>Manually constructs and <c>RaiseEvent</c>s the pointer routed events directly on the
    /// control (matching this project's own established input-simulation precedent,
    /// <c>IndustryStepperTests.cs</c>'s <c>RaiseEvent(new RoutedEventArgs(Button.ClickEvent))</c>),
    /// rather than the headless <c>TopLevel.MouseDown</c>/<c>MouseMove</c>/<c>MouseUp</c> window-level
    /// simulation -- confirmed empirically (a throwaway harness against a plain <c>Control</c> with a
    /// bare <c>OnPointerPressed</c> override) that window-level headless mouse simulation does not
    /// route through hit-testing to reach a control's pointer overrides in this environment, while a
    /// directly-raised <see cref="PointerPressedEventArgs"/> reliably does.</summary>
    private static void RaisePointerPressed(SpectrumTraceControl control, TopLevel root, Point position)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        control.RaiseEvent(new PointerPressedEventArgs(control, pointer, root, position, timestamp: 0, props, KeyModifiers.None, clickCount: 1));
    }

    private static void RaisePointerMoved(SpectrumTraceControl control, TopLevel root, Point position)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.Other);
        control.RaiseEvent(new PointerEventArgs(InputElement.PointerMovedEvent, control, pointer, root, position, timestamp: 0, props, KeyModifiers.None));
    }

    private static void RaisePointerReleased(SpectrumTraceControl control, TopLevel root, Point position)
    {
        var pointer = new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);
        control.RaiseEvent(new PointerReleasedEventArgs(control, pointer, root, position, timestamp: 0, props, KeyModifiers.None, MouseButton.Left));
    }

    [AvaloniaFact]
    public void PointerPressed_LeftButton_InvokesNotchTuneCommandWithTheClickedFrequency()
    {
        // Un-stub-RX-tab Piece A3: StartHz=1000/SpanHz=1600/width=400 -> MapXToFrequency(100, ...) =
        // 1000 + 100/400*1600 = 1400Hz (SpectrumTraceMathTests.cs already covers the pure math itself,
        // this proves the control wires a real pointer press to it).
        var command = new RecordingCommand();
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600, NotchTuneRequestedCommand = command };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        RaisePointerPressed(control, window, new Point(100, 50));

        Assert.Equal([1400.0], command.InvokedFrequenciesHz);
    }

    [AvaloniaFact]
    public void PointerMoved_WhileHeldAfterAPress_ContinuesRetuning()
    {
        var command = new RecordingCommand();
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600, NotchTuneRequestedCommand = command };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        RaisePointerPressed(control, window, new Point(100, 50));
        RaisePointerMoved(control, window, new Point(200, 50));

        Assert.Equal([1400.0, 1800.0], command.InvokedFrequenciesHz);
    }

    [AvaloniaFact]
    public void PointerMoved_WithNoPriorPress_DoesNotInvokeTheCommand()
    {
        // The drag-retune behavior is gated on an actual press first -- a bare hover must not tune.
        var command = new RecordingCommand();
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600, NotchTuneRequestedCommand = command };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        RaisePointerMoved(control, window, new Point(200, 50));

        Assert.Empty(command.InvokedFrequenciesHz);
    }

    [AvaloniaFact]
    public void PointerMoved_AfterRelease_NoLongerRetunes()
    {
        var command = new RecordingCommand();
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600, NotchTuneRequestedCommand = command };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        RaisePointerPressed(control, window, new Point(100, 50));
        RaisePointerReleased(control, window, new Point(100, 50));
        RaisePointerMoved(control, window, new Point(200, 50));

        Assert.Equal([1400.0], command.InvokedFrequenciesHz);
    }

    [AvaloniaFact]
    public void PointerPressed_WithNoCommandBound_DoesNotThrow()
    {
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600 };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        RaisePointerPressed(control, window, new Point(100, 50));
    }

    [AvaloniaFact]
    public void Render_WithNotchEnabledAndAFrame_DoesNotThrow()
    {
        // No pixel-level assertion (WaterfallControlTests' own doc comment on
        // GetColorHistoryPixel already documents this environment's headless-renderer pixel-readback
        // quirk) -- just proves the new NotchEnabled/NotchFrequencyHz draw path added to Render()
        // doesn't throw, in and out of the visible span.
        var control = new SpectrumTraceControl { Width = 400, Height = 200, StartHz = 1000, SpanHz = 1600, NotchEnabled = true, NotchFrequencyHz = 1800 };
        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();
        control.Frame = new WaterfallFrame([-50f, -20f, -5f], BinWidthHz: 100, ObservedAt: DateTimeOffset.UtcNow);
        Dispatcher.UIThread.RunJobs();

        control.NotchFrequencyHz = 5000; // outside [StartHz, StartHz+SpanHz) -- must skip drawing, not throw
        Dispatcher.UIThread.RunJobs();
    }
}
