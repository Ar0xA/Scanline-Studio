using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using ScanlineStudio.Abstractions.Logbook;
using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Core.Logbook;

/// <summary>SQLite-backed <see cref="ILogbookRepository"/> — see spec/08-logging.md's "Storage"
/// section. Deliberately shares the same <c>history.db</c> file (and default path) as
/// <see cref="SqliteReceiveHistoryStore"/>, per that section's own note that a QSO row can
/// foreign-key to a received-image row directly; <see cref="Abstractions.Imaging.ReceiveHistoryEntry.LinkedQsoId"/>
/// already anticipates this table's <c>Id</c> column. No retention/trimming here -- a QSO log is
/// a durable record a ham operator relies on, never a bounded ring buffer; RX history itself no
/// longer trims either, as of the 2026-08-26 removal (`docs/removed-features.md`).</summary>
public sealed partial class SqliteLogbookRepository : ILogbookRepository
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteLogbookRepository> _logger;

    public SqliteLogbookRepository(ILogger<SqliteLogbookRepository> logger, string? dbFilePath = null)
    {
        _logger = logger;

        var path = dbFilePath ?? GetDefaultDbFilePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        EnsureSchema();
    }

    public async Task<QsoRecord> AddAsync(QsoRecord record, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Qso (Id, Callsign, StartUtc, EndUtc, FrequencyHz, Mode, SstvModeId, RstSent, RstReceived, Name, Qth, GridSquare, Country, Notes, ReceivedImageId)
            VALUES ($id, $callsign, $startUtc, $endUtc, $frequencyHz, $mode, $sstvModeId, $rstSent, $rstReceived, $name, $qth, $gridSquare, $country, $notes, $receivedImageId)
            """;
        BindParameters(command, record);

        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            Log.QsoWriteFailed(_logger, record.Id, ex);
            throw;
        }

        Log.QsoAdded(_logger, record.Id, record.Callsign);
        return record;
    }

    public async Task UpdateAsync(QsoRecord record, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Qso SET
                Callsign = $callsign,
                StartUtc = $startUtc,
                EndUtc = $endUtc,
                FrequencyHz = $frequencyHz,
                Mode = $mode,
                SstvModeId = $sstvModeId,
                RstSent = $rstSent,
                RstReceived = $rstReceived,
                Name = $name,
                Qth = $qth,
                GridSquare = $gridSquare,
                Country = $country,
                Notes = $notes,
                ReceivedImageId = $receivedImageId
            WHERE Id = $id
            """;
        BindParameters(command, record);

        try
        {
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            Log.QsoWriteFailed(_logger, record.Id, ex);
            throw;
        }

        Log.QsoUpdated(_logger, record.Id);
    }

    public async Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Callsign, StartUtc, EndUtc, FrequencyHz, Mode, SstvModeId, RstSent, RstReceived, Name, Qth, GridSquare, Country, Notes, ReceivedImageId
            FROM Qso WHERE 1 = 1
            """;

        if (query.Callsign is not null)
        {
            command.CommandText += " AND Callsign = $callsign COLLATE NOCASE";
            command.Parameters.AddWithValue("$callsign", query.Callsign);
        }

        if (query.From is not null)
        {
            command.CommandText += " AND StartUtc >= $from";
            command.Parameters.AddWithValue("$from", query.From.Value.ToString("O"));
        }

        if (query.To is not null)
        {
            command.CommandText += " AND StartUtc <= $to";
            command.Parameters.AddWithValue("$to", query.To.Value.ToString("O"));
        }

        command.CommandText += " ORDER BY StartUtc DESC";

        var results = new List<QsoRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new QsoRecord(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : ParseMode(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
        }

        return results;
    }

    /// <summary>Falls back to <see cref="RadioMode.Unknown"/> rather than throwing (that value's own
    /// doc comment requires this) -- a plain <c>Enum.Parse</c> would take down the entire logbook
    /// list over one row a future/older app version or a hand-edited DB wrote a value this build
    /// doesn't recognize into, matching <c>SqliteReceiveHistoryStore.ParseDecodeState</c>'s own
    /// defensive pattern for the same class of problem.</summary>
    private static RadioMode ParseMode(string value) =>
        Enum.TryParse<RadioMode>(value, out var parsed) ? parsed : RadioMode.Unknown;

    private static void BindParameters(SqliteCommand command, QsoRecord record)
    {
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$callsign", record.Callsign);
        command.Parameters.AddWithValue("$startUtc", record.StartUtc.ToString("O"));
        command.Parameters.AddWithValue("$endUtc", (object?)record.EndUtc?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$frequencyHz", (object?)record.FrequencyHz ?? DBNull.Value);
        command.Parameters.AddWithValue("$mode", (object?)record.Mode?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$sstvModeId", (object?)record.SstvModeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$rstSent", (object?)record.RstSent ?? DBNull.Value);
        command.Parameters.AddWithValue("$rstReceived", (object?)record.RstReceived ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)record.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("$qth", (object?)record.Qth ?? DBNull.Value);
        command.Parameters.AddWithValue("$gridSquare", (object?)record.GridSquare ?? DBNull.Value);
        command.Parameters.AddWithValue("$country", (object?)record.Country ?? DBNull.Value);
        command.Parameters.AddWithValue("$notes", (object?)record.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$receivedImageId", (object?)record.ReceivedImageId ?? DBNull.Value);
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Qso (
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
        command.ExecuteNonQuery();
    }

    private static string GetDefaultDbFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "ScanlineStudio", "history.db");
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "QSO logged: {Id} ({Callsign})")]
        public static partial void QsoAdded(ILogger logger, string id, string callsign);

        [LoggerMessage(Level = LogLevel.Information, Message = "QSO updated: {Id}")]
        public static partial void QsoUpdated(ILogger logger, string id);

        [LoggerMessage(Level = LogLevel.Error, Message = "Failed to write QSO {Id} to the logbook database")]
        public static partial void QsoWriteFailed(ILogger logger, string id, Exception ex);
    }
}
