using System.Globalization;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Runtime.Versioning;
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
public sealed partial class JsonSettingsStore : ISettingsStore, ISettingsFileRelocator, ISettingsRecoveryNotice, IDisposable
{
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>Guarded by <see cref="_fileLock"/>. Set when the last load could not read a settings file;
    /// while set, any write is refused so defaults never replace the user's real settings. Store-wide: the
    /// next successful load by any caller clears it.</summary>
    private string? _unavailableReason;

    /// <summary>Written under <see cref="_fileLock"/> on a pool thread, taken on the UI thread: Interlocked only.</summary>
    private SettingsRecoveryNotice? _notice;
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
            // Validates (and if needed recovers) the file about to become the backup, and sets the flag.
            await LoadCoreAsync(ct).ConfigureAwait(false);
            // The caller's data was built on whatever the last load returned: defaults, if it failed.
            ThrowIfUnavailable();
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
                // A no-op mutate still succeeds while unavailable, so read-and-maybe-write callers keep working.
                ThrowIfUnavailable();
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

    public SettingsRecoveryNotice? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    private string BackupPath => _settingsFilePath + ".bak";

    private void ThrowIfUnavailable()
    {
        if (_unavailableReason is { } reason)
        {
            throw new SettingsUnavailableException(reason);
        }
    }

    private enum ReadOutcome
    {
        Ok,
        Missing,
        Corrupt,
        Unreadable,
    }

