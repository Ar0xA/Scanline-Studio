using AvaloniaColor = Avalonia.Media.Color;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Media;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="Abstractions.Imaging.TemplateTextElement"/>
/// (Phase 1, spec/15-template-designer.md — pre-Phase-1 this wrapped the older, size-less
/// <c>ImageOverlayElement</c>) — the immutable record gets replaced wholesale on every drag-frame
/// otherwise; this stays one instance per element so two-way X/Y/Width/Height/Text bindings from the
/// editor canvas work directly. X/Y are CENTER-anchored (kept from the pre-Phase-1 convention
/// deliberately, not switched to top-left — see <see cref="ITemplateElementViewModel"/>'s own doc
/// comment). <see cref="FontSizeRelative"/> is relative to the image's height, same as before Phase
/// 1, but its MEANING changed: it's now the STARTING/MAXIMUM size for
/// <see cref="ITransmitImagePreparer.ApplyTemplate"/>'s shrink-to-fit search against this element's
/// own <see cref="Width"/> x <see cref="Height"/> box, not a fixed rendered size.</summary>
public sealed partial class OverlayElementViewModel : ObservableObject, ITemplateElementViewModel
{
    [ObservableProperty]
    private string _text = "Text";

    [ObservableProperty]
    private double _x = 0.5;

    [ObservableProperty]
    private double _y = 0.5;

    /// <summary>Phase 1 default: 0.3 x 0.18, deliberately with headroom over the default
    /// <see cref="FontSizeRelative"/> (0.1) rather than a tight ~0.1-tall box — plan-review finding:
    /// <c>TextMeasurer</c>'s line height (ascender+descender+gap) exceeds a bare em size, so a box
    /// sized flush to the font fraction would shrink-to-fit immediately on creation, making "Add
    /// text" a visible regression from day one. 0.18 is comfortably above the ~1.5-2x headroom the
    /// review asked for.</summary>
    [ObservableProperty]
    private double _width = 0.3;

    [ObservableProperty]
    private double _height = 0.18;

    [ObservableProperty]
    private int _z;

    [ObservableProperty]
    private bool _locked;

    [ObservableProperty]
    private double _fontSizeRelative = 0.1;

    [ObservableProperty]
    private Rgb24 _color = new(255, 255, 255);

    /// <summary>Empty by default (matches <see cref="ITransmitImagePreparer.MeasureFittedFontSize"/>'s
    /// own "empty family falls back to the pipeline's default" contract) -- the REAL default (one of
    /// <see cref="ITransmitImagePreparer.AvailableFontFamilies"/>) is set explicitly by
    /// <see cref="TxImageEditorPaneViewModel.CreateOverlayElement"/>'s object initializer at
    /// construction time, queried fresh from the preparer rather than hardcoded here a second time
    /// (Phase 4, spec/15-template-designer.md).</summary>
    [ObservableProperty]
    private string _fontFamily = string.Empty;

    /// <summary>Null means no outline (matches <see cref="TemplateBoxElement.BorderColor"/>'s own
    /// null-means-none convention) -- Phase 4, the user-requested legibility mechanism for text
    /// against varying backgrounds (this session's own real-window testing hit white-on-white text
    /// twice against near-white stock photos).</summary>
    [ObservableProperty]
    private Rgb24? _strokeColor;

    /// <summary>Relative to the image's HEIGHT, same convention as <see cref="FontSizeRelative"/>/
    /// <see cref="BoxElementViewModel.BorderThickness"/>. Meaningless while <see cref="StrokeColor"/>
    /// is null (mirrors <see cref="BoxElementViewModel.BorderThickness"/>'s own "thickness without a
    /// color is a no-op" contract).</summary>
    [ObservableProperty]
    private double _strokeThickness = 0.02;

    /// <summary>Phase 8 (spec/15-template-designer.md, YONIQ-style text-effects follow-up). Null
    /// means no shadow (same null-means-none convention as <see cref="StrokeColor"/>) -- a plain
    /// offset-duplicate-glyph draw (legacy YONIQ's own real mechanism), not a soft blur.</summary>
    [ObservableProperty]
    private Rgb24? _shadowColor;

    /// <summary>Relative to the image's HEIGHT, same convention as <see cref="FontSizeRelative"/>/
    /// <see cref="StrokeThickness"/> (both axes, so a template saved at one SSTV mode renders
    /// correctly at another). Meaningless while <see cref="ShadowColor"/> is null.</summary>
    [ObservableProperty]
    private double _shadowOffsetX = 0.02;

