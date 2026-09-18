using Microsoft.Extensions.Logging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.Services;

public sealed partial class AppearanceSettingsService(ISettingsStore settingsStore, ILogger<AppearanceSettingsService> logger) : IAppearanceSettingsService
{
    public async Task<bool> GetDecodingIndicatorBlinksAsync(CancellationToken ct = default)
    {
        var appSettings = await settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = appSettings.GetSection(AppearanceSettings.SectionKey, AppearanceSettingsJsonContext.Default.AppearanceSettings);
        return section?.DecodingIndicatorBlinks ?? AppearanceSettings.DefaultDecodingIndicatorBlinks;
    }

    public event Action<bool>? DecodingIndicatorBlinksChanged;

    /// <summary>Guarded the same way <see cref="ScanlineStudio.Application.RadioSessionService"/>'s
    /// own <c>RaiseSafetySettingsChanged</c> is -- the settings write this follows already succeeded
    /// by the time this runs, so a throwing subscriber must not make the caller's own Save look
    /// failed.</summary>
    public void NotifyDecodingIndicatorBlinksChanged(bool value)
    {
        try
        {
            DecodingIndicatorBlinksChanged?.Invoke(value);
        }
        catch (Exception ex)
        {
            Log.DecodingIndicatorBlinksChangedHandlerFailed(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "A DecodingIndicatorBlinksChanged subscriber threw -- the settings write itself already succeeded")]
        public static partial void DecodingIndicatorBlinksChangedHandlerFailed(ILogger logger, Exception exception);
    }
}
