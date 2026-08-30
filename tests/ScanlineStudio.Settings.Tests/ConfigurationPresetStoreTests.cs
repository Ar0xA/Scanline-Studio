using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

/// <summary>
/// Configurations-preset backlog, Phase 2 (2026-08-28): <see cref="ConfigurationPresetStore"/> --
/// save/switch/clone/delete/rename named, full-application-configuration presets. 2 plan-review
/// rounds found real defects on paper before this code existed: a config-directory relocation that
/// would have silently orphaned a live-config-directory-relative presets folder (fixed: presets live
/// at a FIXED, non-relocating path); an under-specified section list that would have baked window
/// position into every preset (fixed: an explicit exclude-list, not "everything found in Sections");
/// an active-preset marker design that needed to be a store INVARIANT, not a caller convention
/// (fixed: one shared Sanitize helper, enforced on both read and write).
/// </summary>
public sealed partial class ConfigurationPresetStoreTests : IDisposable
{
    private readonly string _presetsDirectory = Directory.CreateTempSubdirectory("yoniq-presets-tests-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_presetsDirectory))
        {
            Directory.Delete(_presetsDirectory, recursive: true);
        }
    }

    private ConfigurationPresetStore CreateStore() => new(NullLogger<ConfigurationPresetStore>.Instance, _presetsDirectory);

    [Fact]
    public async Task ListPresetsAsync_NoPresetsSaved_ReturnsEmpty()
    {
        var store = CreateStore();

        var names = await store.ListPresetsAsync();

        Assert.Empty(names);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAnArbitrarySection()
    {
        var store = CreateStore();
        var content = new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("Field Day", 20), PresetSampleSectionJsonContext.Default.PresetSampleSection);

        await store.SavePresetAsync("Field Day 2026", content);
        var loaded = await store.LoadPresetAsync("Field Day 2026");

        Assert.NotNull(loaded);
        Assert.Equal(new PresetSampleSection("Field Day", 20), loaded!.GetSection(PresetSampleSection.SectionKey, PresetSampleSectionJsonContext.Default.PresetSampleSection));
    }

    [Theory]
    [InlineData("WindowGeometry")]
    [InlineData("TxPaneUi")]
    [InlineData("RxPaneUi")]
    public async Task SavePresetAsync_ExcludesUiLayoutSections(string excludedKey)
    {
        // Plan-review round-1 finding: WindowGeometry baked into a preset would be actively wrong --
        // "Save Current As New" would freeze in the CURRENT window position, and MainWindow already
        // overwrites that section on every Closing regardless, making it meaningless noise.
        var store = CreateStore();
        var content = new AppSettings()
            .WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("kept", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection)
            .WithSection(excludedKey, new PresetSampleSection("excluded", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection);

        await store.SavePresetAsync("Test", content);
        var loaded = await store.LoadPresetAsync("Test");

        Assert.NotNull(loaded);
        Assert.True(loaded!.Sections.ContainsKey(PresetSampleSection.SectionKey));
        Assert.False(loaded.Sections.ContainsKey(excludedKey));
    }

    [Fact]
    public async Task SavePresetAsync_ExcludesTheActivePresetMarkerSection()
    {
        // Plan-review round-2 finding: this must be a STORE invariant (enforced here), not a caller
        // convention -- a preset file must never carry forward a stale "I am named X" self-reference.
        var store = CreateStore();
        var content = new AppSettings()
            .WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("kept", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection)
            .WithSection(ConfigurationPresetStore.ActivePresetSectionKey, new PresetSampleSection("stale-marker", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection);

        await store.SavePresetAsync("Test", content);
        var loaded = await store.LoadPresetAsync("Test");

        Assert.NotNull(loaded);
        Assert.False(loaded!.Sections.ContainsKey(ConfigurationPresetStore.ActivePresetSectionKey));
    }

    [Fact]
    public async Task SavePresetAsync_StampsCurrentSchemaVersion_RegardlessOfWhatWasPassed()
    {
        var store = CreateStore();
        var content = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 99 };

        await store.SavePresetAsync("Test", content);
        var loaded = await store.LoadPresetAsync("Test");

        Assert.NotNull(loaded);
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded!.SchemaVersion);
    }

