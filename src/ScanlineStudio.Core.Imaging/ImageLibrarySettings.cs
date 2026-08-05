namespace ScanlineStudio.Core.Imaging;

/// <summary>Persisted stock-image-library folder location — see spec/07-image-pipeline.md's "Stock
/// image library" section. A <c>null</c> <see cref="StockDirectory"/> means "use the default,"
/// resolved by <see cref="StockImageLibrary"/> itself (same "no assumptions baked into the settings
/// record" shape as <see cref="ScanlineStudio.Core.Audio.AudioDeviceSettings"/>'s null device IDs).</summary>
public sealed record ImageLibrarySettings
{
    public const string SectionKey = "ImageLibrary";

    public string? StockDirectory { get; init; }
}
