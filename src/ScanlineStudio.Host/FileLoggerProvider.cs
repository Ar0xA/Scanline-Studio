using Microsoft.Extensions.Logging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Host;

/// <summary>Minimal file <see cref="ILoggerProvider"/> — appends timestamped, leveled lines to a
/// fixed path across launches (a crashed/killed prior run's lines must survive to the next launch,
/// or they're unrecoverable evidence for exactly the kind of bug this file exists to diagnose).
/// Exists alongside the console provider `Host.CreateApplicationBuilder` already registers by
/// default, since capturing this app's console output through a background shell has proven
/// unreliable in practice (a process that crashes before flushing loses everything); a file on
/// disk survives that and can be read directly at any time regardless of which shell session
/// launched the process.
///
/// Size-based rotation: once the active file reaches <see cref="_maxFileSizeBytes"/>, it's renamed
/// to <c>app.log.1</c> (shifting any existing <c>.1..N-1</c> backups up by one, dropping the oldest
/// once <see cref="_maxBackupFileCount"/> is reached) and a fresh file opened at the original path —
/// the "always the same fixed path for the live file" property the type above already documents is
/// preserved. Default level ships at Debug (see Program.cs), so an active reception can write a lot
/// of lines; without a cap this file grows unbounded across the life of an install.
///
/// Rotation is best-effort, never fatal to logging: a backup file locked by another process (e.g.
/// the user has `app.log.1` open in an editor -- a real case on Windows, where `File.Move`/`Delete`
/// fail if any process holds the file without <see cref="FileShare.Delete"/>) must not brick this
/// provider for the rest of the process's life. A failed rotation is swallowed and logging continues
/// against the still-oversized live file; only a failure to reopen the live path at all disables
/// further writes (silently, matching this type's own "must not crash the app" contract at the
/// Program.cs call site).</summary>
public sealed class FileLoggerProvider : ILoggerProvider, ILogFileRelocator
{
    private string _filePath;
    private readonly long _maxFileSizeBytes;
    private readonly int _maxBackupFileCount;
    private readonly object _lock = new();
    private StreamWriter _writer;
    private bool _disposed;

