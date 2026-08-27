namespace ScanlineStudio.Settings;

/// <summary>Where <c>history.db</c>'s directory actually is, once <see cref="AppLocationOverrides"/>
/// is taken into account -- <c>SqliteReceiveHistoryStore</c>/<c>SqliteLogbookRepository</c>'s own
/// default-path resolution consults this instead of the raw
/// <see cref="Environment.SpecialFolder.ApplicationData"/> expression directly, same "single
/// source of truth" reasoning as <see cref="AppLogPaths"/>.</summary>
public static class AppDatabasePaths
{
    public static string DatabaseDirectory => GetDatabaseDirectory();

    /// <summary>Real, testable entry point -- <paramref name="overridesFilePath"/> lets a test
    /// point <see cref="AppLocationOverrides.LoadForBootstrap"/> at a throwaway file instead of
    /// this machine's real one. <see cref="DatabaseDirectory"/> itself always passes
    /// <c>null</c> (the real fixed overrides path), so every existing call site keeps working
    /// unchanged.</summary>
    public static string GetDatabaseDirectory(string? overridesFilePath = null) =>
        AppLocationOverrides.LoadForBootstrap(overridesFilePath).DatabaseDirectory ?? GetDefaultDatabaseDirectory();

    /// <summary>The raw OS-folder-based default, ignoring any current override -- used by "reset
    /// to default" flows, which must return to this exact location regardless of where the file
    /// currently is, not <see cref="GetDatabaseDirectory"/>'s "effective now" value.</summary>
    public static string GetDefaultDatabaseDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScanlineStudio");
}
