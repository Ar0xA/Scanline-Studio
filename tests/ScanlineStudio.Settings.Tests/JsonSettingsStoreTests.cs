using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed partial class JsonSettingsStoreTests : IDisposable
{
    private readonly string _settingsFilePath = Path.Combine(
        Directory.CreateTempSubdirectory("yoniq-settings-tests-").FullName, "settings.json");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_settingsFilePath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsSchemaVersion()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var saved = new AppSettings();

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(saved.SchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task LoadAsync_WhenFileDoesNotExist_ReturnsDefaultSettings()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);

        var loaded = await store.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Empty(loaded.Sections);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAModuleSectionViaGenericExtensions()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var section = new SampleSection("hello", 42);
        var saved = new AppSettings().WithSection(SampleSection.SectionKey, section, SampleSectionJsonContext.Default.SampleSection);

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();
        var loadedSection = loaded.GetSection(SampleSection.SectionKey, SampleSectionJsonContext.Default.SampleSection);

        Assert.Equal(section, loadedSection);
    }

    [Fact]
    public void GetSection_WhenKeyAbsent_ReturnsDefault()
    {
        var settings = new AppSettings();

        var section = settings.GetSection(SampleSection.SectionKey, SampleSectionJsonContext.Default.SampleSection);

        Assert.Null(section);
    }

    private sealed record SampleSection(string Name, int Value)
    {
        public const string SectionKey = "Sample";
    }

    [JsonSerializable(typeof(SampleSection))]
    private sealed partial class SampleSectionJsonContext : JsonSerializerContext;
}
