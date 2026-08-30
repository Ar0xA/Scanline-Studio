using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Core.Imaging;
using ScanlineStudio.UI.Imaging;

namespace ScanlineStudio.UI.Tests;

/// <summary>T0-11 (production_audit.md): <see cref="WriteableBitmapPool"/> -- asserts instance
/// identity/count, not pixel bytes, so unaffected by the documented headless-`Lock()`-bytes gotcha
/// (<c>WaterfallControlTests</c>). Requires <see cref="AvaloniaFactAttribute"/>, not a plain
/// `[Fact]`: constructing a real <c>WriteableBitmap</c> and calling <c>.Lock()</c> both need
/// Avalonia's platform render interface initialized.</summary>
public sealed class WriteableBitmapPoolTests
{
    private static ArrayImageSource MakeSource(int width, int height) =>
        new(width, height, new Rgb24[width * height]);

    [AvaloniaFact]
    public void Blit_SameDimensions_AlternatesBetweenExactlyTwoDistinctInstances()
    {
        using var pool = new WriteableBitmapPool();
        var source = MakeSource(4, 4);

        var first = pool.Blit(source);
        var second = pool.Blit(source);
        var third = pool.Blit(source);
        var fourth = pool.Blit(source);

        Assert.NotSame(first, second);
        Assert.Same(first, third);
        Assert.Same(second, fourth);
    }

    [AvaloniaFact]
    public void Blit_DimensionChange_AllocatesFreshAndDisposesTheStaleNonCurrentSlot()
    {
        using var pool = new WriteableBitmapPool();
        var small = MakeSource(2, 2);
        var large = MakeSource(4, 4);

        var firstReturned = pool.Blit(small); // slot A becomes current
        var secondReturned = pool.Blit(large); // writes into (and, since dims changed, resizes) slot B, the non-current one

        Assert.NotSame(firstReturned, secondReturned);
        // The resize invariant (plan-review, stated explicitly): a resize only ever touches the
        // slot about to be written into, never the one most recently returned -- so firstReturned
        // (still "current" until this Blit call flips it) must still be usable/undisposed here.
        Assert.False(IsDisposed(firstReturned));
        Assert.Equal(4, secondReturned.PixelSize.Width);
        Assert.Equal(4, secondReturned.PixelSize.Height);
    }

    [AvaloniaFact]
    public void Blit_NeverDisposesTheSlotMostRecentlyReturned()
    {
        // The resize-invariant test from the pool's own doc comment, exercised across several
        // dimension changes in a row -- whatever Blit most recently returned must never become
        // disposed by a LATER Blit call (which only ever touches the OTHER slot).
        using var pool = new WriteableBitmapPool();
        var dims = new (int Width, int Height)[] { (2, 2), (4, 4), (2, 2), (8, 8), (4, 4) };

        WriteableBitmap? previous = null;
        foreach (var (width, height) in dims)
        {
            var returned = pool.Blit(MakeSource(width, height));
            if (previous is not null)
            {
                Assert.False(IsDisposed(previous), "the slot most recently returned must never be disposed by a subsequent Blit call");
            }

            previous = returned;
        }
    }

    [AvaloniaFact]
    public void Dispose_DisposesBothOwnedSlots()
    {
        var pool = new WriteableBitmapPool();
        var first = pool.Blit(MakeSource(2, 2));
        var second = pool.Blit(MakeSource(2, 2));

        pool.Dispose();

        Assert.True(IsDisposed(first));
        Assert.True(IsDisposed(second));
    }

    // Avalonia's WriteableBitmap has no public IsDisposed -- same technique this codebase's own
    // RxDiskLineStagingBuffer.cs uses for SemaphoreSlim: a disposed instance throws from any real
    // operation, here .Lock(). Empirically NullReferenceException (Dispose() nulls the internal
    // platform impl rather than setting a checked flag), not ObjectDisposedException -- confirmed
    // by running this test against a real disposed instance, not assumed.
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
