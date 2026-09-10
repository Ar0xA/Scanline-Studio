namespace ScanlineStudio.Settings.Tests;

/// <summary>
/// `BACKLOG.md` W7, filesystem half. `DirectoryPathComparer` uses OrdinalIgnoreCase on Windows and
/// macOS but Ordinal on Linux, and the case-insensitive branch has never run against a genuinely
/// case-insensitive filesystem.
///
/// <para><b>Device cost: none.</b> Filesystem only. The serial-port half of W7 lives in
/// <c>ScanlineStudio.Core.Radio.Tests</c>, where the port enumerator is referenced.</para>
/// </summary>
public sealed class WindowsDirectoryPathComparerTests
{
    [WindowsFact]
    public void DirectoryPathComparer_TreatsCaseAsEqual_AndTheFilesystemAgrees()
    {
        // Two assertions in one test on purpose. The comparer claiming case-insensitivity is only
        // correct if the filesystem underneath actually is, and asserting the comparer alone would
        // pass just as well on a case-sensitive volume where it would be WRONG. NTFS can be
        // configured per-directory to be case-sensitive, so this is a real possibility, not a
        // hypothetical.
        var directory = Path.Combine(Path.GetTempPath(), $"ScanlineStudio-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var written = Path.Combine(directory, "Settings.json");
            File.WriteAllText(written, "{}");

            var differentCase = Path.Combine(directory, "SETTINGS.JSON");

            Assert.True(
                File.Exists(differentCase),
                "this volume is case-SENSITIVE, so DirectoryPathComparer's OrdinalIgnoreCase branch "
                + "would treat two distinct files as one. NTFS supports per-directory case "
                + "sensitivity, so this is a real configuration, not a hypothetical.");

            Assert.True(
                DirectoryPathComparer.AreEqual(directory.ToUpperInvariant(), directory.ToLowerInvariant()),
                "DirectoryPathComparer distinguished two spellings of one directory on a "
                + "case-insensitive volume.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
