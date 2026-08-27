namespace ScanlineStudio.Settings;

/// <summary>UI-facing surface for Options &gt; General's Config/Database/Log rows -- see
/// <see cref="AppLocationOverrides"/>'s own doc comment for the staged-vs-live distinction.
/// Database "Set" only stages a pending target (validated, directory created, checked for a
/// same-named conflict) -- the physical move happens once, at the next app startup, before any
/// database connection is ever opened in that process (restart-required by standing user decision:
/// SQLite's pooled native handles make a live relocation unsafe). Config and Log "Set" both apply
/// immediately, live, via <see cref="ISettingsFileRelocator"/>/<see cref="ILogFileRelocator"/>
/// respectively (restart-required-settings backlog item 3, 2026-08-27, for Config -- previously
/// staged/restart-required the same way Database still is). Every "Set" throws
/// <see cref="InvalidOperationException"/> on an unusable path or a destination conflict --
/// callers surface <c>Exception.Message</c> directly, same convention
/// <c>IReceiveHistoryStore.SetImagesDirectoryAsync</c> already established.</summary>
public interface IAppLocationsService
{
    Task<string> GetConfigDirectoryAsync(CancellationToken ct = default);

    Task SetConfigDirectoryAsync(string? directory, CancellationToken ct = default);

    Task<string> GetDatabaseDirectoryAsync(CancellationToken ct = default);

    Task<string?> GetPendingDatabaseDirectoryAsync(CancellationToken ct = default);

    Task SetDatabaseDirectoryAsync(string? directory, CancellationToken ct = default);

    Task<string> GetLogDirectoryAsync(CancellationToken ct = default);

    Task SetLogDirectoryAsync(string? directory, CancellationToken ct = default);
}
