using AvaloniaColor = Avalonia.Media.Color;
using Avalonia;
using Avalonia.Media;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Imaging;

/// <summary>Code-review finding (2026-09-01, box gradient fill): extracted from
/// <c>OverlayElementViewModel.BuildForegroundGradientBrush</c>/<c>BuildPatternBrush</c>, which
/// <c>BoxElementViewModel</c>'s own <c>FillBrush</c> had duplicated verbatim (a deliberate choice at
/// the time, to avoid touching the already-shipped text-gradient render path at all) -- a pure
/// static function taking only <paramref name="kind"/>/<paramref name="startColor"/>/
/// <paramref name="endColor"/>, no VM state, so extracting it can't change either caller's own
/// behavior. The 2-stop, canvas-preview-only shape here mirrors
/// <c>TransmitImagePreparer.BuildGradientBrush</c>'s own REAL pipeline brush -- see that method's
/// own doc comment for why the canvas preview needs its own separate implementation (Avalonia's
/// gradient brushes support relative coordinates directly; ImageSharp's don't).</summary>
internal static class GradientBrushFactory
{
    public static IBrush Build(TextGradientKind kind, Rgb24 startColor, Rgb24 endColor,
        double width = 1, double height = 1, ElementPreviewMetrics? metrics = null)
    {
        width = width > 0 && double.IsFinite(width) ? width : 1;
        height = height > 0 && double.IsFinite(height) ? height : 1;
        var pixelSize = metrics?.PixelSize ?? new Size(1, 1);
        var radius = Math.Max(width / pixelSize.Width, height / pixelSize.Height) / 2;
        var stops = new GradientStops
        {
            new GradientStop(ToAvaloniaColor(startColor), 0),
            new GradientStop(ToAvaloniaColor(endColor), 1),
        };

        return kind switch
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
                RadiusX = new RelativeScalar(radius * pixelSize.Width / width, RelativeUnit.Relative),
                RadiusY = new RelativeScalar(radius * pixelSize.Height / height, RelativeUnit.Relative),
                GradientStops = stops,
            },
            TextGradientKind.BitmapPattern => BuildPatternBrush(startColor, endColor, pixelSize, metrics?.OutputOrigin ?? default),
            _ => throw new NotSupportedException($"Unrecognized {nameof(TextGradientKind)}: {kind}."),
        };
    }

    /// <summary>ImageSharp.Drawing's <c>Brushes.Percent20</c> own internal 4x4 tile (reflected off a
    /// real <c>PatternBrush</c> instance against the pinned 2.1.7 package). <see langword="true"/> =
    /// fore color cell, <see langword="false"/> = back color cell.</summary>
    private static readonly bool[,] BitmapPatternGrid =
    {
        { true, false, false, false },
        { false, false, true, false },
        { true, false, false, false },
        { false, false, true, false },
    };

    private static DrawingBrush BuildPatternBrush(Rgb24 startColor, Rgb24 endColor, Size pixelSize, Point outputOrigin)
    {
        var foreBrush = new SolidColorBrush(ToAvaloniaColor(startColor));
        var backBrush = new SolidColorBrush(ToAvaloniaColor(endColor));
        var tile = new Rect(0, 0, 4, 4);
        var drawingGroup = new DrawingGroup
        {
            Children = { new GeometryDrawing { Brush = backBrush, Geometry = new RectangleGeometry(tile) } },
        };

        for (var y = 0; y < BitmapPatternGrid.GetLength(0); y++)
        {
            for (var x = 0; x < BitmapPatternGrid.GetLength(1); x++)
            {
                if (BitmapPatternGrid[y, x])
                {
                    drawingGroup.Children.Add(new GeometryDrawing { Brush = foreBrush, Geometry = new RectangleGeometry(new Rect(x, y, 1, 1)) });
                }
            }
        }

        return new DrawingBrush
        {
            Drawing = drawingGroup,
            TileMode = TileMode.Tile,
            SourceRect = new RelativeRect(tile, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(new Rect(
                -(outputOrigin.X % 4) * pixelSize.Width, -(outputOrigin.Y % 4) * pixelSize.Height,
                4 * pixelSize.Width, 4 * pixelSize.Height), RelativeUnit.Absolute),
            Stretch = Stretch.Fill,
        };
    }

    private static AvaloniaColor ToAvaloniaColor(Rgb24 color) => AvaloniaColor.FromRgb(color.R, color.G, color.B);
}
