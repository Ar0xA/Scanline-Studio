using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Settings.Tests;

public sealed class AppLocationsServiceTests : IDisposable
{
    private readonly string _rootDirectory = Directory.CreateTempSubdirectory("yoniq-app-locations-service-tests-").FullName;
    private readonly string _overridesFilePath;

    public AppLocationsServiceTests()
    {
        _overridesFilePath = Path.Combine(_rootDirectory, "location-overrides.json");
    }

    public void Dispose() => Directory.Delete(_rootDirectory, recursive: true);

    private string NewSubdirectory(string name)
    {
        var path = Path.Combine(_rootDirectory, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private AppLocationsService CreateService(FakeSettingsFileRelocator? settingsRelocator = null, FakeLogFileRelocator? logRelocator = null) =>
        new(settingsRelocator ?? new FakeSettingsFileRelocator(), logRelocator ?? new FakeLogFileRelocator(), NullLogger<AppLocationsService>.Instance, _overridesFilePath);

    [Fact]
    public async Task SetConfigDirectoryAsync_DelegatesLiveToTheSettingsFileRelocator()
    {
        var relocator = new FakeSettingsFileRelocator();
        var service = CreateService(relocator);
        var target = NewSubdirectory("config-target");

        await service.SetConfigDirectoryAsync(target);

        Assert.Equal(target, relocator.LastRequestedDirectory);
        Assert.Equal(target, await service.GetConfigDirectoryAsync());
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_WhenTheRelocatorRefuses_ThrowsAndDoesNotPersistTheOverride()
    {
        var relocator = new FakeSettingsFileRelocator { NextMoved = false };
        var service = CreateService(relocator);
        var target = NewSubdirectory("config-conflict");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetConfigDirectoryAsync(target));

        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Null(overrides.ConfigDirectory);
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_AlwaysPersists_EvenOnANoOpRelocate_SelfHealsADivergedOverrideRecord()
    {
        // Round-3 plan-review risk-1: a prior failed rollback can leave the store at E while
        // location-overrides.json still (wrongly) says P. The user's natural recovery -- pointing
        // Config back at E, the store's real (correct) directory -- must still rewrite the override
        // record to agree, even though the relocator itself reports a no-op (nothing physically moved).
        // Real, platform-normalized directories (not hand-typed POSIX-style literals, code-review
        // finding) -- NormalizeTargetDirectory's own Path.GetFullPath would otherwise make a literal
        // "/fake/E" compare unequal to itself on Windows (e.g. "C:\fake\E").
        var e = NewSubdirectory("E");
        var p = NewSubdirectory("P");
        var relocator = new FakeSettingsFileRelocator { CurrentDirectory = e };
        var service = CreateService(relocator);
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(ConfigDirectory: p), _overridesFilePath);

        await service.SetConfigDirectoryAsync(e);

        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(e, overrides.ConfigDirectory);
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_ClearsAnyStalePendingConfigDirectory()
    {
        // A value left over from an older, pre-live-relocation build (or a previously-failed retry)
        // must never survive a live Apply -- otherwise Program.cs's own bootstrap-time migration code
        // would silently re-move settings.json to that stale target on the NEXT restart.
        var service = CreateService();
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(PendingConfigDirectory: "/fake/stale-pending"), _overridesFilePath);
        var target = NewSubdirectory("config-target");

        await service.SetConfigDirectoryAsync(target);

        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Null(overrides.PendingConfigDirectory);
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_WhenPersistingTheOverrideFails_RollsBackToThePreviousDirectory()
    {
        // Same reasoning as SetLogDirectoryAsync's own rollback test below, but Config's failure mode
        // is user-data-visible (the running process keeps reading/writing settings.json at the new
        // directory while the next launch would look for it at the old one), unlike Log's cosmetic
        // one -- see AppLocationsService.SetConfigDirectoryAsync's own doc comment.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var relocator = new FakeSettingsFileRelocator { CurrentDirectory = "/fake/original" };
        var service = CreateService(relocator);
        var target = NewSubdirectory("config-target-rollback");

        var originalMode = File.GetUnixFileMode(_rootDirectory);
        File.SetUnixFileMode(_rootDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.SetConfigDirectoryAsync(target));
        }
        finally
        {
            File.SetUnixFileMode(_rootDirectory, originalMode);
        }

        Assert.Equal([target, "/fake/original"], relocator.RequestedDirectories);
    }

    [Fact]
    public async Task SetDatabaseDirectoryAsync_ValidatesAndCreatesTargetDirectory_WithoutTouchingAnyFile()
    {
        var service = CreateService();
        var target = Path.Combine(_rootDirectory, "db-target");

        await service.SetDatabaseDirectoryAsync(target);

        Assert.True(Directory.Exists(target));
        Assert.Equal(target, await service.GetPendingDatabaseDirectoryAsync());
    }

    [Fact]
    public async Task SetDatabaseDirectoryAsync_WhenTargetAlreadyHasASameNamedFile_ThrowsAndDoesNotStage()
    {
        var service = CreateService();
        var target = NewSubdirectory("db-conflict");
        File.WriteAllText(Path.Combine(target, "history.db"), "sqlite-bytes");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetDatabaseDirectoryAsync(target));
        Assert.Null(await service.GetPendingDatabaseDirectoryAsync());
    }

    [Fact]
    public async Task SetLogDirectoryAsync_DelegatesLiveToTheLogFileRelocator()
    {
        var relocator = new FakeLogFileRelocator { NextResult = true };
        var service = CreateService(logRelocator: relocator);
        var target = NewSubdirectory("log-target");

        await service.SetLogDirectoryAsync(target);

        Assert.Equal(target, relocator.LastRequestedDirectory);
        Assert.Equal(target, await service.GetLogDirectoryAsync());
    }

    [Fact]
    public async Task SetLogDirectoryAsync_WhenTheRelocatorRefuses_ThrowsAndDoesNotPersistTheOverride()
    {
        var relocator = new FakeLogFileRelocator { NextResult = false };
        var service = CreateService(logRelocator: relocator);
        var target = NewSubdirectory("log-conflict");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLogDirectoryAsync(target));

        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Null(overrides.LogDirectory);
    }

