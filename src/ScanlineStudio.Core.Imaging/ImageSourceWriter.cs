using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="IImageSourceWriter"/> — mirrors
/// <see cref="ReceivedImageBuffer"/>'s own <c>SaveAsync</c> pixel-copy pattern (never
/// <c>MemoryMarshal.Cast</c> between the two unrelated <c>Rgb24</c> types, see
/// <see cref="ImageFileLoader"/>'s own doc comment) and its <c>Task.Run</c> off-UI-thread
/// convention for the encode/write work.</summary>
public sealed class ImageSourceWriter : IImageSourceWriter
{
    public Task WritePngAsync(IImageSource source, string path, CancellationToken ct = default)
    {
        return Task.Run(
            () =>
            {
                // Code-review finding: TemplateStore's own callers (TxImageEditorPaneViewModel's
                // BuildPersistedElementAsync) write a template's image-element assets to a path
                // whose "assets/" subdirectory nothing else has created yet by that point in the
                // call order -- every write through this REAL implementation must be able to land
                // in a directory that doesn't exist yet, same precedent as ReceivedImageBuffer's own
                // SaveAsync. Belongs HERE, not at any caller: an IImageSourceWriter caller works
                // against the abstraction and can't assume a given path is even a real, creatable
                // filesystem location (a test double's own path need not be).
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // T1-15 (production_audit.md): shared with ReceivedImageBuffer via ImageSharpPixelConversion.
                using var image = ImageSharpPixelConversion.FromImageSource(source);
                // SaveAsPngAsync's SYNC counterpart, explicit -- image.Save(path) infers the
                // encoder from the path's extension (code-review nit): both current callers always
                // pass a ".png" path, but this method's own name promises PNG regardless of what the
                // caller happens to pass.
                image.SaveAsPng(path);
            },
            ct);
    }
}
