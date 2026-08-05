using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Yoniq.Abstractions.Sstv;

namespace Yoniq.UI.Controls;

/// <summary>Minimal scrolling waterfall/spectrogram -- first pass only (spec/09-ui.md: "escalate to
/// SkiaSharp only if profiling shows DrawingContext insufficient"), deliberately not visually
/// elaborate: the user explicitly deprioritized waterfall polish relative to RX/TX image handling and
/// templating (see memory `feedback_ui_effort_allocation`). Plain grayscale, fixed dB range, no color
/// gradient -- refine later, not now.
///
/// Bound via <see cref="Frame"/> (set from the pane view-model's already-Dispatcher-marshaled
/// <c>LatestFrame</c> property, so this control never touches threading itself).</summary>
public sealed class WaterfallControl : Control, IDisposable
{
    private const int HistoryRows = 200;
    private const double MinDb = -100;
    private const double MaxDb = 0;

    public static readonly StyledProperty<WaterfallFrame?> FrameProperty =
        AvaloniaProperty.Register<WaterfallControl, WaterfallFrame?>(nameof(Frame));

    private WriteableBitmap? _bitmap;
    private byte[]? _grayHistory;
    private int _bins;

    static WaterfallControl()
    {
        FrameProperty.Changed.AddClassHandler<WaterfallControl>((control, _) => control.OnFrameChanged());
        AffectsRender<WaterfallControl>(FrameProperty);
    }

    public WaterfallFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is not null && Bounds.Width > 0 && Bounds.Height > 0)
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.PixelSize.ToSize(1)), Bounds);
        }
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Dispose();
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void OnFrameChanged()
    {
        var frame = Frame;
        if (frame is null || frame.MagnitudesDb.Count == 0)
        {
            return;
        }

        var bins = frame.MagnitudesDb.Count;
        if (_grayHistory is null || _bins != bins)
        {
            _bins = bins;
            _grayHistory = new byte[bins * HistoryRows];
            _bitmap = new WriteableBitmap(new PixelSize(bins, HistoryRows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        // Scroll history down by one row (row 0 is always the newest), then write the new row.
        Array.Copy(_grayHistory, 0, _grayHistory, bins, bins * (HistoryRows - 1));
        for (var i = 0; i < bins; i++)
        {
            var normalized = (frame.MagnitudesDb[i] - MinDb) / (MaxDb - MinDb);
            _grayHistory[i] = (byte)(Math.Clamp(normalized, 0.0, 1.0) * 255);
        }

        BlitHistoryToBitmap();
        InvalidateVisual();
    }

    private void BlitHistoryToBitmap()
    {
        if (_bitmap is null || _grayHistory is null)
        {
            return;
        }

        using var frameBuffer = _bitmap.Lock();
        var rowBuffer = new byte[_bins * 4];
        for (var y = 0; y < HistoryRows; y++)
        {
            for (var x = 0; x < _bins; x++)
            {
                var gray = _grayHistory[(y * _bins) + x];
                var offset = x * 4;
                rowBuffer[offset + 0] = gray;
                rowBuffer[offset + 1] = gray;
                rowBuffer[offset + 2] = gray;
                rowBuffer[offset + 3] = 255;
            }

            System.Runtime.InteropServices.Marshal.Copy(rowBuffer, 0, frameBuffer.Address + (y * frameBuffer.RowBytes), rowBuffer.Length);
        }
    }
}
