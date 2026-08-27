using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed class AppDatabasePathsTests : IDisposable
{
    private readonly string _overridesFilePath = Path.Combine(
        Directory.CreateTempSubdirectory("yoniq-appdatabasepaths-tests-").FullName, "location-overrides.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_overridesFilePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GetDatabaseDirectory_WhenNoOverridesFilePresent_ReturnsTheSameDefaultAsBefore()
    {
        var expectedDefault = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScanlineStudio");

        var directory = AppDatabasePaths.GetDatabaseDirectory(_overridesFilePath);

        Assert.Equal(expectedDefault, directory);
    }

    [Fact]
    public async Task GetDatabaseDirectory_WhenOverridePresent_ReturnsTheOverrideValue()
    {
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(DatabaseDirectory: "/custom/db"), _overridesFilePath);

        var directory = AppDatabasePaths.GetDatabaseDirectory(_overridesFilePath);

        Assert.Equal("/custom/db", directory);
    }
}
