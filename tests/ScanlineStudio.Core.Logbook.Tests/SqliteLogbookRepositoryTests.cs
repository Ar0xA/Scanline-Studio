using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook.Tests;

public sealed class SqliteLogbookRepositoryTests
{
    [Fact]
    public async Task AddAsync_ThenSearchAsync_RoundTripsEveryField()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var record = new QsoRecord(
                Id: "1",
                Callsign: "N0CALL",
                StartUtc: new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero),
                EndUtc: new DateTimeOffset(2026, 8, 7, 12, 5, 0, TimeSpan.Zero),
                FrequencyHz: 14230000,
                Mode: RadioMode.Usb,
                SstvModeId: "martin1",
                RstSent: "59",
                RstReceived: "58",
                Name: "Alice",
                Qth: "Somewhere",
                GridSquare: "JO31",
                Country: "Germany",
                Notes: "Nice signal",
                ReceivedImageId: "img-1",
                QslSent: true,
                QslReceived: true);

            await repository.AddAsync(record);
            var results = await repository.SearchAsync(new LogbookQuery());

            var loaded = Assert.Single(results);
            Assert.Equal(record, loaded);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task AddAsync_NullableFieldsOmitted_RoundTripsAsNull()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false);

            await repository.AddAsync(record);
            var results = await repository.SearchAsync(new LogbookQuery());

            var loaded = Assert.Single(results);
            Assert.Null(loaded.EndUtc);
            Assert.Null(loaded.FrequencyHz);
            Assert.Null(loaded.Mode);
            Assert.Null(loaded.GridSquare);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task UpdateAsync_ChangesThePersistedRecord()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false);
            await repository.AddAsync(record);

            var updated = record with { RstSent = "59", Notes = "Updated" };
            await repository.UpdateAsync(updated);
            var results = await repository.SearchAsync(new LogbookQuery());

            var loaded = Assert.Single(results);
            Assert.Equal("59", loaded.RstSent);
            Assert.Equal("Updated", loaded.Notes);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SearchAsync_FiltersByCallsign_CaseInsensitive()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            await repository.AddAsync(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false));
            await repository.AddAsync(new QsoRecord("2", "W1AW", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false));

            var results = await repository.SearchAsync(new LogbookQuery(Callsign: "n0call"));

            var loaded = Assert.Single(results);
            Assert.Equal("1", loaded.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SearchAsync_FiltersByDateRange()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var old = DateTimeOffset.UtcNow.AddDays(-10);
            var recent = DateTimeOffset.UtcNow;
            await repository.AddAsync(new QsoRecord("old", "N0CALL", old, null, null, null, null, null, null, null, null, null, null, null, null, false, false));
            await repository.AddAsync(new QsoRecord("recent", "N0CALL", recent, null, null, null, null, null, null, null, null, null, null, null, null, false, false));

            var results = await repository.SearchAsync(new LogbookQuery(From: DateTimeOffset.UtcNow.AddDays(-1)));

            var loaded = Assert.Single(results);
            Assert.Equal("recent", loaded.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SearchAsync_OrdersMostRecentFirst()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var now = DateTimeOffset.UtcNow;
            await repository.AddAsync(new QsoRecord("first", "N0CALL", now.AddMinutes(-5), null, null, null, null, null, null, null, null, null, null, null, null, false, false));
            await repository.AddAsync(new QsoRecord("second", "N0CALL", now, null, null, null, null, null, null, null, null, null, null, null, null, false, false));

            var results = await repository.SearchAsync(new LogbookQuery());

            Assert.Equal(["second", "first"], results.Select(r => r.Id));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task DeleteAsync_ExistingRow_RemovesItAndReturnsTrue()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false);
            await repository.AddAsync(record);

            var deleted = await repository.DeleteAsync("1");

            Assert.True(deleted);
            Assert.Empty(await repository.SearchAsync(new LogbookQuery()));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse_DoesNotThrow()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            var deleted = await repository.DeleteAsync("does-not-exist");

            Assert.False(deleted);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task DeleteAsync_OneOfSeveralRows_OnlyRemovesTheTargetedRow()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            await repository.AddAsync(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false));
            await repository.AddAsync(new QsoRecord("2", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false));

            await repository.DeleteAsync("1");

            var remaining = await repository.SearchAsync(new LogbookQuery());
            var surviving = Assert.Single(remaining);
            Assert.Equal("2", surviving.Id);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    /// <summary>RX/TX pipeline fix plan (2026-09-01), item 3.</summary>
    [Fact]
    public async Task GetByIdAsync_ExistingRow_ReturnsIt()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var record = new QsoRecord(
                Id: "1", Callsign: "N0CALL", StartUtc: new DateTimeOffset(2026, 8, 7, 12, 0, 0, TimeSpan.Zero),
                EndUtc: null, FrequencyHz: 14230000, Mode: RadioMode.Usb, SstvModeId: "martin1",
                RstSent: "59", RstReceived: "58", Name: "Alice", Qth: "Somewhere", GridSquare: "JO31",
                Country: "Germany", Notes: "Nice signal", ReceivedImageId: "img-1", QslSent: true, QslReceived: true);
            await repository.AddAsync(record);
            await repository.AddAsync(record with { Id = "2", Callsign = "OTHER" }); // a second row rules out a bug that ignores the WHERE clause

            var loaded = await repository.GetByIdAsync("1");

            Assert.Equal(record, loaded);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task GetByIdAsync_UnknownId_ReturnsNull_DoesNotThrow()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            await repository.AddAsync(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false));

            var loaded = await repository.GetByIdAsync("does-not-exist");

            Assert.Null(loaded);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task SearchAsync_UnrecognizedModeValue_FallsBackToUnknown_InsteadOfThrowing()
    {
        // Round-1 Tier B finding: a plain Enum.Parse<RadioMode> on the stored Mode column would
        // throw ArgumentException for one unrecognized row (a future/older app version, or a
        // hand-edited/restored-from-backup DB) and take down the ENTIRE logbook list, not just that
        // row -- unlike SqliteReceiveHistoryStore.ParseDecodeState's own defensive fallback for the
        // same class of problem. Writes the bad value directly via raw SQL, bypassing AddAsync's own
        // enum serialization (which can never itself produce an unrecognized value).
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            await repository.AddAsync(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, false, false));

            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                command.CommandText = "UPDATE Qso SET Mode = 'SomeFutureMode' WHERE Id = '1'";
                await command.ExecuteNonQueryAsync();
            }

            var results = await repository.SearchAsync(new LogbookQuery());

            var loaded = Assert.Single(results);
            Assert.Equal(RadioMode.Unknown, loaded.Mode);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_ExistingPreQslDatabase_AddsTheTwoNewColumnsLast_ExistingRowsDefaultToFalse()
    {
        // ui_transition_plan.md step 15, piece (b) -- same migration shape as
        // SqliteReceiveHistoryStoreTests's own pre-migration test: seed a pre-QSL DB with only the
        // original 15 columns via raw SQL (bypassing SqliteLogbookRepository entirely, so this test
        // doesn't depend on the very migration logic it verifies), then confirm the migration adds
        // QslSent/QslReceived LAST (matching a fresh DB's own CREATE TABLE column order) and that a
        // pre-existing row reads back with both flags false, not some other default.
        var dbPath = TempDbPath();
        try
        {
            await using (var seedConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                await seedConnection.OpenAsync();
                var create = seedConnection.CreateCommand();
                create.CommandText = """
                    CREATE TABLE Qso (
                        Id TEXT PRIMARY KEY,
                        Callsign TEXT NOT NULL,
                        StartUtc TEXT NOT NULL,
                        EndUtc TEXT NULL,
                        FrequencyHz INTEGER NULL,
                        Mode TEXT NULL,
                        SstvModeId TEXT NULL,
                        RstSent TEXT NULL,
                        RstReceived TEXT NULL,
                        Name TEXT NULL,
                        Qth TEXT NULL,
                        GridSquare TEXT NULL,
                        Country TEXT NULL,
                        Notes TEXT NULL,
                        ReceivedImageId TEXT NULL
                    )
                    """;
                await create.ExecuteNonQueryAsync();

                var insert = seedConnection.CreateCommand();
                insert.CommandText = "INSERT INTO Qso (Id, Callsign, StartUtc) VALUES ('1', 'N0CALL', $startUtc)";
                insert.Parameters.AddWithValue("$startUtc", DateTimeOffset.UtcNow.ToString("O"));
                await insert.ExecuteNonQueryAsync();
            }

            // Constructing the repository runs EnsureSchema, which must migrate this existing DB in place.
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            var columns = await ReadColumnNamesAsync(dbPath);
            Assert.Equal(
                ["Id", "Callsign", "StartUtc", "EndUtc", "FrequencyHz", "Mode", "SstvModeId", "RstSent", "RstReceived",
                    "Name", "Qth", "GridSquare", "Country", "Notes", "ReceivedImageId", "QslSent", "QslReceived"],
                columns);

            var loaded = Assert.Single(await repository.SearchAsync(new LogbookQuery()));
            Assert.False(loaded.QslSent);
            Assert.False(loaded.QslReceived);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_RunTwiceAgainstAnAlreadyMigratedDatabase_DoesNotThrow()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            // Second construction re-runs EnsureSchema against the now-already-migrated DB -- the
            // `PRAGMA table_info` probe must correctly see QslSent/QslReceived already present and
            // skip the ALTER TABLE, not throw "duplicate column name."
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            var columns = await ReadColumnNamesAsync(dbPath);
            Assert.Equal(1, columns.Count(c => c == "QslSent"));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task AddAsync_ThenSearchAsync_QslFlagsRoundTrip()
    {
        var dbPath = TempDbPath();
        try
        {
            var repository = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);
            var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null, true, false);
            await repository.AddAsync(record);

            var loaded = Assert.Single(await repository.SearchAsync(new LogbookQuery()));

            Assert.True(loaded.QslSent);
            Assert.False(loaded.QslReceived);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public async Task EnsureSchema_CreatesQsoIndexes()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            var indexNames = await ReadIndexNamesAsync(dbPath, "Qso");
            Assert.Contains("IX_Qso_StartUtc", indexNames);
            Assert.Contains("IX_Qso_Callsign_NoCase", indexNames);

            // Name-only assertions above would still pass if the index silently pointed at the
            // wrong column or dropped its collation -- the exact failure mode that would make this
            // index a no-op against SearchAsync's `Callsign = $callsign COLLATE NOCASE` query.
            var startUtcColumns = await ReadIndexColumnsAsync(dbPath, "IX_Qso_StartUtc");
            var callsignColumns = await ReadIndexColumnsAsync(dbPath, "IX_Qso_Callsign_NoCase");

            var startUtcColumn = Assert.Single(startUtcColumns);
            Assert.Equal("StartUtc", startUtcColumn.ColumnName);

            var callsignColumn = Assert.Single(callsignColumns);
            Assert.Equal("Callsign", callsignColumn.ColumnName);
            Assert.Equal("NOCASE", callsignColumn.Collation);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    private static async Task<List<string>> ReadIndexNamesAsync(string dbPath, string tableName)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list({tableName})";

        var indexNames = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexNames.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return indexNames;
    }

    /// <summary>Returns the indexed (non-rowid) columns of a single-column index, via
    /// <c>PRAGMA index_xinfo</c> (unlike <c>index_info</c>, it also reports each column's
    /// collation). SQLite appends the table's rowid as an extra key column to every index for
    /// uniqueness resolution -- filtered out here via <c>key = 1</c> since it isn't part of the
    /// column list this index was actually declared with.</summary>
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
        command.CommandText = "PRAGMA table_info(Qso)";

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }

        return columns;
    }

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"scanline-studio-logbook-test-{Guid.NewGuid()}.db");

    private static void DeleteDb(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
