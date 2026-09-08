using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

/// <summary>Regressions for two defects introduced by the earlier fix round: a deletion tombstone
/// written on every delete rather than only a failed one, and a UTC-ticks backfill that could make
/// the repository constructor fail on every launch.</summary>
public sealed class DeletionTombstoneAndMigrationTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"scanline-tombstone-{Guid.NewGuid():N}.db");

    private static void DeleteDb(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
        {
            File.Delete(dbPath);
        }
    }

    private static async Task WritePngAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(4, 4);
        await image.SaveAsPngAsync(path);
    }

    [Fact]
    public async Task DeleteAsync_SuccessfulDelete_LetsAReappearingFileBeImportedAgain()
    {
        // The operator deletes an entry, the file really goes, then they restore that same PNG from a
        // backup or the OS trash. Reconciliation exists precisely to pick up files on disk with no
        // row, and a tombstone left behind by a SUCCESSFUL delete made that permanently impossible.
        var dbPath = TempDbPath();
        var images = Path.Combine(Path.GetTempPath(), $"scanline-restore-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore();
        try
        {
            var store = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(images);
            var imagePath = Path.Combine(images, "20260908-120000000_robot36_12345678.png");
            await WritePngAsync(imagePath);
            var entry = new ReceiveHistoryEntry("deleted", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);

            Assert.True(await store.DeleteAsync(entry));
            Assert.False(File.Exists(imagePath));
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));

            await WritePngAsync(imagePath);
            Assert.Equal(1, await store.ReconcileWithDiskAsync());
            Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteAsync_FailedImageDeleteButSuccessfulAudioDelete_StillRecordsTheTombstone()
    {
        // Only the image delete's failure may write a tombstone. An audio cleanup that succeeds
        // afterwards must not clear or suppress it.
        var dbPath = TempDbPath();
        var images = Path.Combine(Path.GetTempPath(), $"scanline-audio-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore();
        try
        {
            var store = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath)
            {
                DeleteImageFileForTests = _ => throw new IOException("Injected image deletion failure"),
            };
            await store.SetImagesDirectoryAsync(images);
            var imagePath = Path.Combine(images, "20260908-130000000_robot36_abcdef12.png");
            await WritePngAsync(imagePath);
            var audioPath = Path.Combine(images, "20260908-130000000_robot36_abcdef12.wav");
            await File.WriteAllBytesAsync(audioPath, [1, 2, 3]);
            var entry = new ReceiveHistoryEntry("deleted", DateTimeOffset.UtcNow, "robot36", imagePath, null,
                ReceiveDecodeState.Completed) { AudioFilePath = audioPath };
            await store.RecordAsync(entry);

            Assert.True(await store.DeleteAsync(entry));
            Assert.True(File.Exists(imagePath));
            Assert.False(File.Exists(audioPath));

            // The tombstone must still suppress the undeletable image.
            Assert.Equal(0, await store.ReconcileWithDiskAsync());
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_TombstoneWhoseFileIsGone_IsPruned()
    {
        // A tombstone outlives its cause once the file it guarded is gone by any means. Left in
        // place it would both grow the table without bound and keep suppressing that path forever.
        var dbPath = TempDbPath();
        var images = Path.Combine(Path.GetTempPath(), $"scanline-prune-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore();
        try
        {
            var store = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath)
            {
                DeleteImageFileForTests = _ => throw new IOException("Injected image deletion failure"),
            };
            await store.SetImagesDirectoryAsync(images);
            var imagePath = Path.Combine(images, "20260908-140000000_robot36_deadbeef.png");
            await WritePngAsync(imagePath);
            var entry = new ReceiveHistoryEntry("deleted", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);
            Assert.True(await store.DeleteAsync(entry));
            Assert.Equal(1, await CountTombstonesAsync(dbPath));

            // The operator removes the stubborn file by hand. The next reconcile has nothing to
            // import, so the prune has to run before that early return.
            File.Delete(imagePath);
            Assert.Equal(0, await store.ReconcileWithDiskAsync());
            Assert.Equal(0, await CountTombstonesAsync(dbPath));

            // And with the record gone, a restored file imports normally again.
            await WritePngAsync(imagePath);
            Assert.Equal(1, await store.ReconcileWithDiskAsync());
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteAsync_FinalRowRemovalFails_LeavesAnOrdinaryRetry()
    {
        // Suppression is staged with the original row still present. Finalization failure must
        // preserve that row and its protection so ordinary Delete can retry after restart.
        var dbPath = TempDbPath();
        var images = Path.Combine(Path.GetTempPath(), $"scanline-tombstone-fail-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore();
        try
        {
            var store = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath)
            {
                // Keep the table intact: dropping it would destroy the protection being tested.
                DeleteImageFileForTests = _ =>
                {
                    using var connection = new SqliteConnection($"Data Source={dbPath}");
                    connection.Open();
                    var trigger = connection.CreateCommand();
                    trigger.CommandText = "CREATE TRIGGER fail_delete BEFORE DELETE ON ReceiveHistory BEGIN SELECT RAISE(ABORT, 'Injected finalization failure'); END";
                    trigger.ExecuteNonQuery();
                    throw new IOException("Injected image deletion failure");
                },
            };
            await store.SetImagesDirectoryAsync(images);
            var imagePath = Path.Combine(images, "20260908-170000000_robot36_0badf00d.png");
            await WritePngAsync(imagePath);
            var entry = new ReceiveHistoryEntry("deleted", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);

            await Assert.ThrowsAsync<SqliteException>(() => store.DeleteAsync(entry));
            Assert.Equal(entry.Id, Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter())).Id);
            Assert.True(File.Exists(imagePath));
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM ReceiveHistoryDeletion";
                Assert.Equal(1L, command.ExecuteScalar());
                command.CommandText = "DROP TRIGGER fail_delete";
                command.ExecuteNonQuery();
            }

            var reopened = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            Assert.True(await reopened.DeleteAsync(entry));
            Assert.Empty(await reopened.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_UnreachableImagesDirectory_KeepsTombstones()
    {
        // File.Exists is also false for "currently unreachable" -- an unmounted volume or a
        // re-pointed images folder. Pruning in that state would wipe every tombstone at once and let
        // undeletable images come back when the volume returned.
        var dbPath = TempDbPath();
        var images = Path.Combine(Path.GetTempPath(), $"scanline-unreachable-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore();
        try
        {
            var store = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath)
            {
                DeleteImageFileForTests = _ => throw new IOException("Injected image deletion failure"),
            };
            await store.SetImagesDirectoryAsync(images);
            var imagePath = Path.Combine(images, "20260908-160000000_robot36_cafebabe.png");
            await WritePngAsync(imagePath);
            var entry = new ReceiveHistoryEntry("deleted", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);
            Assert.True(await store.DeleteAsync(entry));
            Assert.Equal(1, await CountTombstonesAsync(dbPath));

            // The whole folder goes away, as an unmounted share would.
            Directory.Delete(images, recursive: true);
            Assert.Equal(0, await store.ReconcileWithDiskAsync());
            Assert.Equal(1, await CountTombstonesAsync(dbPath));

            // It comes back with the undeletable file still there: still suppressed.
            await WritePngAsync(imagePath);
            Assert.Equal(0, await store.ReconcileWithDiskAsync());
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_SuccessfulDeleteAfterScan_DoesNotImportTheGoneFile()
    {
        // With the tombstone now scoped to failed deletes, the INSERT's tombstone guard no longer
        // covers a successful delete that lands mid-reconcile. Validating a candidate is a full PNG
        // decode, so that window is real, and importing there would leave a thumbnail-less row.
        var dbPath = TempDbPath();
        var images = Path.Combine(Path.GetTempPath(), $"scanline-midscan-{Guid.NewGuid():N}");
        var settings = new FakeSettingsStore();
        var scanned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var store = new SqliteReceiveHistoryStore(settings, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath)
            {
                BeforeReconcileInsertForTests = async () =>
                {
                    scanned.SetResult();
                    await release.Task;
                },
            };
            await store.SetImagesDirectoryAsync(images);
            var imagePath = Path.Combine(images, "20260908-150000000_robot36_feedface.png");
            await WritePngAsync(imagePath);

            var reconcile = store.ReconcileWithDiskAsync();
            await scanned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var entry = new ReceiveHistoryEntry("arrived", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);
            Assert.True(await store.DeleteAsync(entry));
            Assert.False(File.Exists(imagePath));
            release.SetResult();

            Assert.Equal(0, await reconcile.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            release.TrySetResult();
            DeleteDb(dbPath);
            if (Directory.Exists(images))
            {
                Directory.Delete(images, recursive: true);
            }
        }
    }

    private static async Task<long> CountTombstonesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM ReceiveHistoryDeletion";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Constructor_UnparseableStartUtc_StillMigratesAndStarts()
    {
        // One externally written or corrupted StartUtc used to abort the migration transaction, so
        // the column was never added and the constructor threw identically on every launch -- taking
        // application startup with it, since MainViewModel resolves this repository.
        var dbPath = TempDbPath();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                var create = connection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE Qso (Id TEXT PRIMARY KEY, Callsign TEXT NOT NULL, StartUtc TEXT NOT NULL,
                      EndUtc TEXT, FrequencyHz INTEGER, Mode TEXT, SstvModeId TEXT, RstSent TEXT, RstReceived TEXT,
                      Name TEXT, Qth TEXT, GridSquare TEXT, Country TEXT, Notes TEXT, ReceivedImageId TEXT,
                      QslSent INTEGER NOT NULL DEFAULT 0, QslReceived INTEGER NOT NULL DEFAULT 0);
                    INSERT INTO Qso (Id, Callsign, StartUtc) VALUES ('good','PA0AA','2026-09-06T22:30:00.0000000+00:00');
                    INSERT INTO Qso (Id, Callsign, StartUtc) VALUES ('bad','PA0BB','not-a-timestamp');
                    """;
                await create.ExecuteNonQueryAsync();
            }

            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            Assert.Contains("StartUtcTicks", await ReadColumnNamesAsync(dbPath));
            var instant = new DateTimeOffset(2026, 9, 6, 22, 30, 0, TimeSpan.Zero);
            var found = await repository.SearchAsync(new LogbookQuery(From: instant, To: instant));
            Assert.Equal("good", Assert.Single(found).Id);
            Assert.Equal(0L, await ReadTicksAsync(dbPath, "bad"));
            Assert.Equal(instant.UtcTicks, await ReadTicksAsync(dbPath, "good"));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task Constructor_RowMissingTicks_IsBackfilledOnTheNextLaunch()
    {
        // A row written by an older build (or another tool) that does not know the column keeps a
        // correct StartUtc but ticks 0, which every date filter and the ordering would miss.
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var start = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                var insert = connection.CreateCommand();
                insert.CommandText = "INSERT INTO Qso (Id, Callsign, StartUtc) VALUES ('legacy','PA0CC',$startUtc)";
                insert.Parameters.AddWithValue("$startUtc", start.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();
            var reopened = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            Assert.Equal(start.UtcTicks, await ReadTicksAsync(dbPath, "legacy"));
            var found = await reopened.SearchAsync(new LogbookQuery(From: start, To: start));
            Assert.Equal("legacy", Assert.Single(found).Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    private static async Task<List<string>> ReadColumnNamesAsync(string dbPath)
    {
        var names = new List<string>();
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Qso')";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<long> ReadTicksAsync(string dbPath, string id)
    {
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT StartUtcTicks FROM Qso WHERE Id = $id";
        command.Parameters.AddWithValue("$id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
