using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Radio;

/// <summary>Ordered list of <see cref="FrequencyPreset"/> entries, user-editable via the frequency
/// strip's "Edit presets" popup — empty by default (no built-in band-plan assumptions baked in;
/// users add their own, since band/frequency conventions vary by region and this project makes no
/// assumption about which country's allocations apply).</summary>
public sealed record FrequencyPresetsSettings
{
    public const string SectionKey = "FrequencyPresets";

    public IReadOnlyList<FrequencyPreset> Presets { get; init; } = [];
}
