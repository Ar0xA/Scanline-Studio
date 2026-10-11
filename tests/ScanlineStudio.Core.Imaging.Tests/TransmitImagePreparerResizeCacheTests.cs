using System.Runtime.CompilerServices;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging.Tests;

/// <summary>The per-element resize cache of the singleton <see cref="TransmitImagePreparer"/>: it must not keep a
/// discarded element source alive, and a cache hit must render exactly like a cache miss.</summary>
public sealed class TransmitImagePreparerResizeCacheTests
{
    private const int CanvasWidth = 320;
    private const int CanvasHeight = 256;

    [Fact]
    public void ComposePreview_DoesNotRetainADiscardedElementSource()
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var canvas = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);

        var sourceReference = ComposeWithFreshSource(preparer, canvas);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(sourceReference.IsAlive, "the resize cache kept a discarded element source alive");
        GC.KeepAlive(preparer);
    }

    [Theory]
    [InlineData(ImageFitMode.Stretch)]
    [InlineData(ImageFitMode.Contain)]
    [InlineData(ImageFitMode.Cover)]
    public void ComposePreview_CacheHit_EqualsCacheMiss(ImageFitMode fit)
    {
        var preparer = ElementBlitTestSupport.CreatePreparer();
        var canvas = ElementBlitTestSupport.Patterned(CanvasWidth, CanvasHeight, seed: 1);
        var document = new TemplateDocument(null, [Element(ElementBlitTestSupport.Patterned(97, 61, seed: 7), fit)]);

        var miss = Compose(preparer, canvas, document);
        var hit = Compose(preparer, canvas, document);
        var freshPreparerMiss = Compose(ElementBlitTestSupport.CreatePreparer(), canvas, document);

        ElementBlitTestSupport.AssertPixelsEqual(miss, hit);
        ElementBlitTestSupport.AssertPixelsEqual(freshPreparerMiss, hit);
    }

    // NoInlining keeps the source and document locals out of the caller's frame, so only the cache can root them.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ComposeWithFreshSource(TransmitImagePreparer preparer, IImageSource canvas)
    {
        var source = ElementBlitTestSupport.Patterned(97, 61, seed: 7);
        var composed = Compose(preparer, canvas, new TemplateDocument(null, [Element(source, ImageFitMode.Stretch)]));
        Assert.False(ElementBlitTestSupport.Pixels(canvas).SequenceEqual(ElementBlitTestSupport.Pixels(composed)), "the element must actually be drawn");
        return new WeakReference(source);
    }

    private static IImageSource Compose(TransmitImagePreparer preparer, IImageSource canvas, TemplateDocument document) =>
        preparer.ComposePreview(canvas, new NormalizedRect(0, 0, 1, 1), CanvasWidth, CanvasHeight, preserveAspect: false, new ImageAdjustments(), document);

    private static TemplateImageElement Element(IImageSource source, ImageFitMode fit) =>
        new(new NormalizedRect(0.15, 0.2, 0.3, 0.3125), Z: 0, source, fit);
}