    public FileLoggerProvider(string filePath, long maxFileSizeBytes = 10 * 1024 * 1024, int maxBackupFileCount = 5)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxFileSizeBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBackupFileCount, 1);

        _filePath = filePath;
        _maxFileSizeBytes = maxFileSizeBytes;
        _maxBackupFileCount = maxBackupFileCount;

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Catches a file that was already over the cap before this rotation logic existed (or from
        // a prior run that never crossed the threshold mid-write) -- otherwise it would keep growing
        // until the next in-process rotation check, which only runs after a write. Best-effort, same
        // reasoning as WriteLine's own rotation attempt: a locked/undeletable backup must not stop
        // this provider from ever opening a writer at all.
        if (File.Exists(_filePath) && new FileInfo(_filePath).Length >= _maxFileSizeBytes)
        {
            TryRotateBackups();
        }

        _writer = OpenWriter(_filePath);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    /// <summary>Writes one already-formatted line and rotates if the file has grown past the cap.
    /// Internal, not called directly by callers outside <see cref="FileLogger"/> -- rotation needs
    /// exclusive access to swap <see cref="_writer"/>, so all writes must go through this single
    /// locked entry point rather than each <see cref="FileLogger"/> holding its own writer
    /// reference. A no-op once <see cref="Dispose"/> has run -- logging after shutdown must not
    /// throw <see cref="ObjectDisposedException"/> back into caller code (it would surface as an
    /// <see cref="AggregateException"/> from an ordinary <c>logger.LogDebug(...)</c> call).</summary>
    internal void WriteLine(string line)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            // Tier C audit finding (blocker): the write itself used to be unguarded -- AutoFlush
            // means every line is a real syscall, so a full disk, a removed/unmounted volume, or a
            // dropped network path threw IOException straight out of this method, past FileLogger.Log,
            // and into Microsoft.Extensions.Logging's own AggregateException wrapping -- an ordinary
            // `logger.LogDebug(...)` call on a decode thread, inside a catch block, or in a timer
            // callback could crash the process. This class's own doc comment already promises "must
            // not crash the app"; only the ObjectDisposedException half of that promise was actually
            // enforced (the `if (_disposed) return;` above). Same disable-and-swallow shape as the
            // reopen-after-rotation catch just below.
            long length;
            try
            {
                _writer.WriteLine(line);
                length = _writer.BaseStream.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                _disposed = true;
                return;
            }

            if (length >= _maxFileSizeBytes)
            {
                _writer.Dispose();
                TryRotateBackups();

                try
                {
                    _writer = OpenWriter(_filePath);
                }
                catch (IOException)
                {
                    _disposed = true;
                }
                catch (UnauthorizedAccessException)
                {
                    _disposed = true;
                }
            }
        }
    }

    /// <summary>See <see cref="ILogFileRelocator.RelocateAsync"/>. The actual file I/O runs on a
    /// background thread (a multi-MB log plus up to 5 backups is real I/O -- must never block the
    /// UI thread Apply is clicked from), but the whole dispose-move-reopen sequence happens under
    /// <see cref="_lock"/>, same as <see cref="WriteLine"/>'s own rotation. Nothing in the locked
    /// section may log or call anything that could re-enter <see cref="WriteLine"/> -- that
    /// method's own <see cref="ObjectDisposedException"/> catch would otherwise misfire on a
    /// reentrant call while the writer is mid-swap and permanently disable file logging.</summary>
    public Task<bool> RelocateAsync(string newDirectory, CancellationToken ct = default) =>
        Task.Run(() => RelocateCore(newDirectory), ct);

    private bool RelocateCore(string newDirectory)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // Nothing left to relocate once shutting down -- not a failure.
                return true;
            }

            if (DirectoryPathComparer.AreEqual(Path.GetDirectoryName(_filePath)!, newDirectory))
            {
                return true;
            }

            var sourceFiles = CollectExistingLogFiles();

            try
            {
                Directory.CreateDirectory(newDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Code-review round-1 finding: an unusable target (unwritable parent, invalid
                // path) used to throw a raw exception out of RelocateAsync instead of the clean
                // "refuse" IAppLocationsService.SetLogDirectoryAsync's contract promises -- the
                // writer is still untouched at this point, same as a destination conflict below.
                return false;
            }

            // All-or-nothing conflict check, before the first move -- a partial conflict check
            // (checking as we go) could move some files before discovering a later one can't move,
            // leaving a half-relocated set behind for no reason. The live file's own destination
            // is always checked, even when the live file no longer exists at the source (round-2
            // finding): OpenWriter below always reopens at this exact destination regardless, so a
            // foreign file already sitting there must still refuse the relocate, not get silently
            // appended into.
            var liveDestination = Path.Combine(newDirectory, Path.GetFileName(_filePath));
            if (File.Exists(liveDestination))
            {
                return false;
            }

            foreach (var source in sourceFiles)
            {
                var destination = Path.Combine(newDirectory, Path.GetFileName(source));
                if (File.Exists(destination))
                {
                    return false;
                }
            }

            _writer.Dispose();

            var newFilePath = Path.Combine(newDirectory, Path.GetFileName(_filePath));
            var moved = TryMoveAll(sourceFiles, newDirectory);
            if (moved)
            {
                _filePath = newFilePath;
            }

            // Reopen at whichever path is now correct -- the new one on success, the old one
            // (files rolled back there by TryMoveAll) on failure -- so a failed relocate never
            // leaves logging permanently dead. Only a failure to reopen at all disables further
            // writes, matching WriteLine's own existing failure contract.
            try
            {
                _writer = OpenWriter(_filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disposed = true;
            }

            return moved;
        }
    }

    private List<string> CollectExistingLogFiles()
    {
        // Code-review round-1 finding: the live file must be conflict/move-checked only when it
        // actually still exists -- an externally deleted app.log (a tmp cleaner, logrotate) used
        // to always be included here, so RelocateAsync's own File.Move for it threw
        // FileNotFoundException, failing the whole relocate with a misleading "already exists in
        // that folder" message even though nothing was actually in conflict.
        var files = new List<string>();
        if (File.Exists(_filePath))
        {
            files.Add(_filePath);
        }

        for (var i = 1; i <= _maxBackupFileCount; i++)
        {
            var backup = $"{_filePath}.{i}";
            if (File.Exists(backup))
            {
                files.Add(backup);
            }
        }

        return files;
    }

    /// <summary>Moves every file in <paramref name="sourceFiles"/> into <paramref name="newDirectory"/>,
    /// rolling back whatever already moved if any individual move fails partway -- these are
    /// independent files with no atomic multi-file move primitive, so a failed relocate must leave
    /// them exactly where they started rather than half-migrated (a retry, or the caller's
    /// reopen-at-old-path fallback, would otherwise have to reason about a mixed state).</summary>
    private static bool TryMoveAll(IReadOnlyList<string> sourceFiles, string newDirectory)
    {
        var completed = new List<(string Source, string Destination)>();
        foreach (var source in sourceFiles)
        {
            var destination = Path.Combine(newDirectory, Path.GetFileName(source));
            try
            {
                File.Move(source, destination);
                completed.Add((source, destination));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File.Move's cross-volume copy+delete fallback can leave a partial destination
                // file if the copy step itself fails partway -- clean that up before rolling the
                // rest back, so a retry isn't blocked by a false conflict against our own debris.
                TryDeleteIfExists(destination);
                RollBack(completed);
                return false;
            }
        }

        return true;
    }

    private static void RollBack(IReadOnlyList<(string Source, string Destination)> completed)
    {
        foreach (var (source, destination) in completed)
        {
            try
            {
                if (File.Exists(destination))
                {
                    File.Move(destination, source);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort -- if even the rollback fails, the live file (always sourceFiles[0])
                // making it back is what matters for logging to keep working; a stranded backup at
                // the new location is a cosmetic loss, not a functional one.
            }
        }
    }

    private static void TryDeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static StreamWriter OpenWriter(string filePath) =>
        new(new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read)) { AutoFlush = true };

    /// <summary>Shifts <c>app.log.1..N-1</c> up to <c>.2..N</c> (dropping whatever was already at
    /// <c>.N</c>), then moves the live file to <c>.1</c>. Assumes the live file is not held open by
    /// this provider's own <see cref="_writer"/> when called (constructor: never opened yet;
    /// <see cref="WriteLine"/>: disposed just before this call) -- a self-held handle would make the
    /// final move fail on Windows. Swallows <see cref="IOException"/>/
    /// <see cref="UnauthorizedAccessException"/> from any individual delete/move (e.g. a backup file
    /// open in another process) -- see the type-level doc comment for why a failed rotation must not
    /// be fatal. A partial shift left behind by a mid-sequence failure is harmless: the next
    /// successful rotation re-normalizes it, and nothing here depends on every backup slot being
    /// exactly one rotation apart.</summary>
    private void TryRotateBackups()
    {
        try
        {
            var oldest = $"{_filePath}.{_maxBackupFileCount}";
            if (File.Exists(oldest))
            {
                File.Delete(oldest);
            }

            for (var i = _maxBackupFileCount - 1; i >= 1; i--)
            {
                var source = $"{_filePath}.{i}";
                if (File.Exists(source))
                {
                    File.Move(source, $"{_filePath}.{i + 1}");
                }
            }

            if (File.Exists(_filePath))
            {
                File.Move(_filePath, $"{_filePath}.1");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }
}

internal sealed class FileLogger(string categoryName, FileLoggerProvider provider) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss.fff} [{logLevel,-11}] {categoryName}: {formatter(state, exception)}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        provider.WriteLine(line);
    }
}
