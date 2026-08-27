using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed class AppConfigPathsTests : IDisposable
{
    private readonly string _overridesFilePath = Path.Combine(
        Directory.CreateTempSubdirectory("yoniq-appconfigpaths-tests-").FullName, "location-overrides.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_overridesFilePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetConfigDirectory_WhenNoOverridesFilePresent_ReturnsTheSameDefaultAsBefore()
    {
        var expectedDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScanlineStudio");

        var directory = AppConfigPaths.GetConfigDirectory(_overridesFilePath);

        Assert.Equal(expectedDefault, directory);
    }

    [Fact]
    public async Task GetConfigDirectory_WhenOverridePresent_ReturnsTheOverrideValue()
    {
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(ConfigDirectory: "/custom/config"), _overridesFilePath);

        var directory = AppConfigPaths.GetConfigDirectory(_overridesFilePath);

        Assert.Equal("/custom/config", directory);
    }
}
