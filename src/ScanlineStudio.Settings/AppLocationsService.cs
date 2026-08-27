using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Settings;

/// <summary>See <see cref="IAppLocationsService"/>. Needs no <c>ISettingsStore</c>/
/// <c>IReceiveHistoryStore</c> dependency for Config/Database -- both the "currently applied" and
/// "pending target" values come from <see cref="AppLocationOverrides"/> directly, via the same
/// <see cref="AppConfigPaths"/>/<see cref="AppDatabasePaths"/> helpers <c>Program.cs</c> uses. Log
/// needs <see cref="ILogFileRelocator"/>, since that row applies live rather than staging a
/// pending move.</summary>
public sealed partial class AppLocationsService : IAppLocationsService
{
    private readonly ILogFileRelocator _logFileRelocator;
    private readonly ILogger<AppLocationsService> _logger;
    private readonly string? _overridesFilePath;

    public AppLocationsService(ILogFileRelocator logFileRelocator, ILogger<AppLocationsService> logger, string? overridesFilePath = null)
    {
        _logFileRelocator = logFileRelocator;
        _logger = logger;
        _overridesFilePath = overridesFilePath;
    }

    public Task<string> GetConfigDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppConfigPaths.GetConfigDirectory(_overridesFilePath));

    public Task<string?> GetPendingConfigDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppLocationOverrides.LoadForBootstrap(_overridesFilePath).PendingConfigDirectory);

    public async Task SetConfigDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        var currentDirectory = overrides.ConfigDirectory ?? AppConfigPaths.GetDefaultConfigDirectory();
        var pending = StagePendingDirectory(currentDirectory, directory, "settings.json", AppConfigPaths.GetDefaultConfigDirectory());
        await AppLocationOverrides.SaveAsync(overrides with { PendingConfigDirectory = pending }, _overridesFilePath, ct);
        Log.ConfigDirectoryStaged(_logger, pending);
    }

    public Task<string> GetDatabaseDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppDatabasePaths.GetDatabaseDirectory(_overridesFilePath));

    public Task<string?> GetPendingDatabaseDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppLocationOverrides.LoadForBootstrap(_overridesFilePath).PendingDatabaseDirectory);

    public async Task SetDatabaseDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        var currentDirectory = overrides.DatabaseDirectory ?? AppDatabasePaths.GetDefaultDatabaseDirectory();
        var pending = StagePendingDirectory(currentDirectory, directory, "history.db", AppDatabasePaths.GetDefaultDatabaseDirectory());
        await AppLocationOverrides.SaveAsync(overrides with { PendingDatabaseDirectory = pending }, _overridesFilePath, ct);
        Log.DatabaseDirectoryStaged(_logger, pending);
    }

    public Task<string> GetLogDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppLogPaths.GetLogDirectory(_overridesFilePath));

    public async Task SetLogDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        var currentDirectory = AppLogPaths.GetLogDirectory(_overridesFilePath);
        var normalized = NormalizeTargetDirectory(directory, AppLogPaths.GetDefaultLogDirectory());

        if (DirectoryPathComparer.AreEqual(currentDirectory, normalized))
        {
            return;
        }

        var moved = await _logFileRelocator.RelocateAsync(normalized, ct);
        if (!moved)
        {
            throw new InvalidOperationException("A log file already exists in that folder, or the move failed. Choose a different folder.");
        }

        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        try
        {
            await AppLocationOverrides.SaveAsync(overrides with { LogDirectory = normalized }, _overridesFilePath, ct);
        }
        catch (Exception)
        {
            // Code-review round-1 finding: the physical move above already succeeded -- roll it
            // back so the live log file and the (unsaved) override record stay in agreement,
            // rather than leaving app.log at the new location while AppLogPaths.LogDirectory (and
            // the next launch's default-path resolution) keeps resolving the old one.
            //
            // Round-2 finding: deliberately CancellationToken.None, not `ct` -- if the save above
            // failed BECAUSE `ct` was cancelled, passing it here would make the rollback itself a
            // no-op (Task.Run's delegate never runs against an already-cancelled token) and the
            // awaited TaskCanceledException would replace the original exception, silently
            // reintroducing the exact orphaned-file state this rollback exists to prevent. A
            // rollback must run to completion regardless of why the save failed. Its own result is
            // logged, not swallowed -- a refused rollback is still a real (if rare) failure mode.
            var rolledBack = await _logFileRelocator.RelocateAsync(currentDirectory, CancellationToken.None);
            if (!rolledBack)
            {
                Log.LogDirectoryRollbackFailed(_logger, normalized, currentDirectory);
            }

            throw;
        }

        Log.LogDirectoryRelocated(_logger, normalized);
    }

    /// <summary>Validates <paramref name="requestedDirectory"/>, creates it if missing, and
    /// hard-refuses a same-named conflict at the destination -- returns the new pending target
    /// (<c>null</c> if it normalizes to the same directory the file is already in, clearing any
    /// stale pending value instead of restaging a no-op move). <paramref name="defaultDirectory"/>
    /// must be the raw OS default (<see cref="AppConfigPaths.GetDefaultConfigDirectory"/> /
    /// <see cref="AppDatabasePaths.GetDefaultDatabaseDirectory"/>), not the "effective now" value
    /// -- "reset to default" must return to that exact location regardless of where the file
    /// currently is.</summary>
    private static string? StagePendingDirectory(string currentDirectory, string? requestedDirectory, string fileName, string defaultDirectory)
    {
        var normalized = NormalizeTargetDirectory(requestedDirectory, defaultDirectory);

        if (DirectoryPathComparer.AreEqual(currentDirectory, normalized))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(normalized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Could not create or access '{normalized}'.", ex);
        }

        if (File.Exists(Path.Combine(normalized, fileName)))
        {
            throw new InvalidOperationException($"A file named '{fileName}' already exists in that folder. Choose a different folder.");
        }

        return normalized;
    }

    private static string NormalizeTargetDirectory(string? requestedDirectory, string defaultDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedDirectory))
        {
            return defaultDirectory;
        }

        try
        {
            return Path.GetFullPath(requestedDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"'{requestedDirectory}' is not a valid folder path.", ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Config directory staged: {Directory}")]
        public static partial void ConfigDirectoryStaged(ILogger logger, string? directory);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Database directory staged: {Directory}")]
        public static partial void DatabaseDirectoryStaged(ILogger logger, string? directory);

        [LoggerMessage(Level = LogLevel.Information, Message = "Log directory relocated to: {Directory}")]
        public static partial void LogDirectoryRelocated(ILogger logger, string directory);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll back the log directory relocate from {NewDirectory} back to {OldDirectory} after persisting the override failed")]
        public static partial void LogDirectoryRollbackFailed(ILogger logger, string newDirectory, string oldDirectory);
    }
}
