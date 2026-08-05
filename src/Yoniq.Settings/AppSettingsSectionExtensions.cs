using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Yoniq.Settings;

/// <summary>The mechanism named in <see cref="AppSettings"/>'s own doc comment: lets a module above
/// <c>Yoniq.Settings</c> in the layering (e.g. <c>Yoniq.Core.Radio</c>) read/write its own named
/// settings section without <c>Yoniq.Settings</c> ever referencing that module's types. The caller
/// supplies its own source-generated <see cref="JsonTypeInfo{T}"/> (from its own
/// <c>JsonSerializerContext</c>), matching this codebase's existing AOT/trim-friendly convention
/// (<c>AppSettingsJsonContext</c>, <c>LocalizationJsonContext</c>) rather than falling back to
/// reflection-based (de)serialization for section content.</summary>
public static class AppSettingsSectionExtensions
{
    public static T? GetSection<T>(this AppSettings settings, string key, JsonTypeInfo<T> typeInfo)
        => settings.Sections.TryGetValue(key, out var element) ? element.Deserialize(typeInfo) : default;

    public static AppSettings WithSection<T>(this AppSettings settings, string key, T value, JsonTypeInfo<T> typeInfo)
    {
        var sections = new Dictionary<string, JsonElement>(settings.Sections)
        {
            [key] = JsonSerializer.SerializeToElement(value, typeInfo),
        };
        return settings with { Sections = sections };
    }
}
