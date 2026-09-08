using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using ScanlineStudio.Abstractions.Imaging;

namespace ScanlineStudio.Core.Logbook;

// AvailableWaitHandle is never used: retaining this managed semaphore lets admitted operations
// finish during host teardown without disposing a gate that still has waiters.
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The managed-only deletion gate must remain usable by in-flight operations during teardown.")]
public sealed partial class SqliteReceiveHistoryStore
{
    private readonly SemaphoreSlim _deletionGate = new(1, 1);

    internal enum DeletionPathPresence { Present, AbsentInAccessibleParent, Unknown }

    internal Func<Task>? AfterDeletionStagedForTests { get; init; }
    internal Func<Task>? BeforeDeletionFinalizedForTests { get; init; }
    internal Func<Task>? BeforeDeletionPruneForTests { get; init; }
    internal Func<string, DeletionPathPresence>? ProbeDeletionPathForTests { get; init; }

    /// <summary>Stage suppression while retaining a retryable history row, then atomically commit
    /// row removal and the cleanup outcome. Never discard another entry's metadata or shared files.</summary>
    public async Task<bool> DeleteAsync(ReceiveHistoryEntry entry, CancellationToken ct = default)
    {
        ReceiveHistoryEntry? deleted;
        await _deletionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            deleted = await DeleteCoreAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportDeletionSafely(() => Log.DeleteIncomplete(_logger, entry.Id, ex));
            throw;
        }
        finally
        {
            _deletionGate.Release();
        }

