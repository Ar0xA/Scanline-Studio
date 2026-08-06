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

    /// <summary>Legacy default retention count, verified against actual legacy source (not
    /// inferred): <c>yoniq-old/YONIQ-main/Main.cpp:898</c> <c>sys.m_HistMax = 32;</c>, read from
    /// ini <c>[Window]/HistMax</c> (Main.cpp:1811) and applied to the ring buffer's
    /// <c>CBitmapHist::m_Head.m_Max</c> unconditionally on every <c>Open()</c> (ComLib.cpp:2658-2686,
    /// both the fresh-file and existing-file branches) — the class's own constructor default of 64
    /// (ComLib.h:591) never survives to be the *effective* default, since <c>Open()</c> always
    /// overwrites it with <c>sys.m_HistMax</c> first. <c>HISTMAX 256</c> (ComLib.h:561) is a
    /// separate hard array-size cap, not a default.</summary>
    public const int DefaultMaxEntries = 32;

    public string? ImagesDirectory { get; init; }

    /// <summary>Nullable, not <c>= DefaultMaxEntries</c> — System.Text.Json does not honor a C#
    /// property-initializer default for a property absent from an already-persisted JSON payload
    /// (silently deserializes to the CLR default <c>0</c> instead); this is the same trap
    /// documented and fixed on <c>ScanlineStudio.Core.Audio.AudioDeviceSettings.TxVolumePercent</c>.
    /// The <c>?? DefaultMaxEntries</c> fallback lives at the one read site
    /// (<see cref="ResolveMaxEntriesAsync"/>), never here.</summary>
    public int? MaxEntries { get; init; }

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

    public static async Task<int> ResolveMaxEntriesAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = settings.GetSection(SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        return section?.MaxEntries ?? DefaultMaxEntries;
    }
}
