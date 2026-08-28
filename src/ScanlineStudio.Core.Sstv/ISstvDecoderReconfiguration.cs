using ScanlineStudio.Abstractions.Sstv;

namespace ScanlineStudio.Core.Sstv;

/// <summary>Optional side-channel implemented by <see cref="RestartableSstvDecoder"/> only -- same
/// reasoning as <see cref="ISstvDecoderMaintenance"/>'s own doc comment: a real setter on
/// <see cref="AnalogFmSstvDecoder"/> itself would be actively misleading, since setting
/// <c>RxBpfPreset</c>/<c>DemodType</c>/<c>RxBufferMode</c> on an already-constructed instance does
/// nothing there (all three are read once, at construction, by that class alone). Consumers that
/// care (currently only <c>ScanlineStudio.Application.SstvSessionService</c>) test for this via
/// <c>is</c> on their injected <see cref="ISstvDecoder"/>, matching
/// <see cref="ISstvDecoderMaintenance"/>'s own established pattern.</summary>
public interface ISstvDecoderReconfiguration
{
    /// <summary>Requests that <see cref="RxBpfPreset"/>, <see cref="DemodType"/>, and
    /// <see cref="RxBufferMode"/> apply the next time the decoder is idle -- not immediately.
    /// Restart-required-settings backlog item 2 (2026-08-27): all three are constructor-only on
    /// <see cref="AnalogFmSstvDecoder"/>, so this reuses <see cref="RestartableSstvDecoder"/>'s
    /// existing periodic-maintenance whole-instance-swap machinery instead of any in-place field
    /// mutation (see that class's own <c>RequestReconfiguration</c> doc comment for the full
    /// idle-gating and failure-handling contract). A no-op request (all three values already equal
    /// to what's currently applied) clears any previously-queued-but-not-yet-applied request rather
    /// than queuing a pointless swap. Safe to call from any thread.</summary>
    void RequestReconfiguration(RxBpfPreset rxBpfPreset, DemodType demodType, RxBufferMode rxBufferMode);

    /// <summary>Fires when a queued <see cref="RequestReconfiguration"/> request could NOT be
    /// applied because <c>CreateInner</c> threw while draining it -- the request was rolled back and
    /// dropped, not retried (see <see cref="RequestReconfiguration"/>'s own doc comment for why one
    /// attempt only). Can fire alongside <see cref="ISstvDecoderMaintenance.Restarted"/> on the SAME
    /// swap (a mandatory overflow/critical swap that happened to have a pending, but failing,
    /// reconfiguration queued still completes -- using the previous, known-good settings -- and
    /// reports the drop here). Same threading/ordering contract as
    /// <see cref="ISstvDecoderMaintenance"/>'s own events: fires synchronously on whichever thread
    /// called <c>PushSamples</c>, strictly after the swap lock is released. Also fires from
    /// <see cref="ApplyPendingReconfigurationNow"/> on a <see cref="SwapResult.Rejected"/> outcome,
    /// strictly after that method releases its own lock.</summary>
    event Action? ReconfigurationRejected;

    /// <summary>Restart-required-settings backlog item 4 (sample-rate live-apply, 2026-08-27): queues
    /// a sample-rate change, drained ONLY by <see cref="ApplyPendingReconfigurationNow"/> -- never by
    /// the push-driven path <see cref="RequestReconfiguration"/> uses, since a live decode session's
    /// incoming audio may still be physically sampled at the OLD rate at any moment
    /// <c>PushSamples</c> could be called. A separately-queued <see cref="RequestReconfiguration"/>
    /// request is preserved untouched. Throws <see cref="ArgumentOutOfRangeException"/> synchronously
    /// for an unsupported rate -- never queues an invalid value. Safe to call from any thread.</summary>
    void RequestSampleRate(int sampleRate);

    /// <summary>Commits whatever is currently queued (a sample-rate change, and/or an
    /// RxBpfPreset/DemodType/RxBufferMode change) in one swap, RIGHT NOW -- not gated on the
    /// decoder's own idle state, unlike <see cref="RequestReconfiguration"/>'s push-driven path.
    /// Intended caller: <c>ScanlineStudio.Application.SstvSessionService</c>, called ONLY after it
    /// has fully stopped any open capture session (a live rate change is unsafe to commit while
    /// audio could still be arriving at the old hardware rate). Returns
    /// <see cref="SwapResult.Busy"/> rather than blocking if a <c>PushSamples</c> call is genuinely
    /// still in flight (a real, reachable outcome via a capture-stop's own watchdog-timeout path, not
    /// just a defensive guard) -- the caller must treat that as a failure needing its own recovery.
    /// Returns <see cref="SwapResult.NothingPending"/> if called with nothing queued.</summary>
    SwapResult ApplyPendingReconfigurationNow();
}

/// <summary>Outcome of <see cref="ISstvDecoderReconfiguration.ApplyPendingReconfigurationNow"/>.
/// Restart-required-settings backlog item 4, round-4 plan-review finding B3: the caller MUST switch
/// on every member -- in particular, it must never apply a new sample rate to any OTHER component
/// (e.g. the waterfall, or reopen a capture device) unless the result is <see cref="Committed"/>.</summary>
public enum SwapResult
{
    /// <summary>The swap happened; every field this call queued is now live.</summary>
    Committed,

    /// <summary>Called with nothing queued -- no swap was attempted.</summary>
    NothingPending,

    /// <summary>A change was queued, but building the replacement decoder failed -- the previous
    /// decoder is untouched and still fully functional, at its previous settings (including its
    /// previous sample rate, if a rate change was what failed). One attempt only, not retried.</summary>
    Rejected,

    /// <summary>A <c>PushSamples</c> call was genuinely still in flight -- no swap was attempted, and
    /// nothing changed. See this method's own doc comment for why this is a real, reachable outcome,
    /// not just a defensive guard.</summary>
    Busy,
}
