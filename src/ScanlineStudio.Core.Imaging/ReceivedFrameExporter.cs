using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Imaging;

/// <summary>ImageSharp-backed <see cref="IReceivedFrameExporter"/>.</summary>
public sealed class ReceivedFrameExporter : IReceivedFrameExporter
{
    public async Task ExportAsync(string sourcePath, string destinationPath, int jpegQuality, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync(sourcePath, ct).ConfigureAwait(false);

        var extension = Path.GetExtension(destinationPath);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            await image.SaveAsync(destinationPath, new JpegEncoder { Quality = jpegQuality }, ct).ConfigureAwait(false);
        }
        else
        {
            // Auto-detects the encoder from destinationPath's own extension (PNG today, the only
            // other format this pane's picker offers) -- same mechanism ImageFileLoader/
            // ReceivedImageBuffer/ReceiveHistoryRecorder already rely on elsewhere in this project.
            await image.SaveAsync(destinationPath, ct).ConfigureAwait(false);
        }
    }
}
