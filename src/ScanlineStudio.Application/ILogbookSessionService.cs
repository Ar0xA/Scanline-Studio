using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application;

/// <summary>QSO logbook orchestration facade — see spec/08-logging.md and the accompanying plan
/// file. Mirrors <see cref="ISstvSessionService"/>/<c>OptionsSettingsService</c>'s existing role:
/// UI (and any future caller) never touches <c>ScanlineStudio.Core.Logbook</c> types directly.</summary>
public interface ILogbookSessionService
{
    /// <summary>Persists <paramref name="record"/> via <c>ILogbookRepository</c> — this always
    /// happens and is never skipped, regardless of what follows. If GridTracker streaming and/or
    /// QRZ.com upload are enabled in settings, both are then attempted best-effort (network
    /// failures there are logged and reported back via <see cref="LogQsoResult"/>, never thrown —
    /// a GridTracker/QRZ outage must not prevent the QSO from being logged locally). No retry
    /// queue: a failed push is surfaced once, not automatically retried.</summary>
    Task<LogQsoResult> LogQsoAsync(QsoRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<QsoRecord>> SearchAsync(LogbookQuery query, CancellationToken ct = default);

    /// <summary>Edits an already-logged QSO in place. Deliberately does NOT re-push to GridTracker
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

    /// <summary>Validates a username/password pair against QRZ.com — the Options window's "Test"
    /// button, testing credentials the user hasn't saved yet. Deliberately ungated (no
    /// <c>QrzLookupSettings.Enabled</c> check): this IS the settings-configuration flow itself.
    /// A thin passthrough to <see cref="IQrzCallsignLookup.TestCredentialsAsync"/>.</summary>
    Task<QrzLoginResult> TestQrzLookupCredentialsAsync(string username, string password, CancellationToken ct = default);
}