    /// <summary>Corrupt covers bad JSON, an empty or NUL-filled file (power loss before a flush), a null
    /// <c>Sections</c> and a literal <c>null</c>. Cancellation propagates.</summary>
    private static async Task<(ReadOutcome Outcome, AppSettings? Settings, Exception? Error)> TryReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return (ReadOutcome.Missing, null, null);
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                var settings = await JsonSerializer.DeserializeAsync(stream, AppSettingsJsonContext.Default.AppSettings, ct).ConfigureAwait(false);
                return settings is null
                    ? (ReadOutcome.Corrupt, null, new JsonException("The settings file contains a JSON null."))
                    : (ReadOutcome.Ok, settings, null);
            }
        }
        catch (JsonException ex)
        {
            return (ReadOutcome.Corrupt, null, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ReadOutcome.Unreadable, null, ex);
        }
    }

    /// <summary>Never throws for a bad file: startup must keep working. A damaged settings.json is moved
    /// aside and replaced by the previous save's copy (<c>settings.json.bak</c>) when that parses; an
    /// unreadable file serves defaults and blocks writes (see <see cref="_unavailableReason"/>).</summary>
    private async Task<AppSettings> LoadCoreAsync(CancellationToken ct)
    {
        var (outcome, settings, error) = await TryReadAsync(_settingsFilePath, ct).ConfigureAwait(false);
        switch (outcome)
        {
            case ReadOutcome.Ok:
                _unavailableReason = null;
                return settings!;

            case ReadOutcome.Unreadable:
                Log.LoadFailed(_logger, _settingsFilePath, error!);
                return MarkUnavailable(_settingsFilePath, $"The settings file '{_settingsFilePath}' cannot be read.", new AppSettings());

            case ReadOutcome.Missing when !File.Exists(BackupPath):
                Log.NoSettingsFile(_logger, _settingsFilePath);
                _unavailableReason = null;
                return new AppSettings();

            case ReadOutcome.Missing:
                // Left by an interrupted recovery: the backup is the newest good copy, never let a save rotate it away.
                Log.SettingsMissingBackupPresent(_logger, _settingsFilePath);
                return await RecoverFromBackupAsync(corruptOriginalInPlace: false, ct).ConfigureAwait(false);

            default:
                Log.SettingsCorrupt(_logger, _settingsFilePath, error!);
                return await RecoverFromBackupAsync(corruptOriginalInPlace: true, ct).ConfigureAwait(false);
        }
    }

    private async Task<AppSettings> RecoverFromBackupAsync(bool corruptOriginalInPlace, CancellationToken ct)
    {
        var (backupOutcome, backupSettings, backupError) = await TryReadAsync(BackupPath, ct).ConfigureAwait(false);

        // Staged before the original is touched, so a cancellation or I/O failure leaves it in place.
        string? stagedRestore = null;
        if (backupOutcome == ReadOutcome.Ok)
        {
            stagedRestore = _settingsFilePath + ".restore.tmp";
            try
            {
                await CopyOwnerOnlyAsync(BackupPath, stagedRestore, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // A partial copy can hold the QRZ password.
                TryDelete(stagedRestore);
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.RestoreFailed(_logger, _settingsFilePath, ex);
                TryDelete(stagedRestore);
                stagedRestore = null;
            }
        }

        if (corruptOriginalInPlace && !TryQuarantine(_settingsFilePath))
        {
            if (stagedRestore is not null)
            {
                TryDelete(stagedRestore);
            }

            return MarkUnavailable(
                _settingsFilePath,
                $"The damaged settings file '{_settingsFilePath}' could not be moved aside.",
                backupSettings ?? new AppSettings());
        }

        switch (backupOutcome)
        {
            case ReadOutcome.Ok when stagedRestore is not null:
                try
                {
                    File.Move(stagedRestore, _settingsFilePath, overwrite: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.RestoreFailed(_logger, _settingsFilePath, ex);
                    TryDelete(stagedRestore);
                    return MarkUnavailable(_settingsFilePath, $"The settings file '{_settingsFilePath}' could not be restored from its backup.", backupSettings!);
                }

                TrySetOwnerOnlyPermissions(_settingsFilePath);
                Log.RestoredFromBackup(_logger, _settingsFilePath);
                _unavailableReason = null;
                Interlocked.Exchange(ref _notice, new SettingsRecoveryNotice(SettingsRecoveryKind.Restored, _settingsFilePath));
                return backupSettings!;

            case ReadOutcome.Ok:
                return MarkUnavailable(_settingsFilePath, $"The settings file '{_settingsFilePath}' could not be restored from its backup.", backupSettings!);

            case ReadOutcome.Unreadable:
                Log.LoadFailed(_logger, BackupPath, backupError!);
                return MarkUnavailable(BackupPath, $"The settings backup '{BackupPath}' cannot be read.", new AppSettings());

            case ReadOutcome.Corrupt:
                Log.SettingsCorrupt(_logger, BackupPath, backupError!);
                TryQuarantine(BackupPath);
                break;
        }

        Log.ResetToDefaults(_logger, _settingsFilePath);
        _unavailableReason = null;
        Interlocked.Exchange(ref _notice, new SettingsRecoveryNotice(SettingsRecoveryKind.ResetToDefaults, _settingsFilePath));
        return new AppSettings();
    }

    private AppSettings MarkUnavailable(string path, string reason, AppSettings served)
    {
        _unavailableReason = reason;
        Interlocked.Exchange(ref _notice, new SettingsRecoveryNotice(SettingsRecoveryKind.Unavailable, path));
        return served;
    }

    /// <summary>Moves a damaged file to <c>&lt;name&gt;.corrupt-&lt;UTC&gt;</c> (never overwriting) and keeps it
    /// owner-only: it can hold a QRZ password.</summary>
    private bool TryQuarantine(string path)
    {
        try
        {
            var stem = $"{path}.corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            var target = stem;
            for (var suffix = 1; File.Exists(target); suffix++)
            {
                target = $"{stem}-{suffix}";
            }

            File.Move(path, target, overwrite: false);
            TrySetOwnerOnlyPermissions(target);
            Log.Quarantined(_logger, path, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.QuarantineFailed(_logger, path, ex);
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.TempCleanupFailed(_logger, path, ex);
        }
    }

    /// <summary>T0-8: created owner-only from the start on Unix, not a 0644 file chmod-ed later; it can
    /// hold a QRZ password.</summary>
    private static FileStream CreateOwnerOnly(string path) => OperatingSystem.IsWindows()
        ? File.Create(path)
        : File.Open(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    /// <summary>A stream copy, not File.Copy: File.Copy keeps the source's mode and cannot flush to disk.</summary>
    private static async Task CopyOwnerOnlyAsync(string source, string destination, CancellationToken ct)
    {
        var input = File.OpenRead(source);
        await using (input.ConfigureAwait(false))
        {
            var output = CreateOwnerOnly(destination);
            await using (output.ConfigureAwait(false))
            {
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
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
        var stream = CreateOwnerOnly(tempFilePath);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, settings, AppSettingsJsonContext.Default.AppSettings, ct).ConfigureAwait(false);
            // Without this a power loss after the rename can leave an empty or NUL-filled settings.json.
            stream.Flush(flushToDisk: true);
        }

        // After the new content is safely on disk, so a failed serialize costs no backup generation.
        if (File.Exists(_settingsFilePath))
        {
            await TryRotateBackupAsync(ct).ConfigureAwait(false);
        }

        File.Move(tempFilePath, _settingsFilePath, overwrite: true);
        TrySetOwnerOnlyPermissions(_settingsFilePath);
        return _settingsFilePath; // captured under the lock -- logged by the caller, after Release
    }

    /// <summary>Keeps the file about to be replaced as <c>settings.json.bak</c> (one generation). A failure
    /// only logs: the older backup stays and the save goes ahead.</summary>
    private async Task TryRotateBackupAsync(CancellationToken ct)
    {
        var stagedBackup = BackupPath + ".tmp";
        try
        {
            await CopyOwnerOnlyAsync(_settingsFilePath, stagedBackup, ct).ConfigureAwait(false);
            File.Move(stagedBackup, BackupPath, overwrite: true);
            TrySetOwnerOnlyPermissions(BackupPath);
        }
        catch (OperationCanceledException)
        {
            // A partial copy can hold the QRZ password.
            TryDelete(stagedBackup);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.BackupFailed(_logger, BackupPath, ex);
            TryDelete(stagedBackup);
        }
    }

    // T0-8: settings.json can carry a real QRZ.com account password/API key in plaintext (no
    // scoped-credential alternative exists -- see QrzLookupSettings' own doc comment). This call
    // is the belt: File.Move's rename on the same filesystem preserves the creation mode SaveCoreAsync
    // already sets above, but its documented cross-filesystem copy-fallback (RelocateAsync's own
    // call site can hit this; SaveCoreAsync's own .tmp is always same-directory, so never can)
    // doesn't guarantee that -- cheap either way.
    //
    // Windows used to return early here, on the grounds that the per-user profile directory's own
    // ACLs are already private. MEASURED FALSE on a real machine (windows_tests.md section 7): a
    // settings.json under %APPDATA% carried an inherited app-capability ACE (S-1-15-3-...) granting
    // FullControl, present on %APPDATA% itself. Relocation makes it worse -- an operator who moves
    // settings to another drive or a share inherits whatever THAT location grants, commonly
    // BUILTIN\Users. This file can hold a QRZ.com password in plaintext, so it now sets an explicit
    // DACL rather than inheriting whatever the parent happens to carry.
    //
    // Swallowed, not propagated: this throws on vfat/exfat/some network mounts, and a permission-
    // hardening failure must not turn an otherwise-successful save/relocate into a thrown exception.
    private void TrySetOwnerOnlyPermissions(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                SetWindowsOwnerOnlyAcl(path);
                return;
            }

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or PlatformNotSupportedException or InvalidOperationException)
        {
            Log.PermissionRestrictionFailed(_logger, path, ex);
        }
    }

    /// <summary>
    /// Replaces the file's inherited ACL with an explicit one: the current user, SYSTEM and
    /// Administrators, and nobody else. <c>SetAccessRuleProtection(true, false)</c> is the part that
    /// matters — it disconnects inheritance AND discards the copied parent rules, so an ACE the
    /// profile or a relocation target happens to carry does not survive.
    ///
    /// <para>SYSTEM and Administrators are kept deliberately. An administrator can take ownership of
    /// any file regardless, so excluding them would buy nothing and would break backup and repair
    /// tooling.</para>
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void SetWindowsOwnerOnlyAcl(string path)
    {
        var file = new FileInfo(path);
        var security = file.GetAccessControl();

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (FileSystemAccessRule existing in security.GetAccessRules(
            includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)))
        {
            security.RemoveAccessRuleAll(existing);
        }

        using var identity = WindowsIdentity.GetCurrent();
        foreach (var sid in new[]
        {
            identity.User,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        })
        {
            if (sid is null)
            {
                continue;
            }

            security.AddAccessRule(new FileSystemAccessRule(
                sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        file.SetAccessControl(security);
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

                // T0-8: deliberately OUTSIDE the try/catch above -- if this threw from inside that
                // catch, the file would have ALREADY moved while this method reported "nothing
                // moved," and _settingsFilePath below would never update, permanently orphaning
                // the live settings file at a path this store no longer tracks.
                // TrySetOwnerOnlyPermissions swallows its own failures, so it can't reach here.
                TrySetOwnerOnlyPermissions(newPath);
            }

            // Best effort and after settings.json moved: from here on this method must not fail.
            // Moved even when settings.json is absent, since it may be the only good copy.
            var newBackupPath = newPath + ".bak";
            if (File.Exists(BackupPath))
            {
                try
                {
                    File.Move(BackupPath, newBackupPath, overwrite: true);
                    TrySetOwnerOnlyPermissions(newBackupPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.BackupRelocateFailed(_logger, BackupPath, ex);
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

    /// <summary>Registered under 3 interfaces resolving to this same singleton instance
    /// (<see cref="ISettingsStore"/>, <see cref="ISettingsFileRelocator"/>, <see cref="ISettingsRecoveryNotice"/>) -- MS.DI disposes each
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

        [LoggerMessage(Level = LogLevel.Error, Message = "Cannot read settings file {Path}; showing defaults and refusing changes until it can be read")]
        public static partial void LoadFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Settings saved to {Path}")]
        public static partial void Saved(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not restrict permissions on {Path}")]
        public static partial void PermissionRestrictionFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "Settings file {Path} is damaged")]
        public static partial void SettingsCorrupt(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Settings file {Path} is missing but its backup exists; recovering from the backup")]
        public static partial void SettingsMissingBackupPresent(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Error, Message = "Moved damaged settings file {Path} to {QuarantinedPath}")]
        public static partial void Quarantined(ILogger logger, string path, string quarantinedPath);

        [LoggerMessage(Level = LogLevel.Error, Message = "Could not move damaged settings file {Path} aside; settings changes are refused")]
        public static partial void QuarantineFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Restored settings file {Path} from its backup; the last saved change may be lost")]
        public static partial void RestoredFromBackup(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Error, Message = "Could not restore settings file {Path} from its backup")]
        public static partial void RestoreFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Error, Message = "No usable settings backup for {Path}; using defaults")]
        public static partial void ResetToDefaults(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not update settings backup {Path}; the older backup is kept")]
        public static partial void BackupFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not move settings backup {Path} with the settings file")]
        public static partial void BackupRelocateFailed(ILogger logger, string path, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Could not delete temporary file {Path}")]
        public static partial void TempCleanupFailed(ILogger logger, string path, Exception ex);
    }
}

[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;
