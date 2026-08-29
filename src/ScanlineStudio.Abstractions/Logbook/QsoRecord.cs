using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>A logged contact — see spec/08-logging.md's "Storage" section. <see cref="Id"/> and
/// <see cref="ReceivedImageId"/> are <c>string</c> (not <see cref="Guid"/>, unlike the spec's own
/// draft) to match <see cref="ScanlineStudio.Abstractions.Imaging.ReceiveHistoryEntry"/>'s existing
/// <c>Id</c>/<c>LinkedQsoId</c> convention — that FK column already exists in the RX-history table,
/// anticipating this type; callers supply <c>Guid.NewGuid().ToString()</c>, same as RX history
/// entries do. <see cref="GridSquare"/> is not in the spec's original draft — added because it is
/// required for a correct ADIF <c>GRIDSQUARE</c> tag and used by both GridTracker (grid-based
/// mapping) and QRZ; duplicate detection and contest exchange are deliberately not added here.
///
/// <paramref name="QslSent"/>/<paramref name="QslReceived"/> (ui_transition_plan.md step 15,
/// piece (b)): plain booleans, not ADIF's full Y/N/R/I/Q enumeration -- matches this record's own
/// existing minimalism (no other field tracks a "queued/requested" tri-state). Deliberately given
/// NO C# default value, unlike e.g. <c>TemplateManifest.SchemaVersion</c>'s own trailing-default
/// convention -- this record is never deserialized from JSON (it's read column-by-column from a
/// SQLite reader, see <see cref="ScanlineStudio.Core.Logbook.SqliteLogbookRepository"/>), so there
/// is no "missing JSON property" case a default would need to paper over; omitting the default
/// instead forces the compiler to enumerate every construction site when these fields were added,
/// which is what actually caught the real call sites needing updates.</summary>
public sealed record QsoRecord(
    string Id,
    string Callsign,
    DateTimeOffset StartUtc,
    DateTimeOffset? EndUtc,
    long? FrequencyHz,
    RadioMode? Mode,
    string? SstvModeId,
    string? RstSent,
    string? RstReceived,
    string? Name,
    string? Qth,
    string? GridSquare,
    string? Country,
    string? Notes,
    string? ReceivedImageId,
    bool QslSent,
    bool QslReceived);

public sealed record LogbookQuery(string? Callsign = null, DateTimeOffset? From = null, DateTimeOffset? To = null);

public interface ILogbookRepository
{
    Task<QsoRecord> AddAsync(QsoRecord record, CancellationToken ct = default);

    Task UpdateAsync(QsoRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default);

    /// <summary>ui_transition_plan.md step 15 -- returns whether a row was actually deleted
    /// (<c>sqlite3_changes()</c>), same "false means no-longer-exists, not an exception" contract
    /// as <see cref="Abstractions.Imaging.IReceiveHistoryStore.SetNoteAsync"/> and friends.</summary>
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
}
