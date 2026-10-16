using System.Text.Json.Serialization;

namespace ScanlineStudio.UI.Settings;

/// <summary>Update-notification preferences. Read with <c>?? Default*</c> at every read site.</summary>
public sealed record UpdateCheckSettings
{
    public const string SectionKey = "UpdateCheck";

    /// <summary>True: no update notification and no GitHub request at startup. Help → Check for updates still works.</summary>
    public bool? NeverNotify { get; init; }

    public const bool DefaultNeverNotify = false;

    /// <summary>A version (normalized, no <c>v</c> prefix) the user chose not to be told about again.</summary>
    public string? SkippedVersion { get; init; }
}

[JsonSerializable(typeof(UpdateCheckSettings))]
public sealed partial class UpdateCheckSettingsJsonContext : JsonSerializerContext;
