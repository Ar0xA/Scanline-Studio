namespace Yoniq.Abstractions.Imaging;

/// <summary>See spec/07-image-pipeline.md. Updated incrementally as
/// <c>Yoniq.Abstractions.Sstv.ISstvDecoder.LineDecoded</c> fires; the UI binds to this for live
/// partial-image rendering (legacy `RxView`'s "image filling in top-to-bottom as it decodes"
/// behavior).</summary>
public interface IReceivedImageBuffer
{
    IImageSource Current { get; }

    event Action? Updated;

    Task SaveAsync(string path, CancellationToken ct = default);
}
