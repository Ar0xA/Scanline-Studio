namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>See spec/07-image-pipeline.md. Updated incrementally as
/// <c>ScanlineStudio.Abstractions.Sstv.ISstvDecoder.LineDecoded</c> fires; the UI binds to this for live
/// partial-image rendering (legacy `RxView`'s "image filling in top-to-bottom as it decodes"
/// behavior).</summary>
public interface IReceivedImageBuffer
{
    IImageSource Current { get; }

    /// <summary>How far the current decode has progressed, as a `[0.0, 1.0]` fraction of the image's
    /// total rows -- <see langword="null"/> when idle (no active decode). Reaches exactly
    /// <c>1.0</c> on the scanline group that completes the image, not just an asymptotic value close
    /// to it -- see <c>ReceivedImageBuffer</c>'s own doc comment for why a naive `Line / Height`
    /// alone can't reach exactly 1.0 for multi-row-per-event families.</summary>
    double? Progress { get; }

    event Action? Updated;

    Task SaveAsync(string path, CancellationToken ct = default);
}