    [Fact]
    public async Task SavePresetAsync_SameNameTwice_OverwritesRatherThanDuplicating()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Test", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("first", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));
        await store.SavePresetAsync("Test", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("second", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        var names = await store.ListPresetsAsync();
        var loaded = await store.LoadPresetAsync("Test");

        Assert.Single(names);
        Assert.Equal(new PresetSampleSection("second", 2), loaded!.GetSection(PresetSampleSection.SectionKey, PresetSampleSectionJsonContext.Default.PresetSampleSection));
    }

    [Fact]
    public async Task SavePresetAsync_DifferentCasingOfSameName_OverwritesTheExistingFile_CaseInsensitive()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Field Day", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("first", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));
        await store.SavePresetAsync("FIELD DAY", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("second", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        var names = await store.ListPresetsAsync();

        Assert.Single(names); // proves this is the SAME underlying file, not a second one differing only by case
    }

    [Fact]
    public async Task SavePresetAsync_WritesAtomically_NoLingeringTempFile()
    {
        var store = CreateStore();

        await store.SavePresetAsync("Test", new AppSettings());

        Assert.DoesNotContain(Directory.EnumerateFiles(_presetsDirectory), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a..b")]
    [InlineData("..")]
    public async Task SavePresetAsync_InvalidName_ThrowsWithoutMutatingTheName(string invalidName)
    {
        // Plan-review round-2 decision: validate-and-REJECT, never sanitize-and-mutate -- a silently
        // rewritten name would desync from what the user typed, breaking ListPresetsAsync's own
        // filename-derived display and every later name-keyed lookup.
        var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentException>(() => store.SavePresetAsync(invalidName, new AppSettings()));

        Assert.Empty(await store.ListPresetsAsync()); // nothing was written under any mutated form of the name
    }

    [Fact]
    public async Task LoadPresetAsync_NonExistent_ReturnsNull()
    {
        var store = CreateStore();

        var loaded = await store.LoadPresetAsync("Does Not Exist");

        Assert.Null(loaded);
    }

    [Fact]
    public async Task LoadPresetAsync_CaseInsensitiveNameMatch()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Field Day", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("x", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        var loaded = await store.LoadPresetAsync("FIELD DAY");

        Assert.NotNull(loaded);
    }

    [Fact]
    public async Task ClonePresetAsync_CreatesAnIndependentCopy()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Source", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("original", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        await store.ClonePresetAsync("Source", "Clone");
        // Mutate the ORIGINAL after cloning -- proves Clone copied content, not a reference/link.
        await store.SavePresetAsync("Source", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("changed", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        var clone = await store.LoadPresetAsync("Clone");
        Assert.Equal(new PresetSampleSection("original", 1), clone!.GetSection(PresetSampleSection.SectionKey, PresetSampleSectionJsonContext.Default.PresetSampleSection));
    }

    [Fact]
    public async Task ClonePresetAsync_SourceDoesNotExist_Throws()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClonePresetAsync("Nope", "New"));
    }

    [Fact]
    public async Task ClonePresetAsync_TargetNameAlreadyExists_Throws_CaseInsensitive()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Source", new AppSettings());
        await store.SavePresetAsync("existing", new AppSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ClonePresetAsync("Source", "EXISTING"));
    }

    [Fact]
    public async Task DeletePresetAsync_RemovesIt()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Test", new AppSettings());

        await store.DeletePresetAsync("Test");

        Assert.Empty(await store.ListPresetsAsync());
    }

    [Fact]
    public async Task DeletePresetAsync_NonExistent_IsANoOp_DoesNotThrow()
    {
        var store = CreateStore();

        await store.DeletePresetAsync("Does Not Exist");
    }

    [Fact]
    public async Task RenamePresetAsync_RenamesTheFile()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Old Name", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("x", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        await store.RenamePresetAsync("Old Name", "New Name");

