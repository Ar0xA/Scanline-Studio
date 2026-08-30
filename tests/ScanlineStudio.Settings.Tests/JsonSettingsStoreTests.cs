using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
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
    public async Task UpdateAsync_ConcurrentCalls_SerializesAndPreservesBothSections()
    {
        // T0-2 regression test against the REAL store, not just the test double (auditor
        // plan-review round 1 finding: a test proving the FAKE serializes proves nothing about
        // JsonSettingsStore itself). Deliberately does NOT use a shared gate both calls park on --
        // that shape (this project's own `feedback_deterministic_gates_not_shared_race`) would still
        // pass even with zero locking, since both calls would just park on the same gate regardless
        // of ordering. Instead: call A's own mutate lambda signals `entered` the instant it starts
        // running (proof it's genuinely inside the lock), then synchronously blocks on `release` --
        // legal here since ISettingsStore.UpdateAsync's own contract only forbids ASYNC work
        // (awaiting) inside mutate, not a test-only synchronous block used to hold the critical
        // section open. Call A is dispatched via Task.Run because UpdateAsync can complete
        // synchronously up through the mutate call on this test's fresh-file path (no real file I/O
        // ever awaits) -- without Task.Run, `store.UpdateAsync(...)` would block this test method's
        // own thread on `release.Wait()` before ever returning a Task to await `entered` against.
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(initialState: false);

        var taskA = Task.Run(() => store.UpdateAsync(s =>
        {
            entered.TrySetResult();
            // Bounded, not an unbounded Wait() -- auditor code-review finding: if the assertion below
            // ever fails, an unbounded wait here would leave this pool thread permanently blocked
            // inside `mutate`, `using release`'s Dispose() would tear down a ManualResetEventSlim a
            // live waiter still references, and the class's own Dispose() (below) would delete the
            // temp directory out from under a save that might still be in flight -- a hung test host,
            // not a clean assertion failure.
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)), "release was never signaled -- see this test's own comment");
            return s.WithSection(SampleSection.SectionKey, new SampleSection("A", 1), SampleSectionJsonContext.Default.SampleSection);
        }));

        await entered.Task; // genuine happens-before: A has entered its critical section
        var taskB = store.UpdateAsync(s =>
            s.WithSection(AnotherSampleSection.SectionKey, new AnotherSampleSection(true), SampleSectionJsonContext.Default.AnotherSampleSection));

        await Task.Delay(50); // give B a chance to race past a broken (unlocked) implementation
        Assert.False(taskB.IsCompleted); // secondary check only -- see the load-bearing assertion below

        release.Set();
        await taskA;
        await taskB;

        // The load-bearing assertion: BOTH sections survive. Without a real lock, B would read A's
        // pre-write snapshot and its own save would silently clobber A's section -- this fails
        // deterministically on a broken implementation, unlike the IsCompleted check above.
        var loaded = await store.LoadAsync();
        Assert.Equal(new SampleSection("A", 1), loaded.GetSection(SampleSection.SectionKey, SampleSectionJsonContext.Default.SampleSection));
        Assert.Equal(new AnotherSampleSection(true), loaded.GetSection(AnotherSampleSection.SectionKey, SampleSectionJsonContext.Default.AnotherSampleSection));
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
    public async Task LoadAsync_TruncatedJson_ReturnsDefaultsAndLogs()
    {
        // Test-suite fixes phase 1, item 3: JsonSettingsStore.LoadAsync's own corrupt-file hardening
        // (fixing a documented prior "single bad byte bricked startup" bug) has had zero tests of
        // its own -- a future edit narrowing that catch back down would have shipped silently green.
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        await File.WriteAllTextAsync(_settingsFilePath, "{ not valid json");
        var logger = new RecordingLogger<JsonSettingsStore>();
        var store = new JsonSettingsStore(logger, _settingsFilePath);

        var loaded = await store.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Empty(loaded.Sections);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning || e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task LoadAsync_WrongTypedRoot_ReturnsDefaultsAndLogs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        await File.WriteAllTextAsync(_settingsFilePath, "42");
        var logger = new RecordingLogger<JsonSettingsStore>();
        var store = new JsonSettingsStore(logger, _settingsFilePath);

        var loaded = await store.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
        Assert.Empty(loaded.Sections);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning || e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task LoadAsync_UnreadableFile_ReturnsDefaultsRatherThanThrowing()
    {
        // Unix-only, same reasoning as AppLocationOverridesTests' identical test: UnauthorizedAccessException
        // does not derive from IOException, so a catch scoped too narrowly would let this slip past.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsFilePath)!);
        await File.WriteAllTextAsync(_settingsFilePath, "{}");
        File.SetUnixFileMode(_settingsFilePath, UnixFileMode.None);
        var logger = new RecordingLogger<JsonSettingsStore>();
        var store = new JsonSettingsStore(logger, _settingsFilePath);

        try
        {
            var loaded = await store.LoadAsync();

            Assert.Equal(AppSettings.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Empty(loaded.Sections);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning || e.Level == LogLevel.Error);
        }
        finally
        {
            File.SetUnixFileMode(_settingsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public async Task RelocateAsync_MovesTheFileToTheNewDirectory_SubsequentLoadReadsFromThere()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var saved = new AppSettings { SchemaVersion = AppSettings.CurrentSchemaVersion + 1 };
        await store.SaveAsync(saved);

        var originalDirectory = Path.GetDirectoryName(_settingsFilePath)!;
        // Nested under the per-test directory (not its PARENT, which resolves to the shared system
        // temp root -- test-suite fixes phase 1, item 10) so this test can never collide with, or be
        // poisoned by, another run's leftover state at a fixed path. Dispose() below already deletes
        // the whole per-test directory recursively, so no separate cleanup is needed here even on
        // assertion failure.
        var newDirectory = Path.Combine(originalDirectory, "relocated");
        var (moved, previousDirectory) = await store.RelocateAsync(newDirectory);

        Assert.True(moved);
        Assert.Equal(originalDirectory, previousDirectory);
        Assert.False(File.Exists(_settingsFilePath));
        Assert.True(File.Exists(Path.Combine(newDirectory, "settings.json")));

        var loaded = await store.LoadAsync();
        Assert.Equal(saved.SchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public async Task RelocateAsync_WhenNoFileExistsYet_JustRetargets_NoMoveNeeded()
    {
        var store = new JsonSettingsStore(NullLogger<JsonSettingsStore>.Instance, _settingsFilePath);
        var originalDirectory = Path.GetDirectoryName(_settingsFilePath)!;
        // Nested under the per-test directory, not its parent -- see the sibling relocate test's
        // own comment (test-suite fixes phase 1, item 10).
        var newDirectory = Path.Combine(originalDirectory, "fresh-install-target");

        var (moved, previousDirectory) = await store.RelocateAsync(newDirectory);

        Assert.True(moved);
        Assert.Equal(originalDirectory, previousDirectory);
        Assert.False(Directory.Exists(newDirectory) && File.Exists(Path.Combine(newDirectory, "settings.json")));

        // The path retargeted even though nothing existed to move -- a subsequent save lands at the
        // NEW directory, not the original one.
        await store.SaveAsync(new AppSettings());
        Assert.True(File.Exists(Path.Combine(newDirectory, "settings.json")));
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
        // Nested under the per-test directory, not its parent -- see the first relocate test's own
        // comment (test-suite fixes phase 1, item 10).
        var conflictDirectory = Path.Combine(originalDirectory, "conflict");
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
    }

    /// <summary>Same shape as this codebase's own established RecordingLogger&lt;T&gt; idiom
    /// (e.g. Core.Sstv.Tests, Application.Tests, ConfigurationPresetStoreTests) -- reused rather
    /// than reinvented.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
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
