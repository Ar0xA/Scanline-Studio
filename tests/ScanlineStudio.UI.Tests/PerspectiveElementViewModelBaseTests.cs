using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>Shared perspective/geometry behaviour of the Box and Image element view-models.</summary>
public sealed class PerspectiveElementViewModelBaseTests
{
    public const string Box = "Box";
    public const string Image = "Image";

    private sealed class UndoCounter
    {
        public int Geometry { get; set; }

        public int Style { get; set; }
    }

    private static PerspectiveElementViewModelBase Make(string kind, UndoCounter? undo = null)
    {
        undo ??= new UndoCounter();
        return kind switch
        {
            Box => new BoxElementViewModel
            {
                ImageWidth = 100,
                ImageHeight = 100,
                PushUndoSnapshotForGeometryChange = () => undo.Geometry++,
                PushUndoSnapshotForStyleChange = () => undo.Style++,
            },
            Image => new ImageElementViewModel(new ArrayImageSource(2, 2, new Rgb24[4]))
            {
                Origin = new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, null),
                ImageWidth = 100,
                ImageHeight = 100,
                PushUndoSnapshotForGeometryChange = () => undo.Geometry++,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static void SetQuad(PerspectiveElementViewModelBase e, double x0, double y0, double x1, double y1, double x2, double y2, double x3, double y3)
    {
        e.Corner0X = x0; e.Corner0Y = y0;
        e.Corner1X = x1; e.Corner1Y = y1;
        e.Corner2X = x2; e.Corner2Y = y2;
        e.Corner3X = x3; e.Corner3Y = y3;
    }

    private static double[] Quad(PerspectiveElementViewModelBase e) =>
        [e.Corner0X, e.Corner0Y, e.Corner1X, e.Corner1Y, e.Corner2X, e.Corner2Y, e.Corner3X, e.Corner3Y];

    private static void AssertQuad(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], actual[i], 1e-12);
        }
    }

    private static bool IsDisposed(WriteableBitmap bitmap)
    {
        try
        {
            using (bitmap.Lock())
            {
            }

            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void Geometry_WhenPerspectiveDisabled_ReadsAndWritesTheNaturalFields(string kind)
    {
        var e = Make(kind);

        e.X = 0.6;
        e.Y = 0.4;
        e.Width = 0.25;
        e.Height = 0.15;

        Assert.Equal(0.6, (double)e.NaturalX);
        Assert.Equal(0.4, (double)e.NaturalY);
        Assert.Equal(0.25, (double)e.NaturalWidth);
        Assert.Equal(0.15, (double)e.NaturalHeight);
        Assert.Equal(0.6, (double)e.X);
        Assert.Equal(0.15, (double)e.Height);
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void XY_WhenPerspectiveEnabled_TranslateAllFourCorners(string kind)
    {
        var e = Make(kind);
        SetQuad(e, 0.1, 0.1, 0.5, 0.15, 0.45, 0.4, 0.15, 0.35);
        e.PerspectiveEnabled = true;

        e.X = (double)e.X + 0.2;
        e.Y = (double)e.Y - 0.05;

        AssertQuad([0.3, 0.05, 0.7, 0.1, 0.65, 0.35, 0.35, 0.3], Quad(e));
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void WidthHeight_WhenPerspectiveEnabled_ScaleAboutTheBboxCentre(string kind)
    {
        var e = Make(kind);
        SetQuad(e, 0.2, 0.2, 0.4, 0.2, 0.4, 0.4, 0.2, 0.4);
        e.PerspectiveEnabled = true;

        e.Width = 0.4;
        e.Height = 0.1;

        AssertQuad([0.1, 0.25, 0.5, 0.25, 0.5, 0.35, 0.1, 0.35], Quad(e));
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void WidthHeight_OnAZeroExtentQuad_AreNoOps(string kind)
    {
        var e = Make(kind);
        SetQuad(e, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3);
        e.PerspectiveEnabled = true;

        e.Width = 0.5;
        e.Height = 0.5;

        AssertQuad([0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3, 0.3], Quad(e));
    }

    [AvaloniaTheory]
    [InlineData(Box, false)]
    [InlineData(Box, true)]
    [InlineData(Image, false)]
    [InlineData(Image, true)]
    public void Geometry_NonFiniteWrites_AreRejected(string kind, bool perspective)
    {
        var undo = new UndoCounter();
        var e = Make(kind, undo);
        SetQuad(e, 0.1, 0.1, 0.5, 0.1, 0.5, 0.4, 0.1, 0.4);
        e.PerspectiveEnabled = perspective;
        var before = (X: (double)e.X, Y: (double)e.Y, W: (double)e.Width, H: (double)e.Height);
        var quadBefore = Quad(e);
        undo.Geometry = 0;

        foreach (var bad in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            e.X = bad;
            e.Y = bad;
            e.Width = bad;
            e.Height = bad;
        }

        Assert.Equal(before, ((double)e.X, (double)e.Y, (double)e.Width, (double)e.Height));
        AssertQuad(quadBefore, Quad(e));
        Assert.Equal(0, undo.Geometry);
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void EachGeometryFieldWrite_PushesOneUndoStep_AndAnUnchangedWritePushesNone(string kind)
    {
        var undo = new UndoCounter();
        var e = Make(kind, undo);

        e.NaturalX = 0.7;
        Assert.Equal(1, undo.Geometry);
        e.NaturalX = 0.7;
        Assert.Equal(1, undo.Geometry);
        e.Corner1Y = 0.3;
        Assert.Equal(2, undo.Geometry);
        e.Width = 0.45;
        Assert.Equal(3, undo.Geometry);
    }

    [AvaloniaTheory]
    [InlineData(Box, 0, 1)]
    [InlineData(Image, 1, 0)]
    public void RotationDegreesWrite_PushesTheTypesOwnUndoStep(string kind, int expectedGeometry, int expectedStyle)
    {
        var undo = new UndoCounter();
        var e = Make(kind, undo);

        e.RotationDegrees = 90.0;

        Assert.Equal(expectedGeometry, undo.Geometry);
        Assert.Equal(expectedStyle, undo.Style);
        Assert.NotNull(e.RotationTransform);
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void RenderWarpedPreview_AssignedAfterPerspectiveEnabled_StillRendersOnce(string kind)
    {
        var e = Make(kind);
        e.PerspectiveEnabled = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Null(e.WarpedCanvasBitmap);
        var calls = 0;

        e.RenderWarpedPreview = (Func<int, int, BgraPixelBuffer>)((w, h) =>
        {
            calls++;
            return BgraPixelBuffer.FromSolidColor(new Rgb24(1, 2, 3), w, h);
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, calls);
        Assert.NotNull(e.WarpedCanvasBitmap);
    }

    [AvaloniaTheory]
    [InlineData(Box, 2048, 256)]
    [InlineData(Image, 2048, 512)]
    public void WarpedPreviewSize_At4096x512CanvasPixels_FollowsEachTypesRule(string kind, int expectedWidth, int expectedHeight)
    {
        var e = Make(kind);
        e.ImageWidth = 4096.0;
        e.ImageHeight = 512.0;
        SetQuad(e, 0, 0, 1, 0, 1, 1, 0, 1);
        e.PerspectiveEnabled = true;
        (int Width, int Height) actual = default;

        e.RenderWarpedPreview = (Func<int, int, BgraPixelBuffer>)((w, h) =>
        {
            actual = (w, h);
            return BgraPixelBuffer.FromSolidColor(default, w, h);
        });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal((expectedWidth, expectedHeight), actual);
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void Dispose_SecondCallIsANoOp_OwnedBitmapsEndDisposed_AndNoRebuildAfterDispose(string kind)
    {
        var e = Make(kind);
        e.PerspectiveEnabled = true;
        e.RenderWarpedPreview = (Func<int, int, BgraPixelBuffer>)((w, h) => BgraPixelBuffer.FromSolidColor(new Rgb24(1, 2, 3), w, h));
        Dispatcher.UIThread.RunJobs();
        var warped = e.WarpedCanvasBitmap!;
        var canvas = (e as ImageElementViewModel)?.CanvasBitmap;

        ((IDisposable)e).Dispose();
        ((IDisposable)e).Dispose();

        Assert.False(IsDisposed(warped));
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(warped));
        if (canvas is not null)
        {
            Assert.True(IsDisposed(canvas));
        }

        ((IDisposable)e).Dispose();
        Dispatcher.UIThread.RunJobs();

        // No preview rebuild after dispose.
        e.Corner0X = 0.05;
        Dispatcher.UIThread.RunJobs();
        Assert.Same(warped, e.WarpedCanvasBitmap);
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void SeedThenDisable_RoundTripsTheBounds(string kind)
    {
        var e = Make(kind);
        e.X = 0.4;
        e.Y = 0.6;
        e.Width = 0.3;
        e.Height = 0.2;

        e.SeedCornersFromBounds();
        Assert.False(e.PerspectiveEnabled);
        AssertQuad([0.25, 0.5, 0.55, 0.5, 0.55, 0.7, 0.25, 0.7], Quad(e));
        e.PerspectiveEnabled = true;
        var c = e.Corners;
        AssertQuad(Quad(e), new[] { c.Corner0X, c.Corner0Y, c.Corner1X, c.Corner1Y, c.Corner2X, c.Corner2Y, c.Corner3X, c.Corner3Y });

        e.Corner1X = 0.65;
        e.DisablePerspectiveKeepingBounds();

        Assert.False(e.PerspectiveEnabled);
        Assert.Equal(0.45, e.NaturalX, 1e-12);
        Assert.Equal(0.6, e.NaturalY, 1e-12);
        Assert.Equal(0.4, e.NaturalWidth, 1e-12);
        Assert.Equal(0.2, e.NaturalHeight, 1e-12);
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void RotateCornersInFrameClockwise_MapsXYTo1MinusYX_AndFourTimesIsIdentity(string kind)
    {
        var e = Make(kind);
        SetQuad(e, 0.1, 0.2, 0.6, 0.25, 0.55, 0.7, 0.05, 0.65);
        e.PerspectiveEnabled = true;
        var start = Quad(e);

        e.RotateCornersInFrameClockwise();
        AssertQuad([0.8, 0.1, 0.75, 0.6, 0.3, 0.55, 0.35, 0.05], Quad(e));
        e.RotateCornersInFrameClockwise();
        e.RotateCornersInFrameClockwise();
        e.RotateCornersInFrameClockwise();

        AssertQuad(start, Quad(e));
    }

    [AvaloniaTheory]
    [InlineData(Box)]
    [InlineData(Image)]
    public void SetCorner_WritesOnlyTheIndexedCorner_AndIgnoresAnOutOfRangeIndex(string kind)
    {
        var e = Make(kind);
        SetQuad(e, 0.1, 0.1, 0.5, 0.1, 0.5, 0.4, 0.1, 0.4);
        for (var i = 0; i < 4; i++)
        {
            var expected = Quad(e);
            expected[2 * i] = 0.9 - (i * 0.1);
            expected[(2 * i) + 1] = 0.05 + (i * 0.1);

            e.SetCorner(i, expected[2 * i], expected[(2 * i) + 1]);

            AssertQuad(expected, Quad(e));
        }

        var before = Quad(e);
        e.SetCorner(-1, 0.33, 0.33);
        e.SetCorner(4, 0.33, 0.33);
        AssertQuad(before, Quad(e));
    }
}
