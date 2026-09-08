using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Settings;

/// <summary>See <see cref="IAppLocationsService"/>. Needs no <c>IReceiveHistoryStore</c> dependency
/// for Database -- both its "currently applied" and "pending target" values come from
/// <see cref="AppLocationOverrides"/> directly, via <see cref="AppDatabasePaths"/>, the same as
/// <c>Program.cs</c> uses. Config and Log both need a live relocator instead
/// (<see cref="ISettingsFileRelocator"/>/<see cref="ILogFileRelocator"/>), since both rows apply
/// immediately rather than staging a pending move.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The singleton's async-only semaphore never creates a wait handle; retain it so admitted relocation/rollback tasks can finish during shutdown.")]
public sealed partial class AppLocationsService : IAppLocationsService
{
    private readonly ISettingsFileRelocator _settingsFileRelocator;
    private readonly ILogFileRelocator _logFileRelocator;
    private readonly ILogger<AppLocationsService> _logger;
    private readonly string? _overridesFilePath;
    private readonly SemaphoreSlim _updateGate = new(1, 1);

    private async Task UpdateLocationAsync(Func<Task> update, CancellationToken ct)
    {
        await _updateGate.WaitAsync(ct).ConfigureAwait(false);
        try { await update().ConfigureAwait(false); }
        finally { _updateGate.Release(); }
    }

    public AppLocationsService(ISettingsFileRelocator settingsFileRelocator, ILogFileRelocator logFileRelocator, ILogger<AppLocationsService> logger, string? overridesFilePath = null)
    {
        _settingsFileRelocator = settingsFileRelocator;
        _logFileRelocator = logFileRelocator;
        _logger = logger;
        _overridesFilePath = overridesFilePath;
    }

    public Task<string> GetConfigDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppConfigPaths.GetConfigDirectory(_overridesFilePath));

    /// <summary>Code-review finding: a process kill between the physical move committing and the
    /// override-file write below (not a THROWN failure -- the rollback below only covers that case)
    /// strands settings.json at <c>normalized</c> while the next launch's bootstrap still resolves
    /// the old directory -- no reconciliation exists for that specific window, on either ordering of
    /// the two steps. Accepted, not fixed: the window is narrow (one file move plus one small JSON
    /// write, not a long-running operation) and this is the exact same shape
    /// <see cref="SetLogDirectoryAsync"/> already has, unremediated, for <c>app.log</c>.</summary>
    public Task SetConfigDirectoryAsync(string? directory, CancellationToken ct = default) =>
        UpdateLocationAsync(() => SetConfigDirectoryCoreAsync(directory, ct), ct);

    private async Task SetConfigDirectoryCoreAsync(string? directory, CancellationToken ct)
    {
        var normalized = NormalizeTargetDirectory(directory, AppConfigPaths.GetDefaultConfigDirectory());

        var (moved, previousDirectory) = await _settingsFileRelocator.RelocateAsync(normalized, ct);
        if (!moved)
        {
            throw new InvalidOperationException($"Could not move settings to '{normalized}': a settings file already exists there, or the move failed. Choose a different folder.");
        }

        // ALWAYS persisted, even when RelocateAsync's own no-op guard found nothing to move (round-3
        // plan-review risk-1) -- self-heals a prior store/overrides divergence (e.g. a previous
        // rollback that failed to re-save the override record after its physical move succeeded)
        // instead of silently preserving it, and unconditionally clears a stale PendingConfigDirectory
        // (whether left over from a retry-pending failure, or from an older, pre-this-feature build
        // that staged a move and hasn't restarted since upgrading -- Program.cs's own bootstrap-time
        // migration code still applies that ONE leftover value on the bridging restart, but nothing
        // new ever writes to it again after this ships).
        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        try
        {
            await AppLocationOverrides.SaveAsync(overrides with { ConfigDirectory = normalized, PendingConfigDirectory = null }, _overridesFilePath, ct);
        }
        catch (Exception)
        {
            // Round-2 finding: deliberately CancellationToken.None, not `ct` -- if the save above
            // failed BECAUSE `ct` was cancelled, passing it here would make the rollback itself a
            // no-op and silently strand the file at `normalized` while the override record (and every
            // future launch's default-path resolution) still points at `previousDirectory`. A
            // rollback must run to completion regardless of why the save failed.
            var (rolledBack, _) = await _settingsFileRelocator.RelocateAsync(previousDirectory, CancellationToken.None);
            if (!rolledBack)
            {
                // User-data-visible, unlike Log's equivalent failure (that one is cosmetic -- app.log
                // just keeps writing wherever it actually is). Here the RUNNING process keeps reading/
                // writing settings.json at `normalized` while the NEXT launch would look for it at
                // `previousDirectory` -- the classic silent "my settings reset" report. Surfaced with
                // its own distinct message rather than folded into the generic move-failed one above.
                Log.ConfigDirectoryRollbackFailed(_logger, normalized, previousDirectory);
                throw new InvalidOperationException($"Settings were moved to '{normalized}' but the change could not be recorded, and rolling back to '{previousDirectory}' also failed. Your settings may not be found on the next launch -- check both folders.");
            }

            throw;
        }

        Log.ConfigDirectoryRelocated(_logger, normalized);
    }

    public Task<string> GetDatabaseDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppDatabasePaths.GetDatabaseDirectory(_overridesFilePath));

    public Task<string?> GetPendingDatabaseDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppLocationOverrides.LoadForBootstrap(_overridesFilePath).PendingDatabaseDirectory);

    public Task SetDatabaseDirectoryAsync(string? directory, CancellationToken ct = default) =>
        UpdateLocationAsync(() => SetDatabaseDirectoryCoreAsync(directory, ct), ct);

    private async Task SetDatabaseDirectoryCoreAsync(string? directory, CancellationToken ct)
    {
        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        var currentDirectory = overrides.DatabaseDirectory ?? AppDatabasePaths.GetDefaultDatabaseDirectory();
        var pending = StagePendingDirectory(currentDirectory, directory, "history.db", AppDatabasePaths.GetDefaultDatabaseDirectory());
        await AppLocationOverrides.SaveAsync(overrides with { PendingDatabaseDirectory = pending }, _overridesFilePath, ct);
        Log.DatabaseDirectoryStaged(_logger, pending);
    }

    public Task<string> GetLogDirectoryAsync(CancellationToken ct = default) =>
        Task.FromResult(AppLogPaths.GetLogDirectory(_overridesFilePath));

    public Task SetLogDirectoryAsync(string? directory, CancellationToken ct = default) =>
        UpdateLocationAsync(() => SetLogDirectoryCoreAsync(directory, ct), ct);

    private async Task SetLogDirectoryCoreAsync(string? directory, CancellationToken ct)
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
        [LoggerMessage(Level = LogLevel.Information, Message = "Config directory relocated to: {Directory}")]
        public static partial void ConfigDirectoryRelocated(ILogger logger, string directory);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll back the config directory relocate from {NewDirectory} back to {OldDirectory} after persisting the override failed")]
        public static partial void ConfigDirectoryRollbackFailed(ILogger logger, string newDirectory, string oldDirectory);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Database directory staged: {Directory}")]
        public static partial void DatabaseDirectoryStaged(ILogger logger, string? directory);

        [LoggerMessage(Level = LogLevel.Information, Message = "Log directory relocated to: {Directory}")]
        public static partial void LogDirectoryRelocated(ILogger logger, string directory);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to roll back the log directory relocate from {NewDirectory} back to {OldDirectory} after persisting the override failed")]
        public static partial void LogDirectoryRollbackFailed(ILogger logger, string newDirectory, string oldDirectory);
    }
}
