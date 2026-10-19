using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace ScanlineStudio.Settings;

/// <summary>The mechanism named in <see cref="AppSettings"/>'s own doc comment: lets a module above
/// <c>ScanlineStudio.Settings</c> in the layering (e.g. <c>ScanlineStudio.Core.Radio</c>) read/write its own named
/// settings section without <c>ScanlineStudio.Settings</c> ever referencing that module's types. The caller
/// supplies its own source-generated <see cref="JsonTypeInfo{T}"/> (from its own
/// <c>JsonSerializerContext</c>), matching this codebase's existing AOT/trim-friendly convention
/// (<c>AppSettingsJsonContext</c>, <c>LocalizationJsonContext</c>) rather than falling back to
/// reflection-based (de)serialization for section content.</summary>
public static class AppSettingsSectionExtensions
{
    public static T? GetSection<T>(this AppSettings settings, string key, JsonTypeInfo<T> typeInfo)
        => settings.Sections.TryGetValue(key, out var element) ? element.Deserialize(typeInfo) : default;

    /// <summary>Suffix of the key that <see cref="SetSectionAside"/> moves an unreadable section to.</summary>
    public const string UnreadableSuffix = ".unreadable";

    /// <summary>Like <see cref="GetSection{T}"/>, but a present section that fails to deserialize returns
    /// false with the error instead of throwing. A missing section (or a JSON null) returns true with
    /// <c>default</c>. Callers that fall back to defaults on false must not then write the section back
    /// without <see cref="SetSectionAside"/>, or the user's raw text is lost.</summary>
    public static bool TryReadSection<T>(this AppSettings settings, string key, JsonTypeInfo<T> typeInfo, out T? value, out JsonException? error)
    {
        error = null;
        try
        {
            value = settings.GetSection(key, typeInfo);
            return true;
        }
        catch (JsonException ex)
        {
            value = default;
            error = ex;
            return false;
        }
    }

    /// <summary>Returns a copy with section <paramref name="key"/> moved, raw and unchanged, to
    /// <c>key + <see cref="UnreadableSuffix"/></c> (replacing any earlier copy there). An absent key
    /// returns <paramref name="settings"/> unchanged. Never mutates the input.</summary>
    public static AppSettings SetSectionAside(this AppSettings settings, string key)
    {
        if (!settings.Sections.TryGetValue(key, out var raw))
        {
            return settings;
        }

        var sections = new Dictionary<string, JsonElement>(settings.Sections)
        {
            [key + UnreadableSuffix] = raw,
        };
        sections.Remove(key);
        return settings with { Sections = sections };
    }

    public static AppSettings WithSection<T>(this AppSettings settings, string key, T value, JsonTypeInfo<T> typeInfo)
    {
        var sections = new Dictionary<string, JsonElement>(settings.Sections)
        {
            [key] = JsonSerializer.SerializeToElement(value, typeInfo),
        };
        return settings with { Sections = sections };
    }
}
