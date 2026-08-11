using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted QRZ.com XML Callbook lookup configuration — see spec/08-logging.md's "QRZ.com
/// lookup" section and legacy `qrzcom.cpp`. Opt-in, off by default, same posture as
/// <see cref="QrzUploadSettings"/> -- the app never queries QRZ without the user explicitly
/// enabling it and supplying their own account credentials (never legacy's hardcoded personal
/// username/password). Nullable properties only, per the STJ-missing-property-defaults-to-CLR-default
/// trap documented on <see cref="QrzUploadSettings"/>/<see cref="GridTrackerStreamingSettings"/>.
///
/// Storing a real QRZ.com account PASSWORD here (not a scoped, revocable API key like
/// <see cref="QrzUploadSettings.ApiKey"/>) is a materially different exposure -- plaintext in the
/// local settings JSON is still the only option (QRZ's XML Callbook API has no scoped-credential
/// mode), so the Options UI itself carries a hint recommending a unique/throwaway password rather
/// than the account's real one.</summary>
public sealed record QrzLookupSettings
{
    public const string SectionKey = "QrzLookup";

    public bool? Enabled { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public static async Task<QrzLookupSettings> ResolveAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return settings.GetSection(SectionKey, QrzLookupSettingsJsonContext.Default.QrzLookupSettings) ?? new QrzLookupSettings();
    }
}
