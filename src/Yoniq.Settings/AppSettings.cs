namespace Yoniq.Settings;

/// <summary>
/// Root settings document persisted to <c>settings.json</c>. Per-module sections (audio, radio,
/// SSTV, etc. — see spec/12-settings.md) are added here as those modules gain real configuration
/// needs; kept minimal for the Phase 0 walking skeleton.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
}
