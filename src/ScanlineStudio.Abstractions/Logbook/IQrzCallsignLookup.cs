namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Result of a credentials-only login attempt against QRZ's XML Callbook API -- see
/// <see cref="IQrzCallsignLookup.TestCredentialsAsync"/>'s own doc comment for why this is a
/// SEPARATE result type from <see cref="QrzCallsignLookupResult"/>, not the same shape reused.</summary>
public sealed record QrzLoginResult(bool Success, string? ErrorReason);

/// <summary>Result of one QRZ.com XML Callbook lookup. <see cref="Name"/>/<see cref="Qth"/> are
/// already composed from the XML response's separate `fname`/`name`/`addr2`/`country` fields,
/// matching legacy `qrzcom::qrzname`'s own composition (`Name` = first + last;
/// `Qth` = address, with country appended in parens when present) -- there is deliberately no
/// separate `Country` field here with no UI slot to show it in on its own.</summary>
public sealed record QrzCallsignLookupResult(bool Success, string? Name, string? Qth, string? Grid, string? ErrorReason);

/// <summary>Looks up a callsign against QRZ.com's XML Callbook API (`xmldata.qrz.com`) -- see
/// spec/08-logging.md's "QRZ.com lookup" section and legacy `qrzcom.cpp`. Distinct from
/// <see cref="IQrzLogbookUploader"/> (a different QRZ product entirely: that one PUSHES a logged
/// QSO to the user's QRZ Logbook via API-key auth at `logbook.qrz.com/api`; this one PULLS
/// enrichment data -- name/address/grid -- for a callsign via username/password auth). Same
/// "credentials as an explicit per-call parameter, never stored inside the service" contract as
/// <see cref="IQrzLogbookUploader.UploadAsync"/> -- an implementation may cache an opaque QRZ
/// session token keyed by a one-way hash of the credentials, but never the plaintext password
/// itself past the login call that used it.</summary>
public interface IQrzCallsignLookup
{
    /// <summary>Validates a username/password pair by logging in only -- no callsign lookup.
    /// Deliberately NOT implemented as "call <see cref="LookupAsync"/> against some test callsign
    /// and see if it succeeds": a valid login followed by a lookup miss (unlisted callsign) or a
    /// subscription-tier lookup restriction both look like <c>Success:false</c> to
    /// <see cref="LookupAsync"/>'s caller even though the CREDENTIALS were fine -- this method asks
    /// exactly the question "do these credentials authenticate," nothing else, and always forces a
    /// fresh login (never satisfied by, and never mutates, any cached session from a prior call).</summary>
    Task<QrzLoginResult> TestCredentialsAsync(string username, string password, CancellationToken ct = default);

    Task<QrzCallsignLookupResult> LookupAsync(string callsign, string username, string password, CancellationToken ct = default);
}
