using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.UI.Controls;

/// <summary>Minimal scrolling waterfall/spectrogram (spec/09-ui.md: "escalate to SkiaSharp only if
/// profiling shows DrawingContext insufficient"). Colorized via <see cref="WaterfallPalette"/> (batch
/// 8) -- <see cref="ZeroDb"/>/<see cref="GainDb"/> control the dB-to-color normalization window,
/// wired to mock2's "Zero"/"Gain" sliders (<c>MainWindow.axaml</c>'s Spectrum &amp; waterfall card),
/// defaults chosen from real captured-audio measurement (see <c>WaterfallPaneViewModel</c>'s own doc
/// comment for the measured dB ranges), not legacy's adaptive `WaterMin`/`WaterMax` AGC (spec/06
/// exempts this visualization from strict legacy-port fidelity).
///
/// Bound via <see cref="Frame"/> (set from the pane view-model's already-Dispatcher-marshaled
/// <c>LatestFrame</c> property, so this control never touches threading itself).</summary>
public sealed class WaterfallControl : Control, IDisposable
{
    private const int HistoryRows = 200;
    private const int BytesPerPixel = 4;

    public static readonly StyledProperty<WaterfallFrame?> FrameProperty =
        AvaloniaProperty.Register<WaterfallControl, WaterfallFrame?>(nameof(Frame));

    public static readonly StyledProperty<double> ZeroDbProperty =
        AvaloniaProperty.Register<WaterfallControl, double>(nameof(ZeroDb), defaultValue: 0.0);

    public static readonly StyledProperty<double> GainDbProperty =
        AvaloniaProperty.Register<WaterfallControl, double>(nameof(GainDb), defaultValue: 50.0);

    private WriteableBitmap? _bitmap;
    private byte[]? _colorHistory;

    // Auditor-caught (batch 8): dragging Zero/Gain must recolor ALREADY-SCROLLED-IN history rows,
    // not just future ones -- _colorHistory alone can't do that since the raw dB magnitude that
    // produced each row's color is gone once it's been through the LERP once. Kept as a parallel
    // buffer purely so a slider drag can trigger one full recolor pass (RecolorizeAll) without
    // needing to re-decode audio; never touched on the per-frame hot path beyond one row write.
    private float[]? _dbHistory;
    private int _bins;

    static WaterfallControl()
    {
        FrameProperty.Changed.AddClassHandler<WaterfallControl>((control, _) => control.OnFrameChanged());
        // RecolorizeAll already calls InvalidateVisual itself once it's done recoloring -- ZeroDb/
        // GainDb are deliberately NOT also passed to AffectsRender, which would invalidate BEFORE
        // the recolor pass runs and just repaint the stale bitmap a frame early.
        ZeroDbProperty.Changed.AddClassHandler<WaterfallControl>((control, _) => control.RecolorizeAll());
        GainDbProperty.Changed.AddClassHandler<WaterfallControl>((control, _) => control.RecolorizeAll());
        AffectsRender<WaterfallControl>(FrameProperty);
    }

    public WaterfallFrame? Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    /// <summary>Floor reference in dB -- magnitudes at or below this render as the palette's weakest
    /// (near-black) stop. See class doc comment for how the default was chosen.</summary>
    public double ZeroDb
    {
        get => GetValue(ZeroDbProperty);
        set => SetValue(ZeroDbProperty, value);
    }

    /// <summary>Span in dB above <see cref="ZeroDb"/> -- magnitudes at or above <c>ZeroDb + GainDb</c>
    /// render as the palette's strongest (red) stop.</summary>
    public double GainDb
    {
        get => GetValue(GainDbProperty);
        set => SetValue(GainDbProperty, value);
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
        // Auditor-caught (batch 8): a non-selected TabItem detaches its content (WaterfallPaneView
        // sits inside the Receive tab, MainWindow.axaml), so Dispose runs on every tab switch away
        // from Receive, not just app shutdown. Leaving _colorHistory/_bins behind meant the next
        // frame's `_colorHistory is null || _bins != bins` init-guard was false, _bitmap was never
        // recreated, and the waterfall went permanently blank for the rest of the session the moment
        // a user switched tabs and back. Full reset, not just the bitmap.
        _bitmap?.Dispose();
        _bitmap = null;
        _colorHistory = null;
        _dbHistory = null;
        _bins = 0;
    }

