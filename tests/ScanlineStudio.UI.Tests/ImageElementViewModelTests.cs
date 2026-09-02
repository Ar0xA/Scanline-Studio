using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.ViewModels;

namespace ScanlineStudio.UI.Tests;

/// <summary>T0-11 (production_audit.md): <see cref="ImageElementViewModel"/>'s own bitmap-disposal
/// logic -- <see cref="ImageElementViewModel.OnSourceChanged"/> (a surviving element's Source
/// reassigned) and <see cref="ImageElementViewModel.Dispose"/> (the whole element discarded).</summary>
public sealed class ImageElementViewModelTests
{
    private static ArrayImageSource MakeSource(int width, int height) =>
        new(width, height, new Rgb24[width * height]);

    [AvaloniaFact]
    public void SourceChanged_DisposesTheOldCanvasBitmap_DeferredViaDispatcherPost()
    {
        var element = new ImageElementViewModel(MakeSource(2, 2)) { Origin = new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, null) };
        var firstBitmap = element.CanvasBitmap;

        element.Source = MakeSource(2, 2);

        Assert.False(IsDisposed(firstBitmap), "must not be disposed before the deferred post runs");
        Assert.NotSame(firstBitmap, element.CanvasBitmap);
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(firstBitmap));
    }

    [AvaloniaFact]
    public void Dispose_DisposesCanvasBitmap_DeferredViaDispatcherPost()
    {
        var element = new ImageElementViewModel(MakeSource(2, 2)) { Origin = new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, null) };
        var bitmap = element.CanvasBitmap;

        element.Dispose();

        Assert.False(IsDisposed(bitmap), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(bitmap));
    }

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique WriteableBitmapPoolTests
    // uses: a disposed instance throws NullReferenceException (not ObjectDisposedException) from
    // any real operation, here .Lock().
    private static bool IsDisposed(WriteableBitmap bitmap)
    {
        try
        {
            using (bitmap.Lock())
            {
            }

            return false;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    }

    private static ImageElementViewModel MakeElement() =>
        new(MakeSource(2, 2)) { Origin = new TxImageEditorPaneViewModel.ImageSourceOrigin(TxImageEditorPaneViewModel.ImageSourceKind.File, null), ImageWidth = 100, ImageHeight = 100 };

    [AvaloniaFact]
    public void X_WhenPerspectiveDisabled_ReadsAndWritesNaturalXDirectly()
    {
        var element = MakeElement();

        element.X = 0.7;

        Assert.Equal(0.7, element.X);
        Assert.Equal(0.7, element.NaturalX);
    }

    [AvaloniaFact]
    public void X_WhenPerspectiveEnabled_TranslatesAllFourCornersBySameDelta()
    {
        var element = MakeElement();
        element.Corner0X = 0.1;
        element.Corner1X = 0.5;
        element.Corner2X = 0.5;
        element.Corner3X = 0.1;
        element.Corner0Y = 0.1;
        element.Corner1Y = 0.1;
        element.Corner2Y = 0.3;
        element.Corner3Y = 0.3;
        element.PerspectiveEnabled = true;
        var centerBefore = element.X;

        element.X = centerBefore + 0.2;

        Assert.Equal(0.3, element.Corner0X, precision: 10);
        Assert.Equal(0.7, element.Corner1X, precision: 10);
        Assert.Equal(0.7, element.Corner2X, precision: 10);
        Assert.Equal(0.3, element.Corner3X, precision: 10);
        // Y corners must be untouched by an X-only write.
        Assert.Equal(0.1, element.Corner0Y, precision: 10);
        Assert.Equal(0.3, element.Corner2Y, precision: 10);
    }

    [AvaloniaFact]
    public void Width_WhenPerspectiveEnabled_ScalesCornerXOffsetsAboutTheBboxCenter()
    {
        var element = MakeElement();
        // A square quad, bbox X in [0.2, 0.4] (width 0.2), center 0.3.
        element.Corner0X = 0.2; element.Corner0Y = 0.2;
        element.Corner1X = 0.4; element.Corner1Y = 0.2;
        element.Corner2X = 0.4; element.Corner2Y = 0.4;
        element.Corner3X = 0.2; element.Corner3Y = 0.4;
        element.PerspectiveEnabled = true;

        element.Width = 0.4; // double the width -> scale factor 2 about center 0.3

        Assert.Equal(0.1, element.Corner0X, precision: 10);
        Assert.Equal(0.5, element.Corner1X, precision: 10);
        Assert.Equal(0.5, element.Corner2X, precision: 10);
        Assert.Equal(0.1, element.Corner3X, precision: 10);
        // Y corners untouched by a Width-only write.
        Assert.Equal(0.2, element.Corner0Y, precision: 10);
        Assert.Equal(0.4, element.Corner2Y, precision: 10);
    }

    [AvaloniaFact]
    public void CornerWrite_WhilePerspectiveEnabled_RaisesTheFullGeometryCascade()
    {
        var element = MakeElement();
        element.PerspectiveEnabled = true;
        var raised = new HashSet<string>();
        element.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is { } name)
            {
                raised.Add(name);
            }
        };

        element.Corner0X = 0.15;

        Assert.Contains(nameof(ImageElementViewModel.X), raised);
        Assert.Contains(nameof(ImageElementViewModel.Y), raised);
        Assert.Contains(nameof(ImageElementViewModel.Width), raised);
        Assert.Contains(nameof(ImageElementViewModel.Height), raised);
        Assert.Contains(nameof(ImageElementViewModel.LeftPixels), raised);
        Assert.Contains(nameof(ImageElementViewModel.TopPixels), raised);
        Assert.Contains(nameof(ImageElementViewModel.CanvasWidthPixels), raised);
        Assert.Contains(nameof(ImageElementViewModel.CanvasHeightPixels), raised);
        Assert.Contains(nameof(ImageElementViewModel.CanvasCorner0Point), raised);
        Assert.Contains(nameof(ImageElementViewModel.CanvasCorner1Point), raised);
        Assert.Contains(nameof(ImageElementViewModel.CanvasCorner2Point), raised);
        Assert.Contains(nameof(ImageElementViewModel.CanvasCorner3Point), raised);
    }

    [AvaloniaFact]
    public void RenderWarpedPreview_AssignedAfterPerspectiveAlreadyEnabled_StillProducesAFirstRender()
    {
        // Round-3 plan-review blocker: CreateElementFromSnapshot/template-load restore paths set
        // PerspectiveEnabled BEFORE RenderWarpedPreview can be assigned (the delegate must close over
        // the constructed element, so it can only be assigned post-construction) -- without this
        // fix, the element would silently stay invisible until the next unrelated corner edit.
        var element = MakeElement();
        element.PerspectiveEnabled = true;
        // Flush BEFORE assigning the delegate -- lets the rebuild scheduled by PerspectiveEnabled's
        // own OnChanged hook actually run (and correctly no-op, since RenderWarpedPreview is still
        // null then) and clear its own "already scheduled" flag, so only the delegate assignment's
        // OWN trigger is left to produce a render. Without this flush, the test can't tell the fix
        // apart from a version that never re-triggers on assignment at all (confirmed: a first draft
        // of this test without the flush passed against a reverted, un-fixed property).
        Dispatcher.UIThread.RunJobs();
        Assert.Null(element.WarpedCanvasBitmap);
        var callCount = 0;

        element.RenderWarpedPreview = (w, h) =>
        {
            callCount++;
            return BgraPixelBuffer.FromSolidColor(new Rgb24(1, 2, 3), w, h);
        };
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, callCount);
        Assert.NotNull(element.WarpedCanvasBitmap);
    }

    [AvaloniaFact]
    public void PerspectiveEnabled_SetFalse_DisposesAndNullsTheWarpedBitmap()
    {
        var element = MakeElement();
        element.PerspectiveEnabled = true;
        element.RenderWarpedPreview = (w, h) => BgraPixelBuffer.FromSolidColor(new Rgb24(1, 2, 3), w, h);
        Dispatcher.UIThread.RunJobs();
        var warped = element.WarpedCanvasBitmap;
        Assert.NotNull(warped);

        element.PerspectiveEnabled = false;

        Assert.Null(element.WarpedCanvasBitmap);
        Assert.False(IsDisposed(warped!), "must not be disposed before the deferred post runs");
        Dispatcher.UIThread.RunJobs();
        Assert.True(IsDisposed(warped!));
    }
}
