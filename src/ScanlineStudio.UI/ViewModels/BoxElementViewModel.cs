using AvaloniaColor = Avalonia.Media.Color;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="TemplateBoxElement"/> (Phase 1,
/// spec/15-template-designer.md) -- the immutable pipeline record gets replaced wholesale on every
/// drag-frame otherwise. Mirrors <see cref="OverlayElementViewModel"/>'s own shape/wiring pattern
/// exactly (X/Y CENTER-anchored, same parent-pushed <see cref="RemoveCommand"/>/
/// <see cref="PushUndoSnapshotForGeometryChange"/> convention) since both implement
/// <see cref="ITemplateElementViewModel"/> and share one remove/undo/rotate path in
/// <see cref="TxImageEditorPaneViewModel"/> -- see that class's own <c>CreateBoxElement</c> for the
/// construction-time wiring.
///
/// <para>Perspective, mode-switched X/Y/Width/Height and the warped preview live in
/// <see cref="PerspectiveElementViewModelBase"/>; this class adds its fill/border notifications and its
/// uniform warped-preview size rule.</para></summary>
public sealed partial class BoxElementViewModel : PerspectiveElementViewModelBase, ITemplateElementViewModel, IDisposable
{
    public BoxElementViewModel()
        : base(naturalHeight: 0.2)
    {
    }

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private Rgb24 _fillColor = new(64, 64, 64);

    /// <summary>Legacy `.mtm` import -- legacy's plain CM_BOX draws an outline with NO fill
    /// (GetStockObject(NULL_BRUSH)), which this model had no way to express before this field
    /// existed. True (today's only behavior) for every box that isn't an import of one of those.</summary>
    [ObservableProperty]
    private bool _fillEnabled = true;

    [ObservableProperty]
    private Rgb24? _borderColor;

    [ObservableProperty]
    private double _borderThickness;

    [ObservableProperty]
    private double _opacity = 1.0;

    /// <summary>Auditor usability review follow-up (2026-08-18) -- "missing item" against the
    /// original box-elements friction risk in spec/15-template-designer.md ("need border,
    /// corner-radius, and opacity"). Relative to image HEIGHT, same convention as
    /// <see cref="BorderThickness"/>. 0 (default) renders identically to a plain square-cornered box.</summary>
    [ObservableProperty]
    private double _cornerRadius;

    /// <inheritdoc cref="OverlayElementViewModel.ShowRotateFlyoutContent"/>
    [ObservableProperty]
    private bool _showRotateFlyoutContent;

    /// <summary>TX editor gap-items plan (2026-09-01, box gradient fill) -- SAME simplified 2-stop
    /// shape <see cref="OverlayElementViewModel.GradientEnabled"/> already established for text, on
    /// the SAME "text gradients already shipped, boxes only had flat fill" gap Fable's comparative
    /// review flagged. <see cref="TxImageEditorPaneViewModel.BuildTemplateElement"/> composes these
    /// four fields into a real <see cref="TextGradient"/> only when <see cref="GradientEnabled"/> is
    /// true, mirroring the text element's own composition exactly.</summary>
    [ObservableProperty]
    private bool _gradientEnabled;

    [ObservableProperty]
    private TextGradientKind _gradientKind = TextGradientKind.Horizontal;

    [ObservableProperty]
    private Rgb24 _gradientStartColor = new(255, 0, 0);

    [ObservableProperty]
    private Rgb24 _gradientEndColor = new(0, 0, 255);

    public IRelayCommand? RemoveCommand { get; init; }

    public IRelayCommand? MoveUpCommand { get; init; }

    public IRelayCommand? MoveDownCommand { get; init; }

    public IRelayCommand? BringToFrontCommand { get; init; }

    public IRelayCommand? SendToBackCommand { get; init; }

    public IRelayCommand? DuplicateCommand { get; init; }

    public IRelayCommand? AlignSelectedElementToCropCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.CopyCommand"/>
    public IRelayCommand? CopyCommand { get; init; }

    public IRelayCommand? CutCommand { get; init; }

    public IRelayCommand? PasteCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.FlattenCommand"/>
    public IRelayCommand? FlattenCommand { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 -- box-only (matches
    /// <see cref="OverlayElementViewModel.CopyStyleCommand"/>'s own text-only precedent: box style
    /// is fill/border/corner-radius, not the text one).</summary>
    public IRelayCommand? CopyStyleCommand { get; init; }

    public IRelayCommand? PasteStyleCommand { get; init; }

    /// <inheritdoc cref="ImageElementViewModel.TogglePerspectiveCommand"/>
    public IRelayCommand? TogglePerspectiveCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.RotateClockwise90Command"/>
    public IRelayCommand? RotateClockwise90Command { get; init; }

    public IRelayCommand? RotateCounterclockwise90Command { get; init; }

    public IRelayCommand? Rotate180Command { get; init; }

    /// <summary>TX workflow modernization plan, Phase 1 (Fill &amp; Border flyout) -- box's own
    /// counterpart to <see cref="OverlayElementViewModel.PushUndoSnapshotForStyleChange"/>. Real
    /// pre-existing gap closed here, not new scope: before this, NONE of FillColor/BorderColor/
    /// BorderThickness/Opacity/CornerRadius had any undo hook at all, on ANY entry point --
    /// including the already-shipped BOX STYLE sidebar block, which has silently produced zero undo
    /// steps since it shipped. Discovered only because the flyout adds a second entry point onto
    /// these same properties; fixed at the source instead of shipping a second silently-broken-undo
    /// instance of the identical bug class <see cref="OverlayElementViewModel.FontSizeRelative"/>/
    /// <see cref="OverlayElementViewModel.Color"/> already hit once (see that field's own doc
    /// comment).</summary>
    public Action? PushUndoSnapshotForStyleChange { get; init; }

    [ObservableProperty]
    private ElementPreviewMetrics? _previewMetrics;

    private double StyleImageHeight => PreviewMetrics?.ImageHeight ?? ImageHeight;

    /// <summary>BorderThickness is normalized to image height (same convention as
    /// <see cref="OverlayElementViewModel.FontSizeRelative"/>, per spec/15-template-designer.md) --
    /// this converts it to the canvas's own pixel space, mirroring <see cref="TemplateBoxElement"/>'s
    /// pipeline-side rendering so the editor canvas is actually WYSIWYG for a bordered box.</summary>
    public double CanvasBorderThicknessPixels => BorderThickness * StyleImageHeight;

    /// <summary>Same image-height-relative-to-canvas-pixel conversion as
    /// <see cref="CanvasBorderThicknessPixels"/>, for <see cref="CornerRadius"/> -- Avalonia's
    /// <c>Border.CornerRadius</c> has the SAME "no double-accepting implicit/explicit operator, no
    /// runtime <c>TypeConverter</c> reachable from a value binding" gap <see cref="CanvasBorderThicknessPixels"/>'s
    /// own doc comment already documents for <c>BorderThickness</c>/<c>Thickness</c> (checked via
    /// reflection before use, not assumed identical just because both are Avalonia struct types --
    /// confirmed no <c>CornerRadiusTypeConverter</c> exists anywhere in the installed Avalonia
    /// assemblies, same absence that motivated <see cref="Converters.DoubleToThicknessConverter"/>
    /// originally) -- the canvas binding uses a new, analogous
    /// <see cref="Converters.DoubleToCornerRadiusConverter"/>, not a bare binding.</summary>
    public double CanvasCornerRadiusPixels => CornerRadius * StyleImageHeight;

    /// <summary>TX workflow modernization plan, Phase 1 (Fill &amp; Border flyout) -- same
    /// element-level px-conversion pattern as <see cref="OverlayElementViewModel.TargetModeHeightPx"/>/
    /// <see cref="OverlayElementViewModel.FontSizePx"/>, see that pair's own doc comments for why
    /// this needs to live on the element itself rather than the parent VM's existing
    /// <c>SelectedBoxElementBorderThicknessPx</c>/<c>SelectedBoxElementCornerRadiusPx</c>.</summary>
    public double TargetModeHeightPx { get; init; }

    /// <inheritdoc cref="OverlayElementViewModel.FontSizePx"/>
    public double BorderThicknessPx
    {
        get => BorderThickness * TargetModeHeightPx;
        set
        {
            if (TargetModeHeightPx <= 0)
            {
                return;
            }

            BorderThickness = value / TargetModeHeightPx;
        }
    }

    /// <inheritdoc cref="OverlayElementViewModel.FontSizePx"/>
    public double CornerRadiusPx
    {
        get => CornerRadius * TargetModeHeightPx;
        set
        {
            if (TargetModeHeightPx <= 0)
            {
                return;
            }

            CornerRadius = value / TargetModeHeightPx;
        }
    }

    /// <summary>Backlog item (auditor usability review, 2026-08-17) -- same nullable-color/checkbox
    /// bridge as <see cref="OverlayElementViewModel.HasStroke"/>, so the new BOX STYLE inspector
    /// block can bind a plain CheckBox to a <c>Rgb24?</c> the same way TEXT STYLE's own Outline row
    /// already does, including that row's own "ColorPicker un-nulls a disabled nullable color" fix
    /// (see <see cref="BorderColorForPicker"/> below).</summary>
    public bool HasBorder
    {
        get => BorderColor is not null;
        set => BorderColor = value ? (BorderColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <inheritdoc cref="OverlayElementViewModel.StrokeColorForPicker"/>
    public Rgb24 BorderColorForPicker
    {
        get => BorderColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasBorder)
            {
                BorderColor = value;
            }
        }
    }

    /// <summary>Canvas-preview counterpart to <see cref="OverlayElementViewModel.ForegroundBrush"/> --
    /// same shape, shared <see cref="Imaging.GradientBrushFactory"/> implementation (code-review
    /// finding: an earlier version of this duplicated the brush-building logic locally to avoid any
    /// risk of touching the already-shipped text-gradient render path; the shared factory is a pure
    /// function of its own arguments with no VM state, so it can't regress that path by
    /// construction). The real pipeline side (<c>TransmitImagePreparer.BuildGradientBrush</c>) is
    /// ALREADY shared between text and box -- this brings the canvas-preview side in line too.</summary>
    public IBrush FillBrush => !FillEnabled
        ? Brushes.Transparent
        : GradientEnabled
            ? GradientBrushFactory.Build(GradientKind, GradientStartColor, GradientEndColor, CanvasWidthPixels, CanvasHeightPixels, PreviewMetrics)
            : new SolidColorBrush(ToAvaloniaColor(FillColor));

    /// <summary>TX editor gap-items plan, item 3 (perspective transform) -- while perspective is on,
    /// the Border's own local Fill/Border/Opacity bindings must yield to the warped-preview `Image`
    /// child, but Avalonia's LocalValue-beats-Style precedence means a `Style` selector CANNOT
    /// override those already-local bindings (verified, not assumed, before this design was
    /// finalized). So the AXAML binds to these 3 "Effective*" properties INSTEAD of the raw ones
    /// directly -- still a single local binding each, no precedence conflict, and pixel-identical to
    /// the raw properties when perspective is off (nothing changes for the ordinary box).
    /// <see cref="CornerRadius"/> deliberately has no `Effective*` counterpart: a `Transparent`
    /// background with 0 border thickness draws no visible rounded chrome regardless (Avalonia's
    /// `Border` doesn't clip its child by default), so neutralizing it would be a no-op.</summary>
    public IBrush EffectiveBackground => PerspectiveEnabled ? Brushes.Transparent : FillBrush;

    public double EffectiveBorderThicknessPixels => PerspectiveEnabled ? 0 : CanvasBorderThicknessPixels;

    public double EffectiveOpacity => PerspectiveEnabled ? 1 : Opacity;

    /// <inheritdoc/>
    public TxImageEditorPaneViewModel.RawElementSnapshot ToSnapshot() => new TxImageEditorPaneViewModel.RawBoxElementSnapshot(
        X: X, Y: Y, Width: Width, Height: Height, Z: Z, Locked: Locked,
        FillColor: FillColor, BorderColor: BorderColor, BorderThickness: BorderThickness,
        Opacity: Opacity, CornerRadius: CornerRadius,
        GradientEnabled: GradientEnabled, GradientKind: GradientKind,
        GradientStartColor: GradientStartColor, GradientEndColor: GradientEndColor,
        PerspectiveEnabled: PerspectiveEnabled,
        Corner0X: Corner0X, Corner0Y: Corner0Y, Corner1X: Corner1X, Corner1Y: Corner1Y,
        Corner2X: Corner2X, Corner2Y: Corner2Y, Corner3X: Corner3X, Corner3Y: Corner3Y,
        FillEnabled: FillEnabled, RotationDegrees: RotationDegrees);

    /// <summary>Paste Style: copies only the style fields of <paramref name="style"/> onto this
    /// element. Every new style field must be added here or Paste Style silently drops it. The pane
    /// owns the undo step and preview suspension around this call.</summary>
    public void ApplyStyleFrom(TxImageEditorPaneViewModel.RawBoxElementSnapshot style)
    {
        FillColor = style.FillColor;
        // FillEnabled carries legacy .mtm import's no-fill boxes (see the FillEnabled field doc).
        FillEnabled = style.FillEnabled;
        BorderColor = style.BorderColor;
        BorderThickness = style.BorderThickness;
        Opacity = style.Opacity;
        CornerRadius = style.CornerRadius;
        // Gradient is style too: omitting it turned a pasted gradient box solid, and left a gradient on under a pasted solid.
        GradientEnabled = style.GradientEnabled;
        GradientKind = style.GradientKind;
        GradientStartColor = style.GradientStartColor ?? new Rgb24(255, 0, 0);
        GradientEndColor = style.GradientEndColor ?? new Rgb24(0, 0, 255);
        // Rotation carries with style, matching text.
        RotationDegrees = style.RotationDegrees;
    }

    private static AvaloniaColor ToAvaloniaColor(Rgb24 color) => AvaloniaColor.FromRgb(color.R, color.G, color.B);

    protected override void OnGeometryChangedCore()
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
    }

    protected override void OnPerspectiveEnabledChangedCore()
    {
        OnPropertyChanged(nameof(EffectiveBackground));
        OnPropertyChanged(nameof(EffectiveBorderThicknessPixels));
        OnPropertyChanged(nameof(EffectiveOpacity));
    }

    partial void OnPreviewMetricsChanged(ElementPreviewMetrics? value)
    {
        OnPropertyChanged(nameof(CanvasBorderThicknessPixels));
        OnPropertyChanged(nameof(EffectiveBorderThicknessPixels));
        OnPropertyChanged(nameof(CanvasCornerRadiusPixels));
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    protected override void OnImageWidthChangedCore()
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
    }

    protected override void OnImageHeightChangedCore()
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        OnPropertyChanged(nameof(CanvasBorderThicknessPixels));
        OnPropertyChanged(nameof(EffectiveBorderThicknessPixels));
        OnPropertyChanged(nameof(CanvasCornerRadiusPixels));
    }

    partial void OnBorderThicknessChanged(double value)
    {
        OnPropertyChanged(nameof(CanvasBorderThicknessPixels));
        OnPropertyChanged(nameof(BorderThicknessPx));
        OnPropertyChanged(nameof(EffectiveBorderThicknessPixels));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnCornerRadiusChanged(double value)
    {
        OnPropertyChanged(nameof(CanvasCornerRadiusPixels));
        OnPropertyChanged(nameof(CornerRadiusPx));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnBorderColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasBorder));
        OnPropertyChanged(nameof(BorderColorForPicker));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    // Fill & Border flyout undo wiring -- see PushUndoSnapshotForStyleChange's own doc comment for
    // why these five didn't have one before this change.
    partial void OnFillColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnFillColorChanged(Rgb24 value)
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnFillEnabledChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnFillEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnBorderColorChanging(Rgb24? value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnBorderThicknessChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnOpacityChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnOpacityChanged(double value)
    {
        OnPropertyChanged(nameof(EffectiveOpacity));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnCornerRadiusChanging(double value) => PushUndoSnapshotForStyleChange?.Invoke();

    // Same PushUndoSnapshotForStyleChange convention as FillColor/BorderColor/etc. above.
    protected override void OnRotationDegreesChangingCore() => PushUndoSnapshotForStyleChange?.Invoke();

    // Gradient fill undo wiring -- same PushUndoSnapshotForStyleChange convention as
    // FillColor/BorderColor/etc. above, plus a FillBrush re-notify so the canvas preview updates.
    partial void OnGradientEnabledChanging(bool value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnGradientKindChanging(TextGradientKind value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientKindChanged(TextGradientKind value)
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnGradientStartColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientStartColorChanged(Rgb24 value)
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    partial void OnGradientEndColorChanging(Rgb24 value) => PushUndoSnapshotForStyleChange?.Invoke();

    partial void OnGradientEndColorChanged(Rgb24 value)
    {
        OnPropertyChanged(nameof(FillBrush));
        OnPropertyChanged(nameof(EffectiveBackground));
        if (PerspectiveEnabled)
        {
            ScheduleWarpedPreviewRebuild();
        }
    }

    /// <summary>Keep one uniform bitmap scale: independently clamping one dimension stretches
    /// borders/radii along that axis when the preview is displayed at its original aspect (border and
    /// radius scale from the vertical axis, see <c>TxImageEditorPaneViewModel.RenderWarpedElementPreview</c>'s
    /// style height).</summary>
    protected override (int Width, int Height) ComputeWarpedPreviewSize()
    {
        var scale = Math.Min(1, PreviewSizeCeilingPx / Math.Max(CanvasWidthPixels, CanvasHeightPixels));
        return (Math.Max(1, (int)Math.Round(CanvasWidthPixels * scale)), Math.Max(1, (int)Math.Round(CanvasHeightPixels * scale)));
    }

    /// <summary>TX editor gap-items plan, item 3 -- <see cref="BoxElementViewModel"/> didn't need
    /// <see cref="IDisposable"/> before this feature (no bitmap resource); now it owns
    /// <see cref="PerspectiveElementViewModelBase.WarpedCanvasBitmap"/>. Same deferred-dispose/guarded-against-a-second-call shape as
    /// <see cref="ImageElementViewModel.Dispose"/> -- every existing element-disposal call site
    /// already checks the generic <c>element is IDisposable</c>, so no call-site edit is needed.</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        DisposeWarpedPreview();
    }
}
