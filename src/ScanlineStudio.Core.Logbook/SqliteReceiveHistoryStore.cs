using System.Text.RegularExpressions;
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

        // Round-2 user decision (2026-08-26): no automatic retention trim -- the Gallery tab's own
        // "All" filter must show every entry ever recorded, not the newest N. Legacy's own fixed-size
        // ring buffer (sys.m_HistMax = 32, see docs/removed-features.md) has no "show everything"
        // concept to preserve either -- this is new UI this port added, and its meaning is this
        // project's own call, not a legacy-fidelity question. See that doc entry for the full
        // reasoning and citations; do not reintroduce a silent row-deletion pass without raising it
        // with the user first.

        // Isolated deliberately, same reasoning as IReceivedImageBuffer.SaveAsync's own Saved-event
        // fix: a subscriber's own exception must not surface as if THIS write had failed -- the
        // insert above already fully succeeded by this point.
        try
        {
            Recorded?.Invoke(entry);
        }
        catch (Exception ex)
        {
            Log.RecordedSubscriberFailed(_logger, entry.Id, ex);
        }
    }

    public Task<string> GetImagesDirectoryAsync(CancellationToken ct = default) => ReceiveHistorySettings.ResolveDirectoryAsync(_settingsStore, ct);

    /// <summary>See <see cref="IReceiveHistoryStore.SetImagesDirectoryAsync"/>.</summary>
    public async Task SetImagesDirectoryAsync(string? directory, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(directory) ? null : directory;
        if (normalized is not null)
        {
            // Auditor-caught (2026-08-26): persist the RESOLVED absolute path, not whatever the
            // user typed. A relative path (or literal "~", which .NET does not shell-expand) would
            // otherwise get created under the process's current CWD, "succeed," and then re-resolve
            // to a DIFFERENT real location on the next launch -- ReceiveHistoryRecorder.cs's own
            // ResolveDirectoryAsync call re-resolves this same setting on every save, not once at
            // startup, so a relative value is silently unstable across process restarts.
            normalized = Path.GetFullPath(normalized);

            // Validate BEFORE persisting -- see this method's own interface doc comment for why.
            // Deliberately left to throw straight out of this method; the caller (the Storage
            // settings dialog) surfaces the real exception instead of a generic failure.
            Directory.CreateDirectory(normalized);
        }

        var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var current = settings.GetSection(ReceiveHistorySettings.SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings) ?? new ReceiveHistorySettings();
        var updated = settings.WithSection(ReceiveHistorySettings.SectionKey, current with { ImagesDirectory = normalized }, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        await _settingsStore.SaveAsync(updated, ct).ConfigureAwait(false);
        Log.ImagesDirectorySet(_logger, normalized);
    }

    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET Note = $note WHERE Id = $id", entryId, "$note", (object?)note ?? DBNull.Value, ct);

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET IsFlagged = $isFlagged WHERE Id = $id", entryId, "$isFlagged", isFlagged ? 1 : 0, ct);

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET LinkedQsoId = $linkedQsoId WHERE Id = $id", entryId, "$linkedQsoId", qsoId, ct);

    /// <summary>See <see cref="IReceiveHistoryStore.ReconcileWithDiskAsync"/>. Matches
    /// <c>ReceiveHistoryRecorder</c>'s CURRENT filename shapes (`RecordCompletedImageAsync` writes
    /// <c>{yyyyMMdd-HHmmssfff}_{modeId}_{entryId8}.png</c>, `RecordAbandonedImageAsync` adds a
    /// `_partial_` marker before the id token) AND its pre-fix completed-image shape,
    /// <c>{yyyyMMdd-HHmmss}_{modeId}.png</c> (no milliseconds, no id token) -- auditor-caught,
    /// 2026-08-26: `docs/functional-audit-playbook.md`'s own record of that filename fix confirms
    /// files this old genuinely exist on disk for any install that predates it, and this feature's
    /// whole point is recovering exactly those files. See <see cref="FilenamePattern"/>'s own
    /// doc comment for the two-shape grammar.</summary>
    public async Task<int> ReconcileWithDiskAsync(CancellationToken ct = default)
    {
        var directory = await GetImagesDirectoryAsync(ct).ConfigureAwait(false);
        if (!Directory.Exists(directory))
        {
            // Nothing saved yet (or the configured folder was moved/deleted) -- not an error, just
            // nothing to reconcile. Directory.GetFiles below would throw on a missing directory.
            return 0;
        }

        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var query = connection.CreateCommand();
            query.CommandText = "SELECT FilePath FROM ReceiveHistory";
            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                // Auditor-caught: a single malformed stored path (empty string, or Windows-invalid
                // characters from a hand-edited settings.json) used to throw here and abort the WHOLE
                // reconcile -- with _hasReconciledDiskThisSession already latched true by the caller,
                // that meant no retry until the app restarted. Isolated per-row instead: one bad
                // stored path just can't be matched against disk (never treated as "existing"),
                // everything else still reconciles normally.
                var storedPath = reader.GetString(0);
                try
                {
                    existingPaths.Add(Path.GetFullPath(storedPath));
                }
                catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
                {
                    Log.ReconcileSkippedUnrecognizedFile(_logger, storedPath);
                }
            }
        }

        var toImport = new List<ReceiveHistoryEntry>();
        foreach (var filePath in Directory.GetFiles(directory, "*.png"))
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(filePath);
            }
            catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
            {
                Log.ReconcileSkippedUnrecognizedFile(_logger, filePath);
                continue;
            }

            if (existingPaths.Contains(fullPath))
            {
                continue;
            }

            var match = FilenamePattern().Match(Path.GetFileName(filePath));
            if (!match.Success)
            {
                Log.ReconcileSkippedUnrecognizedFile(_logger, filePath);
                continue;
            }

            // Two shapes share one pattern: ms (9-digit HHmmssfff) when present, else the pre-fix
            // 6-digit HHmmss form -- see FilenamePattern's own doc comment for why both are real.
            var hasMilliseconds = match.Groups["ms"].Success;
            var format = hasMilliseconds ? "yyyyMMdd-HHmmssfff" : "yyyyMMdd-HHmmss";
            if (!DateTime.TryParseExact(match.Groups["timestamp"].Value, format, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var localTimestamp))
            {
                Log.ReconcileSkippedUnrecognizedFile(_logger, filePath);
                continue;
            }

            // The recorder's own filename timestamp is wall-clock local (ReceiveHistoryRecorder.cs
            // stamps DateTimeOffset.Now) -- reconstruct the SAME kind here, not UTC, matching what
            // every genuinely-recorded row already stores.
            var receivedAt = new DateTimeOffset(localTimestamp, TimeZoneInfo.Local.GetUtcOffset(localTimestamp));
            var decodeState = match.Groups["partial"].Success ? ReceiveDecodeState.Abandoned : ReceiveDecodeState.Completed;
            toImport.Add(new ReceiveHistoryEntry(Guid.NewGuid().ToString(), receivedAt, match.Groups["mode"].Value, fullPath, LinkedQsoId: null, decodeState));
        }

        if (toImport.Count == 0)
        {
            return 0;
        }

        var importedCount = 0;
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            foreach (var entry in toImport)
            {
                var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                // Auditor-caught TOCTOU: a frame ReceiveHistoryRecorder is actively saving writes its
                // PNG (SaveSnapshotAsync) BEFORE its own RecordAsync call inserts the real row -- if
                // the Gallery tab is selected inside that window, the SELECT above can see the file
                // with no row yet, and a plain INSERT here would then race RecordAsync's own insert
                // into a genuine duplicate row for the same FilePath (no UNIQUE constraint on that
                // column to reject it). WHERE NOT EXISTS makes this insert a no-op instead, checked
                // against the live table at INSERT time, not just the SELECT snapshot taken above.
                // Untested defense-in-depth, honestly: no seam exists in this class to inject a real
                // row landing between the SELECT above and this INSERT (would need a mid-transaction
                // hook), so no test exercises this WHERE clause specifically -- the earlier, simpler
                // "row already existed before reconcile ever ran" case (SqliteReceiveHistoryStoreTests'
                // own ReconcileWithDiskAsync_FileAlreadyInDatabase_IsNotDuplicated) is covered by the
                // SELECT-snapshot dedup above this loop instead, and is NOT the same code path as this
                // guard. An earlier version of this test suite had a test CLAIMING to cover this race
                // that didn't (auditor-caught) -- removed rather than left as false confidence.
                insert.CommandText = """
                    INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged)
                    SELECT $id, $receivedAt, $modeId, $filePath, $linkedQsoId, $decodeState, $note, $isFlagged
                    WHERE NOT EXISTS (SELECT 1 FROM ReceiveHistory WHERE FilePath = $filePath)
                    """;
                insert.Parameters.AddWithValue("$id", entry.Id);
                insert.Parameters.AddWithValue("$receivedAt", entry.ReceivedAt.ToString("O"));
                insert.Parameters.AddWithValue("$modeId", entry.ModeId);
                insert.Parameters.AddWithValue("$filePath", entry.FilePath);
                insert.Parameters.AddWithValue("$linkedQsoId", DBNull.Value);
                insert.Parameters.AddWithValue("$decodeState", entry.DecodeState.ToString());
                insert.Parameters.AddWithValue("$note", DBNull.Value);
                insert.Parameters.AddWithValue("$isFlagged", 0);
                importedCount += await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        Log.ReconcileImported(_logger, importedCount, directory);
        return importedCount;
    }

    // Two real filename shapes, one pattern: the CURRENT scheme has millisecond precision (captured
    // by the inner `ms` group) and a mandatory id token; the PRE-FIX scheme (any install predating
    // ReceiveHistoryRecorder's own collision fix, docs/functional-audit-playbook.md) has second-only
    // precision and NO id token at all -- `(?<ms>\d{3})?` and the whole trailing `(?:_...)?` group
    // are both optional for exactly that reason, not defensive over-generality. Named groups:
    // timestamp (the whole `yyyyMMdd-HHmmss[fff]` span, parsed against one format or the other
    // depending on whether `ms` matched), mode (ModeId -- never contains '_', confirmed against every
    // SstvModeRegistry entry), partial (present only for RecordAbandonedImageAsync's own "_partial_"
    // marker -- unreachable on the pre-fix shape, which never had an abandoned-path counterpart with
    // this problem), id (the entry-id fragment -- NOT reused as the reconciled entry's own Id, since
    // only the first 8 hex chars of the original GUID survive in the filename; a fresh GUID is
    // generated instead, same as every other RecordAsync caller). `id` is a hard `{8}`, matching
    // `entryId[..8]` (this class's own write path, the only variant ever found in this repo's git
    // history): Guid.ToString()'s default format always has exactly 8 hex digits before its first
    // hyphen. A real production filename (`20260825-004916556_martin-m2_1a081cf8.png`) was checked
    // against this pattern, 2026-08-27 -- id token `1a081cf8` is 8 characters and matches cleanly; an
    // earlier report of a 7-character id from the same file turned out to be a transcription typo,
    // corrected by the user, not a real filename shape.
    [GeneratedRegex(@"^(?<timestamp>\d{8}-\d{6}(?<ms>\d{3})?)_(?<mode>[^_]+)(?:_(?:(?<partial>partial)_)?(?<id>[0-9a-f]{8}))?\.png$", RegexOptions.IgnoreCase)]
    private static partial Regex FilenamePattern();

    /// <summary>Shared single-column-`UPDATE` implementation for <see cref="SetNoteAsync"/>/
    /// <see cref="SetFlaggedAsync"/>/<see cref="SetLinkedQsoIdAsync"/> -- three narrow,
    /// single-purpose setters (one atomic `UPDATE` each, mapping to one of three distinct,
    /// non-simultaneous user actions: commit a note / toggle a flag / click "Log entry") rather
    /// than one generic "patch" method, which would need its own which-fields-to-touch ambiguity
    /// this doesn't have. Returns whether a row was actually updated (`sqlite3_changes()` via
    /// `ExecuteNonQueryAsync`'s return value) -- <see langword="false"/> means `entryId` no longer
    /// exists (no automatic deletion path exists in production as of 2026-08-26, see
    /// `docs/removed-features.md`, but a missing row is still a reachable state -- a different
    /// process editing the same `history.db` file, for one), not an exception -- callers are
    /// expected to surface that to the user.</summary>
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

    private static string GetDefaultDbFilePath() => Path.Combine(AppDatabasePaths.DatabaseDirectory, "history.db");

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

        [LoggerMessage(Level = LogLevel.Information, Message = "RX images directory set to {Directory}")]
        public static partial void ImagesDirectorySet(ILogger logger, string? directory);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Disk/DB reconcile: skipped a file not matching the app's own naming convention: {FilePath}")]
        public static partial void ReconcileSkippedUnrecognizedFile(ILogger logger, string filePath);

        [LoggerMessage(Level = LogLevel.Information, Message = "Disk/DB reconcile: imported {Count} entries from {Directory}")]
        public static partial void ReconcileImported(ILogger logger, int count, string directory);
    }
}
