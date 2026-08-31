using ScanlineStudio.Abstractions.Logbook;

namespace ScanlineStudio.Application;

/// <summary>Outcome of <see cref="ILogbookSessionService.LogQsoAsync"/>. <see cref="Record"/>
/// having been persisted is unconditional (see that method's own doc comment) — the
/// ADIF-UDP/QRZ fields describe two independent, best-effort pushes that never affect whether
/// persistence itself succeeded. No per-destination ADIF-UDP failure reason is carried here (fire-
/// and-forget UDP has no response to report one from; a per-destination failure, if any, is already
/// logged inside <see cref="IAdifUdpStreamer"/>) — <see cref="AdifUdpSentCount"/>/
/// <see cref="AdifUdpEnabledCount"/> is the full granularity surfaced to callers, matching
/// <see cref="IAdifUdpStreamer.SendLoggedQsoAsync"/>'s own return shape. <see cref="QrzError"/> DOES
/// carry a real reason, since QRZ's HTTP API returns one — it must never carry a reason from
/// anywhere else (auditor code-review finding, 2026-08-31: an unrelated settings/ADIF-export/
/// ADIF-UDP throw once landed here too, misattributing e.g. a settings.json permissions error to
/// QRZ specifically). <see cref="PostPersistError"/> carries that other case instead — set only
/// when the whole best-effort span after persistence threw, independent of and mutually exclusive
/// with a real per-service QRZ failure reason.</summary>
public sealed record LogQsoResult(QsoRecord Record, int AdifUdpSentCount, int AdifUdpEnabledCount, bool QrzUploaded, string? QrzError, string? PostPersistError = null);
