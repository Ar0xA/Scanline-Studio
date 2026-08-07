using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>Persisted GridTracker UDP-streaming configuration — see spec/08-logging.md and the
/// accompanying plan file. Opt-in, off by default (no cloud/external-app dependency unless the
/// user explicitly enables it), matching the spec's existing posture for QRZ.com lookup.
/// Every property nullable, never a property-initializer default — System.Text.Json silently
/// deserializes a property missing from an already-persisted JSON payload to the CLR default, not
/// the C# initializer value (the same trap fixed on <see cref="ReceiveHistorySettings.MaxEntries"/>
/// and <c>AudioDeviceSettings.TxVolumePercent</c>); fallbacks live only at the single read site,
/// <see cref="ResolveAsync"/>.</summary>
public sealed record GridTrackerStreamingSettings
{
    public const string SectionKey = "GridTrackerStreaming";
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 2237;
    public const string DefaultClientId = "Scanline Studio";

    public bool? Enabled { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public string? ClientId { get; init; }

    public static async Task<GridTrackerStreamingSettings> ResolveAsync(ISettingsStore settingsStore, CancellationToken ct = default)
    {
        var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        return settings.GetSection(SectionKey, GridTrackerStreamingSettingsJsonContext.Default.GridTrackerStreamingSettings) ?? new GridTrackerStreamingSettings();
    }
}
