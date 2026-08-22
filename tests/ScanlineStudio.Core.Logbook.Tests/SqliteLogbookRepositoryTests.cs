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
                ReceivedImageId: "img-1");

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
            var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null);

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
            var record = new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null);
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
            await repository.AddAsync(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null));
            await repository.AddAsync(new QsoRecord("2", "W1AW", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null));

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
            await repository.AddAsync(new QsoRecord("old", "N0CALL", old, null, null, null, null, null, null, null, null, null, null, null, null));
            await repository.AddAsync(new QsoRecord("recent", "N0CALL", recent, null, null, null, null, null, null, null, null, null, null, null, null));

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
            await repository.AddAsync(new QsoRecord("first", "N0CALL", now.AddMinutes(-5), null, null, null, null, null, null, null, null, null, null, null, null));
            await repository.AddAsync(new QsoRecord("second", "N0CALL", now, null, null, null, null, null, null, null, null, null, null, null, null));

            var results = await repository.SearchAsync(new LogbookQuery());

            Assert.Equal(["second", "first"], results.Select(r => r.Id));
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
            await repository.AddAsync(new QsoRecord("1", "N0CALL", DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null, null, null, null));

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

    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"scanline-studio-logbook-test-{Guid.NewGuid()}.db");

    private static void DeleteDb(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
