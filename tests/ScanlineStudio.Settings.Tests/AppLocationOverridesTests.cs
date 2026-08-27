using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed class AppLocationOverridesTests : IDisposable
{
    private readonly string _overridesFilePath = Path.Combine(
        Directory.CreateTempSubdirectory("yoniq-location-overrides-tests-").FullName, "location-overrides.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_overridesFilePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadForBootstrap_WhenFileDoesNotExist_ReturnsEmpty()
    {
        var loaded = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);

        Assert.Equal(AppLocationOverrides.Empty, loaded);
    }

    [Fact]
    public void LoadForBootstrap_WhenFileIsCorruptJson_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_overridesFilePath)!);
        File.WriteAllText(_overridesFilePath, "{ not valid json ");

        var loaded = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);

        Assert.Equal(AppLocationOverrides.Empty, loaded);
    }

    [Fact]
    public void LoadForBootstrap_WhenFileIsUnreadable_ReturnsEmptyRatherThanThrowing()
    {
        // Tier C audit lesson (JsonSettingsStore.cs's own history): UnauthorizedAccessException
        // does NOT derive from IOException, so a catch scoped too narrowly would let this one
        // slip past and brick startup. Unix-only (File.SetUnixFileMode throws
        // PlatformNotSupportedException on Windows, where ACL-based denial isn't this simple to
        // set up in a test) -- the broad `catch (Exception)` this asserts on is platform-agnostic,
        // this is just the cheapest real repro available on this dev/CI platform.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_overridesFilePath)!);
        File.WriteAllText(_overridesFilePath, "{}");
        File.SetUnixFileMode(_overridesFilePath, UnixFileMode.None);

        try
        {
            var loaded = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);

            Assert.Equal(AppLocationOverrides.Empty, loaded);
        }
        finally
        {
            File.SetUnixFileMode(_overridesFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAllFields()
    {
        var saved = new AppLocationOverrides(
            ConfigDirectory: "/config/dir",
            PendingConfigDirectory: "/config/pending",
            DatabaseDirectory: "/db/dir",
            PendingDatabaseDirectory: "/db/pending",
            LogDirectory: "/log/dir");

        await AppLocationOverrides.SaveAsync(saved, _overridesFilePath);
        var loaded = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);

        Assert.Equal(saved, loaded);
    }

    [Fact]
    public async Task SaveAsync_IsAtomic_NoTempFileLeftBehindAfterSuccess()
    {
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(ConfigDirectory: "/x"), _overridesFilePath);

        Assert.True(File.Exists(_overridesFilePath));
        Assert.False(File.Exists(_overridesFilePath + ".tmp"));
    }

    [Fact]
    public async Task SaveAsync_CreatesDirectoryIfMissing()
    {
        var nestedPath = Path.Combine(
            Directory.CreateTempSubdirectory("yoniq-location-overrides-tests-").FullName, "nested", "location-overrides.json");
        try
        {
            await AppLocationOverrides.SaveAsync(new AppLocationOverrides(LogDirectory: "/log"), nestedPath);

            Assert.True(File.Exists(nestedPath));
        }
        finally
        {
            var directory = Path.GetDirectoryName(Path.GetDirectoryName(nestedPath))!;
            Directory.Delete(directory, recursive: true);
        }
    }
}