        if (deleted is null) return false;
        ReportDeletionSafely(() => Log.DeleteCompleted(_logger, deleted.Id));
        // Arbitrary subscribers must not hold the deletion gate or turn a committed delete into
        // a reported failure. The event retains its existing caller/continuation scheduling policy.
        try { Deleted?.Invoke(deleted); }
        catch (Exception ex) { ReportDeletionSafely(() => Log.DeletedSubscriberFailed(_logger, deleted.Id, ex)); }
        return true;
    }

    private async Task<ReceiveHistoryEntry?> DeleteCoreAsync(ReceiveHistoryEntry entry, CancellationToken ct)
    {
        await using var connection = CreateDeletionConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        var token = Guid.NewGuid().ToString("N");
        string canonicalPath;
        bool sharedImage;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT FilePath, AudioFilePath FROM ReceiveHistory WHERE Id = $id";
                select.Parameters.AddWithValue("$id", entry.Id);
                await using var reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
                // A captured UI entry can predate a storage move or an audio attachment.
                entry = entry with { FilePath = reader.GetString(0), AudioFilePath = reader.IsDBNull(1) ? null : reader.GetString(1) };
            }

            canonicalPath = CanonicalFilePath(entry.FilePath);
            sharedImage = await HasFileReferenceAsync(connection, transaction, canonicalPath, entry.Id, ct).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            if (sharedImage)
            {
                command.CommandText = "DELETE FROM ReceiveHistory WHERE Id = $id";
                command.Parameters.AddWithValue("$id", entry.Id);
            }
            else
            {
                // Keep the row until finalization. A crash/SQL failure leaves a normal Delete retry,
                // rather than a permanently suppressed path with no remaining UI entry.
                command.CommandText = """
                    INSERT INTO ReceiveHistoryDeletion (FilePath, Token) VALUES ($path, $token)
                    ON CONFLICT(FilePath) DO UPDATE SET FilePath = excluded.FilePath, Token = excluded.Token
                    """;
                command.Parameters.AddWithValue("$path", canonicalPath);
                command.Parameters.AddWithValue("$token", token);
            }

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        if (!sharedImage)
        {
            ReportDeletionSafely(() => Log.DeleteStaged(_logger, entry.Id));
            if (AfterDeletionStagedForTests is { } staged) await staged().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var removed = await Task.Run(() => TryRemoveImage(entry.FilePath), ct).ConfigureAwait(false);
            if (BeforeDeletionFinalizedForTests is { } finalizing) await finalizing().ConfigureAwait(false);

            // No filesystem work while SQLite is locked. Cancellation or rollback preserves row+T.
            using var transaction = connection.BeginTransaction(deferred: false);
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM ReceiveHistory WHERE Id = $id
                AND EXISTS (SELECT 1 FROM ReceiveHistoryDeletion WHERE FilePath = $path AND Token = $token)
                """;
            delete.Parameters.AddWithValue("$id", entry.Id);
            delete.Parameters.AddWithValue("$path", canonicalPath);
            delete.Parameters.AddWithValue("$token", token);
            var rows = await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (rows == 0) return null; // Superseded: never clear a newer operation's protection.

            if (removed)
            {
                using var retire = connection.CreateCommand();
                retire.Transaction = transaction;
                retire.CommandText = "DELETE FROM ReceiveHistoryDeletion WHERE FilePath = $path AND Token = $token";
                retire.Parameters.AddWithValue("$path", canonicalPath);
                retire.Parameters.AddWithValue("$token", token);
                await retire.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        // Row removal is now committed. Audio cleanup/reporting cannot change that result.
        await RemoveUnreferencedAudioAsync(entry.AudioFilePath).ConfigureAwait(false);
        return entry;
    }

    private bool TryRemoveImage(string filePath)
    {
        try
        {
            DeleteImageFileForTests(filePath);
            if (ProbeDeletionPath(filePath) == DeletionPathPresence.AbsentInAccessibleParent) return true;
            ReportDeletionSafely(() => Log.DeleteAbsenceUnconfirmed(_logger, filePath));
        }
        catch (Exception ex) when (IsDeletionPathFailure(ex))
        {
            ReportDeletionSafely(() => Log.DeleteFileFailed(_logger, filePath, ex));
        }

        return false;
    }

    private DeletionPathPresence ProbeDeletionPath(string filePath)
    {
        try
        {
            if (ProbeDeletionPathForTests is { } probe) return probe(filePath);
            var path = Path.GetFullPath(filePath);
            var parent = Path.GetDirectoryName(path);
            if (parent is null) return DeletionPathPresence.Unknown;
            var name = Path.GetFileName(path);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = false,
                AttributesToSkip = 0,
            };
            foreach (var candidate in Directory.EnumerateFileSystemEntries(parent, "*", options))
            {
                // Conservative comparison matches the existing tombstone-key semantics.
                if (string.Equals(Path.GetFileName(candidate), name, StringComparison.OrdinalIgnoreCase))
                    return DeletionPathPresence.Present;
            }

            return DeletionPathPresence.AbsentInAccessibleParent;
        }
        catch (Exception ex) when (IsDeletionPathFailure(ex))
        {
            ReportDeletionSafely(() => Log.DeletePathProbeFailed(_logger, filePath, ex));
            return DeletionPathPresence.Unknown;
        }
    }

    private async Task RemoveUnreferencedAudioAsync(string? filePath)
    {
        if (filePath is null) return;
        try
        {
            await using var connection = CreateDeletionConnection();
            await connection.OpenAsync().ConfigureAwait(false);
            if (await HasFileReferenceAsync(connection, null, CanonicalFilePath(filePath), null, CancellationToken.None).ConfigureAwait(false)) return;
            await Task.Run(() => File.Delete(filePath)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ReportDeletionSafely(() => Log.DeleteFileFailed(_logger, filePath, ex));
        }
    }

    private SqliteConnection CreateDeletionConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        // Pure normalization; this does not rewrite stored paths or introduce a core/platform edge.
        connection.CreateFunction<string, string>("history_path", CanonicalFilePath, isDeterministic: true);
        return connection;
    }

    private static async Task<bool> HasFileReferenceAsync(SqliteConnection connection, SqliteTransaction? transaction,
        string canonicalPath, string? excludedId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM ReceiveHistory
            WHERE ($id IS NULL OR Id <> $id)
              AND (history_path(FilePath) = $path COLLATE NOCASE
                   OR (AudioFilePath IS NOT NULL AND history_path(AudioFilePath) = $path COLLATE NOCASE)))
            """;
        command.Parameters.AddWithValue("$id", (object?)excludedId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", canonicalPath);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false))! != 0;
    }

    private async Task PruneSatisfiedDeletionTombstonesAsync(CancellationToken ct)
    {
        var snapshot = new List<(string Path, string Token)>();
        await using var connection = CreateDeletionConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using (var query = connection.CreateCommand())
        {
            query.CommandText = """
                SELECT FilePath, Token FROM ReceiveHistoryDeletion AS deletion
                WHERE NOT EXISTS (SELECT 1 FROM ReceiveHistory
                    WHERE history_path(ReceiveHistory.FilePath) = deletion.FilePath COLLATE NOCASE)
                """;
            await using var reader = await query.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                snapshot.Add((reader.GetString(0), reader.GetString(1)));
        }

        // The retained row is the retry anchor. Neither active nor interrupted deletion intent
        // may be pruned; fully close the reader before probing or letting a competing writer run.
        var absent = await Task.Run(() => snapshot.Where(item =>
            ProbeDeletionPath(item.Path) == DeletionPathPresence.AbsentInAccessibleParent).ToList(), ct).ConfigureAwait(false);
        if (absent.Count == 0) return;
        if (BeforeDeletionPruneForTests is { } beforePrune) await beforePrune().ConfigureAwait(false);

        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var item in absent)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM ReceiveHistoryDeletion WHERE FilePath = $path AND Token = $token
                AND NOT EXISTS (SELECT 1 FROM ReceiveHistory
                    WHERE history_path(ReceiveHistory.FilePath) = $path COLLATE NOCASE)
                """;
            delete.Parameters.AddWithValue("$path", item.Path);
            delete.Parameters.AddWithValue("$token", item.Token);
            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static bool IsDeletionPathFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static void ReportDeletionSafely(Action report)
    {
        try { report(); }
        catch (Exception) { /* A diagnostic provider must not change the committed deletion outcome. */ }
    }
}
