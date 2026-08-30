using System.Reactive.Subjects;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Settings;

/// <summary>Restart-required-settings backlog item 3 (2026-08-27, "Config directory" live relocation):
/// <see cref="_fileLock"/> serializes every <see cref="LoadAsync"/>/<see cref="SaveAsync"/>/
/// <see cref="RelocateAsync"/> call against each other -- without it, a relocation moving the file
/// away mid-<see cref="SaveAsync"/> could strand a write at the old, now-abandoned directory, or a
/// concurrent <see cref="LoadAsync"/> could observe a torn/missing file mid-move. It does NOT make a
/// caller's own read-then-modify-then-save sequence atomic against a DIFFERENT concurrent caller
/// doing the same -- that's what <see cref="UpdateAsync"/> (T0-2) is for: it does the whole
/// load-mutate-save sequence under one <see cref="_fileLock"/> acquisition instead of two separate
/// calls. Callers doing a read-modify-write should use <see cref="UpdateAsync"/>, not a manual
/// <see cref="LoadAsync"/>+<see cref="SaveAsync"/> pair.
///
/// Every await in this class uses <c>ConfigureAwait(false)</c> -- required, not a style preference.
/// Several call sites BLOCK the UI thread synchronously on this store's own async methods
/// (`Program.cs`'s bootstrap, `MainWindow.axaml.cs`'s constructor and Closing handler, via
/// <c>Task.Run(...).GetAwaiter().GetResult()</c>). Without <c>ConfigureAwait(false)</c>, a method
/// that started on the UI thread would hold <see cref="_fileLock"/> across an await whose
/// continuation posts back to that same (currently blocked) UI thread -- a genuine deadlock, found
/// by this feature's own round-1 plan-review before any code existed.
///
/// <see cref="LoadAsync"/>'s cancellation behavior changed by this feature, deliberately: a cancelled
/// token now fails fast via <see cref="_fileLock"/>'s own <c>WaitAsync(ct)</c>, even for the
/// fresh-install/no-file case that previously returned defaults synchronously without ever consulting
/// <paramref name="ct"/>. Accepted as more correct, not a regression to work around -- a cancelled
/// load racing an in-flight relocation should fail rather than risk observing a half-relocated
/// state. Every real caller either passes no token (the synchronous bootstrap/shutdown paths above)
/// or already wraps this store's calls in its own timeout (<c>SstvSessionService</c>'s
/// <c>WaitAsync(_cleanupTimeout, ct)</c>).</summary>
public sealed partial class JsonSettingsStore : ISettingsStore, ISettingsFileRelocator, IDisposable
{
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly ILogger<JsonSettingsStore> _logger;
    private readonly Subject<AppSettings> _changes = new();
    private string _settingsFilePath;

    public JsonSettingsStore(ILogger<JsonSettingsStore> logger, string? settingsFilePath = null)
    {
        _logger = logger;
        _settingsFilePath = settingsFilePath ?? GetDefaultSettingsFilePath();
    }