    [ObservableProperty]
    private double _shadowOffsetY = 0.02;

    /// <summary>Phase 8: in-plane (2D) rotation only, clockwise-positive degrees -- true 3D/
    /// perspective is a separate, deferred future phase (Tier 3).</summary>
    [ObservableProperty]
    private double _rotationDegrees;

    /// <summary>Phase 8: simplified 2-stop gradient (start/end color + axis) rather than exposing
    /// <see cref="Abstractions.Imaging.TextGradient"/>'s own full arbitrary-stop-list shape directly
    /// on this VM -- a real, deliberate scope cut for the style panel's own UI (a 2-color picker
    /// pair + an axis ComboBox is the whole surface; an N-stop editor is real, unbudgeted UI work
    /// for a feature this project's own plan explicitly scoped as "feature parity, not literal
    /// legacy replication"). <see cref="BuildTemplateElement"/> in the owning
    /// <see cref="TxImageEditorPaneViewModel"/> composes these three fields into a real
    /// <see cref="Abstractions.Imaging.TextGradient"/> only when <see cref="GradientEnabled"/> is
    /// true.</summary>
    [ObservableProperty]
    private bool _gradientEnabled;

    [ObservableProperty]
    private TextGradientKind _gradientKind = TextGradientKind.Horizontal;

    [ObservableProperty]
    private Rgb24 _gradientStartColor = new(255, 0, 0);

    [ObservableProperty]
    private Rgb24 _gradientEndColor = new(0, 0, 255);

    /// <summary>Set by the owning <see cref="TxImageEditorPaneViewModel"/> at creation time -- lets
    /// this element compute its own on-screen position without the View needing a
    /// multi-binding/converter to combine X/Y with the canvas size itself. Settable (not
    /// <c>init</c>), not because it changes often, but because
    /// <see cref="TxImageEditorPaneViewModel.RotateCommand"/> updates every existing element's
    /// dimensions in place after a 90° rotation swaps width/height.</summary>
    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    /// <summary>Font size in CANVAS DISPLAY pixels (the same pixel space as <see cref="ImageWidth"/>/
    /// <see cref="ImageHeight"/>), pushed by <see cref="TxImageEditorPaneViewModel"/> whenever the
    /// crop rect, PreserveAspect, or this element's own <see cref="FontSizeRelative"/>/
    /// <see cref="Width"/>/<see cref="Height"/>/<see cref="Text"/> changes (Phase 1 widened the
    /// dependency set from FontSizeRelative-only, since the rendered size is now a real shrink-to-fit
    /// result of the box and content, not a closed-form function of FontSizeRelative alone) -- same
    /// parent-pushed pattern as <see cref="ImageWidth"/>/<see cref="ImageHeight"/>, for the same
    /// reason (no live ambient binding back to the parent VM from inside this DataTemplate; see
    /// <see cref="RemoveCommand"/>'s own doc comment for the concrete crash that pattern hit).
    /// Purely canvas-chrome display state -- never feeds
    /// <see cref="TxImageEditorPaneViewModel.BuildTemplateElement"/> or the real pipeline, so
    /// <c>TxImageEditorPaneViewModel.OnOverlayElementPropertyChanged</c> excludes it from triggering
    /// a preview recompute.</summary>
    [ObservableProperty]
    private double _canvasFontSize;

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel.CreateOverlayElement"/> at creation
    /// time (its own <c>RemoveOverlayElementCommand</c>), not bound in XAML via
    /// `$parent[ItemsControl].((vm:TxImageEditorPaneViewModel)DataContext)...` -- that pattern
    /// throws `ArgumentException: Unable to resolve type` the first time this element's
    /// DataTemplate is actually realized (this project uses classic, non-compiled bindings; an
    /// inline type cast in a binding path forces a runtime type-resolution step that doesn't
    /// reliably find sibling view-model types). This list starts empty and is only ever populated
    /// by <c>AddOverlayElement</c>/<c>AddBoxElement</c>, so the old binding had never actually been
    /// exercised by any hands-on session so far -- same latent-crash shape as TxControlsPaneViewModel's
    /// own Drive slider, found and fixed the same way.</summary>
    public IRelayCommand? RemoveCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.MoveUpCommand"/>
    public IRelayCommand? MoveUpCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.MoveDownCommand"/>
    public IRelayCommand? MoveDownCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.BringToFrontCommand"/>
    public IRelayCommand? BringToFrontCommand { get; init; }

