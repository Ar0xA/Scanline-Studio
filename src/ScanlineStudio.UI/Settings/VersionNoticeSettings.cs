using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

/// <summary>Startup "new version" notice. Null <see cref="DismissedForVersion"/> means never
/// dismissed, so a missing section shows the notice (no property initializer needed).</summary>
public sealed record VersionNoticeSettings
{
    public const string SectionKey = "VersionNotice";

    /// <summary>The version (without the <c>+sha</c> build suffix) the user ticked "Don't show again" for.</summary>
    public string? DismissedForVersion { get; init; }
}

[JsonSerializable(typeof(VersionNoticeSettings))]
public sealed partial class VersionNoticeSettingsJsonContext : JsonSerializerContext;
