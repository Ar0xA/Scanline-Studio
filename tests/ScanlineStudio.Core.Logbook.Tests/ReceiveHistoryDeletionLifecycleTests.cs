using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Imaging;
using SixLabors.ImageSharp;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class ReceiveHistoryDeletionLifecycleTests
{
    [Fact]
    public async Task FailedDelete_BlocksLateFirstRecordWhileCleanupIsRunning()
    {
        using var fixture = new Fixture();
        var entered = Gate();
        var release = Gate();
        var store = fixture.Store(delete: _ => throw new IOException("Locked image"), afterStage: async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var entry = await fixture.RecordAsync(store);
        var deletion = store.DeleteAsync(entry);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var other = fixture.Store();
            await other.RecordAsync(entry with { Id = "late-first-record" });
            Assert.Equal(entry.Id, Assert.Single(await other.QueryAsync(new ReceiveHistoryFilter())).Id);
            Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        }
        finally { release.TrySetResult(); }

        Assert.True(await deletion);
        Assert.True(File.Exists(entry.FilePath));
        Assert.Empty(await fixture.Store().QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal(0, await fixture.Store().ReconcileWithDiskAsync());
    }

    [Fact]
    public async Task UnavailableParent_StaysDeletedWhenParentReturnsAndRecorderFinishes()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = await fixture.RecordAsync(store);
        Directory.Move(fixture.Images, fixture.Offline);

        Assert.True(await store.DeleteAsync(entry));
        await store.RecordAsync(entry with { Id = "delayed-recorder" });
        Directory.Move(fixture.Offline, fixture.Images);

        Assert.Equal(0, await fixture.Store().ReconcileWithDiskAsync());
        Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.True(File.Exists(entry.FilePath));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task Prune_ExistingCurrentFolderDoesNotAuthorizeRetiringOfflineHistoricalPath()
    {
        using var fixture = new Fixture();
        var store = fixture.Store(delete: _ => throw new IOException("Locked image"));
        var entry = await fixture.RecordAsync(store);
        Assert.True(await store.DeleteAsync(entry));
        Directory.Move(fixture.Images, fixture.Offline);
        var alternate = Path.Combine(fixture.Root, "other-images");
        Directory.CreateDirectory(alternate);
        await store.SetImagesDirectoryAsync(alternate);

        Assert.Equal(0, await fixture.Store().ReconcileWithDiskAsync());
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        Directory.Move(fixture.Offline, fixture.Images);
        await store.SetImagesDirectoryAsync(fixture.Images);
        Assert.Equal(0, await store.ReconcileWithDiskAsync());
    }

    [Fact]
    public async Task ExistingRowAndTombstone_SuccessfulRetryAllowsImmediateRestore()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = await fixture.RecordAsync(store);
        fixture.Execute("INSERT INTO ReceiveHistoryDeletion (FilePath, Token) VALUES ($path, 'old')", entry.FilePath);

        Assert.True(await store.DeleteAsync(entry));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        await Fixture.WritePngAsync(entry.FilePath);
        Assert.Equal(1, await fixture.Store().ReconcileWithDiskAsync());
    }

    [Fact]
    public async Task FinalizationFailure_PreservesRowAndProtectionForOrdinaryRetryAfterRestart()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = await fixture.RecordAsync(store);
        fixture.Execute("CREATE TRIGGER block_retirement BEFORE DELETE ON ReceiveHistoryDeletion BEGIN SELECT RAISE(ABORT, 'Injected retirement failure'); END");
        var notifications = 0;
        store.Deleted += _ => notifications++;

        await Assert.ThrowsAsync<SqliteException>(() => store.DeleteAsync(entry));
        Assert.False(File.Exists(entry.FilePath));
        Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        Assert.Equal(0, notifications);

        // Reconcile must not erase an interrupted operation's retry anchor, even with the PNG gone.
        var reopened = fixture.Store();
        Assert.Equal(0, await reopened.ReconcileWithDiskAsync());
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        fixture.Execute("DROP TRIGGER block_retirement");
        Assert.True(await reopened.DeleteAsync(entry));
        Assert.Empty(await reopened.QueryAsync(new ReceiveHistoryFilter()));
        await Fixture.WritePngAsync(entry.FilePath);
        Assert.Equal(1, await reopened.ReconcileWithDiskAsync());
    }

    [Fact]
    public async Task StageFailure_LeavesRowAndFileUntouched()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = await fixture.RecordAsync(store);
        fixture.Execute("CREATE TRIGGER block_stage BEFORE INSERT ON ReceiveHistoryDeletion BEGIN SELECT RAISE(ABORT, 'Injected stage failure'); END");
        var notified = false;
        store.Deleted += _ => notified = true;

        await Assert.ThrowsAsync<SqliteException>(() => store.DeleteAsync(entry));
        Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.True(File.Exists(entry.FilePath));
        Assert.False(notified);
    }

    [Fact]
    public async Task CancellationAfterStage_LeavesOrdinaryRetryRatherThanPermanentSuppression()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var store = fixture.Store(afterStage: () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        var entry = await fixture.RecordAsync(store);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(entry, cancellation.Token));
        Assert.True(File.Exists(entry.FilePath));
        Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        Assert.True(await fixture.Store().DeleteAsync(entry));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task QueuedSecondDelete_DoesNotUnlinkRestoredFile()
    {
        using var fixture = new Fixture();
        var entered = Gate();
        var release = Gate();
        var unlinks = 0;
        var store = fixture.Store(delete: path => { Interlocked.Increment(ref unlinks); File.Delete(path); }, afterStage: async () =>
        {
            entered.TrySetResult();
            await release.Task;
        });
        var entry = await fixture.RecordAsync(store);
        var first = store.DeleteAsync(entry);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = store.DeleteAsync(entry);
        Assert.False(second.IsCompleted);
        var png = await File.ReadAllBytesAsync(entry.FilePath);
        // Restore from the completion notification. A stale queued call must inspect the missing
        // persisted ID before any file operation, whether it runs before or after this callback.
        store.Deleted += _ => File.WriteAllBytes(entry.FilePath, png);
        release.TrySetResult();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, unlinks);
        Assert.True(File.Exists(entry.FilePath));
    }

    [Fact]
    public async Task StaleCompletion_PreservesNewerTokenAndSelectedRow()
    {
        using var fixture = new Fixture();
        var store = fixture.Store(beforeFinalize: () =>
        {
            fixture.Execute("UPDATE ReceiveHistoryDeletion SET Token = 'new-owner'");
            return Task.CompletedTask;
        });
        var entry = await fixture.RecordAsync(store);

        Assert.False(await store.DeleteAsync(entry));
        Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal("new-owner", fixture.Scalar("SELECT Token FROM ReceiveHistoryDeletion"));
        Assert.True(await fixture.Store().DeleteAsync(entry));
    }

    [Fact]
    public async Task StalePrune_DoesNotRetireNewFailedDeletionAfterOldTokenWasRemoved()
    {
        using var fixture = new Fixture();
        var setup = fixture.Store(delete: _ => throw new IOException("Locked"));
        var entry = await fixture.RecordAsync(setup);
        Assert.True(await setup.DeleteAsync(entry));
        File.Delete(entry.FilePath);
        var reached = Gate();
        var release = Gate();
        var pruneStore = fixture.Store(beforePrune: async () => { reached.TrySetResult(); await release.Task; });
        var prune = pruneStore.ReconcileWithDiskAsync();
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // A second independent store retires A. Restore, record, then unsuccessfully delete B.
            Assert.Equal(0, await fixture.Store().ReconcileWithDiskAsync());
            await Fixture.WritePngAsync(entry.FilePath);
            await setup.RecordAsync(entry with { Id = "restored" });
            Assert.True(await setup.DeleteAsync(entry with { Id = "restored" }));
        }
        finally { release.TrySetResult(); }

        Assert.Equal(0, await prune.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
        Assert.Empty(await setup.QueryAsync(new ReceiveHistoryFilter()));
        Assert.True(File.Exists(entry.FilePath));
    }

    [Fact]
    public async Task SharedCanonicalImage_PreservesOtherRowsMetadataAndFiles()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = await fixture.RecordAsync(store);
        var audio = Path.Combine(fixture.Images, "shared.wav");
        await File.WriteAllBytesAsync(audio, [1, 2, 3]);
        await store.SetAudioFilePathAsync(entry.Id, audio);
        var other = entry with { Id = "other", FilePath = Path.Combine(fixture.Images, ".", Path.GetFileName(entry.FilePath)),
            Note = "Keep this annotation", IsFlagged = true, LinkedQsoId = "linked-qso", AudioFilePath = audio };
        await store.RecordAsync(other);

        Assert.True(await store.DeleteAsync(entry));
        var retained = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal(other, retained);
        Assert.True(File.Exists(entry.FilePath));
        Assert.True(File.Exists(audio));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task MissingSelectedId_DoesNotTouchRestoredButUnindexedFile()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        await Fixture.WritePngAsync(fixture.Image);
        var missing = new ReceiveHistoryEntry("absent", DateTimeOffset.UtcNow, "robot36", fixture.Image, null, ReceiveDecodeState.Completed);
        Assert.False(await store.DeleteAsync(missing));
        Assert.True(File.Exists(fixture.Image));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task MalformedImagePath_RemovesRowRetainsProtectionAndNotifiesOnce()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = new ReceiveHistoryEntry("invalid", DateTimeOffset.UtcNow, "robot36", "invalid\0.png", null, ReceiveDecodeState.Completed);
        await store.RecordAsync(entry);
        var events = 0;
        store.Deleted += _ => events++;

        Assert.True(await store.DeleteAsync(entry));
        Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal(1, events);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task ThrowingDiagnosticsAndSubscriber_DoNotChangeCommittedDeleteResult()
    {
        using var fixture = new Fixture();
        var logger = new ThrowingLogger();
        var store = fixture.Store(delete: _ => throw new IOException("Locked"), logger: logger);
        var entry = await fixture.RecordAsync(store);
        logger.Throw = true;
        store.Deleted += _ => throw new InvalidOperationException("Subscriber failed");

        Assert.True(await store.DeleteAsync(entry));
        Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task ProbeFailure_DoesNotRetireProtection()
    {
        using var fixture = new Fixture();
        var store = fixture.Store(delete: _ => throw new IOException("Locked"));
        var entry = await fixture.RecordAsync(store);
        Assert.True(await store.DeleteAsync(entry));
        File.Delete(entry.FilePath);
        var failing = fixture.Store(probe: _ => throw new UnauthorizedAccessException("Parent listing denied"));
        Assert.Equal(0, await failing.ReconcileWithDiskAsync());
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    [Fact]
    public async Task LegacyTombstoneSchema_MigratesIdempotentlyAndRemainsProtected()
    {
        using var fixture = new Fixture();
        var store = fixture.Store();
        var entry = await fixture.RecordAsync(store);
        fixture.Execute("DROP TABLE ReceiveHistoryDeletion; CREATE TABLE ReceiveHistoryDeletion (FilePath TEXT COLLATE NOCASE PRIMARY KEY); INSERT INTO ReceiveHistoryDeletion (FilePath) VALUES ($path)", entry.FilePath);

        var migrated = fixture.Store();
        _ = fixture.Store();
        Assert.Equal("", fixture.Scalar("SELECT Token FROM ReceiveHistoryDeletion"));
        Assert.True(await migrated.DeleteAsync(entry));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM ReceiveHistoryDeletion"));
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ThrowingLogger : ILogger<SqliteReceiveHistoryStore>
    {
        public bool Throw { get; set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (Throw) throw new InvalidOperationException("Logger provider failed"); }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"scanline-deletion-lifecycle-{Guid.NewGuid():N}");
        public string Images => Path.Combine(Root, "images");
        public string Offline => Path.Combine(Root, "offline");
        public string Image => Path.Combine(Images, "20260908-120000000_robot36_abcd1234.png");
        public string Database => Path.Combine(Root, "history.db");
        private readonly FakeSettingsStore _settings = new();

        public Fixture() => Directory.CreateDirectory(Images);

        public SqliteReceiveHistoryStore Store(Action<string>? delete = null, Func<Task>? afterStage = null,
            Func<Task>? beforeFinalize = null, Func<Task>? beforePrune = null,
            Func<string, SqliteReceiveHistoryStore.DeletionPathPresence>? probe = null,
            ILogger<SqliteReceiveHistoryStore>? logger = null) => new(_settings, logger ?? NullLogger<SqliteReceiveHistoryStore>.Instance, Database)
            {
                DeleteImageFileForTests = delete ?? File.Delete,
                AfterDeletionStagedForTests = afterStage,
                BeforeDeletionFinalizedForTests = beforeFinalize,
                BeforeDeletionPruneForTests = beforePrune,
                ProbeDeletionPathForTests = probe,
            };

        public async Task<ReceiveHistoryEntry> RecordAsync(SqliteReceiveHistoryStore store)
        {
            await store.SetImagesDirectoryAsync(Images);
            await WritePngAsync(Image);
            var entry = new ReceiveHistoryEntry("selected", DateTimeOffset.UtcNow, "robot36", Image, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);
            return entry;
        }

        public static async Task WritePngAsync(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4);
            await image.SaveAsPngAsync(path);
        }

        public void Execute(string sql, string? path = null)
        {
            using var connection = new SqliteConnection($"Data Source={Database}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (path is not null) command.Parameters.AddWithValue("$path", path);
            command.ExecuteNonQuery();
        }

        public object? Scalar(string sql)
        {
            using var connection = new SqliteConnection($"Data Source={Database}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}