    /// <inheritdoc cref="ITemplateElementViewModel.SendToBackCommand"/>
    public IRelayCommand? SendToBackCommand { get; init; }

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel"/> at creation time, same pattern
    /// as <see cref="RemoveCommand"/> -- resolves this element's raw <see cref="Text"/> (which may
    /// contain macro tokens like <c>%m</c>/<c>{name}</c>) against the current operator settings.
    /// Null only in tests/design-time contexts that don't care about macro resolution.</summary>
    public Func<string, string>? ResolveMacros { get; init; }

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel"/> at creation time, same
    /// parent-pushed pattern as <see cref="RemoveCommand"/>/<see cref="ResolveMacros"/> -- pushes an
    /// undo/redo snapshot BEFORE <see cref="X"/>/<see cref="Y"/>/<see cref="Width"/>/<see cref="Height"/>
    /// actually change, coalesced under one SHARED key covering all four (Phase 1 plan-review
    /// finding: a diagonal drag-resize gesture must collapse to ONE undo step, same reasoning that
    /// already applied pre-Phase-1 to X/Y alone). Covers BOTH the canvas pointer-drag path and the
    /// sidebar X/Y TextBox edits. Null only in tests/design-time contexts that don't care about undo.</summary>
    public Action? PushUndoSnapshotForGeometryChange { get; init; }

    public double LeftPixels => (X - (Width / 2)) * ImageWidth;

    public double TopPixels => (Y - (Height / 2)) * ImageHeight;

    public double CanvasWidthPixels => Width * ImageWidth;

    public double CanvasHeightPixels => Height * ImageHeight;

