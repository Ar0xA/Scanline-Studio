using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.Tests;

public sealed class DecoderTraceControlTests
{
    [AvaloniaFact]
    public void Defaults_MatchDecoderTracePaneViewModelsOwnDefaults()
    {
        var control = new DecoderTraceControl();

        Assert.Equal(3072, control.XOffset);
        Assert.Equal(2048, control.XWindow);
        Assert.Equal(2.0, control.Gain);
        Assert.Null(control.Channel0);
        Assert.Null(control.Channel1);
    }

    [AvaloniaFact]
    public void Render_WithNoData_DoesNotThrow()
    {
        var control = new DecoderTraceControl { Width = 200, Height = 100 };
        var window = new Window { Content = control, Width = 220, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Render_WithBothChannelsPopulated_DoesNotThrow()
    {
        var rng = new Random(7);
        var channel0 = new double[8192];
        var channel1 = new double[8192];
        for (var i = 0; i < channel0.Length; i++)
        {
            channel0[i] = rng.NextDouble() * 16384.0;
            channel1[i] = (rng.NextDouble() - 0.5) * 2 * 32768.0;
        }

        var control = new DecoderTraceControl { Width = 200, Height = 100, Channel0 = channel0, Channel1 = channel1 };
        var window = new Window { Content = control, Width = 220, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Render_WithZeroSizeBounds_DoesNotThrow()
    {
        var control = new DecoderTraceControl { Width = 0, Height = 0, Channel0 = [1.0, 2.0], Channel1 = [1.0, 2.0] };
        var window = new Window { Content = control, Width = 10, Height = 10 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Render_WithXOffsetPastTheDataLength_DoesNotThrow()
    {
        // A pan/zoom combination the VM's own clamping should never produce in practice, but the
        // control itself must not assume its caller got that clamping right.
        var control = new DecoderTraceControl { Width = 200, Height = 100, Channel0 = [1.0, 2.0, 3.0], XOffset = 999_999, XWindow = 2048 };
        var window = new Window { Content = control, Width = 220, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Render_WithExtremeGain_ClampsRatherThanThrowing()
    {
        var control = new DecoderTraceControl { Width = 200, Height = 100, Channel0 = [16384.0], Channel1 = [32768.0], Gain = 1_000_000.0 };
        var window = new Window { Content = control, Width = 220, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Render_WithNegativeGain_DoesNotThrow()
    {
        var control = new DecoderTraceControl { Width = 200, Height = 100, Channel0 = [1000.0], Channel1 = [1000.0], Gain = -5.0 };
        var window = new Window { Content = control, Width = 220, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
    }
}
