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

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio): nullable, not a defaulted
    /// <c>bool</c> -- matching the System.Text.Json init-only-default trap already documented at
    /// <c>AudioDeviceSettings.cs:49-55</c>; the desired default (off) equals the CLR default, so the
    /// trap doesn't actually bite here, but the sibling settings on this type are all nullable for
    /// this reason and a future default change should not have to remember to re-check it.
    /// <see langword="null"/> reads as off, same convention as every nullable flag on this
    /// type.</summary>
    public bool? AutoSaveAudioEnabled { get; init; }

    /// <summary>See <see cref="ImagesDirectory"/>'s own doc comment for the null-means-default
    /// convention -- resolved by <see cref="ResolveAudioDirectoryAsync"/>, a separate method (not a
    /// shared helper with <see cref="ResolveDirectoryAsync"/>) since the two settings resolve to
    /// different default locations.</summary>
    public string? AudioDirectory { get; init; }

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

    /// <summary>Same null-means-default resolution shape as <see cref="ResolveDirectoryAsync"/>, a
    /// separate method (not shared) since the default location differs -- <c>MyMusic</c>, matching
    /// platform convention for audio, mirroring <see cref="ResolveDirectoryAsync"/>'s own use of
    /// <c>MyPictures</c> for images.</summary>
    public static async Task<string> ResolveAudioDirectoryAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = settings.GetSection(SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        return ResolveAudioDirectory(section);
    }

    /// <summary>Synchronous core of <see cref="ResolveAudioDirectoryAsync"/>, taking an
    /// ALREADY-LOADED section -- exists so a caller that needs BOTH <see cref="AutoSaveAudioEnabled"/>
    /// and the resolved directory together (<c>SqliteReceiveHistoryStore.GetAudioSettingsAsync</c>)
    /// can do so from ONE <see cref="ISettingsStore.LoadAsync"/> snapshot, rather than one load here
    /// plus a second, independent one for the enabled flag -- the two could otherwise theoretically
    /// disagree if a concurrent <c>SetAudioSettingsAsync</c> landed between the two loads.</summary>
    public static string ResolveAudioDirectory(ReceiveHistorySettings? section) =>
        !string.IsNullOrWhiteSpace(section?.AudioDirectory)
            ? section.AudioDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "ScanlineStudio", "History");
}
