using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScanlineStudio.Settings;

/// <summary>Where the config file (<c>settings.json</c>), database file (<c>history.db</c>), and
/// log folder actually live, when the user has moved any of them away from their default
/// location via Options &gt; General. Lives at a fixed, hardcoded, <b>never itself relocatable</b>
/// path (<see cref="GetDefaultOverridesFilePath"/>) -- there is nowhere else to record "where do I
/// look for my own settings" that doesn't have the same bootstrapping problem one level up.
///
/// Database is <b>staged, not live</b> (permanently, by standing user decision -- SQLite's pooled
/// native handles make a live relocation unsafe): <c>DatabaseDirectory</c> is where the file
/// actually is right now (what this running process is using); <c>PendingDatabaseDirectory</c> is
/// where the user asked to move it to, applied once at the next startup, before any database
/// connection is ever opened in that process (see <c>Program.cs</c>'s <c>ApplyPendingRelocations</c>).
/// <c>ConfigDirectory</c> and <c>LogDirectory</c> both apply LIVE, no restart needed (Config as of
/// restart-required-settings backlog item 3, 2026-08-27 -- previously staged the same way Database
/// still is). <c>PendingConfigDirectory</c> survives ONLY as a one-time backward-compatibility path:
/// a value staged under an older, pre-item-3 build is still applied by <c>ApplyPendingRelocations</c>
/// on that one bridging restart, but nothing new ever writes to it again once
/// <c>AppLocationsService.SetConfigDirectoryAsync</c> has run.</summary>
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

        var tempFilePath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, overrides, AppLocationOverridesJsonContext.Default.AppLocationOverrides, ct);
            }

            File.Move(tempFilePath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempFilePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string GetDefaultOverridesFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "ScanlineStudio", "location-overrides.json");
    }
}

[JsonSerializable(typeof(AppLocationOverrides))]
internal sealed partial class AppLocationOverridesJsonContext : JsonSerializerContext;
