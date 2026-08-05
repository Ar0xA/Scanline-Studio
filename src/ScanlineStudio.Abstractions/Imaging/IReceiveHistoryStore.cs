namespace ScanlineStudio.Abstractions.Imaging;

/// <summary>A browsable index of received images — see spec/07-image-pipeline.md's "RX history"
/// section. Declared here (not alongside its `ScanlineStudio.Core.Logbook` implementation) so
/// `ScanlineStudio.UI` can depend on the interface without pulling in a concrete SQLite-backed
/// reference, same reasoning as <see cref="IImageFileLoader"/>/<see cref="IStockImageLibrary"/>.</summary>
public sealed record ReceiveHistoryEntry(string Id, DateTimeOffset ReceivedAt, string ModeId, string FilePath, string? LinkedQsoId);

public sealed record ReceiveHistoryFilter(string? ModeId = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public interface IReceiveHistoryStore
{
    Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default);

    /// <summary>Same <see cref="IImageSource"/>-only contract as <see cref="IStockImageLibrary"/> —
    /// no SQLite/ImageSharp type ever crosses into `ScanlineStudio.UI`. The SQLite-backed
    /// implementation lives in a `Core.*` project; `ScanlineStudio.UI` only ever sees this interface
    /// via DI.</summary>
    Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default);

    /// <summary>Called by the same application-layer adapter that populates <see cref="IReceivedImageBuffer"/>,
    /// on decode completion.</summary>
    Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default);
}
