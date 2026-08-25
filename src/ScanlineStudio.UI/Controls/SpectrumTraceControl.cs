using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.UI.Controls;

/// <summary>Live FFT amplitude-vs-frequency trace, filling mock2's own "Spectrum" placeholder
/// (batch 8b) -- <see cref="WaterfallControl"/>'s scrolling-bitmap sibling, sharing the same
/// <see cref="WaterfallFrame"/> data and the same <see cref="ZeroDb"/>/<see cref="GainDb"/>
/// normalization window (see <see cref="SpectrumTraceMath.MapDbToY"/>'s own doc comment for why).
/// Deliberately drawn directly via <see cref="Render"/> each frame (no bitmap/history buffer --
/// this is a single live trace, not a scrolling history), inside a parent <c>Border Classes="plot"</c>
/// which already supplies the dark background (same pattern <c>WaterfallPaneView.axaml</c> uses for
/// <see cref="WaterfallControl"/>), so this control paints no background of its own.</summary>
public sealed class SpectrumTraceControl : Control, ICustomHitTest
{
    /// <summary>Un-stub-RX-tab Piece A3, found live: this control's <see cref="Render"/> only ever
    /// draws thin lines (trace/markers), never a fill covering the whole surface -- Avalonia 11's
    /// composition renderer hit-tests against actually-painted geometry, not logical
    /// <see cref="Visual.Bounds"/>, so almost the entire control was NOT click-hittable without this;
    /// a real click on empty space between drawn lines silently fell through to the parent
    /// <c>Border</c> instead of reaching <see cref="OnPointerPressed"/>. Confirmed via a real running
    /// window, not just the headless test suite -- <see cref="SpectrumTraceControlTests"/>'s own
    /// pointer tests raise events directly on this control (bypassing hit-testing entirely), so they
    /// could not and did not catch this.</summary>
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    private static readonly IPen TracePen = new Pen(new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xE8)), thickness: 1);
    private static readonly IPen PeakHoldPen = new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0x60, 0xFF)), thickness: 1);
    private static readonly IPen SyncMarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0xD0, 0x60)), thickness: 1);
    private static readonly IPen FreqMarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(0xD0, 0x90, 0x30)), thickness: 1, dashStyle: DashStyle.Dash);
    // Un-stub-RX-tab Piece A3: a distinct color from the mode-derived reference markers above, since
    // this one represents an active filter the user placed, not a passive frequency reference. No
    // legacy precedent for this exact visual -- legacy's own PBoxFFTMouseDown never draws a notch
    // marker at all (Main.cpp has no PaintScope-style rendering for it), this is this port's own
    // UI-only addition (spec/06's UI exemption covers it, same as every other marker on this control).
    private static readonly IPen NotchMarkerPen = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0x30, 0x30)), thickness: 2);

    public static readonly StyledProperty<WaterfallFrame?> FrameProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, WaterfallFrame?>(nameof(Frame));

    public static readonly StyledProperty<double> ZeroDbProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, double>(nameof(ZeroDb), defaultValue: 0.0);

    public static readonly StyledProperty<double> GainDbProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, double>(nameof(GainDb), defaultValue: 50.0);

    public static readonly StyledProperty<double> StartHzProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, double>(nameof(StartHz), defaultValue: 1000.0);

    public static readonly StyledProperty<double> SpanHzProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, double>(nameof(SpanHz), defaultValue: 1600.0);

    public static readonly StyledProperty<bool> PeakHoldEnabledProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, bool>(nameof(PeakHoldEnabled));

    public static readonly StyledProperty<SstvModeDefinition?> CurrentModeProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, SstvModeDefinition?>(nameof(CurrentMode));

    public static readonly StyledProperty<bool> NotchEnabledProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, bool>(nameof(NotchEnabled));

    public static readonly StyledProperty<double> NotchFrequencyHzProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, double>(nameof(NotchFrequencyHz), defaultValue: 2400.0);

    /// <summary>Click-to-tune entry point (Un-stub-RX-tab Piece A3) -- invoked with the clicked/dragged
    /// frequency in Hz from <see cref="OnPointerPressed"/>/<see cref="OnPointerMoved"/>. A bindable
    /// <see cref="ICommand"/> property rather than a routed/CLR event, matching this control's existing
    /// <c>BinsPerPixel</c> precedent for reaching the owning view-model without a code-behind handler.</summary>
    public static readonly StyledProperty<ICommand?> NotchTuneRequestedCommandProperty =
        AvaloniaProperty.Register<SpectrumTraceControl, ICommand?>(nameof(NotchTuneRequestedCommand));

    /// <summary>Read-only computed telemetry, not a user input (auditor round-2 finding: Bins/px,
    /// Start, Span were over-determined as three independent knobs for two real degrees of freedom --
    /// Start/Span are the real controls, this is a live readout of what they currently work out to).
    /// Computed in <see cref="ArrangeOverride"/> plus the <see cref="FrameProperty"/>/
    /// <see cref="SpanHzProperty"/> changed handlers (auditor round-3 finding: computing this inside
    /// <see cref="Render"/> mutates the binding graph -- SetAndRaise -> OneWayToSource -> view-model
    /// -> a sibling NumericUpDown's own layout -- mid-render, which only worked by luck because the
    /// value is stable during a steady live-frame stream).</summary>
    public static readonly DirectProperty<SpectrumTraceControl, double> BinsPerPixelProperty =
        AvaloniaProperty.RegisterDirect<SpectrumTraceControl, double>(nameof(BinsPerPixel), o => o.BinsPerPixel);

    private double _binsPerPixel;
    private float[]? _peakDb;
    private int _bins;
    private DateTimeOffset? _lastFrameObservedAt;
    private IReadOnlyList<SpectrumMarker>? _cachedMarkers;
    private Size _lastArrangedSize;
    private bool _isDraggingNotch;

    static SpectrumTraceControl()
    {
        FrameProperty.Changed.AddClassHandler<SpectrumTraceControl>((control, _) =>
        {
            control.OnFrameChanged();
            control.UpdateBinsPerPixel();
        });
        SpanHzProperty.Changed.AddClassHandler<SpectrumTraceControl>((control, _) => control.UpdateBinsPerPixel());
        CurrentModeProperty.Changed.AddClassHandler<SpectrumTraceControl>((control, _) => control._cachedMarkers = null);

        // BinsPerPixelProperty must NEVER be added here (auditor round-3 finding): AffectsRender's
        // own change handler calls InvalidateVisual, which would re-enter Render while THIS render
        // pass's own SetAndRaise on BinsPerPixel is still unwinding -- self-limiting only because
        // SetAndRaise no-ops on an unchanged value, not a real guarantee against re-entrancy.
        AffectsRender<SpectrumTraceControl>(FrameProperty, ZeroDbProperty, GainDbProperty, StartHzProperty, SpanHzProperty, PeakHoldEnabledProperty, CurrentModeProperty, NotchEnabledProperty, NotchFrequencyHzProperty);
    }

    public WaterfallFrame? Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public double ZeroDb { get => GetValue(ZeroDbProperty); set => SetValue(ZeroDbProperty, value); }
    public double GainDb { get => GetValue(GainDbProperty); set => SetValue(GainDbProperty, value); }
    public double StartHz { get => GetValue(StartHzProperty); set => SetValue(StartHzProperty, value); }
    public double SpanHz { get => GetValue(SpanHzProperty); set => SetValue(SpanHzProperty, value); }
    public bool PeakHoldEnabled { get => GetValue(PeakHoldEnabledProperty); set => SetValue(PeakHoldEnabledProperty, value); }
    public SstvModeDefinition? CurrentMode { get => GetValue(CurrentModeProperty); set => SetValue(CurrentModeProperty, value); }
    public bool NotchEnabled { get => GetValue(NotchEnabledProperty); set => SetValue(NotchEnabledProperty, value); }
    public double NotchFrequencyHz { get => GetValue(NotchFrequencyHzProperty); set => SetValue(NotchFrequencyHzProperty, value); }
    public ICommand? NotchTuneRequestedCommand { get => GetValue(NotchTuneRequestedCommandProperty); set => SetValue(NotchTuneRequestedCommandProperty, value); }

    public double BinsPerPixel
    {
        get => _binsPerPixel;
        private set => SetAndRaise(BinsPerPixelProperty, ref _binsPerPixel, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _lastArrangedSize = finalSize;
        UpdateBinsPerPixel();
        return base.ArrangeOverride(finalSize);
    }

    private void UpdateBinsPerPixel()
    {
        var frame = Frame;
        var width = _lastArrangedSize.Width;
        var spanHz = SpanHz;
        if (frame is null || width < 1 || spanHz <= 0 || frame.BinWidthHz <= 0)
        {
            // Hold the last value -- matches the original Render-based behavior for a collapsed
            // (ViewMode=WaterfallOnly) or not-yet-laid-out control, which is a reasonable display
            // choice (auditor round-3: "preserve it explicitly, don't leave it as a side effect").
            return;
        }

        BinsPerPixel = spanHz / frame.BinWidthHz / width;
    }

    private void OnFrameChanged()
    {
        var frame = Frame;
        if (frame is null || frame.MagnitudesDb.Count == 0)
        {
            return;
        }

        var bins = frame.MagnitudesDb.Count;
        if (_peakDb is null || _bins != bins)
        {
            _bins = bins;
            _peakDb = new float[bins];
            Array.Fill(_peakDb, float.NegativeInfinity);
            _lastFrameObservedAt = null;
        }

        var elapsedSeconds = _lastFrameObservedAt is null ? 0.0 : (frame.ObservedAt - _lastFrameObservedAt.Value).TotalSeconds;
        _lastFrameObservedAt = frame.ObservedAt;
        for (var i = 0; i < bins; i++)
        {
            _peakDb![i] = (float)SpectrumTraceMath.DecayPeak(_peakDb[i], frame.MagnitudesDb[i], elapsedSeconds);
        }
    }

    public override void Render(DrawingContext context)
    {
        var frame = Frame;
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (frame is null || frame.MagnitudesDb.Count == 0 || width < 1 || height < 1)
        {
            return;
        }

        var startHz = StartHz;
        var spanHz = SpanHz;
        var zeroDb = ZeroDb;
        var gainDb = GainDb;

        DrawMarkers(context, CurrentMode, startHz, spanHz, width, height);
        if (NotchEnabled)
        {
            var notchX = SpectrumTraceMath.MapFrequencyToX(NotchFrequencyHz, startHz, spanHz, width);
            if (notchX >= 0 && notchX <= width)
            {
                context.DrawLine(NotchMarkerPen, new Point(notchX, 0), new Point(notchX, height));
            }
        }

        if (PeakHoldEnabled && _peakDb is not null)
        {
            DrawTrace(context, PeakHoldPen, _peakDb, frame.BinWidthHz, startHz, spanHz, zeroDb, gainDb, width, height);
        }

        DrawTrace(context, TracePen, frame.MagnitudesDb, frame.BinWidthHz, startHz, spanHz, zeroDb, gainDb, width, height);
    }

    /// <summary>Left-click-anywhere tunes the notch AND enables it (Un-stub-RX-tab Piece A3, matching
    /// legacy's own left-click-both-tunes-and-enables gesture, <c>Main.cpp:14364-14371</c>, and
    /// <see cref="ISstvDecoder.RequestNotch"/>'s own "frequencyHz implies enabled" contract) -- the
    /// bound VM decides enable/tune semantics from the raw frequency this passes it, this control has
    /// no notion of "enabled" beyond what it's told to render. Captures the pointer so a drag off the
    /// control's own bounds still delivers <see cref="OnPointerMoved"/> events (continuous
    /// retune-while-held).</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        e.Pointer.Capture(this);
        _isDraggingNotch = true;
        RequestNotchTune(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        // Auditor-caught hardening: _isDraggingNotch alone would keep retuning on a button-less hover
        // if pointer capture were ever lost without a release reaching this control (element detached
        // mid-drag, touch cancel) -- self-healing against that, not reachable via ordinary mouse input
        // (platform capture guarantees the release lands here) but cheap insurance regardless.
        if (_isDraggingNotch && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            RequestNotchTune(e.GetPosition(this).X);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isDraggingNotch = false;
        e.Pointer.Capture(null);
    }

    private void RequestNotchTune(double x)
    {
        var width = Bounds.Width;
        if (width < 1)
        {
            return;
        }

        var clampedX = Math.Clamp(x, 0, width);
        var frequencyHz = SpectrumTraceMath.MapXToFrequency(clampedX, StartHz, SpanHz, width);
        var command = NotchTuneRequestedCommand;
        if (command?.CanExecute(frequencyHz) is true)
        {
            command.Execute(frequencyHz);
        }
    }

    private void DrawMarkers(DrawingContext context, SstvModeDefinition? mode, double startHz, double spanHz, double width, double height)
    {
        // Memoized (auditor round-3 finding): ComputeMarkerFrequencies allocates a SortedSet/LINQ
        // pipeline/List/N records -- CurrentMode only actually changes on ModeDetected, not every
        // render, and CurrentModeProperty's own changed handler above invalidates this cache.
        _cachedMarkers ??= SpectrumTraceMath.ComputeMarkerFrequencies(mode);
        foreach (var marker in _cachedMarkers)
        {
            var x = SpectrumTraceMath.MapFrequencyToX(marker.FrequencyHz, startHz, spanHz, width);
            if (x < 0 || x > width)
            {
                continue;
            }

            context.DrawLine(marker.IsSync ? SyncMarkerPen : FreqMarkerPen, new Point(x, 0), new Point(x, height));
        }
    }

    private static void DrawTrace(DrawingContext context, IPen pen, IReadOnlyList<float> magnitudesDb, double binWidthHz, double startHz, double spanHz, double zeroDb, double gainDb, double width, double height)
    {
        Point? previous = null;
        for (var i = 0; i < magnitudesDb.Count; i++)
        {
            var freqHz = i * binWidthHz;
            if (freqHz < startHz || freqHz > startHz + spanHz)
            {
                continue;
            }

            var x = SpectrumTraceMath.MapFrequencyToX(freqHz, startHz, spanHz, width);
            var y = SpectrumTraceMath.MapDbToY(magnitudesDb[i], zeroDb, gainDb, height);
            var point = new Point(x, Math.Clamp(y, 0, height));
            if (previous is not null)
            {
                context.DrawLine(pen, previous.Value, point);
            }

            previous = point;
        }
    }
}
