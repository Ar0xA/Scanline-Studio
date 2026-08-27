using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed class DirectoryPathComparerTests
{
    [Fact]
    public void AreEqual_SamePathDifferentTrailingSeparator_ReturnsTrue()
    {
        var a = Path.Combine(Path.GetTempPath(), "same-dir");
        var b = a + Path.DirectorySeparatorChar;

        Assert.True(DirectoryPathComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_DifferentDirectories_ReturnsFalse()
    {
        var a = Path.Combine(Path.GetTempPath(), "dir-a");
        var b = Path.Combine(Path.GetTempPath(), "dir-b");

        Assert.False(DirectoryPathComparer.AreEqual(a, b));
    }

    [Fact]
    public void AreEqual_RelativeVsAbsoluteResolvingToSamePath_ReturnsTrue()
    {
        var absolute = Path.Combine(Directory.GetCurrentDirectory(), "some-dir");

        Assert.True(DirectoryPathComparer.AreEqual(absolute, "some-dir"));
    }

    [Fact]
    public void AreEqual_SamePathDifferentCasing_MatchesThisPlatformsOwnCaseSensitivity()
    {
        // The type's only platform-conditional behavior: OrdinalIgnoreCase on Windows/macOS
        // (case-insensitive filesystems), Ordinal on Linux (case-sensitive).
        var a = Path.Combine(Path.GetTempPath(), "Same-Dir");
        var b = Path.Combine(Path.GetTempPath(), "same-dir");

        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(expected, DirectoryPathComparer.AreEqual(a, b));
    }
}
