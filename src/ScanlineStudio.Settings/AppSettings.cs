using System.Text.Json;

namespace ScanlineStudio.Settings;

/// <summary>Root settings document persisted to <c>settings.json</c> — see spec/12-settings.md.
///
/// <b>Section shape — corrected from an earlier draft of spec/12-settings.md.</b> That draft showed
/// <c>AppSettings(AudioSettings Audio, RadioConnectionSettings Radio, ...)</c> — a flat record
/// directly typed with each module's own settings type. That shape can never actually compile:
/// <c>ScanlineStudio.Settings</c> sits at the *bottom* of spec/01-architecture.md's layering diagram (below
/// even <c>ScanlineStudio.Abstractions</c>), so it cannot reference a type defined in <c>ScanlineStudio.Core.Radio</c>
/// or any other module above it without inverting that layering — the two specs were never
/// cross-checked against each other on this point. Fixed here, spec updated to match: <see cref="Sections"/>
/// is a named bag of raw <see cref="JsonElement"/> blobs; each module (still following spec/12's
/// "typed section defined in that module's own project" rule) reads/writes its own named slice via
/// <see cref="AppSettingsSectionExtensions.GetSection{T}"/>/<see cref="AppSettingsSectionExtensions.WithSection{T}"/>,
/// supplying its own source-generated <c>JsonTypeInfo&lt;T&gt;</c> — <c>ScanlineStudio.Settings</c> itself never
/// needs to know any module-specific type, preserving the strict downward-only dependency rule.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Dictionary<string, JsonElement> Sections { get; init; } = new();
}
