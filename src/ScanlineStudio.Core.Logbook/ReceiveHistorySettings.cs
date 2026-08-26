using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted RX-history saved-image folder location — see spec/07-image-pipeline.md's "RX
/// flow" section. A <c>null</c> <see cref="ImagesDirectory"/> means "use the default," resolved by
/// <see cref="ResolveDirectoryAsync"/> (shared by <see cref="ReceiveHistoryRecorder"/> and
/// <see cref="SqliteReceiveHistoryStore"/> so both agree on the same folder), same shape as
/// <see cref="ScanlineStudio.Core.Imaging.ImageLibrarySettings"/>'s null <c>StockDirectory</c> (kept
/// as a separate settings section rather than shared, to avoid a <c>Core.Logbook</c> ->
/// <c>Core.Imaging</c> reference).</summary>
public sealed record ReceiveHistorySettings
{
    public const string SectionKey = "ReceiveHistory";

    public string? ImagesDirectory { get; init; }

    public static async Task<string> ResolveDirectoryAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = settings.GetSection(SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        if (!string.IsNullOrWhiteSpace(section?.ImagesDirectory))
        {
            return section.ImagesDirectory;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "History");
    }
}
