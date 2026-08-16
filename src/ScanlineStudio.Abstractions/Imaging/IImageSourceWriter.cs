namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Writes an already-resolved <see cref="IImageSource"/> out as a PNG file — the missing
/// counterpart to <see cref="IImageFileLoader"/> (load-only) that Phase 5 template persistence
/// (spec/15-template-designer.md) needs to embed a real pixel copy of every image element into a
/// saved template's own <c>assets/</c> folder. Declared here (not alongside its
/// <c>ScanlineStudio.Core.Imaging</c> implementation) so <c>ScanlineStudio.Application</c> can depend
/// on the interface without pulling in a concrete <c>SixLabors.ImageSharp</c> reference, same
/// reasoning as <see cref="IImageFileLoader"/>.</summary>
public interface IImageSourceWriter
{
    Task WritePngAsync(IImageSource source, string path, CancellationToken ct = default);
}
