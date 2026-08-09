using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Settings;

namespace ScanlineStudio.Core.Logbook;

/// <summary>SQLite-backed <see cref="IReceiveHistoryStore"/> — see spec/07-image-pipeline.md's "RX
/// history" section. Thumbnail decode mirrors <c>ScanlineStudio.Core.Imaging</c>'s
/// <c>ImageFileLoader</c>/<c>StockImageLibrary</c> explicit-field-copy pixel pattern (never
/// <c>MemoryMarshal.Cast</c> between the two unrelated <c>Rgb24</c> types) — deliberately not
/// *sharing* code with that project, though: a <c>Core.Logbook</c> -> <c>Core.Imaging</c> reference
/// would be exactly the Core-to-Core edge <c>ReceivedImageBuffer</c>'s own doc comment (in
/// <c>Core.Imaging</c>) says must never happen, just the mirrored direction. A local
/// <see cref="IImageSource"/> holder here is cheap enough that sharing isn't worth the coupling.</summary>
public sealed partial class SqliteReceiveHistoryStore : IReceiveHistoryStore
{
    private readonly string _connectionString;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<SqliteReceiveHistoryStore> _logger;

    public event Action<ReceiveHistoryEntry>? Recorded;

    public SqliteReceiveHistoryStore(ISettingsStore settingsStore, ILogger<SqliteReceiveHistoryStore> logger, string? dbFilePath = null)
    {
        _settingsStore = settingsStore;
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

    public async Task<IReadOnlyList<ReceiveHistoryEntry>> QueryAsync(ReceiveHistoryFilter filter, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged FROM ReceiveHistory WHERE 1 = 1";

        if (filter.ModeId is not null)
        {
            command.CommandText += " AND ModeId = $modeId";
            command.Parameters.AddWithValue("$modeId", filter.ModeId);
        }

        if (filter.From is not null)
        {
            command.CommandText += " AND ReceivedAt >= $from";
            command.Parameters.AddWithValue("$from", filter.From.Value.ToString("O"));
        }

        if (filter.To is not null)
        {
            command.CommandText += " AND ReceivedAt <= $to";
            command.Parameters.AddWithValue("$to", filter.To.Value.ToString("O"));
        }

        command.CommandText += " ORDER BY ReceivedAt DESC";

        var results = new List<ReceiveHistoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(new ReceiveHistoryEntry(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                ParseDecodeState(reader.GetString(5)),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7) != 0));
        }

