using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ScanlineStudio.UI.Controls;

/// <summary>Un-stub-RX-tab Piece B2: RX Decoder Trace oscilloscope, port of legacy's
/// <c>TTScope::PaintScope</c> (`Scope.cpp:127-178`) -- reuses <see cref="Controls.SpectrumTraceControl"/>'s
/// own rendering PATTERN (custom <see cref="Control.Render"/> + per-pixel <c>DrawLine</c> walk over
/// an array), not the class itself: this is time-domain, not frequency-domain, and legacy's own
/// pan/zoom/gain semantics for this control genuinely differ from the spectrum's.
///
/// Two independently-scaled, independently-positioned traces stacked in the control's own top/
/// bottom halves -- NOT a synchronized dual-channel overlay (channel 0 and channel 1 are not
/// index-aligned to a shared timebase, see <c>AnalogFmSstvDecoder</c>'s own Decoder Trace capture
/// doc comments). Channel 0 (top half) is BOTTOM-anchored (a tone-envelope magnitude, always ≥0);
/// channel 1 (bottom half) is CENTER-anchored (a bipolar frequency deviation around the picture
/// center frequency) -- both anchor choices and the exact Y-scale divisors (16384.0 vs 32768.0) are
/// legacy's own (`Scope.cpp:162-167`), not invented. Both halves get the dotted zero-reference line
/// regardless of channel (legacy's own <c>if(n&lt;2)</c> check at `Scope.cpp:144` is vacuously
/// always true -- <c>n</c> is only ever 0 or 1 -- so this isn't a "channel 0 only" gate the way it
/// might look; ported faithfully rather than "fixed").</summary>
public sealed class DecoderTraceControl : Control
{
    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)), thickness: 1);
    private static readonly IPen CenterLinePen = new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), thickness: 1, dashStyle: DashStyle.Dot);

    public static readonly StyledProperty<double[]?> Channel0Property =
        AvaloniaProperty.Register<DecoderTraceControl, double[]?>(nameof(Channel0));

    public static readonly StyledProperty<double[]?> Channel1Property =
        AvaloniaProperty.Register<DecoderTraceControl, double[]?>(nameof(Channel1));

    public static readonly StyledProperty<int> XOffsetProperty =
        AvaloniaProperty.Register<DecoderTraceControl, int>(nameof(XOffset), defaultValue: 3072);

    public static readonly StyledProperty<int> XWindowProperty =
        AvaloniaProperty.Register<DecoderTraceControl, int>(nameof(XWindow), defaultValue: 2048);

    public static readonly StyledProperty<double> GainProperty =
        AvaloniaProperty.Register<DecoderTraceControl, double>(nameof(Gain), defaultValue: 2.0);

    static DecoderTraceControl()
    {
        AffectsRender<DecoderTraceControl>(Channel0Property, Channel1Property, XOffsetProperty, XWindowProperty, GainProperty);
    }

    public double[]? Channel0 { get => GetValue(Channel0Property); set => SetValue(Channel0Property, value); }
    public double[]? Channel1 { get => GetValue(Channel1Property); set => SetValue(Channel1Property, value); }
    public int XOffset { get => GetValue(XOffsetProperty); set => SetValue(XOffsetProperty, value); }
    public int XWindow { get => GetValue(XWindowProperty); set => SetValue(XWindowProperty, value); }
    public double Gain { get => GetValue(GainProperty); set => SetValue(GainProperty, value); }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 1 || height < 1)
        {
            return;
        }

        var halfHeight = height / 2.0;
        DrawChannel(context, Channel0, top: 0, halfHeight, width, scaleDivisor: 16384.0, centerAnchored: false);
        DrawChannel(context, Channel1, top: halfHeight, halfHeight, width, scaleDivisor: 32768.0, centerAnchored: true);
    }

    private void DrawChannel(DrawingContext context, double[]? data, double top, double halfHeight, double width, double scaleDivisor, bool centerAnchored)
    {
        var bottom = top + halfHeight;
        var centerY = top + halfHeight / 2.0;
        context.DrawLine(CenterLinePen, new Point(0, centerY), new Point(width, centerY));

        var xWindow = Math.Max(1, XWindow);
        var pixelWidth = Math.Max(1, (int)width);
        if (data is null || data.Length == 0)
        {
            return;
        }

        var gain = Gain;
        var previous = default(Point);
        for (var x = 0; x < pixelWidth; x++)
        {
            var xx = (x * xWindow / pixelWidth) + XOffset;
            if (xx < 0 || xx >= data.Length)
            {
                continue;
            }

            var xe = xx + (xWindow / pixelWidth);
            if (xe >= data.Length)
            {
                xe = data.Length - 1;
            }

            for (; xx <= xe; xx++)
            {
                var d = data[xx];
                var y = centerAnchored
                    ? bottom - (d * halfHeight * gain / scaleDivisor) - (halfHeight / 2.0)
                    : bottom - (d * halfHeight * gain / scaleDivisor) - 1;
                y = Math.Clamp(y, top, bottom);

                var point = new Point(x, y);
                if (x != 0)
                {
                    context.DrawLine(TracePen, previous, point);
                }

                previous = point;
            }
        }
    }
}