        Assert.Null(await store.LoadPresetAsync("Old Name"));
        Assert.NotNull(await store.LoadPresetAsync("New Name"));
        Assert.Single(await store.ListPresetsAsync());
    }

    [Fact]
    public async Task RenamePresetAsync_OldNameDoesNotExist_Throws()
    {
        var store = CreateStore();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RenamePresetAsync("Nope", "New"));
    }

    [Fact]
    public async Task RenamePresetAsync_NewNameAlreadyExists_Throws_CaseInsensitive()
    {
        var store = CreateStore();
        await store.SavePresetAsync("Old", new AppSettings());
        await store.SavePresetAsync("existing", new AppSettings());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RenamePresetAsync("Old", "EXISTING"));

        // Neither side was touched by the rejected rename.
        Assert.NotNull(await store.LoadPresetAsync("Old"));
        Assert.NotNull(await store.LoadPresetAsync("existing"));
    }

    [Fact]
    public async Task RenamePresetAsync_CaseOnlyRename_Succeeds()
    {
        // Code-review round-1 finding: a case-only rename used to be impossible --
        // FindExistingPresetFile(newName) is case-insensitive, so it found the SOURCE file itself
        // and rejected the rename as "already exists," with no other way to fix a preset's casing.
        var store = CreateStore();
        await store.SavePresetAsync("field day", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("x", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        await store.RenamePresetAsync("field day", "Field Day");

        var names = await store.ListPresetsAsync();
        Assert.Single(names);
        Assert.Equal("Field Day", names[0]); // the casing actually changed, not silently ignored
        Assert.NotNull(await store.LoadPresetAsync("field day")); // still findable case-insensitively
    }

    [Fact]
    public async Task RenamePresetAsync_IdenticalName_IsANoOp_DoesNotThrow()
    {
        // Code-review round-2 finding: the case-only-rename fix above widened the collision check
        // enough that an EXACT no-op rename (same name, same casing) now reaches the file-move step
        // -- explicitly short-circuited rather than relying on File.Move(p, p) happening to succeed.
        var store = CreateStore();
        await store.SavePresetAsync("Test", new AppSettings().WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("x", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection));

        await store.RenamePresetAsync("Test", "Test");

        Assert.Single(await store.ListPresetsAsync());
        Assert.NotNull(await store.LoadPresetAsync("Test"));
    }

    [Fact]
    public void GetDefaultPresetsDirectory_PinsTheDirectoryNameAndDerivationShape()
    {
        // Code-review round-2 finding: this does NOT actually catch a regression back to
        // AppConfigPaths.ConfigDirectory (the round-1 plan-review blocker this method exists to
        // avoid) -- on any machine/CI runner with no location-overrides.json (the normal case),
        // ConfigDirectory and GetDefaultConfigDirectory() are STRING-IDENTICAL
        // (AppConfigPaths.GetConfigDirectory falls back to GetDefaultConfigDirectory when no
        // override file exists), so that one-token mutation would still pass this test. What this
        // DOES still pin: the "presets" literal itself, and that the method derives from
        // GetDefaultConfigDirectory() (not some unrelated path) -- real value, just not the
        // regression-proof round 1 originally claimed. A genuine catch needs a real override file on
        // disk, which this store's own constructor seam doesn't support testing against.
        var expected = Path.Combine(AppConfigPaths.GetDefaultConfigDirectory(), "presets");

        Assert.Equal(expected, ConfigurationPresetStore.GetDefaultPresetsDirectory());
    }

    [Fact]
    public async Task LoadPresetAsync_HandEditedFileContainingExcludedSections_StripsThemOnRead()
    {
        // Code-review round-1 finding: every existing exclusion test wrote through SavePresetAsync
        // first, so write-side stripping masked whether read-side stripping (the LOAD-BEARING half --
        // a hand-edited or stale preset file's own stale/malicious marker must never hijack
        // active-preset state) actually does anything at all. This test bypasses the store entirely
        // and writes the raw file itself.
        var store = CreateStore();
        Directory.CreateDirectory(_presetsDirectory);
        var rawSettings = new AppSettings()
            .WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("kept", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection)
            .WithSection("WindowGeometry", new PresetSampleSection("should-be-stripped", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection)
            .WithSection(ConfigurationPresetStore.ActivePresetSectionKey, new PresetSampleSection("stale-marker", 3), PresetSampleSectionJsonContext.Default.PresetSampleSection);
        await File.WriteAllTextAsync(Path.Combine(_presetsDirectory, "HandEdited.json"), JsonSerializer.Serialize(rawSettings));

        var loaded = await store.LoadPresetAsync("HandEdited");

        Assert.NotNull(loaded);
        Assert.True(loaded!.Sections.ContainsKey(PresetSampleSection.SectionKey));
        Assert.False(loaded.Sections.ContainsKey("WindowGeometry"));
        Assert.False(loaded.Sections.ContainsKey(ConfigurationPresetStore.ActivePresetSectionKey));
    }

    [Fact]
    public async Task ClonePresetAsync_HandEditedSourceContainingExcludedSections_StripsThemOnClone()
    {
        // Same read-side-sanitizing proof as LoadPresetAsync's own test above, but through
        // ClonePresetAsync's own read-then-write path -- IConfigurationPresetStore.cs explicitly
        // promises this and nothing previously verified it.
        var store = CreateStore();
        Directory.CreateDirectory(_presetsDirectory);
        var rawSettings = new AppSettings()
            .WithSection(PresetSampleSection.SectionKey, new PresetSampleSection("kept", 1), PresetSampleSectionJsonContext.Default.PresetSampleSection)
            .WithSection("TxPaneUi", new PresetSampleSection("should-be-stripped", 2), PresetSampleSectionJsonContext.Default.PresetSampleSection);
        await File.WriteAllTextAsync(Path.Combine(_presetsDirectory, "HandEdited.json"), JsonSerializer.Serialize(rawSettings));

        await store.ClonePresetAsync("HandEdited", "Clone");
        var clone = await store.LoadPresetAsync("Clone");

        Assert.NotNull(clone);
        Assert.True(clone!.Sections.ContainsKey(PresetSampleSection.SectionKey));
        Assert.False(clone.Sections.ContainsKey("TxPaneUi"));
    }

    [Fact]
    public async Task LoadPresetAsync_SchemaVersionMismatch_LogsWarning()
    {
        // Code-review round-1 finding: Sanitize stamps SchemaVersion to current on READ too, which
        // would silently erase the exact evidence a mismatch warning needs if checked afterward --
        // the check must happen BEFORE Sanitize runs.
        var logger = new RecordingLogger<ConfigurationPresetStore>();
        var store = new ConfigurationPresetStore(logger, _presetsDirectory);
        Directory.CreateDirectory(_presetsDirectory);
        var rawSettings = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 1 };
        await File.WriteAllTextAsync(Path.Combine(_presetsDirectory, "Old.json"), JsonSerializer.Serialize(rawSettings));

        var loaded = await store.LoadPresetAsync("Old");

        // Code-review round-2 finding: assert the actual message content (not just "some Warning
        // fired," which a mutated `if (true)` would also satisfy), AND that the returned value is
        // still stamped to CURRENT despite the mismatch -- proves the warning is purely
        // informational, not a silent no-load, and separately proves read-side STAMPING (not just
        // the warning check) actually ran.
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("schema version", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded!.SchemaVersion);
    }

    [Fact]
    public async Task LoadPresetAsync_CorruptFile_ReturnsNullAndLogs()
    {
        // Test-suite fixes phase 1, item 3: a hand-edited/corrupt preset file (the kind users are
        // most likely to share) used to throw straight out of an interactive menu click. Returns
        // null -- the same value already used for "no preset named this exists" (see
        // ConfigurationPresetStore.LoadPresetAsync's own doc comment for why that collision is a
        // deliberate choice here, unlike ClonePresetAsync below).
        var logger = new RecordingLogger<ConfigurationPresetStore>();
        var store = new ConfigurationPresetStore(logger, _presetsDirectory);
        Directory.CreateDirectory(_presetsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_presetsDirectory, "Corrupt.json"), "{ not valid json");

        var loaded = await store.LoadPresetAsync("Corrupt");

        Assert.Null(loaded);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ClonePresetAsync_SourceIsCorrupt_ThrowsInvalidOperationException()
    {
        // Test-suite fixes phase 1, item 3: unlike LoadPresetAsync's "return null" contract above,
        // cloning a corrupt source must fail loudly -- silently writing an empty-but-valid clone
        // from unreadable content would be a worse outcome than today's uncaught throw.
        var logger = new RecordingLogger<ConfigurationPresetStore>();
        var store = new ConfigurationPresetStore(logger, _presetsDirectory);
        Directory.CreateDirectory(_presetsDirectory);
        await File.WriteAllTextAsync(Path.Combine(_presetsDirectory, "Corrupt.json"), "{ not valid json");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ClonePresetAsync("Corrupt", "Clone"));

        Assert.Contains("Corrupt", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Clone", await store.ListPresetsAsync()); // no partial/empty clone was ever written
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    /// <summary>Same shape as this codebase's own established RecordingLogger&lt;T&gt; idiom
    /// (e.g. Core.Sstv.Tests, Application.Tests) -- reused rather than reinvented.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed record PresetSampleSection(string Name, int Value)
    {
        public const string SectionKey = "Sample";
    }

    [JsonSerializable(typeof(PresetSampleSection))]
    private sealed partial class PresetSampleSectionJsonContext : JsonSerializerContext;
}
