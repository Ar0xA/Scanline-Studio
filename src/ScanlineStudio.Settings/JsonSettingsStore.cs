using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Settings;

public sealed partial class JsonSettingsStore : ISettingsStore, IDisposable
{
    private readonly string _settingsFilePath;
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly Subject<AppSettings> _changes = new();

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger, string? settingsFilePath = null)
    {
        _logger = logger;
        _settingsFilePath = settingsFilePath ?? GetDefaultSettingsFilePath();
    }

    public IObservable<AppSettings> Changes => _changes;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            Log.NoSettingsFile(_logger, _settingsFilePath);
            return new AppSettings();
        }

        // A corrupt/truncated settings.json (partial write from an old build predating the atomic
        // temp-file+rename below, manual editing, disk corruption) used to throw JsonException out
        // of every caller with no log trace at all -- a single bad byte bricked startup. Falls back
        // to defaults instead, now visibly logged so "why did my settings reset" is answerable.
        // Tier C audit finding: a permission-denied file (Linux: owned by root after a stray sudo
        // run; Windows: ACL/EFS) bricked startup the same way -- UnauthorizedAccessException does
        // NOT derive from IOException, so it slipped past this exact guard.
        try
        {
            await using var stream = File.OpenRead(_settingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, ct);
            return settings ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.LoadFailed(_logger, _settingsFilePath, ex);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic write (temp file + rename) so a crash mid-write never leaves settings.json
        // truncated or corrupted — see spec/12-settings.md.
        var tempFilePath = _settingsFilePath + ".tmp";
        await using (var stream = File.Create(tempFilePath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettings, ct);
        }

        File.Move(tempFilePath, _settingsFilePath, overwrite: true);
        _changes.OnNext(settings);
        Log.Saved(_logger, _settingsFilePath);
    }

    public void Dispose() => _changes.Dispose();

    private static string GetDefaultSettingsFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "ScanlineStudio", "settings.json");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "No settings file at {Path}; using defaults")]
        public static partial void NoSettingsFile(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to load settings from {Path}; falling back to defaults")]
        public static partial void LoadFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Settings saved to {Path}")]
        public static partial void Saved(ILogger logger, string path);
    }
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
