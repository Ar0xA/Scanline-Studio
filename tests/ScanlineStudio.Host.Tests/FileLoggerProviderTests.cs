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
}