        return results;
    }

    /// <summary>Defensive fallback (not a throw) for a value this build doesn't recognize -- a DB
    /// written by a future build with a third <see cref="ReceiveDecodeState"/> value must not crash
    /// a downgrade back to this one; falls back to <see cref="ReceiveDecodeState.Completed"/>, the
    /// same conservative default the schema migration itself uses for pre-existing rows before
    /// backfill.</summary>
    private static ReceiveDecodeState ParseDecodeState(string value) =>
        Enum.TryParse<ReceiveDecodeState>(value, out var parsed) ? parsed : ReceiveDecodeState.Completed;

    public async Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(entry.FilePath, ct).ConfigureAwait(false);
        var (width, height) = FitWithinLongestSide(image.Width, image.Height, maxDimension);
        image.Mutate(x => x.Resize(width, height));

        var pixels = new Rgb24[width * height];
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var source = row[x];
                    pixels[(y * width) + x] = new Rgb24(source.R, source.G, source.B);
                }
            }
        });

        return new HistoryThumbnailImageSource(width, height, pixels);
    }

    public async Task RecordAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged)
            VALUES ($id, $receivedAt, $modeId, $filePath, $linkedQsoId, $decodeState, $note, $isFlagged)
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$receivedAt", entry.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("$modeId", entry.ModeId);
        command.Parameters.AddWithValue("$filePath", entry.FilePath);
        command.Parameters.AddWithValue("$linkedQsoId", (object?)entry.LinkedQsoId ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodeState", entry.DecodeState.ToString());
        command.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$isFlagged", entry.IsFlagged ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        await TrimToRetentionLimitAsync(connection, ct).ConfigureAwait(false);

        // Isolated deliberately, same reasoning as IReceivedImageBuffer.SaveAsync's own Saved-event
        // fix: a subscriber's own exception must not surface as if THIS write had failed -- the
        // insert (and trim) above already fully succeeded by this point.
        try
        {
            Recorded?.Invoke(entry);
        }
        catch (Exception ex)
        {
            Log.RecordedSubscriberFailed(_logger, entry.Id, ex);
        }
    }

    /// <summary>Legacy's real retention behavior (verified: see <see cref="ReceiveHistorySettings.DefaultMaxEntries"/>'s
    /// own doc comment for the exact source citations) is a fixed-size ring buffer — the oldest
    /// image's on-disk slot is physically overwritten once the buffer is full. This keeps the same
    /// "newest N survive" semantics for the queryable index (oldest rows beyond the limit are
    /// deleted here), but deliberately does <b>not</b> delete the corresponding image files from
    /// disk — legacy's single fixed-size history.bin blob has no equivalent to this port's
    /// separate real image files, and unsupervised automatic file deletion is a materially
    /// different risk than trimming a database index. Orphaned files beyond the retention window
    /// are a real, known follow-up (not a silent gap), not a bug in this method.
    ///
    /// <b>Deliberate behavior change (added alongside `Note`/`IsFlagged`/`LinkedQsoId`'s update
    /// methods)</b>: a row carrying any user-authored data (a note, the flagged toggle, or a linked
    /// QSO) is exempted from this trim regardless of age — the ring buffer's "newest N survive"
    /// semantics now apply only to untouched rows. Before this exemption, a background RX arriving
    /// after the entry limit would silently delete a user's note/flag/QSO-link along with the
    /// index row (the entry limit defaults to 32, roughly one afternoon of activity) with no
    /// cleanup of the now-dangling reverse FK on the logbook side (`QsoRecord.ReceivedImageId`).
    /// An exempted row's total count is therefore no longer capped at
    /// <see cref="ReceiveHistorySettings.DefaultMaxEntries"/> — it grows as user-touched rows
    /// accumulate, which is the intended tradeoff, not an oversight.</summary>
    private async Task TrimToRetentionLimitAsync(SqliteConnection connection, CancellationToken ct)
    {
        var maxEntries = await ReceiveHistorySettings.ResolveMaxEntriesAsync(_settingsStore, ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ReceiveHistory
            WHERE Id NOT IN (
                SELECT Id FROM ReceiveHistory ORDER BY ReceivedAt DESC LIMIT $maxEntries
            )
            AND Note IS NULL AND IsFlagged = 0 AND LinkedQsoId IS NULL
            """;
        command.Parameters.AddWithValue("$maxEntries", maxEntries);

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => ReceiveHistorySettings.ResolveDirectoryAsync(_settingsStore, ct);

    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET Note = $note WHERE Id = $id", entryId, "$note", (object?)note ?? DBNull.Value, ct);

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET IsFlagged = $isFlagged WHERE Id = $id", entryId, "$isFlagged", isFlagged ? 1 : 0, ct);

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET LinkedQsoId = $linkedQsoId WHERE Id = $id", entryId, "$linkedQsoId", qsoId, ct);

    /// <summary>Shared single-column-`UPDATE` implementation for <see cref="SetNoteAsync"/>/
    /// <see cref="SetFlaggedAsync"/>/<see cref="SetLinkedQsoIdAsync"/> -- three narrow,
    /// single-purpose setters (one atomic `UPDATE` each, mapping to one of three distinct,
    /// non-simultaneous user actions: commit a note / toggle a flag / click "Log entry") rather
    /// than one generic "patch" method, which would need its own which-fields-to-touch ambiguity
    /// this doesn't have. Returns whether a row was actually updated (`sqlite3_changes()` via
    /// `ExecuteNonQueryAsync`'s return value) -- <see langword="false"/> means `entryId` no longer
    /// exists (the retention-trim ring buffer can delete an untouched row between a Gallery load
    /// and a user's edit), not an exception -- callers are expected to surface that to the
    /// user.</summary>
    private async Task<bool> ExecuteUpdateAsync(string commandText, string entryId, string valueParameterName, object valueParameter, CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue(valueParameterName, valueParameter);
        command.Parameters.AddWithValue("$id", entryId);

        var rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    /// <summary>Creates the table on a fresh DB, and migrates an existing pre-`Note`/`IsFlagged`/
    /// `DecodeState` DB in place -- the first schema change this store has ever needed. Lightweight
    /// `PRAGMA table_info` probe + `ALTER TABLE ADD COLUMN` (NOT a versioned-migration framework --
    /// proportionate to a single 8-column table; do not "improve" this without a real second table
    /// to justify it). `CREATE TABLE`'s own column definitions carry the identical `DEFAULT`s the
    /// `ALTER TABLE` statements below use, AND the 3 `ALTER TABLE ADD COLUMN`s below run in the
    /// same order `CREATE TABLE` declares them (`DecodeState`, then `Note`, then `IsFlagged`) --
    /// deliberate, not incidental: SQLite's `ADD COLUMN` always appends, so a migrated DB's column
    /// ORDER would otherwise permanently diverge from a fresh DB's the moment this ships (code-level
    /// audit finding -- harmless today, since no query anywhere uses `SELECT *`, but a real,
    /// permanent schema drift baked into every already-migrated user's `history.db` if shipped
    /// wrong, and cheap to get right now). "Byte-identical" is not literally achievable either way
    /// (`sqlite_master`'s stored SQL text differs between a `CREATE TABLE` and a sequence of `ALTER
    /// TABLE`s) -- the real, load-bearing invariant is identical column set, order, and defaults.
    /// The whole probe/alter/backfill sequence runs inside one transaction:
    /// <see cref="SqliteConnection.BeginTransaction()"/> with <c>deferred: false</c> explicit (not
    /// relying on the parameterless overload's default, even though it's documented to already mean
    /// non-deferred in this library) takes the write lock up front, so two processes racing against
    /// the same `history.db` serialize correctly via Microsoft.Data.Sqlite's own busy-retry instead
    /// of both observing "column missing" and the second `ALTER` throwing from inside this
    /// constructor-time call.</summary>
    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: false);

        var createCommand = connection.CreateCommand();
        createCommand.Transaction = transaction;
        createCommand.CommandText = """
            CREATE TABLE IF NOT EXISTS ReceiveHistory (
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
        createCommand.ExecuteNonQuery();

        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var probeCommand = connection.CreateCommand();
        probeCommand.Transaction = transaction;
        probeCommand.CommandText = "PRAGMA table_info(ReceiveHistory)";
        using (var reader = probeCommand.ExecuteReader())
        {
            // Fully materialize into existingColumns before issuing any ALTER below -- running DDL
            // against a connection with a live reader still open on it is a real SQLite failure
            // mode, not just a style concern.
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(reader.GetOrdinal("name")));
            }
        }

        // Order matches CREATE TABLE's own column declaration order above (DecodeState, Note,
        // IsFlagged) -- ADD COLUMN always appends, so a migrated DB's column order would otherwise
        // permanently diverge from a fresh DB's; see this method's own doc comment.
        var decodeStateWasJustAdded = false;

        if (!existingColumns.Contains("DecodeState"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN DecodeState TEXT NOT NULL DEFAULT 'Completed'");
            decodeStateWasJustAdded = true;
        }

        if (!existingColumns.Contains("Note"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN Note TEXT NULL");
        }

        if (!existingColumns.Contains("IsFlagged"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN IsFlagged INTEGER NOT NULL DEFAULT 0");
        }

        // Backfill ONLY when DecodeState was newly added THIS pass -- never on subsequent startups,
        // and never for a fresh DB (CREATE TABLE already gave it the right default). GLOB, not
        // LIKE/instr: anchors on the real filename SHAPE ReceiveHistoryRecorder.RecordAbandonedImageAsync
        // writes (`..._partial_<8-char-id>.png`), not a full-path substring match -- LIKE's `_`
        // wildcard + case-insensitivity, or a plain instr() substring match, can both false-positive
        // against a user's IMAGES DIRECTORY happening to contain "partial" anywhere in its own name
        // (e.g. a folder named `rx_partial_saves`), silently misclassifying every completed image in
        // that installation. GLOB is case-sensitive and treats `_` as a literal character (only
        // `*`/`?`/`[...]` are wildcards), so it can't match a directory-name coincidence.
        if (decodeStateWasJustAdded)
        {
            ExecuteNonQuery(connection, transaction, "UPDATE ReceiveHistory SET DecodeState = 'Abandoned' WHERE FilePath GLOB '*_partial_????????.png'");
        }

        transaction.Commit();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static (int Width, int Height) FitWithinLongestSide(int width, int height, int maxDimension)
    {
        if (width <= maxDimension && height <= maxDimension)
        {
            return (width, height);
        }

        var scale = width >= height ? (double)maxDimension / width : (double)maxDimension / height;
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static string GetDefaultDbFilePath()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appDataDirectory, "ScanlineStudio", "history.db");
    }

    private sealed class HistoryThumbnailImageSource : IImageSource
    {
        private readonly Rgb24[] _pixels;

        public HistoryThumbnailImageSource(int width, int height, Rgb24[] pixels)
        {
            Width = width;
            Height = height;
            _pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public ReadOnlySpan<Rgb24> GetScanline(int y) => _pixels.AsSpan(y * Width, Width);
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "A Recorded event subscriber threw for entry {EntryId}")]
        public static partial void RecordedSubscriberFailed(ILogger logger, string entryId, Exception ex);
    }
}
