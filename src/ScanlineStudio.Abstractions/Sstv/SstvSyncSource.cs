namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Which lock mechanism currently owns the decoder's <c>_mode</c> state, for the Sync &amp;
/// Slant card's "Source" row. Deliberately a 3-value classification, not the originally-considered
/// 4-way Search/VisLock/Forced/AvtTraining split -- plan-review (2026-08-25) found that split
/// impossible to derive honestly from existing state alone: <c>ForceMode</c>'s own forced-mode flag
/// is a one-shot request cleared before the decoder even starts consuming samples for that
/// reception (no residual "was forced" state survives), and the VIS lock state machine's own
/// "searching" flag is true both while genuinely idle AND while fully locked (it resets for its own
/// post-lock re-verification use). Distinguishing "unlocked" from "forced" would need adding new
/// state at every commit site, out of scope for this pass.
///
/// Computed directly from <c>AnalogFmSstvDecoder</c>'s already-existing, already-vetted
/// <c>IsIdle</c> invariant (<c>_mode is null &amp;&amp; !_avtTrainingPending</c>) -- not a new
/// invariant of its own.</summary>
public enum SstvSyncSource
{
    /// <summary>No mode locked and no AVT training in progress -- searching for a header (VIS tone
    /// or a narrow-family sync bypass).</summary>
    Idle,

    /// <summary>A mode is locked -- via VIS, sync bypass, or <c>ForceMode</c>, this port has no way
    /// to distinguish which one after the fact (see this enum's own summary). Lines are usually
    /// already decoding by this point, but not always: during the brief (3-4 line) non-AVT
    /// pending-anchor-correction window, <c>_mode</c> is committed before anything has been drawn
    /// and before <c>ModeDetected</c> fires, so a poll can briefly report <see cref="Locked"/> while
    /// the Mode card's own Detected-mode row still reads "—".</summary>
    Locked,

    /// <summary>AVT training is in progress (up to ~7.1s) -- a confirmed detection that hasn't
    /// reached <c>Commit()</c> yet.</summary>
    AvtTraining,
}
