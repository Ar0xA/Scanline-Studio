using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Sstv;
using ScanlineStudio.UI.Controls;
using SixLabors.ImageSharp.PixelFormats;

namespace ScanlineStudio.UI.FontTests;

/// <summary>Real-Skia render of a hosted <see cref="WaterfallControl"/> must match, pixel for pixel, a naive reference
/// that keeps a "row 0 = newest" list, colours it with <see cref="WaterfallPalette.Lerp"/> and draws it with the same
/// <see cref="WaterfallRenderMath.TryComputeWindow"/> rects. Pins the waterfall history layout across ring wrap,
/// Zero/Gain recolour, bin-count change and Dispose.</summary>
public sealed class WaterfallControlRenderEqualityTests
{
    private const int ControlWidth = 517;
    private const int ControlHeight = 173;
    private const int HistoryRows = 200;
    private const double SampleRateHz = 11025;

    [AvaloniaFact]
    public void Render_MatchesNaiveReference_AcrossWrapRecolourBinCountChangeAndDispose()
    {
        var control = new WaterfallControl();
        var window = new Window { Width = ControlWidth, Height = ControlHeight, Content = control };
        var reference = new NaiveWaterfallReference();
        window.Show();
        Pump(window);
        try
        {
            Assert.Equal(new Size(ControlWidth, ControlHeight), control.Bounds.Size);

            Push(window, control, reference, bins: 512, count: 450, seed: 1);
            AssertRendersEqual(control, reference, "450 frames, 512 bins");

            control.ZeroDb = -30;
            control.GainDb = 70;
            reference.ZeroDb = -30;
            reference.GainDb = 70;
            AssertRendersEqual(control, reference, "after a Zero/Gain change");

            Push(window, control, reference, bins: 384, count: 450, seed: 2);
            AssertRendersEqual(control, reference, "after a bin-count change to 384");

            control.Dispose();
            reference.Clear();
            Push(window, control, reference, bins: 512, count: 137, seed: 3);
            AssertRendersEqual(control, reference, "after Dispose, 137 frames");
            Push(window, control, reference, bins: 512, count: 313, seed: 4);
            AssertRendersEqual(control, reference, "after Dispose, 450 frames");
        }
        finally
        {
            window.Close();
            reference.Dispose();
        }
    }

    private static void Push(Window window, WaterfallControl control, NaiveWaterfallReference reference, int bins, int count, int seed)
    {
        var binWidthHz = SampleRateHz / (bins * 2);
        var state = (uint)(seed * 2654435761u) | 1u;
        var t0 = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        for (var f = 0; f < count; f++)
        {
            var db = new float[bins];
            for (var i = 0; i < bins; i++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                // Spread over -25..+75 dB so both palette clamps and every stop are exercised.
                db[i] = -25f + ((state % 1000) / 10f);
            }

            control.Frame = new WaterfallFrame(db, binWidthHz, t0.AddMilliseconds(f * 46));
            reference.Push(db, binWidthHz);
            if (f % 64 == 63)
            {
                Pump(window);
            }
        }

        Pump(window);
    }

    private static void AssertRendersEqual(WaterfallControl control, NaiveWaterfallReference reference, string stage)
    {
        using var actual = RenderToImage(control);
        reference.Rebuild();
        reference.Measure(new Size(ControlWidth, ControlHeight));
        reference.Arrange(new Rect(0, 0, ControlWidth, ControlHeight));
        using var expected = RenderToImage(reference);

        var differing = 0;
        var maxDelta = 0;
        var colours = new HashSet<Rgba32>();
        for (var y = 0; y < ControlHeight; y++)
        {
            for (var x = 0; x < ControlWidth; x++)
            {
                var a = actual[x, y];
                var e = expected[x, y];
                colours.Add(a);
                if (a != e)
                {
                    differing++;
                    maxDelta = Math.Max(maxDelta, Math.Max(Math.Abs(a.R - e.R), Math.Max(Math.Abs(a.G - e.G), Math.Abs(a.B - e.B))));
                }
            }
        }

        Assert.True(colours.Count > 20, $"{stage}: the render is near-uniform ({colours.Count} colours), so the comparison would be vacuous");
        Assert.True(differing == 0, $"{stage}: {differing} pixels differ from the naive reference, max channel delta {maxDelta}");
    }

    private static SixLabors.ImageSharp.Image<Rgba32> RenderToImage(Control control)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize(ControlWidth, ControlHeight), new Vector(96, 96));
        bitmap.Render(control);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return SixLabors.ImageSharp.Image.Load<Rgba32>(stream);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Row 0 is the newest frame. Rows never written stay all-zero bytes. That matches the control only while no
    /// Zero/Gain recolour has run since its history was allocated (a recolour paints unwritten rows palette near-black), so
    /// every recolour in this test happens with a full history.</summary>
    private sealed class NaiveWaterfallReference : Control, IDisposable
    {
        private readonly List<float[]> _rows = [];
        private WriteableBitmap? _bitmap;
        private int _bins;
        private double _binWidthHz;

        public double ZeroDb { get; set; }

        public double GainDb { get; set; } = 50;

        public double StartHz { get; set; } = 1000;

        public double SpanHz { get; set; } = 1600;

        public void Push(float[] db, double binWidthHz)
        {
            if (db.Length != _bins)
            {
                _rows.Clear();
                _bins = db.Length;
            }

            _binWidthHz = binWidthHz;
            _rows.Insert(0, db);
            if (_rows.Count > HistoryRows)
            {
                _rows.RemoveAt(HistoryRows);
            }
        }

        public void Clear()
        {
            _rows.Clear();
            _bins = 0;
            _binWidthHz = 0;
            _bitmap?.Dispose();
            _bitmap = null;
        }

        public void Dispose() => Clear();

        public void Rebuild()
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(_bins, HistoryRows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            var row = new byte[_bins * 4];
            using var frameBuffer = _bitmap.Lock();
            for (var y = 0; y < HistoryRows; y++)
            {
                Array.Clear(row);
                if (y < _rows.Count)
                {
                    for (var i = 0; i < _bins; i++)
                    {
                        var normalized = GainDb > 0 ? (_rows[y][i] - ZeroDb) / GainDb : 0.0;
                        var (r, g, b) = WaterfallPalette.Lerp(normalized);
                        row[(i * 4) + 0] = b;
                        row[(i * 4) + 1] = g;
                        row[(i * 4) + 2] = r;
                        row[(i * 4) + 3] = 255;
                    }
                }

                Marshal.Copy(row, 0, frameBuffer.Address + (y * frameBuffer.RowBytes), row.Length);
            }
        }

        public override void Render(DrawingContext context)
        {
            if (_bitmap is null || !WaterfallRenderMath.TryComputeWindow(StartHz, SpanHz, _binWidthHz, _bins, Bounds.Width,
                    out var sourceX, out var sourceWidth, out var destX, out var destWidth))
            {
                return;
            }

            context.DrawImage(_bitmap, new Rect(sourceX, 0, sourceWidth, HistoryRows), new Rect(destX, 0, destWidth, Bounds.Height));
        }
    }
}
