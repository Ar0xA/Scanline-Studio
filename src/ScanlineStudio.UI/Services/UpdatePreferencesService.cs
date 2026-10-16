using Microsoft.Extensions.Logging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.Services;

public sealed record UpdatePreferences(bool NeverNotify, string? SkippedVersion);

/// <summary>Loads and saves the update-notification preferences.</summary>
public interface IUpdatePreferencesService
{
    /// <summary>A settings read failure returns the defaults (notify, nothing skipped).</summary>
    Task<UpdatePreferences> LoadAsync(CancellationToken ct = default);

    /// <summary>Never throws: a settings write failure is logged.</summary>
    Task SaveAsync(UpdatePreferences preferences, CancellationToken ct = default);
}

public sealed partial class UpdatePreferencesService(ISettingsStore settingsStore, ILogger<UpdatePreferencesService> logger)
    : IUpdatePreferencesService
{
    public async Task<UpdatePreferences> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            var settings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var section = settings.GetSection(UpdateCheckSettings.SectionKey, UpdateCheckSettingsJsonContext.Default.UpdateCheckSettings);
            return new UpdatePreferences(section?.NeverNotify ?? UpdateCheckSettings.DefaultNeverNotify, section?.SkippedVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LoadFailed(logger, ex);
            return new UpdatePreferences(UpdateCheckSettings.DefaultNeverNotify, null);
        }
    }

    public async Task SaveAsync(UpdatePreferences preferences, CancellationToken ct = default)
    {
        try
        {
            await settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(UpdateCheckSettings.SectionKey, UpdateCheckSettingsJsonContext.Default.UpdateCheckSettings) ?? new UpdateCheckSettings();
                var updated = current with { NeverNotify = preferences.NeverNotify, SkippedVersion = preferences.SkippedVersion };
                return settings.WithSection(UpdateCheckSettings.SectionKey, updated, UpdateCheckSettingsJsonContext.Default.UpdateCheckSettings);
            }, ct).ConfigureAwait(false);
            Log.Saved(logger, preferences.NeverNotify, preferences.SkippedVersion ?? "(none)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.SaveFailed(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the update-check settings; using defaults")]
        public static partial void LoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Update-check settings saved: never notify {NeverNotify}, skipped version {SkippedVersion}")]
        public static partial void Saved(ILogger logger, bool neverNotify, string skippedVersion);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the update-check settings")]
        public static partial void SaveFailed(ILogger logger, Exception ex);
    }
}
