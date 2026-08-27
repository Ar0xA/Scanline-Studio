namespace ScanlineStudio.Settings;

/// <summary>UI-facing surface for Options &gt; General's Config/Database/Log rows -- see
/// <see cref="AppLocationOverrides"/>'s own doc comment for the staged-vs-live distinction.
/// Config/Database "Set" only stages a pending target (validated, directory created, checked for
/// a same-named conflict) -- the physical move happens once, at the next app startup, before any
/// settings/database connection is ever opened in that process. Log "Set" applies immediately,
/// live, via <see cref="ILogFileRelocator"/>. Every "Set" throws <see cref="InvalidOperationException"/>
/// on an unusable path or a destination conflict -- callers surface <c>Exception.Message</c>
/// directly, same convention <c>IReceiveHistoryStore.SetImagesDirectoryAsync</c> already
/// established.</summary>
public interface IAppLocationsService
{
    Task<string> GetConfigDirectoryAsync(CancellationToken ct = default);

    Task<string?> GetPendingConfigDirectoryAsync(CancellationToken ct = default);

    Task SetConfigDirectoryAsync(string? directory, CancellationToken ct = default);

    Task<string> GetDatabaseDirectoryAsync(CancellationToken ct = default);

    Task<string?> GetPendingDatabaseDirectoryAsync(CancellationToken ct = default);

    Task SetDatabaseDirectoryAsync(string? directory, CancellationToken ct = default);

    Task<string> GetLogDirectoryAsync(CancellationToken ct = default);

    Task SetLogDirectoryAsync(string? directory, CancellationToken ct = default);
}