    [Fact]
    public async Task SetLogDirectoryAsync_WhenTargetEqualsCurrentDirectory_IsANoOp_NeverCallsTheRelocator()
    {
        var relocator = new FakeLogFileRelocator { NextResult = true };
        var service = CreateService(logRelocator: relocator);
        var current = await service.GetLogDirectoryAsync();

        await service.SetLogDirectoryAsync(current);

        Assert.Null(relocator.LastRequestedDirectory);
    }

    [Fact]
    public async Task SetLogDirectoryAsync_WhenPersistingTheOverrideFails_RollsBackTheAlreadyRelocatedFiles()
    {
        // Code-review round-1 finding: the relocate can succeed and the override-record save can
        // still fail afterward -- without a rollback, app.log ends up at the new location while
        // AppLogPaths.LogDirectory (and the next launch) keeps resolving the old one. Unix-only:
        // makes the overrides file's own directory temporarily unwritable so
        // AppLocationOverrides.SaveAsync's atomic temp-file+rename fails after the relocate above
        // already succeeded.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var relocator = new FakeLogFileRelocator { NextResult = true };
        var service = CreateService(logRelocator: relocator);
        var currentDirectory = await service.GetLogDirectoryAsync();
        var target = NewSubdirectory("log-target-rollback");

        var originalMode = File.GetUnixFileMode(_rootDirectory);
        File.SetUnixFileMode(_rootDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.SetLogDirectoryAsync(target));
        }
        finally
        {
            File.SetUnixFileMode(_rootDirectory, originalMode);
        }

        Assert.Equal([target, currentDirectory], relocator.RequestedDirectories);
    }

    private sealed class FakeLogFileRelocator : ILogFileRelocator
    {
        public bool NextResult { get; set; } = true;
        public string? LastRequestedDirectory { get; private set; }
        public List<string> RequestedDirectories { get; } = [];

        public Task<bool> RelocateAsync(string newDirectory, CancellationToken ct = default)
        {
            LastRequestedDirectory = newDirectory;
            RequestedDirectories.Add(newDirectory);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeSettingsFileRelocator : ISettingsFileRelocator
    {
        public bool NextMoved { get; set; } = true;
        public string CurrentDirectory { get; set; } = "";
        public string? LastRequestedDirectory { get; private set; }
        public List<string> RequestedDirectories { get; } = [];

        public Task<(bool Moved, string PreviousDirectory)> RelocateAsync(string newDirectory, CancellationToken ct = default)
        {
            LastRequestedDirectory = newDirectory;
            RequestedDirectories.Add(newDirectory);
            var previous = CurrentDirectory;
            if (NextMoved)
            {
                CurrentDirectory = newDirectory;
            }

            return Task.FromResult((NextMoved, previous));
        }
    }
}
