using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    }

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ResolvedText));
}
