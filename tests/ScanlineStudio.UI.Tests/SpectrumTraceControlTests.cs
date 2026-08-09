using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
}
