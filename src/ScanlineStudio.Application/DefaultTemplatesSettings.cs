using System.Text.Json.Serialization;

namespace ScanlineStudio.Application;

/// <summary>Records which starter templates <see cref="DefaultTemplateSeeder"/> already added or
/// skipped. Null means none yet, so a missing section seeds once (no property initializer needed).</summary>
public sealed record DefaultTemplatesSettings
{
    public const string SectionKey = "DefaultTemplates";

    public string[]? HandledNameKeys { get; init; }
}

[JsonSerializable(typeof(DefaultTemplatesSettings))]
public sealed partial class DefaultTemplatesSettingsJsonContext : JsonSerializerContext;
