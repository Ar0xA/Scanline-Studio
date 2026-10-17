namespace ScanlineStudio.Abstractions.Drm;

/// <summary>A snapshot of the DRM receiver for display.</summary>
/// <param name="IsActive">False only for the terminal "not receiving" state the session raises when a receiver stops.</param>
/// <param name="TimeSync">Time sync holds.</param>
/// <param name="FrequencySync">Frequency sync holds.</param>
/// <param name="FrameSync">Frame sync holds.</param>
/// <param name="RobustnessMode">0 = A, 1 = B, 2 = E; −1 before frame sync.</param>
/// <param name="SpectrumOccupancy">FAC spectrum occupancy (0 = 2.3 kHz, 1 = 2.5 kHz); −1 before frame sync.</param>
/// <param name="MscMode">FAC <c>msc_mode_new</c>: 0 = 64-QAM, 1 = 16-QAM, 3 = 4-QAM; −1 before frame sync.</param>
/// <param name="Callsign">The last complete FAC callsign; empty if none.</param>
/// <param name="SegmentsReceived">Body segments received of the current transport block.</param>
/// <param name="TotalSegments">Body segments of the current transport block; 0 when unknown.</param>
/// <param name="ReceivingName">The MOT ContentName being received; empty if none.</param>
/// <param name="Generation">The session's receiver generation, newer is larger. A subscriber drops any state older
/// than the newest generation it has seen, so a late state from a stopped receiver never overwrites a newer one.</param>
/// <param name="ExpectedSegments">Body segments the current file has: exact once its last segment arrived, else from
/// the header's body size over the segment size; 0 when unknown. Unlike <paramref name="TotalSegments"/> (the highest
/// segment seen + 1 until the last one arrives), this is a real progress denominator.</param>
/// <param name="CurrentFileComplete">The file the receiver is on was already stored (now or earlier), so a repeat or
/// the sender's run-out is not a reception in progress.</param>
public sealed record DrmSignalState(
    bool IsActive,
    bool TimeSync,
    bool FrequencySync,
    bool FrameSync,
    int RobustnessMode,
    int SpectrumOccupancy,
    int MscMode,
    string Callsign,
    int SegmentsReceived,
    int TotalSegments,
    string ReceivingName,
    long Generation,
    int ExpectedSegments = 0,
    bool CurrentFileComplete = false)
{
    /// <summary>The terminal "not receiving" state at <paramref name="generation"/>.</summary>
    public static DrmSignalState Inactive(long generation) =>
        new(false, false, false, false, -1, -1, -1, string.Empty, 0, 0, string.Empty, generation);
}
