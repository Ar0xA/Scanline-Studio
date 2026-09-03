namespace ScanlineStudio.Application;

/// <summary>fsk_cwid.md B-P5, auditor plan-review finding: a named payload for
/// <see cref="IRxStationIdAttacher.StationIdAttached"/> instead of a bare tuple of adjacent nullable
/// strings -- with 4 value fields (2 at A-P3b, 2 more at B-P5), a positional
/// <c>Action&lt;string, string?, string?, string?, string?&gt;</c> lets a transposed pair of arguments
/// compile silently and corrupt the Gallery; named properties make that a compile error instead.</summary>
public sealed record StationIdAttachment(string EntryId, string? Callsign, string? CallsignSource, string? NrRst, string? CwId);

/// <summary>fsk_cwid.md §5 A2 -- minimal seam so <c>RxHistoryPaneViewModel</c> can subscribe
/// <see cref="RxStationIdAttacher.StationIdAttached"/> without a direct concrete-class dependency,
/// same reasoning as <see cref="IRxAudioAutoSaver"/>'s own doc comment.</summary>
public interface IRxStationIdAttacher
{
    /// <summary>See <see cref="RxStationIdAttacher.StationIdAttached"/>.</summary>
    event Action<StationIdAttachment>? StationIdAttached;
}
