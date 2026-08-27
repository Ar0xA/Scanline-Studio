namespace ScanlineStudio.Settings;

/// <summary>Moves the live application log file (plus any rotated backups) to a new directory
/// while the app keeps running -- unlike Config/Database, log relocation applies live, no
/// restart needed, since nothing else ever holds <c>app.log</c> open. Implemented by
/// <c>ScanlineStudio.Host.FileLoggerProvider</c> itself (the sole owner of the file's write
/// handle -- an external mover would race its own lock and its <see cref="FileShare.Read"/>-only
/// open mode); <c>NoneLogFileRelocator</c> is the DI fallback when no real provider exists (e.g.
/// the log file failed to open at startup, console-only fallback).</summary>
public interface ILogFileRelocator
{
    /// <summary>Returns <c>true</c> on success (including a no-op when the target already is the
    /// current directory, or there is nothing to move), <c>false</c> on a destination conflict or
    /// a failed move -- logging keeps working at whichever path is actually correct either
    /// way.</summary>
    Task<bool> RelocateAsync(string newDirectory, CancellationToken ct = default);
}
