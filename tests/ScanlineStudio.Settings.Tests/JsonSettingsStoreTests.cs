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
        // Tier C audit finding: a schema version equal to AppSettings' own current-version constant
        // used to be saved AND read back as the default fallback on ANY load failure -- this test
        // would pass even if SaveAsync were a no-op, since LoadAsync's own "file missing/corrupt"
        // fallback returns that exact same constant. A non-default value makes a no-op save fail.
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var saved = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 1 };

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(saved.SchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task SaveThenLoad_WithSectionPreservesAPreviouslySavedUnrelatedSection()
    {
        // Tier C audit finding: WithSection's own dictionary-copy contract -- the property every
        // module in the app relies on to read/write its own settings section without clobbering
        // every OTHER module's section -- had zero coverage through a real save+load cycle. A
        // regression that replaced the copy ctor with a fresh dictionary would still pass every
        // OTHER test in this file.
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var sample = new SampleSection("hello", 42);
        var other = new AnotherSampleSection(true);
        var saved = new AppSettings()
            .WithSection(SampleSection.SectionKey, sample, SampleSectionJsonContext.Default.SampleSection)
            .WithSection(AnotherSampleSection.SectionKey, other, SampleSectionJsonContext.Default.AnotherSampleSection);

        await store.SaveAsync(saved);
        var loaded = await store.LoadAsync();

        Assert.Equal(sample, loaded.GetSection(SampleSection.SectionKey, SampleSectionJsonContext.Default.SampleSection));
        Assert.Equal(other, loaded.GetSection(AnotherSampleSection.SectionKey, SampleSectionJsonContext.Default.AnotherSampleSection));
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

    [Fact]
    public async Task RelocateAsync_MovesTheFileToTheNewDirectory_SubsequentLoadReadsFromThere()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var saved = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 1 };
        await store.SaveAsync(saved);

        var originalDirectory = Path.GetDirectoryName(_settingsFilePath)!;
        var newDirectory = Path.Combine(Directory.GetParent(originalDirectory)!.FullName, "relocated");
        var (moved, previousDirectory) = await store.RelocateAsync(newDirectory);

        Assert.True(moved);
        Assert.Equal(originalDirectory, previousDirectory);
        Assert.False(File.Exists(_settingsFilePath));
        Assert.True(File.Exists(Path.Combine(newDirectory, "settings.json")));

        var loaded = await store.LoadAsync();
        Assert.Equal(saved.SchemaVersion, loaded.SchemaVersion);

        Directory.Delete(newDirectory, recursive: true);
    }

    [Fact]
    public async Task RelocateAsync_WhenNoFileExistsYet_JustRetargets_NoMoveNeeded()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var originalDirectory = Path.GetDirectoryName(_settingsFilePath)!;
        var newDirectory = Path.Combine(Directory.GetParent(originalDirectory)!.FullName, "fresh-install-target");

        var (moved, previousDirectory) = await store.RelocateAsync(newDirectory);

        Assert.True(moved);
        Assert.Equal(originalDirectory, previousDirectory);
        Assert.False(Directory.Exists(newDirectory) && File.Exists(Path.Combine(newDirectory, "settings.json")));

        // The path retargeted even though nothing existed to move -- a subsequent save lands at the
        // NEW directory, not the original one.
        await store.SaveAsync(new AppSettings());
        Assert.True(File.Exists(Path.Combine(newDirectory, "settings.json")));

        Directory.Delete(newDirectory, recursive: true);
    }

    [Fact]
    public async Task RelocateAsync_WhenTargetEqualsCurrentDirectory_IsANoOp_ReturnsTrueAndThePreviousDirectory()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var currentDirectory = Path.GetDirectoryName(_settingsFilePath)!;

        var (moved, previousDirectory) = await store.RelocateAsync(currentDirectory);

        Assert.True(moved);
        Assert.Equal(currentDirectory, previousDirectory);
    }

    [Fact]
    public async Task RelocateAsync_WhenDestinationAlreadyHasASettingsFile_ReturnsFalse_NeverUpdatesThePath()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        await store.SaveAsync(new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 1 });

        var originalDirectory = Path.GetDirectoryName(_settingsFilePath)!;
        var conflictDirectory = Path.Combine(Directory.GetParent(originalDirectory)!.FullName, "conflict");
        Directory.CreateDirectory(conflictDirectory);
        await File.WriteAllTextAsync(Path.Combine(conflictDirectory, "settings.json"), "{}");

        var (moved, previousDirectory) = await store.RelocateAsync(conflictDirectory);

        Assert.False(moved);
        Assert.Equal(originalDirectory, previousDirectory);
        Assert.True(File.Exists(_settingsFilePath)); // the ORIGINAL file was never touched

        // The path contract: never updated on a `false` return -- a subsequent Load still reads the
        // original, unmoved file, not the (unrelated) conflicting one at the destination.
        var loaded = await store.LoadAsync();
        Assert.Equal(AppSettings.CurrentSchemaVersion + 1, loaded.SchemaVersion);

        Directory.Delete(conflictDirectory, recursive: true);
    }

    private sealed record SampleSection(string Name, int Value)
    {
        public const string SectionKey = "Sample";
    }

    private sealed record AnotherSampleSection(bool Flag)
    {
        public const string SectionKey = "AnotherSample";
    }

    [JsonSerializable(typeof(SampleSection))]
    [JsonSerializable(typeof(AnotherSampleSection))]
    private sealed partial class SampleSectionJsonContext : JsonSerializerContext;
}
