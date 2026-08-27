namespace ScanlineStudio.Settings;

/// <summary>Shared "is this the same directory" comparison for every relocatable location
/// (Config/Database/Log) -- normalizes via <see cref="Path.GetFullPath"/> +
/// <see cref="Path.TrimEndingDirectorySeparator(string)"/>, then compares
/// <see cref="StringComparison.OrdinalIgnoreCase"/> on Windows/macOS (case-insensitive
/// filesystems) or <see cref="StringComparison.Ordinal"/> on Linux (case-sensitive), matching
/// .NET's own platform path-comparison convention. Symlink/bind-mount aliasing across
/// different-looking paths is explicitly out of scope -- two paths that only resolve to the same
/// location via a symlink are treated as different directories.</summary>
public static class DirectoryPathComparer
{
    public static bool AreEqual(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), Comparison);

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
