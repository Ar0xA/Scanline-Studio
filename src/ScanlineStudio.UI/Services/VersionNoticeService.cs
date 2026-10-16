using System.Reflection;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Settings;
using ScanlineStudio.UI.Settings;

namespace ScanlineStudio.UI.Services;

/// <summary>Decides whether the startup "new version" notice shows, and remembers a per-version dismissal.</summary>
public interface IVersionNoticeService
{
    /// <summary>The running version without the <c>+sha</c> build suffix, e.g. <c>0.9.2-beta</c>.</summary>
    string CurrentVersion { get; }

    /// <summary>True for a prerelease version (a <c>-…</c> suffix such as <c>-beta</c>).</summary>
    bool IsPrerelease { get; }

    /// <summary>True unless the user dismissed this exact version. A settings read failure shows the notice.</summary>
    Task<bool> ShouldShowAsync(CancellationToken ct = default);

    /// <summary>Never throws: a settings write failure is logged and the notice shows again next launch.</summary>
    Task DismissCurrentVersionAsync(CancellationToken ct = default);
}

public sealed partial class VersionNoticeService : IVersionNoticeService
{
    private const string UnknownVersion = "unknown";

    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<VersionNoticeService> _logger;

    public VersionNoticeService(ISettingsStore settingsStore, ILogger<VersionNoticeService> logger)
        : this(settingsStore, logger, ReadInformationalVersion())
    {
    }

    /// <summary>Test seam: the informational version is injectable.</summary>
    public VersionNoticeService(ISettingsStore settingsStore, ILogger<VersionNoticeService> logger, string informationalVersion)
    {
        _settingsStore = settingsStore;
        _logger = logger;
        CurrentVersion = StripBuildMetadata(informationalVersion);
    }

    public string CurrentVersion { get; }

    public bool IsPrerelease => CurrentVersion.Contains('-', StringComparison.Ordinal);

    public async Task<bool> ShouldShowAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(CurrentVersion) || CurrentVersion == UnknownVersion)
        {
            return false;
        }

        try
        {
            var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
            var section = settings.GetSection(VersionNoticeSettings.SectionKey, VersionNoticeSettingsJsonContext.Default.VersionNoticeSettings);
            return !string.Equals(section?.DismissedForVersion, CurrentVersion, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.LoadFailed(_logger, ex);
            return true;
        }
    }

    public async Task DismissCurrentVersionAsync(CancellationToken ct = default)
    {
        try
        {
            await _settingsStore.UpdateAsync(settings =>
            {
                var current = settings.GetSection(VersionNoticeSettings.SectionKey, VersionNoticeSettingsJsonContext.Default.VersionNoticeSettings) ?? new VersionNoticeSettings();
                var updated = current with { DismissedForVersion = CurrentVersion };
                return settings.WithSection(VersionNoticeSettings.SectionKey, updated, VersionNoticeSettingsJsonContext.Default.VersionNoticeSettings);
            }, ct).ConfigureAwait(false);
            Log.Dismissed(_logger, CurrentVersion);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.DismissFailed(_logger, CurrentVersion, ex);
        }
    }

    public static string StripBuildMetadata(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
    }

    // Same GetEntryAssembly-first chain as AboutWindowViewModel and MainViewModel's title.
    private static string ReadInformationalVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? UnknownVersion;
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the version-notice setting; showing the notice")]
        public static partial void LoadFailed(ILogger logger, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "Version notice dismissed for {Version}")]
        public static partial void Dismissed(ILogger logger, string version);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the version-notice dismissal for {Version}")]
        public static partial void DismissFailed(ILogger logger, string version, Exception ex);
    }
}
