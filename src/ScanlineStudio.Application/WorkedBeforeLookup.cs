namespace ScanlineStudio.Application;

/// <summary>Whether <see cref="ILogbookSessionService.GetWorkedBeforeAsync"/>'s lookup succeeded --
/// deliberately distinguishes <see cref="NotFound"/> (a real, confirmed answer: no prior contact)
/// from <see cref="Failed"/> (the DB couldn't be queried, no answer at all). Collapsing the two into
/// a single "null" result (as <see cref="ILogbookSessionService.FindLikelyDuplicateAsync"/>'s
/// fail-open contract does) would render a transient DB error as a false "New station" for a
/// dupe-avoidance indicator -- the harmful direction, unlike <c>FindLikelyDuplicateAsync</c>, where
/// failing open just means "don't block the operator from logging."</summary>
public enum WorkedBeforeOutcome
{
    Found,
    NotFound,
    Failed,
}

/// <summary><paramref name="Count"/> is every prior contact with the callsign, not just the one
/// summarized by <paramref name="LastStartUtc"/>/<paramref name="LastBand"/> -- the RX pane's own
/// display composes "3× · 20m · 2026-08-12" from all three. <paramref name="LastBand"/> is
/// <see langword="null"/> when the most recent contact's frequency is unknown (e.g. an ADIF-imported
/// row with no frequency), not when the lookup failed -- a failed lookup is
/// <see cref="WorkedBeforeOutcome.Failed"/> with no <see cref="WorkedBeforeInfo"/> at all.</summary>
public sealed record WorkedBeforeInfo(int Count, DateTimeOffset LastStartUtc, string? LastBand);

public sealed record WorkedBeforeLookup(WorkedBeforeOutcome Outcome, WorkedBeforeInfo? Info);
