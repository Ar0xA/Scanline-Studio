namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Re-saves an already-received image file to a user-chosen location, optionally
/// re-encoding it — see spec/14-roadmap.md's "Export frame" Tier 2 item. Declared here (not
/// alongside its `ScanlineStudio.Core.Imaging` implementation) so `ScanlineStudio.UI` can depend on
/// the interface without pulling in a concrete reference, same layering reasoning as
/// <see cref="IImageFileLoader"/>.</summary>
public interface IReceivedFrameExporter
{
    /// <summary>Loads <paramref name="sourcePath"/> and saves it to <paramref name="destinationPath"/>.
    /// The encoder is chosen from <paramref name="destinationPath"/>'s own extension — the caller
    /// (`ScanlineStudio.UI`'s file-save picker) is responsible for making sure that extension
    /// actually matches what the user chose, this method does not second-guess it. When the
    /// destination is `.jpg`/`.jpeg`, <paramref name="jpegQuality"/> (1..100) controls compression;
    /// ignored for every other format. Caller must pass an already-clamped value —
    /// `SixLabors.ImageSharp`'s own `JpegEncoder.Quality` setter throws outside 1..100, this method
    /// does not clamp on the caller's behalf.</summary>
    Task ExportAsync(string sourcePath, string destinationPath, int jpegQuality, CancellationToken ct = default);
}
