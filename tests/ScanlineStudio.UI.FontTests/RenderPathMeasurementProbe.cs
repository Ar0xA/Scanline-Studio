using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Controls;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Opt-in real-Skia timing probe for the per-frame render paths: (a) <see cref="WaterfallControl"/>,
/// (b) <see cref="SpectrumTraceControl"/> with peak hold, (c) <see cref="StrokedTextBlock"/>. One op is the change +
/// <c>InvalidateVisual</c> + the dispatcher/layout/render-tick pump, so it covers render-data recording and compositor
/// replay. Reports the min-max of 5 repeat medians in us/op and B/op (all threads) through the failure message, so
/// it always fails when run. Writes PNGs of (b) and (c) into <c>$SCANLINE_PROBE_OUT</c>.</summary>
public sealed class RenderPathMeasurementProbe
{
    public const string OptInVariable = "SCANLINE_RUN_RENDER_PROBE";
    public const string OutputVariable = "SCANLINE_PROBE_OUT";
    private const int WarmUp = 200;
    private const int Repeats = 5;
    private const int Bins = 512;
    private const double BinWidthHz = 11025.0 / 1024;

    [AvaloniaFact]
    public void Measure()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable(OptInVariable) == "1",
            $"Set {OptInVariable}=1 to run the render-path timing probe. It reports through the failure message by design.");
        var outDir = Environment.GetEnvironmentVariable(OutputVariable);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"warm-up {WarmUp}, {Repeats} repeats; op = change + InvalidateVisual + Pump; min-max of repeat medians");
        report.AppendLine(MeasureWaterfall());
        report.AppendLine(MeasureSpectrum(outDir));
        report.AppendLine(MeasureStrokedText(outDir));

        if (!string.IsNullOrEmpty(outDir))
        {
            File.WriteAllText(Path.Combine(outDir, "render-path-probe.txt"), report.ToString());
        }

        Assert.Fail(report.ToString());
    }

    private static string MeasureWaterfall()
    {
        var frames = BuildFrames(4);
        var control = new WaterfallControl();
        var window = new Window { Width = 600, Height = 200, Content = control };
        window.Show();
        Pump(window);
        var next = 0;
        var row = TimeOps(window, 2000, () =>
        {
            control.Frame = frames[next++ % frames.Length];
            control.InvalidateVisual();
        });
        window.Close();
        return Format("(a) waterfall 512 bins, N=2000", row);
    }

    private static string MeasureSpectrum(string? outDir)
    {
        var frames = BuildFrames(4);
        var (control, window) = HostSpectrum();
        var next = 0;
        var row = TimeOps(window, 500, () =>
        {
            control.Frame = frames[next++ % frames.Length];
            control.InvalidateVisual();
        });
        window.Close();

        // Fresh control fed a fixed sequence, so the PNG is reproducible for the later before/after diff.
        var (pngControl, pngWindow) = HostSpectrum();
        foreach (var frame in frames)
        {
            pngControl.Frame = frame;
            Pump(pngWindow);
        }

        SavePng(pngControl, 600, 150, outDir, "spectrum-trace.png");
        pngWindow.Close();
        return Format("(b) spectrum 600x150 peak hold, N=500", row);
    }

    private static (SpectrumTraceControl Control, Window Window) HostSpectrum()
    {
        var control = new SpectrumTraceControl { Width = 600, Height = 150, PeakHoldEnabled = true };
        var window = new Window { Width = 600, Height = 150, Content = control };
        window.Show();
        Pump(window);
        return (control, window);
    }

    private static string MeasureStrokedText(string? outDir)
    {
        string[] texts = ["SCANLINE STUDIO 0123", "CQ CQ DE N0CALL 5959"];
        var (control, window) = HostStrokedText(texts[0]);
        var next = 0;
        var row = TimeOps(window, 500, () =>
        {
            control.Text = texts[++next % texts.Length];
            control.InvalidateVisual();
        });
        window.Close();

        var (pngControl, pngWindow) = HostStrokedText(texts[0]);
        SavePng(pngControl, (int)Math.Ceiling(pngControl.Bounds.Width), (int)Math.Ceiling(pngControl.Bounds.Height), outDir, "stroked-text.png");
        pngWindow.Close();
        return Format("(c) stroked text 20 ch 40 px stroke+stack 8, N=500", row);
    }

    private static (StrokedTextBlock Control, Window Window) HostStrokedText(string text)
    {
        var control = new StrokedTextBlock
        {
            Text = text,
            FontSize = 40,
            Fill = Brushes.White,
            Stroke = Brushes.Black,
            StrokeThickness = 2,
            StackFill = Brushes.SteelBlue,
            StackStepXPixels = 8,
            StackStepYPixels = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var window = new Window { Width = 800, Height = 200, Content = control };
        window.Show();
        Pump(window);
        return (control, window);
    }

    private static (double MinUs, double MaxUs, double MinBytes, double MaxBytes) TimeOps(Window window, int opsPerRepeat, Action change)
    {
        for (var i = 0; i < WarmUp; i++)
        {
            change();
            Pump(window);
        }

        var medians = new List<double>();
        var bytesPerOp = new List<double>();
        var samples = new double[opsPerRepeat];
        for (var r = 0; r < Repeats; r++)
        {
            var before = GC.GetTotalAllocatedBytes(precise: true);
            for (var i = 0; i < opsPerRepeat; i++)
            {
                var start = Stopwatch.GetTimestamp();
                change();
                Pump(window);
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }

            bytesPerOp.Add((GC.GetTotalAllocatedBytes(precise: true) - before) / (double)opsPerRepeat);
            Array.Sort(samples);
            medians.Add(samples[opsPerRepeat / 2]);
        }

        return (medians.Min(), medians.Max(), bytesPerOp.Min(), bytesPerOp.Max());
    }

    private static string Format(string name, (double MinUs, double MaxUs, double MinBytes, double MaxBytes) row) =>
        string.Create(CultureInfo.InvariantCulture, $"{name,-52} | {row.MinUs,8:F1} - {row.MaxUs,8:F1} us/op | {row.MinBytes,9:F0} - {row.MaxBytes,9:F0} B/op");

    private static WaterfallFrame[] BuildFrames(int count)
    {
        var t0 = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        var frames = new WaterfallFrame[count];
        for (var f = 0; f < count; f++)
        {
            var db = new float[Bins];
            for (var i = 0; i < Bins; i++)
            {
                // A noise floor with a few bin-dependent peaks that move between frames.
                db[i] = (float)(-55 + (12 * Math.Sin((i * 0.37) + f)) + (45 * Math.Exp(-Math.Pow((i - 140 - (f * 3)) / 4.0, 2))) + (30 * Math.Exp(-Math.Pow((i - 200) / 2.5, 2))));
            }

            frames[f] = new WaterfallFrame(db, BinWidthHz, t0.AddMilliseconds(f * 46));
        }

        return frames;
    }

    private static void SavePng(Control control, int width, int height, string? outDir, string fileName)
    {
        if (string.IsNullOrEmpty(outDir))
        {
            return;
        }

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(control);
        bitmap.Save(Path.Combine(outDir, fileName), PngBitmapEncoderOptions.Default);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
