using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted ADIF-over-UDP forwarding configuration -- see spec/08-logging.md and the
/// accompanying plan file. Opt-in, off by default (no cloud/external-app dependency unless the user
/// explicitly enables at least one destination), matching the spec's existing posture for QRZ.com
/// lookup. Generalized (2026-08-15) from a single-destination GridTracker-only setting to a list of
/// arbitrary named destinations -- see <see cref="IAdifUdpStreamer"/>'s own doc comment for why.
/// <see cref="Destinations"/> defaults to empty, never pre-populated (no silent UDP traffic to a
/// service the user never configured) -- the sole exception is <see cref="MigrateIfNeeded"/> seeding
/// ONE entry from an already-enabled legacy GridTracker config, so an existing user's already-working
/// forwarding survives the upgrade rather than silently vanishing.
///
/// Every property nullable, never a property-initializer default -- same reasoning as
/// <see cref="AdifUdpDestination"/>'s own doc comment.</summary>
public sealed record AdifUdpStreamingSettings
{
    public const string SectionKey = "AdifUdpStreaming";
    public const string DefaultClientId = "Scanline Studio";

    public IReadOnlyList<AdifUdpDestination>? Destinations { get; init; }

    public string? ClientId { get; init; }

    /// <summary>The single source of truth for "does this settings.json need a GridTracker-to-
    /// multi-destination migration" -- called from BOTH <see cref="ResolveAsync"/> (the streamer's
    /// own read path) and <c>OptionsSettingsService.LoadAsync</c> (the Options dialog's read path),
    /// so the dialog always shows exactly what the streamer will actually send; a migration seeded
    /// in only one of the two would let the dialog and the real forwarding behavior silently
    /// diverge.
    ///
    /// Migration triggers on the NEW section being genuinely ABSENT from <paramref name="settings"/>
    /// -- checked via <see cref="AppSettings.Sections"/>'s own key presence, not "has an empty/null
    /// <see cref="Destinations"/> list." That distinction matters: an explicitly-saved empty list
    /// (the user removed every destination on purpose) must stay empty forever, never re-trigger
    /// this seed -- re-triggering on "empty" would silently resurrect GridTracker forwarding on
    /// every later load with no UI way to turn it off, since deleting the migrated row would just
    /// recreate it next read.</summary>
    public static AdifUdpStreamingSettings MigrateIfNeeded(AppSettings settings)
    {
        if (settings.Sections.ContainsKey(SectionKey))
        {
            return settings.GetSection(SectionKey, AdifUdpStreamingSettingsJsonContext.Default.AdifUdpStreamingSettings) ?? new AdifUdpStreamingSettings();
        }

        var legacy = settings.GetSection(LegacyGridTrackerStreamingSettings.SectionKey, AdifUdpStreamingSettingsJsonContext.Default.LegacyGridTrackerStreamingSettings);
        if (legacy is { Enabled: true })
        {
            return new AdifUdpStreamingSettings
            {
                Destinations =
                [
                    new AdifUdpDestination
                    {
                        Enabled = true,
                        Name = "GridTracker",
                        Host = legacy.Host is { Length: > 0 } ? legacy.Host : LegacyGridTrackerStreamingSettings.DefaultHost,
                        Port = legacy.Port is > 0 and <= 65535 ? legacy.Port : LegacyGridTrackerStreamingSettings.DefaultPort,
                    },
                ],
                ClientId = legacy.ClientId,
            };
        }

        return new AdifUdpStreamingSettings();
    }

    /// <summary>This read-time seed (via <see cref="MigrateIfNeeded"/>) does NOT write anything back
    /// to disk itself -- the first real Save (from either this class's own callers being exercised,
    /// or the Options dialog) is what persists the migrated destination for good.</summary>
    public static async Task<AdifUdpStreamingSettings> ResolveAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return MigrateIfNeeded(settings);
    }
}

/// <summary>The pre-generalization (2026-08-15) single-destination GridTracker-only settings shape,
/// retained ONLY so <see cref="AdifUdpStreamingSettings.MigrateIfNeeded"/> can still deserialize an
/// already-persisted <see cref="SectionKey"/> section written by an older version of this app --
/// never written to by any current code path, never read anywhere except that one migration call.</summary>
public sealed record LegacyGridTrackerStreamingSettings
{
    public const string SectionKey = "GridTrackerStreaming";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 2237;

    public bool? Enabled { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? ClientId { get; init; }
}
