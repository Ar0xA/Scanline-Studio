namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Streams a logged QSO to <a href="https://gridtracker.org">GridTracker</a> — see
/// spec/08-logging.md and the accompanying plan file. GridTracker's real-time logging ingestion is
/// the WSJT-X UDP network protocol's own <c>LoggedADIF</c> message (type 12): a small binary
/// header (magic number, schema, message type) followed by a client-id string and a complete
/// single-QSO ADIF-text string — see <c>GridTrackerStreamer</c>'s own doc comment for the exact
/// wire format, verified directly against WSJT-X's <c>NetworkMessage.hpp</c> source rather than
/// inferred. Deliberately does not implement the protocol's <c>Heartbeat</c>/<c>Status</c>/
/// <c>Decode</c> messages (schema negotiation, live station/spot tracking) — out of scope; this is
/// "get a logged QSO into GridTracker," not live tracking.</summary>
public interface IGridTrackerStreamer
{
    /// <param name="adifText">A complete, single-record ADIF file (header + one QSO + <c>&lt;EOR&gt;</c>)
    /// — the same text <c>IAdifExporter</c> produces for one record. Returns <c>false</c> without
    /// throwing when streaming is disabled in settings or the send fails (best-effort, never blocks
    /// the QSO from being persisted locally).</param>
    Task<bool> SendLoggedQsoAsync(string adifText, CancellationToken ct = default);
}
