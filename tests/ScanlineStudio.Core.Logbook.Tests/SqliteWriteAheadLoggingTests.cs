using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook.Tests;

/// <summary>
/// Pins that both stores actually put <c>history.db</c> into WAL mode, and that the mode persists.
///
/// <para>Every assertion here reads the journal mode from a SEPARATE connection, never the one that
/// ran the pragma. That is the whole point: when the pragma cannot switch the mode against a locked
/// file it reports the CURRENT mode instead of failing, so a test that asked the setting connection
/// would pass without proving anything.</para>
/// </summary>
public sealed class SqliteWriteAheadLoggingTests
{
    private static string TempDbPath() => Path.Combine(Path.GetTempPath(), $"scanline-wal-{Guid.NewGuid():N}.db");

    private static void DeleteDb(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static string ReadJournalModeFromAFreshConnection(string dbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode";
        return (command.ExecuteScalar() as string) ?? string.Empty;
    }

    [Fact]
    public void ReceiveHistoryStoreConstructor_PutsTheDatabaseIntoWalMode()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);

            Assert.Equal("wal", ReadJournalModeFromAFreshConnection(dbPath), ignoreCase: true);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public void LogbookRepositoryConstructor_PutsTheDatabaseIntoWalMode()
    {
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            Assert.Equal("wal", ReadJournalModeFromAFreshConnection(dbPath), ignoreCase: true);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public void WalMode_SurvivesEveryConnectionClosing()
    {
        // WAL is a property of the file, not of a connection. This is what lets one pragma at
        // schema-ensure time cover all 13 later OpenAsync sites instead of repeating it at each.
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            SqliteConnection.ClearAllPools();

            Assert.Equal("wal", ReadJournalModeFromAFreshConnection(dbPath), ignoreCase: true);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public void BothStoresSharingOneFile_AgreeOnWalMode()
    {
        // The two stores deliberately share history.db. Whichever constructs second reads back the
        // mode the first one set rather than fighting it, and neither throws.
        var dbPath = TempDbPath();
        try
        {
            _ = new SqliteReceiveHistoryStore(new FakeSettingsStore(), NullLogger<SqliteReceiveHistoryStore>.Instance, dbPath);
            _ = new SqliteLogbookRepository(NullLogger<SqliteLogbookRepository>.Instance, dbPath);

            Assert.Equal("wal", ReadJournalModeFromAFreshConnection(dbPath), ignoreCase: true);
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }

    [Fact]
    public void TryEnable_InsideATransaction_ReportsFailureRatherThanClaimingSuccess()
    {
        // The mutation gate for the "must sit above BeginTransaction" comment at both call sites.
        // Asserts the MECHANISM, not just the boolean: SQLite raises rather than no-opping, so
        // TryEnable's catch is load-bearing and must not be dropped as redundant.
        var dbPath = TempDbPath();
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS Probe (Id TEXT)";
            command.ExecuteNonQuery();

            using var transaction = connection.BeginTransaction(deferred: false);

            using var raw = connection.CreateCommand();
            raw.CommandText = "PRAGMA journal_mode=WAL";
            var thrown = Assert.Throws<SqliteException>(() => raw.ExecuteScalar());
            Assert.Contains("wal mode from within a transaction", thrown.Message, StringComparison.OrdinalIgnoreCase);

            // And TryEnable turns that into a false, without letting it escape into a constructor.
            Assert.False(SqliteWriteAheadLogging.TryEnable(connection, NullLogger.Instance));
        }
        finally
        {
            DeleteDb(dbPath);
        }
    }
}
