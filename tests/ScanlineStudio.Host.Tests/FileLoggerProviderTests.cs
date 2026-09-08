using Microsoft.Extensions.Logging;
using ScanlineStudio.Host;

namespace ScanlineStudio.Host.Tests;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory;
    private readonly string _logPath;

    public FileLoggerProviderTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"FileLoggerProviderTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _logPath = Path.Combine(_directory, "app.log");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static void WriteLines(FileLoggerProvider provider, string categoryName, int count, string message = "line")
    {
        var logger = provider.CreateLogger(categoryName);
        for (var i = 0; i < count; i++)
        {
            logger.Log(LogLevel.Information, new EventId(0), message, null, (s, _) => s);
        }
    }

    [Fact]
    public async Task Relocation_RacingDestinationIsPreservedAndOriginalStillLogs()
    {
        using var provider = new FileLoggerProvider(_logPath);
        WriteLines(provider, "test", 1, "original");
        var destinationDirectory = Path.Combine(_directory, "new");
        provider.BeforePublishForTests = destination => File.WriteAllText(destination, "foreign log");
        Assert.False(await provider.RelocateAsync(destinationDirectory));
        Assert.Equal("foreign log", File.ReadAllText(Path.Combine(destinationDirectory, "app.log")));
        WriteLines(provider, "test", 1, "still working");
        Assert.Contains("still working", File.ReadAllText(_logPath));
        Assert.Empty(Directory.GetFiles(destinationDirectory, "*.tmp"));
    }

    [Fact]
    public async Task FailedWriter_AutomaticallyRetriesAfterThrottle()
    {
        using var provider = new FileLoggerProvider(_logPath);
        provider.WriterForTests.Dispose();
        WriteLines(provider, "test", 1, "unavailable");
        WriteLines(provider, "test", 1, "throttled");
        Assert.DoesNotContain("throttled", File.ReadAllText(_logPath));
        await Task.Delay(1100);
        WriteLines(provider, "test", 1, "automatic recovery");
        Assert.Contains("automatic recovery", File.ReadAllText(_logPath));
    }

    private sealed class FailingDisposeWriter : StreamWriter
    {
        public FailingDisposeWriter() : base(new MemoryStream()) { AutoFlush = true; }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            throw new IOException("injected close failure");
        }
    }

    [Fact]
    public void RotationAndRecovery_AbsorbWriterDisposeFailure()
    {
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 1);
        provider.WriterForTests.Dispose();
        provider.WriterForTests = new FailingDisposeWriter();
        WriteLines(provider, "test", 1, "trigger rotation");
        WriteLines(provider, "test", 1, "recovered after close failure");
        Assert.Contains("recovered after close failure", File.ReadAllText(_logPath + ".1"));
    }

    [Fact]
    public async Task FailedWriter_CanRecoverThroughSameDirectoryRelocation()
    {
        using var provider = new FileLoggerProvider(_logPath);
        provider.WriterForTests.Dispose();
        WriteLines(provider, "test", 1, "lost while unavailable");
        Assert.True(await provider.RelocateAsync(_directory));
        WriteLines(provider, "test", 1, "recovered");
        Assert.Contains("recovered", File.ReadAllText(_logPath));
        provider.Dispose();
        WriteLines(provider, "test", 1, "must stay disposed");
        Assert.DoesNotContain("must stay disposed", File.ReadAllText(_logPath));
    }

    [Fact]
    public void CreateLogger_WritesLinesToTheConfiguredPath()
    {
        using var provider = new FileLoggerProvider(_logPath);

        WriteLines(provider, "TestCategory", 1, "hello world");
        provider.Dispose();

        var content = File.ReadAllText(_logPath);
        Assert.Contains("TestCategory", content);
        Assert.Contains("hello world", content);
    }

    [Fact]
    public void WritingPastTheSizeCap_RotatesTheLiveFileToDotOne()
    {
        // A tiny cap makes a handful of lines enough to cross it without needing megabytes of data.
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 5);

        WriteLines(provider, "Cat", count: 20, message: new string('x', 20));
        provider.Dispose();

        Assert.True(File.Exists($"{_logPath}.1"), "expected a .1 backup to exist after crossing the size cap");
        Assert.True(File.Exists(_logPath), "the live path must still exist and be writable after rotation");
    }

    [Fact]
    public void SizeExactlyAtTheCap_TriggersRotation_NotOnlyWhenStrictlyOver()
    {
        // Measures the exact byte length of one written line (via a provider whose cap can never be
        // reached), then reuses that exact length as a second provider's cap -- pins the ">="
        // boundary specifically: a ">" mutant would need the file to exceed the cap, not just reach
        // it, and would fail only this test while every "clearly over the cap" test above still passes.
        using (var measuring = new FileLoggerProvider(_logPath, maxFileSizeBytes: long.MaxValue, maxBackupFileCount: 5))
        {
            WriteLines(measuring, "Cat", count: 1, message: "boundary");
        }

        var exactLineLength = new FileInfo(_logPath).Length;
        File.Delete(_logPath);

        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: exactLineLength, maxBackupFileCount: 5);
        WriteLines(provider, "Cat", count: 1, message: "boundary");
        provider.Dispose();

        Assert.True(File.Exists($"{_logPath}.1"), "a file whose length reaches the cap exactly must still rotate");
    }

    [Fact]
    public void RotatingWithExistingBackups_ShiftsThemUpByOne_OldestDropped()
    {
        // Pre-populated, distinguishable backup content (rather than inferring shift order from
        // rotated-through log lines) proves the exact shift direction deterministically: correct
        // behavior moves .1->.2, .2->.3 (dropping old .3 since maxBackupFileCount=3), and leaves .1
        // holding only the freshly-rotated live content, not any pre-existing backup text.
        File.WriteAllText($"{_logPath}.1", "backup-one");
        File.WriteAllText($"{_logPath}.2", "backup-two");
        File.WriteAllText($"{_logPath}.3", "backup-three");

        // Cap smaller than a single formatted line, and exactly one write, so exactly one rotation
        // happens -- anything looser risks a second rotation shifting the pre-populated backups
        // further than this test means to exercise.
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 10, maxBackupFileCount: 3);
        WriteLines(provider, "Cat", count: 1, message: "x");
        provider.Dispose();

        Assert.Equal("backup-two", File.ReadAllText($"{_logPath}.3"));
        Assert.Equal("backup-one", File.ReadAllText($"{_logPath}.2"));
        Assert.DoesNotContain("backup", File.ReadAllText($"{_logPath}.1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Constructing_WithAnAlreadyOversizedExistingFile_RotatesItImmediately()
    {
        var oversizedContent = new string('x', 500);
        File.WriteAllText(_logPath, oversizedContent);

        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 5);
        provider.Dispose();

        Assert.True(File.Exists($"{_logPath}.1"), "a pre-existing oversized file must be rotated out at construction, not left to grow further");
        Assert.Equal(oversizedContent, File.ReadAllText($"{_logPath}.1"));
    }

    [Fact]
    public void MultipleLoggersFromTheSameProvider_ShareOneRotationSequence()
    {
        // FileLogger no longer owns its own writer/lock -- every category must route through the
        // same provider-level WriteLine so rotation state (and the file on disk) stays consistent
        // across categories, not just within one logger instance. CategoryA alone must stay well
        // under the cap, or this test can't distinguish from the old per-logger-writer design (which
        // would also show a .1 file after enough writes on ONE logger alone).
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 400, maxBackupFileCount: 5);

        WriteLines(provider, "CategoryA", count: 2, message: new string('a', 20));
        Assert.False(File.Exists($"{_logPath}.1"), "should not have rotated yet from CategoryA alone");

        WriteLines(provider, "CategoryB", count: 10, message: new string('b', 20));
        provider.Dispose();

        Assert.True(File.Exists($"{_logPath}.1"));
        var rotated = File.ReadAllText($"{_logPath}.1");
        Assert.Contains("CategoryA", rotated, StringComparison.Ordinal);
        Assert.Contains("CategoryB", rotated, StringComparison.Ordinal);
    }

    [Fact]
    public void MaxBackupFileCountOfOne_RotatesRepeatedlyWithoutThrowing()
    {
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 1);

        for (var round = 0; round < 4; round++)
        {
            WriteLines(provider, "Cat", count: 20, message: $"round{round}-{new string('x', 15)}");
        }

        provider.Dispose();

        Assert.True(File.Exists($"{_logPath}.1"));
        Assert.False(File.Exists($"{_logPath}.2"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveMaxBackupFileCount(int invalidCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLoggerProvider(_logPath, maxBackupFileCount: invalidCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveMaxFileSizeBytes(long invalidSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileLoggerProvider(_logPath, maxFileSizeBytes: invalidSize));
    }

    // Tier C audit finding (blocker, fixed): WriteLine's own _writer.WriteLine(line) call used to be
    // unguarded -- a full disk, a removed/unmounted volume, or a dropped network path threw
    // IOException straight out of this method and into Microsoft.Extensions.Logging's own
    // AggregateException wrapping, crashing whatever caller made an ordinary logger.LogDebug(...)
    // call. Fixed with the same swallow-and-disable shape RotationFailure_...'s own test already
    // pins for the sibling reopen-after-rotation catch just below WriteLine's own new one. NOT given
    // a dedicated test: reliably provoking a genuine disk-level write failure (as opposed to the
    // File.Move-blocked-by-a-directory trick RotationFailure_... uses, which only reaches
    // TryRotateBackups, not this new catch) needs either a platform-specific trick with no portable
    // equivalent (permission changes after opening don't affect an already-open handle's writes on
    // POSIX; a locked/full/unmounted volume isn't reproducible deterministically in CI) or
    // restructuring FileLoggerProvider to accept an injectable TextWriter/Stream purely for
    // testability -- a larger change than this fix itself. The fix is a 6-line try/catch verified
    // correct by inspection, structurally identical to the already-tested sibling catch immediately
    // below it.
    [Fact]
    public void RotationFailure_FallsBackToKeepingTheLoggerAlive_InsteadOfBrickingIt()
    {
        // A directory sitting where the ".1" backup would go makes File.Move throw when
        // TryRotateBackups attempts to move the live file there -- simulates a locked/undeletable
        // backup (the real-world case: app.log.1 open in another process on Windows) without relying
        // on platform-specific file-locking behavior.
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 5);
        Directory.CreateDirectory($"{_logPath}.1");

        var duringRotation = Record.Exception(() => WriteLines(provider, "Cat", count: 20, message: new string('x', 20)));
        Assert.Null(duringRotation);

        var afterFailedRotation = Record.Exception(() =>
            provider.CreateLogger("Cat").Log(LogLevel.Information, new EventId(0), "still alive", null, (s, _) => s));
        Assert.Null(afterFailedRotation);
    }

    [Fact]
    public void WritingAfterDispose_IsANoOp_DoesNotThrow()
    {
        var provider = new FileLoggerProvider(_logPath);
        var logger = provider.CreateLogger("Cat");
        provider.Dispose();

        var exception = Record.Exception(() => logger.Log(LogLevel.Information, new EventId(0), "after dispose", null, (s, _) => s));

        Assert.Null(exception);
    }

    [Fact]
    public void DisposingTwice_DoesNotThrow()
    {
        var provider = new FileLoggerProvider(_logPath);

        provider.Dispose();
        var exception = Record.Exception(provider.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void Constructor_CreatesTheLogDirectory_WhenItDoesNotExist()
    {
        var nestedPath = Path.Combine(_directory, "nested", "deeper", "app.log");

        using var provider = new FileLoggerProvider(nestedPath);
        WriteLines(provider, "Cat", 1);
        provider.Dispose();

        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public async Task RelocateAsync_MovesTheLiveFileAndBackups_AndAWriteAfterLandsInTheNewLocation()
    {
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 5);
        WriteLines(provider, "Cat", count: 20, message: new string('x', 20));
        Assert.True(File.Exists($"{_logPath}.1"), "test setup: expected a rotated backup to exist before relocating");

        var newDirectory = Path.Combine(_directory, "relocated");
        var result = await provider.RelocateAsync(newDirectory);

        Assert.True(result);
        Assert.True(File.Exists(Path.Combine(newDirectory, "app.log")));
        Assert.True(File.Exists(Path.Combine(newDirectory, "app.log.1")));
        Assert.False(File.Exists(_logPath), "the old live file must not be left behind");
        Assert.False(File.Exists($"{_logPath}.1"), "the old backup must not be left behind");

        WriteLines(provider, "Cat", count: 1, message: "after relocate");
        provider.Dispose();

        Assert.Contains("after relocate", File.ReadAllText(Path.Combine(newDirectory, "app.log")));
    }

    [Fact]
    public async Task RelocateAsync_WhenNoLogFilesExistYet_IsANoOpSuccess()
    {
        // Constructor already created _logPath (an empty live file), so delete it to simulate the
        // "nothing written yet" case this fresh-install scenario needs.
        var provider = new FileLoggerProvider(_logPath);
        provider.Dispose();
        File.Delete(_logPath);
        var freshProvider = new FileLoggerProvider(Path.Combine(_directory, "unused-does-not-matter.log"));

        var result = await freshProvider.RelocateAsync(Path.Combine(_directory, "elsewhere"));

        Assert.True(result);
        freshProvider.Dispose();
    }

    [Fact]
    public async Task RelocateAsync_WhenTargetAlreadyHasASameNamedFile_RefusesAndLeavesTheWriterUsableAtTheOldPath()
    {
        using var provider = new FileLoggerProvider(_logPath);
        var conflictingDirectory = Path.Combine(_directory, "conflict");
        Directory.CreateDirectory(conflictingDirectory);
        File.WriteAllText(Path.Combine(conflictingDirectory, "app.log"), "someone else's file");

        var result = await provider.RelocateAsync(conflictingDirectory);

        Assert.False(result);
        Assert.True(File.Exists(_logPath), "the writer must still be usable at the old path after a refused relocate");

        var exception = Record.Exception(() => WriteLines(provider, "Cat", 1, "still alive"));
        Assert.Null(exception);
        Assert.Contains("still alive", File.ReadAllText(_logPath));
    }

    [Fact]
    public async Task RelocateAsync_WhenTargetEqualsCurrentDirectory_IsANoOpSuccess()
    {
        using var provider = new FileLoggerProvider(_logPath);

        var result = await provider.RelocateAsync(_directory);

        Assert.True(result);
        Assert.True(File.Exists(_logPath));
    }

    [Fact]
    public async Task RelocateAsync_WhenTheLiveFileWasDeletedExternally_StillRelocatesTheBackups()
    {
        // Code-review round-1 finding: an externally deleted live file (a tmp cleaner, logrotate)
        // used to always be treated as an existing source, so File.Move on it threw
        // FileNotFoundException and failed the whole relocate.
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 5);
        WriteLines(provider, "Cat", count: 20, message: new string('x', 20));
        Assert.True(File.Exists($"{_logPath}.1"), "test setup: expected a rotated backup to exist before relocating");
        File.Delete(_logPath);

        var newDirectory = Path.Combine(_directory, "live-file-deleted");
        var result = await provider.RelocateAsync(newDirectory);

        Assert.True(result);
        Assert.True(File.Exists(Path.Combine(newDirectory, "app.log.1")));

        WriteLines(provider, "Cat", count: 1, message: "after relocate");
        provider.Dispose();

        Assert.Contains("after relocate", File.ReadAllText(Path.Combine(newDirectory, "app.log")));
    }

    [Fact]
    public async Task RelocateAsync_OnADisposedProvider_IsANoOpSuccess()
    {
        var provider = new FileLoggerProvider(_logPath);
        provider.Dispose();

        var result = await provider.RelocateAsync(Path.Combine(_directory, "elsewhere"));

        Assert.True(result);
    }

    [Fact]
    public async Task RelocateAsync_WhenTheMoveFailsPartway_RollsBackAndReopensAtTheOldPath()
    {
        // A file (not a directory) sitting where the ".1" backup's DESTINATION directory would go
        // makes the second File.Move throw, simulating a mid-sequence failure after the live file
        // (sourceFiles[0]) has already moved -- proves rollback restores it, rather than leaving
        // the live file relocated while a backup failed.
        using var provider = new FileLoggerProvider(_logPath, maxFileSizeBytes: 200, maxBackupFileCount: 5);
        WriteLines(provider, "Cat", count: 20, message: new string('x', 20));
        Assert.True(File.Exists($"{_logPath}.1"), "test setup: expected a rotated backup to exist before relocating");

        var newDirectory = Path.Combine(_directory, "partial-failure");
        Directory.CreateDirectory(newDirectory);
        // Pre-create a read-only file at the backup's destination path so File.Move for THAT one
        // file throws, without the up-front conflict check catching it first (the conflict check
        // only looks at whether a file with that name already exists at the destination -- delete
        // it only after the check would have run isn't practical here, so instead make the live
        // file's own destination directory temporarily unwritable is avoided in favor of a
        // deterministic per-file trick): make ".1"'s destination a directory, which File.Exists
        // treats as "no conflicting FILE" (File.Exists is false for directories) but File.Move
        // still fails against.
        Directory.CreateDirectory(Path.Combine(newDirectory, "app.log.1"));

        var result = await provider.RelocateAsync(newDirectory);

        Assert.False(result);
        Assert.True(File.Exists(_logPath), "the live file must be rolled back to the old path");
        Assert.True(File.Exists($"{_logPath}.1"), "the backup must be rolled back to the old path");

        var exception = Record.Exception(() => WriteLines(provider, "Cat", 1, "still alive after rollback"));
        Assert.Null(exception);
        Assert.Contains("still alive after rollback", File.ReadAllText(_logPath));

        provider.Dispose();
    }
}
