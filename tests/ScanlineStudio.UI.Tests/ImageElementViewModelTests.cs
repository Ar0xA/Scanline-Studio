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
}
