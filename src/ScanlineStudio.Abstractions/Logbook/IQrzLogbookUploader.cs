namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Result of one QRZ.com Logbook API upload attempt — mirrors the API's own
/// <c>RESULT</c>/<c>LOGID</c>/<c>REASON</c> response fields (see
/// <a href="https://www.qrz.com/docs/logbook/QRZLogbookAPI.html">QRZ Logbook API Developer
/// Guide</a>). <see cref="Success"/> covers both <c>RESULT=OK</c> and <c>RESULT=REPLACE</c>
/// (a duplicate the caller explicitly asked to overwrite via <c>OPTION=REPLACE</c>) — both mean
/// the QSO now exists in the user's QRZ logbook, which is the caller's actual question.</summary>
public sealed record QrzUploadResult(bool Success, string? LogId, string? ErrorReason);

/// <summary>Uploads a logged QSO to the user's own QRZ.com Logbook via QRZ's Logbook API — see
/// spec/08-logging.md and the accompanying plan file. Distinct from the spec's separate
/// <c>IOnlineCallsignLookup</c> (read-only callsign enrichment, legacy <c>qrzcom.cpp</c>'s actual
/// scope) — this is a genuinely new, one-way upload, opt-in only, off by default. Requires the
/// user's own QRZ XML-level-or-higher subscription and API key; never bundles or assumes
/// credentials.</summary>
public interface IQrzLogbookUploader
{
    /// <param name="adifText">A complete, single-record ADIF file — same shape
    /// <c>IAdifExporter</c> produces for one record.</param>
    /// <param name="apiKey">The user's own QRZ Logbook API access key.</param>
    Task<QrzUploadResult> UploadAsync(string adifText, string apiKey, CancellationToken ct = default);
}
