using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ScanlineStudio.Abstractions.Imaging;
using ScanlineStudio.Abstractions.Radio;
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
    public event Action<ReceiveHistoryEntry>? Deleted;

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
        command.CommandText = "SELECT Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged, FrequencyHz, RigMode, AudioFilePath, DecodedCallsign, DecodedNrRst, DecodedCallsignSource, DecodedCwId FROM ReceiveHistory WHERE 1 = 1";

        if (filter.ModeId is not null)
        {
            command.CommandText += " AND ModeId = $modeId";
            command.Parameters.AddWithValue("$modeId", filter.ModeId);
        }

        // T1-16 (production_audit.md): filters/sorts on ReceivedAtUtc, not the displayed ReceivedAt
        // column -- see ReceivedAtUtc's own doc comment above EnsureSchema for why a raw TEXT compare
        // on ReceivedAt's own local-offset format is NOT instant-based (a real, previously-live bug).
        if (filter.From is not null)
        {
            command.CommandText += " AND ReceivedAtUtc >= $from";
            command.Parameters.AddWithValue("$from", filter.From.Value.UtcDateTime.ToString("O"));
        }

        if (filter.To is not null)
        {
            command.CommandText += " AND ReceivedAtUtc <= $to";
            command.Parameters.AddWithValue("$to", filter.To.Value.UtcDateTime.ToString("O"));
        }

        command.CommandText += " ORDER BY ReceivedAtUtc DESC";

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
                reader.GetInt64(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : ParseRigMode(reader.GetString(9)),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetString(14)));
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

    /// <summary>Same DBNull-means-null / garbage-string-means-Unknown-not-throw shape as
    /// <c>SqliteLogbookRepository.ParseMode</c>, mirrored exactly (both persist a <see cref="RadioMode"/>?
    /// the identical way).</summary>
    private static RadioMode ParseRigMode(string value) =>
        Enum.TryParse<RadioMode>(value, out var parsed) ? parsed : RadioMode.Unknown;

    public async Task<IImageSource> LoadThumbnailAsync(ReceiveHistoryEntry entry, int maxDimension, CancellationToken ct = default)
    {
        using var image = await Image.LoadAsync<SixLabors.ImageSharp.PixelFormats.Rgb24>(entry.FilePath, ct).ConfigureAwait(false);
        // T1-15 (production_audit.md): this class used to skip AutoOrient entirely, the same bug
        // class already fixed once for StockImageLibrary (Core.Imaging) -- decoded SSTV images
        // rarely carry EXIF orientation, but a re-decoded WAV/image saved through a path that does
        // would show sideways here while every other read-direction site orients correctly. Must
        // run BEFORE the fit computation, same ordering rationale as every other AutoOrient call
        // site in this codebase -- the fit must be measured from the POST-orient dimensions.
        image.Mutate(x => x.AutoOrient());
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
            INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged, FrequencyHz, RigMode, AudioFilePath, ReceivedAtUtc, DecodedCallsign, DecodedNrRst, DecodedCallsignSource, DecodedCwId)
            VALUES ($id, $receivedAt, $modeId, $filePath, $linkedQsoId, $decodeState, $note, $isFlagged, $frequencyHz, $rigMode, $audioFilePath, $receivedAtUtc, $decodedCallsign, $decodedNrRst, $decodedCallsignSource, $decodedCwId)
            """;
        command.Parameters.AddWithValue("$id", entry.Id);
        command.Parameters.AddWithValue("$receivedAt", entry.ReceivedAt.ToString("O"));
        command.Parameters.AddWithValue("$modeId", entry.ModeId);
        command.Parameters.AddWithValue("$filePath", entry.FilePath);
        command.Parameters.AddWithValue("$linkedQsoId", (object?)entry.LinkedQsoId ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodeState", entry.DecodeState.ToString());
        command.Parameters.AddWithValue("$note", (object?)entry.Note ?? DBNull.Value);
        command.Parameters.AddWithValue("$isFlagged", entry.IsFlagged ? 1 : 0);
        command.Parameters.AddWithValue("$frequencyHz", (object?)entry.FrequencyHz ?? DBNull.Value);
        command.Parameters.AddWithValue("$rigMode", (object?)entry.RigMode?.ToString() ?? DBNull.Value);
        // Always null at write time in production -- RxAudioAutoSaver's join always completes AFTER
        // this row already exists, attaching the path later via SetAudioFilePathAsync. Still taken
        // from entry.AudioFilePath (not hardcoded DBNull.Value) so a test/ReconcileWithDiskAsync-style
        // caller that already knows the path isn't forced through a second UPDATE round-trip.
        command.Parameters.AddWithValue("$audioFilePath", (object?)entry.AudioFilePath ?? DBNull.Value);
        // T1-16 (production_audit.md): see ReceivedAtUtc's own doc comment above EnsureSchema.
        command.Parameters.AddWithValue("$receivedAtUtc", entry.ReceivedAt.UtcDateTime.ToString("O"));
        // fsk_cwid.md §5 A2: always null at write time in production -- RxStationIdAttacher's join
        // always completes AFTER this row already exists (the FSK ID transmits after the image),
        // attaching later via SetDecodedStationIdAsync. Same "still taken from the entry, not
        // hardcoded DBNull.Value" reasoning as AudioFilePath above.
        command.Parameters.AddWithValue("$decodedCallsign", (object?)entry.DecodedCallsign ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodedNrRst", (object?)entry.DecodedNrRst ?? DBNull.Value);
        // fsk_cwid.md B-P5: same "always null at write time in production, still taken from the
        // entry rather than hardcoded DBNull.Value" reasoning as DecodedCallsign/DecodedNrRst above --
        // RxStationIdAttacher's join (now also covering CwIdDecoded) always completes AFTER this row
        // already exists.
        command.Parameters.AddWithValue("$decodedCallsignSource", (object?)entry.DecodedCallsignSource ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodedCwId", (object?)entry.DecodedCwId ?? DBNull.Value);

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

        await _settingsStore.UpdateAsync(settings =>
        {
            var current = settings.GetSection(ReceiveHistorySettings.SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings) ?? new ReceiveHistorySettings();
            return settings.WithSection(ReceiveHistorySettings.SectionKey, current with { ImagesDirectory = normalized }, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        }, ct).ConfigureAwait(false);
        Log.ImagesDirectorySet(_logger, normalized);
    }

    public async Task<AudioAutoSaveSettings> GetAudioSettingsAsync(CancellationToken ct = default)
    {
        // Single load, not one via ResolveAudioDirectoryAsync plus a second one here -- Enabled and
        // Directory must come from the SAME snapshot, matching GetImagesDirectoryAsync's own
        // single-load shape (an earlier draft loaded twice, letting the two values theoretically
        // disagree if a concurrent SetAudioSettingsAsync landed in between).
        var settings = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        var section = settings.GetSection(ReceiveHistorySettings.SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        var directory = ReceiveHistorySettings.ResolveAudioDirectory(section);
        return new AudioAutoSaveSettings(section?.AutoSaveAudioEnabled ?? false, directory);
    }

    /// <summary>See <see cref="IReceiveHistoryStore.SetAudioSettingsAsync"/>. Same validate-then-
    /// persist-the-resolved-absolute-path shape as <see cref="SetImagesDirectoryAsync"/> -- see that
    /// method's own doc comment for why (relative-path/`~` instability across process restarts).</summary>
    public async Task SetAudioSettingsAsync(bool enabled, string? directory, CancellationToken ct = default)
    {
        var normalized = string.IsNullOrWhiteSpace(directory) ? null : directory;
        if (normalized is not null)
        {
            normalized = Path.GetFullPath(normalized);
            Directory.CreateDirectory(normalized);
        }

        await _settingsStore.UpdateAsync(settings =>
        {
            var current = settings.GetSection(ReceiveHistorySettings.SectionKey, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings) ?? new ReceiveHistorySettings();
            return settings.WithSection(ReceiveHistorySettings.SectionKey, current with { AutoSaveAudioEnabled = enabled, AudioDirectory = normalized }, ReceiveHistorySettingsJsonContext.Default.ReceiveHistorySettings);
        }, ct).ConfigureAwait(false);
        Log.AudioSettingsSet(_logger, enabled, normalized);
    }

    public Task<bool> SetAudioFilePathAsync(string entryId, string path, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET AudioFilePath = $audioFilePath WHERE Id = $id", entryId, "$audioFilePath", path, ct);

    /// <summary>See <see cref="IReceiveHistoryStore.SetDecodedStationIdAsync"/> for the "all four
    /// columns written exactly as given, no leave-unchanged semantics" contract. A dedicated
    /// four-value implementation, not <see cref="ExecuteUpdateAsync"/> (that helper is single-value
    /// only).</summary>
    public async Task<bool> SetDecodedStationIdAsync(string entryId, string? callsign, string? callsignSource, string? nrRst, string? cwId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE ReceiveHistory SET DecodedCallsign = $decodedCallsign, DecodedCallsignSource = $decodedCallsignSource, DecodedNrRst = $decodedNrRst, DecodedCwId = $decodedCwId WHERE Id = $id";
        command.Parameters.AddWithValue("$decodedCallsign", (object?)callsign ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodedCallsignSource", (object?)callsignSource ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodedNrRst", (object?)nrRst ?? DBNull.Value);
        command.Parameters.AddWithValue("$decodedCwId", (object?)cwId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", entryId);

        var rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return rowsAffected > 0;
    }

    public Task<bool> SetNoteAsync(string entryId, string? note, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET Note = $note WHERE Id = $id", entryId, "$note", (object?)note ?? DBNull.Value, ct);

    public Task<bool> SetFlaggedAsync(string entryId, bool isFlagged, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET IsFlagged = $isFlagged WHERE Id = $id", entryId, "$isFlagged", isFlagged ? 1 : 0, ct);

    public Task<bool> SetLinkedQsoIdAsync(string entryId, string qsoId, CancellationToken ct = default) =>
        ExecuteUpdateAsync("UPDATE ReceiveHistory SET LinkedQsoId = $linkedQsoId WHERE Id = $id", entryId, "$linkedQsoId", qsoId, ct);

    /// <summary>See <see cref="IReceiveHistoryStore.ClearLinkedQsoIdAsync"/>. A sibling of
    /// <see cref="ExecuteUpdateAsync"/> in shape, not a reuse of it -- that helper always keys its
    /// `WHERE` clause on `Id`, while this one keys on the `LinkedQsoId` FK column itself and can
    /// legitimately affect more than one row.</summary>
    public async Task<int> ClearLinkedQsoIdAsync(string qsoId, CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE ReceiveHistory SET LinkedQsoId = NULL WHERE LinkedQsoId = $qsoId";
        command.Parameters.AddWithValue("$qsoId", qsoId);

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="IReceiveHistoryStore.DeleteAsync"/>. File removal happens FIRST,
    /// before the DB row -- if the row were deleted first and the file delete then failed, a
    /// later <see cref="ReconcileWithDiskAsync"/> pass would re-adopt that orphaned file as a
    /// "new" entry, silently resurrecting something the operator just deleted. Doing the file
    /// first means the worst case is the reverse (a row briefly outlives its file, already an
    /// explicitly-tolerated state per <see cref="SetNoteAsync"/>'s own doc comment), not a
    /// resurrection.</summary>
    public async Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        try
        {
            if (File.Exists(entry.FilePath))
            {
                File.Delete(entry.FilePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logged, not rethrown -- deliberately does not block the row delete below (see this
            // method's own doc comment on the interface: "this entry disappears from the Gallery"
            // is the promise, not "and disk space is reclaimed, guaranteed").
            Log.DeleteFileFailed(_logger, entry.FilePath, ex);
        }

        // ui_transition_plan.md step 12 (Auto-save RX audio), Step 4: same tolerant, isolated
        // best-effort delete as the image file above -- a null AudioFilePath (no audio was ever
        // attached) is simply skipped, not an error.
        if (entry.AudioFilePath is { } audioFilePath)
        {
            try
            {
                if (File.Exists(audioFilePath))
                {
                    File.Delete(audioFilePath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.DeleteFileFailed(_logger, audioFilePath, ex);
            }
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ReceiveHistory WHERE Id = $id";
        command.Parameters.AddWithValue("$id", entry.Id);
        var rowsAffected = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (rowsAffected == 0)
        {
            return false;
        }

        // Isolated deliberately, same reasoning as RecordAsync's own Recorded-event try/catch: a
        // subscriber's own exception must not surface as if THIS delete had failed -- both the
        // file removal attempt and the row delete already fully ran by this point.
        try
        {
            Deleted?.Invoke(entry);
        }
        catch (Exception ex)
        {
            Log.DeletedSubscriberFailed(_logger, entry.Id, ex);
        }

        return true;
    }

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
                    INSERT INTO ReceiveHistory (Id, ReceivedAt, ModeId, FilePath, LinkedQsoId, DecodeState, Note, IsFlagged, ReceivedAtUtc)
                    SELECT $id, $receivedAt, $modeId, $filePath, $linkedQsoId, $decodeState, $note, $isFlagged, $receivedAtUtc
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
                // T1-16 (production_audit.md): see ReceivedAtUtc's own doc comment above EnsureSchema.
                insert.Parameters.AddWithValue("$receivedAtUtc", entry.ReceivedAt.UtcDateTime.ToString("O"));
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
    /// proportionate to a single 16-column table; do not "improve" this without a real second table
    /// to justify it). `CREATE TABLE`'s own column definitions carry the identical `DEFAULT`s the
    /// `ALTER TABLE` statements below use, AND the 11 `ALTER TABLE ADD COLUMN`s below run in the
    /// same order `CREATE TABLE` declares them (`DecodeState`, `Note`, `IsFlagged`, `FrequencyHz`,
    /// `RigMode`, `AudioFilePath`, `ReceivedAtUtc`, `DecodedCallsign`, `DecodedNrRst`,
    /// `DecodedCallsignSource`, `DecodedCwId`) --
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
    // T1-16 (production_audit.md): ReceivedAt stores a genuinely correct instant, but as
    // DateTimeOffset.ToString("O") -- LOCAL offset preserved (ReceiveHistoryRecorder/
    // ReconcileWithDiskAsync both stamp wall-clock local time, by design, since that's what the
    // Gallery/status-bar UI displays). QueryAsync's own From/To/ORDER BY used to compare that TEXT
    // column directly -- a lexicographic compare, not an instant-based one, so two rows/queries with
    // DIFFERENT offsets (e.g. a query anchored at UTC midnight against rows stored at local offset)
    // can sort/filter WRONG even though every individual value is itself correct (confirmed:
    // "2026-08-09T20:00:00-05:00", a real instant AFTER 2026-08-10T00:00:00Z, sorts BEFORE it as
    // text). Fixed with a SEPARATE column, not by changing ReceivedAt's own meaning: ReceivedAtUtc
    // always stores DateTimeOffset.UtcDateTime.ToString("O") (fixed offset "Z", so lexicographic
    // order among ReceivedAtUtc values IS chronological order) -- QueryAsync filters/sorts on THIS
    // column exclusively now; ReceivedAt itself, its stored format, and every existing reader of
    // ReceiveHistoryEntry.ReceivedAt are all untouched. Genuinely NULLABLE, not just nullable-for-
    // schema-consistency: both write sites (RecordAsync, ReconcileWithDiskAsync's insert) always
    // supply a real value, and BackfillReceivedAtUtc runs UNCONDITIONALLY on every startup (not
    // gated on "just added this pass" -- auditor code-review finding, 2026-08-31: a one-time gate
    // would let a NULL row introduced later, e.g. an older build's INSERT running against an
    // already-migrated DB, or a parse failure, stay silently invisible from every filtered
    // Gallery/status-bar view forever, with nothing left to ever repair it), scoped to
    // `WHERE ReceivedAtUtc IS NULL` -- so any row that's ever NULL self-heals on the next startup
    // once its ReceivedAt value parses, rather than being treated as a should-never-happen case.
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
                IsFlagged INTEGER NOT NULL DEFAULT 0,
                FrequencyHz INTEGER NULL,
                RigMode TEXT NULL,
                AudioFilePath TEXT NULL,
                ReceivedAtUtc TEXT NULL,
                DecodedCallsign TEXT NULL,
                DecodedNrRst TEXT NULL,
                DecodedCallsignSource TEXT NULL,
                DecodedCwId TEXT NULL
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
        // IsFlagged, FrequencyHz, RigMode, AudioFilePath) -- ADD COLUMN always appends, so a migrated
        // DB's column order would otherwise permanently diverge from a fresh DB's; see this method's
        // own doc comment.
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

        // ui_transition_plan.md step 6 (T2-4): appended AFTER IsFlagged, same "always appends,
        // never reorders" reasoning as the 3 columns above -- a pre-existing row simply has no
        // latched frequency/mode (NULL, correctly meaning "unknown," not backfilled/guessed).
        if (!existingColumns.Contains("FrequencyHz"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN FrequencyHz INTEGER NULL");
        }

        if (!existingColumns.Contains("RigMode"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN RigMode TEXT NULL");
        }

        // ui_transition_plan.md step 12 (Auto-save RX audio): the 6th ALTER TABLE block, appended
        // AFTER RigMode -- same "always appends, never reorders" reasoning as the columns above. A
        // pre-existing row simply has no linked audio (NULL, correctly meaning "none ever attached,"
        // matching every real row this feature didn't exist for yet -- not backfilled/guessed).
        if (!existingColumns.Contains("AudioFilePath"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN AudioFilePath TEXT NULL");
        }

        // T1-16 (production_audit.md): the 7th ALTER TABLE block, appended AFTER AudioFilePath --
        // same "always appends, never reorders" reasoning as the columns above. See this new
        // column's own doc comment (just above CREATE TABLE's own declaration) for why it exists
        // and why it's a SEPARATE column rather than replacing ReceivedAt's own meaning.
        if (!existingColumns.Contains("ReceivedAtUtc"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN ReceivedAtUtc TEXT NULL");
        }

        // fsk_cwid.md §5 A2: the 8th and 9th ALTER TABLE blocks, appended AFTER ReceivedAtUtc -- same
        // "always appends, never reorders" reasoning as every column above. A pre-existing row simply
        // has no decoded station ID (NULL, correctly meaning "none ever attached" -- FSK-ID decode is
        // new capability this row predates, not backfilled/guessed).
        if (!existingColumns.Contains("DecodedCallsign"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN DecodedCallsign TEXT NULL");
        }

        if (!existingColumns.Contains("DecodedNrRst"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN DecodedNrRst TEXT NULL");
        }

        // fsk_cwid.md B-P5: the 10th and 11th ALTER TABLE blocks, appended AFTER DecodedNrRst -- same
        // "always appends, never reorders" reasoning as every column above. A pre-existing row simply
        // has no CW-ID decode (NULL, correctly meaning "none ever attached" -- CW-ID persistence is
        // new capability this row predates, not backfilled/guessed). A row whose DecodedCallsign was
        // already set by a pre-B-P5 build correctly gets DecodedCallsignSource = NULL here (not
        // "FSK") -- see that field's own doc comment on ReceiveHistoryEntry for why a NULL source is
        // read back as FSK without needing a real backfill value.
        if (!existingColumns.Contains("DecodedCallsignSource"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN DecodedCallsignSource TEXT NULL");
        }

        if (!existingColumns.Contains("DecodedCwId"))
        {
            ExecuteNonQuery(connection, transaction, "ALTER TABLE ReceiveHistory ADD COLUMN DecodedCwId TEXT NULL");
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

        // T1-16 (production_audit.md), auditor code-review finding (2026-08-31): backfills every row
        // whose ReceivedAtUtc is still NULL -- runs EVERY pass, unconditionally, NOT gated on "the
        // column was newly added this pass" the way DecodeState's own backfill above deliberately is.
        // DecodeState's gate is correct for ITS OWN backfill because that one is a one-time,
        // heuristic, GLOB-based guess -- re-running it on every startup risks re-classifying a row a
        // future feature legitimately changed. This backfill is neither: it is idempotent (an
        // already-non-NULL row is never touched, scoped by the WHERE clause below, not a pass-level
        // flag) and non-destructive (it only ever fills a gap, never overwrites a real value). A
        // gate here would leave a genuine, reachable failure mode: a NULL ReceivedAtUtc row (e.g. an
        // OLDER build writes a fresh row against an already-migrated DB, or a hand/foreign-tool edit)
        // silently DISAPPEARS from every filtered view forever (WHERE ReceivedAtUtc >= $from
        // evaluates to SQL NULL, so the row never matches; ORDER BY sorts NULLs last, so it's also
        // buried at the bottom of the unfiltered "All" view) -- with the old once-only gate, nothing
        // would ever repair that row again. Per-row C# loop (DateTimeOffset.Parse + reformat), not a
        // single SQL UPDATE with a computed expression: SQLite's own datetime()/strftime() functions
        // don't reproduce .NET's "O" round-trip format byte-for-byte (different field widths, no
        // fractional-second/timezone-suffix parity), so doing the conversion in .NET against the
        // exact same parser QueryAsync's own read path already uses is the only way to guarantee the
        // backfilled values match what a freshly-written row gets. Still runs inside this same
        // transaction (this method's own doc comment's crash-safety guarantee) -- on a healthy,
        // fully-migrated DB the WHERE clause matches zero rows (served by the new index below, which
        // SQLite uses for IS NULL), so this costs nothing on the overwhelmingly common startup.
        BackfillReceivedAtUtc(connection, transaction);

        // Every logbook/gallery view query sorts/filters by ReceivedAtUtc, ReconcileWithDiskAsync
        // probes FilePath per candidate file, and SetLinkedQsoIdAsync's sibling ClearLinkedQsoIdAsync
        // keys on LinkedQsoId -- without these, each is a full table scan (T0-9). CREATE INDEX IF NOT
        // EXISTS is idempotent, so this runs unconditionally on every startup. Names are
        // table-qualified and explicit: this database file also holds the Qso table's indexes
        // (SqliteLogbookRepository.EnsureSchema), and SQLite's index namespace is per-database, not
        // per-table -- an accidental name collision would silently no-op under IF NOT EXISTS with no
        // error and no test failure.
        //
        // T1-16 (production_audit.md): IX_ReceiveHistory_ReceivedAt (on the old ReceivedAt column) is
        // dropped, not kept alongside the new index below -- QueryAsync's own WHERE/ORDER BY now
        // filter/sort exclusively on ReceivedAtUtc (ReceivedAt only ever appears in the plain SELECT
        // list now, where no index helps), so the old index is pure dead weight this change itself
        // creates -- cheap to remove now, no reason to carry it forward.
        ExecuteNonQuery(connection, transaction, "DROP INDEX IF EXISTS IX_ReceiveHistory_ReceivedAt");
        ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_ReceiveHistory_ReceivedAtUtc ON ReceiveHistory(ReceivedAtUtc)");
        ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_ReceiveHistory_FilePath ON ReceiveHistory(FilePath)");
        ExecuteNonQuery(connection, transaction, "CREATE INDEX IF NOT EXISTS IX_ReceiveHistory_LinkedQsoId ON ReceiveHistory(LinkedQsoId)");

        transaction.Commit();
    }

    // T1-16 (production_audit.md): see EnsureSchema's own call site comment for why this is a
    // per-row C# loop instead of a single SQL UPDATE, and why it's scoped to ReceivedAtUtc IS NULL
    // rather than gated on "just added this pass." Auditor code-review finding (2026-08-31): a
    // single unparseable ReceivedAt value (hand-edited DB, a foreign writer) must not crash app
    // startup from inside DI resolution the way an unguarded throw here would (every subsequent
    // launch would fail identically, since the transaction rolls back and the row stays NULL,
    // re-triggering this same throw) -- log and leave that ONE row NULL instead, same shape as
    // ReconcileWithDiskAsync's own per-file skip-and-log for a file it can't make sense of. A NULL
    // row is a degraded-but-recoverable state now (invisible from filtered views until the value is
    // fixed and a later startup's own IS-NULL scope picks it up again -- see the "unconditional,
    // not gated" reasoning at the call site), not a permanent one.
    private void BackfillReceivedAtUtc(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<(string Id, string ReceivedAt)>();
        var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = "SELECT Id, ReceivedAt FROM ReceiveHistory WHERE ReceivedAtUtc IS NULL";
        using (var reader = selectCommand.ExecuteReader())
        {
            // Fully materialize before issuing any UPDATE below -- same "no live reader during DDL/DML
            // on the same connection" reasoning as the PRAGMA table_info probe above.
            while (reader.Read())
            {
                rows.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (id, receivedAt) in rows)
        {
            string utcText;
            try
            {
                utcText = DateTimeOffset.Parse(receivedAt, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime.ToString("O");
            }
            catch (FormatException ex)
            {
                Log.ReceivedAtUtcBackfillFailed(_logger, id, ex);
                continue;
            }

            var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = "UPDATE ReceiveHistory SET ReceivedAtUtc = $utc WHERE Id = $id";
            updateCommand.Parameters.AddWithValue("$utc", utcText);
            updateCommand.Parameters.AddWithValue("$id", id);
            updateCommand.ExecuteNonQuery();
        }
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

        [LoggerMessage(Level = LogLevel.Warning, Message = "A Deleted event subscriber threw for entry {EntryId}")]
        public static partial void DeletedSubscriberFailed(ILogger logger, string entryId, Exception ex);

        [LoggerMessage(Level = LogLevel.Warning, Message = "Deleting the image file failed, the history row was removed anyway: {FilePath}")]
        public static partial void DeleteFileFailed(ILogger logger, string filePath, Exception ex);

        [LoggerMessage(Level = LogLevel.Information, Message = "RX images directory set to {Directory}")]
        public static partial void ImagesDirectorySet(ILogger logger, string? directory);

        [LoggerMessage(Level = LogLevel.Information, Message = "Auto-save RX audio settings set: enabled={Enabled}, directory={Directory}")]
        public static partial void AudioSettingsSet(ILogger logger, bool enabled, string? directory);

        [LoggerMessage(Level = LogLevel.Debug, Message = "Disk/DB reconcile: skipped a file not matching the app's own naming convention: {FilePath}")]
        public static partial void ReconcileSkippedUnrecognizedFile(ILogger logger, string filePath);

        [LoggerMessage(Level = LogLevel.Information, Message = "Disk/DB reconcile: imported {Count} entries from {Directory}")]
        public static partial void ReconcileImported(ILogger logger, int count, string directory);

        [LoggerMessage(Level = LogLevel.Error, Message = "Backfilling ReceivedAtUtc failed for entry {EntryId}; left NULL, will retry on the next startup")]
        public static partial void ReceivedAtUtcBackfillFailed(ILogger logger, string entryId, Exception ex);
    }
}
