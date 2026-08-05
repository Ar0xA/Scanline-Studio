namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>A user-managed folder of TX source images with thumbnails — see spec/07-image-pipeline.md's
/// "Stock image library" section. Declared here (not alongside its `ScanlineStudio.Core.Imaging`
/// implementation) so `ScanlineStudio.UI` can depend on the interface without pulling in a
/// `ScanlineStudio.Core.Imaging` concrete reference, same reasoning as <see cref="IImageFileLoader"/>.</summary>
public sealed record StockImageEntry(string Id, string FileName, string FilePath);

public interface IStockImageLibrary
{
    Task<IReadOnlyList<StockImageEntry>> ListAsync(CancellationToken ct = default);

    /// <summary>Returns <see cref="IImageSource"/> — the same abstraction <see cref="IImageFileLoader"/>
    /// already returns — never a UI-toolkit bitmap type or an ImageSharp type; `ScanlineStudio.UI` owns
    /// converting <see cref="IImageSource"/> to an Avalonia `Bitmap` via the existing
    /// `ImageSourceBitmapConverter`, exactly like every other pane today.</summary>
    Task<IImageSource> LoadThumbnailAsync(StockImageEntry entry, int maxDimension, CancellationToken ct = default);

    /// <summary>Mirrors <see cref="IImageFileLoader.LoadAsync"/>'s fit-to-mode contract, so
    /// `TxControlsPaneViewModel` can treat a stock pick and a browsed file identically once loaded.</summary>
    Task<IImageSource> LoadFullAsync(StockImageEntry entry, int targetWidth, int targetHeight, CancellationToken ct = default);
}