    private void OnFrameChanged()
    {
        var frame = Frame;
        if (frame is null || frame.MagnitudesDb.Count == 0)
        {
            return;
        }

        var bins = frame.MagnitudesDb.Count;
        if (_colorHistory is null || _dbHistory is null || _bins != bins)
        {
            _bins = bins;
            _colorHistory = new byte[bins * BytesPerPixel * HistoryRows];
            _dbHistory = new float[bins * HistoryRows];
            // Auditor-caught (batch 8): a zero-filled _dbHistory is indistinguishable from a
            // genuine 0dB reading -- RecolorizeAll would colorize never-written rows as real data
            // the first time a slider moves (e.g. ZeroDb=-70/GainDb=20 normalizes 0dB to 3.5,
            // clamped to 1.0 -- solid red across the whole not-yet-filled history). NegativeInfinity
            // clamps to 0.0 in WaterfallPalette.Lerp regardless of ZeroDb/GainDb, landing on the
            // palette's near-black stop -- matching what an unwritten row looked like before this
            // batch (WaterfallSource.cs:83 already guarantees no non-finite value ever reaches a
            // REAL frame's MagnitudesDb, so this sentinel can never collide with real data).
            Array.Fill(_dbHistory, float.NegativeInfinity);
            _bitmap = new WriteableBitmap(new PixelSize(bins, HistoryRows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }

        var rowBytes = bins * BytesPerPixel;

        // Scroll history down by one row (row 0 is always the newest), then write the new row --
        // the gradient LERP happens exactly once per bin here on the per-frame hot path, never
        // recomputed for the other 199 rows unless Zero/Gain actually changes (RecolorizeAll).
        Array.Copy(_colorHistory!, 0, _colorHistory!, rowBytes, rowBytes * (HistoryRows - 1));
        Array.Copy(_dbHistory!, 0, _dbHistory!, bins, bins * (HistoryRows - 1));
        for (var i = 0; i < bins; i++)
        {
            _dbHistory![i] = frame.MagnitudesDb[i];
        }

        ColorizeRow(rowIndex: 0, GainDb, ZeroDb);

        BlitHistoryToBitmap();
        InvalidateVisual();
    }

    /// <summary>Auditor-caught (batch 8): without this, dragging Zero/Gain only affected FUTURE
    /// rows -- the 199 already-scrolled-in rows kept whatever color the OLD slider position produced
    /// until each one aged out 200 frames later, so the display would visibly show two different
    /// normalizations mixed together for a long stretch after every slider move. One full recolor
    /// pass per slider change (not per frame) -- <see cref="_dbHistory"/> is exactly what makes this
    /// possible without re-decoding audio.</summary>
    private void RecolorizeAll()
    {
        if (_dbHistory is null || _colorHistory is null)
        {
            return;
        }

        var gainDb = GainDb;
        var zeroDb = ZeroDb;
        for (var row = 0; row < HistoryRows; row++)
        {
            ColorizeRow(row, gainDb, zeroDb);
        }

        BlitHistoryToBitmap();
        InvalidateVisual();
    }

    private void ColorizeRow(int rowIndex, double gainDb, double zeroDb)
    {
        var dbRowStart = rowIndex * _bins;
        var colorRowStart = rowIndex * _bins * BytesPerPixel;
        for (var i = 0; i < _bins; i++)
        {
            var normalized = gainDb > 0 ? (_dbHistory![dbRowStart + i] - zeroDb) / gainDb : 0.0;
            var (r, g, b) = WaterfallPalette.Lerp(normalized);
            var offset = colorRowStart + (i * BytesPerPixel);
            _colorHistory![offset + 0] = b;
            _colorHistory![offset + 1] = g;
            _colorHistory![offset + 2] = r;
            _colorHistory![offset + 3] = 255;
        }
    }

    private void BlitHistoryToBitmap()
    {
        if (_bitmap is null || _colorHistory is null)
        {
            return;
        }

        var rowBytes = _bins * BytesPerPixel;
        using var frameBuffer = _bitmap.Lock();
        for (var y = 0; y < HistoryRows; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(_colorHistory, y * rowBytes, frameBuffer.Address + (y * frameBuffer.RowBytes), rowBytes);
        }
    }
}