    public IObservable<AppSettings> Changes => _changes;

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        string savedPath;
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            savedPath = await SaveCoreAsync(settings, ct).ConfigureAwait(false);
        }
        finally
        {
            _fileLock.Release();
        }

        // Fired AFTER releasing the lock -- _fileLock is not reentrant, so a subscriber that calls
        // back into LoadAsync/SaveAsync from inside OnNext would otherwise self-deadlock. Changes has
        // no subscribers in this codebase today, but it is a public interface member.
        _changes.OnNext(settings);
        Log.Saved(_logger, savedPath);
    }

    /// <summary>T0-2: see <see cref="ISettingsStore.UpdateAsync"/>'s own doc comment for the full
    /// <c>mutate</c> contract. Calls the non-locking <see cref="LoadCoreAsync"/>/<see cref="SaveCoreAsync"/>
    /// cores directly, inside ONE <see cref="_fileLock"/> acquisition -- not the public
    /// <see cref="LoadAsync"/>/<see cref="SaveAsync"/>, which would each try to acquire the same
    /// non-reentrant lock again and deadlock. No-op short-circuit: if <c>mutate</c> returns the SAME
    /// reference it was given, nothing is written -- needed so a caller's own internal guard (e.g.
    /// "only save if a setting is enabled") can return its input unchanged without this method
    /// unconditionally rewriting the file/firing Changes/logging a save that didn't really happen.</summary>
    public async Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> mutate, CancellationToken ct = default)
    {
        AppSettings updated;
        string? savedPath = null;
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var current = await LoadCoreAsync(ct).ConfigureAwait(false);
            updated = mutate(current);
            if (!ReferenceEquals(updated, current))
            {
                savedPath = await SaveCoreAsync(updated, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _fileLock.Release();
        }

        // Same after-release ordering as SaveAsync, and same reasoning -- see that method.
        if (savedPath is not null)
        {
            _changes.OnNext(updated);
            Log.Saved(_logger, savedPath);
        }

        return updated;
    }

    private async Task<AppSettings> LoadCoreAsync(CancellationToken ct)
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
            var stream = File.OpenRead(_settingsFilePath);
            await using (stream.ConfigureAwait(false))
            {
                var settings = await JsonSerializer.DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, ct).ConfigureAwait(false);
                return settings ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.LoadFailed(_logger, _settingsFilePath, ex);
            return new AppSettings();
        }
    }

    private async Task<string> SaveCoreAsync(AppSettings settings, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Atomic write (temp file + rename) so a crash mid-write never leaves settings.json
        // truncated or corrupted — see spec/12-settings.md.
        var tempFilePath = _settingsFilePath + ".tmp";
        var stream = File.Create(tempFilePath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettings, ct).ConfigureAwait(false);
        }

        File.Move(tempFilePath, _settingsFilePath, overwrite: true);
        return _settingsFilePath; // captured under the lock -- logged by the caller, after Release
    }

    /// <summary>See <see cref="ISettingsFileRelocator.RelocateAsync"/> for the caller-facing
    /// contract.</summary>
    public async Task<(bool Moved, string PreviousDirectory)> RelocateAsync(string newDirectory, CancellationToken ct = default)
    {
        await _fileLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previousDirectory = Path.GetDirectoryName(_settingsFilePath);
            if (string.IsNullOrEmpty(previousDirectory))
            {
                // Unreachable via real DI (GetDefaultSettingsFilePath always returns an absolute
                // path under a real directory) -- only reachable if this store were constructed
                // directly with a bare filename, which no production or test code does today.
                throw new InvalidOperationException($"'{_settingsFilePath}' has no resolvable directory.");
            }

            if (DirectoryPathComparer.AreEqual(previousDirectory, newDirectory))
            {
                return (true, previousDirectory);
            }

            try
            {
                Directory.CreateDirectory(newDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Code-review round-1 finding: mirrors FileLoggerProvider.RelocateAsync's own
                // identical fix -- an unusable target (unwritable parent, invalid path) used to throw
                // a raw exception out of RelocateAsync instead of the clean "refuse" contract
                // ISettingsFileRelocator/IAppLocationsService.SetConfigDirectoryAsync both promise.
                // Nothing was moved yet, so returning false here is exactly as safe as the
                // destination-conflict/move-failure returns below.
                return (false, previousDirectory);
            }

            var newPath = Path.Combine(newDirectory, Path.GetFileName(_settingsFilePath));
            if (File.Exists(newPath))
            {
                return (false, previousDirectory);
            }

            if (File.Exists(_settingsFilePath))
            {
                try
                {
                    File.Move(_settingsFilePath, newPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (false, previousDirectory);
                }
            }

            // Only ever updated here, on a physically-confirmed move (or the no-op return above) --
            // never on a `false` return, so this field always points at where the file actually is.
            _settingsFilePath = newPath;
            return (true, previousDirectory);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>Registered under 2 interfaces resolving to this same singleton instance
    /// (<see cref="ISettingsStore"/>, <see cref="ISettingsFileRelocator"/>) -- MS.DI disposes each
    /// resolved service instance once per call site with no dedup, so this can run more than once.
    /// Both <see cref="Subject{T}.Dispose"/> and <see cref="SemaphoreSlim.Dispose"/> are safe to call
    /// repeatedly; any FUTURE teardown added here must keep that same idempotence.</summary>
    public void Dispose()
    {
        _changes.Dispose();
        _fileLock.Dispose();
    }

    private static string GetDefaultSettingsFilePath() => Path.Combine(AppConfigPaths.ConfigDirectory, "settings.json");

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
