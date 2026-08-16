using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.ViewModels;

/// <summary>Mutable, drag/edit-friendly wrapper around <see cref="ImageOverlayElement"/> — the
/// immutable record gets replaced wholesale on every drag-frame otherwise; this stays one instance
/// per element so two-way X/Y/Text bindings from the editor canvas work directly. See
/// spec/07-image-pipeline.md's "TX image editor" section: X/Y are CENTER-anchored,
/// FontSizeRelative is relative to the image's height.</summary>
public sealed partial class OverlayElementViewModel : ObservableObject
{
    [ObservableProperty]
    private string _text = "Text";

    [ObservableProperty]
    private double _x = 0.5;

    [ObservableProperty]
    private double _y = 0.5;

    [ObservableProperty]
    private double _fontSizeRelative = 0.1;

    [ObservableProperty]
    private Rgb24 _color = new(255, 255, 255);

    /// <summary>Set by the owning <see cref="TxImageEditorPaneViewModel"/> at creation time -- lets
    /// this element compute its own on-screen position without the View needing a
    /// multi-binding/converter to combine X/Y with the canvas size itself. Settable (not the
    /// original <c>init</c>), not because it changes often -- the canvas's pixel dimensions were
    /// fixed for the editor's whole lifetime until Rotate existed -- but because
    /// <see cref="TxImageEditorPaneViewModel.RotateCommand"/> now updates every existing element's
    /// dimensions in place after a 90° rotation swaps width/height.</summary>
    [ObservableProperty]
    private double _imageWidth;

    [ObservableProperty]
    private double _imageHeight;

    /// <summary>Font size in CANVAS DISPLAY pixels (the same pixel space as <see cref="ImageWidth"/>/
    /// <see cref="ImageHeight"/>), pushed by <see cref="TxImageEditorPaneViewModel"/> whenever the
    /// crop rect, PreserveAspect, or this element's own <see cref="FontSizeRelative"/> changes --
    /// same parent-pushed pattern as <see cref="ImageWidth"/>/<see cref="ImageHeight"/>, for the same
    /// reason (no live ambient binding back to the parent VM from inside this DataTemplate; see
    /// <see cref="RemoveCommand"/>'s own doc comment for the concrete crash that pattern hit).
    /// Purely canvas-chrome display state -- never feeds
    /// <see cref="TxImageEditorPaneViewModel.BuildImageOverlayElement"/> or the real pipeline, so
    /// <c>TxImageEditorPaneViewModel.OnOverlayElementPropertyChanged</c> excludes it from triggering
    /// a preview recompute.</summary>
    [ObservableProperty]
    private double _canvasFontSize;

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel.AddOverlayElement"/> at creation
    /// time (its own <c>RemoveOverlayElementCommand</c>), not bound in XAML via
    /// `$parent[ItemsControl].((vm:TxImageEditorPaneViewModel)DataContext)...` -- that pattern
    /// throws `ArgumentException: Unable to resolve type` the first time this element's
    /// DataTemplate is actually realized (this project uses classic, non-compiled bindings; an
    /// inline type cast in a binding path forces a runtime type-resolution step that doesn't
    /// reliably find sibling view-model types). This list starts empty and is only ever populated
    /// by <c>AddOverlayElement</c>, so the old binding had never actually been exercised by any
    /// hands-on session so far -- same latent-crash shape as TxControlsPaneViewModel's own Drive
    /// slider, found and fixed the same way.</summary>
    public IRelayCommand? RemoveCommand { get; init; }

    /// <summary>Set once by <see cref="TxImageEditorPaneViewModel"/> at creation time, same pattern
    /// as <see cref="RemoveCommand"/> -- resolves this element's raw <see cref="Text"/> (which may
    /// contain macro tokens like <c>%m</c>/<c>{name}</c>) against the current operator settings.
    /// Null only in tests/design-time contexts that don't care about macro resolution.</summary>
    public Func<string, string>? ResolveMacros { get; init; }

    public double LeftPixels => X * ImageWidth;

    public double TopPixels => Y * ImageHeight;

    /// <summary>What actually gets drawn -- the canvas preview binds here, not <see cref="Text"/>,
    /// so the user sees "DE W1AW" rather than the literal "DE %m" template while editing.</summary>
    public string ResolvedText => ResolveMacros?.Invoke(Text) ?? Text;

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(LeftPixels));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(TopPixels));

    partial void OnImageWidthChanged(double value) => OnPropertyChanged(nameof(LeftPixels));

    partial void OnImageHeightChanged(double value) => OnPropertyChanged(nameof(TopPixels));

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ResolvedText));
}
