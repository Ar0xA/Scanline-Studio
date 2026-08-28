namespace ScanlineStudio.Abstractions.Sstv;

/// <summary>Result of <c>ScanlineStudio.Application.ISstvSessionService.RequestSampleRateAsync</c>
/// -- restart-required-settings backlog item 4 (2026-08-27), see that member's own doc comment for
/// the full design. Covers every EXPECTED outcome; a <c>_rxTransitionGate</c> timeout throws
/// <see cref="TimeoutException"/> instead (matching <c>StartReceivingAsync</c>'s own established
/// choice), and a decoder <c>ScanlineStudio.Core.Sstv.SwapResult.Busy</c> outcome always throws too
/// (an unexpected invariant violation, not a normal outcome this enum should model).</summary>
public enum SampleRateApplyResult
{
    /// <summary>The requested rate is now live -- the decoder (and, if a transmission/self-test
    /// isn't currently in progress, the encoder) reflect it, and RX capture (if it was running) has
    /// been reopened at the new rate.</summary>
    Applied,

    /// <summary>The requested rate already matched what was live -- nothing was touched, no capture
    /// was disturbed.</summary>
    NoChange,

    /// <summary>Deferred: a recording is currently in progress. Nothing was changed -- retry once the
    /// recording stops.</summary>
    DeferredRecordingInProgress,

    /// <summary>A change was queued, but the decoder rejected it while building the replacement
    /// (e.g. an RX buffer mode's scratch-file backend failing to create) -- the previous rate is
    /// still live, RX capture (if it was running) has been restored at that previous rate. One
    /// attempt only, not automatically retried.</summary>
    Rejected,
}
