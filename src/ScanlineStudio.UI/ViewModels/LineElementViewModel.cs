using ScanlineStudio.UI.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="TemplateLineElement"/> (TX editor
/// gap-items plan, line element, 2026-09-01) -- 4th <see cref="ITemplateElementViewModel"/>
/// implementation alongside <see cref="OverlayElementViewModel"/> (text)/<see cref="BoxElementViewModel"/>
/// (box)/<see cref="ImageElementViewModel"/> (image). Same parent-pushed command/undo wiring
/// convention as <see cref="BoxElementViewModel"/>, its closest structural analog (no text, has
/// stroke-style properties) -- see that class's own doc comment.
///
/// <para><b>Endpoints are the sole truth; X/Y/Width/Height are DERIVED, not backing fields</b> (3
/// rounds of plan-review settled this design -- see <c>project_line_element_plan</c> memory for the
/// full reasoning). <see cref="X1"/>/<see cref="Y1"/>/<see cref="X2"/>/<see cref="Y2"/> are the real
/// <c>[ObservableProperty]</c> fields, same full-working-copy-normalized space as every other
/// element's X/Y. <see cref="ITemplateElementViewModel"/>'s shared box contract is satisfied by
/// computed get/set properties below: too much GENERIC canvas machinery (drag-move, align-to-crop,
/// clone/paste offset, nudge) writes X/Y/Width/Height directly, and making those setters translate/
/// scale the real endpoints is what lets all of it work UNMODIFIED against a line -- the alternative
/// (a line that also independently stores its own box) would desync the two representations on every
/// external write.</para></summary>
public sealed partial class LineElementViewModel : ObservableObject, ITemplateElementViewModel
{
    [ObservableProperty]
    private double _x1 = 0.35;

    [ObservableProperty]
    private double _y1 = 0.5;

    [ObservableProperty]
    private double _x2 = 0.65;

    [ObservableProperty]
    private double _y2 = 0.5;

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private Rgb24 _strokeColor = new(255, 255, 255);

    /// <summary>Image-HEIGHT-relative, same convention as <see cref="BoxElementViewModel.BorderThickness"/>.
    /// ~2-3px at a typical SSTV mode's own render height.</summary>
    [ObservableProperty]
    private double _strokeThickness = 0.01;

    [ObservableProperty]
    private double _opacity = 1.0;

    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    public IRelayCommand? RemoveCommand { get; init; }

    public IRelayCommand? MoveUpCommand { get; init; }

    public IRelayCommand? MoveDownCommand { get; init; }

    public IRelayCommand? BringToFrontCommand { get; init; }

    public IRelayCommand? SendToBackCommand { get; init; }

    public IRelayCommand? DuplicateCommand { get; init; }

    public IRelayCommand? AlignSelectedElementToCropCommand { get; init; }

    public IRelayCommand? CopyCommand { get; init; }

    public IRelayCommand? CutCommand { get; init; }

    public IRelayCommand? PasteCommand { get; init; }

    public IRelayCommand? FlattenCommand { get; init; }

    /// <summary>Line-only (matches <see cref="BoxElementViewModel.CopyStyleCommand"/>'s own
    /// per-kind-style precedent) -- copies StrokeColor/StrokeThickness/Opacity only.</summary>
    public IRelayCommand? CopyStyleCommand { get; init; }

    public IRelayCommand? PasteStyleCommand { get; init; }

    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    public Action? PushUndoSnapshotForStyleChange { get; init; }

    /// <summary>Get: endpoint midpoint. Set: translate BOTH endpoints by the delta -- required by
    /// every generic caller that moves an element via <see cref="ITemplateElementViewModel.X"/>
    /// (drag-move, align-to-crop, clone/paste offset, arrow-key nudge) rather than the endpoints
    /// directly.</summary>
    public double X
    {
        get => (X1 + X2) / 2;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            var delta = value - X;
            X1 += delta;
            X2 += delta;
        }
    }

    public double Y
    {
        get => (Y1 + Y2) / 2;
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            var delta = value - Y;
            Y1 += delta;
            Y2 += delta;
        }
    }

    /// <summary>Get: <c>Math.Abs(X2 - X1)</c> -- MUST be the absolute extent, not a signed
    /// difference (plan-review round 3 finding: a signed getter makes an identity write -- reading
    /// the current Width back and writing the SAME value -- flip the line end-for-end on a
    /// right-to-left line, since <c>Sign(dx)</c> of the getter's own signed value would invert).
    /// Set: rejects non-finite input (round 3 finding: an unguarded value from
    /// <see cref="TxImageEditorPaneViewModel.SelectedElementWidthPx"/>'s own text-box binding can be
    /// NaN/Infinity, which would otherwise corrupt BOTH endpoints and make the next
    /// <c>Math.Sign</c>-based write throw); clamps to non-negative; scales the endpoint extent about
    /// the CENTER, preserving the current left-right orientation. A currently-degenerate X extent
    /// (line is vertical, <c>X1 == X2</c>) has no orientation to preserve -- defaults to positive
    /// (left-to-right), a stated decision, not left implicit.</summary>
    public double Width
    {
        get => Math.Abs(X2 - X1);
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            var w = Math.Abs(value);
            var dx = X2 - X1;
            var cx = (X1 + X2) / 2;
            if (w == 0)
            {
                X1 = cx;
                X2 = cx;
            }
            else if (dx == 0)
            {
                X1 = cx - (w / 2);
                X2 = cx + (w / 2);
            }
            else
            {
                var newDx = dx < 0 ? -w : w;
                X1 = cx - (newDx / 2);
                X2 = cx + (newDx / 2);
            }
        }
    }

    /// <inheritdoc cref="Width"/>
    public double Height
    {
        get => Math.Abs(Y2 - Y1);
        set
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            var h = Math.Abs(value);
            var dy = Y2 - Y1;
            var cy = (Y1 + Y2) / 2;
            if (h == 0)
            {
                Y1 = cy;
                Y2 = cy;
            }
            else if (dy == 0)
            {
                Y1 = cy - (h / 2);
                Y2 = cy + (h / 2);
            }
            else
            {
                var newDy = dy < 0 ? -h : h;
                Y1 = cy - (newDy / 2);
                Y2 = cy + (newDy / 2);
            }
        }
    }

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    [ObservableProperty]
    private ElementPreviewMetrics? _previewMetrics;

    private double StyleImageHeight => PreviewMetrics?.ImageHeight ?? ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>Endpoint 1 in CONTAINER-relative canvas pixels (relative to this element's own
    /// <see cref="LeftPixels"/>/<see cref="TopPixels"/>, matching how the per-element root
    /// <c>Canvas</c> is itself positioned on the outer canvas) -- what the AXAML <c>Line</c>
    /// control's own <c>StartPoint</c> binds to.</summary>
    public Avalonia.Point CanvasStartPoint => new((X1 * ImageWidth) - LeftPixels, (Y1 * ImageHeight) - TopPixels);

    public Avalonia.Point CanvasEndPoint => new((X2 * ImageWidth) - LeftPixels, (Y2 * ImageHeight) - TopPixels);

    public double CanvasStrokeThicknessPixels => StrokeThickness * StyleImageHeight;

    public double TargetModeHeightPx { get; init; }

    /// <inheritdoc cref="BoxElementViewModel.BorderThicknessPx"/>
    public double StrokeThicknessPx
    {
        get => StrokeThickness * TargetModeHeightPx;
        set
        {
            if (TargetModeHeightPx <= 0)
            {
                return;
            }

            StrokeThickness = value / TargetModeHeightPx;
        }
    }

    partial void OnX1Changing(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnY1Changing(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnX2Changing(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnY2Changing(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnX1Changed(double value) => RaiseGeometryChanged();

    partial void OnY1Changed(double value) => RaiseGeometryChanged();

    partial void OnX2Changed(double value) => RaiseGeometryChanged();

    partial void OnY2Changed(double value) => RaiseGeometryChanged();

    /// <summary>Every derived property that could have changed from an endpoint write -- X1..Y2 are
    /// the ONLY real backing fields, so any of them changing can move X/Y (translate), Width/Height
    /// (extent), and every pixel-space property derived from those. The parent VM's own
    /// recompute-notification filter (<c>OnOverlayElementPropertyChanged</c>) filters these BACK out
    /// for a <see cref="LineElementViewModel"/> sender specifically, so only the raw X1..Y2 names
    /// (unfiltered) drive exactly one recompute pass per change -- see that method's own doc comment.</summary>
    private void RaiseGeometryChanged()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Width));
        OnPropertyChanged(nameof(Height));
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(CanvasStartPoint));
        OnPropertyChanged(nameof(CanvasEndPoint));
    }

    partial void OnImageWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
        OnPropertyChanged(nameof(CanvasStartPoint));
        OnPropertyChanged(nameof(CanvasEndPoint));
    }

    partial void OnPreviewMetricsChanged(ElementPreviewMetrics? value) => OnPropertyChanged(nameof(CanvasStrokeThicknessPixels));

    partial void OnImageHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        OnPropertyChanged(nameof(CanvasStartPoint));
        OnPropertyChanged(nameof(CanvasEndPoint));
        OnPropertyChanged(nameof(CanvasStrokeThicknessPixels));
    }

    partial void OnStrokeThicknessChanged(double value)
    {
        OnPropertyChanged(nameof(CanvasStrokeThicknessPixels));
        OnPropertyChanged(nameof(StrokeThicknessPx));
    }

    // Style undo wiring -- same PushUndoSnapshotForStyleChange convention as
    // BoxElementViewModel's own FillColor/BorderColor/etc.
    partial void OnStrokeColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnStrokeThicknessChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnOpacityChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();
}
