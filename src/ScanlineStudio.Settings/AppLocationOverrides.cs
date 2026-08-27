using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScanlineStudio.Settings;

/// <summary>Where the config file (<c>settings.json</c>), database file (<c>history.db</c>), and
/// log folder actually live, when the user has moved any of them away from their default
/// location via Options &gt; General. Lives at a fixed, hardcoded, <b>never itself relocatable</b>
/// path (<see cref="GetDefaultOverridesFilePath"/>) -- there is nowhere else to record "where do I
/// look for my own settings" that doesn't have the same bootstrapping problem one level up.
///
/// Config/Database are <b>staged, not live</b>: <c>ConfigDirectory</c>/<c>DatabaseDirectory</c> is
/// where the file actually is right now (what this running process is using); the matching
/// <c>Pending*Directory</c> is where the user asked to move it to, applied once at the next
/// startup, before any settings/database connection is ever opened in that process (see
/// <c>Program.cs</c>'s <c>ApplyPendingRelocations</c>). <c>LogDirectory</c> has no pending
/// counterpart -- log relocation applies live, no restart needed.</summary>
public sealed record AppLocationOverrides(
    string? ConfigDirectory = null,
    string? PendingConfigDirectory = null,
    string? DatabaseDirectory = null,
    string? PendingDatabaseDirectory = null,
    string? LogDirectory = null)
{
    public static AppLocationOverrides Empty { get; } = new();

    /// <summary>Synchronous and exception-swallowing on purpose: called from <c>Program.cs</c>
    /// before any DI container, logger, or async infrastructure exists. Must never throw --
    /// swallows every exception type, not just <see cref="JsonException"/>
    /// (<see cref="UnauthorizedAccessException"/> does not derive from <see cref="IOException"/>,
    /// the exact miss <c>JsonSettingsStore</c>'s own history already documents once) -- a
    /// missing/corrupt/permission-denied overrides file must fall back to "no overrides", never
    /// brick startup.</summary>
    public static AppLocationOverrides LoadForBootstrap(string? overridesFilePath = null)
    {
        var path = overridesFilePath ?? GetDefaultOverridesFilePath();
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var overrides = JsonSerializer.Deserialize(stream, AppLocationOverridesJsonContext.Default.AppLocationOverrides);
            return overrides ?? Empty;
        }
        catch (Exception)
        {
            return Empty;
        }
    }

    /// <summary>Atomic temp-file+rename, same pattern as <see cref="JsonSettingsStore.SaveAsync"/>
    /// -- a crash mid-write must never leave this file truncated (a corrupt overrides file falls
    /// back to defaults via <see cref="LoadForBootstrap"/>, but a truncated one read successfully
    /// as valid-but-wrong JSON would not).</summary>
    public static async Task SaveAsync(AppLocationOverrides overrides, string? overridesFilePath = null, CancellationToken ct = default)
    {
        var path = overridesFilePath ?? GetDefaultOverridesFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFilePath = path + ".tmp";
        await using (var stream = File.Create(tempFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, overrides, AppLocationOverridesJsonContext.Default.AppLocationOverrides, ct);
        }

        File.Move(tempFilePath, path, overwrite: true);
    }

    private static string GetDefaultOverridesFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "ScanlineStudio", "location-overrides.json");
    }
}

[JsonSerializable(typeof(AppLocationOverrides))]
internal sealed partial class AppLocationOverridesJsonContext : JsonSerializerContext;
