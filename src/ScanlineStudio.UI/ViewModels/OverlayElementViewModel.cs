using CommunityToolkit.Mvvm.ComponentModel;
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

    /// <summary>Set once by the owning <see cref="TxImageEditorPaneViewModel"/> at creation time (the
    /// canvas's own pixel dimensions never change after the editor opens) -- lets this element compute
    /// its own on-screen position without the View needing a multi-binding/converter to combine X/Y
    /// with the canvas size itself.</summary>
    public double ImageWidth { get; init; }

    public double ImageHeight { get; init; }

    public double LeftPixels => X * ImageWidth;

    public double TopPixels => Y * ImageHeight;

    public ImageOverlayElement ToImageOverlayElement() => new(Text, X, Y, FontSizeRelative, Color);

    partial void OnXChanged(double value) => OnPropertyChanged(nameof(LeftPixels));

    partial void OnYChanged(double value) => OnPropertyChanged(nameof(TopPixels));
}
