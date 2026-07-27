using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yoniq.Settings;

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private readonly string _settingsFilePath;
    private readonly Subject<AppSettings> _changes = new();

    public JsonSettingsStore(string? settingsFilePath = null)
    {
        _settingsFilePath = settingsFilePath ?? GetDefaultSettingsFilePath();
    }

    public IObservable<AppSettings> Changes => _changes;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_settingsFilePath))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(_settingsFilePath);
        var settings = await JsonSerializer.DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, ct);
        return settings ?? new AppSettings();
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
    }

    public void Dispose() => _changes.Dispose();

    private static string GetDefaultSettingsFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "Yoniq", "settings.json");
    }
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
