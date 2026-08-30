using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.UI.Imaging;

/// <summary>T0-11 (production_audit.md): 2 preallocated <see cref="WriteableBitmap"/> instances,
/// ping-ponged, for a hot path that used to allocate a fresh <see cref="WriteableBitmap"/> on
/// every update (RX live frame, TX editor preview) -- each holds a native Skia buffer with no GC
/// pressure signal, so unbounded reallocation is a real native-memory leak on a long RX session or
/// a slider-drag.
///
/// <b>Reference-swap, not in-place blit-and-invalidate</b> (plan-review decision, rejecting the
/// audit's own literal "keep one, blit in place" wording): every <see cref="Blit"/> call writes
/// into whichever of the 2 owned instances is NOT the one most recently returned, then returns
/// THAT instance -- a genuine object-reference change every time, using the exact same
/// already-proven "assign a different `Bitmap?` reference to an <c>[ObservableProperty]`" ->
/// Avalonia <c>Image</c> re-renders" path this code already relied on before this fix (it used to
/// assign a freshly-allocated bitmap every time; this assigns one of 2 recycled ones instead).
/// This avoids depending on whether Avalonia's <c>Image</c> control re-renders on a bare
/// <c>PropertyChanged</c> notification for an UNCHANGED reference, which no precedent in this
/// codebase answers (<c>WaterfallControl</c>'s own reused-single-buffer blit is NOT comparable --
/// it's a custom <c>Control</c> overriding <c>Render()</c> and calling <c>InvalidateVisual()</c>
/// directly, not an <c>Image</c> bound through a VM property).
///
/// <b>Invariant:</b> a dimension-change resize always happens on the TARGET slot (the one about to
/// be written into), which by construction is never the slot most recently returned -- so a resize
/// can never dispose or reallocate the buffer an <c>Image</c> control is currently displaying.</summary>
// Public, not internal -- this project has no InternalsVisibleTo wired up anywhere (established
// convention, e.g. MainViewModel.cs's own "no InternalsVisibleTo" doc comment), so a test needing
// to construct/call this directly couldn't otherwise.
public sealed class WriteableBitmapPool : IDisposable
{
    private WriteableBitmap? _a;
    private WriteableBitmap? _b;
    private bool _aIsCurrent;

    public WriteableBitmap Blit(IImageSource source)
    {
        var target = _aIsCurrent
            ? EnsureSized(ref _b, source.Width, source.Height)
            : EnsureSized(ref _a, source.Width, source.Height);

        ImageSourceBitmapConverter.BlitInto(target, source);
        _aIsCurrent = !_aIsCurrent;
        return target;
    }

    // Code-review finding: the old value must be dropped from the field BEFORE the (possibly
    // throwing, e.g. out-of-memory on an oversized frame) allocation below -- otherwise a failed
    // reallocation leaves the field pointing at the instance just disposed on the line above,
    // permanently poisoning this slot (every later Blit targeting it would NRE on PixelSize/Lock()).
    private static WriteableBitmap EnsureSized(ref WriteableBitmap? slot, int width, int height)
    {
        if (slot is not null && slot.PixelSize.Width == width && slot.PixelSize.Height == height)
        {
            return slot;
        }

        var stale = slot;
        slot = null;
        stale?.Dispose();
        slot = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        return slot;
    }

    public void Dispose()
    {
        _a?.Dispose();
        _b?.Dispose();
    }
}
