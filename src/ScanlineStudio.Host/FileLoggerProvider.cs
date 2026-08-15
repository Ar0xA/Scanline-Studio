using Microsoft.Extensions.Logging;

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
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _filePath;
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

            _writer.WriteLine(line);

            if (_writer.BaseStream.Length >= _maxFileSizeBytes)
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
