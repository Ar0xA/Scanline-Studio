using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>Loads a standard image file (PNG/JPEG/BMP/...) as TX source material — see
/// spec/07-image-pipeline.md's TX flow. Declared here (not alongside its `ScanlineStudio.Core.Imaging`
/// implementation) so `ScanlineStudio.UI` can depend on the interface without pulling in a
/// `ScanlineStudio.Core.Imaging` concrete reference, which would fail the architecture test
/// (spec/09-ui.md's layering rule) the moment a Dock pane needs this.</summary>
public interface IImageFileLoader
{
    /// <summary>Resizes to exactly <paramref name="targetWidth"/>/<paramref name="targetHeight"/> —
    /// <see cref="ISstvEncoder.EncodeAsync"/> throws on any dimension mismatch, so a picked file
    /// must already be the mode's exact size by the time it gets there. This is a plain fit-to-mode
    /// resize, not the Phase-4 crop/resize/filter tooling (`ITransmitImagePreparer`,
    /// spec/07-image-pipeline.md) — no user-chosen crop region, just "make it transmittable."</summary>
    Task<IImageSource> LoadAsync(string path, int targetWidth, int targetHeight, CancellationToken ct = default);
}
