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

    private AppLocationsService CreateService(FakeLogFileRelocator? relocator = null) =>
        new(relocator ?? new FakeLogFileRelocator(), NullLogger<AppLocationsService>.Instance, _overridesFilePath);

    [Fact]
    public async Task SetConfigDirectoryAsync_ValidatesAndCreatesTargetDirectory_WithoutTouchingAnyFile()
    {
        var service = CreateService();
        var target = Path.Combine(_rootDirectory, "does-not-exist-yet");

        await service.SetConfigDirectoryAsync(target);

        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFiles(target));
        Assert.Equal(target, await service.GetPendingConfigDirectoryAsync());
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_ASecondApply_ReplacesThePendingTarget_DoesNotStack()
    {
        var service = CreateService();
        await service.SetConfigDirectoryAsync(NewSubdirectory("first-target"));

        var secondTarget = NewSubdirectory("second-target");
        await service.SetConfigDirectoryAsync(secondTarget);

        Assert.Equal(secondTarget, await service.GetPendingConfigDirectoryAsync());
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_WhenTargetEqualsCurrentDirectory_ClearsAnyPendingAsANoOp()
    {
        var service = CreateService();
        var current = NewSubdirectory("current");
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(ConfigDirectory: current, PendingConfigDirectory: NewSubdirectory("stale-pending")), _overridesFilePath);

        await service.SetConfigDirectoryAsync(current);

        Assert.Null(await service.GetPendingConfigDirectoryAsync());
    }

    [Fact]
    public async Task SetConfigDirectoryAsync_WhenTargetAlreadyHasASameNamedFile_ThrowsAndDoesNotStage()
    {
        var service = CreateService();
        var target = NewSubdirectory("conflict");
        File.WriteAllText(Path.Combine(target, "settings.json"), "{}");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetConfigDirectoryAsync(target));
        Assert.Null(await service.GetPendingConfigDirectoryAsync());
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
        var service = CreateService(relocator);
        var target = NewSubdirectory("log-target");

        await service.SetLogDirectoryAsync(target);

        Assert.Equal(target, relocator.LastRequestedDirectory);
        Assert.Equal(target, await service.GetLogDirectoryAsync());
    }

    [Fact]
    public async Task SetLogDirectoryAsync_WhenTheRelocatorRefuses_ThrowsAndDoesNotPersistTheOverride()
    {
        var relocator = new FakeLogFileRelocator { NextResult = false };
        var service = CreateService(relocator);
        var target = NewSubdirectory("log-conflict");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetLogDirectoryAsync(target));

        var overrides = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Null(overrides.LogDirectory);
    }

    [Fact]
    public async Task SetLogDirectoryAsync_WhenTargetEqualsCurrentDirectory_IsANoOp_NeverCallsTheRelocator()
    {
        var relocator = new FakeLogFileRelocator { NextResult = true };
        var service = CreateService(relocator);
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
        var service = CreateService(relocator);
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
}
