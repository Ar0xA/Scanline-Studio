namespace ScanlineStudio.Settings;

/// <summary>Single source of truth for where this app's log files live -- both
/// <c>ScanlineStudio.Host.Program</c> (configures the logger to write here) and the UI's Help menu
/// "Open application log" command (opens this folder in the OS file manager) need the identical
/// path; duplicating the <see cref="Environment.SpecialFolder.LocalApplicationData"/> expression in
/// both places would let them silently drift.</summary>
public static class AppLogPaths
{
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanlineStudio", "logs");
}
