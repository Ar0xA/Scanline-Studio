using ScanlineStudio.Abstractions.Radio;

namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>A logged contact — see spec/08-logging.md's "Storage" section. <see cref="Id"/> and
/// <see cref="ReceivedImageId"/> are <c>string</c> (not <see cref="Guid"/>, unlike the spec's own
/// draft) to match <see cref="ScanlineStudio.Abstractions.Imaging.ReceiveHistoryEntry"/>'s existing
/// <c>Id</c>/<c>LinkedQsoId</c> convention — that FK column already exists in the RX-history table,
/// anticipating this type; callers supply <c>Guid.NewGuid().ToString()</c>, same as RX history
/// entries do. <see cref="GridSquare"/> is not in the spec's original draft — added because it is
/// required for a correct ADIF <c>GRIDSQUARE</c> tag and used by both GridTracker (grid-based
/// mapping) and QRZ; everything else the roadmap flagged as a logbook gap (QSL flags, duplicate
/// detection, contest exchange) is deliberately not added here.</summary>
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
    string? ReceivedImageId);

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
