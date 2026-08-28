using System.Text.Json.Serialization;

namespace ScanlineStudio.Application;

/// <summary>Tracks which preset (if any) the LIVE settings.json currently considers active --
/// Configurations-preset backlog, Phase 3 (2026-08-28). Lives ONLY in the live settings.json, never
/// inside a preset FILE (a saved/cloned preset must not carry forward a stale "I am named X" self-
/// reference) -- <see cref="ScanlineStudio.Settings.ConfigurationPresetStore"/>'s own `Sanitize` step
/// strips this section unconditionally on every read AND write of a preset file, enforced as a store
/// invariant, not a caller convention (Phase 2 round-2 finding).
///
/// <see cref="SectionKey"/>'s value MUST match
/// <see cref="ScanlineStudio.Settings.ConfigurationPresetStore.ActivePresetSectionKey"/> exactly --
/// duplicated as a literal there because <c>ScanlineStudio.Settings</c> sits BELOW this project in
/// the layering and can never reference this type directly (same reasoning as every other settings
/// section's own split between its typed home and <c>ScanlineStudio.Settings</c>' raw
/// <c>Dictionary&lt;string, JsonElement&gt;</c> bag). A mismatch between the two literals would
/// silently stop <c>ConfigurationPresetStore</c> from stripping this marker.</summary>
public sealed record ConfigurationPresetSettings
{
    public const string SectionKey = "ConfigurationPreset";

    /// <summary><see langword="null"/> means no preset is currently considered active (either none
    /// has ever been switched to, or the live settings.json has diverged from every saved preset
    /// since the last switch -- this field is NOT re-validated against what's actually on disk on
    /// every read, only updated by <c>IConfigurationPresetService.SwitchToPresetAsync</c>'s own
    /// write).</summary>
    public string? ActivePresetName { get; init; }
}

[JsonSerializable(typeof(ConfigurationPresetSettings))]
public sealed partial class ConfigurationPresetSettingsJsonContext : JsonSerializerContext;
