using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application;

/// <summary>Outcome of <see cref="ILogbookSessionService.LogQsoAsync"/>. <see cref="Record"/>
/// having been persisted is unconditional (see that method's own doc comment) — the
/// GridTracker/QRZ fields describe two independent, best-effort pushes that never affect whether
/// persistence itself succeeded. <see cref="GridTrackerError"/> stays <c>null</c> even on a failed
/// send by design: <see cref="IGridTrackerStreamer"/> is a fire-and-forget UDP protocol with no
/// response to report a reason from (the actual failure, if any, is already logged inside that
/// class) — <see cref="QrzError"/> does carry a real reason, since QRZ's HTTP API returns one.</summary>
public sealed record LogQsoResult(QsoRecord Record, bool GridTrackerSent, bool QrzUploaded, string? GridTrackerError, string? QrzError);
