using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace ScanlineStudio.Core.Logbook;

/// <summary>
/// Switches <c>history.db</c> to write-ahead logging. Shared by the two stores that open that same
/// file (<see cref="SqliteLogbookRepository"/> and <see cref="SqliteReceiveHistoryStore"/>) so the
/// two call sites cannot drift apart on the constraints below.
///
/// <para>WAL is a persistent property of the database file, not of a connection, so one successful
/// call covers every connection either store opens afterwards. Both stores calling it is harmless
/// and order-independent — the second call reads back the mode the first one set.</para>
///
/// <para>Reason for WAL: readers no longer block the writer and the writer no longer blocks
/// readers, which is what the Gallery's own query path contends with while a decode is recording.
/// This is a latency change, not a correctness fix — nothing here repairs a crash.</para>
/// </summary>
internal static partial class SqliteWriteAheadLogging
{
    /// <summary>
    /// Applies <c>PRAGMA journal_mode=WAL</c> and reports whether the database is now in WAL mode.
    /// </summary>
    /// <remarks>
    /// Two constraints, which is why this is one shared helper and why its own test asserts the mode
    /// from a SEPARATE connection:
    /// <list type="number">
    /// <item>The pragma cannot run inside a transaction. Measured, not assumed: SQLite raises
    /// <c>SqliteException</c>, "SQLite Error 1: 'cannot change into wal mode from within a
    /// transaction'". That is specific to entering or leaving WAL — transitions among the
    /// rollback-journal modes DO silently return the current mode instead. Call this after
    /// <c>Open()</c> and before any <c>BeginTransaction</c>.</item>
    /// <item>The pragma needs no other connection holding a lock on the file. A concurrent writer
    /// makes it return the old mode (or raise SQLITE_BUSY) rather than switch — this one IS the
    /// silent case, and it is why the return value is checked rather than trusting the absence of
    /// an exception.</item>
    /// </list>
    /// Both stores call this from their constructor-time <c>EnsureSchema</c>, which resolves inside
    /// DI, so a failure here must NOT throw: that would take app startup down over a latency
    /// optimisation. It logs and leaves the database in its existing journal mode, which is
    /// correct, just slower under contention.
    /// </remarks>
    public static bool TryEnable(SqliteConnection connection, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL";

            // The pragma reports the resulting mode as a scalar. Check that, do not infer success
            // from the absence of an exception -- constraint 2 above fails by returning the old
            // mode, not by raising.
            var resulting = command.ExecuteScalar() as string;
            var enabled = string.Equals(resulting, "wal", StringComparison.OrdinalIgnoreCase);

            if (!enabled)
            {
                Log.WriteAheadLoggingNotApplied(logger, resulting ?? "(null)");
            }

            return enabled;
        }
        catch (SqliteException ex)
        {
            Log.WriteAheadLoggingFailed(logger, ex);
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Debug, Message = "Database stayed in journal mode {ResultingMode} instead of WAL")]
        public static partial void WriteAheadLoggingNotApplied(ILogger logger, string resultingMode);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Enabling write-ahead logging failed; the database keeps its existing journal mode")]
        public static partial void WriteAheadLoggingFailed(ILogger logger, Exception ex);
    }
}
