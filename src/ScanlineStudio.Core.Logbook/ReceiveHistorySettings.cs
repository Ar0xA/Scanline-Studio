namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted RX-history saved-image folder location — see spec/07-image-pipeline.md's "RX
/// flow" section. A <c>null</c> <see cref="ImagesDirectory"/> means "use the default," resolved by
/// <see cref="ReceiveHistoryRecorder"/> itself, same shape as
/// <see cref="ScanlineStudio.Core.Imaging.ImageLibrarySettings"/>'s null <c>StockDirectory</c> (kept
/// as a separate settings section rather than shared, to avoid a <c>Core.Logbook</c> ->
/// <c>Core.Imaging</c> reference).</summary>
public sealed record ReceiveHistorySettings
{
    public const string SectionKey = "ReceiveHistory";

    public string? ImagesDirectory { get; init; }
}
