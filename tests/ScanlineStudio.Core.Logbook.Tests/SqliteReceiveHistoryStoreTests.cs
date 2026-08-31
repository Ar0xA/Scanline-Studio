using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
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
            Assert.Null(loaded.FrequencyHz);
            Assert.Null(loaded.RigMode);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    /// <summary>ui_transition_plan.md step 6 (T2-4).</summary>
    [Fact]
    public async Task RecordAsync_ThenQueryAsync_RoundTripsFrequencyHzAndRigMode()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry(
                "1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed,
                FrequencyHz: 14_230_000, RigMode: RadioMode.Usb);

            await store.RecordAsync(entry);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));

            Assert.Equal(14_230_000, loaded.FrequencyHz);
            Assert.Equal(RadioMode.Usb, loaded.RigMode);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    /// <summary>No radio connected at the moment of reception -- null, never a fake zero. Same
    /// convention <see cref="RadioState"/>'s own doc comment establishes for its optional fields.
    /// </summary>
    [Fact]
    public async Task RecordAsync_WithNoFrequencyOrRigMode_RoundTripsAsNull()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed);

            await store.RecordAsync(entry);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));

            Assert.Null(loaded.FrequencyHz);
            Assert.Null(loaded.RigMode);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    /// <summary>Auditor code-review finding (2026-08-29): mirrors
    /// SqliteLogbookRepositoryTests.SearchAsync_UnrecognizedModeValue_FallsBackToUnknown_InsteadOfThrowing
    /// -- <see cref="SqliteReceiveHistoryStore"/>'s own <c>ParseRigMode</c> claims the identical
    /// DBNull-means-null / garbage-means-Unknown-not-throw contract, but nothing exercised the
    /// garbage-string half of it. Writes the bad value via raw SQL, bypassing RecordAsync's own enum
    /// serialization (which can never itself produce an unrecognized value).</summary>
    [Fact]
    public async Task QueryAsync_UnrecognizedRigModeValue_FallsBackToUnknown_InsteadOfThrowing()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed, FrequencyHz: 14_230_000, RigMode: RadioMode.Usb));

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE ReceiveHistory SET RigMode = 'SomeFutureMode' WHERE Id = '1'";
                await command.ExecuteNonQueryAsync();
            }

            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));

            Assert.Equal(RadioMode.Unknown, loaded.RigMode);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task RecordAsync_RaisesRecorded_WithTheEntry_AfterTheWriteCompletes()
    {
        // Regression test for the RX-history live-update feature (batch 7): the only hook a live UI
        // pane has for "a new frame just landed" -- Assert.Single below also proves it fires AFTER
        // the row is genuinely queryable, not before the insert settles.
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
    public async Task QueryAsync_DateRangeCompareIsInstantBased_NotLexicographicOnStoredOffset()
    {
        // T1-16 (production_audit.md), regression test for a real bug an auditor caught in the UI
        // layer: RxHistoryPaneViewModel's "frames today" status-bar count originally queried UTC
        // midnight, on the (false) assumption that ReceivedAt is always stored UTC -- but
        // ReceiveHistoryRecorder.RecordCompletedImageAsync/RecordAbandonedImageAsync both actually
        // write DateTimeOffset.Now (LOCAL offset). This store's own From/To filter USED TO compare
        // that column directly via SQLite's default BINARY collation on ToString("O") -- a
        // LEXICOGRAPHIC TEXT compare, not an instant-based one -- so a query whose own offset
        // differed from the stored rows' offset could silently miss a row that IS chronologically in
        // range once real instants are compared. Fixed: queries now filter on a separate, always-UTC
        // ReceivedAtUtc column (see that column's own doc comment above SqliteReceiveHistoryStore.
        // EnsureSchema) -- this test used to assert the BUGGY (miss) result as expected; flipped to
        // assert the correct one once the fix landed.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            // 2026-08-09 20:00 at UTC-5 == 2026-08-10 01:00Z -- a real instant strictly AFTER UTC
            // midnight on the 10th.
            var storedAtLocalOffset = new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.FromHours(-5));
            await store.RecordAsync(new ReceiveHistoryEntry("1", storedAtLocalOffset, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));

            // A query anchored on the SAME offset as the write still finds it.
            var matchingOffsetQuery = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.FromHours(-5)));
            Assert.Single(await store.QueryAsync(matchingOffsetQuery));

            // The SAME real instant is >= UTC midnight on the 10th (2026-08-10T01:00:00Z >=
            // 2026-08-10T00:00:00Z) -- the OLD lexicographic compare missed this (the stored string
            // "2026-08-09T20:00:00.0000000-05:00" sorts BEFORE the query string
            // "2026-08-10T00:00:00.0000000+00:00", day-digit '0' < '1' being the first difference,
            // even though the real instants are correctly ordered the other way). The fixed,
            // instant-based compare finds it.
            var utcMidnightNextDayQuery = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
            Assert.Single(await store.QueryAsync(utcMidnightNextDayQuery));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task QueryAsync_DateRangeCompareAcrossARealDstTransition_IsInstantBased()
    {
        // T1-16 (production_audit.md): the previous test's UTC-vs-local mismatch is an ARTIFICIAL
        // trigger (a caller anchoring at UTC when it should anchor local) -- this one shows the same
        // class of bug was reachable with NO caller mistake at all: two real US-Eastern offsets
        // (EST/-05:00 before, EDT/-04:00 after) straddling an actual DST-forward transition
        // (2026-03-08 02:00 EST -> 03:00 EDT, the 2nd Sunday in March). ReceiveHistoryRecorder's own
        // DateTimeOffset.Now would genuinely produce rows in both offsets across that boundary on a
        // real US-Eastern machine.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            // 01:30 EST == 06:30Z -- captured shortly before the DST jump.
            var storedJustBeforeDstJump = new DateTimeOffset(2026, 3, 8, 1, 30, 0, TimeSpan.FromHours(-5));
            await store.RecordAsync(new ReceiveHistoryEntry("1", storedJustBeforeDstJump, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));

            // A query anchored at 02:00 EDT == 06:00Z -- a real instant BEFORE the stored row
            // (06:30Z), so an instant-based compare correctly finds it. The OLD lexicographic compare
            // did not: "...T01:30:00...-05:00" sorts BEFORE "...T02:00:00...-04:00" (the hour digit
            // '1' < '2' is the first difference), so the stored row would have read as NOT >= the
            // query -- a false miss despite the real instants being correctly ordered the other way,
            // with neither offset being a caller mistake -- both are genuine EST/EDT wall-clock
            // offsets for this exact date.
            var queryJustAfterDstJump = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 3, 8, 2, 0, 0, TimeSpan.FromHours(-4)));
            Assert.Single(await store.QueryAsync(queryJustAfterDstJump));
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
    public async Task LoadThumbnailAsync_ExifOrientation_FitsUsingPostOrientDimensions()
    {
        // T1-15 (production_audit.md): this method used to skip AutoOrient entirely, the same bug
        // class already fixed once for StockImageLibrary (Core.Imaging) -- mirrors that class's own
        // LoadThumbnailAsync_ExifOrientation_FitsUsingPostOrientDimensions test exactly. A 4x2
        // source rotated to 2x4 is now TALL, so fitting within maxDimension=4 should produce a
        // 2-wide x 4-tall thumbnail, not the pre-orient 4-wide x 2-tall shape.
        var dbPath = TempDbPath();
        var imagePath = Path.Combine(Path.GetTempPath(), $"scanline-studio-history-thumb-exif-test-{Guid.NewGuid()}.jpg");
        try
        {
            using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Rgb24>(4, 2))
            {
                image.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
                image.Metadata.ExifProfile.SetValue(SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Orientation, (ushort)6);
                await image.SaveAsJpegAsync(imagePath);
            }

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);

            var thumbnail = await store.LoadThumbnailAsync(entry, maxDimension: 4);

            Assert.Equal(2, thumbnail.Width);
            Assert.Equal(4, thumbnail.Height);
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
    public async Task SetImagesDirectoryAsync_ValidDirectory_CreatesItAndPersistsIt()
    {
        var dbPath = TempDbPath();
        var targetDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-images-test-{Guid.NewGuid()}");
        try
        {
            var settingsStore = new FakeSettingsStore();
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            Assert.False(Directory.Exists(targetDirectory));

            await store.SetImagesDirectoryAsync(targetDirectory);

            // Validated by actually creating the directory (plan-review finding) -- not just a
            // string written to settings with nothing checking it's ever usable.
            Assert.True(Directory.Exists(targetDirectory));
            Assert.Equal(targetDirectory, await store.GetImagesDirectoryAsync());
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task SetImagesDirectoryAsync_RelativePath_PersistsTheResolvedAbsolutePath()
    {
        // Auditor-caught (2026-08-26): a relative value would otherwise get created under the
        // process's current CWD, "succeed," then re-resolve to a DIFFERENT real location on the
        // next launch (ReceiveHistoryRecorder re-resolves this setting on every save, not once at
        // startup) -- persisting the resolved absolute form makes it stable across process restarts.
        var dbPath = TempDbPath();
        var relativeName = $"scanline-studio-images-test-{Guid.NewGuid()}";
        var expectedAbsolute = Path.GetFullPath(relativeName);
        try
        {
            var settingsStore = new FakeSettingsStore();
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            await store.SetImagesDirectoryAsync(relativeName);

            Assert.True(Path.IsPathRooted(await store.GetImagesDirectoryAsync()));
            Assert.Equal(expectedAbsolute, await store.GetImagesDirectoryAsync());
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(expectedAbsolute))
            {
                Directory.Delete(expectedAbsolute);
            }
        }
    }

    [Fact]
    public async Task SetImagesDirectoryAsync_NullOrWhitespace_ResetsToTheDefaultPicturesFolder()
    {
        var dbPath = TempDbPath();
        var targetDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-images-test-{Guid.NewGuid()}");
        try
        {
            var settingsStore = new FakeSettingsStore();
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(targetDirectory);
            Assert.Equal(targetDirectory, await store.GetImagesDirectoryAsync());

            await store.SetImagesDirectoryAsync("   ");

            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "History");
            Assert.Equal(expected, await store.GetImagesDirectoryAsync());
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory);
            }
        }
    }

    [Fact]
    public async Task SetImagesDirectoryAsync_PathIsActuallyAFile_ThrowsAndDoesNotPersist()
    {
        // The Storage settings dialog's own reason for validating up front (plan-review finding):
        // without this, a typo/permission problem would only surface later as every subsequent RX
        // image silently failing to save (ReceiveHistoryRecorder's own write path is fire-and-forget
        // with no user-visible failure surface).
        var dbPath = TempDbPath();
        var conflictingFile = Path.Combine(Path.GetTempPath(), $"scanline-studio-images-test-file-{Guid.NewGuid()}");
        File.WriteAllText(conflictingFile, "not a directory");
        try
        {
            var settingsStore = new FakeSettingsStore();
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var invalidTarget = Path.Combine(conflictingFile, "sub");

            await Assert.ThrowsAnyAsync<IOException>(() => store.SetImagesDirectoryAsync(invalidTarget));

            // The default, unchanged -- the failed attempt never reached SaveAsync.
            var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScanlineStudio", "History");
            Assert.Equal(expected, await store.GetImagesDirectoryAsync());
        }
        finally
        {
            DeleteDb(dbPath);
            File.Delete(conflictingFile);
        }
    }

    [Fact]
    public async Task RecordAsync_ManyEntries_KeepsEveryEntry_NoAutomaticRetentionTrim()
    {
        // User decision (2026-08-26): the Gallery tab's "All" filter must show every entry ever
        // recorded, not the newest N -- see docs/removed-features.md's "RX history retention limit"
        // entry for the full reasoning. Pins the reversal directly: more than the OLD legacy-derived
        // default of 32 survive.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;

            for (var i = 0; i < 33; i++)
            {
                await store.RecordAsync(new ReceiveHistoryEntry($"entry-{i}", now.AddMinutes(i), "robot36", $"/tmp/{i}.png", null, ReceiveDecodeState.Completed));
            }

            var results = await store.QueryAsync(new ReceiveHistoryFilter());

            Assert.Equal(33, results.Count);
            Assert.Contains(results, r => r.Id == "entry-0");
            Assert.Contains(results, r => r.Id == "entry-32");
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_FreshDatabase_HasAllElevenColumnsFromCreateTableAlone()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var columns = await ReadColumnNamesAsync(dbPath);

            string[] expectedColumns = ["Id", "ReceivedAt", "ModeId", "FilePath", "LinkedQsoId", "DecodeState", "Note", "IsFlagged", "FrequencyHz", "RigMode", "AudioFilePath", "ReceivedAtUtc"];
            Assert.Equal(expectedColumns, columns);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    /// <summary>Auditor plan-review finding (2026-08-29): every OTHER migration test in this file
    /// seeds a 5-column pre-`Note`/`IsFlagged`/`DecodeState` DB, but every CURRENT user's
    /// `history.db` already has all 8 of those columns -- this is the actual migration path
    /// FrequencyHz/RigMode ship against in production, and nothing exercised it before this test.
    /// Also asserts column ORDER equality against a fresh DB -- the method's own doc comment claims
    /// this invariant is protected, but no prior test actually checked it.</summary>
    [Fact]
    public async Task EnsureSchema_ExistingEightColumnDatabase_AddsFrequencyHzAndRigMode_InTheSameOrderAsAFreshDatabase()
    {
        var dbPath = TempDbPath();
        try
        {
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
                        LinkedQsoId TEXT NULL,
                        DecodeState TEXT NOT NULL DEFAULT 'Completed',
                        Note TEXT NULL,
                        IsFlagged INTEGER NOT NULL DEFAULT 0
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                var insert = seedConnection.CreateCommand();
                insert.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged) VALUES ('a', $receivedAt, 'robot36', '/tmp/a.png', NULL, 'Completed', NULL, 0)";
                insert.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var migratedColumns = await ReadColumnNamesAsync(dbPath);
            string[] expectedColumns = ["Id", "ReceivedAt", "ModeId", "FilePath", "LinkedQsoId", "DecodeState", "Note", "IsFlagged", "FrequencyHz", "RigMode", "AudioFilePath", "ReceivedAtUtc"];
            Assert.Equal(expectedColumns, migratedColumns);

            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Null(loaded.FrequencyHz);
            Assert.Null(loaded.RigMode);
            Assert.Null(loaded.AudioFilePath);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    /// <summary>ui_transition_plan.md step 12 (Auto-save RX audio): same reasoning as the
    /// FrequencyHz/RigMode migration test above -- seeds a DB at the shape every CURRENT user's
    /// `history.db` actually has (10 columns, pre-`AudioFilePath`), the real migration path this
    /// column ships against in production, and asserts column ORDER equality (AudioFilePath
    /// appended last) against a fresh DB.</summary>
    [Fact]
    public async Task EnsureSchema_ExistingTenColumnDatabase_AddsAudioFilePath_InTheSameOrderAsAFreshDatabase()
    {
        var dbPath = TempDbPath();
        try
        {
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
                        LinkedQsoId TEXT NULL,
                        DecodeState TEXT NOT NULL DEFAULT 'Completed',
                        Note TEXT NULL,
                        IsFlagged INTEGER NOT NULL DEFAULT 0,
                        FrequencyHz INTEGER NULL,
                        RigMode TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                var insert = seedConnection.CreateCommand();
                insert.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged, FrequencyHz, RigMode) VALUES ('a', $receivedAt, 'robot36', '/tmp/a.png', NULL, 'Completed', NULL, 0, NULL, NULL)";
                insert.Parameters.AddWithValue("$receivedAt", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var migratedColumns = await ReadColumnNamesAsync(dbPath);
            string[] expectedColumns = ["Id", "ReceivedAt", "ModeId", "FilePath", "LinkedQsoId", "DecodeState", "Note", "IsFlagged", "FrequencyHz", "RigMode", "AudioFilePath", "ReceivedAtUtc"];
            Assert.Equal(expectedColumns, migratedColumns);

            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Null(loaded.AudioFilePath);

            // Idempotent across two startups (plan doc Verification: "Migration adds the column in
            // the right position and is idempotent across two startups") -- a second store instance
            // against the ALREADY-migrated DB must not throw (a naive unconditional ALTER TABLE would
            // fail with "duplicate column name").
            var secondStore = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            Assert.Equal(expectedColumns, await ReadColumnNamesAsync(dbPath));
            _ = secondStore;
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
            Assert.Null(loadedA.FrequencyHz);
            Assert.Null(loadedA.RigMode);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_ExistingPreT1_16Database_AddsReceivedAtUtc_BackfillsItFromReceivedAt_AndDropsTheOldIndex()
    {
        // T1-16 (production_audit.md): seeds a pre-fix DB with the full 11-column schema (every
        // column up to and including AudioFilePath) MINUS ReceivedAtUtc, plus the OLD
        // IX_ReceiveHistory_ReceivedAt index this fix drops -- exactly what a real installation that
        // predates this change has on disk, backfill AND index migration both exercised together.
        var dbPath = TempDbPath();
        try
        {
            var storedAtLocalOffset = new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.FromHours(-5));
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
                        LinkedQsoId TEXT NULL,
                        DecodeState TEXT NOT NULL DEFAULT 'Completed',
                        Note TEXT NULL,
                        IsFlagged INTEGER NOT NULL DEFAULT 0,
                        FrequencyHz INTEGER NULL,
                        RigMode TEXT NULL,
                        AudioFilePath TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                var createOldIndex = seedConnection.CreateCommand();
                createOldIndex.CommandText = "CREATE INDEX IX_ReceiveHistory_ReceivedAt ON ReceiveHistory(ReceivedAt)";
                await createOldIndex.ExecuteNonQueryAsync();

                var insert = seedConnection.CreateCommand();
                insert.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged, FrequencyHz, RigMode, AudioFilePath) VALUES ('a', $receivedAt, 'robot36', '/tmp/a.png', NULL, 'Completed', NULL, 0, NULL, NULL, NULL)";
                insert.Parameters.AddWithValue("$receivedAt", storedAtLocalOffset.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var migratedColumns = await ReadColumnNamesAsync(dbPath);
            string[] expectedColumns = ["Id", "ReceivedAt", "ModeId", "FilePath", "LinkedQsoId", "DecodeState", "Note", "IsFlagged", "FrequencyHz", "RigMode", "AudioFilePath", "ReceivedAtUtc"];
            Assert.Equal(expectedColumns, migratedColumns);

            var indexNames = await ReadIndexNamesAsync(dbPath);
            Assert.DoesNotContain("IX_ReceiveHistory_ReceivedAt", indexNames);
            Assert.Contains("IX_ReceiveHistory_ReceivedAtUtc", indexNames);

            // Read the raw backfilled value directly -- proves the backfill computed the CORRECT UTC
            // instant, not just "some non-null string". Must match exactly what RecordAsync would
            // have written for the same source value (entry.ReceivedAt.UtcDateTime.ToString("O")).
            await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            await verifyConnection.OpenAsync();
            var select = verifyConnection.CreateCommand();
            select.CommandText = "SELECT ReceivedAtUtc FROM ReceiveHistory WHERE Id = 'a'";
            var rawUtc = (string)(await select.ExecuteScalarAsync())!;
            Assert.Equal(storedAtLocalOffset.UtcDateTime.ToString("O"), rawUtc);

            // And confirm the fix actually works end-to-end through the backfilled column: a query
            // anchored at UTC midnight on the 10th (the exact scenario the pinned-bug test above used)
            // now finds this migrated row too, not just a freshly-recorded one.
            var utcMidnightNextDayQuery = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
            Assert.Single(await store.QueryAsync(utcMidnightNextDayQuery));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_ReceivedAtUtcLeftNullByAnOlderBuildOrForeignWriter_SelfHealsOnTheNextStartup()
    {
        // Auditor code-review finding (2026-08-31): the first-pass fix gated the ReceivedAtUtc
        // backfill on "only when the column was newly added this pass" (matching DecodeState's own
        // gate) -- but unlike DecodeState's one-time heuristic backfill, that gate was WRONG here: a
        // NULL ReceivedAtUtc silently excludes a row from every filtered query (WHERE
        // ReceivedAtUtc >= $from evaluates to SQL NULL) and buries it at the bottom of the
        // unfiltered "All" view (ORDER BY sorts NULLs last) -- and with a one-time gate, nothing
        // would ever repair a row that went NULL AFTER the column already existed (e.g. an older
        // build's own INSERT, which doesn't know about this column, running against an
        // already-migrated history.db -- this file's own ParseDecodeState doc comment already
        // designs for exactly this "a downgrade... " scenario being real, not hypothetical). Fixed:
        // the backfill now runs unconditionally, scoped to `WHERE ReceivedAtUtc IS NULL`, every
        // startup. This test seeds an ALREADY-migrated DB (has the ReceivedAtUtc column, matching
        // today's real schema) with one row that has it NULL -- simulating exactly that older-build
        // scenario -- and confirms the very next store construction heals it.
        var dbPath = TempDbPath();
        try
        {
            var storedAtLocalOffset = new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.FromHours(-5));
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
                        LinkedQsoId TEXT NULL,
                        DecodeState TEXT NOT NULL DEFAULT 'Completed',
                        Note TEXT NULL,
                        IsFlagged INTEGER NOT NULL DEFAULT 0,
                        FrequencyHz INTEGER NULL,
                        RigMode TEXT NULL,
                        AudioFilePath TEXT NULL,
                        ReceivedAtUtc TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                var insert = seedConnection.CreateCommand();
                insert.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged, FrequencyHz, RigMode, AudioFilePath, ReceivedAtUtc) VALUES ('a', $receivedAt, 'robot36', '/tmp/a.png', NULL, 'Completed', NULL, 0, NULL, NULL, NULL, NULL)";
                insert.Parameters.AddWithValue("$receivedAt", storedAtLocalOffset.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            await verifyConnection.OpenAsync();
            var select = verifyConnection.CreateCommand();
            select.CommandText = "SELECT ReceivedAtUtc FROM ReceiveHistory WHERE Id = 'a'";
            var rawUtc = (string)(await select.ExecuteScalarAsync())!;
            Assert.Equal(storedAtLocalOffset.UtcDateTime.ToString("O"), rawUtc);

            // And the row is now genuinely visible through a filtered query, not just non-null.
            var utcMidnightNextDayQuery = new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
            Assert.Single(await store.QueryAsync(utcMidnightNextDayQuery));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_OneRowHasAnUnparseableReceivedAt_DoesNotCrashStartup_StillBackfillsTheOtherRow()
    {
        // Auditor code-review finding (2026-08-31): the backfill's DateTimeOffset.Parse used to be
        // unguarded -- a single unparseable ReceivedAt value (hand-edited DB, a foreign writer) would
        // have thrown out of the constructor, from inside DI resolution, and would have kept throwing
        // on EVERY subsequent launch (the transaction rolls back, so the row stays NULL, re-triggering
        // the same throw). Fixed: log and leave that one row's own ReceivedAtUtc NULL instead. This
        // does not fully repair that row (it stays invisible from filtered queries, same reasoning as
        // the self-healing test above -- there is no correct UTC value to derive from unparseable
        // text), but the app starts, and every OTHER row still backfills correctly.
        var dbPath = TempDbPath();
        try
        {
            var goodRowLocalOffset = new DateTimeOffset(2026, 8, 9, 20, 0, 0, TimeSpan.FromHours(-5));
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

                var insertBad = seedConnection.CreateCommand();
                insertBad.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId) VALUES ('bad', 'not-a-real-timestamp', 'robot36', '/tmp/bad.png', NULL)";
                await insertBad.ExecuteNonQueryAsync();

                var insertGood = seedConnection.CreateCommand();
                insertGood.CommandText = "INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId) VALUES ('good', $receivedAt, 'robot36', '/tmp/good.png', NULL)";
                insertGood.Parameters.AddWithValue("$receivedAt", goodRowLocalOffset.ToString("O"));
                await insertGood.ExecuteNonQueryAsync();
            }

            // Must not throw -- this is the regression itself.
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            await using var verifyConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            await verifyConnection.OpenAsync();
            var select = verifyConnection.CreateCommand();
            select.CommandText = "SELECT Id, ReceivedAtUtc FROM ReceiveHistory ORDER BY Id";
            var values = new Dictionary<string, string?>();
            await using (var reader = await select.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    values[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
                }
            }

            Assert.Null(values["bad"]);
            Assert.Equal(goodRowLocalOffset.UtcDateTime.ToString("O"), values["good"]);

            // The good row is fully usable through the public API too.
            var found = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter(From: new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero))));
            Assert.Equal("good", found.Id);
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
    public async Task ClearLinkedQsoIdAsync_EntryLinkedToTheDeletedQso_ClearsItAndReturnsOne()
    {
        // ui_transition_plan.md step 15 -- the fix for the round-1 plan-review blocker: a QSO
        // delete must not leave ReceiveHistory.LinkedQsoId dangling, or the Gallery reports that
        // frame as permanently "Logged" against a QSO that no longer exists.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.SetLinkedQsoIdAsync("1", "qso-42");

            var cleared = await store.ClearLinkedQsoIdAsync("qso-42");

            Assert.Equal(1, cleared);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Null(loaded.LinkedQsoId);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task ClearLinkedQsoIdAsync_NoEntryLinkedToThatQso_ReturnsZero_LeavesOtherLinksUntouched()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.RecordAsync(new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/a.png", null, ReceiveDecodeState.Completed));
            await store.SetLinkedQsoIdAsync("1", "qso-other");

            var cleared = await store.ClearLinkedQsoIdAsync("qso-does-not-exist");

            Assert.Equal(0, cleared);
            var loaded = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Equal("qso-other", loaded.LinkedQsoId);
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

    // ui_transition_plan.md step 4 (T1-4, reframed): per-item manual delete.

    [Fact]
    public async Task DeleteAsync_ExistingEntry_RemovesRowAndFile_ReturnsTrue_RaisesDeleted()
    {
        var dbPath = TempDbPath();
        var imagePath = Path.Combine(Path.GetTempPath(), $"scanline-studio-delete-test-{Guid.NewGuid()}.png");
        try
        {
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);

            ReceiveHistoryEntry? raised = null;
            store.Deleted += e => raised = e;

            var deleted = await store.DeleteAsync(entry);

            Assert.True(deleted);
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.False(File.Exists(imagePath));
            Assert.NotNull(raised);
            Assert.Equal("1", raised!.Id);
        }
        finally
        {
            DeleteDb(dbPath);
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }
        }
    }

    [Fact]
    public async Task DeleteAsync_FileAlreadyMissingOnDisk_StillRemovesTheRow_ReturnsTrue_DoesNotThrow()
    {
        // The referenced file being gone already is an expected, tolerated state (a manual
        // on-disk delete, or a prior partial cleanup) -- not an error this method should surface.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/does-not-exist-scanline-studio.png", null, ReceiveDecodeState.Completed);
            await store.RecordAsync(entry);

            var deleted = await store.DeleteAsync(entry);

            Assert.True(deleted);
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: delete-linked-WAV.

    [Fact]
    public async Task DeleteAsync_ExistingEntryWithLinkedAudio_RemovesRowImageAndAudioFile()
    {
        var dbPath = TempDbPath();
        var imagePath = Path.Combine(Path.GetTempPath(), $"scanline-studio-delete-test-{Guid.NewGuid()}.png");
        var audioPath = Path.Combine(Path.GetTempPath(), $"scanline-studio-delete-test-{Guid.NewGuid()}.wav");
        try
        {
            await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
            await File.WriteAllBytesAsync(audioPath, [4, 5, 6]);
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", imagePath, null, ReceiveDecodeState.Completed) { AudioFilePath = audioPath };
            await store.RecordAsync(entry);

            var deleted = await store.DeleteAsync(entry);

            Assert.True(deleted);
            Assert.False(File.Exists(imagePath));
            Assert.False(File.Exists(audioPath));
        }
        finally
        {
            DeleteDb(dbPath);
            if (File.Exists(imagePath))
            {
                File.Delete(imagePath);
            }

            if (File.Exists(audioPath))
            {
                File.Delete(audioPath);
            }
        }
    }

    [Fact]
    public async Task DeleteAsync_AudioFileAlreadyMissingOnDisk_StillRemovesTheRow_DoesNotThrow()
    {
        // Same tolerated-state reasoning as DeleteAsync_FileAlreadyMissingOnDisk_StillRemovesTheRow_
        // ReturnsTrue_DoesNotThrow above, applied to the linked audio file.
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var entry = new ReceiveHistoryEntry("1", DateTimeOffset.UtcNow, "robot36", "/tmp/does-not-exist-scanline-studio.png", null, ReceiveDecodeState.Completed)
            {
                AudioFilePath = "/tmp/does-not-exist-scanline-studio.wav",
            };
            await store.RecordAsync(entry);

            var deleted = await store.DeleteAsync(entry);

            Assert.True(deleted);
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task DeleteAsync_NonexistentEntryId_ReturnsFalse_DoesNotRaiseDeleted()
    {
        var dbPath = TempDbPath();
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            var raised = false;
            store.Deleted += _ => raised = true;

            var deleted = await store.DeleteAsync(new ReceiveHistoryEntry("missing", DateTimeOffset.UtcNow, "robot36", "/tmp/whatever.png", null, ReceiveDecodeState.Completed));

            Assert.False(deleted);
            Assert.False(raised);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    // Disk/DB reconciliation (user-reported gap, 2026-08-26): files can exist on disk with no
    // matching history row. ReconcileWithDiskAsync scans the configured images directory and
    // backfills entries for files matching ReceiveHistoryRecorder's own naming convention.

    [Fact]
    public async Task ReconcileWithDiskAsync_CompletedAndAbandonedFiles_ImportsBothWithCorrectMetadata()
    {
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);

            var completedPath = Path.Combine(imagesDirectory, "20260826-143052123_robot36_a1b2c3d4.png");
            var abandonedPath = Path.Combine(imagesDirectory, "20260826-150000000_martin-m1_partial_e5f6a7b8.png");
            File.WriteAllBytes(completedPath, []);
            File.WriteAllBytes(abandonedPath, []);

            var imported = await store.ReconcileWithDiskAsync();

            Assert.Equal(2, imported);
            var entries = (await store.QueryAsync(new ReceiveHistoryFilter())).OrderBy(e => e.ReceivedAt).ToList();
            Assert.Equal(2, entries.Count);

            var completed = entries.Single(e => e.FilePath == completedPath);
            Assert.Equal("robot36", completed.ModeId);
            Assert.Equal(ReceiveDecodeState.Completed, completed.DecodeState);
            var expectedCompletedLocal = new DateTime(2026, 8, 26, 14, 30, 52, 123);
            Assert.Equal(expectedCompletedLocal, completed.ReceivedAt.LocalDateTime);
            // Auditor-caught: asserting LocalDateTime alone passes identically for a UTC-reconstructed
            // (TimeSpan.Zero) offset on a UTC-configured CI leg -- this is what actually discriminates
            // local-vs-UTC reconstruction, not just the wall-clock display value.
            Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(expectedCompletedLocal), completed.ReceivedAt.Offset);

            var abandoned = entries.Single(e => e.FilePath == abandonedPath);
            Assert.Equal("martin-m1", abandoned.ModeId);
            Assert.Equal(ReceiveDecodeState.Abandoned, abandoned.DecodeState);
            Assert.Equal(new DateTime(2026, 8, 26, 15, 0, 0, 0), abandoned.ReceivedAt.LocalDateTime);
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_PreFixCompletedFile_NoMillisecondsNoIdToken_IsStillImported()
    {
        // Auditor-caught (2026-08-26): ReceiveHistoryRecorder's completed-image filename scheme used
        // to be second-granularity with no uniqueness token at all (docs/functional-audit-playbook.md's
        // own record of the later collision fix) before becoming the current ms+id8 scheme -- any
        // install that predates that fix has files in exactly this older shape, and recovering them
        // is this whole feature's actual point.
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);

            var oldShapePath = Path.Combine(imagesDirectory, "20260826-143052_robot36.png");
            File.WriteAllBytes(oldShapePath, []);

            var imported = await store.ReconcileWithDiskAsync();

            Assert.Equal(1, imported);
            var entry = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Equal("robot36", entry.ModeId);
            Assert.Equal(ReceiveDecodeState.Completed, entry.DecodeState);
            Assert.Equal(new DateTime(2026, 8, 26, 14, 30, 52), entry.ReceivedAt.LocalDateTime);
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_RealUserReportedFile_IsImported()
    {
        // Regression fixture from a real user-reported install, 2026-08-27:
        // "20260825-004916556_martin-m2_1a081cf8.png". An earlier report of this same filename had a
        // transcription typo dropping one hex character from the id token; the user corrected it, and
        // the real id ("1a081cf8") is a normal 8-character entryId[..8] fragment -- no pattern change
        // was needed. Kept as a real-sample regression guard rather than deleted as pure duplication.
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);

            var realPath = Path.Combine(imagesDirectory, "20260825-004916556_martin-m2_1a081cf8.png");
            File.WriteAllBytes(realPath, []);

            var imported = await store.ReconcileWithDiskAsync();

            Assert.Equal(1, imported);
            var entry = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Equal("martin-m2", entry.ModeId);
            Assert.Equal(ReceiveDecodeState.Completed, entry.DecodeState);
            Assert.Equal(new DateTime(2026, 8, 25, 0, 49, 16, 556), entry.ReceivedAt.LocalDateTime);
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_FilenameMatchesPatternButDateIsInvalid_IsSkipped()
    {
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);
            // Month 13 -- matches the regex shape exactly (8 digits, 6 digits, mode, .png) but isn't
            // a real calendar date.
            File.WriteAllBytes(Path.Combine(imagesDirectory, "20261345-120000_robot36.png"), []);

            var imported = await store.ReconcileWithDiskAsync();

            Assert.Equal(0, imported);
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_FileAlreadyInDatabase_IsNotDuplicated()
    {
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);

            var filePath = Path.Combine(imagesDirectory, "20260826-143052123_robot36_a1b2c3d4.png");
            File.WriteAllBytes(filePath, []);
            await store.RecordAsync(new ReceiveHistoryEntry("existing-id", DateTimeOffset.Now, "robot36", filePath, null, ReceiveDecodeState.Completed));

            var imported = await store.ReconcileWithDiskAsync();

            Assert.Equal(0, imported);
            var entry = Assert.Single(await store.QueryAsync(new ReceiveHistoryFilter()));
            Assert.Equal("existing-id", entry.Id); // the ORIGINAL row, not a reconciled duplicate
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_FileNotMatchingNamingConvention_IsSkipped()
    {
        // A manual copy, a different app's export, or a genuinely foreign file -- inventing
        // ReceivedAt/ModeId for it would be fabricated data, not a real reconciliation.
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);
            File.WriteAllBytes(Path.Combine(imagesDirectory, "vacation-photo.png"), []);

            var imported = await store.ReconcileWithDiskAsync();

            Assert.Equal(0, imported);
            Assert.Empty(await store.QueryAsync(new ReceiveHistoryFilter()));
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_ImagesDirectoryDoesNotExist_ReturnsZero_DoesNotThrow()
    {
        var dbPath = TempDbPath();
        try
        {
            var settingsStore = new FakeSettingsStore
            {
                Settings = new AppSettings().WithSection(
                    ReceiveHistorySettings.SectionKey,
                    new ReceiveHistorySettings { ImagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-never-created-{Guid.NewGuid()}") },
                    ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings),
            };
            var store = new SqliteReceiveHistoryStore(settingsStore, NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            Assert.Equal(0, await store.ReconcileWithDiskAsync());
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task ReconcileWithDiskAsync_ImportedEntries_DoNotRaiseRecorded()
    {
        // Load-bearing, not a style choice: Recorded means "a frame just landed" -- a historical
        // backfill firing it would pollute RxImagePaneViewModel.PreviousFrames (a SESSION-only list)
        // with old, already-on-disk frames. See ReconcileWithDiskAsync's own interface doc comment.
        var dbPath = TempDbPath();
        var imagesDirectory = Path.Combine(Path.GetTempPath(), $"scanline-studio-reconcile-test-{Guid.NewGuid()}");
        try
        {
            var store = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            await store.SetImagesDirectoryAsync(imagesDirectory);
            File.WriteAllBytes(Path.Combine(imagesDirectory, "20260826-143052123_robot36_a1b2c3d4.png"), []);

            var raisedCount = 0;
            store.Recorded += _ => raisedCount++;

            await store.ReconcileWithDiskAsync();

            Assert.Equal(0, raisedCount);
        }
        finally
        {
            DeleteDb(dbPath);
            if (Directory.Exists(imagesDirectory))
            {
                Directory.Delete(imagesDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task EnsureSchema_CreatesReceiveHistoryIndexes()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            var indexNames = await ReadIndexNamesAsync(dbPath);

            // T1-16 (production_audit.md): IX_ReceiveHistory_ReceivedAt (on the old, no-longer-queried
            // ReceivedAt column) is gone -- QueryAsync now filters/sorts on ReceivedAtUtc exclusively.
            Assert.DoesNotContain("IX_ReceiveHistory_ReceivedAt", indexNames);
            Assert.Contains("IX_ReceiveHistory_ReceivedAtUtc", indexNames);
            Assert.Contains("IX_ReceiveHistory_FilePath", indexNames);
            Assert.Contains("IX_ReceiveHistory_LinkedQsoId", indexNames);

            // Name-only assertions above would still pass if an index silently pointed at the
            // wrong column -- check each index actually indexes the column its name claims.
            Assert.Equal("ReceivedAtUtc", Assert.Single(await ReadIndexColumnsAsync(dbPath, "IX_ReceiveHistory_ReceivedAtUtc")).ColumnName);
            Assert.Equal("FilePath", Assert.Single(await ReadIndexColumnsAsync(dbPath, "IX_ReceiveHistory_FilePath")).ColumnName);
            Assert.Equal("LinkedQsoId", Assert.Single(await ReadIndexColumnsAsync(dbPath, "IX_ReceiveHistory_LinkedQsoId")).ColumnName);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    private static async Task<List<string>> ReadIndexNamesAsync(string dbPath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list(ReceiveHistory)";

        var indexNames = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexNames.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return indexNames;
    }

    /// <summary>Returns the indexed (non-rowid) columns of a single-column index, via
    /// <c>PRAGMA index_xinfo</c>. SQLite appends the table's rowid as an extra key column to every
    /// index for uniqueness resolution -- filtered out here via <c>key = 1</c> since it isn't part
    /// of the column list this index was actually declared with.</summary>
    private static async Task<List<(string ColumnName, string Collation)>> ReadIndexColumnsAsync(string dbPath, string indexName)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_xinfo({indexName})";

        var columns = new List<(string, string)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetInt64(reader.GetOrdinal("key")) == 0)
            {
                continue;
            }

            columns.Add((reader.GetString(reader.GetOrdinal("name")), reader.GetString(reader.GetOrdinal("coll"))));
        }

        return columns;
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
