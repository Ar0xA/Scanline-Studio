namespace ScanlineStudio.Settings;

/// <summary>Moves <c>settings.json</c> to a new directory while the app keeps running --
/// restart-required-settings backlog item 3 (2026-08-27). Deliberately a SEPARATE, narrow interface
/// from <see cref="ISettingsStore"/> (not a member added there) -- <see cref="ISettingsStore"/> is
/// consumed by every settings-reading component in the app as a pure load/save abstraction, and
/// widening it would force every one of those (and every test fake of it) to implement relocation
/// too. Same narrow-side-channel shape as <see cref="ILogFileRelocator"/>. Implemented by
/// <c>JsonSettingsStore</c> itself (the sole owner of the mutable settings-file path and the lock
/// that serializes it against concurrent <see cref="ISettingsStore.LoadAsync"/>/
/// <see cref="ISettingsStore.SaveAsync"/> calls) -- registered in DI as the SAME singleton instance
/// both interfaces resolve to, never a separate instance.</summary>
public interface ISettingsFileRelocator
{
    /// <summary>Moves the settings file to <paramref name="newDirectory"/> if it isn't already
    /// there. Returns <c>(true, previousDirectory)</c> on success, INCLUDING the no-op case where
    /// <paramref name="newDirectory"/> already matches the current directory (nothing is touched,
    /// but the caller still learns what the previous directory was). Returns
    /// <c>(false, previousDirectory)</c> only on a destination conflict (a settings file already
    /// exists there) or a failed physical move -- the settings file's own path is NEVER updated in
    /// that case, so a caller can safely retry or give up without any risk of the store pointing
    /// somewhere the file doesn't actually exist. Both facts come from a SINGLE internal lock
    /// acquisition -- a caller needing the previous directory as a rollback target (e.g. if a
    /// subsequent step fails) must use the value returned here, not a separate query call, which
    /// would reopen a window for another relocation to land in between the two.</summary>
    Task<(bool Moved, string PreviousDirectory)> RelocateAsync(string newDirectory, CancellationToken ct = default);
}
