using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application;

/// <summary>QSO logbook orchestration facade — see spec/08-logging.md and the accompanying plan
/// file. Mirrors <see cref="ISstvSessionService"/>/<c>OptionsSettingsService</c>'s existing role:
/// UI (and any future caller) never touches <c>ScanlineStudio.Core.Logbook</c> types directly.</summary>
public interface ILogbookSessionService
{
    /// <summary>Persists <paramref name="record"/> via <c>ILogbookRepository</c> — this always
    /// happens and is never skipped, regardless of what follows. If ADIF-UDP streaming and/or
    /// QRZ.com upload are enabled in settings, both are then attempted best-effort (network
    /// failures there are logged and reported back via <see cref="LogQsoResult"/>, never thrown —
    /// an ADIF-UDP/QRZ outage must not prevent the QSO from being logged locally). No retry
    /// queue: a failed push is surfaced once, not automatically retried.</summary>
    Task<LogQsoResult> LogQsoAsync(QsoRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default);

    /// <summary>Edits an already-logged QSO in place. Deliberately does NOT re-push via ADIF-UDP
    /// or QRZ the way <see cref="LogQsoAsync"/> does -- see the implementation's own doc comment for
    /// why (QRZ's real upload API is INSERT-only; a re-push would file as a duplicate, not an
    /// update).</summary>
    Task UpdateQsoAsync(QsoRecord record, CancellationToken ct = default);

    Task ExportAdifFileAsync(string filePath, LogbookQuery query, CancellationToken ct = default);

    /// <summary>Parses <paramref name="filePath"/> and persists every record it contains via
    /// <c>ILogbookRepository</c> (a batch import, not just a parse-and-preview) — returns the
    /// persisted records.</summary>
    Task<IReadOnlyList<QsoRecord>> ImportAdifFileAsync(string filePath, CancellationToken ct = default);

    /// <summary>Looks up <paramref name="callsign"/> against QRZ.com's XML Callbook API (see
    /// <see cref="IQrzCallsignLookup"/>) — the LIVE path (Receive tab's "Lookup QRZ" button).
    /// Internally resolves <c>QrzLookupSettings</c> and returns a "not configured" result WITHOUT
    /// making any network call if lookup is disabled or no credentials are saved — same
    /// "never throws, always returns a result" contract as <see cref="LogQsoAsync"/>.</summary>
    Task<QrzCallsignLookupResult> LookupCallsignAsync(string callsign, CancellationToken ct = default);

    /// <summary>Whether <see cref="LookupCallsignAsync"/> would actually attempt a network call
    /// right now (lookup enabled AND a username/password are saved) — the same check
    /// <see cref="LookupCallsignAsync"/> already makes internally, exposed separately so the
    /// Receive tab's "Lookup QRZ" button can be disabled ahead of time instead of only failing
    /// after the click. Never throws — a settings-read failure resolves to <c>false</c>, same
    /// "not configured" outcome as a genuinely unconfigured lookup.</summary>
    Task<bool> IsQrzLookupConfiguredAsync(CancellationToken ct = default);

    /// <summary>Validates a username/password pair against QRZ.com — the Options window's "Test"
    /// button, testing credentials the user hasn't saved yet. Deliberately ungated (no
    /// <c>QrzLookupSettings.Enabled</c> check): this IS the settings-configuration flow itself.
    /// A thin passthrough to <see cref="IQrzCallsignLookup.TestCredentialsAsync"/>.</summary>
    Task<QrzLoginResult> TestQrzLookupCredentialsAsync(string username, string password, CancellationToken ct = default);

    /// <summary>ui_transition_plan.md step 15 -- deletes a logged QSO. Does NOT attempt to retract
    /// an already-made GridTracker/ADIF-UDP broadcast or QRZ upload -- both are fire-and-forget/
    /// INSERT-only external APIs with no retraction call, same stated limitation
    /// <see cref="UpdateQsoAsync"/>'s own doc comment already accepts for edits. Before deleting the
    /// row, best-effort clears <see cref="Abstractions.Imaging.ReceiveHistoryEntry.LinkedQsoId"/> on
    /// any RX-history entry still linked to this QSO (via <see cref="Abstractions.Imaging.IReceiveHistoryStore.ClearLinkedQsoIdAsync"/>)
    /// -- otherwise the Gallery would report that frame as permanently "Logged" against a QSO that
    /// no longer exists, with no way for the operator to find and re-log it. Returns whether the QSO
    /// row itself was actually deleted (<see langword="false"/> means it no longer existed, e.g.
    /// deleted from another window/process) -- never throws for that case.</summary>
    Task<bool> DeleteQsoAsync(string id, CancellationToken ct = default);

    /// <summary>ui_transition_plan.md step 15, piece (c) -- likely-duplicate check by callsign +
    /// band, bounded to the SAME UTC CALENDAR DAY as <paramref name="startUtc"/> (matching contest/
    /// LoTW dup-check convention) -- a regular sked partner worked again six months later must never
    /// flag as a duplicate. Band comes from <see cref="AmateurBandLookup.BandFor"/>;
    /// <paramref name="frequencyHz"/> <see langword="null"/> means no band, which the match also
    /// requires equal (both sides "no band"), not a wildcard -- two QSOs with genuinely unknown
    /// frequencies are not thereby "the same band." <paramref name="excludeId"/> (pass the QSO's own
    /// id when editing) prevents a QSO from matching itself. Returns the first likely-duplicate
    /// record found, or <see langword="null"/> if none -- also <see langword="null"/> on ANY
    /// failure (fail OPEN: a transient DB hiccup must never block logging or saving a real QSO),
    /// logged, never thrown.</summary>
    Task<QsoRecord?> FindLikelyDuplicateAsync(string callsign, DateTimeOffset startUtc, long? frequencyHz, string? excludeId, CancellationToken ct = default);
}
