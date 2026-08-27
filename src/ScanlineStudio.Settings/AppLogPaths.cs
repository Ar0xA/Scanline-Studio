namespace ScanlineStudio.Settings;

/// <summary>Single source of truth for where this app's log files live -- both
/// <c>ScanlineStudio.Host.Program</c> (configures the logger to write here) and the UI's Help menu
/// "Open application log" command (opens this folder in the OS file manager) need the identical
/// path; duplicating the <see cref="Environment.SpecialFolder.LocalApplicationData"/> expression in
/// both places would let them silently drift. Consults <see cref="AppLocationOverrides"/> on every
/// read (no caching) -- unlike <see cref="AppConfigPaths"/>/<see cref="AppDatabasePaths"/>, log
/// relocation applies live, so this must reflect a just-relocated directory immediately for
/// Help &gt; Open application log, not just at process startup.</summary>
public static class AppLogPaths
{
    public static string LogDirectory => GetLogDirectory();

    /// <summary>Real, testable entry point -- <paramref name="overridesFilePath"/> lets a test
    /// point <see cref="AppLocationOverrides.LoadForBootstrap"/> at a throwaway file instead of
    /// this machine's real one. <see cref="LogDirectory"/> itself always passes <c>null</c> (the
    /// real fixed overrides path), so every existing call site keeps working unchanged.</summary>
    public static string GetLogDirectory(string? overridesFilePath = null) =>
        AppLocationOverrides.LoadForBootstrap(overridesFilePath).LogDirectory ?? GetDefaultLogDirectory();

    /// <summary>The raw OS-folder-based default, ignoring any current override -- used by "reset
    /// to default" flows, which must return to this exact location regardless of where the file
    /// currently is, not <see cref="GetLogDirectory"/>'s "effective now" value.</summary>
    public static string GetDefaultLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanlineStudio", "logs");
}
