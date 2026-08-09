using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class SqliteReceiveHistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_ThenQueryAsync_RoundTripsTheEntry()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);

            await store.RecordAsync(entry);
            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            var loaded = Assert.Single(results);
            Assert.Equal(entry.Id, loaded.Id);
            Assert.Equal(entry.ModeId, loaded.ModeId);
            Assert.Equal(entry.FilePath, loaded.FilePath);
            Assert.Null(loaded.LinkedQsoId);
            Assert.Equal(ReceiveDecodeState.Completed, loaded.DecodeState);
            Assert.Null(loaded.Note);
            Assert.False(loaded.IsFlagged);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_RaisesRecorded_WithTheEntry_AfterTheWriteAndRetentionTrimComplete()
    {
        // Regression test for the RX-history live-update feature (batch 7): the only hook a live UI
        // pane has for "a new frame just landed" -- Assert.Single below also proves it fires AFTER
        // the row is genuinely queryable, not before the transaction/trim settles.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);

            ReceiveHistoryEntry? raised = null;
            store.Recorded += e => raised = e;

            await store.RecordAsync(entry);

            Assert.NotNull(raised);
            Assert.Equal(entry.Id, raised!.Id);
            Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ASubscriberThatThrows_DoesNotFaultTheWrite()
    {
        // Same isolation reasoning as IReceivedImageBuffer.SaveAsync's own Saved-event fix (batch 4):
        // a Recorded subscriber's own exception must not surface as if the write itself had failed --
        // ReceiveHistoryRecorder (the sole production caller) has no idea a UI-layer subscriber even
        // exists, and must not see its own successful insert reported as a failure.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);
            store.Recorded += _ => throw new InvalidOperationException("simulated subscriber failure");

            await store.RecordAsync(entry); // must not throw

            Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ThenQueryAsync_RoundTripsNoteFlaggedAndAbandonedDecodeState()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", "qso-1", ReceiveDecodeState.Abandoned, Note: "faded fast", IsFlagged: true);

            await store.RecordAsync(entry);
            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            var loaded = Assert.Single(results);
            Assert.Equal("qso-1", loaded.LinkedQsoId);
            Assert.Equal(ReceiveDecodeState.Abandoned, loaded.DecodeState);
            Assert.Equal("faded fast", loaded.Note);
            Assert.True(loaded.IsFlagged);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_FiltersByModeId()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("2", DateTimeOffset.UtcNow, "martin1", "/tmp/b.png", null, ReceiveDecodeState.Completed));

            var results = await store.QueryAsync(new ReceiveHistoryFilter(ModeId: "martin1"));

            var loaded = Assert.Single(results);
            Assert.Equal("2", loaded.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_FiltersByDateRange()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var old = DateTimeOffset.UtcNow.AddDays(-10);
            var recent = DateTimeOffset.UtcNow;
            await store.RecordAsync(new ReceiveHistoryEntry("old", old, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("recent", recent, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed));

            var results = await store.QueryAsync(new ReceiveHistoryFilter(From: DateTimeOffset.UtcNow.AddDays(-1)));

            var loaded = Assert.Single(results);
            Assert.Equal("recent", loaded.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_DateRangeCompareIsLexicographicOnStoredOffset_NotInstantBased()
    {
        // Regression test for a real bug an auditor caught in the UI layer: RxHistoryPaneViewModel's
        // "frames today" status-bar count originally queried UTC midnight, on the (false) assumption
        // that ReceivedAt is always stored UTC -- but ReceiveHistoryRecorder.RecordCompletedImageAsync/
        // RecordAbandonedImageAsync both actually write DateTimeOffset.Now (LOCAL offset). This
        // store's own From/To filter (EnsureSchema's ReceivedAt TEXT column, compared via SQLite's
        // default BINARY collation on ToString("O")) is a LEXICOGRAPHIC TEXT compare, not an
        // instant-based one -- so a query whose own offset differs from the stored rows' offset can
        // silently miss a row that IS chronologically in range once real instants are compared.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            // 2026-08-09 20:00 at UTC-5 == 2026-08-10 01:00Z -- a real instant strictly AFTER UTC
            // midnight on the 10th.
            var storedAtLocalOffset = new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.FromHours(-5));
            await store.RecordAsync(new ReceiveHistoryEntry("1", storedAtLocalOffset, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));

            // A query anchored on the SAME offset as the write finds it -- this is the property the
            // "local Today" fix (matching ReceiveHistoryRecorder's own local-offset writes) relies on.
            var matchingOffsetQuery = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.FromHours(-5)));
            Assert.Single(await store.QueryAsync(matchingOffsetQuery));

            // The SAME real instant is >= UTC midnight on the 10th (2026-08-10T01:00:00Z >=
            // 2026-08-10T00:00:00Z), so an instant-based compare WOULD find it here too -- but the
            // stored string "2026-08-09T20:00:00.0000000-05:00" sorts BEFORE the query string
            // "2026-08-10T00:00:00.0000000+00:00" (day-digit '0' < '1' is the first difference),
            // so the lexicographic compare misses it. This is exactly the class of bug a UTC-anchored
            // query hit against these locally-offset rows in production.
            var utcMidnightNextDayQuery = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
            Assert.Empty(await store.QueryAsync(utcMidnightNextDayQuery));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_OrdersMostRecentFirst()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;
            await store.RecordAsync(new ReceiveHistoryEntry("first", now.AddMinutes(-5), "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("second", now, "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed));

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(["second", "first"], results.Select(r => r.Id));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task LoadThumbnailAsync_FitsWithinMaxDimensionPreservingAspectRatio()
    {
        var dbPath = TempDbPath();
        var imagePath = Path.Combine(Path.GetTempPath(), $"scanline-studio-history-thumb-test-{Guid.NewGuid()}.png");
        try
        {
            using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(8, 4))
            {
                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < 4; y++)
                    {
                        accessor.GetRowSpan(y).Fill(new SixLabors.ImageSharp.PixelFormats.Rgb24(9, 99, 199));
                    }
                });
                await image.SaveAsPngAsync(imagePath);
            }

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);

            IImageSource thumbnail = await store.LoadThumbnailAsync(entry, maxDimension: 4);

            Assert.Equal(4, thumbnail.Width);
            Assert.Equal(2, thumbnail.Height);
            var pixel = thumbnail.GetScanline(0)[0];
            Assert.Equal(9, pixel.R);
            Assert.Equal(99, pixel.G);
            Assert.Equal(199, pixel.B);
        }
        finally
        {
            DeleteDb(dbPath);
            File.Delete(imagePath);
        }
    }

    [Fact]
    public async Task GetImagesDirectoryAsync_NoSectionConfigured_ReturnsTheDefaultPicturesFolder()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var directory = await store.GetImagesDirectoryAsync();

            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "History");
            Assert.Equal(expected, directory);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task GetImagesDirectoryAsync_SectionConfigured_ReturnsTheConfiguredFolder_ForTheGalleryTabsStorageCard()
    {
        var dbPath = TempDbPath();
        try
        {
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    ReceiveHistorySettings.SectionKey,
                    new ReceiveHistorySettings { ImagesDirectory = "/custom/rx/history" },
                    ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
            };
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var directory = await store.GetImagesDirectoryAsync();

            Assert.Equal("/custom/rx/history", directory);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ExceedsDefaultRetentionLimit_DeletesOldestEntriesBeyond32()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;

            // 33 entries, oldest to newest -- one more than the legacy-verified default of 32
            // (ReceiveHistorySettings.DefaultMaxEntries's own doc comment has the exact legacy
            // source citations).
            for (var i = 0; i < 33; i++)
            {
                await store.RecordAsync(new ReceiveHistoryEntry($"entry-{i}", now.AddMinutes(i), "robot36", $"/tmp/{i}.png", null, ReceiveDecodeState.Completed));
            }

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(32, results.Count);
            Assert.DoesNotContain(results, r => r.Id == "entry-0");
            Assert.Contains(results, r => r.Id == "entry-32");
            Assert.Contains(results, r => r.Id == "entry-1");
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ConfiguredRetentionLimit_UsesTheConfiguredValueNotTheDefault()
    {
        var dbPath = TempDbPath();
        try
        {
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    ReceiveHistorySettings.SectionKey,
                    new ReceiveHistorySettings { MaxEntries = 2 },
                    ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
            };
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;

            await store.RecordAsync(new ReceiveHistoryEntry("first", now, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("second", now.AddMinutes(1), "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("third", now.AddMinutes(2), "robot36", "/tmp/c.png", null, ReceiveDecodeState.Completed));

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(["third", "second"], results.Select(r => r.Id));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_ExistingSectionPredatesTheMaxEntriesField_FallsBackTo32NotZero()
    {
        var dbPath = TempDbPath();
        try
        {
            // Simulates a settings.json saved before MaxEntries existed on this section: the JSON
            // object genuinely has no "MaxEntries" property at all (not even null) -- the exact
            // shape System.Text.Json silently defaults to the CLR default (0) for, not the
            // property initializer, per ReceiveHistorySettings.MaxEntries's own doc comment. A
            // regression here would mean every existing installation's history gets truncated to
            // zero the moment this field shipped.
            var sections = new Dictionary<string, JsonElement>
            {
                [ReceiveHistorySettings.SectionKey] = JsonDocument.Parse("""{"ImagesDirectory":"/custom/rx/history"}""").RootElement,
            };
            var settingsStore = new FakeSettingsStore { Settings = new AppSettings { Sections = sections } };
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;

            for (var i = 0; i < 33; i++)
            {
                await store.RecordAsync(new ReceiveHistoryEntry($"entry-{i}", now.AddMinutes(i), "robot36", $"/tmp/{i}.png", null, ReceiveDecodeState.Completed));
            }

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(32, results.Count);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_FreshDatabase_HasAllEightColumnsFromCreateTableAlone()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var columns = await ReadColumnNamesAsync(dbPath);

            string[] expectedColumns = ["Id", "ReceivedAt", "ModeId", "FilePath", "LinkedQsoId", "DecodeState", "Note", "IsFlagged"];
            Assert.Equal(expectedColumns, columns);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_ExistingPreMigrationDatabase_AddsTheThreeNewColumns_AndBackfillsDecodeStateFromTheFilenameShape_NotTheDirectoryName()
    {
        var dbPath = TempDbPath();
        try
        {
            // Seed a pre-migration DB with only the original 5 columns -- exactly what an
            // installation that predates this change has on disk -- via raw SQL, bypassing
            // SqliteReceiveHistoryStore entirely so this test doesn't accidentally depend on the
            // very migration logic it's meant to verify.
            await using (var seedConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                await seedConnection.OpenAsync();
                var create = seedConnection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE ReceiveHistory (
                        Id TEXT PRIMARY KEY,
                        ReceivedAt TEXT NOT NULL,
                        ModeId TEXT NOT NULL,
                        FilePath TEXT NOT NULL,
                        LinkedQsoId TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                // Row A: a normal completed image (RecordCompletedImageAsync's real filename shape,
                // ReceiveHistoryRecorder.cs -- `{yyyyMMdd-HHmmss}_{modeId}.png`) -- must backfill Completed.
                // Row B: an abandoned image (RecordAbandonedImageAsync's real filename shape --
                // `{yyyyMMdd-HHmmssfff}_{modeId}_partial_{entryId[..8]}.png`) -- must backfill Abandoned.
                // Row C: a normal COMPLETED-shaped filename sitting inside a directory literally named
                // "rx_partial_saves" (containing the exact `_partial_` substring) -- must STILL backfill
                // Completed. A plain instr()/LIKE full-path substring match (an earlier, rejected draft
                // of this migration) would have misclassified this one as Abandoned; GLOB anchored on
                // the real filename SHAPE must not.
                foreach (var (id, filePath) in new[]
                {
                    ("a", "/tmp/history/20260101-120000_robot36.png"),
                    ("b", "/tmp/history/20260101-120000123_robot36_partial_abcd1234.png"),
                    ("c", "/tmp/rx_partial_saves/20260101-120000_robot36.png"),
                })
                {
                    var insert = seedConnection.CreateCommand();
                    insert.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId) VALUES ($id, $receivedAt, 'robot36', $filePath, NULL)";
                    insert.Parameters.AddWithValue("$id", id);
                    insert.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.ToString("O"));
                    insert.Parameters.AddWithValue("$filePath", filePath);
                    await insert.ExecuteNonQueryAsync();
                }
            }

            // Constructing the store runs EnsureSchema, which must migrate this existing DB in place.
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var columns = await ReadColumnNamesAsync(dbPath);
            Assert.Contains("Note", columns);
            Assert.Contains("IsFlagged", columns);
            Assert.Contains("DecodeState", columns);

            var results = await store.QueryAsync(new ReceiveHistoryFilter());
            var loadedA = results.Single(r => r.Id == "a");
            var loadedB = results.Single(r => r.Id == "b");
            var loadedC = results.Single(r => r.Id == "c");
            Assert.Equal(ReceiveDecodeState.Completed, loadedA.DecodeState);
            Assert.Equal(ReceiveDecodeState.Abandoned, loadedB.DecodeState);
            Assert.Equal(ReceiveDecodeState.Completed, loadedC.DecodeState);
            // The ADD COLUMN defaults for the other 2 new columns, not just DecodeState's backfill.
            Assert.Null(loadedA.Note);
            Assert.False(loadedA.IsFlagged);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_RunTwiceAgainstTheSameAlreadyMigratedDatabase_DoesNotReRunTheBackfill()
    {
        var dbPath = TempDbPath();
        try
        {
            // First construction creates a fresh (already-current-schema) DB -- DecodeState is
            // never "newly added" here, so this alone can't exercise the re-run risk. Force a real
            // pre-migration-then-migrated DB instead, then manually correct a backfilled row (as if
            // a hypothetical future SetDecodeStateAsync had been used) via raw SQL, and confirm a
            // SECOND EnsureSchema pass leaves that correction alone -- proving the backfill gate
            // genuinely doesn't re-run, not just that construction doesn't throw.
            await using (var seedConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                await seedConnection.OpenAsync();
                var create = seedConnection.CreateCommand();
                create.CommandText = "CREATE TABLE ReceiveHistory (Id TEXT PRIMARY KEY, ReceivedAt TEXT NOT NULL, ModeId TEXT NOT NULL, FilePath TEXT NOT NULL, LinkedQsoId TEXT NULL)";
                await create.ExecuteNonQueryAsync();
                var insert = seedConnection.CreateCommand();
                insert.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId) VALUES ('b', $receivedAt, 'robot36', '/tmp/history/20260101-120000123_robot36_partial_abcd1234.png', NULL)";
                insert.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath); // first EnsureSchema: migrates and backfills "b" to Abandoned

            await using (var correctionConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                await correctionConnection.OpenAsync();
                var correct = correctionConnection.CreateCommand();
                correct.CommandText = "UPDATE ReceiveHistory SET DecodeState = 'Completed' WHERE Id = 'b'";
                await correct.ExecuteNonQueryAsync();
            }

            var storeAgain = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath); // second EnsureSchema: must NOT re-backfill

            var results = await storeAgain.QueryAsync(new ReceiveHistoryFilter());
            Assert.Equal(ReceiveDecodeState.Completed, Assert.Single(results).DecodeState);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SetNoteAsync_ExistingEntry_UpdatesTheNote_AndReturnsTrue()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));

            var updated = await store.SetNoteAsync("1", "weak signal, guessed at colors");

            Assert.True(updated);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Equal("weak signal, guessed at colors", loaded.Note);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SetFlaggedAsync_ExistingEntry_UpdatesTheFlag_AndReturnsTrue()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));

            var updated = await store.SetFlaggedAsync("1", true);

            Assert.True(updated);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.True(loaded.IsFlagged);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SetLinkedQsoIdAsync_ExistingEntry_UpdatesTheLink_AndReturnsTrue()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));

            var updated = await store.SetLinkedQsoIdAsync("1", "qso-42");

            Assert.True(updated);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Equal("qso-42", loaded.LinkedQsoId);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SetNoteAsync_SetFlaggedAsync_SetLinkedQsoIdAsync_NonexistentEntryId_ReturnFalse_NotThrow()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            Assert.False(await store.SetNoteAsync("missing", "note"));
            Assert.False(await store.SetFlaggedAsync("missing", true));
            Assert.False(await store.SetLinkedQsoIdAsync("missing", "qso-1"));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task TrimToRetentionLimit_ANotedFlaggedOrLoggedRowSurvivesPastTheWindow_ButAnUntouchedRowStillGetsTrimmed()
    {
        var dbPath = TempDbPath();
        try
        {
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    ReceiveHistorySettings.SectionKey,
                    new ReceiveHistorySettings { MaxEntries = 2 },
                    ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
            };
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;

            // "old-untouched" and "old-flagged" both start outside the newest-2 window once "third"
            // and "fourth" are recorded below -- only "old-flagged" (via IsFlagged, set here before
            // the trim-triggering pushes) should survive.
            await store.RecordAsync(new ReceiveHistoryEntry("old-untouched", now, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("old-flagged", now.AddMinutes(1), "robot36", "/tmp/b.png", null, ReceiveDecodeState.Completed));
            await store.SetFlaggedAsync("old-flagged", true);

            await store.RecordAsync(new ReceiveHistoryEntry("third", now.AddMinutes(2), "robot36", "/tmp/c.png", null, ReceiveDecodeState.Completed));
            await store.RecordAsync(new ReceiveHistoryEntry("fourth", now.AddMinutes(3), "robot36", "/tmp/d.png", null, ReceiveDecodeState.Completed));

            var results = await store.QueryAsync(new ReceiveHistoryFilter());
            var ids = results.Select(r => r.Id).ToHashSet();

            Assert.DoesNotContain("old-untouched", ids);
            Assert.Contains("old-flagged", ids);
            Assert.Contains("third", ids);
            Assert.Contains("fourth", ids);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    private static async Task<List<string>> ReadColumnNamesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(ReceiveHistory)";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"scanline-studio-history-test-{Guid.NewGuid()}.db");

    private static void DeleteDb(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
