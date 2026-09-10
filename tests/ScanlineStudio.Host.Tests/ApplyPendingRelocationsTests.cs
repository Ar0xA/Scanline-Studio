using ScanlineStudio.Settings;

namespace ScanlineStudio.Host.Tests;

public sealed class ApplyPendingRelocationsTests : IDisposable
{
    private readonly string _rootDirectory = Directory.CreateTempSubdirectory("yoniq-apply-pending-relocations-tests-").FullName;
    private readonly string _overridesFilePath;

    public ApplyPendingRelocationsTests()
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

    [Fact]
    public async Task WhenNoOverridesFileExists_IsANoOp()
    {
        var exception = Record.Exception(() => Program.ApplyPendingRelocations(_overridesFilePath));

        Assert.Null(exception);
        Assert.False(File.Exists(_overridesFilePath));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task WhenNoPendingDirectorySet_IsANoOp()
    {
        var currentDir = NewSubdirectory("current");
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(ConfigDirectory: currentDir), _overridesFilePath);
        var before = File.ReadAllText(_overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        Assert.Equal(before, File.ReadAllText(_overridesFilePath));
    }

    [Fact]
    public async Task WhenPendingEqualsCurrentDirectory_ClearsPendingAsANoOp()
    {
        var currentDir = NewSubdirectory("current");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(ConfigDirectory: currentDir, PendingConfigDirectory: currentDir), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(currentDir, updated.ConfigDirectory);
        Assert.Null(updated.PendingConfigDirectory);
    }

    [Fact]
    public async Task WhenPendingDiffersAndSourceFileExists_MovesTheFileAndUpdatesTheOverride()
    {
        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        File.WriteAllText(Path.Combine(currentDir, "settings.json"), "{}");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(ConfigDirectory: currentDir, PendingConfigDirectory: pendingDir), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        Assert.True(File.Exists(Path.Combine(pendingDir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(currentDir, "settings.json")));
        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(pendingDir, updated.ConfigDirectory);
        Assert.Null(updated.PendingConfigDirectory);
    }

    [Fact]
    public async Task WhenSourceFileDoesNotExist_TreatsAsSuccessAndStillUpdatesTheOverride()
    {
        // Fresh install (never saved yet) or an already-moved file -- either way, nothing to move
        // is not an error; the override still needs to point at the new directory.
        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(DatabaseDirectory: currentDir, PendingDatabaseDirectory: pendingDir), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(pendingDir, updated.DatabaseDirectory);
        Assert.Null(updated.PendingDatabaseDirectory);
    }

    [Fact]
    public async Task WhenTargetAlreadyHasAConflictingFile_LeavesPendingSetAndKeepsTheOldDirectory()
    {
        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        File.WriteAllText(Path.Combine(currentDir, "settings.json"), "{\"mine\":true}");
        File.WriteAllText(Path.Combine(pendingDir, "settings.json"), "{\"someone-elses\":true}");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(ConfigDirectory: currentDir, PendingConfigDirectory: pendingDir), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        Assert.True(File.Exists(Path.Combine(currentDir, "settings.json")), "must not have moved/overwritten anything");
        Assert.Contains("someone-elses", File.ReadAllText(Path.Combine(pendingDir, "settings.json")));
        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(currentDir, updated.ConfigDirectory);
        Assert.Equal(pendingDir, updated.PendingConfigDirectory);
    }

    [Fact]
    public async Task WhenBothConfigAndDatabaseArePending_BothApplyInTheSamePass()
    {
        var configCurrent = NewSubdirectory("config-current");
        var configPending = NewSubdirectory("config-pending");
        var dbCurrent = NewSubdirectory("db-current");
        var dbPending = NewSubdirectory("db-pending");
        File.WriteAllText(Path.Combine(configCurrent, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(dbCurrent, "history.db"), "sqlite-bytes");
        await AppLocationOverrides.SaveAsync(new AppLocationOverrides(
            ConfigDirectory: configCurrent, PendingConfigDirectory: configPending,
            DatabaseDirectory: dbCurrent, PendingDatabaseDirectory: dbPending), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        Assert.True(File.Exists(Path.Combine(configPending, "settings.json")));
        Assert.True(File.Exists(Path.Combine(dbPending, "history.db")));
        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(configPending, updated.ConfigDirectory);
        Assert.Null(updated.PendingConfigDirectory);
        Assert.Equal(dbPending, updated.DatabaseDirectory);
        Assert.Null(updated.PendingDatabaseDirectory);
    }

    [Fact]
    public async Task WhenDatabaseHasAHotJournal_MovesItAlongsideHistoryDb()
    {
        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        File.WriteAllText(Path.Combine(currentDir, "history.db"), "sqlite-bytes");
        File.WriteAllText(Path.Combine(currentDir, "history.db-journal"), "hot-journal-bytes");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(DatabaseDirectory: currentDir, PendingDatabaseDirectory: pendingDir), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        Assert.True(File.Exists(Path.Combine(pendingDir, "history.db")));
        Assert.True(File.Exists(Path.Combine(pendingDir, "history.db-journal")));
        Assert.False(File.Exists(Path.Combine(currentDir, "history.db-journal")));
    }

    [Fact]
    public async Task WhenDatabaseIsInWalMode_MovesBothWalSidecarsAlongsideHistoryDb()
    {
        // The database runs in WAL mode (SqliteWriteAheadLogging), so -journal is not the sidecar
        // that exists -- -wal and -shm are. -wal holds COMMITTED transactions until a checkpoint
        // folds them back, so leaving it behind loses contacts and receive history that the
        // operator has every reason to believe were saved. Same guarantee as the -journal case
        // above, for the sidecars this application actually produces.
        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        File.WriteAllText(Path.Combine(currentDir, "history.db"), "sqlite-bytes");
        File.WriteAllText(Path.Combine(currentDir, "history.db-wal"), "committed-but-uncheckpointed-bytes");
        File.WriteAllText(Path.Combine(currentDir, "history.db-shm"), "shared-memory-index-bytes");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(DatabaseDirectory: currentDir, PendingDatabaseDirectory: pendingDir), _overridesFilePath);

        Program.ApplyPendingRelocations(_overridesFilePath);

        Assert.True(File.Exists(Path.Combine(pendingDir, "history.db")));
        Assert.True(File.Exists(Path.Combine(pendingDir, "history.db-wal")));
        Assert.True(File.Exists(Path.Combine(pendingDir, "history.db-shm")));
        Assert.False(File.Exists(Path.Combine(currentDir, "history.db-wal")));
        Assert.False(File.Exists(Path.Combine(currentDir, "history.db-shm")));

        // The moved -wal must still carry its bytes. An empty file at the destination would pass
        // every Exists check above while having thrown the committed transactions away.
        Assert.Equal("committed-but-uncheckpointed-bytes", File.ReadAllText(Path.Combine(pendingDir, "history.db-wal")));
    }

    [Fact]
    public async Task WhenTheFirstAttemptFailsTransiently_SucceedsOnRetry()
    {
        // A file (not the expected destination-conflict shape) sitting where the DESTINATION
        // directory needs to be a plain directory makes File.Move fail deterministically on both
        // Windows and Linux (unlike a real file lock, which POSIX rename() doesn't respect) --
        // simulated as transient by removing the obstruction shortly after the first attempt would
        // have run, well within the 5-attempt/1s-apart retry window.
        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        File.WriteAllText(Path.Combine(currentDir, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(pendingDir, "settings.json"));
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(ConfigDirectory: currentDir, PendingConfigDirectory: pendingDir), _overridesFilePath);

        var removeObstructionAfterDelay = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            Directory.Delete(Path.Combine(pendingDir, "settings.json"));
        });

        Program.ApplyPendingRelocations(_overridesFilePath);
        await removeObstructionAfterDelay;

        Assert.True(File.Exists(Path.Combine(pendingDir, "settings.json")));
        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(pendingDir, updated.ConfigDirectory);
        Assert.Null(updated.PendingConfigDirectory);
    }

    [Fact]
    public async Task WhenThePendingDirectoryIsUnusable_DoesNotThrow_KeepsUsingTheOldDirectory()
    {
        // Code-review round-1 finding (blocker): a pending target that's become genuinely
        // unusable (removable/network drive gone, permission revoked, a parent path component
        // that's actually a FILE) used to throw straight out of this method, before any window or
        // logger exists. A file sitting where the pending directory itself needs to be makes
        // Directory.CreateDirectory throw deterministically on both Windows and Linux.
        var currentDir = NewSubdirectory("current");
        File.WriteAllText(Path.Combine(currentDir, "settings.json"), "{}");
        var unusablePendingPath = Path.Combine(_rootDirectory, "not-a-directory");
        File.WriteAllText(unusablePendingPath, "this is a file, not a directory");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(ConfigDirectory: currentDir, PendingConfigDirectory: unusablePendingPath), _overridesFilePath);

        var exception = Record.Exception(() => Program.ApplyPendingRelocations(_overridesFilePath));

        Assert.Null(exception);
        Assert.True(File.Exists(Path.Combine(currentDir, "settings.json")), "the original file must be untouched");
        var updated = AppLocationOverrides.LoadForBootstrap(_overridesFilePath);
        Assert.Equal(currentDir, updated.ConfigDirectory);
        Assert.Equal(unusablePendingPath, updated.PendingConfigDirectory);
    }

