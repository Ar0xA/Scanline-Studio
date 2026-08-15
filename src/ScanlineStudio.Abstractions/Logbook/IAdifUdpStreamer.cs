namespace ScanlineStudio.Abstractions.Logbook;

/// <summary>Streams a logged QSO to every configured, enabled <see cref="AdifUdpDestination"/> --
/// see spec/08-logging.md and the accompanying plan file. Generalized (2026-08-15) from a
/// GridTracker-only streamer: GridTracker, N1MM Logger+, and Log4OM all listen for the exact same
/// WSJT-X network-protocol <c>LoggedADIF</c> message (type 12) on their own configured port -- one
/// wire format, fanned out to as many destinations as the user configures, not three separate
/// integrations. A small binary header (magic number, schema, message type) followed by a
/// client-id string and a complete single-QSO ADIF-text string -- see <c>AdifUdpStreamer</c>'s own
/// doc comment for the exact wire format, verified directly against WSJT-X's
/// <c>NetworkMessage.hpp</c> source rather than inferred. Deliberately does not implement the
/// protocol's <c>Heartbeat</c>/<c>Status</c>/<c>Decode</c> messages (schema negotiation, live
/// station/spot tracking) -- out of scope; this is "get a logged QSO to every configured
/// destination," not live tracking.</summary>
public interface IAdifUdpStreamer
{
    /// <param name="adifText">A complete, single-record ADIF file (header + one QSO + <c>&lt;EOR&gt;</c>)
    /// -- the same text <c>IAdifExporter</c> produces for one record.</param>
    /// <returns>How many of the currently-enabled destinations the datagram was successfully sent
    /// to, and how many were enabled in total -- "enabled" here means every
    /// <see cref="AdifUdpDestination.Enabled"/> == <see langword="true"/> entry, INCLUDING one with
    /// a missing/malformed Host or Port (that one just can never count toward "sent"), so a
    /// misconfigured destination shows up as a real gap in the count rather than silently
    /// disappearing from both numbers. Best-effort per destination -- a destination that fails or
    /// times out never blocks the others, and this method never throws for a send failure (only for
    /// a genuine caller-requested cancellation via <paramref name="ct"/>).</returns>
    Task<AdifUdpSendResult> SendLoggedQsoAsync(string adifText, CancellationToken ct = default);
}
