using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Imaging;

/// <summary>Backlog fix (user request, 2026-08-17): a solid-color placeholder <see cref="IImageSource"/>
/// so the TX Image Editor can be opened WITHOUT first picking a real photo (Browse/Stock) -- every
/// scanline is the same fixed color, computed once and shared. Deliberately implemented here rather
/// than via <c>ScanlineStudio.Core.Imaging.ArrayImageSource</c>: this project's own layering rule
/// keeps the UI project off Core.Imaging (only <c>ScanlineStudio.Application</c> talks to it), and a
/// one-color source needs no per-pixel array at all -- <see cref="GetScanline"/> returns the same
/// shared row buffer for every y, since nothing ever mutates it.</summary>
public sealed class BlankImageSource : IImageSource
{
    /// <summary>Light neutral gray (matches this app's own Industry design system's neutral-surface
    /// family, e.g. AtomsTokens.axaml's IndustrySurfaceColor #E9E9EA) -- reads clearly as "no real
    /// photo loaded yet" without being visually jarring against the rest of the chrome. Single source
    /// of truth -- previously a private constant on <c>TxControlsPaneViewModel</c> alone; the
    /// background/backdrop promote/demote feature needed the same color at a second call site
    /// (<c>TxImageEditorPaneViewModel</c>'s own Promote), which would have made a third independently
    /// maintained copy instead of reusing one.</summary>
    public static readonly Rgb24 DefaultColor = new(0xE9, 0xE9, 0xEA);

    private readonly Rgb24[] _row;

    public BlankImageSource(int width, int height, Rgb24 color)
    {
        Width = width;
        Height = height;
        _row = new Rgb24[width];
        Array.Fill(_row, color);
    }

    public int Width { get; }

    public int Height { get; }

    public ReadOnlySpan<Rgb24> GetScanline(int y) => _row;
}
