namespace ScanlineStudio.Application;

/// <summary>fsk_cwid.md §5 A2 -- minimal seam so <c>RxHistoryPaneViewModel</c> can subscribe
/// <see cref="RxStationIdAttacher.StationIdAttached"/> without a direct concrete-class dependency,
/// same reasoning as <see cref="IRxAudioAutoSaver"/>'s own doc comment.</summary>
public interface IRxStationIdAttacher
{
    /// <summary>See <see cref="RxStationIdAttacher.StationIdAttached"/>.</summary>
    event Action<string, string?, string?>? StationIdAttached;
}
