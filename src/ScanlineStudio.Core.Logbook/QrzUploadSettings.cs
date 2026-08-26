using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted QRZ.com Logbook API upload configuration — see spec/08-logging.md and the
/// accompanying plan file. Opt-in, off by default, same posture as
/// <see cref="AdifUdpStreamingSettings"/> and the spec's existing QRZ.com *lookup* section —
/// the app never uploads anywhere without the user explicitly enabling it and supplying their own
/// API key. Nullable properties only, per the STJ-missing-property-defaults-to-CLR-default trap
/// documented on <see cref="AdifUdpStreamingSettings"/>.</summary>
public sealed record QrzUploadSettings
{
    public const string SectionKey = "QrzUpload";

    public bool? Enabled { get; init; }

    public string? ApiKey { get; init; }

    public static async Task<QrzUploadSettings> ResolveAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return settings.GetSection(SectionKey, QrzUploadSettingsJsonContext.Default.QrzUploadSettings) ?? new QrzUploadSettings();
    }
}