    [Fact]
    public async Task WhenPersistingTheOverrideFails_RollsBackTheAlreadyMovedFile()
    {
        // Code-review round-1 finding: a move can succeed and then the override-record save can
        // still fail (disk full, permission revoked between the move and the save) -- without a
        // rollback, settings.json ends up stranded at the new location while the (unsaved) override
        // record still points at the old one, so every future launch looks for it in the wrong
        // place. Unix-only: makes the overrides file's own directory temporarily unwritable so
        // AppLocationOverrides.SaveAsync's atomic temp-file+rename fails after the real move above
        // already succeeded.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var currentDir = NewSubdirectory("current");
        var pendingDir = NewSubdirectory("pending");
        File.WriteAllText(Path.Combine(currentDir, "settings.json"), "{}");
        await AppLocationOverrides.SaveAsync(
            new AppLocationOverrides(ConfigDirectory: currentDir, PendingConfigDirectory: pendingDir), _overridesFilePath);

        var overridesDirectory = Path.GetDirectoryName(_overridesFilePath)!;
        var originalMode = File.GetUnixFileMode(overridesDirectory);
        File.SetUnixFileMode(overridesDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var exception = Record.Exception(() => Program.ApplyPendingRelocations(_overridesFilePath));

            Assert.Null(exception);
            Assert.True(File.Exists(Path.Combine(currentDir, "settings.json")), "the move must have been rolled back");
            Assert.False(File.Exists(Path.Combine(pendingDir, "settings.json")), "nothing should be left at the destination after rollback");
        }
        finally
        {
            File.SetUnixFileMode(overridesDirectory, originalMode);
        }
    }
}