    /// <summary>Phase 4 -- the TEXT STYLE panel's "Outline" checkbox binds here rather than directly
    /// to <see cref="StrokeColor"/> (a <c>Rgb24?</c>, not directly checkbox-bindable). Turning it ON
    /// seeds a real default color (black -- the highest-contrast choice against this app's own
    /// default WHITE text fill, which is exactly the "white text on a light photo" legibility
    /// problem this feature exists to fix); turning it OFF clears <see cref="StrokeColor"/> back to
    /// null (matches <see cref="Abstractions.Imaging.TemplateBoxElement.BorderColor"/>'s own
    /// null-means-none contract) rather than leaving a stale color the pipeline would just ignore
    /// anyway, so re-enabling later doesn't silently resurrect an old value with no visible
    /// indication it was still there.</summary>
    public bool HasStroke
    {
        get => StrokeColor is not null;
        set => StrokeColor = value ? (StrokeColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <summary>Real-window finding (Phase 8 verification, but the bug is pre-existing since Phase 4
    /// -- caught here because <see cref="ShadowColor"/> inherited the identical pattern): binding a
    /// <c>ColorPicker.Color</c> two-way, through <c>Rgb24ToColorConverter</c>, DIRECTLY to a nullable
    /// <see cref="StrokeColor"/>/<see cref="ShadowColor"/> silently un-nulls it. That converter's own
    /// <c>Convert</c> maps <c>null -&gt; Colors.Black</c> for display (so the DISABLED picker doesn't
    /// spam a binding error while unchecked) -- but the picker's own two-way binding writes that
    /// synthetic fallback color straight back through <c>ConvertBack</c>, turning <c>null</c> into a
    /// real <c>Rgb24(0,0,0)</c> with no user action at all. Confirmed live: a freshly-created text
    /// element (never touched by the operator) showed BOTH "Outline" and "Shadow" checked with a
    /// black swatch, meaning every new element silently got a black outline AND a black drop-shadow
    /// baked into the transmitted image by default. Fixed by giving the picker its own NON-nullable
    /// view (<see cref="StrokeColorForPicker"/>/<see cref="ShadowColorForPicker"/> below) instead of
    /// binding the nullable source directly -- <see cref="GradientStartColor"/>/<see cref="GradientEndColor"/>
    /// never had this bug for exactly this reason (already non-nullable <c>Rgb24</c>, no null branch
    /// in the converter ever gets hit for them).</summary>
    public bool HasShadow
    {
        get => ShadowColor is not null;
        set => ShadowColor = value ? (ShadowColor ?? new Rgb24(0, 0, 0)) : null;
    }

    /// <summary>Non-nullable ColorPicker-facing view of <see cref="StrokeColor"/> -- see
    /// <see cref="HasShadow"/>'s own doc comment for the exact bug this sidesteps. Setter only
    /// commits while <see cref="HasStroke"/> is already true (the picker is disabled/decorative
    /// otherwise -- an incidental write while disabled is dropped, not silently un-nulling
    /// <see cref="StrokeColor"/>).</summary>
    public Rgb24 StrokeColorForPicker
    {
        get => StrokeColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasStroke)
            {
                StrokeColor = value;
            }
        }
    }

    /// <inheritdoc cref="StrokeColorForPicker"/>
    public Rgb24 ShadowColorForPicker
    {
        get => ShadowColor ?? new Rgb24(0, 0, 0);
        set
        {
            if (HasShadow)
            {
                ShadowColor = value;
            }
        }
    }

    /// <summary>Canvas-preview amendment (user explicitly asked for real WYSIWYG here, overriding
    /// this phase's own original "canvas can't show rotation/shadow/gradient, only the mini-preview
    /// can" scope decision -- Phase 4 made the identical call for stroke and it's STILL true that a
    /// plain Avalonia <c>TextBlock</c> has no native outline capability, so stroke stays
    /// mini-preview-only; rotation and gradient, unlike stroke, DO have real Avalonia primitives to
    /// use here). Bound to the outer element <c>Border</c>'s own <c>RenderTransform</c> (not the
    /// <c>TextBlock</c> alone) so it rotates the text AND its shadow copy together, matching the real
    /// pipeline's own behavior of rotating the whole offscreen sub-bitmap as one unit. Null at 0°
    /// (no transform needed for the common case) -- Avalonia's own <c>RenderTransform</c> accepts a
    /// null value as "identity," so this is not a special case, just an optimization.</summary>
    public Transform? RotationTransform => RotationDegrees != 0 ? new RotateTransform(RotationDegrees) : null;

    /// <summary>Canvas-preview amendment, shadow half -- Avalonia's <c>TextBlock</c> has no native
    /// drop-shadow primitive (confirmed, same as the stroke case), so this fakes it the same way the
    /// real pipeline does: a second, offset TextBlock copy underneath the real one (see the
    /// DataTemplate's own XAML for the actual two-TextBlock structure). Offset is in the SAME
    /// canvas-display pixel space <see cref="CanvasFontSize"/> already uses (both scale off
    /// <see cref="ImageHeight"/>, matching <see cref="Abstractions.Imaging.TemplateTextElement.ShadowOffsetX"/>/
    /// <see cref="ShadowOffsetY"/>'s own image-height-relative convention, so a template saved at one
    /// SSTV mode previews correctly at another here too). Null while <see cref="HasShadow"/> is false
    /// -- the shadow TextBlock is ALSO <c>IsVisible</c>-gated on <see cref="HasShadow"/>, so this only
    /// matters while it's actually shown.</summary>
    public Transform? ShadowRenderTransform => HasShadow
        ? new TranslateTransform(ShadowOffsetX * ImageHeight, ShadowOffsetY * ImageHeight)
        : null;

    /// <summary>Canvas-preview amendment, gradient half -- the main TextBlock's own
    /// <c>Foreground</c>: a plain solid brush from <see cref="Color"/> (identical to the pre-Phase-8
    /// binding), or a real Avalonia <see cref="LinearGradientBrush"/>/<see cref="RadialGradientBrush"/>
    /// when <see cref="GradientEnabled"/> is set. Coordinates use Avalonia's own
    /// <see cref="RelativeUnit.Relative"/> (0,0)-(1,1) across the TextBlock's own layout box --
    /// genuinely simpler than the real pipeline's own gradient-brush construction
    /// (<c>TransmitImagePreparer.BuildGradientBrush</c>), which has to compute real destination-image
    /// pixel coordinates by hand because ImageSharp's own gradient brushes have no relative-coordinate
    /// mode at all.</summary>
    public IBrush ForegroundBrush => GradientEnabled ? BuildForegroundGradientBrush() : new SolidColorBrush(ToAvaloniaColor(Color));

    private IBrush BuildForegroundGradientBrush()
    {
        var stops = new GradientStops
        {
            new GradientStop(ToAvaloniaColor(GradientStartColor), 0),
            new GradientStop(ToAvaloniaColor(GradientEndColor), 1),
        };

        return GradientKind switch
        {
            TextGradientKind.Horizontal => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = stops,
            },
            TextGradientKind.Vertical => new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                GradientStops = stops,
            },
            // RadiusX/RadiusY (not the obsolete Radius) -- both already relative regardless (0.5 =
            // 50%), matching this brush's own Center/GradientOrigin RelativeUnit.Relative usage.
            TextGradientKind.Radial => new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                GradientOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops = stops,
            },
            _ => throw new NotSupportedException($"Unrecognized {nameof(TextGradientKind)}: {GradientKind}."),
        };
    }

    private static AvaloniaColor ToAvaloniaColor(Rgb24 color) => AvaloniaColor.FromRgb(color.R, color.G, color.B);

    /// <summary>What actually gets drawn -- the canvas preview binds here, not <see cref="Text"/>,
    /// so the user sees "DE W1AW" rather than the literal "DE %m" template while editing.</summary>
    public string ResolvedText => ResolveMacros?.Invoke(Text) ?? Text;

    /// <summary>Phase 3 (spec/15-template-designer.md, named template variables + fill bar) --
    /// forces a <see cref="ResolvedText"/> property-changed raise from OUTSIDE this class. Needed
    /// because <see cref="ResolvedText"/> otherwise only ever
    /// re-raises as a SIDE EFFECT of <see cref="Text"/> changing (<see cref="OnTextChanged"/>
    /// below) -- that held by coincidence pre-Phase-3, since nothing else could change what
    /// <see cref="ResolveMacros"/> resolves to. A fill-bar edit changes the RESOLUTION CONTEXT
    /// (<see cref="TxImageEditorPaneViewModel"/>'s own template-variable dictionary), not this
    /// element's own <see cref="Text"/>, so it needs its own explicit trigger -- see
    /// <c>TxImageEditorPaneViewModel</c>'s own template-variable-changed handler for the full
    /// picture (this call covers only the canvas <c>TextBlock</c> binding refresh; the pipeline
    /// recompute and font-size refresh are separate, explicit calls that handler also makes, since
    /// <see cref="ResolvedText"/> itself is filtered out of
    /// <c>TxImageEditorPaneViewModel.OnOverlayElementPropertyChanged</c>'s own recompute trigger).</summary>
    public void NotifyResolvedTextChanged() => OnPropertyChanged(nameof(ResolvedText));

    partial void OnXChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnYChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnWidthChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnHeightChanging(double value) => PushUndoSnapshotForGeometryChange?.Invoke();

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(LeftPixels));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(TopPixels));

    partial void OnWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
    }

    partial void OnHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
    }

    partial void OnImageWidthChanged(double value)
    {
        OnPropertyChanged(nameof(LeftPixels));
        OnPropertyChanged(nameof(CanvasWidthPixels));
    }

    partial void OnImageHeightChanged(double value)
    {
        OnPropertyChanged(nameof(TopPixels));
        OnPropertyChanged(nameof(CanvasHeightPixels));
        // Canvas-preview amendment: ShadowRenderTransform's own offset scales off ImageHeight too.
        OnPropertyChanged(nameof(ShadowRenderTransform));
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ResolvedText));

    partial void OnStrokeColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasStroke));
        OnPropertyChanged(nameof(StrokeColorForPicker));
    }

    partial void OnShadowColorChanged(Rgb24? value)
    {
        OnPropertyChanged(nameof(HasShadow));
        OnPropertyChanged(nameof(ShadowColorForPicker));
        // Canvas-preview amendment: HasShadow gates ShadowRenderTransform (null while off).
        OnPropertyChanged(nameof(ShadowRenderTransform));
    }

    // Canvas-preview amendment (see RotationTransform/ShadowRenderTransform/ForegroundBrush's own
    // doc comments) -- every dependency of those three computed properties needs its own re-raise
    // hook, same "parent-pushed cascade, not left to accidentally work" discipline this file already
    // established for LeftPixels/TopPixels/CanvasWidthPixels/CanvasHeightPixels above.
    partial void OnRotationDegreesChanged(double value) => OnPropertyChanged(nameof(RotationTransform));

    partial void OnShadowOffsetXChanged(double value) => OnPropertyChanged(nameof(ShadowRenderTransform));

    partial void OnShadowOffsetYChanged(double value) => OnPropertyChanged(nameof(ShadowRenderTransform));

    partial void OnColorChanged(Rgb24 value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientEnabledChanged(bool value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientKindChanged(TextGradientKind value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientStartColorChanged(Rgb24 value) => OnPropertyChanged(nameof(ForegroundBrush));

    partial void OnGradientEndColorChanged(Rgb24 value) => OnPropertyChanged(nameof(ForegroundBrush));
}
