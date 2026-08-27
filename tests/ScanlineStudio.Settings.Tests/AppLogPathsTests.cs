using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed class AppLogPathsTests : IDisposable
{
    private readonly string _overridesFilePath = Path.Combine(
        Directory.CreateTempSubdirectory("yoniq-applogpaths-tests-").FullName, "location-overrides.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_overridesFilePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetLogDirectory_WhenNoOverridesFilePresent_ReturnsTheSameDefaultAsBefore()
    {
        var expectedDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScanlineStudio", "logs");

        var directory = AppLogPaths.GetLogDirectory(_overridesFilePath);

        Assert.Equal(expectedDefault, directory);
    }

    [Fact]
    public async Task GetLogDirectory_WhenOverridePresent_ReturnsTheOverrideValue()
    {
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(LogDirectory: "/custom/logs"), _overridesFilePath);

        var directory = AppLogPaths.GetLogDirectory(_overridesFilePath);

        Assert.Equal("/custom/logs", directory);
    }

    [Fact]
    public async Task GetLogDirectory_ReadsFreshEveryCall_NotCached()
    {
        // Log relocation applies live (no restart) -- Help > Open application log must reflect a
        // just-relocated directory immediately, so this property must never cache a stale value.
        var first = AppLogPaths.GetLogDirectory(_overridesFilePath);
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(LogDirectory: "/relocated/logs"), _overridesFilePath);

        var second = AppLogPaths.GetLogDirectory(_overridesFilePath);

        Assert.NotEqual(first, second);
        Assert.Equal("/relocated/logs", second);
    }
}
